// Eye care in the island: the model of the card. The reminders are the classic Lazo's;
// engine/test-tools.mjs runs them against the real program.

import { test } from "node:test";
import assert from "node:assert/strict";
import { EyeCareModel, countdown, worked } from "../src/core/eyecare.ts";

const base = { type: "eyecare.state", microLeft: 1200, activeLeft: 3300, workToday: 0, micro: 0, active: 0, paused: false, sound: true, clock: "digital", rest: "notice", microEvery: 1200, activeEvery: 3300, microFor: 20, activeFor: 300 };

function model(visible) {
  const sent = [];
  let changes = 0;
  const m = new EyeCareModel(async (c) => { sent.push(c); }, () => { changes++; }, visible);
  return { m, sent, changes: () => changes };
}

test("the settings are orders to the program, not changes made on the page", () => {
  const { m, sent } = model(() => true);
  m.start();
  m.setPaused(true);
  m.setSound(false);
  m.setClock("analog");
  m.setRest("eyes");
  m.trigger("active");
  assert.deepEqual(sent, [
    { cmd: "eyecare.state" }, { cmd: "eyecare.pause", value: true }, { cmd: "eyecare.sound", value: false },
    { cmd: "eyecare.clock", value: "analog" }, { cmd: "eyecare.rest", value: "eyes" }, { cmd: "eyecare.trigger", value: "active" },
  ]);
  assert.equal(m.state, null, "nothing changes until the program says so");
});

test("the program's state arrives; countdowns only redraw the island while the card is on screen", () => {
  let on = false;
  const { m, changes } = model(() => on);
  m.handle(base);
  assert.equal(m.state.microLeft, 1200);
  assert.equal(changes(), 1, "the first state is news");
  m.handle({ ...base, microLeft: 1199 });
  m.handle({ ...base, microLeft: 1198 });
  assert.equal(changes(), 1, "a tick on a hidden card redraws nothing");
  on = true;
  m.handle({ ...base, microLeft: 1197 });
  assert.equal(changes(), 2, "on screen it does");
  on = false;
  m.handle({ ...base, microLeft: 1196, micro: 1 });
  assert.equal(changes(), 3, "a finished break is always news");
  m.handle({ type: "xpace.done" });
  assert.equal(changes(), 3, "other tools' messages are not for it");
});

test("a program that cannot start shows its reason", async () => {
  const m = new EyeCareModel(async () => { throw new Error("The Lazo tools were not found."); }, () => {});
  m.start();
  await new Promise((r) => setTimeout(r, 0));
  assert.equal(m.error, "The Lazo tools were not found.");
});

test("countdowns and worked time read as people read them", () => {
  assert.equal(countdown(1200), "20:00");
  assert.equal(countdown(65), "1:05");
  assert.equal(countdown(3300), "55:00");
  assert.equal(countdown(3725), "1:02:05");
  assert.equal(countdown(-3), "0:00");
  assert.equal(worked(59), "0 min");
  assert.equal(worked(3000), "50 min");
  assert.equal(worked(7500), "2 h 05 min");
});
