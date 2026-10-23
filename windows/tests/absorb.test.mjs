// The little card that flies into a person's tile: what it says.
import { test } from "node:test";
import assert from "node:assert/strict";
import { fileBadge } from "../src/core/../views/absorb.ts";

test("the card says the file's type in capitals", () => {
  assert.equal(fileBadge("C:/datos/Lote 4.las"), "LAS");
  assert.equal(fileBadge("plano.PDF"), "PDF");
  assert.equal(fileBadge("C:/a/b/c.tar.gz"), "GZ");
});

test("a file with no usable type says FILE", () => {
  assert.equal(fileBadge("C:/a/LEEME"), "FILE");
  assert.equal(fileBadge(".gitignore"), "FILE");
  assert.equal(fileBadge("raro.documentos"), "FILE", "too long to be a type");
  assert.equal(fileBadge("x.a b"), "FILE");
  assert.equal(fileBadge(""), "FILE");
});
