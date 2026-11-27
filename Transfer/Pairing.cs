using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeskLink.Transfer
{
    internal static class Pairing
    {
        internal const int Port = 42861;
        internal const string Scheme = "tongzhuan://";
        internal const string Version = "TongZhuoChuan/1";
        private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        internal static string NewCode()
        {
            byte[] random = new byte[12];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(random);
            return new string(random.Select(b => Alphabet[b & 31]).ToArray());
        }

        internal static string FormatCode(string code)
        {
            return code.Substring(0, 4) + "-" + code.Substring(4, 4) + "-" + code.Substring(8, 4);
        }

        internal static string Normalize(string input)
        {
            string code = new string((input ?? "").Where(c => !(char.IsWhiteSpace(c) || c == '-')).ToArray())
                .ToUpperInvariant().Replace('O', '0').Replace('I', '1').Replace('L', '1');
            if (code.Length != 12 || code.Any(c => Alphabet.IndexOf(c) < 0))
                throw new FormatException("请输入创建端显示的 12 位配对码。");
            return code;
        }

        internal static void ParseEndpoint(string address, out string host, out int port)
        {
            string[] parts = address.Trim().Split(':');
            host = parts[0];
            port = Port;
            var kind = Uri.CheckHostName(host);
            if ((kind != UriHostNameType.Dns && kind != UriHostNameType.IPv4) || parts.Length > 2 ||
                (parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out port)
                    || port < 1 || port > 65535)))
                throw new FormatException("请输入对方显示的 IPv4 地址和端口，例如 192.168.1.8:42861。");
        }

        internal static bool TryInvite(string text, out string address, out string code)
        {
            address = code = null;
            Uri uri;
            if (!text.Trim().StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) ||
                !Uri.TryCreate(text.Trim(), UriKind.Absolute, out uri) || uri.UserInfo.Length != 0 ||
                uri.Query.Length != 0 || uri.Fragment.Length != 0 || string.IsNullOrEmpty(uri.Host)) return false;
            address = uri.Host + ":" + (uri.Port > 0 ? uri.Port : Port);
            code = uri.AbsolutePath.Trim('/');
            return true;
        }

        // The invitation secret authenticates the TLS public key before the client
        // discloses the secret over TLS. No certificate is installed in a trust store.
        private static string KeyTag(string code, byte[] publicKey)
        {
            using (var hmac = new HMACSHA256(Encoding.ASCII.GetBytes(code)))
                return BitConverter.ToString(hmac.ComputeHash(publicKey)).Replace("-", "");
        }

        internal static X509Certificate2 NewCertificate(string code)
        {
            using (RSA key = RSA.Create(2048))
            {
                var probe = new CertificateRequest("CN=DeskLink", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                string tag = KeyTag(code, probe.PublicKey.EncodedKeyValue.RawData);
                var request = new CertificateRequest("CN=" + tag, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
                using (var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1)))
                {
                    // Windows Schannel on .NET Framework needs a user key container for server TLS.
                    string password = Guid.NewGuid().ToString("N");
                    return new X509Certificate2(cert.Export(X509ContentType.Pfx, password), password, X509KeyStorageFlags.UserKeySet);
                }
            }
        }

        private static bool MatchesInvitation(X509Certificate certificate, string code)
        {
            if (certificate == null) return false;
            using (var cert = new X509Certificate2(certificate))
            {
                string actual = cert.GetNameInfo(X509NameType.SimpleName, false);
                string expected = KeyTag(code, cert.PublicKey.EncodedKeyValue.RawData);
                return Wire.SameBytes(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expected));
            }
        }

        internal static void Configure(TcpClient client, bool handshake)
        {
            client.NoDelay = true;
            client.ReceiveTimeout = handshake ? 10000 : 0;
            client.SendTimeout = handshake ? 10000 : 45000;
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            // Detect a vanished computer even while both users leave the chat idle.
            var keepAlive = new byte[12];
            Buffer.BlockCopy(BitConverter.GetBytes(1u), 0, keepAlive, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(20000u), 0, keepAlive, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(3000u), 0, keepAlive, 8, 4);
            client.Client.IOControl(IOControlCode.KeepAliveValues, keepAlive, null);
        }

        internal static Peer Join(string address, string inputCode, string ownName, CancellationToken cancellation, DeviceHello local = null)
        {
            string host; int port;
            ParseEndpoint(address, out host, out port);
            string code = Normalize(inputCode);
            var client = new TcpClient(AddressFamily.InterNetwork);
            SslStream tls = null;
            bool matchedKey = true;
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    using (timeout.Token.Register(client.Close))
                    {
                        try
                        {
                            client.Connect(host, port);
                            Configure(client, true);
                            tls = new SslStream(client.GetStream(), false, (s, cert, chain, errors) =>
                            {
                                matchedKey = MatchesInvitation(cert, code);
                                return matchedKey;
                            });
                            tls.AuthenticateAsClient(host, null, SslProtocols.Tls12, false);
                            Wire.Write(tls, Packet.Hello, w =>
                            {
                                Wire.PutString(w, Version, 32);
                                Wire.PutString(w, code, 32);
                                Wire.PutString(w, ownName, 256);
                                local?.Write(w);
                            });
                            byte[] data = new byte[512]; int length;
                            if (Wire.Read(tls, new byte[Wire.HeaderSize], data, out length) != Packet.Welcome)
                                throw new IOException("对方没有接受连接。");
                            using (var reader = Wire.Reader(data, length))
                            {
                                string peer = ReadName(reader);
                                var device = DeviceHello.Read(reader);
                                Configure(client, false);
                                timeout.Token.ThrowIfCancellationRequested();
                                return new Peer(client, tls, peer, device);
                            }
                        }
                        catch (Exception ex)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            if (timeout.IsCancellationRequested) throw new TimeoutException("连接超时，请检查两台电脑的网络和对方地址。", ex);
                            if (!matchedKey) throw new AuthenticationException("配对码与对方不匹配，请核对后重试。", ex);
                            throw;
                        }
                    }
                }
            }
            catch
            {
                client.Close();
                if (tls != null) tls.Dispose();
                throw;
            }
        }

        internal static string ReadName(BinaryReader reader)
        {
            string value = new string(Wire.GetString(reader, 256).Where(c => !char.IsControl(c) &&
                char.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray()).Trim();
            return value.Length == 0 ? "另一台电脑" : value;
        }
    }

    internal sealed class Invitation : IDisposable
    {
        private readonly object gate = new object();
        private readonly TcpListener listener;
        private readonly X509Certificate2 certificate;
        private readonly string ownName;
        private readonly DeviceHello local;
        private readonly TaskCompletionSource<Peer> result =
            new TaskCompletionSource<Peer>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly HashSet<TcpClient> candidates = new HashSet<TcpClient>();
        private readonly Discovery.Responder discovery;
        private readonly Timer expiry;
        private bool stopped;

        internal string Code { get; }
        internal int Port { get; }
        internal Task<Peer> Accepted { get { return result.Task; } }

        internal Invitation(string machineName, DeviceHello localDevice = null)
        {
            ownName = machineName;
            local = localDevice;
            Code = Pairing.NewCode();
            certificate = Pairing.NewCertificate(Code);
            try
            {
                listener = new TcpListener(IPAddress.Any, Pairing.Port);
                try { listener.Start(8); }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
                {
                    listener.Stop();
                    listener = new TcpListener(IPAddress.Any, 0);
                    listener.Start(8);
                }
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                discovery = new Discovery.Responder(Port, machineName);
                expiry = new Timer(_ => Stop(new TimeoutException("邀请已过期，请重新创建连接。")), null,
                    TimeSpan.FromMinutes(10), Timeout.InfiniteTimeSpan);
                Task.Run((Action)AcceptLoop);
            }
            catch
            {
                if (listener != null) listener.Stop();
                if (discovery != null) discovery.Dispose();
                certificate.Dispose();
                throw;
            }
        }

        private void AcceptLoop()
        {
            try
            {
                while (true)
                {
                    TcpClient client = listener.AcceptTcpClient();
                    lock (gate)
                    {
                        if (stopped || candidates.Count >= 4) { client.Close(); continue; }
                        var certCopy = new X509Certificate2(certificate);
                        candidates.Add(client);
                        Task.Run(() => Handshake(client, certCopy));
                    }
                }
            }
            catch (Exception ex) { Stop(ex); }
        }

        private void Handshake(TcpClient client, X509Certificate2 certCopy)
        {
            SslStream tls = null;
            bool transferred = false;
            try
            {
                Pairing.Configure(client, true);
                tls = new SslStream(client.GetStream(), false);
                tls.AuthenticateAsServer(certCopy, false, SslProtocols.Tls12, false);
                byte[] data = new byte[512]; int length;
                if (Wire.Read(tls, new byte[Wire.HeaderSize], data, out length) != Packet.Hello) return;
                using (var reader = Wire.Reader(data, length))
                {
                    if (Wire.GetString(reader, 32) != Pairing.Version ||
                        !Wire.SameBytes(Encoding.ASCII.GetBytes(Wire.GetString(reader, 32)), Encoding.ASCII.GetBytes(Code))) return;
                    string name = Pairing.ReadName(reader);
                    var device = DeviceHello.Read(reader);
                    lock (gate)
                    {
                        if (stopped) return;
                        Wire.Write(tls, Packet.Welcome, w =>
                        {
                            Wire.PutString(w, ownName, 256);
                            if (device != null) local?.Write(w);
                        });
                        Pairing.Configure(client, false);
                        candidates.Remove(client);
                        transferred = true;
                        var peer = new Peer(client, tls, name, local == null ? null : device);
                        StopLocked();
                        result.TrySetResult(peer);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is AuthenticationException ||
                ex is SocketException || ex is InvalidOperationException || ex is CryptographicException ||
                ex is ArgumentException) { /* An unsuccessful attempt leaves the invitation available. */ }
            finally
            {
                lock (gate) candidates.Remove(client);
                if (!transferred)
                {
                    client.Close();
                    if (tls != null) tls.Dispose();
                }
                certCopy.Dispose();
            }
        }

        private void StopLocked()
        {
            stopped = true;
            expiry.Dispose();
            discovery.Dispose();
            listener.Stop();
            foreach (var client in candidates) client.Close();
            certificate.Dispose();
        }

        private void Stop(Exception error)
        {
            lock (gate)
            {
                if (stopped) return;
                StopLocked();
                if (error == null) result.TrySetCanceled();
                else result.TrySetException(error);
            }
        }

        public void Dispose() { Stop(null); }
    }
}
