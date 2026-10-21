import Foundation
import LazoCore

// Prueba de interoperabilidad con la versión de Windows (src/*.cs ejecutado con .NET).
// Uso:
//   lazo-interop serve <perfil> <carpeta> <udp> <tcp>        espera y acepta todo (empareja solo)
//   lazo-interop send  <perfil> <ip> <tcp> <id> <huella> <archivo> <mensaje>
//   lazo-interop self  <carpeta>                              Mac ↔ Mac en el mismo equipo

setvbuf(stdout, nil, _IOLBF, 0)
let args = CommandLine.arguments
func fail(_ text: String) -> Never { print("FALLO " + text); exit(1) }
func ok(_ text: String) { print("OK    " + text) }

func localAddress() -> String {
    guard let first = IPv4.interfaces().first else { fail("sin IPv4 privada") }
    return IPv4.text(first.address)
}

switch args.count > 1 ? args[1] : "" {
case "serve":
    let engine = NetworkEngine(receiveDirectory: URL(fileURLWithPath: args[3]), discoveryPort: Int(args[4])!,
                               transferPort: Int(args[5])!, profile: URL(fileURLWithPath: args[2]))
    engine.displayName = { "Mac de prueba" }
    engine.pairingPrompt = { request, answer in print("CODIGO \(request.code)"); answer(true) }
    engine.offerReceived = { offer, answer in print("OFERTA \(offer.fileName) \(offer.size) de \(offer.peerId)"); answer(true) }
    engine.receiveFinished = { _, message, url in print("RECIBIDO \(message) \(url?.path ?? "-")") }
    engine.chatReceived = { id, name, text in print("CHAT \(id) \(name): \(text)") }
    if let secret = ProcessInfo.processInfo.environment["LAZO_REMOTE_PASSWORD"] { try engine.remoteAccess.setPassword(secret) }
    engine.remoteAccepted = { connection, name in
        print("REMOTO \(name)")
        try? connection.stream.write(Data([UInt8(ascii: "E")])); try? connection.stream.flush()
        connection.close()
    }
    engine.joinRequested = { _, name in print("UNIRSE \(name)"); return true }
    engine.shareRequested = { _, name in print("COMPARTIR \(name)"); return true }
    try engine.start()
    print("LISTO \(engine.selfId) \(engine.identity.fingerprint)")
    RunLoop.main.run()

case "send":
    let engine = NetworkEngine(receiveDirectory: FileManager.default.temporaryDirectory, discoveryPort: 0, transferPort: Int(args[4])!,
                               profile: URL(fileURLWithPath: args[2]))
    engine.displayName = { "Mac de prueba" }
    engine.pairingPrompt = { request, answer in print("CODIGO \(request.code)"); answer(true) }
    let peer = Peer(id: args[5], name: "Windows", address: args[3], port: Int(args[4])!, fingerprint: args[6])
    do {
        try engine.send(peer, file: URL(fileURLWithPath: args[7]), progress: nil)
        ok("archivo enviado a Windows")
        try engine.sendChat(peer, text: args[8])
        ok("chat enviado a Windows")
        if try engine.requestJoin(peer) { ok("Mac → Windows: petición de unirse aceptada") } else { fail("petición de unirse rechazada") }
        if try engine.requestShare(peer) { ok("Mac → Windows: petición de compartir aceptada") } else { fail("petición de compartir rechazada") }
        try engine.sendReliableSignal(peer, kind: NetworkEngine.chatKindDelete, body: "a1b2c3d4")
        ok("Mac → Windows: borrado entregado")
    } catch { fail(error.localizedDescription) }

case "remote":
    // lazo-interop remote <perfil> <ip> <tcp> <id> <huella> <contraseña> [falla]
    let engine = NetworkEngine(receiveDirectory: FileManager.default.temporaryDirectory, discoveryPort: 0, transferPort: Int(args[4])!,
                               profile: URL(fileURLWithPath: args[2]))
    engine.displayName = { "Mac de prueba" }
    let peer = Peer(id: args[5], name: "Windows", address: args[3], port: Int(args[4])!, fingerprint: args[6])
    let expectFailure = args.count > 8
    do {
        let connection = try engine.connectRemote(peer, password: args[7])
        // El anfitrión empieza a emitir: se lee hasta el aviso de fin que envía la prueba.
        let first = try connection.stream.readByte()
        connection.close()
        if expectFailure { fail("entró con una contraseña que debía fallar") }
        ok("Mac → Windows: control remoto aceptado (primer paquete \(Character(UnicodeScalar(first))))")
    } catch {
        if expectFailure { ok("Mac → Windows: contraseña incorrecta rechazada (\(error.localizedDescription))") } else { fail("control remoto: \(error.localizedDescription)") }
    }

case "self":
    let root = URL(fileURLWithPath: args[2])
    let a = NetworkEngine(receiveDirectory: root.appendingPathComponent("a-in"), discoveryPort: 0, transferPort: 48452, profile: root.appendingPathComponent("a"))
    let b = NetworkEngine(receiveDirectory: root.appendingPathComponent("b-in"), discoveryPort: 48451, transferPort: 48452, profile: root.appendingPathComponent("b"))
    var codes: [String] = []
    a.pairingPrompt = { r, answer in codes.append(r.code); answer(true) }
    b.pairingPrompt = { r, answer in codes.append(r.code); answer(true) }
    b.offerReceived = { _, answer in answer(true) }
    let done = DispatchSemaphore(value: 0)
    var received: URL?
    b.receiveFinished = { _, _, url in received = url; done.signal() }
    try b.start()
    let file = root.appendingPathComponent("origen.bin")
    let payload = LazoCrypto.random(2 * 1024 * 1024 + 13)
    try payload.write(to: file)
    let peer = Peer(id: b.selfId, name: "B", address: localAddress(), port: 48452, fingerprint: b.identity.fingerprint)
    do { try a.send(peer, file: file, progress: nil) } catch { fail(error.localizedDescription) }
    _ = done.wait(timeout: .now() + 10)
    guard let received, (try? Data(contentsOf: received)) == payload else { fail("archivo Mac → Mac") }
    ok("Mac → Mac cifrado al primer contacto, sin código (\(payload.count) bytes, avisos: \(codes.count))")
    let verified = (try? a.verify(peer)) ?? false
    Thread.sleep(forTimeInterval: 0.3)
    guard verified, codes.count == 2, Set(codes).count == 1, a.pairings.get(b.selfId)?.isVerified == true, b.pairings.get(a.selfId)?.isVerified == true else { fail("verificación opcional") }
    ok("verificación opcional: mismo código \(codes[0]) en los dos y ambos marcados")
    // Control remoto con contraseña (Mac ↔ Mac): acierto, error y bloqueo tras 5 fallos. B es el anfitrión; A, el visor.
    try b.remoteAccess.setPassword("claveSegura1")
    var entered = 0
    b.remoteAccepted = { connection, _ in entered += 1; connection.close() }
    do { let c = try a.connectRemote(peer, password: "claveSegura1"); c.close() } catch { fail("control remoto con la contraseña correcta: \(error.localizedDescription)") }
    Thread.sleep(forTimeInterval: 0.2)
    guard entered == 1 else { fail("el anfitrión no recibió la sesión remota") }
    ok("control remoto: contraseña correcta acepta y entrega la conexión")
    var rejected = 0
    for _ in 0..<5 { if (try? a.connectRemote(peer, password: "equivocada")) == nil { rejected += 1 } }
    guard rejected == 5 else { fail("la contraseña incorrecta debía rechazarse") }
    ok("control remoto: contraseña incorrecta rechazada 5 veces")
    if (try? a.connectRemote(peer, password: "claveSegura1")) != nil { fail("tras 5 fallos debía quedar bloqueado") }
    ok("control remoto: bloqueo tras 5 intentos fallidos (incluso con la contraseña correcta)")
    b.stop()

default:
    print("Uso: lazo-interop serve|send|self …")
}
