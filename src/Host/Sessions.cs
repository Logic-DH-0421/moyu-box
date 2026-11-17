using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MoyuBox
{
    internal sealed class ProjectSession
    {
        public Project Project;
        public WebView2 View;
        public string Status = "idle";
        public string Error;
        public Exception LastException;
        public OperationLog OwnerOperation;
        public bool ExitReported;
        public readonly HashSet<ulong> TimedOutNavigations = new HashSet<ulong>();
        public readonly Dictionary<string,string> TextPaths = new Dictionary<string,string>();
        public readonly Dictionary<ulong, OperationLog> Navigations = new Dictionary<ulong, OperationLog>();
        public Task Opening;
        public TaskCompletionSource<bool> NavigationReady;
        public Process Process;
        public TaskbarWindowGroup TaskbarGroup;
        public int GroupAttempts;
        public string ServiceUrl;
        public string ServiceToken;
        public StaticSiteServer StaticServer;
        public int ZoomRequest;
        public bool Disposed;
    }
    internal sealed partial class SessionManager
    {
        readonly MainWindow owner;
        readonly RegistryStore store;
        readonly System.Windows.Forms.Timer fitTimer;
        readonly System.Windows.Forms.Timer processTimer;
        string lastDiagnosticWarning;
        readonly string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        public readonly Dictionary<string, ProjectSession> Items = new Dictionary<string, ProjectSession>();
        public string ActiveId { get; private set; }
        public Rectangle Viewport = new Rectangle(210, 110, 100, 100);
        public SessionManager(MainWindow owner, RegistryStore store) { this.owner = owner; this.store = store; processTimer = new System.Windows.Forms.Timer { Interval = 2000 }; processTimer.Tick += delegate { ObserveProcesses(); if (Telemetry.Current != null && lastDiagnosticWarning != Telemetry.Current.DegradedMessage) { lastDiagnosticWarning = Telemetry.Current.DegradedMessage; owner.Publish(); } }; processTimer.Start(); fitTimer = new System.Windows.Forms.Timer { Interval = 180 }; fitTimer.Tick += async delegate { fitTimer.Stop(); if (ActiveId != null) await ApplyZoom(ActiveId); }; }
        public void HideAll() { owner.SetProjectFloating(false); ActiveId = null; foreach (var item in Items.Values) if (item.View != null) item.View.Visible = false; }
        public void Layout(Rectangle bounds) { bool changed = Viewport.Size != bounds.Size; Viewport = bounds; foreach (var item in Items.Values) if (item.View != null) item.View.Bounds = bounds; if (changed && ActiveId != null) { ProjectSession active; if (Items.TryGetValue(ActiveId, out active) && active.Project.zoomPercent == 0) { fitTimer.Stop(); fitTimer.Start(); } } }
        public async Task Open(string id, bool activate = true)
        {
            Project project = store.Find(id);
            if (!project.enabled) throw new InvalidOperationException("项目已停用，请在项目管理中启用。");
            if (activate) { if (ActiveId == null) owner.PrepareProjectWindow(project); HideAll(); ActiveId = id; }
            ProjectSession item;
            if (!Items.TryGetValue(id, out item)) { item = new ProjectSession { Project = project }; Items[id] = item; }
            if (item.Status == "error") { await Close(id); item = new ProjectSession { Project = project }; Items[id] = item; if (activate) ActiveId = id; }
            if (item.Opening == null) item.Opening = Initialize(item);
            await item.Opening;
            if (activate && ActiveId == id && item.View != null && item.Status == "ready") { await ApplyZoom(id); item.View.Bounds = Viewport; item.View.Visible = true; item.View.BringToFront(); item.View.Focus(); }
            owner.Publish();
        }
        async Task Initialize(ProjectSession item)
        {
            using (var operation = Telemetry.Begin("projects", "project.open", DiagnosticLog.Fingerprint(item.Project.id)))
            try
            {
            item.OwnerOperation = operation;
            SetStatus(item, "loading", "open_requested"); owner.Publish();
            Telemetry.Write("INFO", "projects", "initialization.started", "Host begins project-container initialization", new Dictionary<string, object> { { "kind", item.Project.kind } }, null, true);
                string data = store.ProjectRoot(item.Project.id);
                string url = item.Project.kind == "github" && !String.IsNullOrWhiteSpace(item.Project.launchUrl) ? item.Project.launchUrl : item.Project.url;
                if (item.Project.kind == "external")
                {
                    if (!File.Exists(item.Project.entry)) throw new FileNotFoundException("程序路径不存在，请重新添加。", item.Project.entry);
                    using (var launch = Telemetry.Begin("process", "external.launch"))
                    try { item.Process = Process.Start(new ProcessStartInfo(item.Project.entry) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(item.Project.entry) }); launch.Success(false, new Dictionary<string, object> { { "child_process_id", item.Process.Id }, { "kind", "external" } }); }
                    catch (Exception error) { launch.Fail(error); throw; }
                    SetStatus(item, "external", "process_started"); operation.Success(); owner.Publish(); return;
                }
                if (item.Project.kind == "reader" || item.Project.kind == "word-reader" || item.Project.kind == "book")
                {
                    string library = Path.Combine(data, "library");
                    bool seeded = File.Exists(Path.Combine(library, "library.json"));
                    if (item.Project.kind == "book" && !seeded) new AiNovelReader.DataStore(library).ImportBook(Rules.ContainedPath(data, item.Project.entry));
                    bool word = item.Project.kind == "word-reader" || (item.Project.kind == "book" && store.State.defaultBookReader == "word");
                    url = await StartManagedService(item, data, word ? "word" : "ai");
                }
                if (item.Project.kind == "bilibili") url = await StartManagedService(item, data, null);
                if (item.Project.kind == "multiai") url = await StartManagedService(item, data, null);
                if (item.Disposed) { operation.Cancel("project_closed"); return; }
                var view = new WebView2 { Bounds = Viewport, Visible = false, DefaultBackgroundColor = Color.White };
                item.View = view; owner.Controls.Add(view);
                var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(data, "browser"));
                await view.EnsureCoreWebView2Async(environment);
                if (item.Disposed) { operation.Cancel("project_closed"); return; }
                var core = view.CoreWebView2;
                core.Settings.AreHostObjectsAllowed = false;
                core.Settings.IsWebMessageEnabled = item.Project.kind == "shijie";
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreDevToolsEnabled = owner.Integration;
                if (item.Project.kind == "shijie" || item.Project.kind == "local-web")
                {
                    string host = item.Project.id + ".project.moyubox.local";
                    string folder = item.Project.kind == "shijie" ? Path.Combine(baseDir, "plugins", "shijie") : Path.Combine(data, "package");
                    string entry = item.Project.kind == "shijie" ? "app.html" : item.Project.entry;
                    if (!File.Exists(Rules.ContainedPath(folder, entry))) throw new FileNotFoundException("项目入口文件不存在。");
                    core.SetVirtualHostNameToFolderMapping(host, folder, CoreWebView2HostResourceAccessKind.DenyCors);
                    url = "https://" + host + "/" + String.Join("/", Array.ConvertAll(entry.Replace('\\', '/').Split('/'), Uri.EscapeDataString));
                    if (item.Project.kind == "shijie") core.WebMessageReceived += async delegate(object sender, CoreWebView2WebMessageReceivedEventArgs e) { await HandleTimeMessage(item, e); };
                }
                core.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs e)
                {
                    OperationLog navigation;
                    if (!item.Navigations.TryGetValue(e.NavigationId, out navigation)) { navigation = new OperationLog(Telemetry.Current, "webview", "navigation", DiagnosticLog.Fingerprint(item.Project.id), false, operation); item.Navigations[e.NavigationId] = navigation; }
                    else Telemetry.Summary("webview", "redirect");
                    Uri destination;
                    if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out destination) || (destination.Scheme != "https" && !(destination.Scheme == "http" && destination.IsLoopback))) { e.Cancel = true; navigation.Cancel("scheme_blocked"); item.Navigations.Remove(e.NavigationId); }
                };
                core.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs e)
                {
                    e.Handled = true;
                    try { Rules.Website(e.Uri); core.Navigate(e.Uri); } catch (Exception error) { Bootstrap.Log(store.Root, error); }
                };
                core.PermissionRequested += delegate(object sender, CoreWebView2PermissionRequestedEventArgs e) { e.State = CoreWebView2PermissionState.Deny; };
                core.DownloadStarting += delegate(object sender, CoreWebView2DownloadStartingEventArgs e)
                {
                    var deferral = e.GetDeferral();
                    using (var destinationOperation = Telemetry.Begin("dialogs", "download.destination"))
                    try { using (var dialog = new SaveFileDialog { FileName = Path.GetFileName(e.ResultFilePath), OverwritePrompt = true }) { if (dialog.ShowDialog(owner) == DialogResult.OK) { e.ResultFilePath = dialog.FileName; destinationOperation.Success(false); } else { e.Cancel = true; destinationOperation.Cancel(); } } } catch (Exception error) { destinationOperation.Fail(error); e.Cancel = true; } finally { deferral.Complete(); }
                };
                core.ProcessFailed += delegate(object sender, CoreWebView2ProcessFailedEventArgs e) { if (item.Disposed) return; item.LastException = new InvalidOperationException("Project webview process failed"); SetStatus(item, "error", "webview_process_failed"); item.Error = "项目网页进程意外结束。可以重试，其他项目不受影响。"; Telemetry.Write("ERROR", "webview", "process.failed", "Project webview process failed", new Dictionary<string, object> { { "code", e.ProcessFailedKind.ToString() } }, item.LastException, true); foreach (var pending in item.Navigations.Values) { pending.Fail(item.LastException); pending.Dispose(); } item.Navigations.Clear(); view.Visible = false; owner.Publish(); };
                var loaded = new TaskCompletionSource<bool>(); item.NavigationReady = loaded;
                core.NavigationCompleted += async delegate(object sender, CoreWebView2NavigationCompletedEventArgs e)
                {
                    if (item.Disposed) return;
                    if(item.TimedOutNavigations.Contains(e.NavigationId)) { loaded.TrySetResult(false); return; }
                    bool success = e.IsSuccess && e.HttpStatusCode < 400;
                    if (!success) { Telemetry.Write("WARN", "webview", "navigation.response_failed", "Navigation returned transport or HTTP error", new Dictionary<string, object> { { "code", e.WebErrorStatus.ToString() }, { "status", e.HttpStatusCode }, { "source_hash", DiagnosticLog.Fingerprint(item.Project.id) } }, null, true); item.LastException = new System.Net.WebException("Project navigation failed: " + e.WebErrorStatus); SetStatus(item, "error", "navigation_failed"); item.Error = NavigationMessages.Failure(e.WebErrorStatus.ToString(), e.HttpStatusCode); view.Visible = false; }
                    else { SetStatus(item, "ready", "navigation_completed"); item.LastException = null; item.Error = null; if (ActiveId == item.Project.id) { view.Visible = true; view.BringToFront(); } }
                    if (success) await ApplyZoom(item.Project.id);
                    OperationLog navigation;
                    if (item.Navigations.TryGetValue(e.NavigationId, out navigation)) { if (success) navigation.Success(false, new Dictionary<string, object> { { "code", e.HttpStatusCode } }); else navigation.Fail(item.LastException); navigation.Dispose(); item.Navigations.Remove(e.NavigationId); }
                    loaded.TrySetResult(success); owner.Publish();
                };
                core.Navigate(url);
                if (await Task.WhenAny(loaded.Task, Task.Delay(30000)) != loaded.Task)
                {
                    foreach(ulong navigationId in item.Navigations.Keys)item.TimedOutNavigations.Add(navigationId);
                    core.Stop(); item.LastException = new TimeoutException("Project navigation timed out"); SetStatus(item, "error", "navigation_timeout"); item.Error = "等待页面响应超时，请检查连接后重试。";
                    foreach (var pending in item.Navigations.Values) { pending.Fail(item.LastException); pending.Dispose(); } item.Navigations.Clear();
                }
                if (item.Disposed) { operation.Cancel("project_closed"); return; }
                if (item.LastException != null) operation.Fail(item.LastException); else operation.Success();
                owner.Publish();
            }
            catch (Exception error)
            {
                operation.Fail(error);
                if (item.Disposed) return;
                item.LastException = error; SetStatus(item, "error", "initialization_failed"); item.Error = error.Message; if (item.View != null) item.View.Visible = false;
                owner.Publish();
            }
        }
        static void SetStatus(ProjectSession item, string status, string reason)
        {
            string previous = item.Status; item.Status = status;
            if (previous != status && Telemetry.Current != null) Telemetry.Current.Write("INFO", "projects", "state.changed", "Host project-container state changed", new Dictionary<string, object> { { "from", previous }, { "to", status }, { "reason", reason }, { "source_hash", DiagnosticLog.Fingerprint(item.Project.id) } }, null, true, item.OwnerOperation);
        }
        void ObserveProcesses()
        {
            foreach (var item in new List<ProjectSession>(Items.Values))
            {
                if (item.Disposed || item.ExitReported || item.Process == null || item.Opening == null || !item.Opening.IsCompleted) continue;
                try {
                    if (!item.Process.HasExited) {
                        if(item.Project.kind=="external"&&item.TaskbarGroup==null&&item.GroupAttempts<3){
                            try {item.TaskbarGroup=TaskbarWindowGroup.Attach(item.Process);if(item.TaskbarGroup!=null&&Telemetry.Current!=null)Telemetry.Current.Write("INFO","taskbar","external.grouped","Verified taskbar AppUserModelID of a host-launched EXE",new Dictionary<string,object>{{"child_process_id",item.Process.Id},{"source_hash",DiagnosticLog.Fingerprint(item.Project.id)}},null,true,item.OwnerOperation);if(item.TaskbarGroup!=null)owner.Publish();}
                            catch(Exception error){item.GroupAttempts++;if(item.GroupAttempts==3&&Telemetry.Current!=null)Telemetry.Current.Write("WARN","taskbar","external.group_failed","EXE taskbar grouping unavailable; independent window retained",new Dictionary<string,object>{{"child_process_id",item.Process.Id},{"source_hash",DiagnosticLog.Fingerprint(item.Project.id)}},error,true,item.OwnerOperation);if(item.GroupAttempts==3)owner.Publish();}
                        }
                        continue;
                    }
                    item.ExitReported = true;
                    bool unexpected = item.Project.kind != "external";
                    if (Telemetry.Current != null) Telemetry.Current.Write(unexpected || item.Process.ExitCode != 0 ? "ERROR" : "INFO", "process", "child.exited", "Observed exit of host-launched process", new Dictionary<string, object> { { "child_process_id", item.Process.Id }, { "exit_code", item.Process.ExitCode }, { "kind", item.Project.kind }, { "source_hash", DiagnosticLog.Fingerprint(item.Project.id) }, { "reason", unexpected ? "unexpected_service_exit" : "external_exit" } }, null, true, item.OwnerOperation);
                    if (unexpected) { item.LastException = new InvalidOperationException("Host-launched service process exited"); SetStatus(item, "error", "service_process_exited"); item.Error = "本体拉起的服务进程已退出，请重试。"; owner.Publish(); }
                } catch (Exception error) { Telemetry.Caught("process", "observe.failed", error); item.ExitReported = true; }
            }
        }
        public async Task ApplyZoom(string id)
        {
            ProjectSession item;
            if (!Items.TryGetValue(id, out item) || item.Disposed || item.View == null || item.View.IsDisposed || item.View.CoreWebView2 == null) return;
            try
            {
                int fitRequest = ++item.ZoomRequest;
                int requestedZoom = item.Project.zoomPercent;
                double zoom = requestedZoom / 100.0;
                string fitScript = File.ReadAllText(Path.Combine(baseDir,"shell","project-fit.js"));
                bool localFit = item.Project.kind == "local-web" && requestedZoom == 0;
                var preparation = item.View.CoreWebView2.ExecuteScriptAsync(fitScript+"("+(localFit?"true":"false")+")");
                if(await Task.WhenAny(preparation,Task.Delay(2500))!=preparation)throw new TimeoutException("Project layout probe timed out");
                await preparation;
                if(item.Disposed||item.View.IsDisposed||item.ZoomRequest!=fitRequest||item.Project.zoomPercent!=requestedZoom)return;
                if (requestedZoom == 0)
                {
                    if (Math.Abs(item.View.ZoomFactor - 1) > .005) item.View.ZoomFactor = 1;
                    // Layout and canvas resize transitions must settle before fitting.
                    for(int attempt=0;attempt<3;attempt++) {
                        await Task.Delay(200);
                        if(item.Disposed||item.View.IsDisposed||item.ZoomRequest!=fitRequest||item.Project.zoomPercent!=requestedZoom)return;
                        var probe=item.View.CoreWebView2.ExecuteScriptAsync("JSON.stringify({width:innerWidth,scroll:Math.max(document.documentElement.scrollWidth,document.body?document.body.scrollWidth:0)})");
                        if(await Task.WhenAny(probe,Task.Delay(2500))!=probe)throw new TimeoutException("Project layout probe timed out");
                        string metrics=await probe;
                        if(item.Disposed||item.View.IsDisposed||item.ZoomRequest!=fitRequest||item.Project.zoomPercent!=requestedZoom)return;
                        var values=RegistryStore.Json.Deserialize<Dictionary<string,double>>(RegistryStore.Json.Deserialize<string>(metrics));
                        double required=values["scroll"], available=values["width"];
                        if(item.Project.kind=="reader")required=Math.Max(required,900);
                        if(item.Project.kind=="bilibili")required=Math.Max(required,1050);
                        if(required<=available+1) {zoom=item.View.ZoomFactor;break;}
                        zoom=Math.Max(.65,Math.Min(item.View.ZoomFactor,item.View.ZoomFactor*available/Math.Max(1,required)));
                        item.View.ZoomFactor=zoom;
                    }
                }
                if (Math.Abs(item.View.ZoomFactor - zoom) > .005) item.View.ZoomFactor = zoom;
                Telemetry.Write("DEBUG", "window", "zoom.applied", "Project-container zoom applied", new Dictionary<string, object> { { "zoom", zoom }, { "width", Viewport.Width }, { "height", Viewport.Height } });
            }
            catch (Exception error) { if (!item.Disposed) Bootstrap.Log(store.Root, error); }
        }
        public async Task<string> BrowserUrl(string id)
        {
            Project p = store.Find(id);
            if (!p.enabled) throw new InvalidOperationException("项目已停用。");
            if (p.kind == "external") throw new InvalidOperationException("EXE 程序使用 Windows 独立窗口运行，浏览器不能运行 EXE。");
            ProjectSession item;
            if (p.kind == "website" || p.kind == "github")
            {
                if (Items.TryGetValue(id, out item) && item.View != null && item.View.Source != null) return Rules.Website(item.View.Source.AbsoluteUri).AbsoluteUri;
                return Rules.Website(p.kind == "github" && !String.IsNullOrWhiteSpace(p.launchUrl) ? p.launchUrl : p.url).AbsoluteUri;
            }
            if (p.kind == "local-web" || p.kind == "shijie")
            {
                if (!Items.TryGetValue(id, out item)) { item = new ProjectSession { Project = p }; Items[id] = item; }
                if (item.StaticServer == null) item.StaticServer = new StaticSiteServer(p.kind == "shijie" ? Path.Combine(baseDir, "plugins", "shijie") : Path.Combine(store.ProjectRoot(id), "package"));
                string entry = p.kind == "shijie" ? "app.html" : p.entry;
                return item.StaticServer.BaseUrl + String.Join("/", Array.ConvertAll(entry.Replace('\\', '/').Split('/'), Uri.EscapeDataString));
            }
            // Start the service without stealing the user's active tab while awaiting initialization.
            await Open(id, false); item = Items[id];
            if (item.Status != "ready") throw new InvalidOperationException(item.Error ?? "本地服务未就绪。");
            return item.View.Source.AbsoluteUri;
        }
        async Task<string> StartManagedService(ProjectSession item, string data, string readerUi)
        {
            using (var operation = Telemetry.Begin("process", "service.start", DiagnosticLog.Fingerprint(item.Project.id)))
            try {
            string ready = Path.Combine(data, "service-ready.json");
            if (File.Exists(ready)) File.Delete(ready);
            item.ServiceToken = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            bool reader = readerUi != null;
            string executable = reader ? "MoyuReaderService.exe" : item.Project.kind == "bilibili" ? "MoyuBiliService.exe" : "MoyuProjectsService.exe";
            string exe = Path.Combine(baseDir, "adapters", executable);
            if (!File.Exists(exe)) throw new FileNotFoundException("项目适配服务缺失，请重新解压完整程序包。", exe);
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe) };
            string web = reader ? Path.Combine(baseDir, "plugins", "word-reader") : Path.Combine(baseDir, "plugins", item.Project.kind, item.Project.kind == "multiai" ? "web" : "");
            info.Arguments = "--data " + Quote(reader ? Path.Combine(data, "library") : data) + " --web " + Quote(web) + " --ready " + Quote(ready);
            if (reader) info.Arguments += " --ui " + readerUi;
            else if (item.Project.kind != "bilibili") info.Arguments += " --kind " + item.Project.kind;
            info.EnvironmentVariables["MOYU_SERVICE_TOKEN"] = item.ServiceToken;
            item.Process = Process.Start(info);
            Telemetry.Write("INFO", "process", "child.started", "Host launched isolated service", new Dictionary<string, object> { { "child_process_id", item.Process.Id }, { "kind", item.Project.kind } }, null, true);
            for (int i = 0; i < 240; i++)
            {
                if (item.Disposed) throw new OperationCanceledException();
                if (item.Process.HasExited) throw new InvalidOperationException("项目适配服务启动失败，退出代码 " + item.Process.ExitCode + "。请查看项目日志。");
                if (File.Exists(ready))
                {
                    try { var message = RegistryStore.Json.Deserialize<Dictionary<string, string>>(File.ReadAllText(ready)); item.ServiceUrl = Rules.Website(message["url"]).AbsoluteUri; if (!new Uri(item.ServiceUrl).IsLoopback) throw new InvalidDataException(); operation.Success(false, new Dictionary<string, object> { { "attempt", i + 1 }, { "stage", "ready_file_validated" } }); return item.ServiceUrl; } catch (IOException error) { Telemetry.Summary("process", "ready.retry", new Dictionary<string, object> { { "code", error.HResult }, { "attempt", i + 1 } }); } catch (ArgumentException error) { Telemetry.Summary("process", "ready.retry", new Dictionary<string, object> { { "code", error.HResult }, { "attempt", i + 1 } }); }
                }
                await Task.Delay(100);
            }
            throw new TimeoutException("项目适配服务启动超时。");
            } catch (Exception error) { operation.Fail(error); throw; }
        }
        static string Quote(string text) { return "\"" + text.Replace("\"", "") + "\""; }
        async Task HandleTimeMessage(ProjectSession item, CoreWebView2WebMessageReceivedEventArgs args)
        {
            Uri origin;
            if (!Uri.TryCreate(args.Source, UriKind.Absolute, out origin) || origin.Scheme != "https" || origin.Host != "shijie.project.moyubox.local" || item.Disposed) return;
            try
            {
                var message = RegistryStore.Json.Deserialize<Dictionary<string, object>>(args.WebMessageAsJson);
                string type = Convert.ToString(message["type"]);
                if (type == "text-open" || type == "text-save") await HandleTimeText(item, type, message);
                else if (type == "drag") owner.DragProjectWindow();
                else if (type == "notify") owner.Notify("时节", Convert.ToString(message["message"]));
                else if (type == "backup")
                {
                    using (var operation = Telemetry.Begin("bridge", "backup.write"))
                    try {
                        string folder = Path.Combine(store.ProjectRoot(item.Project.id), "backups"); Directory.CreateDirectory(folder);
                        string path = Path.Combine(folder, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".json"), content = Convert.ToString(message["data"]);
                        File.WriteAllText(path, content, Encoding.UTF8);
                        if (File.ReadAllText(path, Encoding.UTF8) != content) throw new IOException("Backup verification failed");
                        operation.Success(true, new Dictionary<string, object> { { "bytes", Encoding.UTF8.GetByteCount(content) } });
                        await item.View.CoreWebView2.ExecuteScriptAsync("window.pendingRestore && window.pendingRestore()");
                    } catch (Exception error) { operation.Fail(error); throw; }
                }
                else if (type == "image")
                {
                    bool saved = false;
                    using (var operation = Telemetry.Begin("bridge", "image.export")) {
                    try
                    {
                        string metrics = await item.View.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.getLayoutMetrics", "{}");
                        var result = RegistryStore.Json.Deserialize<Dictionary<string, object>>(metrics);
                        var size = (Dictionary<string, object>)result["cssContentSize"];
                        var clip = new Dictionary<string, object> { { "x", 0 }, { "y", 0 }, { "width", size["width"] }, { "height", size["height"] }, { "scale", 1 } };
                        string request = RegistryStore.Json.Serialize(new { format = "png", captureBeyondViewport = true, clip = clip });
                        string capture = await item.View.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", request);
                        var image = RegistryStore.Json.Deserialize<Dictionary<string, object>>(capture);
                        using (var dialog = new SaveFileDialog { Filter = "PNG 图片|*.png", FileName = "时节-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png" })
                        { if (dialog.ShowDialog(owner) == DialogResult.OK) { File.WriteAllBytes(dialog.FileName, Convert.FromBase64String(Convert.ToString(image["data"]))); saved = true; operation.Success(); } else operation.Cancel(); }
                    }
                    catch (Exception error) { operation.Fail(error); }
                    }
                    await item.View.CoreWebView2.ExecuteScriptAsync("window.finishImageExport && window.finishImageExport(" + (saved ? "true" : "false") + ")");
                }
                else if (type == "float") owner.SetProjectFloating(true);
                else if (type == "restore") owner.SetProjectFloating(false);
            }
            catch (Exception error) { Bootstrap.Log(store.Root, error); owner.Notify("时节操作未完成", error.Message); }
        }
        public async Task Close(string id)
        {
            ProjectSession item; if (!Items.TryGetValue(id, out item)) return;
            using (var operation = Telemetry.Begin("projects", "project.close", DiagnosticLog.Fingerprint(id)))
            try {
            foreach (var pending in item.Navigations.Values) { pending.Cancel("project_closed"); pending.Dispose(); } item.Navigations.Clear();
            if (ActiveId == id) owner.SetProjectFloating(false);
            Items.Remove(id); item.Disposed = true;
            if (item.NavigationReady != null) item.NavigationReady.TrySetResult(false);
            if (item.Opening != null && !item.Opening.IsCompleted) {
                if (await Task.WhenAny(item.Opening, Task.Delay(2000)) != item.Opening) Telemetry.Write("WARN", "projects", "close.pending_initialization", "Initialization did not settle before resource cleanup", null, null, true);
            }
            if (item.View != null) { item.View.Visible = false; item.View.Dispose(); }
            if (item.StaticServer != null) item.StaticServer.Dispose();
            if (item.Project.kind != "external" && item.Process != null)
            {
                if (!String.IsNullOrEmpty(item.ServiceUrl))
                {
                    try { await Task.Run(delegate { var request = (HttpWebRequest)WebRequest.Create(item.ServiceUrl + "shutdown"); request.Method = "POST"; request.Timeout = 2000; request.ReadWriteTimeout = 2000; request.Headers["X-Moyu-Token"] = item.ServiceToken; request.ContentLength = 2; using (var body = request.GetRequestStream()) body.Write(new byte[] { 123, 125 }, 0, 2); using (var response = request.GetResponse()) { } }); } catch (Exception error) { Bootstrap.Log(store.Root, error); }
                }
                await Task.Run(delegate { if (!item.Process.WaitForExit(item.Project.kind == "multiai" ? 12000 : 4000)) { Telemetry.Write("WARN", "process", "stop.forced", "Service did not exit after graceful shutdown; terminating owned service", null, null, true); item.Process.Kill(); if (!item.Process.WaitForExit(item.Project.kind == "multiai" ? 12000 : 4000)) throw new TimeoutException("Service process did not exit"); } });
                if (!String.IsNullOrEmpty(item.ServiceUrl))
                {
                    bool stopped = await Task.Run(delegate {
                        var endpoint = new Uri(item.ServiceUrl);
                        for (int attempt = 0; attempt < 3; attempt++)
                        {
                            using (var probe = new System.Net.Sockets.TcpClient())
                            try { if (probe.ConnectAsync(endpoint.Host, endpoint.Port).Wait(250)) { } }
                            catch (AggregateException error) { var socket = error.InnerException as System.Net.Sockets.SocketException; if (socket != null && socket.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused) return true; }
                            System.Threading.Thread.Sleep(100);
                        }
                        return false;
                    });
                    Telemetry.Write(stopped ? "INFO" : "WARN", "process", stopped ? "listener.stopped" : "release.unverified", stopped ? "Host verified that the service listener closed" : "Host process exited but listener release could not be verified", new Dictionary<string, object> { { "verified", stopped }, { "reason", stopped ? "connection_refused" : "probe_no_confirmed_refusal" } }, null, true);
                }
            }
            SetStatus(item, "closed", "close_requested");
            if(item.TaskbarGroup!=null)try{item.TaskbarGroup.Dispose();Telemetry.Write("INFO","taskbar","external.restored","Restored original taskbar group for independent EXE",null,null,true);}catch(Exception error){Telemetry.Caught("taskbar","restore.failed",error);}
            if (item.Process != null) item.Process.Dispose();
            if (ActiveId == id) ActiveId = null;
            operation.Success(); owner.Publish();
            } catch (Exception error) { operation.Fail(error); throw; }
        }
        public async Task CloseAll() { processTimer.Stop(); processTimer.Dispose(); fitTimer.Stop(); foreach (string id in new List<string>(Items.Keys)) await Close(id); fitTimer.Dispose(); }
    }
}
