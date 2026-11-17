/* Opt-in layout constraints for locally imported pages. No source-file rewriting. */
(function(enabled){
  const id='__moyubox_fit_width';let style=document.getElementById(id);
  if(enabled&&!style){style=document.createElement('style');style.id=id;style.textContent='html,body{box-sizing:border-box!important;min-width:0!important;max-width:100%!important}body{margin-left:0!important;margin-right:0!important}canvas,video,iframe,img{max-width:100%!important;box-sizing:border-box!important}body>div{max-width:100%!important;box-sizing:border-box!important}[data-moyubox-fit-edge]{max-width:var(--moyubox-fit-edge)!important}';(document.head||document.documentElement).append(style);}
  if(!enabled&&style)style.remove();
  if(!enabled&&window.__moyuboxFitEdge){window.removeEventListener('resize',window.__moyuboxFitEdge);window.__moyuboxFitEdge=null;document.querySelectorAll('[data-moyubox-fit-edge]').forEach(e=>{e.removeAttribute('data-moyubox-fit-edge');e.style.removeProperty('--moyubox-fit-edge');});}
  if(enabled){
    if(!window.__moyuboxFitEdge){window.__moyuboxFitEdge=function(){
      document.querySelectorAll('[data-moyubox-fit-edge]').forEach(e=>{e.removeAttribute('data-moyubox-fit-edge');e.style.removeProperty('--moyubox-fit-edge');});
      [...document.querySelectorAll('canvas,video,iframe,img,div,section,main')].slice(0,2000).forEach(e=>{const r=e.getBoundingClientRect(),s=getComputedStyle(e);if((s.position==='absolute'||s.position==='fixed')&&r.width>=innerWidth-1&&r.left>=0&&r.right>innerWidth+1){e.setAttribute('data-moyubox-fit-edge','');e.style.setProperty('--moyubox-fit-edge',Math.max(0,innerWidth-r.left)+'px');}});
    };window.addEventListener('resize',window.__moyuboxFitEdge);}
    window.__moyuboxFitEdge();
  }
  return JSON.stringify({width:innerWidth,scroll:Math.max(document.documentElement.scrollWidth,document.body?document.body.scrollWidth:0)});
})
