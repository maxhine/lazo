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
            let pane = ViewerPane(sender: invite.sender, source: invite.source)
            StageWindow.add(pane)
            answer(ViewerSink(pane: pane))
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
    private let window: ViewerPane
    init(pane: ViewerPane) { window = pane }

    func run(_ stream: SecureStream) {
        let back = BackChannel(stream: stream)
        DispatchQueue.main.async { self.window.attach(back) { stream.close() } }
        back.send(Data([UInt8(ascii: "H"), ScreenShareSession.backChannelVersion]))
        var reason = "La transmisión terminó."
        let input = BinaryIn(stream)
        var width = 0, height = 0
        var frameCount = 0
        DiagLog.write("visor: esperando la transmisión")
        do {
            loop: while true {
                let type = try input.u8()
                switch type {
                case UInt8(ascii: "K"): continue
                case UInt8(ascii: "E"): break loop
                case UInt8(ascii: "H"):
                    let version = try input.u8()
                    DispatchQueue.main.async { self.window.backChannelReady(version) }
                case UInt8(ascii: "M"):
                    let length = Int(try input.u16())
                    if let update = PointerUpdate.parse(try input.bytes(length)) { DispatchQueue.main.async { self.window.pointers.apply(update) } }
                case UInt8(ascii: "P"), UInt8(ascii: "D"):
                    let value = try input.u8()
                    let denied = type == UInt8(ascii: "D")
                    DispatchQueue.main.async { denied ? self.window.denied(value) : self.window.setGranted(value) }
                case UInt8(ascii: "A"):
                    let length = Int(try input.u16())
                    if let op = AnnotationOp.parse(try input.bytes(length)) { DispatchQueue.main.async { self.window.annotations.apply(op) } }
                case UInt8(ascii: "S"):
                    width = Int(try input.i32()); height = Int(try input.i32())
                    DiagLog.write("visor: tamaño de pantalla \(width)×\(height)")
                    guard (1...8192).contains(width), (1...8192).contains(height) else { throw LazoError.invalid("Tamaño de pantalla no válido.") }
                    let w = width, h = height
                    DispatchQueue.main.sync { self.window.resize(w, h) }
                case UInt8(ascii: "F"):
                    frameCount += 1
                    if frameCount == 1 { DiagLog.write("visor: primer paquete de imagen") }
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
            DiagLog.write("visor: terminó tras \(frameCount) paquetes de imagen: \(error.localizedDescription)")
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

    /// Movimientos que se pueden sustituir por el último: del mouse controlado o del puntero colaborativo.
    private static func isMove(_ p: Data) -> Bool {
        p.count >= 2 && ((p[p.startIndex] == UInt8(ascii: "I") && p[p.startIndex + 1] == RemoteInputEvent.move) || p[p.startIndex] == UInt8(ascii: "M"))
    }

    func send(_ packet: Data) {
        condition.lock()
        defer { condition.unlock() }
        guard !closed else { return }
        if Self.isMove(packet), let last = queue.last, Self.isMove(last), last[last.startIndex] == packet[packet.startIndex] { queue[queue.count - 1] = packet } else { queue.append(packet) }
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
    /// El emisor entiende «ajustar resolución» (canal de retorno v3) y estoy controlando.
    @Published var canFit = false
    @Published var fit = false
    @Published var granted: UInt8 = 0
    @Published var asking: UInt8 = 0
    @Published var mode: ViewerPane.Mode = .watch
    @Published var hint = ""
    @Published var ended = false
}

/// Una pantalla compartida que se está viendo: imagen, trazos, punteros y barra. Vive dentro de `StageWindow`, solo o junto a otras.
@MainActor
final class ViewerPane: NSView {
    enum Mode { case watch, draw, control }
    let state = ViewerState()
    let annotations = AnnotationView()
    /// Punteros con nombre de quienes miran y dibujan o controlan.
    let pointers = PointerView()
    let frameView = FrameView()
    let sender: String
    let source: String
    private var back: BackChannel?
    private var onClose: (() -> Void)?
    private var sharerVersion: UInt8 = 0
    private var frames = 0
    private var statStart = Date()
    private var hintTimer: Timer?
    private var bar: NSHostingView<ViewerBar>!
    private let nameTag = NSTextField(labelWithString: "")
    private(set) var isEnded = false
    /// Sesión de control remoto con contraseña: en cuanto llega el permiso se entra directamente en modo control.
    var autoControl = false
    var closeRequested: (() -> Void)?
    var spotlightRequested: (() -> Void)?
    var fullRequested: (() -> Void)?
    var endedChanged: (() -> Void)?
    var sizeKnown: ((Int, Int) -> Void)?
    private(set) var frameSize = CGSize.zero
    private var gotFrame = false
    private var fitPending = false

    /// En la tira del mosaico: sin barra, con el nombre de quien comparte.
    var compact = false {
        didSet {
            bar.isHidden = compact
            nameTag.isHidden = !compact
            if compact { setMode(.watch) }
        }
    }

    init(sender: String, source: String) {
        self.sender = sender
        self.source = source
        super.init(frame: .zero)
        wantsLayer = true
        layer?.cornerRadius = 12
        layer?.masksToBounds = true
        layer?.backgroundColor = NSColor(red: 0.06, green: 0.04, blue: 0.04, alpha: 1).cgColor
        frameView.translatesAutoresizingMaskIntoConstraints = false
        addSubview(frameView)
        annotations.onlyMine = true
        annotations.local = { [weak self] op in self?.back?.send(ScreenShareSession.framed(UInt8(ascii: "A"), op.bytes())) }
        annotations.pointer = { [weak self] visible, x, y in self?.sendPointer(visible, x, y) }
        frameView.addSubview(annotations)
        frameView.addSubview(pointers)
        frameView.annotations = annotations
        frameView.pointers = pointers
        frameView.viewer = self
        bar = NSHostingView(rootView: ViewerBar(state: state, pane: self))
        bar.translatesAutoresizingMaskIntoConstraints = false
        addSubview(bar)
        nameTag.stringValue = sender
        nameTag.font = NSFont(name: "DINAlternate-Bold", size: 11.5) ?? .boldSystemFont(ofSize: 11.5)
        nameTag.textColor = .white
        nameTag.backgroundColor = NSColor.black.withAlphaComponent(0.7)
        nameTag.drawsBackground = true
        nameTag.isHidden = true
        nameTag.translatesAutoresizingMaskIntoConstraints = false
        addSubview(nameTag)
        NSLayoutConstraint.activate([
            frameView.leadingAnchor.constraint(equalTo: leadingAnchor), frameView.trailingAnchor.constraint(equalTo: trailingAnchor),
            frameView.topAnchor.constraint(equalTo: topAnchor), frameView.bottomAnchor.constraint(equalTo: bottomAnchor),
            bar.centerXAnchor.constraint(equalTo: centerXAnchor), bar.topAnchor.constraint(equalTo: topAnchor, constant: 8),
            nameTag.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 6), nameTag.bottomAnchor.constraint(equalTo: bottomAnchor, constant: -6),
        ])
    }

    required init?(coder: NSCoder) { fatalError() }

    func attach(_ back: BackChannel, onClose: @escaping () -> Void) {
        self.back = back
        self.onClose = onClose
    }

    /// Cierra esta pantalla: corta la conexión y suelta el teclado.
    func shutdown() {
        releaseInput()
        onClose?()
        onClose = nil
        back?.close()
        back = nil
    }

    func resize(_ w: Int, _ h: Int) {
        frameSize = CGSize(width: w, height: h)
        frameView.resize(w, h)
        state.status = "\(sender) · \(w) × \(h)"
        sizeKnown?(w, h)
    }

    func apply(_ patches: [(Int, Int, CGImage)], cursor: (Int, Int), changed: Bool) {
        frameView.apply(patches, cursor: cursor)
        if !gotFrame && !patches.isEmpty {
            gotFrame = true
            DiagLog.write("visor \(sender): llegó el primer fotograma")
            // El ajuste de resolución espera a que ya se vea algo: cambiar el monitor remoto durante el arranque podía dejar la pantalla en negro.
            if fitPending { DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { [weak self] in self?.sendFit() } }
        }
        if changed { frames += 1 }
        if Date().timeIntervalSince(statStart) >= 1 {
            let fps = Double(frames) / Date().timeIntervalSince(statStart)
            frames = 0
            statStart = Date()
            state.status = "\(sender) · \(Int(fps.rounded())) fps"
        }
    }

    func ended(_ reason: String) {
        pointers.hideAll()
        setMode(.watch)
        state.canAsk = false
        state.ended = true
        state.status = reason
        back = nil
        isEnded = true
        endedChanged?()
    }

    func doubleClicked() { spotlightRequested?() }

    // MARK: Permisos

    func backChannelReady(_ version: UInt8) {
        sharerVersion = version
        state.canFit = version >= 3
        if back != nil { state.canAsk = true }
    }

    /// «Ajustar resolución»: pide que la pantalla compartida tome el tamaño de esta ventana (en píxeles). Apagado: la restaura.
    func toggleFit() {
        guard sharerVersion >= 3, state.mode == .control else { return }
        state.fit.toggle()
        sendFit()
    }

    private var fitTimer: Timer?

    private func sendFit() {
        guard let back, sharerVersion >= 3 else { return }
        if state.fit && !gotFrame { fitPending = true; return }
        fitPending = false
        var w: UInt16 = 0, h: UInt16 = 0
        if state.fit {
            let scale = window?.backingScaleFactor ?? 2
            let rect = frameView.bounds
            w = UInt16(min(8192, max(320, Int(rect.width * scale)))); h = UInt16(min(8192, max(240, Int(rect.height * scale))))
        }
        back.send(Data([UInt8(ascii: "Z"), UInt8(w & 255), UInt8(w >> 8), UInt8(h & 255), UInt8(h >> 8)]))
    }

    /// Si cambia el tamaño de la ventana con el ajuste activo, se vuelve a pedir tras 0,5 s sin cambios.
    override func layout() {
        super.layout()
        guard state.fit else { return }
        fitTimer?.invalidate()
        fitTimer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: false) { [weak self] _ in Task { @MainActor in self?.sendFit() } }
    }

    /// Mi puntero, para que quien comparte y los demás lo vean (solo si quien comparte lo entiende y puedo dibujar o controlar).
    func sendPointer(_ visible: Bool, _ x: UInt16, _ y: UInt16) {
        guard let back, sharerVersion >= ScreenShareSession.pointerVersion, state.mode != .watch else { return }
        back.send(Data([UInt8(ascii: "M"), visible ? 1 : 0, UInt8(x & 255), UInt8(x >> 8), UInt8(y & 255), UInt8(y >> 8)]))
    }

    func setGranted(_ granted: UInt8) {
        let gained = granted & ~state.granted
        state.granted = granted
        if state.mode == .draw && granted & SharePermission.draw == 0 { setMode(.watch); hint("Ya no puedes dibujar.") }
        if state.mode == .control && granted & SharePermission.control == 0 { setMode(.watch); hint("Ya no controlas el equipo.") }
        if autoControl && granted & SharePermission.control != 0 && state.mode != .control {
            setMode(.control)
            hint("Controlas \(sender). Pulsa el botón de control para soltarlo.")
        } else if gained & state.asking & SharePermission.control != 0 {
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
        if state.mode == .control && mode != .control {
            releaseInput()
            if state.fit { state.fit = false; sendFit() }
        }
        if mode == .watch && state.mode != .watch { sendPointer(false, 0, 0) }
        let entering = mode == .control && state.mode != .control
        state.mode = mode
        annotations.drawing = mode == .draw
        frameView.controlling = mode == .control
        // Al empezar a controlar, la otra pantalla toma el tamaño de esta ventana (si el ajuste está activo y el emisor lo entiende).
        if entering && Prefs.shared.autoFit && state.canFit && !state.fit { state.fit = true; sendFit() }
        window?.makeFirstResponder(mode == .draw ? annotations : frameView)
    }

    func hint(_ text: String) {
        state.hint = text
        hintTimer?.invalidate()
        hintTimer = Timer.scheduledTimer(withTimeInterval: 3.5, repeats: false) { [weak self] _ in Task { @MainActor in self?.state.hint = "" } }
    }

    // MARK: Control remoto

    var controlling: Bool { state.mode == .control && back != nil && frameView.hasFrame }
    func sendInput(_ event: RemoteInputEvent) { back?.send(event.bytes()) }
    func releaseInput() { frameView.releaseAll() }
}

// MARK: - Ventana con una o varias pantallas

final class StageState: ObservableObject {
    @Published var count = 1
    @Published var mosaic = true
}

private final class StageContent: NSView {
    weak var stage: StageWindow?
    override var isFlipped: Bool { true }
    override func layout() { super.layout(); stage?.arrange() }
}

/// Varias pantallas compartidas a la vez en una sola ventana, como en Discord: en mosaico, o una destacada con las demás en
/// una tira. Doble clic destaca una; con una sola (o ya destacada) pone la ventana a pantalla completa.
@MainActor
final class StageWindow: NSWindow, NSWindowDelegate {
    static var shared: StageWindow?
    private(set) var panes: [ViewerPane] = []
    private let state = StageState()
    private let content = StageContent()
    private var spotlight: ViewerPane?
    private var sized = false
    private static let headHeight: CGFloat = 40

    static func add(_ pane: ViewerPane) {
        let stage = shared ?? StageWindow()
        shared = stage
        stage.attach(pane)
    }

    private init() {
        super.init(contentRect: NSRect(x: 0, y: 0, width: 1100, height: 700), styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
                   backing: .buffered, defer: false)
        titlebarAppearsTransparent = true
        backgroundColor = NSColor(red: 0.04, green: 0.027, blue: 0.03, alpha: 1)
        isReleasedWhenClosed = false
        acceptsMouseMovedEvents = true
        delegate = self
        collectionBehavior = [.fullScreenPrimary]
        content.stage = self
        let root = NSView()
        content.translatesAutoresizingMaskIntoConstraints = false
        root.addSubview(content)
        let head = NSHostingView(rootView: StageHead(state: state, setMosaic: { [weak self] in self?.setMosaic($0) }))
        head.translatesAutoresizingMaskIntoConstraints = false
        root.addSubview(head)
        NSLayoutConstraint.activate([
            content.leadingAnchor.constraint(equalTo: root.leadingAnchor), content.trailingAnchor.constraint(equalTo: root.trailingAnchor),
            content.topAnchor.constraint(equalTo: root.topAnchor), content.bottomAnchor.constraint(equalTo: root.bottomAnchor),
            head.topAnchor.constraint(equalTo: root.topAnchor, constant: 26), head.centerXAnchor.constraint(equalTo: root.centerXAnchor),
        ])
        contentView = root
        center()
    }

    private func attach(_ pane: ViewerPane) {
        panes.append(pane)
        content.addSubview(pane)
        pane.spotlightRequested = { [weak self, weak pane] in guard let pane else { return }; self?.highlight(pane) }
        pane.closeRequested = { [weak self, weak pane] in guard let pane else { return }; self?.remove(pane) }
        pane.fullRequested = { [weak self] in self?.toggleFullScreen(nil) }
        pane.sizeKnown = { [weak self, weak pane] w, h in guard let pane else { return }; self?.paneSized(pane, w, h) }
        // Si una transmisión termina y hay otras, se retira sola al poco, como en Discord.
        pane.endedChanged = { [weak self, weak pane] in
            guard let self, let pane, self.panes.count > 1 else { return }
            DispatchQueue.main.asyncAfter(deadline: .now() + 4) { if self.panes.contains(where: { $0 === pane }), self.panes.count > 1 { self.remove(pane) } }
        }
        if spotlight == nil { spotlight = pane }
        if panes.count > 1 { state.mosaic = true }
        refresh()
        NSApp.activate(ignoringOtherApps: true)
        makeKeyAndOrderFront(nil)
        makeFirstResponder(pane.frameView)
    }

    func remove(_ pane: ViewerPane) {
        guard let index = panes.firstIndex(where: { $0 === pane }) else { return }
        panes.remove(at: index)
        pane.removeFromSuperview()
        pane.shutdown()
        if spotlight === pane { spotlight = panes.first }
        if panes.isEmpty { close(); return }
        refresh()
    }

    private func highlight(_ pane: ViewerPane) {
        if panes.count > 1 && state.mosaic || spotlight !== pane && panes.count > 1 {
            spotlight = pane
            state.mosaic = false
            refresh()
            makeFirstResponder(pane.frameView)
        } else {
            toggleFullScreen(nil)
        }
    }

    func setMosaic(_ mosaic: Bool) {
        state.mosaic = mosaic
        refresh()
    }

    private func paneSized(_ pane: ViewerPane, _ w: Int, _ h: Int) {
        guard !sized, panes.count == 1, !isZoomed, let screen = screen ?? NSScreen.main else { return }
        sized = true
        let area = screen.visibleFrame
        let scale = min(1, area.width * 0.9 / CGFloat(w), area.height * 0.9 / CGFloat(h))
        setContentSize(NSSize(width: max(480, min(CGFloat(w) * scale, area.width * 0.9)), height: max(320, min(CGFloat(h) * scale, area.height * 0.9))))
        center()
    }

    private func refresh() {
        state.count = panes.count
        title = panes.count == 1 ? "Pantalla de \(panes[0].sender)" + (panes[0].source.isEmpty ? "" : " · " + panes[0].source) : "Lazo · pantallas compartidas"
        arrange()
    }

    /// Mosaico (cuadrícula) o una destacada con las demás en una tira de miniaturas.
    func arrange() {
        let bounds = content.bounds
        guard !panes.isEmpty, bounds.width > 1 else { return }
        let top: CGFloat = panes.count > 1 ? Self.headHeight + 24 : 0
        let area = NSRect(x: 8, y: top, width: bounds.width - 16, height: max(1, bounds.height - top - 8))
        if panes.count == 1 {
            panes[0].compact = false
            panes[0].frame = NSRect(x: 0, y: 0, width: bounds.width, height: bounds.height)
            return
        }
        if state.mosaic {
            let columns = Int(ceil(sqrt(Double(panes.count)))), rows = Int(ceil(Double(panes.count) / Double(columns)))
            let w = area.width / CGFloat(columns), h = area.height / CGFloat(rows)
            for (i, pane) in panes.enumerated() {
                pane.compact = false
                pane.frame = NSRect(x: area.minX + CGFloat(i % columns) * w + 4, y: area.minY + CGFloat(i / columns) * h + 4, width: w - 8, height: h - 8)
            }
            return
        }
        let main = spotlight.flatMap { s in panes.first { $0 === s } } ?? panes[0]
        let strip: CGFloat = 130
        main.compact = false
        main.frame = NSRect(x: area.minX, y: area.minY, width: area.width, height: max(1, area.height - strip - 6))
        var x = area.minX
        for pane in panes where pane !== main {
            pane.compact = true
            pane.frame = NSRect(x: x, y: area.maxY - strip, width: 200, height: strip - 6)
            x += 208
        }
    }

    func windowDidResignKey(_ notification: Notification) { panes.forEach { $0.releaseInput() } }

    func windowWillClose(_ notification: Notification) {
        for pane in panes { pane.shutdown() }
        panes.removeAll()
        StageWindow.shared = nil
    }
}

private struct StageHead: View {
    @ObservedObject var state: StageState
    let setMosaic: (Bool) -> Void

    var body: some View {
        if state.count > 1 {
            HStack(spacing: 8) {
                Circle().fill(Color(hex: "#F25C45")).frame(width: 8, height: 8)
                Text("Pantallas compartidas · \(state.count)").font(.lazo(13, .semibold))
                modeButton("Mosaico", on: state.mosaic) { setMosaic(true) }
                modeButton("Destacada", on: !state.mosaic) { setMosaic(false) }
            }
            .foregroundStyle(.white).padding(.horizontal, 14).padding(.vertical, 6)
            .background(Capsule().fill(Color(hex: "#CC2A0E12")))
        }
    }

    private func modeButton(_ title: String, on: Bool, _ action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title).font(.lazo(12, .medium)).padding(.horizontal, 10).padding(.vertical, 3)
                .background(Capsule().fill(Color.white.opacity(on ? 0.33 : 0)))
        }.buttonStyle(.plain)
    }
}

/// Barra superior del visor: estado, dibujar, controlar, pantalla completa y cerrar.
struct ViewerBar: View {
    @ObservedObject var state: ViewerState
    let pane: ViewerPane

    var body: some View {
        VStack(spacing: 4) {
            HStack(spacing: 6) {
                Text(state.status).font(.lazo(12, .medium)).lineLimit(1).padding(.horizontal, 6)
                if state.canAsk {
                    barButton("pencil", state.granted & SharePermission.draw != 0 ? (state.mode == .draw ? "Dejar de dibujar" : "Dibujar") : "Dibujar sobre la pantalla (pide permiso)",
                              active: state.mode == .draw, waiting: state.asking & SharePermission.draw != 0) { pane.drawClicked() }
                    barButton("cursorarrow.rays", state.granted & SharePermission.control != 0 ? "Soltar el control" : "Controlar el equipo (pide permiso)",
                              active: state.mode == .control, waiting: state.asking & SharePermission.control != 0) { pane.controlClicked() }
                }
                if state.canFit && state.mode == .control {
                    barButton("rectangle.expand.vertical", state.fit ? "Restaurar la resolución de la otra pantalla" : "Ajustar la resolución de la otra pantalla a esta ventana",
                              active: state.fit, waiting: false) { pane.toggleFit() }
                }
                barButton("arrow.up.left.and.arrow.down.right", "Pantalla completa", active: false, waiting: false) { pane.fullRequested?() }
                barButton("xmark", "Cerrar", active: false, waiting: false) { pane.closeRequested?() }
            }
            if state.mode == .draw {
                AnnotationToolbar(view: pane.annotations, clearTip: "Borrar mis trazos") { pane.annotations.eraseMine() }
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
    weak var pointers: PointerView?
    weak var viewer: ViewerPane?
    var controlling = false {
        didSet {
            window?.invalidateCursorRects(for: self)
            if controlling { cursorLayer.isHidden = true }
        }
    }
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
        // En modo control solo se ve el puntero del sistema; el cursor remoto del paquete se oculta para que no haya dos.
        if !controlling && cursor.0 >= 0 && cursor.1 >= 0 && size.width > 0 {
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
        pointers?.frame = displayRect
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
        viewer.sendPointer(inside, x, y)
    }

    override func mouseDown(with event: NSEvent) {
        if event.clickCount == 2 && !(viewer?.controlling ?? false) { viewer?.doubleClicked(); return }
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
