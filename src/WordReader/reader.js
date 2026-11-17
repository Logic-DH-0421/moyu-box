/* MoyuBox local TXT/EPUB adapter for NiaoBlush/novel-disguise (MIT). */
window.MoyuWordAdapter=function(renderer){
  'use strict';
  const token=document.querySelector('meta[name="novel-token"]').content;
  const state={books:[],chapters:[],bookId:'',index:0,chapter:null,ready:false,safe:false,settings:{}};
  let generation=0,progressTimer=0,searchTimer=0,searchGeneration=0,progressQueue=Promise.resolve(),settingsQueue=Promise.resolve(),navigating=false,loadingPosition=null;
  const page=document.getElementById('disguised-page'),body=document.getElementById('disguised-body');
  page.dataset.reader='novel-disguise';
  const controls=document.createElement('div'); controls.className='word-controls';
  controls.innerHTML=`<button id="word-import" type="button">打开 TXT / EPUB</button><button id="word-library" type="button" aria-expanded="false">书库与目录</button><span class="word-divider"></span><button id="word-prev" type="button">上一章</button><button id="word-next" type="button">下一章</button><label class="word-size">字号 <select id="word-font" aria-label="阅读字号"><option>14</option><option>16</option><option selected>18</option><option>20</option><option>22</option><option>24</option><option>26</option><option>28</option><option>30</option></select></label><button id="word-safe" type="button" title="Ctrl + 反引号切换">安全文档</button><button id="word-about" type="button">关于</button><input id="word-file" type="file" accept=".txt,.epub" hidden>`;
  page.insertBefore(controls,body);
  new ResizeObserver(()=>page.style.setProperty('--word-panel-top',(controls.offsetTop+controls.offsetHeight)+'px')).observe(controls);
  new ResizeObserver(()=>page.style.setProperty('--word-panel-top',(controls.offsetTop+controls.offsetHeight)+'px')).observe(document.getElementById('disguised-header'));
  const panel=document.createElement('aside');panel.id='word-panel';panel.hidden=true;
  panel.innerHTML='<div class="panel-heading"><strong>书库与目录</strong><button id="word-panel-close" type="button" aria-label="关闭目录">×</button></div><label>本地书籍<select id="word-books" aria-label="选择本地书籍"></select></label><input id="word-search" type="search" placeholder="搜索当前书籍…" aria-label="搜索当前书籍"><div id="word-results"></div><div id="word-chapters"></div>';
  page.append(panel);
  const notice=document.createElement('div');notice.id='word-notice';notice.hidden=true;notice.setAttribute('role','status');page.append(notice);
  const about=document.createElement('dialog');about.id='word-about-dialog';about.innerHTML='<h2>Word 小说阅读器</h2><p>界面引擎：NiaoBlush / novel-disguise 2.15.0，MIT 许可。</p><p>摸鱼盒子提供本地 TXT / EPUB 解析、书库、搜索与阅读进度适配。文件与进度保存在本机，不上传书籍。</p><p>上方 Office 功能区图像为原项目的界面外观；实际阅读操作位于下方工具栏。本阅读器不提供 Word 文档编辑。</p><button type="button" id="word-about-close">关闭</button>';page.append(about);
  const $=id=>document.getElementById(id);
  let toastTimer=0;
  function toast(text){notice.textContent=text;notice.hidden=false;clearTimeout(toastTimer);toastTimer=setTimeout(()=>notice.hidden=true,4000);}
  async function api(path,options={}){const response=await fetch(path,{...options,headers:{...options.headers,'X-Novel-Token':token}});const data=await response.json();if(!response.ok)throw new Error(data.error||'请求未完成');return data;}
  function post(path,data){return api(path,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(data)});}
  function showError(error){toast(error.message||'操作未完成');}
  function setNavigation(){const busy=navigating||!state.ready||state.safe;$('word-prev').disabled=busy||state.index<=0;$('word-next').disabled=busy||!state.chapter||state.index>=state.chapter.count-1;$('word-books').disabled=navigating||state.safe;}
  function setPanel(open){panel.hidden=!open;$('word-library').setAttribute('aria-expanded',String(open));if(open)$('word-books').focus();}
  function position(){
    if(!state.ready||state.safe||!state.bookId)return null;
    const paragraphs=[...document.querySelectorAll('#word-text p')];const top=body.getBoundingClientRect().top;let index=0,offset=0;
    for(let i=0;i<paragraphs.length;i++){const rect=paragraphs[i].getBoundingClientRect();if(rect.bottom>top){index=i;offset=Math.max(0,Math.min(1,(top-rect.top)/Math.max(1,rect.height)));break;}}
    return {bookId:state.bookId,index:state.index,paragraph:index,offset};
  }
  function enqueuePosition(data){
    if(!data)return progressQueue;
    const operation=progressQueue.catch(()=>{}).then(async()=>{
      const book=state.books.find(b=>b.id===data.bookId);
      if(!book)throw new Error('书籍状态尚未就绪，请重新打开书库。');
      const result=await post('/api/progress',{...data,expectedRevision:book.progressRevision||0});
      if(result.conflict){book.progressRevision=result.progressRevision;throw new Error('阅读进度已被另一页面更新，请重新打开章节后继续。');}
      if(!result.ok)throw new Error('阅读进度未保存。');
      book.progressRevision=result.progressRevision;book.lastChapter=data.index;book.lastParagraph=data.paragraph;book.lastOffset=data.offset;
      return result;
    });progressQueue=operation;operation.catch(showError);return operation;
  }
  function savePosition(){clearTimeout(progressTimer);return enqueuePosition(position());}
  function restorePosition(paragraph,offset){const items=document.querySelectorAll('#word-text p');body.scrollTop=0;if(!items.length)return;const item=items[Math.max(0,Math.min(items.length-1,paragraph||0))];const rect=item.getBoundingClientRect();body.scrollTop=rect.top-body.getBoundingClientRect().top+(offset||0)*rect.height;}
  function renderContent(chapter){
    const article=document.createElement('article');article.id='word-document';
    const heading=document.createElement('h1');heading.textContent=chapter?chapter.title:'开始阅读你的文档';article.append(heading);
    const text=document.createElement('div');text.id='word-text';
    const content=chapter?chapter.content:'点击“打开 TXT / EPUB”，选择本地书籍。\n导入后，目录与阅读进度会保存在本机。';
    content.split(/\r?\n/).filter(p=>p.trim()).forEach(value=>{const p=document.createElement('p');p.textContent=value;text.append(p);});article.append(text);renderer.setContent(article);body.scrollTop=0;
    renderer.title('工作文档 - Word');document.title='工作文档 - Word';
    renderer.detail(chapter?`第 ${chapter.index+1} / ${chapter.count} 节　 ·　${content.replace(/\s/g,'').length} 字　 ·　简体中文`:'简体中文（中国大陆）　 ·　本地文档');
    applyFont();
  }
  function applyFont(){const documentEl=$('word-document');if(documentEl)documentEl.style.fontSize=$('word-font').value+'px';}
  function renderChapters(){
    const target=$('word-chapters');target.replaceChildren();state.chapters.forEach(ch=>{const button=document.createElement('button');button.type='button';button.className='chapter-row'+(ch.index===state.index?' active':'');button.textContent=ch.title;button.dataset.index=ch.index;button.onclick=()=>loadChapter(state.bookId,ch.index,true).catch(showError);target.append(button);});
  }
  function renderBooks(){const select=$('word-books');select.replaceChildren();if(!state.books.length){const empty=new Option('尚未导入书籍','');select.add(empty);}state.books.forEach(book=>select.add(new Option(book.title,book.id,false,book.id===state.bookId)));renderChapters();}
  function clearSearch(){++searchGeneration;clearTimeout(searchTimer);$('word-search').value='';$('word-results').replaceChildren();}
  async function loadChapter(bookId,index,reset=false){
    if(state.safe){toast('请先恢复阅读文档');return;}if(!bookId)return;
    const request=++generation;const saved=position();loadingPosition=saved;state.ready=false;navigating=true;setNavigation();clearTimeout(progressTimer);clearSearch();
    try{
      await enqueuePosition(saved);if(request!==generation)return;
      const data=await api('/api/chapter?bookId='+encodeURIComponent(bookId)+'&index='+index);if(request!==generation)return;
      const currentBook=state.books.find(b=>b.id===bookId);if(currentBook)currentBook.progressRevision=data.progressRevision||0;
      // Save the destination before presenting it; ACK and render stay bound to this request.
      const paragraph=!reset&&data.index===data.lastChapter?data.lastParagraph:0,offset=!reset&&data.index===data.lastChapter?data.lastOffset:0;
      await enqueuePosition({bookId,index:data.index,paragraph:paragraph||0,offset:offset||0});if(request!==generation)return;
      state.bookId=bookId;state.index=data.index;state.chapter=data;state.chapters=data.chapters||[];renderContent(data);renderBooks();
      await new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));if(request!==generation)return;
      restorePosition(paragraph,offset);state.ready=true;loadingPosition=null;
    }catch(error){if(request===generation)state.ready=!!state.chapter;throw error;}finally{if(request===generation){navigating=false;setNavigation();}}
  }
  async function loadLibrary(payload){
    state.settings=payload.settings||{};if([14,16,18,20,22,24,26,28,30].includes(state.settings.FontSize))$('word-font').value=state.settings.FontSize;state.books=payload.books||[];state.bookId=payload.currentBookId||'';state.index=payload.currentChapter||0;state.chapters=payload.chapters||[];renderBooks();
    if(state.bookId)await loadChapter(state.bookId,state.index,false);else{renderContent(null);state.ready=false;setNavigation();}
  }
  async function upload(file){
    if(!file)return;if(state.safe){toast('请先恢复阅读文档');return;}
    if(!/\.(txt|epub)$/i.test(file.name))throw new Error('请选择 TXT 或 EPUB 文件');if(file.size>50*1024*1024)throw new Error('文件不能超过 50MB');
    const button=$('word-import');button.disabled=true;let imported=false;await savePosition();state.ready=false;++generation;
    try{const result=await api('/api/import',{method:'POST',headers:{'X-File-Name':encodeURIComponent(file.name)},body:await file.arrayBuffer()});await loadLibrary(result.library);imported=true;toast('书籍已保存到本机');}finally{if(!imported)state.ready=!!state.chapter;setNavigation();button.disabled=false;$('word-file').value='';}
  }
  async function search(query){
    const request=++searchGeneration,book=state.bookId;$('word-results').replaceChildren();if(!query||!book||state.safe)return;
    const result=await api('/api/search?bookId='+encodeURIComponent(book)+'&q='+encodeURIComponent(query));if(request!==searchGeneration||book!==state.bookId||state.safe)return;
    const target=$('word-results');if(!result.results?.length){target.textContent='没有找到匹配内容';return;}
    result.results.forEach(hit=>{const button=document.createElement('button');button.type='button';button.className='search-row';const title=document.createElement('strong');title.textContent=hit.title;const snippet=document.createElement('span');snippet.textContent=hit.snippet;button.append(title,snippet);button.onclick=()=>loadChapter(hit.bookId,hit.index,true).catch(showError);target.append(button);});
  }
  let safePosition=null;
  function toggleSafe(){
    if(!state.safe){safePosition=position()||loadingPosition;enqueuePosition(safePosition);++generation;clearTimeout(progressTimer);navigating=false;state.safe=true;clearSearch();setPanel(false);about.close();renderer.setContent(document.createElement('article'));const paper=$('disguised-content');paper.innerHTML='<article class="safe-document"><h1>项目进度汇总</h1><p>一、本周工作情况</p><p>已完成资料整理、进度复核及相关事项跟踪，后续将按计划推进各项工作。</p><p>二、下一步安排</p><p>继续完善文档，核对关键数据，及时同步工作进展。</p></article>';renderer.detail('第 1 页　 ·　简体中文（中国大陆）');$('word-safe').textContent='恢复文档';page.classList.add('safe-view');
    }else{state.safe=false;page.classList.remove('safe-view');renderContent(state.chapter);restorePosition(safePosition?.paragraph,safePosition?.offset);$('word-safe').textContent='安全文档';state.ready=!!state.chapter;if(!state.chapter&&state.bookId)loadChapter(state.bookId,state.index,false).catch(showError);}setNavigation();
  }
  $('word-import').onclick=()=>{if(state.safe){toast('请先恢复阅读文档');return;}$('word-file').click();};
  $('word-file').onchange=e=>upload(e.target.files[0]).catch(showError);
  $('word-library').onclick=()=>{if(state.safe){toast('请先恢复阅读文档');return;}setPanel(panel.hidden);};$('word-panel-close').onclick=()=>setPanel(false);
  $('word-books').onchange=e=>{const book=state.books.find(b=>b.id===e.target.value);if(book)loadChapter(book.id,book.lastChapter||0,false).catch(showError);};
  $('word-prev').onclick=()=>loadChapter(state.bookId,state.index-1,true).catch(showError);$('word-next').onclick=()=>loadChapter(state.bookId,state.index+1,true).catch(showError);
  $('word-font').onchange=()=>{const saved=position();applyFont();if(saved)restorePosition(saved.paragraph,saved.offset);state.settings.FontSize=Number($('word-font').value);const snapshot={...state.settings};settingsQueue=settingsQueue.catch(()=>{}).then(()=>post('/api/settings',snapshot));settingsQueue.catch(showError);};

  $('word-safe').onclick=toggleSafe;$('word-about').onclick=()=>about.showModal();$('word-about-close').onclick=()=>about.close();
  $('word-search').oninput=e=>{clearTimeout(searchTimer);++searchGeneration;const q=e.target.value.trim();$('word-results').replaceChildren();searchTimer=setTimeout(()=>search(q).catch(showError),200);};
  body.addEventListener('scroll',()=>{if(!state.ready||state.safe)return;clearTimeout(progressTimer);progressTimer=setTimeout(()=>savePosition().catch(()=>{}),220);},{passive:true});
  document.addEventListener('keydown',e=>{if(e.ctrlKey&&e.code==='Backquote'){e.preventDefault();toggleSafe();return;}if(e.code==='Escape'){setPanel(false);about.close();return;}if(e.target.closest('input,select,textarea,[contenteditable]')||state.safe)return;if(e.code==='BracketLeft'&&!$('word-prev').disabled){e.preventDefault();$('word-prev').click();}else if(e.code==='BracketRight'&&!$('word-next').disabled){e.preventDefault();$('word-next').click();}else if(e.ctrlKey&&e.code==='KeyO'){e.preventDefault();$('word-import').click();}});
  window.addEventListener('pagehide',()=>{const data=position();if(data)fetch('/api/progress',{method:'POST',keepalive:true,headers:{'X-Novel-Token':token,'Content-Type':'application/json'},body:JSON.stringify({...data,expectedRevision:state.books.find(b=>b.id===data.bookId)?.progressRevision||0})}).catch(()=>{});});
  document.addEventListener('visibilitychange',()=>{if(document.hidden)savePosition().catch(()=>{});});
  window.MoyuWordReader={state,loadChapter,upload,savePosition,toggleSafe,renderer:renderer.upstream};
  renderContent(null);setNavigation();api('/api/library').then(loadLibrary).then(()=>page.dataset.loaded='true').catch(error=>{page.dataset.loaded='error';showError(error);});
};
