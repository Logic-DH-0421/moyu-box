let videoLayouts = {};
function setLayoutPreferences(value) { videoLayouts = {...value}; }
function updateSelectedControls() {
  const selected = app.rows.filter(row=>app.selected.has(Number(row.id)));
  const busy=app.taskRunning||app.taskSubmitting||app.taskAwaitingPoll||app.cacheControlBusy;
  const active=new Set(['正在缓存','已提交','已暂停']);
  const valid=selected.length>0&&selected.every(row=>active.has(row.status));
  $('#pauseSelectedBtn').disabled=busy||!valid||!selected.some(row=>row.status!=='已暂停');
  $('#resumeSelectedBtn').disabled=busy||!valid||!selected.some(row=>row.status==='已暂停');
}
function applyVideoLayout() {
  const cache=['queue','downloading','completed'].includes(app.view);
  const source=app.view;
  if(!cache&&!feedViews[source])return;
  const container=cache?$('#videoList'):$('#feedList');
  const parent=cache?$('.content-card'):$('#feedCard');
  const mode=videoLayouts[source]||'list';
  container.classList.toggle('media-grid',mode==='grid');
  parent.classList.toggle('grid-view',mode==='grid');
  if(cache){
    const order=[0,1,2,3,4,9,10,11,5,6,7,8];
    const labels=['选择','序号','视频信息','UP主','时长','播放量','弹幕量','大小','BV号','状态','画质','进度'];
    for(const row of [$('.list-head'),...$$('#videoList .video-row')]){
      if(row.dataset.columnOrder==='0720')continue;
      const cells=[...row.children];if(cells.length!==12)continue;
      order.forEach((index,n)=>{const cell=cells[index];cell.dataset.label=labels[n];row.appendChild(cell)});
      row.dataset.columnOrder='0720';
    }
  }
  const lower=['recommend','popular','dynamic','search','history'].includes(source);
  const host=cache?$('.filter-row'):$('#feedCard '+(lower?'.feed-filters':'.feed-header'));
  let toggle=parent.querySelector('.layout-toggle');
  if(!toggle){
    toggle=document.createElement('div');toggle.className='layout-toggle';toggle.setAttribute('aria-label','视频展示方式');
    for(const [value,text] of [['list','列表'],['grid','网格']]){
      const button=document.createElement('button');button.type='button';button.textContent=text;button.dataset.layout=value;
      button.addEventListener('click',async()=>{
        if(button.disabled)return;
        const view=app.view,old=videoLayouts[view]||'list';videoLayouts[view]=value;applyVideoLayout();
        toggle.querySelectorAll('button').forEach(b=>b.disabled=true);
        try{if(await app.api.set_video_layout(view,value)!==true)throw Error('保存失败')}
        catch(error){videoLayouts[view]=old;if(app.view===view)applyVideoLayout();toast('展示方式保存失败，请重试','error')}
        finally{toggle.querySelectorAll('button').forEach(b=>b.disabled=false)}
      });toggle.appendChild(button);
    }
    if(cache)host.insertBefore(toggle,host.querySelector('.view-tabs'));else host.appendChild(toggle);
  }
  if(toggle.parentElement!==host)host.appendChild(toggle);
  toggle.querySelectorAll('button').forEach(button=>{button.classList.toggle('active',button.dataset.layout===mode);button.setAttribute('aria-pressed',String(button.dataset.layout===mode))});
}
document.addEventListener('DOMContentLoaded',()=>{
  for(const [id,paused] of [['pauseSelectedBtn',true],['resumeSelectedBtn',false]]){
    $('#'+id).addEventListener('click',async()=>{
      const ids=app.rows.filter(row=>app.selected.has(Number(row.id))).map(row=>Number(row.id));
      if(!ids.length||app.taskSubmitting||app.taskRunning||app.cacheControlBusy)return;
      app.cacheControlBusy=true;updateSelectedControls();
      try{if(!await app.api.control_selected_cache(ids,paused))toast('已有任务正在运行，请稍后重试','error')}
      catch(error){toast(String(error.message||error),'error')}
      finally{app.cacheControlBusy=false;pollSoon()}
    });
  }
});
