import AppKit
import CoreMedia
import CoreVideo
import LazoCore
import ScreenCaptureKit

// MARK: - Fuentes: pantallas y ventanas

struct WindowInfo {
    let id: CGWindowID
    let pid: pid_t
    let bounds: CGRect
    let layer: Int
}

enum ScreenSources {
    /// Ventanas visibles de delante hacia atrás, en coordenadas globales (origen arriba a la izquierda).
    static func onScreenWindows() -> [WindowInfo] {
        guard let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] else { return [] }
        return list.compactMap { info in
            guard let id = info[kCGWindowNumber as String] as? CGWindowID, let pid = info[kCGWindowOwnerPID as String] as? pid_t,
                  let boundsDict = info[kCGWindowBounds as String] as? NSDictionary, let bounds = CGRect(dictionaryRepresentation: boundsDict),
                  let layer = info[kCGWindowLayer as String] as? Int, (info[kCGWindowAlpha as String] as? Double ?? 1) > 0.01 else { return nil }
            return WindowInfo(id: id, pid: pid, bounds: bounds, layer: layer)
        }
    }

    static func info(_ id: CGWindowID) -> WindowInfo? {
        guard let list = CGWindowListCopyWindowInfo([.optionIncludingWindow], id) as? [[String: Any]], let info = list.first,
              let pid = info[kCGWindowOwnerPID as String] as? pid_t, let boundsDict = info[kCGWindowBounds as String] as? NSDictionary,
              let bounds = CGRect(dictionaryRepresentation: boundsDict) else { return nil }
        return WindowInfo(id: id, pid: pid, bounds: bounds, layer: info[kCGWindowLayer as String] as? Int ?? 0)
    }

    static func ownerPid(_ id: CGWindowID) -> pid_t? { info(id)?.pid }
}

/// Una pantalla o una ventana que se puede compartir.
final class ShareSource: Identifiable {
    let id = UUID()
    let label: String
    let detail: String
    let display: SCDisplay?
    let window: SCWindow?
    let frame: CGRect

    init(display: SCDisplay, index: Int) {
        self.display = display
        window = nil
        frame = display.frame
        label = "Pantalla \(index + 1)"
        detail = "\(display.width) × \(display.height)" + (CGDisplayIsMain(display.displayID) != 0 ? " · principal" : "")
    }

    init(window: SCWindow) {
        display = nil
        self.window = window
        frame = window.frame
        label = window.title?.isEmpty == false ? window.title! : (window.owningApplication?.applicationName ?? "Ventana")
        detail = window.owningApplication?.applicationName ?? "Ventana"
    }

    var windowID: CGWindowID? { window?.windowID }

    /// Rectángulo actual en puntos globales (origen arriba a la izquierda).
    func currentFrame() -> CGRect? {
        if let display { return CGDisplayBounds(display.displayID) }
        if let window { return ScreenSources.info(window.windowID)?.bounds }
        return nil
    }

    /// Escala de píxeles por punto de la pantalla donde está.
    var scale: CGFloat {
        let rect = currentFrame() ?? frame
        let primary = NSScreen.screens.first?.frame.height ?? 0
        let center = NSPoint(x: rect.midX, y: primary - rect.midY)
        return (NSScreen.screens.first { NSMouseInRect(center, $0.frame, false) } ?? NSScreen.main)?.backingScaleFactor ?? 2
    }

    static func load() async throws -> [ShareSource] {
        let content = try await SCShareableContent.excludingDesktopWindows(true, onScreenWindowsOnly: true)
        let me = ProcessInfo.processInfo.processIdentifier
        var list = content.displays.sorted { $0.frame.minX < $1.frame.minX }.enumerated().map { ShareSource(display: $0.element, index: $0.offset) }
        list += content.windows.filter { w in
            w.windowLayer == 0 && w.frame.width > 80 && w.frame.height > 60 && w.owningApplication?.processID != me && !(w.title ?? "").isEmpty
        }.map { ShareSource(window: $0) }
        return list
    }
}

// MARK: - Codificación por bloques

/// Compara cada fotograma con el último enviado en bloques de 128 × 64 y codifica en JPEG solo lo que cambió.
/// Lo que se queda quieto tras moverse se vuelve a enviar nítido.
final class TileEncoder {
    static let tileW = 128, tileH = 64
    static let motion = 0.58, normal = 0.74, sharp = 0.9
    private(set) var width = 0, height = 0
    private var previous: UnsafeMutablePointer<UInt8>?
    private var refine: [Int] = []
    private var cols = 0, rows = 0

    struct Tile { let x, y, w, h: Int; let jpeg: Data }

    func reset(_ w: Int, _ h: Int) {
        previous?.deallocate()
        width = w; height = h
        previous = UnsafeMutablePointer<UInt8>.allocate(capacity: w * h * 4)
        cols = (w + Self.tileW - 1) / Self.tileW
        rows = (h + Self.tileH - 1) / Self.tileH
        refine = Array(repeating: 0, count: cols * rows)
    }

    deinit { previous?.deallocate() }

    func encode(_ buffer: CVPixelBuffer, key: Bool) -> [Tile] {
        CVPixelBufferLockBaseAddress(buffer, .readOnly)
        defer { CVPixelBufferUnlockBaseAddress(buffer, .readOnly) }
        let w = CVPixelBufferGetWidth(buffer), h = CVPixelBufferGetHeight(buffer)
        let key = key || w != width || h != height
        if w != width || h != height { reset(w, h) }
        guard let base = CVPixelBufferGetBaseAddress(buffer)?.assumingMemoryBound(to: UInt8.self), let previous else { return [] }
        let stride = CVPixelBufferGetBytesPerRow(buffer)
        // 0 = igual, 1 = cambió, 2 = afinar (quieto tras moverse)
        var state = [UInt8](repeating: 0, count: cols * rows)
        var dirty = 0
        for ty in 0..<rows {
            let y0 = ty * Self.tileH, th = min(Self.tileH, h - y0)
            for tx in 0..<cols {
                let x0 = tx * Self.tileW, tw = min(Self.tileW, w - x0)
                let index = ty * cols + tx
                var changed = key
                if !changed {
                    for row in y0..<y0 + th where memcmp(base + row * stride + x0 * 4, previous + (row * w + x0) * 4, tw * 4) != 0 { changed = true; break }
                }
                if changed {
                    state[index] = 1
                    dirty += 1
                    for row in y0..<y0 + th { memcpy(previous + (row * w + x0) * 4, base + row * stride + x0 * 4, tw * 4) }
                } else if refine[index] > 0 {
                    refine[index] -= 1
                    if refine[index] == 0 { state[index] = 2 }
                }
            }
        }
        let quality = key ? Self.normal : dirty * 3 > cols * rows ? Self.motion : Self.normal
        for i in 0..<state.count where state[i] == 1 { refine[i] = quality < Self.sharp ? 8 : 0 }
        // Bloques contiguos en una fila con el mismo estado se codifican juntos.
        var runs: [(x: Int, y: Int, w: Int, h: Int, q: Double)] = []
        for ty in 0..<rows {
            var tx = 0
            while tx < cols {
                let s = state[ty * cols + tx]
                if s == 0 { tx += 1; continue }
                let start = tx
                while tx < cols && state[ty * cols + tx] == s { tx += 1 }
                let x0 = start * Self.tileW, y0 = ty * Self.tileH
                runs.append((x0, y0, min(w, tx * Self.tileW) - x0, min(h, y0 + Self.tileH) - y0, s == 2 ? Self.sharp : quality))
            }
        }
        var tiles = [Tile?](repeating: nil, count: runs.count)
        let frame = previous, frameWidth = w
        tiles.withUnsafeMutableBufferPointer { out in
            DispatchQueue.concurrentPerform(iterations: runs.count) { i in
                let r = runs[i]
                if let jpeg = Self.jpeg(frame + (r.y * frameWidth + r.x) * 4, stride: frameWidth * 4, r.w, r.h, r.q) {
                    out[i] = Tile(x: r.x, y: r.y, w: r.w, h: r.h, jpeg: jpeg)
                }
            }
        }
        return tiles.compactMap { $0 }
    }

    private static func jpeg(_ pixels: UnsafeMutablePointer<UInt8>, stride: Int, _ w: Int, _ h: Int, _ quality: Double) -> Data? {
        let data = Data(bytes: pixels, count: stride * (h - 1) + w * 4)
        guard let provider = CGDataProvider(data: data as CFData),
              let image = CGImage(width: w, height: h, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: stride,
                                  space: CGColorSpaceCreateDeviceRGB(),
                                  bitmapInfo: CGBitmapInfo(rawValue: CGBitmapInfo.byteOrder32Little.rawValue | CGImageAlphaInfo.noneSkipFirst.rawValue),
                                  provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent) else { return nil }
        return ImagePreview.encode(image, quality: quality)
    }
}

// MARK: - Transmisión

/// Transmisión de la pantalla de este Mac hacia uno o varios compañeros por el canal cifrado (mismo protocolo que Windows).
final class ScreenShareSession: NSObject, SCStreamOutput, SCStreamDelegate {
    final class Viewer {
        let id: Int
        let name: String
        let connection: SecureConnection
        let writeLock = NSLock()
        var capable = false
        var granted: UInt8 = 0
        var asked = Set<UInt8>()
        init(id: Int, name: String, connection: SecureConnection) { self.id = id; self.name = name; self.connection = connection }
    }

    static let backChannelVersion: UInt8 = 1
    static let maxWidth = 2560, maxHeight = 1600
    let source: ShareSource
    private let engine: NetworkEngine
    private let lock = NSLock()
    private var viewers: [Viewer] = []
    private var running = true
    private var key = true
    private var pending = 0
    private var nextViewer = 0
    private var stream: SCStream?
    private let frameLock = NSLock()
    private var latest: CVPixelBuffer?
    private let frameReady = DispatchSemaphore(value: 0)
    private let control = DispatchQueue(label: "Lazo screen control")
    private let annotations = AnnotationModel()
    private var owners: [Int32: Int] = [:]
    let injector: RemoteInputInjector
    private var outputSize = CGSize.zero
    private(set) var fps = 0.0

    /// Mientras quien comparte dibuja, la entrada remota se descarta.
    var inputPaused = false
    var changed: (() -> Void)?
    var notice: ((String) -> Void)?
    var ended: (() -> Void)?
    var permissionRequested: ((Int, String, UInt8) -> Void)?
    var viewerLeft: ((Int) -> Void)?
    var annotated: ((AnnotationOp) -> Void)?

    init(engine: NetworkEngine, source: ShareSource) {
        self.engine = engine
        self.source = source
        injector = RemoteInputInjector(source: source)
    }

    var viewerNames: [String] { lock.lock(); defer { lock.unlock() }; return viewers.map(\.name) }
    var controlActive: Bool { lock.lock(); defer { lock.unlock() }; return viewers.contains { $0.granted & SharePermission.control != 0 } }
    var anyGranted: Bool { lock.lock(); defer { lock.unlock() }; return viewers.contains { $0.granted != 0 } }
    var controller: String? {
        lock.lock(); defer { lock.unlock() }
        guard let v = viewers.first(where: { $0.granted & SharePermission.control != 0 }) else { return nil }
        return v.name + (v.granted & SharePermission.fullControl != 0 ? " (control total)" : " (ventana activa)")
    }

    func invite(_ peer: Peer) {
        lock.lock(); pending += 1; lock.unlock()
        raise()
        let label = source.label
        Thread.detachNewThread { [self] in
            defer { lock.lock(); pending -= 1; lock.unlock(); raise() }
            do {
                let connection = try engine.openScreen(peer, source: label)
                guard running else { connection.close(); return }
                lock.lock()
                nextViewer += 1
                let viewer = Viewer(id: nextViewer, name: peer.name, connection: connection)
                viewers.append(viewer)
                key = true
                lock.unlock()
                Thread.detachNewThread { self.readBack(viewer) }
                say("\(peer.name) está viendo tu pantalla")
            } catch { say(error.localizedDescription) }
        }
    }

    func start() async throws {
        let filter: SCContentFilter
        if let display = source.display {
            let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
            let me = content.applications.filter { $0.processID == ProcessInfo.processInfo.processIdentifier }
            filter = SCContentFilter(display: display, excludingApplications: me, exceptingWindows: [])
        } else if let window = source.window {
            filter = SCContentFilter(desktopIndependentWindow: window)
        } else { throw LazoError.invalid("Fuente no válida.") }
        let config = SCStreamConfiguration()
        configure(config)
        config.pixelFormat = kCVPixelFormatType_32BGRA
        config.minimumFrameInterval = CMTime(value: 1, timescale: 60)
        config.showsCursor = false
        config.queueDepth = 4
        let stream = SCStream(filter: filter, configuration: config, delegate: self)
        try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: DispatchQueue(label: "Lazo capture", qos: .userInteractive))
        try await stream.startCapture()
        self.stream = stream
        let worker = Thread { self.loop() }
        worker.name = "Lazo screen share"
        worker.qualityOfService = .userInteractive
        worker.start()
    }

    private func configure(_ config: SCStreamConfiguration) {
        let frame = source.currentFrame() ?? source.frame
        let scale = source.scale
        var w = frame.width * scale, h = frame.height * scale
        let fit = min(1, Double(Self.maxWidth) / w, Double(Self.maxHeight) / h)
        w = (w * fit).rounded(); h = (h * fit).rounded()
        config.width = max(2, Int(w)); config.height = max(2, Int(h))
        outputSize = CGSize(width: config.width, height: config.height)
    }

    func stop() { running = false; frameReady.signal() }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen, let buffer = sampleBuffer.imageBuffer,
              let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
              let raw = attachments.first?[.status] as? Int, SCFrameStatus(rawValue: raw) == .complete else { return }
        frameLock.lock()
        latest = buffer
        frameLock.unlock()
        frameReady.signal()
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        say("Se detuvo la captura: " + error.localizedDescription)
        stop()
    }

    private func loop() {
        let encoder = TileEncoder()
        let started = Date()
        var lastSend = Date(), statStart = Date(), lastResizeCheck = Date()
        var frames = 0, hadViewer = false, lastCursor = (x: Int32.min, y: Int32.min)
        while running {
            let got = frameReady.wait(timeout: .now() + 0.1) == .success
            lock.lock()
            let targets = viewers
            let waiting = pending
            lock.unlock()
            if targets.isEmpty {
                if waiting == 0 && (hadViewer || Date().timeIntervalSince(started) > 1.5) {
                    say(hadViewer ? "Nadie está viendo; se detuvo la transmisión." : "Nadie aceptó la invitación.")
                    break
                }
                continue
            }
            hadViewer = true
            if source.window != nil, ScreenSources.info(source.window!.windowID) == nil { say("La ventana compartida se cerró."); break }
            if Date().timeIntervalSince(lastResizeCheck) > 1, let stream { lastResizeCheck = Date(); resizeIfNeeded(stream) }
            frameLock.lock()
            let buffer = got ? latest : nil
            latest = nil
            frameLock.unlock()
            let cursor = cursorPosition(width: encoder.width, height: encoder.height)
            guard let buffer else {
                if Date().timeIntervalSince(lastSend) > 1 { send(targets, Data([UInt8(ascii: "K")])); lastSend = Date() }
                continue
            }
            lock.lock()
            let isKey = key
            key = false
            lock.unlock()
            let sizeChanged = CVPixelBufferGetWidth(buffer) != encoder.width || CVPixelBufferGetHeight(buffer) != encoder.height
            let tiles = encoder.encode(buffer, key: isKey)
            let moved = cursor.x != lastCursor.x || cursor.y != lastCursor.y
            if tiles.isEmpty && !moved && !isKey && !sizeChanged {
                if Date().timeIntervalSince(lastSend) > 1 { send(targets, Data([UInt8(ascii: "K")])); lastSend = Date() }
                continue
            }
            var out = BinaryOut()
            if isKey || sizeChanged {
                out.u8(UInt8(ascii: "S")); out.i32(Int32(encoder.width)); out.i32(Int32(encoder.height))
            }
            let point = cursorPosition(width: encoder.width, height: encoder.height)
            out.u8(UInt8(ascii: "F")); out.i32(point.x); out.i32(point.y); out.i32(Int32(tiles.count))
            for tile in tiles {
                out.u16(UInt16(tile.x)); out.u16(UInt16(tile.y)); out.u16(UInt16(tile.w)); out.u16(UInt16(tile.h))
                out.i32(Int32(tile.jpeg.count)); out.bytes(tile.jpeg)
            }
            send(targets, out.data)
            lastSend = Date()
            lastCursor = point
            if !tiles.isEmpty { frames += 1 }
            if Date().timeIntervalSince(statStart) >= 1 {
                fps = Double(frames) / Date().timeIntervalSince(statStart)
                frames = 0
                statStart = Date()
                raise()
            }
        }
        finish()
    }

    /// Si la ventana compartida cambia de tamaño, la captura se ajusta y se envía un fotograma completo.
    private func resizeIfNeeded(_ stream: SCStream) {
        let config = SCStreamConfiguration()
        configure(config)
        let size = outputSize
        config.pixelFormat = kCVPixelFormatType_32BGRA
        config.minimumFrameInterval = CMTime(value: 1, timescale: 60)
        config.showsCursor = false
        config.queueDepth = 4
        if abs(Double(size.width) - Double(lastConfigured.width)) > 2 || abs(Double(size.height) - Double(lastConfigured.height)) > 2 {
            lastConfigured = size
            stream.updateConfiguration(config) { _ in }
        }
    }
    private lazy var lastConfigured = outputSize

    /// Cursor en píxeles del fotograma, o -1 si está fuera de lo compartido.
    private func cursorPosition(width: Int, height: Int) -> (x: Int32, y: Int32) {
        guard width > 0, let frame = source.currentFrame() else { return (-1, -1) }
        let mouse = NSEvent.mouseLocation
        let primary = NSScreen.screens.first?.frame.height ?? 0
        let x = mouse.x, y = primary - mouse.y
        guard frame.contains(CGPoint(x: x, y: y)) else { return (-1, -1) }
        return (Int32((x - frame.minX) / frame.width * Double(width)), Int32((y - frame.minY) / frame.height * Double(height)))
    }

    private func send(_ targets: [Viewer], _ data: Data) {
        if targets.count == 1 { write(targets[0], data); return }
        DispatchQueue.concurrentPerform(iterations: targets.count) { write(targets[$0], data) }
    }

    private func write(_ viewer: Viewer, _ data: Data) {
        viewer.writeLock.lock()
        do {
            try viewer.connection.stream.write(data)
            try viewer.connection.stream.flush()
            viewer.writeLock.unlock()
        } catch {
            viewer.writeLock.unlock()
            lock.lock()
            let removed = viewers.contains { $0 === viewer }
            viewers.removeAll { $0 === viewer }
            lock.unlock()
            viewer.connection.close()
            if removed { say("\(viewer.name) dejó de ver tu pantalla"); raise() }
        }
    }

    private func finish() {
        running = false
        injector.releaseAll()
        if let stream { stream.stopCapture { _ in } }
        lock.lock()
        let rest = viewers
        viewers.removeAll()
        lock.unlock()
        for viewer in rest {
            viewer.writeLock.lock()
            try? viewer.connection.stream.write(Data([UInt8(ascii: "E")]))
            try? viewer.connection.stream.flush()
            viewer.writeLock.unlock()
            viewer.connection.close()
        }
        DispatchQueue.main.async { self.ended?() }
    }

    // MARK: Canal de retorno

    func annotate(_ op: AnnotationOp) {
        control.async { [self] in
            guard annotations.apply(op) else { return }
            track(op, owner: 0)
            relay(op, except: nil)
        }
    }

    private func track(_ op: AnnotationOp, owner: Int) {
        switch op.kind {
        case AnnotationOp.begin: owners[op.id] = owner
        case AnnotationOp.erase: owners.removeValue(forKey: op.id)
        case AnnotationOp.clear: owners.removeAll()
        default: break
        }
    }

    func answer(_ viewerId: Int, kind: UInt8, accept: Bool, full: Bool = false) {
        control.async { [self] in
            guard let viewer = find(viewerId) else { return }
            viewer.asked.remove(kind)
            var accept = accept
            if accept && kind == SharePermission.control && controllerOther(viewer) != nil { accept = false }
            guard accept else { write(viewer, Data([UInt8(ascii: "D"), kind])); return }
            var granted = viewer.granted | kind
            if kind == SharePermission.control {
                granted = full ? granted | SharePermission.fullControl : granted & ~SharePermission.fullControl
                injector.configure(full: full)
            }
            setGranted(viewer, granted)
        }
    }

    func revokeAll() {
        control.async { [self] in
            lock.lock(); let all = viewers; lock.unlock()
            for viewer in all where viewer.granted != 0 { setGranted(viewer, 0) }
        }
    }

    private func setGranted(_ viewer: Viewer, _ value: UInt8) {
        var granted = value
        if granted & SharePermission.control == 0 { granted &= ~SharePermission.fullControl }
        let hadControl = viewer.granted & SharePermission.control != 0
        viewer.granted = granted
        if hadControl && granted & SharePermission.control == 0 { injector.releaseAll() }
        write(viewer, Data([UInt8(ascii: "P"), granted]))
        raise()
    }

    private func controllerOther(_ except: Viewer) -> Viewer? {
        lock.lock(); defer { lock.unlock() }
        return viewers.first { $0 !== except && $0.granted & SharePermission.control != 0 }
    }

    private func find(_ id: Int) -> Viewer? {
        lock.lock(); defer { lock.unlock() }
        return viewers.first { $0.id == id }
    }

    static func framed(_ type: UInt8, _ payload: Data) -> Data {
        Data([type, UInt8(payload.count & 255), UInt8(payload.count >> 8 & 255)]) + payload
    }

    private func relay(_ op: AnnotationOp, except: Viewer?) {
        let packet = Self.framed(UInt8(ascii: "A"), op.bytes())
        lock.lock(); let targets = viewers.filter { $0.capable && $0 !== except }; lock.unlock()
        for viewer in targets { write(viewer, packet) }
    }

    /// Un trazo de quien mira: solo puede crear trazos nuevos y cambiar o borrar los suyos.
    private func applyFromViewer(_ viewer: Viewer, _ op: AnnotationOp) {
        guard viewer.granted & SharePermission.draw != 0, op.kind != AnnotationOp.clear else { return }
        if op.kind != AnnotationOp.begin && owners[op.id] != viewer.id { return }
        guard annotations.apply(op) else {
            if op.kind == AnnotationOp.begin && owners[op.id] == nil {
                write(viewer, Self.framed(UInt8(ascii: "A"), AnnotationOp(kind: AnnotationOp.erase, id: op.id).bytes()))
            }
            return
        }
        track(op, owner: viewer.id)
        relay(op, except: viewer)
        DispatchQueue.main.async { self.annotated?(op) }
    }

    private func readBack(_ viewer: Viewer) {
        let input = BinaryIn(viewer.connection.stream)
        defer {
            control.async { [self] in
                if viewer.granted & SharePermission.control != 0 { injector.releaseAll() }
                viewer.granted = 0
                viewer.capable = false
                viewer.asked.removeAll()
                DispatchQueue.main.async { self.viewerLeft?(viewer.id) }
                raise()
            }
        }
        do {
            while running {
                let type = try input.u8()
                switch type {
                case UInt8(ascii: "H"):
                    _ = try input.u8()
                    control.async { [self] in
                        guard !viewer.capable else { return }
                        write(viewer, Data([UInt8(ascii: "H"), Self.backChannelVersion]))
                        write(viewer, Data([UInt8(ascii: "P"), viewer.granted]))
                        for op in annotations.snapshot() { write(viewer, Self.framed(UInt8(ascii: "A"), op.bytes())) }
                        viewer.capable = true
                    }
                case UInt8(ascii: "R"):
                    let kind = try input.u8()
                    control.async { self.request(viewer, kind) }
                case UInt8(ascii: "A"):
                    let length = Int(try input.u16())
                    guard let op = AnnotationOp.parse(try input.bytes(length)) else { continue }
                    control.async { self.applyFromViewer(viewer, op) }
                case UInt8(ascii: "I"):
                    guard let event = try RemoteInputEvent.read(input) else { return }
                    control.async { [self] in
                        guard viewer.granted & SharePermission.control != 0, running, !inputPaused else { return }
                        injector.apply(event)
                    }
                default: return
                }
            }
        } catch {}
    }

    private func request(_ viewer: Viewer, _ kind: UInt8) {
        if kind & SharePermission.release != 0 {
            let which = kind & ~SharePermission.release
            if viewer.granted & which != 0 { setGranted(viewer, viewer.granted & ~which) }
            return
        }
        guard kind == SharePermission.draw || kind == SharePermission.control else { return }
        if viewer.granted & kind != 0 { write(viewer, Data([UInt8(ascii: "P"), viewer.granted])); return }
        if kind == SharePermission.control && controllerOther(viewer) != nil {
            write(viewer, Data([UInt8(ascii: "D"), kind]))
            say("\(viewer.name) pidió el control, pero ya lo tiene otra persona.")
            return
        }
        guard viewer.asked.insert(kind).inserted else { return }
        if let handler = permissionRequested {
            DispatchQueue.main.async { handler(viewer.id, viewer.name, kind) }
        } else {
            answer(viewer.id, kind: kind, accept: false)
        }
    }

    private func say(_ text: String) { DispatchQueue.main.async { self.notice?(text) } }
    private func raise() { DispatchQueue.main.async { self.changed?() } }
}
