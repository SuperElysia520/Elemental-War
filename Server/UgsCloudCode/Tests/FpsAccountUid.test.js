const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const { test } = require("node:test");
const SOURCE = fs.readFileSync(path.join(__dirname, "..", "FpsAccountUid.js"), "utf8");
const FIRST = 111100001, LAST = 999999999;
const copy = value => JSON.parse(JSON.stringify(value));
const httpError = status => Object.assign(new Error("SECRET SDK body and token"), { response: { status } });

function harness() {
  let lock = 1;
  const state = {
    counter: { key: "sequence", value: { version: 1, nextUid: FIRST, pending: null }, writeLock: "seed" },
    players: new Map(), writes: [], logs: [], counterFailures: [], identityFailures: [], readError: null
  };
  class DataApi {
    constructor(config) { assert.deepEqual(copy(config), { accessToken: "service-secret" }); }
    async getProtectedItems(project, player, keys) {
      assert.equal(project, "project"); assert.deepEqual(copy(keys), ["fps_uid_v1"]);
      if (state.readError) throw state.readError;
      const item = copy(state.players.get(player) || null);
      if (state.afterIdentitySnapshot) await state.afterIdentitySnapshot(player, item);
      return { data: { results: item ? [item] : [] } };
    }
    async getPrivateCustomItems(project, customId, keys) {
      assert.equal(project, "project"); assert.equal(customId, "fps_account_uid_v1");
      assert.deepEqual(copy(keys), ["sequence"]);
      return { data: { results: state.counter ? [copy(state.counter)] : [] } };
    }
    async setPrivateCustomItem(project, customId, body) {
      assert.equal(project, "project"); assert.equal(customId, "fps_account_uid_v1");
      assert.ok(typeof body.writeLock === "string" && body.writeLock);
      if (!state.counter || state.counter.writeLock !== body.writeLock) throw httpError(409);
      const failure = state.counterFailures.shift();
      if (failure === "before") throw httpError(503);
      state.writes.push({ type: "counter", body: copy(body) });
      state.counter = { key: body.key, value: copy(body.value), writeLock: "lock-" + lock++ };
      if (failure === "after") throw httpError(503);
      return { data: { writeLock: state.counter.writeLock } };
    }
    async setProtectedItem(project, player, body) {
      assert.equal(project, "project"); assert.equal(body.key, "fps_uid_v1");
      const failure = state.identityFailures.shift();
      if (failure === "before") throw httpError(503);
      const old = state.players.get(player);
      if (old) assert.deepEqual(copy(body.value), old.value, "A helper must never change an existing UID");
      state.writes.push({ type: "identity", player, body: copy(body) });
      state.players.set(player, { key: body.key, value: copy(body.value), writeLock: "identity-" + lock++ });
      if (failure === "after") throw httpError(503);
      return { data: { writeLock: "identity-" + lock } };
    }
  }
  const sandbox = { module: { exports: {} }, require(name) {
    assert.equal(name, "@unity-services/cloud-save-1.4"); return { DataApi };
  } };
  vm.runInNewContext(SOURCE, sandbox, { filename: "FpsAccountUid.js" });
  const script = sandbox.module.exports;
  return {
    state,
    seed(player, uid) { state.players.set(player, { key: "fps_uid_v1", value: { version: 1, playerId: player, uid }, writeLock: "seed-identity" }); },
    async run(player = "player-1", params = { operation: "ensure" }, changes = {}) {
      return copy(await script({ params, context: { playerId: player, projectId: "project", serviceToken: "service-secret", ...changes },
        logger: { error: (...args) => state.logs.push(copy(args)) } }));
    }
  };
}

test("first and second registrations get consecutive nine-digit UIDs", async () => {
  const h = harness();
  assert.deepEqual(await h.run("earlier"), { ok: true, uid: FIRST });
  assert.deepEqual(await h.run("later"), { ok: true, uid: FIRST + 1 });
  assert.equal(h.state.counter.value.nextUid, FIRST + 2);
});
test("repeated login and registration confirmation never allocate again", async () => {
  const h = harness(); await h.run(); const count = h.state.writes.length;
  for (let i = 0; i < 10; i++) assert.deepEqual(await h.run(), { ok: true, uid: FIRST });
  assert.equal(h.state.writes.length, count);
});
test("existing accounts retain backfilled IDs and new registration starts at the next value", async () => {
  const h = harness(); h.seed("older", FIRST); h.seed("newer", FIRST + 1); h.state.counter.value.nextUid = FIRST + 2;
  assert.equal((await h.run("newer")).uid, FIRST + 1);
  assert.equal((await h.run("older")).uid, FIRST);
  assert.equal((await h.run("third")).uid, FIRST + 2);
});
test("concurrent requests for the same account converge on one UID", async () => {
  const h = harness(); const results = await Promise.all(Array.from({ length: 16 }, () => h.run("same")));
  for (const result of results) assert.deepEqual(result, { ok: true, uid: FIRST });
  assert.equal(h.state.counter.value.nextUid, FIRST + 1);
});
test("concurrent registrations get unique consecutive numbers", async () => {
  const h = harness(); const results = await Promise.all(Array.from({ length: 8 }, (_, i) => h.run("player-" + i)));
  assert.ok(results.every(result => result.ok));
  assert.deepEqual(results.map(result => result.uid).sort((a, b) => a - b), Array.from({ length: 8 }, (_, i) => FIRST + i));
});
test("a delayed empty identity snapshot cannot allocate a second UID for the same player", async () => {
  const h = harness(); let release, captured;
  const capturedPromise = new Promise(resolve => captured = resolve);
  const gate = new Promise(resolve => release = resolve);
  let paused = false;
  h.state.afterIdentitySnapshot = async () => { if (!paused) { paused = true; captured(); await gate; } };
  const delayed = h.run("same"); await capturedPromise;
  assert.equal((await h.run("same")).uid, FIRST);
  release();
  assert.equal((await delayed).uid, FIRST);
  assert.equal(h.state.counter.value.nextUid, FIRST + 1);
});
test("failed counter write does not consume a number", async () => {
  const h = harness(); h.state.counterFailures = ["before"];
  assert.equal((await h.run()).ok, false);
  assert.deepEqual(await h.run(), { ok: true, uid: FIRST });
});
test("lost counter-write response is recovered without allocating twice", async () => {
  const h = harness(); h.state.counterFailures = ["after"];
  assert.equal((await h.run()).ok, false);
  assert.deepEqual(await h.run(), { ok: true, uid: FIRST });
  assert.equal(h.state.counter.value.nextUid, FIRST + 1);
});
test("another registration completes an interrupted reservation before taking its own UID", async () => {
  const h = harness(); h.state.identityFailures = ["before"];
  assert.equal((await h.run("first")).ok, false);
  assert.equal((await h.run("second")).uid, FIRST + 1);
  assert.equal((await h.run("first")).uid, FIRST);
});
test("lost identity response preserves its immutable UID", async () => {
  const h = harness(); h.state.identityFailures = ["after"];
  assert.equal((await h.run()).ok, false);
  assert.equal((await h.run()).uid, FIRST);
  assert.equal((await h.run("next")).uid, FIRST + 1);
});
test("failed reservation cleanup is completed by the next request", async () => {
  const h = harness(); h.state.counterFailures = [null, "before"];
  assert.equal((await h.run()).ok, false);
  assert.equal((await h.run("next")).uid, FIRST + 1);
  assert.equal((await h.run()).uid, FIRST);
});
test("uninitialized sequence is never created by a player call", async () => {
  const h = harness(); h.state.counter = null;
  assert.deepEqual(await h.run(), { ok: false, code: "uid_setup_required" }); assert.equal(h.state.writes.length, 0);
});
test("existing identity is readable while initial migration has not yet enabled new registrations", async () => {
  const h = harness(); h.seed("player-1", FIRST); h.state.counter = null;
  assert.deepEqual(await h.run(), { ok: true, uid: FIRST });
});
test("the last nine-digit value is issued once and exhaustion is explicit", async () => {
  const h = harness(); h.state.counter.value.nextUid = LAST;
  assert.equal((await h.run()).uid, LAST);
  assert.deepEqual(await h.run("next"), { ok: false, code: "uid_exhausted" });
  assert.equal((await h.run()).uid, LAST);
});
for (const params of [{ operation: "reset" }, { operation: "ensure", uid: FIRST }, { operation: "ensure", playerId: "victim" }, {}, null]) {
  test("reject client allocation parameters: " + JSON.stringify(params), async () => {
    const h = harness(); assert.deepEqual(await h.run("player-1", params), { ok: false, code: "invalid_request" });
    assert.equal(h.state.writes.length, 0);
  });
}
for (const player of ["", "../player", null]) {
  test("reject missing or malformed player identity: " + player, async () => {
    const h = harness(); assert.deepEqual(await h.run(player), { ok: false, code: "authentication_required" });
  });
}
test("reject damaged identity without rewriting it", async () => {
  const h = harness(); h.seed("player-1", 11110001);
  assert.deepEqual(await h.run(), { ok: false, code: "uid_corrupt" }); assert.equal(h.state.writes.length, 0);
});
test("reject an identity assigned to another player", async () => {
  const h = harness(); h.seed("player-1", FIRST); h.state.players.get("player-1").value.playerId = "other";
  assert.equal((await h.run()).code, "uid_corrupt");
});
test("a pending reservation cannot replace a conflicting identity", async () => {
  const h = harness(); h.state.counter.value = { version: 1, nextUid: FIRST + 1, pending: { playerId: "other", uid: FIRST } };
  h.seed("other", FIRST + 20);
  assert.equal((await h.run()).code, "uid_corrupt"); assert.equal(h.state.writes.length, 0);
});
test("reject corrupt sequence and lock", async () => {
  const h = harness(); h.state.counter.value.nextUid = 0;
  assert.equal((await h.run()).code, "uid_corrupt");
  h.state.counter.value.nextUid = FIRST; h.state.counter.writeLock = "";
  assert.equal((await h.run()).code, "uid_unavailable");
});
test("service failures only log status and category", async () => {
  const h = harness(); h.state.readError = httpError(503);
  assert.equal((await h.run()).code, "uid_unavailable");
  const output = JSON.stringify(h.state.logs);
  assert.ok(!output.includes("SECRET") && !output.includes("service-secret"));
});
test("rate limits give a retryable failure without allocating", async () => {
  const h = harness(); h.state.readError = httpError(429);
  assert.equal((await h.run()).code, "uid_busy"); assert.equal(h.state.writes.length, 0);
});
