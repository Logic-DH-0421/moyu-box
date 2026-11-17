const feedNames = {recommend:'推荐', popular:'热门', dynamic:'动态', search:'搜索', creator:'UP主投稿',history:'历史记录'};
const feedViews = Object.fromEntries(Object.keys(feedNames).map(key => [key, {items:[],selected:new Set(),keyword:'',query:'',min:'',max:'',hideCached:false,scroll:0,loaded:false,signature:''}]));
let creatorReturn = 'queue';
let creatorName = '';
feedViews.creator.order = 'pubdate';
feedViews.creator.tab = 'posts';
feedViews.creator.keyword = '';
feedViews.creator.collectionId = '';
feedViews.creator.collectionType = '';
feedViews.creator.collectionOrder = 'desc';
let pendingSearch = null;
let expectedSearch = null;
let searchRevision = 0;
let searchStopped = false;
let searchStopRevision = 0;
let feedStopPending = false;
let feedOperation = '';
const searchSession = `${Date.now()}-${Math.random().toString(36).slice(2)}`;
let feedBusy = false;
let feedAccount = '';
let feedRequestPending = false;
let feedActionPending = false;

function formatFeedDate(value) {
  const raw = String(value || '').trim();
  if (!raw) return '—';
  if (/^\d{10,13}$/.test(raw)) {
    const timestamp = Number(raw) * (raw.length === 10 ? 1000 : 1);
    const date = new Date(timestamp);
    if (!Number.isNaN(date.getTime())) return `${date.getFullYear()}/${date.getMonth()+1}/${date.getDate()}`;
  }
  const match = raw.match(/^(\d{4})[-/]?(\d{1,2})[-/]?(\d{1,2})/);
  return match ? `${match[1]}/${Number(match[2])}/${Number(match[3])}` : raw;
}

function formatFeedCount(value) {
  const number = Number(String(value ?? '').replace(/,/g, ''));
  if (!Number.isFinite(number) || number < 0) return /^[0-9.]+[万亿]$/.test(String(value)) ? String(value) : '—';
  const compact = (amount, unit) => `${Number((number / amount).toFixed(1))}${unit}`;
  if (number >= 100000000) return compact(100000000, '亿');
  if (number >= 10000) return compact(10000, '万');
  return String(Math.trunc(number));
}

function saveFeedView() {
  const state = feedViews[app.view];
  if (!state) return;
  if (app.view === 'search') state.draftQuery = $('#feedSearchKeyword').value;
  Object.assign(state, {keyword:$('#feedKeyword').value,min:$('#feedMin').value,max:$('#feedMax').value,hideCached:$('#feedHideCached').checked,scroll:$('#feedList').scrollTop});
}

async function showFeed(source) {
  const state = feedViews[source];
  $('#feedTitle').textContent = source === 'creator' ? `${creatorName || 'UP主'}的投稿` : feedNames[source];
  $('#creatorBack').hidden = source !== 'creator';
  $('#searchOptions').hidden = source !== 'search';
  $('#creatorOptions').hidden = source !== 'creator';
  if (source === 'creator') $('#creatorOrder').value = state.order;
  $('#feedDescription').textContent = source === 'creator' ? '支持最新发布、最多播放排序 · 加载更多读取后续视频' : source === 'search' ? '通过B站客户端原生搜索框提交关键词，再读取该客户端会话的搜索结果' : source === 'dynamic' ? '关注动态中的视频投稿 · 转发视频按BV号合并' : source === 'popular' ? '综合热门 · 按B站客户端返回顺序展示' : '当前账号的首页推荐 · 刷新获取新一批';
  $('#feedSearchBox').hidden = !['search','history'].includes(source);
  $('#feedSearchKeyword').placeholder=source==='history'?'搜索历史记录':'输入关键词搜索B站视频';
  $('#feedSearchKeyword').value = source === 'search' ? (state.draftQuery ?? state.query) : state.query;
  $('#feedKeyword').value = state.keyword;
  $('#feedMin').value = state.min;
  $('#feedMax').value = state.max;
  $('#feedHideCached').checked = state.hideCached;
  renderFeed();
  $('#feedList').scrollTop = state.scroll;
  await refreshFeedSnapshot(source);
  if (app.view === source && !['search','creator'].includes(source) && !state.loaded && !feedBusy && !feedRequestPending) await requestFeed(false);
}

async function refreshFeedSnapshot(source) {
  const revision = searchRevision;
  const result = await app.api.get_feed(source);
  if (source === 'search' && revision !== searchRevision) return;
  if (!result || !Array.isArray(result.items)) return;
  if (source === 'history' && result.query !== feedViews.history.query) return;
  if (source === 'creator' && typeof nativeSnapshotMatches==='function' && !nativeSnapshotMatches(result)) return;
  if (source === 'creator' && (result.query !== feedViews.creator.query || (!result.options?.tab && (result.options?.order || 'pubdate') !== feedViews.creator.order))) return;
  if (feedAccount !== result.account) {
    feedAccount = result.account;
    Object.values(feedViews).forEach(state => {state.items=[];state.selected.clear();state.loaded=false;state.signature='';state.scroll=0;});
  }
  if (source === 'search' && (searchStopped || (expectedSearch && result.request_id !== expectedSearch.id))) {
    if (app.view === source) renderFeed();
    return;
  }
  const state = feedViews[source], signature = JSON.stringify(result);
  if (state.signature === signature) return;
  Object.assign(state,{items:result.items,updated:result.updated,ended:result.ended,query:result.query || state.query,loaded:!!result.updated,signature,profile:result.profile||{},total:result.total||0});
  if (source === 'search') {
    state.resultRequestId = result.request_id;
    state.resultOptions = result.options || {order:'totalrank',duration:0,tids:0};
    if (state.draftQuery === undefined) {
      state.draftQuery = state.query;
      if (app.view === source) $('#feedSearchKeyword').value = state.draftQuery;
    }
  }
  const keys = new Set(state.items.map(item=>item.bvid));
  state.selected.forEach(key=>{if(!keys.has(key)) state.selected.delete(key);});
  if (app.view === source) renderFeed();
}

function renderFeed() {
  const state = feedViews[app.view];
  if (!state) return;
  const keyword = state.keyword.trim().toLowerCase();
  const rows = state.items.filter(row => (!keyword || `${row.title} ${row.uploader} ${row.bvid}`.toLowerCase().includes(keyword)) && (!state.min || row.duration_seconds >= Number(state.min)*60) && (!state.max || row.duration_seconds <= Number(state.max)*60) && (!state.hideCached || row.status !== '已完成'));
  const oldScroll = $('#feedList').scrollTop;
  const nativeMarkup=typeof renderNativeList==='function'?renderNativeList(rows,state):null;
  $('#feedList').innerHTML = nativeMarkup ?? (rows.length ? rows.map((row,index) => {
    const cover = app.hiddenMode ? '' : coverUrl(row.cover);
    const published = formatFeedDate(row.published);
    const views = formatFeedCount(row.views);
    const danmaku = formatFeedCount(row.danmaku);
    const watchLabel = row.watch_later ? '<span class="status-chip done">已加入稍后再看</span>' : '';
    return `<article class="feed-row ${state.selected.has(row.bvid)?'selected':''}" data-bvid="${escapeHtml(row.bvid)}"><input class="feed-check" type="checkbox" aria-label="选择视频" ${state.selected.has(row.bvid)?'checked':''}><span class="feed-index">${index+1}</span>${app.hiddenMode?'':`<div class="feed-cover">${cover?`<img src="${escapeHtml(cover)}" referrerpolicy="no-referrer" alt="">`:'▶'}<span>${escapeHtml(row.duration_text)}</span></div>`}<div class="feed-copy"><strong>${escapeHtml(row.title)}</strong><small><button class="uploader-link" data-uploader-bvid="${escapeHtml(row.bvid)}" data-uploader-mid="${escapeHtml(row.uploader_mid || '')}" data-uploader-name="${escapeHtml(row.uploader || '')}">${escapeHtml(row.uploader || '—')}</button> · ${escapeHtml(row.bvid)}${app.hiddenMode?` · ${escapeHtml(row.duration_text)}`:''}</small><small>${escapeHtml(published)} · 播放 ${escapeHtml(views)} · 弹幕 ${escapeHtml(danmaku)}${row.repost_by?` · ${escapeHtml(row.repost_by)} 转发`:''}</small></div>${watchLabel}<span class="status-chip ${row.status==='已完成'?'done':''}">${escapeHtml(row.status || '未缓存')}</span><div class="feed-row-actions"><button class="btn secondary feed-watch" data-action="watch" ${row.watch_later?'disabled':''}>${row.watch_later?'已加入稍后再看':'加入稍后再看'}</button><button class="btn primary feed-cache" data-action="cache">直接缓存</button></div></article>`;
  }).join('') : `<div class="feed-empty">${state.loaded?'当前没有符合条件的视频':'点击刷新，读取B站客户端视频流'}<small>${state.loaded?'可以调整筛选或继续加载':'读取结果与客户端当前账号一致'}</small></div>`);
  $('#feedList').scrollTop = oldScroll;
  $('#feedList').querySelectorAll('img').forEach(img=>img.addEventListener('error',()=>img.remove()));
  $('#feedCount').textContent = `已读取 ${state.items.length} 条 · 当前显示 ${rows.length} 条 · 已选 ${state.selected.size} 条${state.ended?' · 没有更多':''}`;
  $('#feedUpdated').textContent = state.updated ? `更新于 ${state.updated.replace('T',' ')} · 仅筛选已读取内容` : '仅筛选已读取内容';
  if(typeof renderNativeControls==='function')renderNativeControls(state);
  if(typeof applyVideoLayout==='function')applyVideoLayout();
  updateFeedButtons();
}

function updateFeedButtons() {
  $$('#feedList [data-action]').forEach(button => {
    const row = feedViews[app.view]?.items.find(item => item.bvid === button.closest('[data-bvid]').dataset.bvid);
    button.disabled = feedBusy || feedActionPending || (button.dataset.action === 'watch' && row?.watch_later) || (button.dataset.action === 'cache' && ['已完成','正在缓存','已提交','已暂停'].includes(row?.status));
  });
  $('#feedRefresh').disabled = feedBusy || feedRequestPending;
  if(typeof updateNativeButtons==='function')updateNativeButtons();
  $('#creatorOrder').disabled = feedBusy || feedRequestPending || feedStopPending;
  $('#feedMore').disabled = feedBusy || feedRequestPending || !feedViews[app.view]?.loaded || !!feedViews[app.view]?.ended;
  if (app.view === 'search' && (pendingSearch || searchStopped || (expectedSearch && expectedSearch.id !== feedViews.search.resultRequestId))) $('#feedMore').disabled = true;
  $('#feedStop').disabled = feedStopPending || !(pendingSearch || feedRequestPending || (feedBusy && feedOperation === 'feed'));
  const empty = $('#feedList .feed-empty');
  if (empty && !feedViews[app.view]?.loaded) {
    empty.innerHTML = feedBusy || feedRequestPending
      ? '正在读取，请稍候…<small>读取完成后视频会自动显示</small>'
      : '点击刷新，读取B站客户端视频流<small>读取失败的原因会显示在顶部提示中</small>';
  }
}

async function queueSearch(more = false) {
  const state = feedViews.search;
  if (more && (feedBusy || feedRequestPending || pendingSearch || searchStopped || !state.loaded || (expectedSearch && expectedSearch.id !== state.resultRequestId))) return;
  state.draftQuery = $('#feedSearchKeyword').value;
  const query = more ? state.query : state.draftQuery.trim();
  if (!query) { toast('请输入搜索关键词', 'error'); return; }
  const options = more ? {...state.resultOptions} : {order:$('#searchOrder').value,duration:$('#searchDuration').value,tids:$('#searchPartition').value,...(typeof nativeSearchType!=='undefined'?{type:nativeSearchType}:{})};
  const request = {id:`${searchSession}-${++searchRevision}`, query, options, more};
  pendingSearch = expectedSearch = request;
  searchStopped = false;
  if (feedBusy || feedRequestPending || feedStopPending) toast('已保留最新搜索，当前任务结束后执行');
  updateFeedButtons();
  await flushPendingSearch();
}

async function flushPendingSearch() {
  if (!pendingSearch || app.view !== 'search' || feedBusy || feedRequestPending || feedActionPending || feedStopPending) return;
  const request = pendingSearch, stopRevision = searchStopRevision;
  pendingSearch = null;
  feedRequestPending = true;
  updateFeedButtons();
  try {
    const accepted = await app.api.read_feed('search', request.more, request.query, request.options, request.id);
    if (accepted) {
      feedBusy = true;
      feedOperation = 'feed';
      if (stopRevision !== searchStopRevision) await app.api.stop_feed();
      else $('#headlineStatus').textContent = `正在搜索“${request.query}”…`;
    } else if (stopRevision === searchStopRevision && expectedSearch === request && !pendingSearch) {
      // A poll can briefly report idle just before another worker starts.
      pendingSearch = request;
    }
  } catch (error) {
    toast('搜索请求失败，请重新提交', 'error');
  } finally {
    feedRequestPending = false;
    updateFeedButtons();
    pollSoon();
  }
}

async function stopFeedRequests() {
  pendingSearch = null;
  expectedSearch = null;
  searchStopped = true;
  searchRevision++;
  searchStopRevision++;
  feedStopPending = true;
  updateFeedButtons();
  try {
    await app.api.stop_feed();
    toast('已停止读取并清除待执行搜索');
  } catch (error) {
    toast('待执行搜索已清除，停止当前读取失败，请重试', 'error');
  } finally {
    feedStopPending = false;
    updateFeedButtons();
    pollSoon();
  }
}

async function requestFeed(more) {
  const source = app.view;
  if (source === 'search') return queueSearch(more);
  if (!feedViews[source] || feedBusy || feedRequestPending) return;
  feedRequestPending = true; updateFeedButtons();
  try {
    if(source==='history'&&!more)feedViews.history.query=$('#feedSearchKeyword').value.trim();
    const query = ['search','creator','history'].includes(source) ? feedViews[source].query.trim() : '';
    const options = source === 'creator' ? (typeof nativeCreatorOptions==='function'?nativeCreatorOptions():{order:feedViews.creator.order}) : {};
    if (source === 'search' && !query) { toast('请输入搜索关键词', 'error'); return; }
    const accepted = await app.api.read_feed(source, more, query, options);
    if (!accepted) toast(source === 'search' ? '请输入关键词，或等待当前任务完成' : '已有任务正在运行，请完成后再读取', 'error');
    else {feedBusy=true;$('#headlineStatus').textContent=`正在读取${feedNames[source]}…`;}
  } catch(error) {toast('读取请求失败，请重试','error');}
  finally {feedRequestPending=false;updateFeedButtons();pollSoon();}
}

async function showUploader(bvid, mid, name) {
  if (feedBusy || feedRequestPending) return toast('请等待当前任务完成', 'error');
  if (!mid || !Number(mid)) {
    const result = await app.api.resolve_uploader(bvid);
    if (!result?.ok) return toast(result?.error || '无法读取UP主信息', 'error');
    mid = String(result.mid); name = result.name || name;
  }
  if (app.view !== 'creator') creatorReturn = app.view;
  creatorName = name;
  Object.assign(feedViews.creator,{query:String(mid),order:'pubdate',tab:'posts',collectionId:'',collectionType:'',collectionOrder:'desc',creatorKeyword:'',creatorDraft:'',profile:{},collectionName:'',total:0,items:[],loaded:false,signature:'',scroll:0,keyword:'',min:'',max:'',hideCached:false});
  setView('creator');
  await requestFeed(false);
}

function bindFeedActions() {
  const partitions = [{"tids": 0, "name": "全部分区", "children": []}, {"tids": 1, "name": "动画", "children": [{"tids": 24, "name": "MAD·AMV"}, {"tids": 25, "name": "MMD·3D"}, {"tids": 47, "name": "短片·手书·配音"}, {"tids": 210, "name": "手办·模玩"}, {"tids": 86, "name": "特摄"}, {"tids": 27, "name": "综合"}]}, {"tids": 13, "name": "番剧", "children": [{"tids": 32, "name": "完结动画"}, {"tids": 33, "name": "连载动画"}, {"tids": 51, "name": "资讯"}, {"tids": 152, "name": "官方延伸"}]}, {"tids": 167, "name": "国创", "children": [{"tids": 153, "name": "国产动画"}, {"tids": 168, "name": "国产原创相关"}, {"tids": 169, "name": "布袋戏"}, {"tids": 195, "name": "动态漫·广播剧"}, {"tids": 170, "name": "资讯"}]}, {"tids": 3, "name": "音乐", "children": [{"tids": 28, "name": "原创音乐"}, {"tids": 31, "name": "翻唱"}, {"tids": 59, "name": "演奏"}, {"tids": 30, "name": "VOCALOID·UTAU"}, {"tids": 29, "name": "音乐现场"}, {"tids": 193, "name": "MV"}, {"tids": 243, "name": "乐评盘点"}, {"tids": 244, "name": "音乐教学"}, {"tids": 130, "name": "音乐综合"}]}, {"tids": 129, "name": "舞蹈", "children": [{"tids": 20, "name": "宅舞"}, {"tids": 198, "name": "街舞"}, {"tids": 199, "name": "明星舞蹈"}, {"tids": 200, "name": "中国舞"}, {"tids": 154, "name": "舞蹈综合"}, {"tids": 156, "name": "舞蹈教程"}]}, {"tids": 4, "name": "游戏", "children": [{"tids": 17, "name": "单机游戏"}, {"tids": 171, "name": "电子竞技"}, {"tids": 172, "name": "手机游戏"}, {"tids": 65, "name": "网络游戏"}, {"tids": 173, "name": "桌游棋牌"}, {"tids": 121, "name": "GMV"}, {"tids": 136, "name": "音游"}, {"tids": 19, "name": "Mugen"}]}, {"tids": 36, "name": "知识", "children": [{"tids": 201, "name": "科学科普"}, {"tids": 124, "name": "社科·法律·心理"}, {"tids": 228, "name": "人文历史"}, {"tids": 207, "name": "财经商业"}, {"tids": 208, "name": "校园学习"}, {"tids": 209, "name": "职业职场"}, {"tids": 229, "name": "设计·创意"}, {"tids": 122, "name": "野生技能协会"}]}, {"tids": 188, "name": "科技", "children": [{"tids": 95, "name": "数码"}, {"tids": 230, "name": "软件应用"}, {"tids": 231, "name": "计算机技术"}, {"tids": 232, "name": "科工机械"}, {"tids": 233, "name": "极客DIY"}]}, {"tids": 234, "name": "运动", "children": [{"tids": 235, "name": "篮球·足球"}, {"tids": 164, "name": "健身"}, {"tids": 236, "name": "竞技体育"}, {"tids": 237, "name": "运动文化"}, {"tids": 238, "name": "运动综合"}]}, {"tids": 223, "name": "汽车", "children": [{"tids": 176, "name": "汽车生活"}, {"tids": 224, "name": "汽车文化"}, {"tids": 245, "name": "赛车"}, {"tids": 225, "name": "汽车极客"}, {"tids": 240, "name": "摩托车"}, {"tids": 226, "name": "智能出行"}, {"tids": 227, "name": "购车攻略"}]}, {"tids": 160, "name": "生活", "children": [{"tids": 138, "name": "搞笑"}, {"tids": 239, "name": "家居房产"}, {"tids": 161, "name": "手工"}, {"tids": 162, "name": "绘画"}, {"tids": 21, "name": "日常"}]}, {"tids": 211, "name": "美食", "children": [{"tids": 76, "name": "美食制作"}, {"tids": 212, "name": "美食侦探"}, {"tids": 213, "name": "美食测评"}, {"tids": 214, "name": "田园美食"}, {"tids": 215, "name": "美食记录"}]}, {"tids": 217, "name": "动物圈", "children": [{"tids": 218, "name": "喵星人"}, {"tids": 219, "name": "汪星人"}, {"tids": 220, "name": "大熊猫"}, {"tids": 221, "name": "野生动物"}, {"tids": 222, "name": "爬宠"}, {"tids": 75, "name": "动物综合"}]}, {"tids": 119, "name": "鬼畜", "children": [{"tids": 22, "name": "鬼畜调教"}, {"tids": 26, "name": "音MAD"}, {"tids": 126, "name": "人力VOCALOID"}, {"tids": 216, "name": "鬼畜剧场"}, {"tids": 127, "name": "教程演示"}]}, {"tids": 155, "name": "时尚", "children": [{"tids": 157, "name": "美妆护肤"}, {"tids": 158, "name": "穿搭"}, {"tids": 159, "name": "时尚潮流"}]}, {"tids": 202, "name": "资讯", "children": [{"tids": 203, "name": "热点"}, {"tids": 204, "name": "环球"}, {"tids": 205, "name": "社会"}, {"tids": 206, "name": "综合"}]}, {"tids": 5, "name": "娱乐", "children": [{"tids": 71, "name": "综艺"}, {"tids": 241, "name": "娱乐杂谈"}, {"tids": 242, "name": "粉丝创作"}, {"tids": 137, "name": "明星综合"}]}, {"tids": 181, "name": "影视", "children": [{"tids": 182, "name": "影视杂谈"}, {"tids": 183, "name": "影视剪辑"}, {"tids": 85, "name": "短片"}, {"tids": 184, "name": "预告·资讯"}]}, {"tids": 177, "name": "纪录片", "children": [{"tids": 37, "name": "人文·历史"}, {"tids": 178, "name": "科学·探索·自然"}, {"tids": 179, "name": "军事"}, {"tids": 180, "name": "社会·美食·旅行"}]}, {"tids": 23, "name": "电影", "children": [{"tids": 147, "name": "国产电影"}, {"tids": 145, "name": "欧美电影"}, {"tids": 146, "name": "日本电影"}, {"tids": 83, "name": "其他国家"}]}, {"tids": 11, "name": "电视剧", "children": [{"tids": 185, "name": "国产剧"}, {"tids": 187, "name": "海外剧"}]}];
  $('#searchPartition').innerHTML=partitions.map(group=>`<option value="${group.tids}">${escapeHtml(group.name)}</option>`+(group.children||[]).map(child=>`<option value="${child.tids}">　${escapeHtml(child.name)}</option>`).join('')).join('');
  $$('[data-feed]').forEach(button=>button.addEventListener('click',()=>setView(button.dataset.feed)));
  $('#creatorBack').addEventListener('click',()=>{if(typeof nativeCreatorBack==='function' && nativeCreatorBack())return;setView(creatorReturn)});
  if(typeof bindNativeViews==='function')bindNativeViews();
  $('#creatorOrder').addEventListener('change',()=>{
    feedViews.creator.order=$('#creatorOrder').value;
    feedViews.creator.selected.clear();
    requestFeed(false);
  });
  ['searchOrder','searchDuration','searchPartition'].forEach(id=>$('#'+id).addEventListener('change',()=>{
    if (!$('#feedSearchKeyword').value.trim()) return;
    requestFeed(false);
  }));
  $('#feedRefresh').addEventListener('click',()=>requestFeed(false));
  $('#feedMore').addEventListener('click',()=>requestFeed(true));
  $('#feedStop').addEventListener('click',stopFeedRequests);
  const submitSearch = () => requestFeed(false);
  $('#feedSearchKeyword').addEventListener('input',()=>{feedViews.search.draftQuery=$('#feedSearchKeyword').value;});
  $('#feedSearchSubmit').addEventListener('click', submitSearch);
  $('#feedSearchKeyword').addEventListener('keydown', event => { if (event.key === 'Enter') submitSearch(); });
  for (const id of ['feedKeyword','feedMin','feedMax','feedHideCached']) $('#'+id).addEventListener('input',()=>{saveFeedView();renderFeed();});
  $('#feedReset').addEventListener('click',()=>{['feedKeyword','feedMin','feedMax'].forEach(id=>$('#'+id).value='');$('#feedHideCached').checked=false;saveFeedView();renderFeed();});
  $('#feedList').addEventListener('change',event=>{const row=event.target.closest('[data-bvid]');if(!row)return;const selected=feedViews[app.view].selected;event.target.checked?selected.add(row.dataset.bvid):selected.delete(row.dataset.bvid);renderFeed();});
  $('#feedList').addEventListener('click',async event=>{
    const uploader=event.target.closest('[data-uploader-bvid]');
    if (uploader) { try { await showUploader(uploader.dataset.uploaderBvid,uploader.dataset.uploaderMid,uploader.dataset.uploaderName); } catch (error) { toast('读取UP主投稿失败：'+error.message,'error'); } return; }
    const button = event.target.closest('[data-action]');
    if (!button || feedBusy || feedActionPending) return;
    const source = app.view, bvid = button.closest('[data-bvid]').dataset.bvid;
    feedActionPending = true;updateFeedButtons();
    try {
      const accepted = await app.api.feed_video_action(source,bvid,button.dataset.action);
      if (!accepted) toast('任务正在运行或视频已失效，请稍后重试','error');
      else {feedBusy=true;toast(button.dataset.action==='watch'?'正在加入稍后再看…':'正在提交此视频缓存…');}
    } catch(error) {toast('操作提交失败，请重试','error');}
    finally {feedActionPending=false;updateFeedButtons();pollSoon();}
  });
}
