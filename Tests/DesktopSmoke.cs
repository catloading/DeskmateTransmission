using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeskLink.Transfer;

namespace DeskLink.Tests
{
    internal static class DesktopSmoke
    {
        private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        private static void PumpUntil(Func<bool> condition)
        {
            var clock = Stopwatch.StartNew();
            while (!condition())
            {
                if (clock.Elapsed.TotalSeconds > 12) throw new TimeoutException("Desktop condition timed out");
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame); Thread.Sleep(10);
            }
        }

        private static void Preview(bool send)
        {
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                var window = Application.Current.Windows.OfType<ImagePreviewWindow>().Single();
                Check(((BitmapSource)window.Preview.Source).PixelWidth == 8, "Screenshot preview missing");
                window.DialogResult = send;
            }));
        }

        internal static void Run(string root)
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var store = new LocalStore(Path.Combine(root, "desktop"));
            var receiverStore = new LocalStore(Path.Combine(root, "desktop-peer"));
            receiverStore.SetFolder(Path.Combine(root, "desktop-in"));
            string device, localId;
            using (var certificate = new DeviceBook(receiverStore.Root).OpenIdentity()) device = DeviceHello.Fingerprint(certificate);
            using (var certificate = new DeviceBook(store.Root).OpenIdentity()) localId = DeviceHello.Fingerprint(certificate);
            new DeviceBook(receiverStore.Root).Save(new SavedDevice { Id = localId, Name = Environment.MachineName, Address = "127.0.0.1:1", PairedUtc = DateTime.UtcNow });
            new DeviceBook(store.Root).Save(new SavedDevice { Id = device, Name = "测试笔记本", Address = "127.0.0.1:1", PairedUtc = DateTime.UtcNow });
            var main = new MainWindow(store) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -16000, Top = -16000 };
            try
            {
                app.MainWindow = main; main.Show();
                PumpUntil(() => main.Roster.Items.Cast<Conversation>().Any(c => c.Key == device));
                var peer = main.Roster.Items.Cast<Conversation>().Single(c => c.Key == device);
                main.Roster.SelectedItem = peer;
                main.Draft.Text = "窗口离线消息";
                Check(main.SendButton.IsEnabled && main.FileButton.IsEnabled && main.FolderSend.IsEnabled, "Offline composer disabled");
                main.SendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var box = new Outbox(store.Root);
                Check(box.Load().Any(i => i.Content == "窗口离线消息"), "Offline text did not reach disk from the UI");
                var data = new DataObject();
                var image = BitmapSource.Create(8, 4, 96, 96, PixelFormats.Bgra32, null, new byte[8 * 4 * 4], 8 * 4);
                data.SetData(DataFormats.Bitmap, image);
                Preview(false); Check(main.PasteData(peer, data) && box.Load().Count == 1, "Cancelled paste enqueued a file");
                Preview(true); Check(main.PasteData(peer, data), "Image paste not handled");
                PumpUntil(() => peer.Messages.Any(i => i.IsFile && i.Queued));
                string screenshot = box.Load().Single(i => i.IsFile).FilePath;
                using (var stream = File.OpenRead(screenshot))
                {
                    var png = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    Check(png.Frames[0].PixelWidth == 8 && png.Frames[0].PixelHeight == 4, "Clipboard PNG dimensions differ");
                }
                var copied = new DataObject(); copied.SetData(DataFormats.FileDrop, new[] { screenshot });
                Check(main.PasteData(peer, copied), "Explorer paste not handled");
                PumpUntil(() => box.Load().Count == 3);
                var words = new DataObject(DataFormats.UnicodeText, "keep normal paste");
                Check(!main.PasteData(peer, words), "Ordinary text paste was intercepted");
                main.Close(); Check(!main.IsVisible && Application.Current.Windows.Cast<Window>().Contains(main), "Close did not hide to tray");
                main.ShowConversation(peer); Check(main.IsVisible && (Conversation)main.Roster.SelectedItem == peer, "Tray restore did not open conversation");
                CheckHistory(store, device);
                main.Quit();
                main = new MainWindow(new LocalStore(store.Root)) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -16000, Top = -16000 };
                app.MainWindow = main; main.Show();
                PumpUntil(() => main.Roster.Items.Cast<Conversation>().Any(c => c.Key == device && c.Trusted));
                var restored = main.Roster.Items.Cast<Conversation>().Single(c => c.Key == device);
                Check(restored.Messages.Count(i => i.Queued) == 3, "Desktop restart lost pending messages");
                using (var network = new DeviceNetwork(receiverStore.Root, "测试笔记本"))
                {
                    TransferSession remote = null;
                    network.Connected += (connection, first) =>
                    {
                        var session = new TransferSession(connection, () => receiverStore.Folder, receiverStore);
                        session.Ended += message => network.Release(connection);
                        remote = session; session.Start();
                    };
                    network.Start();
                    try
                    {
                        PumpUntil(() => restored.Messages.All(i => !i.Queued) && box.Load().Count == 0);
                        var received = receiverStore.Load();
                        Check(received.Count == 3 && received.Count(i => i.IsFile) == 2 &&
                            received.Single(i => !i.IsFile).Content == "窗口离线消息", "UI queue did not drain automatically on reconnect");
                        Check(Directory.GetFiles(receiverStore.Folder, "*.png").Length == 2, "Two different sends of the same filename were lost");
                        main.Close();
                        var reply = Task.Run(() => remote.SendDurableText(Guid.NewGuid(), "后台收件"));
                        PumpUntil(() => reply.IsCompleted);
                        reply.GetAwaiter().GetResult();
                        Check(!main.IsVisible && restored.Messages.Any(i => i.Content == "后台收件"), "Tray background receive failed");
                        main.Tray.OpenActiveNotice();
                        Check(main.IsVisible && (Conversation)main.Roster.SelectedItem == restored, "Receipt notification did not reopen the sender conversation");
                    }
                    finally { remote?.Dispose(); remote?.Finished.Wait(3000); }
                }
                Console.WriteLine("PASS: actual WPF controls, offline send, clipboard bitmap preview/cancel and PNG, file-drop clipboard, ordinary paste, close-to-tray and conversation restore, file filters and retention; actual desktop queue automatically drained after restart; background delivery and notification activation opened the sender conversation.");
            }
            finally { main.Quit(); app.Shutdown(); }
        }

        private static void CheckHistory(LocalStore store, string device)
        {
            foreach (var item in new[]
            {
                new ChatItem { Content = "报告.PDF", DeviceId = device, Utc = DateTime.UtcNow },
                new ChatItem { Content = "报告.png", DeviceId = device, Utc = DateTime.UtcNow.AddDays(-32) },
                new ChatItem { Content = "项目", DeviceId = device, Utc = DateTime.UtcNow, IsFolder = true },
                new ChatItem { Content = "报告.pdf", DeviceId = new string('B', 64), Utc = DateTime.UtcNow }
            })
            {
                item.Id = Guid.NewGuid(); item.IsFile = true; item.Peer = "乙"; store.Save(item);
            }
            var window = new FileHistoryWindow(store, device, "乙", store.Load());
            try
            {
                Check(window.Results.Items.Count == 3, "Conversation isolation failed");
                window.Query.Text = "报告"; Check(window.Results.Items.Count == 2, "Name filter failed");
                window.FileType.Text = ".pdf"; Check(window.Results.Items.Count == 1, "Type filter failed");
                window.FileType.Text = ""; window.Day.SelectedDate = DateTime.Now.Date.AddDays(-32);
                Check(window.Results.Items.Count == 1, "Date filter failed");
                window.Day.SelectedDate = null; window.Query.Text = ""; window.FileType.Text = "文件夹";
                Check(window.Results.Items.Count == 1, "Folder filter failed");
                window.Retention.SelectedIndex = 2;
                var reopened = new LocalStore(store.Root);
                Check(reopened.RetentionDays == 30 && reopened.Load().All(i => i.Content != "报告.png"), "Retention not persisted or applied");
            }
            finally { window.Close(); }
        }
    }
}
