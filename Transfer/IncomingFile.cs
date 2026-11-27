using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace DeskLink.Transfer
{
    internal sealed class IncomingFile : IDisposable
    {
        internal readonly Guid Id;
        internal readonly string Name;
        internal readonly long Size;
        internal long Received { get; private set; }
        private readonly string folder;
        private string temporaryPath;
        private FileStream output;
        private readonly SHA256 digest;

        internal static string SafeName(string raw)
        {
            string leaf = raw.Replace('\\', '/').Split('/').Last();
            char[] invalid = Path.GetInvalidFileNameChars();
            string clean = new string(leaf.Select(c => invalid.Contains(c) || char.IsControl(c) ||
                char.GetUnicodeCategory(c) == UnicodeCategory.Format ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
            if (clean.Length == 0) clean = "收到的文件";
            string extension = Path.GetExtension(clean);
            if (clean.Length > 140)
            {
                if (extension.Length > 20) extension = "";
                string stem = clean.Substring(0, 140 - extension.Length);
                if (char.IsHighSurrogate(stem[stem.Length - 1])) stem = stem.Substring(0, stem.Length - 1);
                clean = stem.TrimEnd(' ', '.') + extension;
            }
            if (Regex.IsMatch(clean, @"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|(?:COM|LPT)[0-9¹²³])\s*(?:\.|$)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) clean = "_" + clean;
            return clean;
        }

        internal IncomingFile(Guid id, string name, long size, string directory)
        {
            if (size < 0) throw new InvalidDataException("文件大小无效。");
            Id = id; Name = SafeName(name); Size = size;
            folder = Path.GetFullPath(directory);
            Directory.CreateDirectory(folder);
            string root = Path.GetPathRoot(folder);
            if (root.Length >= 2 && root[1] == ':')
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady && drive.AvailableFreeSpace < size) throw new IOException("接收磁盘空间不足。");
            }
            digest = SHA256.Create();
            string candidate = Path.Combine(folder, ".desklink-" + Guid.NewGuid().ToString("N") + ".part");
            try
            {
                output = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    Wire.BlockSize, FileOptions.SequentialScan);
                temporaryPath = candidate;
            }
            catch { digest.Dispose(); throw; }
        }

        internal void Append(byte[] data, int count)
        {
            if (count == 0 || count > Wire.BlockSize || count > Size - Received)
                throw new InvalidDataException("收到的文件数据超出声明的大小。");
            output.Write(data, 0, count);
            digest.TransformBlock(data, 0, count, null, 0);
            Received += count;
        }

        internal string Commit(byte[] expected)
        {
            digest.TransformFinalBlock(new byte[0], 0, 0);
            if (Received != Size || !Wire.SameBytes(expected, digest.Hash)) throw new IOException("文件完整性校验失败。");
            output.Flush(true);
            output.Dispose();
            output = null;
            string stem = Path.GetFileNameWithoutExtension(Name), extension = Path.GetExtension(Name);
            for (int suffix = 0; ; suffix++)
            {
                string leaf = suffix == 0 ? Name : stem + " (" + suffix + ")" + extension;
                string final = Path.Combine(folder, leaf);
                try
                {
                    File.Move(temporaryPath, final);
                    temporaryPath = null;
                    return final;
                }
                catch (IOException) when (File.Exists(final) || Directory.Exists(final)) { }
            }
        }

        public void Dispose()
        {
            try { if (output != null) output.Dispose(); }
            catch (IOException) { /* A failed flush is already reported by Commit or Append. */ }
            catch (UnauthorizedAccessException) { }
            finally
            {
                digest.Dispose();
                if (temporaryPath != null)
                {
                    try { File.Delete(temporaryPath); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
    }
}
