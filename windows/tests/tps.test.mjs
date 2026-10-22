// TPS to RINEX in the island: the model of the screen and the time helpers. The conversion is Topcon's tps2rin.exe;
// engine/test-tools.mjs runs the whole path against the real program (with a stand-in for tps2rin.exe).

import { test } from "node:test";
import assert from "node:assert/strict";
import { TpsModel, baseName, between, clock, isTpsFile, span } from "../src/core/tps.ts";

function model() {
  const sent = [];
  const m = new TpsModel(async (c) => { sent.push(c); }, () => {});
  return { m, sent };
}
const obs = { hasTimes: true, first: "2026-10-21T21:02:25", last: "2026-10-21T21:18:35", leap: 18, interval: 1, epochs: 970, expected: 971, gaps: 1, missing: 1, marker: "M", receiver: "R", antenna: "A", satellites: 12, systems: "GR" };

test("only .tps files are added, once each, and they are read by the program", () => {
  const { m, sent } = model();
  m.exe = "C:\\t\\tps2rin.exe";
  m.addFiles(["C:\\a\\log1021v.tps", "C:\\a\\notas.txt", "c:\\a\\LOG1021V.TPS"]);
  assert.deepEqual(m.files.map((f) => f.path), ["C:\\a\\log1021v.tps"]);
  assert.deepEqual(sent.at(-1), { cmd: "tps.probe", files: ["C:\\a\\log1021v.tps"] });
  m.addFiles(["C:\\a\\notas.txt"]);
  assert.equal(m.notice, "unsupported");
});

test("what the program reads fills the file: times, gaps and the files it makes", () => {
  const { m } = model();
  m.exe = "x";
  m.addFiles(["C:\\a\\log1021v.tps"]);
  m.handle({ type: "tps.file", path: "C:\\a\\log1021v.tps", ok: true, message: "", obs, outputs: [{ ext: ".26O" }, { ext: ".26N" }] });
  const f = m.files[0];
  assert.equal(f.state, "ok");
  assert.equal(f.obs.first, "2026-10-21T21:02:25");
  assert.deepEqual(f.outputs, [".26O", ".26N"]);
  m.handle({ type: "tps.probed" });
  assert.equal(m.running, false);
  assert.equal(m.ready, true);
});

test("a file that fails says why and is not converted", () => {
  const { m, sent } = model();
  m.exe = "x";
  m.addFiles(["C:\\a\\malo.tps", "C:\\a\\bueno.tps"]);
  m.handle({ type: "tps.file", path: "C:\\a\\malo.tps", ok: false, message: "TPS2RIN no generó archivos (código 3).", outputs: [] });
  m.handle({ type: "tps.file", path: "C:\\a\\bueno.tps", ok: true, message: "", obs, outputs: [] });
  assert.equal(m.files[0].state, "bad");
  m.setName("C:\\a\\bueno.tps", "  Lote 4  ");
  m.folder = "D:\\salida";
  m.convert();
  assert.deepEqual(sent.at(-1), { cmd: "tps.convert", folder: "D:\\salida", items: [{ file: "C:\\a\\bueno.tps", name: "Lote 4" }] });
  m.handle({ type: "tps.done", ok: true, message: "", written: ["D:\\salida\\Lote 4.26O"] });
  assert.equal(m.result, "ok");
  assert.equal(m.converting, false);
});

test("without tps2rin.exe nothing is read, and choosing it reads what was waiting", () => {
  const { m, sent } = model();
  m.handle({ type: "tps.status", exe: null });
  assert.equal(m.exe, null);
  m.addFiles(["C:\\a\\x.tps"]);
  assert.equal(sent.some((c) => c.cmd === "tps.probe"), false);
  m.handle({ type: "tps.picked", kind: "exe", paths: ["C:\\t\\tps2rin.exe"] });
  assert.deepEqual(sent.at(-1), { cmd: "tps.setExe", path: "C:\\t\\tps2rin.exe" });
  m.handle({ type: "tps.status", exe: "C:\\t\\tps2rin.exe", problem: "" });
  assert.deepEqual(sent.at(-1), { cmd: "tps.probe", files: ["C:\\a\\x.tps"] });
});

test("a failed conversion shows its reason", () => {
  const { m } = model();
  m.exe = "x";
  m.files = [{ path: "C:\\a\\x.tps", name: "", state: "ok", message: "", obs, outputs: [] }];
  m.convert();
  m.handle({ type: "tps.done", ok: false, message: "Access denied", written: [] });
  assert.equal(m.result, "Access denied");
});

test("times read as people read them", () => {
  assert.deepEqual(clock("2026-10-21T21:02:25"), { date: "2026-10-21", time: "21:02:25" });
  assert.deepEqual(clock("2026-10-21T21:02:25", -18), { date: "2026-10-21", time: "21:02:07" });
  assert.deepEqual(clock("2026-10-21T00:00:10", -18), { date: "2026-10-20", time: "23:59:52" }, "UTC can be the day before");
  assert.equal(between("2026-10-21T21:02:25", "2026-10-21T21:18:35"), 970);
  assert.equal(span(970), "16 min 10 s");
  assert.equal(span(45), "45 s");
  assert.equal(span(3725), "1 h 02 min");
  assert.equal(baseName("C:\\a\\log1021v.tps"), "log1021v");
  assert.equal(isTpsFile("X.TPS"), true);
  assert.equal(isTpsFile("x.jps"), false);
});
