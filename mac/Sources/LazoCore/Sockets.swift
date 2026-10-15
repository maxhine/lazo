import Darwin
import Foundation

public enum LazoError: Error, LocalizedError {
    case closed
    case timeout(String)
    case invalid(String)
    case refused(String)
    case pairingRequired(String)
    case system(String)

    public var errorDescription: String? {
        switch self {
        case .closed: return "Se perdió la conexión."
        case .timeout(let text), .invalid(let text), .refused(let text), .pairingRequired(let text), .system(let text): return text
        }
    }
}

/// Flujo de bytes bloqueante. Lo implementan el socket TCP y el canal cifrado.
public protocol ByteStream: AnyObject {
    /// Devuelve 0 al final del flujo.
    func read(into buffer: UnsafeMutableRawPointer, count: Int) throws -> Int
    func write(_ data: Data) throws
    func flush() throws
    func close()
}

public extension ByteStream {
    func readExactly(_ count: Int) throws -> Data {
        var data = Data(count: count)
        var done = 0
        try data.withUnsafeMutableBytes { raw in
            while done < count {
                let n = try read(into: raw.baseAddress! + done, count: count - done)
                if n <= 0 { throw LazoError.closed }
                done += n
            }
        }
        return data
    }

    func readByte() throws -> UInt8 {
        var byte: UInt8 = 0
        let n = try withUnsafeMutableBytes(of: &byte) { try read(into: $0.baseAddress!, count: 1) }
        if n <= 0 { throw LazoError.closed }
        return byte
    }

    func writeByte(_ value: UInt8) throws {
        try write(Data([value]))
    }
}

/// Socket TCP IPv4 bloqueante.
public final class TCPSocket: ByteStream {
    public let fd: Int32
    public let remoteAddress: String
    private let lock = NSLock()
    private var closed = false

    init(fd: Int32, remote: String) {
        self.fd = fd
        self.remoteAddress = remote
        var on: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &on, socklen_t(MemoryLayout<Int32>.size))
    }

    public static func connect(host: String, port: Int, timeout: TimeInterval) throws -> TCPSocket {
        let fd = socket(AF_INET, SOCK_STREAM, 0)
        if fd < 0 { throw LazoError.system("No se pudo crear el socket.") }
        var address = sockaddr_in()
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = in_port_t(UInt16(port).bigEndian)
        guard inet_pton(AF_INET, host, &address.sin_addr) == 1 else { Darwin.close(fd); throw LazoError.invalid("Dirección no válida.") }
        let flags = fcntl(fd, F_GETFL, 0)
        _ = fcntl(fd, F_SETFL, flags | O_NONBLOCK)
        let result = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { Darwin.connect(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
        }
        if result != 0 && errno != EINPROGRESS { Darwin.close(fd); throw LazoError.refused("El compañero no respondió.") }
        if result != 0 {
            var pfd = pollfd(fd: fd, events: Int16(POLLOUT), revents: 0)
            let ready = poll(&pfd, 1, Int32(timeout * 1000))
            var error: Int32 = 0
            var length = socklen_t(MemoryLayout<Int32>.size)
            getsockopt(fd, SOL_SOCKET, SO_ERROR, &error, &length)
            if ready <= 0 || error != 0 {
                Darwin.close(fd)
                throw ready == 0 ? LazoError.timeout("El compañero no respondió.") : LazoError.refused("El compañero no respondió.")
            }
        }
        _ = fcntl(fd, F_SETFL, flags & ~O_NONBLOCK)
        let socket = TCPSocket(fd: fd, remote: host)
        socket.noDelay = true
        return socket
    }

    public var noDelay: Bool = false {
        didSet {
            var on: Int32 = noDelay ? 1 : 0
            setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &on, socklen_t(MemoryLayout<Int32>.size))
        }
    }

    /// Tiempo máximo de lectura en segundos (0 = sin límite).
    public func setReadTimeout(_ seconds: TimeInterval) {
        var tv = timeval(tv_sec: Int(seconds), tv_usec: Int32((seconds - floor(seconds)) * 1_000_000))
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
    }

    public func setWriteTimeout(_ seconds: TimeInterval) {
        var tv = timeval(tv_sec: Int(seconds), tv_usec: Int32((seconds - floor(seconds)) * 1_000_000))
        setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
    }

    public func setBuffers(send: Int, receive: Int) {
        var s = Int32(send), r = Int32(receive)
        setsockopt(fd, SOL_SOCKET, SO_SNDBUF, &s, socklen_t(MemoryLayout<Int32>.size))
        setsockopt(fd, SOL_SOCKET, SO_RCVBUF, &r, socklen_t(MemoryLayout<Int32>.size))
    }

    public func read(into buffer: UnsafeMutableRawPointer, count: Int) throws -> Int {
        while true {
            let n = Darwin.recv(fd, buffer, count, 0)
            if n >= 0 { return n }
            if errno == EINTR { continue }
            if errno == EAGAIN || errno == EWOULDBLOCK { throw LazoError.timeout("El compañero no respondió.") }
            throw LazoError.closed
        }
    }

    public func write(_ data: Data) throws {
        try data.withUnsafeBytes { raw in
            var done = 0
            while done < raw.count {
                let n = Darwin.send(fd, raw.baseAddress! + done, raw.count - done, 0)
                if n < 0 {
                    if errno == EINTR { continue }
                    throw LazoError.closed
                }
                done += n
            }
        }
    }

    public func flush() throws {}

    public func close() {
        lock.lock()
        defer { lock.unlock() }
        if closed { return }
        closed = true
        Darwin.shutdown(fd, SHUT_RDWR)
        Darwin.close(fd)
    }

    deinit { close() }
}

/// Escucha conexiones TCP en todas las interfaces IPv4.
public final class TCPListener {
    private let fd: Int32
    private var running = true

    public init(port: Int) throws {
        fd = socket(AF_INET, SOCK_STREAM, 0)
        if fd < 0 { throw LazoError.system("No se pudo crear el socket de escucha.") }
        var on: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &on, socklen_t(MemoryLayout<Int32>.size))
        var address = sockaddr_in()
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = in_port_t(UInt16(port).bigEndian)
        address.sin_addr = in_addr(s_addr: INADDR_ANY)
        let bound = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
        }
        if bound != 0 || listen(fd, 16) != 0 {
            Darwin.close(fd)
            throw LazoError.system("El puerto TCP \(port) está ocupado. ¿Hay otra copia de Lazo abierta?")
        }
    }

    public func run(_ handler: @escaping (TCPSocket) -> Void) {
        let thread = Thread { [weak self] in
            guard let self else { return }
            while self.running {
                var address = sockaddr_in()
                var length = socklen_t(MemoryLayout<sockaddr_in>.size)
                let client = withUnsafeMutablePointer(to: &address) {
                    $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { accept(self.fd, $0, &length) }
                }
                if client < 0 { if errno == EINTR { continue }; if !self.running { return }; usleep(50_000); continue }
                let remote = IPv4.string(address.sin_addr)
                let socket = TCPSocket(fd: client, remote: remote)
                Thread.detachNewThread { handler(socket) }
            }
        }
        thread.name = "Lazo TCP"
        thread.start()
    }

    public func stop() {
        running = false
        Darwin.shutdown(fd, SHUT_RDWR)
        Darwin.close(fd)
    }
}

/// Difusión UDP del descubrimiento.
public final class UDPBroadcaster {
    private let fd: Int32
    private var running = true

    public init(port: Int) throws {
        fd = socket(AF_INET, SOCK_DGRAM, 0)
        if fd < 0 { throw LazoError.system("No se pudo crear el socket UDP.") }
        var on: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &on, socklen_t(MemoryLayout<Int32>.size))
        setsockopt(fd, SOL_SOCKET, SO_REUSEPORT, &on, socklen_t(MemoryLayout<Int32>.size))
        setsockopt(fd, SOL_SOCKET, SO_BROADCAST, &on, socklen_t(MemoryLayout<Int32>.size))
        var address = sockaddr_in()
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = in_port_t(UInt16(port).bigEndian)
        address.sin_addr = in_addr(s_addr: INADDR_ANY)
        let bound = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
        }
        if bound != 0 { Darwin.close(fd); throw LazoError.system("El puerto UDP \(port) está ocupado.") }
    }

    public func send(_ data: Data, to host: String, port: Int) {
        var address = sockaddr_in()
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = in_port_t(UInt16(port).bigEndian)
        guard inet_pton(AF_INET, host, &address.sin_addr) == 1 else { return }
        data.withUnsafeBytes { raw in
            _ = withUnsafePointer(to: &address) {
                $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    sendto(fd, raw.baseAddress, raw.count, 0, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
                }
            }
        }
    }

    public func run(_ handler: @escaping (Data, String) -> Void) {
        let thread = Thread { [weak self] in
            var buffer = [UInt8](repeating: 0, count: 17000)
            while let self, self.running {
                var address = sockaddr_in()
                var length = socklen_t(MemoryLayout<sockaddr_in>.size)
                let n = buffer.withUnsafeMutableBytes { raw in
                    withUnsafeMutablePointer(to: &address) {
                        $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { recvfrom(self.fd, raw.baseAddress, raw.count, 0, $0, &length) }
                    }
                }
                if n <= 0 { if !self.running { return }; usleep(50_000); continue }
                handler(Data(buffer[0..<n]), IPv4.string(address.sin_addr))
            }
        }
        thread.name = "Lazo UDP"
        thread.start()
    }

    public func stop() {
        running = false
        Darwin.shutdown(fd, SHUT_RDWR)
        Darwin.close(fd)
    }
}

/// Direcciones IPv4 privadas locales y sus redes.
public enum IPv4 {
    public struct Interface { public let address: UInt32; public let mask: UInt32; public let broadcast: UInt32 }

    static func string(_ address: in_addr) -> String {
        var copy = address
        var buffer = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
        inet_ntop(AF_INET, &copy, &buffer, socklen_t(INET_ADDRSTRLEN))
        return String(cString: buffer)
    }

    public static func value(_ text: String) -> UInt32? {
        var address = in_addr()
        guard inet_pton(AF_INET, text, &address) == 1 else { return nil }
        return UInt32(bigEndian: address.s_addr)
    }

    public static func text(_ value: UInt32) -> String {
        "\(value >> 24 & 255).\(value >> 16 & 255).\(value >> 8 & 255).\(value & 255)"
    }

    public static func isPrivate(_ value: UInt32) -> Bool {
        let a = value >> 24, b = value >> 16 & 255
        return a == 10 || (a == 172 && b >= 16 && b <= 31) || (a == 192 && b == 168)
    }

    public static func interfaces() -> [Interface] {
        var result: [Interface] = []
        var list: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&list) == 0, let first = list else { return [] }
        defer { freeifaddrs(list) }
        var cursor: UnsafeMutablePointer<ifaddrs>? = first
        while let entry = cursor {
            defer { cursor = entry.pointee.ifa_next }
            let flags = Int32(entry.pointee.ifa_flags)
            guard flags & IFF_UP != 0, flags & IFF_LOOPBACK == 0,
                  let addr = entry.pointee.ifa_addr, addr.pointee.sa_family == UInt8(AF_INET),
                  let maskPointer = entry.pointee.ifa_netmask else { continue }
            let address = addr.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { UInt32(bigEndian: $0.pointee.sin_addr.s_addr) }
            let mask = maskPointer.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { UInt32(bigEndian: $0.pointee.sin_addr.s_addr) }
            guard isPrivate(address) else { continue }
            result.append(Interface(address: address, mask: mask, broadcast: address | ~mask))
        }
        return result
    }

    public static func isLocalSubnet(_ text: String) -> Bool {
        guard let value = value(text), isPrivate(value) else { return false }
        return interfaces().contains { $0.address & $0.mask == value & $0.mask }
    }
}
