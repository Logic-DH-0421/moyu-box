async function cacheItemAction(wanted, action) {
  const bridge=window.biliBridgePc;
  const keys=['bvid','aid','cid','item_id'].filter(k=>wanted[k]&&wanted[k]!=='0');
  if(!keys.length)throw Error('缺少缓存任务身份，本次未操作');
  const identity=task=>String(task.itemId||task.item_id||task.cid||'');
  const matches=task=>keys.every(k=>String(k==='item_id'?(task.itemId||task.item_id||''):(task[k]||''))===wanted[k]);
  async function read(){
    const [groups,tasks]=await Promise.all(['download/getCompletedGroups','download/getTaskList'].map(async method=>{
      const response=await bridge.callNative(method);if(!Array.isArray(response?.data))throw Error('客户端未返回有效缓存列表，本次未操作');return response.data;
    }));
    const rows=[];
    function visit(task,completed,parent={}){
      if(Array.isArray(task.itemList)){task.itemList.forEach(child=>visit(child,completed,task));return;}
      rows.push({...task,...task.video,groupId:task.groupId??parent.groupId,completed});
    }
    groups.forEach(task=>visit(task,true));tasks.forEach(task=>visit(task,false));
    const unique=new Map();for(const row of rows){const id=identity(row);if(!id)throw Error('缓存条目缺少itemId，本次未操作');if(!unique.has(id)||row.completed)unique.set(id,row);}return [...unique.values()];
  }
  const before=await read(),found=before.filter(matches);
  if(found.length!==1)throw Error(found.length?'缓存身份对应多个条目，本次未操作':'客户端缓存中没有找到匹配的BV/aid/cid，本次未操作');
  const target=found[0],id=identity(target),itemId=target.itemId||target.item_id;
  if(!itemId)throw Error('缺少官方itemId，本次未操作');
  if(action==='danmaku'){
    if(!target.completed)throw Error('只有已完成的视频可以更新弹幕');
    await bridge.callNative('download/updateItemDmInfo',{itemId});return {confirmed:true,itemId};
  }
  if(action!=='delete')throw Error('未知缓存操作');
  if(target.completed){
    if(!target.groupId)throw Error('缺少合集groupId，本次未删除');
    await bridge.callNative('download/deleteGroupItems',{groupId:target.groupId,itemIds:[itemId]});
  }else await bridge.callNative('download/deleteTasks',{taskIds:[itemId]});
  for(let attempt=0;attempt<12;attempt++){
    const after=await read();
    if(!after.some(row=>identity(row)===id)){
      const remaining=new Set(after.map(identity));
      if(before.some(row=>identity(row)!==id&&!remaining.has(identity(row))))throw Error('检测到其他条目同时发生变化，请核对客户端缓存列表');
      return {confirmed:true,itemId,remainingCount:after.length};
    }
    await new Promise(resolve=>setTimeout(resolve,250));
  }
  throw Error('客户端未确认目标缓存消失，请同步状态；本次不重复删除');
}
