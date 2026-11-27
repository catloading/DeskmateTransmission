using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using DeskLink.Transfer;
using Forms = System.Windows.Forms;

namespace DeskLink
{
    public partial class MainWindow
    {
        private bool CanSend(Conversation c) { return !closing && c != null && (c.Trusted || c.Online); }
        private static bool Durable(Conversation c) { return c.Trusted && (!c.Online || !c.LegacyLive); }

        private static async Task IdentifyLivePeer(Conversation c)
        {
            var link = c.Session;
            if (!c.Trusted || link == null || !link.Connected || c.LegacyLive || link.Reliable) return;
            bool supported = await link.Negotiate();
            if (c.Session == link && link.Connected) c.LegacyLive = !supported;
        }

        private static ChatItem Outgoing(Conversation c, string content)
        {
            return new ChatItem { Id = Guid.NewGuid(), Utc = DateTime.UtcNow, Mine = true, Peer = c.Name,
                DeviceId = DeviceHello.ValidId(c.Key) ? c.Key : null, Content = content };
        }

        private async void ObserveCapabilities(Conversation c, TransferSession link)
        {
            bool supported = await link.Negotiate();
            if (c.Session != link || closing || !link.Connected) return;
            c.LegacyLive = !supported;
            if (!supported)
            {
                foreach (var item in c.Messages.Where(m => m.Queued && !m.Busy && m.Problem == null))
                    item.Update("待发送 · 请将对方升级到 2.2 后补发");
                if (!await link.CapabilityChanged || c.Session != link || closing) return;
                c.LegacyLive = false;
            }
            Pump(c);
        }

        private void QueueItem(Conversation c, ChatItem item)
        {
            outbox.Save(item);
            item.Update(c.Online ? "排队中" : "待发送 · 等待设备上线");
            AddItem(c, item);
            Pump(c);
        }

        private async Task SendDraft()
        {
            var c = selected;
            string text = Draft.Text;
            if (!CanSend(c) || string.IsNullOrWhiteSpace(text)) return;
            var item = Outgoing(c, text);
            try
            {
                if (Wire.Utf8.GetByteCount(text) > Wire.MaxTextBytes) throw new IOException("文字不能超过 128 KB。");
                Draft.Clear();
                await IdentifyLivePeer(c);
                if (!CanSend(c)) throw new IOException("设备已断开，请重新选择。");
                if (Durable(c)) { QueueItem(c, item); return; }
                var link = c.Session;
                item.Update("发送中…", true); AddItem(c, item);
                c.LastText = c.LastText.ContinueWith(_ => link.SendText(item.Id, text), TaskScheduler.Default);
                await c.LastText;
                item.Complete(); Save(item);
            }
            catch (Exception ex)
            {
                if (!c.Messages.Contains(item))
                {
                    if (selected == c && Draft.Text.Length == 0) Draft.Text = text;
                    else if (selected != c && c.Draft.Length == 0) c.Draft = text;
                    else AddItem(c, item);
                }
                item.Update("发送未完成：" + ex.Message, failed: true); Notice(ex.Message);
            }
        }

        private void PickFiles(object sender, RoutedEventArgs e)
        {
            var c = selected;
            if (!CanSend(c)) return;
            var picker = new Microsoft.Win32.OpenFileDialog { Title = "选择要发送的文件", Multiselect = true, CheckFileExists = true };
            if (picker.ShowDialog(this) == true) EnqueueFiles(c, picker.FileNames);
        }

        private string ChooseFolder()
        {
            using (var picker = new Forms.FolderBrowserDialog { Description = "选择要发送的文件夹", ShowNewFolderButton = false })
                return picker.ShowDialog(new FolderOwner { Handle = new WindowInteropHelper(this).Handle }) == Forms.DialogResult.OK
                    ? picker.SelectedPath : null;
        }

        private void PickFolder(object sender, RoutedEventArgs e)
        {
            var c = selected;
            if (!CanSend(c)) return;
            string path = ChooseFolder();
            if (path != null) EnqueueFiles(c, new[] { path });
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Effects = CanSend(selected) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Handled = true;
            if (!CanSend(selected)) { Notice("请先选择一台已配对的设备。"); return; }
            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths != null) EnqueueFiles(selected, paths);
        }

        private async void EnqueueFiles(Conversation c, IEnumerable<string> paths)
        {
            foreach (string path in paths)
            {
                try { await QueuePath(c, path); }
                catch (Exception ex) { Notice("无法发送「" + Path.GetFileName(path) + "」：" + ex.Message); }
            }
        }

        private async Task<bool> QueuePath(Conversation c, string path)
        {
            if (!CanSend(c)) throw new IOException("请先与设备配对。");
            var item = Outgoing(c, Path.GetFileName(path));
            bool folder = Directory.Exists(path);
            await IdentifyLivePeer(c);
            if (!CanSend(c)) throw new IOException("设备已断开，请重新选择。");
            if (!Durable(c))
            {
                if (folder) throw new IOException("发送文件夹需要对方使用 2.2 或更新版本。");
                var link = c.Session;
                item.IsFile = true; item.FilePath = path; item.Size = new FileInfo(path).Length;
                item.Update("排队中", true); AddItem(c, item);
                try
                {
                    var progress = FileProgress(item);
                    await Task.Run(() => link.SendFile(item.Id, path, progress));
                    item.Complete(); Save(item);
                }
                catch (Exception ex) { item.Update("发送未完成：" + ex.Message, failed: true); }
                return true;
            }
            FolderPayload contents = null;
            if (folder)
            {
                contents = await Task.Run(() => new FolderPayload(path));
                if (!CanSend(c)) return false;
                if (MessageBox.Show(this, "将「" + item.Content + "」发送给「" + c.Name + "」？\n" +
                    contents.Count + " 个文件，共 " + ChatItem.FormatSize(contents.Bytes) + "。", "发送整个文件夹",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return false;
            }
            Notice("正在准备「" + item.Content + "」…");
            try
            {
                await Task.Run(() => outbox.Prepare(item, path, contents));
                if (!CanSend(c) || !c.Trusted) throw new IOException("设备配对已变更，请重新选择。");
                QueueItem(c, item);
                NoticeBox.Visibility = Visibility.Collapsed;
                return true;
            }
            catch { if (folder && !item.Queued) outbox.DiscardBundle(item.Id); throw; }
        }

        private static Action<long, long> FileProgress(ChatItem item)
        {
            IProgress<Tuple<long, long>> reporter = new Progress<Tuple<long, long>>(p =>
            {
                if (!item.Busy) return;
                item.Update(p.Item1 == p.Item2 ? "等待对方校验…" :
                    "发送中  " + ChatItem.FormatSize(p.Item1) + " / " + ChatItem.FormatSize(p.Item2), true, bytes: p.Item1);
            });
            return (n, total) => reporter.Report(Tuple.Create(n, total));
        }

        private void Pump(Conversation c)
        {
            if (closing || !c.Trusted || !c.Online || !c.Session.Reliable) return;
            if (!c.SendingQueuedText) Drain(c, false);
            if (!c.SendingQueuedFile) Drain(c, true);
        }

        private async void Drain(Conversation c, bool file)
        {
            var link = c.Session;
            if (file) c.SendingQueuedFile = true; else c.SendingQueuedText = true;
            try
            {
                while (!closing && c.Trusted && c.Session == link && link.Connected)
                {
                    var item = c.Messages.FirstOrDefault(i => i.Queued && i.IsFile == file && i.Problem == null);
                    if (item == null) break;
                    item.Update("发送中…", true);
                    var progress = FileProgress(item);
                    try
                    {
                        await Task.Run(() =>
                        {
                            if (file)
                                using (var input = outbox.OpenVerified(item)) link.SendDurableFile(item, input, progress);
                            else link.SendDurableText(item.Id, item.Content);
                        });
                        if (!item.Queued) continue;
                        store.Save(item); outbox.Remove(item); item.Complete();
                    }
                    catch (Exception ex)
                    {
                        if (!item.Queued) continue;
                        if (!link.Connected || c.Session != link)
                        {
                            item.Update("待发送 · 重连后继续"); break;
                        }
                        item.Problem = ex.Message;
                        try { outbox.Save(item); } catch (Exception saveError) { Notice(saveError.Message); }
                        item.Update("需要处理：" + item.Problem, failed: true);
                    }
                }
            }
            finally
            {
                if (file) c.SendingQueuedFile = false; else c.SendingQueuedText = false;
                if (c.Session != link) Pump(c);
            }
        }

        private void CancelItem(Conversation c, ChatItem item)
        {
            outbox.Remove(item); c.Messages.Remove(item); RefreshRoster();
        }

        private void CancelQueued(object sender, RoutedEventArgs e)
        {
            var item = (ChatItem)((Button)sender).DataContext;
            if (!item.CanCancel) return;
            try { CancelItem(selected, item); } catch (Exception ex) { Notice(ex.Message); }
        }

        private void RetryQueued(object sender, RoutedEventArgs e)
        {
            var item = (ChatItem)((Button)sender).DataContext;
            if (!item.CanRetry) return;
            try { item.Problem = null; outbox.Save(item); item.Update("待发送"); Pump(selected); }
            catch (Exception ex) { item.Problem = ex.Message; item.Update(ex.Message, failed: true); }
        }

        private async void ReselectQueued(object sender, RoutedEventArgs e)
        {
            var item = (ChatItem)((Button)sender).DataContext;
            var c = selected;
            if (!item.CanReselect) return;
            string path;
            if (item.IsFolder) path = ChooseFolder();
            else
            {
                var picker = new Microsoft.Win32.OpenFileDialog { Title = "重新选择要发送的文件", CheckFileExists = true };
                path = picker.ShowDialog(this) == true ? picker.FileName : null;
            }
            if (path == null) return;
            try
            {
                if (await Task.Run(() => outbox.SameSelectedSource(item, path)))
                {
                    if (!item.CanReselect || !CanSend(c)) return;
                    // Keep the transfer ID and original display name when a queued source was only moved.
                    item.FilePath = path; item.Problem = null; outbox.Save(item);
                    item.Update("待发送"); Pump(c); return;
                }
                if (item.CanReselect && await QueuePath(c, path) && item.Queued) CancelItem(c, item);
            }
            catch (Exception ex) { Notice(ex.Message); }
        }
    }
}
