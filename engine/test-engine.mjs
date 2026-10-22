// Pruebas del motor sin interfaz (Lazo.Engine.exe): seguridad del WebSocket local, protocolo y una
// transferencia real de archivo entre dos motores en este equipo.
//   node engine/test-engine.mjs [ruta\a\Lazo.Engine.exe]
// Sin argumento compila el motor en una carpeta temporal.
import { spawn, spawnSync } from "node:child_process";
import { mkdtempSync, readFileSync, writeFileSync, existsSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import assert from "node:assert/strict";
import dgram from "node:dgram";
import http from "node:http";
import { networkInterfaces } from "node:os";
import { test, after } from "node:test";
import { LazoEngine, engineUrl } from "../windows/src/lazo/engine.ts";

const here = dirname(fileURLToPath(import.meta.url));
const work = mkdtempSync(join(tmpdir(), "lazo-engine-test-"));
let exe = process.argv[2];
if (!exe) {
  const out = join(work, "bin");
  const r = spawnSync("pwsh", ["-NoProfile", "-File", join(here, "build.ps1"), "-OutputDirectory", out], { encoding: "utf8" });
  assert.equal(r.status, 0, r.stdout + r.stderr);
  exe = join(out, "Lazo.Engine.exe");
}
assert.ok(existsSync(exe), "no existe " + exe);

const procs = [];
function startEngine(name, ports, port) {
  const dir = join(work, name);
  const info = join(dir, "engine.json");
  const child = spawn(exe, [
    "--allow-parallel", "--ports", ports, "--port", String(port), "--profile", dir,
    "--info", info, "--receive", join(dir, "in"), "--exit-on-stdin-close",
  ], { stdio: ["pipe", "pipe", "pipe"] });
  procs.push(child);
  return new Promise((ok, fail) => {
    let out = "", err = "";
    child.stderr.on("data", (d) => (err += d));
    child.stdout.on("data", (d) => {
      out += d;
      if (out.includes("READY")) ok({ child, dir, port, token: JSON.parse(readFileSync(info, "utf8")).token });
    });
    child.on("exit", (c) => fail(new Error(`el motor ${name} terminó (${c}): ${err}`)));
    setTimeout(() => fail(new Error(`el motor ${name} no arrancó: ${out} ${err}`)), 15000);
  });
}

/** Cliente de pruebas: guarda todos los mensajes y deja esperar uno por condición. */
function connect(engine, { token = engine.token, origin } = {}) {
  return new Promise((ok, fail) => {
    const ws = new WebSocket(`ws://127.0.0.1:${engine.port}/ws?t=${token}`, origin ? { headers: { Origin: origin } } : undefined);
    const log = [];
    const waiters = [];
    ws.onmessage = (e) => {
      const m = JSON.parse(e.data);
      log.push(m);
      for (const w of [...waiters]) if (w.test(m)) { waiters.splice(waiters.indexOf(w), 1); w.ok(m); }
    };
    const client = {
      ws, log,
      send: (m) => ws.send(JSON.stringify(m)),
      next: (test, ms = 20000) => new Promise((res, rej) => {
        const found = log.find(test);
        if (found) return res(found);
        const w = { test, ok: res };
        waiters.push(w);
        setTimeout(() => rej(new Error("tiempo agotado esperando un mensaje; recibidos: " + JSON.stringify(log.map((l) => l.type)))), ms).unref();
      }),
    };
    ws.onopen = () => ok(client);
    ws.onerror = () => fail(new Error("conexión rechazada"));
  });
}

after(() => {
  for (const p of procs) { try { p.stdin.end(); p.kill(); } catch {} }
  setTimeout(() => { try { rmSync(work, { recursive: true, force: true }); } catch {} }, 500).unref();
});

const A = await startEngine("a", "49351,49352", 49360);
const B = await startEngine("b", "49361,49353", 49361);

/** Pide el WebSocket a mano para poder fijar el Origin (el cliente integrado no deja). */
function upgradeStatus(engine, origin) {
  return new Promise((ok, fail) => {
    const req = http.request({ host: "127.0.0.1", port: engine.port, path: `/ws?t=${engine.token}`, headers: {
      Connection: "Upgrade", Upgrade: "websocket", "Sec-WebSocket-Version": "13", "Sec-WebSocket-Key": "dGhlIHNhbXBsZSBub25jZQ==", ...(origin ? { Origin: origin } : {}),
    } });
    req.on("response", (res) => { ok(res.statusCode); res.resume(); });
    req.on("upgrade", (res, socket) => { ok(res.statusCode); socket.destroy(); });
    req.on("error", fail);
    req.end();
  });
}

test("sin token o con un origen ajeno la conexión se rechaza", async () => {
  assert.equal(await upgradeStatus(A, "http://evil.example"), 403);
  assert.equal(await upgradeStatus(A, "tauri://localhost"), 101);
  assert.equal(await upgradeStatus(A, undefined), 101);
  await assert.rejects(connect(A, { token: "x".repeat(48) }), /rechazada/);
  await assert.rejects(connect(A, { token: "" }), /rechazada/);
  const info = await fetch(`http://127.0.0.1:${A.port}/`).then((r) => r.status);
  assert.equal(info, 404);
});

test("con token: saludo, lista de personas, ping y orden desconocida", async () => {
  const c = await connect(A);
  const hello = await c.next((m) => m.type === "hello");
  assert.equal(hello.version, 1);
  assert.match(hello.selfId, /^[0-9a-f-]{36}$/);
  assert.ok(hello.fingerprint.length > 8);
  await c.next((m) => m.type === "peers");
  c.send({ cmd: "ping" });
  await c.next((m) => m.type === "pong");
  c.send({ cmd: "nada" });
  assert.match((await c.next((m) => m.type === "error")).message, /desconocida/);
  c.ws.close();
});

test("enviar a alguien que no existe o un archivo que no existe falla con un mensaje", async () => {
  const c = await connect(A);
  c.send({ cmd: "send", token: "t1", peerId: "00000000-0000-0000-0000-000000000000", path: "C:\\no-existe.txt" });
  const done = await c.next((m) => m.type === "sendDone" && m.token === "t1");
  assert.equal(done.ok, false);
  assert.match(done.message, /ya no está disponible/);
  c.ws.close();
});

/** Un anuncio LAZO3 unicast, como lo emitiría el otro motor por difusión (la difusión no se prueba entre dos copias en un mismo equipo). */
async function announce(to, helloFrom, transferPort) {
  const local = localAddress();
  const socket = dgram.createSocket({ type: "udp4" });
  await new Promise((ok) => socket.bind(0, local, ok));
  const packet = Buffer.from(`LAZO3|${helloFrom.selfId}|${helloFrom.name}|${transferPort}|${helloFrom.fingerprint}`);
  const send = () => socket.send(packet, to, local);
  send();
  const again = setInterval(send, 1000);
  setTimeout(() => { clearInterval(again); socket.close(); }, 25000).unref();
}
function localAddress() {
  const found = Object.values(networkInterfaces()).flat().find((i) => i.family === "IPv4" && !i.internal && /^(10.|192.168.|172.(1[6-9]|2d|3[01]).)/.test(i.address));
  assert.ok(found, "este equipo no tiene una dirección IPv4 privada");
  return found.address;
}

test("dos motores se ven, intercambian chat y transfieren un archivo cifrado", async () => {
  const ca = await connect(A);
  const cb = await connect(B);
  const helloA = await ca.next((m) => m.type === "hello");
  const helloB = await cb.next((m) => m.type === "hello");
  assert.notEqual(helloA.selfId, helloB.selfId);

  // A descubre a B por un anuncio LAZO3 (el puerto anunciado debe ser el de transferencia de A);
  // B registra a A a mano porque su puerto real no es el que exige el anuncio.
  await announce(49351, helloB, 49352);
  await ca.next((m) => m.type === "peers" && m.peers.some((p) => p.id === helloB.selfId), 30000);
  cb.send({ cmd: "addPeer", id: helloA.selfId, name: helloA.name, address: localAddress(), port: 49352, fingerprint: helloA.fingerprint });
  await cb.next((m) => m.type === "peerAdded");

  // Chat de B hacia A.
  cb.send({ cmd: "chat", peerId: helloA.selfId, text: "hola desde la isla" });
  const chat = await ca.next((m) => m.type === "chat" && m.text === "hola desde la isla");
  assert.equal(chat.peerId, helloB.selfId);

  // Archivo de B hacia A: A decide aceptar.
  const payload = Buffer.alloc(3 * 1024 * 1024 + 123);
  for (let i = 0; i < payload.length; i++) payload[i] = (i * 31 + 7) & 255;
  const source = join(work, "muestra.bin");
  writeFileSync(source, payload);
  cb.send({ cmd: "send", token: "f1", peerId: helloA.selfId, path: source });
  const offer = await ca.next((m) => m.type === "offer");
  assert.equal(offer.fileName, "muestra.bin");
  assert.equal(offer.size, payload.length);
  assert.equal(offer.peerId, helloB.selfId);
  ca.send({ cmd: "offerDecision", id: offer.id, accept: true });
  const finished = await ca.next((m) => m.type === "receiveFinished" && m.id === offer.id);
  const sent = await cb.next((m) => m.type === "sendDone" && m.token === "f1");
  assert.equal(sent.ok, true, sent.message);
  assert.ok(existsSync(finished.path), "no se guardó " + finished.path);
  assert.ok(readFileSync(finished.path).equals(payload), "el archivo recibido no es idéntico");

  // Rechazo: no se guarda nada y quien envía lo sabe.
  cb.send({ cmd: "send", token: "f2", peerId: helloA.selfId, path: source });
  const second = await ca.next((m) => m.type === "offer" && m.id !== offer.id);
  ca.send({ cmd: "offerDecision", id: second.id, accept: false });
  const refused = await cb.next((m) => m.type === "sendDone" && m.token === "f2");
  assert.equal(refused.ok, false);
  ca.ws.close(); cb.ws.close();
});

test("el cliente TypeScript de la isla habla con el motor real", async () => {
  const client = new LazoEngine(engineUrl(A.port, A.token), (url) => new WebSocket(url), 100);
  await client.connect();
  const until = (cond) => new Promise((ok, fail) => {
    const stop = setTimeout(() => fail(new Error("tiempo agotado; estado: " + JSON.stringify(client.state))), 10000);
    stop.unref();
    const check = () => { if (cond()) { clearTimeout(stop); ok(); } };
    client.on(check); check();
  });
  await until(() => client.state.connected && client.state.self !== null);
  assert.match(client.state.self.id, /^[0-9a-f-]{36}$/);
  assert.equal(client.state.self.receiveDir, join(A.dir, "in"));
  const result = await client.sendFile("00000000-0000-0000-0000-000000000000", "C:\no-existe.bin");
  assert.equal(result.ok, false);
  assert.deepEqual(client.state.sending, {});
  client.close();
});
