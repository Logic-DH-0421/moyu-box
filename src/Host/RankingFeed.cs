using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MoyuBox
{
    internal sealed class RankingEntry { public string name; public string url; public string description; }
    internal sealed class RankingFeed
    {
        public string status = "idle", fetchedAt, error;
        public List<RankingEntry> items = new List<RankingEntry>();
        bool busy;
        readonly string cache;
        public RankingFeed(string root) {
            cache = Path.Combine(root, "github-trending-cache.json");
            try { if (File.Exists(cache)) {
                var old = RegistryStore.Json.Deserialize<RankingFeedCache>(File.ReadAllText(cache, Encoding.UTF8));
                if (old != null && old.items != null && old.items.Count <= 50 && old.items.TrueForAll(x => IsRepo(x.url))) { items=old.items; fetchedAt=old.fetchedAt; status="cached"; }
            } } catch(Exception ex) { Telemetry.Caught("rankings", "cache.read_failed", ex, true); }
        }
        internal sealed class RankingFeedCache { public string fetchedAt; public List<RankingEntry> items; }
        public static bool IsRepo(string url) { Uri u; return Uri.TryCreate(url,UriKind.Absolute,out u) && u.Scheme=="https" && u.Host=="github.com" && String.IsNullOrEmpty(u.UserInfo) && String.IsNullOrEmpty(u.Query) && String.IsNullOrEmpty(u.Fragment) && Regex.IsMatch(u.AbsolutePath,@"^/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/?$"); }
        static string Plain(string value) { return Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(value,"<[^>]+>"," ")),@"\s+"," ").Trim(); }
        public static List<RankingEntry> Parse(string html) {
            var result=new List<RankingEntry>();
            foreach(Match article in Regex.Matches(html,@"<article\b[^>]*>([\s\S]*?)</article>",RegexOptions.IgnoreCase)) {
                var heading=Regex.Match(article.Groups[1].Value,@"<h2\b[^>]*>([\s\S]*?)</h2>",RegexOptions.IgnoreCase);
                var link=Regex.Match(heading.Groups[1].Value,"href=[\"'](/[^\"']+)[\"']",RegexOptions.IgnoreCase);
                string url="https://github.com"+WebUtility.HtmlDecode(link.Groups[1].Value);
                if(!link.Success || !IsRepo(url) || result.Exists(x=>x.url==url))continue;
                var desc=Regex.Match(article.Groups[1].Value,@"<p\b[^>]*>([\s\S]*?)</p>",RegexOptions.IgnoreCase);
                string description=Plain(desc.Groups[1].Value); if(description.Length>400)description=description.Substring(0,400);
                result.Add(new RankingEntry { name=url.Substring(19), url=url, description=description }); if(result.Count>=25)break;
            }
            if(result.Count==0)throw new InvalidDataException("来源页面未包含可识别的仓库条目，可能遇到页面变化或验证页面。");
            return result;
        }
        public async Task Refresh(Action publish) {
            if(busy)return; busy=true;status="loading";error=null;publish();
            using(var operation=Telemetry.Begin("rankings","github.refresh"))
            try {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                var req=(HttpWebRequest)WebRequest.Create("https://github.com/trending");req.UserAgent="MoyuBox/0.8.0";req.Accept="text/html";req.AllowAutoRedirect=false;req.AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate;
                List<RankingEntry> parsed=await Task.Run(()=>{using(var timer=new System.Threading.Timer(_=>req.Abort(),null,15000,System.Threading.Timeout.Infinite)) using(var response=(HttpWebResponse)req.GetResponse()) using(var stream=response.GetResponseStream()) using(var output=new MemoryStream()){
                    if(response.StatusCode!=HttpStatusCode.OK)throw new IOException("来源响应异常："+(int)response.StatusCode);
                    var buffer=new byte[8192];int n;while((n=stream.Read(buffer,0,buffer.Length))>0){if(output.Length+n>2*1024*1024)throw new InvalidDataException("来源页面超过读取上限。");output.Write(buffer,0,n);}return Parse(Encoding.UTF8.GetString(output.ToArray()));}});
                items=parsed;fetchedAt=DateTimeOffset.UtcNow.ToString("o");status="ready";
                try{string temp=cache+".tmp";File.WriteAllText(temp,RegistryStore.Json.Serialize(new RankingFeedCache { items=items,fetchedAt=fetchedAt }),new UTF8Encoding(false));if(File.Exists(cache))File.Replace(temp,cache,null);else File.Move(temp,cache);}catch(Exception ex){Telemetry.Caught("rankings","cache.write_failed",ex,true);}
                operation.Success(true,new Dictionary<string,object>{{"count",items.Count}});
            } catch(Exception ex) { status=items.Count>0?"stale":"error";error="本次榜单读取未完成，可重试或打开原站。";operation.Fail(ex); }
            finally {busy=false;publish();}
        }
    }
}
