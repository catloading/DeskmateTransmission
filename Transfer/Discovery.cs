using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace DeskLink.Transfer
{
    internal static class Discovery
    {
        private const int Port = 42862;
        private const string ReplyPrefix = "TongZhuoChuan/1 here\n";
        private static readonly byte[] Query = Encoding.ASCII.GetBytes("TongZhuoChuan/1 discover");

        internal sealed class LocalAddress
        {
            internal IPAddress Ip;
            internal IPAddress Broadcast;
            internal bool HasGateway;
            internal string Adapter;
            public override string ToString() { return Ip + "  ·  " + Adapter; }
        }

        internal static List<LocalAddress> Addresses()
        {
            var list = new List<LocalAddress>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                try
                {
                    var props = nic.GetIPProperties();
                    foreach (var ip in props.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                    {
                        byte[] address = ip.Address.GetAddressBytes();
                        if (ip.IPv4Mask == null) continue;
                        byte[] mask = ip.IPv4Mask.GetAddressBytes();
                        byte[] broadcast = new byte[4];
                        for (int i = 0; i < 4; i++) broadcast[i] = (byte)(address[i] | ~mask[i]);
                        list.Add(new LocalAddress { Ip = ip.Address, Broadcast = new IPAddress(broadcast), Adapter = nic.Name,
                            HasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork) });
                    }
                }
                catch (NetworkInformationException) { }
            }
            if (list.Count == 0)
                list.Add(new LocalAddress { Ip = IPAddress.Loopback, Broadcast = IPAddress.Loopback, Adapter = "本机回环" });
            return list.OrderBy(a => a.Ip.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .ThenByDescending(a => a.HasGateway).ToList();
        }

        internal sealed class Nearby
        {
            internal string Address;
            internal string Name;
            public override string ToString() { return Name + "  ·  " + Address; }
        }

        // Discovery is optional. A direct IP connection also works when broadcasts are filtered.
        internal sealed class Responder : IDisposable
        {
            private readonly UdpClient udp;
            internal Responder(int tcpPort, string name)
            {
                try { udp = new UdpClient(new IPEndPoint(IPAddress.Any, Port)); }
                catch (SocketException) { return; }
                var reply = Encoding.UTF8.GetBytes(ReplyPrefix + tcpPort + "\n" + name);
                Task.Run(() =>
                {
                    try
                    {
                        while (true)
                        {
                            var source = new IPEndPoint(IPAddress.Any, 0);
                            byte[] request = udp.Receive(ref source);
                            if (request.SequenceEqual(Query)) udp.Send(reply, reply.Length, source);
                        }
                    }
                    catch (SocketException) { }
                    catch (ObjectDisposedException) { }
                });
            }
            public void Dispose() { if (udp != null) udp.Close(); }
        }

        internal static List<Nearby> Find()
        {
            var found = new Dictionary<string, Nearby>();
            using (var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)))
            {
                udp.EnableBroadcast = true;
                udp.Client.ReceiveTimeout = 200;
                var targets = Addresses().Select(a => a.Broadcast).Concat(new[] { IPAddress.Broadcast, IPAddress.Loopback }).Distinct();
                foreach (var target in targets)
                {
                    try { udp.Send(Query, Query.Length, new IPEndPoint(target, Port)); }
                    catch (SocketException) { }
                }
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 1300)
                {
                    var source = new IPEndPoint(IPAddress.Any, 0);
                    byte[] bytes;
                    try { bytes = udp.Receive(ref source); } catch (SocketException) { continue; }
                    if (bytes.Length > 512) continue;
                    string reply = Encoding.UTF8.GetString(bytes);
                    if (!reply.StartsWith(ReplyPrefix, StringComparison.Ordinal)) continue;
                    string[] fields = reply.Substring(ReplyPrefix.Length).Split('\n');
                    int port;
                    if (fields.Length != 2 || fields[1].Length == 0 || fields[1].Any(char.IsControl) ||
                        !int.TryParse(fields[0], out port) || port < 1 || port > 65535) continue;
                    string address = source.Address + ":" + port;
                    found[address] = new Nearby { Name = fields[1], Address = address };
                }
            }
            return found.Values.OrderBy(x => x.Name).ThenBy(x => x.Address).ToList();
        }
    }
}
