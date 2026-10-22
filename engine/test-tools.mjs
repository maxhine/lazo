// Las utilidades del Lazo clásico servidas a la isla por Lazo.Engine.exe --tools-only (sin red):
// Xpace analiza una carpeta de prueba y entrega el árbol, los archivos grandes y los tipos, y quita un archivo.
//   node engine/test-tools.mjs [ruta\a\Lazo.Engine.exe]
import { spawn, spawnSync } from "node:child_process";
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, existsSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import assert from "node:assert/strict";
import { test, after } from "node:test";

const here = dirname(fileURLToPath(import.meta.url));
const work = mkdtempSync(join(tmpdir(), "lazo-tools-test-"));
let exe = process.argv[2];
if (!exe) {
  const out = join(work, "bin");
  const r = spawnSync("pwsh", ["-NoProfile", "-File", join(here, "build.ps1"), "-OutputDirectory", out], { encoding: "utf8" });
  assert.equal(r.status, 0, r.stdout + r.stderr);
  exe = join(out, "Lazo.Engine.exe");
}

const info = join(work, "engine.json");
const child = spawn(exe, ["--tools-only", "--port", "49390", "--info", info, "--exit-on-stdin-close"], { stdio: ["pipe", "pipe", "pipe"] });
const ready = new Promise((ok, fail) => {
  let out = "";
  child.stdout.on("data", (d) => { out += d; if (out.includes("READY")) ok(); });
  child.on("exit", (c) => fail(new Error("el motor terminó: " + c)));
  setTimeout(() => fail(new Error("el motor no arrancó")), 15000);
});
await ready;
const token = JSON.parse(readFileSync(info, "utf8")).token;
const ws = new WebSocket(`ws://127.0.0.1:49390/ws?t=${token}`);
const log = [];
const waiters = [];
ws.onmessage = (e) => {
  const m = JSON.parse(e.data);
  log.push(m);
  for (const w of [...waiters]) if (w.test(m)) { waiters.splice(waiters.indexOf(w), 1); w.ok(m); }
};
await new Promise((ok, fail) => { ws.onopen = ok; ws.onerror = () => fail(new Error("sin conexión")); });
const send = (m) => ws.send(JSON.stringify(m));
const next = (test, ms = 20000) => new Promise((res, rej) => {
  const found = log.find(test);
  if (found) return res(found);
  waiters.push({ test, ok: res });
  setTimeout(() => rej(new Error("tiempo agotado; recibidos: " + JSON.stringify(log.map((l) => l.type)))), ms).unref();
});

after(() => {
  try { child.stdin.end(); child.kill(); } catch {}
  setTimeout(() => { try { rmSync(work, { recursive: true, force: true }); } catch {} }, 500).unref();
});

// Una carpeta con tamaños conocidos.
const root = join(work, "datos");
mkdirSync(join(root, "sub", "hondo"), { recursive: true });
writeFileSync(join(root, "a.txt"), Buffer.alloc(1000));
writeFileSync(join(root, "b.TXT"), Buffer.alloc(2000));
writeFileSync(join(root, "sub", "c.bin"), Buffer.alloc(5000));
writeFileSync(join(root, "sub", "hondo", "d.bin"), Buffer.alloc(100));

test("sin red: el motor de herramientas no ofrece órdenes de red", async () => {
  const hello = await next((m) => m.type === "hello");
  assert.equal(hello.tools, true);
  send({ cmd: "chat", peerId: "x", text: "hola" });
  assert.match((await next((m) => m.type === "error")).message, /red no está/);
});

test("Xpace analiza una carpeta y entrega árbol, archivos grandes y tipos", async () => {
  send({ cmd: "xpace.scan", path: root });
  const done = await next((m) => m.type === "xpace.done");
  assert.equal(done.files, 4);
  assert.equal(done.bytes, 8100);
  assert.equal(done.folders >= 2, true);

  send({ cmd: "xpace.children", id: done.rootId });
  const top = await next((m) => m.type === "xpace.children" && m.node.id === done.rootId);
  assert.equal(top.node.size, 8100);
  assert.deepEqual(top.children.map((c) => c.name), ["sub"], "la carpeta más grande, con sus subcarpetas");
  assert.equal(top.children[0].size, 5100);
  assert.equal(top.node.ownSize, 3000);

  send({ cmd: "xpace.children", id: top.children[0].id });
  const sub = await next((m) => m.type === "xpace.children" && m.node.id === top.children[0].id);
  assert.deepEqual(sub.children.map((c) => c.name), ["hondo"]);
  assert.equal(sub.node.path, join(root, "sub"));

  send({ cmd: "xpace.largest", limit: 3 });
  const largest = await next((m) => m.type === "xpace.largest");
  assert.deepEqual(largest.files.map((f) => [f.name, f.size]), [["c.bin", 5000], ["b.TXT", 2000], ["a.txt", 1000]]);
  assert.equal(largest.files[0].path, join(root, "sub", "c.bin"));

  send({ cmd: "xpace.types" });
  const types = await next((m) => m.type === "xpace.types");
  assert.deepEqual(types.types.map((t) => [t.extension.toLowerCase(), t.size, t.count]), [[".bin", 5100, 2], [".txt", 3000, 2]]);
});

test("una carpeta que no existe y un id viejo se dicen con un mensaje", async () => {
  send({ cmd: "xpace.scan", path: join(work, "no-existe") });
  assert.match((await next((m) => m.type === "xpace.error" && /no existe/.test(m.message))).message, /no existe/);
  send({ cmd: "xpace.children", id: 9999 });
  await next((m) => m.type === "xpace.error" && /ya no está/.test(m.message));
});

test("quitar un archivo lo borra y deja el análisis al día", async () => {
  const victim = join(root, "a.txt");
  assert.ok(existsSync(victim));
  send({ cmd: "xpace.remove", path: victim, isFolder: false, permanent: true, contentsOnly: false });
  const removed = await next((m) => m.type === "xpace.removed" && m.path === victim);
  assert.equal(removed.gone, true, removed.problem);
  assert.ok(!existsSync(victim));
  send({ cmd: "xpace.children", id: 0 });
  const top = await next((m) => m.type === "xpace.children" && m.node.size === 7100);
  assert.equal(top.node.ownSize, 2000);
});

test("las sugerencias de limpieza llegan con lo que Xpace sabe quitar", async () => {
  send({ cmd: "xpace.tips" });
  const tips = await next((m) => m.type === "xpace.tips", 60000);
  assert.ok(Array.isArray(tips.tips));
  for (const t of tips.tips) {
    assert.equal(typeof t.path, "string");
    assert.equal(typeof t.size, "number");
  }
});
