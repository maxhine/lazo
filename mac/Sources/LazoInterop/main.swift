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
    } catch { fail(error.localizedDescription) }

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
    b.stop()

default:
    print("Uso: lazo-interop serve|send|self …")
}
