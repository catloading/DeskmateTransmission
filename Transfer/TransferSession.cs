using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace DeskLink.Transfer
{
    internal enum ReceiveState { Started, Progress, Complete, Failed }

    internal sealed class Received
    {
        internal Guid Id;
        internal bool IsFile;
        internal string Content;
        internal long Size;
        internal long Bytes;
        internal string Path;
        internal string Error;
        internal ReceiveState State;
    }

    internal sealed class TransferSession : IDisposable
    {
        private sealed class Reply
        {
            internal bool Ok;
            internal string Error;
        }
        private sealed class Pending
        {
            internal readonly TaskCompletionSource<Reply> Ready = new TaskCompletionSource<Reply>();
            internal readonly TaskCompletionSource<Reply> Done = new TaskCompletionSource<Reply>();
        }

        private readonly Peer peer;
        private readonly Func<string> receiveFolder;
        private readonly object writeLock = new object();
        private readonly object fileLock = new object();
        private readonly ConcurrentDictionary<Guid, Pending> pending = new ConcurrentDictionary<Guid, Pending>();
        private readonly Stopwatch incomingClock = new Stopwatch();
        private readonly TaskCompletionSource<bool> finished =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Only the read loop owns these fields, including final cleanup.
        private IncomingFile incoming;
        private Guid? discarding;
        private int closed;
        private int started;

        internal event Action<Received> ItemReceived;
        internal event Action<string> Ended;
        internal string PeerName { get { return peer.Name; } }
        internal bool Connected { get { return Volatile.Read(ref closed) == 0; } }
        internal Task Finished { get { return finished.Task; } }

        internal TransferSession(Peer connection, Func<string> folder)
        {
            peer = connection;
            receiveFolder = folder;
        }

        internal void Start()
        {
            if (Interlocked.Exchange(ref started, 1) == 0)
                Task.Factory.StartNew(ReadLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private void CheckOpen()
        {
            if (!Connected) throw new IOException("连接已断开。");
        }

        private void WriteLocked(Action write)
        {
            lock (writeLock)
            {
                CheckOpen();
                try { write(); }
                catch { Dispose(); throw; }
            }
        }

        private void Send(Packet kind, Action<BinaryWriter> fields)
        {
            WriteLocked(() => Wire.Write(peer.Stream, kind, fields));
        }

        private Pending Register(Guid id)
        {
            var item = new Pending();
            if (!pending.TryAdd(id, item)) throw new InvalidOperationException("传输编号重复。");
            if (!Connected)
            {
                pending.TryRemove(id, out item);
                throw new IOException("连接已断开。");
            }
            return item;
        }

        private static void CheckReply(Task<Reply> task)
        {
            if (!task.Wait(TimeSpan.FromSeconds(60))) throw new TimeoutException("尚未收到对方确认。");
            if (!task.Result.Ok) throw new IOException(task.Result.Error);
        }

        internal void SendText(Guid id, string text)
        {
            if (string.IsNullOrWhiteSpace(text) || Wire.Utf8.GetByteCount(text) > Wire.MaxTextBytes)
                throw new ArgumentException("消息不能为空，且不能超过 128 KB。");
            Pending ack = Register(id);
            try
            {
                Send(Packet.Text, w => { w.Write(id.ToByteArray()); Wire.PutString(w, text, Wire.MaxTextBytes); });
                CheckReply(ack.Done.Task);
            }
            finally { pending.TryRemove(id, out ack); }
        }

        internal void SendFile(Guid id, string path, Action<long, long> progress)
        {
            lock (fileLock)
            {
                Pending ack = Register(id);
                bool offered = false, ended = false;
                try
                {
                    using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                        Wire.BlockSize, FileOptions.SequentialScan))
                    using (var hash = SHA256.Create())
                    {
                        long size = file.Length;
                        Send(Packet.FileStart, w =>
                        {
                            w.Write(id.ToByteArray()); w.Write(size);
                            Wire.PutString(w, Path.GetFileName(path), 2048);
                        });
                        offered = true;
                        CheckReply(ack.Ready.Task);
                        var clock = Stopwatch.StartNew();
                        byte[] buffer = new byte[Wire.HeaderSize + Wire.BlockSize];
                        long sent = 0;
                        progress?.Invoke(0, size);
                        int count;
                        while ((count = file.Read(buffer, Wire.HeaderSize, Wire.BlockSize)) != 0)
                        {
                            if (ack.Done.Task.IsCompleted) CheckReply(ack.Done.Task);
                            hash.TransformBlock(buffer, Wire.HeaderSize, count, null, 0);
                            WriteLocked(() => Wire.Write(peer.Stream, Packet.FileData, buffer, count));
                            sent += count;
                            if (clock.ElapsedMilliseconds >= 160)
                            {
                                progress?.Invoke(sent, size);
                                clock.Restart();
                            }
                        }
                        if (sent != size) throw new IOException("文件大小在发送时发生变化。");
                        hash.TransformFinalBlock(new byte[0], 0, 0);
                        Send(Packet.FileEnd, w => { w.Write(id.ToByteArray()); w.Write(hash.Hash); });
                        ended = true;
                        progress?.Invoke(sent, size);
                        CheckReply(ack.Done.Task);
                    }
                }
                catch
                {
                    if (offered && !ended && Connected)
                    {
                        try { Send(Packet.FileAbort, w => w.Write(id.ToByteArray())); } catch (IOException) { }
                    }
                    throw;
                }
                finally { pending.TryRemove(id, out ack); }
            }
        }

        private void ReplyTo(Packet kind, Guid id, bool ok, string error = "")
        {
            Send(kind, w =>
            {
                w.Write(id.ToByteArray()); w.Write(ok);
                Wire.PutString(w, error.Length > 200 ? error.Substring(0, 200) : error, 1024);
            });
        }

        private void Publish(ReceiveState state, string path = null, string error = null)
        {
            ItemReceived?.Invoke(new Received { Id = incoming.Id, IsFile = true, Content = incoming.Name,
                Size = incoming.Size, Bytes = incoming.Received, State = state, Path = path, Error = error });
        }

        private void StartFile(Guid id, BinaryReader reader)
        {
            if (incoming != null || discarding.HasValue) throw new InvalidDataException("上一个文件尚未结束。");
            long size = reader.ReadInt64();
            string name = Wire.GetString(reader, 2048);
            try { incoming = new IncomingFile(id, name, size, receiveFolder()); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                ex is ArgumentException || ex is NotSupportedException)
            {
                ReplyTo(Packet.Ready, id, false, "对方无法保存文件：" + ex.Message);
                ItemReceived?.Invoke(new Received { Id = id, IsFile = true, Content = IncomingFile.SafeName(name),
                    Size = size, State = ReceiveState.Failed, Error = ex.Message });
                return;
            }
            Publish(ReceiveState.Started);
            incomingClock.Restart();
            ReplyTo(Packet.Ready, id, true);
        }

        private void FailIncoming(string reason)
        {
            Publish(ReceiveState.Failed, error: reason);
            incoming.Dispose();
            incoming = null;
        }

        private void ReceiveData(byte[] data, int count)
        {
            if (discarding.HasValue) return;
            if (incoming == null) throw new InvalidDataException("未收到文件信息。");
            try { incoming.Append(data, count); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                discarding = incoming.Id;
                FailIncoming(ex.Message);
                ReplyTo(Packet.Receipt, discarding.Value, false, "对方保存失败：" + ex.Message);
                return;
            }
            if (incomingClock.ElapsedMilliseconds >= 160)
            {
                Publish(ReceiveState.Progress);
                incomingClock.Restart();
            }
        }

        private void FinishFile(Guid id, BinaryReader reader)
        {
            byte[] hash = Wire.GetBytes(reader, 32);
            if (discarding == id) { discarding = null; return; }
            if (incoming == null || incoming.Id != id) throw new InvalidDataException("文件编号不匹配。");
            string path;
            try { path = incoming.Commit(hash); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                FailIncoming(ex.Message);
                ReplyTo(Packet.Receipt, id, false, "对方保存失败：" + ex.Message);
                return;
            }
            Publish(ReceiveState.Complete, path);
            incoming.Dispose();
            incoming = null;
            ReplyTo(Packet.Receipt, id, true);
        }

        private void Dispatch(Packet kind, byte[] buffer, int length)
        {
            if (kind == Packet.FileData) { ReceiveData(buffer, length); return; }
            using (var reader = Wire.Reader(buffer, length))
            {
                Guid id = new Guid(Wire.GetBytes(reader, 16));
                switch (kind)
                {
                    case Packet.Text:
                        string text = Wire.GetString(reader, Wire.MaxTextBytes);
                        ItemReceived?.Invoke(new Received { Id = id, Content = text, State = ReceiveState.Complete });
                        ReplyTo(Packet.Receipt, id, true);
                        break;
                    case Packet.FileStart: StartFile(id, reader); break;
                    case Packet.FileEnd: FinishFile(id, reader); break;
                    case Packet.FileAbort:
                        if (discarding == id) discarding = null;
                        if (incoming != null && incoming.Id == id) FailIncoming("发送端中止了传输。");
                        break;
                    case Packet.Ready:
                    case Packet.Receipt:
                        var reply = new Reply { Ok = reader.ReadBoolean(), Error = Wire.GetString(reader, 1024) };
                        Pending item;
                        if (pending.TryGetValue(id, out item))
                            (kind == Packet.Ready ? item.Ready : item.Done).TrySetResult(reply);
                        break;
                    default: throw new InvalidDataException("无法识别传输数据。");
                }
                if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("数据格式无效。");
            }
        }

        private void ReadLoop()
        {
            string reason = "连接已断开。";
            try
            {
                byte[] header = new byte[Wire.HeaderSize], data = new byte[Wire.MaxPayload];
                while (Connected)
                {
                    int count;
                    Packet kind = Wire.Read(peer.Stream, header, data, out count);
                    Dispatch(kind, data, count);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException ||
                ex is System.Net.Sockets.SocketException || ex is CryptographicException || ex is ArgumentException)
            {
                if (Connected) reason = ex is EndOfStreamException ? "对方已断开连接。" : "连接已中断。";
            }
            finally
            {
                try
                {
                    Dispose();
                    if (incoming != null) FailIncoming("连接中断，文件未收全。");
                    Ended?.Invoke(reason);
                }
                finally { finished.TrySetResult(true); }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
            var disconnected = new Reply { Error = "连接中断，未收到对方确认。" };
            foreach (var item in pending.Values)
            {
                item.Ready.TrySetResult(disconnected);
                item.Done.TrySetResult(disconnected);
            }
            peer.Dispose();
        }
    }
}
