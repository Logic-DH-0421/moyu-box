using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AiNovelReader
{
    internal sealed class ReadingBlock
    {
        public string Text = String.Empty;
        public string NavigationTitle;
        public int HeadingLevel;
        public bool DocumentStart;
    }

    internal static class TextStructure
    {
        public const int Version = 2;
        [ThreadStatic] private static bool version1Headings;

        internal static ParsedBook ParseVersion1(Func<ParsedBook> parse)
        {
            bool previous = version1Headings;
            try { version1Headings = true; return parse(); }
            finally { version1Headings = previous; }
        }
        private static readonly Regex ChineseHeading = new Regex(@"^第[ \t\u3000]*([0-9０-９一二三四五六七八九十百千万零〇○两兩廿卅壹贰叁肆伍陆柒捌玖拾佰仟]+)[ \t\u3000]*([章回节節卷部集篇])([^\r\n]{0,100})$", RegexOptions.Compiled);
        private static readonly Regex EnglishHeading = new Regex(@"^(CHAPTER|BOOK|PART|ACT|SCENE)[ \t]+([0-9]{1,6}|[IVXLCDM]{1,12}|ONE|TWO|THREE|FOUR|FIVE|SIX|SEVEN|EIGHT|NINE|TEN|ELEVEN|TWELVE)(?:[.\s:：\-–—]+([^\r\n]{0,100}))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool IsHeading(string line)
        {
            string key;
            return HeadingKey(line, out key);
        }

        private static bool HeadingKey(string line, out string key)
        {
            line = (line ?? String.Empty).Trim();
            Match match = ChineseHeading.Match(line);
            string suffix = match.Groups[3].Value;
            // A colon immediately after the chapter marker is a title separator, not prose.
            // Keep the old rule available when mapping version-1 reading positions.
            bool hasSeparator = !version1Headings && Regex.IsMatch(suffix, @"^\s*[:：]");
            if (hasSeparator) suffix = Regex.Replace(suffix, @"^\s*[:：]\s*", "");
            if (match.Success && !Regex.IsMatch(suffix, "[。．；;：“”「」！？?]") && (hasSeparator || !Regex.IsMatch(suffix, "^[中了里裡时時也的后後前之]")))
            {
                key = match.Groups[1].Value + match.Groups[2].Value;
                return true;
            }
            match = EnglishHeading.Match(line);
            key = match.Success ? match.Groups[1].Value.ToUpperInvariant() + match.Groups[2].Value.ToUpperInvariant() : null;
            return match.Success;
        }

        public static List<ChapterData> Split(IList<ReadingBlock> blocks, bool epub)
        {
            // Some plain-text-derived EPUBs put the first narrative word on the title line.
            // Keep that text as body instead of losing it with the extracted heading.
            var prepared = new List<ReadingBlock>();
            foreach (var block in blocks)
            {
                Match narrative = Regex.Match(block.Text, @"[ \t\u3000]+(?=話說|话说|卻說|却说)|[ \t\u3000]+(?=[話话卻却]$)");
                if (narrative.Success && ChineseHeading.IsMatch(block.Text) && narrative.Index > 4)
                {
                    prepared.Add(new ReadingBlock { Text = block.Text.Substring(0, narrative.Index).Trim(), HeadingLevel = block.HeadingLevel, NavigationTitle = block.NavigationTitle, DocumentStart = block.DocumentStart });
                    prepared.Add(new ReadingBlock { Text = block.Text.Substring(narrative.Index).Trim() });
                }
                else prepared.Add(block);
            }
            blocks = prepared;
            var starts = new SortedDictionary<int, string>();
            var keys = new Dictionary<int, string>();
            string activeHeadingKey = null;
            int activeHeadingLevel = 0;
            for (int i = 0; i < blocks.Count; i++)
            {
                string key;
                bool heading = HeadingKey(blocks[i].Text, out key);
                if (heading && blocks[i].HeadingLevel > activeHeadingLevel && activeHeadingLevel > 0 && key == activeHeadingKey && blocks[i].NavigationTitle == null) heading = false;
                bool explicitHeading = blocks[i].NavigationTitle != null || (epub && blocks[i].HeadingLevel == 1 && blocks[i].Text.Length <= 120);
                if (heading || explicitHeading)
                {
                    starts[i] = blocks[i].NavigationTitle ?? blocks[i].Text;
                    if (heading) keys[i] = key;
                    if (heading && blocks[i].HeadingLevel > 0) { activeHeadingKey = key; activeHeadingLevel = blocks[i].HeadingLevel; }
                }
            }
            // A contents list is not a sequence of empty chapters. Retain it in front matter.
            int[] candidates = starts.Keys.ToArray();
            for (int n = 0; n < candidates.Length - 1; n++)
            {
                int i = candidates[n];
                if (blocks[i].HeadingLevel > 0 || blocks[i].NavigationTitle != null) continue;
                string key;
                if (!keys.TryGetValue(i, out key)) continue;
                bool repeatedLater = keys.Any(p => p.Key > i && p.Value == key);
                int textBetween = 0;
                for (int j = i + 1; j < candidates[n + 1]; j++) textBetween += blocks[j].Text.Length;
                if (repeatedLater && textBetween < 160) starts.Remove(i);
            }

            if (!epub && starts.Count == 0)
                return new List<ChapterData> { new ChapterData { Title = "正文", Content = String.Join("\n", blocks.Select(b => b.Text).ToArray()) } };

            var result = new List<ChapterData>();
            var content = new List<string>();
            string title = "作品信息";
            bool logical = starts.Count > 0;
            for (int i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                string nextTitle;
                bool begin = starts.TryGetValue(i, out nextTitle);
                if (!logical && (i == 0 || block.DocumentStart))
                {
                    begin = true;
                    nextTitle = block.Text.Length <= 48 ? block.Text : "正文 " + (result.Count + 1);
                }
                if (begin)
                {
                    Add(result, title, content);
                    content.Clear();
                    title = nextTitle.Trim();
                    // Plain Gutenberg TXT places a short subtitle below "CHAPTER I.".
                    if (Regex.IsMatch(block.Text, @"^(?i:CHAPTER|BOOK|PART)\s+[0-9IVXLCDM]+\.$") && i + 1 < blocks.Count)
                    {
                        string subtitle = blocks[i + 1].Text;
                        if (subtitle.Length > 0 && subtitle.Length <= 80 && !IsHeading(subtitle) && !Regex.IsMatch(subtitle, "[。！？.!?;；:\"]") && !blocks[i + 1].DocumentStart)
                        {
                            if (title == block.Text) title += " " + subtitle;
                            i++;
                        }
                    }
                    // A heading-only item is still readable; do not erase the sole content.
                    if (i + 1 == blocks.Count || starts.ContainsKey(i + 1)) content.Add(block.Text);
                    else if (block.Text != nextTitle && !IsHeading(block.Text) && block.HeadingLevel == 0) content.Add(block.Text);
                }
                else if (!String.IsNullOrWhiteSpace(block.Text)) content.Add(block.Text);
            }
            Add(result, title, content);
            if (result.Count == 0 && blocks.Count > 0)
                result.Add(new ChapterData { Title = "正文", Content = String.Join("\n", blocks.Select(b => b.Text).ToArray()) });
            return result;
        }

        private static void Add(List<ChapterData> result, string title, List<string> lines)
        {
            if (lines.Count > 0) result.Add(new ChapterData { Title = title, Content = String.Join("\n", lines.ToArray()) });
        }

        public static string FindTitle(string text, string fallback)
        {
            // Only trust explicit metadata or a standalone opening title, never a quotation in prose.
            string head = text.Substring(0, Math.Min(text.Length, 8192));
            Match metadata = Regex.Match(head, @"(?im)^[ \t]*(?:Title|书名|書名|作品名称)[ \t]*[:：][ \t]*([^\r\n]{1,120})[ \t]*$");
            if (metadata.Success) return metadata.Groups[1].Value.Trim().Trim('《', '》');
            foreach (string raw in head.Split('\n').Take(12))
            {
                string line = raw.Trim();
                if (IsHeading(line)) break;
                Match standalone = Regex.Match(line, @"^《([^》\r\n]{1,100})》(?:[ \t]*(?:作者|Author)[ \t]*[:：].*)?$");
                if (standalone.Success) return standalone.Groups[1].Value.Trim();
            }
            return fallback;
        }
    }
}
