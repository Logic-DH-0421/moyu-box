using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace AiNovelReader
{
    internal static class BrowserHost
    {
        public static void Run(DataStore store)
        {
            using (Mutex mutex = new Mutex(false, "Local\\AiNovelReader-" + store.LibraryIdentity))
            {
                bool owned = false;
                try
                {
                    try { owned = mutex.WaitOne(0); }
                    catch (AbandonedMutexException ex) { owned = true; RuntimeLog.Warning("host", "abandoned_instance_lock", ex, RuntimeLog.Fields("result", "recovered")); }
                    string infoPath = Path.Combine(store.RootDirectory, "server.url");
                    if (File.Exists(infoPath))
                    {
                        string existingUrl = File.ReadAllText(infoPath, Encoding.UTF8).Trim();
                        if (IsReaderServer(existingUrl, store.LibraryIdentity))
                        {
                            RuntimeLog.Emit("INFO", "host", "existing_instance_verified", "同版本同书库实例已通过 ping 校验。", RuntimeLog.Fields("stage", "verified", "libraryId", store.LibraryIdentity));
                            OpenBrowser(existingUrl);
                            return;
                        }
                    }
                    if (!owned)
                    {
                        RuntimeLog.Emit("WARN", "host", "instance_busy", "实例锁已占用，未启动第二个服务。", RuntimeLog.Fields("result", "cancelled"));
                        MessageBox.Show("同一书库已有阅读器运行，请退出该实例后重试。", "阅读器已运行", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    LocalHttpServer server = new LocalHttpServer(store);
                    server.Start();
                    try
                    {
                        string url = server.BaseUrl;
                        File.WriteAllText(infoPath, url, new UTF8Encoding(false));
                        OpenBrowser(url);
                        Application.EnableVisualStyles();
                        Application.SetCompatibleTextRenderingDefault(false);
                        Application.Run(new TrayContext(server, url));
                    }
                    finally { server.Stop(); }
                }
                finally { if (owned) mutex.ReleaseMutex(); }
            }
        }

        public static void OpenBrowser(string url)
        {
            using (var operation = RuntimeLog.Begin("browser", "launch", RuntimeLog.Fields("source", "default_browser")))
            { try {
            ProcessStartInfo info = new ProcessStartInfo(url);
            info.UseShellExecute = true;
            Process launched = Process.Start(info);
            if (launched != null) launched.Dispose();
            operation.Success(RuntimeLog.Fields("stage", "shell_accepted"));
            } catch (Exception ex) { operation.Fail(ex); throw; } }
        }

        private static bool IsReaderServer(string url, string libraryIdentity)
        {
            Uri address;
            if (!Uri.TryCreate(url, UriKind.Absolute, out address) || address.Scheme != "http" || address.Host != "127.0.0.1" || address.AbsolutePath != "/" || address.Query.Length != 0 || address.Fragment.Length != 0 || address.UserInfo.Length != 0) return false;
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(new Uri(address, "api/ping"));
                request.Timeout = 700;
                request.ReadWriteTimeout = 700;
                request.AllowAutoRedirect = false;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                {
                    if (response.StatusCode != HttpStatusCode.OK) return false;
                    Dictionary<string, object> data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                    object ok, application, version, identity;
                    bool verified = data != null && data.TryGetValue("ok", out ok) && ok is bool && (bool)ok
                        && data.TryGetValue("application", out application) && Convert.ToString(application) == "AiNovelReader"
                        && data.TryGetValue("version", out version) && Convert.ToString(version) == Program.Version
                        && data.TryGetValue("libraryId", out identity) && Convert.ToString(identity) == libraryIdentity;
                    if (!verified) RuntimeLog.Emit("WARN", "host", "instance_verification_mismatch", "旧地址的应用、版本或书库标识不匹配，未复用。", RuntimeLog.Fields("result", "ignored"));
                    return verified;
                }
            }
            catch (Exception ex) { RuntimeLog.Warning("host", "instance_probe_failed", ex, RuntimeLog.Fields("result", RuntimeLog.IsTimeout(ex) ? "timeout" : "unavailable")); return false; }
        }
    }

    internal sealed class TrayContext : ApplicationContext
    {
        private readonly LocalHttpServer server;
        private readonly string url;
        private readonly NotifyIcon tray;

        public TrayContext(LocalHttpServer server, string url)
        {
            this.server = server;
            this.url = url;
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("在浏览器中打开", null, delegate { BrowserHost.OpenBrowser(this.url); });
            menu.Items.Add("打开诊断日志目录", null, delegate { try { if (!String.IsNullOrEmpty(RuntimeLog.DirectoryPath)) Process.Start(new ProcessStartInfo(RuntimeLog.DirectoryPath) { UseShellExecute = true }); } catch (Exception ex) { RuntimeLog.Error("diagnostics", "open_directory_failed", ex, RuntimeLog.Fields("result", "failure")); } });
            menu.Items.Add("导出诊断日志", null, delegate { using (var dialog = new SaveFileDialog()) { dialog.Filter = "诊断包 (*.zip)|*.zip"; dialog.FileName = "NovelReader-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip"; if (dialog.ShowDialog() != DialogResult.OK) { RuntimeLog.Emit("INFO", "diagnostics", "export_cancelled", "用户取消选择诊断包保存位置。", RuntimeLog.Fields("result", "cancelled")); return; } using (var operation = RuntimeLog.Begin("diagnostics", "export_file", null)) { try { byte[] bytes = RuntimeLog.Export(); File.WriteAllBytes(dialog.FileName, bytes); operation.Success(RuntimeLog.Fields("bytes", bytes.Length, "stage", "written")); } catch (Exception ex) { operation.Fail(ex); MessageBox.Show("诊断日志导出失败，请检查保存位置。", "诊断日志"); } } } });
            menu.Items.Add("退出阅读器", null, delegate { ExitThread(); });
            tray = new NotifyIcon();
            tray.Icon = SystemIcons.Application;
            tray.Text = "AI 小说阅读器 " + Program.Version;
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            if (RuntimeLog.Degraded) tray.ShowBalloonTip(7000, "诊断日志使用备用存储", "日志目录不可写或写入失败。阅读器可继续使用，请尽快导出诊断包。", ToolTipIcon.Warning);
            tray.DoubleClick += delegate { BrowserHost.OpenBrowser(this.url); };
        }

        protected override void ExitThreadCore()
        {
            RuntimeLog.Emit("INFO", "host", "exit_requested", "用户退出阅读器，开始释放托盘和本地服务。", RuntimeLog.Fields("result", "started"));
            tray.Visible = false;
            tray.Dispose();
            server.Stop();
            base.ExitThreadCore();
        }
    }

    internal sealed class LocalHttpServer
    {
        private readonly DataStore store;
        private readonly JavaScriptSerializer serializer;
        private readonly string token;
        private TcpListener listener;
        private Thread acceptThread;
        private volatile bool running;
        private readonly string acceptTaskId = Guid.NewGuid().ToString("N");
        private int pendingRequests;
        private readonly ManualResetEvent requestsDrained = new ManualResetEvent(true);
        [ThreadStatic] private static HttpRequest respondingTo;

        public string WordUiDirectory { get; set; }
        public Action ShutdownRequested { get; set; }
        public bool UseEphemeralPort { get; set; }
        public string BaseUrl { get; private set; }

        public LocalHttpServer(DataStore store)
        {
            this.store = store;
            serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = Int32.MaxValue;
            token = Guid.NewGuid().ToString("N");
        }

        public void Start()
        {
            using (var operation = RuntimeLog.Begin("server", "start", null)) { try {
            int attempts = 0;
            for (int port = UseEphemeralPort ? 0 : 17890; port <= (UseEphemeralPort ? 0 : 17920); port++)
            {
                try
                {
                    attempts++;
                    listener = new TcpListener(IPAddress.Loopback, port);
                    listener.Start();
                    BaseUrl = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/";
                    break;
                }
                catch (SocketException ex)
                {
                    if (attempts == 1) RuntimeLog.Warning("server", "bind_retry", ex, RuntimeLog.Fields("attempt", attempts, "result", "retrying"));
                    if (listener != null) listener.Stop();
                    listener = null;
                }
            }
            if (listener == null) throw new InvalidOperationException("无法启动本地阅读服务，请关闭已运行的阅读器后重试。");
            running = true;
            acceptThread = new Thread(AcceptLoop);
            acceptThread.IsBackground = true;
            acceptThread.Name = "NovelReader HTTP";
            RuntimeLog.Emit("INFO", "background", "task_created", "创建本地服务接收线程。", RuntimeLog.Fields("taskId", acceptTaskId, "result", "created"));
            acceptThread.Start();
            operation.Success(RuntimeLog.Fields("attempt", attempts, "stage", "listening"));
            } catch (Exception ex) { operation.Fail(ex); throw; } }
        }

        public void Stop()
        {
            if (!running) return;
            using (var operation = RuntimeLog.Begin("server", "stop", null)) {
            running = false;
            Exception failure = null;
            try { listener.Stop(); } catch (Exception ex) { failure = ex; RuntimeLog.Warning("server", "listener_release_failed", ex, RuntimeLog.Fields("result", "failure")); }
            if (!requestsDrained.WaitOne(2000)) { RuntimeLog.MarkShutdownProblem(); failure = new TimeoutException("Pending local requests did not drain"); RuntimeLog.Emit("WARN", "server", "request_drain_timeout", "退出时仍有本地请求未结束。", RuntimeLog.Fields("count", pendingRequests, "result", "timeout")); }
            try { lock (store) { store.Save(); } } catch (Exception ex) { failure = ex; }
            if (Thread.CurrentThread != acceptThread && !acceptThread.Join(1500)) RuntimeLog.Emit("WARN", "server", "accept_thread_join_timeout", "接收线程未在退出等待期内结束。", RuntimeLog.Fields("result", "timeout"));
            if (failure == null) operation.Success(null); else { RuntimeLog.MarkShutdownProblem(); operation.Fail(failure); }
            }
        }

        private void AcceptLoop()
        {
            RuntimeLog.Emit("INFO", "background", "task_started", "服务接收线程开始。", RuntimeLog.Fields("taskId", acceptTaskId, "result", "started"));
            try {
            while (running)
            {
                try
                {
                    TcpClient client = listener.AcceptTcpClient();
                    lock (requestsDrained) { pendingRequests++; requestsDrained.Reset(); }
                    ThreadPool.QueueUserWorkItem(delegate { Handle(client); });
                }
                catch (Exception ex) { if (!running) return; RuntimeLog.RepeatedWarning("server", "accept_retry", ex); Thread.Sleep(250); }
            }
            } finally { RuntimeLog.Emit("INFO", "background", "task_finished", "服务接收线程结束。", RuntimeLog.Fields("taskId", acceptTaskId, "result", running ? "failure" : "cancelled")); }
        }

        private void Handle(TcpClient client)
        {
            using (client)
            {
                HttpRequest request = null;
                var receiveWatch = Stopwatch.StartNew();
                try
                {
                    client.ReceiveTimeout = 30000;
                    client.SendTimeout = 30000;
                    request = HttpRequest.Read(client.GetStream());
                    if (request == null) return;
                    respondingTo = request;
                    string clientOperation, clientId;
                    request.Headers.TryGetValue("x-operation-id", out clientOperation); request.Headers.TryGetValue("x-client-id", out clientId);
                    request.OperationId = RuntimeLog.Id(clientOperation);
                    using (RuntimeLog.Correlate(request.OperationId, clientId))
                    {
                        string route = SafeRoute(request.Target);
                        if (route == "/api/diagnostics/events") { Route(client.GetStream(), request); return; }
                        string interaction; request.Headers.TryGetValue("x-interaction-id", out interaction);
                        RuntimeLog.Emit("INFO", "http", "request_received", "请求已完整接收。", RuntimeLog.Fields("interactionId", String.IsNullOrEmpty(interaction) ? null : RuntimeLog.Id(interaction), "route", route, "bytes", request.Body.Length, "durationMs", receiveWatch.ElapsedMilliseconds));
                        using (var operation = RuntimeLog.Begin("http", "request", RuntimeLog.Fields("route", route)))
                        {
                            try { Route(client.GetStream(), request); if (request.ResponseStatus >= 400) { operation.Fail(new HttpStatusFailure(request.ResponseStatus), RuntimeLog.Fields("route", route, "status", request.ResponseStatus)); } else operation.Success(RuntimeLog.Fields("route", route, "status", request.ResponseStatus, "stage", "response_written")); }
                            catch (Exception ex) { operation.Fail(ex); throw; }
                        }
                    }
                }
                catch (Exception ex)
                {
                    RuntimeLog.Error("http", "request_failed", ex, RuntimeLog.Fields("operationId", request == null ? Guid.NewGuid().ToString("N") : request.OperationId, "result", RuntimeLog.IsTimeout(ex) ? "timeout" : "failure", "stage", request == null ? "receiving" : "routing_or_response", "durationMs", receiveWatch.ElapsedMilliseconds));
                    try { WriteJson(client.GetStream(), 500, serializer.Serialize(new { error = ex.Message, diagnosticSession = RuntimeLog.SessionId })); } catch (Exception writeError) { RuntimeLog.Warning("http", "error_response_failed", writeError, RuntimeLog.Fields("operationId", request == null ? null : request.OperationId, "result", "failure", "stage", "response_write")); }
                }
                finally { respondingTo = null; lock (requestsDrained) { if (--pendingRequests == 0) requestsDrained.Set(); } }
            }
        }

        private sealed class HttpStatusFailure : Exception { public HttpStatusFailure(int status) : base("HTTP operation failed") { Data["httpStatus"] = status; } }
        private static string SafeRoute(string target)
        {
            string path = (target ?? "").Split('?')[0];
            string[] allowed = { "/", "/favicon.ico", "/api/ping", "/api/library", "/api/chapter", "/api/search", "/api/progress", "/api/settings", "/api/import", "/api/remove", "/api/diagnostics/info", "/api/diagnostics/events", "/api/diagnostics/export" };
            return Array.IndexOf(allowed, path) >= 0 ? path : "unknown";
        }

        private void Route(Stream stream, HttpRequest request)
        {
            string expectedHost = new Uri(BaseUrl).Authority;
            if (!request.Headers.ContainsKey("host") || request.Headers["host"] != expectedHost || (request.Headers.ContainsKey("origin") && request.Headers["origin"] != BaseUrl.TrimEnd('/'))) { WriteJson(stream, 403, "{}"); return; }
            if (request.Method == "POST" && request.Target == "/shutdown") {
                string expected = Environment.GetEnvironmentVariable("MOYU_SERVICE_TOKEN");
                if (String.IsNullOrEmpty(expected) || !request.Headers.ContainsKey("x-moyu-token") || request.Headers["x-moyu-token"] != expected) { WriteJson(stream, 403, "{}"); return; }
                WriteJson(stream, 200, "{\"ok\":true}"); if (ShutdownRequested != null) ShutdownRequested(); return;
            }
            Uri uri = new Uri("http://127.0.0.1" + request.Target);
            string path = uri.AbsolutePath;
            if (request.Method == "GET" && path == "/")
            {
                string html = (String.IsNullOrEmpty(WordUiDirectory) ? ResourceLoader.BuildDocument() : File.ReadAllText(Path.Combine(WordUiDirectory, "index.html"), Encoding.UTF8)).Replace("/*__APP_TOKEN__*/", token);
                Write(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));
                return;
            }
            if (request.Method == "GET" && !String.IsNullOrEmpty(WordUiDirectory) && path.StartsWith("/word/", StringComparison.Ordinal)) {
                string name = path.Substring(6);
                string[] assets = { "reader.js", "reader.css", "gm-shim.js", "engine.js", "novel-disguise-resource.js", "novel-disguise-jquery.js" };
                if (Array.IndexOf(assets, name) < 0) { WriteJson(stream, 404, "{}"); return; }
                Write(stream, 200, name.EndsWith(".css") ? "text/css; charset=utf-8" : "text/javascript; charset=utf-8", File.ReadAllBytes(Path.Combine(WordUiDirectory, name))); return;
            }
            if (request.Method == "GET" && path == "/favicon.ico")
            {
                Write(stream, 204, "image/x-icon", new byte[0]);
                return;
            }
            if (request.Method == "GET" && path == "/api/ping")
            {
                WriteJson(stream, 200, serializer.Serialize(new { ok = true, application = "AiNovelReader", version = Program.Version, libraryId = store.LibraryIdentity }));
                return;
            }
            if (!request.Headers.ContainsKey("x-novel-token") || request.Headers["x-novel-token"] != token)
            {
                WriteJson(stream, 403, serializer.Serialize(new { error = "请求令牌无效。" }));
                return;
            }

            if (request.Method == "GET" && path == "/api/diagnostics/info")
            {
                WriteJson(stream, 200, serializer.Serialize(new { sessionId = RuntimeLog.SessionId, directory = RuntimeLog.DirectoryPath, level = RuntimeLog.DebugEnabled ? "DEBUG" : "INFO", degraded = RuntimeLog.Degraded })); return;
            }
            if (request.Method == "GET" && path == "/api/diagnostics/export")
            {
                Write(stream, 200, "application/zip", RuntimeLog.Export()); return;
            }
            if (request.Method == "POST" && path == "/api/diagnostics/events")
            {
                if (request.Body.Length > 8192) { WriteJson(stream, 400, "{\"error\":\"diagnostic payload too large\"}"); return; }
                RecordClientEvent(request.Body); WriteJson(stream, 200, "{\"ok\":true}"); return;
            }

            lock (store)
            {
                if (request.Method == "GET" && path == "/api/library")
                {
                    WriteJson(stream, 200, store.GetLibraryJson());
                    return;
                }
                if (request.Method == "GET" && path == "/api/chapter")
                {
                    Dictionary<string, string> query = ParseQuery(uri.Query);
                    string bookId = query.ContainsKey("bookId") ? query["bookId"] : String.Empty;
                    int index;
                    if (!Int32.TryParse(query.ContainsKey("index") ? query["index"] : "0", out index)) index = 0;
                    WriteJson(stream, 200, store.GetChapterJson(bookId, index));
                    return;
                }
                if (request.Method == "GET" && path == "/api/search")
                {
                    Dictionary<string, string> query = ParseQuery(uri.Query);
                    string keyword = query.ContainsKey("q") ? query["q"] : String.Empty;
                    string bookId = query.ContainsKey("bookId") ? query["bookId"] : String.Empty;
                    WriteJson(stream, 200, store.SearchJson(bookId, keyword));
                    return;
                }
                if (request.Method == "POST" && path == "/api/settings")
                {
                    try { store.SaveSettings(Encoding.UTF8.GetString(request.Body)); }
                    catch (InvalidDataException) { WriteJson(stream, 400, "{\"error\":\"设置无效，请检查快捷键是否重复。\"}"); return; }
                    WriteJson(stream, 200, store.GetLibraryJson());
                    return;
                }
                if (request.Method == "POST" && path == "/api/progress")
                {
                    Dictionary<string, object> data = serializer.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(request.Body));
                    string result = store.SaveProgressChecked(Convert.ToString(data["bookId"]), Convert.ToInt32(data["index"]),
                        data.ContainsKey("paragraph") ? Convert.ToInt32(data["paragraph"]) : 0,
                        data.ContainsKey("offset") ? Convert.ToDouble(data["offset"]) : 0,
                        data.ContainsKey("expectedRevision") ? Convert.ToInt64(data["expectedRevision"]) : -1);
                    WriteJson(stream, 200, result);
                    return;
                }
                if (request.Method == "POST" && path == "/api/import")
                {
                    string encodedName = request.Headers.ContainsKey("x-file-name") ? request.Headers["x-file-name"] : "novel.txt";
                    string fileName = Uri.UnescapeDataString(encodedName);
                    BookEntry book = store.ImportBookBytes(request.Body, fileName);
                    WriteJson(stream, 200, serializer.Serialize(new { ok = true, book = new { id = book.Id, title = book.Title }, library = serializer.DeserializeObject(store.GetLibraryJson()) }));
                    return;
                }
                if (request.Method == "POST" && path == "/api/remove")
                {
                    Dictionary<string, object> data = serializer.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(request.Body));
                    store.RemoveBook(Convert.ToString(data["bookId"]));
                    WriteJson(stream, 200, store.GetLibraryJson());
                    return;
                }
            }
            WriteJson(stream, 404, serializer.Serialize(new { error = "接口不存在。" }));
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string value = query.TrimStart('?');
            foreach (string pair in value.Split('&'))
            {
                if (String.IsNullOrEmpty(pair)) continue;
                string[] parts = pair.Split(new char[] { '=' }, 2);
                result[Uri.UnescapeDataString(parts[0])] = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : String.Empty;
            }
            return result;
        }

        private static void WriteJson(Stream stream, int status, string json)
        {
            Write(stream, status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
        }

        private static void Write(Stream stream, int status, string contentType, byte[] body)
        {
            string reason = status == 200 ? "OK" : status == 204 ? "No Content" : status == 403 ? "Forbidden" : status == 404 ? "Not Found" : "Internal Server Error";
            string headers = "HTTP/1.1 " + status + " " + reason + "\r\nContent-Type: " + contentType + "\r\nContent-Length: " + body.Length + "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nX-Diagnostic-Session: " + RuntimeLog.SessionId + "\r\nConnection: close\r\n\r\n";
            byte[] head = Encoding.ASCII.GetBytes(headers);
            stream.Write(head, 0, head.Length);
            if (body.Length > 0) stream.Write(body, 0, body.Length);
            stream.Flush();
            if (respondingTo != null) respondingTo.ResponseStatus = status;
        }

        private static readonly object clientEventGate = new object();
        private static long clientWindow;
        private static int clientEvents, clientSuppressed;
        private static readonly Stopwatch clientClock = Stopwatch.StartNew();
        private static void RecordClientEvent(byte[] body)
        {
            var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(body));
            if (data == null || !data.ContainsKey("event")) return;
            string name = Convert.ToString(data["event"]);
            string[] allowed = { "action_received", "request_started", "request_finished", "runtime_error", "unhandled_rejection", "user_action", "view_rendered", "stale_response", "selection_cancelled", "settings_state", "connection_recovered", "page_hidden", "queue_summary" };
            if (Array.IndexOf(allowed, name) < 0) return;
            string result = data.ContainsKey("result") ? Convert.ToString(data["result"]) : "started";
            if (Array.IndexOf(new string[] { "success", "failure", "cancelled", "timeout", "started", "ignored" }, result) < 0) result = "ignored";
            bool critical = name == "runtime_error" || name == "unhandled_rejection" || name == "request_finished" || result == "failure" || result == "timeout";
            lock (clientEventGate)
            {
                if (clientClock.ElapsedMilliseconds - clientWindow >= 10000) { if (clientSuppressed > 0) RuntimeLog.Emit("WARN", "frontend", "events_coalesced", "非关键前端事件已限频，终态与错误保留。", RuntimeLog.Fields("repeatCount", clientSuppressed, "intervalMs", clientClock.ElapsedMilliseconds - clientWindow)); clientSuppressed = 0; clientEvents = 0; clientWindow = clientClock.ElapsedMilliseconds; }
                if (++clientEvents > 100 && !critical) { clientSuppressed++; return; }
            }
            var fields = RuntimeLog.Fields("result", result);
            foreach (string key in new string[] { "durationMs", "status", "count", "line", "column", "repeatCount", "intervalMs" })
            { double value; if (data.ContainsKey(key) && Double.TryParse(Convert.ToString(data[key], System.Globalization.CultureInfo.InvariantCulture), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out value) && !Double.IsNaN(value) && !Double.IsInfinity(value)) fields[key] = Math.Max(0, Math.Min(value, 1e10)); }
            foreach (string key in new string[] { "clientTime", "clientMonotonicMs", "clientSequence" })
            { double value; if (data.ContainsKey(key) && Double.TryParse(Convert.ToString(data[key], System.Globalization.CultureInfo.InvariantCulture), out value) && !Double.IsNaN(value) && !Double.IsInfinity(value)) fields[key] = Math.Max(0, Math.Min(value, 8640000000000000)); }
            if (data.ContainsKey("stage") && Array.IndexOf(new string[] { "received", "download_triggered", "response_parsed", "rendered" }, Convert.ToString(data["stage"])) >= 0) fields["stage"] = data["stage"];
            if (data.ContainsKey("frames"))
            {
                var clean = new List<object>(); var frames = data["frames"] as System.Collections.IEnumerable;
                if (frames != null) foreach (var frame in frames) { if (clean.Count >= 12) break; var f = frame as Dictionary<string, object>; int line, column; if (f != null && f.ContainsKey("line") && f.ContainsKey("column") && Int32.TryParse(Convert.ToString(f["line"]), out line) && Int32.TryParse(Convert.ToString(f["column"]), out column)) clean.Add(RuntimeLog.Fields("line", Math.Max(0, line), "column", Math.Max(0, column))); }
                fields["frames"] = clean;
            }
            string[] actions = { "importButton", "composerImport", "deepseekNewChat", "settingsButton", "shortcutsButton", "safeButton", "libraryButton", "headerLibraryButton", "moreButton", "doubaoMoreButton", "sendButton", "previousChapterButton", "nextChapterButton", "reloadButton", "copyButton", "chapterShareButton", "doubaoInterfaceChoice", "deepseekInterfaceChoice", "readerInterfaceChoice", "diagnosticsExportButton", "font-increase", "font-decrease", "line-increase", "line-decrease", "paragraph-increase", "paragraph-decrease", "select_book", "select_chapter", "unknown", "export", "bookmark", "page", "api", "import", "settings", "chapter", "search", "progress" };
            if (data.ContainsKey("action") && Array.IndexOf(actions, Convert.ToString(data["action"])) >= 0) fields["action"] = data["action"];
            if (data.ContainsKey("errorType") && Array.IndexOf(new string[] { "Error", "TypeError", "RangeError", "SyntaxError", "AbortError", "TimeoutError", "NetworkError", "HTTPError", "UnknownError", "unhandled", "unknown" }, Convert.ToString(data["errorType"])) >= 0) fields["errorType"] = data["errorType"];
            using (RuntimeLog.Correlate(data.ContainsKey("operationId") ? Convert.ToString(data["operationId"]) : null, data.ContainsKey("clientId") ? Convert.ToString(data["clientId"]) : null))
                RuntimeLog.Emit(result == "failure" || result == "timeout" ? "ERROR" : name == "stale_response" ? "DEBUG" : "INFO", "frontend", name, "浏览器报告事件，未保存用户文本。", fields);
        }
    }

    internal sealed class HttpRequest
    {
        public string Method;
        public string Target;
        public Dictionary<string, string> Headers;
        public byte[] Body;
        public int ResponseStatus;
        public string OperationId;

        public static HttpRequest Read(Stream stream)
        {
            List<byte> received = new List<byte>();
            byte[] buffer = new byte[8192];
            int headerEnd = -1;
            while (headerEnd < 0)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) return null;
                for (int i = 0; i < read; i++) received.Add(buffer[i]);
                headerEnd = FindHeaderEnd(received);
                if (received.Count > 65536) throw new InvalidDataException("请求头过大。");
            }

            byte[] all = received.ToArray();
            string head = Encoding.ASCII.GetString(all, 0, headerEnd);
            string[] lines = head.Split(new string[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            HttpRequest request = new HttpRequest();
            request.Method = first[0].ToUpperInvariant();
            request.Target = first.Length > 1 ? first[1] : "/";
            request.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon > 0) request.Headers[lines[i].Substring(0, colon).Trim().ToLowerInvariant()] = lines[i].Substring(colon + 1).Trim();
            }
            int contentLength = 0;
            if (request.Headers.ContainsKey("content-length")) Int32.TryParse(request.Headers["content-length"], out contentLength);
            if (contentLength > 100 * 1024 * 1024) throw new InvalidDataException("文件超过 100MB 限制。");
            request.Body = new byte[contentLength];
            int already = all.Length - (headerEnd + 4);
            if (already > 0) Array.Copy(all, headerEnd + 4, request.Body, 0, Math.Min(already, contentLength));
            int offset = Math.Min(already, contentLength);
            while (offset < contentLength)
            {
                int read = stream.Read(request.Body, offset, contentLength - offset);
                if (read <= 0) throw new EndOfStreamException("请求正文未完整接收。");
                offset += read;
            }
            return request;
        }

        private static int FindHeaderEnd(List<byte> bytes)
        {
            for (int i = 3; i < bytes.Count; i++)
            {
                if (bytes[i - 3] == 13 && bytes[i - 2] == 10 && bytes[i - 1] == 13 && bytes[i] == 10) return i - 3;
            }
            return -1;
        }
    }
}
