using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using DeskLink.Transfer;

namespace DeskLink.Tests
{
    internal static class ReconnectSmoke
    {
        private sealed class Node : IDisposable
        {
            internal readonly DeviceNetwork Net;
            internal readonly string Name;
            internal readonly ConcurrentQueue<Received> Received = new ConcurrentQueue<Received>();
            internal readonly ConcurrentDictionary<string, TransferSession> Sessions = new ConcurrentDictionary<string, TransferSession>();

            internal Node(string path, string name)
            {
                Name = name;
                Net = new DeviceNetwork(path, name);
                Net.Connected += (peer, paired) =>
                {
                    var session = new TransferSession(peer, () => Path.Combine(path, "inbox"));
                    if (!Sessions.TryAdd(peer.Device.Id, session)) throw new Exception("Duplicate active session");
                    session.ItemReceived += Received.Enqueue;
                    session.Ended += reason =>
                    {
                        TransferSession prior;
                        Sessions.TryRemove(peer.Device.Id, out prior);
                        Net.Release(peer);
                    };
                    session.Start();
                };
                Net.Start();
            }
            public void Dispose()
            {
                var sessions = Sessions.Values.ToArray();
                Net.Dispose();
                foreach (var s in sessions) { s.Dispose(); s.Finished.Wait(5000); }
            }
        }

        private static void Wait(Func<bool> condition, string error)
        {
            if (!SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(23))) throw new Exception(error);
        }

        private static void Pair(Node a, Node b)
        {
            using (var invite = new Invitation(a.Name, a.Net.Local))
            {
                var joined = Pairing.Join("127.0.0.1:" + invite.Port, invite.Code, b.Name, CancellationToken.None, b.Net.Local);
                a.Net.Remember(invite.Accepted.GetAwaiter().GetResult());
                b.Net.Remember(joined);
            }
        }

        internal static void Run(string root)
        {
            string first = Path.Combine(root, "profile-A"), second = Path.Combine(root, "profile-B");
            string aid, bid;
            using (var a = new Node(first, "同名电脑"))
            using (var b = new Node(second, "同名电脑"))
            {
                aid = a.Net.Local.Id; bid = b.Net.Local.Id;
                Wait(() => a.Net.Snapshot().Any(d => d.Id == bid && d.Nearby && !d.Trusted) &&
                    b.Net.Snapshot().Any(d => d.Id == aid && d.Nearby && !d.Trusted), "Nearby discovery failed");
                Pair(a, b);
                a.Sessions[bid].SendText(Guid.NewGuid(), "首次配对");
                Wait(() => b.Received.Any(r => r.Content == "首次配对"), "First paired transfer failed");
            }
            // The saved route is deliberately obsolete, as it may be after DHCP or a port change.
            var dialerBook = new DeviceBook(string.CompareOrdinal(aid, bid) < 0 ? first : second);
            var saved = dialerBook.Load().Single();
            saved.Address = "127.0.0.1:1";
            dialerBook.Save(saved);
            using (var a = new Node(first, "同名电脑"))
            using (var b = new Node(second, "已改名的电脑"))
            {
                if (aid != a.Net.Local.Id || bid != b.Net.Local.Id) throw new Exception("Device identity changed across restart");
                Wait(() => a.Sessions.ContainsKey(bid) && b.Sessions.ContainsKey(aid), "Automatic TLS reconnect after restart failed");
                if (!a.Net.Snapshot().Any(d => d.Id == bid && d.Name == b.Name && d.Trusted)) throw new Exception("Rename lost identity");
                b.Sessions[aid].SendText(Guid.NewGuid(), "重连成功");
                Wait(() => a.Received.Any(r => r.Content == "重连成功"), "Resumed message missing");
                string file = Path.Combine(root, "重连传输.txt"); File.WriteAllText(file, "重连后的文件");
                a.Sessions[bid].SendFile(Guid.NewGuid(), file, null);
                if (File.ReadAllText(Path.Combine(second, "inbox", Path.GetFileName(file))) != "重连后的文件")
                    throw new Exception("Resumed file mismatch");
                var interrupted = a.Sessions[bid];
                interrupted.Dispose();
                Wait(() => a.Sessions.TryGetValue(bid, out var current) && current != interrupted && b.Sessions.ContainsKey(aid),
                    "Connection was not recovered after interruption with both apps running");
                a.Net.Pause(bid);
                Wait(() => a.Sessions.IsEmpty && b.Sessions.IsEmpty, "Pause did not break the connection");
                if (!a.Net.Snapshot().Single(d => d.Id == bid).Paused) throw new Exception("Pause state lost");
                a.Net.Resume(bid);
                Wait(() => a.Sessions.ContainsKey(bid) && b.Sessions.ContainsKey(aid), "Explicit resume failed");
                a.Net.Forget(bid);
                Wait(() => a.Sessions.IsEmpty && b.Sessions.IsEmpty, "Removing trust did not disconnect");
                if (new DeviceBook(first).Load().Any(d => d.Id == bid)) throw new Exception("Removed trust persisted");
            }
            Console.WriteLine("PASS: LAN discovery, two identities with the same name, persisted pairing, authenticated auto-reconnect after changing address/name, chat/file, pause/resume and unpair.");
        }
    }
}
