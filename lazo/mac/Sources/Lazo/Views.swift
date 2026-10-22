import AppKit
import LazoCore
import SwiftUI

/// Dentro de la tarjeta de Standard la barra lateral ya sirve para volver; la flecha solo aparece en Minimal.
private struct InCardKey: EnvironmentKey { static let defaultValue = false }
extension EnvironmentValues {
    var inCard: Bool { get { self[InCardKey.self] } set { self[InCardKey.self] = newValue } }
}

/// Ancho fijo en Minimal; en Standard el panel se adapta a la tarjeta o al cuerpo.
struct PanelFrame: ViewModifier {
    let width: CGFloat
    @Environment(\.inCard) private var inCard
    func body(content: Content) -> some View {
        if inCard { content.frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top) } else { content.frame(width: width) }
    }
}

extension View {
    func panelFrame(_ width: CGFloat) -> some View { modifier(PanelFrame(width: width)) }
}

struct PanelHeader: View {
    let title: String
    @ObservedObject var state: LauncherState
    @Environment(\.inCard) private var inCard

    var body: some View {
        HStack {
            if !inCard {
                Button { state.route = .home } label: { Image(systemName: "chevron.left").font(.system(size: 13, weight: .semibold)) }
                    .buttonStyle(.plain).help("Volver")
            }
            Text(title).font(.lazo(16, .semibold))
            Spacer()
            Button { LauncherController.shared.hide() } label: { Image(systemName: "xmark").font(.system(size: 12, weight: .semibold)) }
                .buttonStyle(.plain).opacity(0.7).help("Cerrar")
        }
    }
}

struct SectionLabel: View {
    let text: String
    var body: some View { Text(text.uppercased()).font(.lazo(10.5, .semibold)).opacity(0.55).padding(.top, 10) }
}

/// Control segmentado con píldora deslizante, como en Windows.
struct Segmented<T: Hashable>: View {
    let options: [(T, String)]
    @Binding var selection: T
    @ObservedObject var prefs = Prefs.shared
    @Namespace private var pill

    var body: some View {
        HStack(spacing: 2) {
            ForEach(options, id: \.0) { option in
                Button { withAnimation(.spring(response: 0.3, dampingFraction: 0.85)) { selection = option.0 } } label: {
                    Text(option.1).font(.lazo(12, .medium)).frame(maxWidth: .infinity).padding(.vertical, 6)
                        .background {
                            if selection == option.0 { Capsule().fill(prefs.palette.chip).matchedGeometryEffect(id: "pill", in: pill) }
                        }
                        .foregroundStyle(selection == option.0 ? prefs.palette.chipText : prefs.palette.ink)
                }.buttonStyle(.plain)
            }
        }
        .padding(3)
        .background(Capsule().fill(prefs.palette.track))
    }
}

// MARK: - Ajustes

struct SettingsView: View {
    @ObservedObject var state: LauncherState
    @ObservedObject var prefs = Prefs.shared
    @ObservedObject var model = AppModel.shared
    @State private var updateState = ""
    @State private var nameDraft = ""
    @Environment(\.inCard) private var inCard

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            PanelHeader(title: "Ajustes", state: state)
            ScrollView {
                VStack(alignment: .leading, spacing: 8) {
                    HStack(spacing: 12) {
                        Avatar(name: prefs.displayName, photo: prefs.photo, size: 56)
                        VStack(alignment: .leading, spacing: 6) {
                            TextField("Tu nombre", text: $nameDraft, onCommit: { prefs.name = nameDraft })
                                .textFieldStyle(.plain).font(.lazo(15, .semibold))
                                .padding(.horizontal, 10).padding(.vertical, 6).surface(10)
                            HStack(spacing: 6) {
                                PillButton(title: "Cambiar foto") {
                                    if let url = LauncherController.shared.pickImage() {
                                        do { try prefs.setPhoto(from: url) } catch { model.say(error.localizedDescription) }
                                    }
                                }
                                if !prefs.photo.isEmpty { PillButton(title: "Quitar") { try? prefs.setPhoto(from: nil) } }
                            }
                        }
                    }
                    SectionLabel(text: "Modo")
                    Segmented(options: [(InterfaceKind.minimal, "Minimal"), (.standard, "Standard")], selection: $prefs.interface)
                    SectionLabel(text: "Apariencia")
                    ThemeChips()
                    SectionLabel(text: "Acento")
                    AccentPicker()
                    SectionLabel(text: "Fondo de inicio")
                    HeroBackgroundRow()
                    Toggle("Liquid Glass", isOn: $prefs.liquidGlass).toggleStyle(.switch).font(.lazo(12.5))
                    SectionLabel(text: "Avisos de chat")
                    Segmented(options: [(false, "Discretos"), (true, "Notificación")], selection: $prefs.chatBanner)
                    RemoteHostSection()
                    SectionLabel(text: "Carpeta de recepción")
                    ReceiveFolderRow()
                    SectionLabel(text: "Sistema")
                    Toggle("Abrir al iniciar sesión", isOn: Binding(get: { prefs.startsAtLogin }, set: { prefs.startsAtLogin = $0 }))
                        .toggleStyle(.switch).font(.lazo(12.5))
                    Toggle("Cerrar la ventana al hacer clic fuera", isOn: $prefs.closeOnClickOutside).toggleStyle(.switch).font(.lazo(12.5))
                    Text(prefs.closeOnClickOutside ? "Se oculta al pulsar en otra parte. Con Esc, × o doble ⌥ también." : "Se queda abierta hasta que la cierres con Esc, × o doble ⌥.")
                        .font(.lazo(10.5)).foregroundStyle(prefs.palette.muted)
                    Toggle("Buscar actualizaciones", isOn: $prefs.updates).toggleStyle(.switch).font(.lazo(12.5))
                    HStack {
                        PillButton(title: "Verificar actualizaciones") {
                            updateState = "Buscando…"
                            Updater.check { text in updateState = text }
                        }
                        Text(updateState).font(.lazo(11)).opacity(0.65)
                    }
                    HStack {
                        PillButton(title: "Abrir registro de diagnóstico") { NSWorkspace.shared.open(DiagLog.url) }
                        Text("Para ver por qué falla una conexión").font(.lazo(10.5)).foregroundStyle(prefs.palette.muted)
                    }
                    PermissionsSection()
                    PairedSection()
                    Text("Lazo \(Updater.currentVersion)").font(.lazo(10.5)).opacity(0.5).padding(.top, 8)
                }
                .padding(.trailing, 8)
            }
            .frame(maxHeight: inCard ? .infinity : 470)
        }
        .panelFrame(360)
        .onAppear { nameDraft = prefs.name.isEmpty ? prefs.displayName : prefs.name }
        .onDisappear { if nameDraft != prefs.displayName { prefs.name = nameDraft } }
    }
}

/// Imagen de fondo del inicio (la usa el tema «Imagen» y el acento «De la imagen de inicio»), como «Fondo de inicio» en Windows.
struct HeroBackgroundRow: View {
    @ObservedObject var prefs = Prefs.shared

    var body: some View {
        HStack(spacing: 8) {
            if prefs.hasHero, let image = NSImage(contentsOf: prefs.heroURL) {
                Image(nsImage: image).resizable().scaledToFill().frame(width: 56, height: 36).clipShape(RoundedRectangle(cornerRadius: 8)).id(prefs.heroVersion)
            }
            PillButton(title: prefs.hasHero ? "Cambiar imagen…" : "Elegir imagen…") {
                if let url = LauncherController.shared.pickImage() { prefs.setHero(from: url) }
            }
            if prefs.hasHero { PillButton(title: "Restablecer") { prefs.setHero(from: nil) } }
            Spacer(minLength: 0)
        }
    }
}

/// Carpeta donde se guardan los archivos recibidos, con cambiar y restablecer.
struct ReceiveFolderRow: View {
    @ObservedObject var model = AppModel.shared
    @ObservedObject var prefs = Prefs.shared
    @State private var tick = 0

    var body: some View {
        let _ = tick
        let engine = model.engine
        VStack(alignment: .leading, spacing: 6) {
            Text(engine.receiveDirectory.path.replacingOccurrences(of: NSHomeDirectory(), with: "~"))
                .font(.lazo(12)).lineLimit(1).truncationMode(.middle)
                .frame(maxWidth: .infinity, alignment: .leading).padding(.horizontal, 10).padding(.vertical, 7).surface(10)
            HStack(spacing: 6) {
                PillButton(title: "Cambiar…") { choose() }
                if !engine.usesDefaultReceiveDirectory { PillButton(title: "Restablecer") { engine.setReceiveDirectory(nil); tick += 1 } }
                PillButton(title: "Abrir") { NSWorkspace.shared.open(engine.receiveDirectory) }
            }
            Text("Lo que ya llegó se queda donde está; lo nuevo va a esta carpeta.").font(.lazo(10.5)).foregroundStyle(prefs.palette.muted)
        }
    }

    private func choose() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.prompt = "Elegir"
        panel.message = "Carpeta donde Lazo guardará lo que recibas"
        panel.directoryURL = model.engine.receiveDirectory
        LauncherController.shared.holding += 1
        defer { LauncherController.shared.holding -= 1 }
        NSApp.activate(ignoringOtherApps: true)
        if panel.runModal() == .OK, let url = panel.url { model.engine.setReceiveDirectory(url); tick += 1 }
    }
}

struct ThemeChips: View {
    @ObservedObject var prefs = Prefs.shared

    var body: some View {
        LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 6), count: 4), spacing: 6) {
            ForEach(ThemeKind.allCases) { kind in
                let p = kind == .image ? ImageTheme.palette : palette(kind)
                Button { prefs.theme = kind } label: {
                    VStack(spacing: 4) {
                        RoundedRectangle(cornerRadius: 8).fill(LinearGradient(colors: [p.shell0, p.shell2], startPoint: .top, endPoint: .bottom))
                            .overlay(alignment: .bottomTrailing) { Circle().fill(p.accent).frame(width: 10, height: 10).padding(5) }
                            .frame(height: 34)
                            .overlay(RoundedRectangle(cornerRadius: 8).stroke(prefs.theme == kind ? prefs.accentColor : prefs.palette.line, lineWidth: prefs.theme == kind ? 2 : 1))
                        Text(kind.label).font(.lazo(10.5))
                    }
                }.buttonStyle(.plain)
            }
        }
    }

    private func palette(_ kind: ThemeKind) -> Palette {
        switch kind {
        case .wine: return .wine
        case .flat: return .flat
        case .glass: return .glass
        case .dark: return .darkP
        case .warm: return .warm
        case .meet: return .meet
        case .image: return ImageTheme.palette
        }
    }
}

struct AccentPicker: View {
    @ObservedObject var prefs = Prefs.shared

    var body: some View {
        HStack(spacing: 6) {
            swatch("", prefs.palette.accent, "Del tema")
            if prefs.hasHero { swatch("image", ImageTheme.palette.accent, "De la imagen de inicio") }
            ForEach(accentChoices, id: \.self) { hex in swatch(hex, Color(hex: hex), hex) }
        }
    }

    private func swatch(_ value: String, _ color: Color, _ tip: String) -> some View {
        Button { prefs.accent = value } label: {
            Circle().fill(color).frame(width: 20, height: 20)
                .overlay(Circle().stroke(prefs.palette.ink, lineWidth: prefs.accent == value ? 2 : 0).padding(-3))
                .overlay { if value.isEmpty { Image(systemName: "a.circle").font(.system(size: 9)).foregroundStyle(.white) } }
        }.buttonStyle(.plain).help(tip)
    }
}

/// Permisos de macOS que Lazo necesita y su estado.
struct PermissionsSection: View {
    @State private var tick = 0

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            SectionLabel(text: "Permisos de macOS")
            row("Grabación de pantalla", "Para compartir tu pantalla", Permissions.screenRecording) { Permissions.requestScreenRecording() }
            row("Accesibilidad", "Para que alguien controle este Mac y para el atajo doble ⌥", Permissions.accessibility) { Permissions.requestAccessibility() }
        }
        .id(tick)
        .onAppear { Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { _ in tick += 1 } }
    }

    private func row(_ title: String, _ detail: String, _ granted: Bool, _ ask: @escaping () -> Void) -> some View {
        HStack {
            VStack(alignment: .leading, spacing: 1) {
                Text(title).font(.lazo(12.5, .medium))
                Text(detail).font(.lazo(10.5)).opacity(0.6)
            }
            Spacer()
            if granted { Image(systemName: "checkmark.circle.fill").foregroundStyle(.green) } else { PillButton(title: "Permitir", action: ask) }
        }
    }
}

/// Equipos conocidos y huella propia. Se recuerdan al primer contacto; verificar con el código es opcional.
struct PairedSection: View {
    @ObservedObject var model = AppModel.shared

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            SectionLabel(text: "Equipos conocidos")
            Text("Lazo recuerda cada equipo la primera vez que se conectan y avisa si su identidad cambia. Verificar el código con la otra persona es opcional.")
                .font(.lazo(11)).opacity(0.65).fixedSize(horizontal: false, vertical: true)
            Text("Huella de este Mac: " + LazoCrypto.shortFingerprint(model.engine.identity.fingerprint)).font(.lazo(10.5)).opacity(0.6)
            ForEach(model.engine.pairings.all(), id: \.id) { device in
                HStack {
                    VStack(alignment: .leading, spacing: 1) {
                        HStack(spacing: 4) {
                            Text(device.name).font(.lazo(12.5, .medium))
                            if device.isVerified { Image(systemName: "checkmark.seal.fill").font(.system(size: 11)).help("Verificado con código") }
                        }
                        Text(LazoCrypto.shortFingerprint(device.fingerprint)).font(.lazo(10.5)).opacity(0.6)
                    }
                    Spacer()
                    if !device.isVerified, let peer = model.peer(device.id), peer.paired {
                        PillButton(title: "Verificar") { model.verify(peer) }.help("Comparar un código de 6 cifras con la otra persona")
                    }
                    PillButton(title: "Olvidar") { model.engine.pairings.remove(device.id) }
                        .help("Úsalo si reinstaló Lazo: su nueva identidad se recordará en el siguiente contacto")
                }
            }
        }
        .id(model.pairedVersion)
    }
}

// MARK: - Historial

struct HistoryView: View {
    @ObservedObject var state: LauncherState
    @Environment(\.inCard) private var inCard
    @State private var refresh = 0

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            PanelHeader(title: "Historial", state: state)
            let _ = refresh
            let entries = TransferLog.recent()
            if entries.isEmpty {
                Text("Todavía no has enviado ni recibido nada.").font(.lazo(12)).opacity(0.6).padding(.vertical, 20).frame(maxWidth: .infinity)
            }
            ScrollView {
                VStack(spacing: 2) {
                    ForEach(entries) { entry in
                        Button {
                            if FileManager.default.fileExists(atPath: entry.path) { NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: entry.path)]) }
                        } label: {
                            HStack(spacing: 10) {
                                Image(systemName: entry.incoming ? "arrow.down.circle" : "arrow.up.circle").opacity(0.7)
                                VStack(alignment: .leading, spacing: 1) {
                                    Text(entry.name).font(.lazo(12.5, .medium)).lineLimit(1).truncationMode(.middle)
                                    Text((entry.incoming ? "De " : "Para ") + entry.peer + " · " + entry.when.formatted(date: .abbreviated, time: .shortened))
                                        .font(.lazo(10.5)).opacity(0.6)
                                }
                                Spacer()
                            }.padding(.horizontal, 8).padding(.vertical, 5).contentShape(Rectangle())
                        }.buttonStyle(.plain)
                    }
                }
            }.frame(maxHeight: inCard ? .infinity : 360)
            if !entries.isEmpty {
                HStack {
                    Spacer()
                    PillButton(title: "Borrar historial") { TransferLog.clear(); refresh += 1 }
                }
            }
        }
        .panelFrame(380)
    }
}

// MARK: - Chat al estilo de Discord

struct ChatView: View {
    @Environment(\.inCard) private var inCard
    @ObservedObject var state: LauncherState
    @ObservedObject var model = AppModel.shared
    @ObservedObject var prefs = Prefs.shared
    @State private var draft = ""
    @State private var drafts: [String: String] = [:]
    @State private var lastTyping = Date.distantPast
    @State private var presence: Timer?
    @State private var newGroup = false

    private typealias Row = (id: String, name: String, preview: String, unread: Int, online: Bool)

    private var groupIds: Set<String> { Set(ChatGroups.all().map(\.id)) }

    private var threads: [Row] {
        _ = model.chatVersion
        let groups = groupIds
        var list: [Row] = ChatStore.threads().filter { !groups.contains($0.id) }
            .map { ($0.id, model.peer($0.id)?.name ?? $0.name, $0.preview, $0.unread, model.peer($0.id) != nil) }
        for peer in model.peers where !list.contains(where: { $0.id == peer.id }) { list.append((peer.id, peer.name, "", 0, true)) }
        return list
    }

    private var groups: [Row] {
        _ = model.chatVersion
        let index = Dictionary(uniqueKeysWithValues: ChatStore.threads().map { ($0.id, $0) })
        return ChatGroups.all().sorted { $0.name.lowercased() < $1.name.lowercased() }
            .map { ($0.id, $0.name, index[$0.id]?.preview ?? "", index[$0.id]?.unread ?? 0, true) }
    }

    var body: some View {
        let p = prefs.palette
        VStack(alignment: .leading, spacing: 8) {
            PanelHeader(title: "Chat", state: state)
            ZStack {
                HStack(alignment: .top, spacing: 10) {
                    ScrollView {
                        VStack(spacing: 2) {
                            HStack {
                                Text("GRUPOS").font(.lazo(10, .bold)).tracking(1).foregroundStyle(p.muted)
                                Spacer()
                                Button { newGroup = true } label: { Image(systemName: "plus").font(.system(size: 11, weight: .bold)) }
                                    .buttonStyle(.plain).help("Nuevo grupo")
                            }.padding(.horizontal, 6).padding(.top, 2).padding(.bottom, 2)
                            ForEach(groups, id: \.id) { row in threadButton(row, group: true) }
                            Text("PERSONAS").font(.lazo(10, .bold)).tracking(1).foregroundStyle(p.muted)
                                .frame(maxWidth: .infinity, alignment: .leading).padding(.horizontal, 6).padding(.top, 8).padding(.bottom, 2)
                            ForEach(threads, id: \.id) { row in threadButton(row, group: false) }
                        }
                    }
                    .frame(width: 170)
                    Divider().opacity(0.3)
                    if let id = state.chatPeer {
                        conversation(id)
                    } else {
                        Text("Elige una conversación").font(.lazo(12)).opacity(0.6).frame(maxWidth: .infinity, maxHeight: .infinity)
                    }
                }
                if newGroup {
                    NewGroupForm(peers: model.peers) { title, chosen in
                        let group = model.createGroup(title, chosen)
                        state.chatPeer = group.id
                        newGroup = false
                    } cancel: { newGroup = false }
                    .background(RoundedRectangle(cornerRadius: 16, style: .continuous).fill(p.shell1))
                    .overlay(RoundedRectangle(cornerRadius: 16, style: .continuous).stroke(p.line, lineWidth: 1))
                    .frame(maxWidth: 360).shadow(color: .black.opacity(0.35), radius: 16, y: 4)
                }
            }
            .frame(maxHeight: inCard ? .infinity : 400)
            .frame(height: inCard ? nil : 400)
        }
        .panelFrame(620)
        .onAppear {
            if state.chatPeer == nil { state.chatPeer = (groups + threads).first?.id }
            if let id = state.chatPeer { select(id) }
            presence = Timer.scheduledTimer(withTimeInterval: 5, repeats: true) { _ in
                Task { @MainActor in
                    if let id = state.chatPeer, ChatGroups.get(id) == nil, let peer = model.peer(id) { model.signal(peer, NetworkEngine.chatKindPresence, "1") }
                }
            }
        }
        .onDisappear {
            presence?.invalidate()
            if let id = state.chatPeer, let peer = model.peer(id) { model.signal(peer, NetworkEngine.chatKindPresence, "0") }
        }
    }

    private func threadButton(_ row: Row, group: Bool) -> some View {
        let p = prefs.palette
        return Button { select(row.id) } label: {
            HStack(spacing: 8) {
                if group {
                    Circle().fill(p.avatar).frame(width: 30, height: 30)
                        .overlay(Image(systemName: "person.2.fill").font(.system(size: 12)))
                } else {
                    Avatar(name: row.name, photo: model.peer(row.id)?.photo ?? "", size: 30, online: row.online)
                }
                VStack(alignment: .leading, spacing: 1) {
                    Text(row.name).font(.lazo(12.5, row.unread > 0 ? .bold : .medium)).lineLimit(1)
                    if !row.preview.isEmpty { Text(row.preview).font(.lazo(10.5)).opacity(0.6).lineLimit(1) }
                }
                Spacer(minLength: 0)
                if row.unread > 0 { Text("\(row.unread)").font(.lazo(10, .bold)).padding(.horizontal, 6).padding(.vertical, 2).background(Capsule().fill(prefs.accentColor)).foregroundStyle(.white) }
            }
            .padding(6)
            .background(RoundedRectangle(cornerRadius: 10).fill(state.chatPeer == row.id ? p.soft : .clear))
            .contentShape(Rectangle())
        }.buttonStyle(.plain)
    }

    private func select(_ id: String) {
        if let current = state.chatPeer { drafts[current] = draft }
        state.chatPeer = id
        draft = drafts[id] ?? ""
        model.markRead(id)
        model.chatVersion += 1
    }

    @ViewBuilder private func conversation(_ id: String) -> some View {
        let group = ChatGroups.get(id)
        let peer = group == nil ? model.peer(id) : nil
        let name = group?.name ?? peer?.name ?? ChatStore.threads().first { $0.id == id }?.name ?? "Compañero"
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                VStack(alignment: .leading, spacing: 0) {
                    Text(name).font(.lazo(14, .semibold))
                    if let group {
                        Text("\(group.members.count + 1) integrantes").font(.lazo(10.5)).opacity(0.6)
                    } else if let typing = model.typing[id], typing > Date() { Text("Escribiendo…").font(.lazo(11)).opacity(0.6) }
                    else if let inChat = model.inChat[id], inChat > Date() { Text("En el chat").font(.lazo(11)).opacity(0.6) }
                }
                Spacer()
                if let peer {
                    IconButton(symbol: "display", tip: "Compartir pantalla") { ShareStarter.startQuick(with: peer) }
                    IconButton(symbol: "bell.and.waves.left.and.right", tip: "Zumbido") { model.nudge(peer) }
                    IconButton(symbol: "paperclip", tip: "Enviar archivo") {
                        let files = LauncherController.shared.pickFiles("Elige qué enviar a \(peer.name)")
                        if !files.isEmpty { model.send(files, to: peer) }
                    }
                }
                Menu {
                    Button("Vaciar conversación", role: .destructive) {
                        model.clearThread(id)
                        if group == nil { state.chatPeer = nil }
                    }
                    if let group { Button("Salir del grupo", role: .destructive) { model.leaveGroup(group); state.chatPeer = nil } }
                } label: { Image(systemName: "ellipsis").font(.system(size: 14, weight: .semibold)).frame(width: 26, height: 26) }
                .menuStyle(.borderlessButton).menuIndicator(.hidden).frame(width: 30)
            }
            Messages(id: id, name: name, isGroup: group != nil)
            HStack(spacing: 8) {
                let canWrite = group != nil || peer != nil
                TextField(canWrite ? "Mensaje para \(name)" : "\(name) no está conectado", text: $draft, axis: .vertical)
                    .textFieldStyle(.plain).font(.lazo(13)).lineLimit(1...4)
                    .disabled(!canWrite)
                    .onSubmit { send(group: group, peer: peer) }
                    .onChange(of: draft) { _, _ in
                        guard let peer, Date().timeIntervalSince(lastTyping) > 2 else { return }
                        lastTyping = Date()
                        model.signal(peer, NetworkEngine.chatKindTyping, "1")
                    }
                Button { send(group: group, peer: peer) } label: { Image(systemName: "arrow.up.circle.fill").font(.system(size: 20)) }
                    .buttonStyle(.plain).disabled(!canWrite || draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
            .padding(.horizontal, 12).padding(.vertical, 8).surface(18)
        }
        .onDrop(of: [.fileURL], isTargeted: nil) { providers in
            guard let peer else { return false }
            Drop.files(providers) { model.send($0, to: peer) }
            return true
        }
    }

    private func send(group: ChatGroup?, peer: Peer?) {
        let text = String(draft.trimmingCharacters(in: .whitespacesAndNewlines).prefix(2000))
        guard !text.isEmpty else { return }
        if let group {
            // Grupo: se guarda al instante y se entrega a cada integrante; a quien esté desconectado le llega al volver.
            draft = ""
            drafts[group.id] = nil
            model.sendGroup(group, text)
            return
        }
        guard let peer else { return }
        draft = ""
        Task {
            // Si el envío falla, el borrador vuelve para no perder lo escrito.
            if !(await model.sendChat(peer, text)) { draft = text }
            model.signal(peer, NetworkEngine.chatKindTyping, "0")
        }
    }
}

/// Formulario para crear un grupo: nombre y a quién añadir.
private struct NewGroupForm: View {
    let peers: [Peer]
    let create: (String, [Peer]) -> Void
    let cancel: () -> Void
    @ObservedObject var prefs = Prefs.shared
    @State private var title = ""
    @State private var chosen = Set<String>()
    @State private var error = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Nuevo grupo").font(.lazo(16, .semibold))
            SectionLabel(text: "Nombre")
            TextField("Nombre del grupo", text: $title).textFieldStyle(.plain).font(.lazo(13))
                .padding(.horizontal, 10).padding(.vertical, 7).surface(10)
            SectionLabel(text: "Integrantes")
            ScrollView {
                VStack(alignment: .leading, spacing: 4) {
                    if peers.isEmpty { Text("No hay nadie más en la red ahora mismo.").font(.lazo(12)).opacity(0.6) }
                    ForEach(peers) { peer in
                        Toggle(peer.name, isOn: Binding(get: { chosen.contains(peer.id) }, set: { on in if on { chosen.insert(peer.id) } else { chosen.remove(peer.id) } }))
                            .toggleStyle(.checkbox).font(.lazo(12.5))
                    }
                }
            }.frame(maxHeight: 160)
            if !error.isEmpty { Text(error).font(.lazo(11.5)).foregroundStyle(Color(hex: "#E5675A")) }
            HStack {
                Spacer()
                PillButton(title: "Cancelar", action: cancel)
                PillButton(title: "Crear grupo", primary: true) {
                    let name = title.trimmingCharacters(in: .whitespaces)
                    let people = peers.filter { chosen.contains($0.id) }
                    if name.isEmpty { error = "Ponle un nombre al grupo."; return }
                    if people.isEmpty { error = "Elige al menos a una persona."; return }
                    if people.count >= ChatGroups.maxMembers { error = "Máximo \(ChatGroups.maxMembers - 1) personas."; return }
                    create(name, people)
                }
            }
        }
        .padding(16)
    }
}

private struct Messages: View {
    let id: String
    let name: String
    var isGroup = false
    @ObservedObject var model = AppModel.shared
    @ObservedObject var prefs = Prefs.shared

    var body: some View {
        let _ = model.chatVersion
        let lines = ChatStore.messages(id)
        ScrollViewReader { proxy in
            ScrollView {
                VStack(alignment: .leading, spacing: 4) {
                    ForEach(Array(lines.enumerated()), id: \.element.id) { index, line in
                        let previous = index > 0 ? lines[index - 1] : nil
                        if previous == nil || !Calendar.current.isDate(previous!.when, inSameDayAlso: line.when) {
                            Text(line.when.formatted(date: .complete, time: .omitted)).font(.lazo(10.5, .semibold)).opacity(0.5)
                                .frame(maxWidth: .infinity).padding(.vertical, 4)
                        }
                        let who = line.mine ? "Tú" : (isGroup && !line.sender.isEmpty ? line.sender : name)
                        let newBlock = previous == nil || previous!.mine != line.mine || previous!.sender != line.sender || line.when.timeIntervalSince(previous!.when) > 300
                        if newBlock {
                            HStack(spacing: 6) {
                                Text(who).font(.lazo(12, .semibold))
                                Text(line.when.formatted(date: .omitted, time: .shortened)).font(.lazo(10)).opacity(0.5)
                            }.padding(.top, 4)
                        }
                        MessageBody(line: line)
                            .contextMenu {
                                if !line.deleted && !line.nudge && line.filePath == nil {
                                    Button("Copiar texto") { NSPasteboard.general.clearContents(); NSPasteboard.general.setString(line.text, forType: .string) }
                                }
                                Button("Eliminar para mí", role: .destructive) { model.deleteForMe(id, line) }
                                if line.mine && !line.deleted && !line.msgId.isEmpty && !line.nudge {
                                    Button("Eliminar para todos", role: .destructive) { model.deleteForEveryone(id, line) }
                                }
                            }
                    }
                    Color.clear.frame(height: 1).id("end")
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .onAppear { proxy.scrollTo("end") }
            .onChange(of: lines.count) { _, _ in withAnimation { proxy.scrollTo("end") } }
        }
    }
}

private extension Calendar {
    func isDate(_ a: Date, inSameDayAlso b: Date) -> Bool { isDate(a, inSameDayAs: b) }
}

private struct MessageBody: View {
    let line: ChatLine
    @ObservedObject var prefs = Prefs.shared

    var body: some View {
        if line.deleted {
            Label(line.text, systemImage: "nosign").font(.lazo(12)).italic().opacity(0.6)
        } else if line.nudge {
            Label("Zumbido", systemImage: "bell.and.waves.left.and.right").font(.lazo(12)).opacity(0.75)
        } else if let path = line.filePath {
            let exists = FileManager.default.fileExists(atPath: path)
            Button {
                if exists { NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: path)]) }
            } label: {
                HStack(spacing: 8) {
                    Image(nsImage: NSWorkspace.shared.icon(forFile: path)).resizable().frame(width: 26, height: 26)
                    VStack(alignment: .leading, spacing: 1) {
                        Text(line.text).font(.lazo(12.5, .medium)).lineLimit(1).truncationMode(.middle)
                        Text(exists ? "Mostrar en carpeta" : "Ya no está en esta ubicación").font(.lazo(10.5)).opacity(0.6)
                    }
                }
                .padding(8).frame(maxWidth: 320, alignment: .leading)
                .background(RoundedRectangle(cornerRadius: 12).fill(prefs.palette.soft))
            }.buttonStyle(.plain).disabled(!exists)
        } else {
            Text(line.text).font(.lazo(13)).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
        }
    }
}
