using System;
using System.IO;
using System.Net.Sockets;
using System.Net.Security;
using System.Text;

namespace DeskLink.Transfer
{
    internal enum Packet : byte
    {
        Hello = 1, Welcome, Text, FileStart, FileData, FileEnd, FileAbort, Ready, Receipt
    }

    internal sealed class Peer : IDisposable
    {
        internal readonly TcpClient Client;
        internal readonly SslStream Stream;
        internal readonly string Name;
        internal readonly DeviceHello Device;
        internal readonly string Address;
        private int closed;

        internal Peer(TcpClient client, SslStream stream, string name, DeviceHello device = null)
        {
            Client = client;
            Stream = stream;
            Name = name;
            Device = device;
            if (device != null) Address = ((System.Net.IPEndPoint)client.Client.RemoteEndPoint).Address + ":" + device.Port;
        }

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref closed, 1) != 0) return;
            Client.Close();
            Stream.Dispose();
        }
    }

    // Each TLS frame has a one-byte type and a four-byte little-endian payload length.
    // A connection carries at most one active file in each direction, alongside text.
    internal static class Wire
    {
        internal const int HeaderSize = 5;
        internal const int BlockSize = 64 * 1024;
        internal const int MaxTextBytes = 128 * 1024;
        internal const int MaxPayload = MaxTextBytes + 64;
        internal static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        internal static void ReadExact(Stream stream, byte[] buffer, int count)
        {
            int position = 0;
            while (position < count)
            {
                int read = stream.Read(buffer, position, count - position);
                if (read == 0) throw new EndOfStreamException("对方已断开连接。");
                position += read;
            }
        }

        internal static Packet Read(Stream stream, byte[] header, byte[] payload, out int size)
        {
            ReadExact(stream, header, HeaderSize);
            size = header[1] | (header[2] << 8) | (header[3] << 16) | (header[4] << 24);
            if (size < 0 || size > payload.Length) throw new InvalidDataException("数据帧长度无效。");
            ReadExact(stream, payload, size);
            return (Packet)header[0];
        }

        // Callers reserve the first five bytes so large files reuse a single send buffer.
        internal static void Write(Stream stream, Packet kind, byte[] buffer, int size)
        {
            if (size < 0 || size > MaxPayload) throw new InvalidDataException("数据过长。");
            buffer[0] = (byte)kind;
            buffer[1] = (byte)size;
            buffer[2] = (byte)(size >> 8);
            buffer[3] = (byte)(size >> 16);
            buffer[4] = (byte)(size >> 24);
            stream.Write(buffer, 0, HeaderSize + size);
        }

        internal static void Write(Stream stream, Packet kind, Action<BinaryWriter> fields)
        {
            using (var body = new MemoryStream())
            using (var writer = new BinaryWriter(body, Utf8, true))
            {
                body.Position = HeaderSize;
                fields(writer);
                Write(stream, kind, body.GetBuffer(), checked((int)body.Position - HeaderSize));
            }
        }

        internal static BinaryReader Reader(byte[] buffer, int size)
        {
            return new BinaryReader(new MemoryStream(buffer, 0, size, false), Utf8);
        }

        internal static void PutString(BinaryWriter writer, string value, int maxBytes)
        {
            byte[] bytes = Utf8.GetBytes(value);
            if (bytes.Length > maxBytes) throw new InvalidDataException("文字超出长度限制。");
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        internal static string GetString(BinaryReader reader, int maxBytes)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > maxBytes) throw new InvalidDataException("文字长度无效。");
            return Utf8.GetString(GetBytes(reader, count));
        }

        internal static byte[] GetBytes(BinaryReader reader, int count)
        {
            byte[] bytes = reader.ReadBytes(count);
            if (bytes.Length != count) throw new EndOfStreamException("数据不完整。");
            return bytes;
        }

        internal static bool SameBytes(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            int difference = 0;
            for (int i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
            return difference == 0;
        }
    }
}
