using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace DeskLink.Transfer
{
    internal sealed class Advertisement
    {
        internal string Id, Name, Address, PairAddress;
        internal DateTime Seen;
    }

    // Advertisements only supply addresses. Authentication happens on the TLS connection.
    internal sealed class Presence : IDisposable
    {
        private const int Port = 42864;
        private const string Protocol = "TongZhuoChuan/presence/2";
        private static readonly IPAddress Group = IPAddress.Parse("239.255.42.64");
        private readonly HashSet<IPAddress> joined = new HashSet<IPAddress>();
        private readonly object membership = new object();
        private readonly UdpClient receiver;
        private readonly Action<Advertisement> onSeen;
        private volatile bool closed;

        internal Presence(Action<Advertisement> seen)
        {
            onSeen = seen;
            receiver = new UdpClient(AddressFamily.InterNetwork);
            try
            {
                receiver.ExclusiveAddressUse = false;
                receiver.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                receiver.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
                Task.Run((Action)ReadLoop);
            }
            catch { receiver.Close(); throw; }
        }

        // Called by one worker. Re-evaluate adapters to follow Wi-Fi and DHCP changes.
        internal void Announce(DeviceHello local, string name, int pairingPort)
        {
            lock (membership)
            {
                if (closed) return;
                AnnounceOnInterfaces(local, name, pairingPort);
            }
        }

        private void AnnounceOnInterfaces(DeviceHello local, string name, int pairingPort)
        {
            var interfaces = Discovery.Addresses().Select(x => x.Ip).Concat(new[] { IPAddress.Loopback }).Distinct().ToList();
            var bytes = Encoding.UTF8.GetBytes(string.Join("\n", Protocol, local.Id, local.Port.ToString(),
                pairingPort.ToString(), name));
            foreach (var old in joined.Except(interfaces).ToList())
            {
                try { receiver.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership, new MulticastOption(Group, old)); }
                catch (SocketException) { }
                joined.Remove(old);
            }
            foreach (var ip in interfaces)
            {
                if (closed) return;
                try
                {
                    if (!joined.Contains(ip)) { receiver.JoinMulticastGroup(Group, ip); joined.Add(ip); }
                    using (var sender = new UdpClient(new IPEndPoint(ip, 0)))
                    {
                        sender.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, ip.GetAddressBytes());
                        sender.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                        sender.MulticastLoopback = true;
                        sender.Send(bytes, bytes.Length, new IPEndPoint(Group, Port));
                    }
                }
                catch (SocketException) { }
            }
        }

        private void ReadLoop()
        {
            while (!closed)
            {
                try
                {
                    var source = new IPEndPoint(IPAddress.Any, 0);
                    byte[] bytes = receiver.Receive(ref source);
                    if (bytes.Length > 512) continue;
                    string[] fields = Encoding.UTF8.GetString(bytes).Split('\n');
                    int port, pairing;
                    if (fields.Length != 5 || fields[0] != Protocol || !DeviceHello.ValidId(fields[1]) ||
                        !int.TryParse(fields[2], out port) || port < 1 || port > 65535 ||
                        !int.TryParse(fields[3], out pairing) || pairing < 0 || pairing > 65535 ||
                        string.IsNullOrWhiteSpace(fields[4]) || fields[4].Length > 100 || fields[4].Any(char.IsControl)) continue;
                    onSeen(new Advertisement { Id = fields[1], Name = fields[4], Address = source.Address + ":" + port,
                        PairAddress = source.Address + ":" + (pairing == 0 ? Pairing.Port : pairing), Seen = DateTime.UtcNow });
                }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { if (closed) return; }
            }
        }

        public void Dispose() { closed = true; lock (membership) receiver.Close(); }
    }
}
