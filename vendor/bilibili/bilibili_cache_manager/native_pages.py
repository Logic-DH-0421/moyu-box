"""Native creator collections and history, read with the installed client's APIs."""
import json
import re
from .bilibili_client import BilibiliError, parse_duration


READ_RETRY_JS = r'''(async(load,readAccount,account)=>{
  for(let attempt=0;attempt<3;attempt++){
    if(readAccount()!==account)throw Error('客户端账号已变化');
    try{return await load();}catch(error){
      if(attempt===2||!/abort|cancel/i.test(String(error?.message||error)))throw error;
      await new Promise(resolve=>setTimeout(resolve,250*(attempt+1)));
    }
  }
})'''


def video_row(row, mid='', name=''):
    author = row.get('author') or row.get('owner') or {}
    stats = row.get('stat') or row.get('stats') or {}
    duration = row.get('duration') or row.get('length') or 0
    seconds = parse_duration(str(duration)) if ':' in str(duration) else max(0, int(float(duration or 0)))
    return {**row, 'title':re.sub('<[^>]+>', '', str(row.get('title') or '')),
            'uploader':author.get('name', name) if isinstance(author, dict) else str(author),
            'uploader_mid':str(author.get('mid') or mid) if isinstance(author, dict) else str(mid),
            'cover':row.get('pic') or row.get('cover') or '', 'kind':'video',
            'views':stats.get('view', row.get('play',0)), 'danmaku':stats.get('danmaku',row.get('video_review',0)),
            'published':row.get('pubdate') or row.get('ctime') or '',
            'duration_seconds':seconds, 'duration_text':f'{seconds//60}:{seconds%60:02d}'}


def native_call(client, marker, args, prefix=''):
    uri, export = client.native_export(marker, prefix)
    return f'(await (await import({json.dumps(uri)}))[{json.dumps(export)}](...{json.dumps(args)}))'


def creator_payload(client, mid, pn, options):
    """Return an expression evaluated only in the official client's renderer."""
    tab = options.get('tab', 'posts')
    if tab == 'collections':
        call = native_call(client, '/x/space/seasons/series/list', [mid, pn, 20])
        return f'''const d={call}, lists=d.items_lists||{{}}, p=d.page||lists.page||{{}};
          const items=[...(lists.seasons_list||[]).map(x=>({{...x,collection_type:'season'}})),...(lists.series_list||[]).map(x=>({{...x,collection_type:'series'}}))];
          return {{items,ended:Number(p.total)>0?pageNumber*20>=Number(p.total):items.length===0,total:Number(p.total||0)}};'''
    if tab == 'collection':
        ident = options.get('collection_id','')
        if not str(ident).isdigit() or int(ident) <= 0:
            raise BilibiliError('无效的合集编号，请重新打开合集')
        kind = options.get('collection_type','season')
        if kind == 'season':
            call = native_call(client, '/x/space/seasons/archives/list', [{'mid':mid,'season_id':ident,'reverse':options.get('order')=='asc','pn':pn,'ps':30}])
        elif kind == 'series':
            call = native_call(client, '/x/series/archives?', [{'mid':mid,'series_id':ident,'sort':'asc' if options.get('order')=='asc' else 'desc','pn':pn,'ps':30}])
        else:
            raise BilibiliError('无法识别合集类型')
        return f'''const d={call}, items=d.archives||[], total=Number(d.page?.total||d.meta?.total||0);
          return {{items,ended:total?pageNumber*30>=total:items.length<30,total}};'''
    if tab == 'activity':
        uri,export = client.native_http()
        return f'''const m=await import({json.dumps(uri)});
          const response=await m[{json.dumps(export)}].get('https://api.bilibili.com/x/polymer/web-dynamic/desktop/v1/feed/space',{{params:{{host_mid:{json.dumps(mid)},page:pageNumber,offset:{json.dumps(options.get('offset',''))},timezone_offset:new Date().getTimezoneOffset()}},isWbi:true}});
          if(response.code!==0)throw Error(response.message||'读取动态失败');
          const d=response.data||{{}};
          return {{items:d.items||[],ended:!d.has_more,cursor:d.offset||''}};'''
    call = native_call(client, '/x/space/arc/search', [{'mid':mid,'pn':pn,'ps':30,'order':options.get('order','pubdate'),'keyword':options.get('keyword',''),'tid':0}])
    return f'''const d={call},items=Array.isArray(d.list)?d.list:(d.list?.vlist||[]),total=Number(d.page?.count||0);return {{items,ended:total?pageNumber*30>=total:items.length<30,total}};'''


def normalize_creator(rows, mid, options):
    result=[]
    for row in rows:
        if options.get('tab') == 'collections':
            meta=row.get('meta') or {}
            kind=row.get('collection_type','season')
            ident=str(meta.get('season_id') or meta.get('series_id') or '')
            if not ident.isdigit():
                continue
            result.append({'bvid':f'collection:{kind}:{ident}','kind':'collection',
                'collection_id':ident,'collection_type':kind,'title':meta.get('name','合集'),
                'cover':meta.get('cover',''),'uploader_mid':mid,'uploader':'',
                'total':meta.get('total',0),'published':meta.get('mtime',0),'duration_seconds':0,'duration_text':''})
        elif options.get('tab') == 'activity':
            modules=row.get('modules') or {}
            if isinstance(modules,list): modules={key:value for part in modules for key,value in part.items() if key!='module_type'}
            dynamic=modules.get('module_dynamic') or {}
            author=modules.get('module_author') or {}
            user=author.get('user') or author
            forward=(dynamic.get('dyn_forward') or {}).get('item')
            if forward:
                children=normalize_creator([forward],mid,options)
                for child in children:child['repost_by']=user.get('name','')
                result.extend(children)
                continue
            archive=dynamic.get('dyn_archive') or ((dynamic.get('major') or {}).get('archive')) or {}
            if archive.get('bvid'):
                result.append(video_row({**archive,'pic':archive.get('cover'),'author':user,'pubdate':author.get('pub_ts',0),
                    'duration':archive.get('duration_text',0),'stat':{'view':(archive.get('stat') or {}).get('play',0),'danmaku':(archive.get('stat') or {}).get('danmaku',0)}},mid))
            else:
                desc=modules.get('module_desc') or dynamic.get('desc') or {}
                images=(dynamic.get('dyn_draw') or (dynamic.get('major') or {}).get('draw') or {}).get('items') or [{}]
                result.append({'bvid':'activity:'+str(row.get('id_str','')),'kind':'activity','title':desc.get('text','非视频动态'),
                    'uploader':user.get('name',''),'uploader_mid':str(user.get('mid') or mid),'published':author.get('pub_ts',0),'duration_seconds':0,
                    'cover':images[0].get('src',''),'uri':'https://t.bilibili.com/'+str(row.get('id_str',''))})
        elif row.get('bvid'):
            result.append(video_row(row,mid))
    return result


def history_payload(client, keyword, pn, cursor):
    if keyword:
        call=native_call(client, '/x/web-interface/history/search', [{'pn':pn,'keyword':keyword,'business':'all'}])
        return f'const d={call},items=d.list||[],total=Number(d.page?.total||0);return {{items,ended:d.has_more!==undefined?!d.has_more:total?pageNumber*Number(d.page?.size||20)>=total:items.length<20,total}};'
    call=native_call(client, '/x/web-interface/history/cursor', [{**(cursor or {'max':'','view_at':'0','business':''}),'ps':20}])
    return f'const d={call};return {{items:d.list||[],cursor:d.cursor||{{}},ended:!(d.list||[]).length}};'


def normalize_history(rows):
    result=[]
    for row in rows:
        h=row.get('history') or {}; business=h.get('business') or 'archive'
        key=business+'_'+str(row.get('kid') or h.get('oid') or '')
        bvid=h.get('bvid') or ''
        uri=row.get('uri','')
        if not uri and business=='live':uri='https://live.bilibili.com/'+str(h.get('oid') or row.get('kid') or '')
        if not uri and business=='article':uri='https://www.bilibili.com/read/cv'+str(h.get('oid') or row.get('kid') or '')
        if not uri and h.get('epid'):uri='https://www.bilibili.com/bangumi/play/ep'+str(h['epid'])
        video=video_row({**row,'bvid':bvid,'pic':row.get('cover'),'author':{'name':row.get('author_name',''),'mid':row.get('author_mid','')}},str(row.get('author_mid') or ''))
        result.append({**video,'bvid':bvid or 'history:'+key,'kind':'video' if re.fullmatch(r'BV[0-9A-Za-z]{10}',bvid) else 'history',
            'history_key':key,'business':business,'view_at':row.get('view_at',0),'watch_progress':row.get('progress',0),
            'uri':uri,'device':h.get('dt',0),'subtitle':row.get('long_title','')})
    return result
