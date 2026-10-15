import AppKit
import ImageIO
import LazoCore
import SwiftUI

/// Invitaciones para ver una pantalla: cada persona acepta antes de ver nada.
@MainActor
enum ScreenViewerController {
    static func offer(_ invite: ScreenInvite, answer: @escaping (ScreenSink?) -> Void) {
        var panel: FloatingPanel!
        var done = false
        func respond(_ accept: Bool) {
            guard !done else { return }
            done = true
            panel.fadeOut()
            if !accept { answer(nil); return }
            let window = ViewerWindow(sender: invite.sender, source: invite.source)
            window.show()
            answer(ViewerSink(window: window))
        }
        let view = Card {
            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 10) {
                    Avatar(name: invite.sender, photo: AppModel.shared.peer(invite.peerId)?.photo ?? "", size: 36)
                    VStack(alignment: .leading, spacing: 2) {
                        Text("\(invite.sender) quiere mostrarte su pantalla").font(.lazo(13, .semibold))
                        if !invite.source.isEmpty { Text(invite.source).font(.lazo(11.5)).opacity(0.7).lineLimit(1) }
                    }
                }
                HStack {
                    Spacer()
                    PillButton(title: "Rechazar") { respond(false) }
                    PillButton(title: "Ver", primary: true) { respond(true) }
                }
            }
        }
        panel = FloatingPanel(view, width: 360)
        FloatingPanel.Stack.add(panel)
        panel.fadeIn()
        NSSound(named: "Pop")?.play()
        DispatchQueue.main.asyncAfter(deadline: .now() + 55) { respond(false) }
    }
}

/// Lee la transmisión, decodifica los bloques en paralelo y los pinta en la ventana.
final class ViewerSink: ScreenSink {
    private let window: ViewerWindow
    init(window: ViewerWindow) { self.window = window }

    func run(_ stream: SecureStream) {
        let back = BackChannel(stream: stream)
        DispatchQueue.main.async { self.window.attach(back) { stream.close() } }
        back.send(Data([UInt8(ascii: "H"), ScreenShareSession.backChannelVersion]))
        var reason = "La transmisión terminó."
        let input = BinaryIn(stream)
        var width = 0, height = 0
        do {
            loop: while true {
                let type = try input.u8()
                switch type {
                case UInt8(ascii: "K"): continue
                case UInt8(ascii: "E"): break loop
                case UInt8(ascii: "H"):
                    _ = try input.u8()
                    DispatchQueue.main.async { self.window.backChannelReady() }
                case UInt8(ascii: "P"), UInt8(ascii: "D"):
                    let value = try input.u8()
                    let denied = type == UInt8(ascii: "D")
                    DispatchQueue.main.async { denied ? self.window.denied(value) : self.window.setGranted(value) }
                case UInt8(ascii: "A"):
                    let length = Int(try input.u16())
                    if let op = AnnotationOp.parse(try input.bytes(length)) { DispatchQueue.main.async { self.window.annotations.apply(op) } }
                case UInt8(ascii: "S"):
                    width = Int(try input.i32()); height = Int(try input.i32())
                    guard (1...8192).contains(width), (1...8192).contains(height) else { throw LazoError.invalid("Tamaño de pantalla no válido.") }
                    let w = width, h = height
                    DispatchQueue.main.sync { self.window.resize(w, h) }
                case UInt8(ascii: "F"):
                    let cx = try input.i32(), cy = try input.i32(), count = Int(try input.i32())
                    guard (0...40000).contains(count) else { throw LazoError.invalid("Fotograma no válido.") }
                    var tiles: [(x: Int, y: Int, w: Int, h: Int, jpeg: Data)] = []
                    for _ in 0..<count {
                        let x = Int(try input.u16()), y = Int(try input.u16()), w = Int(try input.u16()), h = Int(try input.u16())
                        let length = Int(try input.i32())
                        guard length >= 0, length <= 32 << 20, x + w <= width, y + h <= height, w > 0, h > 0 else { throw LazoError.invalid("Bloque fuera de rango.") }
                        tiles.append((x, y, w, h, try input.bytes(length)))
                    }
                    var images = [CGImage?](repeating: nil, count: tiles.count)
                    images.withUnsafeMutableBufferPointer { out in
                        DispatchQueue.concurrentPerform(iterations: tiles.count) { i in
                            guard let source = CGImageSourceCreateWithData(tiles[i].jpeg as CFData, nil),
                                  let image = CGImageSourceCreateImageAtIndex(source, 0, nil),
                                  image.width == tiles[i].w, image.height == tiles[i].h else { return }
                            out[i] = image
                        }
                    }
                    let patches = zip(tiles, images).compactMap { tile, image in image.map { (tile.x, tile.y, $0) } }
                    DispatchQueue.main.sync { self.window.apply(patches, cursor: (Int(cx), Int(cy)), changed: count > 0) }
                default:
                    throw LazoError.invalid("Paquete desconocido.")
                }
            }
        } catch {
            if case LazoError.closed = error { reason = "Se perdió la conexión." }
            else if case LazoError.timeout = error { reason = "Se perdió la conexión." }
            else { reason = "Error: " + error.localizedDescription }
        }
        back.close()
        DispatchQueue.main.async { self.window.ended(reason) }
    }
}

/// Envío de quien mira hacia quien comparte en su propio hilo. Nunca descarta nada salvo movimientos repetidos.
final class BackChannel {
    private let stream: SecureStream
    private let condition = NSCondition()
    private var queue: [Data] = []
    private var closed = false

    init(stream: SecureStream) {
        self.stream = stream
        Thread.detachNewThread { self.pump() }
    }

    private static func isMove(_ p: Data) -> Bool { p.count >= 2 && p[p.startIndex] == UInt8(ascii: "I") && p[p.startIndex + 1] == RemoteInputEvent.move }

    func send(_ packet: Data) {
        condition.lock()
        defer { condition.unlock() }
        guard !closed else { return }
        if Self.isMove(packet), let last = queue.last, Self.isMove(last) { queue[queue.count - 1] = packet } else { queue.append(packet) }
        condition.signal()
    }

    func close() {
        condition.lock()
        closed = true
        condition.signal()
        condition.unlock()
    }

    private func pump() {
        while true {
            condition.lock()
            while queue.isEmpty && !closed { condition.wait() }
            if queue.isEmpty && closed { condition.unlock(); return }
            let batch = queue
            queue.removeAll()
            condition.unlock()
            do {
                for packet in batch { try stream.write(packet) }
                try stream.flush()
            } catch { close(); return }
        }
    }
}

// MARK: - Ventana del visor

final class ViewerState: ObservableObject {
    @Published var status = "Conectando…"
    @Published var canAsk = false
    @Published var granted: UInt8 = 0
    @Published var asking: UInt8 = 0
    @Published var mode: ViewerWindow.Mode = .watch
    @Published var hint = ""
    @Published var ended = false
}

@MainActor
final class ViewerWindow: NSWindow, NSWindowDelegate {
    enum Mode { case watch, draw, control }
    let state = ViewerState()
    let annotations = AnnotationView()
    private let frameView = FrameView()
    private var back: BackChannel?
    private var onClose: (() -> Void)?
    private let sender: String
    private var frames = 0
    private var statStart = Date()
    private var hintTimer: Timer?

    init(sender: String, source: String) {
        self.sender = sender
        super.init(contentRect: NSRect(x: 0, y: 0, width: 1100, height: 700), styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
                   backing: .buffered, defer: false)
        title = "Pantalla de \(sender)" + (source.isEmpty ? "" : " · " + source)
        titlebarAppearsTransparent = true
        backgroundColor = NSColor(red: 0.08, green: 0.04, blue: 0.05, alpha: 1)
        isReleasedWhenClosed = false
        delegate = self
        collectionBehavior = [.fullScreenPrimary]
        let root = NSView()
        frameView.translatesAutoresizingMaskIntoConstraints = false
        root.addSubview(frameView)
        annotations.onlyMine = true
        annotations.local = { [weak self] op in self?.back?.send(ScreenShareSession.framed(UInt8(ascii: "A"), op.bytes())) }
        frameView.addSubview(annotations)
        frameView.annotations = annotations
        frameView.viewer = self
        let bar = NSHostingView(rootView: ViewerBar(state: state, window: self))
        bar.translatesAutoresizingMaskIntoConstraints = false
        root.addSubview(bar)
        NSLayoutConstraint.activate([
            frameView.leadingAnchor.constraint(equalTo: root.leadingAnchor), frameView.trailingAnchor.constraint(equalTo: root.trailingAnchor),
            frameView.topAnchor.constraint(equalTo: root.topAnchor), frameView.bottomAnchor.constraint(equalTo: root.bottomAnchor),
            bar.centerXAnchor.constraint(equalTo: root.centerXAnchor), bar.topAnchor.constraint(equalTo: root.topAnchor, constant: 30),
        ])
        contentView = root
        center()
    }

    func show() {
        NSApp.activate(ignoringOtherApps: true)
        makeKeyAndOrderFront(nil)
        makeFirstResponder(frameView)
    }

    func attach(_ back: BackChannel, onClose: @escaping () -> Void) {
        self.back = back
        self.onClose = onClose
    }

    func windowWillClose(_ notification: Notification) {
        releaseInput()
        onClose?()
        back?.close()
    }

    func windowDidResignKey(_ notification: Notification) { releaseInput() }

    func resize(_ w: Int, _ h: Int) {
        frameView.resize(w, h)
        state.status = "\(sender) · \(w) × \(h)"
        if !isZoomed, let screen = screen ?? NSScreen.main {
            let area = screen.visibleFrame
            let scale = min(1, area.width * 0.9 / CGFloat(w), area.height * 0.9 / CGFloat(h)) * (screen.backingScaleFactor > 1 ? 1 : 1)
            let size = NSSize(width: max(480, CGFloat(w) * scale / (screen.backingScaleFactor > 1 ? 1 : 1)), height: max(320, CGFloat(h) * scale))
            setContentSize(NSSize(width: min(size.width, area.width * 0.9), height: min(size.height, area.height * 0.9)))
            center()
        }
    }

    func apply(_ patches: [(Int, Int, CGImage)], cursor: (Int, Int), changed: Bool) {
        frameView.apply(patches, cursor: cursor)
        if changed { frames += 1 }
        if Date().timeIntervalSince(statStart) >= 1 {
            let fps = Double(frames) / Date().timeIntervalSince(statStart)
            frames = 0
            statStart = Date()
            state.status = "\(sender) · \(Int(fps.rounded())) fps"
        }
    }

    func ended(_ reason: String) {
        setMode(.watch)
        state.canAsk = false
        state.ended = true
        state.status = reason
        back = nil
    }

    // MARK: Permisos

    func backChannelReady() { if back != nil { state.canAsk = true } }

    func setGranted(_ granted: UInt8) {
        let gained = granted & ~state.granted
        state.granted = granted
        if state.mode == .draw && granted & SharePermission.draw == 0 { setMode(.watch); hint("Ya no puedes dibujar.") }
        if state.mode == .control && granted & SharePermission.control == 0 { setMode(.watch); hint("Ya no controlas el equipo.") }
        if gained & state.asking & SharePermission.control != 0 {
            setMode(.control)
            hint(granted & SharePermission.fullControl != 0 ? "Tienes el control total del equipo. Pulsa el mismo botón para soltarlo." : "Controlas la ventana activa. Pulsa el mismo botón para soltarlo.")
        } else if gained & state.asking & SharePermission.draw != 0 {
            setMode(.draw)
            hint("Ya puedes dibujar.")
        }
        state.asking &= ~gained
    }

    func denied(_ kind: UInt8) {
        state.asking &= ~kind
        hint(kind == SharePermission.control ? "No se concedió el control." : "No se concedió permiso para dibujar.")
    }

    func drawClicked() {
        if state.granted & SharePermission.draw != 0 { setMode(state.mode == .draw ? .watch : .draw); return }
        ask(SharePermission.draw, "Pidiendo permiso para dibujar…")
    }

    func controlClicked() {
        if state.granted & SharePermission.control != 0 {
            setMode(.watch)
            back?.send(Data([UInt8(ascii: "R"), SharePermission.control | SharePermission.release]))
            return
        }
        ask(SharePermission.control, "Pidiendo el control del equipo…")
    }

    private func ask(_ kind: UInt8, _ text: String) {
        guard let back, state.asking & kind == 0 else { return }
        state.asking |= kind
        back.send(Data([UInt8(ascii: "R"), kind]))
        hint(text)
    }

    func setMode(_ mode: Mode) {
        if state.mode == .control && mode != .control { releaseInput() }
        state.mode = mode
        annotations.drawing = mode == .draw
        frameView.controlling = mode == .control
        makeFirstResponder(mode == .draw ? annotations : frameView)
    }

    func hint(_ text: String) {
        state.hint = text
        hintTimer?.invalidate()
        hintTimer = Timer.scheduledTimer(withTimeInterval: 3.5, repeats: false) { [weak self] _ in Task { @MainActor in self?.state.hint = "" } }
    }

    // MARK: Control remoto

    var controlling: Bool { state.mode == .control && back != nil && frameView.hasFrame }
    func sendInput(_ event: RemoteInputEvent) { back?.send(event.bytes()) }
    private func releaseInput() { frameView.releaseAll() }
}

/// Barra superior del visor: estado, dibujar, controlar, pantalla completa y cerrar.
private struct ViewerBar: View {
    @ObservedObject var state: ViewerState
    let window: ViewerWindow

    var body: some View {
        VStack(spacing: 4) {
            HStack(spacing: 6) {
                Text(state.status).font(.lazo(12, .medium)).lineLimit(1).padding(.horizontal, 6)
                if state.canAsk {
                    barButton("pencil", state.granted & SharePermission.draw != 0 ? (state.mode == .draw ? "Dejar de dibujar" : "Dibujar") : "Dibujar sobre la pantalla (pide permiso)",
                              active: state.mode == .draw, waiting: state.asking & SharePermission.draw != 0) { window.drawClicked() }
                    barButton("cursorarrow.rays", state.granted & SharePermission.control != 0 ? "Soltar el control" : "Controlar el equipo (pide permiso)",
                              active: state.mode == .control, waiting: state.asking & SharePermission.control != 0) { window.controlClicked() }
                }
                barButton("arrow.up.left.and.arrow.down.right", "Pantalla completa", active: false, waiting: false) { window.toggleFullScreen(nil) }
                barButton("xmark", "Cerrar", active: false, waiting: false) { window.close() }
            }
            if state.mode == .draw {
                AnnotationToolbar(view: window.annotations, clearTip: "Borrar mis trazos") { window.annotations.eraseMine() }
            }
            if !state.hint.isEmpty {
                Text(state.hint).font(.lazo(12.5, .semibold)).padding(.horizontal, 12).padding(.vertical, 5)
                    .background(Capsule().fill(Color(hex: "#E62A0E12")))
            }
        }
        .foregroundStyle(.white)
        .padding(.horizontal, 10).padding(.vertical, 5)
        .background(RoundedRectangle(cornerRadius: 18).fill(Color(hex: "#CC2A0E12")))
    }

    private func barButton(_ symbol: String, _ tip: String, active: Bool, waiting: Bool, _ action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol).font(.system(size: 13, weight: .medium)).frame(width: 30, height: 30)
                .background(Circle().fill(Color.white.opacity(active ? 0.33 : 0)))
                .opacity(waiting ? 0.5 : 1)
        }.buttonStyle(.plain).help(tip)
    }
}

/// Pinta el fotograma (CGContext propio) ajustado a la ventana, con el cursor de quien comparte y la capa de trazos.
final class FrameView: NSView {
    private var context: CGContext?
    private var size = CGSize.zero
    private let cursorLayer = CALayer()
    weak var annotations: AnnotationView?
    weak var viewer: ViewerWindow?
    var controlling = false { didSet { window?.invalidateCursorRects(for: self) } }
    private var keysDown = Set<UInt16>()
    private var buttonsDown = Set<UInt8>()
    private var last = (x: UInt16.max, y: UInt16.max)

    var hasFrame: Bool { context != nil }
    override var acceptsFirstResponder: Bool { true }
    override var isFlipped: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        layer?.contentsGravity = .resizeAspect
        layer?.magnificationFilter = .linear
        cursorLayer.contents = NSCursor.arrow.image.cgImage(forProposedRect: nil, context: nil, hints: nil)
        cursorLayer.bounds = CGRect(origin: .zero, size: NSCursor.arrow.image.size)
        cursorLayer.anchorPoint = .zero
        cursorLayer.isHidden = true
        layer?.addSublayer(cursorLayer)
    }

    required init?(coder: NSCoder) { fatalError() }

    func resize(_ w: Int, _ h: Int) {
        size = CGSize(width: w, height: h)
        context = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4, space: CGColorSpaceCreateDeviceRGB(),
                            bitmapInfo: CGBitmapInfo.byteOrder32Little.rawValue | CGImageAlphaInfo.noneSkipFirst.rawValue)
        context?.setFillColor(CGColor(gray: 0.08, alpha: 1))
        context?.fill(CGRect(origin: .zero, size: size))
        needsLayout = true
    }

    func apply(_ patches: [(Int, Int, CGImage)], cursor: (Int, Int)) {
        guard let context else { return }
        for (x, y, image) in patches {
            context.draw(image, in: CGRect(x: x, y: Int(size.height) - y - image.height, width: image.width, height: image.height))
        }
        if !patches.isEmpty { layer?.contents = context.makeImage() }
        let rect = displayRect
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        if cursor.0 >= 0 && cursor.1 >= 0 && size.width > 0 {
            cursorLayer.isHidden = false
            let scale = rect.width / size.width
            let p = CGPoint(x: rect.minX + CGFloat(cursor.0) * scale, y: bounds.height - (rect.minY + CGFloat(cursor.1) * scale) - cursorLayer.bounds.height)
            cursorLayer.position = p
        } else { cursorLayer.isHidden = true }
        CATransaction.commit()
    }

    /// Rectángulo donde se ve el fotograma (ajustado sin deformar), en coordenadas de la vista (arriba a la izquierda).
    var displayRect: CGRect {
        guard size.width > 0, size.height > 0 else { return bounds }
        let scale = min(bounds.width / size.width, bounds.height / size.height)
        let w = size.width * scale, h = size.height * scale
        return CGRect(x: (bounds.width - w) / 2, y: (bounds.height - h) / 2, width: w, height: h)
    }

    override func layout() {
        super.layout()
        annotations?.frame = displayRect
    }

    override func resetCursorRects() {
        if controlling { addCursorRect(displayRect, cursor: .arrow) }
    }

    // MARK: Entrada en modo control

    private func normalized(_ event: NSEvent) -> (UInt16, UInt16, Bool) {
        let p = convert(event.locationInWindow, from: nil)
        let r = displayRect
        let inside = r.contains(p)
        return (AnnotationOp.norm((p.x - r.minX) / max(1, r.width)), AnnotationOp.norm((p.y - r.minY) / max(1, r.height)), inside)
    }

    private func button(_ event: NSEvent) -> UInt8 {
        switch event.type {
        case .leftMouseDown, .leftMouseUp: return event.modifierFlags.contains(.control) ? 2 : 1
        case .rightMouseDown, .rightMouseUp: return 2
        default: return event.buttonNumber == 2 ? 3 : 0
        }
    }

    private func down(_ event: NSEvent) {
        guard let viewer, viewer.controlling else { return }
        let (x, y, _) = normalized(event)
        let code = button(event)
        guard code != 0 else { return }
        buttonsDown.insert(code)
        viewer.sendInput(RemoteInputEvent(type: RemoteInputEvent.down, button: code, x: x, y: y))
    }

    private func up(_ event: NSEvent) {
        guard let viewer, viewer.controlling else { return }
        let (x, y, _) = normalized(event)
        let code = event.type == .leftMouseUp && !buttonsDown.contains(1) && buttonsDown.contains(2) ? 2 : button(event)
        guard buttonsDown.remove(code) != nil else { return }
        viewer.sendInput(RemoteInputEvent(type: RemoteInputEvent.up, button: code, x: x, y: y))
    }

    private func move(_ event: NSEvent) {
        guard let viewer, viewer.controlling else { return }
        let (x, y, inside) = normalized(event)
        guard inside || !buttonsDown.isEmpty, x != last.x || y != last.y else { return }
        last = (x, y)
        viewer.sendInput(RemoteInputEvent(type: RemoteInputEvent.move, x: x, y: y))
    }

    override func mouseDown(with event: NSEvent) {
        if event.clickCount == 2 && !(viewer?.controlling ?? false) { window?.toggleFullScreen(nil); return }
        down(event)
    }
    override func mouseUp(with event: NSEvent) { up(event) }
    override func rightMouseDown(with event: NSEvent) { down(event) }
    override func rightMouseUp(with event: NSEvent) { up(event) }
    override func otherMouseDown(with event: NSEvent) { down(event) }
    override func otherMouseUp(with event: NSEvent) { up(event) }
    override func mouseMoved(with event: NSEvent) { move(event) }
    override func mouseDragged(with event: NSEvent) { move(event) }
    override func rightMouseDragged(with event: NSEvent) { move(event) }
    override func otherMouseDragged(with event: NSEvent) { move(event) }

    override func scrollWheel(with event: NSEvent) {
        guard let viewer, viewer.controlling else { return }
        let (x, y, _) = normalized(event)
        let delta = event.hasPreciseScrollingDeltas ? event.scrollingDeltaY * 4 : event.scrollingDeltaY * 40
        viewer.sendInput(RemoteInputEvent(type: RemoteInputEvent.wheel, x: x, y: y, delta: Int16(max(-32000, min(32000, delta)))))
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: bounds, options: [.mouseMoved, .activeInKeyWindow, .inVisibleRect], owner: self))
    }

    override func keyDown(with event: NSEvent) { key(event, down: true) }
    override func keyUp(with event: NSEvent) { key(event, down: false) }

    override func flagsChanged(with event: NSEvent) {
        guard let viewer, viewer.controlling, let vk = KeyMap.macToVK[event.keyCode] else { return }
        let flag: NSEvent.ModifierFlags
        switch event.keyCode {
        case 54, 55: flag = .command
        case 56, 60: flag = .shift
        case 58, 61: flag = .option
        case 59, 62: flag = .control
        case 57: flag = .capsLock
        default: return
        }
        let isDown = event.modifierFlags.contains(flag)
        send(vk, down: isDown)
    }

    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        // Con el control, ⌘C y compañía van al otro equipo en lugar de a los menús de Lazo.
        guard let viewer, viewer.controlling, window?.firstResponder === self else { return super.performKeyEquivalent(with: event) }
        key(event, down: event.type == .keyDown)
        return true
    }

    private func key(_ event: NSEvent, down: Bool) {
        guard let viewer, viewer.controlling else {
            if down && event.keyCode == 53, window?.styleMask.contains(.fullScreen) == true { window?.toggleFullScreen(nil) }
            return
        }
        guard let vk = KeyMap.macToVK[event.keyCode] else { return }
        send(vk, down: down)
    }

    private func send(_ vk: UInt16, down: Bool) {
        if down { keysDown.insert(vk) } else if keysDown.remove(vk) == nil { return }
        viewer?.sendInput(RemoteInputEvent(type: down ? RemoteInputEvent.keyDown : RemoteInputEvent.keyUp, key: vk))
    }

    func releaseAll() {
        for vk in keysDown { viewer?.sendInput(RemoteInputEvent(type: RemoteInputEvent.keyUp, key: vk)) }
        keysDown.removeAll()
        for b in buttonsDown { viewer?.sendInput(RemoteInputEvent(type: RemoteInputEvent.up, button: b, x: last.x, y: last.y)) }
        buttonsDown.removeAll()
    }
}
