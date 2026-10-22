import AppKit
import Foundation

/// Borrar desde Xpace: comprueba que la ruta sea segura, pide confirmación y mueve a la papelera o elimina.
enum XpaceActions {
    enum Outcome { case trashed, deleted, cancelled }

    /// Rutas que nunca se tocan: raíces, el inicio, las carpetas principales del usuario y el sistema.
    static func protectionReason(_ path: String) -> String? {
        let url = URL(fileURLWithPath: path).standardizedFileURL
        let p = url.path
        let home = NSHomeDirectory()
        if p == "/" || p == home { return "Es una ubicación principal del Mac." }
        let blocked = ["/System", "/Library", "/usr", "/bin", "/sbin", "/private", "/etc", "/var", "/Applications", "/Users", "/Volumes", "/opt", "/cores"]
        if blocked.contains(p) { return "Es una carpeta del sistema." }
        if p.hasPrefix("/System/") || p.hasPrefix("/usr/") || p.hasPrefix("/bin/") || p.hasPrefix("/sbin/") { return "Pertenece al sistema de macOS." }
        if p.hasPrefix(home + "/") {
            let parts = p.dropFirst(home.count + 1).split(separator: "/")
            if parts.count <= 1 { return "Es una carpeta principal de tu usuario; elige algo dentro de ella." }
            if parts.count == 2, parts[0] == "Library" { return "Es una carpeta de datos de las apps; elige algo dentro de ella." }
            if parts.first == "Library", parts.count >= 2, ["Keychains", "Preferences", "Mobile Documents", "Accounts"].contains(String(parts[1])) {
                return "Contiene claves, preferencias o datos de iCloud."
            }
            return nil
        }
        if p.hasPrefix("/Volumes/") {
            return p.split(separator: "/").count <= 2 ? "Es un disco completo." : nil
        }
        return "Está fuera de tu carpeta de usuario y de los discos externos."
    }

    static func isSynced(_ path: String) -> Bool {
        path.contains("/Library/CloudStorage/") || path.contains("/Library/Mobile Documents/") || path.contains("/OneDrive") || path.contains("/Dropbox")
    }

    /// Pregunta qué hacer con un elemento. Devuelve `.cancelled` si no se confirma.
    @MainActor
    static func confirmAndRemove(path: String, size: Int64, isFolder: Bool) -> Outcome {
        if let reason = protectionReason(path) {
            let alert = NSAlert()
            alert.messageText = "Xpace no borra esto"
            alert.informativeText = "\((path as NSString).lastPathComponent): \(reason)"
            alert.addButton(withTitle: "Entendido")
            run(alert)
            return .cancelled
        }
        let name = (path as NSString).lastPathComponent
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = "¿Quitar «\(name)»?"
        var text = "\(isFolder ? "Carpeta" : "Archivo") de \(xpaceSize(size)).\n\(path)\n\nEn la papelera puedes recuperarlo, pero el espacio no se libera hasta que la vacíes."
        if isSynced(path) { text += "\n\nOjo: está en una carpeta sincronizada. Quitarlo también lo quita en la nube y en tus otros equipos." }
        alert.informativeText = text
        alert.addButton(withTitle: "Mover a la papelera")
        alert.addButton(withTitle: "Cancelar")
        let danger = alert.addButton(withTitle: "Eliminar definitivamente")
        danger.hasDestructiveAction = true
        switch run(alert) {
        case .alertFirstButtonReturn:
            do { try FileManager.default.trashItem(at: URL(fileURLWithPath: path), resultingItemURL: nil); return .trashed }
            catch { fail(error); return .cancelled }
        case .alertThirdButtonReturn:
            let sure = NSAlert()
            sure.alertStyle = .critical
            sure.messageText = "Eliminar «\(name)» para siempre"
            sure.informativeText = "No se podrá recuperar. Se liberan \(xpaceSize(size)) de inmediato."
            sure.addButton(withTitle: "Cancelar")
            sure.addButton(withTitle: "Eliminar").hasDestructiveAction = true
            guard run(sure) == .alertSecondButtonReturn else { return .cancelled }
            do { try FileManager.default.removeItem(atPath: path); return .deleted }
            catch { fail(error); return .cancelled }
        default:
            return .cancelled
        }
    }

    @discardableResult
    @MainActor private static func run(_ alert: NSAlert) -> NSApplication.ModalResponse {
        LauncherController.shared.holding += 1
        defer { LauncherController.shared.holding -= 1 }
        NSApp.activate(ignoringOtherApps: true)
        return alert.runModal()
    }

    @MainActor private static func fail(_ error: Error) {
        let alert = NSAlert()
        alert.messageText = "No se pudo quitar"
        alert.informativeText = error.localizedDescription + "\nSi es una carpeta protegida, dale a Lazo «Acceso total al disco» en Ajustes del Sistema › Privacidad."
        alert.addButton(withTitle: "Entendido")
        run(alert)
    }

    static func reveal(_ path: String) {
        guard FileManager.default.fileExists(atPath: path) else { return }
        var isDir: ObjCBool = false
        FileManager.default.fileExists(atPath: path, isDirectory: &isDir)
        if isDir.boolValue { NSWorkspace.shared.open(URL(fileURLWithPath: path)) }
        else { NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: path)]) }
    }
}

// MARK: - Sugerencias

struct XSuggestion: Identifiable {
    enum Risk { case safe, review }
    let path: String
    let title: String
    let why: String
    let risk: Risk
    let size: Int64
    let isFolder: Bool
    /// Solo informativo: no se borra desde Xpace (por ejemplo la papelera).
    var infoOnly = false
    var id: String { path }
}

enum XpaceSuggestions {
    private struct Spot { let rel: String; let title: String; let why: String; let risk: XSuggestion.Risk }

    private static let spots: [Spot] = [
        Spot(rel: "Library/Developer/Xcode/DerivedData", title: "Compilaciones de Xcode", why: "Xcode las vuelve a generar al compilar.", risk: .safe),
        Spot(rel: "Library/Developer/Xcode/iOS DeviceSupport", title: "Soporte de dispositivos iOS", why: "Se vuelve a descargar al conectar el iPhone.", risk: .safe),
        Spot(rel: "Library/Developer/CoreSimulator/Caches", title: "Caché del simulador de iOS", why: "Se regenera sola.", risk: .safe),
        Spot(rel: "Library/Developer/Xcode/Archives", title: "Archivos de Xcode (Archives)", why: "Versiones empaquetadas de apps; bórralos solo si ya no las necesitas.", risk: .review),
        Spot(rel: "Library/Logs", title: "Registros de apps", why: "Solo sirven para diagnosticar errores.", risk: .safe),
        Spot(rel: "Library/Application Support/MobileSync/Backup", title: "Copias de seguridad de iPhone/iPad", why: "Pueden ser enormes. Confirma que tienes otra copia (iCloud) antes de quitarlas.", risk: .review),
        Spot(rel: ".npm/_cacache", title: "Caché de npm", why: "npm la vuelve a llenar cuando instala paquetes.", risk: .safe),
        Spot(rel: ".gradle/caches", title: "Caché de Gradle", why: "Se vuelve a descargar al compilar.", risk: .safe),
        Spot(rel: ".cache", title: "Caché de herramientas (.cache)", why: "Las herramientas la recrean cuando hace falta.", risk: .safe),
    ]

    /// Ubicaciones conocidas que suelen ocupar espacio recuperable. Mide cada una; se omiten las pequeñas.
    static func known(minimum: Int64 = 50 << 20) -> [XSuggestion] {
        let home = NSHomeDirectory()
        let fm = FileManager.default
        var found: [XSuggestion] = []
        var candidates: [(path: String, title: String, why: String, risk: XSuggestion.Risk)] = []
        for spot in spots {
            let path = home + "/" + spot.rel
            if fm.fileExists(atPath: path) { candidates.append((path, spot.title, spot.why, spot.risk)) }
        }
        let caches = home + "/Library/Caches"
        for name in (try? fm.contentsOfDirectory(atPath: caches)) ?? [] {
            candidates.append((caches + "/" + name, "Caché de " + friendly(name), "Datos temporales de la app; ella los recrea.", .safe))
        }
        let lock = NSLock()
        DispatchQueue.concurrentPerform(iterations: candidates.count) { i in
            let c = candidates[i]
            let result = XpaceScanner().scan(c.path)
            guard result.bytes >= minimum else { return }
            lock.withLock { found.append(XSuggestion(path: c.path, title: c.title, why: c.why, risk: c.risk, size: result.bytes, isFolder: true)) }
        }
        let trash = home + "/.Trash"
        if let items = try? fm.contentsOfDirectory(atPath: trash), !items.isEmpty {
            let result = XpaceScanner().scan(trash)
            if result.bytes >= 1 << 20 {
                found.append(XSuggestion(path: trash, title: "Papelera", why: "Ya está «borrado» pero sigue ocupando espacio. Vacíala desde Finder (⇧⌘⌫).", risk: .safe, size: result.bytes, isFolder: true, infoOnly: true))
            }
        }
        return found.sorted { $0.size > $1.size }
    }

    private static func friendly(_ name: String) -> String {
        name.hasPrefix("com.") ? name.split(separator: ".").dropFirst().joined(separator: ".") : name
    }

    /// Sugerencias sacadas de lo analizado: carpetas regenerables, instaladores viejos y archivos enormes sin tocar.
    static func from(_ result: XResult, minimum: Int64 = 50 << 20) -> [XSuggestion] {
        var out: [XSuggestion] = []
        let regenerable: [String: (String, String, XSuggestion.Risk)] = [
            "node_modules": ("Dependencias de Node", "npm install las vuelve a crear.", .safe),
            "__pycache__": ("Caché de Python", "Python la recrea al ejecutar.", .safe),
            ".build": ("Compilación de Swift", "swift build la regenera.", .safe),
            "DerivedData": ("Compilaciones de Xcode", "Xcode las regenera.", .safe),
            "Pods": ("Dependencias de CocoaPods", "pod install las recrea.", .review),
            ".venv": ("Entorno virtual de Python", "Se puede recrear, pero revisa que tengas el requirements.", .review),
            "venv": ("Entorno virtual de Python", "Se puede recrear, pero revisa que tengas el requirements.", .review),
        ]
        var stack = [result.root]
        while let node = stack.popLast() {
            for child in node.children {
                if let rule = regenerable[child.name], child.size >= minimum {
                    out.append(XSuggestion(path: child.fullPath(result.path), title: rule.0 + " · " + node.name, why: rule.1, risk: rule.2, size: child.size, isFolder: true))
                } else { stack.append(child) }
            }
        }
        let year = Date().addingTimeInterval(-365 * 86_400)
        let installers: Set<String> = ["dmg", "pkg", "iso", "xip"]
        for file in result.largest {
            let folder = file.folder.fullPath(result.path)
            let path = (folder == "/" ? "" : folder) + "/" + file.name
            let ext = (file.name as NSString).pathExtension.lowercased()
            if installers.contains(ext), file.size >= minimum {
                out.append(XSuggestion(path: path, title: file.name, why: "Instalador o imagen de disco: si ya instalaste la app, no hace falta.", risk: .review, size: file.size, isFolder: false))
            } else if file.size >= 1 << 30, let date = file.modified, date < year {
                out.append(XSuggestion(path: path, title: file.name, why: "Más de 1 GB sin modificar desde \(date.formatted(.iso8601.year().month().day())).", risk: .review, size: file.size, isFolder: false))
            } else if ext == "zip", file.size >= 200 << 20, folder.contains("/Downloads") {
                out.append(XSuggestion(path: path, title: file.name, why: "Comprimido grande en Descargas; si ya lo extrajiste, sobra.", risk: .review, size: file.size, isFolder: false))
            }
        }
        // Lo que está dentro de otra sugerencia no se repite.
        let paths = out.map(\.path)
        out = out.filter { s in !paths.contains { $0 != s.path && s.path.hasPrefix($0 + "/") } }
        return out.sorted { $0.size > $1.size }
    }
}
