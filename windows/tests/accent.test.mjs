// The colour each person gets: always the same, always from the pills' palette, never the island's own white.
import { test } from "node:test";
import assert from "node:assert/strict";
import { accentFor } from "../src/core/accent.ts";
import { PILL_PALETTE } from "../src/core/pill-colors.ts";

test("a person keeps their colour and it is one of the palette's", () => {
  const id = "3f2c9a52-1c0e-4c53-9d55-0d2c5e9a8f10";
  assert.equal(accentFor(id), accentFor(id));
  assert.ok(PILL_PALETTE.includes(accentFor(id)));
});

test("the island's white is never a person's colour, and the people spread over the palette", () => {
  const seen = new Set();
  for (let i = 0; i < 300; i++) {
    const c = accentFor(`device-${i}`);
    assert.notEqual(c, "#F5F6F8");
    seen.add(c);
  }
  assert.ok(seen.size >= 6, `only ${seen.size} colours`);
});
