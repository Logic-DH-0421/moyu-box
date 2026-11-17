using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AiNovelReader
{
    internal static class StructuredEpubParser
    {
        private static string Attribute(string tag, string name)
        {
            Match match = Regex.Match(tag, @"\b" + Regex.Escape(name) + @"\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase);
            return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value) : null;
        }
        private static string Resolve(string baseFile, string href)
        {
            if (String.IsNullOrEmpty(href)) return baseFile;
            Uri resolved;
            var basis = new Uri("https://epub.invalid/" + baseFile);
            if (!Uri.TryCreate(basis, href, out resolved) || resolved.Host != "epub.invalid") return null;
            return Uri.UnescapeDataString(resolved.AbsolutePath.TrimStart('/')) + Uri.UnescapeDataString(resolved.Fragment);
        }
        private static string Read(ZipArchiveEntry entry)
        {
            using (var reader = new StreamReader(entry.Open(), Encoding.UTF8, true)) return reader.ReadToEnd();
        }
        private static XDocument Xml(ZipArchiveEntry entry)
        {
            using (var stream = entry.Open())
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null })) return XDocument.Load(reader);
        }
        private static string Clean(string html) { return Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", String.Empty)), @"\s+", " ").Trim(); }

        public static ParsedBook Parse(byte[] bytes, string fallback)
        {
            using (var memory = new MemoryStream(bytes, false))
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Read, false))
            {
                var entries = zip.Entries.ToDictionary(e => e.FullName.Replace('\\', '/'), e => e, StringComparer.Ordinal);
                ZipArchiveEntry container;
                if (!entries.TryGetValue("META-INF/container.xml", out container)) throw new InvalidDataException("EPUB 缺少 META-INF/container.xml。");
                var root = Xml(container).Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile");
                string opfPath = root == null ? null : (string)root.Attribute("full-path");
                ZipArchiveEntry opfEntry;
                if (opfPath == null || !entries.TryGetValue(opfPath, out opfEntry)) throw new InvalidDataException("EPUB 的 OPF 文件不存在。");
                var opf = Xml(opfEntry);
                var manifest = opf.Descendants().Where(e => e.Name.LocalName == "item" && e.Attribute("id") != null && e.Attribute("href") != null).ToDictionary(e => (string)e.Attribute("id"), e => e);
                var nav = new Dictionary<string, string>(StringComparer.Ordinal);
                var navigationDocuments = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in manifest.Values)
                {
                    string path = Resolve(opfPath, (string)item.Attribute("href"));
                    ZipArchiveEntry entry;
                    if (path == null || !entries.TryGetValue(path, out entry)) continue;
                    if (((string)item.Attribute("properties") ?? "").Split(' ').Contains("nav"))
                    {
                        navigationDocuments.Add(path);
                        string document = Read(entry);
                        // Restrict to the table of contents; landmarks/page lists are not chapters.
                        Match toc = Regex.Match(document, @"<nav\b[^>]*(?:epub:type|role)\s*=\s*['""][^'""]*(?:toc|doc-toc)[^'""]*['""][^>]*>(.*?)</nav>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                        if (toc.Success) foreach (Match a in Regex.Matches(toc.Groups[1].Value, @"<a\b([^>]*)>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
                        {
                            string target = Resolve(path, Attribute(a.Groups[1].Value, "href")), label = Clean(a.Groups[2].Value);
                            if (target != null && label.Length > 0 && !nav.ContainsKey(target)) nav[target] = label;
                        }
                    }
                    if ((string)item.Attribute("media-type") == "application/x-dtbncx+xml")
                        foreach (var point in Xml(entry).Descendants().Where(e => e.Name.LocalName == "navPoint"))
                        {
                            var source = point.Elements().FirstOrDefault(e => e.Name.LocalName == "content");
                            var labelElement = point.Elements().FirstOrDefault(e => e.Name.LocalName == "navLabel");
                            string target = source == null ? null : Resolve(path, (string)source.Attribute("src"));
                            if (target != null && labelElement != null && !String.IsNullOrWhiteSpace(labelElement.Value) && !nav.ContainsKey(target)) nav[target] = Clean(labelElement.Value);
                        }
                }
                var blocks = new List<ReadingBlock>();
                foreach (var itemref in opf.Descendants().Where(e => e.Name.LocalName == "itemref"))
                {
                    XElement item;
                    if (!manifest.TryGetValue((string)itemref.Attribute("idref") ?? "", out item)) throw new InvalidDataException("EPUB 阅读顺序引用的文件未声明。");
                    string path = Resolve(opfPath, (string)item.Attribute("href"));
                    ZipArchiveEntry entry;
                    if (path == null || !entries.TryGetValue(path, out entry)) throw new InvalidDataException("EPUB 正文文件缺失，未导入不完整内容。");
                    if (navigationDocuments.Contains(path) || path.EndsWith("titlepage.xhtml", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).Equals("wenyuan.html", StringComparison.OrdinalIgnoreCase)) continue;
                    var documentBlocks = Extract(Read(entry), path, nav);
                    if (documentBlocks.Count > 0)
                    {
                        documentBlocks[0].DocumentStart = true;
                        string documentTitle;
                        if (nav.TryGetValue(path, out documentTitle)) documentBlocks[0].NavigationTitle = documentTitle;
                        // A document's first real heading remains useful even without a nav file.
                        var first = documentBlocks.FirstOrDefault(b => b.HeadingLevel > 0 && b.Text.Length < 120);
                        if (first != null && !first.Text.StartsWith("The Project Gutenberg", StringComparison.OrdinalIgnoreCase) && !TextStructure.IsHeading(first.Text) && first.HeadingLevel <= 2 && documentBlocks.IndexOf(first) == 0 && first.NavigationTitle == null)
                            first.NavigationTitle = first.Text;
                        blocks.AddRange(documentBlocks);
                    }
                }
                var title = opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "title");
                var result = new ParsedBook { SuggestedTitle = title == null || String.IsNullOrWhiteSpace(title.Value) ? fallback : Clean(title.Value), EncodingName = "epub" };
                result.Chapters = TextStructure.Split(blocks, true);
                if (result.Chapters.Count == 0) throw new InvalidDataException("EPUB 中没有可读取的正文。");
                return result;
            }
        }

        private static List<ReadingBlock> Extract(string html, string path, Dictionary<string, string> navigation)
        {
            Match body = Regex.Match(html, @"<body\b[^>]*>(.*?)</body>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            string value = body.Success ? body.Groups[1].Value : html;
            value = Regex.Replace(value, @"<!--.*?-->|<(script|style)\b[^>]*>.*?</\1>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var result = new List<ReadingBlock>();
            var text = new StringBuilder();
            int heading = 0, pre = 0;
            string pendingTitle = null;
            bool dropCap = false;
            Action flush = delegate {
                if (dropCap && text.Length <= 2) return;
                string line = text.ToString().Trim();
                if (line.Length > 0) { result.Add(new ReadingBlock { Text = line, HeadingLevel = heading, NavigationTitle = pendingTitle }); pendingTitle = null; }
                text.Clear(); dropCap = false;
            };
            foreach (Match token in Regex.Matches(value, @"<[^>]+>|[^<]+", RegexOptions.Singleline))
            {
                string part = token.Value;
                if (part[0] == '<')
                {
                    Match tag = Regex.Match(part, @"^<\s*(/?)\s*([a-zA-Z0-9:]+)");
                    if (!tag.Success) continue;
                    bool close = tag.Groups[1].Length > 0;
                    string name = tag.Groups[2].Value.ToLowerInvariant();
                    if (!close)
                    {
                        string id = Attribute(part, "id") ?? Attribute(part, "name"), label;
                        if (id != null && navigation.TryGetValue(path + "#" + id, out label)) { flush(); pendingTitle = label; }
                    }
                    if (Regex.IsMatch(name, @"^(p|div|li|h[1-6]|section|article|header|footer|blockquote|pre|tr|dt|dd)$")) flush();
                    if (name == "br") flush();
                    if (name == "pre") pre += close ? -1 : 1;
                    if (Regex.IsMatch(name, "^h[1-6]$")) heading = close ? 0 : name[1] - '0';
                    if (name == "li" && !close) text.Append("• ");
                    if (name == "td" || name == "th") { if (text.Length > 0) text.Append(" "); }
                    if (name == "img" && !close)
                    {
                        string alt = Attribute(part, "alt");
                        if (!String.IsNullOrWhiteSpace(alt))
                        {
                            bool initial = text.Length == 0 && Regex.IsMatch(alt, @"^\p{L}{1,2}$");
                            text.Append(initial ? alt : "【图片：" + alt.Trim() + "】");
                            dropCap = initial;
                        }
                    }
                }
                else
                {
                    part = WebUtility.HtmlDecode(part).Replace("\r\n", "\n").Replace('\r', '\n');
                    string[] lines = part.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i];
                        bool meaningfulBreak = pre > 0 || (i > 0 && (TextStructure.IsHeading(line.Trim()) || Regex.IsMatch(line, @"^(?:\u3000+|[ \t]{2,})\S"))) || (i > 0 && TextStructure.IsHeading(lines[i - 1].Trim()));
                        if (meaningfulBreak) flush();
                        if (i > 0 && !meaningfulBreak && text.Length > 0 && !dropCap && text[text.Length - 1] != ' ') text.Append(' ');
                        string normalized = Regex.Replace(line, @"[\t\u00a0\u3000 ]+", " ");
                        if (dropCap) normalized = normalized.TrimStart();
                        text.Append(normalized);
                        if (!String.IsNullOrWhiteSpace(normalized)) dropCap = false;
                    }
                }
            }
            dropCap = false; flush();
            return result;
        }
    }
}
