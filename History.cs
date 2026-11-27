using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace DeskLink
{
    [DataContract]
    internal sealed class ChatItem : INotifyPropertyChanged
    {
        [DataMember] public Guid Id { get; set; }
        [DataMember] public DateTime Utc { get; set; }
        [DataMember] public bool Mine { get; set; }
        [DataMember] public bool IsFile { get; set; }
        [DataMember] public string Peer { get; set; }
        [DataMember(EmitDefaultValue = false)] public string DeviceId { get; set; }
        [DataMember] public string Content { get; set; }
        [DataMember] public string FilePath { get; set; }
        [DataMember] public long Size { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool IsFolder { get; set; }
        [DataMember(EmitDefaultValue = false)] public string Digest { get; set; }
        [DataMember(EmitDefaultValue = false)] public string SourceStamp { get; set; }
        [DataMember(EmitDefaultValue = false)] public long WireSize { get; set; }
        [DataMember(EmitDefaultValue = false)] public string Problem { get; set; }
        public bool Queued { get; internal set; }
        public bool CanCancel { get { return Queued && !Busy; } }
        public bool CanRetry { get { return CanCancel && !string.IsNullOrEmpty(Problem); } }
        public bool CanReselect { get { return CanRetry && IsFile; } }
        public bool Busy { get; private set; }
        public bool Failed { get; private set; }
        public double Percent { get; private set; }
        public string StateText { get; private set; }
        public bool HasPath { get { return IsFile && !string.IsNullOrEmpty(FilePath) && !Busy && !Failed && !Queued; } }
        public string SizeText { get { return (IsFolder ? "文件夹  ·  " : "") + FormatSize(Size); } }
        public string Byline
        {
            get
            {
                DateTime local = Utc.ToLocalTime();
                string format = local.Date == DateTime.Today ? "HH:mm" : "yyyy/MM/dd HH:mm";
                return (Mine ? "我 → " + Peer : Peer) + "   ·   " + local.ToString(format);
            }
        }

        internal static string FormatSize(long size)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = size; int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return value.ToString(unit == 0 ? "0" : "0.#", CultureInfo.CurrentCulture) + " " + units[unit];
        }

        internal void Update(string status, bool busy = false, bool failed = false, long bytes = 0)
        {
            StateText = status; Busy = busy; Failed = failed;
            long total = IsFolder && busy ? WireSize : Size;
            Percent = total > 0 ? Math.Min(100, bytes * 100d / total) : 0;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }

        internal void Complete()
        {
            Queued = false; Problem = null;
            Update(Mine ? "对方已接收" : IsFile ? "已保存" : "已接收", bytes: Size);
        }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    internal sealed class LocalStore
    {
        private readonly object historyLock = new object();
        internal int RetentionDays { get; private set; } = -1; // -1: newest 200; 0: unlimited.
        private readonly string retentionFile;
        private readonly string root;
        private readonly string history;
        private volatile string folder;
        internal string Folder { get { return folder; } }
        internal string Root { get { return root; } }

        internal LocalStore(string directory = null)
        {
            root = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TongZhuoChuan");
            history = Path.Combine(root, "history");
            retentionFile = Path.Combine(root, "retention.txt");
            folder = Path.Combine(Downloads(), "同桌传");
            string config = Path.Combine(root, "folder.txt");
            try
            {
                int days;
                if (File.Exists(retentionFile) && int.TryParse(File.ReadAllText(retentionFile), out days) && ValidRetention(days))
                    RetentionDays = days;
                if (File.Exists(config))
                {
                    string saved = File.ReadAllText(config);
                    if (Path.IsPathRooted(saved)) folder = Path.GetFullPath(saved);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                ex is ArgumentException || ex is NotSupportedException) { }
        }

        [DllImport("shell32.dll")]
        private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);
        private static string Downloads()
        {
            var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            IntPtr path;
            int status = SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out path);
            try
            {
                if (status == 0 && path != IntPtr.Zero) return Marshal.PtrToStringUni(path);
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            }
            finally { if (path != IntPtr.Zero) Marshal.FreeCoTaskMem(path); }
        }

        internal void SetFolder(string value)
        {
            string absolute = Path.GetFullPath(value);
            Directory.CreateDirectory(absolute);
            Directory.CreateDirectory(root);
            string target = Path.Combine(root, "folder.txt");
            WriteAtomic(target, Encoding.UTF8.GetBytes(absolute));
            folder = absolute;
        }

        internal static void WriteAtomic(string path, byte[] bytes)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { output.Write(bytes, 0, bytes.Length); output.Flush(true); }
                try { File.Move(temp, path); }
                catch (IOException) when (File.Exists(path)) { File.Replace(temp, path, null); }
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        internal static void WritePrivate<T>(string path, T value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
                WriteAtomic(path, ProtectedData.Protect(stream.ToArray(), null, DataProtectionScope.CurrentUser));
            }
        }

        internal static T ReadPrivate<T>(string path)
        {
            byte[] bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            using (var stream = new MemoryStream(bytes))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }

        private static bool ValidRetention(int days) { return days == -1 || days == 0 || days == 7 || days == 30 || days == 90; }

        internal void SetRetention(int days)
        {
            if (!ValidRetention(days)) throw new ArgumentOutOfRangeException(nameof(days));
            lock (historyLock)
            {
                Directory.CreateDirectory(root);
                WriteAtomic(retentionFile, Encoding.ASCII.GetBytes(days.ToString(CultureInfo.InvariantCulture)));
                RetentionDays = days;
                TrimHistory();
            }
        }

        private IEnumerable<string> NewestFirst()
        {
            return Directory.EnumerateFiles(history, "*.msg").OrderByDescending(s => s, StringComparer.Ordinal);
        }

        private void TrimHistory()
        {
            if (RetentionDays == 0 || !Directory.Exists(history)) return;
            string cutoff = RetentionDays > 0 ? DateTime.UtcNow.AddDays(-RetentionDays).Ticks.ToString("D19", CultureInfo.InvariantCulture) : null;
            var expired = cutoff == null ? NewestFirst().Skip(200) :
                Directory.EnumerateFiles(history, "*.msg").Where(p => string.CompareOrdinal(Path.GetFileName(p), cutoff) < 0);
            foreach (string path in expired.ToList()) File.Delete(path);
        }

        internal List<ChatItem> Load()
        {
            lock (historyLock)
            {
                var items = new List<ChatItem>();
                if (!Directory.Exists(history)) return items;
                TrimHistory();
                foreach (string path in NewestFirst().Reverse())
                {
                    try
                    {
                        var item = ReadPrivate<ChatItem>(path);
                        if (item == null || item.Content == null) continue;
                        item.Complete();
                        items.Add(item);
                    }
                    catch (Exception ex) when (ex is CryptographicException || ex is SerializationException ||
                        ex is IOException || ex is UnauthorizedAccessException) { }
                }
                return items;
            }
        }

        internal void Save(ChatItem item)
        {
            string name = item.Utc.Ticks.ToString("D19", CultureInfo.InvariantCulture) + "-" + item.Id.ToString("N") +
                (item.Mine ? "-out.msg" : "-in.msg");
            lock (historyLock)
            {
                WritePrivate(Path.Combine(history, name), item);
                TrimHistory();
            }
        }
    }
}
