using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MoyuBox
{
    public static class DiagnosticExport
    {
        public static int Create(DiagnosticLog log, string destination)
        {
            using (var operation = Telemetry.Begin("diagnostics", "export"))
            try
            {
                var json = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
                string[] lines = log.SnapshotLines(); int count = 0, skipped = log.ExportSkipped;
                string temp = destination + ".partial-" + Guid.NewGuid().ToString("N");
                try
                {
                    using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
                    {
                        using (var writer = new StreamWriter(archive.CreateEntry("host.jsonl").Open(), new UTF8Encoding(false)))
                        foreach (string line in lines)
                        {
                            Dictionary<string, object> record;
                            try { record = DiagnosticLog.ExportRecord(line); } catch { skipped++; continue; }
                            writer.WriteLine(json.Serialize(record)); count++;
                        }
                        using (var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open(), new UTF8Encoding(false)))
                            writer.Write(json.Serialize(new { schema = 1, scope = "MoyuBox-host-only", session_id = log.SessionId, records = count, skipped = skipped, memory_records_lost = log.MemoryLost, complete = skipped == 0 && log.MemoryLost == 0, boundary = "Recent bounded logs only; export completion is recorded after this snapshot. No project logs, settings, user files or browser profiles are included." }));
                    }
                    using (var archive = ZipFile.OpenRead(temp))
                    {
                        if (archive.GetEntry("host.jsonl") == null || archive.GetEntry("manifest.json") == null) throw new InvalidDataException("诊断包校验失败。");
                        foreach (var entry in archive.Entries) using (var input = entry.Open()) input.CopyTo(Stream.Null);
                    }
                    if (File.Exists(destination)) File.Replace(temp, destination, null); else File.Move(temp, destination);
                }
                finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception cleanupError) { Telemetry.Caught("diagnostics", "export.cleanup_failed", cleanupError); } }
                if (skipped > 0 || log.MemoryLost > 0) Telemetry.Write("WARN", "diagnostics", "export.incomplete", "Diagnostic archive contains a bounded or partial snapshot; inspect manifest", new Dictionary<string, object> { { "skipped_count", skipped }, { "dropped", log.MemoryLost } }, null, true);
                operation.Success(true, new Dictionary<string, object> { { "count", count }, { "skipped_count", skipped } }); return count;
            }
            catch (Exception error) { operation.Fail(error); throw; }
        }
    }
    internal sealed partial class MainWindow
    {
        async Task<bool> ExportDiagnostics()
        {
            using (var dialog = new SaveFileDialog { Filter = "诊断包|*.zip", FileName = "摸鱼盒子-本体诊断-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip", OverwritePrompt = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return false;
                string destination = dialog.FileName;
                await Task.Run(delegate { DiagnosticExport.Create(Telemetry.Current, destination); });
                return true;
            }
        }
    }
}
