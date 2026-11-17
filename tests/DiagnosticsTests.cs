using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MoyuBox;

class DiagnosticsTests
{
    static readonly List<string> checks = new List<string>();
    static string root, defaults;
    static void Check(bool value, string label) { if (!value) throw new Exception(label); checks.Add(label); }
    static Dictionary<string, object>[] Records(DiagnosticLog log) { return log.SnapshotLines().Select(x => RegistryStore.Json.Deserialize<Dictionary<string, object>>(x)).ToArray(); }
    static string Ev(Dictionary<string, object> r) { return Convert.ToString(r["event"]); }
    static void Expected(Action action) { try { action(); throw new Exception("Expected fault was not raised"); } catch (IOException) { } catch (ArgumentException) { } }
    static DiagnosticLog Open(string name, LogOptions options = null) { var log = new DiagnosticLog(Path.Combine(root, name), options); Telemetry.Current = log; return log; }
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "crash")
        {
            var crash = new DiagnosticLog(args[1]); Telemetry.Current = crash;
            Telemetry.Begin("test", "forced_operation"); crash.Flush(); File.WriteAllText(Path.Combine(args[1], "ready"), crash.SessionId);
            Thread.Sleep(Timeout.Infinite); return;
        }
        try {
            root = Path.GetFullPath(args[0]); defaults = Path.GetFullPath(args[1]); Directory.CreateDirectory(root);
            NormalAndFaults(); Fallback(); Rotation(); CrashRecovery(); Benchmark(); ExportWindow();
            File.WriteAllText(Path.Combine(root, "result.json"), RegistryStore.Json.Serialize(new { ok = true, checks = checks }), new UTF8Encoding(false));
            Console.WriteLine("Diagnostics checks passed: " + checks.Count);
        } catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
    }
    static void NormalAndFaults()
    {
        using (var log = Open("normal"))
        {
            var store = new RegistryStore(Path.Combine(root, "normal"), defaults);
            string correlation, parent;
            using (var operation = Telemetry.Begin("user", "save_fixture"))
            {
                correlation = operation.CorrelationId; parent = operation.Id;
                Task.Run(delegate { store.Save(); }).Wait(); operation.Success();
            }
            using (var operation = Telemetry.Begin("task", "cancel_fixture")) operation.Cancel();
            using (var operation = Telemetry.Begin("task", "timeout_fixture")) operation.Fail(new TimeoutException());
            using (var operation = Telemetry.Begin("task", "retry_fixture"))
            {
                try { File.ReadAllText(Path.Combine(root, "retry-missing.txt")); }
                catch (IOException error) { Telemetry.Write("WARN", "task", "attempt.failed", "First attempt failed; retry scheduled", new Dictionary<string, object> { { "attempt", 1 } }, error, true); }
                Task.Run(delegate { using (var retry = Telemetry.Begin("task", "attempt")) { Thread.Sleep(30); File.WriteAllText(Path.Combine(root, "retry-missing.txt"), "fixture"); retry.Success(); } }).Wait(); operation.Success();
            }
            string settings = Path.Combine(root, "normal", "settings.json");
            using (var locked = new FileStream(settings + ".tmp", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None)) Expected(store.Save);
            store.Save();
            File.WriteAllText(settings, "broken-canary-private-book-title");
            var recovered = new RegistryStore(Path.Combine(root, "normal"), defaults);
            Check(recovered.RecoveryNotice != null, "Corrupt settings recovered from backup; no false final failure");
            string failedRoot = Path.Combine(root, "failed-registry"); Directory.CreateDirectory(failedRoot); File.WriteAllText(Path.Combine(failedRoot, "settings.json"), "broken");
            Expected(delegate { new RegistryStore(failedRoot, defaults); });
            Expected(delegate { new RegistryStore(Path.Combine(root, "missing-defaults"), Path.Combine(root, "no-defaults")); });
            Telemetry.Write("DEBUG", "test", "debug.hidden", "Hidden diagnostic detail"); log.SetLevel("DEBUG"); Telemetry.Write("DEBUG", "test", "debug.visible", "Visible diagnostic detail");
            for (int i = 0; i < 100; i++) Telemetry.Summary("test", "poll");
            Exception fault;
            try { throw new IOException("SECRET_CANARY C:\\private\\novel.txt https://host/?token=TOKEN_CANARY", new InvalidDataException("PRIVATE_BODY_CANARY")); } catch (Exception error) { fault = error; }
            using (var operation = Telemetry.Begin("test", "sensitive_fixture", "C:/private/book.txt?token=TOKEN_CANARY"))
            {
                Telemetry.Write("WARN", "test", "first.failure", "Caught test exception", new Dictionary<string, object> { { "url", "https://host/?token=TOKEN_CANARY" }, { "reason", "C:/private/novel.txt" } }, fault, true);
                operation.Fail(fault);
            }
            string requestA, requestB;
            using (var request = Telemetry.Request("requestA", "fixture")) { requestA = request.CorrelationId; request.Success(); }
            using (var request = Telemetry.Request("requestB", "fixture")) { requestB = request.CorrelationId; request.Success(); }
            Check(requestA != requestB, "Independent user requests have distinct root correlations");
            using (var background = Telemetry.Begin("task", "background_failure"))
            try { Task.Run(delegate { throw new InvalidOperationException("Private background failure message"); }).GetAwaiter().GetResult(); }
            catch (InvalidOperationException error) { background.Fail(error); }
            using (var cancellation = Telemetry.Begin("task", "actual_task_cancel"))
            using (var token = new CancellationTokenSource()) {
                token.Cancel();
                try { Task.Run(delegate { }, token.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException error) { cancellation.Fail(error); }
            }
            var records = Records(log);
            Check(records.Any(x => Ev(x) == "background_failure.finished" && Convert.ToString(x["result"]) == "failed" && x.ContainsKey("exception")), "Actual Task.Run exception is linked and retains stack");
            Check(records.Any(x => Ev(x) == "actual_task_cancel.finished" && Convert.ToString(x["result"]) == "cancelled"), "Actual cancelled Task has cancellation terminal");
            foreach (var record in records) Check(record.ContainsKey("timestamp") && DateTimeOffset.Parse((string)record["timestamp"]).Offset != TimeSpan.FromDays(9) && new[] { "level", "module", "event", "session_id", "message" }.All(record.ContainsKey), "Required structured fields: " + Ev(record));
            Check(records.Any(x => Ev(x) == "settings.save.finished" && Convert.ToString(x["result"]) == "success" && Convert.ToBoolean(x["verified"]) && Convert.ToString(x["correlation_id"]) == correlation && Convert.ToString(x["parent_operation_id"]) == parent), "Cross-thread configuration save shares root correlation and parent");
            Check(records.Count(x => Ev(x) == "settings.save.finished" && Convert.ToString(x["result"]) == "failed") == 1, "Real file lock produces one failed save terminal");
            Check(records.Any(x => Ev(x) == "settings.recovered"), "Backup recovery has separate warning and successful load terminal");
            Check(records.Any(x => Ev(x) == "settings.load.finished" && Convert.ToString(x["result"]) == "failed" && x.ContainsKey("exception")), "Startup configuration failure has error type and stack");
            Check(records.Any(x => Ev(x) == "cancel_fixture.finished" && Convert.ToString(x["result"]) == "cancelled"), "Cancellation is not a success");
            Check(records.Any(x => Ev(x) == "timeout_fixture.finished" && Convert.ToString(x["result"]) == "timeout"), "Timeout is distinct from generic failure");
            Check(records.Any(x => Ev(x) == "retry_fixture.finished" && Convert.ToString(x["result"]) == "success" && Convert.ToDouble(x["elapsed_ms"]) >= 25), "Retry success has monotonic elapsed time");
            Check(!records.Any(x => Ev(x) == "debug.hidden") && records.Any(x => Ev(x) == "debug.visible"), "DEBUG is configurable");
            Check(records.Any(x => Ev(x) == "poll.summary" && Convert.ToInt32(x["repeats"]) == 100), "Poll noise summarized with count and time range");
            string raw = String.Join("\n", log.SnapshotLines());
            Check(!new[] { "SECRET_CANARY", "TOKEN_CANARY", "PRIVATE_BODY_CANARY", "novel.txt", "private/book" }.Any(raw.Contains), "On-disk records exclude content, paths and URL query secrets");
            var errorRecords = records.Where(x => Ev(x) == "first.failure" || Ev(x) == "sensitive_fixture.finished").ToArray();
            Check(errorRecords.Length == 2 && errorRecords.Count(x => x.ContainsKey("exception")) == 1 && errorRecords.Select(x => Convert.ToString(x["error_id"])).Distinct().Count() == 1, "Exception propagation reuses error identifier and one detailed stack");
            var errorBody = (Dictionary<string, object>)errorRecords.First(x => x.ContainsKey("exception"))["exception"];
            Check(errorBody.ContainsKey("inner") && Convert.ToString(errorBody["stack"]).Contains("NormalAndFaults"), "Inner exception and complete managed frame retained");
            string zip = Path.Combine(root, "diagnostics.zip"); int count = DiagnosticExport.Create(log, zip);
            using (var archive = ZipFile.OpenRead(zip)) {
                Check(count > 0 && archive.Entries.Count == 2, "Verified export includes only host.jsonl and manifest.json");
                using (var reader = new StreamReader(archive.GetEntry("host.jsonl").Open())) { string data = reader.ReadToEnd(); Check(!data.Contains("TOKEN_CANARY") && !data.Contains("displayName") && data.Contains("settings.save.finished"), "Export is redacted and excludes preferences/user files"); }
            }
            Check(Records(log).Any(x => Ev(x) == "export.finished" && Convert.ToBoolean(x["verified"])), "Export verification terminal is recorded after archive validation");
            foreach (var group in Records(log).Where(x => x.ContainsKey("operation_id")).GroupBy(x => Convert.ToString(x["operation_id"])))
                Check(group.Count(x => Ev(x).EndsWith(".started")) == 1 && group.Count(x => Ev(x).EndsWith(".finished")) == 1, "Exactly one start and terminal: " + Ev(group.First()));
            File.WriteAllLines(Path.Combine(root, "representative.jsonl"), log.SnapshotLines().Where(x => x.Contains("settings.primary_invalid") || x.Contains("settings.recovered") || x.Contains("settings.save.finished") || x.Contains("sensitive_fixture.finished")).Take(16));
            log.NormalExit = true;
        }
        string marker = Directory.GetFiles(Path.Combine(root, "normal", "logs"), "session-*.state.json").Single();
        Check(Convert.ToBoolean(RegistryStore.Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(marker))["clean"]), "Normal shutdown drains terminal records before clean marker");
    }
    static void Fallback()
    {
        string dir = Path.Combine(root, "fallback"); Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, "logs"), "not a directory");
        using (var log = Open("fallback", new LogOptions { fallbackDirectory = Path.Combine(root, "fallback-sink") }))
        {
            using (var operation = Telemetry.Begin("test", "fallback_operation")) operation.Success();
            Check(log.DegradedMessage != null && Records(log).Any(x => Ev(x) == "fallback_operation.finished"), "Unwritable primary sink falls back without blocking host operation"); log.NormalExit = true;
        }
        string blocked = Path.Combine(root, "blocked-sink"); File.WriteAllText(blocked, "blocked");
        string memory = Path.Combine(root, "memory"); Directory.CreateDirectory(memory); File.WriteAllText(Path.Combine(memory, "logs"), "blocked");
        using (var log = Open("memory", new LogOptions { fallbackDirectory = blocked, queueCapacity = 1 }))
        {
            using (var operation = Telemetry.Begin("test", "memory_operation")) operation.Success();
            Check(Records(log).Any(x => Ev(x) == "memory_operation.finished"), "Both disk sinks unavailable: critical terminal remains exportable in memory");
            DiagnosticExport.Create(log, Path.Combine(root, "memory-diagnostics.zip"));
            for (int i = 0; i < 500; i++) Telemetry.Write("INFO", "test", "important", "Bounded memory test", null, null, true);
            log.Flush(); Check(log.MemoryLost > 0 && log.DegradedMessage != null, "Exhausted emergency ring reports loss explicitly");
        }
        using (var log = Open("queue", new LogOptions { queueCapacity = 1, maxTotalBytes = 1024 * 1024 }))
        {
            for (int i = 0; i < 10000; i++) Telemetry.Write("DEBUG", "test", "noncritical", "Load test");
            log.SetLevel("DEBUG");
            for (int i = 0; i < 10000; i++) Telemetry.Write("DEBUG", "test", "noncritical", "Load test");
            using (var operation = Telemetry.Begin("test", "important_terminal")) operation.Fail(new IOException());
            Check(Records(log).Any(x => Ev(x) == "important_terminal.finished") && Records(log).Any(x => Ev(x) == "queue.saturated"), "Saturated queue retains critical terminal and counts lower priority drops"); log.NormalExit = true;
        }
    }
    static void Rotation()
    {
        string dir = Path.Combine(root, "rotation", "logs"); Directory.CreateDirectory(dir);
        string old = Path.Combine(dir, "moyu-old.jsonl"); File.WriteAllText(old, "expired"); File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-20));
        using (var log = Open("rotation", new LogOptions { maxFileBytes = 2048, maxTotalBytes = 16384, queueCapacity = 2048 }))
        {
            for (int i = 0; i < 120; i++) { Telemetry.Write("INFO", "test", "rotation", "Rotation fixture", new Dictionary<string, object> { { "count", i } }); if (i % 5 == 0) log.Flush(); }
            log.Flush(); var files = new DirectoryInfo(dir).GetFiles("moyu-*.jsonl");
            Check(!File.Exists(old) && files.Length > 1 && files.Sum(x => x.Length) <= 16384, "Rotation enforces age and bounded total file bytes"); log.NormalExit = true;
        }
    }
    static void ExportWindow()
    {
        using (var log = Open("export-window", new LogOptions { maxFileBytes = 1024, maxTotalBytes = 128 * 1024 })) {
            for (int i = 0; i < 180; i++) { Telemetry.Write("INFO", "test", "bounded_export", "Export window fixture"); if (i % 8 == 0) log.Flush(); }
            DiagnosticExport.Create(log, Path.Combine(root, "bounded-export.zip"));
            using (var archive = ZipFile.OpenRead(Path.Combine(root, "bounded-export.zip"))) using (var reader = new StreamReader(archive.GetEntry("manifest.json").Open())) {
                var manifest = RegistryStore.Json.Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                Check(!Convert.ToBoolean(manifest["complete"]) && Convert.ToInt32(manifest["skipped"]) > 0, "Export window truncation is explicit in manifest");
            }
            log.NormalExit = true;
        }
    }
    static void Benchmark()
    {
        using (var log = Open("performance")) {
            var timings = new List<double>();
            for (int i = 0; i < 100; i++) { var watch = Stopwatch.StartNew(); using (var operation = Telemetry.Begin("test", "timed_operation")) operation.Success(); timings.Add(watch.Elapsed.TotalMilliseconds); }
            log.Flush(); timings.Sort();
            File.WriteAllText(Path.Combine(root, "latency.json"), RegistryStore.Json.Serialize(new { operations = 100, median_ms = timings[50], p95_ms = timings[95], max_ms = timings[99], note = "Caller wall time for host checkpoint + enqueue; local disk, not a promise for slow disks" }));
            log.NormalExit = true;
        }
    }
    static void CrashRecovery()
    {
        string dir = Path.Combine(root, "crash"); Directory.CreateDirectory(dir);
        var child = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "crash \"" + dir + "\"") { UseShellExecute = false, CreateNoWindow = true });
        var watch = Stopwatch.StartNew(); while (!File.Exists(Path.Combine(dir, "ready")) && watch.ElapsedMilliseconds < 10000) Thread.Sleep(20);
        Check(File.Exists(Path.Combine(dir, "ready")), "Real crash helper reached durable active-operation checkpoint");
        string previous = File.ReadAllText(Path.Combine(dir, "ready")); child.Kill(); child.WaitForExit(); child.Dispose();
        using (var log = Open("crash"))
        {
            var records = Records(log);
            Check(records.Any(x => Ev(x) == "session.previous_incomplete" && Convert.ToString(x["previous_session_id"]) == previous), "Restart detects actual force-terminated process session");
            Check(records.Any(x => Ev(x) == "operation.previous_interrupted" && Convert.ToString(x["result"]) == "interrupted" && x.ContainsKey("previous_operation_id")), "Crash recovery identifies unfinished operation without guessing crash cause"); log.NormalExit = true;
        }
    }
}
