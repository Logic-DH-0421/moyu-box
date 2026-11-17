using System;
using System.IO;
using System.Text;
using System.Threading;
using AiNovelReader;
internal static class MoyuReaderService
{
    static string Value(string[] args, string name) { return args[Array.IndexOf(args, name) + 1]; }
    [STAThread] static void Main(string[] args)
    {
        string root = Value(args, "--data"), ready = Value(args, "--ready");
        LocalHttpServer server = null;
        try {
            RuntimeLog.Initialize(root, Path.Combine(root, "logs"));
            var books = new DataStore(root); server = new LocalHttpServer(books); server.UseEphemeralPort = true;
            if (Value(args, "--ui") == "word") server.WordUiDirectory = Value(args, "--web");
            using (var stop = new ManualResetEvent(false)) {
                server.ShutdownRequested = delegate { stop.Set(); }; server.Start();
                File.WriteAllText(ready, "{\"url\":\"" + server.BaseUrl + "\"}", new UTF8Encoding(false));
                stop.WaitOne(); server.Stop(); server = null;
            }
            RuntimeLog.Shutdown("success");
        } catch (Exception error) { RuntimeLog.Error("adapter", "service.failed", error, null); RuntimeLog.Shutdown("failure"); Environment.ExitCode = 1; }
        finally { if (server != null) server.Stop(); if (File.Exists(ready)) File.Delete(ready); }
    }
}
