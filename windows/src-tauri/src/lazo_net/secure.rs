// The LAZO3 handshake and its encrypted records (docs/protocolo-seguro.md), compatible byte for
// byte with lazo/src/SecureChannel.cs and the Mac's LazoCore.

use std::io;

use aes::Aes256;
use cbc::cipher::block_padding::Pkcs7;
use cbc::cipher::{BlockDecryptMut, BlockEncryptMut, KeyIvInit};
use p256::ecdh::EphemeralSecret;
use p256::PublicKey;
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt};
use tokio::net::tcp::{OwnedReadHalf, OwnedWriteHalf};
use tokio::net::TcpStream;

use super::crypto;
use super::identity::DeviceIdentity;

pub const VERSION: u8 = 1;
pub const MAX_PLAIN: usize = 65536;

fn bad(message: &str) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, message.to_string())
}

/// A finished handshake: who the other computer is, and the encrypted channel to it.
pub struct Session {
    pub reader: SecureReader<OwnedReadHalf>,
    pub writer: SecureWriter<OwnedWriteHalf>,
    pub peer_id: String,
    pub peer_key: [u8; 64],
    pub peer_fingerprint: String,
    pub transcript: [u8; 32],
}

impl Session {
    pub fn sas(&self) -> String {
        crypto::sas(&self.transcript)
    }
}

/// The side that connects: sends the "LAZO3" header.
pub async fn connect(mut tcp: TcpStream, me: &DeviceIdentity) -> io::Result<Session> {
    let ephemeral = EphemeralSecret::random(&mut rand_core::OsRng);
    let ec = crypto::point_bytes(&PublicKey::from(&ephemeral));
    let nc = crypto::random::<32>();
    let mut hello = Vec::with_capacity(5 + 1 + 64 + 32);
    hello.extend_from_slice(b"LAZO3");
    hello.push(VERSION);
    hello.extend_from_slice(&ec);
    hello.extend_from_slice(&nc);
    tcp.write_all(&hello).await?;
    tcp.flush().await?;

    if tcp.read_u8().await? != VERSION {
        return Err(bad("Incompatible protocol version."));
    }
    let es = read_array::<64>(&mut tcp).await?;
    let ns = read_array::<32>(&mut tcp).await?;
    let ids = read_array::<36>(&mut tcp).await?;
    let ks = read_array::<64>(&mut tcp).await?;
    let sigs = read_array::<64>(&mut tcp).await?;
    let th1 = crypto::sha256(&[b"LAZO3/1", &ec, &nc, &es, &ns, &ids, &ks]);
    if !crypto::verify(&ks, &th1, &sigs) {
        return Err(bad("The other computer did not prove its identity."));
    }
    let idc = me.id.as_bytes();
    let th2 = crypto::sha256(&[&th1, idc, &me.public]);
    let mut reply = Vec::with_capacity(36 + 64 + 64);
    reply.extend_from_slice(idc);
    reply.extend_from_slice(&me.public);
    reply.extend_from_slice(&me.sign(&th2));
    tcp.write_all(&reply).await?;
    tcp.flush().await?;
    let keys = derive_keys(&ephemeral, &es, &th2)?;
    finish(tcp, &keys, true, &ids, ks, th2)
}

/// The side that listens, after the "LAZO3" header has been read.
pub async fn accept(mut tcp: TcpStream, me: &DeviceIdentity) -> io::Result<Session> {
    if tcp.read_u8().await? != VERSION {
        return Err(bad("Incompatible protocol version."));
    }
    let ec = read_array::<64>(&mut tcp).await?;
    let nc = read_array::<32>(&mut tcp).await?;
    let ephemeral = EphemeralSecret::random(&mut rand_core::OsRng);
    let es = crypto::point_bytes(&PublicKey::from(&ephemeral));
    let ns = crypto::random::<32>();
    let ids = me.id.as_bytes();
    let th1 = crypto::sha256(&[b"LAZO3/1", &ec, &nc, &es, &ns, ids, &me.public]);
    let mut hello = Vec::with_capacity(1 + 64 + 32 + 36 + 64 + 64);
    hello.push(VERSION);
    hello.extend_from_slice(&es);
    hello.extend_from_slice(&ns);
    hello.extend_from_slice(ids);
    hello.extend_from_slice(&me.public);
    hello.extend_from_slice(&me.sign(&th1));
    tcp.write_all(&hello).await?;
    tcp.flush().await?;

    let idc = read_array::<36>(&mut tcp).await?;
    let kc = read_array::<64>(&mut tcp).await?;
    let sigc = read_array::<64>(&mut tcp).await?;
    let th2 = crypto::sha256(&[&th1, &idc, &kc]);
    if !crypto::verify(&kc, &th2, &sigc) {
        return Err(bad("The other computer did not prove its identity."));
    }
    let keys = derive_keys(&ephemeral, &ec, &th2)?;
    finish(tcp, &keys, false, &idc, kc, th2)
}

async fn read_array<const N: usize>(tcp: &mut TcpStream) -> io::Result<[u8; N]> {
    let mut bytes = [0u8; N];
    tcp.read_exact(&mut bytes).await?;
    Ok(bytes)
}

fn derive_keys(ephemeral: &EphemeralSecret, peer_point: &[u8; 64], th2: &[u8; 32]) -> io::Result<Vec<u8>> {
    let peer = crypto::point_from(peer_point).map_err(|e| bad(&e))?;
    let shared = ephemeral.diffie_hellman(&peer);
    // .NET's DeriveKeyFromHash(SHA256): the hash of the shared X coordinate.
    let ikm = crypto::sha256(&[shared.raw_secret_bytes().as_slice()]);
    Ok(crypto::hkdf(th2, &ikm, b"lazo3 keys", 128))
}

fn finish(tcp: TcpStream, keys: &[u8], client: bool, peer_id: &[u8; 36], peer_key: [u8; 64], th2: [u8; 32]) -> io::Result<Session> {
    let id = std::str::from_utf8(peer_id).map_err(|_| bad("Invalid identifier."))?;
    if !super::identity::is_guid(id) {
        return Err(bad("Invalid identifier."));
    }
    let key = |from: usize| -> [u8; 32] { keys[from..from + 32].try_into().unwrap() };
    let (enc_cs, mac_cs, enc_sc, mac_sc) = (key(0), key(32), key(64), key(96));
    let (read_half, write_half) = tcp.into_split();
    let (reader, writer) = if client {
        (SecureReader::new(read_half, enc_sc, mac_sc), SecureWriter::new(write_half, enc_cs, mac_cs))
    } else {
        (SecureReader::new(read_half, enc_cs, mac_cs), SecureWriter::new(write_half, enc_sc, mac_sc))
    };
    Ok(Session {
        reader,
        writer,
        peer_id: id.to_ascii_lowercase(),
        peer_key,
        peer_fingerprint: crypto::hex(&crypto::sha256(&[&peer_key])),
        transcript: th2,
    })
}

fn tag(mac: &[u8; 32], seq: u64, header: &[u8; 4], iv: &[u8; 16], cipher: &[u8]) -> [u8; 32] {
    crypto::hmac(mac, &[&seq.to_le_bytes(), header, iv, cipher])
}

// ── Reading ───────────────────────────────────────────────────────────────────

/// Records: u32 length, 16-byte IV, AES-256-CBC (PKCS#7), HMAC-SHA256 over seq || length || IV || cipher.
pub struct SecureReader<R> {
    inner: R,
    key: [u8; 32],
    mac: [u8; 32],
    seq: u64,
    plain: Vec<u8>,
    offset: usize,
    ended: bool,
}

impl<R: AsyncRead + Unpin> SecureReader<R> {
    pub fn new(inner: R, key: [u8; 32], mac: [u8; 32]) -> Self {
        SecureReader { inner, key, mac, seq: 0, plain: Vec::new(), offset: 0, ended: false }
    }

    /// Next record into `plain`; false at a clean end of stream.
    async fn next_record(&mut self) -> io::Result<bool> {
        loop {
            let mut header = [0u8; 4];
            let mut got = 0;
            while got < 4 {
                let n = self.inner.read(&mut header[got..]).await?;
                if n == 0 {
                    if got == 0 {
                        return Ok(false);
                    }
                    return Err(io::ErrorKind::UnexpectedEof.into());
                }
                got += n;
            }
            let length = u32::from_le_bytes(header) as usize;
            if length < 16 || length > MAX_PLAIN + 16 || length % 16 != 0 {
                return Err(bad("Invalid encrypted record."));
            }
            let mut iv = [0u8; 16];
            self.inner.read_exact(&mut iv).await?;
            let mut cipher = vec![0u8; length];
            self.inner.read_exact(&mut cipher).await?;
            let mut received = [0u8; 32];
            self.inner.read_exact(&mut received).await?;
            if !crypto::fixed_eq(&received, &tag(&self.mac, self.seq, &header, &iv, &cipher)) {
                return Err(bad("Record altered or out of order."));
            }
            self.seq += 1;
            let plain = cbc::Decryptor::<Aes256>::new(&self.key.into(), &iv.into())
                .decrypt_padded_vec_mut::<Pkcs7>(&cipher)
                .map_err(|_| bad("Invalid encrypted record."))?;
            self.plain = plain;
            self.offset = 0;
            if !self.plain.is_empty() {
                return Ok(true);
            }
        }
    }

    /// Up to `buf.len()` bytes; 0 at the end of the stream.
    pub async fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
        if buf.is_empty() {
            return Ok(0);
        }
        if self.offset >= self.plain.len() && (self.ended || !self.next_record().await?) {
            self.ended = true;
            return Ok(0);
        }
        let take = buf.len().min(self.plain.len() - self.offset);
        buf[..take].copy_from_slice(&self.plain[self.offset..self.offset + take]);
        self.offset += take;
        Ok(take)
    }

    pub async fn read_exact(&mut self, buf: &mut [u8]) -> io::Result<()> {
        let mut done = 0;
        while done < buf.len() {
            let n = self.read(&mut buf[done..]).await?;
            if n == 0 {
                return Err(io::ErrorKind::UnexpectedEof.into());
            }
            done += n;
        }
        Ok(())
    }

    pub async fn read_u8(&mut self) -> io::Result<u8> {
        let mut b = [0u8; 1];
        self.read_exact(&mut b).await?;
        Ok(b[0])
    }

    pub async fn read_u16(&mut self) -> io::Result<u16> {
        let mut b = [0u8; 2];
        self.read_exact(&mut b).await?;
        Ok(u16::from_le_bytes(b))
    }

    pub async fn read_i32(&mut self) -> io::Result<i32> {
        let mut b = [0u8; 4];
        self.read_exact(&mut b).await?;
        Ok(i32::from_le_bytes(b))
    }

    pub async fn read_i64(&mut self) -> io::Result<i64> {
        let mut b = [0u8; 8];
        self.read_exact(&mut b).await?;
        Ok(i64::from_le_bytes(b))
    }

    pub async fn read_bytes(&mut self, count: usize) -> io::Result<Vec<u8>> {
        let mut bytes = vec![0u8; count];
        self.read_exact(&mut bytes).await?;
        Ok(bytes)
    }

    /// `texto`: u16 length in bytes, then UTF-8.
    pub async fn read_text(&mut self, max_bytes: usize) -> io::Result<String> {
        let length = self.read_u16().await? as usize;
        if length > max_bytes {
            return Err(bad("Field too long."));
        }
        String::from_utf8(self.read_bytes(length).await?).map_err(|_| bad("Invalid text."))
    }
}

// ── Writing ───────────────────────────────────────────────────────────────────

/// What is written piles up until `flush` or a full 64 KiB record, as in the C# stream.
pub struct SecureWriter<W> {
    inner: W,
    key: [u8; 32],
    mac: [u8; 32],
    seq: u64,
    pending: Vec<u8>,
}

impl<W: AsyncWrite + Unpin> SecureWriter<W> {
    pub fn new(inner: W, key: [u8; 32], mac: [u8; 32]) -> Self {
        SecureWriter { inner, key, mac, seq: 0, pending: Vec::new() }
    }

    pub async fn write_all(&mut self, mut data: &[u8]) -> io::Result<()> {
        while !data.is_empty() {
            let take = data.len().min(MAX_PLAIN - self.pending.len());
            self.pending.extend_from_slice(&data[..take]);
            data = &data[take..];
            if self.pending.len() >= MAX_PLAIN {
                self.emit().await?;
            }
        }
        Ok(())
    }

    pub async fn flush(&mut self) -> io::Result<()> {
        self.emit().await?;
        self.inner.flush().await
    }

    async fn emit(&mut self) -> io::Result<()> {
        if self.pending.is_empty() {
            return Ok(());
        }
        let iv = crypto::random::<16>();
        let cipher = cbc::Encryptor::<Aes256>::new(&self.key.into(), &iv.into()).encrypt_padded_vec_mut::<Pkcs7>(&self.pending);
        self.pending.clear();
        let header = (cipher.len() as u32).to_le_bytes();
        let tag = tag(&self.mac, self.seq, &header, &iv, &cipher);
        self.seq += 1;
        let mut record = Vec::with_capacity(4 + 16 + cipher.len() + 32);
        record.extend_from_slice(&header);
        record.extend_from_slice(&iv);
        record.extend_from_slice(&cipher);
        record.extend_from_slice(&tag);
        self.inner.write_all(&record).await
    }

    pub async fn write_u8(&mut self, value: u8) -> io::Result<()> {
        self.write_all(&[value]).await
    }

    pub async fn write_i32(&mut self, value: i32) -> io::Result<()> {
        self.write_all(&value.to_le_bytes()).await
    }

    pub async fn write_i64(&mut self, value: i64) -> io::Result<()> {
        self.write_all(&value.to_le_bytes()).await
    }

    pub async fn write_text(&mut self, value: &str, max_bytes: usize) -> io::Result<()> {
        if value.len() > max_bytes {
            return Err(bad("Text too long."));
        }
        self.write_all(&(value.len() as u16).to_le_bytes()).await?;
        self.write_all(value.as_bytes()).await
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use tokio::net::TcpListener;

    async fn pair() -> (TcpStream, TcpStream) {
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let addr = listener.local_addr().unwrap();
        let (client, server) = tokio::join!(TcpStream::connect(addr), listener.accept());
        (client.unwrap(), server.unwrap().0)
    }

    #[tokio::test]
    async fn two_identities_shake_hands_and_talk() {
        let a = DeviceIdentity::generate("11111111-1111-4111-8111-111111111111");
        let b = DeviceIdentity::generate("22222222-2222-4222-8222-222222222222");
        let (client, mut server) = pair().await;
        let b2 = b.clone();
        let accept = tokio::spawn(async move {
            let mut magic = [0u8; 5];
            server.read_exact(&mut magic).await.unwrap();
            assert_eq!(&magic, b"LAZO3");
            accept(server, &b2).await.unwrap()
        });
        let mut ours = connect(client, &a).await.unwrap();
        let mut theirs = accept.await.unwrap();
        assert_eq!(ours.peer_id, b.id);
        assert_eq!(theirs.peer_id, a.id);
        assert_eq!(ours.peer_fingerprint, b.fingerprint);
        assert_eq!(theirs.peer_fingerprint, a.fingerprint);
        assert_eq!(ours.sas(), theirs.sas(), "both people see the same code");

        // The writer runs on its own: 200 KB do not fit in the socket buffers before somebody reads.
        let big: Vec<u8> = (0..200_000u32).map(|i| (i * 7) as u8).collect();
        let to_send = big.clone();
        let writer = tokio::spawn(async move {
            ours.writer.write_u8(b'C').await.unwrap();
            ours.writer.write_text("hola, ñandú", 80).await.unwrap();
            ours.writer.write_all(&to_send).await.unwrap();
            ours.writer.flush().await.unwrap();
            ours
        });
        assert_eq!(theirs.reader.read_u8().await.unwrap(), b'C');
        assert_eq!(theirs.reader.read_text(80).await.unwrap(), "hola, ñandú");
        assert_eq!(theirs.reader.read_bytes(big.len()).await.unwrap(), big);
        let mut ours = writer.await.unwrap();

        theirs.writer.write_u8(1).await.unwrap();
        theirs.writer.flush().await.unwrap();
        assert_eq!(ours.reader.read_u8().await.unwrap(), 1);
    }

    #[tokio::test]
    async fn a_server_that_cannot_prove_its_identity_is_refused() {
        let a = DeviceIdentity::generate("11111111-1111-4111-8111-111111111111");
        let (client, mut server) = pair().await;
        let liar = tokio::spawn(async move {
            // Reads the hello, answers with a signature made by some other key.
            let mut hello = [0u8; 5 + 1 + 64 + 32];
            server.read_exact(&mut hello).await.unwrap();
            let me = DeviceIdentity::generate("33333333-3333-4333-8333-333333333333");
            let other = DeviceIdentity::generate("44444444-4444-4444-8444-444444444444");
            let ephemeral = EphemeralSecret::random(&mut rand_core::OsRng);
            let es = crypto::point_bytes(&PublicKey::from(&ephemeral));
            let ns = crypto::random::<32>();
            let th1 = crypto::sha256(&[b"LAZO3/1", &hello[6..70], &hello[70..102], &es, &ns, me.id.as_bytes(), &me.public]);
            let mut reply = vec![VERSION];
            reply.extend_from_slice(&es);
            reply.extend_from_slice(&ns);
            reply.extend_from_slice(me.id.as_bytes());
            reply.extend_from_slice(&me.public);
            reply.extend_from_slice(&other.sign(&th1));
            server.write_all(&reply).await.unwrap();
            server
        });
        let result = connect(client, &a).await;
        assert!(result.is_err());
        let _ = liar.await;
    }

    /// One record exactly as the writer would emit it, with its tag optionally spoiled.
    fn record(key: &[u8; 32], mac: &[u8; 32], seq: u64, plain: &[u8], spoil: bool) -> Vec<u8> {
        let iv = [1u8; 16];
        let cipher = cbc::Encryptor::<Aes256>::new(&(*key).into(), &iv.into()).encrypt_padded_vec_mut::<Pkcs7>(plain);
        let header = (cipher.len() as u32).to_le_bytes();
        let mut t = tag(mac, seq, &header, &iv, &cipher);
        if spoil {
            t[0] ^= 1;
        }
        let mut out = Vec::new();
        out.extend_from_slice(&header);
        out.extend_from_slice(&iv);
        out.extend_from_slice(&cipher);
        out.extend_from_slice(&t);
        out
    }

    #[tokio::test]
    async fn records_are_checked_before_they_are_read() {
        let (key, mac) = ([7u8; 32], [9u8; 32]);
        // A good record, then one with a spoiled tag, then a replay of the first (wrong sequence).
        let (mut raw, other) = pair().await;
        raw.write_all(&record(&key, &mac, 0, b"hello", false)).await.unwrap();
        raw.write_all(&record(&key, &mac, 1, b"world", true)).await.unwrap();
        let (r, _w) = other.into_split();
        let mut reader = SecureReader::new(r, key, mac);
        let mut five = [0u8; 5];
        reader.read_exact(&mut five).await.unwrap();
        assert_eq!(&five, b"hello");
        assert!(reader.read_exact(&mut five).await.is_err(), "a spoiled tag closes the channel");

        let (mut raw, other) = pair().await;
        raw.write_all(&record(&key, &mac, 0, b"hello", false)).await.unwrap();
        raw.write_all(&record(&key, &mac, 0, b"hello", false)).await.unwrap();
        let (r, _w) = other.into_split();
        let mut reader = SecureReader::new(r, key, mac);
        reader.read_exact(&mut five).await.unwrap();
        assert!(reader.read_exact(&mut five).await.is_err(), "a replayed record is out of order");
    }

    #[tokio::test]
    async fn the_writer_and_reader_agree_on_the_record_format() {
        let (key, mac) = ([3u8; 32], [4u8; 32]);
        let (a, b) = pair().await;
        let (_ar, aw) = a.into_split();
        let (br, _bw) = b.into_split();
        let mut writer = SecureWriter::new(aw, key, mac);
        let mut reader = SecureReader::new(br, key, mac);
        writer.write_text("uno", 10).await.unwrap();
        writer.flush().await.unwrap();
        writer.write_i64(-5).await.unwrap();
        writer.write_i32(70000).await.unwrap();
        writer.flush().await.unwrap();
        assert_eq!(reader.read_text(10).await.unwrap(), "uno");
        assert_eq!(reader.read_i64().await.unwrap(), -5);
        assert_eq!(reader.read_i32().await.unwrap(), 70000);
        assert!(writer.write_text("demasiado largo", 3).await.is_err());
    }
}
