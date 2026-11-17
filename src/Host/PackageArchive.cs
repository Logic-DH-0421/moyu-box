using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace MoyuBox
{
    // No execution/build scripts: archives contain static projects only.
    public static class PackageArchive
    {
        public const long MaxBytes = 50L * 1024 * 1024;
        public static void Extract(string source, string target)
        {
            if (new FileInfo(source).Length > MaxBytes) throw new InvalidDataException("ZIP 文件不能超过 50MB。");
            byte[] raw = File.ReadAllBytes(source);
            var checksums = CentralChecksums(raw);
            using (var memory = new MemoryStream(raw, false))
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Read, false, Encoding.GetEncoding(437)))
            {
                if (archive.Entries.Count != checksums.Count || archive.Entries.Count > 5000) throw new InvalidDataException("ZIP 目录无效或超过 5000 个条目。");
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0; int index = 0;
                Directory.CreateDirectory(target);
                foreach (var entry in archive.Entries)
                {
                    string name = entry.FullName.Replace('\\', '/');
                    if (name.StartsWith("/") || name.Split('/').Any(x => x == ".." || x == "." || x.IndexOfAny(Path.GetInvalidFileNameChars())>=0 || System.Text.RegularExpressions.Regex.IsMatch(x,@"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])([ .]|$)",System.Text.RegularExpressions.RegexOptions.IgnoreCase)) || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000) throw new InvalidDataException("ZIP 不允许绝对路径、目录越界、设备文件名或符号链接。");
                    string relative = name.TrimEnd('/');
                    if (relative.Length == 0 || !names.Add(relative)) throw new InvalidDataException("ZIP 包含重复或空路径。");
                    string destination = Rules.ContainedPath(target, relative);
                    uint expected = checksums[index++];
                    if (name.EndsWith("/")) { Directory.CreateDirectory(destination); continue; }
                    if (entry.Length > MaxBytes - total) throw new InvalidDataException("解压后的项目不能超过 50MB。");
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    long length = 0; uint crc = 0xffffffff;
                    using (var input = entry.Open()) using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write))
                    {
                        var buffer = new byte[65536]; int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            total += read; length += read;
                            if (total > MaxBytes) throw new InvalidDataException("解压后的项目不能超过 50MB。");
                            for (int i = 0; i < read; i++) crc = CrcTable[(crc ^ buffer[i]) & 255] ^ (crc >> 8);
                            output.Write(buffer, 0, read);
                        }
                    }
                    if (length != entry.Length || (crc ^ 0xffffffff) != expected) throw new InvalidDataException("ZIP 内容校验失败，文件可能损坏，请重新下载。");
                }
            }
        }
        static readonly uint[] CrcTable = MakeTable();
        static uint[] MakeTable() { var table = new uint[256]; for (uint i = 0; i < 256; i++) { uint value = i; for (int n = 0; n < 8; n++) value = (value & 1) != 0 ? 0xedb88320 ^ (value >> 1) : value >> 1; table[i] = value; } return table; }
        static List<uint> CentralChecksums(byte[] bytes)
        {
            int end = -1;
            for (int i = bytes.Length - 22; i >= Math.Max(0, bytes.Length - 65557); i--) if (BitConverter.ToUInt32(bytes, i) == 0x06054b50 && i + 22 + BitConverter.ToUInt16(bytes, i + 20) == bytes.Length) { end = i; break; }
            if (end < 0) throw new InvalidDataException("ZIP 文件不完整，请重新下载。");
            int count = BitConverter.ToUInt16(bytes, end + 10);
            uint offset = BitConverter.ToUInt32(bytes, end + 16), size = BitConverter.ToUInt32(bytes, end + 12);
            if (count == 65535 || offset == UInt32.MaxValue || BitConverter.ToUInt16(bytes,end+4)!=0 || BitConverter.ToUInt16(bytes,end+6)!=0 || BitConverter.ToUInt16(bytes,end+8)!=count || offset + (long)size > end || count > 5000) throw new InvalidDataException("不支持分卷、ZIP64 或超出限制的 ZIP 项目。");
            var result = new List<uint>(); int position = (int)offset;
            for (int i = 0; i < count; i++)
            {
                if (position < 0 || position > end - 46 || BitConverter.ToUInt32(bytes, position) != 0x02014b50) throw new InvalidDataException("ZIP 中央目录损坏。");
                if ((BitConverter.ToUInt16(bytes,position+8)&1)!=0) throw new InvalidDataException("不支持加密 ZIP，请先解密后导入。");
                result.Add(BitConverter.ToUInt32(bytes, position + 16));
                position += 46 + BitConverter.ToUInt16(bytes,position+28) + BitConverter.ToUInt16(bytes,position+30) + BitConverter.ToUInt16(bytes,position+32);
                if(position > offset + size) throw new InvalidDataException("ZIP 中央目录长度无效。");
            }
            return result;
        }
        public static string[] Entries(string directory)
        {
            var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
            var manifests = files.Where(x => String.Equals(Path.GetFileName(x), "manifest.json", StringComparison.OrdinalIgnoreCase)).Where(x => {
                try { var p = RegistryStore.Json.Deserialize<Project>(File.ReadAllText(x, Encoding.UTF8)); return p != null && p.kind == "local-web"; } catch (ArgumentException) { return false; } catch (InvalidOperationException) { return false; }
            }).OrderBy(x => x.Length).ToArray();
            if (manifests.Length > 0) return manifests;
            var html = files.Where(x => x.EndsWith(".html",StringComparison.OrdinalIgnoreCase)||x.EndsWith(".htm",StringComparison.OrdinalIgnoreCase)).ToArray();
            string[] indexes = html.Where(x => String.Equals(Path.GetFileNameWithoutExtension(x),"index",StringComparison.OrdinalIgnoreCase)).ToArray();
            if(indexes.Length>0) { int depth=indexes.Min(x=>x.Substring(directory.Length).Count(c=>c=='\\'||c=='/'));return indexes.Where(x=>x.Substring(directory.Length).Count(c=>c=='\\'||c=='/')==depth).OrderBy(x=>x).ToArray(); }
            return html.OrderBy(x=>x).ToArray();
        }
    }
}
