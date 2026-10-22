// The engine client (src/lazo/engine.ts) against a scripted socket: what it keeps from each
// message and what it sends back. The real engine is exercised by engine/test-engine.mjs.

import { test } from "node:test";
import assert from "node:assert/strict";
import { ENGINE_PROTOCOL, LazoEngine, engineUrl } from "../src/lazo/engine.ts";

function fakeSocket() {
  const sent = [];
  const socket = { onopen: null, onmessage: null, onclose: null, onerror: null, sent, closed: false,
    send: (d) => sent.push(JSON.parse(d)), close() { socket.closed = true; } };
  return socket;
}
async function connected() {
  const socket = fakeSocket();
  const urls = [];
  const engine = new LazoEngine(async () => "ws://x", (u) => { urls.push(u); return socket; }, 5);
  await engine.connect();
  socket.onopen({});
  const push = (m) => socket.onmessage({ data: JSON.stringify(m) });
  return { engine, socket, push, urls };
}
const hello = { type: "hello", version: ENGINE_PROTOCOL, selfId: "a", name: "Ana", fingerprint: "ff", receiveDir: "C:\\r" };
const peer = { id: "p1", name: "Luis", address: "10.0.0.5", paired: true, verified: false, identityChanged: false, sharing: "", hasPhoto: false };

test("the URL carries the token, escaped", () => {
  assert.equal(engineUrl(48360, "ab c"), "ws://127.0.0.1:48360/ws?t=ab%20c");
});

test("hello and peers fill the state", async () => {
  const { engine, push } = await connected();
  assert.equal(engine.state.connected, true);
  push(hello);
  push({ type: "peers", peers: [peer] });
  assert.equal(engine.state.self.name, "Ana");
  assert.deepEqual(engine.state.peers.map((p) => p.name), ["Luis"]);
});

test("an engine speaking another protocol is dropped", async () => {
  const { engine, push, socket } = await connected();
  const errors = [];
  engine.on((e) => e.type === "error" && errors.push(e.message));
  push({ ...hello, version: ENGINE_PROTOCOL + 1 });
  assert.equal(errors.length, 1);
  assert.equal(socket.closed, true);
});

test("an offer waits, then follows accept, progress and the result", async () => {
  const { engine, push, socket } = await connected();
  push({ type: "offer", id: "o1", peerId: "p1", sender: "Luis", fileName: "plano.pdf", size: 10, preview: "" });
  assert.equal(engine.state.offers[0].status, "pending");
  engine.decideOffer("o1", true);
  assert.deepEqual(socket.sent.at(-1), { cmd: "offerDecision", id: "o1", accept: true });
  assert.equal(engine.state.offers[0].status, "receiving");
  push({ type: "receiveProgress", id: "o1", value: 0.5 });
  assert.equal(engine.state.offers[0].progress, 0.5);
  push({ type: "receiveFinished", id: "o1", message: "ok", path: "C:\\r\\plano.pdf" });
  assert.equal(engine.state.offers[0].status, "done");
  assert.equal(engine.state.offers[0].detail, "C:\\r\\plano.pdf");
  engine.dismissOffer("o1");
  assert.equal(engine.state.offers.length, 0);
});

test("a rejected offer disappears and the engine is told", async () => {
  const { engine, push, socket } = await connected();
  push({ type: "offer", id: "o2", peerId: "p1", sender: "Luis", fileName: "a.zip", size: 1 });
  engine.decideOffer("o2", false);
  assert.equal(engine.state.offers.length, 0);
  assert.deepEqual(socket.sent.at(-1), { cmd: "offerDecision", id: "o2", accept: false });
});

test("a failed receive keeps its reason", async () => {
  const { engine, push } = await connected();
  push({ type: "offer", id: "o3", peerId: "p1", sender: "Luis", fileName: "a.zip", size: 1 });
  push({ type: "receiveFinished", id: "o3", message: "Se cortó la conexión", path: "" });
  assert.equal(engine.state.offers[0].status, "failed");
  assert.equal(engine.state.offers[0].detail, "Se cortó la conexión");
});

test("sendFile resolves with the result and tracks progress by token", async () => {
  const { engine, push, socket } = await connected();
  const result = engine.sendFile("p1", "C:\\x.bin");
  const cmd = socket.sent.at(-1);
  assert.equal(cmd.cmd, "send");
  assert.equal(cmd.peerId, "p1");
  assert.equal(engine.state.sending[cmd.token], 0);
  push({ type: "sendProgress", token: cmd.token, value: 0.4 });
  assert.equal(engine.state.sending[cmd.token], 0.4);
  push({ type: "sendDone", token: cmd.token, peerId: "p1", ok: true, message: "" });
  assert.deepEqual(await result, { ok: true, message: "" });
  assert.deepEqual(engine.state.sending, {});
});

test("sendFile without a connection fails at once", async () => {
  const engine = new LazoEngine("ws://x", () => fakeSocket(), 5);
  assert.deepEqual(await engine.sendFile("p1", "C:\\x.bin"), { ok: false, message: "El motor de Lazo no está conectado." });
  assert.deepEqual(engine.state.sending, {});
});

test("chat messages and results reach the listeners", async () => {
  const { engine, push, socket } = await connected();
  const seen = [];
  engine.on((e) => seen.push(e));
  push({ type: "chat", peerId: "p1", name: "Luis", text: "hola" });
  engine.sendChat("p1", "buenas");
  push({ type: "chatSent", peerId: "p1", ok: false, message: "sin conexión" });
  assert.deepEqual(seen.find((e) => e.type === "chat").chat, { peerId: "p1", name: "Luis", text: "hola" });
  assert.deepEqual(socket.sent.at(-1), { cmd: "chat", peerId: "p1", text: "buenas" });
  assert.deepEqual(seen.find((e) => e.type === "chatSent"), { type: "chatSent", peerId: "p1", ok: false, message: "sin conexión" });
});

test("losing the socket clears the state, fails what was in flight and reconnects", async () => {
  const sockets = [];
  const engine = new LazoEngine(async () => "ws://x", () => { const s = fakeSocket(); sockets.push(s); return s; }, 5);
  await engine.connect();
  sockets[0].onopen({});
  sockets[0].onmessage({ data: JSON.stringify(hello) });
  const pending = engine.sendFile("p1", "C:\\x.bin");
  sockets[0].onclose({});
  assert.equal(engine.state.connected, false);
  assert.equal(engine.state.self, null);
  assert.equal((await pending).ok, false);
  await new Promise((r) => setTimeout(r, 40));
  assert.equal(sockets.length, 2, "a new attempt is made");
  engine.close();
});

test("close() stops the retries", async () => {
  const sockets = [];
  const engine = new LazoEngine(async () => "ws://x", () => { const s = fakeSocket(); sockets.push(s); return s; }, 5);
  await engine.connect();
  engine.close();
  sockets[0].onclose({});
  await new Promise((r) => setTimeout(r, 30));
  assert.equal(sockets.length, 1);
});
