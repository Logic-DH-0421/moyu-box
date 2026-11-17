const test=require('node:test');
const assert=require('node:assert/strict');
const core=require('../src/Shell/core');
test('website admission rejects active content, credentials and remote HTTP',()=>{
 for(const url of ['javascript:alert(1)','file:///C:/Windows/win.ini','http://example.com','https://user:password@example.com','not a url'])assert.equal(core.website(url),false,url);
 for(const url of ['https://example.com/工具','http://127.0.0.1:17890/','http://localhost:8080/'])assert.equal(core.website(url),true,url);
});
test('project labels are escaped before markup rendering',()=>assert.equal(core.escape('<img src=x onerror="alert(1)">'), '&lt;img src=x onerror=&quot;alert(1)&quot;&gt;'));
test('fishpond classification includes custom project types and disabled entries',()=>{
 const list=[{name:'时节',kind:'shijie',description:'计时'},{name:'我的工具',kind:'local-web',description:'转换',enabled:false},{name:'网站',kind:'website',description:'工具'}];
 assert.deepEqual(core.filterProjects(list,'local','工具'),[list[1]]);
 assert.deepEqual(core.filterProjects(list,'web',''),[list[2]]);
 assert.equal(core.sectionFor('project'),'pond');
 assert.equal(core.statusLabel({...list[1],enabled:false},{status:'ready'}),'已停用');
});

test('GitHub is searchable as web and electronic books as local',()=>{
 const gh={name:'GitHub',kind:'github',description:'仓库'},book={name:'小说',kind:'book',description:'EPUB'};
 assert.deepEqual(core.filterProjects([gh,book],'web',''),[gh]);
 assert.deepEqual(core.filterProjects([gh,book],'local',''),[book]);
});
