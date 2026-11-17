using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace MoyuBox
{
    internal sealed partial class MainWindow
    {
        readonly List<string> checks = new List<string>();
        void Check(bool condition, string label) { if (!condition) throw new Exception("验证失败：" + label); checks.Add(label); }
        async Task WaitFor(Func<bool> condition, string label, int ticks = 150)
        { for (int i = 0; i < ticks; i++) { if (condition()) return; await Task.Delay(100); } throw new TimeoutException(label); }
        async Task WaitScript(CoreWebView2 core, string script, string label, int ticks = 120)
        { for (int i = 0; i < ticks; i++) { if (await core.ExecuteScriptAsync(script) == "true") return; await Task.Delay(100); } throw new TimeoutException(label); }
        async Task RunIntegration()
        {
            try
            {
                await Task.Delay(600);
                if (Array.IndexOf(Environment.GetCommandLineArgs(), "--websites-only-test") >= 0) { await VerifyBuiltinSites(); await FinishIntegration(true, null); return; }
                if (Array.IndexOf(Environment.GetCommandLineArgs(), "--feed-only-test") >= 0) { await rankingFeed.Refresh(Publish); Check(rankingFeed.status=="ready", "真实 GitHub 趋势读取完成"); await shell.CoreWebView2.ExecuteScriptAsync("document.querySelector('.sidebar [data-section=rankings]').click()"); await WaitScript(shell.CoreWebView2,"document.querySelectorAll('.ranking-row').length>0","榜单内容显示在框架内"); Check(true,"来源条目在框架中直接显示"); using(var file=File.Create(Path.Combine(store.Root,"feed.png"))) await shell.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,file); await FinishIntegration(true,null); return; }

                if (Array.IndexOf(Environment.GetCommandLineArgs(), "--rankings-only-test") >= 0) { await VerifyRankings(); await FinishIntegration(true, null); return; }
                Check(WindowState==FormWindowState.Normal && Width<=1040,"启动使用紧凑普通窗口");
                Check(await shell.CoreWebView2.ExecuteScriptAsync("document.documentElement.scrollWidth<=innerWidth+1")=="true","紧凑首页无整体横向溢出");
                Check(await shell.CoreWebView2.ExecuteScriptAsync("document.querySelectorAll('[data-open]').length") == "12", "鱼塘首页按十二项分页");
                Check(await shell.CoreWebView2.ExecuteScriptAsync("document.querySelectorAll('.sidebar [data-open]').length") == "0", "项目均位于鱼塘内容区");
                using (var file = File.Create(Path.Combine(store.Root, "01-pond.png"))) await shell.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
                await shell.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-open=shijie]').click()");
                await WaitFor(() => sessions.Items.ContainsKey("shijie") && sessions.Items["shijie"].Status == "ready", "时节加载");
                Check(WindowState==FormWindowState.Maximized,"进入盒内项目自动最大化");
                var time = sessions.Items["shijie"].View;
                await Task.Delay(500);
                Check(await time.CoreWebView2.ExecuteScriptAsync("typeof window.ShijieTest === 'object'") == "true", "时节真实程序初始化");
                Check(time.Visible && time.Width > 500 && time.Height > 300, "原生项目容器可见且布局有效");
                await time.CoreWebView2.ExecuteScriptAsync("localStorage.setItem('moyu-integration-marker','shijie-only')");
                using (var file = File.Create(Path.Combine(store.Root, "02-shijie.png"))) await time.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
                await shell.CoreWebView2.ExecuteScriptAsync("document.querySelector('.sidebar [data-section=chat]').click()");
                await WaitFor(() => sessions.ActiveId == null, "离开项目时隐藏项目容器");
                Check(!time.Visible && !time.IsDisposed, "返回框架时保留项目实例");
                Check(WindowState==FormWindowState.Normal && Width<=1040,"返回功能页恢复紧凑窗口");
                Check(await shell.CoreWebView2.ExecuteScriptAsync("document.querySelectorAll('[data-tab=shijie]').length") == "1", "跨功能保留项目标签页");
                Check(await shell.CoreWebView2.ExecuteScriptAsync("document.querySelector('.toolbar')===null") == "true", "已删除原顶部工具栏");
                await shell.CoreWebView2.ExecuteScriptAsync("document.querySelector('.sidebar [data-section=pond]').click()");
                await Task.Delay(150);
                await shell.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-open=shijie]').click()");
                await WaitFor(() => time.Visible, "恢复时节实例");
                Check(await time.CoreWebView2.ExecuteScriptAsync("localStorage.getItem('moyu-integration-marker')") == "\"shijie-only\"", "项目状态在切换后保留");
                Check(await shell.CoreWebView2.ExecuteScriptAsync("document.querySelectorAll('[data-tab=shijie]').length") == "1", "重复打开同一项目复用标签页");
                await shell.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-action=favorite]').click()");
                await WaitFor(() => store.State.favoriteIds.Contains("shijie"), "收藏保存");
                await shell.CoreWebView2.ExecuteScriptAsync("document.querySelector('.sidebar [data-section=gear]').click()");
                await WaitFor(() => sessions.ActiveId == null, "进入渔具"); await Task.Delay(200);
                Check(await shell.CoreWebView2.ExecuteScriptAsync("document.querySelectorAll('[data-open=shijie]').length===0 && !!document.getElementById('profile-form')") == "true", "鱼苗仅显示资料，收藏已移动到鱼塘");
                await shell.CoreWebView2.ExecuteScriptAsync("document.getElementById('display-name').value='标签页测试鱼';document.getElementById('profile-form').requestSubmit()");
                await WaitFor(() => store.State.displayName == "标签页测试鱼", "资料持久化");
                using (var file = File.Create(Path.Combine(store.Root, "05-gear.png"))) await shell.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
                await shell.CoreWebView2.ExecuteScriptAsync("document.querySelector('.sidebar [data-section=pond]').click()");await Task.Delay(150);
                await shell.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-open=reader]').click()");
                await WaitFor(() => sessions.Items.ContainsKey("reader") && sessions.Items["reader"].Status == "ready", "第二标签页启动");
                Check(await shell.CoreWebView2.ExecuteScriptAsync("document.querySelectorAll('[data-tab]').length") == "2", "两个项目拥有独立标签页");
                await shell.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-tab=shijie]').click()");await WaitFor(() => time.Visible, "标签页切回时节");
                await shell.CoreWebView2.ExecuteScriptAsync("document.querySelector('.sidebar [data-section=pond]').click()");await WaitFor(() => sessions.ActiveId == null, "标签页保留时返回鱼塘");await Task.Delay(200);
                using (var file = File.Create(Path.Combine(store.Root, "06-tabs.png"))) await shell.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
                await shell.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-tab=shijie]').click()");await WaitFor(() => sessions.ActiveId == "shijie", "再次选择活动标签页");
                await shell.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-close-tab=shijie]').click()");
                await WaitFor(() => !sessions.Items.ContainsKey("shijie") && sessions.ActiveId == "reader", "关闭活动标签页自动切换相邻项目");
                Check(time.IsDisposed && sessions.Items["reader"].View.Visible, "关闭仅释放目标项目实例");
                await sessions.HideForTest();
                await sessions.Open("reader");
                var reader = sessions.Items["reader"].View;
                Check(sessions.Items["reader"].Status == "ready", "阅读器真实服务和界面加载");
                await Task.Delay(300);
                Check(await reader.CoreWebView2.ExecuteScriptAsync("localStorage.getItem('moyu-integration-marker')") == "null", "项目浏览存储相互隔离");
                string importScript = "fetch('/api/import',{method:'POST',headers:{'X-Novel-Token':document.querySelector('meta[name=novel-token]').content,'X-File-Name':encodeURIComponent('框架验证.txt')},body:new TextEncoder().encode('第一章 初见\\n这是一份独立测试书籍。\\n第二章 回来\\n测试阅读器的真实导入。')}).then(r=>r.json()).then(x=>window.__moyuImport=x).catch(e=>window.__moyuImport={error:e.message})";
                await reader.CoreWebView2.ExecuteScriptAsync(importScript);
                for (int i = 0; i < 50; i++) { if (await reader.CoreWebView2.ExecuteScriptAsync("!!window.__moyuImport") == "true") break; await Task.Delay(100); }
                Check((await reader.CoreWebView2.ExecuteScriptAsync("window.__moyuImport && window.__moyuImport.ok")) == "true", "阅读器真实接口导入 TXT 成功");
                using (var file = File.Create(Path.Combine(store.Root, "03-reader.png"))) await reader.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
                await sessions.Open("bilibili");
                var bili = sessions.Items["bilibili"];
                Check(bili.Status == "ready", "B站真实适配服务及界面加载");
                await Task.Delay(1000);
                await bili.View.CoreWebView2.ExecuteScriptAsync("window.pywebview.api.get_state({}).then(s=>window.__moyuState=s)");
                for (int i = 0; i < 50; i++) { if (await bili.View.CoreWebView2.ExecuteScriptAsync("!!window.__moyuState") == "true") break; await Task.Delay(100); }
                Check(await bili.View.CoreWebView2.ExecuteScriptAsync("!!window.__moyuState && window.__moyuState.config.launch_client_on_startup === false") == "true", "B站真实接口可读，启动时不接管官方客户端");
                using (var file = File.Create(Path.Combine(store.Root, "04-bilibili.png"))) await bili.View.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
                string fixture = store.ProjectRoot("integration-web"); Directory.CreateDirectory(Path.Combine(fixture, "package"));
                File.WriteAllText(Path.Combine(fixture, "package", "assets.css"), "body{color:blue}");
                File.WriteAllText(Path.Combine(fixture, "package", "测试 #1.html"), "<!doctype html><html><body><h1 id='test'>第六个项目</h1></body></html>", Encoding.UTF8);
                store.Add(new Project { id = "integration-web", name = "第六个项目", kind = "local-web", entry = "测试 #1.html" });
                await sessions.Open("integration-web");
                var sixth = sessions.Items["integration-web"];
                Check(sixth.Status == "ready" && await sixth.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('test').textContent") == "\"第六个项目\"", "第六个项目由配置加载，无需修改主程序");
                Check(!sixth.View.CoreWebView2.Settings.IsWebMessageEnabled, "普通网页项目不获得原生桥接权限");
                await sessions.Open("shijie");
                Check(await sessions.Items["shijie"].View.CoreWebView2.ExecuteScriptAsync("localStorage.getItem('moyu-integration-marker')") == "\"shijie-only\"", "关闭后重开仍保留项目磁盘数据");
                string staticUrl = await sessions.BrowserUrl("integration-web");
                string staticContent = await Task.Run(delegate { using (var web = new System.Net.WebClient()) return web.DownloadString(staticUrl); });
                Check(staticContent.Contains("id='test'"), "静态项目的浏览器地址可通过真实 HTTP 访问");
                string assetContent = await Task.Run(delegate { using (var web = new System.Net.WebClient()) return web.DownloadString(new Uri(new Uri(staticUrl), "/assets.css")); });
                Check(assetContent.Contains("color:blue"), "静态浏览器模式支持根路径资源");
                bool originBlocked = await Task.Run(delegate { try { using (var web = new System.Net.WebClient()) { web.Headers["Origin"] = "https://example.com"; web.DownloadString(staticUrl); } return false; } catch (System.Net.WebException error) { if (error.Response != null) error.Response.Close(); return true; } });
                Check(originBlocked, "静态服务拒绝外部 Origin");
                string badUrl = staticUrl.Substring(0, staticUrl.LastIndexOf('/') + 1) + "%2e%2e%5csettings.json";
                bool blocked = await Task.Run(delegate { try { using (var web = new System.Net.WebClient()) web.DownloadString(badUrl); return false; } catch (System.Net.WebException) { return true; } });
                Check(blocked, "静态服务器阻止目录越界读取");
                Check(sessions.ActiveId == "shijie", "获取浏览器地址不改变活动标签页");
                store.Add(new Project { id = "integration-github", name = "GitHub 示例", kind = "github", url = "https://github.com/owner/repo", launchUrl = staticUrl });
                Check(await sessions.BrowserUrl("integration-github") == staticUrl, "GitHub 运行入口解析到部署地址");
                string txt = Path.Combine(store.Root, "import-fixture.txt"); File.WriteAllText(txt, "第一章 开始\n测试 TXT 项目入口与独立书库。", Encoding.UTF8);
                Project bookProject = AddBook(txt); await sessions.Open(bookProject.id);
                Check(sessions.Items[bookProject.id].Status == "ready", "TXT 文件作为独立项目在真实阅读器打开");
                Check(new RegistryStore(store.Root, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "projects")).State.favoriteIds.Contains("shijie"), "收藏在重新读取设置后仍然存在");
                string epubPath = Path.Combine(store.Root, "import-fixture.epub");
                using (var zipFile = File.Create(epubPath)) using (var zip = new ZipArchive(zipFile, ZipArchiveMode.Create))
                {
                    ZipText(zip, "mimetype", "application/epub+zip");
                    ZipText(zip, "META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container' version='1.0'><rootfiles><rootfile full-path='OEBPS/content.opf' media-type='application/oebps-package+xml'/></rootfiles></container>");
                    ZipText(zip, "OEBPS/content.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='3.0'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:title>EPUB 入口验证</dc:title></metadata><manifest><item id='ch1' href='chapter.xhtml' media-type='application/xhtml+xml'/></manifest><spine><itemref idref='ch1'/></spine></package>");
                    ZipText(zip, "OEBPS/chapter.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'><head><title>第一章</title></head><body><h1>第一章</h1><p>这是 EPUB 真实正文，用来验证项目入口。</p></body></html>");
                }
                Project epubProject = AddBook(epubPath); await sessions.Open(epubProject.id);
                Check(sessions.Items[epubProject.id].Status == "ready", "EPUB 文件作为独立项目解析并打开");
                string readerUrl = await sessions.BrowserUrl(epubProject.id);
                string readerHtml = await Task.Run(delegate { using (var web = new System.Net.WebClient()) return web.DownloadString(readerUrl); });
                Check(readerHtml.Contains("novel-token"), "电子书的默认浏览器地址能提供真实阅读界面");
                string fixtureSource = Path.Combine(store.Root, "Fixture.cs"), fixtureExe = Path.Combine(store.Root, "Fixture.exe");
                File.WriteAllText(fixtureSource, "class Fixture { static void Main(){System.IO.File.WriteAllText(System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory,\"exe-started.txt\"),\"ok\");} }", Encoding.UTF8);
                var compileInfo = new ProcessStartInfo(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "csc.exe"), "/nologo /target:winexe /out:\"" + fixtureExe + "\" \"" + fixtureSource + "\"") { UseShellExecute = false, CreateNoWindow = true };
                using (var compiler = Process.Start(compileInfo)) { compiler.WaitForExit(); Check(compiler.ExitCode == 0, "生成隔离 EXE 测试程序"); }
                store.Add(new Project { id = "integration-exe", name = "EXE 验证", kind = "external", entry = fixtureExe });await sessions.Open("integration-exe");
                await WaitFor(() => File.Exists(Path.Combine(store.Root, "exe-started.txt")), "EXE 实际运行");
                Check(sessions.Items["integration-exe"].Status == "external", "EXE 入口成功启动真实独立进程");
                await VerifyWordReader(bookProject.id, epubProject.id);
                await VerifyCompactWindows(epubProject.id);
                if (Array.IndexOf(Environment.GetCommandLineArgs(), "--projects-test") >= 0) await VerifyProjects();
                if (Array.IndexOf(Environment.GetCommandLineArgs(), "--logging-test") >= 0) await VerifyHostDiagnostics();
                var onlineFailures = new List<string>();
                if (Array.IndexOf(Environment.GetCommandLineArgs(), "--online-test") >= 0)
                {
                    foreach (string id in new[] { "lks", "toolbox365" })
                    {
                        await sessions.Open(id); var site = sessions.Items[id];
                        if (site.Status != "ready") { onlineFailures.Add(id + ": " + site.Error); continue; }
                        bool readable = false;
                        for (int i = 0; i < 100; i++) { if (await site.View.CoreWebView2.ExecuteScriptAsync("document.body.innerText.length > 200") == "true") { readable = true; break; } await Task.Delay(150); }
                        if (readable) Check(true, id + " 真实网站在原生容器加载且内容可读");
                        else onlineFailures.Add(id + ": 页面导航完成，但正文长度未达验证阈值");
                        using (var file = File.Create(Path.Combine(store.Root, "website-" + id + ".png"))) await site.View.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
                    }
                }
                if (onlineFailures.Count > 0) { await FinishIntegration(false, "在线网站验证未通过：" + String.Join("；", onlineFailures)); return; }
                await FinishIntegration(true, null);
            }
            catch (Exception error) { Task finishing = FinishIntegration(false, error.ToString()); }
        }
        async Task VerifyWordReader(string txtId, string epubId)
        {
            await sessions.Open(txtId);
            var txt = sessions.Items[txtId].View.CoreWebView2;
            await WaitScript(txt, "!!window.MoyuWordReader && window.MoyuWordReader.state.ready", "TXT 默认 Word 界面就绪");
            Check(await txt.ExecuteScriptAsync("document.getElementById('disguised-page').dataset.reader==='novel-disguise'") == "true", "TXT 默认使用真实 upstream Word 渲染引擎");
            await sessions.Open(epubId); var epub = sessions.Items[epubId].View.CoreWebView2;
            await WaitScript(epub, "!!window.MoyuWordReader && window.MoyuWordReader.state.ready", "EPUB 默认 Word 界面就绪");
            Check(await epub.ExecuteScriptAsync("document.getElementById('word-text').innerText.includes('真实正文')") == "true", "EPUB 默认以 Word 文档展示解析后的真实正文");
            string browserUrl = await sessions.BrowserUrl(epubId);
            string browserEngine = await Task.Run(delegate { using (var web = new System.Net.WebClient()) return web.DownloadString(new Uri(new Uri(browserUrl), "/word/engine.js")); });
            Check(browserEngine.Contains("NiaoBlush") && browserEngine.Contains("MoyuWordAdapter"), "默认浏览器地址提供本地 Word 引擎资源");
            await sessions.Open("word-reader"); var word = sessions.Items["word-reader"].View.CoreWebView2;
            await WaitScript(word, "!!window.MoyuWordReader && document.getElementById('disguised-page').dataset.loaded==='true'", "Word 独立入口加载");
            Check(!sessions.Items["word-reader"].View.CoreWebView2.Settings.IsWebMessageEnabled,"Word 项目不获得原生桥接权限");
            Check(await word.ExecuteScriptAsync("MoyuWordReader.renderer==='NiaoBlush/novel-disguise@2.15.0'") == "true", "独立入口实际执行 MIT 源码的 Word 界面");
            string import = "MoyuWordReader.upload(new File([['第一章 文件验证',...Array.from({length:45},(_,i)=>'本地验证正文第'+i+'段。'+('这是用于测试滚动恢复的正文内容。'.repeat(5))),'第二章 搜索验证','关键线索在第二章。<img src=x onerror=alert(1)>'].join('\\n')],'Word原生验证.txt',{type:'text/plain'})).then(()=>window.__wordImported=true).catch(e=>window.__wordError=e.message)";
            await word.ExecuteScriptAsync(import);
            await WaitScript(word, "!!window.__wordImported", "Word 原生文件导入");
            Check(await word.ExecuteScriptAsync("MoyuWordReader.state.ready && MoyuWordReader.state.chapter.count===2 && document.getElementById('word-text').innerText.includes('本地验证正文')") == "true", "Word 界面通过真实后端导入 TXT 并识别两章");
            await word.ExecuteScriptAsync("document.getElementById('word-font').value='20';document.getElementById('word-font').dispatchEvent(new Event('change'))");
            await word.ExecuteScriptAsync("document.getElementById('word-next').click()");await WaitScript(word, "MoyuWordReader.state.ready && MoyuWordReader.state.index===1", "Word 下一章");
            Check(await word.ExecuteScriptAsync("document.getElementById('word-text').innerText.includes('关键线索') && !document.querySelector('#word-text img')") == "true", "Word 翻章呈现真实正文且按纯文本处理 HTML");
            await word.ExecuteScriptAsync("document.getElementById('word-prev').click()");await WaitScript(word, "MoyuWordReader.state.ready && MoyuWordReader.state.index===0", "Word 上一章");
            await word.ExecuteScriptAsync("document.getElementById('word-library').click();document.getElementById('word-search').value='关键线索';document.getElementById('word-search').dispatchEvent(new Event('input',{bubbles:true}))");
            await WaitScript(word, "!!document.querySelector('.search-row')", "Word 搜索真实章节");
            await word.ExecuteScriptAsync("document.querySelector('.search-row').click()");await WaitScript(word, "MoyuWordReader.state.ready && MoyuWordReader.state.index===1", "Word 搜索跳转");
            Check(true, "Word 搜索结果能跳转到对应章节");
            await word.ExecuteScriptAsync("document.getElementById('word-prev').click()");await WaitScript(word, "MoyuWordReader.state.ready && MoyuWordReader.state.index===0", "返回长章节");
            await word.ExecuteScriptAsync("document.getElementById('word-panel-close').click();document.getElementById('disguised-body').scrollTop=1000;MoyuWordReader.savePosition().then(()=>window.__wordSaved=true)");await WaitScript(word, "!!window.__wordSaved", "Word 保存进度");
            Check(await word.ExecuteScriptAsync("document.getElementById('disguised-body').scrollTop>500") == "true", "Word 文档实际支持长章节滚动");
            await word.ExecuteScriptAsync("window.__wordFetch=window.fetch;window.fetch=async function(input,options){const response=await window.__wordFetch(input,options);if(String(input).startsWith('/api/chapter')){window.__wordLateResponse=true;MoyuWordReader.toggleSafe();await new Promise(r=>setTimeout(r,300));}return response;};MoyuWordReader.loadChapter(MoyuWordReader.state.bookId,1,true)");await WaitScript(word,"!!window.__wordLateResponse", "真实章节响应返回后切换安全文档");await Task.Delay(600);
            await word.ExecuteScriptAsync("window.fetch=window.__wordFetch");
            Check(await word.ExecuteScriptAsync("MoyuWordReader.state.safe && document.body.innerText.includes('项目进度汇总') && !document.body.innerText.includes('本地验证正文') && !document.body.innerText.includes('关键线索')") == "true", "安全文档阻止切章中的迟到正文重新显露");
            await word.ExecuteScriptAsync("MoyuWordReader.toggleSafe()");
            Check(await word.ExecuteScriptAsync("!MoyuWordReader.state.safe && document.body.innerText.includes('本地验证正文')") == "true", "安全切换后可恢复原阅读文档");
            await word.ExecuteScriptAsync("document.getElementById('disguised-body').scrollTop=1000;MoyuWordReader.savePosition().then(()=>window.__wordSavedAgain=true)");await WaitScript(word, "!!window.__wordSavedAgain", "保存最终段落");
            using (var file = File.Create(Path.Combine(store.Root, "07-word-reader.png"))) await sessions.Items["word-reader"].View.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
            Check(await word.ExecuteScriptAsync("performance.getEntriesByType('resource').every(x=>new URL(x.name,location.href).hostname===location.hostname||x.name.startsWith('data:'))") == "true", "Word 阅读所有运行资源来自本地，无 CDN 依赖");
            await sessions.Close("word-reader");await sessions.Open("word-reader");word=sessions.Items["word-reader"].View.CoreWebView2;
            await WaitScript(word, "!!window.MoyuWordReader && MoyuWordReader.state.ready", "重开 Word 书库");
            Check(await word.ExecuteScriptAsync("MoyuWordReader.state.index===0 && document.getElementById('disguised-body').scrollTop>500") == "true", "关闭再打开 Word 书库恢复章节与段落进度");
            Check(await word.ExecuteScriptAsync("document.getElementById('word-font').value==='20'")=="true","字号通过本地后端持久化，跨服务端口重开仍生效");
            await word.ExecuteScriptAsync("document.getElementById('disguised-body').scrollTop=0");
            using (var file = File.Create(Path.Combine(store.Root, "08-word-document.png"))) await word.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
            await word.ExecuteScriptAsync("document.getElementById('disguised-body').scrollTop=1000");
            await Dispatch("bookReader", new Dictionary<string, object> { { "reader", "ai" } });await sessions.Close(epubId);await sessions.Open(epubId);
            Check(await sessions.Items[epubId].View.CoreWebView2.ExecuteScriptAsync("typeof window.MoyuWordReader==='undefined' && !!document.getElementById('chapterText')") == "true", "默认阅读方式可切回原仿 AI 界面");
            await Dispatch("bookReader", new Dictionary<string, object> { { "reader", "word" } });
            Check(new RegistryStore(store.Root,Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"projects")).State.defaultBookReader=="word", "默认阅读器选择持久化");
        }
        async Task VerifyCompactWindows(string bookId)
        {
            await shell.CoreWebView2.ExecuteScriptAsync("document.querySelector('.sidebar [data-section=pond]').click()");await WaitFor(()=>sessions.ActiveId==null,"回到鱼塘测试小窗");
            await Dispatch("windowPolicy",new Dictionary<string,object>{{"mode","keep"}});
            WindowState=FormWindowState.Normal; Size=new Size(760,560);await Task.Delay(500);
            Check(await shell.CoreWebView2.ExecuteScriptAsync("document.documentElement.scrollWidth<=innerWidth+1 && document.querySelector('.sidebar').getBoundingClientRect().width<=64")=="true","760×560 小窗折叠侧栏且首页无横向溢出");
            using(var file=File.Create(Path.Combine(store.Root,"09-compact-pond.png")))await shell.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,file);
            await shell.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-open=word-reader]').click()");await WaitFor(()=>sessions.ActiveId=="word-reader"&&sessions.Items["word-reader"].Status=="ready","小窗打开 Word");await Task.Delay(400);
            Check(WindowState==FormWindowState.Normal,"关闭自动最大化后项目保持小窗");
            var word=sessions.Items["word-reader"].View.CoreWebView2;
            Check(await word.ExecuteScriptAsync("document.documentElement.scrollWidth<=innerWidth+1 && document.getElementById('disguised-body').clientHeight>85")=="true","小窗 Word 保留正文区域且无页面横向溢出");
            await word.ExecuteScriptAsync("document.getElementById('word-library').click()");await Task.Delay(120);
            Check(await word.ExecuteScriptAsync("document.getElementById('word-panel').getBoundingClientRect().top>=document.querySelector('.word-controls').getBoundingClientRect().bottom-1")=="true","Word 目录随换行工具栏自动定位，不遮挡操作栏");
            await word.ExecuteScriptAsync("document.getElementById('word-panel-close').click()");
            using(var file=File.Create(Path.Combine(store.Root,"10-compact-word.png")))await word.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,file);
            await shell.CoreWebView2.ExecuteScriptAsync("document.querySelector('.sidebar [data-section=pond]').click()");await WaitFor(()=>sessions.ActiveId==null,"小窗返回鱼塘");await Task.Delay(120);
            await shell.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-open=reader]').click()");await WaitFor(()=>sessions.ActiveId=="reader"&&sessions.Items["reader"].Status=="ready","小窗打开 AI 阅读器");await Task.Delay(300);
            var ai=sessions.Items["reader"].View.CoreWebView2;
            Check(await ai.ExecuteScriptAsync("document.getElementById('mainArea').getBoundingClientRect().left<1 && getComputedStyle(document.getElementById('moyu-catalog-toggle')).display!=='none'")=="true","小窗 AI 阅读器正文占满宽度，提供目录抽屉入口");
            await ai.ExecuteScriptAsync("document.getElementById('moyu-catalog-toggle').click()");await Task.Delay(200);
            Check(await ai.ExecuteScriptAsync("document.getElementById('sidebar').getBoundingClientRect().left>=-1")=="true","小窗 AI 目录抽屉可实际展开");
            await ai.ExecuteScriptAsync("document.getElementById('moyu-catalog-toggle').click()");await Task.Delay(200);
            Check(await ai.ExecuteScriptAsync("getComputedStyle(document.getElementById('sidebar')).visibility==='hidden'")=="true","收起 AI 目录后隐藏其覆盖层，正文不被残留侧栏遮挡");
            using(var file=File.Create(Path.Combine(store.Root,"11-compact-ai.png")))await ai.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,file);
            await shell.CoreWebView2.ExecuteScriptAsync("document.querySelector('.sidebar [data-section=pond]').click()");await WaitFor(()=>sessions.ActiveId==null,"打开小窗 B站前返回");await Task.Delay(100);
            await shell.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-open=bilibili]').click()");await WaitFor(()=>sessions.ActiveId=="bilibili"&&sessions.Items["bilibili"].Status=="ready","小窗 B站");await Task.Delay(350);
            var bili=sessions.Items["bilibili"].View;
            Check(await bili.CoreWebView2.ExecuteScriptAsync("document.documentElement.scrollWidth<=innerWidth+1 && getComputedStyle(document.querySelector('.main')).overflowY==='auto'")=="true","小窗 B站在内容区滚动，页面整体不横向裁切");
            Check(await bili.CoreWebView2.ExecuteScriptAsync("document.querySelector('.content-card').scrollWidth>document.querySelector('.content-card').clientWidth")=="true","B站宽表格保留横向滚动入口");
            await Dispatch("zoom",new Dictionary<string,object>{{"id","bilibili"},{"percent",75}});await Task.Delay(250);
            Check(Math.Abs(bili.ZoomFactor-.75)<.01,"项目缩放真实应用至 WebView2");
            Check(await bili.CoreWebView2.ExecuteScriptAsync("getComputedStyle(document.querySelector('.ui-scale-root')).transform==='matrix(1, 0, 0, 1, 0, 0)'")=="true","B站 DPI 补偿不再抵消浏览器缩放");
            using(var file=File.Create(Path.Combine(store.Root,"12-compact-bilibili.png")))await bili.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,file);
            await sessions.Close("bilibili");await sessions.Open("bilibili");bili=sessions.Items["bilibili"].View;
            Check(Math.Abs(bili.ZoomFactor-.75)<.01,"单项目缩放关闭重开仍保留");
            await Dispatch("zoom",new Dictionary<string,object>{{"id","bilibili"},{"percent",0}});Check(bili.ZoomFactor>=.65&&bili.ZoomFactor<=1,"适合宽度保留可读的缩放下限");
            Size=new Size(980,680);await Task.Delay(500);Check(bili.ZoomFactor>=.65&&bili.ZoomFactor<=1,"适合宽度随小窗尺寸变化重新计算");
            Size=new Size(760,560);await Task.Delay(500);
            await Dispatch("zoom",new Dictionary<string,object>{{"id","bilibili"},{"percent",100}});
            await Dispatch("windowPolicy",new Dictionary<string,object>{{"mode","auto"}});
            await Dispatch("home",new Dictionary<string,object>());await sessions.Open("bilibili");Check(WindowState==FormWindowState.Maximized,"重新启用自动最大化后进入项目生效");
            await Dispatch("window",new Dictionary<string,object>());await sessions.Open("word-reader");Check(WindowState==FormWindowState.Normal,"手动还原后切换项目保持小窗");
            await Dispatch("home",new Dictionary<string,object>());await sessions.BrowserUrl(bookId);Check(WindowState==FormWindowState.Normal,"仅获取默认浏览器页面不会放大盒子窗口");
            await sessions.Open("integration-exe");Check(WindowState==FormWindowState.Normal,"独立 EXE 入口不强制放大盒子");
            Check(new RegistryStore(store.Root,Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"projects")).State.autoMaximizeProjects,"窗口模式设置可持久化");
        }
        static void ZipText(ZipArchive zip, string name, string value) { using (var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false))) writer.Write(value); }
        async Task VerifyProjects()
        {
            await sessions.Open("shijie"); var time=sessions.Items["shijie"]; string notePath=Path.Combine(store.Root,"native-time-note.txt");
            File.WriteAllText(notePath,"initial",Encoding.UTF8);var imported=sessions.ReadTimeFiles(time,new[]{notePath});
            await time.View.CoreWebView2.ExecuteScriptAsync("window.receiveTextFiles("+RegistryStore.Json.Serialize(imported)+")");
            string noteId=new List<string>(time.TextPaths.Keys)[0];
            await time.View.CoreWebView2.ExecuteScriptAsync("chrome.webview.postMessage("+RegistryStore.Json.Serialize(new { type="text-save", documentId=noteId, name="note.txt", text="FRAME_NOTE_CANARY\n第二行", encoding="utf-16le", eol="CRLF", asNew=false })+")");
            await WaitFor(()=>new FileInfo(notePath).Length>10,"时节原生文本保存");
            Check(File.ReadAllBytes(notePath)[0]==255 && File.ReadAllText(notePath,Encoding.Unicode)=="FRAME_NOTE_CANARY\r\n第二行","时节原生便签保存保留 UTF-16 与 CRLF");
            await time.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('scrollTop').click()");await WaitFor(()=>TopMost&&Width==370,"时节原生浮窗");
            Check(Width==370&&TopMost,"时节浮窗实际缩小本体并置顶");
            await time.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('floatRestore').click()");await WaitFor(()=>Width>=760&&!TopMost,"恢复时节浮窗");
            Check(!TopMost && MinimumSize.Width>=760,"时节浮窗恢复原本体窗口约束");
            foreach (string id in new[] { "multiai" }) {
                await sessions.Open(id); var item = sessions.Items[id];
                Check(item.Status == "ready", id + " 真实适配服务和标签页就绪");
                Check(!item.View.CoreWebView2.Settings.IsWebMessageEnabled, id + " 无框架原生桥接权限");
                await WaitScript(item.View.CoreWebView2, "!!window.__moyuAdapter", id + " API 授权脚本已执行");
                await item.View.CoreWebView2.ExecuteScriptAsync("fetch('/api/state').then(r=>r.json()).then(x=>window.__integrationState=x)");
                await WaitScript(item.View.CoreWebView2, "!!window.__integrationState", id + " 真实项目 API 可读");
                Check(await item.View.CoreWebView2.ExecuteScriptAsync("window.__integrationState.version==='0.14.1' && window.__integrationState.platforms.length>=9") == "true", id + " 后端版本与默认状态真实可用");
                var first = item.View; await Dispatch("home", new Dictionary<string, object>()); await sessions.Open(id);
                Check(sessions.Items[id].View == first, id + " 切回项目复用视图和服务");
                await Dispatch("windowPolicy", new Dictionary<string, object> { { "mode", "keep" } }); WindowState=FormWindowState.Normal; Size=new Size(760,560); await Task.Delay(300);
                Check(await first.CoreWebView2.ExecuteScriptAsync("document.documentElement.scrollWidth<=innerWidth+1") == "true", id + " 小窗无整体横向溢出");
                using (var file=File.Create(Path.Combine(store.Root,"adapted-"+id+".png"))) await first.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,file);
                string url=await sessions.BrowserUrl(id); string html=await Task.Run(delegate { using(var client=new System.Net.WebClient()) return client.DownloadString(url); });
                Check(html.Contains("__moyuAdapter"), id + " 默认浏览器地址可提供完整项目页面");
                await sessions.Close(id); Check(!File.Exists(Path.Combine(store.ProjectRoot(id),"service-ready.json")), id + " 关闭后实际移除就绪文件");
            }
            Check(store.Find("logicspace").url=="https://www.openlogicspace.cn/", "个人网站配置使用用户指定正式域名");
            Check(File.Exists(Path.Combine(store.ProjectRoot("multiai"),"logs","runtime.jsonl")), "同步输入器的项目日志保留在独立项目目录");
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--personal-site-test")>=0) {
                await sessions.Open("logicspace");var site=sessions.Items["logicspace"];
                bool readable=site.Status=="ready" && await site.View.CoreWebView2.ExecuteScriptAsync("document.body.innerText.trim().length>100")=="true";
                File.WriteAllText(Path.Combine(store.Root,"personal-site-result.json"),RegistryStore.Json.Serialize(new { loaded=readable,status=site.Status,error=site.Error }));
                if (site.View!=null) using(var file=File.Create(Path.Combine(store.Root,"personal-site.png"))) await site.View.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,file);
            }
        }
        async Task VerifyHostDiagnostics()
        {
            var log = Telemetry.Current;
            await shell.CoreWebView2.ExecuteScriptAsync("document.querySelector('.sidebar [data-section=settings]').click()"); await Task.Delay(200);
            Check(await shell.CoreWebView2.ExecuteScriptAsync("!!document.querySelector('[data-action=diagnostics]') && !!document.getElementById('log-level')") == "true", "设置中提供本体日志导出与级别入口");
            await Dispatch("logLevel", new Dictionary<string, object> { { "level", "DEBUG" } });
            using (var file = File.Create(Path.Combine(store.Root, "13-host-logging-settings.png"))) await shell.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
            Check(log.Level == "DEBUG" && File.ReadAllText(Path.Combine(store.Root, "diagnostics.json")).Contains("DEBUG"), "真实设置 RPC 保存 DEBUG 级别");
            await shell.CoreWebView2.ExecuteScriptAsync("window.dispatchEvent(new ErrorEvent('error',{error:new TypeError('SHELL_PRIVATE_CANARY'),lineno:123}));"); await Task.Delay(150);
            Check(Array.Exists(log.SnapshotLines(), x => x.Contains("script.unhandled")), "本体壳未处理 JavaScript 异常实际进入统一日志");
            bool failed = false;
            using (var locked = new FileStream(Path.Combine(store.Root, "settings.json.tmp"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
            { try { await Dispatch("favorite", new Dictionary<string, object> { { "id", "shijie" } }); } catch (IOException) { failed = true; } }
            Check(failed, "真实收藏操作遇到保存文件锁时抛出失败");
            string badBook = Path.Combine(store.Root, "private-fixture.epub"); File.WriteAllText(badBook, "invalid zip private content");
            failed = false; try { AddBook(badBook); } catch { failed = true; }
            Check(failed, "损坏 EPUB 在本体导入边界记录失败");
            var missing = new Project { id = "logging-missing-exe", name = "missing", kind = "external", entry = Path.Combine(store.Root, "missing.exe") }; store.Add(missing);
            failed = false; try { await Dispatch("open", new Dictionary<string, object> { { "id", missing.id } }); } catch (FileNotFoundException) { failed = true; }
            Check(failed && sessions.Items[missing.id].Status == "error", "不存在 EXE 的真实打开请求返回失败，避免假成功"); await sessions.Close(missing.id);
            string invalidExe = Path.Combine(store.Root, "invalid-fixture.exe"); File.Copy(Application.ExecutablePath, invalidExe, true);
            var invalid = new Project { id = "logging-invalid-exe", name = "invalid executable", kind = "external", entry = invalidExe }; store.Add(invalid);
            failed = false;
            using (var executableLock = new FileStream(invalidExe, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            { try { await Dispatch("open", new Dictionary<string, object> { { "id", invalid.id } }); } catch (System.ComponentModel.Win32Exception) { failed = true; } }
            Check(failed, "真实 CreateProcess 失败保留 Win32 原生错误码"); await sessions.Close(invalid.id);
            using (var server = new LoggingFixtureServer())
            {
                foreach (string mode in new[] { "error", "refused", "timeout" })
                {
                    string url = mode == "refused" ? server.RefusedUrl : server.BaseUrl + mode;
                    var project = new Project { id = "logging-" + mode, name = "network fixture", kind = "website", url = url }; store.Add(project);
                    failed = false; try { await Dispatch("open", new Dictionary<string, object> { { "id", project.id } }); } catch { failed = true; }
                    Check(failed && sessions.Items[project.id].Status == "error", "本体容器真实网络故障终态：" + mode); await sessions.Close(project.id);
                }
            }
            await sessions.Open("bilibili"); var child = sessions.Items["bilibili"].Process; int pid = child.Id; child.Kill(); child.WaitForExit();
            await WaitFor(() => sessions.Items["bilibili"].Status == "error", "观察本体拉起服务的异常退出", 80);
            Check(sessions.Items["bilibili"].ExitReported, "监测到隔离服务实际意外退出，保留子进程标识"); await sessions.Close("bilibili");
            Check(!File.Exists(Path.Combine(store.ProjectRoot("bilibili"), "service-ready.json")), "启动器异常退出后仍请求关闭服务，实际就绪文件已移除");
            Check(Array.Exists(log.SnapshotLines(), x => (x.Contains("listener.stopped") || x.Contains("release.unverified")) && x.Contains("process")), "资源释放具备确认记录或明确的未验证警告，不伪造成功");
            string zip = Path.Combine(store.Root, "host-diagnostics.zip"); await Task.Run(delegate { DiagnosticExport.Create(log, zip); });
            var records = new List<Dictionary<string, object>>(); foreach (string line in log.SnapshotLines()) records.Add(RegistryStore.Json.Deserialize<Dictionary<string, object>>(line));
            Check(records.Exists(x => Convert.ToString(x["event"]) == "action.open.finished" && Convert.ToString(x["result"]) == "failed"), "真实打开请求日志没有将失败写为成功");
            Check(records.Exists(x => Convert.ToString(x["event"]) == "project.open.finished" && Convert.ToString(x["result"]) == "timeout"), "真实 WebView 等待超时有明确 timeout 终态");
            Check(records.Exists(x => Convert.ToString(x["event"]) == "child.exited" && x.ContainsKey("child_process_id") && Convert.ToInt32(x["child_process_id"]) == pid), "真实子进程退出日志可对应 PID");
            Check(File.Exists(zip), "真实宿主可导出通过回读校验的本体诊断包");
        }
        async Task VerifyBuiltinSites()
        {
            var results=new List<object>();
            foreach(string id in new[]{"maps","wikipedia","archive","moyu-games","zlib-tv","saucenao","playzip","somecert"}) {
                try {
                    await sessions.Open(id);var item=sessions.Items[id];
                    string title=item.View==null?"null":await item.View.CoreWebView2.ExecuteScriptAsync("document.title");
                    string length=item.View==null?"0":await item.View.CoreWebView2.ExecuteScriptAsync("document.body?document.body.innerText.length:0");
                    results.Add(new{id=id,status=item.Status,error=item.Error,title=title,bodyLength=length});
                    Check(item.View==null||!item.View.CoreWebView2.Settings.IsWebMessageEnabled,id+" 网页不获得原生桥接");
                    await sessions.Close(id);
                }catch(Exception ex){results.Add(new{id=id,status="error",error=ex.GetType().Name});}
                File.WriteAllText(Path.Combine(store.Root,"websites-results.json"),RegistryStore.Json.Serialize(results),Encoding.UTF8);
            }
        }
        async Task VerifyRankings()
        {
            var results = new List<object>();
            await shell.CoreWebView2.ExecuteScriptAsync("document.querySelector('.sidebar [data-section=rankings]').click()");
            await WaitScript(shell.CoreWebView2, "document.querySelectorAll('[data-open^=rank-]').length===6", "榜单入口展示");
            Check(true, "渔具显示六个榜单与目录入口");
            foreach (Project project in store.State.projects.FindAll(x => !String.IsNullOrEmpty(x.rankingCategory)))
            {
                await shell.CoreWebView2.ExecuteScriptAsync("document.querySelector('.sidebar [data-section=rankings]').click()"); await Task.Delay(150);
                await shell.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-action=ranking-filter][data-filter=all]').click()");
                await shell.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-open=" + project.id + "]').click()");
                await WaitFor(() => sessions.Items.ContainsKey(project.id) && (sessions.Items[project.id].Status == "ready" || sessions.Items[project.id].Status == "error"), "榜单导航结束", 450);
                var item = sessions.Items[project.id];
                Check(item.View != null && !item.View.CoreWebView2.Settings.IsWebMessageEnabled, project.id + " 不获得本体桥接权限");
                await Task.Delay(500);
                string text = item.View == null ? "null" : await item.View.CoreWebView2.ExecuteScriptAsync("document.body ? document.body.innerText.length : 0");
                results.Add(new { id=project.id, status=item.Status, error=item.Error, bodyLength=text });
                using (var file = File.Create(Path.Combine(store.Root, project.id + ".png"))) await shell.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
                File.WriteAllText(Path.Combine(store.Root,"rankings-results.json"),RegistryStore.Json.Serialize(results),Encoding.UTF8);
            }
            store.State.favoriteIds.Add("rank-skills-sh"); store.Save();
            var reloaded = new RegistryStore(store.Root,Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"projects"));
            Check(reloaded.State.favoriteIds.Contains("rank-skills-sh"), "榜单收藏持久化");
        }
        async Task FinishIntegration(bool ok, string error)
        {
            try { await sessions.CloseAll(); } catch (Exception closeError) { ok = false; error = (error ?? "") + closeError; }
            File.WriteAllText(Path.Combine(store.Root, "integration-result.json"), RegistryStore.Json.Serialize(new { ok = ok, checks = checks, error = error, completedAt = DateTimeOffset.Now.ToString("o") }), new UTF8Encoding(false));
            Environment.ExitCode = ok ? 0 : 2; exiting = true; Close();
        }
    }
    internal static class IntegrationExtensions
    { public static Task HideForTest(this SessionManager manager) { manager.HideAll(); return Task.FromResult(true); } }
    // Isolated loopback faults; never disconnect the user's network or contact a third party.
    internal sealed class LoggingFixtureServer : IDisposable
    {
        readonly System.Net.Sockets.TcpListener listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        volatile bool running = true;
        public string BaseUrl, RefusedUrl;
        public LoggingFixtureServer()
        {
            listener.Start(); BaseUrl = "http://127.0.0.1:" + ((System.Net.IPEndPoint)listener.LocalEndpoint).Port + "/";
            var closed = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); closed.Start(); RefusedUrl = "http://127.0.0.1:" + ((System.Net.IPEndPoint)closed.LocalEndpoint).Port + "/"; closed.Stop();
            new System.Threading.Thread(delegate() { while (running) try { var client = listener.AcceptTcpClient(); System.Threading.ThreadPool.QueueUserWorkItem(delegate { using (client) try { client.ReceiveTimeout = 3000; var stream = client.GetStream(); var buffer = new byte[8192]; int count = stream.Read(buffer, 0, buffer.Length); string request = Encoding.ASCII.GetString(buffer, 0, count); if (request.Contains("/timeout")) { for (int i = 0; i < 350 && running; i++) System.Threading.Thread.Sleep(100); return; } byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 503 Unavailable\r\nContent-Length: 7\r\nConnection: close\r\n\r\nfixture"); stream.Write(response, 0, response.Length); } catch { } }); } catch { if (!running) return; } }) { IsBackground = true }.Start();
        }
        public void Dispose() { running = false; listener.Stop(); }
    }
}
