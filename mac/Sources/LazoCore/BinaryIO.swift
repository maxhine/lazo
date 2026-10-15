import Foundation

/// Lectura little endian, como BinaryReader de .NET.
public struct BinaryIn {
    public let stream: ByteStream
    public init(_ stream: ByteStream) { self.stream = stream }

    public func u8() throws -> UInt8 { try stream.readByte() }
    public func u16() throws -> UInt16 { try load(UInt16.self) }
    public func i16() throws -> Int16 { try load(Int16.self) }
    public func i32() throws -> Int32 { try load(Int32.self) }
    public func u32() throws -> UInt32 { try load(UInt32.self) }
    public func i64() throws -> Int64 { try load(Int64.self) }
    public func bytes(_ count: Int) throws -> Data { try stream.readExactly(count) }

    private func load<T: FixedWidthInteger>(_ type: T.Type) throws -> T {
        let data = try stream.readExactly(MemoryLayout<T>.size)
        return T(littleEndian: data.withUnsafeBytes { $0.loadUnaligned(as: T.self) })
    }

    /// `u16` con la longitud en bytes y UTF-8.
    public func text(max: Int) throws -> String {
        let length = Int(try u16())
        if length > max { throw LazoError.invalid("Campo demasiado largo.") }
        guard let value = String(data: try bytes(length), encoding: .utf8) else { throw LazoError.invalid("Texto no válido.") }
        return value
    }
}

/// Escritura little endian acumulada; `send` la entrega entera y vacía el canal.
public struct BinaryOut {
    public private(set) var data = Data()
    public init() {}

    public mutating func u8(_ v: UInt8) { data.append(v) }
    public mutating func u16(_ v: UInt16) { append(v) }
    public mutating func i16(_ v: Int16) { append(v) }
    public mutating func i32(_ v: Int32) { append(v) }
    public mutating func u32(_ v: UInt32) { append(v) }
    public mutating func i64(_ v: Int64) { append(v) }
    public mutating func bytes(_ v: Data) { data.append(v) }

    private mutating func append<T: FixedWidthInteger>(_ value: T) {
        var le = value.littleEndian
        withUnsafeBytes(of: &le) { data.append(contentsOf: $0) }
    }

    public mutating func text(_ value: String, max: Int) throws {
        let bytes = Data(value.utf8)
        if bytes.count > max { throw LazoError.invalid("Texto demasiado largo.") }
        u16(UInt16(bytes.count))
        data.append(bytes)
    }

    public func send(_ stream: ByteStream) throws {
        try stream.write(data)
        try stream.flush()
    }
}

public enum Labels {
    /// Sin controles, sin `|` y sin caracteres que invierten el sentido del texto.
    public static func clean(_ value: String, max: Int = 60) -> String {
        let filtered = value.unicodeScalars.filter { s in
            !CharacterSet.controlCharacters.contains(s) && s != "|" && !isBidi(s)
        }
        return String(String.UnicodeScalarView(filtered.prefix(max))).trimmingCharacters(in: .whitespaces)
    }

    static func isBidi(_ s: Unicode.Scalar) -> Bool {
        let v = s.value
        return (0x202A...0x202E).contains(v) || (0x2066...0x2069).contains(v) || v == 0x200E || v == 0x200F || v == 0x061C
    }

    /// Nombre de archivo recibido seguro, o "" si no lo es.
    public static func safeFileName(_ raw: String) -> String {
        var name = raw
        while name.hasSuffix(" ") || name.hasSuffix(".") { name.removeLast() }
        if name.isEmpty || name.count > 200 || name == "." || name == ".." { return "" }
        // En Finder un nombre con punto inicial queda oculto: se recibe visible.
        if name.hasPrefix(".") { name = "_" + name.dropFirst() }
        if name.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) || isBidi($0) || "/\\:*?\"<>|".unicodeScalars.contains($0) }) { return "" }
        return name
    }
}
