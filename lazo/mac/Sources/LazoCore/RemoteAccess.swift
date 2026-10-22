import CommonCrypto
import CryptoKit
import Foundation

/// Control remoto con contraseña (docs/control-remoto.md): el anfitrión guarda solo un hash PBKDF2 y comprueba una prueba
/// HMAC sobre un reto, de modo que la contraseña nunca viaja ni se guarda en claro.
public final class RemoteAccess {
    public static let iterations = 150_000
    public static let minLength = 8
    static let maxFailures = 5
    static let failureWindow: TimeInterval = 600
    static let lockout: TimeInterval = 600

    private let file: URL
    private let lock = NSLock()
    private var failures: [String: [Date]] = [:]
    private var lockedUntil: [String: Date] = [:]

    public init(profile: URL) {
        file = profile.appendingPathComponent("remote.txt")
    }

    private struct Record {
        var iterations: Int
        var salt: Data
        var hash: Data
        var enabled: Bool
    }

    private func load() -> Record? {
        guard let text = try? String(contentsOf: file, encoding: .utf8) else { return nil }
        let parts = text.trimmingCharacters(in: .whitespacesAndNewlines).split(separator: "|", omittingEmptySubsequences: false).map(String.init)
        guard parts.count >= 5, parts[0] == "v1", let iterations = Int(parts[1]), (50_000...1_000_000).contains(iterations),
              let salt = Data(base64Encoded: parts[2]), salt.count == 16, let hash = Data(base64Encoded: parts[3]), hash.count == 32 else { return nil }
        return Record(iterations: iterations, salt: salt, hash: hash, enabled: parts[4] == "1")
    }

    private func store(_ record: Record) {
        let line = "v1|\(record.iterations)|\(record.salt.base64EncodedString())|\(record.hash.base64EncodedString())|\(record.enabled ? 1 : 0)"
        try? FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? line.write(to: file, atomically: true, encoding: .utf8)
        try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path)
    }

    public var hasPassword: Bool { lock.lock(); defer { lock.unlock() }; return load() != nil }

    public var isEnabled: Bool { lock.lock(); defer { lock.unlock() }; return load()?.enabled ?? false }

    /// Activa o desactiva el acceso sin tocar la contraseña. No se puede activar sin contraseña.
    public func setEnabled(_ on: Bool) {
        lock.lock(); defer { lock.unlock() }
        guard var record = load() else { return }
        record.enabled = on
        store(record)
    }

    /// Guarda una contraseña nueva (mínimo 8 caracteres) y deja el acceso activado.
    public func setPassword(_ password: String) throws {
        guard password.count >= Self.minLength else { throw LazoError.invalid("La contraseña debe tener al menos \(Self.minLength) caracteres.") }
        var salt = Data(count: 16)
        _ = salt.withUnsafeMutableBytes { SecRandomCopyBytes(kSecRandomDefault, 16, $0.baseAddress!) }
        let hash = Self.derive(password, salt: salt, iterations: Self.iterations)
        lock.lock(); defer { lock.unlock() }
        store(Record(iterations: Self.iterations, salt: salt, hash: hash, enabled: true))
    }

    /// Reto para quien quiere entrar: sal e iteraciones guardadas y un nonce aleatorio. nil si no hay contraseña.
    public func makeChallenge() -> (salt: Data, iterations: Int, nonce: Data)? {
        lock.lock(); defer { lock.unlock() }
        guard let record = load() else { return nil }
        var nonce = Data(count: 16)
        _ = nonce.withUnsafeMutableBytes { SecRandomCopyBytes(kSecRandomDefault, 16, $0.baseAddress!) }
        return (record.salt, record.iterations, nonce)
    }

    /// Comprueba la prueba del visor en tiempo constante.
    public func verify(proof: Data, nonce: Data, viewerId: String, hostId: String) -> Bool {
        lock.lock()
        let record = load()
        lock.unlock()
        guard let record, proof.count == 32 else { return false }
        let expected = Self.mac(key: record.hash, nonce: nonce, viewerId: viewerId, hostId: hostId)
        return LazoCrypto.fixedEquals(expected, proof)
    }

    public func isLocked(_ peerId: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        if let until = lockedUntil[peerId] {
            if until > Date() { return true }
            lockedUntil.removeValue(forKey: peerId)
        }
        return false
    }

    /// Anota una contraseña errónea; a la quinta en 10 minutos, ese equipo queda bloqueado 10 minutos.
    public func registerFailure(_ peerId: String) {
        lock.lock(); defer { lock.unlock() }
        let now = Date()
        var list = (failures[peerId] ?? []).filter { now.timeIntervalSince($0) < Self.failureWindow }
        list.append(now)
        failures[peerId] = list
        if list.count >= Self.maxFailures { lockedUntil[peerId] = now.addingTimeInterval(Self.lockout); failures[peerId] = [] }
    }

    public func registerSuccess(_ peerId: String) {
        lock.lock(); defer { lock.unlock() }
        failures[peerId] = nil
        lockedUntil[peerId] = nil
    }

    // MARK: Criptografía (igual que src/RemoteAccess.cs)

    static func derive(_ password: String, salt: Data, iterations: Int) -> Data {
        var out = Data(count: 32)
        let secret = Array(password.utf8)
        let status = out.withUnsafeMutableBytes { outBytes in
            salt.withUnsafeBytes { saltBytes in
                CCKeyDerivationPBKDF(CCPBKDFAlgorithm(kCCPBKDF2), secret.map { CChar(bitPattern: $0) }, secret.count,
                                     saltBytes.bindMemory(to: UInt8.self).baseAddress!, salt.count,
                                     CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA256), UInt32(iterations),
                                     outBytes.bindMemory(to: UInt8.self).baseAddress!, 32)
            }
        }
        return status == kCCSuccess ? out : Data(count: 32)
    }

    static func mac(key: Data, nonce: Data, viewerId: String, hostId: String) -> Data {
        var message = nonce
        message.append(Data("\(viewerId)|\(hostId)".utf8))
        return Data(HMAC<SHA256>.authenticationCode(for: message, using: SymmetricKey(data: key)))
    }

    /// Prueba que envía el visor: HMAC(PBKDF2(contraseña, sal, iteraciones), nonce ‖ "<id visor>|<id anfitrión>").
    public static func proof(password: String, salt: Data, iterations: Int, nonce: Data, viewerId: String, hostId: String) -> Data {
        mac(key: derive(password, salt: salt, iterations: iterations), nonce: nonce, viewerId: viewerId.lowercased(), hostId: hostId.lowercased())
    }
}
