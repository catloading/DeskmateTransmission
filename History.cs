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
        public bool Busy { get; private set; }
        public bool Failed { get; private set; }
        public double Percent { get; private set; }
        public string StateText { get; private set; }
        public bool HasPath { get { return IsFile && !string.IsNullOrEmpty(FilePath) && !Busy && !Failed; } }
        public string SizeText { get { return FormatSize(Size); } }
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
            Percent = Size > 0 ? Math.Min(100, bytes * 100d / Size) : 0;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }

        internal void Complete()
        {
            Update(Mine ? "对方已接收" : IsFile ? "已保存" : "已接收", bytes: Size);
        }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    internal sealed class LocalStore
    {
        private const int HistoryLimit = 200;
        private readonly string root;
        private readonly string history;
        private volatile string folder;
        internal string Folder { get { return folder; } }
        internal string Root { get { return root; } }

        internal LocalStore(string directory = null)
        {
            root = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TongZhuoChuan");
            history = Path.Combine(root, "history");
            folder = Path.Combine(Downloads(), "同桌传");
            string config = Path.Combine(root, "folder.txt");
            try
            {
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
                File.WriteAllBytes(temp, bytes);
                try { File.Move(temp, path); }
                catch (IOException) when (File.Exists(path)) { File.Replace(temp, path, null); }
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        private IEnumerable<string> NewestFirst()
        {
            return Directory.EnumerateFiles(history, "*.msg").OrderByDescending(s => s, StringComparer.Ordinal);
        }

        internal List<ChatItem> Load()
        {
            var items = new List<ChatItem>();
            if (!Directory.Exists(history)) return items;
            var serializer = new DataContractJsonSerializer(typeof(ChatItem));
            foreach (string path in NewestFirst().Take(HistoryLimit).Reverse())
            {
                try
                {
                    byte[] data = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
                    using (var stream = new MemoryStream(data))
                    {
                        var item = (ChatItem)serializer.ReadObject(stream);
                        if (item == null || item.Content == null) continue;
                        item.Complete();
                        items.Add(item);
                    }
                }
                catch (Exception ex) when (ex is CryptographicException || ex is SerializationException ||
                    ex is IOException || ex is UnauthorizedAccessException) { }
            }
            return items;
        }

        internal void Save(ChatItem item)
        {
            Directory.CreateDirectory(history);
            string name = item.Utc.Ticks.ToString("D19", CultureInfo.InvariantCulture) + "-" + item.Id.ToString("N") +
                (item.Mine ? "-out.msg" : "-in.msg");
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(ChatItem)).WriteObject(stream, item);
                WriteAtomic(Path.Combine(history, name), ProtectedData.Protect(stream.ToArray(), null, DataProtectionScope.CurrentUser));
            }
            foreach (string old in NewestFirst().Skip(HistoryLimit)) File.Delete(old);
        }
    }
}
