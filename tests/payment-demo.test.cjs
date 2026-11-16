const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const source = fs.readFileSync(path.join(__dirname, "..", "wwwroot", "app.js"), "utf8");
const start = source.indexOf("function simulatedBalance(");
const end = source.indexOf("async function renderPaymentDemo(", start);
const context = {};
vm.runInNewContext(source.slice(start, end) + "\nglobalThis.simulatedBalance = simulatedBalance; globalThis.demoElapsedTime = demoElapsedTime; globalThis.createDemoExampleGenerator = createDemoExampleGenerator;", context);
const balance = context.simulatedBalance;
const startedAt = "2026-01-01T00:00:00Z";
const at = hours => Date.parse(startedAt) + hours * 3600000;

test("payment activity displays up to 500 clearly labeled simulation entries", () => {
  const limit = Number(source.match(/const PAYMENT_ACTIVITY_LIMIT = (\d+);/)?.[1]);
  assert.equal(limit, 500);
  assert.match(source, /<small>Fictional example<\/small>/);
  assert.match(source, />SIMULATION ONLY<\/span>/);
});

test("demo uses five dollars above $10,000 and two dollars at or below it", () => {
  assert.equal(balance({ startedAt, startingAmount: 12345, goal: 1000001 }, at(5)), 12845);
  assert.equal(balance({ startedAt, startingAmount: 12345, goal: 1000000 }, at(5)), 12545);
  assert.equal(balance({ startedAt, startingAmount: 12345, goal: 999999 }, at(4)), 12345);
});

test("demo caps at the goal and resets to its original balance on the next step", () => {
  const item = { startedAt, startingAmount: 925, goal: 1500 };
  assert.equal(balance(item, at(10)), 1325);
  assert.equal(balance(item, at(15)), 1500);
  assert.equal(balance(item, at(20)), 925);
  assert.equal(balance(item, at(25)), 1125);
});

test("demo retains starting balance when already at or above its goal", () => {
  assert.equal(balance({ startedAt, startingAmount: 1500, goal: 1000 }, at(100)), 1500);
});

test("a $15 starting balance returns to $15 after reaching its $20 goal", () => {
  const item = { startedAt, startingAmount: 1500, goal: 2000 };
  assert.deepEqual([0, 5, 10, 15, 20, 25].map(hours => balance(item, at(hours))), [1500, 1700, 1900, 2000, 1500, 1700]);
});

test("demo activity elapsed times advance in seconds, minutes, and hours", () => {
  assert.equal(context.demoElapsedTime(5000), "5s ago");
  assert.equal(context.demoElapsedTime(65000), "1m ago");
  assert.equal(context.demoElapsedTime(3600000), "1h ago");
});

test("demo examples avoid repeating recent names even with repeated random values", () => {
  const next = context.createDemoExampleGenerator(() => 0);
  const examples = Array.from({ length: 200 }, () => next());
  for (let i = 0; i < examples.length; i++) {
    assert.match(examples[i].name, /^\w+ \w+$/);
    assert.equal(examples.slice(Math.max(0, i - 100), i).some(item => item.name === examples[i].name), false);
    if (i) assert.notEqual(examples[i].amount, examples[i - 1].amount);
  }
});

test("demo example amounts vary across the full supported range", () => {
  const values = [0, 0, 0, 0.999999];
  const next = context.createDemoExampleGenerator(() => values.shift());
  assert.equal(next().amount, 5);
  assert.equal(next().amount, 10000);
});
