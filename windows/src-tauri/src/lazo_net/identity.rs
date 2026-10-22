// This computer's identity and the computers it knows. Same files, same formats as classic Lazo
// (%APPDATA%\Lazo\device.id, identity.key, paired.txt), so the island takes over a computer that
// had classic Lazo without anybody having to pair again.

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

use p256::ecdsa::SigningKey;
use p256::PublicKey;

use super::crypto;

/// A GUID in the "D" format: 8-4-4-4-12 hexadecimal digits.
pub fn is_guid(text: &str) -> bool {
    let b = text.as_bytes();
    b.len() == 36
        && b.iter().enumerate().all(|(i, c)| match i {
            8 | 13 | 18 | 23 => *c == b'-',
            _ => c.is_ascii_hexdigit(),
        })
}

/// A new random identifier (version 4), lowercase.
pub fn new_guid() -> String {
    let mut b = crypto::random::<16>();
    b[6] = (b[6] & 0x0f) | 0x40;
    b[8] = (b[8] & 0x3f) | 0x80;
    let h = crypto::hex(&b);
    format!("{}-{}-{}-{}-{}", &h[0..8], &h[8..12], &h[12..16], &h[16..20], &h[20..32])
}

/// `device.id`: the identifier, created on first use.
pub fn load_id(folder: &Path) -> String {
    let path = folder.join("device.id");
    if let Ok(text) = std::fs::read_to_string(&path) {
        let text = text.trim().trim_start_matches('{').trim_end_matches('}').to_ascii_lowercase();
        if is_guid(&text) {
            return text;
        }
    }
    let id = new_guid();
    let _ = std::fs::create_dir_all(folder);
    let _ = std::fs::write(&path, &id);
    id
}

#[derive(Clone)]
pub struct DeviceIdentity {
    pub id: String,
    key: SigningKey,
    /// X || Y, 64 bytes.
    pub public: [u8; 64],
    /// SHA-256 of `public`, lowercase hexadecimal.
    pub fingerprint: String,
}

impl DeviceIdentity {
    fn from_key(id: &str, key: SigningKey) -> Self {
        let public = crypto::point_bytes(&PublicKey::from(key.verifying_key()));
        DeviceIdentity {
            id: id.to_ascii_lowercase(),
            fingerprint: crypto::hex(&crypto::sha256(&[&public])),
            public,
            key,
        }
    }

    /// A throwaway identity (tests).
    pub fn generate(id: &str) -> Self {
        Self::from_key(id, SigningKey::random(&mut rand_core::OsRng))
    }

    pub fn sign(&self, digest: &[u8; 32]) -> [u8; 64] {
        crypto::sign(&self.key, digest)
    }

    /// `identity.key`: D || X || Y (96 bytes), protected with the user's DPAPI on Windows.
    pub fn load(folder: &Path, id: &str) -> Self {
        let path = folder.join("identity.key");
        if let Ok(stored) = std::fs::read(&path) {
            if let Some(raw) = unprotect(&stored) {
                if raw.len() == 96 {
                    if let Ok(key) = SigningKey::from_slice(&raw[..32]) {
                        return Self::from_key(id, key);
                    }
                }
            }
        }
        let identity = Self::generate(id);
        let public = crypto::point_bytes(&PublicKey::from(identity.key.verifying_key()));
        let mut raw = Vec::with_capacity(96);
        raw.extend_from_slice(identity.key.to_bytes().as_slice());
        raw.extend_from_slice(&public);
        let _ = std::fs::create_dir_all(folder);
        if let Some(stored) = protect(&raw) {
            let _ = std::fs::write(&path, stored);
        }
        identity
    }
}

const ENTROPY: &[u8] = b"Lazo identity v1";

#[cfg(windows)]
fn protect(raw: &[u8]) -> Option<Vec<u8>> {
    dpapi(raw, true)
}

#[cfg(windows)]
fn unprotect(stored: &[u8]) -> Option<Vec<u8>> {
    dpapi(stored, false)
}

#[cfg(not(windows))]
fn protect(raw: &[u8]) -> Option<Vec<u8>> {
    Some(raw.to_vec())
}

#[cfg(not(windows))]
fn unprotect(stored: &[u8]) -> Option<Vec<u8>> {
    Some(stored.to_vec())
}

#[cfg(windows)]
fn dpapi(data: &[u8], protect: bool) -> Option<Vec<u8>> {
    use windows::core::PCWSTR;
    use windows::Win32::Foundation::{LocalFree, HLOCAL};
    use windows::Win32::Security::Cryptography::{CryptProtectData, CryptUnprotectData, CRYPT_INTEGER_BLOB};

    let input = CRYPT_INTEGER_BLOB { cbData: data.len() as u32, pbData: data.as_ptr() as *mut u8 };
    let entropy = CRYPT_INTEGER_BLOB { cbData: ENTROPY.len() as u32, pbData: ENTROPY.as_ptr() as *mut u8 };
    let mut output = CRYPT_INTEGER_BLOB { cbData: 0, pbData: std::ptr::null_mut() };
    // SAFETY: the blobs point into slices that outlive the call; the output buffer belongs to
    // the system and is copied, then freed with LocalFree as the API requires.
    unsafe {
        let done = if protect {
            CryptProtectData(&input, PCWSTR::null(), Some(&entropy), None, None, 0, &mut output)
        } else {
            CryptUnprotectData(&input, None, Some(&entropy), None, None, 0, &mut output)
        };
        if done.is_err() || output.pbData.is_null() {
            return None;
        }
        let bytes = std::slice::from_raw_parts(output.pbData, output.cbData as usize).to_vec();
        let _ = LocalFree(Some(HLOCAL(output.pbData as *mut _)));
        Some(bytes)
    }
}

// ── Known computers ───────────────────────────────────────────────────────────

#[derive(Clone, Debug, PartialEq)]
pub struct PairedDevice {
    pub id: String,
    pub fingerprint: String,
    pub name: String,
    pub since: String,
    pub verified: bool,
}

/// `paired.txt`: one `id|fingerprint|name|date|v` row per computer, kept at first contact (like SSH).
pub struct PairingStore {
    path: PathBuf,
    devices: Mutex<HashMap<String, PairedDevice>>,
}

fn clean_name(value: &str) -> String {
    value.chars().filter(|c| !c.is_control() && *c != '|').take(60).collect::<String>().trim().to_string()
}

impl PairingStore {
    pub fn new(folder: &Path) -> Self {
        let path = folder.join("paired.txt");
        let mut devices = HashMap::new();
        if let Ok(text) = std::fs::read_to_string(&path) {
            for row in text.lines() {
                let parts: Vec<&str> = row.split('|').collect();
                if parts.len() < 4 || parts[1].len() != 64 {
                    continue;
                }
                devices.insert(
                    parts[0].to_ascii_lowercase(),
                    PairedDevice {
                        id: parts[0].to_ascii_lowercase(),
                        fingerprint: parts[1].to_string(),
                        name: parts[2].to_string(),
                        since: parts[3].to_string(),
                        verified: parts.get(4) == Some(&"v"),
                    },
                );
            }
        }
        PairingStore { path, devices: Mutex::new(devices) }
    }

    pub fn get(&self, id: &str) -> Option<PairedDevice> {
        self.devices.lock().unwrap().get(&id.to_ascii_lowercase()).cloned()
    }

    pub fn set(&self, id: &str, fingerprint: &str, name: &str) {
        let id = id.to_ascii_lowercase();
        let device = PairedDevice {
            id: id.clone(),
            fingerprint: fingerprint.to_string(),
            name: clean_name(name),
            since: today(),
            verified: false,
        };
        let mut devices = self.devices.lock().unwrap();
        devices.insert(id, device);
        self.save(&devices);
    }

    pub fn mark_verified(&self, id: &str, name: &str) {
        let mut devices = self.devices.lock().unwrap();
        if let Some(device) = devices.get_mut(&id.to_ascii_lowercase()) {
            device.verified = true;
            if !name.trim().is_empty() {
                device.name = clean_name(name);
            }
            self.save(&devices);
        }
    }

    pub fn remove(&self, id: &str) {
        let mut devices = self.devices.lock().unwrap();
        if devices.remove(&id.to_ascii_lowercase()).is_some() {
            self.save(&devices);
        }
    }

    fn save(&self, devices: &HashMap<String, PairedDevice>) {
        let mut rows: Vec<&PairedDevice> = devices.values().collect();
        rows.sort_by(|a, b| a.id.cmp(&b.id));
        let text: String = rows
            .iter()
            .map(|d| format!("{}|{}|{}|{}|{}\r\n", d.id, d.fingerprint, clean_name(&d.name), d.since, if d.verified { "v" } else { "" }))
            .collect();
        if let Some(parent) = self.path.parent() {
            let _ = std::fs::create_dir_all(parent);
        }
        let _ = std::fs::write(&self.path, text);
    }
}

/// yyyy-MM-dd, from the system clock (UTC is close enough for "since").
fn today() -> String {
    let days = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs() / 86_400)
        .unwrap_or(0) as i64;
    // Civil-from-days (Howard Hinnant).
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1_460 + doe / 36_524 - doe / 146_096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    format!("{:04}-{:02}-{:02}", if m <= 2 { y + 1 } else { y }, m, d)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp(name: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("lazo-net-{name}-{}", new_guid()));
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    #[test]
    fn guids_are_recognised_and_made() {
        assert!(is_guid("0b1c2d3e-4f50-4a6b-8c7d-9e0f1a2b3c4d"));
        assert!(!is_guid("0b1c2d3e4f504a6b8c7d9e0f1a2b3c4d"));
        assert!(!is_guid("zb1c2d3e-4f50-4a6b-8c7d-9e0f1a2b3c4d"));
        let g = new_guid();
        assert!(is_guid(&g));
        assert_ne!(g, new_guid());
    }

    #[test]
    fn the_identifier_is_kept_between_runs() {
        let dir = temp("id");
        let first = load_id(&dir);
        assert_eq!(first, load_id(&dir));
        std::fs::write(dir.join("device.id"), "{ABCDEF01-2345-4678-89AB-CDEF01234567}").unwrap();
        assert_eq!(load_id(&dir), "abcdef01-2345-4678-89ab-cdef01234567", "classic Lazo may write braces and capitals");
    }

    #[test]
    fn the_key_survives_a_restart_and_is_not_stored_in_clear() {
        let dir = temp("key");
        let id = new_guid();
        let a = DeviceIdentity::load(&dir, &id);
        let b = DeviceIdentity::load(&dir, &id);
        assert_eq!(a.fingerprint, b.fingerprint);
        assert_eq!(a.fingerprint.len(), 64);
        let stored = std::fs::read(dir.join("identity.key")).unwrap();
        #[cfg(windows)]
        assert!(!stored.windows(32).any(|w| w == a.key.to_bytes().as_slice()), "DPAPI hides the private key");
        assert!(!stored.is_empty());
    }

    #[test]
    fn known_computers_round_trip_in_the_classic_format() {
        let dir = temp("paired");
        let store = PairingStore::new(&dir);
        let fp = "a".repeat(64);
        store.set("AAAAAAAA-0000-4000-8000-000000000001", &fp, "Luis | Gómez");
        let got = store.get("aaaaaaaa-0000-4000-8000-000000000001").unwrap();
        assert_eq!(got.name, "Luis  Gómez");
        assert!(!got.verified);
        store.mark_verified(&got.id, "Luis Gómez");
        let again = PairingStore::new(&dir);
        let reloaded = again.get(&got.id).unwrap();
        assert!(reloaded.verified);
        assert_eq!(reloaded.fingerprint, fp);
        let text = std::fs::read_to_string(dir.join("paired.txt")).unwrap();
        assert!(text.trim_end().ends_with("|v"), "{text}");
        again.remove(&got.id);
        assert!(PairingStore::new(&dir).get(&got.id).is_none());
    }

    #[test]
    fn the_date_is_iso() {
        let d = today();
        assert_eq!(d.len(), 10);
        assert!(d.starts_with("20"));
    }
}
