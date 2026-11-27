using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace DeskLink.Transfer
{
    internal sealed class DeviceStatus
    {
        internal string Id, Name, PairAddress, Address;
        internal bool Trusted, Nearby, Paused;
        internal DateTime PairedUtc;
    }

    internal sealed class DeviceNetwork : IDisposable
    {
        private const string Protocol = "TongZhuoChuan/resume/2";
        private readonly object gate = new object();
        private readonly DeviceBook book;
        private readonly string ownName;
        private readonly X509Certificate2 identity;
        private readonly TcpListener listener;
        private readonly Dictionary<string, SavedDevice> trusted;
        private readonly Dictionary<string, Dictionary<string, Advertisement>> sightings =
            new Dictionary<string, Dictionary<string, Advertisement>>();
        private readonly Dictionary<string, Peer> connected = new Dictionary<string, Peer>();
        private readonly HashSet<string> paused = new HashSet<string>(), dialing = new HashSet<string>();
        private readonly Dictionary<string, DateTime> nextTry = new Dictionary<string, DateTime>();
        private readonly HashSet<TcpClient> handshakes = new HashSet<TcpClient>();
        private readonly SemaphoreSlim wake = new SemaphoreSlim(0, 1);
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private Presence presence;
        private bool stopped;
        private int invitePort, revision;
        internal DeviceHello Local { get; }
        internal int Revision { get { return Volatile.Read(ref revision); } }
        internal event Action<Peer, bool> Connected;
        internal event Action<string> StatusMessage;

        internal DeviceNetwork(string root, string name)
        {
            ownName = name;
            book = new DeviceBook(root);
            trusted = book.Load().ToDictionary(d => d.Id);
            identity = book.OpenIdentity();
            try
            {
                listener = new TcpListener(IPAddress.Any, 42863);
                try { listener.Start(12); }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
                {
                    listener.Stop();
                    listener = new TcpListener(IPAddress.Any, 0);
                    listener.Start(12);
                }
                Local = new DeviceHello(DeviceHello.Fingerprint(identity), ((IPEndPoint)listener.LocalEndpoint).Port);
            }
            catch { listener?.Stop(); identity.Dispose(); throw; }
        }

        internal void Start()
        {
            try { presence = new Presence(Seen); }
            catch (SocketException) { StatusMessage?.Invoke("附近设备发现暂不可用，已配对设备仍会尝试上次地址。"); }
            Task.Run((Action)AcceptLoop);
            Task.Run((Func<Task>)MonitorAsync);
        }

        internal void SetInvitationPort(int port) { Volatile.Write(ref invitePort, port); Wake(); }
        private void Wake() { try { wake.Release(); } catch (SemaphoreFullException) { } }

        private void Seen(Advertisement entry)
        {
            if (entry.Id == Local.Id) return;
            lock (gate)
            {
                if (stopped) return;
                Dictionary<string, Advertisement> routes;
                if (!sightings.TryGetValue(entry.Id, out routes))
                {
                    if (sightings.Count >= 256 && !trusted.ContainsKey(entry.Id)) return;
                    sightings[entry.Id] = routes = new Dictionary<string, Advertisement>();
                }
                Advertisement old;
                if (!routes.TryGetValue(entry.Address, out old) || old.Name != entry.Name || old.PairAddress != entry.PairAddress)
                {
                    if (old == null && routes.Count >= 8) return;
                    revision++;
                    nextTry.Remove(entry.Id);
                    Wake();
                }
                routes[entry.Address] = entry;
            }
        }

        internal List<DeviceStatus> Snapshot()
        {
            lock (gate)
            {
                var list = new List<DeviceStatus>();
                foreach (string id in trusted.Keys.Union(sightings.Keys))
                {
                    SavedDevice saved;
                    Dictionary<string, Advertisement> routes;
                    trusted.TryGetValue(id, out saved);
                    Advertisement seen = sightings.TryGetValue(id, out routes) ? routes.Values.FirstOrDefault() : null;
                    list.Add(new DeviceStatus { Id = id, Name = saved?.Name ?? seen?.Name, Trusted = saved != null,
                        Address = seen?.Address ?? saved?.Address,
                        Nearby = seen != null, PairAddress = seen?.PairAddress, Paused = paused.Contains(id),
                        PairedUtc = saved?.PairedUtc ?? DateTime.MinValue });
                }
                return list;
            }
        }

        private async Task MonitorAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                try { presence?.Announce(Local, ownName, Volatile.Read(ref invitePort)); }
                catch (Exception ex) when (ex is SocketException || ex is ObjectDisposedException || ex is NetworkInformationException) { }
                lock (gate)
                {
                    if (stopped) return;
                    DateTime now = DateTime.UtcNow;
                    foreach (var host in sightings.ToArray())
                    {
                        foreach (var old in host.Value.Values.Where(x => (now - x.Seen).TotalSeconds > 14).ToList())
                        { host.Value.Remove(old.Address); revision++; }
                        if (host.Value.Count == 0) sightings.Remove(host.Key);
                    }
                    foreach (var d in trusted.Values)
                    {
                        // A fixed initiator per pair prevents two simultaneous connections on startup.
                        if (string.CompareOrdinal(Local.Id, d.Id) >= 0 || connected.ContainsKey(d.Id) ||
                            paused.Contains(d.Id) || dialing.Contains(d.Id)) continue;
                        DateTime after;
                        if (nextTry.TryGetValue(d.Id, out after) && now < after) continue;
                        Dictionary<string, Advertisement> routes;
                        var addresses = sightings.TryGetValue(d.Id, out routes) ? routes.Keys.ToList() : new List<string>();
                        if (!string.IsNullOrEmpty(d.Address)) addresses.Add(d.Address);
                        if (addresses.Count == 0) continue;
                        dialing.Add(d.Id);
                        nextTry[d.Id] = now.AddSeconds(6);
                        _ = Task.Run(() => Dial(d.Id, addresses.Distinct().ToArray()));
                    }
                }
                try { await wake.WaitAsync(TimeSpan.FromSeconds(3), stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        private X509Certificate2 Track(TcpClient client)
        {
            lock (gate)
            {
                if (stopped || handshakes.Count >= 12) return null;
                var copy = new X509Certificate2(identity);
                handshakes.Add(client);
                return copy;
            }
        }

        private void AcceptLoop()
        {
            try
            {
                while (true)
                {
                    var client = listener.AcceptTcpClient();
                    var copy = Track(client);
                    if (copy == null) { client.Close(); continue; }
                    Task.Run(() => Handshake(client, copy, null, null));
                }
            }
            catch (Exception ex) when (ex is SocketException || ex is InvalidOperationException) { }
        }

        private void Dial(string id, string[] addresses)
        {
            try
            {
                foreach (var address in addresses)
                {
                    lock (gate) if (!Allowed(id) || connected.ContainsKey(id)) return;
                    var client = new TcpClient(AddressFamily.InterNetwork);
                    var copy = Track(client);
                    if (copy == null) { client.Close(); return; }
                    if (Handshake(client, copy, id, address)) return;
                }
            }
            finally { lock (gate) dialing.Remove(id); }
        }

        private bool Allowed(string id)
        {
            return !stopped && id != null && trusted.ContainsKey(id) && !paused.Contains(id);
        }

        private void SendIdentity(SslStream tls, Packet type)
        {
            Wire.Write(tls, type, w => { Wire.PutString(w, Protocol, 32); Wire.PutString(w, ownName, 256); Local.Write(w); });
        }

        private static Peer ReadIdentity(TcpClient client, SslStream tls, Packet expected, string pin)
        {
            byte[] data = new byte[512]; int count;
            if (Wire.Read(tls, new byte[Wire.HeaderSize], data, out count) != expected)
                throw new InvalidDataException("连接类型无效。");
            using (var reader = Wire.Reader(data, count))
            {
                if (Wire.GetString(reader, 32) != Protocol) throw new InvalidDataException("设备协议不匹配。");
                string name = Pairing.ReadName(reader);
                var device = DeviceHello.Read(reader);
                if (device == null || device.Id != pin) throw new AuthenticationException("设备身份验证失败。");
                return new Peer(client, tls, name, device);
            }
        }

        private bool Handshake(TcpClient client, X509Certificate2 copy, string targetId, string address)
        {
            SslStream tls = null;
            bool adopted = false;
            try
            {
                if (targetId != null)
                {
                    string host; int port;
                    Pairing.ParseEndpoint(address, out host, out port);
                    var connect = client.BeginConnect(host, port, null, null);
                    using (var wait = connect.AsyncWaitHandle)
                    {
                        if (!wait.WaitOne(2000)) return false;
                        client.EndConnect(connect);
                    }
                }
                Pairing.Configure(client, true);
                tls = new SslStream(client.GetStream(), false, (s, cert, chain, errors) =>
                {
                    string pin = DeviceHello.Fingerprint(cert);
                    lock (gate) return Allowed(pin) && (targetId == null || targetId == pin);
                }, (s, host, certs, remote, issuers) => copy);
                Peer peer;
                if (targetId == null)
                {
                    tls.AuthenticateAsServer(copy, true, SslProtocols.Tls12, false);
                    string pin = DeviceHello.Fingerprint(tls.RemoteCertificate);
                    if (!tls.IsMutuallyAuthenticated || string.CompareOrdinal(pin, Local.Id) >= 0) return false;
                    peer = ReadIdentity(client, tls, Packet.Hello, pin);
                }
                else
                {
                    tls.AuthenticateAsClient(targetId, new X509CertificateCollection { copy }, SslProtocols.Tls12, false);
                    if (!tls.IsMutuallyAuthenticated) return false;
                    SendIdentity(tls, Packet.Hello);
                    peer = ReadIdentity(client, tls, Packet.Welcome, targetId);
                }
                Pairing.Configure(client, false);
                adopted = Adopt(peer, false, targetId == null);
                return adopted;
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is SocketException || ex is AuthenticationException ||
                ex is CryptographicException || ex is InvalidOperationException || ex is ArgumentException || ex is FormatException) { return false; }
            finally
            {
                lock (gate) handshakes.Remove(client);
                if (!adopted) { client.Close(); tls?.Dispose(); }
                copy.Dispose();
            }
        }

        internal void Remember(Peer peer)
        {
            try { if (!Adopt(peer, true)) peer.Dispose(); }
            catch { peer.Dispose(); throw; }
        }

        private bool Adopt(Peer peer, bool firstPair, bool acknowledge = false)
        {
            string id = peer.Device.Id;
            string storageError = null;
            lock (gate)
            {
                if (stopped || id == Local.Id || (!firstPair && !Allowed(id))) return false;
                SavedDevice old;
                trusted.TryGetValue(id, out old);
                var saved = new SavedDevice { Id = id, Name = peer.Name, Address = peer.Address,
                    PairedUtc = old?.PairedUtc ?? DateTime.UtcNow };
                if (old == null || old.Address != saved.Address || old.Name != saved.Name)
                {
                    try { book.Save(saved); }
                    catch (Exception ex) when (!firstPair && (ex is IOException || ex is UnauthorizedAccessException || ex is CryptographicException))
                    { storageError = "上次连接地址未能保存：" + ex.Message; }
                }
                trusted[id] = saved;
                if (firstPair) paused.Remove(id);
                if (connected.ContainsKey(id)) return false;
                connected[id] = peer;
                handshakes.Remove(peer.Client);
                revision++;
            }
            if (acknowledge)
            {
                try { SendIdentity(peer.Stream, Packet.Welcome); }
                catch { Release(peer); throw; }
            }
            if (storageError != null) StatusMessage?.Invoke(storageError);
            Connected?.Invoke(peer, firstPair);
            return true;
        }

        internal void Release(Peer peer)
        {
            if (peer.Device == null) return;
            lock (gate)
            {
                string id = peer.Device.Id;
                Peer active;
                if (connected.TryGetValue(id, out active) && active == peer)
                { connected.Remove(id); nextTry.Remove(id); revision++; }
            }
            Wake();
        }

        internal void Pause(string id)
        {
            Peer active;
            lock (gate) { paused.Add(id); connected.TryGetValue(id, out active); revision++; }
            active?.Dispose();
        }

        internal void Resume(string id)
        {
            lock (gate) { paused.Remove(id); nextTry.Remove(id); revision++; }
            Wake();
        }

        internal void Forget(string id)
        {
            Peer active;
            lock (gate)
            {
                book.Forget(id);
                trusted.Remove(id); paused.Remove(id); nextTry.Remove(id);
                connected.TryGetValue(id, out active); revision++;
            }
            active?.Dispose();
        }

        public void Dispose()
        {
            Peer[] peers;
            lock (gate)
            {
                if (stopped) return;
                stopped = true;
                stop.Cancel();
                listener.Stop();
                foreach (var client in handshakes) client.Close();
                peers = connected.Values.ToArray();
                identity.Dispose();
            }
            presence?.Dispose();
            foreach (var peer in peers) peer.Dispose();
        }
    }
}
