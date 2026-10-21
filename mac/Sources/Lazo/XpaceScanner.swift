import Foundation

/// Una carpeta del análisis: lo suyo (archivos directos) y el total acumulado de todo lo que contiene.
final class XNode: Identifiable, Hashable {
    let name: String
    weak var parent: XNode?
    var children: [XNode] = []
    /// Bytes y cantidad de los archivos que están directamente en esta carpeta.
    var ownSize: Int64 = 0
    var ownFiles: Int64 = 0
    /// Totales con todo el contenido (se calculan al terminar).
    var size: Int64 = 0
    var files: Int64 = 0
    var folders = 0
    var modified: Date?
    var denied = false

    init(name: String, parent: XNode? = nil) { self.name = name; self.parent = parent }

    var id: ObjectIdentifier { ObjectIdentifier(self) }
    static func == (a: XNode, b: XNode) -> Bool { a === b }
    func hash(into hasher: inout Hasher) { hasher.combine(ObjectIdentifier(self)) }

    func fullPath(_ root: String) -> String {
        var parts: [String] = []
        var node: XNode? = self
        while let n = node, n.parent != nil { parts.append(n.name); node = n.parent }
        if parts.isEmpty { return root }
        return (root == "/" ? "" : root) + "/" + parts.reversed().joined(separator: "/")
    }
}

struct XFile {
    let name: String
    let folder: XNode
    let size: Int64
    let modified: Date?
}

struct XType {
    let ext: String
    var size: Int64
    var count: Int64
}

struct XResult {
    let path: String
    let root: XNode
    var largest: [XFile]
    var types: [XType]
    var files: Int64
    var bytes: Int64
    var folders: Int
    let denied: Int
    let elapsed: TimeInterval
    let cancelled: Bool
}

/// Recorre un árbol de carpetas en paralelo sin leer el contenido de los archivos. Mide tamaño lógico. No sigue
/// enlaces simbólicos ni entra a otros volúmenes montados (evita ciclos y contar dos veces).
final class XpaceScanner: @unchecked Sendable {
    private let lock = NSLock()
    private var _files: Int64 = 0, _bytes: Int64 = 0, _folders = 0, _denied = 0
    private var _current = ""
    private var _cancelled = false
    private var types: [String: XType] = [:]
    private var big: [XFile] = []
    private var threshold: Int64 = 0

    var files: Int64 { lock.withLock { _files } }
    var bytes: Int64 { lock.withLock { _bytes } }
    var folders: Int { lock.withLock { _folders } }
    var current: String { lock.withLock { _current } }
    func cancel() { lock.withLock { _cancelled = true } }
    private var isCancelled: Bool { lock.withLock { _cancelled } }

    /// Carpetas que no se recorren desde la raíz: otros volúmenes y el espejo de datos del sistema.
    private static let skipped: Set<String> = ["/Volumes", "/System/Volumes", "/dev", "/private/var/vm", "/.vol", "/cores", "/Network"]

    func scan(_ path: String) -> XResult {
        let started = Date()
        let root = path.count > 1 && path.hasSuffix("/") ? String(path.dropLast()) : path
        let top = XNode(name: root)
        let group = DispatchGroup()
        let gate = DispatchSemaphore(value: max(4, ProcessInfo.processInfo.activeProcessorCount * 2))
        let queue = DispatchQueue(label: "lazo.xpace", attributes: .concurrent)

        func spawn(_ node: XNode) {
            group.enter()
            queue.async {
                gate.wait()
                if !self.isCancelled { self.process(node, root, spawn) }
                gate.signal()
                group.leave()
            }
        }
        spawn(top)
        group.wait()

        total(top)
        let bigSorted = lock.withLock { Array(big.sorted { $0.size > $1.size }.prefix(300)) }
        let typeList = lock.withLock { types.values.sorted { $0.size > $1.size } }
        return XResult(path: root, root: top, largest: bigSorted, types: typeList, files: top.files, bytes: top.size,
                       folders: top.folders, denied: lock.withLock { _denied }, elapsed: Date().timeIntervalSince(started), cancelled: isCancelled)
    }

    private func process(_ node: XNode, _ rootPath: String, _ spawn: (XNode) -> Void) {
        let folder = node.fullPath(rootPath)
        lock.withLock { _current = folder }
        let keys: [URLResourceKey] = [.isDirectoryKey, .isSymbolicLinkKey, .fileSizeKey, .contentModificationDateKey, .isPackageKey]
        let url = URL(fileURLWithPath: folder, isDirectory: true)
        guard let entries = try? FileManager.default.contentsOfDirectory(at: url, includingPropertiesForKeys: keys, options: []) else {
            node.denied = true
            lock.withLock { _denied += 1 }
            return
        }
        var localTypes: [String: (Int64, Int64)] = [:]
        var localBig: [XFile] = []
        var files: Int64 = 0, bytes: Int64 = 0, folders = 0
        let floor = lock.withLock { threshold }
        for entry in entries {
            let values = try? entry.resourceValues(forKeys: Set(keys))
            let name = entry.lastPathComponent
            let date = values?.contentModificationDate
            if values?.isSymbolicLink == true { continue }
            if values?.isDirectory == true {
                let path = folder == "/" ? "/" + name : folder + "/" + name
                if Self.skipped.contains(path) { continue }
                let child = XNode(name: name, parent: node)
                child.modified = date
                node.children.append(child)
                folders += 1
                spawn(child)
            } else {
                let size = Int64(values?.fileSize ?? 0)
                node.ownSize += size
                node.ownFiles += 1
                if let date, date > (node.modified ?? .distantPast) { node.modified = date }
                files += 1
                bytes += size
                var ext = (name as NSString).pathExtension.lowercased()
                if ext.isEmpty { ext = "(sin extensión)" } else { ext = "." + ext }
                let old = localTypes[ext] ?? (0, 0)
                localTypes[ext] = (old.0 + size, old.1 + 1)
                if size > floor { localBig.append(XFile(name: name, folder: node, size: size, modified: date)) }
            }
        }
        lock.withLock {
            _files += files; _bytes += bytes; _folders += folders
            for (ext, value) in localTypes {
                var type = types[ext] ?? XType(ext: ext, size: 0, count: 0)
                type.size += value.0
                type.count += value.1
                types[ext] = type
            }
            big.append(contentsOf: localBig)
            if big.count > 4000 {
                big = Array(big.sorted { $0.size > $1.size }.prefix(1000))
                threshold = big.last?.size ?? 0
            }
        }
    }

    /// Suma de abajo hacia arriba y ordena cada carpeta por tamaño (sin recursión: hay árboles muy hondos).
    private func total(_ top: XNode) {
        var order: [XNode] = []
        var stack = [top]
        while let n = stack.popLast() { order.append(n); stack.append(contentsOf: n.children) }
        for n in order.reversed() {
            n.size += n.ownSize
            n.files += n.ownFiles
            if n.children.count > 1 { n.children.sort { $0.size > $1.size } }
            if let parent = n.parent {
                parent.size += n.size
                parent.files += n.files
                parent.folders += n.folders + 1
                if let date = n.modified, date > (parent.modified ?? .distantPast) { parent.modified = date }
            }
        }
    }
}
