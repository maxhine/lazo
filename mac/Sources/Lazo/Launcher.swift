import AppKit
import LazoCore
import SwiftUI
import UniformTypeIdentifiers

enum Route: Equatable {
    case home, settings, chat, history, eyecare, share
}

@MainActor
final class LauncherState: ObservableObject {
    @Published var route: Route = .home
    @Published var query = ""
    @Published var chatPeer: String?
    @Published var shakeTick = 0
}

final class LauncherPanel: NSPanel {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
    override func cancelOperation(_ sender: Any?) {
        Task { @MainActor in LauncherController.shared.escape() }
    }
}

/// Ventana principal: emerge desde la base de la pantalla donde está el cursor y queda centrada.
/// Escape o un clic fuera la ocultan; la app sigue en la barra de menús.
@MainActor
final class LauncherController: NSObject, NSWindowDelegate {
    static let shared = LauncherController()
    let state = LauncherState()
    private var panel: LauncherPanel!
    private var screen: NSScreen?
    /// Mientras hay un diálogo de archivos abierto, perder el foco no oculta la ventana.
    var holding = 0

    func setup() {
        panel = LauncherPanel(contentRect: NSRect(x: 0, y: 0, width: 520, height: 300), styleMask: [.borderless, .fullSizeContentView],
                              backing: .buffered, defer: false)
        panel.isFloatingPanel = true
        panel.level = .floating
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = true
        panel.isMovableByWindowBackground = true
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.delegate = self
        let host = NSHostingView(rootView: LauncherView(state: state))
        host.sizingOptions = [.preferredContentSize]
        panel.contentView = host
        NotificationCenter.default.addObserver(forName: NSView.frameDidChangeNotification, object: host, queue: .main) { _ in
            Task { @MainActor in LauncherController.shared.keepAnchored() }
        }
        host.postsFrameChangedNotifications = true
    }

    var isVisible: Bool { panel.isVisible }

    func toggle() { isVisible ? hide() : show() }

    func show(route: Route? = nil) {
        if let route { state.route = route }
        screen = NSScreen.screens.first { NSMouseInRect(NSEvent.mouseLocation, $0.frame, false) } ?? NSScreen.main
        let size = panel.contentView?.fittingSize ?? panel.frame.size
        let target = anchoredFrame(size)
        if !panel.isVisible {
            panel.setFrame(target.offsetBy(dx: 0, dy: -40), display: false)
            panel.alphaValue = 0
        }
        NSApp.activate(ignoringOtherApps: true)
        panel.makeKeyAndOrderFront(nil)
        let reduce = NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
        NSAnimationContext.runAnimationGroup { context in
            context.duration = reduce ? 0 : 0.26
            context.timingFunction = CAMediaTimingFunction(controlPoints: 0.2, 0.9, 0.25, 1)
            panel.animator().setFrame(target, display: true)
            panel.animator().alphaValue = 1
        }
    }

    func hide() {
        guard panel.isVisible else { return }
        let reduce = NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
        NSAnimationContext.runAnimationGroup({ context in
            context.duration = reduce ? 0 : 0.18
            panel.animator().setFrame(panel.frame.offsetBy(dx: 0, dy: -30), display: true)
            panel.animator().alphaValue = 0
        }) {
            Task { @MainActor in
                self.panel.orderOut(nil)
                self.state.query = ""
                if self.state.route != .share { self.state.route = .home }
            }
        }
    }

    func escape() {
        if !state.query.isEmpty { state.query = ""; return }
        if state.route != .home { state.route = .home; return }
        hide()
    }

    private func anchoredFrame(_ size: NSSize) -> NSRect {
        let area = (screen ?? NSScreen.main!).visibleFrame
        return NSRect(x: area.midX - size.width / 2, y: area.minY + 24, width: size.width, height: size.height)
    }

    /// Al cambiar de tamaño, la ventana crece hacia arriba desde la base y sigue centrada.
    func keepAnchored() {
        guard panel.isVisible, let host = panel.contentView else { return }
        let size = host.fittingSize
        let frame = panel.frame
        guard abs(frame.width - size.width) > 0.5 || abs(frame.height - size.height) > 0.5 else { return }
        let next = NSRect(x: frame.midX - size.width / 2, y: frame.minY, width: size.width, height: size.height)
        NSAnimationContext.runAnimationGroup { $0.duration = NSWorkspace.shared.accessibilityDisplayShouldReduceMotion ? 0 : 0.2; panel.animator().setFrame(next, display: true) }
    }

    func windowDidResignKey(_ notification: Notification) {
        guard holding == 0 else { return }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.15) {
            Task { @MainActor in
                if self.holding == 0, !self.panel.isKeyWindow, NSApp.modalWindow == nil, !(NSApp.keyWindow is NSOpenPanel) { self.hide() }
            }
        }
    }

    func isViewingChat(_ id: String) -> Bool {
        panel.isVisible && state.route == .chat && state.chatPeer == id
    }

    func openChat(_ id: String?) {
        state.chatPeer = id
        show(route: .chat)
    }

    func shake() {
        if !panel.isVisible { show() }
        state.shakeTick += 1
        let origin = panel.frame.origin
        let offsets: [CGFloat] = [-10, 10, -8, 8, -5, 5, 0]
        for (i, dx) in offsets.enumerated() {
            DispatchQueue.main.asyncAfter(deadline: .now() + Double(i) * 0.04) { self.panel.setFrameOrigin(NSPoint(x: origin.x + dx, y: origin.y)) }
        }
    }

    /// Diálogo para elegir archivos sin que la ventana se oculte al perder el foco.
    func pickFiles(_ message: String) -> [URL] {
        holding += 1
        defer { holding -= 1 }
        let open = NSOpenPanel()
        open.allowsMultipleSelection = true
        open.canChooseDirectories = false
        open.message = message
        NSApp.activate(ignoringOtherApps: true)
        return open.runModal() == .OK ? open.urls : []
    }

    func pickImage() -> URL? {
        holding += 1
        defer { holding -= 1 }
        let open = NSOpenPanel()
        open.allowedContentTypes = [.image]
        NSApp.activate(ignoringOtherApps: true)
        return open.runModal() == .OK ? open.url : nil
    }
}

// MARK: - Vista raíz

struct LauncherView: View {
    @ObservedObject var state: LauncherState
    @ObservedObject var prefs = Prefs.shared
    @ObservedObject var model = AppModel.shared

    var body: some View {
        let p = prefs.palette
        VStack(spacing: 0) {
            switch state.route {
            case .home: HomeView(state: state)
            case .settings: SettingsView(state: state)
            case .chat: ChatView(state: state)
            case .history: HistoryView(state: state)
            case .eyecare: EyeCareView(state: state)
            case .share: SharePickerView(state: state)
            }
        }
        .padding(prefs.interface == .minimal && state.route == .home ? 10 : 16)
        .background(Shell(palette: p, glass: prefs.liquidGlass, hero: prefs.theme == .image))
        .clipShape(RoundedRectangle(cornerRadius: 24, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 24, style: .continuous).stroke(p.line, lineWidth: 1))
        .foregroundStyle(p.ink)
        .tint(prefs.accentColor)
        .environment(\.colorScheme, p.dark ? .dark : .light)
        .animation(NSWorkspace.shared.accessibilityDisplayShouldReduceMotion ? nil : .spring(response: 0.32, dampingFraction: 0.86), value: state.route)
        .padding(1)
    }
}

/// Fondo de la ventana: degradado del tema o, en «Imagen», la foto de inicio desenfocada.
struct Shell: View {
    let palette: Palette
    let glass: Bool
    let hero: Bool
    @ObservedObject var prefs = Prefs.shared

    var body: some View {
        ZStack {
            LinearGradient(colors: [palette.shell0, palette.shell1, palette.shell2], startPoint: .top, endPoint: .bottom)
            if hero, prefs.hasHero, let image = NSImage(contentsOf: prefs.heroURL) {
                Image(nsImage: image).resizable().scaledToFill().blur(radius: 30).opacity(0.55).id(prefs.heroVersion)
                palette.shell1.opacity(0.45)
            }
        }
    }
}

/// Superficie translúcida con brillo (Liquid Glass) o sólida, según Ajustes.
struct Surface: ViewModifier {
    @ObservedObject var prefs = Prefs.shared
    var radius: CGFloat = 16

    func body(content: Content) -> some View {
        let p = prefs.palette
        if prefs.liquidGlass {
            if #available(macOS 26.0, *) {
                content.glassEffect(.regular, in: RoundedRectangle(cornerRadius: radius, style: .continuous))
            } else {
                content.background(RoundedRectangle(cornerRadius: radius, style: .continuous).fill(.ultraThinMaterial))
                    .overlay(RoundedRectangle(cornerRadius: radius, style: .continuous).stroke(Color.white.opacity(0.16), lineWidth: 1))
            }
        } else {
            content.background(RoundedRectangle(cornerRadius: radius, style: .continuous).fill(p.surface))
        }
    }
}

extension View {
    func surface(_ radius: CGFloat = 16) -> some View { modifier(Surface(radius: radius)) }
}

// MARK: - Avatares

struct Avatar: View {
    let name: String
    let photo: String
    var size: CGFloat = 40
    var online = false
    @ObservedObject var prefs = Prefs.shared

    var body: some View {
        let p = prefs.palette
        ZStack {
            Circle().fill(p.avatar)
            if !photo.isEmpty, let data = Data(base64Encoded: photo), let image = NSImage(data: data) {
                Image(nsImage: image).resizable().scaledToFill().clipShape(Circle())
            } else {
                Text(Avatar.initials(name)).font(.lazo(size * 0.36, .semibold)).foregroundStyle(p.ink)
            }
        }
        .frame(width: size, height: size)
        .overlay(alignment: .bottomTrailing) {
            if online { Circle().fill(p.online).frame(width: size * 0.26, height: size * 0.26).overlay(Circle().stroke(p.shell1, lineWidth: 2)) }
        }
    }

    static func initials(_ name: String) -> String {
        let words = name.split(whereSeparator: { $0 == " " || $0 == "." || $0 == "-" || $0 == "_" })
        if words.count >= 2 { return (String(words[0].prefix(1)) + String(words[1].prefix(1))).uppercased() }
        return String(name.prefix(2)).uppercased()
    }
}

struct IconButton: View {
    let symbol: String
    let tip: String
    var badge = false
    var active = false
    let action: () -> Void
    @ObservedObject var prefs = Prefs.shared
    @State private var hover = false

    var body: some View {
        Button(action: action) {
            Image(systemName: symbol).font(.system(size: 14, weight: .medium)).frame(width: 32, height: 32)
                .background(Circle().fill(active ? prefs.palette.soft : hover ? prefs.palette.soft.opacity(0.7) : .clear))
                .overlay(alignment: .topTrailing) { if badge { Circle().fill(prefs.accentColor).frame(width: 8, height: 8).offset(x: -4, y: 4) } }
        }
        .buttonStyle(.plain)
        .help(tip)
        .onHover { hover = $0 }
    }
}

// MARK: - Inicio

struct HomeView: View {
    @ObservedObject var state: LauncherState
    @ObservedObject var prefs = Prefs.shared
    @ObservedObject var model = AppModel.shared

    var body: some View {
        if prefs.interface == .minimal { MinimalHome(state: state) } else { StandardHome(state: state) }
    }
}

/// Fila de iconos común: tu foto abre el perfil; chat, historial, descanso, pantalla y ajustes.
struct HeaderTools: View {
    @ObservedObject var state: LauncherState
    @ObservedObject var prefs = Prefs.shared
    @ObservedObject var model = AppModel.shared

    var body: some View {
        HStack(spacing: 2) {
            Button { state.route = .settings } label: { Avatar(name: prefs.displayName, photo: prefs.photo, size: 30) }
                .buttonStyle(.plain).help("Tu perfil")
            Spacer(minLength: 8)
            IconButton(symbol: "bubble.left.and.bubble.right", tip: "Chat", badge: model.unread > 0) { LauncherController.shared.openChat(nil) }
            IconButton(symbol: "clock", tip: "Historial") { state.route = .history }
            IconButton(symbol: "eye", tip: "Descanso Visual") { state.route = .eyecare }
            IconButton(symbol: "display", tip: "Compartir pantalla", active: model.share != nil) { state.route = .share }
            IconButton(symbol: "gearshape", tip: "Ajustes") { state.route = .settings }
        }
    }
}

struct MinimalHome: View {
    @ObservedObject var state: LauncherState
    @ObservedObject var model = AppModel.shared
    @ObservedObject var prefs = Prefs.shared
    @StateObject private var search = SpotlightSearch()
    @FocusState private var focused: Bool

    var body: some View {
        VStack(spacing: 8) {
            if !model.peers.isEmpty && state.query.isEmpty {
                HStack(spacing: 8) {
                    ForEach(model.peers) { peer in
                        PeerCircle(peer: peer, size: 34) { pickAndSend(peer) }
                    }
                }.padding(.horizontal, 6)
            }
            if !state.query.isEmpty {
                ResultsList(search: search, peers: model.peers).frame(maxHeight: 320)
            }
            HStack(spacing: 8) {
                Image(systemName: "magnifyingglass").opacity(0.6)
                TextField("Buscar archivo para enviar", text: $state.query)
                    .textFieldStyle(.plain).font(.lazo(15)).focused($focused)
                    .onChange(of: state.query) { _, text in search.run(text) }
                HeaderTools(state: state).fixedSize()
            }
            .padding(.leading, 14).padding(.trailing, 6).padding(.vertical, 6)
            .surface(22)
        }
        .frame(width: 560)
        .onAppear { focused = true }
    }

    private func pickAndSend(_ peer: Peer) {
        let files = LauncherController.shared.pickFiles("Elige qué enviar a \(peer.name)")
        if !files.isEmpty { model.send(files, to: peer) }
    }
}

struct StandardHome: View {
    @ObservedObject var state: LauncherState
    @ObservedObject var model = AppModel.shared
    @ObservedObject var prefs = Prefs.shared
    @StateObject private var search = SpotlightSearch()

    private var columns: Int { max(1, min(4, model.peers.count)) }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                VStack(alignment: .leading, spacing: 2) {
                    Text(greeting + ", " + (prefs.displayName.split(separator: " ").first.map(String.init) ?? "")).font(.lazo(18, .semibold))
                    Text(model.peers.isEmpty ? "Nadie conectado en esta red" : model.peers.count == 1 ? "1 equipo conectado" : "\(model.peers.count) equipos conectados")
                        .font(.lazo(12)).opacity(0.65)
                }
                Spacer()
                HeaderTools(state: state).fixedSize()
            }
            HeroCard(state: state)
            if model.peers.isEmpty {
                VStack(spacing: 6) {
                    Image(systemName: "wifi").font(.system(size: 22)).opacity(0.5)
                    Text(model.networkError ?? "Abre Lazo en otro equipo de esta red privada.").font(.lazo(12)).opacity(0.65).multilineTextAlignment(.center)
                }.frame(maxWidth: .infinity).padding(.vertical, 18)
            } else {
                LazyVGrid(columns: Array(repeating: GridItem(.fixed(118), spacing: 12), count: columns), spacing: 12) {
                    ForEach(model.peers) { peer in DeviceTile(peer: peer) }
                }.frame(maxWidth: .infinity)
            }
            HStack(spacing: 8) {
                Image(systemName: "magnifyingglass").opacity(0.6)
                TextField("Buscar archivo para enviar", text: $state.query).textFieldStyle(.plain).font(.lazo(13))
                    .onChange(of: state.query) { _, text in search.run(text) }
            }.padding(.horizontal, 12).padding(.vertical, 8).surface(14)
            if !state.query.isEmpty { ResultsList(search: search, peers: model.peers).frame(maxHeight: 260) }
        }
        .frame(width: max(420, CGFloat(columns) * 130 + 40))
    }

    private var greeting: String {
        let hour = Calendar.current.component(.hour, from: Date())
        return hour < 12 ? "Buenos días" : hour < 19 ? "Buenas tardes" : "Buenas noches"
    }
}

/// Tarjeta de inicio con la foto de fondo (clic derecho o los botones al pasar el cursor la cambian).
struct HeroCard: View {
    @ObservedObject var state: LauncherState
    @ObservedObject var prefs = Prefs.shared
    @ObservedObject var model = AppModel.shared
    @State private var hover = false

    var body: some View {
        ZStack(alignment: .bottomLeading) {
            if prefs.hasHero, let image = NSImage(contentsOf: prefs.heroURL) {
                Image(nsImage: image).resizable().scaledToFill().frame(height: 96).clipped().id(prefs.heroVersion)
                LinearGradient(colors: [.clear, .black.opacity(0.45)], startPoint: .top, endPoint: .bottom)
            } else {
                LinearGradient(colors: [prefs.accentColor.opacity(0.85), prefs.palette.soft], startPoint: .topLeading, endPoint: .bottomTrailing)
            }
            HStack {
                VStack(alignment: .leading, spacing: 2) {
                    Text(model.share != nil ? "Compartiendo pantalla" : "Compartir pantalla").font(.lazo(14, .semibold))
                    Text(model.share != nil ? "\(model.share!.viewerNames.count) mirando" : "Muestra tu pantalla o una ventana").font(.lazo(11.5)).opacity(0.85)
                }.foregroundStyle(.white)
                Spacer()
                PillButton(title: model.share != nil ? "Detener" : "Elegir", primary: model.share == nil, danger: model.share != nil) {
                    if let share = model.share { share.stop() } else { state.route = .share }
                }
            }.padding(12)
            if hover {
                HStack(spacing: 4) {
                    IconButton(symbol: "photo", tip: "Cambiar fondo de inicio") { if let url = LauncherController.shared.pickImage() { prefs.setHero(from: url) } }
                    if prefs.hasHero { IconButton(symbol: "arrow.uturn.backward", tip: "Restablecer fondo") { prefs.setHero(from: nil) } }
                }.foregroundStyle(.white).frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topTrailing).padding(6)
            }
        }
        .frame(height: 96)
        .clipShape(RoundedRectangle(cornerRadius: 18, style: .continuous))
        .onHover { hover = $0 }
        .contextMenu {
            Button("Cambiar fondo de inicio…") { if let url = LauncherController.shared.pickImage() { prefs.setHero(from: url) } }
            if prefs.hasHero { Button("Restablecer fondo") { prefs.setHero(from: nil) } }
        }
    }
}

/// Equipo como círculo con halo al pasar el cursor; acepta archivos soltados desde Finder.
struct PeerCircle: View {
    let peer: Peer
    var size: CGFloat = 64
    let action: () -> Void
    @ObservedObject var prefs = Prefs.shared
    @ObservedObject var model = AppModel.shared
    @State private var hover = false
    @State private var dropping = false

    var body: some View {
        Button(action: action) {
            Avatar(name: peer.name, photo: peer.photo, size: size, online: true)
                .overlay(Circle().stroke(prefs.accentColor, lineWidth: dropping ? 3 : 0))
                .shadow(color: prefs.accentColor.opacity(hover ? 0.55 : 0), radius: hover ? 10 : 0)
                .overlay(alignment: .topTrailing) {
                    if peer.identityChanged {
                        Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange).font(.system(size: size * 0.24))
                    } else if !peer.paired {
                        Image(systemName: "lock.open").font(.system(size: size * 0.2)).padding(3).background(Circle().fill(prefs.palette.soft))
                    }
                }
        }
        .buttonStyle(.plain)
        .help(peer.identityChanged ? "\(peer.name): su identidad cambió" : peer.paired ? peer.name : "\(peer.name) · sin emparejar: al enviar se confirmará un código")
        .onHover { hover = $0 }
        .onDrop(of: [.fileURL], isTargeted: $dropping) { providers in
            Drop.files(providers) { urls in model.send(urls, to: peer) }
            return true
        }
    }
}

struct DeviceTile: View {
    let peer: Peer
    @ObservedObject var model = AppModel.shared

    var body: some View {
        VStack(spacing: 6) {
            PeerCircle(peer: peer, size: 64) {
                let files = LauncherController.shared.pickFiles("Elige qué enviar a \(peer.name)")
                if !files.isEmpty { model.send(files, to: peer) }
            }
            Text(peer.name).font(.lazo(12, .medium)).lineLimit(2).multilineTextAlignment(.center)
            let progress = model.sending.filter { $0.key.hasPrefix(peer.id + "/") }.map(\.value)
            if !progress.isEmpty {
                ProgressView(value: progress.reduce(0, +) / Double(progress.count)).frame(width: 70)
            }
        }
        .frame(width: 118)
        .contextMenu {
            Button("Abrir chat") { LauncherController.shared.openChat(peer.id) }
            if !peer.paired { Button("Emparejar…") { model.pair(peer) } }
        }
    }
}

enum Drop {
    static func files(_ providers: [NSItemProvider], _ done: @escaping ([URL]) -> Void) {
        var urls: [URL] = []
        let group = DispatchGroup()
        for provider in providers {
            group.enter()
            _ = provider.loadObject(ofClass: URL.self) { url, _ in
                if let url, url.isFileURL { DispatchQueue.main.async { urls.append(url) } }
                group.leave()
            }
        }
        group.notify(queue: .main) { done(urls) }
    }
}

// MARK: - Búsqueda con Spotlight (en lugar de Everything)

@MainActor
final class SpotlightSearch: ObservableObject {
    @Published var results: [URL] = []
    private var query: NSMetadataQuery?
    private var observer: NSObjectProtocol?

    func run(_ text: String) {
        query?.stop()
        if let observer { NotificationCenter.default.removeObserver(observer) }
        let term = text.trimmingCharacters(in: .whitespaces)
        guard term.count >= 2 else { results = []; return }
        let q = NSMetadataQuery()
        q.predicate = NSPredicate(format: "%K LIKE[cd] %@", NSMetadataItemFSNameKey, "*\(term)*")
        q.searchScopes = [NSMetadataQueryUserHomeScope]
        q.sortDescriptors = [NSSortDescriptor(key: NSMetadataItemFSContentChangeDateKey, ascending: false)]
        observer = NotificationCenter.default.addObserver(forName: .NSMetadataQueryDidFinishGathering, object: q, queue: .main) { _ in
            Task { @MainActor in
                q.disableUpdates()
                var list: [URL] = []
                for i in 0..<min(q.resultCount, 60) {
                    guard let item = q.result(at: i) as? NSMetadataItem, let path = item.value(forAttribute: NSMetadataItemPathKey) as? String else { continue }
                    var isDir: ObjCBool = false
                    if FileManager.default.fileExists(atPath: path, isDirectory: &isDir), !isDir.boolValue { list.append(URL(fileURLWithPath: path)) }
                    if list.count == 24 { break }
                }
                self.results = list
                q.stop()
            }
        }
        query = q
        q.start()
    }
}

struct ResultsList: View {
    @ObservedObject var search: SpotlightSearch
    let peers: [Peer]
    @ObservedObject var model = AppModel.shared

    var body: some View {
        ScrollView {
            VStack(spacing: 2) {
                if search.results.isEmpty {
                    Text("Sin resultados en Spotlight").font(.lazo(12)).opacity(0.6).padding(12)
                }
                ForEach(search.results, id: \.self) { url in
                    HStack(spacing: 10) {
                        Image(nsImage: NSWorkspace.shared.icon(forFile: url.path)).resizable().frame(width: 22, height: 22)
                        VStack(alignment: .leading, spacing: 1) {
                            Text(url.lastPathComponent).font(.lazo(12.5, .medium)).lineLimit(1).truncationMode(.middle)
                            Text(url.deletingLastPathComponent().path.replacingOccurrences(of: NSHomeDirectory(), with: "~")).font(.lazo(10.5)).opacity(0.55).lineLimit(1).truncationMode(.head)
                        }
                        Spacer()
                        ForEach(peers) { peer in
                            Button { model.send([url], to: peer) } label: { Avatar(name: peer.name, photo: peer.photo, size: 26) }
                                .buttonStyle(.plain).help("Enviar a \(peer.name)")
                        }
                    }
                    .padding(.horizontal, 10).padding(.vertical, 5)
                    .contentShape(Rectangle())
                    .onDrag { NSItemProvider(contentsOf: url) ?? NSItemProvider() }
                }
            }
        }
        .surface(16)
    }
}
