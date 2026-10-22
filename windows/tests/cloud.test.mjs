// Point clouds in the island: the model of the screen. The conversion itself is the classic Lazo's;
// engine/test-tools.mjs runs it against the real program (including a real ReCap project).

import { test } from "node:test";
import assert from "node:assert/strict";
import { CloudModel, baseName, dirName, formatDuration, isCloudFile } from "../src/core/cloud.ts";

function model() {
  const sent = [];
  let changes = 0;
  const m = new CloudModel(async (c) => { sent.push(c); }, () => { changes++; });
  return { m, sent, changes: () => changes };
}
const ready = (m) => m.handle({ type: "cloud.status", deCap: "C:\\ReCap\\decap.exe", geoid: true, library: true, units: ["mm", "cm", "m"], projections: [] });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

test("only the clouds the converter takes are added, once each, and they name the result", () => {
  const { m, sent } = model();
  m.addFiles(["C:\\a\\Lote 4.LAZ", "C:\\a\\notas.txt", "c:\\a\\lote 4.laz"]);
  assert.deepEqual(m.files.map((f) => f.path), ["C:\\a\\Lote 4.LAZ"], "the case of a path does not make another file");
  assert.equal(m.name, "Lote 4");
  assert.deepEqual(sent.find((c) => c.cmd === "cloud.probe").files, ["C:\\a\\Lote 4.LAZ"]);
  m.addFiles(["C:\\a\\notas.txt"]);
  assert.equal(m.notice, "unsupported");
  const before = m.files.length;
  m.addFiles(["C:\\A\\LOTE 4.laz"]);
  assert.equal(m.files.length, before, "the same file twice is not added, whatever its case");
});

test("the system the files declare becomes the source, and the sizes arrive with it", () => {
  const { m } = model();
  m.addFiles(["C:\\a\\x.las"]);
  const detected = { ref: "lazo:3116", label: "MAGNA-SIRGAS / Colombia Bogotá", epsg: 3116, detail: "Colombia", lazo: true };
  m.handle({ type: "cloud.probe", files: [{ path: "C:\\a\\x.las", size: 2048 }], detected, declared: "MAGNA_Bogota", note: "" });
  assert.equal(m.files[0].size, 2048);
  assert.equal(m.source.ref, "lazo:3116");
  m.removeFile("C:\\a\\x.las");
  assert.equal(m.source, null);
  assert.equal(m.files.length, 0);
});

test("a conversion sends what was chosen and ends with its result", () => {
  const { m, sent } = model();
  ready(m);
  m.addFiles(["C:\\a\\x.las"]);
  m.handle({ type: "cloud.probe", files: [{ path: "C:\\a\\x.las", size: 1 }], detected: { ref: "lazo:3116", label: "B", epsg: 3116 }, declared: "", note: "" });
  m.setFormat("rcp");
  m.merge = true;
  m.setUnit(1);
  m.setDecimation("5");
  m.chooseTarget({ ref: "lazo:on-egm", label: "ON + EGM2008", epsg: 9377, lazo: true, egm2008: true });
  m.setEllipsoidal(true);
  m.convert();
  const order = sent.find((c) => c.cmd === "cloud.convert");
  assert.deepEqual(order, {
    cmd: "cloud.convert", files: ["C:\\a\\x.las"], folder: "", name: "x", format: "rcp", unit: 1, decimation: "5", merge: true,
    source: "lazo:3116", target: "lazo:on-egm", ellipsoidal: true,
  });
  assert.equal(m.running, true);
  m.convert();
  assert.equal(sent.filter((c) => c.cmd === "cloud.convert").length, 1, "nothing starts while one runs");
  m.handle({ type: "cloud.progress", value: 0.4, text: "Convirtiendo" });
  assert.deepEqual(m.progress, { value: 0.4, text: "Convirtiendo" });
  m.handle({ type: "cloud.done", ok: true, output: "C:\\a\\x.rcp", message: "", report: "C:\\a\\x - reporte.txt", elapsedMs: 4200 });
  assert.equal(m.running, false);
  assert.equal(m.result.ok, true);
  assert.equal(m.progress.value, 1);
});

test("the heights are ellipsoidal only when the output has EGM2008 and the geoid is there", () => {
  const { m, sent } = model();
  ready(m);
  m.addFiles(["C:\\a\\x.las"]);
  m.setEllipsoidal(true);
  m.chooseTarget({ ref: "lazo:on", label: "ON", epsg: 9377, lazo: true, egm2008: false });
  m.convert();
  assert.equal(sent.find((c) => c.cmd === "cloud.convert").ellipsoidal, false);
});

test("nothing converts without ReCap or without files, and a failed start shows its reason", async () => {
  const { m, sent } = model();
  m.addFiles(["C:\\a\\x.las"]);
  m.convert();
  assert.equal(sent.some((c) => c.cmd === "cloud.convert"), false, "the program has not said ReCap is there");
  const broken = new CloudModel(async () => { throw new Error("The Lazo tools were not found."); }, () => {});
  broken.status = { deCap: "x", geoid: false, library: false, units: [], projections: [] };
  broken.files = [{ path: "C:\\a\\x.las", size: 0 }];
  broken.convert();
  await wait(0);
  assert.equal(broken.running, false);
  assert.equal(broken.notice, "The Lazo tools were not found.");
});

test("a failed conversion says why and leaves the screen ready to try again", () => {
  const { m } = model();
  ready(m);
  m.files = [{ path: "C:\\a\\x.las", size: 1 }];
  m.convert();
  m.handle({ type: "cloud.done", ok: false, output: "", message: "ReCap no tiene licencia", report: "", elapsedMs: 10 });
  assert.equal(m.running, false);
  assert.equal(m.result.message, "ReCap no tiene licencia");
  assert.equal(m.progress.value, 0);
});

test("the preview is asked for once typing stops, and only the latest answer counts", async () => {
  const { m, sent } = model();
  m.files = [{ path: "C:\\a\\x.las", size: 1 }];
  m.setDecimation("1");
  m.setDecimation("12");
  m.setDecimation("125");
  await wait(400);
  const asks = sent.filter((c) => c.cmd === "cloud.preview");
  assert.equal(asks.length, 1);
  assert.equal(asks[0].mm, 125);
  m.handle({ type: "cloud.preview", seq: asks[0].seq - 1, ok: true, original: "old", reduced: "", before: 1, after: 1, windowMeters: 1 });
  assert.equal(m.preview, null, "an older answer is ignored");
  m.handle({ type: "cloud.preview", seq: asks[0].seq, ok: true, original: "a", reduced: "b", before: 900, after: 300, windowMeters: 0.4, reducedShown: true, canZone: true });
  assert.equal(m.preview.after, 300);
  m.handle({ type: "cloud.preview", seq: asks[0].seq, ok: false, message: "No se pudo mostrar la vista previa: x" });
  assert.equal(m.preview, null);
  assert.match(m.previewMessage, /No se pudo/);
});

test("choosing a system from the search sets the source or the output", () => {
  const { m, sent } = model();
  m.openPicker("source");
  assert.deepEqual(sent.at(-1), { cmd: "cloud.search", query: "", source: true });
  m.handle({ type: "cloud.search", query: "", results: [{ ref: "recap:UTM84-18N", label: "UTM 18N", epsg: 32618 }] });
  assert.equal(m.results.length, 1);
  m.choose(m.results[0]);
  assert.equal(m.source.ref, "recap:UTM84-18N");
  assert.equal(m.picker, null);
  m.openPicker("target");
  assert.equal(sent.at(-1).source, false);
  m.choose({ ref: "lazo:on", label: "ON", epsg: 9377 });
  assert.equal(m.target.ref, "lazo:on");
});

test("picked files go through the same door as dropped ones, and a folder sets where to save", () => {
  const { m } = model();
  m.handle({ type: "cloud.picked", kind: "files", paths: ["C:\\a\\x.e57", "C:\\a\\y.png"] });
  assert.deepEqual(m.files.map((f) => f.path), ["C:\\a\\x.e57"]);
  m.handle({ type: "cloud.picked", kind: "folder", paths: ["D:\\salida"] });
  assert.equal(m.folder, "D:\\salida");
  m.handle({ type: "cloud.picked", kind: "folder", paths: [] });
  assert.equal(m.folder, "D:\\salida", "a cancelled dialog changes nothing");
});

test("small helpers", () => {
  assert.equal(isCloudFile("C:\\x\\a.E57"), true);
  assert.equal(isCloudFile("C:\\x\\a.rcp"), false);
  assert.equal(baseName("C:\\x\\Lote.final.las"), "Lote.final");
  assert.equal(dirName("C:\\x\\a.las"), "C:\\x");
  assert.equal(formatDuration(4200), "4 s");
  assert.equal(formatDuration(185000), "3 min 05 s");
});
