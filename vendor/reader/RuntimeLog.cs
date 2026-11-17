using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace AiNovelReader
{
    // One process-wide sink. No book text, file names, request bodies or credentials enter it.
    public static class RuntimeLog
    {
        private static readonly object gate = new object(), ioGate = new object();
        private static readonly Queue<Dictionary<string, object>> queue = new Queue<Dictionary<string, object>>();
        private static readonly Dictionary<string, object> active = new Dictionary<string, object>();
        private static readonly Dictionary<string, long[]> repeats = new Dictionary<string, long[]>();
        private static readonly AutoResetEvent wake = new AutoResetEvent(false);
        private static readonly List<string> emergency = new List<string>();
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static Thread writer;
        private static StreamWriter output;
        private static string filePath, markerPath;
        private static bool closing, initialized, stopped, exitRequested, shutdownProblem;
        private static int droppedDebug, degradedCount, memoryEvictions, segmentIndex;
        private static long nextSequence;
        private static long firstDroppedMs, lastDroppedMs;
        private static string processStarted;
        public static string SessionId { get; private set; }
        public static string DirectoryPath { get; private set; }
        public static bool DebugEnabled { get; private set; }
        public static bool Degraded { get { return degradedCount > 0; } }
        public static string BuildId { get { return typeof(RuntimeLog).Assembly.ManifestModule.ModuleVersionId.ToString("N"); } }
        [ThreadStatic] private static string currentOperation;
        [ThreadStatic] private static string currentClient;
        public static string OperationId { get { return currentOperation; } }
        public static string ClientId { get { return currentClient; } }
        public static void MarkShutdownProblem() { shutdownProblem = true; }
        public static void InstallExceptionHandlers(Action requestUiExit)
        {
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e) {
                Exception error = e.ExceptionObject as Exception ?? new Exception("Non-Exception unhandled failure");
                Error("application", "unhandled_exception", error, Fields("result", "failure", "terminating", e.IsTerminating)); Shutdown("failure");
            };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += delegate(object sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e) { Error("background", "unobserved_task_exception", e.Exception, Fields("result", "failure")); };
            System.Windows.Forms.Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e) { Error("ui", "unhandled_exception", e.Exception, Fields("result", "failure")); requestUiExit(); };
        }
        public const int QueueLimit = 1024;
        public const long FileLimit = 2 * 1024 * 1024;
        public const int RetainedFiles = 8;
        public const int RetainedDays = 10;

        public static string Fingerprint(string value)
        {
            using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))).Replace("-", "").Substring(0, 16).ToLowerInvariant();
        }
        public static string Id(string value)
        {
            return value != null && Regex.IsMatch(value, "^[a-fA-F0-9-]{12,64}$") ? value.ToLowerInvariant() : Guid.NewGuid().ToString("N");
        }
        public static Dictionary<string, object> Fields(params object[] pairs)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            for (int i = 0; i + 1 < pairs.Length; i += 2) result[Convert.ToString(pairs[i], CultureInfo.InvariantCulture)] = pairs[i + 1];
            return result;
        }
        public static void Initialize(string libraryRoot, string directoryOverride)
        {
            if (initialized) return;
            initialized = true;
            SessionId = Guid.NewGuid().ToString("N");
            processStarted = Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("o");
            DebugEnabled = String.Equals(Environment.GetEnvironmentVariable("NOVELREADER_LOG_LEVEL"), "DEBUG", StringComparison.OrdinalIgnoreCase);
            string library = Fingerprint(Path.GetFullPath(libraryRoot).ToUpperInvariant());
            string preferred = directoryOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiNovelReader", "Diagnostics", library);
            DirectoryPath = preferred;
            try { OpenDirectory(preferred); }
            catch (Exception ex)
            {
                degradedCount++;
                DirectoryPath = Path.Combine(Path.GetTempPath(), "AiNovelReader-Diagnostics", library);
                try { OpenDirectory(DirectoryPath); }
                catch { degradedCount++; DirectoryPath = String.Empty; }
                Emit("WARN", "logging", "sink_fallback", "首选日志目录不可写，已尝试临时目录；查看 degraded 状态。", Fields("result", "degraded", "errorType", ex.GetType().FullName));
            }
            RecoverSessions();
            writer = new Thread(WriteLoop); writer.Name = "NovelReader diagnostics"; writer.IsBackground = true; writer.Start();
            Emit("INFO", "application", "session_started", "进程会话开始。", Fields("version", Program.Version, "buildId", BuildId, "processId", Process.GetCurrentProcess().Id,
                "osVersion", Environment.OSVersion.Version.ToString(), "clrVersion", Environment.Version.ToString(), "processBits", IntPtr.Size * 8,
                "libraryId", library, "level", DebugEnabled ? "DEBUG" : "INFO", "result", "started"));
        }
        private static void OpenDirectory(string path)
        {
            Directory.CreateDirectory(path);
            filePath = Path.Combine(path, "session-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + SessionId + "-0.jsonl");
            output = new StreamWriter(new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            markerPath = Path.Combine(path, SessionId + ".active");
            PersistMarker();
            Prune();
        }
        private static void RecoverSessions()
        {
            if (String.IsNullOrEmpty(DirectoryPath)) return;
            try
            {
                foreach (string path in Directory.GetFiles(DirectoryPath, "*.active"))
                {
                    if (path == markerPath) continue;
                    try
                    {
                        var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
                        bool alive = false;
                        try { Process p = Process.GetProcessById(Convert.ToInt32(data["processId"])); alive = !p.HasExited && p.StartTime.ToUniversalTime().ToString("o") == Convert.ToString(data["processStarted"]); } catch { }
                        if (alive) continue;
                        bool requested = data.ContainsKey("exitRequested") && Convert.ToBoolean(data["exitRequested"]);
                        Emit("WARN", "application", requested ? "previous_shutdown_incomplete" : "previous_session_interrupted", requested ? "旧会话曾请求退出，但清理或日志刷新未完成；不能断言为崩溃。" : "发现未正常结束的旧会话；不能仅据此区分强制关闭、断电或崩溃。", Fields("previousSessionId", Id(Convert.ToString(data["sessionId"])), "result", "interrupted", "pendingOperations", data.ContainsKey("operations") ? data["operations"] : null));
                        File.Move(path, path + ".recovered");
                    }
                    catch (Exception ex) { Emit("WARN", "logging", "marker_read_failed", "旧会话标记不可读。", Fields("errorType", ex.GetType().FullName)); }
                }
            }
            catch (Exception ex) { Emit("WARN", "logging", "recovery_scan_failed", "无法扫描旧会话标记。", Fields("errorType", ex.GetType().FullName)); }
        }
        private static void PersistMarker()
        {
            if (String.IsNullOrEmpty(markerPath)) return;
            object[] pending;
            lock (gate) pending = active.Values.ToArray();
            string json = new JavaScriptSerializer().Serialize(Fields("sessionId", SessionId, "processId", Process.GetCurrentProcess().Id, "processStarted", processStarted, "exitRequested", exitRequested, "operations", pending));
            File.WriteAllText(markerPath + ".tmp", json, new UTF8Encoding(false));
            if (File.Exists(markerPath)) File.Replace(markerPath + ".tmp", markerPath, null); else File.Move(markerPath + ".tmp", markerPath);
        }
        private static void Prune()
        {
            var files = new DirectoryInfo(DirectoryPath).GetFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
            for (int i = 0; i < files.Length; i++)
            {
                // Never remove another live process's log or this process's current segment.
                string[] parts = files[i].Name.Split('-');
                bool live = parts.Length >= 4 && parts[3] != SessionId && File.Exists(Path.Combine(DirectoryPath, parts[3] + ".active"));
                if (files[i].FullName != filePath && !live && (i >= RetainedFiles || files[i].LastWriteTimeUtc < DateTime.UtcNow.AddDays(-RetainedDays))) files[i].Delete();
            }
            foreach (var marker in new DirectoryInfo(DirectoryPath).GetFiles("*.recovered")) if (marker.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-RetainedDays)) marker.Delete();
        }
        public static void Emit(string level, string module, string name, string message, Dictionary<string, object> fields)
        {
            if (!initialized || stopped || (level == "DEBUG" && !DebugEnabled)) return;
            var record = Fields("time", DateTimeOffset.Now.ToString("o"), "level", level, "module", module, "event", name,
                "sessionId", SessionId, "message", message, "sequence", Interlocked.Increment(ref nextSequence), "monotonicMs", clock.ElapsedMilliseconds,
                "processId", Process.GetCurrentProcess().Id, "threadId", Thread.CurrentThread.ManagedThreadId);
            if (!String.IsNullOrEmpty(currentOperation)) record["operationId"] = currentOperation;
            if (!String.IsNullOrEmpty(currentClient)) record["clientId"] = currentClient;
            if (fields != null) foreach (var p in fields)
            {
                if (Regex.IsMatch(p.Key, "(?i)(password|token|cookie|authorization|content|filename|filepath|query|url|title|requestbody|responsebody)")) continue;
                record[p.Key] = p.Value;
            }
            bool direct = false;
            lock (gate)
            {
                if (queue.Count >= QueueLimit)
                {
                    if (level == "DEBUG") { if (droppedDebug == 0) firstDroppedMs = clock.ElapsedMilliseconds; lastDroppedMs = clock.ElapsedMilliseconds; droppedDebug++; return; }
                    direct = true; // Important records never silently disappear on queue pressure.
                }
                else queue.Enqueue(record);
            }
            if (direct) WriteOne(record);
            wake.Set();
        }
        public static void Error(string module, string name, Exception error, Dictionary<string, object> fields)
        { LogException("ERROR", module, name, error, fields); }
        public static void Warning(string module, string name, Exception error, Dictionary<string, object> fields)
        { LogException("WARN", module, name, error, fields); }
        public static void RepeatedWarning(string module, string name, Exception error)
        {
            string key = module + "." + name + "." + error.GetType().FullName;
            long count = 0, interval = 0; bool emit = false;
            lock (gate)
            {
                long[] state;
                if (!repeats.TryGetValue(key, out state)) { state = new long[] { clock.ElapsedMilliseconds, 0 }; repeats[key] = state; emit = true; }
                state[1]++; interval = clock.ElapsedMilliseconds - state[0];
                if (interval >= 10000) { count = state[1]; state[0] = clock.ElapsedMilliseconds; state[1] = 0; emit = true; }
                else if (emit) state[1] = 0;
            }
            if (emit) Warning(module, name, error, Fields("repeatCount", count == 0 ? 1 : count, "intervalMs", interval, "result", "retrying"));
        }
        private static void LogException(string level, string module, string name, Exception error, Dictionary<string, object> fields)
        {
            if (fields == null) fields = Fields();
            lock (error)
            {
                string reference = error.Data["NovelReaderDiagnosticError"] as string;
                if (reference == null)
                {
                    reference = Guid.NewGuid().ToString("N"); error.Data["NovelReaderDiagnosticError"] = reference;
                    fields["exception"] = ExceptionInfo(error, 0);
                }
                fields["errorId"] = reference;
            }
            Emit(level, module, name, "异常正文已省略，类型、错误码与调用栈如下；请结合终态判断最终结果。", fields);
        }
        private static object ExceptionInfo(Exception ex, int depth)
        {
            if (depth > 12) return Fields("type", "inner_depth_limit");
            // Exception messages often contain titles, user paths or malformed file contents.
            var result = Fields("type", ex.GetType().FullName, "hresult", System.Runtime.InteropServices.Marshal.GetHRForException(ex).ToString("X8"),
                "stack", SafeStack(ex.StackTrace), "messagePolicy", "omitted_to_protect_user_content");
            var socket = ex as System.Net.Sockets.SocketException;
            if (socket != null) result["nativeCode"] = socket.NativeErrorCode;
            var native = ex as System.ComponentModel.Win32Exception;
            if (native != null) result["nativeCode"] = native.NativeErrorCode;
            var aggregate = ex as AggregateException;
            if (aggregate != null) result["inner"] = aggregate.InnerExceptions.Select(e => ExceptionInfo(e, depth + 1)).ToArray();
            else if (ex.InnerException != null) result["inner"] = ExceptionInfo(ex.InnerException, depth + 1);
            return result;
        }
        private static string SafeStack(string stack)
        {
            return Regex.Replace(stack ?? "", @"(?:[A-Za-z]:\\|/)[^\r\n]*?(?=:line |:行号 |$)", "<source>", RegexOptions.Multiline);
        }
        public static Operation Begin(string module, string name, Dictionary<string, object> fields) { return new Operation(module, name, fields); }
        public sealed class Operation : IDisposable
        {
            private readonly string parent, module, name;
            private readonly Stopwatch watch = Stopwatch.StartNew();
            private bool ended;
            public string Id { get; private set; }
            public Operation(string mod, string ev, Dictionary<string, object> fields)
            {
                parent = currentOperation; module = mod; name = ev; Id = Guid.NewGuid().ToString("N"); currentOperation = Id;
                var info = fields == null ? Fields() : new Dictionary<string, object>(fields); info["parentOperationId"] = parent; info["result"] = "started";
                lock (gate) active[Id] = Fields("operationId", Id, "parentOperationId", parent, "module", mod, "event", ev);
                Emit("INFO", module, name + "_started", "开始执行。", info);
            }
            public void Success(Dictionary<string, object> fields) { End("success", null, fields); }
            public void Cancel() { End("cancelled", null, null); }
            public void Fail(Exception ex) { End(IsTimeout(ex) ? "timeout" : "failure", ex, null); }
            public void Fail(Exception ex, Dictionary<string, object> fields) { End(IsTimeout(ex) ? "timeout" : "failure", ex, fields); }
            private void End(string result, Exception ex, Dictionary<string, object> fields)
            {
                if (ended) return; ended = true;
                var info = fields == null ? Fields() : new Dictionary<string, object>(fields); info["result"] = result; info["durationMs"] = watch.ElapsedMilliseconds; info["parentOperationId"] = parent;
                if (ex == null) Emit(result == "success" ? "INFO" : "WARN", module, name + "_finished", "操作已结束。", info); else Error(module, name + "_finished", ex, info);
                lock (gate) active.Remove(Id);
                currentOperation = parent;
            }
            public void Dispose() { if (!ended) End("failure", new InvalidOperationException("Operation exited without explicit terminal result"), null); currentOperation = parent; }
        }
        public static bool IsTimeout(Exception ex)
        {
            var io = ex as IOException; var socket = ex as System.Net.Sockets.SocketException;
            var web = ex as System.Net.WebException;
            return ex is TimeoutException || (socket != null && socket.SocketErrorCode == System.Net.Sockets.SocketError.TimedOut) ||
                (web != null && web.Status == System.Net.WebExceptionStatus.Timeout) || (io != null && io.InnerException != null && IsTimeout(io.InnerException));
        }
        public static IDisposable Correlate(string operation, string client)
        {
            return new Correlation(operation, client);
        }
        private sealed class Correlation : IDisposable
        {
            private readonly string previous, previousClient;
            public Correlation(string op, string client) { previous = currentOperation; previousClient = currentClient; currentOperation = RuntimeLog.Id(op); currentClient = String.IsNullOrEmpty(client) ? null : RuntimeLog.Id(client); }
            public void Dispose() { currentOperation = previous; currentClient = previousClient; }
        }
        private static void WriteLoop()
        {
            long lastSample = 0, lastMemory = 0;
            while (true)
            {
                wake.WaitOne(500);
                try { Drain(); }
                catch (Exception ex) { lock (ioGate) SinkFailure(ex); }
                if (clock.ElapsedMilliseconds - lastSample >= 60000)
                {
                    try { long memory = Process.GetCurrentProcess().WorkingSet64; if (memory > 512L * 1024 * 1024 || (lastMemory > 0 && memory - lastMemory > 128L * 1024 * 1024)) Emit("WARN", "resources", "memory_growth", "内存超过关注阈值；此记录不等于泄漏诊断。", Fields("workingSetBytes", memory)); lastMemory = memory; lastSample = clock.ElapsedMilliseconds; } catch { }
                }
                lock (gate) if (closing && queue.Count == 0) break;
            }
        }
        private static void Drain()
        {
            Dictionary<string, object> item;
            while (true) { lock (gate) { if (queue.Count == 0) break; item = queue.Dequeue(); } WriteOne(item); }
            lock (ioGate)
            {
                try
                {
                    if (output != null) output.Flush();
                    PersistMarker();
                }
                catch (Exception ex) { SinkFailure(ex); }
            }
            int count; long interval; lock (gate) { count = droppedDebug; interval = lastDroppedMs - firstDroppedMs; droppedDebug = 0; }
            if (count > 0) Emit("WARN", "logging", "queue_pressure", "队列已合并 DEBUG 记录；重要记录保留。", Fields("repeatCount", count, "intervalMs", interval));
        }
        private static void WriteOne(Dictionary<string, object> item)
        {
            lock (ioGate)
            {
                string json;
                try { json = new JavaScriptSerializer().Serialize(item); }
                catch (Exception ex) { SinkFailure(ex); return; }
                try
                {
                    if (output == null) throw new IOException("No writable log sink");
                    if (output.BaseStream.Length > FileLimit)
                    {
                        output.Dispose();
                        filePath = Path.Combine(DirectoryPath, "session-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + SessionId + "-" + (++segmentIndex) + ".jsonl");
                        output = new StreamWriter(new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)); Prune();
                    }
                    output.WriteLine(json);
                    // Bound crash-loss window; terminal/error records are flushed immediately.
                    if (Convert.ToString(item["level"]) == "ERROR" || Convert.ToString(item["event"]).EndsWith("_finished")) output.Flush();
                }
                catch (Exception ex)
                {
                    SinkFailure(ex); if (emergency.Count >= 256) { emergency.RemoveAt(0); memoryEvictions++; } emergency.Add(json);
                }
            }
        }
        private static void SinkFailure(Exception ex)
        {
            degradedCount++;
            if (degradedCount == 1 || degradedCount % 100 == 0) Trace.WriteLine("NovelReader diagnostics degraded: " + ex.GetType().FullName + "; count=" + degradedCount);
        }
        public static bool Flush(int milliseconds)
        {
            if (!initialized) return true;
            var timer = Stopwatch.StartNew(); wake.Set();
            while (timer.ElapsedMilliseconds < milliseconds)
            {
                bool empty; lock (gate) empty = queue.Count == 0;
                if (empty) { lock (ioGate) { try { if (output != null) output.Flush(); PersistMarker(); return !Degraded; } catch { return false; } } }
                Thread.Sleep(10);
            }
            return false;
        }
        public static byte[] Export()
        {
            using (var op = Begin("diagnostics", "export", null))
            {
                try
                {
                    Flush(2000);
                    using (var memory = new MemoryStream())
                    {
                        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
                        {
                            var manifest = zip.CreateEntry("diagnostics-info.json");
                            using (var text = new StreamWriter(manifest.Open(), new UTF8Encoding(false))) text.Write(new JavaScriptSerializer().Serialize(Fields("version", Program.Version, "buildId", BuildId, "sessionId", SessionId, "snapshotTime", DateTimeOffset.Now.ToString("o"), "degraded", Degraded, "degradedCount", degradedCount, "memoryEvictions", memoryEvictions, "formatVersion", 1, "containsBooks", false)));
                            long bytes = 0;
                            if (!String.IsNullOrEmpty(DirectoryPath)) foreach (var f in new DirectoryInfo(DirectoryPath).GetFiles("*.jsonl").OrderByDescending(x => x.LastWriteTimeUtc).Take(RetainedFiles))
                            {
                                if (bytes + f.Length > FileLimit * RetainedFiles) break;
                                using (var stream = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                                using (var reader = new StreamReader(stream))
                                using (var text = new StreamWriter(zip.CreateEntry(f.Name).Open(), new UTF8Encoding(false)))
                                {
                                    string line; while ((line = reader.ReadLine()) != null) { string safe = ExportLine(line); if (safe != null) text.WriteLine(safe); }
                                }
                                bytes += f.Length;
                            }
                            lock (ioGate) if (emergency.Count > 0) using (var text = new StreamWriter(zip.CreateEntry("memory-fallback.jsonl").Open(), new UTF8Encoding(false))) foreach (string line in emergency) { string safe = ExportLine(line); if (safe != null) text.WriteLine(safe); }
                        }
                        op.Success(Fields("bytes", memory.Length, "stage", "package_created")); return memory.ToArray();
                    }
                }
                catch (Exception ex) { op.Fail(ex); throw; }
            }
        }
        private static string ExportLine(string line)
        {
            try
            {
                var record = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(line);
                // Project only schema fields; ignore additions/foreign content before exporting.
                string[] allowed = { "time", "level", "module", "event", "sessionId", "sequence", "monotonicMs", "processId", "threadId", "operationId", "parentOperationId", "interactionId", "taskId", "clientId", "clientTime", "clientMonotonicMs", "clientSequence", "line", "column", "frames", "result", "durationMs", "errorId", "exception", "errorType", "status", "route", "bytes", "bookId", "chapter", "count", "version", "buildId", "repeatCount", "intervalMs", "previousSessionId", "pendingOperations", "pendingCount", "degradedCount", "processBits", "clrVersion", "osVersion", "libraryId", "formatVersion", "fromChapter", "toChapter", "paragraph", "offset", "action", "source", "stage", "attempt", "encoding", "extension", "bookCount", "chapterCount", "resourceBytes", "workingSetBytes", "fromMode", "toMode", "fromTheme", "toTheme", "fromFont", "toFont", "bookmarkCount" };
                allowed = allowed.Concat(new[] { "expectedRevision", "actualRevision", "revision", "changed" }).ToArray();
                var safe = new Dictionary<string, object>(); foreach (string key in allowed) if (record.ContainsKey(key)) safe[key] = record[key];
                // Export omits even our trusted free-text messages and all filesystem paths.
                safe["message"] = "See event and structured fields";
                string json = new JavaScriptSerializer().Serialize(safe);
                json = Regex.Replace(json, @"(?i)(?:[a-z]:\\\\|https?://)[^""\r\n]*", "<redacted>");
                return json;
            }
            catch { return null; }
        }
        public static void Shutdown(string result)
        {
            if (!initialized || closing || stopped) return;
            exitRequested = true;
            List<Dictionary<string, object>> summaries = new List<Dictionary<string, object>>();
            lock (gate) foreach (var repeat in repeats) if (repeat.Value[1] > 0) summaries.Add(Fields("source", repeat.Key, "repeatCount", repeat.Value[1], "intervalMs", clock.ElapsedMilliseconds - repeat.Value[0]));
            foreach (var summary in summaries) Emit("WARN", "logging", "repeat_summary", "重复错误剩余次数摘要。", summary);
            Emit("INFO", "application", "session_finished", "进程准备退出。", Fields("result", shutdownProblem ? "failure" : result, "durationMs", clock.ElapsedMilliseconds, "pendingCount", active.Count, "degradedCount", degradedCount));
            closing = true; wake.Set(); bool joined = writer == null || Thread.CurrentThread == writer || writer.Join(3000);
            if (!joined) { stopped = true; Trace.WriteLine("NovelReader diagnostics shutdown timed out; session marker retained."); return; }
            lock (ioGate)
            {
                try { if (output != null) { output.Flush(); output.Dispose(); output = null; } if (joined && active.Count == 0 && File.Exists(markerPath)) File.Delete(markerPath); }
                catch (Exception ex) { SinkFailure(ex); }
                stopped = true;
            }
        }
    }
}
