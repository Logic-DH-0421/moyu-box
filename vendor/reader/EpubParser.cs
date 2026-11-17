using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AiNovelReader
{
    internal static class EpubBookParser
    {
        public static ParsedBook Parse(byte[] bytes, string fallbackTitle)
        {
            return StructuredEpubParser.Parse(bytes, fallbackTitle);
        }

        // Retained only to relocate pre-upgrade progress/bookmarks against their old text layout.
        public static ParsedBook ParseLegacy(byte[] bytes, string fallbackTitle)
        {
            ParsedBook result = new ParsedBook();
            result.EncodingName = "epub";
            result.SuggestedTitle = fallbackTitle;
            using (MemoryStream memory = new MemoryStream(bytes, false))
            using (ZipArchive archive = new ZipArchive(memory, ZipArchiveMode.Read, false))
            {
                ZipArchiveEntry containerEntry = FindEntry(archive, "META-INF/container.xml");
                if (containerEntry == null) throw new InvalidDataException("EPUB 缺少 META-INF/container.xml。");
                XDocument container = LoadXml(containerEntry);
                XElement rootFile = container.Descendants().FirstOrDefault(delegate(XElement e) { return e.Name.LocalName == "rootfile"; });
                if (rootFile == null || rootFile.Attribute("full-path") == null) throw new InvalidDataException("EPUB 未声明 OPF 文件。");
                string opfPath = NormalizePath(rootFile.Attribute("full-path").Value);
                ZipArchiveEntry opfEntry = FindEntry(archive, opfPath);
                if (opfEntry == null) throw new InvalidDataException("EPUB 的 OPF 文件不存在。");
                XDocument opf = LoadXml(opfEntry);
                XElement titleNode = opf.Descendants().FirstOrDefault(delegate(XElement e) { return e.Name.LocalName == "title"; });
                if (titleNode != null && !String.IsNullOrWhiteSpace(titleNode.Value)) result.SuggestedTitle = CleanInline(titleNode.Value);

                Dictionary<string, string> manifest = new Dictionary<string, string>();
                foreach (XElement item in opf.Descendants().Where(delegate(XElement e) { return e.Name.LocalName == "item"; }))
                {
                    XAttribute id = item.Attribute("id");
                    XAttribute href = item.Attribute("href");
                    if (id != null && href != null) manifest[id.Value] = href.Value;
                }
                string basePath = opfPath.Contains("/") ? opfPath.Substring(0, opfPath.LastIndexOf('/') + 1) : String.Empty;
                int number = 1;
                foreach (XElement itemRef in opf.Descendants().Where(delegate(XElement e) { return e.Name.LocalName == "itemref"; }))
                {
                    XAttribute idRef = itemRef.Attribute("idref");
                    if (idRef == null || !manifest.ContainsKey(idRef.Value)) continue;
                    string href = manifest[idRef.Value];
                    int hash = href.IndexOf('#');
                    if (hash >= 0) href = href.Substring(0, hash);
                    href = Uri.UnescapeDataString(href);
                    string entryPath = NormalizePath(basePath + href);
                    if (entryPath.EndsWith("titlepage.xhtml", StringComparison.OrdinalIgnoreCase)) continue;
                    string shortName = Path.GetFileName(entryPath).ToLowerInvariant();
                    if (shortName == "wenyuan.html") continue;
                    ZipArchiveEntry chapterEntry = FindEntry(archive, entryPath);
                    if (chapterEntry == null) continue;
                    string html = ReadText(chapterEntry);
                    string content = ExtractText(html);
                    if (String.IsNullOrWhiteSpace(content)) continue;
                    string title = ExtractHeading(html);
                    if (shortName == "zzsm.html") title = "版权声明";
                    else if (shortName == "start.html") title = "上架感言";
                    else if (shortName == "intro.html") title = "作品简介";
                    if (Regex.IsMatch(title ?? String.Empty, @"^[a-z0-9_\-]{5,}$", RegexOptions.IgnoreCase)) title = String.Empty;
                    string[] contentLines = content.Split('\n');
                    if (String.IsNullOrWhiteSpace(title) && contentLines.Length > 0 && contentLines[0].Length <= 48) title = contentLines[0];
                    if (String.IsNullOrWhiteSpace(title)) title = "第" + number + "章";
                    if (contentLines.Length > 1 && CleanInline(contentLines[0]) == CleanInline(title)) content = String.Join("\n", contentLines.Skip(1).ToArray());
                    if (String.Equals(title, "Cover", StringComparison.OrdinalIgnoreCase) || String.IsNullOrWhiteSpace(content)) continue;
                    result.Chapters.Add(new ChapterData { Title = title, Content = content });
                    number++;
                }
            }
            if (result.Chapters.Count == 0) throw new InvalidDataException("EPUB 中没有可读取的正文。");
            return result;
        }

        private static ZipArchiveEntry FindEntry(ZipArchive archive, string path)
        {
            string wanted = NormalizePath(path);
            return archive.Entries.FirstOrDefault(delegate(ZipArchiveEntry e) { return String.Equals(NormalizePath(e.FullName), wanted, StringComparison.OrdinalIgnoreCase); });
        }

        private static string NormalizePath(string path)
        {
            List<string> parts = new List<string>();
            foreach (string part in path.Replace('\\', '/').Split('/'))
            {
                if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
                else if (part.Length > 0 && part != ".") parts.Add(part);
            }
            return String.Join("/", parts.ToArray());
        }

        private static XDocument LoadXml(ZipArchiveEntry entry)
        {
            using (Stream stream = entry.Open()) return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }

        private static string ReadText(ZipArchiveEntry entry)
        {
            using (Stream stream = entry.Open())
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true)) return reader.ReadToEnd();
        }

        private static string ExtractHeading(string html)
        {
            string body = ExtractBody(html);
            Match match = Regex.Match(body, @"<(h1|h2|h3)[^>]*>(.*?)</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!match.Success) match = Regex.Match(body, @"<[^>]+class=""[^""]*(chapter-title|vol-title|surf-title)[^""]*""[^>]*>(.*?)</[^>]+>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            string value = match.Success ? match.Groups[match.Groups.Count - 1].Value : String.Empty;
            value = Regex.Replace(value, @"<br\s*/?>", " ", RegexOptions.IgnoreCase);
            return CleanInline(Regex.Replace(value, "<[^>]+>", " "));
        }

        private static string ExtractText(string html)
        {
            string value = ExtractBody(html);
            value = Regex.Replace(value, @"<(script|style)[^>]*>.*?</\1>", String.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            // Keep heading text; Parse removes only the leading line used as the chapter title.
            value = Regex.Replace(value, @"<h[1-6]\b[^>]*>", "\n", RegexOptions.IgnoreCase);
            value = Regex.Replace(value, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            value = Regex.Replace(value, @"</(p|div|li|h1|h2|h3|h4|h5|h6|section|article)\s*>", "\n", RegexOptions.IgnoreCase);
            value = Regex.Replace(value, @"<li[^>]*>", "• ", RegexOptions.IgnoreCase);
            value = Regex.Replace(value, "<[^>]+>", " ");
            value = WebUtility.HtmlDecode(value).Replace("\r\n", "\n").Replace("\r", "\n");
            List<string> lines = new List<string>();
            foreach (string raw in value.Split('\n'))
            {
                string line = Regex.Replace(raw, @"[\t\u00a0 ]+", " ").Trim();
                if (line.Length > 0) lines.Add(line);
            }
            return String.Join("\n", lines.ToArray());
        }

        private static string ExtractBody(string html)
        {
            Match body = Regex.Match(html, @"<body[^>]*>(.*?)</body>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return body.Success ? body.Groups[1].Value : html;
        }

        private static string CleanInline(string value)
        {
            return Regex.Replace(WebUtility.HtmlDecode(value ?? String.Empty), @"\s+", " ").Trim();
        }
    }
}
