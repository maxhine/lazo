import CryptoKit
import Foundation

public struct Peer: Identifiable, Equatable {
    public let id: String
    public var name: String
    public var photo: String = ""
    public var address: String
    public var port: Int
    public var seen: Date
    /// Huella que anuncia (sin verificar hasta conectar).
    public var fingerprint: String
    /// Conocido (ya hubo contacto) y con la misma huella que la guardada.
    public var paired = false
    /// Además, las dos personas confirmaron el código de verificación.
    public var verified = false
    /// Conocido antes pero ahora anuncia otra clave: no se le envía nada.
    public var identityChanged = false

    public init(id: String, name: String, address: String, port: Int, fingerprint: String) {
        self.id = id.lowercased()
        self.name = name
        self.address = address
        self.port = port
        self.seen = Date()
        self.fingerprint = fingerprint
    }
}

public struct Offer {
    public let id: UUID
    public let peerId: String
    public let sender: String
    public let address: String
    public let fileName: String
    public let size: Int64
    public let preview: Data?

    public init(id: UUID, peerId: String, sender: String, address: String, fileName: String, size: Int64, preview: Data?) {
        self.id = id; self.peerId = peerId; self.sender = sender; self.address = address
        self.fileName = fileName; self.size = size; self.preview = preview
    }
}

public struct ScreenInvite {
    public let peerId: String
    public let sender: String
    public let source: String
    public let address: String
}

/// Destino de una transmisión de pantalla aceptada; lee hasta que termina.
public protocol ScreenSink: AnyObject {
    func run(_ stream: SecureStream)
}

/// Verificación opcional: las dos personas comparan el mismo código de 6 cifras.
public final class PairingRequest {
    public let peerId: String
    public let peerName: String
    public let code: String
    public let fingerprint: String
    public let incoming: Bool
    private let lock = NSLock()
    private var remote: Bool?
    private var observers: [(Bool) -> Void] = []

    init(peerId: String, peerName: String, code: String, fingerprint: String, incoming: Bool) {
        self.peerId = peerId
        self.peerName = peerName
        self.code = code
        self.fingerprint = fingerprint
        self.incoming = incoming
    }

    /// Decisión del otro equipo (false si cancela o se corta).
    public func onRemote(_ observer: @escaping (Bool) -> Void) {
        lock.lock()
        if let remote { lock.unlock(); observer(remote); return }
        observers.append(observer)
        lock.unlock()
    }

    func setRemote(_ value: Bool) {
        lock.lock()
        if remote != nil { lock.unlock(); return }
        remote = value
        let list = observers
        observers = []
        lock.unlock()
        list.forEach { $0(value) }
    }
}

/// Conexión cifrada y autenticada con un compañero emparejado.
public final class SecureConnection {
    public let socket: TCPSocket
    public let stream: SecureStream
    public let peerId: String
    let session: SecureSession

    init(socket: TCPSocket, session: SecureSession) {
        self.socket = socket
        self.session = session
        self.stream = session.stream
        self.peerId = session.peerId
    }

    public func close() { socket.close() }
}

/// Red de Lazo con el protocolo LAZO3 (docs/protocolo-seguro.md). Las operaciones bloquean: llamarlas fuera del hilo principal.
public final class NetworkEngine {
    public static let discoveryPort = 48351
    public static let transferPort = 48352
    public static let chatKindText: UInt8 = 1, chatKindTyping: UInt8 = 2, chatKindPresence: UInt8 = 3, chatKindNudge: UInt8 = 4
    static let maxFileSize: Int64 = 20 * 1024 * 1024 * 1024
    private static let statusKnown: UInt8 = 1, statusMismatch: UInt8 = 2

    public let selfId: String
    public let identity: DeviceIdentity
    public let pairings: PairingStore
    public let receiveDirectory: URL
    private let discoveryPort: Int
    private let transferPort: Int
    private var udp: UDPBroadcaster?
    private var listener: TCPListener?
    private var running = false
    private let peerLock = NSLock()
    private var peers: [String: Peer] = [:]
    private let pairLock = NSLock()
    private var pairingOpen = Set<String>()
    private var pairingAsked: [String: [Date]] = [:]
    private let receiveLock = NSLock()
    private var activeReceives = 0
    private let sendSlots = DispatchSemaphore(value: 8)

    /// Nombre y foto (JPEG en base64) que se anuncian.
    public var displayName: () -> String = { NSFullUserName() }
    public var photo: () -> String = { "" }
    public var peersChanged: (([Peer]) -> Void)?
    /// Oferta de archivo: llamar a la respuesta con true para aceptar.
    public var offerReceived: ((Offer, @escaping (Bool) -> Void) -> Void)?
    public var receiveProgress: ((UUID, Double) -> Void)?
    public var receiveFinished: ((UUID, String, URL?) -> Void)?
    public var chatReceived: ((String, String, String) -> Void)?
    public var chatSignal: ((String, String, UInt8, String) -> Void)?
    public var screenOffered: ((ScreenInvite, @escaping (ScreenSink?) -> Void) -> Void)?
    /// Verificación: muestra el código; llamar a la respuesta con true si la persona confirma que coincide.
    public var pairingPrompt: ((PairingRequest, @escaping (Bool) -> Void) -> Void)?

    public static var defaultProfile: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("Lazo")
    }

    public static var defaultReceiveDirectory: URL {
        FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask)[0].appendingPathComponent("Lazo")
    }

    public init(receiveDirectory: URL = NetworkEngine.defaultReceiveDirectory, discoveryPort: Int = NetworkEngine.discoveryPort,
                transferPort: Int = NetworkEngine.transferPort, profile: URL = NetworkEngine.defaultProfile) {
        self.receiveDirectory = receiveDirectory
        self.discoveryPort = discoveryPort
        self.transferPort = transferPort
        selfId = NetworkEngine.loadId(profile)
        identity = DeviceIdentity.load(folder: profile, id: selfId)
        pairings = PairingStore(folder: profile)
        pairings.changed = { [weak self] in self?.publish() }
    }

    private static func loadId(_ folder: URL) -> String {
        let path = folder.appendingPathComponent("device.id")
        if let text = try? String(contentsOf: path, encoding: .utf8), let uuid = UUID(uuidString: text.trimmingCharacters(in: .whitespacesAndNewlines)) {
            return uuid.uuidString.lowercased()
        }
        let id = UUID().uuidString.lowercased()
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try? id.write(to: path, atomically: true, encoding: .utf8)
        return id
    }

    public func start() throws {
        let udp = try UDPBroadcaster(port: discoveryPort)
        let listener: TCPListener
        do { listener = try TCPListener(port: transferPort) } catch { udp.stop(); throw error }
        self.udp = udp
        self.listener = listener
        running = true
        udp.run { [weak self] data, address in self?.discovered(data, from: address) }
        listener.run { [weak self] socket in self?.handle(socket) }
        let thread = Thread { [weak self] in
            while let self, self.running {
                self.announce()
                for _ in 0..<25 where self.running { Thread.sleep(forTimeInterval: 0.1) }
            }
        }
        thread.name = "Lazo anuncios"
        thread.start()
    }

    public func stop() {
        running = false
        udp?.stop()
        listener?.stop()
    }

    // MARK: Descubrimiento

    private func discovered(_ data: Data, from address: String) {
        guard data.count <= 16500, IPv4.isLocalSubnet(address), let text = String(data: data, encoding: .utf8) else { return }
        let fields = text.components(separatedBy: "|")
        if fields.count == 3, fields[0] == "LAZO3A", let uuid = UUID(uuidString: fields[1]) {
            guard fields[2].count <= 16000, fields[2].isEmpty || Data(base64Encoded: fields[2]) != nil else { return }
            let id = uuid.uuidString.lowercased()
            peerLock.lock()
            if var peer = peers[id], peer.address == address, peer.photo != fields[2] {
                peer.photo = fields[2]
                peers[id] = peer
                peerLock.unlock()
                publish()
                return
            }
            peerLock.unlock()
            return
        }
        guard data.count <= 600, fields.count == 5, fields[0] == "LAZO3", let uuid = UUID(uuidString: fields[1]),
              let port = Int(fields[3]), port == transferPort, fields[4].count == 64,
              fields[4].allSatisfy({ $0.isHexDigit }) else { return }
        let id = uuid.uuidString.lowercased()
        guard id != selfId else { return }
        let name = Labels.clean(fields[2])
        guard !name.isEmpty else { return }
        let fingerprint = fields[4].lowercased()
        peerLock.lock()
        let previous = peers[id]
        var peer = Peer(id: id, name: name, address: address, port: port, fingerprint: fingerprint)
        peer.photo = previous?.photo ?? ""
        peers[id] = peer
        let changed = previous == nil || previous!.name != name || previous!.address != address || previous!.fingerprint != fingerprint
        peerLock.unlock()
        if changed { publish() }
    }

    private func announce() {
        guard let udp else { return }
        let payload = Data("LAZO3|\(selfId)|\(Labels.clean(displayName()))|\(transferPort)|\(identity.fingerprint)".utf8)
        let photoPayload = Data("LAZO3A|\(selfId)|\(photo())".utf8)
        for interface in IPv4.interfaces() {
            let target = IPv4.text(interface.broadcast)
            udp.send(payload, to: target, port: discoveryPort)
            udp.send(photoPayload, to: target, port: discoveryPort)
        }
        peerLock.lock()
        let before = peers.count
        peers = peers.filter { Date().timeIntervalSince($0.value.seen) <= 9 }
        let removed = peers.count != before
        peerLock.unlock()
        if removed { publish() }
    }

    public func snapshot() -> [Peer] {
        peerLock.lock(); defer { peerLock.unlock() }
        return decorate(Array(peers.values))
    }

    private func decorate(_ list: [Peer]) -> [Peer] {
        list.map { peer in
            var copy = peer
            let paired = pairings.get(peer.id)
            copy.paired = paired != nil && paired!.fingerprint == peer.fingerprint
            copy.verified = copy.paired && paired!.isVerified
            copy.identityChanged = paired != nil && paired!.fingerprint != peer.fingerprint
            return copy
        }.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }

    private func publish() {
        peersChanged?(snapshot())
    }

    // MARK: Servidor

    private func handle(_ socket: TCPSocket) {
        defer { socket.close() }
        guard IPv4.isLocalSubnet(socket.remoteAddress) else { return }
        do {
            socket.setReadTimeout(SecureSession.handshakeTimeout)
            socket.setWriteTimeout(30)
            guard try socket.readExactly(5) == LazoCrypto.ascii("LAZO3") else { return }
            let session = try SecureSession.accept(socket, me: identity)
            let known = pairings.get(session.peerId)
            let stream = session.stream
            if let known, known.fingerprint != session.peerFingerprint {
                try stream.writeByte(Self.statusMismatch)
                try stream.flush()
                return
            }
            // Primer contacto: se recuerda su clave sin preguntar (como SSH). Si un día cambia, se rechaza.
            if known == nil { pairings.set(id: session.peerId, fingerprint: session.peerFingerprint, name: nameOf(session.peerId)) }
            try stream.writeByte(Self.statusKnown)
            try stream.flush()
            socket.setReadTimeout(120)
            let input = BinaryIn(stream)
            let op = try input.u8()
            if op == UInt8(ascii: "P") {
                // Verificación opcional con código: solo marca el equipo como verificado.
                let name = Labels.clean(try input.text(max: 80))
                if !name.isEmpty { _ = verifyIncoming(session, name: name) }
                return
            }
            switch op {
            case UInt8(ascii: "F"): try receiveFile(socket, session, input)
            case UInt8(ascii: "C"): try readChat(session, input)
            case UInt8(ascii: "D"): try readSignal(session, input)
            case UInt8(ascii: "S"): try readScreen(socket, session, input)
            default: return
            }
        } catch {
            // Conexión no válida, cortada o rechazada.
        }
    }

    private func nameOf(_ id: String) -> String {
        peerLock.lock(); defer { peerLock.unlock() }
        return peers[id]?.name ?? "Equipo"
    }

    private func verifyIncoming(_ session: SecureSession, name: String) -> Bool {
        pairLock.lock()
        var asked = (pairingAsked[session.peerId] ?? []).filter { Date().timeIntervalSince($0) < 300 }
        if pairingOpen.contains(session.peerId) || asked.count >= 3 {
            pairLock.unlock()
            try? session.stream.writeByte(0)
            try? session.stream.flush()
            return false
        }
        asked.append(Date())
        pairingAsked[session.peerId] = asked
        pairingOpen.insert(session.peerId)
        pairLock.unlock()
        defer { pairLock.lock(); pairingOpen.remove(session.peerId); pairLock.unlock() }
        let ok = exchangePairing(session, name: name, incoming: true)
        if ok { pairings.markVerified(session.peerId, name: name) }
        return ok
    }

    /// Muestra el código, envía la decisión propia y espera la del otro equipo.
    private func exchangePairing(_ session: SecureSession, name: String, incoming: Bool) -> Bool {
        let request = PairingRequest(peerId: session.peerId, peerName: name, code: session.sas,
                                     fingerprint: session.peerFingerprint, incoming: incoming)
        let remoteDone = DispatchSemaphore(value: 0)
        var remote = false
        Thread.detachNewThread {
            let value = (try? session.stream.readByte()) ?? 0
            remote = value == 1
            request.setRemote(remote)
            remoteDone.signal()
        }
        var mine = false
        if let prompt = pairingPrompt {
            let decided = DispatchSemaphore(value: 0)
            prompt(request) { answer in mine = answer; decided.signal() }
            if decided.wait(timeout: .now() + 90) == .timedOut { mine = false }
        }
        do {
            try session.stream.writeByte(mine ? 1 : 0)
            try session.stream.flush()
        } catch { return false }
        guard mine else { return false }
        if remoteDone.wait(timeout: .now() + 95) == .timedOut { return false }
        return remote
    }

    // MARK: Cliente

    /// Conecta y comprueba la identidad. La primera vez se recuerda la clave; si después cambia, se rechaza.
    func open(_ peer: Peer, connectTimeout: TimeInterval = 7) throws -> SecureConnection {
        guard IPv4.isLocalSubnet(peer.address) else { throw LazoError.refused("El compañero ya no está en la misma subred.") }
        let known = pairings.get(peer.id)
        let socket = try TCPSocket.connect(host: peer.address, port: peer.port, timeout: connectTimeout)
        do {
            socket.setReadTimeout(SecureSession.handshakeTimeout)
            socket.setWriteTimeout(30)
            let session = try SecureSession.connect(socket, me: identity)
            guard session.peerId == peer.id else { throw LazoError.refused("Respondió un equipo distinto del esperado.") }
            if let known, known.fingerprint != session.peerFingerprint {
                throw LazoError.identityChanged("La identidad de \(peer.name) cambió. Si reinstaló Lazo, olvídalo en Ajustes › Equipos conocidos.")
            }
            let status = try session.stream.readByte()
            if status == Self.statusMismatch {
                throw LazoError.identityChanged("\(peer.name) tiene guardada otra identidad de este equipo. Debe olvidarlo en sus Ajustes.")
            }
            guard status == Self.statusKnown else { throw LazoError.closed }
            if known == nil { pairings.set(id: peer.id, fingerprint: session.peerFingerprint, name: peer.name) }
            socket.setReadTimeout(120)
            return SecureConnection(socket: socket, session: session)
        } catch {
            socket.close()
            throw error
        }
    }

    /// Verificación opcional: las dos personas comparan un código de 6 cifras. Devuelve false si se cancela o no coincide.
    public func verify(_ peer: Peer) throws -> Bool {
        let connection = try open(peer)
        defer { connection.close() }
        var out = BinaryOut()
        out.u8(UInt8(ascii: "P"))
        try out.text(Labels.clean(displayName()), max: 80)
        try out.send(connection.stream)
        let ok = exchangePairing(connection.session, name: peer.name, incoming: false)
        if ok { pairings.markVerified(peer.id, name: peer.name) }
        return ok
    }

    // MARK: Chat y señales

    public func sendChat(_ peer: Peer, text raw: String) throws {
        var text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        if text.isEmpty { return }
        if text.count > 2000 { text = String(text.prefix(2000)) }
        let connection = try open(peer)
        defer { connection.close() }
        var out = BinaryOut()
        out.u8(UInt8(ascii: "C"))
        try out.text(Labels.clean(displayName()), max: 80)
        try out.text(text, max: 4000)
        try out.send(connection.stream)
        connection.socket.setReadTimeout(7)
        guard try connection.stream.readByte() == 1 else { throw LazoError.refused("No se pudo entregar el mensaje.") }
    }

    private func readChat(_ session: SecureSession, _ input: BinaryIn) throws {
        let name = Labels.clean(try input.text(max: 80))
        let body = try input.text(max: 4000).trimmingCharacters(in: .whitespacesAndNewlines)
        guard !name.isEmpty, !body.isEmpty, body.count <= 2000 else { return }
        try session.stream.writeByte(1)
        try session.stream.flush()
        chatReceived?(session.peerId, name, body)
    }

    /// Escribiendo, presencia y zumbido. Nunca pide emparejar.
    public func sendSignal(_ peer: Peer, kind: UInt8, body: String) {
        guard peer.paired, let connection = try? open(peer, connectTimeout: 4) else { return }
        defer { connection.close() }
        var out = BinaryOut()
        out.u8(UInt8(ascii: "D"))
        try? out.text(Labels.clean(displayName()), max: 80)
        out.u8(kind)
        try? out.text(String(body.prefix(2000)), max: 4000)
        try? out.send(connection.stream)
        connection.socket.setReadTimeout(4)
        _ = try? connection.stream.readByte()
    }

    private func readSignal(_ session: SecureSession, _ input: BinaryIn) throws {
        let name = Labels.clean(try input.text(max: 80))
        let kind = try input.u8()
        let body = try input.text(max: 4000)
        guard !name.isEmpty else { return }
        try session.stream.writeByte(1)
        try session.stream.flush()
        if kind == Self.chatKindText {
            let text = body.trimmingCharacters(in: .whitespacesAndNewlines)
            if !text.isEmpty { chatReceived?(session.peerId, name, text) }
            return
        }
        chatSignal?(session.peerId, name, kind, body)
    }

    // MARK: Pantalla

    /// Invita a ver la pantalla. Devuelve la conexión abierta si acepta.
    public func openScreen(_ peer: Peer, source: String) throws -> SecureConnection {
        let connection = try open(peer)
        do {
            connection.socket.noDelay = true
            connection.socket.setBuffers(send: 1 << 20, receive: 1 << 16)
            connection.socket.setWriteTimeout(6)
            var out = BinaryOut()
            out.u8(UInt8(ascii: "S"))
            try out.text(Labels.clean(displayName()), max: 80)
            try out.text(Labels.clean(source, max: 400), max: 1600)
            try out.send(connection.stream)
            connection.socket.setReadTimeout(65)
            guard try connection.stream.readByte() == 1 else { throw LazoError.refused("\(peer.name) no aceptó ver la pantalla.") }
            connection.socket.setReadTimeout(0)
            return connection
        } catch {
            connection.close()
            throw error
        }
    }

    private func readScreen(_ socket: TCPSocket, _ session: SecureSession, _ input: BinaryIn) throws {
        let name = Labels.clean(try input.text(max: 80))
        let source = Labels.clean(try input.text(max: 1600), max: 400)
        guard !name.isEmpty, let handler = screenOffered else { return }
        let decided = DispatchSemaphore(value: 0)
        var sink: ScreenSink?
        handler(ScreenInvite(peerId: session.peerId, sender: name, source: source, address: socket.remoteAddress)) { sink = $0; decided.signal() }
        if decided.wait(timeout: .now() + 60) == .timedOut { sink = nil }
        guard let sink else {
            try session.stream.writeByte(0)
            try session.stream.flush()
            return
        }
        try session.stream.writeByte(1)
        try session.stream.flush()
        socket.noDelay = true
        socket.setBuffers(send: 1 << 16, receive: 1 << 20)
        socket.setReadTimeout(15)
        sink.run(session.stream)
    }

    // MARK: Archivos

    public func send(_ peer: Peer, file: URL, progress: ((Double) -> Void)?) throws {
        sendSlots.wait()
        defer { sendSlots.signal() }
        let attributes = try FileManager.default.attributesOfItem(atPath: file.path)
        let size = (attributes[.size] as? NSNumber)?.int64Value ?? 0
        guard size <= Self.maxFileSize else { throw LazoError.refused("Límite: 20 GB por archivo.") }
        let connection = try open(peer)
        defer { connection.close() }
        let stream = connection.stream
        let preview = ImagePreview.jpeg(for: file)
        var out = BinaryOut()
        out.u8(UInt8(ascii: "F"))
        try out.text(Labels.clean(displayName()), max: 80)
        try out.text(file.lastPathComponent, max: 255 * 4)
        out.i64(size)
        out.i32(Int32(preview?.count ?? 0))
        if let preview { out.bytes(preview) }
        try out.send(stream)
        connection.socket.setReadTimeout(120)
        guard try stream.readByte() == 1 else { throw LazoError.refused("El destinatario rechazó la transferencia o está ocupado.") }
        let handle = try FileHandle(forReadingFrom: file)
        defer { try? handle.close() }
        var hasher = SHA256()
        var sent: Int64 = 0
        while let chunk = try handle.read(upToCount: SecureStream.maxPlain), !chunk.isEmpty {
            try stream.write(chunk)
            hasher.update(data: chunk)
            sent += Int64(chunk.count)
            progress?(size == 0 ? 1 : Double(sent) / Double(size))
        }
        try stream.write(Data(hasher.finalize()))
        try stream.flush()
        guard try stream.readByte() == 1 else { throw LazoError.refused("El destinatario no pudo guardar el archivo.") }
    }

    private func receiveFile(_ socket: TCPSocket, _ session: SecureSession, _ input: BinaryIn) throws {
        let stream = session.stream
        let sender = Labels.clean(try input.text(max: 80))
        let name = Labels.safeFileName(try input.text(max: 255 * 4))
        let size = try input.i64()
        let previewLength = Int(try input.i32())
        guard !sender.isEmpty, !name.isEmpty, size >= 0, size <= Self.maxFileSize, previewLength >= 0, previewLength <= 48000 else { return }
        let preview = previewLength == 0 ? nil : try input.bytes(previewLength)
        let offerId = UUID()
        receiveLock.lock()
        let busy = activeReceives >= 8 || !Self.hasRoom(receiveDirectory, size)
        if !busy { activeReceives += 1 }
        receiveLock.unlock()
        if busy { try stream.writeByte(0); try stream.flush(); return }
        defer { receiveLock.lock(); activeReceives -= 1; receiveLock.unlock() }
        var allow = false
        if let handler = offerReceived {
            let decided = DispatchSemaphore(value: 0)
            handler(Offer(id: offerId, peerId: session.peerId, sender: sender, address: socket.remoteAddress,
                          fileName: name, size: size, preview: preview)) { allow = $0; decided.signal() }
            if decided.wait(timeout: .now() + 95) == .timedOut { allow = false }
        }
        guard allow else { try stream.writeByte(0); try stream.flush(); return }
        try stream.writeByte(1)
        try stream.flush()
        socket.setReadTimeout(30)
        try? FileManager.default.createDirectory(at: receiveDirectory, withIntermediateDirectories: true)
        let temp = receiveDirectory.appendingPathComponent(".\(offerId.uuidString).part")
        FileManager.default.createFile(atPath: temp.path, contents: nil, attributes: [.posixPermissions: 0o600])
        var keepTemp = false
        defer { if !keepTemp { try? FileManager.default.removeItem(at: temp) } }
        do {
            let file = try FileHandle(forWritingTo: temp)
            var hasher = SHA256()
            var received: Int64 = 0
            var buffer = [UInt8](repeating: 0, count: SecureStream.maxPlain)
            while received < size {
                let want = Int(min(Int64(buffer.count), size - received))
                let count = try buffer.withUnsafeMutableBytes { try stream.read(into: $0.baseAddress!, count: want) }
                if count <= 0 { throw LazoError.closed }
                let chunk = Data(buffer[0..<count])
                try file.write(contentsOf: chunk)
                hasher.update(data: chunk)
                received += Int64(count)
                receiveProgress?(offerId, size == 0 ? 1 : Double(received) / Double(size))
            }
            try file.close()
            let expected = try stream.readExactly(32)
            guard LazoCrypto.fixedEquals(Data(hasher.finalize()), expected) else { throw LazoError.invalid("El archivo no superó la verificación de integridad.") }
            let destination = Self.uniqueDestination(receiveDirectory, name)
            try FileManager.default.moveItem(at: temp, to: destination)
            keepTemp = true
            Quarantine.apply(destination, sender: sender)
            try stream.writeByte(1)
            try stream.flush()
            receiveFinished?(offerId, "Archivo recibido", destination)
        } catch {
            receiveFinished?(offerId, "Error: " + (error.localizedDescription), nil)
        }
    }

    static func hasRoom(_ folder: URL, _ size: Int64) -> Bool {
        let probe = FileManager.default.fileExists(atPath: folder.path) ? folder : folder.deletingLastPathComponent()
        guard let values = try? probe.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey]),
              let free = values.volumeAvailableCapacityForImportantUsage else { return true }
        return free > size + 256 * 1024 * 1024
    }

    static func uniqueDestination(_ folder: URL, _ name: String) -> URL {
        let base = (name as NSString).deletingPathExtension
        let ext = (name as NSString).pathExtension
        var result = folder.appendingPathComponent(name)
        var i = 1
        while FileManager.default.fileExists(atPath: result.path) {
            result = folder.appendingPathComponent(ext.isEmpty ? "\(base) (\(i))" : "\(base) (\(i)).\(ext)")
            i += 1
        }
        return result
    }
}
