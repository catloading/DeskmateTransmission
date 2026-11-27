using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace DeskLink
{
    public partial class MainWindow
    {
        internal void ShowConversation(Conversation c)
        {
            if (closing) return;
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            if (c != null) FocusConversation(c);
            if (selected != null) { selected.Unread = 0; selected.Changed(); }
        }

        internal void Quit()
        {
            if (closing) return;
            exitRequested = true;
            if (conversations.Values.Any(c => c.Messages.Any(m => m.Busy))) ShowConversation(null);
            Close();
        }

        private void SessionEnding(object sender, SessionEndingCancelEventArgs e) { endingSession = exitRequested = true; }

        internal bool PasteData(Conversation c, IDataObject data)
        {
            if (data == null || !CanSend(c)) return false;
            var files = data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0) { EnqueueFiles(c, files); return true; }
            BitmapSource bitmap = data.GetData(DataFormats.Bitmap) as BitmapSource;
            if (bitmap == null)
            {
                var png = data.GetData("PNG") as Stream;
                if (png != null)
                {
                    if (png.CanSeek) png.Position = 0;
                    bitmap = BitmapFrame.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                }
            }
            if (bitmap == null) return false;
            var preview = new ImagePreviewWindow(bitmap, c.Name) { Owner = this };
            if (preview.ShowDialog() == true)
            {
                string folder = Path.Combine(store.Root, "screenshots");
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "截图-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) encoder.Save(file);
                EnqueueFiles(c, new[] { path });
            }
            return true;
        }

        private async void ShowFiles(object sender, RoutedEventArgs e)
        {
            var c = selected;
            if (c == null || closing) return;
            try
            {
                var records = await Task.Run(() => store.Load());
                if (closing) return;
                int previousRetention = store.RetentionDays;
                new FileHistoryWindow(store, c.Key, c.DisplayName, records) { Owner = this }.ShowDialog();
                if (previousRetention == store.RetentionDays) return;
                var displayed = conversations.Values.SelectMany(conv => conv.Messages).Where(i => !i.Queued && !i.Busy && !i.Failed).ToList();
                var retained = new HashSet<string>((await Task.Run(() => store.Load())).Select(HistoryKey));
                foreach (var conv in conversations.Values)
                    foreach (var item in displayed.Where(i => (i.DeviceId ?? LegacyKey(i.Peer)) == conv.Key && !retained.Contains(HistoryKey(i))))
                        conv.Messages.Remove(item);
                RefreshRoster();
            }
            catch (Exception ex) { Notice("读取会话文件失败：" + ex.Message); }
        }

        private static string HistoryKey(ChatItem i) { return (i.DeviceId ?? LegacyKey(i.Peer)) + i.Id + i.Mine; }
    }
}
