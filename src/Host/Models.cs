using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace MoyuBox
{
    public sealed class Project
    {
        public int schemaVersion = 1;
        public string id;
        public string name;
        public string description;
        public string kind;
        public string icon = "blocks";
        public string rankingCategory;
        public string url;
        public string entry;
        public string launchUrl;
        public int zoomPercent = 100;
        public bool enabled = true;
        public bool builtin;
    }
    public sealed class Preferences
    {
        public int schemaVersion = 1;
        public List<Project> projects = new List<Project>();
        public string theme = "system";
        public string feedbackDraft = "";
        public string contactEmail = "";
        public string contactUrl = "";
        public List<string> favoriteIds = new List<string>();
        public string displayName = "小鱼";
        public string bio = "在自己的节奏里，歇一会儿。";
        public string defaultBookReader = "word";
        public bool autoMaximizeProjects = true;
        public int width = 1040;
        public int height = 720;
    }
    public static class Rules
    {
        public static string ContainedPath(string root, string relative)
        {
            if (String.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new ArgumentException("入口必须是项目内的相对路径。");
            foreach (string part in relative.Replace('\\', '/').Split('/')) if (part.Contains(":") || part.EndsWith(" ") || part.EndsWith(".")) throw new ArgumentException("入口包含不安全的路径片段。");
            string basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("入口不能指向项目目录之外。");
            return path;
        }
        public static Uri Website(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || !String.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("请填写完整的网址。");
            if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) throw new ArgumentException("网站须使用 HTTPS；本机服务可以使用 HTTP。");
            return uri;
        }
        public static void Validate(Project project, bool user)
        {
            if (project == null || project.schemaVersion != 1) throw new ArgumentException("不支持的项目配置版本。");
            if (String.IsNullOrEmpty(project.id) || !Regex.IsMatch(project.id, "^[a-z][a-z0-9-]{1,63}$")) throw new ArgumentException("项目 ID 须为 2–64 位小写字母、数字或连字符。");
            if (String.IsNullOrWhiteSpace(project.name) || project.name.Length > 60) throw new ArgumentException("项目名称须为 1–60 个字符。");
            if (project.description != null && project.description.Length > 240) throw new ArgumentException("项目描述过长。");
            if (project.zoomPercent != 0 && (project.zoomPercent < 50 || project.zoomPercent > 200)) throw new ArgumentException("项目缩放须为适合宽度或 50%–200%。");
            if (project.kind == "website") Website(project.url);
            else if (project.kind == "github")
            {
                Uri repo = Website(project.url);
                if (repo.Scheme != "https" || repo.Host != "github.com" || repo.AbsolutePath.Trim('/').Split('/').Length != 2 || !String.IsNullOrEmpty(repo.Query) || !String.IsNullOrEmpty(repo.Fragment)) throw new ArgumentException("请填写 GitHub 仓库地址，例如 https://github.com/owner/repo。");
                if (!String.IsNullOrWhiteSpace(project.launchUrl)) Website(project.launchUrl);
            }
            else if (project.kind == "book") { if (String.IsNullOrWhiteSpace(project.entry) || !(project.entry.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || project.entry.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("仅支持 TXT / EPUB 文件。"); }
            else if (project.kind == "local-web") { if (String.IsNullOrWhiteSpace(project.entry)) throw new ArgumentException("缺少网页入口。"); }
            else if (project.kind == "external") { if (String.IsNullOrWhiteSpace(project.entry) || !Path.IsPathRooted(project.entry) || !project.entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请选择 EXE 程序。"); }
            else if (user || !new[] { "shijie", "reader", "word-reader", "bilibili", "multiai" }.Contains(project.kind)) throw new ArgumentException("不支持的项目类型。");
        }
    }
    public sealed class RegistryStore
    {
        public static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
        public readonly string Root;
        readonly string file;
        public Preferences State { get; private set; }
        public string RecoveryNotice { get; private set; }
        public RegistryStore(string root, string defaults)
        {
            using (var operation = Telemetry.Begin("registry", "settings.load"))
            try {
            Root = Path.GetFullPath(root); Directory.CreateDirectory(Root);
            file = Path.Combine(Root, "settings.json");
            if (File.Exists(file))
            {
                try { State = Read(file); }
                catch (Exception first)
                {
                    Telemetry.Caught("registry", "settings.primary_invalid", first);
                    if (!File.Exists(file + ".bak")) throw new IOException("设置文件损坏。已保留原文件，请从备份恢复，或使用独立数据目录启动。", first);
                    State = Read(file + ".bak");
                    File.Copy(file, file + ".damaged-" + DateTime.Now.ToString("yyyyMMddHHmmssfff"), false);
                    File.Copy(file + ".bak", file, true);
                    Telemetry.Write("WARN", "registry", "settings.recovered", "Settings restored from validated backup", new Dictionary<string, object> { { "recovered", true } }, null, true);
                    RecoveryNotice = "设置文件异常，已从备份恢复。损坏文件已单独保留。";
                }
            }
            else
            {
                State = new Preferences();
                foreach (string path in Directory.GetFiles(defaults, "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    Project project = Json.Deserialize<Project>(File.ReadAllText(path, Encoding.UTF8)); Rules.Validate(project, false); State.projects.Add(project);
                }
                Save();
            }
            // Register newly shipped adapters without resetting existing projects or preferences.
            var additions = new List<Project>();
            foreach (string path in Directory.GetFiles(defaults, "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) {
                Project project = Json.Deserialize<Project>(File.ReadAllText(path, Encoding.UTF8)); Rules.Validate(project, false);
                if (project.builtin && !State.projects.Any(x => x.id == project.id)) additions.Add(project);
            }
            if (additions.Count > 0) {
                if (State.projects.Count + additions.Count > 200) {
                    RecoveryNotice = "项目数量已达上限，新增内置项目暂未添加；移除不需要的入口后重启可补齐。";
                    Telemetry.Write("WARN", "registry", "migration.deferred", "New built-in adapters deferred because registry is full", new Dictionary<string, object> { { "count", additions.Count } }, null, true);
                } else { State.projects.AddRange(additions); try { Save(); } catch { foreach (var item in additions) State.projects.Remove(item); throw; } }
            }
            operation.Success(true, new Dictionary<string, object> { { "count", State.projects.Count }, { "schema", State.schemaVersion } });
            } catch (Exception error) { operation.Fail(error); throw; }
        }
        static Preferences Read(string path)
        {
            Preferences result = Json.Deserialize<Preferences>(File.ReadAllText(path, Encoding.UTF8));
            if (result == null || result.schemaVersion != 1 || result.projects == null || result.projects.Count > 200) throw new InvalidDataException("设置结构或版本无效。");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Project project in result.projects) { Rules.Validate(project, !project.builtin); if (!ids.Add(project.id)) throw new InvalidDataException("项目 ID 重复。"); }
            if (!new[] { "system", "light", "dark" }.Contains(result.theme)) result.theme = "system";
            if (!new[] { "word", "ai" }.Contains(result.defaultBookReader)) result.defaultBookReader = "word";
            if (result.favoriteIds == null) result.favoriteIds = new List<string>();
            result.favoriteIds = result.favoriteIds.Where(x => ids.Contains(x)).Distinct().ToList();
            return result;
        }
        public void Save()
        {
            using (var operation = Telemetry.Begin("registry", "settings.save"))
            try {
                string temp = file + ".tmp";
                string serialized = Json.Serialize(State);
                File.WriteAllText(temp, serialized, new UTF8Encoding(false));
                if (File.Exists(file)) File.Replace(temp, file, file + ".bak", true); else File.Move(temp, file);
                if (File.ReadAllText(file, Encoding.UTF8) != serialized) throw new IOException("Settings verification failed");
                Read(file);
                operation.Success(true, new Dictionary<string, object> { { "count", State.projects.Count }, { "schema", State.schemaVersion }, { "bytes", Encoding.UTF8.GetByteCount(serialized) } });
            } catch (Exception error) { operation.Fail(error); throw; }
        }
        public Project Find(string id)
        {
            Project result = State.projects.FirstOrDefault(x => x.id == id);
            if (result == null) throw new ArgumentException("项目不存在。");
            return result;
        }
        public string ProjectRoot(string id) { if (!Regex.IsMatch(id ?? "", "^[a-z][a-z0-9-]{1,63}$")) throw new ArgumentException("无效项目 ID"); string path = Path.Combine(Root, "projects", id); Directory.CreateDirectory(path); return path; }
        public void Add(Project project)
        {
            Rules.Validate(project, true);
            if (State.projects.Any(x => x.id == project.id)) throw new ArgumentException("项目 ID 已存在。");
            if (State.projects.Count >= 200) throw new ArgumentException("项目数量已达上限。");
            State.projects.Add(project);
            try { Save(); } catch { State.projects.Remove(project); throw; }
        }
    }
}
