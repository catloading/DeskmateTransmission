using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace DeskLink.Transfer
{
    internal sealed class Outbox
    {
        private readonly string root;
        internal Outbox(string profile) { root = Path.Combine(profile, "outbox"); }
        private string Record(Guid id) { return Path.Combine(root, id.ToString("N") + ".pending"); }
        private string Bundle(Guid id) { return Path.Combine(root, id.ToString("N") + ".zip"); }

        internal void Save(ChatItem item)
        {
            if (!DeviceHello.ValidId(item.DeviceId) || item.Id == Guid.Empty) throw new IOException("需要先与这台电脑配对。");
            LocalStore.WritePrivate(Record(item.Id), item);
            item.Queued = true;
        }

        internal List<ChatItem> Load()
        {
            var result = new List<ChatItem>();
            if (!Directory.Exists(root)) return result;
            foreach (string file in Directory.EnumerateFiles(root, "*.pending"))
            {
                var item = LocalStore.ReadPrivate<ChatItem>(file);
                if (!DeviceHello.ValidId(item.DeviceId) || Record(item.Id) != file) throw new InvalidDataException("待发送记录无效。");
                item.Queued = true;
                item.Update(item.Problem ?? "待发送 · 等待设备上线", failed: item.Problem != null);
                result.Add(item);
            }
            return result.OrderBy(i => i.Utc).ToList();
        }

        internal void Remove(ChatItem item)
        {
            if (Directory.Exists(root)) File.Delete(Record(item.Id));
            item.Queued = false;
            if (item.IsFolder) DiscardBundle(item.Id);
        }

        internal void CleanUnusedBundles()
        {
            if (!Directory.Exists(root)) return;
            foreach (string file in Directory.EnumerateFiles(root, "*.zip"))
            {
                Guid id;
                if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out id) && !File.Exists(Record(id)))
                    DiscardBundle(id);
            }
        }

        internal void DiscardBundle(Guid id)
        {
            try { if (File.Exists(Bundle(id))) File.Delete(Bundle(id)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal void Prepare(ChatItem item, string path, FolderPayload folder = null)
        {
            path = Path.GetFullPath(path);
            item.IsFile = true;
            item.FilePath = path;
            item.Content = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
            if (string.IsNullOrEmpty(item.Content)) throw new IOException("请选择具体的文件或文件夹。");
            item.IsFolder = folder != null;
            try
            {
                if (folder != null)
                {
                    if ((root + Path.DirectorySeparatorChar).StartsWith(path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)) throw new IOException("请选择不包含同桌传自身数据目录的文件夹。");
                    Directory.CreateDirectory(root);
                    item.SourceStamp = folder.Pack(Bundle(item.Id));
                    item.Size = folder.Bytes;
                }
                using (var file = File.OpenRead(folder == null ? path : Bundle(item.Id)))
                using (var hash = SHA256.Create())
                {
                    item.WireSize = file.Length;
                    item.Digest = Convert.ToBase64String(hash.ComputeHash(file));
                    if (folder == null) item.Size = file.Length;
                }
            }
            catch { if (folder != null) DiscardBundle(item.Id); throw; }
        }

        internal bool SameSelectedSource(ChatItem item, string path)
        {
            if (item.IsFolder) return Directory.Exists(path) && File.Exists(Bundle(item.Id)) &&
                new FolderPayload(path).Fingerprint() == item.SourceStamp;
            if (!File.Exists(path)) return false;
            using (var file = File.OpenRead(path)) return MatchesBytes(item, file);
        }

        private static bool MatchesBytes(ChatItem item, FileStream file)
        {
            using (var hash = SHA256.Create())
                return file.Length == item.WireSize && Convert.ToBase64String(hash.ComputeHash(file)) == item.Digest;
        }

        internal FileStream OpenVerified(ChatItem item)
        {
            if (item.IsFolder && (!Directory.Exists(item.FilePath) || new FolderPayload(item.FilePath).Fingerprint() != item.SourceStamp))
                throw new IOException("源文件夹已移动或修改，请重新选择。");
            string path = item.IsFolder ? Bundle(item.Id) : item.FilePath;
            if (!File.Exists(path)) throw new IOException("源文件已移动，请重新选择。");
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, Wire.BlockSize, FileOptions.SequentialScan);
            try
            {
                if (!MatchesBytes(item, file)) throw new IOException("源文件已修改，请重新选择。");
                file.Position = 0;
                return file;
            }
            catch { file.Dispose(); throw; }
        }
    }
}
