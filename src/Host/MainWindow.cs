using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MoyuBox
{
    internal sealed partial class MainWindow : Form
    {
        public readonly bool Integration;
        readonly RegistryStore store;
        readonly RankingFeed rankingFeed;
        readonly WebView2 shell;
        readonly SessionManager sessions;
        readonly NotifyIcon tray;
        bool exiting;
        bool focus;
        bool ready;
        string importWarning;
        public MainWindow(RegistryStore store, bool integration)
        {
            TaskbarWindowGroup.Initialize();
            this.store = store; Integration = integration; rankingFeed = new RankingFeed(store.Root);
            Text = "摸鱼盒子";
            AutoScaleMode = AutoScaleMode.Dpi; ConfigureCompactWindow(); BackColor = Color.FromArgb(247, 248, 250);
            string icon = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico"); if (File.Exists(icon)) Icon = new Icon(icon);
            shell = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
            Controls.Add(shell); sessions = new SessionManager(this, store);
            var menu = new ContextMenuStrip();
            menu.Items.Add("打开摸鱼盒子", null, delegate { Restore(); });
            menu.Items.Add("退出", null, async delegate { await ExitApplication(); });
            tray = new NotifyIcon { Icon = Icon, Text = "摸鱼盒子", ContextMenuStrip = menu, Visible = !integration };
            tray.DoubleClick += delegate { Restore(); };
            Shown += async delegate { await Initialize(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (!exiting && !Integration) { e.Cancel = true; Hide(); Telemetry.Write("INFO", "window", "tray.hidden", "Main window hidden to tray"); Notify("摸鱼盒子仍在后台", "双击托盘图标恢复；从托盘菜单可退出。"); } };
            FormClosed += delegate { tray.Dispose(); };
            ResizeEnd += delegate { if (WindowState == FormWindowState.Normal) { store.State.width = Width; store.State.height = Height; try { store.Save(); } catch (Exception error) { Bootstrap.Log(store.Root, error); } } };
        }
        void Restore() { Telemetry.Write("INFO", "window", "tray.restored", "Main window restored"); Show(); if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal; Activate(); }
        public void Notify(string title, string message) { if (!Integration && tray.Visible) tray.ShowBalloonTip(4500, title, message, ToolTipIcon.Info); }
        async Task Initialize()
        {
            using (var operation = Telemetry.Begin("shell", "initialize"))
            try
            {
                if (!File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shell", "index.html"))) throw new FileNotFoundException("框架页面文件缺失。");
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(store.Root, "shell-browser"));
                await shell.EnsureCoreWebView2Async(env);
                shell.CoreWebView2.SetVirtualHostNameToFolderMapping("shell.moyubox.local", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shell"), CoreWebView2HostResourceAccessKind.DenyCors);
                shell.CoreWebView2.Settings.AreHostObjectsAllowed = false;
                shell.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                shell.CoreWebView2.Settings.AreDevToolsEnabled = Integration;
                shell.CoreWebView2.Settings.IsStatusBarEnabled = false;
                shell.CoreWebView2.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs e) { Uri uri; if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out uri) || uri.Scheme != "https" || uri.Host != "shell.moyubox.local") e.Cancel = true; };
                shell.CoreWebView2.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs e) { e.Handled = true; };
                shell.CoreWebView2.WebMessageReceived += Receive;
                var shellLoaded = new TaskCompletionSource<bool>();
                shell.CoreWebView2.NavigationCompleted += async delegate(object sender, CoreWebView2NavigationCompletedEventArgs e)
                {
                    if (!e.IsSuccess) { shellLoaded.TrySetException(new InvalidOperationException("Shell navigation failed: " + e.WebErrorStatus)); return; }
                    shellLoaded.TrySetResult(true);
                    ready = true; Telemetry.Write("INFO", "shell", "navigation.completed", "Shell page loaded", null, null, true); Publish();
                    if (Integration) await RunIntegration();
                    else { Task fetching = rankingFeed.Refresh(Publish); }
                };
                shell.CoreWebView2.ProcessFailed += delegate(object sender, CoreWebView2ProcessFailedEventArgs e) { Telemetry.Write("ERROR", "shell", "webview.process_failed", "Shell webview process failed", new Dictionary<string, object> { { "code", e.ProcessFailedKind.ToString() } }, null, true); };
                shell.CoreWebView2.Navigate("https://shell.moyubox.local/index.html");
                if (await Task.WhenAny(shellLoaded.Task, Task.Delay(30000)) != shellLoaded.Task) throw new TimeoutException("Shell navigation timeout");
                await shellLoaded.Task; operation.Success();
            }
            catch (Exception error) { operation.Fail(error); if (Integration) { Task finishing = FinishIntegration(false, error.ToString()); } else MessageBox.Show("初始化失败：" + error.Message, "摸鱼盒子"); }
        }
        public void Publish()
        {
            if (!ready || shell.IsDisposed) return;
            var states = sessions.Items.ToDictionary(x => x.Key, x => new { status = x.Value.Status, error = x.Value.Error, taskbar = x.Value.TaskbarGroup != null ? "grouped" : x.Value.GroupAttempts >= 3 ? "unavailable" : "pending", zoom = x.Value.View == null ? 100 : (int)Math.Round(x.Value.View.ZoomFactor * 100) });
            shell.CoreWebView2.PostWebMessageAsJson(RegistryStore.Json.Serialize(new { type = "snapshot", state = new { version = "0.8.1", importWarning = importWarning, maximized = WindowState == FormWindowState.Maximized, autoMaximizeProjects = store.State.autoMaximizeProjects, defaultBookReader = store.State.defaultBookReader, projects = store.State.projects, rankingFeed = rankingFeed, favoriteIds = store.State.favoriteIds, displayName = store.State.displayName, bio = store.State.bio, theme = store.State.theme, feedbackDraft = store.State.feedbackDraft, contactEmail = store.State.contactEmail, contactUrl = store.State.contactUrl, dataPath = store.Root, diagnostics = Telemetry.Current == null ? null : new { path = Telemetry.Current.DirectoryPath, level = Telemetry.Current.Level, session = Telemetry.Current.SessionId, warning = Telemetry.Current.DegradedMessage }, sessions = states, active = sessions.ActiveId, recoveryNotice = store.RecoveryNotice } }));
        }
        async void Receive(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            Uri source;
            if (!Uri.TryCreate(e.Source, UriKind.Absolute, out source) || source.Scheme != "https" || source.Host != "shell.moyubox.local") return;
            string requestId = null;
            try
            {
                var message = RegistryStore.Json.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson);
                requestId = Convert.ToString(message["id"]);
                string method = Convert.ToString(message["method"]);
                var p = message.ContainsKey("params") ? message["params"] as Dictionary<string, object> : null;
                p = p ?? new Dictionary<string, object>();
                object result = await Dispatch(method, p);
                if (!shell.IsDisposed) shell.CoreWebView2.PostWebMessageAsJson(RegistryStore.Json.Serialize(new { replyTo = requestId, ok = true, value = result }));
            }
            catch (Exception error)
            {
                Telemetry.Caught("rpc", "request.failed", error, false);
                if (!shell.IsDisposed) shell.CoreWebView2.PostWebMessageAsJson(RegistryStore.Json.Serialize(new { replyTo = requestId, ok = false, error = error.Message }));
            }
        }
        static string TextValue(Dictionary<string, object> values, string key) { return values.ContainsKey(key) ? Convert.ToString(values[key]) : ""; }
        async Task<object> Dispatch(string method, Dictionary<string, object> p)
        {
            if (method == "viewport") { Telemetry.Summary("window", "viewport"); return await DispatchCore(method, p); }
            using (var operation = Telemetry.Request("action." + DiagnosticLog.Code(method), DiagnosticLog.Fingerprint(TextValue(p, "id"))))
            try {
                object result = await DispatchCore(method, p);
                if (result is bool && !(bool)result && new[] { "importBook", "importHtml", "importLocal", "addExternal", "remove", "exportDiagnostics", "exit" }.Contains(method)) operation.Cancel();
                else operation.Success();
                Publish(); return result;
            } catch (Exception error) { operation.Fail(error); throw; }
        }
        async Task<object> DispatchCore(string method, Dictionary<string, object> p)
        {
            string id = TextValue(p, "id");
            switch (method)
            {
                case "bootstrap": Publish(); return true;
                case "rankingRefresh": await rankingFeed.Refresh(Publish); return rankingFeed.status == "ready";
                case "rankingAdd": { string url = TextValue(p,"url"); if(!RankingFeed.IsRepo(url)) throw new ArgumentException("无效仓库地址。"); if(!store.State.projects.Any(x=>x.url==url && String.IsNullOrEmpty(x.rankingCategory))) store.Add(new Project { id="repo-"+Guid.NewGuid().ToString("N").Substring(0,12),name=new Uri(url).AbsolutePath.Trim('/'),kind="github",url=url,description="从 GitHub 趋势榜加入",icon="blocks" }); Publish(); return true; }
                case "shellError":
                    { string code = TextValue(p, "code"); if (code != "script_error" && code != "unhandled_rejection") code = "unknown";
                      Telemetry.Write("ERROR", "shell", "script.unhandled", "Unhandled framework JavaScript error", new Dictionary<string, object> { { "code", code }, { "script_stack", TextValue(p, "stack") }, { "line", p.ContainsKey("line") ? Convert.ToInt32(p["line"]) : 0 } }, null, true); return true; }
                case "open": await sessions.Open(id); if (sessions.Items[id].Status == "error") throw sessions.Items[id].LastException ?? new InvalidOperationException("项目未能就绪。"); return true;
                case "home": sessions.HideAll(); ReturnToCompactWindow(); Publish(); return true;
                case "viewport":
                    double cssWidth = Convert.ToDouble(p["pageWidth"]);
                    if (cssWidth < 100) return false;
                    double ratio = shell.ClientSize.Width / cssWidth;
                    int left = Math.Max(0, (int)Math.Round(Convert.ToDouble(p["x"]) * ratio));
                    int top = Math.Max(0, (int)Math.Round(Convert.ToDouble(p["y"]) * ratio));
                    int width = Math.Min(shell.ClientSize.Width - left, (int)Math.Round(Convert.ToDouble(p["width"]) * ratio));
                    int height = Math.Min(shell.ClientSize.Height - top, (int)Math.Round(Convert.ToDouble(p["height"]) * ratio));
                    sessions.Layout(new Rectangle(left, top, Math.Max(0, width), Math.Max(0, height))); return true;
                case "stop": await sessions.Close(id); return true;
                case "refresh": { ProjectSession s; if (sessions.Items.TryGetValue(id, out s) && s.View != null) s.View.Reload(); return true; }
                case "back": { ProjectSession s; if (sessions.Items.TryGetValue(id, out s) && s.View != null && s.View.CanGoBack) s.View.GoBack(); return true; }
                case "external":
                    { string url = await sessions.BrowserUrl(id);
                      Process.Start(new ProcessStartInfo(Rules.Website(url).AbsoluteUri) { UseShellExecute = true }); Publish(); return true; }
                case "favorite":
                    { store.Find(id); var previous = new List<string>(store.State.favoriteIds);
                      if (!store.State.favoriteIds.Remove(id)) store.State.favoriteIds.Add(id);
                      try { store.Save(); } catch { store.State.favoriteIds = previous; throw; } Publish(); return true; }
                case "profile":
                    { string name = TextValue(p, "name").Trim(), bio = TextValue(p, "bio").Trim();
                      if (name.Length < 1 || name.Length > 24 || bio.Length > 120) throw new ArgumentException("昵称须为 1–24 字，简介不超过 120 字。");
                      string oldName = store.State.displayName, oldBio = store.State.bio;
                      store.State.displayName = name; store.State.bio = bio;
                      try { store.Save(); } catch { store.State.displayName = oldName; store.State.bio = oldBio; throw; } Publish(); return true; }
                case "addGithub":
                    { var project = new Project { id = "gh-" + Guid.NewGuid().ToString("N").Substring(0, 12), kind = "github", icon = "blocks", name = TextValue(p, "name").Trim(), url = TextValue(p, "url").Trim(), launchUrl = TextValue(p, "launchUrl").Trim() };
                      project.description = String.IsNullOrWhiteSpace(project.launchUrl) ? "GitHub 源码仓库 · 可关联部署地址" : "GitHub 项目 · 打开已部署的应用";
                      store.Add(project); Publish(); return true; }
                case "importBook":
                    using (var dialog = new OpenFileDialog { Filter = "电子书|*.txt;*.epub", Title = "将 TXT / EPUB 加入鱼塘" })
                    { if (dialog.ShowDialog(this) != DialogResult.OK) return false; AddBook(dialog.FileName); Publish(); return true; }
                case "importHtml":
                    using (var dialog = new OpenFileDialog { Filter = "静态网页|*.html;*.htm", Title = "选择网页入口（将复制所在文件夹，最大 50MB）" })
                    {
                        if (dialog.ShowDialog(this) != DialogResult.OK) return false;
                        var project = new Project { id = "site-" + Guid.NewGuid().ToString("N").Substring(0, 12), kind = "local-web", zoomPercent = 0, icon = "globe", name = Path.GetFileNameWithoutExtension(dialog.FileName), entry = Path.GetFileName(dialog.FileName), description = "导入的静态网页" };
                        ImportPackage(project, Path.GetDirectoryName(dialog.FileName)); Publish(); return true;
                    }
                case "window": ToggleProjectWindow(); return true;
                case "windowPolicy":
                    { bool enabled = TextValue(p, "mode") == "auto"; bool previous = store.State.autoMaximizeProjects; store.State.autoMaximizeProjects = enabled;
                      try { store.Save(); } catch { store.State.autoMaximizeProjects = previous; throw; } Publish(); return true; }
                case "zoom":
                    { Project project = store.Find(id); int zoom = Convert.ToInt32(p["percent"]); if (zoom != 0 && (zoom < 50 || zoom > 200)) throw new ArgumentException("不支持的缩放比例。");
                      int previous = project.zoomPercent; project.zoomPercent = zoom; try { store.Save(); } catch { project.zoomPercent = previous; throw; }
                      await sessions.ApplyZoom(id); Publish(); return true; }
                case "pin": TopMost = !TopMost; return TopMost;
                case "hide": Hide(); return true;
                case "focus": SetProjectFocus(!focus); return focus;
                case "addWebsite":
                    { string name = TextValue(p, "name").Trim(); string url = Rules.Website(TextValue(p, "url").Trim()).AbsoluteUri;
                      store.Add(new Project { id = "web-" + Guid.NewGuid().ToString("N").Substring(0, 12), name = name, url = url, kind = "website", description = new Uri(url).Host, icon = "globe" }); Publish(); return true; }
                case "importLocal": return await ImportLocal();
                case "addExternal":
                    using (var dialog = new OpenFileDialog { Filter = "桌面程序|*.exe", Title = "关联一个桌面程序" })
                    { if (dialog.ShowDialog(this) != DialogResult.OK) return false; store.Add(new Project { id = "app-" + Guid.NewGuid().ToString("N").Substring(0, 12), name = Path.GetFileNameWithoutExtension(dialog.FileName), entry = dialog.FileName, kind = "external", icon = "app", description = "在独立窗口中运行的桌面程序" }); Publish(); return true; }
                case "toggle":
                    { Project project = store.Find(id); bool previous = project.enabled; project.enabled = !project.enabled;
                      try { store.Save(); } catch { project.enabled = previous; throw; }
                      if (!project.enabled) await sessions.Close(id); Publish(); return project.enabled; }
                case "remove":
                    { Project project = store.Find(id); if (project.builtin) throw new InvalidOperationException("内置项目可以停用，不能移除。");
                      if (MessageBox.Show(this, "移除“" + project.name + "”的入口？项目数据仍保留。", "移除项目", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return false;
                      await sessions.Close(id); int position = store.State.projects.IndexOf(project); var favorites = new List<string>(store.State.favoriteIds);
                      store.State.projects.Remove(project); store.State.favoriteIds.Remove(id);
                      try { store.Save(); } catch { store.State.projects.Insert(position, project); store.State.favoriteIds = favorites; throw; } Publish(); return true; }
                case "settings":
                    { string theme = TextValue(p, "theme"); if (!new[] { "system", "light", "dark" }.Contains(theme)) throw new ArgumentException("无效主题。");
                      string previous = store.State.theme; store.State.theme = theme; try { store.Save(); } catch { store.State.theme = previous; throw; } Publish(); return true; }
                case "bookReader":
                    { string reader = TextValue(p, "reader"); if (reader != "word" && reader != "ai") throw new ArgumentException("未知阅读方式。");
                      string previous = store.State.defaultBookReader; store.State.defaultBookReader = reader;
                      try { store.Save(); } catch { store.State.defaultBookReader = previous; throw; }
                      Publish(); return true; }
                case "feedback":
                    { string draft = TextValue(p, "draft"); if (draft.Length > 20000) throw new ArgumentException("草稿不能超过 20000 字。"); string previous = store.State.feedbackDraft; store.State.feedbackDraft = draft; try { store.Save(); } catch { store.State.feedbackDraft = previous; throw; } return true; }
                case "openData": Process.Start(new ProcessStartInfo(store.Root) { UseShellExecute = true }); return true;
                case "openLogs": Process.Start(new ProcessStartInfo(Telemetry.Current.DirectoryPath) { UseShellExecute = true }); return true;
                case "exportDiagnostics": return await ExportDiagnostics();
                case "logLevel":
                    { string level = TextValue(p, "level"); if (level != "INFO" && level != "DEBUG") throw new ArgumentException("日志级别无效。");
                      string path = Path.Combine(store.Root, "diagnostics.json"), temp = path + ".tmp";
                      var config = File.Exists(path) ? RegistryStore.Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(path)) : new Dictionary<string, object>();
                      config["level"] = level; string content = RegistryStore.Json.Serialize(config); File.WriteAllText(temp, content);
                      if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
                      if (File.ReadAllText(path) != content) throw new IOException("日志配置校验失败。");
                      Telemetry.Current.SetLevel(level); Publish(); return true; }
                case "exit": await ExitApplication(); return exiting;
                default: throw new ArgumentException("框架不支持此操作。");
            }
        }
        async Task<bool> ImportLocal()
        {
            using (var dialog = new OpenFileDialog { Filter = "网页项目包|*.zip;manifest.json|ZIP 压缩包|*.zip|项目描述文件|manifest.json", Title = "选择 ZIP 或 manifest.json 网页项目包" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return false;
                if(String.Equals(Path.GetExtension(dialog.FileName),".zip",StringComparison.OrdinalIgnoreCase)) return await ImportArchive(dialog.FileName)!=null;
                Project project = RegistryStore.Json.Deserialize<Project>(File.ReadAllText(dialog.FileName, System.Text.Encoding.UTF8));
                project.builtin = false; project.enabled = true;
                if (project.kind != "local-web") throw new ArgumentException("项目包类型须为 local-web。");
                Rules.Validate(project, true);
                if (store.State.projects.Any(x => x.id == project.id)) throw new ArgumentException("此项目 ID 已存在。");
                string source = Path.GetDirectoryName(dialog.FileName); string entry = Rules.ContainedPath(source, project.entry);
                if (!File.Exists(entry)) throw new FileNotFoundException("项目入口不存在。");
                ImportPackage(project, source);
                Publish(); return true;
            }
        }
        void ImportPackage(Project project, string source)
        {
            importWarning=null;
            using (var operation = Telemetry.Begin("imports", "package.import", DiagnosticLog.Fingerprint(project.id)))
            try {
            Rules.Validate(project, true);
            if (!File.Exists(Rules.ContainedPath(source, project.entry))) throw new FileNotFoundException("项目入口不存在。");
            string target = Path.Combine(store.ProjectRoot(project.id), "package");
            if (Directory.Exists(target)) throw new IOException("此 ID 存在保留的项目包，请使用新 ID。");
            string sourceRoot = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(target).StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请先将网页放入独立文件夹，再导入。源文件夹不能包含项目数据目录。");
            string stage = target + ".staging-" + Guid.NewGuid().ToString("N");
            try { long size = 0; int count = 0; CopyPackage(source, stage, ref size, ref count); string warning=PackageResources.Check(stage,project.entry); Directory.Move(stage, target);
                try { store.Add(project); } catch { Directory.Delete(target,true); throw; }
                importWarning = warning;
            }
            catch { if (Directory.Exists(stage)) Directory.Delete(stage, true); throw; }
            operation.Success(true);
            } catch (Exception error) { operation.Fail(error); throw; }
        }
        Project AddBook(string source)
        {
            importWarning=null;
            using (var operation = Telemetry.Begin("imports", "book.import", DiagnosticLog.Fingerprint(source)))
            try {
            if (new FileInfo(source).Length > 50 * 1024 * 1024) throw new ArgumentException("电子书不能超过 50MB。");
            var project = new Project { id = "book-" + Guid.NewGuid().ToString("N").Substring(0, 12), name = Path.GetFileNameWithoutExtension(source), kind = "book", icon = "book", entry = "document" + Path.GetExtension(source).ToLowerInvariant(), description = "电子书 · 独立阅读进度" };
            if (project.name.Length > 60) project.name = project.name.Substring(0, 60);
            Rules.Validate(project, true);
            string data = store.ProjectRoot(project.id);
            // Parse before adding so damaged EPUBs do not leave an unusable entry in the pond.
            try { new AiNovelReader.DataStore(Path.Combine(data, "library")).ImportBook(source); }
            catch (InvalidDataException error) { if(error.Message.StartsWith("EPUB 导入失败"))throw; throw new InvalidDataException(Path.GetExtension(source).Equals(".epub",StringComparison.OrdinalIgnoreCase) ? "EPUB 导入失败：文件可能损坏、不完整或缺少有效正文，请重新下载完整文件后重试。" : "TXT 导入失败：文件为空或内容无效，请检查后重试。",error); }
            File.Copy(source, Rules.ContainedPath(data, project.entry), false);
            if (new FileInfo(source).Length != new FileInfo(Rules.ContainedPath(data, project.entry)).Length) throw new IOException("Imported file size verification failed");
            store.Add(project); operation.Success(true, new Dictionary<string, object> { { "bytes", new FileInfo(source).Length }, { "kind", Path.GetExtension(source).TrimStart('.') } }); return project;
            } catch (Exception error) { operation.Fail(error); throw; }
        }
        static void CopyPackage(string source, string destination, ref long size, ref int count)
        {
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("项目包不支持符号链接。");
            Directory.CreateDirectory(destination);
            foreach (string path in Directory.GetFiles(source))
            { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("项目包不支持符号链接。"); size += new FileInfo(path).Length; count++; if (size > 50 * 1024 * 1024 || count > 5000) throw new IOException("项目包须小于 50MB / 5000 个文件。"); File.Copy(path, Path.Combine(destination, Path.GetFileName(path))); }
            foreach (string path in Directory.GetDirectories(source)) CopyPackage(path, Path.Combine(destination, Path.GetFileName(path)), ref size, ref count);
        }
        public void SetProjectFocus(bool value)
        {
            focus = value;
            if (ready) shell.CoreWebView2.PostWebMessageAsJson(RegistryStore.Json.Serialize(new { type = "focus", value = value }));
        }
        async Task ExitApplication()
        {
            if (exiting) return;
            using (var operation = Telemetry.Begin("lifecycle", "host.shutdown"))
            try {
            if (sessions.Items.Count > 0 && !Integration && MessageBox.Show(this, "退出会关闭盒子内的网页和项目服务。请先在时节结束不需要继续的计时。B站官方客户端已提交的缓存不会被取消。\n\n确认退出？", "退出摸鱼盒子", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) { operation.Cancel(); return; }
            exiting = true; await sessions.CloseAll(); store.Save(); tray.Visible = false; operation.Success(); Close(); }
            catch (Exception error) { exiting = false; operation.Fail(error); throw; }
        }
    }
}
