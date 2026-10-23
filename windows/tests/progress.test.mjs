// What the notch shows while something is in progress: which one, and how far along.
import { test } from "node:test";
import assert from "node:assert/strict";
import { activeProgress, progressText } from "../src/core/progress.ts";

const idle = () => ({ cloud: { running: false, progress: { value: 0 } }, tps: { converting: false }, xpace: { running: false }, receiving: [], sending: [] });

test("nothing in progress, nothing to show", () => {
  assert.equal(activeProgress(idle()), null);
});

test("a point cloud conversion shows how far along it is", () => {
  const p = activeProgress({ ...idle(), cloud: { running: true, progress: { value: 0.424 } } });
  assert.equal(p.value, 0.424);
  assert.equal(p.color, "#38BDF8");
  assert.equal(progressText(p), "Point clouds 42 %");
});

test("what cannot say how far along it is has no value, only a label", () => {
  const tps = activeProgress({ ...idle(), tps: { converting: true } });
  assert.equal(tps.value, null);
  assert.equal(progressText(tps), "TPS to RINEX");
  assert.equal(activeProgress({ ...idle(), xpace: { running: true } }).label, "Xpace");
});

test("conversions come before analyses, and those before files; several files are averaged", () => {
  const all = { cloud: { running: true, progress: { value: 0.1 } }, tps: { converting: true }, xpace: { running: true }, receiving: [0.5], sending: [0.2, 0.6] };
  assert.equal(activeProgress(all).color, "#38BDF8");
  assert.equal(activeProgress({ ...all, cloud: { running: false, progress: { value: 0 } } }).label, "TPS to RINEX");
  const files = activeProgress({ ...idle(), sending: [0.2, 0.6], receiving: [0.9] });
  assert.ok(Math.abs(files.value - 0.4) < 1e-9, "sending wins, averaged");
  assert.equal(activeProgress({ ...idle(), receiving: [0.9] }).value, 0.9);
});

test("a value outside 0…1 or not a number is kept inside the bar", () => {
  assert.equal(activeProgress({ ...idle(), cloud: { running: true, progress: { value: 1.7 } } }).value, 1);
  assert.equal(activeProgress({ ...idle(), cloud: { running: true, progress: { value: NaN } } }).value, 0);
});

test("a file going to somebody says Sending, one going to the AI says Uploading", async () => {
  const { transferLabel } = await import("../src/core/progress.ts");
  assert.equal(transferLabel("plano.las", "Ana"), "Sending plano.las to Ana");
  assert.equal(transferLabel("plano.las", null), "Uploading plano.las");
});
