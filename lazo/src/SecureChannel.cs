using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Lazo
{
    /// <summary>Primitivas del protocolo LAZO3 (docs/protocolo-seguro.md). Sin dependencias de Windows.</summary>
    internal static class LazoCrypto
    {
        private static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

        public static byte[] Random(int count)
        {
            byte[] bytes = new byte[count];
            lock (Rng) Rng.GetBytes(bytes);
            return bytes;
        }

        public static byte[] Sha256(params byte[][] parts)
        {
            using (SHA256 sha = SHA256.Create())
            {
                foreach (byte[] part in parts) sha.TransformBlock(part, 0, part.Length, null, 0);
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return sha.Hash;
            }
        }

        public static byte[] Hmac(byte[] key, params byte[][] parts)
        {
            using (HMACSHA256 hmac = new HMACSHA256(key))
            {
                foreach (byte[] part in parts) hmac.TransformBlock(part, 0, part.Length, null, 0);
                hmac.TransformFinalBlock(new byte[0], 0, 0);
                return hmac.Hash;
            }
        }

        /// <summary>HKDF-SHA256 (RFC 5869).</summary>
        public static byte[] Hkdf(byte[] salt, byte[] ikm, byte[] info, int length)
        {
            byte[] prk = Hmac(salt, ikm);
            byte[] output = new byte[length];
            byte[] previous = new byte[0];
            int done = 0;
            for (byte counter = 1; done < length; counter++)
            {
                previous = Hmac(prk, previous, info, new[] { counter });
                int take = Math.Min(previous.Length, length - done);
                Buffer.BlockCopy(previous, 0, output, done, take);
                done += take;
            }
            return output;
        }

        public static bool FixedEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        public static string Hex(byte[] bytes)
        {
            StringBuilder text = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) text.Append(b.ToString("x2"));
            return text.ToString();
        }

        public static byte[] Ascii(string text)
        {
            return Encoding.ASCII.GetBytes(text);
        }

        /// <summary>Clave pública P-256 como X || Y (64 bytes).</summary>
        public static byte[] PointBytes(ECParameters parameters)
        {
            byte[] result = new byte[64];
            Pad(parameters.Q.X).CopyTo(result, 0);
            Pad(parameters.Q.Y).CopyTo(result, 32);
            return result;
        }

        public static ECParameters PointParameters(byte[] xy)
        {
            if (xy == null || xy.Length != 64) throw new InvalidDataException("Clave pública no válida.");
            ECParameters parameters = new ECParameters { Curve = ECCurve.NamedCurves.nistP256 };
            parameters.Q.X = xy.Take(32).ToArray();
            parameters.Q.Y = xy.Skip(32).ToArray();
            return parameters;
        }

        private static byte[] Pad(byte[] value)
        {
            if (value.Length == 32) return value;
            if (value.Length > 32) throw new InvalidDataException("Coordenada fuera de rango.");
            byte[] padded = new byte[32];
            Buffer.BlockCopy(value, 0, padded, 32 - value.Length, value.Length);
            return padded;
        }

        public static bool Verify(byte[] publicKey, byte[] hash, byte[] signature)
        {
            try
            {
                using (ECDsa key = ECDsa.Create(PointParameters(publicKey)))
                    return signature != null && signature.Length == 64 && key.VerifyHash(hash, signature);
            }
            catch (CryptographicException) { return false; }
            catch (InvalidDataException) { return false; }
        }

        /// <summary>Firma ECDSA en DER (la que produce openssl) a r || s de 64 bytes; null si no es válida.</summary>
        public static byte[] DerToP1363(byte[] der)
        {
            try
            {
                int i = 0;
                if (der[i++] != 0x30) return null;
                int total = der[i++];
                if (total > 0x80) { i += total - 0x80; }
                byte[] result = new byte[64];
                for (int part = 0; part < 2; part++)
                {
                    if (der[i++] != 0x02) return null;
                    int length = der[i++];
                    while (length > 32 && der[i] == 0) { i++; length--; }
                    if (length > 32) return null;
                    Buffer.BlockCopy(der, i, result, part * 32 + 32 - length, length);
                    i += length;
                }
                return result;
            }
            catch (IndexOutOfRangeException) { return null; }
        }

        public static byte[] FromHex(string hex)
        {
            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        /// <summary>Código de 6 cifras que las dos personas comparan al emparejar.</summary>
        public static string Sas(byte[] transcript)
        {
            byte[] digest = Sha256(Ascii("LAZO3 SAS"), transcript);
            uint value = ((uint)digest[0] << 24) | ((uint)digest[1] << 16) | ((uint)digest[2] << 8) | digest[3];
            return (value % 1000000).ToString("000000");
        }

        public static string ShortFingerprint(string fingerprint)
        {
            if (string.IsNullOrEmpty(fingerprint) || fingerprint.Length < 16) return fingerprint ?? "";
            string head = fingerprint.Substring(0, 16).ToUpperInvariant();
            return head.Substring(0, 4) + " " + head.Substring(4, 4) + " " + head.Substring(8, 4) + " " + head.Substring(12, 4);
        }
    }

    /// <summary>Identidad de este equipo: identificador y clave ECDSA P-256 que nunca sale del equipo.</summary>
    internal sealed class DeviceIdentity
    {
        private readonly ECDsa _key;
        public readonly string Id;
        public readonly byte[] PublicKey;
        public readonly string Fingerprint;

        private DeviceIdentity(string id, ECDsa key)
        {
            Id = id;
            _key = key;
            PublicKey = LazoCrypto.PointBytes(key.ExportParameters(false));
            Fingerprint = LazoCrypto.Hex(LazoCrypto.Sha256(PublicKey));
        }

        public byte[] Sign(byte[] hash)
        {
            lock (_key) return _key.SignHash(hash);
        }

        public static DeviceIdentity Load(string folder, Guid id)
        {
            string path = Path.Combine(folder, "identity.key");
            ECDsa key = null;
            try
            {
                if (File.Exists(path))
                {
                    byte[] raw = Unprotect(File.ReadAllBytes(path));
                    if (raw != null && raw.Length == 96)
                    {
                        ECParameters parameters = LazoCrypto.PointParameters(raw.Skip(32).ToArray());
                        parameters.D = raw.Take(32).ToArray();
                        key = ECDsa.Create(parameters);
                    }
                }
            }
            catch { key = null; }
            if (key == null)
            {
                key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                ECParameters full = key.ExportParameters(true);
                byte[] raw = new byte[96];
                byte[] d = full.D;
                Buffer.BlockCopy(d, 0, raw, 32 - d.Length, d.Length);
                LazoCrypto.PointBytes(full).CopyTo(raw, 32);
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(path, Protect(raw));
            }
            return new DeviceIdentity(id.ToString("D").ToLowerInvariant(), key);
        }

        private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("Lazo identity v1");

        private static bool OnWindows { get { return Environment.OSVersion.Platform == PlatformID.Win32NT; } }

        private static byte[] Protect(byte[] raw)
        {
            if (!OnWindows) return raw;
            return ProtectedData.Protect(raw, Entropy, DataProtectionScope.CurrentUser);
        }

        private static byte[] Unprotect(byte[] stored)
        {
            if (!OnWindows) return stored;
            return ProtectedData.Unprotect(stored, Entropy, DataProtectionScope.CurrentUser);
        }
    }

    internal sealed class PairedDevice
    {
        public string Id;
        public string Fingerprint;
        public string Name;
        public DateTime Since;
        /// <summary>Las dos personas confirmaron el código de 6 cifras.</summary>
        public bool Verified;
    }

    /// <summary>
    /// Equipos conocidos: identificador → huella de su clave. Se guardan al primer contacto (como SSH)
    /// y quedan verificados si las dos personas confirman el código de 6 cifras.
    /// </summary>
    internal sealed class PairingStore
    {
        private readonly string _path;
        private readonly object _lock = new object();
        private readonly Dictionary<string, PairedDevice> _devices = new Dictionary<string, PairedDevice>(StringComparer.OrdinalIgnoreCase);

        public event Action Changed;

        public PairingStore(string folder)
        {
            _path = Path.Combine(folder, "paired.txt");
            try
            {
                if (!File.Exists(_path)) return;
                foreach (string row in File.ReadAllLines(_path))
                {
                    string[] parts = row.Split('|');
                    DateTime since;
                    if (parts.Length < 4 || parts[1].Length != 64) continue;
                    DateTime.TryParse(parts[3], out since);
                    _devices[parts[0]] = new PairedDevice { Id = parts[0], Fingerprint = parts[1], Name = parts[2], Since = since, Verified = parts.Length > 4 && parts[4] == "v" };
                }
            }
            catch { }
        }

        public PairedDevice Get(string id)
        {
            lock (_lock)
            {
                PairedDevice device;
                return id != null && _devices.TryGetValue(id, out device) ? device : null;
            }
        }

        public List<PairedDevice> All()
        {
            lock (_lock) return _devices.Values.OrderBy(d => d.Name).ToList();
        }

        public void Set(string id, string fingerprint, string name)
        {
            lock (_lock)
            {
                _devices[id] = new PairedDevice { Id = id, Fingerprint = fingerprint, Name = Clean(name), Since = DateTime.Now };
                Save();
            }
            Raise();
        }

        public void MarkVerified(string id, string name)
        {
            lock (_lock)
            {
                PairedDevice device;
                if (!_devices.TryGetValue(id, out device)) return;
                device.Verified = true;
                if (!string.IsNullOrWhiteSpace(name)) device.Name = Clean(name);
                Save();
            }
            Raise();
        }

        public void Remove(string id)
        {
            lock (_lock)
            {
                if (!_devices.Remove(id)) return;
                Save();
            }
            Raise();
        }

        private void Raise()
        {
            Action handler = Changed;
            if (handler != null) handler();
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                File.WriteAllLines(_path, _devices.Values.Select(d => d.Id + "|" + d.Fingerprint + "|" + Clean(d.Name) + "|" + d.Since.ToString("yyyy-MM-dd") + "|" + (d.Verified ? "v" : "")));
            }
            catch { }
        }

        private static string Clean(string value)
        {
            return new string((value ?? "").Where(c => !char.IsControl(c) && c != '|').Take(60).ToArray()).Trim();
        }
    }

    /// <summary>Saludo LAZO3 ya completado: quién es el otro equipo y el canal cifrado.</summary>
    internal sealed class SecureSession
    {
        public const byte Version = 1;
        public const int HandshakeTimeoutMs = 10000;
        public SecureStream Stream;
        public string PeerId;
        public byte[] PeerKey;
        public string PeerFingerprint;
        public byte[] Transcript;
        public string Sas { get { return LazoCrypto.Sas(Transcript); } }

        /// <summary>Lado que conecta: envía la cabecera "LAZO3".</summary>
        public static SecureSession Connect(Stream network, DeviceIdentity me)
        {
            using (ECDiffieHellman ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
            {
                byte[] ec = LazoCrypto.PointBytes(ephemeral.ExportParameters(false));
                byte[] nc = LazoCrypto.Random(32);
                Write(network, LazoCrypto.Ascii("LAZO3"), new[] { Version }, ec, nc);
                byte version = ReadExactly(network, 1)[0];
                if (version != Version) throw new InvalidDataException("Versión de protocolo no compatible.");
                byte[] es = ReadExactly(network, 64);
                byte[] ns = ReadExactly(network, 32);
                byte[] ids = ReadExactly(network, 36);
                byte[] ks = ReadExactly(network, 64);
                byte[] sigs = ReadExactly(network, 64);
                byte[] th1 = LazoCrypto.Sha256(LazoCrypto.Ascii("LAZO3/1"), ec, nc, es, ns, ids, ks);
                if (!LazoCrypto.Verify(ks, th1, sigs)) throw new InvalidDataException("El otro equipo no demostró su identidad.");
                byte[] idc = LazoCrypto.Ascii(me.Id);
                byte[] th2 = LazoCrypto.Sha256(th1, idc, me.PublicKey);
                Write(network, idc, me.PublicKey, me.Sign(th2));
                byte[] keys = DeriveKeys(ephemeral, es, th2);
                return Finish(network, keys, true, ids, ks, th2);
            }
        }

        /// <summary>Lado que escucha, después de leer la cabecera "LAZO3".</summary>
        public static SecureSession Accept(Stream network, DeviceIdentity me)
        {
            byte version = ReadExactly(network, 1)[0];
            if (version != Version) throw new InvalidDataException("Versión de protocolo no compatible.");
            byte[] ec = ReadExactly(network, 64);
            byte[] nc = ReadExactly(network, 32);
            using (ECDiffieHellman ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
            {
                byte[] es = LazoCrypto.PointBytes(ephemeral.ExportParameters(false));
                byte[] ns = LazoCrypto.Random(32);
                byte[] ids = LazoCrypto.Ascii(me.Id);
                byte[] th1 = LazoCrypto.Sha256(LazoCrypto.Ascii("LAZO3/1"), ec, nc, es, ns, ids, me.PublicKey);
                Write(network, new[] { Version }, es, ns, ids, me.PublicKey, me.Sign(th1));
                byte[] idc = ReadExactly(network, 36);
                byte[] kc = ReadExactly(network, 64);
                byte[] sigc = ReadExactly(network, 64);
                byte[] th2 = LazoCrypto.Sha256(th1, idc, kc);
                if (!LazoCrypto.Verify(kc, th2, sigc)) throw new InvalidDataException("El otro equipo no demostró su identidad.");
                byte[] keys = DeriveKeys(ephemeral, ec, th2);
                return Finish(network, keys, false, idc, kc, th2);
            }
        }

        private static SecureSession Finish(Stream network, byte[] keys, bool client, byte[] peerId, byte[] peerKey, byte[] th2)
        {
            string id = Encoding.ASCII.GetString(peerId);
            Guid parsed;
            if (!Guid.TryParseExact(id, "D", out parsed)) throw new InvalidDataException("Identificador no válido.");
            byte[] encCs = keys.Take(32).ToArray(), macCs = keys.Skip(32).Take(32).ToArray();
            byte[] encSc = keys.Skip(64).Take(32).ToArray(), macSc = keys.Skip(96).Take(32).ToArray();
            SecureStream stream = client
                ? new SecureStream(network, encSc, macSc, encCs, macCs)
                : new SecureStream(network, encCs, macCs, encSc, macSc);
            return new SecureSession
            {
                Stream = stream,
                PeerId = parsed.ToString("D"),
                PeerKey = peerKey,
                PeerFingerprint = LazoCrypto.Hex(LazoCrypto.Sha256(peerKey)),
                Transcript = th2
            };
        }

        private static byte[] DeriveKeys(ECDiffieHellman ephemeral, byte[] peerPoint, byte[] th2)
        {
            byte[] ikm;
            using (ECDiffieHellman peer = ECDiffieHellman.Create(LazoCrypto.PointParameters(peerPoint)))
                ikm = ephemeral.DeriveKeyFromHash(peer.PublicKey, HashAlgorithmName.SHA256);
            return LazoCrypto.Hkdf(th2, ikm, LazoCrypto.Ascii("lazo3 keys"), 128);
        }

        private static void Write(Stream network, params byte[][] parts)
        {
            using (MemoryStream buffer = new MemoryStream())
            {
                foreach (byte[] part in parts) buffer.Write(part, 0, part.Length);
                network.Write(buffer.GetBuffer(), 0, (int)buffer.Length);
                network.Flush();
            }
        }

        internal static byte[] ReadExactly(Stream stream, int count)
        {
            byte[] buffer = new byte[count];
            int done = 0;
            while (done < count)
            {
                int read = stream.Read(buffer, done, count - done);
                if (read <= 0) throw new EndOfStreamException();
                done += read;
            }
            return buffer;
        }
    }

    /// <summary>
    /// Registros AES-256-CBC + HMAC-SHA256 con número de secuencia (cifrar y luego autenticar).
    /// Lectura y escritura son independientes: un hilo puede leer mientras otro escribe.
    /// Lo escrito se acumula hasta <see cref="Flush"/> o hasta llenar un registro de 64 KiB.
    /// </summary>
    internal sealed class SecureStream : Stream
    {
        public const int MaxPlain = 65536;
        private readonly Stream _inner;
        private readonly byte[] _readKey, _writeKey;
        private readonly HMACSHA256 _readMac, _writeMac;
        private readonly Aes _readAes, _writeAes;
        private readonly object _readLock = new object(), _writeLock = new object();
        private readonly MemoryStream _pending = new MemoryStream();
        private ulong _readSeq, _writeSeq;
        private byte[] _plain = new byte[0];
        private int _plainOffset;
        private bool _ended;

        public SecureStream(Stream inner, byte[] readKey, byte[] readMac, byte[] writeKey, byte[] writeMac)
        {
            _inner = inner;
            _readKey = readKey;
            _writeKey = writeKey;
            _readMac = new HMACSHA256(readMac);
            _writeMac = new HMACSHA256(writeMac);
            _readAes = NewAes();
            _writeAes = NewAes();
        }

        private static Aes NewAes()
        {
            Aes aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.KeySize = 256;
            return aes;
        }

        public Stream Inner { get { return _inner; } }
        public override bool CanRead { get { return true; } }
        public override bool CanWrite { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override int ReadTimeout { get { return _inner.ReadTimeout; } set { _inner.ReadTimeout = value; } }
        public override int WriteTimeout { get { return _inner.WriteTimeout; } set { _inner.WriteTimeout = value; } }
        public override bool CanTimeout { get { return _inner.CanTimeout; } }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0) return 0;
            lock (_readLock)
            {
                if (_plainOffset >= _plain.Length)
                {
                    if (_ended || !ReadRecord()) { _ended = true; return 0; }
                }
                int take = Math.Min(count, _plain.Length - _plainOffset);
                Buffer.BlockCopy(_plain, _plainOffset, buffer, offset, take);
                _plainOffset += take;
                return take;
            }
        }

        private bool ReadRecord()
        {
            while (true)
            {
                byte[] header = new byte[4];
                int got = 0;
                while (got < 4)
                {
                    int read = _inner.Read(header, got, 4 - got);
                    if (read <= 0)
                    {
                        if (got == 0) return false;
                        throw new EndOfStreamException();
                    }
                    got += read;
                }
                int length = BitConverter.ToInt32(header, 0);
                if (length < 16 || length > MaxPlain + 16 || length % 16 != 0) throw new InvalidDataException("Registro cifrado no válido.");
                byte[] iv = SecureSession.ReadExactly(_inner, 16);
                byte[] cipher = SecureSession.ReadExactly(_inner, length);
                byte[] tag = SecureSession.ReadExactly(_inner, 32);
                byte[] expected = Tag(_readMac, _readSeq, header, iv, cipher);
                if (!LazoCrypto.FixedEquals(tag, expected)) throw new InvalidDataException("Registro alterado o fuera de orden.");
                _readSeq++;
                using (ICryptoTransform decryptor = _readAes.CreateDecryptor(_readKey, iv))
                    _plain = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
                _plainOffset = 0;
                if (_plain.Length > 0) return true;
            }
        }

        private static byte[] Tag(HMACSHA256 mac, ulong seq, byte[] header, byte[] iv, byte[] cipher)
        {
            mac.Initialize();
            byte[] sequence = BitConverter.GetBytes(seq);
            mac.TransformBlock(sequence, 0, 8, null, 0);
            mac.TransformBlock(header, 0, 4, null, 0);
            mac.TransformBlock(iv, 0, 16, null, 0);
            mac.TransformFinalBlock(cipher, 0, cipher.Length);
            return mac.Hash;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_writeLock)
            {
                while (count > 0)
                {
                    int take = (int)Math.Min(count, MaxPlain - _pending.Length);
                    _pending.Write(buffer, offset, take);
                    offset += take;
                    count -= take;
                    if (_pending.Length >= MaxPlain) EmitPending();
                }
            }
        }

        public override void Flush()
        {
            lock (_writeLock)
            {
                EmitPending();
                _inner.Flush();
            }
        }

        private void EmitPending()
        {
            if (_pending.Length == 0) return;
            byte[] iv = LazoCrypto.Random(16);
            byte[] cipher;
            using (ICryptoTransform encryptor = _writeAes.CreateEncryptor(_writeKey, iv))
                cipher = encryptor.TransformFinalBlock(_pending.GetBuffer(), 0, (int)_pending.Length);
            _pending.SetLength(0);
            byte[] header = BitConverter.GetBytes(cipher.Length);
            byte[] tag = Tag(_writeMac, _writeSeq, header, iv, cipher);
            _writeSeq++;
            byte[] record = new byte[4 + 16 + cipher.Length + 32];
            Buffer.BlockCopy(header, 0, record, 0, 4);
            Buffer.BlockCopy(iv, 0, record, 4, 16);
            Buffer.BlockCopy(cipher, 0, record, 20, cipher.Length);
            Buffer.BlockCopy(tag, 0, record, 20 + cipher.Length, 32);
            _inner.Write(record, 0, record.Length);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _inner.Dispose(); } catch { }
                _readMac.Dispose();
                _writeMac.Dispose();
                _readAes.Dispose();
                _writeAes.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
