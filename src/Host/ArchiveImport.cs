using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MoyuBox
{
    internal sealed partial class MainWindow
    {
        async Task<Project> ImportArchive(string source)
        {
            using(var operation=Telemetry.Begin("imports","archive.import",DiagnosticLog.Fingerprint(source)))
            {
                string temporary=Path.Combine(store.Root,"import-staging",Guid.NewGuid().ToString("N"));
                try {
                    await Task.Run(delegate {PackageArchive.Extract(source,temporary);});
                    string[] entries=PackageArchive.Entries(temporary);
                    if(entries.Length==0)throw new InvalidDataException("ZIP 中没有可运行的 HTML 或网页项目 manifest。源码需先构建；EXE 请解压后使用“关联桌面程序”。");
                    string entry=entries.Length==1?entries[0]:ChooseArchiveEntry(temporary,entries);
                    if(entry==null){operation.Cancel();return null;}
                    Project project;
                    if(String.Equals(Path.GetFileName(entry),"manifest.json",StringComparison.OrdinalIgnoreCase)) {
                        project=RegistryStore.Json.Deserialize<Project>(File.ReadAllText(entry,Encoding.UTF8));project.builtin=false;project.enabled=true;
                        if(project.kind!="local-web")throw new ArgumentException("项目包类型须为 local-web。");
                    } else {
                        string name=Path.GetFileNameWithoutExtension(source);if(name.Length>60)name=name.Substring(0,60);
                        project=new Project{id="zip-"+Guid.NewGuid().ToString("N").Substring(0,12),name=name,kind="local-web",zoomPercent=0,icon="blocks",entry=entry.Substring(temporary.Length+1).Replace('\\','/'),description="ZIP 静态网页项目"};
                    }
                    string package=String.Equals(Path.GetFileName(entry),"manifest.json",StringComparison.OrdinalIgnoreCase)?Path.GetDirectoryName(entry):temporary;
                    // Keep archive-relative assets, including ../ resources beside a nested index.
                    ImportPackage(project,package);Publish();operation.Success(true,new Dictionary<string,object>{{"count",1}});return project;
                }catch(Exception error){operation.Fail(error);throw;}
                finally {try {if(Directory.Exists(temporary))Directory.Delete(temporary,true);}catch(Exception error){Telemetry.Caught("imports","staging.cleanup_failed",error);}}
            }
        }
        string ChooseArchiveEntry(string root,string[] entries)
        {
            using(var dialog=new Form{Text="选择 ZIP 项目入口",Width=620,Height=420,StartPosition=FormStartPosition.CenterParent,MinimizeBox=false,MaximizeBox=false})
            using(var list=new ListBox{Dock=DockStyle.Fill})
            using(var buttons=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=48,FlowDirection=FlowDirection.RightToLeft})
            {
                foreach(string entry in entries)list.Items.Add(entry.Substring(root.Length+1));list.SelectedIndex=0;
                var ok=new Button{Text="导入所选入口",DialogResult=DialogResult.OK,AutoSize=true};var cancel=new Button{Text="取消",DialogResult=DialogResult.Cancel};buttons.Controls.Add(ok);buttons.Controls.Add(cancel);dialog.Controls.Add(list);dialog.Controls.Add(buttons);dialog.AcceptButton=ok;dialog.CancelButton=cancel;
                return dialog.ShowDialog(this)==DialogResult.OK?entries[list.SelectedIndex]:null;
            }
        }
    }
}
