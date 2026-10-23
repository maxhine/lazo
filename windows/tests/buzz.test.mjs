// A person's buzzes: one every few seconds at most reaches the island.
import { test } from "node:test";
import assert from "node:assert/strict";
import { acceptBuzz } from "../src/core/lazo.ts";

test("the first buzz counts, a flood does not, and each person has their own clock", () => {
  assert.equal(acceptBuzz("ana", 1000), true);
  assert.equal(acceptBuzz("ana", 2000), false);
  assert.equal(acceptBuzz("ana", 5999), false);
  assert.equal(acceptBuzz("luis", 2000), true, "another person");
  assert.equal(acceptBuzz("ana", 6000), true, "five seconds later");
});
