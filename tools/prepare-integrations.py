"""Snapshot explicit local sources; never copy user libraries, accounts or build trees."""
from pathlib import Path
import hashlib
import json
import shutil
import re

ROOT = Path(__file__).resolve().parents[1]
DOCS = Path.home() / "Documents"
sources = {
    "shijie": DOCS / "ChatGPT/时节/src",
    "reader": DOCS / "ChatGPT/仿AI小说阅读器/src",
    "bilibili": DOCS / "New project 3/bilibili_cache_manager",
    "multiai": DOCS / "多平台Ai同步输入器",
}
manifest = {}
def copy(source, target):
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, target)
    manifest[str(target.relative_to(ROOT)).replace("\\", "/")] = hashlib.sha256(source.read_bytes()).hexdigest()

reader_css = (ROOT / "vendor/reader/styles.css").read_text(encoding="utf-8-sig")
reader_css = reader_css[reader_css.index("/* MoyuBox compact reader:"):]
reader_js = (ROOT / "vendor/reader/app.js").read_text(encoding="utf-8-sig")
reader_js = reader_js[reader_js.rindex(";(function(){var app=document.getElementById('app');"):]
bili_css = (ROOT / "vendor/bilibili/bilibili_cache_manager/web/style.css").read_text(encoding="utf-8-sig")
bili_css = bili_css[bili_css.index("/* MoyuBox host:"):]

for name in ("app.html", "core.js", "upgrade.css", "next.css", "xlsx.full.min.js"):
    copy(sources["shijie"] / name, ROOT / "vendor/shijie" / name)
for name in ("Program.cs", "BrowserHost.cs", "EpubParser.cs", "RuntimeLog.cs", "StructuredEpubParser.cs", "ContentPositionMapper.cs", "TextStructure.cs", "index.html", "app.js", "styles.css"):
    copy(sources["reader"] / name, ROOT / "vendor/reader" / name)
for filename, suffix in (("app.js", reader_js), ("styles.css", reader_css)):
    path = ROOT / "vendor/reader" / filename
    path.write_text(path.read_text(encoding="utf-8-sig") + "\n" + suffix, encoding="utf-8-sig")

# Preserve English heading compatibility introduced by the host before structured parsing.
p=ROOT / 'vendor/reader/TextStructure.cs'
s=p.read_text(encoding='utf-8-sig').replace('(CHAPTER|BOOK|PART)', '(CHAPTER|BOOK|PART|ACT|SCENE)').replace('([0-9]{1,6}|[IVXLCDM]{1,12})','([0-9]{1,6}|[IVXLCDM]{1,12}|ONE|TWO|THREE|FOUR|FIVE|SIX|SEVEN|EIGHT|NINE|TEN|ELEVEN|TWELVE)')
p.write_text(s,encoding='utf-8-sig')

# Preserve host-maintained reader layout compatibility when refreshing snapshots.
import subprocess, sys
subprocess.run([sys.executable, str(ROOT / "tools/patch-reader-compatibility.py"), str(ROOT / "vendor/reader/Program.cs")], check=True)

# Preserve the controlled Word UI and host-only shutdown boundary in the new reader server.
p = ROOT / "vendor/reader/BrowserHost.cs"
s = p.read_text(encoding="utf-8-sig").replace("        public string BaseUrl { get; private set; }", "        public string WordUiDirectory { get; set; }\n        public Action ShutdownRequested { get; set; }\n        public bool UseEphemeralPort { get; set; }\n        public string BaseUrl { get; private set; }")
s = s.replace("            Uri uri = new Uri(\"http://127.0.0.1\" + request.Target);", '''            string expectedHost = new Uri(BaseUrl).Authority;
            if (!request.Headers.ContainsKey("host") || request.Headers["host"] != expectedHost || (request.Headers.ContainsKey("origin") && request.Headers["origin"] != BaseUrl.TrimEnd('/'))) { WriteJson(stream, 403, "{}"); return; }
            if (request.Method == "POST" && request.Target == "/shutdown") {
                string expected = Environment.GetEnvironmentVariable("MOYU_SERVICE_TOKEN");
                if (String.IsNullOrEmpty(expected) || !request.Headers.ContainsKey("x-moyu-token") || request.Headers["x-moyu-token"] != expected) { WriteJson(stream, 403, "{}"); return; }
                WriteJson(stream, 200, "{\\"ok\\":true}"); if (ShutdownRequested != null) ShutdownRequested(); return;
            }
            Uri uri = new Uri("http://127.0.0.1" + request.Target);''')
s = s.replace('string html = ResourceLoader.BuildDocument().Replace(', 'string html = (String.IsNullOrEmpty(WordUiDirectory) ? ResourceLoader.BuildDocument() : File.ReadAllText(Path.Combine(WordUiDirectory, "index.html"), Encoding.UTF8)).Replace(')
s = s.replace('            if (request.Method == "GET" && path == "/favicon.ico")', '''            if (request.Method == "GET" && !String.IsNullOrEmpty(WordUiDirectory) && path.StartsWith("/word/", StringComparison.Ordinal)) {
                string name = path.Substring(6);
                string[] assets = { "reader.js", "reader.css", "gm-shim.js", "engine.js", "novel-disguise-resource.js", "novel-disguise-jquery.js" };
                if (Array.IndexOf(assets, name) < 0) { WriteJson(stream, 404, "{}"); return; }
                Write(stream, 200, name.EndsWith(".css") ? "text/css; charset=utf-8" : "text/javascript; charset=utf-8", File.ReadAllBytes(Path.Combine(WordUiDirectory, name))); return;
            }
            if (request.Method == "GET" && path == "/favicon.ico")''')
s = s.replace("for (int port = 17890; port <= 17920; port++)", "for (int port = UseEphemeralPort ? 0 : 17890; port <= (UseEphemeralPort ? 0 : 17920); port++)").replace('BaseUrl = "http://127.0.0.1:" + port + "/";', 'BaseUrl = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/";')
p.write_text(s, encoding="utf-8-sig")

for source in sources["bilibili"].rglob("*"):
    if source.is_file() and "__pycache__" not in source.parts and (source.suffix == ".py" or "web" in source.relative_to(sources["bilibili"]).parts):
        copy(source, ROOT / "vendor/bilibili/bilibili_cache_manager" / source.relative_to(sources["bilibili"]))
for project in ("bilibili/bilibili_cache_manager",):
    path = ROOT / "vendor" / project / "config_store.py"
    text = path.read_text(encoding="utf-8-sig").replace('def app_data_dir() -> Path:\n', '''def app_data_dir() -> Path:
    override = os.environ.get("MOYU_PROJECT_DATA")
    if override:
        target = Path(override).resolve()
        target.mkdir(parents=True, exist_ok=True)
        return target
''')
    path.write_text(text, encoding="utf-8")
p = ROOT / "vendor/bilibili/bilibili_cache_manager/web/style.css"
p.write_text(p.read_text(encoding="utf-8-sig") + "\n" + bili_css, encoding="utf-8")

for name, target in (("app.py", "multiai_app.py"), ("platform_adapters.py", "platform_adapters.py"), ("runtime_logging.py", "runtime_logging.py")):
    copy(sources["multiai"] / name, ROOT / "vendor/multiai" / target)
for source in (sources["multiai"] / "web").rglob("*"):
    if source.is_file(): copy(source, ROOT / "vendor/multiai/web" / source.relative_to(sources["multiai"] / "web"))
versions = {
    'shijie': re.search(r"version:'([^']+)'",(sources['shijie']/'app.html').read_text(encoding='utf-8-sig')).group(1),
    'reader': re.search(r'AssemblyVersion\("([0-9.]+)"\)',(sources['reader']/'Program.cs').read_text(encoding='utf-8-sig')).group(1).removesuffix('.0'),
    'bilibili': re.search(r'__version__\s*=\s*"([^"]+)"',(sources['bilibili']/'__init__.py').read_text(encoding='utf-8-sig')).group(1),
    'multiai': re.search(r'VERSION\s*=\s*"([^"]+)"',(sources['multiai']/'app.py').read_text(encoding='utf-8-sig')).group(1),
}
(ROOT / "vendor/integration-sources.json").write_text(json.dumps({"versions": versions, "source_sha256":manifest}, ensure_ascii=False, indent=2), encoding="utf-8")
print("Explicit source snapshots prepared; original projects and user data unchanged.")
