(function(root){
 'use strict';
 const key=d=>d.getFullYear()+'-'+String(d.getMonth()+1).padStart(2,'0')+'-'+String(d.getDate()).padStart(2,'0');
 const day=s=>new Date(s+'T00:00:00');
 let weekStart=1;
 function setWeekStart(value){weekStart=Number.isInteger(+value)&&+value>=0&&+value<=6?+value:1;}
 function duration(value){
  const s=String(value).trim().toLowerCase().replace(/：/g,':');
  let m=s.match(/^(\d+)(?:h|小时)(?:(\d+)(?:min|分))?$/);
  if(m)return +m[1]*3600+(+m[2]||0)*60;
  m=s.match(/^(\d+)(?:min|分钟|分)$/);if(m)return +m[1]*60;
  m=s.match(/^(\d{2,}):?(\d{2})$/);return m&&+m[2]<60?+m[1]*3600+(+m[2])*60:NaN;
 }
 function slices(r,now=new Date()){
  if(!r.start)return [{date:r.assigned,seconds:Math.max(0,+r.manualDuration||0),id:r.id,category:r.category}];
  let a=new Date(r.start),b=r.end?new Date(r.end):now,out=[];
  if(!Number.isFinite(+a)||!Number.isFinite(+b)||b<a)return out;
  if(+a===+b)return [{date:key(a),seconds:0,id:r.id,category:r.category}];
  while(a<b){let next=new Date(a);next.setHours(24,0,0,0);let end=new Date(Math.min(+next,+b));out.push({date:key(a),seconds:(end-a)/1000,id:r.id,category:r.category});a=end;}
  return out;
 }
 function bucket(date,unit){let d=day(date);if(unit==='week')d.setDate(d.getDate()-(d.getDay()-weekStart+7)%7);if(unit==='month')d.setDate(1);return key(d);}
 function aggregate(records,unit='day',includeZero=false,range=null,now=new Date()){
  const map={};records.forEach(r=>slices(r,now).forEach(s=>{if(range&&(s.date<range[0]||s.date>=range[1]))return;const b=bucket(s.date,unit);map[b]=(map[b]||0)+s.seconds;}));
  if(includeZero){const keys=Object.keys(map).sort();let a=range?range[0]:keys[0],b=range?range[1]:keys[keys.length-1];if(a&&b){for(let d=day(a);range?key(d)<b:key(d)<=b;d.setDate(d.getDate()+1)){let k=bucket(key(d),unit);if(!(k in map))map[k]=0;}}}
  return Object.keys(map).sort().map(date=>({date,seconds:map[date]}));
 }
 function metrics(values){return {total:values.reduce((a,b)=>a+b,0),min:values.length?Math.min(...values):0,max:values.length?Math.max(...values):0,avg:values.length?values.reduce((a,b)=>a+b,0)/values.length:0};}
 function diff(a,b){return Math.round((Date.UTC(...a.split('-').map((n,i)=>+n-(i===1?1:0)))-Date.UTC(...b.split('-').map((n,i)=>+n-(i===1?1:0))))/86400000);}
 const lunarFormatter=new Intl.DateTimeFormat('zh-CN-u-ca-chinese',{year:'numeric',month:'numeric',day:'numeric'});
 function lunar(date){const p=lunarFormatter.formatToParts(day(date)),get=t=>(p.find(p=>p.type===t)||{}).value;let m=get('month')||'';return {year:+get('relatedYear'),month:m,day:+get('day')};}
 const recurrenceCache=new Map();
 function nextDate(event,today=key(new Date())){
  if(!event.repeat||event.repeat==='none')return event.date;
  const base=day(event.date),now=day(today);if(base>=now)return event.date;
  if(event.calendar==='lunar'&&(event.repeat==='year'||event.repeat==='month')){
   const cacheKey=JSON.stringify([event.date,event.repeat,event.leapPolicy||'exact',today]);
   if(recurrenceCache.has(cacheKey))return recurrenceCache.get(cacheKey);
   const target=lunar(event.date),month=event.leapPolicy==='regular'?target.month.replace(/^闰/,''):target.month;
   const limit=new Date(now);limit.setFullYear(limit.getFullYear()+100);let result=null;
   for(let d=new Date(now);d<limit;d.setDate(d.getDate()+1)){
    const p=lunar(key(d));if(event.repeat==='year'&&p.month!==month)continue;
    if(p.day===target.day){result=key(d);break}
    if(p.day<target.day&&p.day>=29){const next=new Date(d);next.setDate(next.getDate()+1);if(lunar(key(next)).day===1){result=key(d);break}}
   }
   if(recurrenceCache.size>=256)recurrenceCache.clear();recurrenceCache.set(cacheKey,result);return result;
  }
  if(event.repeat==='week'){let d=new Date(base);d.setDate(d.getDate()+Math.ceil(diff(today,event.date)/7)*7);return key(d);}
  let y=now.getFullYear(),m=event.repeat==='year'?base.getMonth():now.getMonth();
  for(let i=0;i<15;i++){let d=new Date(y,m,Math.min(base.getDate(),new Date(y,m+1,0).getDate()));if(d>=now)return key(d);if(event.repeat==='year')y++;else m++;}return event.date;
 }
 function count(event,today=key(new Date())){const date=nextDate(event,today);if(!date)return {date:null,days:null};let n=diff(date,today);if(event.includeToday&&n!==0)n+=n>0?1:-1;return {date,days:n};}
 const api={key,day,duration,slices,bucket,aggregate,metrics,diff,lunar,nextDate,count,setWeekStart};
 if(typeof module!=='undefined')module.exports=api;root.ShijieCore=api;
})(typeof window==='undefined'?globalThis:window);
