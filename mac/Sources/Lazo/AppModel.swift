import AppKit
import LazoCore
import SwiftUI
import UserNotifications

/// Estado de la app y puente entre la red (hilos propios) y la interfaz (hilo principal).
@MainActor
final class AppModel: ObservableObject {
    static let shared = AppModel()

    let engine = NetworkEngine()
    let prefs = Prefs.shared
    @Published var peers: [Peer] = []
    @Published var networkError: String?
    @Published var toast: String?
    @Published var sending: [String: Double] = [:]
    @Published var unread = ChatStore.unreadTotal()
    @Published var typing: [String: Date] = [:]
    @Published var inChat: [String: Date] = [:]
    @Published var chatVersion = 0
    @Published var pairedVersion = 0
    @Published var share: ScreenShareSession?
    /// Quien pidió que compartas: se marca de antemano en «Compartir pantalla».
    @Published var shareTarget: String?
    var nudged: (() -> Void)?
    private var toastTimer: Timer?

    private init() {}

    func start() {
        engine.displayName = { Prefs.shared.displayName }
        engine.photo = { Prefs.shared.photo }
        engine.peersChanged = { list in Task { @MainActor in AppModel.shared.peers = list; AppModel.shared.flushOutbox() } }
        engine.pairings.changed = { [weak self] in
            Task { @MainActor in
                guard let self else { return }
                self.peers = self.engine.snapshot()
                self.pairedVersion += 1
            }
        }
        engine.pairingPrompt = { request, answer in
            Task { @MainActor in PairingPanel.show(request, answer: answer) }
        }
        engine.offerReceived = { offer, answer in
            DiagLog.write("oferta de archivo de \(offer.sender): \(offer.fileName)")
            Task { @MainActor in DiagLog.write("mostrando aviso de recepción"); ReceiveCenter.shared.offer(offer, answer: answer) }
        }
        engine.receiveProgress = { id, value in Task { @MainActor in ReceiveCenter.shared.progress(id, value) } }
        engine.receiveFinished = { id, message, url in Task { @MainActor in ReceiveCenter.shared.finished(id, message, url) } }
        engine.chatReceived = { id, name, text in Task { @MainActor in AppModel.shared.chatReceived(id, name, text) } }
        engine.chatSignal = { id, name, kind, body in Task { @MainActor in AppModel.shared.chatSignal(id, name, kind, body) } }
        engine.joinRequested = { id, name in
            var taken = false
            DispatchQueue.main.sync { taken = AppModel.shared.share != nil; if taken { ShareStarter.askWatch(id, name) } }
            return taken
        }
        engine.shareRequested = { id, name in
            var taken = false
            DispatchQueue.main.sync { taken = AppModel.shared.share == nil; if taken { ShareStarter.askShare(id, name) } }
            return taken
        }
        engine.remoteBusy = { DispatchQueue.main.sync { AppModel.shared.share != nil } }
        engine.remoteAccepted = { connection, name in
            Task { @MainActor in
                ShareStarter.startRemote(connection, name)
                NSSound(named: "Pop")?.play()
            }
        }
        engine.screenOffered = { invite, answer in Task { @MainActor in ScreenViewerController.offer(invite, answer: answer) } }
        do { try engine.start() } catch {
            networkError = error.localizedDescription
            say("Red no disponible: " + error.localizedDescription)
        }
    }

    func peer(_ id: String) -> Peer? { peers.first { $0.id == id } }

    func say(_ text: String) {
        DiagLog.write("aviso: \(text)")
        toast = text
        toastTimer?.invalidate()
        toastTimer = Timer.scheduledTimer(withTimeInterval: 3.5, repeats: false) { _ in Task { @MainActor in AppModel.shared.toast = nil } }
        ToastPanel.show(text)
    }

    // MARK: Archivos

    func send(_ files: [URL], to peer: Peer) {
        if peer.identityChanged {
            say("La identidad de \(peer.name) cambió. Revísalo en Ajustes › Equipos emparejados.")
            return
        }
        for file in files where !file.hasDirectoryPath {
            let key = peer.id + "/" + file.lastPathComponent
            sending[key] = 0
            Task.detached { [engine] in
                do {
                    try engine.send(peer, file: file) { value in Task { @MainActor in AppModel.shared.sending[key] = value } }
                    await MainActor.run {
                        let model = AppModel.shared
                        model.sending[key] = nil
                        TransferLog.add(incoming: false, peer: peer.name, name: file.lastPathComponent, path: file.path)
                        ChatStore.appendFile(peer.id, name: peer.name, mine: true, path: file.path)
                        model.chatVersion += 1
                        model.say("Entregado a \(peer.name): \(file.lastPathComponent)")
                    }
                } catch {
                    await MainActor.run {
                        AppModel.shared.sending[key] = nil
                        AppModel.shared.say(error.localizedDescription)
                    }
                }
            }
        }
    }

    /// Verificación opcional con código de 6 cifras.
    func verify(_ peer: Peer) {
        Task.detached { [engine] in
            do {
                let ok = try engine.verify(peer)
                await MainActor.run { AppModel.shared.say(ok ? "\(peer.name) quedó verificado" : "No se verificó") }
            } catch {
                await MainActor.run { AppModel.shared.say(error.localizedDescription) }
            }
        }
    }

    // MARK: Chat

    func sendChat(_ peer: Peer, _ text: String) async -> Bool {
        let engine = self.engine
        let messageId = ChatStore.newId()
        let wire = WireId.wrap(messageId, text)
        do {
            try await Task.detached { try engine.sendChat(peer, text: wire) }.value
            ChatStore.append(peer.id, name: peer.name, mine: true, text: text, msgId: messageId)
            chatVersion += 1
            return true
        } catch {
            say(error.localizedDescription)
            return false
        }
    }

    func signal(_ peer: Peer, _ kind: UInt8, _ body: String) {
        let engine = self.engine
        Task.detached { engine.sendSignal(peer, kind: kind, body: body) }
    }

    func nudge(_ peer: Peer) {
        signal(peer, NetworkEngine.chatKindNudge, "")
        ChatStore.appendNudge(peer.id, name: peer.name, mine: true)
        chatVersion += 1
    }

    private func chatReceived(_ id: String, _ name: String, _ raw: String) {
        DiagLog.write("mensaje de \(name)")
        let (messageId, text) = WireId.unwrap(raw)
        ChatStore.appendIncoming(id, name: name, text: text, msgId: messageId)
        chatVersion += 1
        unread = ChatStore.unreadTotal()
        if !LauncherController.shared.isViewingChat(id) {
            if prefs.chatBanner { Notifier.post(title: name, body: text) } else { say("\(name): \(text)") }
        }
    }

    private func chatSignal(_ id: String, _ name: String, _ kind: UInt8, _ body: String) {
        switch kind {
        case NetworkEngine.chatKindTyping:
            typing[id] = body == "1" ? Date().addingTimeInterval(3) : nil
        case NetworkEngine.chatKindPresence:
            inChat[id] = body == "1" ? Date().addingTimeInterval(9) : nil
        case NetworkEngine.chatKindNudge:
            ChatStore.appendNudge(id, name: name, mine: false)
            chatVersion += 1
            unread = ChatStore.unreadTotal()
            NSSound(named: "Funk")?.play()
            LauncherController.shared.shake()
            if !LauncherController.shared.isViewingChat(id) { say("\(name) te envió un zumbido") }
        case NetworkEngine.chatKindDelete...NetworkEngine.chatKindGroupLeave:
            let result = ChatEvents.apply(from: id, name: name, kind: kind, body: body, selfId: engine.selfId)
            guard result.changed else { return }
            chatVersion += 1
            unread = ChatStore.unreadTotal()
            if result.notify && !LauncherController.shared.isViewingChat(result.thread) {
                if prefs.chatBanner { Notifier.post(title: result.title, body: result.text) } else { say("\(result.title): \(result.text)") }
            }
        default: break
        }
    }

    // MARK: Control remoto con contraseña (visor)

    /// Entra al equipo con su contraseña de control remoto y abre su pantalla ya en modo control. Devuelve el error, o nil si entró.
    func connectRemote(_ peer: Peer, password: String) async -> String? {
        let engine = self.engine
        do {
            let connection = try await Task.detached { try engine.connectRemote(peer, password: password) }.value
            let pane = ViewerPane(sender: peer.name, source: "Control remoto")
            pane.autoControl = true
            StageWindow.add(pane)
            Thread.detachNewThread { ViewerSink(pane: pane).run(connection.stream) }
            LauncherController.shared.hide()
            return nil
        } catch {
            return error.localizedDescription
        }
    }

    // MARK: Compartir pantalla: unirse y pedir

    /// Pide unirse a la pantalla que alguien está compartiendo; quien comparte decide.
    func requestJoin(_ peer: Peer) {
        let engine = self.engine
        Task.detached {
            do {
                let taken = try engine.requestJoin(peer)
                await MainActor.run { AppModel.shared.say(taken ? "Pediste ver la pantalla de \(peer.name)" : "\(peer.name) ya no comparte") }
            } catch { await MainActor.run { AppModel.shared.say(error.localizedDescription) } }
        }
    }

    /// Pide a alguien que comparta su pantalla; si acepta, elige qué y llega una invitación normal.
    func requestShare(_ peer: Peer) {
        let engine = self.engine
        Task.detached {
            do {
                let taken = try engine.requestShare(peer)
                await MainActor.run { AppModel.shared.say(taken ? "Le pediste a \(peer.name) que comparta" : "\(peer.name) no puede compartir ahora") }
            } catch { await MainActor.run { AppModel.shared.say(error.localizedDescription) } }
        }
    }

    // MARK: Entrega diferida, grupos y borrado

    private var flushed: [String: Date] = [:]

    /// Lo que no salió porque alguien estaba desconectado sale cuando vuelve.
    func flushOutbox() {
        let engine = self.engine
        for peer in peers {
            if let last = flushed[peer.id], Date().timeIntervalSince(last) < 15 { continue }
            guard ChatOutbox.pending(peer.id) > 0 else { continue }
            flushed[peer.id] = Date()
            Task.detached {
                for item in ChatOutbox.items(for: peer.id) {
                    do { try engine.sendReliableSignal(peer, kind: item.kind, body: item.body); ChatOutbox.done(item) } catch { break }
                }
            }
        }
    }

    /// Envía a una persona; si no se puede ahora, queda en la cola y sale cuando vuelva a estar en línea.
    func deliver(_ person: String, kind: UInt8, body: String) {
        guard let peer = peer(person) else { ChatOutbox.add(person, kind: kind, body: body); return }
        let engine = self.engine
        Task.detached {
            do { try engine.sendReliableSignal(peer, kind: kind, body: body) } catch { ChatOutbox.add(person, kind: kind, body: body) }
        }
    }

    func sendGroup(_ group: ChatGroup, _ text: String) {
        let messageId = ChatStore.newId()
        ChatStore.append(group.id, name: group.name, mine: true, text: text, msgId: messageId, sender: prefs.displayName)
        let packed = GuidText.n(group.id) + "\n" + messageId + "\n" + text
        for member in group.members { deliver(member.id, kind: NetworkEngine.chatKindGroupMessage, body: packed) }
        chatVersion += 1
    }

    func createGroup(_ title: String, _ chosen: [Peer]) -> ChatGroup {
        var group = ChatGroup(id: UUID().uuidString.lowercased(), name: String(ChatGroups.clean(title).prefix(40)), owner: engine.selfId, members: [])
        group.members = chosen.map { GroupMember(id: $0.id, name: $0.name) }
        ChatGroups.save(group)
        ChatStore.ensureThread(group.id, name: group.name)
        let packed = ChatGroups.pack(group, selfId: engine.selfId, selfName: prefs.displayName)
        for member in group.members { deliver(member.id, kind: NetworkEngine.chatKindGroupInfo, body: packed) }
        chatVersion += 1
        return group
    }

    func leaveGroup(_ group: ChatGroup) {
        for member in group.members { deliver(member.id, kind: NetworkEngine.chatKindGroupLeave, body: GuidText.n(group.id)) }
        ChatGroups.remove(group.id)
        ChatStore.remove(group.id)
        chatVersion += 1
        unread = ChatStore.unreadTotal()
    }

    func deleteForMe(_ thread: String, _ line: ChatLine) {
        ChatStore.deleteLocal(thread, line)
        chatVersion += 1
    }

    func deleteForEveryone(_ thread: String, _ line: ChatLine) {
        ChatStore.markDeleted(thread, msgId: line.msgId, sender: nil, fromOther: false)
        if let group = ChatGroups.get(thread) {
            let body = GuidText.n(group.id) + "\n" + line.msgId
            for member in group.members { deliver(member.id, kind: NetworkEngine.chatKindDelete, body: body) }
        } else {
            deliver(thread, kind: NetworkEngine.chatKindDelete, body: line.msgId)
        }
        chatVersion += 1
    }

    func clearThread(_ thread: String) {
        ChatStore.clear(thread)
        chatVersion += 1
        unread = ChatStore.unreadTotal()
    }

    func markRead(_ id: String) {
        ChatStore.markRead(id)
        unread = ChatStore.unreadTotal()
    }
}

/// Avisos del sistema para el chat (si se eligió «Notificación» en Ajustes).
enum Notifier {
    static func post(title: String, body: String) {
        let center = UNUserNotificationCenter.current()
        center.requestAuthorization(options: [.alert, .sound]) { granted, _ in
            guard granted else { return }
            let content = UNMutableNotificationContent()
            content.title = title
            content.body = body
            center.add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
        }
    }
}
