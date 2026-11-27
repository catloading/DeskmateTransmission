using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DeskLink.Transfer;

namespace DeskLink.Tests
{
    internal static class UpgradeSmoke
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }

        internal static void OldHost(string root, string release)
        {
            // Execute the historical binary in its own process; never synthesize its handshake or saved identity.
            var old = Assembly.LoadFile(release);
            Check(old.GetName().Version == new Version(2, 0, 0, 0), "Expected the original 2.0 release");
            object Create(string name, params object[] args) => Activator.CreateInstance(old.GetType("DeskLink." + name, true), Members, null, args, null);
            object Property(object o, string name) => o.GetType().GetProperty(name, Members).GetValue(o);
            object Call(object o, string name, params object[] args) => o.GetType().GetMethod(name, Members).Invoke(o, args);
            string profile = Path.Combine(root, "old"), inbox = Path.Combine(root, "old-in");
            var saved = Create("LocalStore", profile);
            var history = Create("ChatItem");
            foreach (var entry in new System.Collections.Generic.Dictionary<string, object> {
                { "Id", Guid.NewGuid() }, { "Utc", DateTime.UtcNow }, { "Content", "保留 2.0 的历史" }, { "Peer", "老电脑" } })
                history.GetType().GetProperty(entry.Key, Members).SetValue(history, entry.Value);
            Call(saved, "Save", history);
            using (var net = (IDisposable)Create("Transfer.DeviceNetwork", profile, "旧版"))
            using (var invite = (IDisposable)Create("Transfer.Invitation", "旧版", Property(net, "Local")))
            {
                File.WriteAllLines(Path.Combine(root, "offer.tmp"), new[] { "127.0.0.1:" + Property(invite, "Port"), (string)Property(invite, "Code") });
                File.Move(Path.Combine(root, "offer.tmp"), Path.Combine(root, "offer"));
                var accepted = (Task)Property(invite, "Accepted");
                Check(accepted.Wait(30000), "Old pairing timed out");
                var peer = Property(accepted, "Result");
                Call(net, "Remember", peer);
                using (var link = (IDisposable)Create("Transfer.TransferSession", peer, new Func<string>(() => inbox)))
                {
                    Call(link, "Start");
                    Call(link, "SendText", Guid.NewGuid(), "来自 2.0");
                    Call(link, "SendFile", Guid.NewGuid(), Path.Combine(root, "old.txt"), null);
                    Check(SpinWait.SpinUntil(() => File.Exists(Path.Combine(root, "finished")), 30000), "Mixed-version file timed out");
                    Check(File.ReadAllText(Path.Combine(inbox, "new.txt")) == "2.2", "Old peer received wrong bytes");
                }
            }
        }

        internal static void Run(string root, string release)
        {
            File.WriteAllText(Path.Combine(root, "new.txt"), "2.2");
            File.WriteAllText(Path.Combine(root, "old.txt"), "2.0");
            string current = Path.Combine(root, "new");
            using (var child = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,
                "v2-host \"" + root + "\" \"" + release + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true }))
            {
                try
                {
                    Check(SpinWait.SpinUntil(() => File.Exists(Path.Combine(root, "offer")) || child.HasExited, 30000), "Old host did not start");
                    if (child.HasExited) throw new Exception(child.StandardError.ReadToEnd());
                    var addr = File.ReadAllLines(Path.Combine(root, "offer"));
                    using (var net = new DeviceNetwork(current, "新版"))
                    {
                        var peer = Pairing.Join(addr[0], addr[1], "新版", CancellationToken.None, net.Local);
                        net.Remember(peer);
                        using (var link = new TransferSession(peer, () => Path.Combine(root, "new-in"), new LocalStore(current)))
                        {
                            var incoming = new ConcurrentQueue<Received>(); link.ItemReceived += incoming.Enqueue;
                            link.Start();
                            Check(!link.Negotiate().GetAwaiter().GetResult() && link.Connected, "Old peer did not accept capability fallback");
                            link.SendText(Guid.NewGuid(), "来自 2.2"); link.SendFile(Guid.NewGuid(), Path.Combine(root, "new.txt"), null);
                            Check(SpinWait.SpinUntil(() => incoming.Any(r => r.Content == "来自 2.0") && File.Exists(Path.Combine(root, "new-in", "old.txt")), 15000),
                                "New peer did not receive the legacy message/file");
                            Check(File.ReadAllText(Path.Combine(root, "new-in", "old.txt")) == "2.0", "Mixed-version payload mismatch");
                            File.WriteAllText(Path.Combine(root, "finished"), "");
                            Check(child.WaitForExit(20000), "Old host did not finish");
                            Check(child.ExitCode == 0, child.StandardError.ReadToEnd());
                        }
                    }
                }
                finally { if (!child.HasExited) { child.Kill(); child.WaitForExit(); } }
            }
            string oldProfile = Path.Combine(root, "old");
            Check(new LocalStore(oldProfile).Load().Any(i => i.Content == "保留 2.0 的历史"), "2.0 history did not migrate");
            using (var a = new DeviceNetwork(current, "新版"))
            using (var b = new DeviceNetwork(oldProfile, "已升级"))
            {
                TaskCompletionSource<TransferSession> Connect(DeviceNetwork net, string profile)
                {
                    var result = new TaskCompletionSource<TransferSession>(TaskCreationOptions.RunContinuationsAsynchronously);
                    net.Connected += (peer, first) =>
                    {
                        var link = new TransferSession(peer, () => Path.Combine(profile, "in"), new LocalStore(profile));
                        link.Start(); result.TrySetResult(link);
                    };
                    net.Start(); return result;
                }
                var left = Connect(a, current); var right = Connect(b, oldProfile);
                Check(Task.WaitAll(new Task[] { left.Task, right.Task }, 30000), "Upgrade did not reuse the saved 2.0 pairing");
                using (var l = left.Task.Result)
                using (var r = right.Task.Result)
                {
                    Check(l.Negotiate().GetAwaiter().GetResult(), "Updated peer did not negotiate reliable delivery");
                    l.SendDurableText(Guid.NewGuid(), "升级后自动连接");
                    Check(new LocalStore(oldProfile).Load().Any(i => i.Content == "升级后自动连接"), "Delivery after upgrade failed");
                }
            }
            Console.WriteLine("PASS: original 2.0 executable and 2.2 exchanged text/files in both directions, then 2.2 reused the original encrypted history and device pairing after upgrade.");
        }
    }
}
