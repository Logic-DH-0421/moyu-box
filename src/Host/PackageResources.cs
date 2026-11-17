using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace MoyuBox
{
    public static class PackageResources
    {
        // A bounded, local-only preflight. Never fetch remote resources or execute HTML.
        public static string Check(string root,string entry)
        {
            if(!entry.EndsWith(".html",StringComparison.OrdinalIgnoreCase)&&!entry.EndsWith(".htm",StringComparison.OrdinalIgnoreCase))return null;
            string path=Rules.ContainedPath(root,entry);if(new FileInfo(path).Length>2*1024*1024)return null;
            var missing=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(Match match in Regex.Matches(File.ReadAllText(path),"(?:src|href)\\s*=\\s*[\"']([^\"']+)[\"']",RegexOptions.IgnoreCase)) {
                string value=match.Groups[1].Value;
                if(value.StartsWith("#")||value.StartsWith("//")||value.Contains(":"))continue;
                value=Uri.UnescapeDataString(value.Split('?','#')[0]);if(value.Length==0)continue;
                try {
                    string target=Path.GetFullPath(Path.Combine(value.StartsWith("/")?root:Path.GetDirectoryName(path),value.TrimStart('/')));
                    if(!target.StartsWith(Path.GetFullPath(root).TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase)||!File.Exists(target)&&!Directory.Exists(target))missing.Add(value);
                }catch(ArgumentException){missing.Add(value);}catch(NotSupportedException){missing.Add(value);}
                if(missing.Count>=20)break;
            }
            return missing.Count==0?null:"项目已导入，但检测到 "+missing.Count+" 个本地资源引用缺失。请导入完整网页文件夹或 ZIP；单个 HTML 不包含其 CSS、脚本和图片。";
        }
    }
}
