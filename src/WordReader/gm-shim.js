/* Local browser compatibility only. No userscript installation or native privileges. */
window.GM_addStyle=function(css){const style=document.createElement('style');style.textContent=css;document.head.append(style);};
window.GM_getValue=function(key,fallback){try{return JSON.parse(localStorage.getItem('moyu-word:'+key))??fallback;}catch{return fallback;}};
window.GM_setValue=function(key,value){localStorage.setItem('moyu-word:'+key,JSON.stringify(value));};
window.GM_registerMenuCommand=function(){};
