// Xpace in the island: the treemap, the channel to the tools program and the model of the screen.
// The analysis itself is the classic Lazo's; engine/test-tools.mjs runs it against the real program.

import { test } from "node:test";
import assert from "node:assert/strict";
import { squarify } from "../src/core/treemap.ts";
import { LazoTools } from "../src/lazo/tools.ts";
import { XpaceModel, formatBytes } from "../src/core/xpace.ts";

// ── Treemap ───────────────────────────────────────────────────────────────────

const area = (r) => r.w * r.h;

test("the rectangles tile the box: same total area, inside it, none over another", () => {
  const sizes = [600, 300, 200, 150, 90, 60, 40, 10, 5];
  const box = { x: 0, y: 0, w: 840, h: 236 };
  const cells = squarify(sizes.map((size, i) => ({ size, i })), box);
  assert.equal(cells.length, sizes.length);
  const total = cells.reduce((s, c) => s + area(c.rect), 0);
  assert.ok(Math.abs(total - box.w * box.h) < 1e-6, `area ${total}`);
  for (const { rect } of cells) {
    assert.ok(rect.x >= -1e-9 && rect.y >= -1e-9 && rect.x + rect.w <= box.w + 1e-6 && rect.y + rect.h <= box.h + 1e-6);
  }
  for (let a = 0; a < cells.length; a++) {
    for (let b = a + 1; b < cells.length; b++) {
      const p = cells[a].rect, q = cells[b].rect;
      const overlapX = Math.min(p.x + p.w, q.x + q.w) - Math.max(p.x, q.x);
      const overlapY = Math.min(p.y + p.h, q.y + q.h) - Math.max(p.y, q.y);
      assert.ok(overlapX <= 1e-6 || overlapY <= 1e-6, `${a} and ${b} overlap`);
    }
  }
});

test("each area is proportional to its size, the biggest first", () => {
  const cells = squarify([{ size: 1 }, { size: 3 }, { size: 6 }], { x: 10, y: 20, w: 100, h: 100 });
  assert.deepEqual(cells.map((c) => c.item.size), [6, 3, 1]);
  assert.ok(Math.abs(area(cells[0].rect) - 6000) < 1e-6);
  assert.ok(Math.abs(area(cells[2].rect) - 1000) < 1e-6);
  assert.ok(cells.every((c) => c.rect.x >= 10 && c.rect.y >= 20));
});

test("nothing to draw gives nothing", () => {
  assert.deepEqual(squarify([], { x: 0, y: 0, w: 10, h: 10 }), []);
  assert.deepEqual(squarify([{ size: 0 }], { x: 0, y: 0, w: 10, h: 10 }), []);
  assert.deepEqual(squarify([{ size: 5 }], { x: 0, y: 0, w: 0, h: 10 }), []);
});

test("a single item takes the whole box", () => {
  const [only] = squarify([{ size: 42 }], { x: 0, y: 0, w: 300, h: 100 });
  assert.ok(Math.abs(only.rect.w - 300) < 1e-6 && Math.abs(only.rect.h - 100) < 1e-6 && only.rect.x === 0 && only.rect.y === 0);
});

// ── The channel ───────────────────────────────────────────────────────────────

function channel() {
  const sent = [];
  let handler = null;
  let subscribed = 0;
  const tools = new LazoTools({
    send: async (text) => { sent.push(JSON.parse(text)); },
    subscribe: async (h) => { subscribed++; handler = h; return () => {}; },
  });
  return { tools, sent, say: (m) => handler(typeof m === "string" ? m : JSON.stringify(m)), subscribed: () => subscribed };
}

test("the first command subscribes once; what the program says reaches the listeners", async () => {
  const { tools, sent, say, subscribed } = channel();
  const heard = [];
  tools.on((m) => heard.push(m.type));
  await tools.post({ cmd: "xpace.scan", path: "C:\\x" });
  await tools.post({ cmd: "xpace.cancel" });
  assert.equal(subscribed(), 1);
  assert.deepEqual(sent, [{ cmd: "xpace.scan", path: "C:\\x" }, { cmd: "xpace.cancel" }]);
  say({ type: "xpace.progress", files: 3 });
  say("no es json");
  say({ nada: 1 });
  assert.deepEqual(heard, ["xpace.progress"]);
});

test("a program that cannot start rejects the command", async () => {
  const tools = new LazoTools({ send: async () => { throw new Error("Lazo tools were not found"); }, subscribe: async () => () => {} });
  await assert.rejects(tools.post({ cmd: "xpace.scan", path: "C:\\" }), /not found/);
});

// ── The model ─────────────────────────────────────────────────────────────────

function node(id, name, size, extra = {}) {
  return { id, name, path: `C:\\${name}`, size, files: 1, folders: 0, ownSize: 0, ownFiles: 0, denied: false, modified: "", ...extra };
}

function model() {
  const sent = [];
  let changes = 0;
  const m = new XpaceModel(async (c) => { sent.push(c); }, () => { changes++; });
  return { m, sent, changes: () => changes };
}

test("an analysis runs from the order to the map of its top folder", () => {
  const { m, sent } = model();
  m.scan('  "C:\\Users\\Ana"  ');
  assert.equal(m.running, true);
  assert.deepEqual(sent, [{ cmd: "xpace.scan", path: "C:\\Users\\Ana" }]);
  m.scan("C:\\otra");
  assert.equal(sent.length, 1, "nothing starts while one is running");
  m.handle({ type: "xpace.progress", files: 10, bytes: 2048, folders: 2, current: "C:\\Users\\Ana\\x" });
  assert.deepEqual(m.progress, { files: 10, bytes: 2048, folders: 2, current: "C:\\Users\\Ana\\x" });
  m.handle({ type: "xpace.done", path: "C:\\Users\\Ana", rootId: 0, files: 10, bytes: 2048, folders: 2, denied: 0, elapsedMs: 150 });
  assert.equal(m.running, false);
  assert.deepEqual(m.trail, [0]);
  assert.deepEqual(sent.at(-1), { cmd: "xpace.children", id: 0 }, "the top folder is asked for");
  m.handle({ type: "xpace.children", node: node(0, "Ana", 2048, { folders: 1 }), children: [node(1, "Docs", 2000, { folders: 1 })], more: 0, moreSize: 0 });
  assert.equal(m.current.children[0].name, "Docs");
});

test("going in asks once, and going back up returns without asking", () => {
  const { m, sent } = model();
  m.handle({ type: "xpace.done", path: "C:\\", rootId: 0, files: 1, bytes: 1, folders: 1, denied: 0, elapsedMs: 1 });
  m.handle({ type: "xpace.children", node: node(0, "C:", 10, { folders: 1 }), children: [node(1, "a", 10, { folders: 1 })], more: 0, moreSize: 0 });
  m.enter(1);
  m.enter(1);
  assert.equal(sent.filter((c) => c.cmd === "xpace.children" && c.id === 1).length, 1);
  assert.deepEqual(m.trail, [0, 1, 1]);
  m.up(0);
  assert.deepEqual(m.trail, [0]);
});

test("each tab asks for its data once, when the analysis is there", () => {
  const { m, sent } = model();
  m.setTab("large");
  assert.equal(sent.some((c) => c.cmd === "xpace.largest"), false, "nothing to ask before an analysis");
  m.handle({ type: "xpace.done", path: "C:\\", rootId: 0, files: 1, bytes: 1, folders: 1, denied: 0, elapsedMs: 1 });
  assert.equal(sent.filter((c) => c.cmd === "xpace.largest").length, 1, "the open tab asks when the analysis arrives");
  m.setTab("types");
  m.setTab("types");
  assert.equal(sent.filter((c) => c.cmd === "xpace.types").length, 1, "asked once while the answer is on its way");
  m.handle({ type: "xpace.types", types: [{ extension: ".bin", size: 5, count: 1 }] });
  m.setTab("large");
  m.setTab("types");
  assert.equal(sent.filter((c) => c.cmd === "xpace.types").length, 1, "answered, so not asked again");
});

test("an error ends the run and says why", () => {
  const { m } = model();
  m.scan("C:\\no");
  m.handle({ type: "xpace.error", message: "Esa carpeta no existe." });
  assert.equal(m.running, false);
  assert.equal(m.error, "Esa carpeta no existe.");
});

test("a failed command (the program is missing) shows its reason instead of hanging", async () => {
  const m = new XpaceModel(async () => { throw new Error("The Lazo tools were not found."); }, () => {});
  m.scan("C:\\x");
  await new Promise((r) => setTimeout(r, 0));
  assert.equal(m.running, false);
  assert.equal(m.error, "The Lazo tools were not found.");
});

test("removing something takes it from the lists and asks again for the folders on screen", () => {
  const { m, sent } = model();
  m.handle({ type: "xpace.done", path: "C:\\", rootId: 0, files: 1, bytes: 1, folders: 1, denied: 0, elapsedMs: 1 });
  m.handle({ type: "xpace.children", node: node(0, "C:", 10, { folders: 1 }), children: [node(1, "a", 10)], more: 0, moreSize: 0 });
  m.handle({ type: "xpace.tips", tips: [
    { path: "C:\\Temp", title: "Temp", why: "", safe: true, size: 5, isFolder: true, infoOnly: false, contentsOnly: true },
    { path: "C:\\Temp\\x.log", title: "x", why: "", safe: true, size: 1, isFolder: false, infoOnly: false, contentsOnly: false },
    { path: "C:\\Otra", title: "Otra", why: "", safe: false, size: 9, isFolder: true, infoOnly: false, contentsOnly: false },
  ] });
  m.handle({ type: "xpace.largest", files: [{ name: "x.log", folder: "C:\\Temp", path: "C:\\Temp\\x.log", size: 1, modified: "" }] });
  const before = sent.length;
  m.handle({ type: "xpace.removed", path: "C:\\Temp", gone: true, problem: "" });
  assert.deepEqual(m.tips.map((t) => t.path), ["C:\\Otra"], "the folder and what was inside it are gone");
  assert.deepEqual(m.largest, []);
  assert.ok(sent.slice(before).some((c) => c.cmd === "xpace.children" && c.id === 0), "the top folder is asked for again");
  m.handle({ type: "xpace.removed", path: "C:\\Otra", gone: false, problem: "No se puede quitar" });
  assert.equal(m.tips.length, 1);
  assert.equal(m.notice, "No se puede quitar");
});

test("sizes read as people read them", () => {
  assert.equal(formatBytes(0), "0 B");
  assert.equal(formatBytes(1023), "1023 B");
  assert.equal(formatBytes(1024), "1 KB");
  assert.equal(formatBytes(1536), "1.50 KB");
  assert.equal(formatBytes(15 * 1024 * 1024), "15.0 MB");
  assert.equal(formatBytes(320 * 1024 * 1024), "320 MB");
  assert.equal(formatBytes(1.5 * 1024 ** 3), "1.50 GB");
});
