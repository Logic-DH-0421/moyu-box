using System;
using System.IO;
using MoyuBox;
class RegistryTests
{
    static int checks;
    static void Check(bool value) { if (!value) throw new Exception("Assertion " + checks); checks++; }
    static void Reject(Action action) { try { action(); } catch (ArgumentException) { checks++; return; } throw new Exception("Unsafe input accepted"); }
    static int Main(string[] args)
    {
        string root=Path.Combine(Path.GetFullPath(args[1]),"MoyuRegistry-"+Guid.NewGuid().ToString("N"));
        try
        {
            Reject(()=>Rules.ContainedPath(root,"..\\secret.txt"));
            Reject(()=>Rules.ContainedPath(root,".. \\secret.txt"));
            Reject(()=>Rules.ContainedPath(root,"index.html:secret"));
            Reject(()=>Rules.ContainedPath(root,"C:\\Windows\\win.ini"));
            Reject(()=>Rules.Website("javascript:alert(1)"));
            Reject(()=>Rules.Website("https://name:secret@example.com"));
            Reject(()=>Rules.Validate(new Project{id="bad id",name="Bad",kind="website",url="https://example.com"},true));
            var store=new RegistryStore(root,args[0]);Check(store.State.projects.Count==Directory.GetFiles(args[0],"*.json").Length);
            store.Add(new Project{id="sixth-project",name="Sixth",kind="website",url="https://example.com"});
            var legacyJson=RegistryStore.Json.Deserialize<System.Collections.Generic.Dictionary<string,object>>(File.ReadAllText(Path.Combine(root,"settings.json")));
            legacyJson.Remove("autoMaximizeProjects");legacyJson.Remove("defaultBookReader");
            var oldProjects=(System.Collections.ArrayList)legacyJson["projects"];
            for(int n=oldProjects.Count-1;n>=0;n--)if(Convert.ToString(((System.Collections.Generic.Dictionary<string,object>)oldProjects[n])["id"])=="word-reader")oldProjects.RemoveAt(n);
            legacyJson.Remove("favoriteIds");legacyJson.Remove("displayName");legacyJson.Remove("bio");
            File.WriteAllText(Path.Combine(root,"settings.json"),RegistryStore.Json.Serialize(legacyJson));
            var legacy=new RegistryStore(root,args[0]);Check(legacy.State.projects.Count==Directory.GetFiles(args[0],"*.json").Length+1 && legacy.State.favoriteIds.Count==0 && legacy.State.displayName=="小鱼" && legacy.State.autoMaximizeProjects && legacy.State.defaultBookReader=="word" && legacy.Find("word-reader").builtin);
            Check(legacy.Find("reader").zoomPercent==100);
            Reject(()=>Rules.Validate(new Project{id="bad-zoom",name="Bad",kind="website",url="https://example.com",zoomPercent=20},true));
            store.State.displayName="测试小鱼"; store.State.bio="框架资料";store.State.favoriteIds.Add("sixth-project");store.Save();
            var reopened=new RegistryStore(root,args[0]);Check(reopened.State.projects.Count==Directory.GetFiles(args[0],"*.json").Length+1);
            Check(reopened.State.favoriteIds.Contains("sixth-project") && reopened.State.displayName=="测试小鱼");
            Rules.Validate(new Project{id="gh-ok",name="GitHub",kind="github",url="https://github.com/owner/repo",launchUrl="https://owner.github.io/repo/"},true);checks++;
            Reject(()=>Rules.Validate(new Project{id="gh-bad",name="Bad",kind="github",url="https://example.com/owner/repo"},true));
            Reject(()=>Rules.Validate(new Project{id="book-bad",name="Bad",kind="book",entry="payload.exe"},true));
            Rules.Validate(new Project{id="book-ok",name="Book",kind="book",entry="document.epub"},true);checks++;
            File.WriteAllText(Path.Combine(root,"settings.json"),"{corrupt");
            var recovered=new RegistryStore(root,args[0]);Check(recovered.RecoveryNotice!=null);Check(Directory.GetFiles(root,"*.damaged-*").Length==1);
            string oldDefaults=Path.Combine(root,"old-defaults");Directory.CreateDirectory(oldDefaults);
            foreach(string source in Directory.GetFiles(args[0],"*.json"))if(String.CompareOrdinal(Path.GetFileName(source),"07-")<0)File.Copy(source,Path.Combine(oldDefaults,Path.GetFileName(source)));
            var full=new RegistryStore(Path.Combine(root,"full"),oldDefaults);
            while(full.State.projects.Count<200)full.State.projects.Add(new Project{id="custom-"+full.State.projects.Count,name="preserve",kind="website",url="https://example.com"});
            full.State.displayName="keep-profile";full.Save();
            var upgraded=new RegistryStore(full.Root,args[0]);Check(upgraded.State.projects.Count==200 && upgraded.State.displayName=="keep-profile" && upgraded.RecoveryNotice!=null);
            Console.WriteLine("Registry tests: "+checks+" passed");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally { if(Directory.Exists(root))Directory.Delete(root,true); }
    }
}
