using System;
using System.IO;
using MoyuBox;
class RankingFeedTests
{
    static int count;
    static void Check(bool value,string name){if(!value)throw new Exception(name);count++;}
    static int Main(string[] args){try{
        string html="<article><h2><a href='/owner/repo'>owner/repo</a></h2><p>A &amp; B <script>unsafe</script></p></article>";
        var items=RankingFeed.Parse(html+html+"<article><h2><a href='//evil.example/repo'>evil</a></h2></article>");
        Check(items.Count==1,"deduplicate and exclude foreign URLs");Check(items[0].name=="owner/repo","repo name");Check(items[0].description=="A & B unsafe","plain description");
        foreach(string url in new[]{"javascript:alert(1)","https://github.com.evil/a/b","https://name:secret@github.com/a/b","https://github.com/a/b?q=secret","https://github.com/a/b/tree/main"})Check(!RankingFeed.IsRepo(url),"reject invalid URL");
        bool failed=false;try{RankingFeed.Parse("<h1>Verify you are human</h1>");}catch(InvalidDataException){failed=true;}Check(failed,"no fake empty success on verification page");
        string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);Telemetry.Current=new DiagnosticLog(root); System.Net.ServicePointManager.SecurityProtocol=System.Net.SecurityProtocolType.Tls12;var feed=new RankingFeed(root);
        if(args.Length>1){feed.Refresh(()=>{}).GetAwaiter().GetResult();File.WriteAllText(Path.Combine(root,"live-result.json"),RegistryStore.Json.Serialize(feed));Console.WriteLine("Live feed: "+feed.status+" count="+feed.items.Count);Telemetry.Current.Flush(2000);Check(feed.status=="ready" && feed.items.Count>0,"live source parsed");var cached=new RankingFeed(root);Check(cached.status=="cached"&&cached.items.Count==feed.items.Count,"cache reload");}
        Console.WriteLine(count+" ranking checks passed");return 0;
    }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}
