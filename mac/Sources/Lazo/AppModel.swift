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
    var nudged: (() -> Void)?
    private var toastTimer: Timer?

    private init() {}

    func start() {
        engine.displayName = { Prefs.shared.displayName }
        engine.photo = { Prefs.shared.photo }
        engine.peersChanged = { list in Task { @MainActor in AppModel.shared.peers = list } }
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
            Task { @MainActor in ReceiveCenter.shared.offer(offer, answer: answer) }
        }
        engine.receiveProgress = { id, value in Task { @MainActor in ReceiveCenter.shared.progress(id, value) } }
        engine.receiveFinished = { id, message, url in Task { @MainActor in ReceiveCenter.shared.finished(id, message, url) } }
        engine.chatReceived = { id, name, text in Task { @MainActor in AppModel.shared.chatReceived(id, name, text) } }
        engine.chatSignal = { id, name, kind, body in Task { @MainActor in AppModel.shared.chatSignal(id, name, kind, body) } }
        engine.screenOffered = { invite, answer in Task { @MainActor in ScreenViewerController.offer(invite, answer: answer) } }
        do { try engine.start() } catch {
            networkError = error.localizedDescription
            say("Red no disponible: " + error.localizedDescription)
        }
    }

    func peer(_ id: String) -> Peer? { peers.first { $0.id == id } }

    func say(_ text: String) {
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

    func pair(_ peer: Peer) {
        Task.detached { [engine] in
            do {
                try engine.pair(peer)
                await MainActor.run { AppModel.shared.say("Emparejado con \(peer.name)") }
            } catch {
                await MainActor.run { AppModel.shared.say(error.localizedDescription) }
            }
        }
    }

    // MARK: Chat

    func sendChat(_ peer: Peer, _ text: String) async -> Bool {
        let engine = self.engine
        do {
            try await Task.detached { try engine.sendChat(peer, text: text) }.value
            ChatStore.append(peer.id, name: peer.name, mine: true, text: text)
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

    private func chatReceived(_ id: String, _ name: String, _ text: String) {
        ChatStore.appendIncoming(id, name: name, text: text)
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
        default: break
        }
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
