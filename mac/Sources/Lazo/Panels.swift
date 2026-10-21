import AppKit
import LazoCore
import SwiftUI

/// Panel flotante sin bordes con contenido SwiftUI. No aparece en capturas ni en la pantalla compartida.
final class FloatingPanel: NSPanel {
    init<Content: View>(_ content: Content, width: CGFloat, activates: Bool = false) {
        fallbackWidth = width
        super.init(contentRect: NSRect(x: 0, y: 0, width: width, height: 120),
                   styleMask: activates ? [.borderless] : [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        isFloatingPanel = true
        level = .floating
        backgroundColor = .clear
        isOpaque = false
        hasShadow = true
        sharingType = .none
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        hidesOnDeactivate = false
        let host = NSHostingView(rootView: content.frame(width: width))
        // Con `.preferredContentSize` el tamaño propio de la vista queda en cero en esta versión de macOS y el aviso no se ve.
        host.sizingOptions = [.intrinsicContentSize]
        self.host = host
        contentView = host
        refit()
    }

    private var host: NSView?
    private let fallbackWidth: CGFloat

    /// Ajusta la altura al contenido (crece al llegar más archivos al mismo aviso) y vuelve a colocar la pila.
    func refit() {
        guard let host else { return }
        host.layoutSubtreeIfNeeded()
        let fit = host.fittingSize
        let size = fit.width > 1 && fit.height > 1 ? fit : NSSize(width: fallbackWidth, height: 120)
        guard abs(frame.height - size.height) > 0.5 || abs(frame.width - size.width) > 0.5 else { return }
        setContentSize(size)
        if let index = Stack.panels.firstIndex(where: { $0 === self }) { placeBottomRight(index: index) }
    }

    override var canBecomeKey: Bool { true }

    /// Abajo a la derecha de la pantalla con el cursor, apilado sobre los anteriores.
    func placeBottomRight(index: Int) {
        let screen = NSScreen.screens.first { NSMouseInRect(NSEvent.mouseLocation, $0.frame, false) } ?? NSScreen.main ?? NSScreen.screens[0]
        let area = screen.visibleFrame
        var y = area.minY + 16
        for panel in Stack.panels.prefix(index) { y += panel.frame.height + 8 }
        setFrameOrigin(NSPoint(x: area.maxX - frame.width - 16, y: y))
    }

    func fadeIn() {
        alphaValue = 0
        refit()
        orderFrontRegardless()
        DispatchQueue.main.async { [weak self] in self?.refit() }
        DiagLog.write("panel flotante mostrado en \(frame) visible=\(isVisible)")
        NSAnimationContext.runAnimationGroup { $0.duration = 0.18; animator().alphaValue = 1 }
    }

    func fadeOut() {
        NSAnimationContext.runAnimationGroup({ $0.duration = 0.18; animator().alphaValue = 0 }) {
            self.orderOut(nil)
            Stack.remove(self)
        }
    }

    enum Stack {
        static var panels: [FloatingPanel] = []
        static func add(_ panel: FloatingPanel) {
            panels.append(panel)
            panel.placeBottomRight(index: panels.count - 1)
        }
        static func remove(_ panel: FloatingPanel) {
            panels.removeAll { $0 === panel }
            for (i, p) in panels.enumerated() { p.placeBottomRight(index: i) }
        }
    }
}

/// Superficie común de los avisos: fondo del tema, borde fino y esquinas de 20 pt.
struct Card<Content: View>: View {
    @ObservedObject var prefs = Prefs.shared
    let content: Content
    init(@ViewBuilder _ content: () -> Content) { self.content = content() }

    var body: some View {
        let p = prefs.palette
        content
            .padding(14)
            .background(RoundedRectangle(cornerRadius: 20).fill(LinearGradient(colors: [p.shell0, p.shell2], startPoint: .top, endPoint: .bottom)))
            .overlay(RoundedRectangle(cornerRadius: 20).stroke(p.line, lineWidth: 1))
            .foregroundStyle(p.ink)
            .padding(10)
            .environment(\.colorScheme, p.dark ? .dark : .light)
    }
}

struct PillButton: View {
    @ObservedObject var prefs = Prefs.shared
    let title: String
    var primary = false
    var danger = false
    let action: () -> Void

    var body: some View {
        let p = prefs.palette
        Button(action: action) {
            Text(title).font(.lazo(12.5, .semibold)).padding(.horizontal, 14).padding(.vertical, 7)
                .background(Capsule().fill(danger ? Color(hex: "#D9443A") : primary ? prefs.accentColor : p.soft))
                .foregroundStyle(danger || primary ? Color.white : p.ink)
        }
        .buttonStyle(.plain)
    }
}

// MARK: - Avisos breves

enum ToastPanel {
    private static var panel: FloatingPanel?
    private static var timer: Timer?

    @MainActor static func show(_ text: String) {
        panel?.orderOut(nil)
        let view = Card { Text(text).font(.lazo(12.5, .medium)).lineLimit(3).frame(maxWidth: .infinity, alignment: .leading) }
        let p = FloatingPanel(view, width: 340)
        let screen = NSScreen.screens.first { NSMouseInRect(NSEvent.mouseLocation, $0.frame, false) } ?? NSScreen.main!
        p.setFrameOrigin(NSPoint(x: screen.visibleFrame.midX - p.frame.width / 2, y: screen.visibleFrame.minY + 90))
        p.ignoresMouseEvents = true
        p.fadeIn()
        panel = p
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: 3.2, repeats: false) { _ in
            Task { @MainActor in
                guard let current = panel else { return }
                NSAnimationContext.runAnimationGroup({ $0.duration = 0.25; current.animator().alphaValue = 0 }) { current.orderOut(nil) }
            }
        }
    }
}

// MARK: - Emparejamiento

/// Verificación opcional: las dos personas ven el mismo código y lo confirman. Si no coincide, alguien se interpuso en la red.
final class PairingState: ObservableObject {
    @Published var status = ""
    @Published var decided = false
}

enum PairingPanel {
    @MainActor static func show(_ request: PairingRequest, answer: @escaping (Bool) -> Void) {
        let state = PairingState()
        var answered = false
        var panel: FloatingPanel!
        func finish(_ value: Bool) {
            guard !answered else { return }
            answered = true
            state.decided = true
            answer(value)
            if !value { close() }
        }
        func close() { DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) { panel.fadeOut() } }
        let code = request.code
        let view = Card {
            VStack(alignment: .leading, spacing: 6) {
                Text(request.incoming ? "\(request.peerName) quiere verificar la conexión con este equipo" : "Verificar a \(request.peerName)")
                    .font(.lazo(13, .semibold))
                Text("Comprueba con la otra persona que ve exactamente el mismo código. Si no coincide, cancela: alguien se interpuso en la red.")
                    .font(.lazo(11.5)).opacity(0.7).fixedSize(horizontal: false, vertical: true)
                Text(String(code.prefix(3)) + " " + String(code.suffix(3)))
                    .font(.system(size: 34, weight: .semibold, design: .rounded)).monospacedDigit()
                    .frame(maxWidth: .infinity).padding(.top, 8)
                Text("Huella " + LazoCrypto.shortFingerprint(request.fingerprint)).font(.lazo(10.5)).opacity(0.6).frame(maxWidth: .infinity)
                PairingStatus(state: state).frame(maxWidth: .infinity)
                HStack {
                    Spacer()
                    PillButton(title: "Cancelar") { finish(false) }
                    PillButton(title: "Coincide", primary: true) {
                        finish(true)
                        if state.status.isEmpty { state.status = "Esperando a \(request.peerName)…" }
                    }
                }.padding(.top, 6)
            }
        }
        panel = FloatingPanel(view, width: 380, activates: !request.incoming)
        FloatingPanel.Stack.add(panel)
        panel.fadeIn()
        if !request.incoming { NSApp.activate(ignoringOtherApps: true); panel.makeKey() }
        request.onRemote { yes in
            DispatchQueue.main.async {
                if !yes {
                    state.status = answered ? "No se verificó." : "\(request.peerName) canceló."
                    finish(false)
                    close()
                } else if answered {
                    state.status = "Equipo verificado."
                    close()
                } else {
                    state.status = "\(request.peerName) ya confirmó."
                }
            }
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 85) { finish(false) }
        _ = state
    }
}

private struct PairingStatus: View {
    @ObservedObject var state: PairingState
    var body: some View { Text(state.status).font(.lazo(11.5)).opacity(0.7) }
}

// MARK: - Recepción de archivos

final class IncomingItem: ObservableObject, Identifiable {
    let id: UUID
    let name: String
    let size: Int64
    let preview: NSImage?
    @Published var progress: Double = 0
    @Published var decided = false
    @Published var accepted = false
    @Published var path: URL?
    @Published var message = ""
    var answer: ((Bool) -> Void)?

    init(offer: Offer, answer: @escaping (Bool) -> Void) {
        id = offer.id
        name = offer.fileName
        size = offer.size
        preview = offer.preview.flatMap { NSImage(data: $0) }
        self.answer = answer
    }
}

final class IncomingGroup: ObservableObject {
    let peerId: String
    let sender: String
    @Published var items: [IncomingItem] = []
    var panel: FloatingPanel?
    var timeout: Timer?

    init(peerId: String, sender: String) {
        self.peerId = peerId
        self.sender = sender
    }

    var pending: [IncomingItem] { items.filter { !$0.decided } }
    var allDone: Bool { items.allSatisfy { $0.decided && (!$0.accepted || $0.path != nil || !$0.message.isEmpty) } }
}

/// Alertas de archivos entrantes abajo a la derecha, sin robar el foco. Varios del mismo equipo se agrupan.
@MainActor
final class ReceiveCenter {
    static let shared = ReceiveCenter()
    private var groups: [IncomingGroup] = []

    func offer(_ offer: Offer, answer: @escaping (Bool) -> Void) {
        let item = IncomingItem(offer: offer, answer: answer)
        if let group = groups.first(where: { $0.peerId == offer.peerId && !$0.pending.isEmpty }) {
            group.items.append(item)
            DispatchQueue.main.async { group.panel?.refit() }
            return
        }
        let group = IncomingGroup(peerId: offer.peerId, sender: offer.sender)
        group.items.append(item)
        groups.append(group)
        let panel = FloatingPanel(IncomingView(group: group, decide: { [weak self] in self?.decide(group, $0) },
                                               close: { [weak self] in self?.close(group) }), width: 360)
        group.panel = panel
        FloatingPanel.Stack.add(panel)
        panel.fadeIn()
        group.timeout = Timer.scheduledTimer(withTimeInterval: 90, repeats: false) { _ in
            Task { @MainActor in
                if !group.pending.isEmpty { ReceiveCenter.shared.decide(group, false) }
            }
        }
    }

    func decide(_ group: IncomingGroup, _ accept: Bool) {
        for item in group.pending {
            item.decided = true
            item.accepted = accept
            item.answer?(accept)
            item.answer = nil
        }
        group.timeout?.invalidate()
        if !accept { close(group) }
    }

    func progress(_ id: UUID, _ value: Double) {
        item(id)?.progress = value
    }

    func finished(_ id: UUID, _ message: String, _ url: URL?) {
        guard let group = groups.first(where: { $0.items.contains { $0.id == id } }), let item = item(id) else { return }
        item.message = message
        item.path = url
        DispatchQueue.main.async { group.panel?.refit() }
        item.progress = 1
        if let url {
            TransferLog.add(incoming: true, peer: group.sender, name: url.lastPathComponent, path: url.path)
            let name = AppModel.shared.peer(group.peerId)?.name ?? group.sender
            ChatStore.appendFile(group.peerId, name: name, mine: false, path: url.path)
            AppModel.shared.chatVersion += 1
        }
        group.objectWillChange.send()
    }

    private func item(_ id: UUID) -> IncomingItem? {
        groups.lazy.flatMap { $0.items }.first { $0.id == id }
    }

    func close(_ group: IncomingGroup) {
        groups.removeAll { $0 === group }
        group.panel?.fadeOut()
    }

    /// Muestra el archivo en Finder o lo abre; lo que puede ejecutar código pide confirmación.
    static func open(_ url: URL, sender: String, reveal: Bool) {
        if reveal { NSWorkspace.shared.activateFileViewerSelecting([url]); return }
        if Quarantine.isRisky(url) {
            let alert = NSAlert()
            alert.messageText = "\(url.lastPathComponent) puede ejecutar programas"
            alert.informativeText = "Ábrelo solo si confías en \(sender) y lo esperabas. macOS lo revisará antes de abrirlo."
            alert.alertStyle = .warning
            alert.addButton(withTitle: "Cancelar")
            alert.addButton(withTitle: "Abrir de todas formas")
            NSApp.activate(ignoringOtherApps: true)
            guard alert.runModal() == .alertSecondButtonReturn else { return }
        }
        NSWorkspace.shared.open(url)
    }
}

struct IncomingView: View {
    @ObservedObject var group: IncomingGroup
    @ObservedObject var prefs = Prefs.shared
    let decide: (Bool) -> Void
    let close: () -> Void

    var body: some View {
        Card {
            VStack(alignment: .leading, spacing: 10) {
                HStack(alignment: .top, spacing: 10) {
                    if let image = group.items.first?.preview {
                        Image(nsImage: image).resizable().scaledToFill().frame(width: 52, height: 52).clipShape(RoundedRectangle(cornerRadius: 10))
                    } else {
                        RoundedRectangle(cornerRadius: 10).fill(prefs.palette.soft).frame(width: 52, height: 52)
                            .overlay(Image(systemName: "doc").font(.system(size: 20)).opacity(0.7))
                    }
                    VStack(alignment: .leading, spacing: 3) {
                        Text(group.items.count == 1 ? group.items[0].name : "\(group.items.count) archivos")
                            .font(.lazo(13, .semibold)).lineLimit(1).truncationMode(.middle)
                        Text("De \(group.sender) · " + ByteCountFormatter.string(fromByteCount: group.items.reduce(0) { $0 + $1.size }, countStyle: .file))
                            .font(.lazo(11.5)).opacity(0.7)
                        ForEach(group.items) { item in ItemRow(item: item, sender: group.sender) }
                    }
                    Spacer(minLength: 0)
                    Button(action: close) { Image(systemName: "xmark").font(.system(size: 11, weight: .semibold)) }.buttonStyle(.plain).opacity(0.6)
                }
                if !group.pending.isEmpty {
                    HStack {
                        Spacer()
                        PillButton(title: "Rechazar") { decide(false) }
                        PillButton(title: group.pending.count > 1 ? "Aceptar todos" : "Aceptar", primary: true) { decide(true) }
                    }
                }
            }
        }
    }
}

private struct ItemRow: View {
    @ObservedObject var item: IncomingItem
    let sender: String

    var body: some View {
        if item.accepted {
            VStack(alignment: .leading, spacing: 4) {
                if item.path == nil && item.message.isEmpty {
                    ProgressView(value: item.progress).progressViewStyle(.linear)
                } else if let path = item.path {
                    HStack(spacing: 8) {
                        PillButton(title: "Mostrar en carpeta") { ReceiveCenter.open(path, sender: sender, reveal: true) }
                        PillButton(title: "Abrir", primary: true) { ReceiveCenter.open(path, sender: sender, reveal: false) }
                    }
                } else {
                    Text(item.message).font(.lazo(11.5)).foregroundStyle(Color(hex: "#E5675A"))
                }
            }
        }
    }
}
