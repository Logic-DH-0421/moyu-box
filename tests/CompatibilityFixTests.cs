using System;
using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using MoyuBox;
class CompatibilityFixTests
{
    static int checks;
    static string root;
    static void Check(bool condition,string label) {if(!condition)throw new Exception(label);checks++;}
    static void Reject(Action action,string label) {bool rejected=false;try{action();}catch(InvalidDataException){rejected=true;}catch(ArgumentException){rejected=true;}Check(rejected,label);}
    static string Zip(string name,params string[] entries) {
        string path=Path.Combine(root,name+".zip");using(var f=File.Create(path))using(var zip=new ZipArchive(f,ZipArchiveMode.Create))foreach(var entry in entries)using(var w=new StreamWriter(zip.CreateEntry(entry).Open()))w.Write("<!doctype html><p>actual content</p>");return path;
    }
    static void Mutate(string path,int offset,uint value) {var bytes=File.ReadAllBytes(path);for(int i=0;i<bytes.Length-4;i++)if(BitConverter.ToUInt32(bytes,i)==0x02014b50){Buffer.BlockCopy(BitConverter.GetBytes(value),0,bytes,i+offset,4);File.WriteAllBytes(path,bytes);return;}throw new Exception("Central directory not found");}
    static IList Parse(string method,string text) {var type=typeof(Project).Assembly.GetType("AiNovelReader.BookParser");object parsed=type.GetMethod(method,BindingFlags.Static|BindingFlags.Public).Invoke(null,new object[]{Encoding.UTF8.GetBytes(text),"test"});return (IList)parsed.GetType().GetField("Chapters").GetValue(parsed);}
    static string Text(object chapter,string name){return (string)chapter.GetType().GetField(name).GetValue(chapter);}
    static int Main(string[] args) {
        root=Path.Combine(Path.GetFullPath(args[0]),"fix-unit-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try {
            string valid=Zip("valid","project/index.html","project/script.js");string target=Path.Combine(root,"good");PackageArchive.Extract(valid,target);Check(File.Exists(Path.Combine(target,"project/index.html")),"real extraction");Check(PackageArchive.Entries(target).Length==1,"nested index selection");
            foreach(string name in new[]{"../escape.html","/absolute.html","C:/escape.html","a/../../escape.html","a\\..\\escape.html","NUL","CON.txt","a/COM1.log","LPT1 .txt"}) {string p=Zip("unsafe-"+checks,name);Reject(()=>PackageArchive.Extract(p,Path.Combine(root,"reject-"+checks)),"reject "+name);}
            Check(!File.Exists(Path.Combine(root,"escape.html")),"no escaped files");
            string duplicate=Zip("duplicate","index.html","INDEX.html");Reject(()=>PackageArchive.Extract(duplicate,Path.Combine(root,"dup")),"case collisions");
            string symlink=Zip("symlink","index.html");Mutate(symlink,38,0xa1ff0000);Reject(()=>PackageArchive.Extract(symlink,Path.Combine(root,"sym")),"symlink rejected");
            string encrypted=Zip("encrypted","index.html");var encryptedBytes=File.ReadAllBytes(encrypted);for(int i=0;i<encryptedBytes.Length-46;i++)if(BitConverter.ToUInt32(encryptedBytes,i)==0x02014b50){encryptedBytes[i+8]|=1;break;}File.WriteAllBytes(encrypted,encryptedBytes);Reject(()=>PackageArchive.Extract(encrypted,Path.Combine(root,"enc")),"encrypted rejected");
            string crc=Zip("crc","index.html");Mutate(crc,16,0);Reject(()=>PackageArchive.Extract(crc,Path.Combine(root,"crc")),"CRC actually checked");
            string huge=Zip("huge","index.html");Mutate(huge,24,(uint)PackageArchive.MaxBytes+1);Reject(()=>PackageArchive.Extract(huge,Path.Combine(root,"huge")),"expanded budget");
            string many=Zip("many","index.html");using(var f=File.Open(many,FileMode.Open))using(var z=new ZipArchive(f,ZipArchiveMode.Update))for(int i=0;i<5000;i++)z.CreateEntry("empty-"+i);Reject(()=>PackageArchive.Extract(many,Path.Combine(root,"many")),"entry budget");
            string bad=Path.Combine(root,"broken.zip");File.WriteAllText(bad,"not a zip");Reject(()=>PackageArchive.Extract(bad,Path.Combine(root,"bad")),"broken archive");
            var two=Path.Combine(root,"two");Directory.CreateDirectory(two);File.WriteAllText(Path.Combine(two,"one.html"),"1");File.WriteAllText(Path.Combine(two,"two.html"),"2");Check(PackageArchive.Entries(two).Length==2,"ambiguity preserved for chooser");
            File.WriteAllText(Path.Combine(two,"manifest.json"),"{\"schemaVersion\":1,\"id\":\"test-manifest\",\"name\":\"test\",\"kind\":\"local-web\",\"entry\":\"one.html\"}");Check(PackageArchive.Entries(two).Length==1&&PackageArchive.Entries(two)[0].EndsWith("manifest.json"),"manifest priority");
            File.WriteAllText(Path.Combine(two,"one.html"),"<link href='missing.css'><script src='https://example.com/x.js'></script><a href='#anchor'>a</a>");Check(PackageResources.Check(two,"one.html")!=null,"missing local resource warning");File.WriteAllText(Path.Combine(two,"missing.css"),"body{}");Check(PackageResources.Check(two,"one.html")==null,"complete resource not warned");
            var english=Parse("Parse","Preface\nIntro\nCHAPTER I. Start\nfirst\nCHAPTER II\nsecond");Check(english.Count==3,"English chapters and preface");Check(Text(english[1],"Content")=="first","chapter content retained");Check(Text(english[2],"Title")=="CHAPTER II","Roman heading retained");
            Check(Parse("Parse","Chapter One\na\nChapter Two\nb").Count==2,"word headings");Check(Parse("Parse","第一章 开始\n甲\n第二章 继续\n乙").Count==2,"Chinese chapters intact");Check(Parse("ParseLegacy","CHAPTER I\na\nCHAPTER II\nb").Count==1,"legacy layout preserved");Check(Parse("Parse","ordinary prose\nthis chapter is not a heading\nend").Count==1,"no prose false positive");
            Check(Parse("Parse","Contents\n CHAPTER I. Start\n CHAPTER II. End\n\nCHAPTER I.\nfirst\nCHAPTER II.\nlast").Count==3,"contents list not duplicated as chapters");
            string library=Path.Combine(root,"old-library");var data=new AiNovelReader.DataStore(library);var book=data.ImportBookBytes(Encoding.UTF8.GetBytes("CHAPTER I\none\nCHAPTER II\ntwo"),"legacy.txt");
            var settings=Path.Combine(library,"library.json");string state=File.ReadAllText(settings);state=state.Replace("\"ContentVersion\":2","\"ContentVersion\":0").Replace("\"TextLayoutVersion\":0","\"TextLayoutVersion\":1").Replace("\"LastChapter\":0","\"LastChapter\":1");File.WriteAllText(settings,state);
            var reopened=new AiNovelReader.DataStore(library);reopened.GetChapterJson(book.Id,1);string migrated=reopened.GetLibraryJson();Check(migrated.Contains("\"lastChapter\":1"),"host legacy TXT chapter preserved through structured migration");
            Check(File.ReadAllText(settings).Contains("\"ContentVersion\":2"),"structured content version persisted after migration");
            bool friendly=false;try{data.ImportBookBytes(new byte[]{80,75,3,4},"broken.epub");}catch(InvalidDataException error){friendly=error.Message.Contains("重新下载")&&error.InnerException!=null;}Check(friendly,"reader API preserves inner EPUB error and provides actionable message");
            Check(NavigationMessages.Failure("CertificateExpired",200).Contains("证书"),"certificate guidance");Check(NavigationMessages.Failure("Unknown",503).Contains("503"),"HTTP status guidance");Check(NavigationMessages.Failure("HostNameNotResolved",0).Contains("解析"),"DNS guidance");
            string samples=Path.Combine(Path.GetFullPath(args[0]),"compatibility-20261022","samples");
            if(Directory.Exists(samples))foreach(string id in new[]{"gh-zip-1","gh-zip-2","web-zip-1","web-zip-2"}) {string actual=Path.Combine(samples,id,"download.zip");if(File.Exists(actual)){string unpacked=Path.Combine(root,id);PackageArchive.Extract(actual,unpacked);Check(PackageArchive.Entries(unpacked).Length>0,"real ZIP integrity and entry "+id);}}
            Console.WriteLine("Compatibility fixes: "+checks+" checks passed");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
        finally {if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
