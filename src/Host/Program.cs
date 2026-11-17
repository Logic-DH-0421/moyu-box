using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("摸鱼盒子")]
[assembly: AssemblyProduct("摸鱼盒子")]
[assembly: AssemblyDescription("可自定义项目的 Windows 桌面工作台")]
[assembly: AssemblyVersion("0.8.1.0")]
[assembly: AssemblyFileVersion("0.8.1.0")]

namespace MoyuBox
{
    internal static class Bootstrap
    {
        [STAThread]
        public static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoyuBox");
            bool integration = Array.IndexOf(args, "--integration-test") >= 0;
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--data-dir") root = Path.GetFullPath(args[i + 1]);
            if (integration && Array.IndexOf(args, "--data-dir") < 0) root = Path.Combine(Path.GetTempPath(), "MoyuBox-Integration-" + Guid.NewGuid().ToString("N"));
            try
            {
                string hash;
                using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToLowerInvariant()))).Replace("-", "").Substring(0, 24);
                bool created;
                using (var mutex = new Mutex(true, "Local\\MoyuBox-" + hash, out created))
                {
                    if (!created) { MessageBox.Show("摸鱼盒子已在运行，请从任务栏或托盘打开。", "摸鱼盒子"); return; }
                    Telemetry.Current = new DiagnosticLog(root);
                    Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e) { Telemetry.Caught("runtime", "ui.unhandled", e.Exception, false); };
                    AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e) { Telemetry.Caught("runtime", "process.unhandled", e.ExceptionObject as Exception ?? new Exception("Non-exception termination"), false); Telemetry.Current.Flush(1000); };
                    System.Threading.Tasks.TaskScheduler.UnobservedTaskException += delegate(object sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e) { Telemetry.Caught("runtime", "task.unobserved", e.Exception, false); };
                    using (var operation = Telemetry.Begin("lifecycle", "host.run"))
                    try {
                    var store = new RegistryStore(root, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "projects"));
                    Application.Run(new MainWindow(store, integration));
                    operation.Success(); Telemetry.Current.NormalExit = true;
                    } catch (Exception error) { operation.Fail(error); throw; }
                    mutex.ReleaseMutex();
                }
            }
            catch (Exception error)
            {
                Telemetry.Caught("lifecycle", "startup.failed", error, false); Environment.ExitCode = 1;
                if (integration) { try { if (Directory.Exists(root)) File.WriteAllText(Path.Combine(root, "integration-result.json"), RegistryStore.Json.Serialize(new { ok = false, error = error.ToString() }), Encoding.UTF8); } catch (Exception reportError) { Telemetry.Caught("integration", "result.write_failed", reportError, false); } }
                else MessageBox.Show("启动未完成：" + error.Message + "\n\n诊断日志：" + (Telemetry.Current == null ? "未能初始化" : Telemetry.Current.DirectoryPath), "摸鱼盒子", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { if (Telemetry.Current != null) Telemetry.Current.Dispose(); }
        }
        public static void Log(string root, Exception error)
        {
            Telemetry.Caught("host", "boundary.exception", error);
        }
    }
}
