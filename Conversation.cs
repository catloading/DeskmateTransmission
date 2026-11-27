using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using DeskLink.Transfer;

namespace DeskLink
{
    internal sealed class Conversation : INotifyPropertyChanged
    {
        internal readonly string Key;
        internal readonly ObservableCollection<ChatItem> Messages = new ObservableCollection<ChatItem>();
        internal readonly Dictionary<Guid, ChatItem> Incoming = new Dictionary<Guid, ChatItem>();
        internal TransferSession Session;
        internal Task LastText = Task.CompletedTask;
        internal bool SendingQueuedText, SendingQueuedFile, LegacyLive;
        internal string Draft = "", PairAddress, Address;
        internal bool Trusted, Nearby, Paused, DeviceView;
        internal int Unread;
        internal DateTime PairedUtc;
        public string Name { get; internal set; }
        public string DisplayName { get { return Key.StartsWith("legacy:", StringComparison.Ordinal) ? Name + "（旧版）" : Name; } }
        public string DeviceTip { get { return Address == null ? Name : Name + "\n" + Address.Split(':')[0] + "  ·  " + Key.Substring(0, 8); } }
        public bool Online { get { return Session != null && Session.Connected; } }
        public bool Available { get { return Online || Nearby; } }
        public bool HasUnread { get { return Unread > 0; } }
        public string UnreadLabel { get { return Unread > 99 ? "99+" : Unread.ToString(); } }
        internal DateTime Activity { get { return Messages.LastOrDefault()?.Utc ?? PairedUtc; } }
        public string Stamp
        {
            get
            {
                if (DeviceView || Activity == DateTime.MinValue) return "";
                var t = Activity.ToLocalTime();
                return t.ToString(t.Date == DateTime.Today ? "HH:mm" : "MM/dd");
            }
        }
        public string Detail
        {
            get
            {
                if (!DeviceView && Messages.Count > 0)
                {
                    var last = Messages[Messages.Count - 1];
                    string content = last.Content ?? "";
                    if (content.Length > 72) content = content.Substring(0, 72);
                    return (last.Mine ? "我： " : "") + (last.IsFile ? "[文件] " : "") + content.Replace('\r', ' ').Replace('\n', ' ');
                }
                if (Online) return Trusted ? "在线 · 已配对" : "在线 · 本次连接";
                if (Paused) return "已暂停自动重连";
                if (Trusted) return Nearby ? "附近 · 正在重连" : "离线 · 已配对";
                return Nearby && Address != null ? "待配对 · " + Address.Split(':')[0] : "历史记录";
            }
        }
        internal Conversation(string key, string name) { Key = key; Name = name; }
        internal void Changed() { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty)); }
        public event PropertyChangedEventHandler PropertyChanged;
    }
}
