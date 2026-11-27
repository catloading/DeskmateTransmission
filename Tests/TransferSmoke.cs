using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DeskLink.Transfer;

namespace DeskLink.Tests
{
    internal static class TransferSmoke
    {
        private const string Payload = "双向数据.bin";
        private const string Words = "文字、标点🙂\r\n第二行\tend";

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static void MakeFile(string path, int size, int seed)
        {
            byte[] bytes = new byte[65536];
            var random = new Random(seed);
            using (var file = File.Create(path))
            {
                while (size > 0)
                {
                    random.NextBytes(bytes);
                    int count = Math.Min(size, bytes.Length);
                    file.Write(bytes, 0, count);
                    size -= count;
                }
            }
        }

        private static byte[] Digest(string path)
        {
            using (var hash = SHA256.Create())
            using (var file = File.OpenRead(path)) return hash.ComputeHash(file);
        }

        private static void FileMatches(string source, string destination)
        {
            Require(Digest(source).SequenceEqual(Digest(destination)), "Digest mismatch at " + destination);
        }

        private static void Until(Func<bool> condition, string error)
        {
            Require(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(25)), error);
        }

        private static void Guest(string root)
        {
            string[] invite = File.ReadAllLines(Path.Combine(root, "invite"));
            bool rejected = false;
            string wrong = (invite[1][0] == '0' ? "1" : "0") + invite[1].Substring(1);
            try { Pairing.Join(invite[0], wrong, "wrong", CancellationToken.None).Dispose(); }
            catch (AuthenticationException) { rejected = true; }
            Require(rejected, "An incorrect code was accepted");
            using (var link = new TransferSession(Pairing.Join(invite[0], Pairing.FormatCode(invite[1]), "电脑乙",
                CancellationToken.None), () => Path.Combine(root, "guest-in")))
            {
                Require(link.PeerName == "电脑甲", "Wrong host name");
                var seen = new ConcurrentQueue<Received>();
                link.ItemReceived += seen.Enqueue;
                link.Start();
                link.SendFile(Guid.NewGuid(), Path.Combine(root, "guest-out", Payload), (n, size) =>
                {
                    if (n == 0) link.SendText(Guid.NewGuid(), "乙" + Words);
                });
                Until(() => seen.Count(x => x.State == ReceiveState.Complete) >= 3, "Guest did not receive all items");
                Require(seen.Any(x => x.Content == "甲" + Words), "Guest text mismatch");
                FileMatches(Path.Combine(root, "host-out", Payload), Path.Combine(root, "guest-in", Payload));
                Require(new FileInfo(Path.Combine(root, "guest-in", "空白.txt")).Length == 0, "Empty file mismatch");
                link.SendText(Guid.NewGuid(), "verified");
                Require(link.Finished.Wait(TimeSpan.FromSeconds(25)), "Guest did not observe disconnect");
                Require(seen.Any(x => x.Content == "中断.bin" && x.State == ReceiveState.Failed), "Missing interrupted state");
                Require(!File.Exists(Path.Combine(root, "guest-in", "中断.bin")) &&
                    !Directory.EnumerateFiles(Path.Combine(root, "guest-in"), "*.part").Any(), "Partial file was retained");
            }
        }

        private static void LocalStorageChecks(string root)
        {
            string storePath = Path.Combine(root, "profile"), folder = Path.Combine(root, "disk");
            var store = new LocalStore(storePath);
            store.SetFolder(folder);
            var item = new ChatItem { Utc = DateTime.UtcNow, Content = Words, Id = Guid.NewGuid(), Mine = true, Peer = "乙" };
            store.Save(item);
            var reopened = new LocalStore(storePath);
            Require(reopened.Folder == folder && reopened.Load().Single().Content == Words, "Local history or receive folder did not persist");
            Require(IncomingFile.SafeName(@"..\..\CON.txt") == "_CON.txt", "Unexpected saved filename");
            Require(IncomingFile.SafeName("../测试.bin") == "测试.bin", "Nested filename was accepted");
            using (var broken = new IncomingFile(Guid.NewGuid(), "不完整.bin", 3, folder))
            {
                broken.Append(new byte[] { 1, 2, 3 }, 3);
                try { broken.Commit(new byte[32]); throw new Exception("Checksum mismatch accepted"); }
                catch (IOException) { }
            }
            Require(!Directory.EnumerateFiles(folder).Any(), "Failed checksum left a file behind");
        }

        private static void Host(string root)
        {
            foreach (var name in new[] { "host-out", "guest-out", "host-in", "guest-in" }) Directory.CreateDirectory(Path.Combine(root, name));
            MakeFile(Path.Combine(root, "host-out", Payload), 8 * 1024 * 1024 + 7, 35);
            MakeFile(Path.Combine(root, "guest-out", Payload), 7 * 1024 * 1024 + 3, 57);
            File.WriteAllText(Path.Combine(root, "host-out", "空白.txt"), "");
            MakeFile(Path.Combine(root, "host-out", "中断.bin"), 300001, 19);
            File.WriteAllText(Path.Combine(root, "host-in", Payload), "keep");
            using (var invite = new Invitation("电脑甲"))
            {
                File.WriteAllLines(Path.Combine(root, "invite"), new[] { "127.0.0.1:" + invite.Port, invite.Code });
                using (var child = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,
                    "guest \"" + root + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true }))
                {
                    try
                    {
                        Until(() => invite.Accepted.IsCompleted || child.HasExited, "Pairing timed out");
                        Require(invite.Accepted.IsCompleted, child.HasExited ? child.StandardError.ReadToEnd() : "Pairing failed");
                        using (var link = new TransferSession(invite.Accepted.GetAwaiter().GetResult(), () => Path.Combine(root, "host-in")))
                        {
                            Require(link.PeerName == "电脑乙", "Wrong guest name");
                            var seen = new ConcurrentQueue<Received>();
                            link.ItemReceived += seen.Enqueue;
                            link.Start();
                            link.SendFile(Guid.NewGuid(), Path.Combine(root, "host-out", Payload), (n, size) =>
                            {
                                if (n == 0) link.SendText(Guid.NewGuid(), "甲" + Words);
                            });
                            link.SendFile(Guid.NewGuid(), Path.Combine(root, "host-out", "空白.txt"), null);
                            Until(() => seen.Any(x => x.Content == "verified") || child.HasExited, "Missing guest receipt");
                            Require(seen.Any(x => x.Content == "乙" + Words), "Host text mismatch");
                            Require(File.ReadAllText(Path.Combine(root, "host-in", Payload)) == "keep", "An existing file changed");
                            FileMatches(Path.Combine(root, "guest-out", Payload), Path.Combine(root, "host-in", "双向数据 (1).bin"));
                            try { link.SendFile(Guid.NewGuid(), Path.Combine(root, "host-out", "中断.bin"), (n, total) => link.Dispose()); }
                            catch (IOException) { }
                            Require(link.Finished.Wait(10000), "Host reader did not exit");
                        }
                        Require(child.WaitForExit(10000), "Guest did not exit");
                        Require(child.ExitCode == 0, child.StandardError.ReadToEnd());
                    }
                    finally { if (!child.HasExited) { child.Kill(); child.WaitForExit(); } }
                }
            }
            LocalStorageChecks(root);
        }

        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 2 && args[0] == "guest") Guest(args[1]);
                else
                {
                    string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                    string root = Path.Combine(temp, "DeskLink.Test-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(root);
                    try { Host(root); ReconnectSmoke.Run(root); }
                    finally
                    {
                        if (Path.GetDirectoryName(Path.GetFullPath(root)) == temp && Path.GetFileName(root).StartsWith("DeskLink.Test-"))
                            Directory.Delete(root, true);
                    }
                    Console.WriteLine("PASS: two processes, TLS pairing, wrong code, concurrent bidirectional chat/files, empty file, collision, hashes, disconnect cleanup and local history.");
                }
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}
