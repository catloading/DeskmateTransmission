using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DeskLink.Transfer
{
    // Exchanged inside the code-authenticated first connection, never trusted from discovery.
    internal sealed class DeviceHello
    {
        internal readonly string Id;
        internal readonly int Port;
        internal DeviceHello(string id, int port) { Id = id; Port = port; }

        internal void Write(BinaryWriter writer)
        {
            Wire.PutString(writer, Id, 64);
            writer.Write(Port);
        }

        internal static DeviceHello Read(BinaryReader reader)
        {
            if (reader.BaseStream.Position == reader.BaseStream.Length) return null; // Version 1 peer.
            string id = Wire.GetString(reader, 64);
            int port = reader.ReadInt32();
            if (!ValidId(id) || port < 1 || port > 65535 || reader.BaseStream.Position != reader.BaseStream.Length)
                throw new InvalidDataException("设备身份或地址无效。");
            return new DeviceHello(id, port);
        }

        internal static bool ValidId(string id)
        {
            return id != null && id.Length == 64 && id.All(c => (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F'));
        }

        internal static string Fingerprint(X509Certificate cert)
        {
            if (cert == null) return null;
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(cert.GetRawCertData())).Replace("-", "");
        }
    }

    [DataContract]
    internal sealed class SavedDevice
    {
        [DataMember] internal string Id;
        [DataMember] internal string Name;
        [DataMember] internal string Address;
        [DataMember] internal DateTime PairedUtc;
    }

    internal sealed class DeviceBook
    {
        private readonly string root;
        private readonly string devices;
        internal DeviceBook(string directory)
        {
            root = directory;
            devices = Path.Combine(directory, "devices");
        }

        internal X509Certificate2 OpenIdentity()
        {
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, "identity.dat");
            if (!File.Exists(path))
            {
                string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var key = RSA.Create(2048))
                    {
                        var request = new CertificateRequest("CN=DeskLink-" + Guid.NewGuid().ToString("N"), key,
                            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                        request.CertificateExtensions.Add(new X509KeyUsageExtension(
                            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
                        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection {
                            new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2") }, false));
                        using (var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20)))
                            File.WriteAllBytes(temp, ProtectedData.Protect(cert.Export(X509ContentType.Pfx), null, DataProtectionScope.CurrentUser));
                    }
                    // Two instances opening a fresh profile must converge on one identity.
                    try { File.Move(temp, path); } catch (IOException) when (File.Exists(path)) { }
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            byte[] pfx = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            try { return new X509Certificate2(pfx, (string)null, X509KeyStorageFlags.UserKeySet); }
            finally { Array.Clear(pfx, 0, pfx.Length); }
        }

        internal IEnumerable<SavedDevice> Load()
        {
            var saved = new List<SavedDevice>();
            if (!Directory.Exists(devices)) return saved;
            foreach (string path in Directory.EnumerateFiles(devices, "*.device"))
            {
                try
                {
                    byte[] data = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
                    using (var stream = new MemoryStream(data))
                    {
                        var d = (SavedDevice)new DataContractJsonSerializer(typeof(SavedDevice)).ReadObject(stream);
                        if (d != null && DeviceHello.ValidId(d.Id) && Path.GetFileNameWithoutExtension(path) == d.Id)
                            saved.Add(d);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is CryptographicException || ex is SerializationException) { }
            }
            return saved;
        }

        internal void Save(SavedDevice device)
        {
            Directory.CreateDirectory(devices);
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(SavedDevice)).WriteObject(stream, device);
                LocalStore.WriteAtomic(Path.Combine(devices, device.Id + ".device"),
                    ProtectedData.Protect(stream.ToArray(), null, DataProtectionScope.CurrentUser));
            }
        }

        internal void Forget(string id)
        {
            if (DeviceHello.ValidId(id) && Directory.Exists(devices)) File.Delete(Path.Combine(devices, id + ".device"));
        }
    }
}
