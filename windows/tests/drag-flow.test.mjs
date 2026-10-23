// A file dragged over the island: the page's enter/leave flap, and the flow keeps them steady.
import { test } from "node:test";
import assert from "node:assert/strict";
import { DragFlow } from "../src/core/drag-flow.ts";

function flow() {
  const heard = [];
  const timers = new Map();
  let next = 1;
  const f = new DragFlow(
    (phase, x, y) => heard.push(x >= 0 ? `${phase}@${x},${y}` : phase),
    90,
    (fn) => { const id = next++; timers.set(id, fn); return id; },
    (id) => timers.delete(id),
  );
  const fire = () => { for (const [id, fn] of [...timers]) { timers.delete(id); fn(); } };
  return { f, heard, fire, pending: () => timers.size };
}

test("one enter, the moves, and a leave only after a moment with nothing entered", () => {
  const { f, heard, fire } = flow();
  f.enter(10, 20);
  f.over(11, 21);
  f.over(12, 22);
  f.leave();
  assert.deepEqual(heard, ["enter@10,20", "over@11,21", "over@12,22"], "the leave waits");
  fire();
  assert.deepEqual(heard.slice(-1), ["leave"]);
});

test("a flap (leave then enter at once, as when the island rebuilds under the pointer) is no leave at all", () => {
  const { f, heard, fire, pending } = flow();
  f.enter(5, 5);
  for (let i = 0; i < 30; i++) {
    f.leave();
    f.enter(5 + i, 5);
  }
  assert.equal(pending(), 0, "every leave was cancelled by the next enter");
  fire();
  assert.deepEqual(heard, ["enter@5,5"], "one enter, nothing else");
});

test("an over without an enter starts the drag, and a drop ends it without a leave", () => {
  const { f, heard, fire, pending } = flow();
  f.over(1, 2);
  assert.deepEqual(heard, ["enter@1,2", "over@1,2"]);
  f.leave();
  f.drop();
  assert.equal(pending(), 0);
  fire();
  assert.equal(heard.includes("leave"), false, "the drop was not a leave");
  f.enter(3, 4);
  assert.equal(heard.at(-1), "enter@3,4", "a new drag starts clean");
});

test("a leave with no drag in progress is ignored", () => {
  const { f, heard, fire } = flow();
  f.leave();
  fire();
  assert.deepEqual(heard, []);
});
