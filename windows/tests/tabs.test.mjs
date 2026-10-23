// The header tabs' order: what is saved, repaired when it is damaged, and where a dragged tab lands.
import { test } from "node:test";
import assert from "node:assert/strict";
import { tabOrder, dropIndex } from "../src/core/tabs.ts";

test("nothing saved is the usual order", () => {
  assert.deepEqual(tabOrder([]), ["home", "chat", "drop", "tools"]);
  assert.deepEqual(tabOrder(undefined), ["home", "chat", "drop", "tools"]);
});

test("the + after the tools, as asked", () => {
  assert.deepEqual(tabOrder(["home", "chat", "tools", "drop"]), ["home", "chat", "tools", "drop"]);
});

test("a damaged or old list is made whole: unknown and repeated ids go, forgotten tabs come back at the end", () => {
  assert.deepEqual(tabOrder(["tools", "tools", "mail", 7, "home"]), ["tools", "home", "chat", "drop"]);
  assert.deepEqual(tabOrder("chat"), ["home", "chat", "drop", "tools"]);
});

test("a dragged tab lands before the first tab whose middle is to its right", () => {
  const centers = [10, 40, 70];
  assert.equal(dropIndex(0, centers), 0);
  assert.equal(dropIndex(25, centers), 1);
  assert.equal(dropIndex(60, centers), 2);
  assert.equal(dropIndex(100, centers), 3);
  assert.equal(dropIndex(5, []), 0);
});
