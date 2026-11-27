using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace DeskLink.Transfer
{
    internal sealed class FolderPayload
    {
        private sealed class Entry
        {
            internal string Name;
            internal bool Directory;
            internal long Size, Ticks;
        }

        private readonly List<Entry> entries = new List<Entry>();
        private readonly string root;
        internal readonly string Stamp;
        internal long Bytes { get; private set; }
        internal int Count { get; private set; }

        internal FolderPayload(string path)
        {
            root = Path.GetFullPath(path);
            Walk(root, "");
            using (var digest = SHA256.Create())
            using (var stream = new CryptoStream(Stream.Null, digest, CryptoStreamMode.Write))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                foreach (var e in entries)
                {
                    writer.Write(e.Name); writer.Write(e.Directory); writer.Write(e.Size); writer.Write(e.Ticks);
                }
                writer.Flush();
                stream.FlushFinalBlock();
                Stamp = Convert.ToBase64String(digest.Hash);
            }
        }

        private void Walk(string directory, string prefix)
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("请选择实际文件夹，目录中含有链接：" + directory);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory).OrderBy(s => s, StringComparer.Ordinal))
            {
                var info = new FileInfo(path);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("目录中含有链接：" + path);
                var e = new Entry { Name = prefix + info.Name, Directory = (info.Attributes & FileAttributes.Directory) != 0 };
                entries.Add(e);
                if (e.Directory) Walk(path, e.Name + "/");
                else { e.Size = info.Length; e.Ticks = info.LastWriteTimeUtc.Ticks; Bytes = checked(Bytes + e.Size); Count++; }
            }
        }

        internal string Pack(string destination)
        {
            string signature;
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create)) signature = Fingerprint(zip);
            if (new FolderPayload(root).Stamp != Stamp) throw new IOException("文件夹在准备期间已修改，请重新选择。");
            return signature;
        }

        internal string Fingerprint(ZipArchive zip = null)
        {
            using (var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                fingerprint.AppendData(Convert.FromBase64String(Stamp));
                byte[] buffer = new byte[Wire.BlockSize];
                foreach (var e in entries)
                {
                    var entry = zip?.CreateEntry(e.Directory ? e.Name + "/" : e.Name, CompressionLevel.Fastest);
                    if (e.Directory) continue;
                    using (var input = File.OpenRead(Path.Combine(root, e.Name)))
                    using (var target = entry?.Open())
                    {
                        if (input.Length != e.Size) throw new IOException("文件夹内容已修改，请重新选择。");
                        int n;
                        while ((n = input.Read(buffer, 0, buffer.Length)) != 0)
                        {
                            fingerprint.AppendData(buffer, 0, n);
                            target?.Write(buffer, 0, n);
                        }
                    }
                }
                return Convert.ToBase64String(fingerprint.GetHashAndReset());
            }
        }

        internal static void Extract(string archive, string target, long expectedBytes)
        {
            string prefix = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (var input = File.OpenRead(archive))
            using (var zip = new ZipArchive(input, ZipArchiveMode.Read))
            {
                long total = 0;
                var buffer = new byte[65536];
                foreach (var entry in zip.Entries)
                {
                    bool directory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
                    string name = directory ? entry.FullName.Substring(0, entry.FullName.Length - 1) : entry.FullName;
                    var parts = name.Split('/');
                    if (parts.Any(p => p.Length == 0 || p == "." || p == ".." || p.Length > 255 ||
                        p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || p.EndsWith(".") || p.EndsWith(" ") ||
                        IncomingFile.ReservedName(p))) throw new InvalidDataException("文件夹中有无效的路径。");
                    string path = Path.GetFullPath(Path.Combine(target, name));
                    if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("文件夹路径无效。");
                    if (directory) { Directory.CreateDirectory(path); continue; }
                    if (entry.Length > expectedBytes - total) throw new InvalidDataException("文件夹大小与发送清单不一致。");
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using (var src = entry.Open())
                    using (var dest = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        long written = 0;
                        int n;
                        while ((n = src.Read(buffer, 0, buffer.Length)) != 0)
                        {
                            if (n > entry.Length - written) throw new InvalidDataException("文件内容超出声明大小。");
                            dest.Write(buffer, 0, n); written += n;
                        }
                        if (written != entry.Length) throw new InvalidDataException("文件内容不完整。");
                        dest.Flush(true);
                    }
                    total += entry.Length;
                }
                if (total != expectedBytes) throw new InvalidDataException("文件夹总大小不匹配。");
            }
        }
    }
}
