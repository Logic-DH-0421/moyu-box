(function(root,factory){const api=factory();if(typeof module==='object'&&module.exports)module.exports=api;else root.MoyuCore=api;})(typeof globalThis!=='undefined'?globalThis:this,function(){
  'use strict';
  function escape(value){return String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));}
  function filterProjects(projects,filter,query){const q=String(query||'').trim().toLocaleLowerCase();return projects.filter(p=>(filter==='all'||(filter==='web'?['website','github'].includes(p.kind):!['website','github'].includes(p.kind)))&&(!q||(p.name+' '+p.description).toLocaleLowerCase().includes(q)));}
  function sectionFor(route){return route==='project'?'pond':route;}
  function statusLabel(project,session){if(!project.enabled)return '已停用';return ({loading:'正在打开',ready:'已打开 · 保留状态',external:'独立窗口',error:'打开失败'})[session?.status]||(project.kind==='website'?'在线网站':'按需打开');}
  function website(value){try{const u=new URL(value);return !u.username&&!u.password&&(u.protocol==='https:'||(u.protocol==='http:'&&['127.0.0.1','localhost','[::1]'].includes(u.hostname)));}catch{return false;}}
  return {escape,filterProjects,sectionFor,statusLabel,website};
});
