const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

function createRequest(fetch, timers = {}) {
  const source = fs.readFileSync(path.join(__dirname, "..", "wwwroot", "app.js"), "utf8");
  const context = {
    document: { querySelector: () => null },
    Headers,
    FormData,
    AbortController,
    AbortSignal: {},
    fetch,
    setTimeout: timers.setTimeout || setTimeout,
    clearTimeout: timers.clearTimeout || clearTimeout
  };
  vm.runInNewContext(source.slice(0, source.indexOf("async function loadCsrf()")) + "\nglobalThis.request = request;", context);
  return context.request;
}

test("requests work without AbortSignal.timeout", async () => {
  const request = createRequest(async (url, options) => {
    assert.equal(url, "/api/fundraisers");
    assert.equal(options.signal.aborted, false);
    return { ok: true, json: async () => [{ title: "Fundraiser" }] };
  });
  assert.equal((await request("/api/fundraisers"))[0].title, "Fundraiser");
});

test("timeout aborts the request and reports a timeout", async () => {
  let expire;
  let cleared = false;
  const request = createRequest((url, options) => {
    expire();
    assert.equal(options.signal.aborted, true);
    return Promise.reject(new Error("aborted"));
  }, {
    setTimeout: callback => { expire = callback; return 123; },
    clearTimeout: id => { assert.equal(id, 123); cleared = true; }
  });
  await assert.rejects(request("/api/fundraisers"), /took too long/);
  assert.equal(cleared, true);
});

test("network errors are not reported as timeouts", async () => {
  const request = createRequest(async () => { throw new Error("offline"); });
  await assert.rejects(request("/api/fundraisers"), /check your connection/);
});

test("HTTP errors retain the server's message", async () => {
  const request = createRequest(async () => ({ ok: false, status: 401, json: async () => ({ error: "Invalid login" }) }));
  await assert.rejects(request("/api/auth/login"), /Invalid login/);
});
