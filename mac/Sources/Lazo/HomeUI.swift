import AppKit
import LazoCore
import SwiftUI

/// Iconos de acceso a las herramientas (Minimal): discretos, junto al engranaje.
struct HeaderTools: View {
    @ObservedObject var state: LauncherState
    @ObservedObject var model = AppModel.shared

    var body: some View {
        HStack(spacing: 0) {
            IconButton(symbol: "bubble.left.and.bubble.right", tip: "Chat", badge: model.unread > 0) { LauncherController.shared.openChat(nil) }
            IconButton(symbol: "clock", tip: "Historial") { state.route = .history }
            IconButton(symbol: "eye", tip: "Descanso Visual") { state.route = .eyecare }
            IconButton(symbol: "cursorarrow.rays", tip: "Control remoto (con contraseña)") { state.route = .remote }
            IconButton(symbol: "wrench.and.screwdriver", tip: "Utilidades · Xpace") { state.route = .tools }
            IconButton(symbol: "display", tip: "Compartir pantalla", active: model.share != nil) { state.route = .share }
            IconButton(symbol: "gearshape", tip: "Ajustes") { state.route = .settings }
        }
    }
}

// MARK: - Minimal

/// Como en Windows: marca «LAZO», los equipos como iniciales pequeñas, el engranaje y un buscador grande.
struct MinimalHome: View {
    @ObservedObject var state: LauncherState
    @ObservedObject var model = AppModel.shared
    @ObservedObject var prefs = Prefs.shared
    @StateObject private var search = SpotlightSearch()
    @FocusState private var focused: Bool

    var body: some View {
        let p = prefs.palette
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                Text("LAZO").font(.lazo(10.5, .bold)).tracking(3.5).foregroundStyle(p.muted)
                Spacer(minLength: 12)
                ForEach(model.peers) { peer in PeerCircle(peer: peer, size: 22) { pickAndSend(peer) } }
                HeaderTools(state: state).fixedSize()
            }
            ZStack(alignment: .leading) {
                if state.query.isEmpty {
                    Text(state.staged.isEmpty ? "Busca un archivo para enviarlo…" : "Elige a quién enviar \(state.staged.count) archivo\(state.staged.count == 1 ? "" : "s")")
                        .font(.lazo(22, .bold)).foregroundStyle(p.muted.opacity(0.85)).allowsHitTesting(false)
                }
                TextField("", text: $state.query).textFieldStyle(.plain).font(.lazo(22, .bold)).focused($focused)
                    .onChange(of: state.query) { _, text in search.run(text) }
            }
            .padding(.top, 2)
            if state.query.isEmpty { SharingNow(compact: true) }
            if !state.query.isEmpty { ResultsList(search: search, peers: model.peers).frame(maxHeight: 300) }
        }
        .padding(.horizontal, 12).padding(.vertical, 8)
        .frame(width: 560)
        .onAppear { focused = true }
    }

    private func pickAndSend(_ peer: Peer) {
        if !state.staged.isEmpty { model.send(state.staged, to: peer); state.staged = []; return }
        let files = LauncherController.shared.pickFiles("Elige qué enviar a \(peer.name)")
        if !files.isEmpty { model.send(files, to: peer) }
    }
}

struct HomeView: View {
    @ObservedObject var state: LauncherState
    @ObservedObject var prefs = Prefs.shared

    var body: some View {
        if prefs.interface == .minimal { MinimalHome(state: state) } else { StandardShell(state: state) }
    }
}

// MARK: - Standard

/// Barra lateral, cabecera con saludo y buscador, y los equipos como mosaicos. Chat y Utilidades ocupan el cuerpo;
/// los demás paneles se abren en una tarjeta a la derecha, como en Windows.
struct StandardShell: View {
    @ObservedObject var state: LauncherState
    @ObservedObject var model = AppModel.shared
    @ObservedObject var prefs = Prefs.shared
    @StateObject private var search = SpotlightSearch()

    private var wide: Bool { state.route == .chat || state.route == .tools }
    private var card: Bool { state.route != .home && !wide }

    var body: some View {
        let p = prefs.palette
        HStack(spacing: 0) {
            Rail(state: state).padding(.leading, 8).padding(.vertical, 8)
            VStack(spacing: 0) {
                header
                ZStack {
                    if wide {
                        Group { if state.route == .chat { ChatView(state: state) } else { ScrollView { ToolsView(state: state).padding(.bottom, 8) } } }
                            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top).padding(.horizontal, 14).padding(.bottom, 12)
                    } else {
                        HStack(alignment: .top, spacing: 12) {
                            peers.frame(maxWidth: .infinity, maxHeight: .infinity)
                            if card {
                                panel.padding(14).frame(width: 392)
                                    .background(RoundedRectangle(cornerRadius: 20, style: .continuous).fill(p.surface))
                                    .overlay(RoundedRectangle(cornerRadius: 20, style: .continuous).stroke(p.line, lineWidth: 1))
                                    .transition(.move(edge: .trailing).combined(with: .opacity))
                            }
                        }
                        .padding(.horizontal, 12).padding(.bottom, 12)
                    }
                }
            }
        }
        .environment(\.inCard, true)
        .frame(width: 900, height: 560)
        .animation(.spring(response: 0.34, dampingFraction: 0.88), value: state.route)
    }

    @ViewBuilder private var panel: some View {
        switch state.route {
        case .settings: SettingsView(state: state)
        case .history: HistoryView(state: state)
        case .eyecare: EyeCareView(state: state)
        case .share: SharePickerView(state: state)
        case .remote: RemoteView(state: state)
        default: EmptyView()
        }
    }

    private var header: some View {
        let p = prefs.palette
        return HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 1) {
                (Text(greeting + ", ").foregroundStyle(p.muted) + Text(prefs.displayName.split(separator: " ").first.map(String.init) ?? "").bold().foregroundStyle(p.ink))
                    .font(.lazo(17, .bold))
                Text(tagline).font(.lazo(12)).foregroundStyle(p.muted)
            }
            Spacer(minLength: 8)
            HStack(spacing: 8) {
                Image(systemName: "magnifyingglass").foregroundStyle(p.muted)
                TextField("Buscar personas o archivos…", text: $state.query).textFieldStyle(.plain).font(.lazo(13))
                    .onChange(of: state.query) { _, text in search.run(text) }
            }
            .padding(.horizontal, 14).padding(.vertical, 9).frame(width: 280)
            .background(Capsule().fill(p.soft))
            IconButton(symbol: "eye", tip: "Descanso Visual", active: state.route == .eyecare) { state.route = state.route == .eyecare ? .home : .eyecare }
            IconButton(symbol: "xmark", tip: "Cerrar") { LauncherController.shared.hide() }
        }
        .padding(.horizontal, 18).frame(height: 62)
    }

    private var visiblePeers: [Peer] {
        let q = state.query.trimmingCharacters(in: .whitespaces).lowercased()
        return q.isEmpty ? model.peers : model.peers.filter { $0.name.lowercased().contains(q) }
    }

    @ViewBuilder private var peers: some View {
        let p = prefs.palette
        VStack(spacing: 10) {
            if !state.staged.isEmpty {
                HStack {
                    Image(systemName: "paperplane.fill")
                    Text("\(state.staged.count) archivo\(state.staged.count == 1 ? "" : "s") listo\(state.staged.count == 1 ? "" : "s"): elige a quién enviarlo")
                        .font(.lazo(12.5, .semibold))
                    Spacer()
                    Button("Cancelar") { state.staged = [] }.buttonStyle(.plain).font(.lazo(12, .semibold))
                }
                .padding(.horizontal, 14).padding(.vertical, 9)
                .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(prefs.accentColor.opacity(0.25)))
            }
            SharingNow()
            if !state.query.isEmpty && !search.results.isEmpty || (state.query.count >= 2 && visiblePeers.isEmpty) {
                ResultsList(search: search, peers: model.peers)
            } else if model.peers.isEmpty {
                VStack(spacing: 8) {
                    Image(systemName: "wifi").font(.system(size: 26)).foregroundStyle(p.muted)
                    Text(model.networkError ?? "Abre Lazo en otro equipo de esta red privada.").font(.lazo(13)).foregroundStyle(p.muted).multilineTextAlignment(.center)
                }.frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ScrollView {
                    LazyVGrid(columns: [GridItem(.adaptive(minimum: 150, maximum: 170), spacing: 12)], spacing: 12) {
                        ForEach(visiblePeers) { peer in DeviceTile(peer: peer) }
                    }
                    .padding(.top, 2)
                }
            }
        }
    }

    private var tagline: String {
        model.peers.isEmpty ? "Nadie conectado en esta red" : model.peers.count == 1 ? "1 equipo conectado" : "\(model.peers.count) equipos conectados"
    }

    private var greeting: String {
        let hour = Calendar.current.component(.hour, from: Date())
        return hour < 12 ? "Buenos días" : hour < 19 ? "Buenas tardes" : "Buenas noches"
    }
}

/// Barra lateral: logo, navegación y el «+» para elegir archivos.
struct Rail: View {
    @ObservedObject var state: LauncherState
    @ObservedObject var model = AppModel.shared
    @ObservedObject var prefs = Prefs.shared

    var body: some View {
        let p = prefs.palette
        VStack(spacing: 6) {
            Image(systemName: "link").font(.system(size: 20, weight: .bold)).foregroundStyle(p.ink).padding(.top, 10).padding(.bottom, 10)
            nav("house", "Inicio", .home)
            nav("bubble.left.and.bubble.right", "Chat", .chat, badge: model.unread > 0)
            nav("display", "Compartir pantalla", .share, badge: model.share != nil)
            nav("cursorarrow.rays", "Control remoto (con contraseña)", .remote)
            nav("wrench.and.screwdriver", "Utilidades · Xpace", .tools)
            nav("clock", "Historial", .history)
            nav("gearshape", "Ajustes", .settings)
            Spacer()
            Button { addFiles() } label: {
                ZStack {
                    Circle().stroke(prefs.accentColor.opacity(0.5), style: StrokeStyle(lineWidth: 1.2, dash: [3, 3])).frame(width: 46, height: 46)
                    Circle().fill(prefs.accentColor).frame(width: 36, height: 36)
                    Image(systemName: "plus").font(.system(size: 17, weight: .bold)).foregroundStyle(.white)
                }
            }
            .buttonStyle(.plain).help("Elegir archivos para enviar").padding(.bottom, 6)
        }
        .frame(width: 56)
        .background(RoundedRectangle(cornerRadius: 20, style: .continuous).fill(p.soft.opacity(0.7)))
    }

    private func nav(_ symbol: String, _ tip: String, _ route: Route, badge: Bool = false) -> some View {
        let p = prefs.palette
        let active = state.route == route
        return Button { state.route = active && route != .home ? .home : route } label: {
            Image(systemName: symbol).font(.system(size: 17, weight: .medium))
                .foregroundStyle(active ? p.chipText : p.ink)
                .frame(width: 42, height: 42)
                .background(RoundedRectangle(cornerRadius: 13, style: .continuous).fill(active ? p.chip : .clear))
                .overlay(alignment: .topTrailing) { if badge { Circle().fill(Color.red).frame(width: 8, height: 8).padding(8) } }
        }
        .buttonStyle(.plain).help(tip)
    }

    private func addFiles() {
        let files = LauncherController.shared.pickFiles("Elige qué enviar")
        guard !files.isEmpty else { return }
        if model.peers.count == 1, let only = model.peers.first { model.send(files, to: only) } else { state.staged = files; state.route = .home }
    }
}

// MARK: - Compartiendo ahora

/// Quién está compartiendo su pantalla en la red, con «Unirme» (la persona que comparte decide).
struct SharingNow: View {
    @ObservedObject var model = AppModel.shared
    @ObservedObject var prefs = Prefs.shared
    var compact = false

    var body: some View {
        let list = model.peers.filter { $0.sharing != nil }
        let p = prefs.palette
        if !list.isEmpty {
            VStack(alignment: .leading, spacing: 6) {
                Text("COMPARTIENDO AHORA").font(.lazo(10, .bold)).tracking(1).foregroundStyle(p.muted)
                ForEach(list) { peer in
                    HStack(spacing: 10) {
                        Avatar(name: peer.name, photo: peer.photo, size: compact ? 24 : 30)
                        VStack(alignment: .leading, spacing: 0) {
                            Text(peer.name).font(.lazo(12.5, .semibold)).lineLimit(1)
                            Text(peer.sharing ?? "").font(.lazo(10.5)).foregroundStyle(p.muted).lineLimit(1)
                        }
                        Spacer(minLength: 6)
                        PillButton(title: "Unirme", primary: true) { model.requestJoin(peer) }
                    }
                }
            }
            .padding(10)
            .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(prefs.accentColor.opacity(0.16)))
            .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).stroke(prefs.accentColor.opacity(0.4), lineWidth: 1))
        }
    }
}

// MARK: - Mosaicos

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
            Avatar(name: peer.name, photo: peer.photo, size: size, online: size >= 40)
                .overlay(Circle().stroke(prefs.accentColor, lineWidth: dropping ? 3 : 0))
                .shadow(color: prefs.accentColor.opacity(hover ? 0.55 : 0), radius: hover ? 10 : 0)
                .overlay(alignment: .topTrailing) {
                    if peer.identityChanged {
                        Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange).font(.system(size: max(9, size * 0.24)))
                    }
                }
        }
        .buttonStyle(.plain)
        .help(peer.identityChanged ? "\(peer.name): su identidad cambió" : peer.verified ? peer.name + " · verificado" : peer.name)
        .contextMenu {
            Button("Abrir chat") { LauncherController.shared.openChat(peer.id) }
            if peer.sharing != nil { Button("Unirme a su pantalla") { model.requestJoin(peer) } }
            else { Button("Pedir que comparta su pantalla") { model.requestShare(peer) } }
        }
        .onHover { hover = $0 }
        .onDrop(of: [.fileURL], isTargeted: $dropping) { providers in
            Drop.files(providers) { urls in model.send(urls, to: peer) }
            return true
        }
    }
}

/// Mosaico de un equipo: círculo grande, nombre en mayúsculas y progreso de envío.
struct DeviceTile: View {
    let peer: Peer
    @ObservedObject var model = AppModel.shared
    @ObservedObject var prefs = Prefs.shared
    @State private var hover = false
    @State private var dropping = false

    var body: some View {
        let p = prefs.palette
        let state = LauncherController.shared.state
        Button {
            if !state.staged.isEmpty { model.send(state.staged, to: peer); state.staged = []; return }
            let files = LauncherController.shared.pickFiles("Elige qué enviar a \(peer.name)")
            if !files.isEmpty { model.send(files, to: peer) }
        } label: {
            VStack(spacing: 10) {
                Avatar(name: peer.name, photo: peer.photo, size: 84, online: true, backdrop: p.dark ? Color.black.opacity(0.4) : p.track)
                    .overlay(alignment: .topTrailing) {
                        if peer.identityChanged { Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange).font(.system(size: 18)) }
                    }
                Text(peer.name.uppercased()).font(.lazo(13, .bold)).tracking(0.5).lineLimit(1).truncationMode(.tail)
                let progress = model.sending.filter { $0.key.hasPrefix(peer.id + "/") }.map(\.value)
                if !progress.isEmpty {
                    ProgressView(value: progress.reduce(0, +) / Double(progress.count)).frame(width: 90)
                }
            }
            .frame(maxWidth: .infinity).padding(.vertical, 16).padding(.horizontal, 8)
            .background(RoundedRectangle(cornerRadius: 20, style: .continuous).fill(p.dark ? Color.white.opacity(hover || dropping ? 0.14 : 0.09) : p.soft.opacity(hover || dropping ? 1 : 0.8)))
            .overlay(RoundedRectangle(cornerRadius: 20, style: .continuous).stroke(dropping ? prefs.accentColor : p.line, lineWidth: dropping ? 2 : 1))
            .contentShape(RoundedRectangle(cornerRadius: 20, style: .continuous))
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
        .onDrop(of: [.fileURL], isTargeted: $dropping) { providers in
            Drop.files(providers) { urls in model.send(urls, to: peer) }
            return true
        }
        .help(peer.verified ? peer.name + " · verificado" : peer.name)
        .overlay(alignment: .topTrailing) {
            if peer.sharing != nil {
                Image(systemName: "display").font(.system(size: 11, weight: .bold)).foregroundStyle(.white).padding(6)
                    .background(Circle().fill(prefs.accentColor)).padding(8).help("Está compartiendo su pantalla")
            }
        }
        .contextMenu {
            Button("Abrir chat") { LauncherController.shared.openChat(peer.id) }
            Button("Control remoto con contraseña…") { LauncherController.shared.state.remoteTarget = peer.id; LauncherController.shared.show(route: .remote) }
            if peer.sharing != nil { Button("Unirme a su pantalla") { model.requestJoin(peer) } }
            else { Button("Pedir que comparta su pantalla") { model.requestShare(peer) } }
            if peer.paired && !peer.verified { Button("Verificar código…") { model.verify(peer) } }
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
