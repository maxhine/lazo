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

// ── Nubes de puntos ───────────────────────────────────────────────────────────

// Un LAS 1.2 (formato 2, 26 bytes por punto) con una rejilla de n×n puntos cada 1 cm desde (x0, y0).
function writeLas(path, n, x0, y0) {
  const scale = 0.001;
  const header = Buffer.alloc(227);
  header.write("LASF", 0, "ascii");
  header[24] = 1; header[25] = 2;
  header.write("Lazo", 26, "ascii"); header.write("Lazo prueba", 58, "ascii");
  header.writeUInt16LE(1, 90); header.writeUInt16LE(2026, 92); header.writeUInt16LE(227, 94);
  header.writeUInt32LE(227, 96); header.writeUInt32LE(0, 100);
  header[104] = 2; header.writeUInt16LE(26, 105); header.writeUInt32LE(n * n, 107); header.writeUInt32LE(n * n, 111);
  [scale, scale, scale, 0, 0, 0].forEach((v, i) => header.writeDoubleLE(v, 131 + i * 8));
  const max = [x0 + n * 0.01, y0 + n * 0.01, 1], min = [x0, y0, -1];
  [max[0], min[0], max[1], min[1], max[2], min[2]].forEach((v, i) => header.writeDoubleLE(v, 179 + i * 8));
  const points = Buffer.alloc(n * n * 26);
  let at = 0;
  for (let i = 0; i < n; i++) for (let j = 0; j < n; j++) {
    const x = x0 + i * 0.01, y = y0 + j * 0.01, z = 0.5 * Math.sin(i / 10) * Math.cos(j / 10);
    points.writeInt32LE(Math.round(x / scale), at); points.writeInt32LE(Math.round(y / scale), at + 4); points.writeInt32LE(Math.round(z / scale), at + 8);
    points.writeUInt16LE(1000, at + 12); points[at + 14] = 0x11;
    points.writeUInt16LE(i * 257 % 65535, at + 20); points.writeUInt16LE(j * 257 % 65535, at + 22); points.writeUInt16LE(30000, at + 24);
    at += 26;
  }
  writeFileSync(path, Buffer.concat([header, points]));
}

const clouds = join(work, "nubes");
mkdirSync(clouds, { recursive: true });
const lasPath = join(clouds, "rejilla.las");
writeLas(lasPath, 100, 1000000, 1000000);

test("nubes: el estado dice qué hay y la lectura del archivo da su tamaño", async () => {
  send({ cmd: "cloud.status" });
  const st = await next((m) => m.type === "cloud.status");
  assert.deepEqual(st.units, ["mm", "cm", "m", "in", "ft", "ft EE. UU."]);
  assert.ok(st.projections.some((p) => p.ref === "lazo:on"));
  send({ cmd: "cloud.probe", files: [lasPath, join(clouds, "nada.txt")] });
  const probe = await next((m) => m.type === "cloud.probe");
  assert.equal(probe.files.length, 1, "solo entran los formatos que se convierten");
  assert.equal(probe.files[0].size, 227 + 100 * 100 * 26);
});

test("nubes: la búsqueda de sistemas mezcla los de Lazo y los de ReCap", async () => {
  send({ cmd: "cloud.search", query: "3116", source: true });
  const found = await next((m) => m.type === "cloud.search" && m.query === "3116");
  assert.ok(found.results.some((r) => r.ref === "lazo:3116"));
  send({ cmd: "cloud.search", query: "", source: false });
  const all = await next((m) => m.type === "cloud.search" && m.query === "");
  assert.ok(all.results.length > 0 && all.results.every((r) => r.ref.startsWith("lazo:")));
});

test("nubes: la vista previa entrega las dos imágenes y los conteos", async () => {
  send({ cmd: "cloud.preview", seq: 1, file: lasPath, zone: 0.5, mm: 50, unit: 2, elevation: false });
  const p = await next((m) => m.type === "cloud.preview" && m.seq === 1);
  assert.equal(p.ok, true, p.message);
  assert.ok(p.original.length > 100 && p.reduced.length > 100);
  assert.ok(p.after > 0 && p.after < p.before, `${p.before} → ${p.after}`);
  assert.equal(Buffer.from(p.original, "base64").subarray(1, 4).toString(), "PNG");
});

test("nubes: LAS reproyectado por Lazo (Bogotá → Origen Nacional) con reducción", async () => {
  send({ cmd: "cloud.convert", files: [lasPath], folder: clouds, name: "salida", format: "las", unit: 2, decimation: "20",
    source: "lazo:3116", target: "lazo:on", ellipsoidal: false, merge: false });
  const done = await next((m) => m.type === "cloud.done", 60000);
  assert.equal(done.ok, true, done.message);
  assert.ok(existsSync(done.output), done.output);
  const out = readFileSync(done.output);
  assert.equal(out.subarray(0, 4).toString(), "LASF");
  assert.ok(out.readUInt32LE(107) > 0 && out.readUInt32LE(107) < 10000, "menos puntos que los 10000 de entrada");
});

test("nubes: una combinación imposible se explica y no arranca", async () => {
  log.length = 0;
  send({ cmd: "cloud.convert", files: [lasPath], folder: clouds, name: "x", format: "las", unit: 2, decimation: "", source: "", target: "lazo:on" });
  const done = await next((m) => m.type === "cloud.done");
  assert.equal(done.ok, false);
  assert.match(done.message, /en qué sistema está la nube/);
  log.length = 0;
  send({ cmd: "cloud.convert", files: [lasPath], folder: clouds, name: "x", format: "las", unit: 2, decimation: "abc" });
  assert.match((await next((m) => m.type === "cloud.done")).message, /milímetros/);
});

test("nubes: con ReCap instalado, un proyecto .rcp de verdad", async (t) => {
  send({ cmd: "cloud.status" });
  const st = await next((m) => m.type === "cloud.status" && m.deCap !== undefined && log.filter((x) => x.type === "cloud.status").length >= 1);
  const status = [...log].reverse().find((m) => m.type === "cloud.status");
  if (!status.deCap) return t.skip("ReCap no está instalado");
  log.length = 0;
  send({ cmd: "cloud.convert", files: [lasPath], folder: clouds, name: "proyecto", format: "rcp", unit: 2, decimation: "", merge: false });
  const done = await next((m) => m.type === "cloud.done", 240000);
  assert.equal(done.ok, true, done.message);
  assert.ok(existsSync(done.output), done.output);
  assert.ok(log.some((m) => m.type === "cloud.progress"), "hubo avances");
});

// ── Descanso Visual ───────────────────────────────────────────────────────────
// Usa el eyecare.txt de verdad: cada ajuste se cambia y se devuelve a como estaba. No se prueba «trigger»
// porque abre la ventana de aviso (y su sonido) en la pantalla de quien corre las pruebas.

test("descanso visual: el estado llega y los ajustes cambian y se devuelven", async () => {
  const seen = () => [...log].reverse().find((m) => m.type === "eyecare.state");
  send({ cmd: "eyecare.state" });
  await next((m) => m.type === "eyecare.state");
  const first = seen();
  assert.equal(first.microEvery, 1200);
  assert.equal(first.activeEvery, 3300);
  assert.equal(first.microFor, 20);
  assert.equal(first.activeFor, 300);
  assert.ok(first.microLeft >= 0 && first.microLeft <= 1200);
  assert.ok(["digital", "analog"].includes(first.clock) && ["notice", "eyes"].includes(first.rest));

  const flip = async (cmd, value, read) => {
    log.length = 0;
    send({ cmd, value });
    await next((m) => m.type === "eyecare.state" && read(m) === value);
  };
  const other = first.clock === "analog" ? "digital" : "analog";
  await flip("eyecare.clock", other, (m) => m.clock);
  await flip("eyecare.clock", first.clock, (m) => m.clock);
  const rest = first.rest === "eyes" ? "notice" : "eyes";
  await flip("eyecare.rest", rest, (m) => m.rest);
  await flip("eyecare.rest", first.rest, (m) => m.rest);
  await flip("eyecare.sound", !first.sound, (m) => m.sound);
  await flip("eyecare.sound", first.sound, (m) => m.sound);
  await flip("eyecare.pause", !first.paused, (m) => m.paused);
  await flip("eyecare.pause", first.paused, (m) => m.paused);
});

test("descanso visual: el reloj del servicio avanza y lo empuja a la isla cada segundo", async () => {
  log.length = 0;
  await next((m) => m.type === "eyecare.state", 5000);
  const a = log.filter((m) => m.type === "eyecare.state").length;
  await new Promise((r) => setTimeout(r, 2300));
  assert.ok(log.filter((m) => m.type === "eyecare.state").length >= a + 1, "llegan estados sin pedirlos");
});

// ── TPS a RINEX ───────────────────────────────────────────────────────────────
// Con un TPS2RIN de mentiras (fake-tps2rin.cs: misma línea de órdenes y mismos nombres de salida que el de Topcon): lo que se
// prueba es todo lo de Lazo (leer los tiempos del RINEX, huecos, nombres de salida, no pisar archivos). El programa de Topcon
// no se incluye en las pruebas; con un .tps de verdad hay que probarlo a mano.

const tpsDir = join(work, "tps");
mkdirSync(tpsDir, { recursive: true });
const appData = process.env.APPDATA;
const tpsConfig = join(appData, "Lazo", "tps2rin.txt");
const tpsConfigBefore = existsSync(tpsConfig) ? readFileSync(tpsConfig, "utf8") : null;
const fakeExe = join(tpsDir, "tps2rin.exe");
{
  const csc = "C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe";
  const r = spawnSync(csc, ["/nologo", "/target:exe", `/out:${fakeExe}`, join(here, "fake-tps2rin.cs")], { encoding: "utf8" });
  assert.equal(r.status, 0, r.stdout + r.stderr);
}
after(() => {
  try {
    if (tpsConfigBefore === null) rmSync(tpsConfig, { force: true });
    else writeFileSync(tpsConfig, tpsConfigBefore);
  } catch {}
});
writeFileSync(join(tpsDir, "log1021v.tps"), Buffer.alloc(64, 1));
writeFileSync(join(tpsDir, "vacio.tps"), Buffer.alloc(0));

test("tps: se elige el programa de Topcon y se recuerda", async () => {
  log.length = 0;
  send({ cmd: "tps.setExe", path: join(tpsDir, "no-esta.exe") });
  assert.match((await next((m) => m.type === "tps.status" && m.problem)).problem, /No existe/);
  log.length = 0;
  send({ cmd: "tps.setExe", path: fakeExe });
  const ok = await next((m) => m.type === "tps.status" && m.exe);
  assert.equal(ok.exe, fakeExe);
  assert.equal(ok.problem, "");
});

test("tps: se ven los tiempos observados, el intervalo y los huecos", async () => {
  log.length = 0;
  send({ cmd: "tps.probe", files: [join(tpsDir, "log1021v.tps"), join(tpsDir, "notas.txt")] });
  const file = await next((m) => m.type === "tps.file");
  await next((m) => m.type === "tps.probed");
  assert.equal(log.filter((m) => m.type === "tps.file").length, 1, "solo los .tps");
  assert.equal(file.ok, true, file.message);
  assert.equal(file.baseName, "log1021v");
  assert.deepEqual(file.obs.first, "2026-10-21T21:02:25");
  assert.deepEqual(file.obs.last, "2026-10-21T21:02:34");
  assert.equal(file.obs.interval, 1);
  assert.equal(file.obs.epochs, 7);
  assert.equal(file.obs.expected, 10);
  assert.equal(file.obs.gaps, 1);
  assert.equal(file.obs.missing, 3);
  assert.equal(file.obs.leap, 18);
  assert.equal(file.obs.marker, "log1021v_M490");
  assert.match(file.obs.receiver, /TPS BX10/);
  assert.equal(file.obs.systems, "GR");
  assert.deepEqual(file.outputs.map((o) => o.ext).sort(), [".26G", ".26N", ".26O"]);
});

test("tps: un archivo que no se puede convertir dice por qué", async () => {
  log.length = 0;
  send({ cmd: "tps.probe", files: [join(tpsDir, "vacio.tps")] });
  const file = await next((m) => m.type === "tps.file");
  assert.equal(file.ok, false);
  assert.match(file.message, /Empty TPS file/);
});

test("tps: se convierte con el nombre elegido, sin pisar lo que ya hay", async () => {
  const out = join(tpsDir, "salida");
  log.length = 0;
  send({ cmd: "tps.convert", folder: out, items: [{ file: join(tpsDir, "log1021v.tps"), name: "Lote 4: base/A" }] });
  const done = await next((m) => m.type === "tps.done");
  assert.equal(done.ok, true, done.message);
  assert.deepEqual(done.written.map((p) => p.slice(out.length + 1)).sort(), ["Lote 4 baseA.26G", "Lote 4 baseA.26N", "Lote 4 baseA.26O"]);
  assert.match(readFileSync(join(out, "Lote 4 baseA.26O"), "utf8"), /MARKER NAME/);
  log.length = 0;
  send({ cmd: "tps.convert", folder: out, items: [{ file: join(tpsDir, "log1021v.tps"), name: "Lote 4 baseA" }] });
  const again = await next((m) => m.type === "tps.done");
  assert.deepEqual(again.written.map((p) => p.slice(out.length + 1)).sort(), ["Lote 4 baseA (2).26G", "Lote 4 baseA (2).26N", "Lote 4 baseA (2).26O"]);
  log.length = 0;
  send({ cmd: "tps.convert", folder: out, items: [{ file: join(tpsDir, "log1021v.tps"), name: "" }] });
  const plain = await next((m) => m.type === "tps.done");
  assert.ok(plain.written[0].endsWith("log1021v.26G") || plain.written.some((p) => p.endsWith("log1021v.26O")), "sin nombre: el del .tps");
});

test("descanso visual en el notch: el aviso va a la isla en vez de abrir una ventana", async () => {
  send({ cmd: "eyecare.state" });
  await next((m) => m.type === "eyecare.state");
  const before = [...log].reverse().find((m) => m.type === "eyecare.state");
  log.length = 0;
  send({ cmd: "eyecare.rest", value: "notch" });
  await next((m) => m.type === "eyecare.state" && m.rest === "notch");
  log.length = 0;
  send({ cmd: "eyecare.trigger", value: "micro" });
  const micro = await next((m) => m.type === "eyecare.alert");
  assert.equal(micro.kind, "micro");
  assert.equal(micro.seconds, 20);
  assert.equal(typeof micro.sound, "boolean");
  log.length = 0;
  send({ cmd: "eyecare.trigger", value: "active" });
  const active = await next((m) => m.type === "eyecare.alert");
  assert.equal(active.kind, "active");
  assert.equal(active.seconds, 300);
  // Posponer: ese aviso se vuelve a armar a 5 minutos.
  log.length = 0;
  send({ cmd: "eyecare.snooze", kind: "micro" });
  await next((m) => m.type === "eyecare.state" && m.microLeft > 200 && m.microLeft <= 300);
  // Se deja el estilo como estaba y los avisos con su cuenta normal.
  send({ cmd: "eyecare.rest", value: before.rest });
  await next((m) => m.type === "eyecare.state" && m.rest === before.rest);
});
