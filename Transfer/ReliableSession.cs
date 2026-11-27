using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace DeskLink.Transfer
{
    internal sealed partial class TransferSession
    {
        // Version 2 ignores a receipt for a nonexistent ID. Advertise without changing its strict handshake.
        private const string Capability = "DeskLink/reliable/1";
        private readonly TaskCompletionSource<bool> capability = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly DeliveryInbox inbox;
        private ResumableFile resumable;
        internal bool Reliable { get { return capability.Task.Status == TaskStatus.RanToCompletion && capability.Task.Result; } }
        internal Task<bool> CapabilityChanged { get { return capability.Task; } }

        internal async Task<bool> Negotiate()
        {
            if (inbox == null) return false;
            await Task.WhenAny(capability.Task, Task.Delay(1800)).ConfigureAwait(false);
            return Reliable;
        }

        private static Reply WaitDurable(Task<Reply> task)
        {
            // Disk verification and folder extraction may take minutes. Disconnect unblocks every pending receipt.
            var reply = task.GetAwaiter().GetResult();
            if (!reply.Ok) throw new IOException(reply.Error);
            return reply;
        }

        internal void SendDurableText(Guid id, string text)
        {
            if (!Reliable || string.IsNullOrWhiteSpace(text) || Wire.Utf8.GetByteCount(text) > Wire.MaxTextBytes)
                throw new InvalidOperationException("消息内容或对方版本不支持离线发送。");
            var ack = Register(id);
            try
            {
                Send(Packet.DurableText, w => { w.Write(id.ToByteArray()); Wire.PutString(w, text, Wire.MaxTextBytes); });
                WaitDurable(ack.Done.Task);
            }
            finally { pending.TryRemove(id, out ack); }
        }

        internal void SendDurableFile(ChatItem item, FileStream file, Action<long, long> progress)
        {
            if (!Reliable) throw new InvalidOperationException("请将对方升级至 2.2。");
            lock (fileLock)
            {
                var ack = Register(item.Id);
                bool offered = false, ended = false;
                try
                {
                    byte[] digest = Convert.FromBase64String(item.Digest);
                    Send(Packet.Offer, w =>
                    {
                        w.Write(item.Id.ToByteArray()); w.Write(item.IsFolder); w.Write(file.Length); w.Write(item.Size);
                        Wire.PutString(w, item.Content, 2048); w.Write(digest);
                    });
                    offered = true;
                    var response = WaitDurable(ack.Ready.Task);
                    if (response.Offset == -1) return; // The original delivery was committed before its receipt was lost.
                    if (response.Offset < 0 || response.Offset > file.Length) throw new InvalidDataException("断点位置无效。");
                    byte[] buffer = new byte[Wire.HeaderSize + Wire.BlockSize];
                    file.Position = 0;
                    if (response.Offset > 0 && !Wire.SameBytes(HashPrefix(file, response.Offset, buffer), response.Prefix))
                    {
                        Send(Packet.ResetPartial, w => w.Write(item.Id.ToByteArray()));
                        file.Position = 0;
                    }
                    progress?.Invoke(file.Position, file.Length);
                    var clock = Stopwatch.StartNew();
                    int n;
                    while ((n = file.Read(buffer, Wire.HeaderSize, Wire.BlockSize)) != 0)
                    {
                        if (ack.Done.Task.IsCompleted) WaitDurable(ack.Done.Task);
                        WriteLocked(() => Wire.Write(peer.Stream, Packet.FileData, buffer, n));
                        if (clock.ElapsedMilliseconds >= 160) { progress?.Invoke(file.Position, file.Length); clock.Restart(); }
                    }
                    Send(Packet.FileEnd, w => { w.Write(item.Id.ToByteArray()); w.Write(digest); });
                    ended = true;
                    progress?.Invoke(file.Length, file.Length);
                    WaitDurable(ack.Done.Task);
                }
                catch
                {
                    if (offered && !ended && Connected)
                        try { Send(Packet.FileAbort, w => w.Write(item.Id.ToByteArray())); } catch (IOException) { }
                    throw;
                }
                finally { pending.TryRemove(item.Id, out ack); }
            }
        }

        private static byte[] HashPrefix(Stream file, long length, byte[] buffer)
        {
            using (var hash = SHA256.Create())
            {
                while (length > 0)
                {
                    int n = file.Read(buffer, 0, (int)Math.Min(buffer.Length, length));
                    if (n == 0) throw new EndOfStreamException("源文件不完整。");
                    hash.TransformBlock(buffer, 0, n, null, 0);
                    length -= n;
                }
                hash.TransformFinalBlock(new byte[0], 0, 0);
                return hash.Hash;
            }
        }

        private static bool StorageError(Exception e)
        {
            return e is IOException || e is InvalidDataException || e is UnauthorizedAccessException ||
                e is CryptographicException || e is ArgumentException || e is NotSupportedException || e is SerializationException;
        }

        private void ReceiveDurableText(Guid id, BinaryReader reader)
        {
            string value = Wire.GetString(reader, Wire.MaxTextBytes);
            if (inbox == null) throw new InvalidDataException("未协商消息回执。");
            try
            {
                var received = inbox.Text(id, value);
                if (received != null) ItemReceived?.Invoke(received);
            }
            catch (Exception e) when (StorageError(e)) { ReplyTo(Packet.Receipt, id, false, e.Message); return; }
            ReplyTo(Packet.Receipt, id, true);
        }

        private void ReceiveOffer(Guid id, BinaryReader reader)
        {
            bool folder = reader.ReadBoolean();
            long length = reader.ReadInt64(), size = reader.ReadInt64();
            string name = IncomingFile.SafeName(Wire.GetString(reader, 2048));
            byte[] digest = Wire.GetBytes(reader, 32);
            if (inbox == null || incoming != null || resumable != null || discarding.HasValue || (!folder && length != size))
                throw new InvalidDataException("文件传输状态无效。");
            long offset = -1;
            byte[] prefix = null;
            try
            {
                var record = inbox.Open(id, folder ? "folder" : "file", name, Convert.ToBase64String(digest), length, size);
                if (!record.Done)
                {
                    resumable = new ResumableFile(inbox, record);
                    if (record.Prepared)
                    {
                        var received = resumable.FinishPrepared();
                        resumable.Dispose(); resumable = null;
                        if (received != null) ItemReceived?.Invoke(received);
                    }
                    else
                    {
                        prefix = resumable.Open(); offset = resumable.Position;
                        ReportDurable(ReceiveState.Started); incomingClock.Restart();
                    }
                }
            }
            catch (Exception e) when (StorageError(e))
            {
                if (resumable != null) SuspendDurable(e.Message);
                ReplyTo(Packet.Ready, id, false, e.Message); return;
            }
            Send(Packet.Position, w => { w.Write(id.ToByteArray()); w.Write(offset); if (offset >= 0) w.Write(prefix); });
        }

        private void ReceivePosition(Guid id, BinaryReader reader)
        {
            long offset = reader.ReadInt64();
            var reply = new Reply { Ok = true, Offset = offset, Prefix = offset >= 0 ? Wire.GetBytes(reader, 32) : null };
            Pending ack;
            if (pending.TryGetValue(id, out ack)) ack.Ready.TrySetResult(reply);
        }

        private void ReportDurable(ReceiveState state, string error = null)
        {
            var r = resumable.Record;
            ItemReceived?.Invoke(new Received { Id = r.Id, Utc = r.Utc, Content = r.Name, IsFile = true, IsFolder = r.Kind == "folder",
                Size = r.LogicalSize, WireSize = r.Length, Bytes = resumable.Position, State = state, Error = error });
        }

        private void SuspendDurable(string error)
        {
            try { ReportDurable(ReceiveState.Failed, error); }
            finally
            {
                try { resumable.Dispose(); }
                catch (IOException) { /* The next offer verifies the bytes that reached disk. */ }
                finally { resumable = null; }
            }
        }

        private void DurableData(byte[] data, int count)
        {
            try { resumable.Append(data, count); }
            catch (Exception e) when (StorageError(e))
            {
                discarding = resumable.Record.Id; SuspendDurable(e.Message);
                ReplyTo(Packet.Receipt, discarding.Value, false, e.Message); return;
            }
            if (incomingClock.ElapsedMilliseconds >= 160) { ReportDurable(ReceiveState.Progress); incomingClock.Restart(); }
        }

        private void FinishDurable(Guid id, byte[] hash)
        {
            if (resumable.Record.Id != id) throw new InvalidDataException("文件编号不匹配。");
            try
            {
                var item = resumable.Commit(hash);
                if (item != null) ItemReceived?.Invoke(item);
            }
            catch (Exception e) when (StorageError(e))
            {
                SuspendDurable(e.Message); ReplyTo(Packet.Receipt, id, false, e.Message); return;
            }
            resumable.Dispose(); resumable = null;
            ReplyTo(Packet.Receipt, id, true);
        }
    }
}
