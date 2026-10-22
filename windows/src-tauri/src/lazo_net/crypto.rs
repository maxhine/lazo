// LAZO3 primitives (docs/protocolo-seguro.md), byte for byte what lazo/src/SecureChannel.cs does:
// SHA-256, HMAC, HKDF, ECDSA P-256 over a digest that is already computed, ECDH P-256, and the
// 6-digit code two people compare.

use hmac::{Hmac, Mac};
use hkdf::Hkdf;
use p256::ecdsa::signature::hazmat::{PrehashSigner, PrehashVerifier};
use p256::ecdsa::{Signature, SigningKey, VerifyingKey};
use p256::elliptic_curve::sec1::{FromEncodedPoint, ToEncodedPoint};
use p256::{EncodedPoint, PublicKey};
use sha2::{Digest, Sha256};

pub fn random<const N: usize>() -> [u8; N] {
    let mut bytes = [0u8; N];
    getrandom_fill(&mut bytes);
    bytes
}

fn getrandom_fill(bytes: &mut [u8]) {
    use rand_core::RngCore;
    rand_core::OsRng.fill_bytes(bytes);
}

pub fn sha256(parts: &[&[u8]]) -> [u8; 32] {
    let mut sha = Sha256::new();
    for part in parts {
        sha.update(part);
    }
    sha.finalize().into()
}

pub fn hmac(key: &[u8], parts: &[&[u8]]) -> [u8; 32] {
    let mut mac = <Hmac<Sha256> as Mac>::new_from_slice(key).expect("HMAC takes any key length");
    for part in parts {
        mac.update(part);
    }
    mac.finalize().into_bytes().into()
}

/// HKDF-SHA256 (RFC 5869) with the salt first, as the C# engine takes it.
pub fn hkdf(salt: &[u8], ikm: &[u8], info: &[u8], length: usize) -> Vec<u8> {
    let mut out = vec![0u8; length];
    Hkdf::<Sha256>::new(Some(salt), ikm).expand(info, &mut out).expect("128 bytes is within HKDF's limit");
    out
}

pub fn fixed_eq(a: &[u8], b: &[u8]) -> bool {
    if a.len() != b.len() {
        return false;
    }
    a.iter().zip(b).fold(0u8, |d, (x, y)| d | (x ^ y)) == 0
}

pub fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

/// A public key as X || Y (64 bytes, big endian, no 0x04 prefix).
pub fn point_bytes(key: &PublicKey) -> [u8; 64] {
    let encoded = key.to_encoded_point(false);
    let mut out = [0u8; 64];
    out.copy_from_slice(&encoded.as_bytes()[1..65]);
    out
}

pub fn point_from(xy: &[u8]) -> Result<PublicKey, String> {
    if xy.len() != 64 {
        return Err("Invalid public key.".into());
    }
    let mut sec1 = [0u8; 65];
    sec1[0] = 0x04;
    sec1[1..].copy_from_slice(xy);
    let point = EncodedPoint::from_bytes(sec1).map_err(|_| "Invalid public key.".to_string())?;
    Option::from(PublicKey::from_encoded_point(&point)).ok_or_else(|| "Invalid public key.".to_string())
}

/// ECDSA over a digest, signature as r || s (64 bytes).
pub fn sign(key: &SigningKey, digest: &[u8; 32]) -> [u8; 64] {
    let signature: Signature = key.sign_prehash(digest).expect("a 32-byte digest can always be signed");
    signature.to_bytes().into()
}

pub fn verify(public: &[u8], digest: &[u8; 32], signature: &[u8]) -> bool {
    let Ok(key) = point_from(public) else { return false };
    let Ok(signature) = Signature::from_slice(signature) else { return false };
    VerifyingKey::from(&key).verify_prehash(digest, &signature).is_ok()
}

/// The 6 digits both people see: (big-endian u32 of SHA256("LAZO3 SAS" || transcript)[0..4]) mod 1 000 000.
pub fn sas(transcript: &[u8]) -> String {
    let digest = sha256(&[b"LAZO3 SAS", transcript]);
    let value = u32::from_be_bytes([digest[0], digest[1], digest[2], digest[3]]);
    format!("{:06}", value % 1_000_000)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn hkdf_matches_rfc5869_case_1() {
        let ikm = [0x0bu8; 22];
        let salt: Vec<u8> = (0u8..=0x0c).collect();
        let info: Vec<u8> = (0xf0u8..=0xf9).collect();
        let okm = hkdf(&salt, &ikm, &info, 42);
        assert_eq!(
            hex(&okm),
            "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865"
        );
    }

    #[test]
    fn a_signature_made_here_verifies_and_a_changed_digest_does_not() {
        let key = SigningKey::random(&mut rand_core::OsRng);
        let public = point_bytes(&PublicKey::from(key.verifying_key()));
        let digest = sha256(&[b"hello"]);
        let signature = sign(&key, &digest);
        assert!(verify(&public, &digest, &signature));
        assert!(!verify(&public, &sha256(&[b"hellp"]), &signature));
        assert!(!verify(&public, &digest, &signature[..63]));
        assert!(!verify(&[0u8; 64], &digest, &signature), "a point that is not on the curve");
    }

    #[test]
    fn the_code_is_six_digits_and_stable() {
        let a = sas(b"transcript");
        assert_eq!(a.len(), 6);
        assert!(a.chars().all(|c| c.is_ascii_digit()));
        assert_eq!(a, sas(b"transcript"));
        assert_ne!(a, sas(b"transcripT"));
    }

    #[test]
    fn constant_time_compare() {
        assert!(fixed_eq(b"abc", b"abc"));
        assert!(!fixed_eq(b"abc", b"abd"));
        assert!(!fixed_eq(b"abc", b"ab"));
    }
}
