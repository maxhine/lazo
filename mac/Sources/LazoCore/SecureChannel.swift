import CommonCrypto
import CryptoKit
import Foundation

/// Primitivas de LAZO3 (docs/protocolo-seguro.md), idénticas byte a byte a src/SecureChannel.cs.
public enum LazoCrypto {
    public static func random(_ count: Int) -> Data {
        var data = Data(count: count)
        _ = data.withUnsafeMutableBytes { SecRandomCopyBytes(kSecRandomDefault, count, $0.baseAddress!) }
        return data
    }

    public static func sha256(_ parts: Data...) -> Data {
        var hasher = SHA256()
        for part in parts { hasher.update(data: part) }
        return Data(hasher.finalize())
    }

    static func digest(_ parts: Data...) -> SHA256Digest {
        var hasher = SHA256()
        for part in parts { hasher.update(data: part) }
        return hasher.finalize()
    }

    public static func hmac(_ key: Data, _ parts: Data...) -> Data {
        var mac = HMAC<SHA256>(key: SymmetricKey(data: key))
        for part in parts { mac.update(data: part) }
        return Data(mac.finalize())
    }

    /// HKDF-SHA256 (RFC 5869).
    public static func hkdf(salt: Data, ikm: Data, info: Data, length: Int) -> Data {
        let prk = hmac(salt, ikm)
        var output = Data()
        var previous = Data()
        var counter: UInt8 = 1
        while output.count < length {
            previous = hmac(prk, previous, info, Data([counter]))
            output.append(previous)
            counter += 1
        }
        return output.prefix(length)
    }

    public static func fixedEquals(_ a: Data, _ b: Data) -> Bool {
        guard a.count == b.count else { return false }
        var diff: UInt8 = 0
        for (x, y) in zip(a, b) { diff |= x ^ y }
        return diff == 0
    }

    public static func hex(_ data: Data) -> String {
        data.map { String(format: "%02x", $0) }.joined()
    }

    public static func fromHex(_ text: String) -> Data? {
        guard text.count % 2 == 0 else { return nil }
        var data = Data(capacity: text.count / 2)
        var index = text.startIndex
        while index < text.endIndex {
            let next = text.index(index, offsetBy: 2)
            guard let byte = UInt8(text[index..<next], radix: 16) else { return nil }
            data.append(byte)
            index = next
        }
        return data
    }

    public static func ascii(_ text: String) -> Data { Data(text.utf8) }

    /// Código de 6 cifras que las dos personas comparan al emparejar.
    public static func sas(_ transcript: Data) -> String {
        let digest = sha256(ascii("LAZO3 SAS"), transcript)
        let value = UInt32(digest[0]) << 24 | UInt32(digest[1]) << 16 | UInt32(digest[2]) << 8 | UInt32(digest[3])
        return String(format: "%06u", value % 1_000_000)
    }

    public static func shortFingerprint(_ fingerprint: String) -> String {
        guard fingerprint.count >= 16 else { return fingerprint }
        let head = fingerprint.prefix(16).uppercased()
        return stride(from: 0, to: 16, by: 4).map { i -> String in
            let start = head.index(head.startIndex, offsetBy: i)
            return String(head[start..<head.index(start, offsetBy: 4)])
        }.joined(separator: " ")
    }

    public static func verify(publicKey: Data, digest: SHA256Digest, signature: Data) -> Bool {
        guard publicKey.count == 64, signature.count == 64,
              let key = try? P256.Signing.PublicKey(rawRepresentation: publicKey),
              let sig = try? P256.Signing.ECDSASignature(rawRepresentation: signature) else { return false }
        return key.isValidSignature(sig, for: digest)
    }

    /// Firma de versión: DER en base64 (openssl) sobre el SHA-256 del archivo.
    public static func verifyRelease(file: URL, signatureBase64: String, publicKeyHex: String) -> Bool {
        guard let der = Data(base64Encoded: signatureBase64.trimmingCharacters(in: .whitespacesAndNewlines)),
              let sig = try? P256.Signing.ECDSASignature(derRepresentation: der),
              let keyData = fromHex(publicKeyHex), let key = try? P256.Signing.PublicKey(rawRepresentation: keyData),
              let handle = try? FileHandle(forReadingFrom: file) else { return false }
        defer { try? handle.close() }
        var hasher = SHA256()
        while let chunk = try? handle.read(upToCount: 1 << 20), !chunk.isEmpty { hasher.update(data: chunk) }
        return key.isValidSignature(sig, for: hasher.finalize())
    }
}

/// Identidad de este equipo: identificador y clave ECDSA P-256 que nunca sale del equipo.
/// La clave se guarda en ~/Library/Application Support/Lazo/identity.key con permisos 0600.
public final class DeviceIdentity {
    public let id: String
    public let publicKey: Data
    public let fingerprint: String
    private let key: P256.Signing.PrivateKey

    init(id: String, key: P256.Signing.PrivateKey) {
        self.id = id.lowercased()
        self.key = key
        publicKey = key.publicKey.rawRepresentation
        fingerprint = LazoCrypto.hex(LazoCrypto.sha256(publicKey))
    }

    func sign(_ digest: SHA256Digest) -> Data {
        (try? key.signature(for: digest).rawRepresentation) ?? Data()
    }

    public static func load(folder: URL, id: String) -> DeviceIdentity {
        let path = folder.appendingPathComponent("identity.key")
        if let raw = try? Data(contentsOf: path), raw.count == 32, let key = try? P256.Signing.PrivateKey(rawRepresentation: raw) {
            return DeviceIdentity(id: id, key: key)
        }
        let key = P256.Signing.PrivateKey()
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        FileManager.default.createFile(atPath: path.path, contents: key.rawRepresentation, attributes: [.posixPermissions: 0o600])
        return DeviceIdentity(id: id, key: key)
    }
}

public struct PairedDevice: Codable, Equatable {
    public let id: String
    public let fingerprint: String
    public let name: String
    public let since: Date
}

/// Equipos emparejados: identificador → huella. Solo se guardan tras confirmar el código.
public final class PairingStore {
    private let path: URL
    private let lock = NSLock()
    private var devices: [String: PairedDevice] = [:]
    public var changed: (() -> Void)?

    public init(folder: URL) {
        path = folder.appendingPathComponent("paired.json")
        if let data = try? Data(contentsOf: path), let list = try? JSONDecoder().decode([PairedDevice].self, from: data) {
            for device in list where device.fingerprint.count == 64 { devices[device.id.lowercased()] = device }
        }
    }

    public func get(_ id: String) -> PairedDevice? {
        lock.lock(); defer { lock.unlock() }
        return devices[id.lowercased()]
    }

    public func all() -> [PairedDevice] {
        lock.lock(); defer { lock.unlock() }
        return devices.values.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }

    public func set(id: String, fingerprint: String, name: String) {
        lock.lock()
        devices[id.lowercased()] = PairedDevice(id: id.lowercased(), fingerprint: fingerprint, name: Labels.clean(name), since: Date())
        save()
        lock.unlock()
        changed?()
    }

    public func remove(_ id: String) {
        lock.lock()
        let removed = devices.removeValue(forKey: id.lowercased()) != nil
        if removed { save() }
        lock.unlock()
        if removed { changed?() }
    }

    private func save() {
        let list = Array(devices.values)
        if let data = try? JSONEncoder().encode(list) {
            try? FileManager.default.createDirectory(at: path.deletingLastPathComponent(), withIntermediateDirectories: true)
            try? data.write(to: path, options: .atomic)
            try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: path.path)
        }
    }
}

/// Saludo LAZO3 ya completado: quién es el otro equipo y el canal cifrado.
public final class SecureSession {
    public static let version: UInt8 = 1
    public static let handshakeTimeout: TimeInterval = 10
    public let stream: SecureStream
    public let peerId: String
    public let peerKey: Data
    public let peerFingerprint: String
    public let transcript: Data
    public var sas: String { LazoCrypto.sas(transcript) }

    init(stream: SecureStream, peerId: String, peerKey: Data, transcript: Data) {
        self.stream = stream
        self.peerId = peerId
        self.peerKey = peerKey
        self.peerFingerprint = LazoCrypto.hex(LazoCrypto.sha256(peerKey))
        self.transcript = transcript
    }

    /// Lado que conecta: envía la cabecera "LAZO3".
    public static func connect(_ network: ByteStream, me: DeviceIdentity) throws -> SecureSession {
        let ephemeral = P256.KeyAgreement.PrivateKey()
        let ec = ephemeral.publicKey.rawRepresentation
        let nc = LazoCrypto.random(32)
        try network.write(LazoCrypto.ascii("LAZO3") + Data([version]) + ec + nc)
        guard try network.readByte() == version else { throw LazoError.invalid("Versión de protocolo no compatible.") }
        let es = try network.readExactly(64)
        let ns = try network.readExactly(32)
        let ids = try network.readExactly(36)
        let ks = try network.readExactly(64)
        let sigs = try network.readExactly(64)
        let th1 = LazoCrypto.digest(LazoCrypto.ascii("LAZO3/1"), ec, nc, es, ns, ids, ks)
        guard LazoCrypto.verify(publicKey: ks, digest: th1, signature: sigs) else { throw LazoError.invalid("El otro equipo no demostró su identidad.") }
        let idc = LazoCrypto.ascii(me.id)
        let th2 = LazoCrypto.digest(Data(th1), idc, me.publicKey)
        try network.write(idc + me.publicKey + me.sign(th2))
        let keys = try deriveKeys(ephemeral, peer: es, th2: Data(th2))
        return try finish(network, keys: keys, client: true, peerId: ids, peerKey: ks, th2: Data(th2))
    }

    /// Lado que escucha, después de leer la cabecera "LAZO3".
    public static func accept(_ network: ByteStream, me: DeviceIdentity) throws -> SecureSession {
        guard try network.readByte() == version else { throw LazoError.invalid("Versión de protocolo no compatible.") }
        let ec = try network.readExactly(64)
        let nc = try network.readExactly(32)
        let ephemeral = P256.KeyAgreement.PrivateKey()
        let es = ephemeral.publicKey.rawRepresentation
        let ns = LazoCrypto.random(32)
        let ids = LazoCrypto.ascii(me.id)
        let th1 = LazoCrypto.digest(LazoCrypto.ascii("LAZO3/1"), ec, nc, es, ns, ids, me.publicKey)
        try network.write(Data([version]) + es + ns + ids + me.publicKey + me.sign(th1))
        let idc = try network.readExactly(36)
        let kc = try network.readExactly(64)
        let sigc = try network.readExactly(64)
        let th2 = LazoCrypto.digest(Data(th1), idc, kc)
        guard LazoCrypto.verify(publicKey: kc, digest: th2, signature: sigc) else { throw LazoError.invalid("El otro equipo no demostró su identidad.") }
        let keys = try deriveKeys(ephemeral, peer: ec, th2: Data(th2))
        return try finish(network, keys: keys, client: false, peerId: idc, peerKey: kc, th2: Data(th2))
    }

    private static func deriveKeys(_ ephemeral: P256.KeyAgreement.PrivateKey, peer: Data, th2: Data) throws -> Data {
        guard let peerKey = try? P256.KeyAgreement.PublicKey(rawRepresentation: peer) else { throw LazoError.invalid("Clave efímera no válida.") }
        let shared = try ephemeral.sharedSecretFromKeyAgreement(with: peerKey)
        let z = shared.withUnsafeBytes { Data($0) }
        return LazoCrypto.hkdf(salt: th2, ikm: LazoCrypto.sha256(z), info: LazoCrypto.ascii("lazo3 keys"), length: 128)
    }

    private static func finish(_ network: ByteStream, keys: Data, client: Bool, peerId: Data, peerKey: Data, th2: Data) throws -> SecureSession {
        guard let text = String(data: peerId, encoding: .ascii), let uuid = UUID(uuidString: text) else {
            throw LazoError.invalid("Identificador no válido.")
        }
        let k = [UInt8](keys)
        let encCs = Data(k[0..<32]), macCs = Data(k[32..<64]), encSc = Data(k[64..<96]), macSc = Data(k[96..<128])
        let stream = client
            ? SecureStream(inner: network, readKey: encSc, readMac: macSc, writeKey: encCs, writeMac: macCs)
            : SecureStream(inner: network, readKey: encCs, readMac: macCs, writeKey: encSc, writeMac: macSc)
        return SecureSession(stream: stream, peerId: uuid.uuidString.lowercased(), peerKey: peerKey, transcript: th2)
    }
}

/// Registros AES-256-CBC + HMAC-SHA256 con número de secuencia. Lectura y escritura son independientes.
public final class SecureStream: ByteStream {
    public static let maxPlain = 65536
    public let inner: ByteStream
    private let readKey: Data, readMac: Data, writeKey: Data, writeMac: Data
    private let readLock = NSLock(), writeLock = NSLock()
    private var readSeq: UInt64 = 0, writeSeq: UInt64 = 0
    private var plain = Data()
    private var plainOffset = 0
    private var pending = Data()
    private var ended = false

    init(inner: ByteStream, readKey: Data, readMac: Data, writeKey: Data, writeMac: Data) {
        self.inner = inner
        self.readKey = readKey
        self.readMac = readMac
        self.writeKey = writeKey
        self.writeMac = writeMac
    }

    public func read(into buffer: UnsafeMutableRawPointer, count: Int) throws -> Int {
        if count == 0 { return 0 }
        readLock.lock(); defer { readLock.unlock() }
        if plainOffset >= plain.count {
            if ended { return 0 }
            if try !readRecord() { ended = true; return 0 }
        }
        let take = min(count, plain.count - plainOffset)
        plain.withUnsafeBytes { raw in buffer.copyMemory(from: raw.baseAddress! + plainOffset, byteCount: take) }
        plainOffset += take
        return take
    }

    private func readRecord() throws -> Bool {
        while true {
            var header = Data(count: 4)
            var got = 0
            try header.withUnsafeMutableBytes { raw in
                while got < 4 {
                    let n = try inner.read(into: raw.baseAddress! + got, count: 4 - got)
                    if n <= 0 { if got == 0 { return }; throw LazoError.closed }
                    got += n
                }
            }
            if got == 0 { return false }
            let length = Int(header.withUnsafeBytes { $0.loadUnaligned(as: UInt32.self).littleEndian })
            guard length >= 16, length <= SecureStream.maxPlain + 16, length % 16 == 0 else { throw LazoError.invalid("Registro cifrado no válido.") }
            let iv = try inner.readExactly(16)
            let cipher = try inner.readExactly(length)
            let tag = try inner.readExactly(32)
            let expected = SecureStream.tag(readMac, seq: readSeq, header: header, iv: iv, cipher: cipher)
            guard LazoCrypto.fixedEquals(tag, expected) else { throw LazoError.invalid("Registro alterado o fuera de orden.") }
            readSeq += 1
            plain = try SecureStream.aes(CCOperation(kCCDecrypt), key: readKey, iv: iv, data: cipher)
            plainOffset = 0
            if !plain.isEmpty { return true }
        }
    }

    static func tag(_ key: Data, seq: UInt64, header: Data, iv: Data, cipher: Data) -> Data {
        var s = seq.littleEndian
        return LazoCrypto.hmac(key, Data(bytes: &s, count: 8), header, iv, cipher)
    }

    static func aes(_ operation: CCOperation, key: Data, iv: Data, data: Data) throws -> Data {
        var output = Data(count: data.count + kCCBlockSizeAES128)
        var moved = 0
        let status = output.withUnsafeMutableBytes { out in
            data.withUnsafeBytes { input in
                key.withUnsafeBytes { k in
                    iv.withUnsafeBytes { v in
                        CCCrypt(operation, CCAlgorithm(kCCAlgorithmAES), CCOptions(kCCOptionPKCS7Padding),
                                k.baseAddress, key.count, v.baseAddress, input.baseAddress, data.count,
                                out.baseAddress, out.count, &moved)
                    }
                }
            }
        }
        guard status == kCCSuccess else { throw LazoError.invalid("No se pudo descifrar.") }
        return output.prefix(moved)
    }

    public func write(_ data: Data) throws {
        writeLock.lock(); defer { writeLock.unlock() }
        var offset = 0
        while offset < data.count {
            let take = min(data.count - offset, SecureStream.maxPlain - pending.count)
            pending.append(data[data.startIndex + offset ..< data.startIndex + offset + take])
            offset += take
            if pending.count >= SecureStream.maxPlain { try emit() }
        }
    }

    public func flush() throws {
        writeLock.lock(); defer { writeLock.unlock() }
        try emit()
        try inner.flush()
    }

    private func emit() throws {
        if pending.isEmpty { return }
        let iv = LazoCrypto.random(16)
        let cipher = try SecureStream.aes(CCOperation(kCCEncrypt), key: writeKey, iv: iv, data: pending)
        pending.removeAll(keepingCapacity: true)
        var length = UInt32(cipher.count).littleEndian
        let header = Data(bytes: &length, count: 4)
        let tag = SecureStream.tag(writeMac, seq: writeSeq, header: header, iv: iv, cipher: cipher)
        writeSeq += 1
        try inner.write(header + iv + cipher + tag)
    }

    public func close() { inner.close() }
}
