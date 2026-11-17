using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace MoyuBox
{
    public sealed class LogOptions
    {
        public string level = "INFO";
        public int maxFileBytes = 4 * 1024 * 1024;
        public int maxTotalBytes = 64 * 1024 * 1024;
        public int retentionDays = 14;
        public int queueCapacity = 2048;
        public string fallbackDirectory;
    }

    // One host-owned sink. Project/backend logs are intentionally outside this collector.
    public static class Telemetry
    {
        public static DiagnosticLog Current;
        static readonly AsyncLocal<OperationLog> context = new AsyncLocal<OperationLog>();
        public static OperationLog Context { get { return context.Value; } internal set { context.Value = value; } }
        public static OperationLog Begin(string module, string action, string objectId = null) { return new OperationLog(Current, module, action, objectId, true); }
        public static OperationLog Request(string action, string objectId) { return new OperationLog(Current, "user", action, objectId, true, null, true); }
        public static void Write(string level, string module, string ev, string message, IDictionary<string, object> data = null, Exception error = null, bool important = false)
        { if (Current != null) Current.Write(level, module, ev, message, data, error, important, Context); }
        public static void Caught(string module, string ev, Exception error, bool recoverable = true)
        { Write(recoverable ? "WARN" : "ERROR", module, ev, "Caught exception at host boundary", null, error, true); }
        public static void Summary(string module, string ev, IDictionary<string, object> data = null)
        { if (Current != null) Current.Summary(module, ev, data); }
    }

    public sealed class OperationLog : IDisposable
    {
        readonly DiagnosticLog log;
        readonly OperationLog parent, previous;
        readonly bool bind;
        readonly Stopwatch watch = Stopwatch.StartNew();
        int ended;
        public readonly string Id = Guid.NewGuid().ToString("N");
        public readonly string CorrelationId;
        public readonly string Module;
        public readonly string Action;
        public readonly string ObjectId;
        public string ParentId { get { return parent == null ? null : parent.Id; } }
        internal OperationLog(DiagnosticLog log, string module, string action, string objectId, bool bind, OperationLog forcedParent = null, bool rootRequest = false)
        {
            this.log = log; this.bind = bind; previous = Telemetry.Context; parent = rootRequest ? null : forcedParent ?? previous;
            Module = module; Action = action; ObjectId = objectId;
            CorrelationId = parent == null ? Id : parent.CorrelationId;
            if (bind) Telemetry.Context = this;
            if (log != null) { if (rootRequest) log.Write("INFO", Module, Action + ".received", "Host dispatch received request", new Dictionary<string, object> { { "phase", "received" } }, null, true, this); log.Track(this, true); log.Write("INFO", Module, Action + ".started", "Execution started", new Dictionary<string, object> { { "phase", "started" } }, null, true, this); }
        }
        public void Success(bool verified = false, IDictionary<string, object> data = null) { Finish("success", null, verified, data); }
        public void Cancel(string reason = "user_cancelled") { Finish("cancelled", null, false, new Dictionary<string, object> { { "reason", reason } }); }
        public void Fail(Exception error) { Finish(error is TimeoutException ? "timeout" : error is OperationCanceledException ? "cancelled" : "failed", error, false, null); }
        void Finish(string result, Exception error, bool verified, IDictionary<string, object> data)
        {
            if (Interlocked.Exchange(ref ended, 1) != 0) return;
            var fields = data == null ? new Dictionary<string, object>() : new Dictionary<string, object>(data);
            fields["phase"] = "terminal"; fields["result"] = result; fields["elapsed_ms"] = watch.Elapsed.TotalMilliseconds; fields["verified"] = verified;
            if (log != null)
            {
                log.Write(result == "failed" || result == "timeout" ? "ERROR" : result == "interrupted" ? "WARN" : "INFO", Module, Action + ".finished", "Execution reached terminal state", fields, error, true, this);

            }
        }
        public void Dispose()
        {
            if (ended == 0) Finish("interrupted", null, false, new Dictionary<string, object> { { "reason", "scope_left_without_terminal" } });
            if (bind && Telemetry.Context == this) Telemetry.Context = previous;
        }
    }

    public sealed class DiagnosticLog : IDisposable
    {
        readonly object gate = new object(), fileGate = new object(), journalGate = new object(), checkpointGate = new object(), emergencyGate = new object();
        readonly Queue<string> queue = new Queue<string>();
        readonly Dictionary<string, Dictionary<string, object>> active = new Dictionary<string, Dictionary<string, object>>();
        readonly Dictionary<string, SummaryRecord> summaries = new Dictionary<string, SummaryRecord>();
        readonly AutoResetEvent signal = new AutoResetEvent(false);
        readonly ManualResetEvent drained = new ManualResetEvent(true);
        readonly Thread writer;
        readonly string primary, fallback, rootHash;
        readonly DateTimeOffset processStart = new DateTimeOffset(Process.GetCurrentProcess().StartTime);
        readonly LogOptions options;
        readonly System.Threading.Timer resources;
        string file;
        int fileIndex, disposed, journalDirty;
        long sequence, dropped, lastResourceTicks, lastSinkFailureTicks, queuedBytes, memoryBytes;
        int checkpointFailureReported;
        volatile bool stopping;
        public readonly string SessionId = Guid.NewGuid().ToString("N");
        public readonly int Pid = Process.GetCurrentProcess().Id;
        public readonly string Version = typeof(DiagnosticLog).Assembly.GetName().Version.ToString();
        public readonly string Build;
        public string DirectoryPath { get; private set; }
        public string DegradedMessage { get; private set; }
        public string Level { get { return options.level; } }
        public bool NormalExit;
        readonly List<string> recoveredMarkers = new List<string>();
        public int ExportSkipped { get; private set; }
        public long MemoryLost { get { return Interlocked.Read(ref memoryLost); } }
        public string RootHash { get { return rootHash; } }
        sealed class SummaryRecord { public int count; public DateTimeOffset first, last; public IDictionary<string, object> data; }
        static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }; }
        public static string Fingerprint(string value)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))).Replace("-", "").Substring(0, 16).ToLowerInvariant(); }
        public DiagnosticLog(string root, LogOptions supplied = null)
        {
            Exception configError = null;
            options = supplied ?? LoadOptions(root, out configError);
            if (supplied != null) configError = null;
            if (options.level != "INFO" && options.level != "DEBUG") options.level = "INFO";
            options.queueCapacity = Math.Max(1, options.queueCapacity);
            options.maxFileBytes = Math.Max(1024, options.maxFileBytes);
            options.maxTotalBytes = Math.Max(options.maxFileBytes * 2, options.maxTotalBytes);
            options.retentionDays = Math.Max(1, options.retentionDays);
            rootHash = Fingerprint(Path.GetFullPath(root).ToLowerInvariant()); primary = Path.Combine(root, "logs");
            fallback = options.fallbackDirectory ?? Path.Combine(Path.GetTempPath(), "MoyuBox-Diagnostics", rootHash);
            Build = BuildFingerprint(); DirectoryPath = primary;
            Exception sinkError = null;
            try { Probe(primary); } catch (Exception error) { sinkError = error; DirectoryPath = fallback; DegradedMessage = "日志主目录不可写，已尝试切换备用诊断目录。"; try { Probe(fallback); } catch { DegradedMessage = "诊断磁盘写入不可用；日志仅暂存在内存，请尽快导出。"; } }
            RecoverIncomplete();
            WriteJournal(false);
            writer = new Thread(WriterLoop) { IsBackground = true, Name = "Moyu diagnostics" }; writer.Start();
            Write("INFO", "lifecycle", "session.started", "Host session started", new Dictionary<string, object> { { "version", Version }, { "build", Build }, { "runtime", Environment.Version.ToString() }, { "os", Environment.OSVersion.Version.ToString() }, { "is_64bit", Environment.Is64BitProcess }, { "schema", 1 }, { "level", Level } }, null, true, null);
            if (configError != null) Write("WARN", "diagnostics", "config.invalid", "Diagnostic configuration invalid; defaults selected", null, configError, true);
            if (sinkError != null) Write("WARN", "diagnostics", "sink.primary_failed", "Primary diagnostic sink unavailable", null, sinkError, true);
            if (DegradedMessage != null) Write("WARN", "diagnostics", "sink.degraded", "Using fallback or in-memory diagnostics", null, null, true, null);
            if (recoveredMarkers.Count > 0 && Flush()) foreach (string marker in recoveredMarkers) try { File.Delete(marker); } catch { DegradedMessage = "历史中断已记录，但旧检查点未能清理。"; }
            PruneMarkers();
            resources = new System.Threading.Timer(delegate { ResourceSample(); }, null, 60000, 60000);
        }
        static string BuildFingerprint()
        { try { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(typeof(DiagnosticLog).Assembly.Location)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").Substring(0, 16).ToLowerInvariant(); } catch { return "unavailable"; } }
        static LogOptions LoadOptions(string root, out Exception failure)
        {
            failure = null;
            try { string path = Path.Combine(root, "diagnostics.json"); if (File.Exists(path)) { var value = Json().Deserialize<LogOptions>(File.ReadAllText(path)); value.fallbackDirectory = null; value.maxFileBytes = Math.Max(65536, Math.Min(8 * 1024 * 1024, value.maxFileBytes)); value.maxTotalBytes = Math.Max(value.maxFileBytes * 2, Math.Min(128 * 1024 * 1024, value.maxTotalBytes)); value.retentionDays = Math.Max(1, Math.Min(30, value.retentionDays)); value.queueCapacity = 2048; return value; } } catch (Exception error) { failure = error; }
            return new LogOptions();
        }
        static void Probe(string directory) { Directory.CreateDirectory(directory); string probe = Path.Combine(directory, ".probe-" + Guid.NewGuid().ToString("N")); using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.WriteByte(0); File.Delete(probe); }
        public void SetLevel(string level)
        {
            if (level != "INFO" && level != "DEBUG") throw new ArgumentException("日志级别须为 INFO 或 DEBUG。");
            options.level = level;
            Write("INFO", "diagnostics", "level.changed", "Diagnostic level updated", new Dictionary<string, object> { { "level", level } }, null, true, null);
        }
        static readonly HashSet<string> Allowed = new HashSet<string>(("script_stack line child_process_id phase result reason elapsed_ms verified count bytes schema version build runtime os is_64bit level stage from to attempt retry_count error_code error_id dependency exit_code process_id parent_pid width height zoom status code kind method enabled repeats first_time last_time previous_session_id previous_operation_id previous_correlation_id pending dropped private_bytes managed_bytes peak_bytes file_count skipped_count source_hash observed requested recovered transport hash retention_days capacity max_file_bytes max_total_bytes written active_count").Split(' '));
        public static string Code(string value)
        { return value != null && Regex.IsMatch(value, "^[a-zA-Z0-9_.+\\-]{1,160}$") ? value : value == null ? null : "hash-" + Fingerprint(value); }
        static string TimeField(object value)
        { DateTimeOffset time; return DateTimeOffset.TryParse(Convert.ToString(value), out time) ? time.ToString("o") : "invalid"; }
        static Dictionary<string, object> SafeFields(IDictionary<string, object> data)
        {
            var result = new Dictionary<string, object>(); if (data == null) return result;
            foreach (var pair in data)
            {
                if (!Allowed.Contains(pair.Key) || pair.Value == null) continue;
                if (pair.Key == "script_stack") { result[pair.Key] = SafeScriptStack(Convert.ToString(pair.Value)); continue; }
                if (pair.Value is double && (Double.IsNaN((double)pair.Value) || Double.IsInfinity((double)pair.Value))) { result[pair.Key] = "nonfinite"; continue; }
                if (pair.Value is bool || pair.Value is int || pair.Value is long || pair.Value is double) result[pair.Key] = pair.Value;
                else result[pair.Key] = pair.Key == "first_time" || pair.Key == "last_time" ? TimeField(pair.Value) : Code(Convert.ToString(pair.Value));
            }
            return result;
        }
        public static string SafeScriptStack(string value)
        {
            // Drop the arbitrary error message; retain only Chrome stack frame lines.
            return SafeStack(String.Join("\n", (value ?? "").Split('\n').Where(x => Regex.IsMatch(x, @"^\s*at [a-zA-Z0-9_.$<> ]+[( ]")).Take(64)));
        }
        public static string SafeStack(string value)
        {
            value = value ?? "";
            value = Regex.Replace(value, @"[a-zA-Z]:[\\/][^\r\n]*?(?=(:line \d+|:行号 \d+|\r?\n|$))", "<source-path>");
            value = Regex.Replace(value, @"https?://[^\s]+", "<url>");
            return value.Length > 24000 ? value.Substring(0, 24000) + " <stack-truncated>" : value;
        }
        static Dictionary<string, object> ErrorInfo(Exception error, int depth)
        { int budget = 64; return ErrorDetails(error, depth, ref budget); }
        static Dictionary<string, object> ErrorDetails(Exception error, int depth, ref int budget)
        {
            if (budget-- <= 0 || depth > 12) return new Dictionary<string, object> { { "truncated", true } };
            var value = new Dictionary<string, object> { { "type", error.GetType().FullName }, { "hresult", error.HResult }, { "stack", SafeStack(error.StackTrace) }, { "message", "Exception details withheld; use type, code, stack and operation context" } };
            if (error.Data.Contains("MoyuErrorId")) value["error_id"] = Code(Convert.ToString(error.Data["MoyuErrorId"]));
            var native = error as System.ComponentModel.Win32Exception; if (native != null) value["native_error_code"] = native.NativeErrorCode;
            var network = error as System.Net.WebException; if (network != null) value["network_status"] = network.Status.ToString();
            var aggregate = error as AggregateException;
            if (aggregate != null) {
                var causes = new List<object>(); foreach (var cause in aggregate.InnerExceptions.Take(12)) { causes.Add(ErrorDetails(cause, depth + 1, ref budget)); if (budget <= 0) break; }
                value["causes"] = causes; if (aggregate.InnerExceptions.Count > causes.Count) value["truncated"] = true;
            } else if (error.InnerException != null) value["inner"] = ErrorDetails(error.InnerException, depth + 1, ref budget);
            return value;
        }
        public void Write(string level, string module, string ev, string message, IDictionary<string, object> data = null, Exception error = null, bool important = false, OperationLog operation = null)
        {
            try
            {
                if (disposed != 0 || (level == "DEBUG" && options.level != "DEBUG" && !important)) return;
                var record = new Dictionary<string, object> { { "schema", 1 }, { "timestamp", DateTimeOffset.Now.ToString("o") }, { "level", level }, { "module", Code(module) }, { "event", Code(ev) }, { "session_id", SessionId }, { "process_id", Pid }, { "sequence", Interlocked.Increment(ref sequence) }, { "message", message } };
                if (operation != null) { record["operation_id"] = operation.Id; record["correlation_id"] = operation.CorrelationId; record["parent_operation_id"] = operation.ParentId; record["object_id"] = Code(operation.ObjectId); }
                foreach (var pair in SafeFields(data)) record[pair.Key] = pair.Value;
                if (error != null)
                {
                    lock (error)
                    {
                        string errorId = error.Data["MoyuErrorId"] as string;
                        if (errorId == null) { errorId = Guid.NewGuid().ToString("N"); error.Data["MoyuErrorId"] = errorId; record["exception"] = ErrorInfo(error, 0); }
                        record["error_id"] = errorId;
                    }
                }
                string line = Json().Serialize(record);
                bool overflow = false;
                lock (gate) { if (queue.Count < options.queueCapacity && queuedBytes + line.Length * 2L <= 8 * 1024 * 1024) { queue.Enqueue(line); queuedBytes += line.Length * 2L; drained.Reset(); } else overflow = true; }
                if (overflow) { if (important || level == "ERROR" || level == "WARN") Emergency(line); else Interlocked.Increment(ref dropped); }
                else signal.Set();
            }
            catch { DegradedMessage = "日志组件异常，主功能继续；部分诊断信息可能不可用。"; }
        }
        public void Summary(string module, string ev, IDictionary<string, object> data)
        {
            lock (gate)
            {
                string key = Code(module) + ":" + Code(ev); SummaryRecord record;
                if (!summaries.TryGetValue(key, out record)) summaries[key] = record = new SummaryRecord { first = DateTimeOffset.Now };
                record.count++; record.last = DateTimeOffset.Now; record.data = SafeFields(data);
            }
        }
        void FlushSummaries()
        {
            Dictionary<string, SummaryRecord> batch;
            lock (gate) { batch = new Dictionary<string, SummaryRecord>(summaries); summaries.Clear(); }
            foreach (var pair in batch)
            {
                int split = pair.Key.IndexOf(':'); var fields = new Dictionary<string, object>(pair.Value.data ?? new Dictionary<string, object>());
                fields["repeats"] = pair.Value.count; fields["first_time"] = pair.Value.first.ToString("o"); fields["last_time"] = pair.Value.last.ToString("o");
                Write("INFO", pair.Key.Substring(0, split), pair.Key.Substring(split + 1) + ".summary", "Repeated host events summarized", fields, null, true, null);
            }
            long count = Interlocked.Exchange(ref dropped, 0); if (count > 0) Write("WARN", "diagnostics", "queue.saturated", "Noncritical diagnostic records summarized after queue saturation", new Dictionary<string, object> { { "dropped", count } }, null, true, null);
        }
        void WriterLoop()
        {
            long summaryAt = Stopwatch.GetTimestamp();
            while (true)
            {
                string line = null;
                lock (gate) { if (queue.Count > 0) { line = queue.Dequeue(); queuedBytes -= line.Length * 2L; } }
                if (line != null) { try { Append(line); } catch (Exception error) { ReportSinkFailure(error); Emergency(line); } continue; }
                // Batch checkpoint updates off the caller thread. Flush covers both queues.
                if (Interlocked.Exchange(ref journalDirty, 0) != 0) WriteJournal(false);
                lock (gate) { if (queue.Count == 0 && journalDirty == 0) drained.Set(); else continue; }
                if (stopping) return;
                if ((Stopwatch.GetTimestamp() - summaryAt) / (double)Stopwatch.Frequency > 30) { FlushSummaries(); summaryAt = Stopwatch.GetTimestamp(); }
                signal.WaitOne(200);
            }
        }
        void Append(string line)
        {
            lock (fileGate)
            {
                Directory.CreateDirectory(DirectoryPath);
                int bytes = Encoding.UTF8.GetByteCount(line) + 1;
                if (file == null || (File.Exists(file) && new FileInfo(file).Length + bytes > options.maxFileBytes))
                {
                    Prune(DirectoryPath);
                    file = Path.Combine(DirectoryPath, "moyu-" + DateTimeOffset.Now.ToString("yyyyMMdd") + "-" + SessionId + "-" + (++fileIndex).ToString("D4") + ".jsonl");
                }
                File.AppendAllText(file, line + "\n", new UTF8Encoding(false));
            }
            AcknowledgeTerminal(line);
        }
        void ReportSinkFailure(Exception error)
        {
            long now = Stopwatch.GetTimestamp();
            if (lastSinkFailureTicks != 0 && (now - lastSinkFailureTicks) / (double)Stopwatch.Frequency < 30) { Summary("diagnostics", "sink.write_retry", new Dictionary<string, object> { { "code", error.HResult } }); return; }
            lastSinkFailureTicks = now;
            string line = Json().Serialize(new { schema = 1, timestamp = DateTimeOffset.Now.ToString("o"), level = "WARN", module = "diagnostics", @event = "sink.write_failed", session_id = SessionId, process_id = Pid, sequence = Interlocked.Increment(ref sequence), message = "Primary log write failed; attempting emergency sink", exception = ErrorInfo(error, 0) });
            Emergency(line);
        }
        readonly Queue<string> emergencyMemory = new Queue<string>();
        void Emergency(string line)
        {
            DegradedMessage = "日志写入降级；请在设置中导出诊断信息。";
            bool persisted = false;
            lock (emergencyGate)
            {
                try
                {
                    Directory.CreateDirectory(fallback); Prune(fallback);
                    string path = Path.Combine(fallback, "moyu-emergency-" + SessionId + ".jsonl");
                    if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(line) + 1 > options.maxFileBytes) File.Move(path, path + "." + Guid.NewGuid().ToString("N") + ".jsonl");
                    File.AppendAllText(path, line + "\n", new UTF8Encoding(false)); persisted = true;
                }
                catch
                {
                    // Bounded last resort, visibly degraded. Export drains this ring as well.
                    while (emergencyMemory.Count > 0 && (emergencyMemory.Count >= 256 || memoryBytes + line.Length * 2L > 4 * 1024 * 1024)) { memoryBytes -= emergencyMemory.Dequeue().Length * 2L; Interlocked.Increment(ref memoryLost); }
                    emergencyMemory.Enqueue(line); memoryBytes += line.Length * 2L;
                }
            }
            if (persisted) AcknowledgeTerminal(line);
        }
        long memoryLost;
        void Prune(string directory)
        {
            var candidates = new List<FileInfo>();
            foreach (string folder in new[] { primary, fallback }.Distinct()) try { if (System.IO.Directory.Exists(folder)) candidates.AddRange(new DirectoryInfo(folder).GetFiles("moyu-*.jsonl")); } catch { DegradedMessage = "部分日志目录无法统计容量。"; }
            var files = candidates.OrderBy(x => x.LastWriteTimeUtc).ToList();
            long total = files.Sum(x => x.Length);
            foreach (var item in files)
            {
                if (item.FullName == file || (item.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-options.retentionDays) && total <= options.maxTotalBytes - options.maxFileBytes)) continue;
                try { long size = item.Length; item.Delete(); total -= size; } catch { DegradedMessage = "部分旧日志未能清理，诊断目录可能超出容量预算。"; }
            }
        }
        internal void Track(OperationLog operation, bool start)
        {
            lock (journalGate)
            {
                if (start) active[operation.Id] = new Dictionary<string, object> { { "operation_id", operation.Id }, { "correlation_id", operation.CorrelationId }, { "module", Code(operation.Module) }, { "event", Code(operation.Action) }, { "object_id", Code(operation.ObjectId) } };
                else active.Remove(operation.Id);
            }
            MarkJournalDirty();
        }
        void MarkJournalDirty() { lock (gate) { Interlocked.Exchange(ref journalDirty, 1); drained.Reset(); } signal.Set(); }
        string Marker(string directory) { return Path.Combine(directory, "session-" + SessionId + ".state.json"); }
        void WriteJournal(bool clean)
        {
            try
            {
                string content;
                lock (journalGate) {
                    var record = new { schema = 1, session_id = SessionId, root_hash = rootHash, pid = Pid, process_start = processStart.ToString("o"), clean = clean, active = active.Values.Take(256).ToArray(), active_count = active.Count };
                    content = Json().Serialize(record);
                }
                lock (checkpointGate) {
                    string target = Marker(DirectoryPath), temp = target + ".tmp";
                    Directory.CreateDirectory(DirectoryPath); File.WriteAllText(temp, content, new UTF8Encoding(false));
                    if (File.Exists(target)) File.Replace(temp, target, null); else File.Move(temp, target);
                }
            }
            catch (Exception error) { DegradedMessage = "会话检查点无法写入；下次启动的中断检测可能不完整。"; if (Interlocked.Exchange(ref checkpointFailureReported, 1) == 0) Write("WARN", "diagnostics", "checkpoint.write_failed", "Operation checkpoint unavailable; restart diagnosis may be incomplete", null, error, true); }
        }
        void RecoverIncomplete()
        {
            var seen = new HashSet<string>();
            foreach (string directory in new[] { primary, fallback }.Distinct())
            {
                try
                {
                    if (!Directory.Exists(directory)) continue;
                    foreach (string path in Directory.GetFiles(directory, "session-*.state.json"))
                    {
                        try
                        {
                            var record = Json().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
                            string previous = Convert.ToString(record["session_id"]); if (!seen.Add(previous) || Convert.ToBoolean(record["clean"])) continue;
                            bool alive = false;
                            try { var process = Process.GetProcessById(Convert.ToInt32(record["pid"])); alive = Math.Abs((process.StartTime.ToUniversalTime() - DateTimeOffset.Parse(Convert.ToString(record["process_start"])).UtcDateTime).TotalSeconds) < 1; process.Dispose(); }
                            catch (ArgumentException) { }
                            catch { continue; }
                            if (alive) continue;
                            Write("WARN", "lifecycle", "session.previous_incomplete", "Previous host session did not complete its clean checkpoint; cause is not established", new Dictionary<string, object> { { "previous_session_id", previous }, { "pending", record["active_count"] } }, null, true, null);
                            foreach (Dictionary<string, object> item in (IEnumerable)record["active"])
                                Write("WARN", "lifecycle", "operation.previous_interrupted", "Previous operation has no confirmed terminal result", new Dictionary<string, object> { { "previous_session_id", previous }, { "previous_operation_id", item["operation_id"] }, { "previous_correlation_id", item["correlation_id"] }, { "stage", item["event"] }, { "result", "interrupted" } }, null, true, null);
                            recoveredMarkers.Add(path);
                        }
                        catch { Write("WARN", "diagnostics", "checkpoint.unreadable", "A previous checkpoint could not be read", null, null, true, null); }
                    }
                }
                catch { DegradedMessage = "部分历史会话检查点不可访问。"; }
            }
        }
        void ResourceSample()
        {
            try
            {
                using (var process = Process.GetCurrentProcess())
                {
                    long now = Stopwatch.GetTimestamp(); bool high = process.PrivateMemorySize64 > 1024L * 1024 * 1024;
                    if (high && (lastResourceTicks == 0 || (now - lastResourceTicks) / (double)Stopwatch.Frequency > 600)) { lastResourceTicks = now; Write("WARN", "resources", "memory.high", "Host memory crossed diagnostic threshold; not proof of a leak", new Dictionary<string, object> { { "private_bytes", process.PrivateMemorySize64 }, { "managed_bytes", GC.GetTotalMemory(false) }, { "peak_bytes", process.PeakWorkingSet64 } }, null, true, null); }
                }
            }
            catch { }
        }
        public bool Flush(int milliseconds = 3000) { FlushSummaries(); signal.Set(); return drained.WaitOne(milliseconds); }
        public string[] SnapshotLines()
        {
            bool flushed = Flush(); var result = new List<string>(); long bytes = 0; ExportSkipped = flushed ? 0 : 1;
            foreach (string directory in new[] { primary, fallback }.Distinct())
            {
                try {
                    if (!Directory.Exists(directory)) continue;
                    string[] candidates = Directory.GetFiles(directory, "moyu-*.jsonl").OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
                    ExportSkipped += Math.Max(0, candidates.Length - 32);
                    foreach (string path in candidates.Take(32))
                    {
                        try {
                            if (new FileInfo(path).Length > 16 * 1024 * 1024L || bytes >= 32 * 1024 * 1024) { ExportSkipped++; continue; }
                            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) using (var reader = new StreamReader(stream))
                            { string line; while ((line = reader.ReadLine()) != null) { bytes += Encoding.UTF8.GetByteCount(line); if (bytes > 32 * 1024 * 1024) { ExportSkipped++; break; } result.Add(line); } }
                        } catch { ExportSkipped++; }
                    }
                } catch { ExportSkipped++; }
            }
            lock (emergencyGate) result.AddRange(emergencyMemory); return result.ToArray();
        }
        void AcknowledgeTerminal(string line)
        {
            // Only remove an in-flight checkpoint after its terminal record reached a disk sink.
            var record = Json().Deserialize<Dictionary<string, object>>(line);
            if (!Convert.ToString(record["event"]).EndsWith(".finished") || !record.ContainsKey("operation_id")) return;
            bool removed;
            lock (journalGate) removed = active.Remove(Convert.ToString(record["operation_id"]));
            if (removed) MarkJournalDirty();
        }
        void PruneMarkers()
        {
            foreach (string directory in new[] { primary, fallback }.Distinct()) try {
                if (!Directory.Exists(directory)) continue;
                foreach (var item in new DirectoryInfo(directory).GetFiles("session-*.state.json").OrderByDescending(x => x.LastWriteTimeUtc).Skip(128)) {
                    var record = Json().Deserialize<Dictionary<string, object>>(File.ReadAllText(item.FullName));
                    if (Convert.ToBoolean(record["clean"])) item.Delete();
                }
                foreach (var item in new DirectoryInfo(directory).GetFiles("session-*.state.json")) if (item.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-options.retentionDays)) {
                    var record = Json().Deserialize<Dictionary<string, object>>(File.ReadAllText(item.FullName));
                    if (Convert.ToBoolean(record["clean"])) item.Delete();
                }
            } catch { DegradedMessage = "旧会话检查点未能全部清理。"; }
        }
        public static Dictionary<string, object> ExportRecord(string line)
        {
            var source = Json().Deserialize<Dictionary<string, object>>(line);
            if (!source.ContainsKey("schema") || Convert.ToInt32(source["schema"]) != 1) throw new InvalidDataException("Unknown diagnostic schema");
            var result = new Dictionary<string, object>();
            foreach (string key in new[] { "schema", "timestamp", "level", "module", "event", "session_id", "process_id", "sequence", "operation_id", "correlation_id", "parent_operation_id", "object_id" })
                if (source.ContainsKey(key)) result[key] = key == "timestamp" ? TimeField(source[key]) : source[key] is string ? Code((string)source[key]) : source[key];
            result["message"] = "Host diagnostic event: " + Convert.ToString(result["event"]);
            foreach (var pair in SafeFields(source)) result[pair.Key] = pair.Value;
            if (source.ContainsKey("exception")) result["exception"] = SafeExportError(source["exception"] as Dictionary<string, object>, 0);
            return result;
        }
        static object SafeExportError(Dictionary<string, object> source, int depth)
        {
            if (source == null || depth > 12) return null;
            var result = new Dictionary<string, object>();
            foreach (string key in new[] { "type", "hresult", "native_error_code", "network_status", "truncated", "error_id" }) if (source.ContainsKey(key)) result[key] = key == "timestamp" ? TimeField(source[key]) : source[key] is string ? Code((string)source[key]) : source[key];
            result["stack"] = SafeStack(Convert.ToString(source.ContainsKey("stack") ? source["stack"] : ""));
            if (source.ContainsKey("inner")) result["inner"] = SafeExportError(source["inner"] as Dictionary<string, object>, depth + 1);
            if (source.ContainsKey("causes")) result["causes"] = ((IEnumerable)source["causes"]).Cast<object>().Take(12).Select(x => SafeExportError(x as Dictionary<string, object>, depth + 1)).ToArray();
            return result;
        }
        public void Dispose()
        {
            if (disposed != 0) return;
            resources.Dispose();
            Write("INFO", "lifecycle", "session.stopping", "Host shutdown reached diagnostics flush", new Dictionary<string, object> { { "pending", active.Count }, { "dropped", memoryLost }, { "reason", NormalExit ? "normal" : "abnormal" } }, null, true, null);
            bool flushed = Flush(); WriteJournal(flushed && active.Count == 0);
            stopping = true; signal.Set(); if (!writer.Join(3000)) DegradedMessage = "日志刷新超时，部分尾部日志可能未落盘。";
            Interlocked.Exchange(ref disposed, 1);
        }
    }
}
