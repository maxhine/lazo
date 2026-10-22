import AppKit
import LazoCore
import SwiftUI

/// Visor: entrar a otro equipo con su contraseña de control remoto (el anfitrión la define en sus Ajustes).
struct RemoteView: View {
    @ObservedObject var state: LauncherState
    @ObservedObject var model = AppModel.shared
    @ObservedObject var prefs = Prefs.shared
    @State private var selected: String?
    @State private var password = ""
    @State private var busy = false
    @State private var error = ""

    var body: some View {
        let p = prefs.palette
        VStack(alignment: .leading, spacing: 8) {
            PanelHeader(title: "Control remoto", state: state)
            Text("Entra a un equipo con su contraseña, sin que nadie tenga que aceptar. Verás su pantalla y la controlarás con el mouse y el teclado.")
                .font(.lazo(11.5)).foregroundStyle(p.muted).fixedSize(horizontal: false, vertical: true)
            SectionLabel(text: "Equipo")
            if model.peers.isEmpty { Text("Nadie conectado en esta red.").font(.lazo(12)).foregroundStyle(p.muted) }
            ScrollView {
                VStack(spacing: 2) {
                    ForEach(model.peers) { peer in
                        Button { selected = peer.id } label: {
                            HStack(spacing: 10) {
                                Avatar(name: peer.name, photo: peer.photo, size: 28)
                                Text(peer.name).font(.lazo(12.5, .medium)).lineLimit(1)
                                Spacer()
                                if selected == peer.id { Image(systemName: "checkmark.circle.fill").foregroundStyle(prefs.accentColor) }
                            }
                            .padding(.horizontal, 10).padding(.vertical, 6)
                            .background(RoundedRectangle(cornerRadius: 10).fill(selected == peer.id ? p.soft : .clear))
                            .contentShape(Rectangle())
                        }.buttonStyle(.plain)
                    }
                }
            }.frame(maxHeight: 180)
            SectionLabel(text: "Contraseña")
            SecureField("Contraseña de control remoto", text: $password).textFieldStyle(.plain).font(.lazo(13))
                .padding(.horizontal, 10).padding(.vertical, 8).surface(10).onSubmit(connect)
            if !error.isEmpty { Text(error).font(.lazo(11.5)).foregroundStyle(Color(hex: "#E5675A")).fixedSize(horizontal: false, vertical: true) }
            HStack {
                if busy { ProgressView().controlSize(.small); Text("Conectando…").font(.lazo(12)) }
                Spacer()
                PillButton(title: "Conectar", primary: true, action: connect).disabled(busy || selected == nil || password.isEmpty)
            }
            Text("La contraseña no se guarda. El otro equipo debe tener «Control remoto» activado en Ajustes.").font(.lazo(10.5)).foregroundStyle(p.muted)
        }
        .panelFrame(380)
        .onAppear {
            if let target = state.remoteTarget { selected = target; state.remoteTarget = nil }
            else if selected == nil, model.peers.count == 1 { selected = model.peers.first?.id }
        }
    }

    private func connect() {
        guard !busy, let id = selected, let peer = model.peer(id), !password.isEmpty else { return }
        busy = true
        error = ""
        let secret = password
        Task {
            let result = await model.connectRemote(peer, password: secret)
            busy = false
            if let result { error = result } else { password = "" }
        }
    }
}

/// Anfitrión: activar el control remoto y definir su contraseña (solo se guarda un hash).
struct RemoteHostSection: View {
    @ObservedObject var model = AppModel.shared
    @ObservedObject var prefs = Prefs.shared
    @State private var password = ""
    @State private var message = ""
    @State private var tick = 0

    var body: some View {
        let _ = tick
        let access = model.engine.remoteAccess
        VStack(alignment: .leading, spacing: 6) {
            SectionLabel(text: "Control remoto")
            Toggle("Permitir control remoto con contraseña", isOn: Binding(get: { access.isEnabled }, set: { access.setEnabled($0); tick += 1 }))
                .toggleStyle(.switch).font(.lazo(12.5)).disabled(!access.hasPassword)
            HStack(spacing: 6) {
                SecureField(access.hasPassword ? "Nueva contraseña" : "Elige una contraseña (mínimo 8)", text: $password)
                    .textFieldStyle(.plain).font(.lazo(12.5)).padding(.horizontal, 10).padding(.vertical, 6).surface(10)
                PillButton(title: access.hasPassword ? "Cambiar" : "Guardar") {
                    do { try access.setPassword(password); password = ""; message = "Contraseña guardada y control remoto activado."; tick += 1 }
                    catch { message = error.localizedDescription }
                }.disabled(password.isEmpty)
            }
            if !message.isEmpty { Text(message).font(.lazo(11)).foregroundStyle(prefs.palette.muted) }
            Text("Quien conozca la contraseña y esté en tu red podrá ver y controlar este Mac sin que lo aceptes. Tras 5 contraseñas erróneas, ese equipo queda bloqueado 10 minutos. Mientras alguien está dentro verás un aviso con el botón para terminar.")
                .font(.lazo(10.5)).foregroundStyle(prefs.palette.muted).fixedSize(horizontal: false, vertical: true)
            Toggle("Ajustar la resolución del otro equipo al controlar", isOn: $prefs.autoFit).toggleStyle(.switch).font(.lazo(12.5))
        }
    }
}
