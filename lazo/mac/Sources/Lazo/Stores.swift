import AppKit
import CoreGraphics
import ImageIO
import LazoCore
import ServiceManagement
import SwiftUI
import UniformTypeIdentifiers

enum Paths {
    static var support: URL { NetworkEngine.defaultProfile }
    static func file(_ name: String) -> URL { support.appendingPathComponent(name) }
}

/// Preferencias de la persona. Se guardan en UserDefaults salvo foto, fondo y chat (archivos).
final class Prefs: ObservableObject {
    static let shared = Prefs()
    /// Las capturas de prueba (`--preview-snapshots`) usan un dominio aparte para no tocar las preferencias reales.
    private let d: UserDefaults = CommandLine.arguments.contains("--preview-snapshots") ? UserDefaults(suiteName: "com.jhonandrew.lazo.preview")! : .standard

    @Published var name: String { didSet { d.set(name, forKey: "name") } }
    @Published var interface: InterfaceKind { didSet { d.set(interface.rawValue, forKey: "interface") } }
    @Published var theme: ThemeKind { didSet { d.set(theme.rawValue, forKey: "theme") } }
    /// Acento #RRGGBB, vacío para el del tema o "image" para sacarlo de la imagen de inicio.
    @Published var accent: String { didSet { d.set(accent, forKey: "accent") } }
    @Published var liquidGlass: Bool { didSet { d.set(liquidGlass, forKey: "glass") } }
    @Published var updates: Bool { didSet { d.set(updates, forKey: "updates") } }
    /// Al controlar otro equipo, ajustar su resolución al tamaño de la ventana (si ese equipo lo permite).
    @Published var autoFit: Bool { didSet { d.set(autoFit, forKey: "autoFit") } }
    /// Ocultar la ventana al hacer clic fuera de ella (por defecto sí). Apagado, solo se cierra con Esc, × o el atajo.
    @Published var closeOnClickOutside: Bool { didSet { d.set(closeOnClickOutside, forKey: "closeOutside") } }
    @Published var chatBanner: Bool { didSet { d.set(chatBanner, forKey: "chatBanner") } }
    @Published var photo: String = ""
    @Published var heroVersion = 0

    private init() {
        name = d.string(forKey: "name") ?? ""
        interface = InterfaceKind(rawValue: d.string(forKey: "interface") ?? "") ?? .minimal
        theme = ThemeKind(rawValue: d.string(forKey: "theme") ?? "") ?? .dark
        accent = d.string(forKey: "accent") ?? ""
        liquidGlass = d.object(forKey: "glass") as? Bool ?? false
        updates = d.object(forKey: "updates") as? Bool ?? true
        autoFit = d.object(forKey: "autoFit") as? Bool ?? true
        closeOnClickOutside = d.object(forKey: "closeOutside") as? Bool ?? true
        chatBanner = d.object(forKey: "chatBanner") as? Bool ?? false
        if let data = try? Data(contentsOf: Paths.file("profile.jpg")), data.count <= 12000 { photo = data.base64EncodedString() }
    }

    var displayName: String {
        let custom = Labels.clean(name)
        if !custom.isEmpty { return custom }
        let full = Labels.clean(NSFullUserName())
        return full.isEmpty ? Labels.clean(NSUserName()) : full
    }

    var palette: Palette {
        switch theme {
        case .wine: return .wine
        case .flat: return .flat
        case .glass: return .glass
        case .dark: return .darkP
        case .warm: return .warm
        case .meet: return .meet
        case .image: return ImageTheme.palette
        }
    }

    var accentColor: Color {
        if accent == "image" { return ImageTheme.palette.accent }
        if accent.hasPrefix("#") { return Color(hex: accent) }
        return palette.accent
    }

    // MARK: Foto de perfil: recorte central, 96 × 96, gris, JPEG ≤ 12 000 bytes.

    func setPhoto(from url: URL?) throws {
        guard let url else {
            try? FileManager.default.removeItem(at: Paths.file("profile.jpg"))
            photo = ""
            return
        }
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
              let image = CGImageSourceCreateImageAtIndex(source, 0, [kCGImageSourceShouldAllowFloat: false] as CFDictionary) else {
            throw NSError(domain: "Lazo", code: 1, userInfo: [NSLocalizedDescriptionKey: "No se pudo leer la imagen."])
        }
        let side = min(image.width, image.height)
        let crop = image.cropping(to: CGRect(x: (image.width - side) / 2, y: (image.height - side) / 2, width: side, height: side)) ?? image
        guard let context = CGContext(data: nil, width: 96, height: 96, bitsPerComponent: 8, bytesPerRow: 96,
                                      space: CGColorSpaceCreateDeviceGray(), bitmapInfo: CGImageAlphaInfo.none.rawValue) else { return }
        context.interpolationQuality = .high
        context.draw(crop, in: CGRect(x: 0, y: 0, width: 96, height: 96))
        guard let gray = context.makeImage(), let jpeg = ImagePreview.encode(gray, quality: 0.8), jpeg.count <= 12000 else {
            throw NSError(domain: "Lazo", code: 2, userInfo: [NSLocalizedDescriptionKey: "No se pudo reducir la imagen."])
        }
        try FileManager.default.createDirectory(at: Paths.support, withIntermediateDirectories: true)
        try jpeg.write(to: Paths.file("profile.jpg"))
        photo = jpeg.base64EncodedString()
    }

    // MARK: Fondo de inicio

    var heroURL: URL { Paths.file("hero.jpg") }
    var hasHero: Bool { FileManager.default.fileExists(atPath: heroURL.path) }

    func setHero(from url: URL?) {
        if let url {
            try? FileManager.default.createDirectory(at: Paths.support, withIntermediateDirectories: true)
            try? FileManager.default.removeItem(at: heroURL)
            try? FileManager.default.copyItem(at: url, to: heroURL)
        } else {
            try? FileManager.default.removeItem(at: heroURL)
        }
        ImageTheme.reload()
        heroVersion += 1
    }

    // MARK: Abrir al iniciar sesión

    var startsAtLogin: Bool {
        get { SMAppService.mainApp.status == .enabled }
        set {
            do {
                if newValue { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
            } catch { NSLog("Lazo: inicio de sesión: \(error.localizedDescription)") }
            objectWillChange.send()
        }
    }
}

/// Tema Imagen: colores sacados de la foto de inicio.
enum ImageTheme {
    private(set) static var palette: Palette = make(from: nil)

    static func reload() {
        palette = make(from: Prefs.shared.hasHero ? Prefs.shared.heroURL : nil)
    }

    static func make(from url: URL?) -> Palette {
        var base = NSColor(srgbRed: 0.25, green: 0.2, blue: 0.22, alpha: 1)
        if let url, let source = CGImageSourceCreateWithURL(url as CFURL, nil),
           let image = CGImageSourceCreateThumbnailAtIndex(source, 0, [kCGImageSourceCreateThumbnailFromImageAlways: true, kCGImageSourceThumbnailMaxPixelSize: 24] as CFDictionary),
           let context = CGContext(data: nil, width: 1, height: 1, bitsPerComponent: 8, bytesPerRow: 4, space: CGColorSpaceCreateDeviceRGB(),
                                   bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) {
            context.draw(image, in: CGRect(x: 0, y: 0, width: 1, height: 1))
            if let p = context.data?.assumingMemoryBound(to: UInt8.self) {
                base = NSColor(srgbRed: CGFloat(p[0]) / 255, green: CGFloat(p[1]) / 255, blue: CGFloat(p[2]) / 255, alpha: 1)
            }
        }
        func shade(_ k: CGFloat) -> Color { Color(nsColor: base.blended(withFraction: k, of: .black) ?? base) }
        func tint(_ k: CGFloat) -> Color { Color(nsColor: base.blended(withFraction: k, of: .white) ?? base) }
        var hue: CGFloat = 0, sat: CGFloat = 0, bri: CGFloat = 0, alpha: CGFloat = 0
        base.usingColorSpace(.sRGB)?.getHue(&hue, saturation: &sat, brightness: &bri, alpha: &alpha)
        let accent = Color(nsColor: NSColor(hue: hue, saturation: max(0.55, sat), brightness: 0.95, alpha: 1))
        return Palette(dark: true, shell0: shade(0.55), shell1: shade(0.62), shell2: shade(0.7), surface: shade(0.66), soft: shade(0.45),
                       track: shade(0.75), ink: Color(hex: "#FFF8F2"), muted: tint(0.55), line: Color(hex: "#24FFFFFF"),
                       chip: tint(0.85), chipText: shade(0.7), avatar: shade(0.4), online: Color(hex: "#7FD97A"), accent: accent)
    }
}

// MARK: - Chat (mismo formato que Windows: %AppData%\Lazo\chat)

struct ChatLine: Identifiable {
    let id = UUID()
    let when: Date
    let mine: Bool
    let text: String
    let filePath: String?
    let nudge: Bool
    /// Identificador del mensaje (vacío en los antiguos): permite borrarlo en los dos lados.
    var msgId = ""
    /// Quién lo escribió, en los grupos.
    var sender = ""
    /// Se eliminó para todos: queda el aviso, sin el contenido.
    var deleted = false
    /// Línea del archivo de la conversación (para borrar los mensajes antiguos sin identificador).
    var row = 0
}

struct ChatThread: Identifiable {
    let id: String
    var name: String
    var preview: String
    var unread: Int
    var when: Date
}

/// Mismo formato de archivo que Windows (`chat/<id>.txt`): `fecha|in/out|texto|idMensaje|autor`.
enum ChatStore {
    private static let lock = NSLock()
    private static var folder: URL { Paths.file("chat") }
    private static let format: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd HH:mm"
        f.locale = Locale(identifier: "en_US_POSIX")
        return f
    }()

    static func dataFolder() -> URL { folder }
    static func newId() -> String { String(UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased().prefix(8)) }

    private static func clean(_ text: String) -> String {
        text.replacingOccurrences(of: "|", with: " ").replacingOccurrences(of: "\r", with: " ")
            .replacingOccurrences(of: "\n", with: " ").trimmingCharacters(in: .whitespaces)
    }

    private static func threadURL(_ id: String) -> URL { folder.appendingPathComponent(id.replacingOccurrences(of: "-", with: "") + ".txt") }

    /// Guarda un mensaje y devuelve su identificador.
    @discardableResult
    static func append(_ id: String, name: String, mine: Bool, text raw: String, msgId: String? = nil, sender: String? = nil) -> String {
        let text = clean(raw)
        guard !text.isEmpty else { return "" }
        let messageId = (msgId?.isEmpty == false) ? clean(msgId!) : newId()
        lock.lock(); defer { lock.unlock() }
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let path = threadURL(id)
        let line = format.string(from: Date()) + "|" + (mine ? "out" : "in") + "|" + text + "|" + messageId + "|" + clean(sender ?? "") + "\n"
        if let handle = try? FileHandle(forWritingTo: path) {
            handle.seekToEndOfFile(); handle.write(Data(line.utf8)); try? handle.close()
        } else {
            try? line.write(to: path, atomically: true, encoding: .utf8)
        }
        var threads = loadIndex()
        var thread = threads.first { $0.id == id } ?? ChatThread(id: id, name: "Compañero", preview: "", unread: 0, when: Date())
        if !name.isEmpty { thread.name = clean(name) }
        thread.preview = previewOf(text)
        thread.when = Date()
        if !mine { thread.unread += 1 }
        threads.removeAll { $0.id == id }
        threads.append(thread)
        saveIndex(threads)
        return messageId
    }

    /// Mensaje de otra persona: si imita las marcas internas se neutraliza (no puede hacerse pasar por un archivo).
    static func appendIncoming(_ id: String, name: String, text: String, msgId: String? = nil, sender: String? = nil) {
        var value = clean(text)
        if value.hasPrefix("FILE") || value == "NUDGE" || value == "DELETED" { value = "\u{2063}" + value }
        append(id, name: name, mine: false, text: value, msgId: msgId, sender: sender)
    }

    static func appendFile(_ id: String, name: String, mine: Bool, path: String) {
        append(id, name: name, mine: mine, text: "FILE\t" + path.replacingOccurrences(of: "\t", with: " "))
    }

    static func appendNudge(_ id: String, name: String, mine: Bool) {
        append(id, name: name, mine: mine, text: "NUDGE")
    }

    /// Crea la conversación de un grupo (vacía) para que aparezca en la lista.
    static func ensureThread(_ id: String, name: String) {
        lock.lock(); defer { lock.unlock() }
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        var threads = loadIndex()
        var thread = threads.first { $0.id == id } ?? ChatThread(id: id, name: clean(name), preview: "", unread: 0, when: Date())
        thread.name = clean(name)
        threads.removeAll { $0.id == id }
        threads.append(thread)
        saveIndex(threads)
    }

    /// Quita un mensaje solo de este equipo.
    static func deleteLocal(_ thread: String, _ line: ChatLine) {
        lock.lock(); defer { lock.unlock() }
        guard var rows = rows(thread) else { return }
        guard let at = locate(rows, line) else { return }
        rows.remove(at: at)
        write(rows, thread)
        refreshPreview(thread, rows)
    }

    /// El mensaje se eliminó para todos: en su lugar queda un aviso. `fromOther`: lo pide otra persona, solo vale para lo suyo.
    @discardableResult
    static func markDeleted(_ thread: String, msgId: String, sender: String? = nil, fromOther: Bool = true) -> Bool {
        guard !msgId.isEmpty else { return false }
        lock.lock(); defer { lock.unlock() }
        guard var rows = rows(thread) else { return false }
        var changed = false
        for i in rows.indices {
            var parts = rows[i].split(separator: "|", maxSplits: 4, omittingEmptySubsequences: false).map(String.init)
            guard parts.count >= 4, parts[3] == msgId else { continue }
            if fromOther && parts[1] != "in" { continue }
            if let sender, parts.count < 5 || parts[4] != sender { continue }
            parts[2] = "DELETED"
            rows[i] = parts.joined(separator: "|")
            changed = true
        }
        if changed { write(rows, thread); refreshPreview(thread, rows) }
        return changed
    }

    /// Vacía una conversación. Las de personas desaparecen de la lista; los grupos se quedan.
    static func clear(_ thread: String) {
        lock.lock(); defer { lock.unlock() }
        try? FileManager.default.removeItem(at: threadURL(thread))
        var threads = loadIndex()
        guard let index = threads.firstIndex(where: { $0.id == thread }) else { return }
        if ChatGroups.get(thread) != nil { threads[index].preview = ""; threads[index].unread = 0 } else { threads.remove(at: index) }
        saveIndex(threads)
    }

    /// Quita una conversación y su historial (por ejemplo, al salir de un grupo).
    static func remove(_ thread: String) {
        lock.lock(); defer { lock.unlock() }
        try? FileManager.default.removeItem(at: threadURL(thread))
        var threads = loadIndex()
        threads.removeAll { $0.id == thread }
        saveIndex(threads)
    }

    private static func rows(_ thread: String) -> [String]? {
        guard let text = try? String(contentsOf: threadURL(thread), encoding: .utf8) else { return nil }
        return text.split(separator: "\n", omittingEmptySubsequences: true).map(String.init)
    }

    private static func write(_ rows: [String], _ thread: String) {
        let text = rows.isEmpty ? "" : rows.joined(separator: "\n") + "\n"
        try? text.write(to: threadURL(thread), atomically: true, encoding: .utf8)
    }

    private static func locate(_ rows: [String], _ line: ChatLine) -> Int? {
        if line.msgId.isEmpty { return line.row >= 0 && line.row < rows.count ? line.row : nil }
        return rows.firstIndex { row in
            let parts = row.split(separator: "|", maxSplits: 4, omittingEmptySubsequences: false)
            return parts.count >= 4 && parts[3] == line.msgId
        }
    }

    private static func refreshPreview(_ thread: String, _ rows: [String]) {
        var threads = loadIndex()
        guard let index = threads.firstIndex(where: { $0.id == thread }) else { return }
        if let last = rows.last {
            let parts = last.split(separator: "|", maxSplits: 4, omittingEmptySubsequences: false).map(String.init)
            threads[index].preview = parts.count >= 3 ? previewOf(parts[2]) : ""
        } else { threads[index].preview = "" }
        saveIndex(threads)
    }

    static func messages(_ id: String) -> [ChatLine] {
        guard let all = rows(id) else { return [] }
        let start = max(0, all.count - 200)
        var lines: [ChatLine] = []
        for index in start..<all.count {
            let parts = all[index].split(separator: "|", maxSplits: 4, omittingEmptySubsequences: false).map(String.init)
            guard parts.count >= 3, let when = format.date(from: parts[0]) else { continue }
            let mine = parts[1] == "out"
            var line: ChatLine
            let body = parts[2]
            if body == "DELETED" {
                line = ChatLine(when: when, mine: mine, text: mine ? "Eliminaste este mensaje" : "Se eliminó este mensaje", filePath: nil, nudge: false)
                line.deleted = true
            } else if body == "NUDGE" {
                line = ChatLine(when: when, mine: mine, text: "Zumbido", filePath: nil, nudge: true)
            } else if body.hasPrefix("FILE\t") {
                let path = String(body.dropFirst(5))
                line = ChatLine(when: when, mine: mine, text: (path as NSString).lastPathComponent, filePath: path, nudge: false)
            } else {
                line = ChatLine(when: when, mine: mine, text: body, filePath: nil, nudge: false)
            }
            line.row = index
            if parts.count > 3 { line.msgId = parts[3] }
            if parts.count > 4 { line.sender = parts[4] }
            lines.append(line)
        }
        return lines
    }

    static func threads() -> [ChatThread] {
        lock.lock(); defer { lock.unlock() }
        return loadIndex().sorted { $0.when > $1.when }
    }

    static func markRead(_ id: String) {
        lock.lock(); defer { lock.unlock() }
        var threads = loadIndex()
        guard let index = threads.firstIndex(where: { $0.id == id }), threads[index].unread > 0 else { return }
        threads[index].unread = 0
        saveIndex(threads)
    }

    static func unreadTotal() -> Int { threads().reduce(0) { $0 + $1.unread } }

    private static func loadIndex() -> [ChatThread] {
        guard let text = try? String(contentsOf: folder.appendingPathComponent("index.txt"), encoding: .utf8) else { return [] }
        return text.split(separator: "\n").compactMap { row in
            let p = row.split(separator: "|", omittingEmptySubsequences: false).map(String.init)
            guard p.count >= 5, let unread = Int(p[3]), let when = format.date(from: p[4]) else { return nil }
            return ChatThread(id: p[0].lowercased(), name: p[1], preview: p[2], unread: unread, when: when)
        }
    }

    private static func saveIndex(_ threads: [ChatThread]) {
        let text = threads.map { "\($0.id)|\(clean($0.name))|\(clean($0.preview))|\($0.unread)|\(format.string(from: $0.when))" }.joined(separator: "\n")
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try? text.write(to: folder.appendingPathComponent("index.txt"), atomically: true, encoding: .utf8)
    }

    private static func previewOf(_ text: String) -> String {
        if text.hasPrefix("FILE\t") { return "Archivo · " + (String(text.dropFirst(5)) as NSString).lastPathComponent }
        if text == "NUDGE" { return "Zumbido" }
        if text == "DELETED" { return "Mensaje eliminado" }
        return text.count > 42 ? String(text.prefix(42)) + "…" : text
    }
}

// MARK: - Historial de envíos y recepciones

struct TransferEntry: Identifiable {
    let id = UUID()
    let incoming: Bool
    let peer: String
    let name: String
    let path: String
    let when: Date
}

enum TransferLog {
    private static var path: URL { Paths.file("history.txt") }

    static func add(incoming: Bool, peer: String, name: String, path: String) {
        let line = "\(Int(Date().timeIntervalSince1970))|\(incoming ? "in" : "out")|\(Labels.clean(peer))|\(Labels.clean(name, max: 255))|\(path.replacingOccurrences(of: "|", with: " "))\n"
        try? FileManager.default.createDirectory(at: Paths.support, withIntermediateDirectories: true)
        if let handle = try? FileHandle(forWritingTo: self.path) {
            handle.seekToEndOfFile(); handle.write(Data(line.utf8)); try? handle.close()
        } else {
            try? line.write(to: self.path, atomically: true, encoding: .utf8)
        }
    }

    /// Borra el historial de envíos y recepciones (los archivos no se tocan).
    static func clear() { try? FileManager.default.removeItem(at: path) }

    static func recent(_ count: Int = 30) -> [TransferEntry] {
        guard let text = try? String(contentsOf: path, encoding: .utf8) else { return [] }
        return text.split(separator: "\n").suffix(count).reversed().compactMap { row in
            let p = row.split(separator: "|", maxSplits: 4, omittingEmptySubsequences: false).map(String.init)
            guard p.count == 5, let seconds = Double(p[0]) else { return nil }
            return TransferEntry(incoming: p[1] == "in", peer: p[2], name: p[3], path: p[4], when: Date(timeIntervalSince1970: seconds))
        }
    }
}
