using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Lazo
{
    /// <summary>Reto que el anfitrión envía al visor: sal e iteraciones de la contraseña guardada y un nonce nuevo.</summary>
    internal sealed class RemoteChallenge
    {
        public byte[] Salt;
        public int Iterations;
        public byte[] Nonce;
    }

    /// <summary>
    /// Control remoto con contraseña (docs/control-remoto.md): guarda solo un hash de la contraseña
    /// (PBKDF2-HMAC-SHA256, sal de 16 bytes, 150 000 iteraciones) en <c>remote.txt</c> del perfil de Lazo,
    /// comprueba la prueba que manda el visor (HMAC del reto) y bloquea a quien falla demasiadas veces.
    /// Formato de la línea: <c>v1|iteraciones|sal en base64|hash en base64|1 o 0 (activo)</c>.
    /// </summary>
    internal sealed class RemoteAccess
    {
        public const int MinPasswordLength = 8;
        public const int DefaultIterations = 150000;
        public const int SaltLength = 16;
        public const int NonceLength = 16;
        public const int KeyLength = 32;
        /// <summary>Contraseñas erróneas seguidas del mismo equipo que lo bloquean.</summary>
        public const int MaxFailures = 5;
        public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);
        public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(10);

        private readonly string _path;
        private readonly object _gate = new object();
        private readonly Dictionary<string, List<DateTime>> _failures = new Dictionary<string, List<DateTime>>();
        private readonly Dictionary<string, DateTime> _locked = new Dictionary<string, DateTime>();
        private static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();
        private byte[] _salt, _hash;
        private int _iterations;
        private bool _enabled;

        /// <summary>Reloj en UTC; las pruebas lo cambian para no esperar diez minutos.</summary>
        public Func<DateTime> Clock = delegate { return DateTime.UtcNow; };

        public RemoteAccess(string folder)
        {
            _path = Path.Combine(folder, "remote.txt");
            Load();
        }

        /// <summary>Hay una contraseña guardada.</summary>
        public bool HasPassword
        {
            get { lock (_gate) return _hash != null; }
        }

        /// <summary>El control remoto está permitido (activado y con contraseña).</summary>
        public bool Enabled
        {
            get { lock (_gate) return _enabled && _hash != null; }
        }

        /// <summary>Guarda una contraseña nueva (mínimo 8 caracteres) y deja el control remoto activado o no, según <paramref name="enable"/>.</summary>
        public void SetPassword(string password, bool enable)
        {
            if (password == null || password.Length < MinPasswordLength)
                throw new ArgumentException("La contraseña necesita al menos " + MinPasswordLength + " caracteres.");
            byte[] salt = new byte[SaltLength];
            Rng.GetBytes(salt);
            byte[] hash = DeriveKey(password, salt, DefaultIterations);
            lock (_gate)
            {
                _salt = salt;
                _hash = hash;
                _iterations = DefaultIterations;
                _enabled = enable;
                Save();
            }
        }

        /// <summary>Activa o desactiva el control remoto sin tocar la contraseña. Activarlo sin contraseña no es posible.</summary>
        public void SetEnabled(bool enabled)
        {
            lock (_gate)
            {
                if (enabled && _hash == null) throw new InvalidOperationException("Primero elige una contraseña.");
                _enabled = enabled;
                if (_hash != null) Save();
            }
        }

        /// <summary>Quita la contraseña y desactiva el control remoto.</summary>
        public void Clear()
        {
            lock (_gate)
            {
                _salt = null;
                _hash = null;
                _enabled = false;
                try { if (File.Exists(_path)) File.Delete(_path); } catch { }
            }
        }

        /// <summary>Reto nuevo para un intento de conexión: la sal y las iteraciones guardadas y un nonce aleatorio.</summary>
        public RemoteChallenge NewChallenge()
        {
            lock (_gate)
            {
                if (_hash == null) throw new InvalidOperationException("No hay contraseña.");
                byte[] nonce = new byte[NonceLength];
                Rng.GetBytes(nonce);
                return new RemoteChallenge { Salt = (byte[])_salt.Clone(), Iterations = _iterations, Nonce = nonce };
            }
        }

        /// <summary>Comprueba la prueba del visor en tiempo constante.</summary>
        public bool Verify(RemoteChallenge challenge, byte[] proof, string viewerId, string hostId)
        {
            byte[] key;
            lock (_gate) key = _hash == null ? null : (byte[])_hash.Clone();
            if (key == null || challenge == null || proof == null) return false;
            byte[] expected = ProofFromKey(key, challenge.Nonce, viewerId, hostId);
            return FixedEquals(expected, proof);
        }

        /// <summary>El equipo está bloqueado por demasiados intentos fallidos.</summary>
        public bool IsLocked(string peerId)
        {
            lock (_gate)
            {
                DateTime until;
                if (!_locked.TryGetValue(peerId, out until)) return false;
                if (until > Clock()) return true;
                _locked.Remove(peerId);
                return false;
            }
        }

        /// <summary>Anota una contraseña errónea; a la quinta en diez minutos, el equipo queda bloqueado diez minutos.</summary>
        public void RegisterFailure(string peerId)
        {
            lock (_gate)
            {
                DateTime now = Clock();
                List<DateTime> list;
                if (!_failures.TryGetValue(peerId, out list)) { list = new List<DateTime>(); _failures[peerId] = list; }
                list.RemoveAll(t => now - t > FailureWindow);
                list.Add(now);
                if (list.Count >= MaxFailures)
                {
                    _locked[peerId] = now + LockDuration;
                    list.Clear();
                }
            }
        }

        /// <summary>Un acierto reinicia la cuenta de fallos de ese equipo.</summary>
        public void RegisterSuccess(string peerId)
        {
            lock (_gate)
            {
                _failures.Remove(peerId);
                _locked.Remove(peerId);
            }
        }

        // ── Criptografía (la usan también el visor y las pruebas) ─────────────

        /// <summary>PBKDF2-HMAC-SHA256 de 32 bytes con la contraseña en UTF-8.</summary>
        public static byte[] DeriveKey(string password, byte[] salt, int iterations)
        {
            byte[] secret = Encoding.UTF8.GetBytes(password ?? "");
            using (HMACSHA256 hmac = new HMACSHA256(secret))
            {
                // Un solo bloque de 32 bytes: T1 = U1 ^ U2 ^ … ^ Uc, con U1 = HMAC(P, sal ‖ INT(1)).
                byte[] first = new byte[salt.Length + 4];
                Buffer.BlockCopy(salt, 0, first, 0, salt.Length);
                first[salt.Length + 3] = 1;
                byte[] u = hmac.ComputeHash(first);
                byte[] t = (byte[])u.Clone();
                for (int i = 1; i < iterations; i++)
                {
                    u = hmac.ComputeHash(u);
                    for (int j = 0; j < t.Length; j++) t[j] ^= u[j];
                }
                return t;
            }
        }

        /// <summary>
        /// Prueba del visor: HMAC-SHA256 con la clave derivada de la contraseña sobre el nonce y los dos identificadores
        /// (minúscula, con guiones, «visor|anfitrión»). Atar los identificadores impide reutilizarla con otro equipo.
        /// </summary>
        public static byte[] ComputeProof(string password, byte[] salt, int iterations, byte[] nonce, string viewerId, string hostId)
        {
            return ProofFromKey(DeriveKey(password, salt, iterations), nonce, viewerId, hostId);
        }

        internal static byte[] ProofFromKey(byte[] key, byte[] nonce, string viewerId, string hostId)
        {
            byte[] ids = Encoding.UTF8.GetBytes((viewerId ?? "").ToLowerInvariant() + "|" + (hostId ?? "").ToLowerInvariant());
            byte[] message = new byte[nonce.Length + ids.Length];
            Buffer.BlockCopy(nonce, 0, message, 0, nonce.Length);
            Buffer.BlockCopy(ids, 0, message, nonce.Length, ids.Length);
            using (HMACSHA256 hmac = new HMACSHA256(key)) return hmac.ComputeHash(message);
        }

        internal static bool FixedEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int difference = 0;
            for (int i = 0; i < a.Length; i++) difference |= a[i] ^ b[i];
            return difference == 0;
        }

        // ── Archivo ───────────────────────────────────────────────────────

        private void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;
                string[] parts = File.ReadAllText(_path, Encoding.ASCII).Trim().Split('|');
                int iterations;
                if (parts.Length < 5 || parts[0] != "v1" || !int.TryParse(parts[1], out iterations) || iterations < 1000) return;
                byte[] salt = Convert.FromBase64String(parts[2]);
                byte[] hash = Convert.FromBase64String(parts[3]);
                if (salt.Length != SaltLength || hash.Length != KeyLength) return;
                _iterations = iterations;
                _salt = salt;
                _hash = hash;
                _enabled = parts[4] == "1";
            }
            catch { _salt = _hash = null; _enabled = false; }
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                string line = "v1|" + _iterations + "|" + Convert.ToBase64String(_salt) + "|" + Convert.ToBase64String(_hash) + "|" + (_enabled ? "1" : "0");
                string temp = _path + ".tmp";
                File.WriteAllText(temp, line, Encoding.ASCII);
                if (File.Exists(_path)) File.Delete(_path);
                File.Move(temp, _path);
            }
            catch { }
        }
    }
}
