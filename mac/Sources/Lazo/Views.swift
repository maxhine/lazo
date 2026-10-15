import AppKit
import LazoCore
import SwiftUI

struct PanelHeader: View {
    let title: String
    @ObservedObject var state: LauncherState

    var body: some View {
        HStack {
            Button { state.route = .home } label: { Image(systemName: "chevron.left").font(.system(size: 13, weight: .semibold)) }
                .buttonStyle(.plain).help("Volver")
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
                    Toggle("Liquid Glass", isOn: $prefs.liquidGlass).toggleStyle(.switch).font(.lazo(12.5))
                    SectionLabel(text: "Avisos de chat")
                    Segmented(options: [(false, "Discretos"), (true, "Notificación")], selection: $prefs.chatBanner)
                    SectionLabel(text: "Sistema")
                    Toggle("Abrir al iniciar sesión", isOn: Binding(get: { prefs.startsAtLogin }, set: { prefs.startsAtLogin = $0 }))
                        .toggleStyle(.switch).font(.lazo(12.5))
                    Toggle("Buscar actualizaciones", isOn: $prefs.updates).toggleStyle(.switch).font(.lazo(12.5))
                    HStack {
                        PillButton(title: "Verificar actualizaciones") {
                            updateState = "Buscando…"
                            Updater.check { text in updateState = text }
                        }
                        Text(updateState).font(.lazo(11)).opacity(0.65)
                    }
                    PermissionsSection()
                    PairedSection()
                    Text("Lazo \(Updater.currentVersion) · Recibidos en Descargas/Lazo").font(.lazo(10.5)).opacity(0.5).padding(.top, 8)
                }
                .padding(.trailing, 8)
            }
            .frame(maxHeight: 470)
        }
        .frame(width: 360)
        .onAppear { nameDraft = prefs.name.isEmpty ? prefs.displayName : prefs.name }
        .onDisappear { if nameDraft != prefs.displayName { prefs.name = nameDraft } }
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

/// Equipos emparejados y huella propia. Olvidar uno obliga a confirmar el código de nuevo.
struct PairedSection: View {
    @ObservedObject var model = AppModel.shared

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            SectionLabel(text: "Equipos emparejados")
            Text("Huella de este Mac: " + LazoCrypto.shortFingerprint(model.engine.identity.fingerprint)).font(.lazo(10.5)).opacity(0.6)
            let devices = model.engine.pairings.all()
            if devices.isEmpty {
                Text("Ninguno todavía. Al enviar algo a alguien por primera vez, los dos confirman un código de 6 cifras.")
                    .font(.lazo(11)).opacity(0.65).fixedSize(horizontal: false, vertical: true)
            }
            ForEach(devices, id: \.id) { device in
                HStack {
                    VStack(alignment: .leading, spacing: 1) {
                        Text(device.name).font(.lazo(12.5, .medium))
                        Text(LazoCrypto.shortFingerprint(device.fingerprint)).font(.lazo(10.5)).opacity(0.6)
                    }
                    Spacer()
                    PillButton(title: "Olvidar") { model.engine.pairings.remove(device.id) }
                        .help("Habrá que volver a confirmar el código para enviarle algo")
                }
            }
        }
        .id(model.pairedVersion)
    }
}

// MARK: - Historial

struct HistoryView: View {
    @ObservedObject var state: LauncherState

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            PanelHeader(title: "Historial", state: state)
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
            }.frame(maxHeight: 360)
        }
        .frame(width: 380)
    }
}

// MARK: - Chat al estilo de Discord

struct ChatView: View {
    @ObservedObject var state: LauncherState
    @ObservedObject var model = AppModel.shared
    @ObservedObject var prefs = Prefs.shared
    @State private var draft = ""
    @State private var drafts: [String: String] = [:]
    @State private var lastTyping = Date.distantPast
    @State private var presence: Timer?

    private var threads: [(id: String, name: String, preview: String, unread: Int, online: Bool)] {
        _ = model.chatVersion
        var list = ChatStore.threads().map { ($0.id, model.peer($0.id)?.name ?? $0.name, $0.preview, $0.unread, model.peer($0.id) != nil) }
        for peer in model.peers where !list.contains(where: { $0.0 == peer.id }) { list.append((peer.id, peer.name, "", 0, true)) }
        return list
    }

    var body: some View {
        let p = prefs.palette
        VStack(alignment: .leading, spacing: 8) {
            PanelHeader(title: "Chat", state: state)
            HStack(alignment: .top, spacing: 10) {
                ScrollView {
                    VStack(spacing: 2) {
                        ForEach(threads, id: \.id) { thread in
                            Button { select(thread.id) } label: {
                                HStack(spacing: 8) {
                                    Avatar(name: thread.name, photo: model.peer(thread.id)?.photo ?? "", size: 30, online: thread.online)
                                    VStack(alignment: .leading, spacing: 1) {
                                        Text(thread.name).font(.lazo(12.5, thread.unread > 0 ? .bold : .medium)).lineLimit(1)
                                        if !thread.preview.isEmpty { Text(thread.preview).font(.lazo(10.5)).opacity(0.6).lineLimit(1) }
                                    }
                                    Spacer(minLength: 0)
                                    if thread.unread > 0 { Text("\(thread.unread)").font(.lazo(10, .bold)).padding(.horizontal, 6).padding(.vertical, 2).background(Capsule().fill(prefs.accentColor)).foregroundStyle(.white) }
                                }
                                .padding(6)
                                .background(RoundedRectangle(cornerRadius: 10).fill(state.chatPeer == thread.id ? p.soft : .clear))
                                .contentShape(Rectangle())
                            }.buttonStyle(.plain)
                        }
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
            .frame(height: 400)
        }
        .frame(width: 620)
        .onAppear {
            if state.chatPeer == nil { state.chatPeer = threads.first?.id }
            if let id = state.chatPeer { select(id) }
            presence = Timer.scheduledTimer(withTimeInterval: 5, repeats: true) { _ in
                Task { @MainActor in if let id = state.chatPeer, let peer = model.peer(id) { model.signal(peer, NetworkEngine.chatKindPresence, "1") } }
            }
        }
        .onDisappear {
            presence?.invalidate()
            if let id = state.chatPeer, let peer = model.peer(id) { model.signal(peer, NetworkEngine.chatKindPresence, "0") }
        }
    }

    private func select(_ id: String) {
        if let current = state.chatPeer { drafts[current] = draft }
        state.chatPeer = id
        draft = drafts[id] ?? ""
        model.markRead(id)
        model.chatVersion += 1
    }

    @ViewBuilder private func conversation(_ id: String) -> some View {
        let peer = model.peer(id)
        let name = peer?.name ?? ChatStore.threads().first { $0.id == id }?.name ?? "Compañero"
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text(name).font(.lazo(14, .semibold))
                if let typing = model.typing[id], typing > Date() { Text("Escribiendo…").font(.lazo(11)).opacity(0.6) }
                else if let inChat = model.inChat[id], inChat > Date() { Text("En el chat").font(.lazo(11)).opacity(0.6) }
                Spacer()
                if let peer {
                    IconButton(symbol: "display", tip: "Compartir pantalla") { ShareStarter.startQuick(with: peer) }
                    IconButton(symbol: "bell.and.waves.left.and.right", tip: "Zumbido") { model.nudge(peer) }
                    IconButton(symbol: "paperclip", tip: "Enviar archivo") {
                        let files = LauncherController.shared.pickFiles("Elige qué enviar a \(peer.name)")
                        if !files.isEmpty { model.send(files, to: peer) }
                    }
                }
            }
            Messages(id: id, name: name)
            HStack(spacing: 8) {
                TextField(peer == nil ? "\(name) no está conectado" : "Mensaje para \(name)", text: $draft, axis: .vertical)
                    .textFieldStyle(.plain).font(.lazo(13)).lineLimit(1...4)
                    .disabled(peer == nil)
                    .onSubmit { send(peer) }
                    .onChange(of: draft) { _, _ in
                        guard let peer, Date().timeIntervalSince(lastTyping) > 2 else { return }
                        lastTyping = Date()
                        model.signal(peer, NetworkEngine.chatKindTyping, "1")
                    }
                Button { send(peer) } label: { Image(systemName: "arrow.up.circle.fill").font(.system(size: 20)) }
                    .buttonStyle(.plain).disabled(peer == nil || draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
            .padding(.horizontal, 12).padding(.vertical, 8).surface(18)
        }
        .onDrop(of: [.fileURL], isTargeted: nil) { providers in
            guard let peer else { return false }
            Drop.files(providers) { model.send($0, to: peer) }
            return true
        }
    }

    private func send(_ peer: Peer?) {
        guard let peer else { return }
        let text = draft.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return }
        draft = ""
        Task {
            // Si el envío falla, el borrador vuelve para no perder lo escrito.
            if !(await model.sendChat(peer, text)) { draft = text }
            model.signal(peer, NetworkEngine.chatKindTyping, "0")
        }
    }
}

private struct Messages: View {
    let id: String
    let name: String
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
                        if previous == nil || !Calendar.current.isDate(previous!.when, inSameDayAs: line.when) {
                            Text(line.when.formatted(date: .complete, time: .omitted)).font(.lazo(10.5, .semibold)).opacity(0.5)
                                .frame(maxWidth: .infinity).padding(.vertical, 4)
                        }
                        if previous == nil || previous!.mine != line.mine || line.when.timeIntervalSince(previous!.when) > 300 {
                            HStack(spacing: 6) {
                                Text(line.mine ? "Tú" : name).font(.lazo(12, .semibold))
                                Text(line.when.formatted(date: .omitted, time: .shortened)).font(.lazo(10)).opacity(0.5)
                            }.padding(.top, 4)
                        }
                        MessageBody(line: line)
                    }
                    Color.clear.frame(height: 1).id("end")
                }
            }
            .onAppear { proxy.scrollTo("end") }
            .onChange(of: lines.count) { _, _ in withAnimation { proxy.scrollTo("end") } }
        }
    }
}

private struct MessageBody: View {
    let line: ChatLine
    @ObservedObject var prefs = Prefs.shared

    var body: some View {
        if line.nudge {
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
