// What Nudo says when Lazo opens: the hello for the hour, a few variants, and the name after a comma.
import { test } from "node:test";
import assert from "node:assert/strict";
import { greetingLine, period } from "../src/core/greeting-text.ts";

const at = (hour) => new Date(2026, 9, 22, hour, 30);

test("the parts of the day", () => {
  assert.equal(period(4), "night");
  assert.equal(period(5), "morning");
  assert.equal(period(11), "morning");
  assert.equal(period(12), "afternoon");
  assert.equal(period(17), "afternoon");
  assert.equal(period(18), "evening");
  assert.equal(period(22), "evening");
  assert.equal(period(23), "night");
  assert.equal(period(0), "night");
});

test("the first variant is the proper hello for the hour, then the name", () => {
  assert.equal(greetingLine(at(9), "Dr. Sins", 0), "Good morning, Dr. Sins");
  assert.equal(greetingLine(at(15), "Dr. Sins", 0), "Good afternoon, Dr. Sins");
  assert.equal(greetingLine(at(20), "Dr. Sins", 0), "Good evening, Dr. Sins");
  assert.equal(greetingLine(at(2), "Dr. Sins", 0), "Good night, Dr. Sins");
});

test("the plain hello and the third variant, whatever the hour", () => {
  assert.equal(greetingLine(at(9), "Dr. Sins", 1), "Hello, Dr. Sins");
  assert.equal(greetingLine(at(9), "Dr. Sins", 2), "Have a great morning, Dr. Sins");
  assert.equal(greetingLine(at(2), "Dr. Sins", 2), "Working late, Dr. Sins");
  assert.equal(greetingLine(at(9), "Dr. Sins", 4), "Hello, Dr. Sins", "the pick wraps around");
  assert.equal(greetingLine(at(9), "Dr. Sins", -1), "Have a great morning, Dr. Sins");
});

test("without a name it is only the hello, and a name is trimmed", () => {
  assert.equal(greetingLine(at(9), null, 0), "Good morning");
  assert.equal(greetingLine(at(9), "   ", 0), "Good morning");
  assert.equal(greetingLine(at(9), "  Ana ", 0), "Good morning, Ana");
});
