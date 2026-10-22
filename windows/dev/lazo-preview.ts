// Dev harness: la tarjeta de Lazo con un motor simulado, en un navegador normal. No forma parte de la app.
import "../src/style.css";
import { LazoEngine } from "../src/lazo/engine";
import { Lazo } from "../src/core/lazo";
import { buildLazoCard } from "../src/views/lazo";

const row = document.getElementById("row")!;

function scene(caption: string, script: (push: (m: unknown) => void, engine: LazoEngine) => void, pending = false, error = "") {
  const socket: any = { onopen: null, onmessage: null, onclose: null, onerror: null, send() {}, close() {} };
  const engine = new LazoEngine("ws://x", () => socket);
  const card = buildLazoCard();
  const fig = document.createElement("figure");
  const stage = document.createElement("div");
  stage.className = "stage";
  stage.append(card.el);
  const cap = document.createElement("figcaption");
  cap.textContent = caption;
  fig.append(stage, cap);
  row.append(fig);
  void engine.connect().then(() => {
    if (!error) socket.onopen({});
    const push = (m: unknown) => socket.onmessage({ data: JSON.stringify(m) });
    script(push, engine);
    const draw = () => {
      Lazo.engine = engine; Lazo.error = error;
      Lazo.pendingFile = pending ? { name: "plano-estructural.pdf", path: "C:/x/plano-estructural.pdf" } : null;
      card.sync();
    };
    draw();
    engine.on(draw);
  });
}

const hello = { type: "hello", version: 1, selfId: "me", name: "Jhon", fingerprint: "ff", receiveDir: "C:/r" };
const peer = (id: string, name: string, extra = {}) => ({ id, name, address: "10.0.0.5", paired: true, verified: false, identityChanged: false, sharing: "", hasPhoto: false, ...extra });

scene("sin motor (Lazo clásico abierto)", () => {}, false, "Classic Lazo is open: close it so the island can use the network.");
scene("conectado, nadie cerca", (push) => { push(hello); push({ type: "peers", peers: [] }); });
scene("personas", (push) => { push(hello); push({ type: "peers", peers: [peer("a", "Luis Gómez"), peer("b", "Marta", { sharing: "Pantalla" }), peer("c", "Equipo-Obra", { identityChanged: true })] }); });
scene("elegir a quién enviar", (push) => { push(hello); push({ type: "peers", peers: [peer("a", "Luis Gómez"), peer("b", "Marta")] }); }, true);
scene("oferta recibida", (push) => {
  push(hello); push({ type: "peers", peers: [peer("a", "Luis Gómez")] });
  push({ type: "offer", id: "o1", peerId: "a", sender: "Luis Gómez", fileName: "nube-puntos-nivel-2.las", size: 1_840_000_000, preview: "" });
});
scene("recibiendo", (push, engine) => {
  push(hello); push({ type: "peers", peers: [peer("a", "Luis Gómez")] });
  push({ type: "offer", id: "o1", peerId: "a", sender: "Luis Gómez", fileName: "informe.pdf", size: 4_200_000, preview: "" });
  push({ type: "receiveProgress", id: "o1", value: 0.55 });
}, false);
