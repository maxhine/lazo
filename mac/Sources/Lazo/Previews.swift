import AppKit
import CoreVideo
import ImageIO
import LazoCore
import SwiftUI

/// `Lazo --preview-snapshots <carpeta>`: dibuja cada pantalla fuera de la vista y la guarda en PNG,
/// con equipos de ejemplo y sin abrir la red. Sirve para revisar la interfaz sin permisos de captura.
@MainActor
enum Previews {
    static func run(_ folder: URL) {
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let model = AppModel.shared
        var a = Peer(id: UUID().uuidString, name: "Andrea Ruiz", address: "192.168.1.20", port: 48352, fingerprint: String(repeating: "a", count: 64))
        a.paired = true
        let b = Peer(id: UUID().uuidString, name: "EQUIPO-OFICINA", address: "192.168.1.21", port: 48352, fingerprint: String(repeating: "b", count: 64))
        var c = Peer(id: UUID().uuidString, name: "Julián Garzón", address: "192.168.1.22", port: 48352, fingerprint: String(repeating: "c", count: 64))
        c.paired = true
        model.peers = [a, b, c]
        let prefs = Prefs.shared
        let saved = (prefs.theme, prefs.interface, prefs.liquidGlass)
        defer { prefs.theme = saved.0; prefs.interface = saved.1; prefs.liquidGlass = saved.2 }
        let state = LauncherState()

        for theme in [ThemeKind.wine, .flat, .dark, .meet] {
            prefs.theme = theme
            for interface in [InterfaceKind.minimal, .standard] {
                prefs.interface = interface
                state.route = .home
                save(LauncherView(state: state), folder, "inicio-\(interface.rawValue)-\(theme.rawValue)")
            }
        }
        prefs.theme = .wine
        prefs.interface = .standard
        for (route, name) in [(Route.settings, "ajustes"), (.eyecare, "descanso"), (.history, "historial"), (.share, "compartir")] {
            state.route = route
            save(LauncherView(state: state), folder, name)
        }
        state.route = .chat
        state.chatPeer = a.id
        save(LauncherView(state: state), folder, "chat")
        prefs.liquidGlass = false
        state.route = .home
        save(LauncherView(state: state), folder, "inicio-sin-glass")

        let pairing = PairingStateView(name: "Andrea Ruiz", code: "438574", incoming: true)
        save(pairing, folder, "emparejar")
        let offer = Offer(id: UUID(), peerId: a.id, sender: "Andrea Ruiz", address: "192.168.1.20", fileName: "planos-revision-03.pdf", size: 3_429_018, preview: nil)
        let group = IncomingGroup(peerId: a.id, sender: "Andrea Ruiz")
        group.items.append(IncomingItem(offer: offer) { _ in })
        save(IncomingView(group: group, decide: { _ in }, close: {}), folder, "recepcion")
        save(PermissionPreview(), folder, "permiso-control")
        print("Capturas en \(folder.path)")
    }

    private static func save<V: View>(_ view: V, _ folder: URL, _ name: String) {
        let host = NSHostingView(rootView: view)
        let size = host.fittingSize
        host.frame = NSRect(origin: .zero, size: size)
        let window = NSWindow(contentRect: host.frame, styleMask: [.borderless], backing: .buffered, defer: false)
        window.backgroundColor = NSColor(white: 0.5, alpha: 1)
        window.contentView = host
        host.layoutSubtreeIfNeeded()
        RunLoop.current.run(until: Date().addingTimeInterval(0.15))
        guard let rep = host.bitmapImageRepForCachingDisplay(in: host.bounds) else { return }
        host.cacheDisplay(in: host.bounds, to: rep)
        try? rep.representation(using: .png, properties: [:])?.write(to: folder.appendingPathComponent(name + ".png"))
    }
}

private struct PairingStateView: View {
    let name: String
    let code: String
    let incoming: Bool
    var body: some View {
        Card {
            VStack(alignment: .leading, spacing: 6) {
                Text(incoming ? "\(name) quiere verificar la conexión con este equipo" : "Verificar a \(name)").font(.lazo(13, .semibold))
                Text("Comprueba con la otra persona que ve exactamente el mismo código. Si no coincide, cancela: alguien se interpuso en la red.").font(.lazo(11.5)).opacity(0.7)
                Text(String(code.prefix(3)) + " " + String(code.suffix(3))).font(.system(size: 34, weight: .semibold, design: .rounded)).frame(maxWidth: .infinity).padding(.top, 8)
                Text("Huella 3B8C 82FE 8143 77B0").font(.lazo(10.5)).opacity(0.6).frame(maxWidth: .infinity)
                HStack { Spacer(); PillButton(title: "Cancelar") {}; PillButton(title: "Coincide", primary: true) {} }.padding(.top, 6)
            }
        }.frame(width: 380)
    }
}

private struct PermissionPreview: View {
    var body: some View {
        Card {
            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 10) {
                    Circle().fill(Color(hex: "#D9443A")).frame(width: 36, height: 36).overlay(Image(systemName: "cursorarrow").foregroundStyle(.white))
                    Text("Andrea Ruiz quiere controlar tu equipo").font(.lazo(13, .semibold))
                }
                Text("Ventana activa: solo la ventana que compartes (o la que tienes al frente). Control total: mouse y teclado en todo el equipo. Quítalo cuando quieras desde la barra superior.")
                    .font(.lazo(11.5)).opacity(0.7).fixedSize(horizontal: false, vertical: true)
                HStack { Spacer(); PillButton(title: "Rechazar") {}; PillButton(title: "Control total") {}; PillButton(title: "Ventana activa", primary: true) {} }
            }
        }.frame(width: 440)
    }
}

/// `Lazo --self-test`: codificador de bloques → paquete → lectura → JPEG, trazos y entrada, sin red ni permisos.
enum SelfTest {
    static func run() -> Bool {
        var ok = true
        func check(_ condition: Bool, _ what: String) { print((condition ? "OK    " : "FALLO ") + what); ok = ok && condition }

        // 1. Fotograma sintético de 300 × 200 (no múltiplo de 128 × 64) y un cambio parcial.
        let w = 300, h = 200
        var buffer: CVPixelBuffer?
        CVPixelBufferCreate(nil, w, h, kCVPixelFormatType_32BGRA, nil, &buffer)
        guard let buffer else { print("FALLO sin búfer"); return false }
        func paint(_ color: (UInt8, UInt8, UInt8), x0: Int, y0: Int, x1: Int, y1: Int) {
            CVPixelBufferLockBaseAddress(buffer, [])
            let base = CVPixelBufferGetBaseAddress(buffer)!.assumingMemoryBound(to: UInt8.self)
            let stride = CVPixelBufferGetBytesPerRow(buffer)
            for y in y0..<y1 { for x in x0..<x1 { let p = base + y * stride + x * 4; p[0] = color.2; p[1] = color.1; p[2] = color.0; p[3] = 255 } }
            CVPixelBufferUnlockBaseAddress(buffer, [])
        }
        paint((200, 40, 50), x0: 0, y0: 0, x1: w, y1: h)
        let encoder = TileEncoder()
        let first = encoder.encode(buffer, key: true)
        check(first.reduce(0) { $0 + $1.w * $1.h } == w * h, "el fotograma clave cubre toda la imagen (\(first.count) bloques)")
        let same = encoder.encode(buffer, key: false)
        check(same.isEmpty, "sin cambios no se envía nada")
        paint((20, 200, 90), x0: 140, y0: 70, x1: 160, y1: 90)
        let delta = encoder.encode(buffer, key: false)
        check(delta.count == 1 && delta[0].x == 128 && delta[0].y == 64, "un cambio pequeño envía solo su bloque")

        // 2. Paquete como lo escribe quien comparte y lectura como la hace quien mira.
        var out = BinaryOut()
        out.u8(UInt8(ascii: "S")); out.i32(Int32(w)); out.i32(Int32(h))
        out.u8(UInt8(ascii: "F")); out.i32(10); out.i32(20); out.i32(Int32(delta.count))
        for t in delta { out.u16(UInt16(t.x)); out.u16(UInt16(t.y)); out.u16(UInt16(t.w)); out.u16(UInt16(t.h)); out.i32(Int32(t.jpeg.count)); out.bytes(t.jpeg) }
        let reader = MemoryStream(out.data)
        let input = BinaryIn(reader)
        do {
            check(try input.u8() == UInt8(ascii: "S") && Int(try input.i32()) == w && Int(try input.i32()) == h, "cabecera S")
            check(try input.u8() == UInt8(ascii: "F") && (try input.i32()) == 10 && (try input.i32()) == 20, "cabecera F con cursor")
            let count = Int(try input.i32())
            let x = Int(try input.u16()), y = Int(try input.u16()), tw = Int(try input.u16()), th = Int(try input.u16())
            let jpeg = try input.bytes(Int(try input.i32()))
            let image = CGImageSourceCreateWithData(jpeg as CFData, nil).flatMap { CGImageSourceCreateImageAtIndex($0, 0, nil) }
            check(count == 1 && image?.width == tw && image?.height == th && x == 128 && y == 64, "el bloque se decodifica con su tamaño (\(tw) × \(th))")
            if let image, let context = CGContext(data: nil, width: tw, height: th, bitsPerComponent: 8, bytesPerRow: tw * 4, space: CGColorSpaceCreateDeviceRGB(),
                                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) {
                context.draw(image, in: CGRect(x: 0, y: 0, width: tw, height: th))
                let p = context.data!.assumingMemoryBound(to: UInt8.self)
                let row = 80 - 64, col = 150 - 128  // la memoria del bitmap va de arriba abajo
                let g = p + (row * tw + col) * 4
                check(abs(Int(g[0]) - 20) < 25 && abs(Int(g[1]) - 200) < 25, "el color del cambio llega bien (\(g[0]),\(g[1]),\(g[2]))")
            }
        } catch { check(false, "lectura: \(error)") }

        // 3. Trazos y entrada: ida y vuelta.
        let op = AnnotationOp(kind: AnnotationOp.begin, id: 42, tool: .arrow, color: 0xFFF25C45, xs: [1, 65535], ys: [2, 3])
        let back = AnnotationOp.parse(op.bytes())
        check(back?.id == 42 && back?.tool == .arrow && back?.xs == [1, 65535] && back?.color == 0xFFF25C45, "trazo: ida y vuelta")
        check(op.bytes().count == 1 + 4 + 1 + 4 + 2 + 8, "trazo: mismo tamaño que en Windows")
        let key = RemoteInputEvent(type: RemoteInputEvent.keyDown, key: 0x43)
        check(key.bytes() == Data([UInt8(ascii: "I"), 5, 0x43, 0]), "tecla: mismo formato que en Windows")
        check(KeyMap.vkToMac[0x43] == 8 && KeyMap.macToVK[8] == 0x43 && KeyMap.vkToMac[0x11] == 55, "teclas: C y Ctrl→⌘")
        return ok
    }
}

/// Flujo en memoria para leer paquetes en las pruebas.
final class MemoryStream: ByteStream {
    private let data: Data
    private var offset = 0
    init(_ data: Data) { self.data = data }
    func read(into buffer: UnsafeMutableRawPointer, count: Int) throws -> Int {
        let take = min(count, data.count - offset)
        data.withUnsafeBytes { buffer.copyMemory(from: $0.baseAddress! + offset, byteCount: take) }
        offset += take
        return take
    }
    func write(_ data: Data) throws {}
    func flush() throws {}
    func close() {}
}
