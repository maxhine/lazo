import AppKit
import Carbon.HIToolbox
import CoreGraphics
import LazoCore

/// Permisos que quien comparte concede a cada persona que mira (igual que Windows).
enum SharePermission {
    static let draw: UInt8 = 1
    static let control: UInt8 = 2
    /// Junto con `control`: control total del equipo. Sin él, solo la ventana activa.
    static let fullControl: UInt8 = 4
    static let release: UInt8 = 0x80
}

/// Evento de entrada `I`: coordenadas 0–65535 sobre lo compartido; teclas como código virtual de Windows.
struct RemoteInputEvent {
    static let move: UInt8 = 1, down: UInt8 = 2, up: UInt8 = 3, wheel: UInt8 = 4, keyDown: UInt8 = 5, keyUp: UInt8 = 6
    var type: UInt8
    var button: UInt8 = 0
    var x: UInt16 = 0, y: UInt16 = 0
    var delta: Int16 = 0
    var key: UInt16 = 0

    func bytes() -> Data {
        var out = BinaryOut()
        out.u8(UInt8(ascii: "I"))
        out.u8(type)
        switch type {
        case Self.move: out.u16(x); out.u16(y)
        case Self.down, Self.up: out.u8(button); out.u16(x); out.u16(y)
        case Self.wheel: out.i16(delta); out.u16(x); out.u16(y)
        default: out.u16(key)
        }
        return out.data
    }

    static func read(_ input: BinaryIn) throws -> RemoteInputEvent? {
        var e = RemoteInputEvent(type: try input.u8())
        switch e.type {
        case move: e.x = try input.u16(); e.y = try input.u16()
        case down, up: e.button = try input.u8(); e.x = try input.u16(); e.y = try input.u16()
        case wheel: e.delta = try input.i16(); e.x = try input.u16(); e.y = try input.u16()
        case keyDown, keyUp: e.key = try input.u16()
        default: return nil
        }
        return e
    }
}

/// Correspondencia entre teclas de Mac (kVK_*) y códigos virtuales de Windows (VK_*).
enum KeyMap {
    static let macToVK: [UInt16: UInt16] = [
        0: 0x41, 1: 0x53, 2: 0x44, 3: 0x46, 4: 0x48, 5: 0x47, 6: 0x5A, 7: 0x58, 8: 0x43, 9: 0x56, 10: 0xE2, 11: 0x42, 12: 0x51,
        13: 0x57, 14: 0x45, 15: 0x52, 16: 0x59, 17: 0x54, 18: 0x31, 19: 0x32, 20: 0x33, 21: 0x34, 22: 0x36, 23: 0x35, 24: 0xBB,
        25: 0x39, 26: 0x37, 27: 0xBD, 28: 0x38, 29: 0x30, 30: 0xDD, 31: 0x4F, 32: 0x55, 33: 0xDB, 34: 0x49, 35: 0x50, 36: 0x0D,
        37: 0x4C, 38: 0x4A, 39: 0xDE, 40: 0x4B, 41: 0xBA, 42: 0xDC, 43: 0xBC, 44: 0xBF, 45: 0x4E, 46: 0x4D, 47: 0xBE, 48: 0x09,
        49: 0x20, 50: 0xC0, 51: 0x08, 53: 0x1B, 65: 0x6E, 67: 0x6A, 69: 0x6B, 71: 0x0C, 75: 0x6F, 76: 0x0D, 78: 0x6D,
        82: 0x60, 83: 0x61, 84: 0x62, 85: 0x63, 86: 0x64, 87: 0x65, 88: 0x66, 89: 0x67, 91: 0x68, 92: 0x69,
        122: 0x70, 120: 0x71, 99: 0x72, 118: 0x73, 96: 0x74, 97: 0x75, 98: 0x76, 100: 0x77, 101: 0x78, 109: 0x79, 103: 0x7A, 111: 0x7B,
        114: 0x2D, 115: 0x24, 116: 0x21, 117: 0x2E, 119: 0x23, 121: 0x22, 123: 0x25, 124: 0x27, 125: 0x28, 126: 0x26,
        // Modificadores: ⌘ y ⌃ como Ctrl, ⌥ como Alt.
        55: 0x11, 54: 0x11, 59: 0x11, 62: 0x11, 56: 0x10, 60: 0x10, 58: 0x12, 61: 0x12, 57: 0x14,
    ]

    static let vkToMac: [UInt16: UInt16] = {
        var map: [UInt16: UInt16] = [:]
        for (mac, vk) in macToVK where map[vk] == nil && ![54, 59, 62, 60, 61, 76].contains(mac) { map[vk] = mac }
        map[0x11] = 55; map[0xA2] = 55; map[0xA3] = 54
        map[0x10] = 56; map[0xA0] = 56; map[0xA1] = 60
        map[0x12] = 58; map[0xA4] = 58; map[0xA5] = 61
        map[0x5B] = 59; map[0x5C] = 62
        return map
    }()

    static func isModifier(_ mac: UInt16) -> Bool { [54, 55, 56, 57, 58, 59, 60, 61, 62].contains(mac) }
}

/// Reproduce en este Mac el mouse y el teclado de quien tiene el control, limitado a lo compartido.
/// «Ventana activa»: solo la app compartida (o la que estaba al frente). «Control total»: sin límites de teclado.
/// Necesita el permiso de Accesibilidad. Los eventos llevan una marca y el atajo de Lazo los ignora.
final class RemoteInputInjector {
    private let source: ShareSource
    private let lock = NSLock()
    private var keys = Set<UInt16>()
    private var buttons = Set<UInt8>()
    private var full = false
    private var targetPid: pid_t = 0
    private var lastClick = (time: Date.distantPast, button: UInt8(0), count: Int64(0), point: CGPoint.zero)
    private let eventSource = CGEventSource(stateID: .hidSystemState)

    init(source: ShareSource) { self.source = source }

    func configure(full: Bool) {
        lock.lock(); defer { lock.unlock() }
        self.full = full
        targetPid = full ? 0 : pickTarget()
    }

    var targetName: String? {
        lock.lock(); defer { lock.unlock() }
        guard !full, targetPid != 0 else { return nil }
        return NSRunningApplication(processIdentifier: targetPid)?.localizedName
    }

    private func pickTarget() -> pid_t {
        if let window = source.windowID { return ScreenSources.ownerPid(window) ?? 0 }
        let me = ProcessInfo.processInfo.processIdentifier
        if let front = NSWorkspace.shared.frontmostApplication, front.processIdentifier != me { return front.processIdentifier }
        // Lo que está al frente es Lazo (el aviso): se toma la primera ventana debajo, en esa pantalla.
        for info in ScreenSources.onScreenWindows() where info.pid != me && info.bounds.intersects(source.frame) { return info.pid }
        return 0
    }

    func apply(_ e: RemoteInputEvent) {
        lock.lock(); defer { lock.unlock() }
        switch e.type {
        case RemoteInputEvent.move:
            guard let p = point(e.x, e.y) else { return }
            post(mouse(buttons.contains(1) ? .leftMouseDragged : buttons.contains(2) ? .rightMouseDragged : buttons.contains(3) ? .otherMouseDragged : .mouseMoved, p, button(buttons.first ?? 1)))
        case RemoteInputEvent.down, RemoteInputEvent.up:
            guard (1...3).contains(e.button), let p = point(e.x, e.y) else { return }
            let isDown = e.type == RemoteInputEvent.down
            if isDown {
                if !full && (!allowed(p) || !targetInFront()) { return }
                buttons.insert(e.button)
                let now = Date()
                let same = lastClick.button == e.button && now.timeIntervalSince(lastClick.time) < NSEvent.doubleClickInterval && hypot(p.x - lastClick.point.x, p.y - lastClick.point.y) < 5
                lastClick = (now, e.button, same ? lastClick.count + 1 : 1, p)
            } else if buttons.remove(e.button) == nil { return }
            let type: CGEventType = e.button == 1 ? (isDown ? .leftMouseDown : .leftMouseUp) : e.button == 2 ? (isDown ? .rightMouseDown : .rightMouseUp) : (isDown ? .otherMouseDown : .otherMouseUp)
            let event = mouse(type, p, button(e.button))
            event?.setIntegerValueField(.mouseEventClickState, value: lastClick.count)
            post(event)
        case RemoteInputEvent.wheel:
            guard let p = point(e.x, e.y), full || allowed(p) else { return }
            post(mouse(.mouseMoved, p, .left))
            let lines = Int32((Double(e.delta) / 40).rounded())
            post(CGEvent(scrollWheelEvent2Source: eventSource, units: .line, wheelCount: 1, wheel1: lines, wheel2: 0, wheel3: 0))
        case RemoteInputEvent.keyDown:
            guard e.key > 0, e.key <= 0xFE, let mac = KeyMap.vkToMac[e.key] else { return }
            if !full && (blocked(e.key) || !targetInFront()) { return }
            keys.insert(e.key)
            key(mac, down: true)
        case RemoteInputEvent.keyUp:
            guard keys.remove(e.key) != nil, let mac = KeyMap.vkToMac[e.key] else { return }
            key(mac, down: false)
        default: return
        }
    }

    /// Suelta todo lo que quien controlaba dejó pulsado.
    func releaseAll() {
        lock.lock(); defer { lock.unlock() }
        for vk in keys { if let mac = KeyMap.vkToMac[vk] { key(mac, down: false) } }
        keys.removeAll()
        let p = CGEvent(source: nil)?.location ?? .zero
        for b in buttons { post(mouse(b == 1 ? .leftMouseUp : b == 2 ? .rightMouseUp : .otherMouseUp, p, button(b))) }
        buttons.removeAll()
    }

    // MARK: Límites de «Ventana activa»

    private func allowed(_ p: CGPoint) -> Bool {
        guard targetPid != 0 else { return false }
        let me = ProcessInfo.processInfo.processIdentifier
        for info in ScreenSources.onScreenWindows() where info.pid != me && info.bounds.contains(p) { return info.pid == targetPid }
        return false
    }

    private func targetInFront() -> Bool {
        guard targetPid != 0, let app = NSRunningApplication(processIdentifier: targetPid) else { return false }
        if NSWorkspace.shared.frontmostApplication?.processIdentifier == targetPid { return true }
        app.activate()
        Thread.sleep(forTimeInterval: 0.05)
        return NSWorkspace.shared.frontmostApplication?.processIdentifier == targetPid
    }

    /// Combinaciones que sacarían el control de la ventana activa.
    private func blocked(_ vk: UInt16) -> Bool {
        if vk == 0x5B || vk == 0x5C { return true }
        let alt = keys.contains(0x12) || keys.contains(0xA4) || keys.contains(0xA5)
        let ctrl = keys.contains(0x11) || keys.contains(0xA2) || keys.contains(0xA3)
        if vk == 0x09 && alt { return true }
        if vk == 0x1B && (alt || ctrl) { return true }
        if vk == 0x20 && ctrl { return true }  // ⌘Espacio
        return false
    }

    // MARK: Eventos

    private func point(_ nx: UInt16, _ ny: UInt16) -> CGPoint? {
        guard let frame = source.currentFrame() else { return nil }
        return CGPoint(x: frame.minX + Double(nx) / 65535 * (frame.width - 1), y: frame.minY + Double(ny) / 65535 * (frame.height - 1))
    }

    private func button(_ b: UInt8) -> CGMouseButton { b == 2 ? .right : b == 3 ? .center : .left }

    private func mouse(_ type: CGEventType, _ p: CGPoint, _ button: CGMouseButton) -> CGEvent? {
        CGEvent(mouseEventSource: eventSource, mouseType: type, mouseCursorPosition: p, mouseButton: button)
    }

    private var flags: CGEventFlags {
        var f: CGEventFlags = []
        let tab = keys.contains(0x09)
        if keys.contains(0x11) || keys.contains(0xA2) || keys.contains(0xA3) { f.insert(tab ? .maskControl : .maskCommand) }
        if keys.contains(0x5B) || keys.contains(0x5C) { f.insert(.maskControl) }
        if keys.contains(0x10) || keys.contains(0xA0) || keys.contains(0xA1) { f.insert(.maskShift) }
        if keys.contains(0x12) || keys.contains(0xA4) || keys.contains(0xA5) { f.insert(.maskAlternate) }
        return f
    }

    private func key(_ mac: UInt16, down: Bool) {
        guard let event = CGEvent(keyboardEventSource: eventSource, virtualKey: CGKeyCode(mac), keyDown: down) else { return }
        event.flags = flags
        post(event)
    }

    private func post(_ event: CGEvent?) {
        guard let event else { return }
        event.setIntegerValueField(.eventSourceUserData, value: injectedMarker)
        event.post(tap: .cghidEventTap)
    }
}
