using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
namespace AiNovelReader {
    internal static class MoyuLegacyBookParser
    {
        private static readonly Regex ChapterRegex = new Regex(@"(?im)^[ \t]*(第[ \t]*[0-9一二三四五六七八九十百千万零〇两]+[ \t]*章[^\r\n]*|(?:chapter|book|part|act|scene)[ \t]+(?:[0-9]+|[ivxlcdm]+|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve)(?:[.：:—–-][^\r\n]*|[ \t]+[^\r\n]*)?)[ \t]*$", RegexOptions.Compiled);

        private static readonly Regex LegacyChapterRegex = new Regex(@"(?m)^\s*(第\s*[0-9一二三四五六七八九十百千万零〇两]+\s*章[^\r\n]*)\s*$", RegexOptions.Compiled);
        public static ParsedBook Parse(byte[] bytes,string fallbackTitle) { return ParseCore(bytes,fallbackTitle,true); }
        public static ParsedBook ParseLegacy(byte[] bytes,string fallbackTitle) { return ParseCore(bytes,fallbackTitle,false); }
        static ParsedBook ParseCore(byte[] bytes, string fallbackTitle,bool extended)
        {
            string encodingName;
            string text = Decode(bytes, out encodingName).Replace("\r\n", "\n").Replace("\r", "\n");
            ParsedBook parsed = new ParsedBook();
            parsed.EncodingName = encodingName;
            parsed.SuggestedTitle = ExtractTitle(text, fallbackTitle);

            var matches = (extended ? ChapterRegex : LegacyChapterRegex).Matches(text).Cast<Match>().ToList();
            if(extended) matches = matches.Where(m => !(text[m.Index]==' '||text[m.Index]=='\t') || !Regex.IsMatch(m.Groups[1].Value,@"^(chapter|book|part|act|scene)\b",RegexOptions.IgnoreCase) || !matches.Any(n => n.Index>m.Index && text[n.Index]!=' ' && text[n.Index]!='\t' && String.Equals(Regex.Match(n.Groups[1].Value,@"^\w+\s+[\w-]+").Value,Regex.Match(m.Groups[1].Value,@"^\w+\s+[\w-]+").Value,StringComparison.OrdinalIgnoreCase))).ToList();
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
}