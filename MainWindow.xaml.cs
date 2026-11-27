using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DeskLink.Transfer;
using Forms = System.Windows.Forms;

namespace DeskLink
{
    public partial class MainWindow : Window
    {
        private readonly LocalStore store;
        private readonly Dictionary<string, Conversation> conversations = new Dictionary<string, Conversation>();
        private readonly Queue<FileJob> files = new Queue<FileJob>();
        private readonly DispatcherTimer noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        private readonly DispatcherTimer rosterTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private DeviceNetwork network;
        private Conversation selected;
        private int seenRevision = -1;
        private bool sendingFiles, closing, readyToClose, bindingRoster, initialized;
        private EventWaitHandle activateWindow;
        private RegisteredWaitHandle activationWait;

        private sealed class FileJob
        {
            internal ChatItem Item;
            internal TransferSession Session;
        }

        public MainWindow() : this(new LocalStore()) { }

        internal MainWindow(LocalStore localStore)
        {
            store = localStore;
            InitializeComponent();
            LocalName.Text = Environment.MachineName;
            LocalName.ToolTip = LocalName.Text;
            UpdateFolder();
            noticeTimer.Tick += (s, e) => { NoticeBox.Visibility = Visibility.Collapsed; noticeTimer.Stop(); };
            rosterTimer.Tick += (s, e) => SyncDevices();
            try
            {
                foreach (var item in store.Load())
                    GetConversation(item.DeviceId ?? LegacyKey(item.Peer), item.Peer).Messages.Add(item);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Notice("读取本机记录失败：" + ex.Message); }
            initialized = true;
            if (conversations.Count == 0) DevicesTab.IsChecked = true;
            else RecentTab.IsChecked = true;
            RefreshRoster();
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            try
            {
                // Concurrent copies of the same profile would advertise the same certificate.
                bool first;
                using (var hash = SHA256.Create())
                {
                    string root = Path.GetFullPath(store.Root).TrimEnd('\\').ToUpperInvariant();
                    string key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(root))).Replace("-", "");
                    activateWindow = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DeskLink-" + key, out first);
                }
                if (!first) { activateWindow.Set(); Close(); return; }
                activationWait = ThreadPool.RegisterWaitForSingleObject(activateWindow, (state, timedOut) =>
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (closing) return;
                        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                        Activate();
                    })), null, Timeout.Infinite, false);
                var service = await Task.Run(() => new DeviceNetwork(store.Root, Environment.MachineName));
                if (closing) { service.Dispose(); return; }
                network = service;
                service.Connected += (peer, firstPair) =>
                {
                    if (Dispatcher.CheckAccess()) Attach(peer, firstPair);
                    else Dispatcher.BeginInvoke(new Action(() => Attach(peer, firstPair)));
                };
                service.StatusMessage += message => Dispatcher.BeginInvoke(new Action(() => Notice(message)));
                service.Start();
                SyncDevices();
                rosterTimer.Start();
            }
            catch (Exception ex) { if (!closing) Notice("设备自动重连未启动，仍可使用配对码连接：" + ex.Message); }
            if (!closing) { HostButton.IsEnabled = JoinButton.IsEnabled = true; UpdateView(); }
        }

        private static string LegacyKey(string name) { return "legacy:" + (name ?? "旧版设备"); }

        private Conversation GetConversation(string key, string name)
        {
            Conversation c;
            if (!conversations.TryGetValue(key, out c))
                conversations.Add(key, c = new Conversation(key, name ?? "另一台电脑"));
            return c;
        }

        private void SyncDevices()
        {
            if (network == null || closing || seenRevision == network.Revision) return;
            seenRevision = network.Revision;
            foreach (var c in conversations.Values) { c.Trusted = c.Nearby = c.Paused = false; c.PairAddress = c.Address = null; }
            foreach (var state in network.Snapshot())
            {
                var c = GetConversation(state.Id, state.Name);
                c.Name = state.Name; c.Trusted = state.Trusted; c.Nearby = state.Nearby;
                c.Paused = state.Paused; c.PairAddress = state.PairAddress; c.Address = state.Address; c.PairedUtc = state.PairedUtc;
            }
            foreach (var c in conversations.Values.Where(c => !c.Online && !c.Trusted && !c.Nearby &&
                c.Messages.Count == 0 && c.Draft.Length == 0 && c != selected).ToList()) conversations.Remove(c.Key);
            RefreshRoster();
        }

        private void TabChanged(object sender, RoutedEventArgs e) { if (initialized) RefreshRoster(); }

        private void RefreshRoster(Conversation prefer = null)
        {
            if (!initialized) return;
            bool devices = DevicesTab.IsChecked == true;
            foreach (var c in conversations.Values) { c.DeviceView = devices; c.Changed(); }
            var rows = devices
                ? conversations.Values.Where(c => c.Trusted || c.Nearby || c.Online)
                    .OrderByDescending(c => c.Trusted).ThenByDescending(c => c.Online).ThenBy(c => c.Name).ThenBy(c => c.Key).ToList()
                : conversations.Values.Where(c => c.Messages.Count > 0 || c.Trusted || c.Online)
                    .OrderByDescending(c => c.Activity).ThenBy(c => c.Name).ThenBy(c => c.Key).ToList();
            // Reordering by activity must keep the same device selected.
            var next = prefer ?? selected;
            if (next == null || !rows.Contains(next)) next = rows.FirstOrDefault();
            bindingRoster = true;
            try
            {
                if (!rows.SequenceEqual(Roster.Items.Cast<Conversation>())) Roster.ItemsSource = rows;
                Roster.SelectedItem = next;
            }
            finally { bindingRoster = false; }
            RosterEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RosterEmpty.Text = devices ? "等待同一局域网内的电脑上线…" : "配对后的会话会出现在这里。";
            SelectConversation(next);
        }

        private void RosterSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!bindingRoster && initialized) SelectConversation(Roster.SelectedItem as Conversation);
        }

        private void SelectConversation(Conversation c)
        {
            if (selected != c)
            {
                if (selected != null) selected.Draft = Draft.Text;
                selected = c;
                ChatList.ItemsSource = c?.Messages;
                Draft.Text = c?.Draft ?? "";
                Draft.CaretIndex = Draft.Text.Length;
                if (c != null) { c.Unread = 0; c.Changed(); }
                Dispatcher.BeginInvoke(new Action(ChatScroll.ScrollToEnd), DispatcherPriority.Background);
            }
            UpdateView();
        }

        private void Notice(string message)
        {
            NoticeText.Text = message;
            NoticeBox.Visibility = Visibility.Visible;
            noticeTimer.Stop(); noticeTimer.Start();
        }

        private bool Online { get { return selected != null && selected.Online; } }

        private void UpdateView()
        {
            var c = selected;
            bool online = Online, trusted = c != null && c.Trusted, nearby = c != null && c.Nearby;
            ChatTitle.Text = c?.DisplayName ?? "传输会话";
            ChatTitle.ToolTip = c?.DeviceTip ?? ChatTitle.Text;
            StatusLabel.Text = online ? "已加密连接" : c == null ? "未连接" : c.Paused ? "已暂停" : nearby ? "附近" : "离线";
            StatusDot.Fill = online ? (Brush)FindResource("Accent") : Brushes.SlateGray;
            StatusBadge.Background = new SolidColorBrush(online ? Color.FromRgb(218, 237, 220) : Color.FromRgb(233, 238, 231));
            ConversationHint.Text = online ? (trusted ? "已配对设备 · 局域网直连" : "本次连接 · 对方使用旧版本") :
                c == null ? "选择一台电脑，查看消息和文件" : c.Paused ? "已暂停自动重连，可在右侧恢复" :
                trusted ? "对方打开同桌传后会自动重连" : nearby ? "首次连接需要对方的配对码" : "本机保存的会话记录";
            HeaderAction.Visibility = online || trusted || nearby ? Visibility.Visible : Visibility.Collapsed;
            HeaderAction.Content = online ? "断开" : trusted ? (c.Paused ? "恢复连接" : "立即重连") : "配对此设备";
            MoreButton.Visibility = trusted ? Visibility.Visible : Visibility.Collapsed;
            bool hasItems = c != null && c.Messages.Count > 0;
            EmptyView.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
            ChatScroll.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
            EmptyTitle.Text = online ? "发送第一条消息" : c == null ? "连接另一台电脑" :
                trusted ? (c.Paused ? "连接已暂停" : "等待设备上线") : "与这台电脑配对";
            EmptyHint.Text = online ? "输入文字，或将文件拖入窗口发送给对方。" :
                c == null ? "两台电脑打开同桌传，首次使用配对码连接。" :
                trusted ? (c.Paused ? "点击「恢复连接」继续使用这台设备。" : "两台电脑在同一局域网打开同桌传即可重连。") :
                "在对方电脑显示配对码，再点「配对此设备」。";
            Draft.IsEnabled = c != null;
            DraftHint.Text = online ? "输入消息…" : c == null ? "选择设备后输入消息…" : "可先写草稿，连接后发送…";
            FileButton.IsEnabled = online;
            SendButton.IsEnabled = online && !string.IsNullOrWhiteSpace(Draft.Text);
        }

        private void CreateConnection(object sender, RoutedEventArgs e) { Pair(true); }
        private void JoinConnection(object sender, RoutedEventArgs e) { Pair(false); }

        private void Pair(bool create, Conversation target = null)
        {
            if (closing) return;
            var dialog = new PairWindow(create, network, target?.PairAddress, target?.Key) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.ConnectedPeer == null) return;
            var peer = dialog.ConnectedPeer;
            if (network == null || peer.Device == null) { Attach(peer, true); return; }
            if (peer.Device.Id == network.Local.Id) { peer.Dispose(); Notice("请选择另一台电脑。"); return; }
            try
            {
                network.Remember(peer);
                FocusConversation(GetConversation(peer.Device.Id, peer.Name));
            }
            catch (Exception ex) { Notice("保存配对失败：" + ex.Message); }
        }

        private void FocusConversation(Conversation c)
        {
            RecentTab.IsChecked = true;
            RefreshRoster(c);
            Roster.ScrollIntoView(c);
            Draft.Focus();
        }

        private void Attach(Peer peer, bool firstPair)
        {
            if (closing) { peer.Dispose(); network?.Release(peer); return; }
            var c = GetConversation(peer.Device?.Id ?? LegacyKey(peer.Name), peer.Name);
            if (c.Online) { peer.Dispose(); network?.Release(peer); return; }
            var link = new TransferSession(peer, () => store.Folder);
            c.Name = peer.Name;
            c.Session = link;
            link.ItemReceived += update =>
            {
                if (Dispatcher.HasShutdownStarted) return;
                if (update.State == ReceiveState.Complete)
                    Dispatcher.Invoke(() => Receive(c, update), DispatcherPriority.Normal);
                else
                    Dispatcher.BeginInvoke(new Action(() => Receive(c, update)), DispatcherPriority.Normal);
            };
            link.Ended += reason => Dispatcher.BeginInvoke(new Action(() =>
            {
                network?.Release(peer);
                if (c.Session != link) return;
                c.Session = null;
                if (closing) return;
                RefreshRoster();
                if (selected == c && !c.Paused) Notice(reason);
            }));
            SyncDevices();
            if (firstPair) FocusConversation(c); else RefreshRoster();
            NoticeBox.Visibility = Visibility.Collapsed;
            link.Start();
        }

        private void DeviceAction(object sender, RoutedEventArgs e)
        {
            var c = selected;
            if (c == null || closing) return;
            if (c.Online)
            {
                if (!ConfirmInterruption(c.Messages)) return;
                if (c.Trusted) network.Pause(c.Key);
                c.Session.Dispose();
            }
            else if (c.Trusted) { network.Resume(c.Key); Notice("正在等待设备连接，请确认两边都已打开同桌传。"); }
            else if (c.Nearby) Pair(false, c);
            SyncDevices();
        }

        private void DeviceOptions(object sender, RoutedEventArgs e)
        {
            MoreButton.ContextMenu.PlacementTarget = MoreButton;
            MoreButton.ContextMenu.IsOpen = true;
        }

        private void ForgetDevice(object sender, RoutedEventArgs e)
        {
            var c = selected;
            if (c == null || !c.Trusted) return;
            if (MessageBox.Show(this, "取消与「" + c.Name + "」的配对？下次连接需要重新输入配对码。", "取消配对",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            try { network.Forget(c.Key); c.Session?.Dispose(); SyncDevices(); }
            catch (Exception ex) { Notice("取消配对失败：" + ex.Message); }
        }

        private bool ConfirmInterruption(IEnumerable<ChatItem> items)
        {
            return !items.Any(m => m.Busy) || MessageBox.Show(this, "还有传输正在进行。现在断开会中止未完成的传输。",
                "断开连接？", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        }

        private async void OnClosing(object sender, CancelEventArgs e)
        {
            if (readyToClose) return;
            if (closing || !ConfirmInterruption(conversations.Values.SelectMany(c => c.Messages))) { e.Cancel = true; return; }
            closing = true; rosterTimer.Stop();
            var active = conversations.Values.Select(c => c.Session).Where(s => s != null).ToArray();
            network?.Dispose();
            foreach (var link in active) link.Dispose();
            if (active.Length == 0) return;
            e.Cancel = true;
            await Task.WhenAll(active.Select(s => s.Finished));
            readyToClose = true;
            await Dispatcher.InvokeAsync(Close, DispatcherPriority.Background);
        }

        private void OnClosed(object sender, EventArgs e)
        {
            closing = true; rosterTimer.Stop(); noticeTimer.Stop();
            activationWait?.Unregister(null);
            activateWindow?.Dispose();
        }

        private void AddItem(Conversation c, ChatItem item)
        {
            bool follow = c == selected && (item.Mine || ChatScroll.ScrollableHeight - ChatScroll.VerticalOffset < 45);
            if (c.Messages.Count >= 200)
            {
                var oldest = c.Messages.FirstOrDefault(m => !m.Busy);
                if (oldest != null) c.Messages.Remove(oldest);
            }
            c.Messages.Add(item);
            if (!item.Mine && c != selected) c.Unread++;
            RefreshRoster();
            if (follow) Dispatcher.BeginInvoke(new Action(ChatScroll.ScrollToEnd), DispatcherPriority.Background);
        }

        private void Save(ChatItem item)
        {
            try { store.Save(item); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                ex is System.Security.Cryptography.CryptographicException)
            { Notice("已完成传输，本机记录保存失败：" + ex.Message); }
        }

        private void Receive(Conversation c, Received update)
        {
            ChatItem item;
            if (!c.Incoming.TryGetValue(update.Id, out item))
            {
                item = new ChatItem { Id = update.Id, Peer = c.Name, DeviceId = DeviceHello.ValidId(c.Key) ? c.Key : null,
                    Utc = DateTime.UtcNow, IsFile = update.IsFile, Content = update.Content, Size = update.Size };
                if (update.State == ReceiveState.Started) c.Incoming.Add(update.Id, item);
                AddItem(c, item);
            }
            switch (update.State)
            {
                case ReceiveState.Started:
                case ReceiveState.Progress:
                    item.Update("接收中  " + ChatItem.FormatSize(update.Bytes) + " / " + item.SizeText, true, bytes: update.Bytes);
                    break;
                case ReceiveState.Complete:
                    item.FilePath = update.Path;
                    item.Complete();
                    c.Incoming.Remove(update.Id);
                    Save(item);
                    break;
                case ReceiveState.Failed:
                    item.Update(update.Error, failed: true);
                    c.Incoming.Remove(update.Id);
                    break;
            }
        }

        private void DraftChanged(object sender, TextChangedEventArgs e)
        {
            if (DraftHint == null || SendButton == null) return;
            DraftHint.Visibility = Draft.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            SendButton.IsEnabled = Online && !string.IsNullOrWhiteSpace(Draft.Text);
        }

        private async void DraftKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
            e.Handled = true;
            if (SendButton.IsEnabled) await SendDraft();
        }

        private async void SendMessage(object sender, RoutedEventArgs e) { await SendDraft(); }

        private async Task SendDraft()
        {
            string text = Draft.Text;
            var c = selected;
            var link = c?.Session;
            if (link == null || !link.Connected || string.IsNullOrWhiteSpace(text)) return;
            var item = new ChatItem { Id = Guid.NewGuid(), Utc = DateTime.UtcNow, Mine = true, Peer = link.PeerName,
                DeviceId = DeviceHello.ValidId(c.Key) ? c.Key : null, Content = text };
            item.Update("发送中…", true);
            AddItem(c, item);
            Draft.Clear(); Draft.Focus();
            try
            {
                // Keep rapid consecutive messages in compose order while files stream independently.
                c.LastText = c.LastText.ContinueWith(_ => link.SendText(item.Id, text), TaskScheduler.Default);
                await c.LastText;
                item.Complete(); Save(item);
            }
            catch (Exception ex) { item.Update("发送未确认：" + ex.Message, failed: true); }
        }

        private void PickFiles(object sender, RoutedEventArgs e)
        {
            if (!Online) return;
            var picker = new Microsoft.Win32.OpenFileDialog { Title = "选择要发送的文件", Multiselect = true, CheckFileExists = true };
            var target = selected;
            if (picker.ShowDialog(this) == true) EnqueueFiles(target, picker.FileNames);
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Effects = Online ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Handled = true;
            if (!Online) { Notice("请先连接另一台电脑。"); return; }
            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths != null) EnqueueFiles(selected, paths);
        }

        private async void EnqueueFiles(Conversation c, IEnumerable<string> paths)
        {
            var link = c?.Session;
            if (link == null || !link.Connected) { Notice("设备已离线，重新连接后可发送文件。"); return; }
            foreach (string path in paths)
            {
                if (Directory.Exists(path)) { Notice("发送文件夹时，请先将它压缩成一个文件。"); continue; }
                var item = new ChatItem { Id = Guid.NewGuid(), Utc = DateTime.UtcNow, Mine = true, IsFile = true,
                    Peer = link.PeerName, DeviceId = DeviceHello.ValidId(c.Key) ? c.Key : null, FilePath = path, Content = Path.GetFileName(path) };
                try { item.Size = new FileInfo(path).Length; }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                { item.Update(ex.Message, failed: true); AddItem(c, item); continue; }
                item.Update("排队中", true);
                AddItem(c, item);
                files.Enqueue(new FileJob { Item = item, Session = link });
            }
            if (sendingFiles) return;
            sendingFiles = true;
            try
            {
                while (files.Count > 0)
                {
                    var job = files.Dequeue();
                    var item = job.Item;
                    try
                    {
                        item.Update("等待对方接收…", true);
                        var progress = new Progress<Tuple<long, long>>(p =>
                        {
                            if (!item.Busy) return;
                            item.Size = p.Item2;
                            item.Update(p.Item1 == p.Item2 ? "等待对方校验…" : "发送中  " + ChatItem.FormatSize(p.Item1) + " / " + item.SizeText,
                                true, bytes: p.Item1);
                        });
                        await Task.Run(() => job.Session.SendFile(item.Id, item.FilePath, (sent, total) =>
                            ((IProgress<Tuple<long, long>>)progress).Report(Tuple.Create(sent, total))));
                        item.Complete(); Save(item);
                    }
                    catch (Exception ex) { item.Update("发送未完成：" + ex.Message, failed: true); }
                }
            }
            finally { sendingFiles = false; }
        }

        private void CopyMessage(object sender, RoutedEventArgs e)
        {
            var item = (ChatItem)((Button)sender).DataContext;
            try { Clipboard.SetText(item.Content); Notice("消息已复制。"); }
            catch (ExternalException) { Notice("剪贴板正忙，请稍后重试。"); }
        }

        private void UpdateFolder()
        {
            var directory = new DirectoryInfo(store.Folder);
            var parent = directory.Parent;
            FolderHint.Text = parent == null || parent.Parent == null ? directory.FullName :
                (parent.Name.Equals("Downloads", StringComparison.OrdinalIgnoreCase) ? "下载" : parent.Name) + "  /  " + directory.Name;
            FolderHint.ToolTip = FolderButton.ToolTip = store.Folder;
        }

        private void OpenFolder(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(store.Folder);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + store.Folder + "\"") { UseShellExecute = false });
            }
            catch (Exception ex) { Notice("打开文件夹失败：" + ex.Message); }
        }

        private void RevealFile(object sender, RoutedEventArgs e)
        {
            var item = (ChatItem)((Button)sender).DataContext;
            if (!File.Exists(item.FilePath)) { Notice("文件已被移动或删除。"); return; }
            try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + item.FilePath + "\"") { UseShellExecute = false }); }
            catch (Exception ex) { Notice("打开文件位置失败：" + ex.Message); }
        }

        private sealed class FolderOwner : Forms.IWin32Window { public IntPtr Handle { get; set; } }
        private void ChangeFolder(object sender, RoutedEventArgs e)
        {
            using (var picker = new Forms.FolderBrowserDialog { Description = "选择收到的文件的保存位置", SelectedPath = store.Folder })
            {
                if (picker.ShowDialog(new FolderOwner { Handle = new WindowInteropHelper(this).Handle }) != Forms.DialogResult.OK) return;
                try { store.SetFolder(picker.SelectedPath); UpdateFolder(); Notice("接下来收到的文件将保存到新位置。"); }
                catch (Exception ex) { Notice("保存接收位置失败：" + ex.Message); }
            }
        }
    }
}
