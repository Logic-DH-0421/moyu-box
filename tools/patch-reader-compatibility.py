"""Local reader changes; never edits the original standalone reader project."""
from pathlib import Path
import sys
p=Path(sys.argv[1]);s=p.read_text(encoding='utf-8-sig')
# Structured upstream readers already implement content migration. Preserve only
# the host's former extended TXT layout marker when upgrading these libraries.
if 'entry.ContentVersion < TextStructure.Version' in s:
    if 'public int TextLayoutVersion' not in s:
        s=s.replace('public int ContentVersion { get; set; }', 'public int ContentVersion { get; set; }\n        public int TextLayoutVersion { get; set; }')
    s=s.replace(': (epub ? EpubBookParser.ParseLegacy(bytes, entry.Title) : BookParser.ParseLegacy(bytes, entry.Title));', ': (epub ? EpubBookParser.ParseLegacy(bytes, entry.Title) : entry.TextLayoutVersion == 1 ? MoyuLegacyBookParser.Parse(bytes, entry.Title) : BookParser.ParseLegacy(bytes, entry.Title));')
    if 'static ParsedBook ParseEpubImport' not in s:
        s=s.replace('EpubBookParser.Parse(bytes, fallbackTitle)', 'ParseEpubImport(bytes, fallbackTitle)').replace('EpubBookParser.Parse(bytes, entry.Title)', 'ParseEpubImport(bytes, entry.Title)')
        s=s.replace('        private ParsedBook GetParsedBook(string bookId)', '        static ParsedBook ParseEpubImport(byte[] bytes,string title) { try {return EpubBookParser.Parse(bytes,title);} catch(InvalidDataException ex){throw new InvalidDataException("EPUB 导入失败：文件可能损坏或不完整，请重新下载完整文件后重试。",ex);} }\n        private ParsedBook GetParsedBook(string bookId)')
    p.write_text(s,encoding='utf-8-sig')
    print('Structured reader migration preserved, including old Moyu TXT layout')
    sys.exit(0)
if 'TextLayoutVersion' not in s:
    s=s.replace('        public bool Pinned { get; set; }','        public int TextLayoutVersion { get; set; }\n        public bool Pinned { get; set; }')
    s=s.replace('                entry.Pinned = true;','                entry.Pinned = true;\n                entry.TextLayoutVersion = 1;')
    s=s.replace('                entry.Title = parsed.SuggestedTitle;\n                entry.EncodingName = parsed.EncodingName;\n            }','                if(entry.TextLayoutVersion==0 && extension==".txt") parsed=BookParser.ParseLegacy(bytes,fallbackTitle);\n                entry.Title = parsed.SuggestedTitle;\n                entry.EncodingName = parsed.EncodingName;\n            }')
    s=s.replace(': BookParser.Parse(bytes, entry.Title);',': (entry.TextLayoutVersion==0 ? BookParser.ParseLegacy(bytes,entry.Title) : BookParser.Parse(bytes, entry.Title));')
    old='        public static ParsedBook Parse(byte[] bytes, string fallbackTitle)\n        {\n            string encodingName;'
    new=r'''        private static readonly Regex LegacyChapterRegex = new Regex(@"(?m)^\s*(第\s*[0-9一二三四五六七八九十百千万零〇两]+\s*章[^\r\n]*)\s*$", RegexOptions.Compiled);
        public static ParsedBook Parse(byte[] bytes,string fallbackTitle) { return ParseCore(bytes,fallbackTitle,true); }
        public static ParsedBook ParseLegacy(byte[] bytes,string fallbackTitle) { return ParseCore(bytes,fallbackTitle,false); }
        static ParsedBook ParseCore(byte[] bytes, string fallbackTitle,bool extended)
        {
            string encodingName;'''
    # Keep regex escapes as literal C# characters.
    new=new.replace('[^\r\n]',r'[^\r\n]')
    if old not in s:raise RuntimeError('Reader parser changed; review patch')
    s=s.replace(old,new).replace('MatchCollection matches = ChapterRegex.Matches(text);','MatchCollection matches = (extended ? ChapterRegex : LegacyChapterRegex).Matches(text);')
old=r'new Regex(@"(?m)^\s*(第\s*[0-9一二三四五六七八九十百千万零〇两]+\s*章[^\r\n]*)\s*$", RegexOptions.Compiled)'
new=r'new Regex(@"(?im)^[ \t]*(第[ \t]*[0-9一二三四五六七八九十百千万零〇两]+[ \t]*章[^\r\n]*|(?:chapter|book|part|act|scene)[ \t]+(?:[0-9]+|[ivxlcdm]+|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve)(?:[.：:—–-][^\r\n]*|[ \t]+[^\r\n]*)?)[ \t]*$", RegexOptions.Compiled)'
# Replace only the main matcher; legacy books intentionally retain the old layout.
s=s.replace('Regex ChapterRegex = '+old,'Regex ChapterRegex = '+new,1)
import re
s=re.sub(r'(?m)^        private static readonly Regex LegacyChapterRegex = .*$',lambda _: '        private static readonly Regex LegacyChapterRegex = '+old+';',s)
p.write_text(s,encoding='utf-8-sig')

# Exclude indented contents-list headings only when a non-indented counterpart occurs later.
old='MatchCollection matches = (extended ? ChapterRegex : LegacyChapterRegex).Matches(text);'
new=r'''var matches = (extended ? ChapterRegex : LegacyChapterRegex).Matches(text).Cast<Match>().ToList();
            if(extended) matches = matches.Where(m => !(text[m.Index]==' '||text[m.Index]=='\t') || !Regex.IsMatch(m.Groups[1].Value,@"^(chapter|book|part|act|scene)\b",RegexOptions.IgnoreCase) || !matches.Any(n => n.Index>m.Index && text[n.Index]!=' ' && text[n.Index]!='\t' && String.Equals(Regex.Match(n.Groups[1].Value,@"^\w+\s+[\w-]+").Value,Regex.Match(m.Groups[1].Value,@"^\w+\s+[\w-]+").Value,StringComparison.OrdinalIgnoreCase))).ToList();'''
s=s.replace(old,new)
p.write_text(s,encoding='utf-8-sig')

if 'static ParsedBook ParseEpubImport' not in s:
    s=s.replace('EpubBookParser.Parse(bytes, fallbackTitle) : BookParser.Parse', 'ParseEpubImport(bytes, fallbackTitle) : BookParser.Parse')
    s=s.replace('EpubBookParser.Parse(bytes, entry.Title) :', 'ParseEpubImport(bytes, entry.Title) :')
    point='        private ParsedBook GetParsedBook(string bookId)'
    helper='        static ParsedBook ParseEpubImport(byte[] bytes,string title) {\n            try {return EpubBookParser.Parse(bytes,title);}\n            catch(InvalidDataException error){throw new InvalidDataException("EPUB 导入失败：文件可能损坏或不完整，请重新下载完整文件后重试。",error);}\n        }\n\n'
    s=s.replace(point,helper+point)
p.write_text(s,encoding='utf-8-sig')
