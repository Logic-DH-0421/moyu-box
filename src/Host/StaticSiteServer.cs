using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace MoyuBox
{
    // Read-only loopback transport for opening imported static sites in a normal browser.
    internal sealed class StaticSiteServer : IDisposable
    {
        readonly string root;
        readonly string prefix = "/";
        TcpListener listener;
        volatile bool running;
        int reportedFailure;
        public string BaseUrl { get; private set; }
        public StaticSiteServer(string folder)
        {
            root = Path.GetFullPath(folder);
            listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            BaseUrl = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + prefix;
            running = true;
            Telemetry.Write("INFO", "static_host", "listener.started", "Host loopback static transport started", null, null, true);
            new Thread(Accept) { IsBackground = true, Name = "Moyu static site" }.Start();
        }
        void Accept()
        {
            while (running)
            {
                try { TcpClient client = listener.AcceptTcpClient(); ThreadPool.QueueUserWorkItem(delegate { Handle(client); }); }
                catch (SocketException error) { if (!running) return; Telemetry.Caught("static_host", "accept.failed", error, false); running = false; return; }
                catch (ObjectDisposedException) { return; }
            }
        }
        void Handle(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 5000; client.SendTimeout = 5000;
                    var stream = client.GetStream(); var head = new StringBuilder(); int last = 0;
                    while (head.Length < 8192)
                    {
                        int next = stream.ReadByte(); if (next < 0) return;
                        head.Append((char)next);
                        last = ((last << 8) | next); if (last == 0x0d0a0d0a) break;
                    }
                    string[] lines = head.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None);
                    string[] first = lines[0].Split(' '); string host = "", origin = "";
                    foreach (string line in lines) { if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) host = line.Substring(5).Trim(); if (line.StartsWith("Origin:", StringComparison.OrdinalIgnoreCase)) origin = line.Substring(7).Trim(); }
                    if (first.Length != 3 || host != new Uri(BaseUrl).Authority || first[0] != "GET" || (origin.Length > 0 && origin != BaseUrl.TrimEnd('/'))) { Reply(stream, 403, "text/plain", Encoding.UTF8.GetBytes("Forbidden")); return; }
                    string target = first[1].Split('?')[0];
                    if (!target.StartsWith(prefix, StringComparison.Ordinal)) { Reply(stream, 404, "text/plain", new byte[0]); return; }
                    string relative = Uri.UnescapeDataString(target.Substring(prefix.Length)).Replace('/', Path.DirectorySeparatorChar);
                    if (relative.Length == 0) relative = "index.html";
                    string path = Rules.ContainedPath(root, relative);
                    string walk = path;
                    while (walk.Length >= root.Length) { if ((File.Exists(walk) || Directory.Exists(walk)) && (File.GetAttributes(walk) & FileAttributes.ReparsePoint) != 0) throw new IOException(); walk = Path.GetDirectoryName(walk); if (walk == null) break; }
                    if (!File.Exists(path)) { Reply(stream, 404, "text/plain", new byte[0]); return; }
                    Reply(stream, 200, Mime(Path.GetExtension(path)), File.ReadAllBytes(path));
                }
                catch (Exception error) { if (Interlocked.Exchange(ref reportedFailure, 1) == 0) Telemetry.Caught("static_host", "request.first_failure", error); Telemetry.Summary("static_host", "request.failed", new System.Collections.Generic.Dictionary<string, object> { { "code", error.HResult } }); try { Reply(client.GetStream(), 400, "text/plain", new byte[0]); } catch { Telemetry.Summary("static_host", "reply.failed"); } }
            }
        }
        static string Mime(string ext)
        {
            switch (ext.ToLowerInvariant())
            {
                case ".html": case ".htm": return "text/html; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".js": case ".mjs": return "text/javascript; charset=utf-8";
                case ".json": return "application/json";
                case ".svg": return "image/svg+xml";
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".ico": return "image/x-icon";
                case ".webp": return "image/webp";
                case ".woff2": return "font/woff2";
                case ".wasm": return "application/wasm";
                default: return "application/octet-stream";
            }
        }
        static void Reply(Stream stream, int code, string mime, byte[] bytes)
        {
            byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 " + code + " Response\r\nContent-Type: " + mime + "\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\nX-Content-Type-Options: nosniff\r\nCache-Control: no-store\r\n\r\n");
            stream.Write(header, 0, header.Length); stream.Write(bytes, 0, bytes.Length);
        }
        public void Dispose() { running = false; listener.Stop(); Telemetry.Write("INFO", "static_host", "listener.stopped", "Host loopback static transport stopped", null, null, true); }
    }
}
