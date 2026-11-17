// Test-only runner: uses the unchanged packaged host's real import/session methods.
// It bypasses file pickers for the batch, never substitutes rendered web/book content.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MoyuBox;

class PublicCompatibilityRunner
{
    static BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 16*1024*1024 };
    static Form host; static RegistryStore store; static object sessions; static string root;
    static List<Dictionary<string,object>> results = new List<Dictionary<string,object>>();
    static List<Process> owned = new List<Process>();
    static object Field(object o,string n) { return o.GetType().GetField(n,all).GetValue(o); }
    static object Call(object o,string n,params object[] p) {
        try { return o.GetType().GetMethod(n,all).Invoke(o,p); }
        catch(TargetInvocationException e) { throw e.InnerException ?? e; }
    }
    static string S(Dictionary<string,object> d,string n) { return d.ContainsKey(n) ? Convert.ToString(d[n]) : ""; }
    static void Save() { File.WriteAllText(Path.Combine(root,"results.json"),json.Serialize(results),Encoding.UTF8); }
    [STAThread] static void Main(string[] args)
    {
        root=Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        Telemetry.Current=new DiagnosticLog(Path.Combine(root,"data"));
        store=new RegistryStore(Path.Combine(root,"data"),Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"projects"));
        host=(Form)Activator.CreateInstance(typeof(Project).Assembly.GetType("MoyuBox.MainWindow"),all,null,new object[]{store,false},null);
        host.Text="摸鱼盒子 · 公网样本实机测试（隔离数据）";
        sessions=Field(host,"sessions");
        host.Shown+=async delegate {
            try {
                for(int i=0;i<300 && !(bool)Field(host,"ready");i++)await Task.Delay(100);
                if(!(bool)Field(host,"ready"))throw new TimeoutException("Host shell not ready");
                // Give bootstrap and resize publications time to settle.
                await Task.Delay(800);
                var samples=json.Deserialize<List<Dictionary<string,object>>>(File.ReadAllText(Path.Combine(root,"samples.json")));
                foreach(var sample in samples) {await Test(sample);await CloseSample(results[results.Count-1]);Save();}
                Save();File.WriteAllText(Path.Combine(root,"batch-ready.txt"),DateTimeOffset.Now.ToString("o"));
                // Hold real windows for independent Computer Use inspection.
                for(int i=0;i<1200&&!File.Exists(Path.Combine(root,"finish-ui-check.txt"));i++)await Task.Delay(500);
            } catch(Exception error) { File.WriteAllText(Path.Combine(root,"runner-error.txt"),error.ToString()); }
            try { await (Task)Call(sessions,"CloseAll"); } catch(Exception error){File.WriteAllText(Path.Combine(root,"cleanup-error.txt"),error.ToString());}
            foreach(var process in owned)try{if(!process.HasExited){process.CloseMainWindow();if(!process.WaitForExit(3000))process.Kill();}}catch{}
            host.GetType().GetField("exiting",all).SetValue(host,true);host.Close();
        };
        try { Application.Run(host);Telemetry.Current.NormalExit=true; }
        finally { Telemetry.Current.Dispose(); }
    }
    static async Task Test(Dictionary<string,object> sample)
    {
        var result=new Dictionary<string,object>{{"id",S(sample,"id")},{"name",S(sample,"name")},{"source",S(sample,"source")},{"format",S(sample,"format")},{"started",DateTimeOffset.Now.ToString("o")}};
        results.Add(result);var watch=Stopwatch.StartNew();string projectId=null;
        Console.WriteLine("START "+S(sample,"id"));
        try {
            if(S(sample,"download_status")=="failed") {result["result"]="download_failed";result["error"]=S(sample,"download_error");return;}
            string format=S(sample,"format");
            if(format=="PDF") {
                // Same validation/import boundary as native book picker (which filters PDF out).
                try {Call(host,"AddBook",S(sample,"path"));result["result"]="unexpected_acceptance";}
                catch(Exception e){result["result"]="unsupported";result["error"]=e.Message;result["exception"]=e.GetType().FullName;}
                return;
            }
            Project project;
            if(format=="TXT"||format=="EPUB")project=(Project)Call(host,"AddBook",S(sample,"path"));
            else if(format=="ZIP") {
                project=await (Task<Project>)Call(host,"ImportArchive",S(sample,"path"));
                if(project==null)throw new Exception("ZIP import cancelled");
                result["import_mode"]="direct_ZIP_via_real_ImportArchive";result["direct_zip"]="supported";
            }
            else if(format=="MANIFEST") {
                project=RegistryStore.Json.Deserialize<Project>(File.ReadAllText(S(sample,"path"),Encoding.UTF8));
                project.builtin=false;project.enabled=true;Rules.Validate(project,true);
                if(project.kind!="local-web")throw new ArgumentException("Manifest type must be local-web");
                Call(host,"ImportPackage",project,Path.GetDirectoryName(S(sample,"path")));
                result["import_mode"]="real_JSON_deserialize_then_real_ImportPackage";
            }
            else {
                project=new Project{id=S(sample,"id"),name=S(sample,"name"),description="Public compatibility test",kind=format=="EXE"?"external":format=="WEB"?(S(sample,"source")=="GitHub"?"github":"website"):"local-web"};
                if(format=="EXE")project.entry=S(sample,"entry_path");
                if(format=="WEB") {project.url=S(sample,"repo")==""?S(sample,"url"):S(sample,"repo");if(project.kind=="github")project.launchUrl=S(sample,"url");}
                if(format=="ZIP"||format=="HTML") {
                    string entry=format=="ZIP"?S(sample,"entry_path"):S(sample,"path");project.entry=Path.GetFileName(entry);
                    Call(host,"ImportPackage",project,Path.GetDirectoryName(entry));
                    result["import_mode"]=format=="ZIP"?"extracted_folder_then_real_ImportPackage":"html_parent_folder_then_real_ImportPackage";
                    if(format=="ZIP")result["direct_zip"]="unsupported_no_zip_importer";
                } else store.Add(project);
            }
            projectId=project.id;result["project_id"]=project.id;Call(host,"Publish");
            result["import_warning"]=Field(host,"importWarning");
            await OpenThroughShell(project.id);
            var items=(IDictionary)Field(sessions,"Items");var item=items[project.id];
            result["host_status"]=Field(item,"Status");result["host_error"]=Field(item,"Error");
            if(format=="EXE") {
                var process=(Process)Field(item,"Process");var independent=Process.GetProcessById(process.Id);owned.Add(independent);
                for(int i=0;i<80;i++){process.Refresh();if(process.HasExited||process.MainWindowHandle!=IntPtr.Zero)break;await Task.Delay(100);}
                result["child_pid"]=process.Id;result["exited"]=process.HasExited;
                if(process.HasExited){result["exit_code"]=process.ExitCode;result["result"]="exited_before_window";}
                else {result["window_title"]=process.MainWindowTitle;result["has_window"]=process.MainWindowHandle!=IntPtr.Zero;result["file_version"]=FileVersionInfo.GetVersionInfo(project.entry).FileVersion;result["result"]=process.MainWindowHandle!=IntPtr.Zero?"window_running":"process_only";}
                if(!process.HasExited&&process.MainWindowHandle!=IntPtr.Zero) {
                    for(int i=0;i<80&&Field(item,"TaskbarGroup")==null;i++)await Task.Delay(100);
                    IntPtr originalWindow=process.MainWindowHandle;
                    result["taskbar_group_id"]=TaskbarWindowGroup.ReadId(originalWindow);
                    result["taskbar_group_verified"]=Convert.ToString(result["taskbar_group_id"])==TaskbarWindowGroup.AppId;
                    await (Task)Call(sessions,"Close",project.id);
                    result["external_alive_after_tab_close"]=!independent.HasExited;
                    result["taskbar_restored_after_tab_close"]=TaskbarWindowGroup.ReadId(originalWindow)!=TaskbarWindowGroup.AppId;
                }
                // Preserve the external program window for Computer Use inspection.
                return;
            }
            if(Convert.ToString(Field(item,"Status"))!="ready") {result["result"]="runtime_error";return;}
            var view=(WebView2)Field(item,"View");
            if(view==null||view.CoreWebView2==null)throw new Exception("No actual WebView2");
            var core=view.CoreWebView2;
            for(int i=0;i<120;i++) {
                string condition=format=="TXT"||format=="EPUB"?"!!window.MoyuWordReader&&window.MoyuWordReader.state.ready":"!!document.body&&document.body.innerText.trim().length>20";
                if(await Js(core,condition)=="true")break;await Task.Delay(100);
            }
            string measure="JSON.stringify({title:document.title,url:location.href,textLength:(document.body&&document.body.innerText||'').length,scrollWidth:document.documentElement.scrollWidth,innerWidth:innerWidth,images:document.images.length,brokenImages:[...document.images].filter(x=>x.complete&&x.naturalWidth===0).length,scripts:document.scripts.length,ready:document.readyState})";
            result["page"]=ParseScript(await Js(core,measure));
            if(format=="TXT"||format=="EPUB") {
                result["reader"]=ParseScript(await Js(core,"JSON.stringify({ready:!!window.MoyuWordReader&&window.MoyuWordReader.state.ready,chapters:window.MoyuWordReader?.state.chapters.length,chapter:window.MoyuWordReader?.state.index,textLength:document.getElementById('word-text')?.innerText.length,title:window.MoyuWordReader?.state.books[0]?.title})"));
                await Js(core,"document.getElementById('word-next')?.click()");await Task.Delay(450);
                result["reader_after_next"]=ParseScript(await Js(core,"JSON.stringify({ready:window.MoyuWordReader?.state.ready,index:window.MoyuWordReader?.state.index,textLength:document.getElementById('word-text')?.innerText.length})"));
            }
            if(S(sample,"probe")=="2048") {
                result["interaction_before"]=await Js(core,"document.querySelectorAll('.tile').length");
                await Js(core,"for(const key of ['ArrowLeft','ArrowUp','ArrowRight','ArrowDown'])document.dispatchEvent(new KeyboardEvent('keydown',{key:key,keyCode:{ArrowLeft:37,ArrowUp:38,ArrowRight:39,ArrowDown:40}[key],bubbles:true}));");await Task.Delay(300);
                result["interaction_after"]=await Js(core,"document.querySelectorAll('.tile').length");
            }
            if(S(sample,"probe")=="hextris")result["canvas"]=await Js(core,"!!document.querySelector('canvas')&&document.querySelector('canvas').width>0");
            if(S(sample,"probe")=="dimension") {
                await Js(core,"document.querySelector('a[href=\"#intro\"]')?.click()");await Task.Delay(700);
                result["interaction"]=await Js(core,"location.hash==='\u0023intro'&&document.getElementById('intro')?.classList.contains('active')");
            }
            await Capture(core,S(sample,"id")+"-large.png");
            // Exercise the actual session manager and a narrow native view.
            var first=view;await OpenThroughShell(project.id);result["same_view_after_switch"]=Object.ReferenceEquals(first,Field(((IDictionary)Field(sessions,"Items"))[project.id],"View"));
            host.WindowState=FormWindowState.Normal;host.Size=new Size(760,560);await Task.Delay(350);
            result["compact"]=ParseScript(await Js(core,"JSON.stringify({scrollWidth:document.documentElement.scrollWidth,innerWidth:innerWidth,overflow:document.documentElement.scrollWidth>innerWidth+1})"));
            await Capture(core,S(sample,"id")+"-compact.png");
            if(S(sample,"probe")=="hextris") {
                result["overflow_elements"]=ParseScript(await Js(core,"JSON.stringify([...document.querySelectorAll('body *')].map(e=>({tag:e.tagName,id:e.id,right:Math.round(e.getBoundingClientRect().right),width:Math.round(e.getBoundingClientRect().width)})).filter(e=>e.right>innerWidth+1).slice(0,12))"));
                result["game_state_before"]=await Js(core,"typeof gameState==='undefined'?null:gameState");
                await Js(core,"document.getElementById('startBtn')?.dispatchEvent(new MouseEvent('mousedown',{bubbles:true,button:0}))");await Task.Delay(800);
                result["game_state_after"]=await Js(core,"typeof gameState==='undefined'?null:gameState");
                project.zoomPercent=0;store.Save();await (Task)Call(sessions,"ApplyZoom",project.id);await Task.Delay(500);
                result["fit_width"]=ParseScript(await Js(core,"JSON.stringify({scrollWidth:document.documentElement.scrollWidth,innerWidth:innerWidth,overflow:document.documentElement.scrollWidth>innerWidth+1})"));
                result["fit_zoom_percent"]=(int)Math.Round(view.ZoomFactor*100);
                await Capture(core,S(sample,"id")+"-fit.png");
            }
            string browserUrl=(string)await (Task<string>)Call(sessions,"BrowserUrl",project.id);
            result["browser_url_scheme"]=new Uri(browserUrl).Scheme;
            if(new Uri(browserUrl).IsLoopback) {
                result["browser_http"]=await Task.Run(delegate{using(var web=new System.Net.WebClient())return web.DownloadData(browserUrl).Length;});
            }
            result["result"]=Convert.ToString(Field(item,"Status"))=="ready"?"runtime_loaded":"runtime_error";
        } catch(Exception error) {result["result"]="failed";result["error"]=error.ToString();}
        finally {
            result["elapsed_ms"]=watch.ElapsedMilliseconds;result["finished"]=DateTimeOffset.Now.ToString("o");Save();Console.WriteLine("END "+S(sample,"id")+" "+S(result,"result"));
        }
    }
    static async Task CloseSample(Dictionary<string,object> result) {
        if(S(result,"project_id")==""||S(result,"format")=="EXE")return;
        try {await (Task)Call(sessions,"Close",S(result,"project_id"));result["closed"]=true;}
        catch(Exception e){result["close_error"]=e.ToString();}
    }
    static async Task OpenThroughShell(string id) {
        var shell=(WebView2)Field(host,"shell");
        await Js(shell.CoreWebView2,"document.querySelector('.home-tab').click()");
        for(int i=0;i<100&&sessions.GetType().GetProperty("ActiveId").GetValue(sessions,null)!=null;i++)await Task.Delay(50);
        Call(host,"Publish");await Task.Delay(200);
        string selector="[data-open="+id+"]";
        string clicked=await Js(shell.CoreWebView2,"(()=>{const button=document.querySelector("+json.Serialize(selector)+");if(!button)return false;button.click();return true;})()");
        if(clicked!="true")throw new Exception("Real shell project card unavailable: "+id);
        for(int i=0;i<750;i++) {
            var items=(IDictionary)Field(sessions,"Items");
            if(items.Contains(id)) {
                var item=items[id];var opening=Field(item,"Opening") as Task;
                if(opening!=null&&opening.IsCompleted){await opening;await Task.Delay(250);return;}
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("Real shell project-open did not finish");
    }
    static object ParseScript(string s) {return json.DeserializeObject(json.Deserialize<string>(s));}
    static async Task<string> Js(CoreWebView2 core,string script) {var task=core.ExecuteScriptAsync(script);if(await Task.WhenAny(task,Task.Delay(12000))!=task)throw new TimeoutException("Test probe ExecuteScriptAsync did not respond; no page-content conclusion");return await task;}
    static async Task Capture(CoreWebView2 core,string name) {string dir=Path.Combine(root,"screenshots");Directory.CreateDirectory(dir);using(var f=File.Create(Path.Combine(dir,name))){var task=core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,f);if(await Task.WhenAny(task,Task.Delay(12000))!=task)throw new TimeoutException("Test screenshot did not respond");await task;}}
}
