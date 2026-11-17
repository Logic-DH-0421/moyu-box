using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using Microsoft.Win32;

[assembly: AssemblyTitle("AI小说阅读器")]
[assembly: AssemblyDescription("仿AI对话界面的本地小说阅读器")]
[assembly: AssemblyCompany("Local")]
[assembly: AssemblyProduct("AI小说阅读器")]
[assembly: AssemblyVersion("0.3.14.0")]
[assembly: AssemblyFileVersion("0.3.14.0")]

namespace AiNovelReader
{
    internal static class Program
    {
        public const string Version = "0.3.14";

        [STAThread]
        private static void Main(string[] args)
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;

            if (args.Length > 0 && String.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
            {
                string sample = args.Length > 1 ? args[1] : String.Empty;
                int code = SelfTest.Run(sample, exeDir);
                Environment.Exit(code);
                return;
            }

            string dataRoot = Path.Combine(exeDir, "NovelReaderData");
            RuntimeLog.Initialize(dataRoot, null);
            string exitResult = "success";
            RuntimeLog.InstallExceptionHandlers(delegate { exitResult = "failure"; Application.Exit(); });
            AppDomain.CurrentDomain.ProcessExit += delegate { RuntimeLog.Shutdown(exitResult); };
            try
            {
            DataStore store = new DataStore(dataRoot);

            if (args.Length > 1 && String.Equals(args[0], "--seed", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    store.ImportBook(args[1]);
                    File.WriteAllText(Path.Combine(exeDir, "seed-result.txt"), "SEED_OK\r\n" + RuntimeLog.SessionId, Encoding.UTF8);
                    return;
                }
                catch (Exception ex)
                {
                    File.WriteAllText(Path.Combine(exeDir, "seed-result.txt"), "SEED_FAIL\r\n" + RuntimeLog.SessionId, Encoding.UTF8);
                    exitResult = "failure"; RuntimeLog.Error("application", "seed_failed", ex, RuntimeLog.Fields("result", "failure")); Environment.ExitCode = 1;
                }
                return;
            }

            BrowserHost.Run(store);
            }
            catch (Exception ex)
            {
                exitResult = "failure"; Environment.ExitCode = 1;
                RuntimeLog.Error("application", "startup_or_host_failed", ex, RuntimeLog.Fields("result", "failure"));
                MessageBox.Show("阅读器启动或运行失败。\r\n" + ex.Message + "\r\n诊断目录：" + RuntimeLog.DirectoryPath + "\r\n会话：" + RuntimeLog.SessionId, "阅读器运行失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { RuntimeLog.Shutdown(exitResult); }
        }

        private static void ConfigureBrowserEmulation()
        {
            try
            {
                string exeName = Path.GetFileName(Process.GetCurrentProcess().MainModule.FileName);
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION"))
                {
                    key.SetValue(exeName, 11001, RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                // The HTML also includes an IE=edge directive, so registry denial is non-fatal.
                RuntimeLog.Warning("legacy_host", "browser_emulation_denied", ex, RuntimeLog.Fields("result", "degraded"));
            }
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly WebBrowser browser;
        private readonly ScriptBridge bridge;

        public MainForm(DataStore store)
        {
            Text = "智能文档助手";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1100, 700);
            Size = new Size(1600, 920);
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            BackColor = Color.White;
            KeyPreview = true;

            browser = new WebBrowser();
            browser.Dock = DockStyle.Fill;
            browser.IsWebBrowserContextMenuEnabled = false;
            browser.WebBrowserShortcutsEnabled = false;
            browser.ScriptErrorsSuppressed = true;
            browser.AllowWebBrowserDrop = false;

            bridge = new ScriptBridge(this, store);
            browser.ObjectForScripting = bridge;
            Controls.Add(browser);
            browser.DocumentText = ResourceLoader.BuildDocument();

            FormClosing += delegate { bridge.Flush(); };
        }

        public void SetSafeTitle(bool enabled)
        {
            Text = enabled ? "项目资料整理" : "智能文档助手";
        }
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDispatch)]
    public sealed class ScriptBridge
    {
        private readonly MainForm form;
        private readonly DataStore store;

        internal ScriptBridge(MainForm form, DataStore store)
        {
            this.form = form;
            this.store = store;
        }

        public string GetLibraryJson()
        {
            return store.GetLibraryJson();
        }

        public string GetChapterJson(string bookId, int chapterIndex)
        {
            return store.GetChapterJson(bookId, chapterIndex);
        }

        public string ImportNovel()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "导入本地 TXT 小说";
                dialog.Filter = "TXT 小说 (*.txt)|*.txt|所有文件 (*.*)|*.*";
                dialog.Multiselect = false;
                if (dialog.ShowDialog(form) != DialogResult.OK)
                {
                    return store.GetLibraryJson();
                }

                try
                {
                    store.ImportBook(dialog.FileName);
                    return store.GetLibraryJson();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(form, ex.Message, "导入失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return store.GetLibraryJson();
                }
            }
        }

        public string RemoveBook(string bookId)
        {
            BookEntry book = store.FindBook(bookId);
            if (book == null)
            {
                return store.GetLibraryJson();
            }

            DialogResult result = MessageBox.Show(form, "从本地书库移除《" + book.Title + "》？\r\n原始文件不会被删除。", "移除小说", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (result == DialogResult.Yes)
            {
                store.RemoveBook(bookId);
            }
            return store.GetLibraryJson();
        }

        public void SaveProgress(string bookId, int chapterIndex)
        {
            store.SaveProgress(bookId, chapterIndex);
        }

        public void SaveSettings(string json)
        {
            store.SaveSettings(json);
        }

        public void SetSafeMode(bool enabled)
        {
            form.SetSafeTitle(enabled);
        }

        public string GetDataFolder()
        {
            return store.RootDirectory;
        }

        public string GetVersion()
        {
            return Program.Version;
        }

        internal void Flush()
        {
            store.Save();
        }
    }

    internal static class ResourceLoader
    {
        public static string BuildDocument()
        {
            string html = Read("AiNovelReader.index.html");
            string css = Read("AiNovelReader.styles.css");
            string js = Read("AiNovelReader.app.js");
            return html.Replace("/*__APP_STYLES__*/", css).Replace("/*__APP_SCRIPT__*/", js);
        }

        public static string Read(string resourceName)
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException("缺少界面资源：" + resourceName);
                }
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
        }
    }

    public sealed class ReaderSettings
    {
        public List<ReaderBookmark> Bookmarks { get; set; }
        public int FontSize { get; set; }
        public double LineHeight { get; set; }
        public int ParagraphSpacing { get; set; }
        public int ContentWidth { get; set; }
        public string Theme { get; set; }
        public string SafeShortcut { get; set; }
        public string ImportShortcut { get; set; }
        public string SettingsShortcut { get; set; }
        public string PreviousShortcut { get; set; }
        public string NextShortcut { get; set; }
        public bool ContentSimulation { get; set; }
        public bool InterfaceSimulation { get; set; }
        public string InterfaceMode { get; set; }

        public ReaderSettings()
        {
            Bookmarks = new List<ReaderBookmark>();
            FontSize = 16;
            LineHeight = 1.5;
            ParagraphSpacing = 8;
            ContentWidth = 1000;
            Theme = "light";
            SafeShortcut = "Ctrl+Backquote";
            ImportShortcut = "Ctrl+KeyO";
            SettingsShortcut = "Ctrl+Comma";
            PreviousShortcut = "BracketLeft";
            NextShortcut = "BracketRight";
            ContentSimulation = false;
            InterfaceSimulation = true;
            InterfaceMode = "doubao";
        }
    }

    public sealed class ReaderBookmark
    {
        public string Id { get; set; }
        public string BookId { get; set; }
        public string Title { get; set; }
        public int Chapter { get; set; }
        public int Paragraph { get; set; }
        public double Offset { get; set; }
        public string Excerpt { get; set; }
    }

    public sealed class BookEntry
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string FileName { get; set; }
        public string EncodingName { get; set; }
        public int ContentVersion { get; set; }
        public int TextLayoutVersion { get; set; }
        public bool Pinned { get; set; }
        public int LastChapter { get; set; }
        public int LastParagraph { get; set; }
        public double LastOffset { get; set; }
        public long ProgressRevision { get; set; }
        public DateTime ImportedAt { get; set; }
    }

    public sealed class LibraryState
    {
        public List<BookEntry> Books { get; set; }
        public string CurrentBookId { get; set; }
        public int CurrentChapter { get; set; }
        public ReaderSettings Settings { get; set; }

        public LibraryState()
        {
            Books = new List<BookEntry>();
            CurrentBookId = String.Empty;
            CurrentChapter = 0;
            Settings = new ReaderSettings();
        }
    }

    internal sealed class ChapterData
    {
        public string Title;
        public string Content;
    }

    internal sealed class ParsedBook
    {
        public string EncodingName;
        public string SuggestedTitle;
        public List<ChapterData> Chapters = new List<ChapterData>();
    }

    public sealed class DataStore
    {
        private readonly string stateFile;
        private readonly string booksDirectory;
        private readonly JavaScriptSerializer serializer;
        private readonly Dictionary<string, ParsedBook> cache;
        private LibraryState state;
        private int shortcutRepairs;

        public string RootDirectory { get; private set; }
        public string LibraryIdentity
        {
            get
            {
                string path = Path.GetFullPath(RootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
                using (SHA256 hash = SHA256.Create())
                    return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(path))).Replace("-", String.Empty).ToLowerInvariant();
            }
        }

        public DataStore(string rootDirectory)
        {
            using (var operation = RuntimeLog.Begin("storage", "initialize", null))
            { try {
            RootDirectory = Path.GetFullPath(rootDirectory);
            stateFile = Path.Combine(RootDirectory, "library.json");
            booksDirectory = Path.Combine(RootDirectory, "books");
            serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = Int32.MaxValue;
            cache = new Dictionary<string, ParsedBook>();
            Directory.CreateDirectory(booksDirectory);
            state = LoadState();
            int repaired = shortcutRepairs = NormalizeShortcuts(state.Settings, true);
            if (repaired > 0) RuntimeLog.Emit("WARN", "storage", "shortcut_conflicts_repaired", "旧设置中的重复快捷键已恢复为不冲突的绑定。", RuntimeLog.Fields("count", repaired));
            operation.Success(RuntimeLog.Fields("bookCount", state.Books.Count, "formatVersion", 1, "libraryId", LibraryIdentity));
            } catch (Exception ex) { operation.Fail(ex); throw; } }
        }

        public BookEntry FindBook(string id)
        {
            return state.Books.FirstOrDefault(delegate(BookEntry book) { return book.Id == id; });
        }

        public BookEntry ImportBook(string sourcePath)
        {
            using (var operation = RuntimeLog.Begin("storage", "source_read", RuntimeLog.Fields("source", "user_selected_file")))
            { try {
            if (String.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                throw new FileNotFoundException("找不到要导入的小说文件。", sourcePath);
            }

            byte[] bytes = File.ReadAllBytes(sourcePath);
            BookEntry imported = ImportBookBytes(bytes, Path.GetFileName(sourcePath));
            operation.Success(RuntimeLog.Fields("bytes", bytes.Length, "bookId", RuntimeLog.Fingerprint(imported.Id))); return imported;
            } catch (Exception ex) { operation.Fail(ex); throw; } }
        }

        public BookEntry ImportBookBytes(byte[] bytes, string originalFileName)
        {
            using (var operation = RuntimeLog.Begin("storage", "import", RuntimeLog.Fields("bytes", bytes == null ? 0 : bytes.Length)))
            { try {
            if (bytes.Length == 0)
            {
                throw new InvalidDataException("小说文件为空。 ");
            }

            string extension = Path.GetExtension(originalFileName).ToLowerInvariant();
            if (extension != ".txt" && extension != ".epub") throw new InvalidDataException("仅支持 TXT 和 EPUB 小说。");
            string fallbackTitle = Path.GetFileNameWithoutExtension(originalFileName);
            ParsedBook parsed = extension == ".epub" ? ParseEpubImport(bytes, fallbackTitle) : BookParser.Parse(bytes, fallbackTitle);
            RuntimeLog.Emit("INFO", "parser", "parse_completed", "小说解析完成；尚未代表导入保存完成。", RuntimeLog.Fields("extension", extension, "encoding", parsed.EncodingName, "chapterCount", parsed.Chapters.Count));
            if (parsed.Chapters.Count == 0)
            {
                throw new InvalidDataException("未能从文件中读取正文。 ");
            }

            string id;
            using (SHA1 sha1 = SHA1.Create())
            {
                id = BitConverter.ToString(sha1.ComputeHash(bytes)).Replace("-", String.Empty).Substring(0, 12).ToLowerInvariant();
            }

            string storedName = id + extension;
            string destination = Path.Combine(booksDirectory, storedName);
            File.WriteAllBytes(destination, bytes);

            BookEntry entry = FindBook(id);
            if (entry != null && entry.ContentVersion < TextStructure.Version)
                MigrateContent(entry, bytes, parsed);
            if (entry == null)
            {
                entry = new BookEntry();
                entry.Id = id;
                entry.Title = parsed.SuggestedTitle;
                entry.FileName = storedName;
                entry.EncodingName = parsed.EncodingName;
                entry.ContentVersion = TextStructure.Version;
                entry.Pinned = true;
                entry.LastChapter = 0;
                entry.ImportedAt = DateTime.Now;
                state.Books.Insert(0, entry);
            }
            else
            {
                entry.Title = parsed.SuggestedTitle;
                entry.EncodingName = parsed.EncodingName;
            }

            cache[id] = parsed;
            state.CurrentBookId = id;
            state.CurrentChapter = Math.Max(0, Math.Min(entry.LastChapter, parsed.Chapters.Count - 1));
            Save();
            operation.Success(RuntimeLog.Fields("bookId", RuntimeLog.Fingerprint(id), "chapterCount", parsed.Chapters.Count, "bookCount", state.Books.Count, "stage", "committed"));
            return entry;
            } catch (Exception ex) { operation.Fail(ex); throw; } }
        }

        public void RemoveBook(string id)
        {
            using (var operation = RuntimeLog.Begin("storage", "remove", RuntimeLog.Fields("bookId", RuntimeLog.Fingerprint(id))))
            { try {
            BookEntry entry = FindBook(id);
            if (entry == null)
            {
                operation.Success(RuntimeLog.Fields("count", 0));
                return;
            }

            state.Books.Remove(entry);
            cache.Remove(id);
            string stored = Path.Combine(booksDirectory, entry.FileName);
            if (File.Exists(stored))
            {
                File.Delete(stored);
            }

            if (state.CurrentBookId == id)
            {
                state.CurrentBookId = state.Books.Count > 0 ? state.Books[0].Id : String.Empty;
                state.CurrentChapter = state.Books.Count > 0 ? state.Books[0].LastChapter : 0;
            }
            Save();
            operation.Success(RuntimeLog.Fields("count", 1, "bookCount", state.Books.Count));
            } catch (Exception ex) { operation.Fail(ex); throw; } }
        }

        public string GetLibraryJson()
        {
            EnsureCurrentBook();
            // Migration can change the current chapter and title; build the response afterwards.
            if (!String.IsNullOrEmpty(state.CurrentBookId)) GetParsedBook(state.CurrentBookId);
            List<object> books = new List<object>();
            foreach (BookEntry book in state.Books)
            {
                books.Add(new { id = book.Id, title = book.Title, pinned = book.Pinned, lastChapter = book.LastChapter, lastParagraph = book.LastParagraph, lastOffset = book.LastOffset, progressRevision = book.ProgressRevision });
            }

            List<object> chapters = new List<object>();
            if (!String.IsNullOrEmpty(state.CurrentBookId))
            {
                ParsedBook parsed = GetParsedBook(state.CurrentBookId);
                for (int i = 0; i < parsed.Chapters.Count; i++)
                {
                    chapters.Add(new { index = i, title = parsed.Chapters[i].Title });
                }
            }

            object payload = new
            {
                version = Program.Version,
                shortcutRepairs = shortcutRepairs,
                books = books,
                currentBookId = state.CurrentBookId,
                currentChapter = state.CurrentChapter,
                chapters = chapters,
                settings = state.Settings
            };
            return serializer.Serialize(payload);
        }

        public string GetChapterJson(string bookId, int chapterIndex)
        {
            using (var operation = RuntimeLog.Begin("storage", "chapter_read", RuntimeLog.Fields("bookId", RuntimeLog.Fingerprint(bookId), "chapter", chapterIndex)))
            { try {
            BookEntry book = FindBook(bookId);
            if (book == null)
            {
                operation.Success(RuntimeLog.Fields("count", 0, "stage", "empty_state"));
                return serializer.Serialize(new { title = "尚未导入小说", content = "点击左上角“导入小说”，选择本地 TXT 文件后即可开始阅读。", index = 0, count = 0 });
            }

            bool migrated = book.ContentVersion < TextStructure.Version;
            int savedChapterBeforeMigration = book.LastChapter;
            ParsedBook parsed = GetParsedBook(bookId);
            if (migrated && chapterIndex == savedChapterBeforeMigration) chapterIndex = book.LastChapter;
            int safeIndex = Math.Max(0, Math.Min(chapterIndex, parsed.Chapters.Count - 1));
            ChapterData chapter = parsed.Chapters[safeIndex];
            operation.Success(RuntimeLog.Fields("chapter", safeIndex, "count", parsed.Chapters.Count, "bytes", chapter.Content.Length));
            return serializer.Serialize(new { title = chapter.Title, content = chapter.Content, index = safeIndex, count = parsed.Chapters.Count, bookTitle = book.Title,
                lastChapter = book.LastChapter, lastParagraph = book.LastParagraph, lastOffset = book.LastOffset, progressRevision = book.ProgressRevision,
                chapters = parsed.Chapters.Select((c, i) => new { index = i, title = c.Title }).ToArray() });
            } catch (Exception ex) { operation.Fail(ex); throw; } }
        }

        public string SearchJson(string bookId, string query)
        {
            using (var operation = RuntimeLog.Begin("storage", "search", RuntimeLog.Fields("bookId", RuntimeLog.Fingerprint(bookId))))
            { try {
            string keyword = (query ?? String.Empty).Trim();
            if (keyword.Length == 0) { operation.Success(RuntimeLog.Fields("count", 0)); return serializer.Serialize(new { query = keyword, results = new object[0] }); }

            List<object> results = new List<object>();
            BookEntry selectedBook = FindBook(bookId);
            if (selectedBook == null) selectedBook = FindBook(state.CurrentBookId);
            IEnumerable<BookEntry> searchBooks = selectedBook == null ? Enumerable.Empty<BookEntry>() : new BookEntry[] { selectedBook };
            foreach (BookEntry book in searchBooks)
            {
                ParsedBook parsed = GetParsedBook(book.Id);
                for (int i = 0; i < parsed.Chapters.Count && results.Count < 120; i++)
                {
                    ChapterData chapter = parsed.Chapters[i];
                    int titleIndex = chapter.Title.IndexOf(keyword, StringComparison.CurrentCultureIgnoreCase);
                    int contentIndex = chapter.Content.IndexOf(keyword, StringComparison.CurrentCultureIgnoreCase);
                    if (titleIndex < 0 && contentIndex < 0) continue;
                    string source = contentIndex >= 0 ? chapter.Content : chapter.Title;
                    int matchIndex = contentIndex >= 0 ? contentIndex : titleIndex;
                    int start = Math.Max(0, matchIndex - 22);
                    int length = Math.Min(70, source.Length - start);
                    string snippet = source.Substring(start, length).Replace("\r", " ").Replace("\n", " ");
                    results.Add(new { bookId = book.Id, bookTitle = book.Title, index = i, title = chapter.Title, snippet = snippet });
                }
                if (results.Count >= 120) break;
            }
            operation.Success(RuntimeLog.Fields("count", results.Count)); return serializer.Serialize(new { query = keyword, results = results });
            } catch (Exception ex) { operation.Fail(ex); throw; } }
        }

        public void SaveProgress(string bookId, int chapterIndex)
        {
            SaveProgress(bookId, chapterIndex, 0, 0);
        }

        public void SaveProgress(string bookId, int chapterIndex, int paragraph, double offset)
        {
            // Trusted in-process callers retain the original API; HTTP callers must use a revision.
            SaveProgressChecked(bookId, chapterIndex, paragraph, offset, null);
        }

        public string SaveProgressChecked(string bookId, int chapterIndex, int paragraph, double offset, long? expectedRevision)
        {
            using (var operation = RuntimeLog.Begin("storage", "progress", RuntimeLog.Fields("bookId", RuntimeLog.Fingerprint(bookId))))
            { try {
            BookEntry book = FindBook(bookId);
            if (book == null)
            {
                operation.Cancel();
                return serializer.Serialize(new { ok = false, missing = true });
            }
            ParsedBook parsed = GetParsedBook(bookId);
            if (expectedRevision.HasValue && expectedRevision.Value != book.ProgressRevision)
            {
                RuntimeLog.Emit("WARN", "storage", "progress_conflict", "已拒绝旧页面覆盖较新的阅读进度。", RuntimeLog.Fields("bookId", RuntimeLog.Fingerprint(bookId), "expectedRevision", expectedRevision.Value, "actualRevision", book.ProgressRevision, "result", "cancelled"));
                operation.Cancel();
                return serializer.Serialize(new { ok = false, conflict = true, progressRevision = book.ProgressRevision });
            }
            int safe = Math.Max(0, Math.Min(chapterIndex, parsed.Chapters.Count - 1));
            int previous = book.LastChapter;
            int oldParagraph = book.LastParagraph;
            double oldOffset = book.LastOffset;
            string oldCurrentBook = state.CurrentBookId;
            int oldCurrentChapter = state.CurrentChapter;
            long oldRevision = book.ProgressRevision;
            state.CurrentBookId = bookId;
            state.CurrentChapter = safe;
            book.LastChapter = safe;
            int count = parsed.Chapters[safe].Content.Split('\n').Count(line => !String.IsNullOrWhiteSpace(line));
            book.LastParagraph = Math.Max(0, Math.Min(paragraph, Math.Max(0, count - 1)));
            book.LastOffset = Double.IsNaN(offset) || Double.IsInfinity(offset) ? 0 : Math.Max(0, Math.Min(1, offset));
            bool changed = previous != safe || oldParagraph != book.LastParagraph || oldOffset != book.LastOffset;
            if (changed) book.ProgressRevision++;
            try { if (changed || oldCurrentBook != bookId || oldCurrentChapter != safe) Save(); }
            catch { book.LastChapter = previous; book.LastParagraph = oldParagraph; book.LastOffset = oldOffset; book.ProgressRevision = oldRevision; state.CurrentBookId = oldCurrentBook; state.CurrentChapter = oldCurrentChapter; throw; }
            operation.Success(RuntimeLog.Fields("fromChapter", previous, "toChapter", safe, "paragraph", book.LastParagraph, "offset", book.LastOffset, "revision", book.ProgressRevision, "changed", changed));
            return serializer.Serialize(new { ok = true, progressRevision = book.ProgressRevision });
            } catch (Exception ex) { operation.Fail(ex); throw; } }
        }

        public void SaveSettings(string json)
        {
            using (var operation = RuntimeLog.Begin("storage", "settings", null))
            {
            try
            {
                ReaderSettings settings = serializer.Deserialize<ReaderSettings>(json);
                if (settings != null)
                {
                    settings.FontSize = Math.Max(14, Math.Min(30, settings.FontSize));
                    settings.LineHeight = Math.Max(1.3, Math.Min(2.8, settings.LineHeight));
                    settings.ParagraphSpacing = Math.Max(4, Math.Min(36, settings.ParagraphSpacing));
                    settings.ContentWidth = Math.Max(680, Math.Min(1300, settings.ContentWidth));
                    if (settings.Theme != "light" && settings.Theme != "dark" && settings.Theme != "paper" && settings.Theme != "eye")
                    {
                        settings.Theme = "light";
                    }
                    if (settings.InterfaceMode != "doubao" && settings.InterfaceMode != "deepseek" && settings.InterfaceMode != "reader")
                    {
                        settings.InterfaceMode = settings.InterfaceSimulation ? "doubao" : "reader";
                    }
                    settings.InterfaceSimulation = settings.InterfaceMode != "reader";
                    if (String.IsNullOrWhiteSpace(settings.SafeShortcut)) settings.SafeShortcut = "Ctrl+Backquote";
                    if (String.IsNullOrWhiteSpace(settings.ImportShortcut)) settings.ImportShortcut = "Ctrl+KeyO";
                    if (String.IsNullOrWhiteSpace(settings.SettingsShortcut)) settings.SettingsShortcut = "Ctrl+Comma";
                    if (String.IsNullOrWhiteSpace(settings.PreviousShortcut)) settings.PreviousShortcut = "BracketLeft";
                    if (String.IsNullOrWhiteSpace(settings.NextShortcut)) settings.NextShortcut = "BracketRight";
                    NormalizeShortcuts(settings, false);
                    var changed = RuntimeLog.Fields("fromMode", state.Settings.InterfaceMode, "toMode", settings.InterfaceMode, "fromTheme", state.Settings.Theme, "toTheme", settings.Theme, "fromFont", state.Settings.FontSize, "toFont", settings.FontSize, "bookmarkCount", settings.Bookmarks == null ? 0 : settings.Bookmarks.Count);
                    state.Settings = settings;
                    Save();
                    operation.Success(changed);
                }
                else throw new InvalidDataException("设置不能为空。");
            }
            catch (Exception ex)
            {
                operation.Fail(ex);
                throw;
            }
            }
        }

        private static int NormalizeShortcuts(ReaderSettings settings, bool repair)
        {
            string[] values = { settings.SafeShortcut, settings.ImportShortcut, settings.SettingsShortcut, settings.PreviousShortcut, settings.NextShortcut };
            string[] defaults = { "Ctrl+Backquote", "Ctrl+KeyO", "Ctrl+Comma", "BracketLeft", "BracketRight" };
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int repaired = 0;
            for (int i = 0; i < values.Length; i++)
            {
                string value = String.IsNullOrWhiteSpace(values[i]) ? defaults[i] : values[i].Trim();
                if (!used.Add(value))
                {
                    if (!repair) throw new InvalidDataException("快捷键重复，请为每个功能设置不同的快捷键。");
                    value = defaults[i];
                    int fallback = i;
                    while (!used.Add(value)) value = "Ctrl+Alt+Shift+Digit" + (fallback++).ToString();
                    repaired++;
                }
                values[i] = value;
            }
            settings.SafeShortcut = values[0]; settings.ImportShortcut = values[1]; settings.SettingsShortcut = values[2]; settings.PreviousShortcut = values[3]; settings.NextShortcut = values[4];
            return repaired;
        }

        public void Save()
        {
            using (var operation = RuntimeLog.Begin("storage", "save", null))
            { try {
            Directory.CreateDirectory(RootDirectory);
            string temp = stateFile + ".tmp";
            File.WriteAllText(temp, serializer.Serialize(state), new UTF8Encoding(false));
            if (File.Exists(stateFile))
            {
                File.Replace(temp, stateFile, stateFile + ".bak", true);
            }
            else
            {
                File.Move(temp, stateFile);
            }
            operation.Success(RuntimeLog.Fields("stage", "committed", "bookCount", state.Books.Count, "bytes", new FileInfo(stateFile).Length));
            } catch (Exception ex) { operation.Fail(ex); throw; } }
        }

        private LibraryState ReadState(string path)
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            Dictionary<string, object> fields = serializer.Deserialize<Dictionary<string, object>>(json);
            if (fields == null || !fields.ContainsKey("Books") || fields["Books"] == null || !fields.ContainsKey("Settings"))
                throw new InvalidDataException("书库结构无效。");
            LibraryState loaded = serializer.Deserialize<LibraryState>(json);
            if (loaded == null || loaded.Books == null) throw new InvalidDataException("书库结构无效。");
            if (loaded.Settings == null) loaded.Settings = new ReaderSettings();
            int before = loaded.Books.Count;
            loaded.Books = loaded.Books.Where(b => b != null && !String.IsNullOrEmpty(b.FileName) && Path.GetFileName(b.FileName) == b.FileName && File.Exists(Path.Combine(booksDirectory, b.FileName))).ToList();
            if (before != loaded.Books.Count) RuntimeLog.Emit("WARN", "storage", "missing_book_files", "书库中部分记录对应的文件缺失或路径不合法，已按现有行为过滤。", RuntimeLog.Fields("count", before - loaded.Books.Count, "bookCount", loaded.Books.Count));
            RuntimeLog.Emit("INFO", "storage", "library_read", "书库 JSON 结构已校验。", RuntimeLog.Fields("source", path == stateFile ? "primary" : "backup", "bookCount", loaded.Books.Count, "stage", "verified", "bytes", Encoding.UTF8.GetByteCount(json)));
            return loaded;
        }

        private LibraryState LoadState()
        {
            string backup = stateFile + ".bak";
            if (!File.Exists(stateFile) && !File.Exists(backup)) { RuntimeLog.Emit("INFO", "storage", "library_created", "首次启动空书库。", RuntimeLog.Fields("bookCount", 0)); return new LibraryState(); }
            try { return ReadState(stateFile); }
            catch (Exception primaryError)
            {
                RuntimeLog.Warning("storage", "primary_read_failed", primaryError, RuntimeLog.Fields("attempt", 1, "source", "primary", "result", "retrying"));
                LibraryState recovered;
                try { recovered = ReadState(backup); }
                catch (Exception backupError)
                {
                    RuntimeLog.Warning("storage", "backup_read_failed", backupError, RuntimeLog.Fields("attempt", 2, "source", "backup", "result", "failure"));
                    throw new InvalidDataException("书库及备份无法读取，已停止加载以防覆盖。请保留 library.json 和 .bak 后恢复书库。", new AggregateException(primaryError, backupError));
                }
                if (File.Exists(stateFile))
                    File.Copy(stateFile, stateFile + ".corrupt-" + Guid.NewGuid().ToString("N"), false);
                File.Copy(backup, stateFile, true);
                RuntimeLog.Emit("INFO", "storage", "library_recovered", "备份已通过结构校验并恢复主书库。", RuntimeLog.Fields("attempt", 2, "source", "backup", "result", "success", "stage", "verified", "bookCount", recovered.Books.Count));
                return recovered;
            }
        }

        private void EnsureCurrentBook()
        {
            if (state.Books.Count == 0)
            {
                state.CurrentBookId = String.Empty;
                state.CurrentChapter = 0;
                return;
            }
            if (FindBook(state.CurrentBookId) == null)
            {
                state.CurrentBookId = state.Books[0].Id;
                state.CurrentChapter = state.Books[0].LastChapter;
            }
        }

        static ParsedBook ParseEpubImport(byte[] bytes,string title) { try {return EpubBookParser.Parse(bytes,title);} catch(InvalidDataException ex){throw new InvalidDataException("EPUB 导入失败：文件可能损坏或不完整，请重新下载完整文件后重试。",ex);} }
        private ParsedBook GetParsedBook(string bookId)
        {
            ParsedBook parsed;
            if (cache.TryGetValue(bookId, out parsed))
            {
                RuntimeLog.Emit("DEBUG", "parser", "cache_hit", "复用已解析小说。", RuntimeLog.Fields("bookId", RuntimeLog.Fingerprint(bookId)));
                return parsed;
            }

            BookEntry entry = FindBook(bookId);
            if (entry == null)
            {
                throw new InvalidOperationException("小说不存在。 ");
            }

            string path = Path.Combine(booksDirectory, entry.FileName);
            var parseWatch = Stopwatch.StartNew();
            byte[] bytes = File.ReadAllBytes(path);
            parsed = Path.GetExtension(entry.FileName).Equals(".epub", StringComparison.OrdinalIgnoreCase) ? ParseEpubImport(bytes, entry.Title) : BookParser.Parse(bytes, entry.Title);
            if (entry.ContentVersion < TextStructure.Version) MigrateContent(entry, bytes, parsed);
            cache[bookId] = parsed;
            RuntimeLog.Emit("INFO", "parser", "cache_populated", "小说读取并解析完成。", RuntimeLog.Fields("bookId", RuntimeLog.Fingerprint(bookId), "bytes", bytes.Length, "chapterCount", parsed.Chapters.Count, "durationMs", parseWatch.ElapsedMilliseconds, "encoding", parsed.EncodingName, "count", cache.Count));
            return parsed;
        }

        private void MigrateContent(BookEntry entry, byte[] bytes, ParsedBook parsed)
        {
            using (var operation = RuntimeLog.Begin("storage", "content_migration", RuntimeLog.Fields("bookId", RuntimeLog.Fingerprint(entry.Id), "formatVersion", TextStructure.Version)))
            {
                string snapshot = serializer.Serialize(state);
                try
                {
                    bool epub = Path.GetExtension(entry.FileName).Equals(".epub", StringComparison.OrdinalIgnoreCase);
                    ParsedBook legacy = entry.ContentVersion == 1
                        ? TextStructure.ParseVersion1(delegate { return epub ? ParseEpubImport(bytes, entry.Title) : BookParser.Parse(bytes, entry.Title); })
                        : (epub ? EpubBookParser.ParseLegacy(bytes, entry.Title) : entry.TextLayoutVersion == 1 ? MoyuLegacyBookParser.Parse(bytes, entry.Title) : BookParser.ParseLegacy(bytes, entry.Title));
                    var mapper = new ContentPositionMapper(legacy, parsed);
                    var position = mapper.Map(entry.LastChapter, entry.LastParagraph, entry.LastOffset);
                    entry.LastChapter = position.Chapter; entry.LastParagraph = position.Paragraph; entry.LastOffset = position.Offset;
                    if (state.CurrentBookId == entry.Id) state.CurrentChapter = position.Chapter;
                    foreach (var bookmark in state.Settings.Bookmarks ?? new List<ReaderBookmark>()) if (bookmark.BookId == entry.Id)
                    {
                        var moved = mapper.Map(bookmark.Chapter, bookmark.Paragraph, bookmark.Offset);
                        bookmark.Chapter = moved.Chapter; bookmark.Paragraph = moved.Paragraph; bookmark.Offset = moved.Offset;
                        bookmark.Title = parsed.Chapters[moved.Chapter].Title;
                    }
                    entry.Title = parsed.SuggestedTitle; entry.ContentVersion = TextStructure.Version;
                    entry.ProgressRevision++;
                    // A version-specific snapshot remains intact even after normal .bak rotation.
                    string backup = stateFile + ".pre-content-v" + TextStructure.Version;
                    if (File.Exists(stateFile) && !File.Exists(backup)) File.Copy(stateFile, backup, false);
                    Save();
                    operation.Success(RuntimeLog.Fields("chapterCount", parsed.Chapters.Count, "count", mapper.Mapped, "stage", "committed"));
                    if (mapper.Unmatched > 0) RuntimeLog.Emit("WARN", "storage", "content_position_fallback", "部分旧位置未找到唯一正文锚点，已保留原书库快照供核对。", RuntimeLog.Fields("bookId", RuntimeLog.Fingerprint(entry.Id), "count", mapper.Unmatched));
                }
                catch (Exception ex) { state = serializer.Deserialize<LibraryState>(snapshot); operation.Fail(ex); throw; }
            }
        }
    }

    internal static class BookParser
    {
        private static readonly Regex ChapterRegex = new Regex(@"(?m)^\s*(第\s*[0-9一二三四五六七八九十百千万零〇两]+\s*章[^\r\n]*)\s*$", RegexOptions.Compiled);

        public static ParsedBook Parse(byte[] bytes, string fallbackTitle)
        {
            string encoding;
            string text = Decode(bytes, out encoding).Replace("\r\n", "\n").Replace("\r", "\n");
            var lines = text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).Select(line => new ReadingBlock { Text = line }).ToList();
            var chapters = TextStructure.Split(lines, false);
            if (chapters.Count == 1 && chapters[0].Title == "作品信息") chapters[0].Title = "正文";
            return new ParsedBook { EncodingName = encoding, SuggestedTitle = TextStructure.FindTitle(text, fallbackTitle), Chapters = chapters };
        }

        public static ParsedBook ParseLegacy(byte[] bytes, string fallbackTitle)
        {
            string encodingName;
            string text = Decode(bytes, out encodingName).Replace("\r\n", "\n").Replace("\r", "\n");
            ParsedBook parsed = new ParsedBook();
            parsed.EncodingName = encodingName;
            parsed.SuggestedTitle = ExtractTitle(text, fallbackTitle);

            MatchCollection matches = ChapterRegex.Matches(text);
            if (matches.Count == 0)
            {
                parsed.Chapters.Add(new ChapterData { Title = "正文", Content = CleanContent(text) });
                return parsed;
            }

            if (matches[0].Index > 0)
            {
                string preface = CleanContent(text.Substring(0, matches[0].Index));
                if (!String.IsNullOrWhiteSpace(preface))
                {
                    parsed.Chapters.Add(new ChapterData { Title = "作品信息", Content = preface });
                }
            }

            for (int i = 0; i < matches.Count; i++)
            {
                int start = matches[i].Index + matches[i].Length;
                int end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
                string title = matches[i].Groups[1].Value.Trim();
                string content = CleanContent(text.Substring(start, end - start));
                parsed.Chapters.Add(new ChapterData { Title = title, Content = content });
            }
            return parsed;
        }

        private static string Decode(byte[] bytes, out string encodingName)
        {
            if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0)
            {
                encodingName = "utf-32";
                return new UTF32Encoding(false, false, true).GetString(bytes, 4, bytes.Length - 4);
            }
            if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF)
            {
                encodingName = "utf-32BE";
                return new UTF32Encoding(true, false, true).GetString(bytes, 4, bytes.Length - 4);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                encodingName = "utf-16";
                return new UnicodeEncoding(false, false, true).GetString(bytes, 2, bytes.Length - 2);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                encodingName = "utf-16BE";
                return new UnicodeEncoding(true, false, true).GetString(bytes, 2, bytes.Length - 2);
            }
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                encodingName = "utf-8";
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            }

            try
            {
                string utf8 = new UTF8Encoding(false, true).GetString(bytes);
                encodingName = "utf-8";
                return utf8;
            }
            catch (DecoderFallbackException)
            {
                encodingName = "gb18030";
                return Encoding.GetEncoding("GB18030").GetString(bytes);
            }
        }

        private static string ExtractTitle(string text, string fallback)
        {
            Match match = Regex.Match(text, @"《([^》\r\n]{1,100})》");
            return match.Success ? match.Groups[1].Value.Trim() : fallback;
        }

        private static string CleanContent(string content)
        {
            string[] raw = content.Split('\n');
            List<string> lines = new List<string>();
            foreach (string item in raw)
            {
                string line = item.Trim();
                if (line.Length > 0)
                {
                    lines.Add(line);
                }
            }
            return String.Join("\n", lines.ToArray());
        }
    }

    internal static class SelfTest
    {
        public static int Run(string samplePath, string outputDirectory)
        {
            List<string> report = new List<string>();
            report.Add("AI小说阅读器 " + Program.Version + " 自检");
            report.Add("TIME=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            string tempRoot = Path.Combine(Path.GetTempPath(), "AiNovelReader-SelfTest-" + Guid.NewGuid().ToString("N"));

            try
            {
                Assert(File.Exists(samplePath), "测试小说文件存在", report);
                DataStore store = new DataStore(tempRoot);
                BookEntry imported = store.ImportBook(samplePath);
                string libraryJson = store.GetLibraryJson();
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = Int32.MaxValue;
                Dictionary<string, object> library = serializer.Deserialize<Dictionary<string, object>>(libraryJson);
                ICollection chapters = (ICollection)library["chapters"];

                Assert(imported.EncodingName == "gb18030", "GB18030 自动识别", report);
                Assert(imported.Title.Contains("我家老婆来自一千年前"), "书名提取", report);
                Assert(chapters.Count >= 380, "章节解析 >= 380，实际 " + chapters.Count, report);

                string chapterJson = store.GetChapterJson(imported.Id, 1);
                Assert(chapterJson.Contains("第1章") && chapterJson.Contains("大雨"), "首章正文读取", report);

                byte[] epubBytes = CreateSampleEpub();
                BookEntry epub = store.ImportBookBytes(epubBytes, "EPUB测试书.epub");
                string epubChapter = store.GetChapterJson(epub.Id, 1);
                Assert(epub.EncodingName == "epub" && epubChapter.Contains("第二章") && epubChapter.Contains("浏览器阅读"), "EPUB 导入与章节读取", report);

                ReaderSettings settings = new ReaderSettings();
                settings.FontSize = 22;
                settings.LineHeight = 2.1;
                settings.ParagraphSpacing = 20;
                settings.ContentWidth = 1060;
                settings.Theme = "paper";
                settings.SafeShortcut = "Alt+KeyQ";
                settings.InterfaceMode = "deepseek";
                settings.Bookmarks.Add(new ReaderBookmark { Id = "self-test", BookId = imported.Id, Chapter = 1, Paragraph = 3, Offset = 0.25, Title = "测试书签", Excerpt = "恢复位置" });
                store.SaveSettings(serializer.Serialize(settings));
                DataStore reloaded = new DataStore(tempRoot);
                string reloadedJson = reloaded.GetLibraryJson();
                Assert(reloadedJson.Contains("\"Bookmarks\":[") && reloadedJson.Contains("\"Paragraph\":3") && reloadedJson.Contains("\"Offset\":0.25"), "书签与段落位置重启持久化", report);
                Assert(reloadedJson.Contains("\"FontSize\":22") && reloadedJson.Contains("\"Theme\":\"paper\"") && reloadedJson.Contains("\"SafeShortcut\":\"Alt+KeyQ\"") && reloadedJson.Contains("\"InterfaceMode\":\"deepseek\""), "设置、快捷键与三种界面持久化", report);

                string ui = ResourceLoader.BuildDocument();
                Assert(ui.Contains("历史对话") && ui.Contains("bookList") && ui.Contains("chapterList") && ui.Contains("界面切换"), "豆包式主布局资源", report);
                Assert(ui.Contains("加大字号") && ui.Contains("减小字号") && ui.Contains("增加段距") && ui.Contains("减小行距"), "底部阅读设置工具栏", report);
                Assert(ui.Contains("/api/import") && ui.Contains("/api/settings") && ui.Contains("/api/search") && ui.Contains("novelFileInput"), "浏览器本地接口调用与全文搜索", report);
                Assert(ui.Contains(".epub") && ui.Contains("shortcut-input") && ui.Contains("SafeShortcut"), "EPUB 与快捷键界面资源", report);
                Assert(ui.Contains("commandInput") && ui.Contains("下一章") && ui.Contains("theme-eye") && ui.Contains("仿真"), "输入指令与四种背景模式", report);
                Assert(ui.Contains("contentSimulation") && ui.Contains("interfaceSimulation") && ui.Contains("DeepSeek") && ui.Contains("下载电脑版"), "内容模拟与豆包界面模拟", report);
                Assert(ui.Contains("录音转写") && ui.Contains("音乐生成") && ui.Contains("doubao-nav-icon") && ui.Contains("history-scroll"), "豆包导航工具图标与章节滚动条", report);
                Assert(ui.Contains("deepseek-simulation") && ui.Contains("deepseekNewChat") && ui.Contains("深度思考") && ui.Contains("智能搜索") && ui.Contains("给 DeepSeek 发送消息"), "DeepSeek 首页与阅读界面资源", report);
                Assert(ui.Contains("setInterfaceMode('deepseek')") && ui.Contains("InterfaceMode") && ui.Contains("deepseekSettingsButton"), "DeepSeek 功能迁移与界面切换", report);
                Assert(ui.Contains("width:780px") && ui.Contains("width:260px") && ui.Contains("ContentWidth*.78"), "DeepSeek 页面比例校正", report);
                Assert(ui.Contains("pixelDoubaoMark") && ui.Contains("pixelDeepseekMark") && ui.Contains("shape-rendering:crispEdges"), "原创像素品牌联想图标", report);
                Assert(typeof(LocalHttpServer).GetMethod("Start") != null && typeof(DataStore).GetMethod("ImportBookBytes") != null, "本地服务与导入接口", report);

                report.Add("RESULT=PASS");
                File.WriteAllLines(Path.Combine(outputDirectory, "NovelReader-" + Program.Version + "-self-test.txt"), report.ToArray(), Encoding.UTF8);
                return 0;
            }
            catch (Exception ex)
            {
                report.Add("RESULT=FAIL");
                report.Add(ex.ToString());
                try { File.WriteAllLines(Path.Combine(outputDirectory, "NovelReader-" + Program.Version + "-self-test.txt"), report.ToArray(), Encoding.UTF8); } catch { }
                return 1;
            }
            finally
            {
                try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { }
            }
        }

        private static void Assert(bool condition, string name, List<string> report)
        {
            if (!condition)
            {
                throw new InvalidOperationException("FAIL: " + name);
            }
            report.Add("PASS: " + name);
        }

        private static byte[] CreateSampleEpub()
        {
            using (MemoryStream memory = new MemoryStream())
            {
                using (ZipArchive zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
                {
                    WriteZipEntry(zip, "META-INF/container.xml", "<?xml version=\"1.0\"?><container xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
                    WriteZipEntry(zip, "OEBPS/content.opf", "<?xml version=\"1.0\"?><package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>EPUB测试书</dc:title></metadata><manifest><item id=\"c1\" href=\"c1.xhtml\" media-type=\"application/xhtml+xml\"/><item id=\"c2\" href=\"c2.xhtml\" media-type=\"application/xhtml+xml\"/></manifest><spine><itemref idref=\"c1\"/><itemref idref=\"c2\"/></spine></package>");
                    WriteZipEntry(zip, "OEBPS/c1.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><h1>第一章 开始</h1><p>这是 EPUB 第一章。</p></body></html>");
                    WriteZipEntry(zip, "OEBPS/c2.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><h1>第二章 浏览器</h1><p>浏览器阅读测试成功。</p></body></html>");
                }
                return memory.ToArray();
            }
        }

        private static void WriteZipEntry(ZipArchive zip, string path, string content)
        {
            ZipArchiveEntry entry = zip.CreateEntry(path, CompressionLevel.Optimal);
            using (StreamWriter writer = new StreamWriter(entry.Open(), new UTF8Encoding(false))) writer.Write(content);
        }
    }
}
