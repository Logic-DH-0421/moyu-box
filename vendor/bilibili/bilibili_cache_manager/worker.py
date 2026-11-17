from __future__ import annotations

import queue
import re
import threading
import time
import traceback
from pathlib import Path
from typing import Any

from .bilibili_client import BilibiliClient, BilibiliError, ChargeRequired, ClientRestartRequired, LoginRequired, ManualIntervention
from .config_store import AppConfig
from .database import counts, selected_videos, sync_cache_states, sync_queue, update_status, videos_by_ids, save_feed_video
from .feed_store import FeedStore, SOURCES
from .config_store import app_data_dir


ACCOUNT_JS = """(()=>{const states=document.querySelector('#app')?.__vue_app__?.config?.globalProperties?.$pinia?.state?.value||{};
for(const state of Object.values(states)){const u=state?.userInfo;if(u&&typeof u.isLogin==='boolean')return u.isLogin?String(u.mid||u.uid||''):'guest';}return '';})"""


class AutomationWorker:
    def __init__(self, config: AppConfig) -> None:
        self.config = config
        self.events: queue.Queue[dict[str, Any]] = queue.Queue()
        self.thread: threading.Thread | None = None
        self._task_lock = threading.RLock()
        self.operation = ''
        self.pause_event = threading.Event()
        self.pause_event.set()
        self.stop_event = threading.Event()
        self.skip_event = threading.Event()
        self.client: BilibiliClient | None = None
        self.cache_control_thread: threading.Thread | None = None
        self.feeds = FeedStore(app_data_dir() / 'feeds.json')

    def start_feed(self, source: str, more: bool = False, keyword: str = '', options: dict | None = None, request_id: str = '') -> bool:
        if source not in SOURCES:
            return False
        if source == 'history':
            return self._start(lambda: self._read_native_page('history',keyword,more,options or {}), 'feed')
        if source == 'creator':
            return self._start(lambda: self._read_creator_feed(keyword, more, options), 'feed')
        if source == 'search':
            keyword = keyword.strip()
            if not keyword:
                return False
            return self._start(lambda: self._read_search_feed(keyword, more, options, request_id), 'feed')
        return self._start(lambda: self._read_feed(source, more), 'feed')

    def start_feed_action(self, source: str, bvid: str, action: str) -> bool:
        if source not in SOURCES or action not in ('watch', 'cache'):
            return False
        if not re.fullmatch(r'BV[0-9A-Za-z]{10}', bvid):
            return False
        item = next((row for row in self.feeds.get(source)['items'] if row['bvid'] == bvid), None)
        if not item:
            return False
        account = self.feeds.account
        return self._start(lambda: self._feed_action(dict(item), account, action), 'feed_action')

    def _feed_action(self, item: dict, account: str, action: str) -> None:
        client = self._get_client()
        with client.operation_lock, client.background_operation():
            client.ensure_connected()
            page = client._resolve_main_page()
            current = page.evaluate("""(()=>{const states=document.querySelector('#app').__vue_app__.config.globalProperties.$pinia.state.value;
              for(const state of Object.values(states)){const u=state?.userInfo;if(u&&typeof u.isLogin==='boolean')return u.isLogin?String(u.mid||u.uid||''):'guest';}return '';})()""")
            if not current or current != account:
                if isinstance(current, str) and current:
                    self.feeds.set_account(current)
                raise BilibiliError('客户端账号已变化，请刷新视频流后再操作')
            if current == 'guest':
                raise LoginRequired('请先在B站客户端登录')
            if action == 'watch':
                client.add_feed_watch_later(item)
                save_feed_video(item, in_queue=True, quality=self.config.quality)
                self.feeds.set_account(current)
                self.feeds.mark_watch_later(item['bvid'])
                self.emit('feed_action_done', bvid=item['bvid'], text='已加入稍后再看')
                return
            row = save_feed_video(item, in_queue=False, quality=self.config.quality)
            if row['status'] in ('已完成','正在缓存','已提交','已暂停'):
                raise BilibiliError('该视频已缓存或已在缓存队列中，无需重复提交')
            update_status(row['id'], '等待客户端', 0, '正在提交此视频缓存')
            client.manual_intervention.clear()
            try:
                result = client.cache_video({**item, '_feed_direct': True}, self.config.quality, self.config.auto_fallback)
                update_status(row['id'], '已提交', 5, result.message, actual_quality=result.actual_quality)
                self.emit('feed_action_done', bvid=item['bvid'], text='已提交缓存，下载进度由B站客户端处理')
                if not self.config.keep_watch_later:
                    try:
                        client.delete_watch_later_item(item)
                        self.feeds.unmark_watch_later(item['bvid'])
                        self._log('缓存已确认加入，已移出稍后再看：'+str(item.get('title') or item['bvid']))
                    except BilibiliError as exc:
                        self._log(f'缓存已加入，但移出稍后再看未确认：{exc}')
            except Exception as exc:
                status='未缓存' if isinstance(exc,(ClientRestartRequired,LoginRequired)) else '需充电' if isinstance(exc,ChargeRequired) else '失败'
                update_status(row['id'], status, 0, str(exc))
                raise
            try:
                sync_cache_states(client.read_offline_cache())
            except BilibiliError as exc:
                self._log(f'已提交缓存，状态同步暂未完成：{exc}')

    def _feed_stopped(self) -> bool:
        if self.stop_event.is_set():
            self.emit('status', text='已停止读取，保留原有列表')
            return True
        return False

    def _read_account(self, client) -> str:
        client.ensure_connected()
        value = client._resolve_main_page().evaluate(f'({ACCOUNT_JS})()')
        return value if isinstance(value, str) else ''

    def _update_watch_membership(self, client, items, before):
        if not before:
            return
        after = self._read_account(client)
        if after:
            self.feeds.set_account(after)
        if before == after:
            self.feeds.set_watch_later(item.get('bvid') for item in items if item.get('bvid'))
        else:
            self.feeds.set_watch_later([])

    def _read_feed(self, source: str, more: bool) -> None:
        import json
        from .bilibili_client import parse_duration
        if self._feed_stopped():
            return
        client = self._get_client()
        script = (Path(__file__).parent / 'web' / 'feed_reader.js').read_text(encoding='utf-8')
        with client.operation_lock, client.background_operation():
            client.ensure_connected()
            page = client._resolve_main_page()
            def call(action):
                return page.evaluate(f'({script})({json.dumps(source)}, {json.dumps(action)})')
            self.emit('status', text=f'正在读取{SOURCES[source]}视频…')
            call('navigate')
            if not more:
                client.refresh_client_page(page)
            deadline = time.monotonic() + 18
            initial = None
            while time.monotonic() < deadline:
                if self.stop_event.is_set():
                    self.emit('status', text='已停止读取，保留原有列表')
                    return
                initial = call('snapshot') or {}
                if initial.get('ready') and initial.get('status') != 'loading':
                    break
                time.sleep(.4)
            if not initial or not initial.get('ready'):
                raise BilibiliError('视频流未加载完成，已保留原有列表，请检查客户端登录和网络')
            if initial.get('error'):
                raise BilibiliError('客户端视频流加载失败，已保留原有列表')
            account = initial.get('account')
            if not account:
                raise BilibiliError('无法确认客户端账号，已保留原有列表，请检查客户端登录状态')
            if source == 'dynamic' and account == 'guest':
                raise LoginRequired('读取动态需要先在B站客户端登录')
            self.feeds.set_account(account)
            snapshot = initial
            if more and not initial.get('ended'):
                known = {item.get('bvid') for item in self.feeds.get(source)['items']}
                for catchup in range(8):
                    before = json.dumps(initial.get('items', []), sort_keys=True)
                    raw_before = initial.get('raw_count')
                    call('more')
                    deadline = time.monotonic() + 15
                    while time.monotonic() < deadline:
                        if self.stop_event.is_set():
                            self.emit('status', text='已停止读取，保留原有列表')
                            return
                        time.sleep(.4)
                        snapshot = call('snapshot') or {}
                        if snapshot.get('error'):
                            raise BilibiliError('加载更多失败，已保留已读取内容')
                        if snapshot.get('account') != account:
                            raise BilibiliError('客户端账号已切换，请重新刷新视频流')
                        if snapshot.get('ready') and snapshot.get('status') != 'loading' and (snapshot.get('ended') or snapshot.get('raw_count') != raw_before or json.dumps(snapshot.get('items', []), sort_keys=True) != before):
                            break
                    else:
                        raise BilibiliError('本次未加载到新内容，已保留列表，可稍后再次加载')
                    if snapshot.get('ended') or any(item.get('bvid') not in known for item in snapshot.get('items', [])):
                        break
                    initial = snapshot
                else:
                    raise BilibiliError('客户端分页连续返回已读取内容，已保留原列表，请刷新后重试')
            items = []
            for item in snapshot.get('items', []):
                duration = item.get('duration', 0)
                seconds = parse_duration(str(duration)) if ':' in str(duration) else int(float(duration or 0))
                items.append({**item, 'duration_seconds': seconds,
                    'duration_text': f'{seconds // 60}:{seconds % 60:02d}' if seconds else '—'})
            if self._feed_stopped():
                return
            result = self.feeds.update(source, items, more=more, ended=bool(snapshot.get('ended')))
            self.emit('feed_done', source=source, account=account, count=len(result['items']))
            self.emit('status', text=f'{SOURCES[source]}已读取 {len(result["items"])} 条视频')

    def _read_search_feed(self, keyword: str, more: bool, options: dict | None = None, request_id: str = '') -> None:
        """Submit through the native client search box, then read its session result."""
        import json
        from .bilibili_client import parse_duration
        if self._feed_stopped():
            return
        client = self._get_client()
        with client.operation_lock, client.background_operation():
            if self._feed_stopped():
                return
            options = options or {}
            order = options.get('order', 'totalrank')
            if order not in ('totalrank', 'click', 'pubdate', 'dm', 'stow'): order = 'totalrank'
            duration = max(0, min(4, int(options.get('duration') or 0)))
            tids = max(0, int(options.get('tids') or 0))
            search_type=options.get('type','video')
            if search_type not in ('all','video','media_bangumi','media_ft','live','article','bili_user'): search_type='video'
            options = {'order': order, 'duration': duration, 'tids': tids, **({'type':search_type} if 'type' in options else {})}
            previous = self.feeds.get('search')
            page_number = int(previous.get('page') or 1) + 1 if more and previous.get('query') == keyword and previous.get('options') == options else 1
            self.emit('status', text=f'正在通过B站客户端搜索“{keyword}”…')
            expression = f'''(async()=>{{
              const app=document.querySelector('#app')?.__vue_app__;
              const states=app?.config?.globalProperties?.$pinia?.state?.value||{{}};
              // Search must follow the same path as a person using the B站
              // desktop client: set the native header input and submit Enter.
              const input=document.querySelector('input.search-input-focus');
              if(!input) return {{ready:true,error:true,message:'未找到B站客户端原生搜索框，请先回到客户端首页后重试',items:[]}};
              input.focus();
              const setter=Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value')?.set;
              if(setter) setter.call(input,{json.dumps(keyword)}); else input.value={json.dumps(keyword)};
              input.dispatchEvent(new Event('input',{{bubbles:true}}));
              for(const type of ['keydown','keypress','keyup']) input.dispatchEvent(new KeyboardEvent(type,{{key:'Enter',code:'Enter',keyCode:13,which:13,bubbles:true,cancelable:true}}));
              await new Promise(resolve=>setTimeout(resolve,250));
              const nativeSearch=Object.values(states).find(state=>state&&typeof state==='object'&&('searchKeyword' in state||'storeKeyword' in state));
              const submitted=String(nativeSearch?.searchKeyword||nativeSearch?.keyword||nativeSearch?.storeKeyword||'').trim();
              if(submitted!=={json.dumps(keyword)}) return {{ready:true,error:true,message:'B站客户端没有确认搜索关键词，请稍后重试',items:[]}};
              const readAccount={ACCOUNT_JS},account=readAccount();
              if(!account) return {{error:true,message:'无法确认客户端账号'}};
              const pageNumber=account==={json.dumps(self.feeds.account)}?{page_number}:1;
              const searchFrame=document.querySelector('iframe[src*="search.html"]');
              const searchApp=searchFrame?.contentDocument?.querySelector('#app')?.__vue_app__||app;
              const routeNames={{all:'SearchAll',video:'SearchVideo',media_bangumi:'SearchBangumi',media_ft:'SearchPgc',live:'SearchLive',article:'SearchArticle',bili_user:'SearchUpuser'}};
              await searchApp.config.globalProperties.$router.push({{name:routeNames[{json.dumps(search_type)}],query:{{keyword:{json.dumps(keyword)},order:{json.dumps(order)},duration:String({duration}),tids:String({tids}),page:String(pageNumber)}}}});
              const url='https://api.bilibili.com/x/web-interface/search/{'all/v2?' if search_type=='all' else 'type?search_type='+search_type+'&'}keyword='+encodeURIComponent({json.dumps(keyword)})+'&page='+pageNumber+'&order={order}&duration={duration}&tids={tids}';
              const controller=new AbortController();
              const timer=setTimeout(()=>controller.abort(),9000);
              let response,payload;
              try {{ response=await fetch(url,{{credentials:'include',signal:controller.signal}});payload=await response.json(); }}
              catch(error) {{ return {{ready:true,account,error:true,message:error?.name==='AbortError'?'搜索请求超时，请稍后重试':String(error?.message||'搜索请求失败'),items:[]}}; }}
              finally {{ clearTimeout(timer); }}
              if(readAccount()!==account) return {{error:true,message:'客户端账号已切换，请重新刷新'}};
              if(payload.code!==0) return {{ready:true,account,error:true,message:payload.message||'搜索失败',items:[]}};
              const data=payload.data||{{}},strip=value=>String(value||'').replace(/<[^>]*>/g,'').replace(/&amp;/g,'&').trim();
              let entries=[];
              const raw=data.result||[];
              if({json.dumps(search_type)}==='all') entries=raw.flatMap(group=>(group.data||[]).map(item=>({{item,type:group.result_type}})));
              else if(Array.isArray(raw)) entries=raw.map(item=>({{item,type:{json.dumps(search_type)}}}));
              else entries=Object.values(raw).flatMap(group=>Array.isArray(group)?group.map(item=>({{item,type:'live'}})):[]);
              const pageLimit=Number(data.numPages||data.pageinfo?.video?.numPages||0);
              return {{ready:true,account,query:{json.dumps(keyword)},page:pageNumber,ended:pageLimit?pageNumber>=pageLimit:entries.length===0,total:Number(data.numResults||0),raw_count:entries.length,
                items:entries.map(({{item,type}},index)=>({{bvid:String(item.bvid||'search:'+type+':'+(item.id||item.mid||item.season_id||item.roomid||index)),kind:item.bvid?'video':type,aid:item.aid||0,
                title:strip(item.title||item.uname||item.name),uploader:strip(item.author||item.uname),uploader_mid:String(item.mid||item.uid||''),cover:String(item.pic||item.cover||item.user_cover||item.uface||item.face||''),
                uri:String(item.arcurl||item.url||item.jump_url||item.link||(type==='live'&&(item.roomid||item.room_id)?'https://live.bilibili.com/'+(item.roomid||item.room_id):type==='article'&&item.id?'https://www.bilibili.com/read/cv'+item.id:String(type||'').startsWith('media_')&&item.season_id?'https://www.bilibili.com/bangumi/play/ss'+item.season_id:'')),duration:/^[0-9:]+$/.test(String(item.duration||''))?item.duration:0,published:item.pubdate||'',views:item.play||'',danmaku:item.video_review??item.danmaku??''}}))}};
            }})()'''
            # A renderer can be replaced while the client is starting.  Drop the
            # stale page once and reconnect, rather than reporting that transient
            # socket replacement as a search failure.
            snapshot = None
            for attempt in range(2):
                try:
                    if self._feed_stopped():
                        return
                    client.ensure_connected()
                    page = client._resolve_main_page()
                    snapshot = page.evaluate(expression) or {}
                    break
                except BilibiliError as exc:
                    if attempt == 0 and ('超时' in str(exc) or '断开' in str(exc)):
                        client.close()
                        self._log('搜索连接短暂中断，正在重新连接B站客户端…')
                        time.sleep(.6)
                        continue
                    raise
            if self._feed_stopped():
                return
            snapshot = snapshot or {}
            if snapshot.get('error'):
                raise BilibiliError(f"搜索视频流失败：{snapshot.get('message') or '客户端未返回结果'}")
            account = snapshot.get('account')
            if not account:
                raise BilibiliError('无法确认客户端账号，请检查登录状态')
            page_number = int(snapshot.get('page') or (page_number if account == self.feeds.account else 1))
            if account != self.feeds.account:
                more = False
            self.feeds.set_account(account)
            items = []
            for item in snapshot.get('items', []):
                duration = item.get('duration', 0)
                seconds = parse_duration(str(duration)) if ':' in str(duration) else int(float(duration or 0))
                items.append({**item, 'duration_seconds': seconds, 'duration_text': f'{seconds // 60}:{seconds % 60:02d}' if seconds else '—'})
            if self._feed_stopped():
                return
            result = self.feeds.update('search', items, more=more, ended=bool(snapshot.get('ended')), meta={'query': keyword, 'page': page_number, 'options': options, 'request_id': request_id,'total':snapshot.get('total',0)})
            self.emit('feed_done', source='search', account=account, count=len(result['items']))
            self.emit('status', text=f'B站客户端搜索“{keyword}”已读取 {len(result["items"])} 条视频')

    def _read_creator_feed(self, mid: str, more: bool, options: dict | None = None) -> None:
        import json
        from .bilibili_client import parse_duration
        if options and ('tab' in options or 'keyword' in options):
            return self._read_native_page('creator',mid,more,options)
        if not str(mid).isdigit() or int(mid) <= 0:
            raise BilibiliError('无法确认UP主编号，请刷新视频列表后重试')
        order = (options or {}).get('order', 'pubdate')
        if order not in ('pubdate', 'click'):
            order = 'pubdate'
        options = {'order':order, 'duration':0, 'tids':0}
        if self._feed_stopped():
            return
        client = self._get_client()
        with client.operation_lock, client.background_operation():
            client.ensure_connected()
            page = client._resolve_main_page()
            if self._feed_stopped():
                return
            previous = self.feeds.get('creator')
            pn = int(previous.get('page') or 1) + 1 if more and previous.get('query') == mid and (previous.get('options') or {}).get('order', 'pubdate') == order else 1
            module, export = client._creator_client_export()
            snapshot = page.evaluate(f'''(async()=>{{
              const app=document.querySelector('#app').__vue_app__,globals=app.config.globalProperties;
              await globals.$router.push({{name:'Space',query:{{mid:{json.dumps(mid)}}}}});
              const readAccount={ACCOUNT_JS},account=readAccount();
              if(!account) return {{error:true}};
              const pageNumber=account==={json.dumps(self.feeds.account)}?{pn}:1;
              const module=await import({json.dumps(module)});
              const data=await module[{json.dumps(export)}]({{mid:{json.dumps(mid)},pn:pageNumber,ps:30,order:{json.dumps(order)},keyword:'',tid:0}});
              if(readAccount()!==account) return {{error:true}};
              return {{account,page:pageNumber,items:data.list||[],ended:pageNumber*30>=Number(data.page?.count||0)}};
            }})()''') or {}
            if self._feed_stopped():
                return
            if snapshot.get('error') or not snapshot.get('account') or not isinstance(snapshot.get('items'), list):
                raise BilibiliError('客户端未返回UP主投稿，已保留原列表')
            pn = int(snapshot.get('page') or (pn if snapshot['account'] == self.feeds.account else 1))
            if snapshot['account'] != self.feeds.account:
                more = False
            self.feeds.set_account(snapshot['account'])
            items=[]
            for row in snapshot['items']:
                duration=row.get('duration') or row.get('length') or 0
                seconds=parse_duration(str(duration)) if ':' in str(duration) else int(float(duration or 0))
                if not row.get('bvid'): continue
                items.append({**row, 'uploader': (row.get('author') or {}).get('name', ''), 'uploader_mid': mid,
                    'views': (row.get('stats') or {}).get('view', 0), 'danmaku': (row.get('stats') or {}).get('danmaku', 0),
                    'published': row.get('pubdate', ''), 'duration_seconds':seconds, 'duration_text':f'{seconds//60}:{seconds%60:02d}'})
            if self._feed_stopped():
                return
            self.feeds.update('creator',items,more=more,ended=bool(snapshot.get('ended')),meta={'query':mid,'page':pn,'options':options})
            self.emit('feed_done',source='creator',count=len(items))
            self.emit('status',text=f'已读取UP主第 {pn} 页投稿')

    def _read_native_page(self, source, query, more, options):
        import json
        from .native_pages import creator_payload, history_payload, native_call, normalize_creator, normalize_history, READ_RETRY_JS
        query=str(query).strip()
        if source == 'creator' and (not query.isdigit() or int(query)<=0):
            raise BilibiliError('无效的UP主编号')
        if source == 'creator':
            tab=options.get('tab','posts')
            if tab not in ('posts','activity','collections','collection'):
                raise BilibiliError('未知的UP主页面分类')
            order=options.get('order','pubdate')
            if order not in ('pubdate','click','asc','desc'): order='pubdate'
            options={'tab':tab,'order':order,'duration':0,'tids':0,**{k:str(options.get(k) or '') for k in ('collection_id','collection_type','keyword')}}
        else:
            options={'order':'pubdate','duration':0,'tids':0}
        client=self._get_client()
        with client.operation_lock, client.background_operation():
            if self._feed_stopped(): return
            client.ensure_connected()
            page=client._resolve_main_page()
            account=page.evaluate(ACCOUNT_JS+'()')
            if not isinstance(account,str) or not account:
                raise BilibiliError('无法确认客户端账号')
            if source=='history' and account=='guest':
                raise LoginRequired('请先登录B站客户端后读取历史记录')
            if account!=self.feeds.account:
                more=False
                self.feeds.set_account(account)
            views=getattr(self,'_native_page_snapshots',{})
            view_key=(account,source,query,json.dumps(options,sort_keys=True))
            previous=views.get(view_key) or self.feeds.get(source)
            same=previous.get('query')==query and previous.get('options')==options
            more=bool(more and same)
            pn=int(previous.get('page') or 1)+1 if more else 1
            cursor=previous.get('cursor') if more else None
            if source=='creator':
                payload=creator_payload(client,query,pn,{**options,'offset':cursor or ''})
                profile_call=native_call(client,'/x/space/wbi/acc/info',[{'mid':query}])
                relation_call=native_call(client,'/x/relation/stat',[{'vmid':query}],'space.')
                profile_call=f'(await(async()=>{{const profile={profile_call};try{{Object.assign(profile,{relation_call});}}catch(error){{}}return profile;}})())'
                route=f"await globals.$router.push({{name:'Space',query:{{mid:{json.dumps(query)}}}}});"
            else:
                payload=history_payload(client,query,pn,cursor)
                profile_call='({})'
                route="await globals.$router.push({name:'MineHistory'});"
            snapshot=page.evaluate(f'''(async()=>{{
              const globals=document.querySelector('#app').__vue_app__.config.globalProperties;
              const readAccount={ACCOUNT_JS};if(readAccount()!=={json.dumps(account)})return {{error:true}};
              {route}
              const pageNumber={pn};
              const requestWithRetry={READ_RETRY_JS};
              const data=await requestWithRetry(async()=>{{{payload}}},readAccount,{json.dumps(account)});
              let profile={{}};try{{profile=await requestWithRetry(async()=>{profile_call},readAccount,{json.dumps(account)});}}catch(error){{}}
              if(readAccount()!=={json.dumps(account)})return {{error:true}};
              return {{...data,account:{json.dumps(account)},profile}};
            }})()''') or {}
            if self._feed_stopped():return
            if snapshot.get('error') or snapshot.get('account')!=account or not isinstance(snapshot.get('items'),list):
                raise BilibiliError('客户端页面未返回有效数据或账号已变化，已保留原列表')
            items=normalize_creator(snapshot['items'],query,options) if source=='creator' else normalize_history(snapshot['items'])
            if source=='creator':
                for item in items:
                    if not item.get('uploader'):item['uploader']=(snapshot.get('profile') or {}).get('name','')
            meta={'query':query,'options':options,'page':pn,'cursor':snapshot.get('cursor'),'total':snapshot.get('total',0),'profile':snapshot.get('profile') or {}}
            if more:
                items=previous.get('items',[])+items
            result=self.feeds.update(source,items,more=False,ended=bool(snapshot.get('ended')),meta=meta)
            views[view_key]=result
            self._native_page_snapshots=views
            self.emit('feed_done',source=source,count=len(items))
            self.emit('status',text=f'{SOURCES[source]}已读取第 {pn} 页')

    def start_history_action(self, action, key=''):
        if action not in ('delete','clear'):return False
        if action=='delete' and not any(row.get('history_key')==key for row in self.feeds.get('history')['items']):return False
        account=self.feeds.account
        return self._start(lambda:self._history_action(action,key,account),'history_action')

    def _history_action(self, action, key, account):
        import json
        from .native_pages import native_call
        client=self._get_client()
        with client.operation_lock, client.background_operation():
            client.ensure_connected();page=client._resolve_main_page()
            marker='/x/v2/history/delete' if action=='delete' else '/x/v2/history/clear'
            call=native_call(client,marker,[{'kid':key}] if action=='delete' else [])
            result=page.evaluate(f'''(async()=>{{const read={ACCOUNT_JS};if(!{json.dumps(account)}||read()!=={json.dumps(account)}||read()==='guest')throw Error('账号已变化，请重新读取历史记录');await {call};return true;}})()''')
            if result is not True:raise BilibiliError('客户端未确认历史记录操作成功')
            previous=self.feeds.get('history')
            items=[] if action=='clear' else [r for r in previous['items'] if r.get('history_key')!=key]
            self.feeds.update('history',items,meta={k:v for k,v in previous.items() if k not in ('items','updated')},ended=action=='clear' or previous.get('ended',False))
            self._native_page_snapshots={k:v for k,v in getattr(self,'_native_page_snapshots',{}).items() if k[1]!='history'}
            self.emit('status',text='历史记录已清空' if action=='clear' else '历史记录已删除')

    @property
    def running(self) -> bool:
        with self._task_lock:
            return bool(self.thread and self.thread.is_alive())

    def task_state(self) -> tuple[bool, str]:
        with self._task_lock:
            running = self.running
            return running, self.operation if running else ''

    def stop_feed(self) -> bool:
        with self._task_lock:
            if not self.running or self.operation != 'feed':
                return False
            self.stop_event.set()
            return True

    def emit(self, kind: str, **payload: Any) -> None:
        self.events.put({"kind": kind, **payload})

    def _log(self, text: str) -> None:
        self.emit("log", text=text)

    def start_refresh(self) -> bool:
        return self._start(self._refresh, "refresh")

    def start_connect(self) -> bool:
        return self._start(self._connect_client, "connect")

    def start_cache(self) -> bool:
        return self._start(self._cache_selected, "cache")

    def open_offline_cache(self) -> bool:
        return self._start(self._open_offline_cache, "offline")

    def start_restart_refresh(self) -> bool:
        return self._start(self._restart_refresh, "refresh")

    def start_status_sync(self) -> bool:
        return self._start(self._sync_status, "sync")

    def start_delete_cache(self, ids: list[int]) -> bool:
        return self._start(lambda: self._manage_offline(ids, "delete"), "delete")

    def start_delete_watch_later(self, ids: list[int]) -> bool:
        return self._start(lambda: self._delete_watch_later(ids), "delete_queue")

    def start_update_danmaku(self, ids: list[int]) -> bool:
        return self._start(lambda: self._manage_offline(ids, "danmaku"), "danmaku")

    def _start(self, target: Any, operation: str) -> bool:
        with self._task_lock:
            if self.running:
                self.emit("error", text="已有任务正在运行")
                return False
            self.stop_event.clear()
            self.skip_event.clear()
            self.pause_event.set()
            self.operation = operation
            try:
                thread = threading.Thread(target=self._run_guarded, args=(target, operation), daemon=True)
                self.thread = thread
                thread.start()
            except Exception:
                self.thread = None
                self.operation = ''
                raise
            return True

    def _run_guarded(self, target: Any, operation: str) -> None:
        self.emit("operation", operation=operation, running=True)
        try:
            target()
        except ClientRestartRequired as exc:
            self.emit("attention", text=str(exc), action="restart")
        except LoginRequired as exc:
            self.emit("attention", text=str(exc))
        except BilibiliError as exc:
            if operation == "sync":
                self._log(f"缓存状态同步失败，请手动刷新重试：{exc}")
                self.emit("status", text=f"缓存状态同步失败，请手动刷新重试：{exc}")
            else:
                self.emit("error", text=str(exc))
        except Exception as exc:
            self._log(traceback.format_exc())
            self.emit("error", text=f"程序遇到未预期错误：{exc}")
        finally:
            with self._task_lock:
                if self.thread is threading.current_thread():
                    self.operation = ''
                    self.thread = None
                    self.emit("operation", operation=operation, running=False)
                    self.emit("table_refresh")

    def _get_client(self) -> BilibiliClient:
        if self.client is None:
            self.client = BilibiliClient(self.config, self._log)
            stats = counts()
            self.client.expected_cache_count = stats["completed"] + stats["active"]
        else:
            self.client.config = self.config
        return self.client

    def _refresh(self) -> None:
        self.emit("status", text="正在连接B站客户端并读取稍后再看…")
        client = self._get_client()
        with client.operation_lock, client.background_operation():
            account_before = self._read_account(client)
            items = client.read_watch_later(refresh=True)
            self._update_watch_membership(client, items, account_before)
            try:
                states = client.read_offline_cache()
            except ClientRestartRequired:
                raise
            except BilibiliError as exc:
                # A confirmed empty client cache must not prevent the watch-later
                # list from refreshing.  Keep historical cache rows untouched and
                # report the cache-state issue as a warning.
                self._log(f"缓存状态暂未确认，已保留原缓存记录：{exc}")
                states = None
            count = sync_queue(items, cache_all=self.config.cache_all, quality=self.config.quality)
            matched = sync_cache_states(states) if states is not None else 0
        status = f"队列刷新完成，共 {count} 条视频；同步 {matched} 条真实缓存状态"
        if states is None:
            status += "；缓存列表暂不可用，已保留原记录"
        self.emit("status", text=status)
        self.emit("refresh_done", count=count)

    def _connect_client(self) -> None:
        self.emit("status", text="正在启动B站客户端…")
        client = self._get_client()
        with client.operation_lock, client.background_operation():
            client.ensure_connected()
            client.apply_cache_directory()
        self.emit("status", text="B站客户端已连接，等待你的操作")

    def _sync_status(self) -> None:
        client = self._get_client()
        with client.operation_lock, client.background_operation():
            for attempt in range(2):
                try:
                    states = client.read_offline_cache()
                    matched = sync_cache_states(states)
                    break
                except BilibiliError as exc:
                    if attempt == 0 and any(token in str(exc) for token in ("超时", "断开", "closed")):
                        self._log(f"缓存状态同步连接中断，正在重连：{exc}")
                        client.close()
                        time.sleep(0.6)
                        continue
                    raise
        self.emit("status", text=f"缓存状态已同步，更新 {matched} 条列表记录")
        self.emit("table_refresh")

    def _restart_refresh(self) -> None:
        client = self._get_client()
        with client.background_operation():
            client.restart_client()
            client.apply_cache_directory()
        self._refresh()

    def _cache_selected(self) -> None:
        rows = [dict(row) for row in selected_videos()]
        if not rows:
            self.emit("error", text="没有选择需要缓存的视频")
            return
        client = self._get_client()
        client.manual_intervention.clear()
        paused_rows = [dict(row) for row in rows if row.get('status','未缓存') == '已暂停']
        skipped_existing = sum(row.get('status','未缓存') in ('已提交','正在缓存','已完成') for row in rows)
        rows = [row for row in rows if row.get('status','未缓存') not in ('已暂停','已提交','正在缓存','已完成')]
        resumed = 0
        total = len(rows)
        completed = 0
        auto_removed = 0
        auto_remove_failed = 0
        with client.background_operation():
            if paused_rows:
                resumed = client.set_offline_items_paused(paused_rows, False)
                self._log(f'已通过客户端继续 {resumed} 个原有暂停任务；没有创建新任务')
            for index, row in enumerate(rows, start=1):
                if self.stop_event.is_set():
                    update_status(int(row["id"]), "已暂停", int(row["progress"]), "用户停止任务")
                    break
                while not self.pause_event.wait(0.2):
                    if self.stop_event.is_set():
                        break
                if self.stop_event.is_set():
                    break
                video_id = int(row["id"])
                title = str(row["title"])
                self.emit("current", video_id=video_id, title=title, index=index, total=total)
                update_status(video_id, "正在打开", 15, "正在打开视频")
                self.emit("table_refresh")
                try:
                    if self.skip_event.is_set():
                        self.skip_event.clear()
                        update_status(video_id, "已跳过", 0, "用户跳过")
                        continue
                    update_status(video_id, "等待客户端", 35, "正在调用官方缓存")
                    result = client.cache_video(
                        dict(row),
                        str(row["target_quality"] or self.config.quality),
                        self.config.auto_fallback,
                    )
                    update_status(video_id, "已提交", 5, result.message, actual_quality=result.actual_quality)
                    completed += 1
                    self.emit("cache_done", video_id=video_id, completed=completed, total=total)
                    if not self.config.keep_watch_later and bool(row["in_queue"]):
                        try:
                            client.delete_watch_later_item(dict(row))
                            auto_removed += 1
                            self.feeds.unmark_watch_later(str(row.get('bvid') or ''))
                            self._log(f"缓存已提交，已自动移出稍后再看：{title}")
                        except BilibiliError as delete_exc:
                            auto_remove_failed += 1
                            self._log(f"缓存已提交，自动移出稍后再看未确认：{title}：{delete_exc}")
                            self.emit(
                                "item_error",
                                video_id=video_id,
                                title=title,
                                text=f"缓存已加入，自动移出稍后再看未确认：{delete_exc}",
                            )
                except (ClientRestartRequired, LoginRequired) as exc:
                    update_status(video_id, '未缓存', 0, str(exc))
                    self.emit('table_refresh')
                    raise
                except ManualIntervention:
                    update_status(video_id, "未缓存", 0, "已停止自动提交，播放页交给人工处理")
                    self.emit("table_refresh")
                    break
                except BilibiliError as exc:
                    message = str(exc)
                    status = '需充电' if isinstance(exc, ChargeRequired) else "不支持缓存" if any(
                        token in message for token in ("权限", "没有找到官方缓存按钮", "不支持", "不允许", "禁用缓存")
                    ) else "失败"
                    update_status(video_id, status, 0, message)
                    self.emit("item_error", video_id=video_id, title=title, text=message)
                self.emit("table_refresh")
            if (completed or resumed or skipped_existing) and not client.manual_intervention.is_set():
                try:
                    if auto_removed:
                        account_before = self._read_account(client)
                        items = client.read_watch_later()
                        self._update_watch_membership(client, items, account_before)
                        sync_queue(items, cache_all=self.config.cache_all, quality=self.config.quality)
                    sync_cache_states(client.read_offline_cache())
                except BilibiliError as exc:
                    self._log(f"任务已提交，但首次真实状态同步失败：{exc}")
        auto_remove_text = ""
        if not self.config.keep_watch_later:
            auto_remove_text = f"；自动移出稍后再看 {auto_removed} 条"
            if auto_remove_failed:
                auto_remove_text += f"，未确认 {auto_remove_failed} 条"
        self.emit(
            "status",
            text=f"缓存任务结束：新提交 {completed} / {total} 条；继续 {resumed} 条，跳过已有任务 {skipped_existing} 条{auto_remove_text}；下载进度由B站客户端继续更新",
        )

    def _open_offline_cache(self) -> None:
        self._get_client().open_offline_cache()
        self._get_client().show_client()
        self.emit("status", text="已打开B站客户端离线缓存页面")

    def _manage_offline(self, ids: list[int], action: str) -> None:
        rows = videos_by_ids(ids)
        if not rows:
            raise BilibiliError("没有选择需要操作的视频")
        label = "删除缓存" if action == "delete" else "更新弹幕"
        client = self._get_client()
        succeeded = 0
        failed = 0
        with client.background_operation():
            for index, row in enumerate(rows, start=1):
                if self.stop_event.is_set():
                    break
                title = str(row["title"])
                self.emit("current", video_id=int(row["id"]), title=f"{label}：{title}", index=index, total=len(rows))
                try:
                    client.manage_offline_item(dict(row), action)
                    succeeded += 1
                    self._log(f"{label}已提交：{title}")
                except BilibiliError as exc:
                    failed += 1
                    self.emit("item_error", video_id=int(row["id"]), title=title, text=str(exc))
                self.emit("table_refresh")
            if action == "delete":
                states = client.read_offline_cache()
                sync_cache_states(states)
        self.emit("status", text=f"{label}完成：成功 {succeeded} 条，失败 {failed} 条")
        self.emit("table_refresh")

    def _delete_watch_later(self, ids: list[int]) -> None:
        rows = videos_by_ids(ids)
        rows = [row for row in rows if bool(row["in_queue"])]
        if not rows:
            raise BilibiliError("没有选择仍在稍后再看队列中的视频")
        client = self._get_client()
        succeeded = 0
        failed = 0
        with client.background_operation():
            for index, row in enumerate(rows, start=1):
                if self.stop_event.is_set():
                    break
                title = str(row["title"])
                self.emit("current", video_id=int(row["id"]), title=f"移出稍后再看：{title}", index=index, total=len(rows))
                try:
                    client.delete_watch_later_item(dict(row))
                    succeeded += 1
                    self._log(f"已从稍后再看移除：{title}")
                except BilibiliError as exc:
                    failed += 1
                    self.emit("item_error", video_id=int(row["id"]), title=title, text=str(exc))
                self.emit("table_refresh")
            account_before = self._read_account(client)
            items = client.read_watch_later()
            self._update_watch_membership(client, items, account_before)
            sync_queue(items, cache_all=self.config.cache_all, quality=self.config.quality)
            states = client.read_offline_cache()
            sync_cache_states(states)
        self.emit("status", text=f"移出稍后再看完成：成功 {succeeded} 条，失败 {failed} 条")
        self.emit("table_refresh")

    def pause(self) -> None:
        self._start_cache_control(True)

    def start_selected_cache_control(self, ids: list[int], paused: bool) -> bool:
        rows = [dict(row) for row in videos_by_ids(ids)]
        if not rows or any(row['status'] not in ('已提交','正在缓存','已暂停') for row in rows):
            raise BilibiliError('请仅勾选正在缓存或已暂停的视频')
        def control():
            client = self._get_client()
            with client.operation_lock, client.background_operation():
                count = client.set_offline_items_paused(rows, paused)
                sync_cache_states(client.read_offline_cache())
            self.emit('status', text=f'已{"暂停" if paused else "继续"} {count} 个勾选任务')
        return self._start(control, 'cache_control')

    def resume(self) -> None:
        self._start_cache_control(False)

    def _start_cache_control(self, paused: bool) -> None:
        if self.cache_control_thread and self.cache_control_thread.is_alive():
            self.emit("status", text="B站客户端缓存控制正在处理中")
            return
        self.cache_control_thread = threading.Thread(
            target=self._control_official_cache,
            args=(paused,),
            daemon=True,
        )
        self.cache_control_thread.start()

    def _control_official_cache(self, paused: bool) -> None:
        action = "暂停" if paused else "继续"
        try:
            client = self._get_client()
            self.emit("cache_control", running=True)
            with client.operation_lock:
                count = client.set_offline_cache_paused(paused)
                states = client.read_offline_cache()
                sync_cache_states(states)
                active = [s for s in states if s.get("status") in {"正在缓存", "已提交", "已暂停"}]
                if paused and any(s.get("status") != "已暂停" for s in active):
                    raise BilibiliError("暂停指令已发出，但客户端仍显示活动下载，请刷新或人工检查")
                if not paused and any(s.get("status") == "已暂停" for s in active):
                    raise BilibiliError("继续指令已发出，但客户端仍有暂停视频，请刷新或人工检查")
            suffix = "全部任务" if count == -1 else f"{count} 个任务"
            self.emit("status", text=f"已通过B站客户端{action}{suffix}")
        except Exception as exc:
            self.emit("error", text=f"B站客户端{action}缓存失败：{exc}")
        finally:
            self.emit("cache_control", running=False)
            self.emit("table_refresh")

    def stop(self) -> None:
        self.stop_event.set()
        self.pause_event.set()
        self.emit("status", text="正在停止…")

    def skip(self) -> None:
        self.skip_event.set()
        self.emit("status", text="将在安全位置跳过当前视频")

    def show_client(self) -> None:
        try:
            self._get_client().show_client()
        except Exception as exc:
            self.emit("error", text=f"无法显示B站客户端：{exc}")

    def show_player(self) -> None:
        try:
            client = self._get_client()
            if client.player_centered:
                count = client.hide_player()
                self.emit("status", text=f"已将 {count} 个播放窗口移出屏幕，恢复后台显示模式")
            else:
                count = client.show_player()
                self.stop_event.set()
                self.emit("status", text=f"已恢复 {count} 个播放窗口，提交队列将在当前视频处理后停止")
        except BilibiliError as exc:
            self.emit("error", text=str(exc))

    def close(self) -> None:
        self.stop()
        if self.client:
            self.client.close()
