/* Client-shaped search, creator space, history, and the combined settings page. */
let nativeSearchType = 'video';
let collectionSnapshot = null;
const nativeBusy = () => feedBusy || feedRequestPending || feedActionPending || feedStopPending;
function nativeCreatorOptions() {
  const s=feedViews.creator;
  return {tab:s.tab||'posts',order:s.tab==='collection'?s.collectionOrder:s.order,
    keyword:s.creatorKeyword||'',collection_id:s.collectionId||'',collection_type:s.collectionType||''};
}
function nativeSnapshotMatches(result) {
  if(!result.options?.tab) return true;
  const expected=nativeCreatorOptions();
  return Object.keys(expected).every(key=>(result.options[key]||'')===(expected[key]||''));
}
function nativeCreatorBack() {
  if(app.view!=='creator'||feedViews.creator.tab!=='collection')return false;
  if(nativeBusy())return true;
  Object.assign(feedViews.creator,collectionSnapshot||{tab:'collections',collectionId:'',collectionType:'',items:[],loaded:false});
  collectionSnapshot=null;
  showFeed('creator');
  return true;
}
function nativeImage(url, cls='') {
  const safe=coverUrl(url||'');
  return safe?`<img class="${cls}" src="${escapeHtml(safe)}" referrerpolicy="no-referrer" alt="">`:'';
}
function historyGroup(timestamp) {
  const date=new Date(Number(timestamp)*1000),today=new Date();today.setHours(0,0,0,0);
  const start=new Date(date);start.setHours(0,0,0,0);
  const days=Math.round((today-start)/86400000);
  return days===0?'今天':days===1?'昨天':days<7&&days>1?'近一周':'一周前';
}
function watchProgress(row) {
  const seconds=Number(row.watch_progress||0),duration=Number(row.duration_seconds||0);
  if(seconds<0||(duration>0&&seconds>=duration))return '已看完';
  return seconds>0?`看到 ${Math.floor(seconds/60)}:${String(seconds%60).padStart(2,'0')} / ${row.duration_text||'—'}`:'尚未播放';
}
function renderNativeList(rows,state) {
  const enabled=['search','creator','history'].includes(app.view);
  $('#feedList').classList.toggle('native-grid',enabled&&app.view!=='history');
  $('#feedList').classList.toggle('history-list',app.view==='history');
  $('#feedList').classList.toggle('compact-list',app.view==='creator');
  if(!enabled)return null;
  if(!rows.length)return `<div class="feed-empty">${state.loaded?'暂无内容':'点击刷新读取客户端内容'}<small>${state.loaded?'可以切换分类、调整搜索或加载更多':'读取结果与B站客户端当前账号一致'}</small></div>`;
  let lastGroup='';
  return rows.map(row=>{
    const key=escapeHtml(row.bvid),kind=row.kind||'video',video=/^BV[0-9A-Za-z]{10}$/.test(row.bvid);
    let heading='';
    if(app.view==='history') {
      const group=historyGroup(row.view_at);
      if(group!==lastGroup){heading=`<h3 class="history-date">${group}</h3>`;lastGroup=group;}
    }
    const isCollection=kind==='collection';
    const titleAction=isCollection?'collection':kind==='bili_user'?'uploader':'open';
    const cover=app.hiddenMode?'':`<button class="native-cover" data-native-action="${titleAction}">${nativeImage(row.cover)}<span>${escapeHtml(isCollection?`${row.total||0}个内容`:row.duration_text||'')}</span></button>`;
    const up=row.uploader_mid?`<button class="uploader-link" data-uploader-bvid="${key}" data-uploader-mid="${escapeHtml(row.uploader_mid)}" data-uploader-name="${escapeHtml(row.uploader||'')}">${escapeHtml(row.uploader||'UP主')}</button>`:escapeHtml(row.uploader||'');
    const metrics=app.view==='history'?(video?'视频':row.business==='live'?'直播':row.business==='article'?'专栏':'历史内容'):isCollection?`${row.collection_type==='season'?'合集':'列表'} · ${row.total||0}个内容`:video?`播放 ${formatFeedCount(row.views)} · 弹幕 ${formatFeedCount(row.danmaku)}`:escapeHtml(({bili_user:'UP主',media_bangumi:'番剧',media_ft:'影视',live:'直播',article:'专栏',activity:'动态',history:'历史记录'})[kind]||'');
    const progress=app.view==='history'?`<p class="watch-progress">${escapeHtml(watchProgress(row))}<time>${new Date(Number(row.view_at)*1000).toLocaleString('zh-CN',{hour12:false})}</time></p>`:'';
    const actions=isCollection?'<button class="btn secondary" data-native-action="collection">查看全部</button>':video?`<button class="btn secondary" data-native-action="open">${app.view==='history'?'继续播放':'播放'}</button><button class="btn secondary" data-action="watch" ${row.watch_later?'disabled':''}>${row.watch_later?'已加入稍后再看':'加入稍后再看'}</button><button class="btn primary" data-action="cache">直接缓存</button>`:kind==='bili_user'?'<button class="btn secondary" data-native-action="uploader">查看主页</button>':row.uri?'<button class="btn secondary" data-native-action="open">打开内容</button>':'';
    const remove=app.view==='history'?`<button class="history-delete" data-history-key="${escapeHtml(row.history_key)}">删除记录</button>`:'';
    if(['search','creator','history'].includes(app.view)) {
      return `${heading}<article class="native-card compact-card" data-bvid="${key}">${cover}<div class="native-card-info"><button class="native-title" data-native-action="${titleAction}">${escapeHtml(row.title||'无标题')}</button><div class="compact-meta"><span class="compact-uploader">${up}</span><small>${metrics}</small>${row.published?`<small>${formatFeedDate(row.published)}</small>`:''}${progress}${video?`<span class="status-chip">${escapeHtml(row.status||'未缓存')}</span>`:''}</div></div><div class="native-card-actions">${actions}${remove}</div></article>`;
    }
    return `${heading}<article class="native-card ${isCollection?'collection-card':''}" data-bvid="${key}">${cover}<div class="native-card-info"><button class="native-title" data-native-action="${titleAction}">${escapeHtml(row.title||'无标题')}</button><p>${up}</p><small>${metrics}</small>${row.published?`<small>${formatFeedDate(row.published)}</small>`:''}${progress}<div class="native-card-actions">${actions}${remove}</div>${video?`<span class="status-chip">${escapeHtml(row.status||'未缓存')}</span>`:''}</div></article>`;
  }).join('');
}
function renderNativeControls(state) {
  $('#feedCard').dataset.source=app.view;
  const source=app.view,creator=source==='creator',tab=feedViews.creator.tab||'posts';
  $('#creatorProfile').hidden=!creator;
  $('#creatorTabs').hidden=!creator;
  $('#creatorOptions').hidden=!creator||tab!=='posts';
  $('#collectionOptions').hidden=!creator||tab!=='collection';
  $('#collectionTrail').hidden=!creator||tab!=='collection';
  $('#searchTabs').hidden=source!=='search';
  $('#historyToolbar').hidden=source!=='history';
  const actions=$('#feedCard .feed-actions');
  if(source==='history')$('#historyToolbar').insertBefore(actions,$('#historyClear'));
  else if(actions.parentElement!==$('#feedCard .feed-header'))$('#feedCard .feed-header').appendChild(actions);
  if(source==='history')$('#feedDescription').textContent='按观看时间分组 · 支持搜索与继续播放';
  if(source==='search')$('#feedDescription').textContent=`搜索视频、番剧、影视、直播、专栏和UP主${state.total?' · 共 '+formatFeedCount(state.total)+' 个结果':''}`;
  if(creator)$('#feedDescription').textContent=tab==='collection'?'合集内视频 · 支持切换排列顺序':tab==='collections'?'合集和列表 · 点击查看全部内容':tab==='activity'?'UP主发布和转发的动态':'投稿视频 · 支持排序与搜索';
  $('#feedCard').classList.toggle('native-page',['search','creator','history'].includes(source));
  $('#searchOptions').hidden=source!=='search'||!['all','video'].includes(nativeSearchType);
  if(creator) {
    const p=state.profile||{};
    const banner=!app.hiddenMode&&p.top_photo?coverUrl(p.top_photo):'';
    $('#creatorProfile').style.backgroundImage=banner?`linear-gradient(90deg,#fffc,#fffc),url(${JSON.stringify(banner)})`:'';
    $('#creatorProfile').innerHTML=`${app.hiddenMode?'':nativeImage(p.face,'creator-avatar')}<div><h2>${escapeHtml(p.name||creatorName||'UP主主页')}</h2><p>${escapeHtml(p.sign||'')}</p><small>UID ${escapeHtml(state.query)}${p.follower!==undefined?` · 粉丝 ${formatFeedCount(p.follower)} · 关注 ${formatFeedCount(p.following)}`:''}</small></div>`;
    $('#creatorKeyword').value=state.creatorDraft ?? state.creatorKeyword ?? '';
    $('#collectionOrder').value=state.collectionOrder||'desc';
    $('#collectionTrail').innerHTML=`<button class="uploader-link" id="collectionReturn">合集和列表</button><span> / ${escapeHtml(state.collectionName||'合集内视频')}</span>`;
    $('#collectionReturn')?.addEventListener('click',nativeCreatorBack);
    $$('#creatorTabs button').forEach(b=>b.classList.toggle('active',b.dataset.creatorTab===(tab==='collection'?'collections':tab)));
    if(tab==='collection')$('#feedTitle').textContent=state.collectionName||'合集内视频';
  }
  $$('#searchTabs button').forEach(b=>b.classList.toggle('active',b.dataset.searchType===nativeSearchType));
  for(const id of ['searchOrder','searchDuration','searchPartition','creatorOrder','collectionOrder']) {
    const select=$('#'+id),group=select.closest('label');
    if(!group)continue;
    let choices=group.querySelector('.native-choices');
    if(!choices){choices=document.createElement('div');choices.className='native-choices';group.append(choices);select.classList.add('native-select');}
    let options=[...select.options];
    let selectedParent='';
    if(id==='searchPartition') {
      let parent='';const entries=options.map(option=>{
        const child=option.textContent.startsWith('　');if(!child)parent=option.value;
        if(option.selected)selectedParent=parent;
        return {option,parent,child};
      });
      options=entries.filter(x=>!x.child||x.parent===selectedParent).map(x=>x.option);
    }
    choices.innerHTML=options.map(o=>`<button type="button" class="${o.selected?'active':''}" data-choice-target="${id}" data-choice-value="${escapeHtml(o.value)}">${escapeHtml(o.textContent)}</button>`).join('');
  }
}
function updateNativeButtons() {
  $$('#creatorTabs button,#collectionOrder,#creatorKeyword,#creatorSearchForm button,#historyClear').forEach(b=>b.disabled=nativeBusy());
}
function bindNativeViews() {
  $$('#creatorTabs button').forEach(b=>b.addEventListener('click',()=>{
    if(nativeBusy())return;
    Object.assign(feedViews.creator,{tab:b.dataset.creatorTab,collectionId:'',collectionType:'',items:[],selected:new Set(),loaded:false,signature:''});
    collectionSnapshot=null;renderFeed();requestFeed(false);
  }));
  $$('#searchTabs button').forEach(b=>b.addEventListener('click',()=>{nativeSearchType=b.dataset.searchType;renderNativeControls(feedViews.search);requestFeed(false);}));
  $('#creatorSearchForm').addEventListener('submit',event=>{event.preventDefault();if(nativeBusy())return;feedViews.creator.creatorKeyword=$('#creatorKeyword').value.trim();requestFeed(false);});
  $('#creatorKeyword').addEventListener('input',()=>{feedViews.creator.creatorDraft=$('#creatorKeyword').value});
  $('#collectionOrder').addEventListener('change',()=>{feedViews.creator.collectionOrder=$('#collectionOrder').value;requestFeed(false);});
  $('#feedCard').addEventListener('click',event=>{
    const button=event.target.closest('[data-choice-target]');if(!button)return;
    const select=$('#'+button.dataset.choiceTarget);if(select.disabled)return;
    select.value=button.dataset.choiceValue;select.dispatchEvent(new Event('change',{bubbles:true}));renderNativeControls(feedViews[app.view]);
  });
  $('#feedList').addEventListener('click',async event=>{
    const button=event.target.closest('[data-native-action]');if(!button)return;
    const row=feedViews[app.view].items.find(r=>r.bvid===button.closest('[data-bvid]').dataset.bvid);if(!row)return;
    try {
      if(button.dataset.nativeAction==='collection') {
        if(nativeBusy())return;
        collectionSnapshot={...feedViews.creator,items:[...feedViews.creator.items],selected:new Set(feedViews.creator.selected)};
        Object.assign(feedViews.creator,{tab:'collection',collectionId:row.collection_id,collectionType:row.collection_type,collectionName:row.title,items:[],selected:new Set(),loaded:false,signature:''});
        renderFeed();await requestFeed(false);
      } else if(button.dataset.nativeAction==='uploader')await showUploader('',row.uploader_mid,row.uploader||row.title);
      else if(!await app.api.open_page_item(app.view,row.bvid))toast('无法打开此内容','error');
    } catch(error){toast(String(error.message||error),'error')}
  });
  $('#historyClear').addEventListener('click',()=>confirmHistoryAction('clear'));
  $('#feedList').addEventListener('click',event=>{const b=event.target.closest('[data-history-key]');if(b)confirmHistoryAction('delete',b.dataset.historyKey)});
}
async function confirmHistoryAction(action,key='') {
  if(nativeBusy())return;
  if(!await showModal(action==='clear'?'清空历史记录':'删除历史记录',action==='clear'?'将清空当前B站账号的全部历史记录，确认继续？':'将从当前B站账号删除这条历史记录，确认继续？','确认删除'))return;
  if(await app.api.history_action(action,key)){feedBusy=true;updateFeedButtons();pollSoon();}else toast('当前有任务正在运行，请稍后重试','error');
}

async function setSettingsTab() {
  const result=await app.api.read_log();
  $('#logContent').textContent=result.text||'暂无日志';$('#settingsLogPath').textContent=result.path||'';
  $('#aboutVersion').textContent=`版本 ${result.version||''}`;
}
function bindSettingsPanels() {
  $('#readLogsBtn').addEventListener('click',()=>setSettingsTab().catch(()=>toast('读取日志失败','error')));
  $('#openLogFolderBtn').addEventListener('click',()=>app.api.open_log_folder());
  $('#contactAuthorLink').addEventListener('click',async event=>{
    event.preventDefault();
    try {if(!await app.api.open_author_website())toast('无法打开作者网站','error');}
    catch(error){toast('无法打开作者网站','error');}
  });
}
