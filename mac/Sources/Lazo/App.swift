import AppKit
import ApplicationServices
import Carbon.HIToolbox
import LazoCore
import SwiftUI

@main
enum LazoMain {
    static func main() {
        let app = NSApplication.shared
        let args = CommandLine.arguments
        if let index = args.firstIndex(of: "--preview-snapshots"), index + 1 < args.count {
            app.setActivationPolicy(.prohibited)
            MainActor.assumeIsolated { Previews.run(URL(fileURLWithPath: args[index + 1])) }
            return
        }
        if args.contains("--self-test") { exit(SelfTest.run() ? 0 : 1) }
        let delegate = AppDelegate()
        app.delegate = delegate
        app.setActivationPolicy(.accessory)
        app.run()
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var status: NSStatusItem!
    private let hotkeys = Hotkeys()

    func applicationDidFinishLaunching(_ notification: Notification) {
        if NSRunningApplication.runningApplications(withBundleIdentifier: Bundle.main.bundleIdentifier ?? "").count > 1 {
            // Ya hay una copia abierta: se le pide que se muestre y esta se cierra.
            DistributedNotificationCenter.default().postNotificationName(.init("com.lazo.show"), object: nil, deliverImmediately: true)
            NSApp.terminate(nil)
            return
        }
        DistributedNotificationCenter.default().addObserver(forName: .init("com.lazo.show"), object: nil, queue: .main) { _ in
            Task { @MainActor in LauncherController.shared.show() }
        }
        ImageTheme.reload()
        setupMenu()
        LauncherController.shared.setup()
        AppModel.shared.start()
        EyeCare.shared.start()
        hotkeys.start { LauncherController.shared.toggle() }
        if Prefs.shared.updates { Updater.check(silentIfCurrent: true) { _ in } }
        LauncherController.shared.show()
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        LauncherController.shared.show()
        return true
    }

    private func setupMenu() {
        status = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        if let button = status.button {
            button.image = NSImage(systemSymbolName: "link", accessibilityDescription: "Lazo")
            button.action = #selector(statusClicked)
            button.target = self
            button.sendAction(on: [.leftMouseUp, .rightMouseUp])
        }
    }

    @objc private func statusClicked() {
        guard let event = NSApp.currentEvent else { return }
        if event.type == .rightMouseUp || event.modifierFlags.contains(.control) {
            let menu = NSMenu()
            menu.addItem(withTitle: "Abrir Lazo  (doble ⌥ o ⌃⌥L)", action: #selector(openLauncher), keyEquivalent: "").target = self
            menu.addItem(withTitle: "Chat", action: #selector(openChat), keyEquivalent: "").target = self
            menu.addItem(withTitle: "Abrir carpeta de recibidos", action: #selector(openInbox), keyEquivalent: "").target = self
            menu.addItem(.separator())
            menu.addItem(withTitle: "Salir de Lazo", action: #selector(quit), keyEquivalent: "q").target = self
            status.menu = menu
            status.button?.performClick(nil)
            status.menu = nil
        } else {
            LauncherController.shared.toggle()
        }
    }

    @objc private func openLauncher() { LauncherController.shared.show() }
    @objc private func openChat() { LauncherController.shared.openChat(nil) }
    @objc private func openInbox() {
        let folder = NetworkEngine.defaultReceiveDirectory
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        NSWorkspace.shared.open(folder)
    }
    @objc private func quit() {
        AppModel.shared.share?.stop()
        AppModel.shared.engine.stop()
        NSApp.terminate(nil)
    }
}

// MARK: - Permisos

enum Permissions {
    static var screenRecording: Bool { CGPreflightScreenCaptureAccess() }
    static func requestScreenRecording() {
        if !CGRequestScreenCaptureAccess() {
            NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture")!)
        }
    }

    static var accessibility: Bool { AXIsProcessTrusted() }
    static func requestAccessibility() {
        let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        if !AXIsProcessTrustedWithOptions(options) {
            NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")!)
        }
    }
}

// MARK: - Atajos: doble ⌥ izquierdo y ⌃⌥L

/// Valor que Lazo pone en los eventos que inyecta (control remoto); el atajo los ignora.
let injectedMarker: Int64 = 0x4C415A4F

final class Hotkeys {
    private var action: (() -> Void)?
    private var monitors: [Any] = []
    private var hotKey: EventHotKeyRef?
    private var optionDown = false
    private var otherKey = false
    private var downAt = Date.distantPast
    private var lastTap = Date.distantPast

    func start(_ action: @escaping () -> Void) {
        self.action = action
        // ⌃⌥L con Carbon: no necesita permisos.
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        let me = Unmanaged.passUnretained(self).toOpaque()
        InstallEventHandler(GetApplicationEventTarget(), { _, _, data in
            guard let data else { return noErr }
            let hotkeys = Unmanaged<Hotkeys>.fromOpaque(data).takeUnretainedValue()
            DispatchQueue.main.async { hotkeys.action?() }
            return noErr
        }, 1, &spec, me, nil)
        RegisterEventHotKey(UInt32(kVK_ANSI_L), UInt32(controlKey | optionKey), EventHotKeyID(signature: 0x4C415A4F, id: 1),
                            GetApplicationEventTarget(), 0, &hotKey)
        // Doble ⌥ izquierdo: requiere Accesibilidad para verse fuera de Lazo.
        let flags: (NSEvent) -> Void = { [weak self] in self?.flagsChanged($0) }
        let keys: (NSEvent) -> Void = { [weak self] _ in self?.otherKey = true }
        if let m = NSEvent.addGlobalMonitorForEvents(matching: .flagsChanged, handler: flags) { monitors.append(m) }
        if let m = NSEvent.addGlobalMonitorForEvents(matching: .keyDown, handler: keys) { monitors.append(m) }
        if let m = NSEvent.addLocalMonitorForEvents(matching: .flagsChanged, handler: { flags($0); return $0 }) { monitors.append(m) }
        if let m = NSEvent.addLocalMonitorForEvents(matching: .keyDown, handler: { keys($0); return $0 }) { monitors.append(m) }
    }

    private func flagsChanged(_ event: NSEvent) {
        if event.cgEvent?.getIntegerValueField(.eventSourceUserData) == injectedMarker { return }
        guard event.keyCode == UInt16(kVK_Option) else { if optionDown { otherKey = true }; return }
        let down = event.modifierFlags.contains(.option)
        if down && !optionDown {
            optionDown = true
            otherKey = false
            downAt = Date()
        } else if !down && optionDown {
            optionDown = false
            guard !otherKey, Date().timeIntervalSince(downAt) < 0.26 else { return }
            if Date().timeIntervalSince(lastTap) < 0.43 {
                lastTap = .distantPast
                action?()
            } else {
                lastTap = Date()
            }
        }
    }
}

// MARK: - Actualizaciones firmadas

enum Updater {
    static let repo = "maxhine/lazo"
    /// Misma clave pública que la versión de Windows (src/Updater.cs). La privada no está en el repositorio.
    static let releaseKey = "8a3ce980b63d00c676ebe200b094a90f5c8d78a4614bf7b99fc0560bd7e505325a9f09929f4adaaa7fdcfada9cbfcb929737b3595f69175aed94c0485367df89"

    static var currentVersion: String { Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.5.0" }

    /// Busca la última versión; si hay una nueva con firma válida, descarga la imagen de disco y la abre.
    @MainActor static func check(silentIfCurrent: Bool = false, report: @escaping (String) -> Void) {
        Task.detached {
            let result = await run()
            await MainActor.run {
                report(result)
                // En la comprobación automática solo se avisa si hay algo nuevo o una firma inválida.
                if silentIfCurrent && (result.hasPrefix("Hay") || result.hasPrefix("La ")) { AppModel.shared.say(result) }
            }
        }
    }

    private static func run() async -> String {
        do {
            var request = URLRequest(url: URL(string: "https://api.github.com/repos/\(repo)/releases/latest")!)
            request.setValue("Lazo", forHTTPHeaderField: "User-Agent")
            request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
            let (data, _) = try await URLSession.shared.data(for: request)
            guard let json = try JSONSerialization.jsonObject(with: data) as? [String: Any], let tag = json["tag_name"] as? String else { return "No se pudo comprobar." }
            let remote = tag.trimmingCharacters(in: CharacterSet(charactersIn: "v"))
            guard remote.compare(currentVersion, options: .numeric) == .orderedDescending else { return "Lazo está actualizado." }
            let assets = (json["assets"] as? [[String: Any]]) ?? []
            func asset(_ suffix: String) -> URL? {
                assets.compactMap { $0["browser_download_url"] as? String }.first { $0.hasSuffix(suffix) && $0.contains("Lazo-") }
                    .flatMap(URL.init(string:)).flatMap { allowed($0) ? $0 : nil }
            }
            guard let dmg = asset("-mac.dmg") else { return "Hay una versión \(remote), pero aún no para Mac." }
            guard let sig = asset("-mac.dmg.sig") else { return "La actualización no está firmada; no se instaló." }
            let folder = FileManager.default.temporaryDirectory.appendingPathComponent("Lazo-update-" + UUID().uuidString)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            let (downloaded, _) = try await URLSession.shared.download(from: dmg)
            let file = folder.appendingPathComponent(dmg.lastPathComponent)
            try FileManager.default.moveItem(at: downloaded, to: file)
            let (signature, _) = try await URLSession.shared.data(from: sig)
            guard LazoCrypto.verifyRelease(file: file, signatureBase64: String(decoding: signature, as: UTF8.self), publicKeyHex: releaseKey) else {
                try? FileManager.default.removeItem(at: file)
                return "La firma de la actualización no es válida; no se instaló."
            }
            await MainActor.run { _ = NSWorkspace.shared.open(file) }
            return "Hay una versión \(remote): arrastra Lazo a Aplicaciones para actualizar."
        } catch {
            return "No se pudo comprobar."
        }
    }

    private static func allowed(_ url: URL) -> Bool {
        guard url.scheme == "https", let host = url.host else { return false }
        return host == "github.com" || host.hasSuffix(".githubusercontent.com")
    }
}
