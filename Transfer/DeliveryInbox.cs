using System;
using System.IO;
using System.Runtime.Serialization;
using System.Security.Cryptography;

namespace DeskLink.Transfer
{
    // A receipt belongs to the authenticated device and survives chat-history pruning.
    [DataContract]
    internal sealed class DeliveryRecord
    {
        [DataMember] internal Guid Id;
        [DataMember] internal DateTime Utc;
        [DataMember] internal string Kind, Name, Digest, Directory, Target;
        [DataMember] internal long Length, LogicalSize;
        [DataMember] internal bool Prepared, Done;
    }

    internal sealed class DeliveryInbox
    {
        private readonly LocalStore store;
        private readonly string device, peerName, ledger;
        internal DeliveryInbox(LocalStore store, string device, string peerName)
        {
            if (!DeviceHello.ValidId(device)) throw new ArgumentException("无效设备");
            this.store = store; this.device = device; this.peerName = peerName;
            ledger = Path.Combine(store.Root, "receipts", device);
        }

        private string State(Guid id) { return Path.Combine(ledger, id.ToString("N") + ".receipt"); }
        internal void Save(DeliveryRecord record) { LocalStore.WritePrivate(State(record.Id), record); }

        internal DeliveryRecord Open(Guid id, string kind, string name, string digest, long length, long logicalSize)
        {
            if (id == Guid.Empty || length < 0 || logicalSize < 0) throw new InvalidDataException("传输信息无效。");
            if (File.Exists(State(id)))
            {
                var existing = LocalStore.ReadPrivate<DeliveryRecord>(State(id));
                if (existing.Id != id || existing.Kind != kind || existing.Name != name || existing.Digest != digest ||
                    existing.Length != length || existing.LogicalSize != logicalSize)
                    throw new InvalidDataException("同一传输编号的内容已变化，请重新选择。");
                return existing;
            }
            var record = new DeliveryRecord { Id = id, Utc = DateTime.UtcNow, Kind = kind, Name = name,
                Digest = digest, Length = length, LogicalSize = logicalSize, Directory = kind == "text" ? null : store.Folder };
            Save(record);
            return record;
        }

        internal Received Complete(DeliveryRecord record, string text = null)
        {
            if (record.Done) return null;
            var item = new ChatItem { Id = record.Id, Utc = record.Utc, DeviceId = device, Peer = peerName,
                Content = text ?? record.Name, IsFile = record.Kind != "text", IsFolder = record.Kind == "folder",
                FilePath = record.Target, Size = record.LogicalSize, WireSize = record.Length };
            store.Save(item);
            record.Done = true;
            Save(record);
            return new Received { Id = item.Id, Content = item.Content, Size = item.Size, Bytes = record.Length,
                IsFile = item.IsFile, IsFolder = item.IsFolder, Path = item.FilePath, Utc = item.Utc, Stored = true, State = ReceiveState.Complete };
        }

        internal Received Text(Guid id, string text)
        {
            byte[] bytes = Wire.Utf8.GetBytes(text);
            using (var sha = SHA256.Create())
            {
                var record = Open(id, "text", "", Convert.ToBase64String(sha.ComputeHash(bytes)), bytes.Length, 0);
                return Complete(record, text);
            }
        }

        internal string Partial(DeliveryRecord record)
        {
            return Path.Combine(record.Directory, ".desklink-" + device + "-" + record.Id.ToString("N") + ".part");
        }
    }

    internal sealed class ResumableFile : IDisposable
    {
        internal readonly DeliveryRecord Record;
        private readonly DeliveryInbox inbox;
        private readonly string part, expanded;
        private FileStream stream;
        internal long Position { get { return stream == null ? Record.Length : stream.Position; } }

        internal ResumableFile(DeliveryInbox inbox, DeliveryRecord record)
        {
            this.inbox = inbox; Record = record;
            part = inbox.Partial(record);
            expanded = part + ".folder";
        }

        internal byte[] Open()
        {
            Directory.CreateDirectory(Record.Directory);
            stream = new FileStream(part, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, Wire.BlockSize);
            if (stream.Length > Record.Length) Reset();
            var root = Path.GetPathRoot(part);
            if (root.Length >= 2 && root[1] == ':')
            {
                var drive = new DriveInfo(root);
                long remaining = Record.Length - stream.Length;
                if (drive.IsReady && (drive.AvailableFreeSpace < remaining ||
                    (Record.Kind == "folder" && drive.AvailableFreeSpace - remaining < Record.LogicalSize)))
                    throw new IOException("接收磁盘空间不足。");
            }
            stream.Position = 0;
            using (var hash = SHA256.Create()) return hash.ComputeHash(stream);
        }

        internal void Reset() { stream.SetLength(0); stream.Position = 0; }

        internal void Append(byte[] bytes, int count)
        {
            if (count <= 0 || count > Wire.BlockSize || count > Record.Length - Position) throw new InvalidDataException("文件块长度无效。");
            stream.Write(bytes, 0, count);
        }

        internal Received Commit(byte[] digest)
        {
            if (Position != Record.Length) throw new IOException("文件未收全。");
            stream.Flush(true);
            stream.Position = 0;
            using (var hash = SHA256.Create())
            {
                if (Convert.ToBase64String(digest) != Record.Digest || !Wire.SameBytes(digest, hash.ComputeHash(stream)))
                { Reset(); throw new IOException("文件完整性校验失败。"); }
            }
            Dispose();
            if (Record.Kind == "folder")
            {
                if (Directory.Exists(expanded)) Directory.Delete(expanded, true);
                Directory.CreateDirectory(expanded);
                FolderPayload.Extract(part, expanded, Record.LogicalSize);
            }
            Record.Prepared = true;
            Record.Target = AvailableTarget();
            inbox.Save(Record);
            return FinishPrepared();
        }

        private string AvailableTarget()
        {
            string ext = Record.Kind == "folder" ? "" : Path.GetExtension(Record.Name);
            string stem = Record.Name.Substring(0, Record.Name.Length - ext.Length);
            for (int n = 0; ; n++)
            {
                string target = Path.Combine(Record.Directory, n == 0 ? Record.Name : stem + " (" + n + ")" + ext);
                if (!File.Exists(target) && !Directory.Exists(target)) return target;
            }
        }

        internal Received FinishPrepared()
        {
            bool folder = Record.Kind == "folder";
            // After a crash, a missing staging object and existing target mean that the atomic move succeeded.
            while (folder ? Directory.Exists(expanded) : File.Exists(part))
            {
                try
                {
                    if (folder) Directory.Move(expanded, Record.Target);
                    else File.Move(part, Record.Target);
                    break;
                }
                catch (IOException) when (File.Exists(Record.Target) || Directory.Exists(Record.Target))
                {
                    Record.Target = AvailableTarget(); inbox.Save(Record);
                }
            }
            if (!(folder ? Directory.Exists(Record.Target) : File.Exists(Record.Target)))
                throw new IOException("已校验的接收文件被移走。");
            if (folder && File.Exists(part)) File.Delete(part);
            return inbox.Complete(Record);
        }

        public void Dispose() { if (stream != null) { stream.Dispose(); stream = null; } }
    }
}
