const $ = (selector) => document.querySelector(selector);
const $$ = (selector) => [...document.querySelectorAll(selector)];

function normalizeWindowsDpi() {
  if (new URLSearchParams(location.search).get('mock') === '1') {
    document.documentElement.style.setProperty('--ui-scale', '1');
    document.documentElement.style.setProperty('--ui-expand', '100%');
    document.documentElement.classList.toggle('scheme-wide', window.innerWidth >= 1580);
    return;
  }
  const ratio = Math.max(1, Number(window.devicePixelRatio || 1));
  document.documentElement.style.setProperty('--ui-scale', String(1 / ratio));
  document.documentElement.style.setProperty('--ui-expand', `${ratio * 100}%`);
  document.documentElement.classList.toggle('scheme-wide', window.innerWidth * ratio >= 1580);
}

normalizeWindowsDpi();
window.addEventListener('resize', normalizeWindowsDpi);

const app = {
  api: null,
  view: 'queue',
  rows: [],
  selected: new Set(),
  initialized: false,
  paused: false,
  cacheControlBusy: false,
  taskSubmitting: false,
  taskAwaitingPoll: false,
  taskRunning: false,
  taskRevision: 0,
  modalResolver: null,
  attentionAction: '',
  pollTimer: null,
  rowSignature: '',
  coverItem: null,
  coverBusy: false,
  hiddenMode: false,
};

function filters() {
  return {
    view: app.view,
    keyword: $('#keyword').value.trim(),
    uploader: $('#uploader').value.trim(),
    status: $('#statusFilter').value,
    quality: $('#qualityFilter').value,
    min_duration: $('#minDuration').value || 0,
    max_duration: $('#maxDuration').value || 0,
  };
}

function escapeHtml(value) {
  return String(value ?? '').replace(/[&<>'"]/g, char => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[char]));
}

function coverUrl(value) {
  const url = String(value || '').trim();
  if (!url) return '';
  if (url.startsWith('//')) return `https:${url}`;
  return url.replace(/^http:/, 'https:');
}

function displaySize(bytes) {
  const size = Number(bytes || 0);
  if (!Number.isFinite(size) || size <= 0) return '—';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = size; let index = 0;
  while (value >= 1024 && index < units.length - 1) { value /= 1024; index += 1; }
  return `${Number(value.toFixed(value >= 100 || index === 0 ? 0 : 1))} ${units[index]}`;
}

function displayCount(value) {
  const count = Number(value || 0);
  if (!Number.isFinite(count) || count <= 0) return '—';
  if (count >= 100000000) return `${Number((count / 100000000).toFixed(1))}亿`;
  if (count >= 10000) return `${Number((count / 10000).toFixed(1))}万`;
  return String(Math.trunc(count));
}

function applyHiddenMode(enabled) {
  app.hiddenMode = Boolean(enabled);
  document.documentElement.classList.toggle('hidden-mode', app.hiddenMode);
  const switchNode = $('#hiddenMode');
  if (switchNode) switchNode.checked = app.hiddenMode;
}

function showCoverEmpty() {
  app.coverItem = null;
  $('#coverQueueBtn').disabled = true;
  $('#coverDownloadBtn') && ($('#coverDownloadBtn').disabled = true);
  $('#coverActions') && ($('#coverActions').hidden = true);
  $('#coverPreview').innerHTML = '<div class="cover-empty"><span>📺</span>输入链接后，这里会显示视频封面与标题</div>';
}

function renderCoverPreview(item) {
  app.coverItem = item;
  const cover = coverUrl(item.cover || item.source_hint);
  const title = String(item.title || '').trim() || '(无标题)';
  const link = String(item.url || (item.bvid ? `https://www.bilibili.com/video/${item.bvid}` : ''));
  const bvidText = item.bvid ? `BV 号：${escapeHtml(item.bvid)}${item.aid ? ` · aid：${item.aid}` : ''}` : '';
  const coverHtml = cover
    ? `<img class="cover-large" src="${escapeHtml(cover)}" alt="" referrerpolicy="no-referrer">`
    : '<div class="cover-empty"><span>📺</span>该视频未返回封面地址</div>';
  $('#coverPreview').innerHTML = `
    <div class="preview-row">
      ${coverHtml}
      <div class="cover-title">
        <strong title="${escapeHtml(title)}">${escapeHtml(title)}</strong>
        <small>${bvidText}</small>
        ${item.duration_text ? `<small>时长：${escapeHtml(item.duration_text)}${item.part_count && item.part_count > 1 ? ` · ${item.part_count} P` : ''}</small>` : ''}
        ${item.uploader ? `<small>UP：${escapeHtml(item.uploader)}</small>` : ''}
        ${link ? `<small><a href="${escapeHtml(link)}" target="_blank" rel="noopener noreferrer">${escapeHtml(link)}</a></small>` : ''}
      </div>
    </div>`;
  const img = $('#coverPreview img.cover-large');
  if (img) img.addEventListener('error', () => { img.replaceWith(Object.assign(document.createElement('div'), { className: 'cover-empty', innerHTML: '<span>📺</span>封面加载失败' })); });
  $('#coverQueueBtn').disabled = false;
  if ($('#coverDownloadBtn')) {
    $('#coverDownloadBtn').disabled = !cover;
  }
  if ($('#coverActions')) {
    $('#coverActions').hidden = !cover;
  }
}

async function previewFromInput() {
  const raw = $('#coverInput').value.trim();
  if (!raw) return toast('请输入 BV 号、av 号或视频链接', 'error');
  if (app.coverBusy) return;
  app.coverBusy = true;
  $('#coverFetchBtn').disabled = true;
  $('#coverQueueBtn').disabled = true;
  if ($('#coverDownloadBtn')) $('#coverDownloadBtn').disabled = true;
  const start = performance.now();
  try {
    const result = await app.api.preview_video(raw);
    if (!result || !result.ok) {
      showCoverEmpty();
      const code = result && result.code;
      const error = result && result.error ? result.error : '未找到该视频';
      if (code === 'login') {
        return toast(`需要登录后才能查看：${error}`, 'error');
      }
      return toast(error, 'error');
    }
    renderCoverPreview(result.item);
    const ms = Math.round(performance.now() - start);
    const hit = result && result.cached;
    toast(hit ? `封面已提取（命中本地缓存，${ms}ms）` : `封面已提取（${ms}ms），确认无误后可点「加入缓存」`, 'success');
  } catch (err) {
    showCoverEmpty();
    toast(`提取失败：${err && err.message ? err.message : err}`, 'error');
  } finally {
    app.coverBusy = false;
    $('#coverFetchBtn').disabled = false;
    if (app.coverItem) $('#coverQueueBtn').disabled = false;
  }
}

async function downloadCoverImage() {
  if (!app.coverItem || !app.coverItem.bvid) return toast('请先提取封面', 'error');
  if (app.coverBusy) return;
  const bvid = app.coverItem.bvid;
  const btn = $('#coverDownloadBtn');
  const prevText = btn ? btn.textContent : '';
  app.coverBusy = true;
  if (btn) {
    btn.disabled = true;
    btn.textContent = '下载中…';
  }
  try {
    const result = await app.api.download_cover_image(bvid);
    if (!result || !result.ok) {
      return toast(result && result.error ? result.error : '下载失败', 'error');
    }
    const kb = Math.round((result.bytes || 0) / 1024);
    toast(`封面图已保存：${result.path}（${kb} KB）`, 'success');
  } catch (err) {
    toast(`下载失败：${err && err.message ? err.message : err}`, 'error');
  } finally {
    app.coverBusy = false;
    if (btn) {
      btn.disabled = false;
      btn.textContent = prevText || '保存封面图到本地';
    }
  }
}

async function addCoverToQueue() {
  if (!app.coverItem) return toast('请先提取封面', 'error');
  if (app.coverBusy) return;
  app.coverBusy = true;
  $('#coverFetchBtn').disabled = true;
  $('#coverQueueBtn').disabled = true;
  try {
    const raw = $('#coverInput').value.trim();
    const result = await app.api.add_preview_to_queue(raw);
    if (!result || !result.ok) {
      const error = result && result.error ? result.error : '加入缓存失败';
      return toast(error, 'error');
    }
    toast(result.created ? '已加入稍后再看队列' : '已在队列中（已刷新字段）', 'success');
    $('#coverInput').value = '';
    showCoverEmpty();
    pollSoon();
  } catch (err) {
    toast(`加入缓存失败：${err && err.message ? err.message : err}`, 'error');
  } finally {
    app.coverBusy = false;
    $('#coverFetchBtn').disabled = false;
  }
}

function displayTime(value) {
  const text = String(value || '').replace('T', ' ');
  return text ? text.slice(0, 16) : '—';
}

function statusClass(status) {
  if (status === '已完成') return 'done';
  if (['正在缓存','正在打开','等待客户端'].includes(status)) return 'active';
  if (['失败','不支持缓存','需充电'].includes(status)) return 'failed';
  if (['已提交','已暂停'].includes(status)) return 'submitted';
  return '';
}

function toast(text, type = '') {
  if (!text) return;
  const node = document.createElement('div');
  node.className = `toast ${type}`;
  node.textContent = text;
  $('#toastStack').appendChild(node);
  setTimeout(() => node.remove(), type === 'error' ? 5500 : 3200);
}

function showModal(title, text, confirmText = '现在处理') {
  $('#modalTitle').textContent = title;
  $('#modalText').textContent = text;
  $('#modalConfirm').textContent = confirmText;
  $('#modal').hidden = false;
  return new Promise(resolve => { app.modalResolver = resolve; });
}

function closeModal(result) {
  $('#modal').hidden = true;
  const resolver = app.modalResolver;
  app.modalResolver = null;
  if (resolver) resolver(result);
}

function renderMetrics(stats) {
  $('#metricTotal').textContent = stats.total;
  $('#metricSelected').textContent = stats.selected;
  $('#metricActive').textContent = stats.active;
  $('#metricCompleted').textContent = stats.completed;
  $('#metricFailed').textContent = stats.failed;
  $('#tabTotal').textContent = stats.total;
  $('#tabActive').textContent = stats.active;
  $('#tabCompleted').textContent = stats.completed;
}

function renderRows(rows) {
  app.rows = rows;
  if (app.view === 'queue') rows.filter(row => row.selected).forEach(row => app.selected.add(Number(row.id)));
  const list = $('#videoList');
  list.innerHTML = '';
  $('#emptyState').hidden = rows.length !== 0;
  rows.forEach(row => {
    const id = Number(row.id);
    const selected = app.selected.has(id);
    const progress = Math.max(0, Math.min(100, Number(row.progress || 0)));
    const cover = app.hiddenMode ? '' : coverUrl(row.source_hint);
    const visual = app.hiddenMode ? '' : `<div class="cover-wrap">${cover ? `<img class="cover" src="${escapeHtml(cover)}" alt="" referrerpolicy="no-referrer">` : `<div class="cover-fallback">▶</div>`}<b class="cover-duration">${escapeHtml(row.duration_text || '')}</b></div>`;
    const node = document.createElement('div');
    node.className = `video-row row-grid${selected ? ' selected' : ''}`;
    node.dataset.id = String(id);
    node.innerHTML = `
      <label><input class="row-check" type="checkbox" ${selected ? 'checked' : ''}></label>
      <span class="cell-muted">${escapeHtml(row.queue_index || '—')}</span>
      <div class="video-info">
        ${visual}
        <div class="video-copy"><div class="video-title" title="${escapeHtml(row.title)}">${escapeHtml(row.title)}</div><div class="video-sub">${escapeHtml(row.uploader || row.error || `${row.part_count || 1}P`)}</div></div>
      </div>
      <button class="uploader-link cell-muted" title="查看UP主投稿" data-uploader-bvid="${escapeHtml(row.bvid)}" data-uploader-name="${escapeHtml(row.uploader || '')}">${escapeHtml(row.uploader || '—')}</button>
      <span class="cell-muted">${escapeHtml(row.duration_text || '—')}</span>
      <span class="cell-muted">${escapeHtml(row.bvid || '—')}</span>
      <span><i class="status-chip ${statusClass(row.status)}">${escapeHtml(row.status)}</i></span>
      <span class="cell-muted">${escapeHtml(row.actual_quality || row.target_quality || '—')}</span>
      <div class="progress-cell"><div class="progress-track"><div class="progress-bar ${row.status === '已完成' ? 'done' : ''}" style="width:${progress}%"></div></div><small>${progress}%</small></div>
      <span class="cell-muted">${displayCount(row.view_count)}</span>
      <span class="cell-muted">${displayCount(row.danmaku_count)}</span>
      <span class="cell-muted">${displaySize(row.cache_size_bytes)}</span>`;
    const image = node.querySelector('img');
    if (image) image.addEventListener('error', () => image.replaceWith(Object.assign(document.createElement('div'), {className:'cover-fallback', textContent:'▶'})));
    node.querySelector('.row-check').addEventListener('change', event => toggleRow(id, event.target.checked));
    list.appendChild(node);
  });
  updateSelectionSummary();
  if(typeof applyVideoLayout==='function')applyVideoLayout();
}

async function toggleRow(id, checked) {
  if (checked) app.selected.add(id); else app.selected.delete(id);
  await app.api.set_selected([id], checked);
  const row = $(`.video-row[data-id="${id}"]`);
  if (row) {
    row.classList.toggle('selected', checked);
    row.querySelector('.row-check').checked = checked;
  }
  updateSelectionSummary();
}

function updateSelectionSummary() {
  const visible = new Set(app.rows.map(row => Number(row.id)));
  const count = [...app.selected].filter(id => visible.has(id)).length;
  $('#selectionSummary').textContent = `已选择 ${count} 项`;
  $('#recordSummary').textContent = `共 ${app.rows.length} 条记录`;
  $('#masterCheck').checked = app.rows.length > 0 && count === app.rows.length;
  if(typeof updateSelectedControls==='function')updateSelectedControls();
}

function configureToolbar() {
  const labels = {
    queue: { title: '稍后再看', refresh: '刷新列表', remove: '移出稍后再看' },
    downloading: { title: '正在缓存', refresh: '同步缓存状态', remove: '删除选中缓存' },
    completed: { title: '缓存完成', refresh: '同步缓存状态', remove: '删除选中缓存' },
  };
  const current = labels[app.view] || labels.queue;
  $('#toolbarTitle').textContent = current.title;
  $('#refreshBtn').lastChild.textContent = current.refresh;
  $('#deleteBtn b').textContent = current.remove;
  $$('[data-toolbar-view]').forEach(node => { node.hidden = node.dataset.toolbarView !== app.view; });
}

function renderViewState(state) {
  app.taskRunning = Boolean(state.running);
  feedBusy = app.taskRunning || app.taskSubmitting || app.taskAwaitingPoll;
  feedOperation = state.operation || '';
  $('#feedStop').disabled = !state.running || state.operation !== 'feed';
  updateFeedButtons();
  $('#showPlayerBtn b').textContent = state.player_centered ? '移出屏幕' : '播放页居中';
  $('#showPlayerBtn').title = state.player_centered ? '播放页移出屏幕' : '播放页居中显示';
  renderMetrics(state.stats);
  if(state.cache_directory_current){
    $('#clientPath').placeholder=state.cache_directory_current;
    if(!app.cacheDirectoryEdited&&!$('#clientPath').value)$('#clientPath').value=state.config.cache_directory||state.cache_directory_current;
  }
  // Apply the saved privacy mode before the first cache-list render. Previously
  // the initial render ran with hiddenMode=false and loaded cover images once.
  if (!app.initialized) {
    if(typeof setLayoutPreferences==='function')setLayoutPreferences(state.config.layout_modes||{});
    $('#clientPath').value = state.config.cache_directory || state.cache_directory_current || '';
    $('#quality').innerHTML = state.quality_options.map(value => `<option ${value === state.config.quality ? 'selected' : ''}>${escapeHtml(value)}</option>`).join('');
    $('#cacheAll').checked = Boolean(state.config.cache_all);
    $('#autoFallback').checked = Boolean(state.config.auto_fallback);
    $('#autoRemoveWatchLater').checked = !Boolean(state.config.keep_watch_later);
    $('#launchClientOnStartup').checked = Boolean(state.config.launch_client_on_startup);
    applyHiddenMode(state.config.hidden_mode);
    app.initialized = true;
  }
  const rowSignature = JSON.stringify(state.rows);
  if (rowSignature !== app.rowSignature) {
    app.rowSignature = rowSignature;
    renderRows(state.rows);
  }
  $('#logPath').textContent = `日志：${state.log_path}`;
  const running = Boolean(state.running);
  const isCache = state.operation === 'cache';
  const activeCaches = Number(state.stats.active || 0);
  const pausedCaches = Number(state.stats.paused || 0);
  const runningCaches = Math.max(0, activeCaches - pausedCaches);
  app.paused = pausedCaches > 0;
  $('#refreshBtn').disabled = running;
  $('#cacheBtn').disabled = running || app.view !== 'queue';
  $('#resumeBtn').disabled = app.cacheControlBusy || pausedCaches <= 0;
  $('#resumeBtn').title = '调用B站客户端的全部继续/一键开始';
  $('#pauseBtn').disabled = app.cacheControlBusy || runningCaches <= 0;
  $('#pauseBtn').title = '调用B站客户端的全部暂停/一键暂停；不会暂停管理器提交队列';
  const stopControl=$('#stopBtn'); if(stopControl)stopControl.disabled = !running;
  const skipControl=$('#skipBtn'); if(skipControl)skipControl.disabled = !running || !isCache;
  configureToolbar();
  if(typeof updateSelectedControls==='function')updateSelectedControls();
  $('#deleteBtn').disabled = running;
  $('#danmakuBtn').disabled = running || app.view !== 'completed';
  updateTaskButtons();
}

function updateTaskButtons() {
  const busy = app.taskRunning || app.taskSubmitting || app.taskAwaitingPoll;
  for (const id of ['refreshBtn','syncBtn','offlineBtn','deleteBtn']) { const control=$('#'+id); if(control)control.disabled = busy; }
  $('#cacheBtn').disabled = busy || app.view !== 'queue';
  $('#danmakuBtn').disabled = busy || app.view !== 'completed';
}

async function submitTask(action) {
  if (app.taskRunning || app.taskSubmitting || app.taskAwaitingPoll) return false;
  app.taskSubmitting = true;
  app.taskRevision++;
  feedBusy = true;
  updateTaskButtons();
  updateFeedButtons();
  try {
    return await action();
  } catch (error) {
    toast('操作提交失败，请重试', 'error');
    return false;
  } finally {
    app.taskSubmitting = false;
    app.taskAwaitingPoll = true;
    app.taskRevision++;
    updateTaskButtons();
    pollSoon();
  }
}

async function processEvents(events) {
  for (const event of events) {
    const text = String(event.text || '');
    if (event.kind === 'log' || event.kind === 'status') {
      if (text) $('#headlineStatus').textContent = text;
    } else if (event.kind === 'current') {
      $('#currentTask').textContent = `当前任务 ${event.index}/${event.total}：${event.title || ''}`;
    } else if (event.kind === 'operation' && !event.running) {
      $('#currentTask').textContent = '当前任务：无';
    } else if (event.kind === 'cache_control') {
      app.cacheControlBusy = Boolean(event.running);
    } else if (event.kind === 'error') {
      toast(text, 'error');
    } else if (event.kind === 'item_error') {
      toast(`${event.title || ''}：${text}`, 'error');
    } else if (event.kind === 'feed_action_done') {
      toast(text, 'success');
    } else if (event.kind === 'refresh_done') {
      toast(`列表已刷新，共 ${event.count || 0} 条`, 'success');
    } else if (event.kind === 'attention') {
      const approved = await showModal(event.action === 'restart' ? '需要重启B站客户端' : '需要人工处理', text, event.action === 'restart' ? '安全重启' : '打开客户端');
      await app.api.resolve_attention(String(event.action || ''), approved);
    }
  }
}

async function poll() {
  try {
    const revision = app.taskRevision;
    const state = await app.api.get_state(filters());
    if (revision === app.taskRevision && !app.taskSubmitting) app.taskAwaitingPoll = false;
    renderViewState(state);
    await processEvents(state.events || []);
    if (feedViews[app.view]) await refreshFeedSnapshot(app.view);
    await flushPendingSearch();
  } catch (error) {
    $('#headlineStatus').textContent = '界面连接暂时中断';
  } finally {
    app.pollTimer = setTimeout(poll, 650);
  }
}

function setView(view) {
  saveFeedView();
  app.view = view;
  $$('.nav-item').forEach(button => button.classList.toggle('active', button.dataset.feed === view || button.dataset.view === view || (button.dataset.view === 'queue' && ['completed','downloading'].includes(view))));
  app.selected.clear();
  $$('.nav-item[data-view],.view-tabs button').forEach(button => button.classList.toggle('active', button.dataset.view === view));
  configureToolbar();
  if(typeof applyVideoLayout==='function')applyVideoLayout();
  $('#statusFilter').value = '全部';
  const cover = view === 'cover';
  const settings = view === 'settings';
  const settingsCard = $('#settingsCard');
  const overview = document.querySelector('.overview-row');
  const content = $('.content-card');
  const coverCard = $('#coverCard');
  const feed = Boolean(feedViews[view]);
  $('#feedCard').hidden = !feed;
  if (feed) {
    settingsCard?.setAttribute('hidden', '');
    overview?.setAttribute('hidden', '');
    content?.setAttribute('hidden', '');
    coverCard?.setAttribute('hidden', '');
    showFeed(view).catch(() => toast('读取视频流失败，请重试', 'error'));
  } else if (cover) {
    settingsCard?.setAttribute('hidden', '');
    overview?.setAttribute('hidden', '');
    content?.setAttribute('hidden', '');
    coverCard?.removeAttribute('hidden');
  } else if (settings) {
    settingsCard?.removeAttribute('hidden');
    setSettingsTab().catch(()=>toast('读取软件信息失败','error'));
    overview?.setAttribute('hidden', '');
    content?.setAttribute('hidden', '');
    coverCard?.setAttribute('hidden', '');
  } else {
    settingsCard?.setAttribute('hidden', '');
    overview?.removeAttribute('hidden');
    content?.removeAttribute('hidden');
    coverCard?.setAttribute('hidden', '');
  }
  pollSoon();
}

function pollSoon() {
  clearTimeout(app.pollTimer);
  app.pollTimer = setTimeout(poll, 80);
}

let pendingSettings = null;
let settingsSaving = null;

function saveSettings() {
  pendingSettings = {
    cache_directory: $('#clientPath').value.trim(),
    quality: $('#quality').value,
    cache_all: $('#cacheAll').checked,
    auto_fallback: $('#autoFallback').checked,
    keep_watch_later: !$('#autoRemoveWatchLater').checked,
    hidden_mode: $('#hiddenMode').checked,
    launch_client_on_startup: $('#launchClientOnStartup').checked,
  };
  if (!settingsSaving) {
    // Defer the drain so the shared promise exists even on immediate failure.
    settingsSaving = Promise.resolve().then(async () => {
      try {
        let saved = true;
        while (pendingSettings) {
          const values = pendingSettings;
          pendingSettings = null;
          try {
            if (await app.api.save_settings(values) === false) throw new Error('save failed');
            saved = true;
          } catch (error) {
            saved = false;
            toast(`设置保存失败：${error.message||'请重试'}`, 'error');
          }
        }
        if (saved) pollSoon();
        return saved;
      } finally {
        settingsSaving = null;
      }
    });
  }
  return settingsSaving;
}

function selectedVisibleIds() {
  const visible = new Set(app.rows.map(row => Number(row.id)));
  return [...app.selected].filter(id => visible.has(id));
}

function bindActions() {
  bindFeedActions();
  const windowBar = $('.window-bar');
  $('#windowMin').addEventListener('click', () => app.api.minimize_window());
  $('#windowMax').addEventListener('click', () => app.api.toggle_maximize_window());
  $('#windowClose').addEventListener('click', () => app.api.close_window());
  windowBar.addEventListener('dblclick', event => {
    if (!event.target.closest('.window-controls')) app.api.toggle_maximize_window();
  });
  $$('.nav-item[data-view],.view-tabs button').forEach(button => button.addEventListener('click', () => setView(button.dataset.view)));
  $('#coverNav').addEventListener('click', () => {
    setView('cover');
    $$('.nav-item').forEach(button => button.classList.toggle('active', button.id === 'coverNav'));
  });
  $('#videoList').addEventListener('click',event=>{
    const up=event.target.closest('[data-uploader-bvid]');
    if (up) showUploader(up.dataset.uploaderBvid,'',up.dataset.uploaderName).catch(()=>toast('读取UP主投稿失败，请重试','error'));
  });
  $('#settingsNav').addEventListener('click', () => setView('settings'));
  bindSettingsPanels();
  $('#logNav').addEventListener('click', async () => {
    if (!await app.api.open_log()) toast('无法打开诊断日志', 'error');
  });
  $('#clientPath').addEventListener('input',()=>{app.cacheDirectoryEdited=true;});
  $('#clientPath').addEventListener('change', saveSettings);
  $('#clientPath').addEventListener('keydown', event => { if(event.key==='Enter')event.target.blur(); });
  $('#quality').addEventListener('change', saveSettings);
  $('#cacheAll').addEventListener('change', saveSettings);
  $('#autoFallback').addEventListener('change', saveSettings);
  $('#autoRemoveWatchLater').addEventListener('change', saveSettings);
  $('#launchClientOnStartup').addEventListener('change', saveSettings);
  $('#hiddenMode').addEventListener('change', async event => {
    applyHiddenMode(event.target.checked);
    app.rowSignature = '';
    renderRows(app.rows);
    await saveSettings();
  });
  $('#refreshBtn').addEventListener('click', () => submitTask(() => app.view === 'queue' ? app.api.refresh_current(app.view) : app.api.sync_status()));
  $('#syncBtn')?.addEventListener('click', () => submitTask(() => app.api.sync_status()));
  $('#showClientBtn').addEventListener('click', () => app.api.show_client());
  $('#showPlayerBtn').addEventListener('click', async () => {
    const button = $('#showPlayerBtn');
    button.disabled = true;
    try { await app.api.show_player(); }
    finally { button.disabled = false; pollSoon(); }
  });
  $('#offlineBtn').addEventListener('click', () => submitTask(() => app.api.open_offline_cache()));
  $('#retryBtn')?.addEventListener('click', async () => { const count = await app.api.retry_failed(); toast(`已重新选择 ${count} 条失败任务`, 'success'); pollSoon(); });
  $('#cacheBtn').addEventListener('click', () => submitTask(async () => {
    const ids = selectedVisibleIds();
    if (!ids.length && !$('#cacheAll').checked) return toast('请先选择需要缓存的视频', 'error');
    if (!await saveSettings()) return false;
    const started = await app.api.start_cache(ids);
    if (started) { app.selected.clear(); toast('缓存任务已开始', 'success'); pollSoon(); }
    return started;
  }));
  $('#deleteBtn').addEventListener('click', () => submitTask(async () => {
    const ids = selectedVisibleIds();
    if (!ids.length) return toast('请先选择需要删除的视频', 'error');
    const queue = app.view === 'queue';
    const approved = await showModal(queue ? '确认移出稍后再看' : '确认删除缓存', queue ? `将把选中的 ${ids.length} 条视频移出稍后再看，已有缓存不会被删除。` : `将取消或删除选中的 ${ids.length} 条B站客户端缓存。`, queue ? '确认移出' : '确认删除');
    if (!approved) return false;
    const started = await app.api.delete_items(ids, app.view);
    if (started) app.selected.clear();
    return started;
  }));
  $('#danmakuBtn').addEventListener('click', () => submitTask(async () => {
    const ids = selectedVisibleIds();
    if (!ids.length) return toast('请先选择需要更新弹幕的视频', 'error');
    const started = await app.api.update_danmaku(ids);
    if (started) app.selected.clear();
    return started;
  }));
  async function runBulkCacheControl(action) {
    app.cacheControlBusy = true;
    $('#resumeBtn').disabled = true;
    $('#pauseBtn').disabled = true;
    try {
      if (action === 'resume') await app.api.resume(); else await app.api.pause();
    } catch (error) {
      app.cacheControlBusy = false;
      toast('客户端一键开始/暂停操作失败，请重试', 'error');
    }
    pollSoon();
  }
  $('#resumeBtn').addEventListener('click', () => runBulkCacheControl('resume'));
  $('#pauseBtn').addEventListener('click', () => runBulkCacheControl('pause'));
  $('#stopBtn')?.addEventListener('click', () => app.api.stop());
  $('#skipBtn')?.addEventListener('click', () => app.api.skip());
  $('#selectAllBtn')?.addEventListener('click', async () => { const ids=app.rows.map(row=>Number(row.id)); ids.forEach(id=>app.selected.add(id)); await app.api.select_visible(ids); pollSoon(); });
  $('#clearBtn')?.addEventListener('click', async () => { const ids=app.rows.map(row=>Number(row.id)); ids.forEach(id=>app.selected.delete(id)); await app.api.clear_visible(ids); $('#cacheAll').checked=false; pollSoon(); });
  $('#invertBtn')?.addEventListener('click', async () => { const ids=app.rows.map(row=>Number(row.id)); ids.forEach(id=>app.selected.has(id)?app.selected.delete(id):app.selected.add(id)); await app.api.invert_visible(ids); pollSoon(); });
  $('#masterCheck').addEventListener('change', async event => {
    const ids=app.rows.map(row=>Number(row.id));
    if(event.target.checked){ids.forEach(id=>app.selected.add(id));await app.api.select_visible(ids);}
    else {ids.forEach(id=>app.selected.delete(id));await app.api.clear_visible(ids);$('#cacheAll').checked=false;}
    pollSoon();
  });
  $('#applyFilterBtn').addEventListener('click', pollSoon);
  $('#resetFilterBtn').addEventListener('click', () => { ['keyword','uploader','minDuration','maxDuration'].forEach(id=>$('#'+id).value=''); $('#statusFilter').value='全部'; $('#qualityFilter').value=''; pollSoon(); });
  ['statusFilter','qualityFilter'].forEach(id => $('#'+id).addEventListener('change', pollSoon));
  ['keyword','uploader','minDuration','maxDuration'].forEach(id => $('#'+id).addEventListener('keydown', event => { if(event.key==='Enter') pollSoon(); }));
  $('#modalCancel').addEventListener('click', () => closeModal(false));
  $('#modalConfirm').addEventListener('click', () => closeModal(true));

  $('#coverFetchBtn').addEventListener('click', previewFromInput);
  $('#coverQueueBtn').addEventListener('click', addCoverToQueue);
  const dlBtn = $('#coverDownloadBtn');
  if (dlBtn) dlBtn.addEventListener('click', downloadCoverImage);
  $('#coverInput').addEventListener('keydown', event => {
    if (event.key === 'Enter') { event.preventDefault(); previewFromInput(); }
  });
  showCoverEmpty();
}

window.addEventListener('pywebviewready', async () => {
  app.api = window.pywebview.api;
  bindActions();
  const sidebarBox = $('.sidebar').getBoundingClientRect();
  const settingsBox = $('.settings-card').getBoundingClientRect();
  await app.api.report_ui_metrics({devicePixelRatio:window.devicePixelRatio,innerWidth:window.innerWidth,innerHeight:window.innerHeight,uiScale:getComputedStyle(document.documentElement).getPropertyValue('--ui-scale'),sidebarWidth:sidebarBox.width,settingsTop:settingsBox.top,settingsHeight:settingsBox.height});
  setView('queue');
  await poll();
  const overviewBox = $('.overview-row').getBoundingClientRect();
  const contentBox = $('.content-card').getBoundingClientRect();
  const filterBox = $('.filter-row').getBoundingClientRect();
  const headBox = $('.list-head').getBoundingClientRect();
  const firstRowBox = $('.video-row')?.getBoundingClientRect();
  await app.api.report_ui_metrics({phase:'rendered',clientPath:$('#clientPath').value,total:$('#metricTotal').textContent,rowCount:$('#videoList').children.length,overviewTop:overviewBox.top,overviewHeight:overviewBox.height,contentTop:contentBox.top,contentLeft:contentBox.left,contentWidth:contentBox.width,filterHeight:filterBox.height,headHeight:headBox.height,firstRowHeight:firstRowBox?.height || 0});
  // Keep startup idle by default. The explicit setting only connects the
  // client; it does not refresh the cache list or take over a feed request.
  const initialState = await app.api.get_state(filters());
  if (initialState.config.launch_client_on_startup) setTimeout(() => app.api.start_client(), 650);
});

if (new URLSearchParams(location.search).get('mock') === '1') {
  const demoTitles = ['【绝区零】丽塔埃尔刃综合测评：圣光级虚豹！','塞拉斯最实用的大招排行','猫学导论：从脚踝到大腿，猫子如何成就二次元的顶级暗示！','【独立团】解析一万元：王多鱼被迫和ROG超神破冰？','印度什么时候站起来，印度和中国差距有多大？','一个农村孩子的二十四年','100天亏掉40%？理财大赛S2结果公布！','姿态看Faker精彩集锦Top50！震惊主播一年！'];
  const demoRows = demoTitles.map((title,index)=>({id:index+1,queue_index:index+1,title,uploader:['卡特亚','伊诚介','泛式','健康绿色上网','地球知识局','这是个令人疑惑的星球','-LKs-','联盟回忆录'][index],duration_text:['25:23','4:15','17:06','6:33','10:38','15:57','22:31','22:01'][index],bvid:`BV1DEMO${index+1}`,status:index===2||index===4?'正在缓存':index>5?'已完成':'未缓存',target_quality:'1080P',actual_quality:'1080P',progress:index===2?76:index===4?42:index>5?100:0,part_count:1,source_hint:'',first_seen:'2026-07-24T10:08:57',selected:index===2||index===4,error:''}));
  const demoState={rows:demoRows,stats:{total:17,selected:3,active:2,completed:8,failed:1},events:[],config:{bilibili_exe:'C:\\Program Files\\bilibili\\哔哩哔哩.exe',quality:'1080P',cache_all:true,auto_fallback:true,keep_watch_later:true,hidden_mode:false},quality_options:['最高可用','4K','1080P高码率','1080P','720P','480P'],running:false,operation:'',log_path:'diagnostic.log',preview_mode:true};
  const fallback=async()=>true;
  const demoFeeds=Object.fromEntries(['recommend','popular','dynamic'].map(source=>[source,{items:demoRows.map(row=>({...row,title:`【界面示例】${row.title}`,cover:'',duration_seconds:300,published:1789000000,views:1000})),account:'demo',updated:'2026-09-10T12:00:00',ended:false}]));
  window.pywebview={api:new Proxy({get_state:async()=>demoState,get_feed:async source=>demoFeeds[source],read_feed:async()=>false,report_ui_metrics:fallback},{get:(target,key)=>target[key]||fallback})};
  setTimeout(()=>window.dispatchEvent(new Event('pywebviewready')),0);
}
