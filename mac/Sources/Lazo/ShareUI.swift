import AppKit
import LazoCore
import ScreenCaptureKit
import SwiftUI

// MARK: - Elegir qué compartir y con quién

struct SharePickerView: View {
    @ObservedObject var state: LauncherState
    @ObservedObject var model = AppModel.shared
    @ObservedObject var prefs = Prefs.shared
    @State private var sources: [ShareSource] = []
    @State private var chosen: UUID?
    @State private var targets = Set<String>()
    @State private var error: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            PanelHeader(title: "Compartir pantalla", state: state)
            if let share = model.share {
                VStack(alignment: .leading, spacing: 6) {
                    Text("Compartiendo " + share.source.label).font(.lazo(13, .semibold))
                    Text(share.viewerNames.isEmpty ? "Esperando…" : share.viewerNames.joined(separator: ", ")).font(.lazo(12)).opacity(0.7)
                    if let controller = share.controller { Text("Controla " + controller).font(.lazo(12)).opacity(0.7) }
                    HStack {
                        ForEach(model.peers.filter { !share.viewerNames.contains($0.name) }) { peer in
                            PillButton(title: "Invitar a " + peer.name) { share.invite(peer) }
                        }
                        Spacer()
                        PillButton(title: "Detener", danger: true) { share.stop() }
                    }
                }.padding(12).surface(16)
            } else if !Permissions.screenRecording {
                VStack(alignment: .leading, spacing: 8) {
                    Text("macOS necesita tu permiso para compartir la pantalla.").font(.lazo(12.5))
                    Text("Ajustes del Sistema › Privacidad y seguridad › Grabación de pantalla y audio del sistema › activa Lazo. Puede pedir reabrir Lazo.")
                        .font(.lazo(11)).opacity(0.65).fixedSize(horizontal: false, vertical: true)
                    PillButton(title: "Dar permiso", primary: true) { Permissions.requestScreenRecording() }
                }.padding(12).surface(16)
            } else {
                SectionLabel(text: "Qué compartir")
                ScrollView {
                    VStack(spacing: 2) {
                        ForEach(sources) { source in
                            Button { chosen = source.id } label: {
                                HStack(spacing: 10) {
                                    Image(systemName: source.display != nil ? "display" : "macwindow").frame(width: 22)
                                    VStack(alignment: .leading, spacing: 1) {
                                        Text(source.label).font(.lazo(12.5, .medium)).lineLimit(1)
                                        Text(source.detail).font(.lazo(10.5)).opacity(0.6)
                                    }
                                    Spacer()
                                    if chosen == source.id { Image(systemName: "checkmark.circle.fill").foregroundStyle(prefs.accentColor) }
                                }
                                .padding(.horizontal, 10).padding(.vertical, 6)
                                .background(RoundedRectangle(cornerRadius: 10).fill(chosen == source.id ? prefs.palette.soft : .clear))
                                .contentShape(Rectangle())
                            }.buttonStyle(.plain)
                        }
                    }
                }.frame(maxHeight: 220)
                SectionLabel(text: "Con quién")
                if model.peers.isEmpty { Text("Nadie conectado en esta red.").font(.lazo(12)).opacity(0.6) }
                FlowChips(peers: model.peers, selected: $targets)
                if let error { Text(error).font(.lazo(11.5)).foregroundStyle(Color(hex: "#E5675A")) }
                HStack {
                    Spacer()
                    PillButton(title: "Compartir", primary: true) {
                        guard let source = sources.first(where: { $0.id == chosen }) else { error = "Elige una pantalla o una ventana."; return }
                        let peers = model.peers.filter { targets.contains($0.id) }
                        guard !peers.isEmpty else { error = "Elige al menos una persona."; return }
                        ShareStarter.start(source, peers)
                        LauncherController.shared.hide()
                    }
                }
            }
        }
        .panelFrame(400)
        .onAppear { if let id = model.shareTarget { targets = [id]; model.shareTarget = nil } }
        .task {
            guard Permissions.screenRecording else { return }
            do {
                sources = try await ShareSource.load()
                if chosen == nil { chosen = sources.first?.id }
            } catch { self.error = error.localizedDescription }
        }
    }
}

struct FlowChips: View {
    let peers: [Peer]
    @Binding var selected: Set<String>
    @ObservedObject var prefs = Prefs.shared

    var body: some View {
        HStack(spacing: 6) {
            ForEach(peers) { peer in
                Button {
                    if selected.contains(peer.id) { selected.remove(peer.id) } else { selected.insert(peer.id) }
                } label: {
                    HStack(spacing: 6) {
                        Avatar(name: peer.name, photo: peer.photo, size: 22)
                        Text(peer.name).font(.lazo(12, .medium))
                    }
                    .padding(.leading, 4).padding(.trailing, 10).padding(.vertical, 4)
                    .background(Capsule().fill(selected.contains(peer.id) ? prefs.palette.chip : prefs.palette.soft))
                    .foregroundStyle(selected.contains(peer.id) ? prefs.palette.chipText : prefs.palette.ink)
                }.buttonStyle(.plain)
            }
        }
    }
}

// MARK: - Arranque y estado de la transmisión

@MainActor
enum ShareStarter {
    private static var overlay: ShareOverlayWindow?
    private static var bar: ShareBarPanel?
    struct Prompt {
        let viewer: Int
        let panel: FloatingPanel
        let accept: () -> Void
        let reject: () -> Void
    }
    private static var prompts: [Prompt] = []

    /// Desde el chat: comparte la pantalla principal con esa persona (o la invita si ya se comparte).
    static func startQuick(with peer: Peer) {
        if let share = AppModel.shared.share { share.invite(peer); return }
        guard Permissions.screenRecording else {
            LauncherController.shared.show(route: .share)
            return
        }
        Task {
            guard let source = try? await ShareSource.load().first else { return }
            start(source, [peer])
        }
    }

    static func start(_ source: ShareSource, _ peers: [Peer], remote: (SecureConnection, String)? = nil) {
        guard AppModel.shared.share == nil else { remote?.0.close(); return }
        let model = AppModel.shared
        let session = ScreenShareSession(engine: model.engine, source: source)
        let overlay = ShareOverlayWindow(source: source)
        let bar = ShareBarPanel(session: session, overlay: overlay)
        overlay.annotations.local = { session.annotate($0) }
        overlay.drawingChanged = { session.inputPaused = $0 }
        session.annotated = { overlay.annotations.apply($0) }
        session.notice = { model.say($0) }
        session.changed = { update(session) }
        session.permissionRequested = { id, name, kind in ask(session, id, name, kind) }
        session.viewerLeft = { id in for prompt in prompts where prompt.viewer == id { prompt.reject() } }
        session.pointerMoved = { overlay.pointers.apply($0) }
        session.ended = { ended(session) }
        self.overlay = overlay
        self.bar = bar
        model.share = session
        for peer in peers { session.invite(peer) }
        if let remote { session.attachRemote(remote.0, name: remote.1) }
        Task {
            do {
                try await session.start()
                overlay.orderFrontRegardless()
                bar.show()
                update(session)
                model.say(peers.count == 1 ? "Invitación enviada a \(peers[0].name)" : "Invitaciones enviadas")
            } catch {
                model.say("No se pudo compartir: " + error.localizedDescription)
                session.stop()
                ended(session)
            }
        }
    }

    /// Alguien entró con la contraseña de control remoto: se comparte la pantalla principal sin preguntar.
    static func startRemote(_ connection: SecureConnection, _ name: String) {
        guard AppModel.shared.share == nil else { connection.close(); return }
        guard Permissions.screenRecording else {
            AppModel.shared.say("Control remoto: \(name) intentó entrar, pero Lazo no tiene permiso de grabación de pantalla.")
            Permissions.requestScreenRecording()
            connection.close()
            return
        }
        if !Permissions.accessibility {
            Permissions.requestAccessibility()
            AppModel.shared.say("Para que \(name) controle este Mac, activa Lazo en Accesibilidad.")
        }
        Task {
            guard let source = try? await ShareSource.load().first else { connection.close(); return }
            start(source, [], remote: (connection, name))
        }
    }

    static func update(_ session: ScreenShareSession) {
        guard AppModel.shared.share === session else { return }
        let names = session.viewerNames
        var text = session.remoteViewer.map { "Control remoto activo: \($0)" } ?? "Compartiendo " + session.source.label
        if text.count > 46 { text = String(text.prefix(45)) + "…" }
        text += names.isEmpty ? " · esperando…" : " · " + (names.count == 1 ? names[0] : "\(names.count) personas") + " · \(Int(session.fps.rounded())) fps"
        if let controller = session.controller { text += " · controla " + controller }
        let control = session.controlActive
        // Con alguien controlando no se dibuja: los clics remotos acabarían en la capa de dibujo.
        if control, overlay?.drawing == true { overlay?.setDrawing(false) }
        bar?.update(text: text, granted: session.anyGranted, drawAllowed: !control)
        AppModel.shared.objectWillChange.send()
    }

    private static func ended(_ session: ScreenShareSession) {
        guard AppModel.shared.share === session else { return }
        AppModel.shared.share = nil
        overlay?.close(); overlay = nil
        bar?.close(); bar = nil
        for prompt in prompts { prompt.reject() }
        prompts.removeAll()
        AppModel.shared.say("Dejaste de compartir la pantalla")
    }

    /// Alguien que mira pide dibujar o controlar. Para el control se elige el modo.
    private static func ask(_ session: ScreenShareSession, _ viewerId: Int, _ name: String, _ kind: UInt8) {
        let control = kind == SharePermission.control
        if control && !Permissions.accessibility {
            Permissions.requestAccessibility()
            AppModel.shared.say("Para que \(name) controle este Mac, activa Lazo en Accesibilidad y vuelve a aceptar.")
        }
        var panel: FloatingPanel!
        var done = false
        let respond: (Bool, Bool) -> Void = { accept, full in
            guard !done else { return }
            done = true
            prompts.removeAll { $0.panel === panel }
            panel.fadeOut()
            session.answer(viewerId, kind: kind, accept: accept, full: full)
            guard accept else { return }
            AppModel.shared.say(!control ? "\(name) puede dibujar en tu pantalla" : full ? "\(name) tiene el control total del equipo" : "\(name) puede controlar la ventana activa")
        }
        let view = Card {
            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 10) {
                    Circle().fill(control ? Color(hex: "#D9443A") : Prefs.shared.accentColor).frame(width: 36, height: 36)
                        .overlay(Image(systemName: control ? "cursorarrow" : "pencil").foregroundStyle(.white))
                    Text(name + (control ? " quiere controlar tu equipo" : " quiere dibujar en tu pantalla")).font(.lazo(13, .semibold))
                }
                Text(control
                     ? "Ventana activa: solo la ventana que compartes (o la que tienes al frente). Control total: mouse y teclado en todo el equipo. Quítalo cuando quieras desde la barra superior."
                     : "Sus trazos aparecerán sobre tu pantalla y los verán todos los que miran.")
                    .font(.lazo(11.5)).opacity(0.7).fixedSize(horizontal: false, vertical: true)
                HStack {
                    Spacer()
                    PillButton(title: "Rechazar") { respond(false, false) }
                    if control {
                        PillButton(title: "Control total") { respond(true, true) }.help("Control total del equipo")
                        PillButton(title: "Ventana activa", primary: true) { respond(true, false) }
                    } else {
                        PillButton(title: "Permitir", primary: true) { respond(true, false) }
                    }
                }
            }
        }
        panel = FloatingPanel(view, width: control ? 440 : 380)
        FloatingPanel.Stack.add(panel)
        panel.fadeIn()
        // Atajos de emergencia: aceptar es lo prudente (control: solo la ventana activa).
        prompts.append(Prompt(viewer: viewerId, panel: panel, accept: { respond(true, false) }, reject: { respond(false, false) }))
        DispatchQueue.main.asyncAfter(deadline: .now() + 60) { respond(false, false) }
    }

    // MARK: Peticiones de unirse o de compartir

    /// Alguien que no mira pide unirse a lo que se comparte. Si se acepta, se le envía la invitación de siempre.
    static func askWatch(_ peerId: String, _ name: String) {
        notify(id: -1, symbol: "eye", title: "\(name) quiere ver tu pantalla",
               detail: "Verá lo mismo que los demás que miran. Puedes dejar de compartir cuando quieras.", accept: "Permitir", reject: "Rechazar") {
            guard let share = AppModel.shared.share, let peer = AppModel.shared.peer(peerId) else { return }
            share.invite(peer)
        }
    }

    /// Alguien te pide que compartas tu pantalla. Si aceptas, eliges qué compartir; nada se ve hasta que pulses Compartir.
    static func askShare(_ peerId: String, _ name: String) {
        notify(id: -2, symbol: "display", title: "\(name) te pide que compartas tu pantalla",
               detail: "Elige qué compartir; nada se ve hasta que pulses Compartir.", accept: "Elegir qué compartir", reject: "Ahora no") {
            AppModel.shared.shareTarget = peerId
            LauncherController.shared.show(route: .share)
        }
    }

    private static func notify(id: Int, symbol: String, title: String, detail: String, accept: String, reject: String, onAccept: @escaping () -> Void) {
        var panel: FloatingPanel!
        var done = false
        let respond: (Bool) -> Void = { ok in
            guard !done else { return }
            done = true
            prompts.removeAll { $0.panel === panel }
            panel.fadeOut()
            if ok { onAccept() }
        }
        let view = Card {
            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 10) {
                    Circle().fill(Prefs.shared.accentColor).frame(width: 36, height: 36).overlay(Image(systemName: symbol).foregroundStyle(.white))
                    Text(title).font(.lazo(13, .semibold))
                }
                Text(detail).font(.lazo(11.5)).opacity(0.7).fixedSize(horizontal: false, vertical: true)
                HStack {
                    Spacer()
                    PillButton(title: reject) { respond(false) }
                    PillButton(title: accept, primary: true) { respond(true) }
                }
                Text("⌃⌥A: aceptar · ⌃⌥R: rechazar").font(.lazo(10.5)).opacity(0.5).frame(maxWidth: .infinity, alignment: .trailing)
            }
        }
        panel = FloatingPanel(view, width: 400)
        FloatingPanel.Stack.add(panel)
        panel.fadeIn()
        NSSound(named: "Pop")?.play()
        prompts.append(Prompt(viewer: id, panel: panel, accept: { respond(true) }, reject: { respond(false) }))
        DispatchQueue.main.asyncAfter(deadline: .now() + 60) { respond(false) }
    }

    /// Atajos de emergencia ⌃⌥ izquierdo + letra, solo desde el teclado físico: Q quita los permisos, X deja de compartir,
    /// A acepta la petición más antigua y R la rechaza. Devuelve si el atajo se atendió.
    @discardableResult
    static func chord(_ letter: Character) -> Bool {
        switch letter {
        case "q", "x":
            guard let share = AppModel.shared.share else { return false }
            if letter == "q" { share.revokeAll(); AppModel.shared.say("Quitaste todos los permisos") } else { share.stop() }
            return true
        case "a", "r":
            guard let first = prompts.first else { return false }
            if letter == "a" { first.accept() } else { first.reject() }
            return true
        default: return false
        }
    }
}

// MARK: - Capa de dibujo sobre lo compartido

/// Ventana transparente exactamente sobre la fuente, excluida de la captura. Deja pasar el mouse salvo al dibujar.
final class ShareOverlayWindow: NSWindow {
    let annotations = AnnotationView()
    /// Punteros de quienes miran y dibujan o controlan, sobre lo compartido.
    let pointers = PointerView()
    private let source: ShareSource
    private var follow: Timer?
    var drawingChanged: ((Bool) -> Void)?
    var drawingEnded: (() -> Void)?
    var drawing: Bool { annotations.drawing }

    init(source: ShareSource) {
        self.source = source
        super.init(contentRect: .zero, styleMask: [.borderless], backing: .buffered, defer: false)
        level = .floating
        backgroundColor = .clear
        isOpaque = false
        hasShadow = false
        ignoresMouseEvents = true
        sharingType = .none
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        contentView = annotations
        pointers.frame = annotations.bounds
        pointers.autoresizingMask = [.width, .height]
        annotations.addSubview(pointers)
        place()
        follow = Timer.scheduledTimer(withTimeInterval: source.window != nil ? 0.12 : 1, repeats: true) { [weak self] _ in self?.place() }
    }

    override var canBecomeKey: Bool { annotations.drawing }

    override func cancelOperation(_ sender: Any?) { setDrawing(false) }

    func setDrawing(_ on: Bool) {
        guard annotations.drawing != on else { return }
        annotations.drawing = on
        ignoresMouseEvents = !on
        if on { NSApp.activate(ignoringOtherApps: true); makeKeyAndOrderFront(nil); makeFirstResponder(annotations) }
        drawingChanged?(on)
        if !on { drawingEnded?() }
    }

    /// Se coloca sobre la fuente (en coordenadas de AppKit); las ventanas se siguen al moverse.
    private func place() {
        guard let frame = source.currentFrame() else { alphaValue = 0; if drawing { setDrawing(false) }; return }
        alphaValue = 1
        let primary = NSScreen.screens.first?.frame.height ?? 0
        let target = NSRect(x: frame.minX, y: primary - frame.maxY, width: frame.width, height: frame.height)
        if target != self.frame { setFrame(target, display: true) }
    }

    override func close() {
        follow?.invalidate()
        super.close()
    }
}

// MARK: - Barra superior de quien comparte

final class ShareBarState: ObservableObject {
    @Published var text = ""
    @Published var granted = false
    @Published var drawAllowed = true
    @Published var drawing = false
    @Published var hasStrokes = false
}

final class ShareBarPanel {
    private let panel: FloatingPanel
    private let state = ShareBarState()

    init(session: ScreenShareSession, overlay: ShareOverlayWindow) {
        let state = self.state
        overlay.drawingChanged = { [weak session] on in session?.inputPaused = on; state.drawing = on }
        overlay.annotations.strokesChanged = { [weak overlay] in state.hasStrokes = overlay?.annotations.hasStrokes ?? false }
        panel = FloatingPanel(ShareBarView(state: state, overlay: overlay, session: session), width: 640, activates: true)
        panel.isMovableByWindowBackground = true
        panel.level = .statusBar
    }

    func show() {
        let screen = NSScreen.main ?? NSScreen.screens[0]
        panel.setFrameOrigin(NSPoint(x: screen.visibleFrame.midX - panel.frame.width / 2, y: screen.visibleFrame.maxY - panel.frame.height - 6))
        panel.orderFrontRegardless()
    }

    func update(text: String, granted: Bool, drawAllowed: Bool) {
        state.text = text
        state.granted = granted
        state.drawAllowed = drawAllowed
    }

    func close() { panel.orderOut(nil) }
}

private struct ShareBarView: View {
    @ObservedObject var state: ShareBarState
    let overlay: ShareOverlayWindow
    let session: ScreenShareSession

    var body: some View {
        VStack(spacing: 4) {
            HStack(spacing: 8) {
                Circle().fill(Color(hex: "#E5675A")).frame(width: 8, height: 8)
                Text(state.text).font(.lazo(12, .medium)).lineLimit(1).frame(maxWidth: .infinity, alignment: .leading)
                Button { overlay.setDrawing(!overlay.drawing) } label: {
                    Label(state.drawing ? "Terminar" : "Dibujar", systemImage: "pencil").font(.lazo(12, .semibold))
                        .padding(.horizontal, 10).padding(.vertical, 5).background(Capsule().fill(Color.white.opacity(state.drawing ? 0.35 : 0.15)))
                }.buttonStyle(.plain).disabled(!state.drawAllowed)
                    .help(state.drawAllowed ? "Escribir sobre la pantalla (Esc para terminar)" : "Quita el control para poder dibujar")
                if state.hasStrokes {
                    Button { overlay.annotations.clearAll() } label: {
                        Label("Borrar dibujos", systemImage: "trash").font(.lazo(12, .semibold)).padding(.horizontal, 10).padding(.vertical, 5)
                            .background(Capsule().fill(Color.white.opacity(0.15)))
                    }.buttonStyle(.plain).help("Quita todos los trazos de la pantalla, incluidos los de quienes miran")
                }
                if state.granted {
                    Button { session.revokeAll() } label: {
                        Text("Quitar permisos").font(.lazo(12, .semibold)).padding(.horizontal, 10).padding(.vertical, 5)
                            .background(Capsule().fill(Color.white.opacity(0.15)))
                    }.buttonStyle(.plain).help("Nadie podrá dibujar ni controlar tu equipo hasta que vuelvas a permitirlo")
                }
                Button { session.stop() } label: {
                    Text("Detener").font(.lazo(12, .semibold)).padding(.horizontal, 12).padding(.vertical, 5).background(Capsule().fill(Color(hex: "#D9443A")))
                }.buttonStyle(.plain)
            }
            if state.drawing {
                AnnotationToolbar(view: overlay.annotations, clearTip: "Borrar todos los trazos") { overlay.annotations.clearAll() }
            }
        }
        .foregroundStyle(.white)
        .padding(.horizontal, 14).padding(.vertical, 6)
        .background(RoundedRectangle(cornerRadius: 20).fill(Color(hex: "#F22A0E12")))
        .padding(6)
    }
}
