async function readClientFeed(source, action) {
  const routes = {recommend: 'HomeRecommends', popular: 'HomeHotsAll', dynamic: 'Dynamic'};
  const selectors = {recommend: '.home_recommends', popular: '.home_hots', dynamic: '.dynamic_all'};
  if (!routes[source]) throw new Error('未知视频来源');
  const app = document.querySelector('#app')?.__vue_app__;
  const globals = app?.config?.globalProperties;
  const router = globals?.$router;
  if (!router) throw new Error('无法识别客户端路由');
  if (action === 'navigate') {
    await router.push({name: routes[source], ...(source === 'dynamic' ? {query: {tab: 'all'}} : {})});
    return true;
  }
  if (router.currentRoute.value.name !== routes[source]) throw new Error('客户端页面已切换，请重新读取');
  const root = document.querySelector(selectors[source]);
  if (!root) return {ready: false};
  const components = new Set();
  // Production Vue builds omit DOM __vueParentComponent markers. The mounted
  // vnode tree still owns the rendered components and their public card props.
  const visited = new Set();
  function visit(vnode) {
    if (!vnode || typeof vnode !== 'object' || visited.has(vnode)) return;
    visited.add(vnode);
    if (Array.isArray(vnode)) { vnode.forEach(visit); return; }
    const component = vnode.component;
    if (component) {
      if (vnode.el && (vnode.el === root || root.contains(vnode.el) || vnode.el.contains?.(root))) components.add(component);
      visit(component.subTree);
    }
    visit(vnode.children);
    visit(vnode.suspense?.activeBranch);
  }
  visit(app._container?._vnode);
  for (const el of [root, ...root.querySelectorAll('*')]) {
    let c = el.__vueParentComponent;
    for (let depth = 0; c && depth < 8; depth++, c = c.parent) components.add(c);
  }
  if (action === 'more') {
    const scroller = [...components].find(c => (typeof c.vnode?.props?.onLoadMore === 'function' || Array.isArray(c.vnode?.props?.onLoadMore)) && c.props?.active !== false);
    if (!scroller) throw new Error('未找到客户端分页组件');
    scroller.emit('load-more');
    return true;
  }
  const states = globals?.$pinia?.state?.value || {};
  let account = '';
  for (const state of Object.values(states)) {
    const info = state?.userInfo;
    if (info && typeof info.isLogin === 'boolean') {
      account = info.isLogin ? String(info.mid || info.uid || '') : 'guest';
      if (account) break;
    }
  }
  const items = [], seen = new Set();
  const add = info => {
    if (!info || typeof info !== 'object') return;
    if (info.isAd || info.businessInfo || (info.cardType && info.cardType !== 'video')) return;
    const bvid = info.bvid || '';
    if (!/^BV[0-9A-Za-z]{10}$/.test(bvid) || seen.has(bvid)) return;
    seen.add(bvid);
    items.push({bvid, aid: info.aid || 0, title: info.title || '',
      uploader: info.uploader || info.uname || info.upName || info.owner?.name || info.author?.name || (typeof info.author === 'string' ? info.author : ''),
      uploader_mid: info.uploader_mid || info.mid || info.owner?.mid || info.author?.mid || 0,
      cover: info.cover || info.pic || info.coverUrl || '',
      duration: info.duration || info.durationText || 0,
      published: info.published || info.pubdate || info.pubDate || info.pubTime || '',
      views: info.views ?? info.view ?? info.play ?? info.stats?.view ?? info.stat?.view ?? '',
      danmaku: info.danmaku ?? info.video_review ?? info.danmakuCount ?? info.stats?.danmaku ?? info.stat?.danmaku ?? '',
      dynamic_id: info.dynamic_id || '', repost_by: info.repost_by || ''});
  };
  let status = '', hasFragment = false, rawCount = 0;
  const moduleOf = (entry, key) => Array.isArray(entry?.modules)
    ? entry.modules.find(module => module[key])?.[key] : entry?.modules?.[key];
  for (const c of components) {
    const props = c.props || {};
    if (source !== 'dynamic') {
      if(props.active!==false){
        if(Array.isArray(props.list))rawCount=Math.max(rawCount,props.list.length);
        if(typeof props.status==='string')status=props.status||status;
      }
      if (props.info) add(props.info);
    } else if (props.active !== false && Array.isArray(props.list) && String(props.feedKey || '').startsWith('dynamic-all')) {
      hasFragment = true;
      status = props.status || status;
      rawCount = Math.max(rawCount, props.list.length);
      for (const entry of props.list) {
        const original = entry.orig || moduleOf(entry, 'module_dynamic')?.dyn_forward?.item;
        const videoEntry = original || entry;
        const dynamic = moduleOf(videoEntry, 'module_dynamic');
        const archive = dynamic?.dyn_archive || dynamic?.major?.archive;
        if (!archive) continue;
        const author = moduleOf(videoEntry, 'module_author');
        const sender = moduleOf(entry, 'module_author');
        // Client reactive model properties can be non-enumerable: do not spread archive.
        add({bvid: archive.bvid, aid: archive.aid, title: archive.title, cover: archive.cover,
          duration: archive.duration_text,
          uploader: author?.user?.name || author?.name || '', uploader_mid: author?.user?.mid || author?.mid || 0,
          published: sender?.pub_ts || '',
          views: archive.stat?.play || '', danmaku: archive.stat?.danmaku ?? archive.stat?.danmaku_count ?? '', dynamic_id: entry.id_str || '',
          repost_by: original ? sender?.user?.name || sender?.name || '' : ''});
      }
    }
  }
  const text = root.innerText || '';
  const error = /加载失败|加载出错|内容不见了|网络异常/.test(text) || status === 'error';
  const ended = /已经到底|没有更多/.test(text) || ['nomore', 'no-more', 'noMore', 'empty'].includes(status);
  return {ready: items.length > 0 || hasFragment || ended, items, account, error,
    ended, status, raw_count: rawCount || items.length, url: location.href};
}
