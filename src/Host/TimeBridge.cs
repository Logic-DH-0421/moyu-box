using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MoyuBox
{
    internal sealed partial class SessionManager
    {
        internal List<object> ReadTimeFiles(ProjectSession item, IEnumerable<string> paths)
        {
            using (var operation = Telemetry.Begin("bridge", "text.open"))
            try {
                var files=new List<object>();
                foreach (string path in paths) {
                    if(new FileInfo(path).Length>5*1024*1024)throw new IOException("文本文件请小于 5 MB。");
                    byte[] bytes=File.ReadAllBytes(path);string encoding="utf-8",text;
                    if(bytes.Length>=2&&bytes[0]==255&&bytes[1]==254){text=Encoding.Unicode.GetString(bytes,2,bytes.Length-2);encoding="utf-16le";}
                    else if(bytes.Length>=2&&bytes[0]==254&&bytes[1]==255)text=Encoding.BigEndianUnicode.GetString(bytes,2,bytes.Length-2);
                    else if(bytes.Length>=3&&bytes[0]==239&&bytes[1]==187&&bytes[2]==191){text=Encoding.UTF8.GetString(bytes,3,bytes.Length-3);encoding="utf-8-bom";}
                    else {try{text=new UTF8Encoding(false,true).GetString(bytes);}catch(DecoderFallbackException){text=Encoding.GetEncoding(54936).GetString(bytes);}}
                    string id=Guid.NewGuid().ToString("N");item.TextPaths[id]=path;
                    files.Add(new{id=id,name=Path.GetFileName(path),text=text,encoding=encoding,eol=text.Contains("\r\n")?"CRLF":"LF"});
                }
                operation.Success(true,new Dictionary<string,object>{{"count",files.Count}});return files;
            }catch(Exception error){operation.Fail(error);throw;}
        }
        async Task HandleTimeText(ProjectSession item, string type, Dictionary<string,object> message)
        {
            if(type=="text-open") {
                using(var operation=Telemetry.Begin("dialogs","text.choose_files"))
                using(var dialog=new OpenFileDialog{Filter="文本文件|*.txt;*.md;*.log;*.csv|所有文件|*.*",Multiselect=true}) {
                    if(dialog.ShowDialog(owner)!=DialogResult.OK){operation.Cancel();return;}
                    try{var files=ReadTimeFiles(item,dialog.FileNames);await item.View.CoreWebView2.ExecuteScriptAsync("window.receiveTextFiles("+RegistryStore.Json.Serialize(files)+")");operation.Success();}
                    catch(Exception error){operation.Fail(error);throw;}
                }
                return;
            }
            using(var operation=Telemetry.Begin("bridge","text.save",DiagnosticLog.Fingerprint(Convert.ToString(message["documentId"]))))
            try {
                string id=Convert.ToString(message["documentId"]),path;
                if(!item.TextPaths.TryGetValue(id,out path)||Convert.ToBoolean(message["asNew"])) {
                    string name=Path.GetFileName(Convert.ToString(message["name"]));foreach(char invalid in Path.GetInvalidFileNameChars())name=name.Replace(invalid,'_');
                    using(var dialog=new SaveFileDialog{Filter="文本文件|*.txt|Markdown|*.md|所有文件|*.*",FileName=name,DefaultExt="txt",AddExtension=true}) {
                        if(dialog.ShowDialog(owner)!=DialogResult.OK){operation.Cancel();return;}path=dialog.FileName;
                    }
                }
                string content=Convert.ToString(message["text"]).Replace("\r\n","\n").Replace("\r","\n");
                if(content.Length>5*1024*1024)throw new IOException("文本请小于 5 MB。");
                if(Convert.ToString(message["eol"])=="CRLF")content=content.Replace("\n","\r\n");
                string encodingName=Convert.ToString(message["encoding"]);Encoding encoding=encodingName=="utf-16le"?Encoding.Unicode:new UTF8Encoding(encodingName=="utf-8-bom");
                string temp=path+".moyu-"+Guid.NewGuid().ToString("N")+".tmp";
                try {File.WriteAllText(temp,content,encoding);if(File.ReadAllText(temp,encoding)!=content)throw new IOException("文本保存校验失败。");if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);}
                finally{if(File.Exists(temp))File.Delete(temp);}
                item.TextPaths[id]=path;operation.Success(true);
                await item.View.CoreWebView2.ExecuteScriptAsync("window.noteFileSaved("+RegistryStore.Json.Serialize(id)+","+RegistryStore.Json.Serialize(Path.GetFileName(path))+")");
            }catch(Exception error){operation.Fail(error);throw;}
        }
    }
}
