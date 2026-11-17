using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AiNovelReader
{
    internal sealed class ContentPosition
    {
        public int Chapter, Paragraph;
        public double Offset;
    }

    internal sealed class ContentPositionMapper
    {
        private sealed class Index
        {
            public string Text;
            public string[] Lines;
            public int[] Starts;
            public int GlobalStart;
        }
        private readonly ParsedBook oldBook, newBook;
        private readonly List<Index> oldIndex, newIndex;
        public int Mapped { get; private set; }
        public int Unmatched { get; private set; }
        public ContentPositionMapper(ParsedBook before, ParsedBook after)
        {
            oldBook = before; newBook = after;
            oldIndex = Build(before); newIndex = Build(after);
        }
        private static string Normalize(string value) { return String.Concat(value.Where(c => !Char.IsWhiteSpace(c))); }
        private static List<Index> Build(ParsedBook book)
        {
            var result = new List<Index>(); int global = 0;
            foreach (var chapter in book.Chapters)
            {
                string[] lines = chapter.Content.Split('\n').Where(l => !String.IsNullOrWhiteSpace(l)).ToArray();
                var text = new StringBuilder(); var starts = new List<int>();
                foreach (string line in lines) { starts.Add(text.Length); text.Append(Normalize(line)); }
                result.Add(new Index { Text = text.ToString(), Lines = lines, Starts = starts.ToArray(), GlobalStart = global }); global += text.Length;
            }
            return result;
        }
        public ContentPosition Map(int chapter, int paragraph, double offset)
        {
            chapter = Math.Max(0, Math.Min(chapter, oldIndex.Count - 1));
            Index source = oldIndex[chapter]; paragraph = Math.Max(0, Math.Min(paragraph, Math.Max(0, source.Lines.Length - 1)));
            offset = Double.IsNaN(offset) || Double.IsInfinity(offset) ? 0 : Math.Max(0, Math.Min(1, offset));
            if (source.Lines.Length == 0) { Unmatched++; return new ContentPosition(); }
            int start = source.Starts[paragraph];
            string anchor = source.Text.Substring(start, Math.Min(72, source.Text.Length - start));
            string selectedLine = Normalize(source.Lines[paragraph]);
            string oldTitle = Normalize(oldBook.Chapters[chapter].Title);
            int sameTitle = newBook.Chapters.FindIndex(c => String.Equals(Normalize(c.Title), oldTitle, StringComparison.OrdinalIgnoreCase));
            // Legacy EPUB extraction could leave the two-line heading in body. It is now
            // displayed as the chapter title, so its correct successor is the body start.
            if (source.Lines.Length > 1 && paragraph <= 1 && selectedLine.Length > 0 && oldTitle.StartsWith(selectedLine, StringComparison.OrdinalIgnoreCase) && sameTitle >= 0)
            {
                Mapped++;
                return new ContentPosition { Chapter = sameTitle };
            }
            // Avoid an anchor crossing a logical chapter boundary after the parser upgrade.
            if (selectedLine.Length >= 12) anchor = selectedLine.Substring(0, Math.Min(72, selectedLine.Length));
            double ratio = (double)(source.GlobalStart + start) / Math.Max(1, oldIndex.Sum(i => i.Text.Length));
            int total = newIndex.Sum(i => i.Text.Length), bestChapter = -1, bestAt = 0;
            double bestDistance = Double.MaxValue;
            var anchors = new List<string> { anchor };
            if (selectedLine.Length >= 3 && selectedLine.Length < anchor.Length && selectedLine.Any(Char.IsLetterOrDigit)) anchors.Add(selectedLine);
            foreach (string candidate in anchors)
            {
              for (int i = 0; i < newIndex.Count && candidate.Length > 0; i++)
              {
                int from = 0;
                while (from < newIndex[i].Text.Length)
                {
                    int at = newIndex[i].Text.IndexOf(candidate, from, StringComparison.Ordinal);
                    if (at < 0) break;
                    double distance = Math.Abs((newIndex[i].GlobalStart + at) - ratio * total);
                    if (oldBook.Chapters[chapter].Title == newBook.Chapters[i].Title) distance *= .25;
                    if (distance < bestDistance) { bestDistance = distance; bestChapter = i; bestAt = at; }
                    from = at + Math.Max(1, candidate.Length);
                }
              }
              if (bestChapter >= 0) break;
            }
            if (bestChapter < 0)
            {
                Unmatched++;
                bestChapter = Math.Max(0, Math.Min(chapter, newIndex.Count - 1));
                int matching = newBook.Chapters.FindIndex(c => c.Title == oldBook.Chapters[chapter].Title);
                if (matching >= 0) bestChapter = matching;
                return new ContentPosition { Chapter = bestChapter, Paragraph = Math.Min(paragraph, Math.Max(0, newIndex[bestChapter].Lines.Length - 1)), Offset = offset };
            }
            Index target = newIndex[bestChapter]; int p = 0;
            while (p + 1 < target.Starts.Length && target.Starts[p + 1] <= bestAt) p++;
            double newOffset = (bestAt - target.Starts[p] + offset * selectedLine.Length) / Math.Max(1, Normalize(target.Lines[p]).Length);
            Mapped++;
            return new ContentPosition { Chapter = bestChapter, Paragraph = p, Offset = Math.Max(0, Math.Min(1, newOffset)) };
        }
    }
}
