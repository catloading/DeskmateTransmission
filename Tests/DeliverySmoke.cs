using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DeskLink.Transfer;

namespace DeskLink.Tests
{
    internal static class DeliverySmoke
    {
        private const string Large = "断点.bin";

        private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        private static void Wait(Func<bool> check, string what)
        {
            Check(SpinWait.SpinUntil(check, TimeSpan.FromSeconds(35)), what);
        }

        private sealed class Node : IDisposable
        {
            internal readonly DeviceNetwork Network;
            internal readonly LocalStore Store;
            internal volatile TransferSession Link;
            internal event Action<TransferSession, Received> Receiving;
            internal Node(string root, string name)
            {
                Store = new LocalStore(Path.Combine(root, name));
                Store.SetFolder(Path.Combine(root, name + "-in"));
                Network = new DeviceNetwork(Store.Root, name);
                Network.Connected += (peer, first) =>
                {
                    var link = new TransferSession(peer, () => Store.Folder, Store);
                    link.ItemReceived += r => Receiving?.Invoke(link, r);
                    link.Ended += why => { Interlocked.CompareExchange(ref Link, null, link); Network.Release(peer); };
                    link.Start();
                    Link = link;
                };
                Network.Start();
            }
            internal TransferSession Ready()
            {
                Wait(() => Link != null && Link.Reliable, "Reliable TLS handshake timed out");
                return Link;
            }
            public void Dispose()
            {
                var link = Link; Network.Dispose(); link?.Dispose(); link?.Finished.Wait(5000);
            }
        }

        internal static void Guest(string root, string phase)
        {
            using (var b = new Node(root, "receiver"))
            {
                b.Receiving += (link, item) =>
                {
                    bool cut = phase == "cut" && item.State == ReceiveState.Progress && item.Bytes > 0 && item.Bytes < item.WireSize;
                    bool lost = phase == "lost" && item.State == ReceiveState.Complete;
                    if (item.Content == Large && (cut || lost))
                    {
                        b.Network.Pause(b.Network.Snapshot().Single(d => d.Trusted).Id);
                        link.Dispose();
                        File.WriteAllText(Path.Combine(root, phase + ".signal"), item.Bytes.ToString());
                    }
                };
                if (phase == "pair")
                {
                    var address = File.ReadAllLines(Path.Combine(root, "invite-new"));
                    b.Network.Remember(Pairing.Join(address[0], address[1], "receiver", CancellationToken.None, b.Network.Local));
                }
                Wait(() => File.Exists(Path.Combine(root, phase + ".stop")), "Receiver was not stopped: " + phase);
            }
        }

        private static void WithGuest(string root, string phase, Action work)
        {
            using (var child = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,
                "delivery \"" + root + "\" " + phase) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true }))
            {
                try { work(); }
                finally
                {
                    File.WriteAllText(Path.Combine(root, phase + ".stop"), "");
                    if (!child.WaitForExit(10000)) { child.Kill(); child.WaitForExit(); }
                    if (child.ExitCode != 0) throw new Exception("Receiver " + phase + ": " + child.StandardError.ReadToEnd());
                }
            }
        }

        private sealed class SlowFile : FileStream
        {
            internal SlowFile(string path) : base(path, FileMode.Open, FileAccess.Read, FileShare.Read) { }
            public override int Read(byte[] bytes, int offset, int count) { Thread.Sleep(4); return base.Read(bytes, offset, count); }
        }

        private static ChatItem NewItem(string device, string name)
        {
            return new ChatItem { Mine = true, DeviceId = device, Id = Guid.NewGuid(), Peer = "receiver", Content = name, Utc = DateTime.UtcNow };
        }

        private static void ReplayFile(TransferSession link, Outbox box, ChatItem item, Action<long, long> progress = null)
        {
            using (var file = box.OpenVerified(item)) link.SendDurableFile(item, file, progress);
        }

        internal static void Run(string root)
        {
            string device;
            using (var a = new Node(root, "sender"))
            using (var invite = new Invitation("sender", a.Network.Local))
            {
                File.WriteAllLines(Path.Combine(root, "invite-new"), new[] { "127.0.0.1:" + invite.Port, invite.Code });
                WithGuest(root, "pair", () =>
                {
                    Check(invite.Accepted.Wait(30000), "First pairing failed");
                    a.Network.Remember(invite.Accepted.Result);
                    a.Ready().SendDurableText(Guid.NewGuid(), "已配对");
                });
                device = a.Network.Snapshot().Single(d => d.Trusted).Id;
            }
            var box = new Outbox(Path.Combine(root, "sender"));
            var text = NewItem(device, "离线消息🙂");
            box.Save(text);
            string path = Path.Combine(root, Large);
            var random = new byte[12 * 1024 * 1024 + 17];
            new Random(223).NextBytes(random); File.WriteAllBytes(path, random);
            var large = NewItem(device, Large); box.Prepare(large, path); box.Save(large);
            string folder = Path.Combine(root, "资料");
            Directory.CreateDirectory(Path.Combine(folder, "子目录", "空"));
            File.WriteAllText(Path.Combine(folder, "子目录", "说明.txt"), "保留目录与内容🙂");
            var bundle = NewItem(device, "资料"); var manifest = new FolderPayload(folder);
            Check(manifest.Count == 1 && manifest.Bytes > 0, "Folder preview incorrect");
            box.Prepare(bundle, folder, manifest); box.Save(bundle);
            var reopened = new Outbox(Path.Combine(root, "sender")); var queued = reopened.Load();
            Check(queued.Count == 3 && queued.All(i => i.Queued), "Offline queue did not survive restart");
            text = queued.Single(i => !i.IsFile); large = queued.Single(i => i.Content == Large);
            bundle = queued.Single(i => i.IsFolder);
            using (var a = new Node(root, "sender"))
            {
                WithGuest(root, "cut", () =>
                {
                    var link = a.Ready();
                    link.SendDurableText(text.Id, text.Content);
                    ReplayFile(link, reopened, bundle);
                    bool interrupted = false;
                    try { using (var slow = new SlowFile(path)) link.SendDurableFile(large, slow, null); }
                    catch (IOException) { interrupted = true; }
                    Check(interrupted, "Expected a mid-file disconnect");
                    Wait(() => File.Exists(Path.Combine(root, "cut.signal")), "Partial transfer signal missing");
                });
                long partialSize = new FileInfo(Directory.GetFiles(Path.Combine(root, "receiver-in"), "*.part").Single()).Length;
                Check(partialSize > 0 && partialSize < random.Length, "Partial transfer was not retained");
                WithGuest(root, "lost", () =>
                {
                    var link = a.Ready();
                    long first = -1; bool lost = false;
                    try { ReplayFile(link, reopened, large, (sent, total) => { if (first == -1) first = sent; }); }
                    catch (IOException) { lost = true; }
                    Wait(() => File.Exists(Path.Combine(root, "lost.signal")), "Expected lost receipt");
                    Check(lost && first == partialSize, "The transfer did not resume at the verified offset");
                });
                WithGuest(root, "finish", () =>
                {
                    var link = a.Ready();
                    ReplayFile(link, reopened, large, (n, total) => { throw new Exception("An acknowledged file was sent again"); });
                    ReplayFile(link, reopened, bundle, (n, total) => { throw new Exception("Folder was sent twice"); });
                    link.SendDurableText(text.Id, text.Content);
                    var history = new LocalStore(Path.Combine(root, "receiver")).Load();
                    foreach (var outgoing in queued)
                    {
                        Check(history.Count(i => i.Id == outgoing.Id) == 1, "Duplicated history for " + outgoing.Content);
                        reopened.Remove(outgoing);
                    }
                    string inbox = Path.Combine(root, "receiver-in");
                    Check(File.ReadAllBytes(Path.Combine(inbox, Large)).SequenceEqual(random), "Resumed bytes differ");
                    Check(Directory.GetFiles(inbox, "*.bin").Length == 1 && Directory.GetDirectories(inbox).Length == 1, "Duplicate output");
                    Check(Directory.Exists(Path.Combine(inbox, "资料", "子目录", "空")) &&
                        File.ReadAllText(Path.Combine(inbox, "资料", "子目录", "说明.txt")) == "保留目录与内容🙂", "Folder layout lost");
                    Check(!Directory.EnumerateFiles(inbox, "*.part").Any() && reopened.Load().Count == 0, "Completion cleanup failed");
                    CorruptPrefix(root, a, device, link, reopened);
                });
            }
            string moved = Path.Combine(root, "移到这里.bin");
            File.Move(path, moved);
            Check(reopened.SameSelectedSource(large, moved), "A moved identical source lost its transfer identity");
            File.Move(moved, path);
            File.WriteAllBytes(path, new byte[random.Length]);
            Check(!reopened.SameSelectedSource(large, path), "Different bytes retained an old transfer identity");
            try { using (reopened.OpenVerified(large)) { } throw new Exception("Changed source accepted"); } catch (IOException) { }
            File.Delete(path);
            try { using (reopened.OpenVerified(large)) { } throw new Exception("Moved source accepted"); } catch (IOException) { }
            Console.WriteLine("PASS: two-process offline restart, file/folder delivery, empty directories, disk checkpoints, verified resume, lost final receipt, replay deduplication, changed source and corrupt-prefix restart.");
        }

        private static void CorruptPrefix(string root, Node a, string device, TransferSession link, Outbox box)
        {
            string path = Path.Combine(root, "修复.bin"); File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5 });
            var item = NewItem(device, "修复.bin"); box.Prepare(item, path);
            var store = new LocalStore(Path.Combine(root, "receiver"));
            var ledger = new DeliveryInbox(store, a.Network.Local.Id, "sender");
            var record = ledger.Open(item.Id, "file", item.Content, item.Digest, item.WireSize, item.Size);
            File.WriteAllBytes(ledger.Partial(record), new byte[] { 9, 9 });
            long first = -1; ReplayFile(link, box, item, (n, total) => { if (first < 0) first = n; });
            Check(first == 0 && File.ReadAllBytes(Path.Combine(store.Folder, item.Content)).SequenceEqual(File.ReadAllBytes(path)), "Corrupt prefix not replaced");
        }
    }
}
