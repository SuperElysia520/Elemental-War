// Offline tests execute the publishable Cloud Code script in a VM. Only the
// Cloud Save SDK is replaced: no UGS account, service key, worker or network.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const { test } = require("node:test");

const SCRIPT_PATH = path.join(__dirname, "..", "FpsPvp.js");
const MAILBOX = "fps_pvp_bridge_v1";
const PROJECT = "project-1", PLAYER = "player-1", OTHER_PLAYER = "player-2";
const EPOCH = "a".repeat(32), OLD_EPOCH = "b".repeat(32);
const REQUEST = "1".repeat(32), OTHER_REQUEST = "2".repeat(32), OLD_REQUEST = "3".repeat(32);
const NOW = 1800000000000;
const SECRET = "SECRET server-token-private player-token-private SDK request body";
const clone = value => value === undefined ? undefined : JSON.parse(JSON.stringify(value));
const own = (value, key) => Object.prototype.hasOwnProperty.call(value, key);
const bytes = value => Buffer.byteLength(JSON.stringify(value), "utf8");
const keyFor = playerId => "p_" + playerId;
const context = (playerId = PLAYER) => ({ playerId, projectId: PROJECT, serviceToken: "server-token-private", accessToken: "player-token-private" });
const lease = (changes = {}) => ({ version: 1, epoch: EPOCH, expiresAt: NOW + 15000, ...changes });
const submit = (operation = "current", args = {}, changes = {}) => ({ action: "submit", requestId: REQUEST, operation, username: "Account_1", args, ...changes });
const status = (requestId = REQUEST) => ({ action: "status", requestId });
const slot = (changes = {}) => ({
  version: 1, epoch: EPOCH, playerId: PLAYER, requestId: REQUEST, username: "Account_1",
  operation: "current", args: {}, createdAt: NOW, expiresAt: NOW + 30000,
  state: "pending", response: null, ...changes
});
const done = (body = '{"party":null,"message":""}', statusCode = 200, changes = {}) => slot({ state: "done", response: { status: statusCode, body }, ...changes });

function sdkError(statusCode) {
  const error = new Error(SECRET);
  error.response = { status: statusCode, data: { message: SECRET } };
  return error;
}

function deferred() {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
}

function barrier(participants) {
  const wait = deferred();
  let arrived = 0;
  return async () => { if (++arrived === participants) wait.resolve(); await wait.promise; };
}

function harness(options = {}) {
  const state = new Map();
  const calls = { constructors: [], reads: [], writes: [], deletes: [], logs: [] };
  const clock = { now: NOW };
  let sequence = 0;
  const storageId = (projectId, customId, key) => projectId + "/" + customId + "/" + key;
  class FakeDate extends Date { static now() { return clock.now; } }
  class FakeDataApi {
    constructor(configuration) { calls.constructors.push(clone(configuration)); }
    async getPrivateCustomItems(projectId, customId, keys) {
      const call = { projectId, customId, keys: clone(keys) };
      calls.reads.push(call);
      if (options.readError) throw options.readError;
      if (options.readResponse) return clone(typeof options.readResponse === "function" ? options.readResponse(call) : options.readResponse);
      const items = keys.map(key => clone(state.get(storageId(projectId, customId, key)))).filter(Boolean);
      if (options.afterSnapshot) await options.afterSnapshot({ ...call, items: clone(items) });
      return { data: { results: items } };
    }
    async setPrivateCustomItem(projectId, customId, body) {
      const call = { projectId, customId, body: clone(body) };
      calls.writes.push(call);
      if (options.writeError) throw options.writeError;
      const id = storageId(projectId, customId, body.key);
      const current = state.get(id);
      // Omission really is an unconditional upsert, including when another
      // first creator has written since the earlier missing-item read.
      if (own(body, "writeLock") && (!current || current.writeLock !== body.writeLock)) throw sdkError(409);
      const writeLock = "lock-" + (++sequence);
      state.set(id, { key: body.key, value: clone(body.value), writeLock });
      if (options.afterWrite) await options.afterWrite(call);
      return options.writeResponse ? clone(options.writeResponse) : { data: { writeLock } };
    }
    async deletePrivateCustomItem(key, projectId, customId, writeLock) {
      calls.deletes.push({ key, projectId, customId, writeLock });
      const id = storageId(projectId, customId, key);
      const current = state.get(id);
      if (writeLock !== undefined && (!current || current.writeLock !== writeLock)) throw sdkError(409);
      state.delete(id);
      return { status: 204 };
    }
  }
  const sandbox = {
    module: { exports: {} }, Date: FakeDate, Buffer,
    require(name) {
      assert.equal(name, "@unity-services/cloud-save-1.4");
      return { DataApi: FakeDataApi };
    }
  };
  vm.runInNewContext(fs.readFileSync(SCRIPT_PATH, "utf8"), sandbox, { filename: "FpsPvp.js" });
  const script = sandbox.module.exports;
  const seedItem = (key, value, writeLock = "seed-" + key) => {
    state.set(storageId(PROJECT, MAILBOX, key), { key, value: clone(value), writeLock });
  };
  if (options.seedLease !== false) seedItem("bridge", lease());
  return {
    options, state, calls, clock, params: clone(script.params),
    seedLease(value = lease(), writeLock) { seedItem("bridge", value, writeLock); },
    seedSlot(value = slot(), writeLock = "slot-lock", playerId = PLAYER) { seedItem(keyFor(playerId), value, writeLock); },
    item(key = keyFor(PLAYER)) { return state.get(storageId(PROJECT, MAILBOX, key)); },
    async run(params, requestContext = context()) {
      const logger = {};
      for (const level of ["error", "warning", "warn", "info", "debug"]) logger[level] = (...args) => calls.logs.push([level, ...clone(args)]);
      return clone(await script({ params, context: requestContext, logger }));
    }
  };
}

function assertFailure(result, code, requestId = REQUEST) {
  assert.equal(result.ok, false);
  if (Array.isArray(code)) assert.equal(code.includes(result.code), true, JSON.stringify(result));
  else assert.equal(result.code, code);
  assert.equal(result.state, "");
  assert.equal(result.body, "");
  const statuses = { auth_required: 401, invalid_request: 400, request_busy: 409, request_expired: 410,
    request_superseded: 409, bridge_restarted: 409, bridge_unavailable: 503, service_unavailable: 503 };
  assert.equal(result.status, statuses[result.code]);
  if (requestId !== undefined) assert.equal(result.requestId, requestId);
}

function assertPending(result, requestId = REQUEST) {
  assert.deepEqual(result, { ok: true, code: "", state: "pending", requestId, body: "", status: 202 });
}

function assertNoSecrets(h, result) {
  const serialized = JSON.stringify([result, h.calls.logs]);
  for (const forbidden of ["SECRET", "token-private", "SDK request body"]) assert.equal(serialized.includes(forbidden), false);
}

test("publish metadata declares exactly the five Dashboard parameters", () => {
  const h = harness();
  assert.deepEqual(h.params, {
    action: { type: "String", required: true }, requestId: { type: "String", required: true },
    operation: { type: "String", required: false }, username: { type: "String", required: false },
    args: { type: "JSON", required: false }
  });
});

test("submit writes only the authenticated player's private mailbox and returns without a worker", async () => {
  const h = harness();
  const result = await h.run(submit());
  assertPending(result);
  assert.equal(h.calls.writes.length, 1);
  const written = h.calls.writes[0];
  assert.equal(written.projectId, PROJECT);
  assert.equal(written.customId, MAILBOX);
  assert.equal(written.body.key, keyFor(PLAYER));
  assert.equal(own(written.body, "writeLock"), false);
  assert.deepEqual(written.body.value, slot());
  assert.equal(h.calls.deletes.length, 0);
  assert.deepEqual(h.calls.constructors, [{ accessToken: "server-token-private" }]);
  assert.equal(h.calls.reads.every(call => call.projectId === PROJECT && call.customId === MAILBOX && call.keys.every(key => key === "bridge" || key === keyFor(PLAYER))), true);
  assert.equal(h.calls.reads.filter(call => call.keys.includes(keyFor(PLAYER))).length >= 2, true, "first create must verify that its own request survived the unlocked upsert");
});

test("username and args may be omitted without changing the authenticated identity", async () => {
  const h = harness();
  assertPending(await h.run({ action: "submit", requestId: REQUEST, operation: "current" }));
  const stored = h.item().value;
  assert.equal(stored.playerId, PLAYER);
  assert.equal(/^[A-Za-z0-9.@_-]{3,20}$/.test(stored.username), true);
  assert.deepEqual(stored.args, {});
  assert.equal(stored.epoch, EPOCH);
});

test("all supported operations accept only their documented arguments", async t => {
  const operations = ["current", "rooms", "create", "queue", "cancel", "leave", "fail", "ticket", "game-ticket"];
  for (const operation of operations) await t.test(operation, async () => {
    const h = harness();
    assertPending(await h.run(submit(operation)));
    assert.equal(h.item().value.operation, operation);
  });
  for (const [operation, args] of [["join", { partyId: "9".repeat(32) }], ["ready", { ready: true }], ["ready", { ready: false }]]) {
    await t.test(operation + JSON.stringify(args), async () => {
      const h = harness();
      assertPending(await h.run(submit(operation, args)));
      assert.deepEqual(h.item().value.args, args);
    });
  }
});

test("authentication and service context errors fail before private SDK access", async t => {
  const cases = [
    ["no context", null, "auth_required"], ["no player", { ...context(), playerId: "" }, "auth_required"],
    ["numeric player", { ...context(), playerId: 123 }, "auth_required"],
    ["path traversal player", { ...context(), playerId: "../victim" }, "auth_required"],
    ["oversized player ID", { ...context(), playerId: "x".repeat(65) }, "auth_required"],
    ["no project", { ...context(), projectId: "" }, "service_unavailable"],
    ["no server token", { ...context(), serviceToken: "" }, "service_unavailable"]
  ];
  for (const [name, requestContext, code] of cases) await t.test(name, async () => {
    const h = harness();
    assertFailure(await h.run(submit(), requestContext), code);
    assert.equal(h.calls.constructors.length, 0);
    assert.equal(h.calls.reads.length, 0);
    assert.equal(h.calls.writes.length, 0);
  });
});

test("malformed params and injected authority never reach the SDK", async t => {
  const cases = [
    ["missing params", null], ["array params", []], ["string params", "bad"],
    ["unknown action", { ...submit(), action: "execute" }], ["unknown operation", submit("delete")],
    ["missing operation", { action: "submit", requestId: REQUEST }],
    ["non-string action", { ...submit(), action: 1 }], ["non-string operation", submit(1)],
    ["invalid request length", { ...submit(), requestId: "1".repeat(31) }],
    ["nonhex request", { ...submit(), requestId: "z".repeat(32) }],
    ["numeric request", { ...submit(), requestId: 123 }],
    ["invalid username", { ...submit(), username: "中文账号" }],
    ["numeric username", { ...submit(), username: 123 }],
    ["short username", { ...submit(), username: "ab" }],
    ["long username", { ...submit(), username: "a".repeat(21) }],
    ["null args", { ...submit(), args: null }], ["array args", { ...submit(), args: [] }],
    ["forged player", { ...submit(), playerId: OTHER_PLAYER }],
    ["forged account", { ...submit(), accountId: OTHER_PLAYER }],
    ["forged profile", { ...submit(), profile: { accountId: OTHER_PLAYER, revision: 999 } }],
    ["forged epoch", { ...submit(), epoch: OLD_EPOCH }],
    ["forged service token", { ...submit(), serviceToken: "attacker" }],
    ["forged context", { ...submit(), context: context(OTHER_PLAYER) }],
    ["forged match", { ...submit(), match: { matchId: "9".repeat(32), team: 1 } }],
    ["forged server port", submit("current", { serverPort: 7777 })],
    ["forged request state", { ...submit(), state: "done" }],
    ["forged proto", JSON.parse(JSON.stringify(submit()).slice(0, -1) + ',"__proto__":{"playerId":"victim"}}')]
  ];
  for (const [name, params] of cases) await t.test(name, async () => {
    const h = harness();
    const result = await h.run(params);
    assert.equal(result.ok, false);
    assert.equal(result.code, "invalid_request");
    assert.equal(result.body, "");
    assert.equal(h.calls.reads.length, 0);
    assert.equal(h.calls.writes.length, 0);
  });
});

test("join, ready and no-argument commands reject extra or mistyped arguments", async t => {
  const cases = [
    ["join missing party", "join", {}], ["join short ID", "join", { partyId: "123" }],
    ["join nonhex ID", "join", { partyId: "z".repeat(32) }],
    ["join numeric ID", "join", { partyId: 123 }],
    ["join forged player", "join", { partyId: "9".repeat(32), playerId: OTHER_PLAYER }],
    ["ready missing bool", "ready", {}], ["ready numeric bool", "ready", { ready: 1 }],
    ["ready string bool", "ready", { ready: "true" }],
    ["ready forged team", "ready", { ready: true, team: 2 }]
  ];
  for (const operation of ["current", "rooms", "create", "queue", "cancel", "leave", "fail", "ticket", "game-ticket"])
    cases.push([operation + " rejects args", operation, { accountId: OTHER_PLAYER }]);
  for (const [name, operation, args] of cases) await t.test(name, async () => {
    const h = harness();
    assertFailure(await h.run(submit(operation, args)), "invalid_request");
    assert.equal(h.calls.reads.length, 0);
    assert.equal(h.calls.writes.length, 0);
  });
});

test("missing, stale and malformed worker leases fail closed", async t => {
  const cases = [
    ["missing", undefined], ["null", null], ["array", []], ["wrong version", lease({ version: 2 })],
    ["wrong epoch", lease({ epoch: "bad" })], ["nonhex epoch", lease({ epoch: "z".repeat(32) })],
    ["expired", lease({ expiresAt: NOW - 1 })], ["expiry boundary", lease({ expiresAt: NOW })],
    ["string expiry", lease({ expiresAt: String(NOW + 15000) })], ["missing expiry", { version: 1, epoch: EPOCH }]
  ];
  for (const [name, value] of cases) await t.test(name, async () => {
    const h = harness({ seedLease: false });
    if (value !== undefined) h.seedLease(value);
    for (const request of [submit(), status()]) assertFailure(await h.run(request), "bridge_unavailable");
    assert.equal(h.calls.writes.length, 0);
    assert.equal(h.calls.deletes.length, 0);
  });
});

test("a status request reads only its own request and does not create a mailbox", async () => {
  const h = harness();
  const privateOtherBody = '{"private":"other-player-secret"}';
  h.seedSlot(done(privateOtherBody, 200, { playerId: OTHER_PLAYER }), "other-lock", OTHER_PLAYER);
  assertFailure(await h.run(status()), "request_expired");
  assert.equal(h.calls.writes.length, 0);
  assert.equal(h.calls.reads.every(call => !call.keys.includes(keyFor(OTHER_PLAYER))), true);
  h.seedSlot(done('{"mine":true}'));
  assertFailure(await h.run(status(OTHER_REQUEST)), "request_superseded", OTHER_REQUEST);
  const ownResult = await h.run(status());
  assert.deepEqual(ownResult, { ok: true, code: "", state: "done", requestId: REQUEST, body: '{"mine":true}', status: 200 });
  assert.equal(JSON.stringify(ownResult).includes("other-player-secret"), false);
  assert.equal(h.calls.writes.length, 0);
});

test("same request ID is idempotent while a changed command cannot reuse it", async t => {
  const h = harness();
  assertPending(await h.run(submit("ready", { ready: true })));
  assertPending(await h.run(submit("ready", { ready: true })));
  assert.equal(h.calls.writes.length, 1);
  const before = clone(h.item());
  for (const [name, request] of [
    ["operation", submit("current")], ["args", submit("ready", { ready: false })],
    ["username", submit("ready", { ready: true }, { username: "Changed_2" })]
  ]) await t.test(name, async () => {
    assertFailure(await h.run(request), "invalid_request");
    assert.deepEqual(h.item(), before);
  });
  h.seedSlot(done('{"ready":true}', 200, { operation: "ready", args: { ready: true } }), "completed-lock");
  const completed = await h.run(submit("ready", { ready: true }));
  assert.equal(completed.state, "done");
  assert.equal(completed.body, '{"ready":true}');
  assert.equal(h.calls.writes.length, 1);
});

test("a different live pending request is busy without an overwrite or leaked result", async () => {
  const h = harness();
  h.seedSlot(slot({ requestId: OTHER_REQUEST }));
  const before = clone(h.item());
  assertFailure(await h.run(submit()), "request_busy");
  assert.deepEqual(h.item(), before);
  assert.equal(h.calls.writes.length, 0);
});

test("done, expired and previous-epoch slots can only be replaced using their real CAS lock", async t => {
  for (const [name, value] of [
    ["done", done('{"old-result":"must-not-escape"}', 200, { requestId: OLD_REQUEST })],
    ["expired", slot({ requestId: OLD_REQUEST, createdAt: NOW - 30000, expiresAt: NOW })],
    ["previous epoch", slot({ requestId: OLD_REQUEST, epoch: OLD_EPOCH })]
  ]) await t.test(name, async () => {
    const h = harness();
    h.seedSlot(value, "real-existing-lock");
    const result = await h.run(submit());
    assertPending(result);
    assert.equal(h.calls.writes.length, 1);
    assert.equal(h.calls.writes[0].body.writeLock, "real-existing-lock");
    assert.deepEqual(h.item().value, slot());
    assert.equal(h.calls.deletes.length, 0);
    assert.equal(JSON.stringify(result).includes("must-not-escape"), false);
  });
});

test("expired requests and old epochs return fixed errors rather than stale result bodies", async t => {
  for (const [name, value, code] of [
    ["expired pending", slot({ createdAt: NOW - 30000, expiresAt: NOW }), "request_expired"],
    ["expired done", done('{"private":"expired-body"}', 200, { createdAt: NOW - 30001, expiresAt: NOW - 1 }), "request_expired"],
    ["old epoch pending", slot({ epoch: OLD_EPOCH }), "bridge_restarted"],
    ["old epoch done", done('{"private":"old-epoch-body"}', 200, { epoch: OLD_EPOCH }), "bridge_restarted"]
  ]) await t.test(name, async () => {
    const h = harness();
    h.seedSlot(value);
    const result = await h.run(status());
    assertFailure(result, code);
    assert.equal(h.calls.writes.length, 0);
    assert.equal(JSON.stringify(result).includes("private"), false);
  });
});

test("expired same-ID retries fail instead of recreating or executing the command", async () => {
  const h = harness();
  h.seedSlot(slot({ createdAt: NOW - 30000, expiresAt: NOW }));
  const before = clone(h.item());
  assertFailure(await h.run(submit()), "request_expired");
  assert.equal(h.calls.writes.length, 0);
  assert.deepEqual(h.item(), before);
});

test("a reused request ID from an earlier worker epoch creates a fresh command instead of leaking its response", async () => {
  const h = harness();
  h.seedSlot(done('{"private":"old-worker-result"}', 200, { epoch: OLD_EPOCH }), "old-worker-lock");
  assertPending(await h.run(submit()));
  assert.equal(h.calls.writes.length, 1);
  assert.equal(h.calls.writes[0].body.writeLock, "old-worker-lock");
  assert.equal(h.item().value.epoch, EPOCH);
});

test("valid worker business errors are successful done envelopes with their HTTP status", async t => {
  for (const statusCode of [200, 204, 400, 401, 403, 404, 409, 429, 503]) await t.test(String(statusCode), async () => {
    const h = harness();
    const body = statusCode === 204 ? "" : JSON.stringify({ code: "party_not_ready", message: "队伍尚未准备。" });
    h.seedSlot(done(body, statusCode));
    assert.deepEqual(await h.run(status()), { ok: true, code: "", state: "done", requestId: REQUEST, body, status: statusCode });
    assert.equal(h.calls.writes.length, 0);
  });
});

test("malformed stored slots and responses fail without disclosing worker content", async t => {
  const cases = [
    ["wrong owner", value => { value.playerId = OTHER_PLAYER; }],
    ["wrong version", value => { value.version = 2; }],
    ["unknown state", value => { value.state = "running"; }],
    ["missing response", value => { value.response = null; }],
    ["nonstring body", value => { value.response.body = { secret: SECRET }; }],
    ["missing body", value => { delete value.response.body; }],
    ["body is not JSON", value => { value.response.body = "invalid JSON " + SECRET; }],
    ["empty 200 body", value => { value.response.body = ""; }],
    ["noninteger status", value => { value.response.status = 200.5; }],
    ["string status", value => { value.response.status = "200"; }],
    ["too-small status", value => { value.response.status = 199; }],
    ["too-large status", value => { value.response.status = 600; }],
    ["invalid request ID", value => { value.requestId = "bad"; }],
    ["invalid expiry", value => { value.expiresAt = "future"; }],
    ["fractional creation", value => { value.createdAt += 0.5; value.expiresAt += 0.5; }],
    ["wrong TTL", value => { value.expiresAt--; }],
    ["unsafe timestamps", value => { value.createdAt = Number.MAX_SAFE_INTEGER + 1; value.expiresAt = value.createdAt + 30000; }],
    ["unexpected slot field", value => { value.serviceToken = SECRET; }],
    ["unexpected response field", value => { value.response.serviceToken = SECRET; }],
    ["pending response payload", value => { value.state = "pending"; }],
    ["invalid operation", value => { value.operation = "inject"; }],
    ["invalid args", value => { value.args = []; }]
  ];
  for (const [name, mutate] of cases) await t.test(name, async () => {
    const h = harness();
    const value = done('{"private":"' + SECRET + '"}');
    mutate(value);
    h.seedSlot(value);
    const before = clone(h.item());
    const result = await h.run(status());
    assertFailure(result, ["service_unavailable", "request_superseded", "request_expired"]);
    assertNoSecrets(h, result);
    assert.deepEqual(h.item(), before);
    assert.equal(h.calls.writes.length, 0);
  });
});

test("an existing owner's slot cannot be overwritten when it contains another player's data", async () => {
  const h = harness();
  h.seedSlot(done('{"private":"other-owner-result"}', 200, { playerId: OTHER_PLAYER }));
  const before = clone(h.item());
  assertFailure(await h.run(submit()), "service_unavailable");
  assert.equal(h.calls.writes.length, 0);
  assert.deepEqual(h.item(), before);
});

test("malformed SDK item containers and absent existing CAS locks fail closed", async t => {
  for (const [name, response] of [
    ["missing results", { data: {} }], ["wrong results shape", { data: { results: {} } }],
    ["unrequested item", { data: { results: [{ key: keyFor(OTHER_PLAYER), value: done(SECRET), writeLock: "other-lock" }] } }],
    ["duplicate item", { data: { results: [{ key: "bridge", value: lease() }, { key: "bridge", value: lease() }] } }]
  ]) await t.test(name, async () => {
    const h = harness({ readResponse: response });
    const result = await h.run(submit());
    assertFailure(result, ["service_unavailable", "bridge_unavailable"]);
    assertNoSecrets(h, result);
    assert.equal(h.calls.writes.length, 0);
  });
  for (const writeLock of [undefined, ""]) await t.test("lock " + String(writeLock), async () => {
    const h = harness();
    h.seedSlot(done('{"old":true}', 200, { requestId: OLD_REQUEST }));
    if (writeLock === undefined) delete h.item().writeLock;
    else h.item().writeLock = writeLock;
    assertFailure(await h.run(submit()), "service_unavailable");
    assert.equal(h.calls.writes.length, 0);
  });
});

test("SDK failures expose only safe fixed codes for reads and writes", async t => {
  for (const phase of ["read", "write"]) for (const statusCode of [401, 403, 409, 429, 500, 503]) {
    await t.test(phase + " " + statusCode, async () => {
      const h = harness({ [phase + "Error"]: sdkError(statusCode) });
      const result = await h.run(submit());
      assertFailure(result, ["service_unavailable", "request_busy", "request_superseded"]);
      assertNoSecrets(h, result);
      assert.equal(h.calls.writes.length, phase === "write" ? 1 : 0);
      assert.equal(h.calls.deletes.length, 0);
    });
  }
});

test("malformed write acknowledgements are uncertain failures and never cause blind retries", async t => {
  for (const [name, writeResponse] of [
    ["missing data", {}], ["missing lock", { data: {} }], ["empty lock", { data: { writeLock: "" } }]
  ]) await t.test(name, async () => {
    const h = harness({ writeResponse });
    assertFailure(await h.run(submit()), "service_unavailable");
    assert.equal(h.calls.writes.length, 1);
    assert.equal(h.item().value.requestId, REQUEST, "the write might already have committed");
    assertPending(await h.run(status()));
    assert.equal(h.calls.writes.length, 1);
  });
});

test("write verification never returns a disappeared, replaced or mismatched request", async t => {
  const cases = [
    ["missing readback", h => { h.state.delete(PROJECT + "/" + MAILBOX + "/" + keyFor(PLAYER)); }, "request_superseded"],
    ["different request", h => h.seedSlot(done('{"private":"replacement"}', 200, { requestId: OTHER_REQUEST })), "request_superseded"],
    ["different epoch", h => h.seedSlot(done('{"private":"replacement"}', 200, { epoch: OLD_EPOCH })), "request_superseded"],
    ["same ID different command", h => h.seedSlot(done('{"private":"replacement"}', 200, { operation: "create" })), "request_superseded"],
    ["same ID different username", h => h.seedSlot(done('{"private":"replacement"}', 200, { username: "Changed_2" })), "request_superseded"],
    ["wrong owner", h => h.seedSlot(done('{"private":"replacement"}', 200, { playerId: OTHER_PLAYER })), "service_unavailable"]
  ];
  for (const [name, mutate, code] of cases) await t.test(name, async () => {
    const h = harness();
    h.options.afterWrite = () => mutate(h);
    const result = await h.run(submit());
    assertFailure(result, code);
    assert.equal(JSON.stringify(result).includes("replacement"), false);
    assert.equal(h.calls.writes.length, 1);
  });
});

test("existing concurrent replacements use CAS: one request wins and no unlocked retry occurs", async () => {
  const bothRead = barrier(2);
  let reads = 0;
  const h = harness({ afterSnapshot: async ({ keys }) => { if (keys.includes(keyFor(PLAYER)) && ++reads <= 2) await bothRead(); } });
  h.seedSlot(done('{"previous":true}', 200, { requestId: OLD_REQUEST }), "shared-real-lock");
  const results = await Promise.all([
    h.run(submit("create")), h.run(submit("queue", {}, { requestId: OTHER_REQUEST }))
  ]);
  assert.equal(results.filter(value => value.ok).length, 1);
  assert.equal(results.filter(value => !value.ok).length, 1);
  assert.equal(h.calls.writes.length, 2);
  assert.equal(h.calls.writes.every(call => call.body.writeLock === "shared-real-lock"), true);
  for (const result of results) {
    if (result.ok) { assertPending(result, h.item().value.requestId); }
    else { assertFailure(result, ["request_superseded", "request_busy", "service_unavailable"], result.requestId); }
    assert.equal(result.body, "");
  }
});

test("simultaneous FIRST requests model the non-atomic create and recover safely from superseding", async () => {
  const bothRead = barrier(2), bothWrote = barrier(2);
  let reads = 0;
  const h = harness({
    afterSnapshot: async ({ keys }) => { if (keys.includes(keyFor(PLAYER)) && ++reads <= 2) await bothRead(); },
    afterWrite: bothWrote
  });
  const results = await Promise.all([
    h.run(submit("create")), h.run(submit("queue", {}, { requestId: OTHER_REQUEST }))
  ]);
  assert.equal(h.calls.writes.length, 2);
  assert.equal(h.calls.writes.every(call => !own(call.body, "writeLock")), true);
  assert.equal(results.filter(value => value.ok).length, 1);
  assert.equal(results.filter(value => value.code === "request_superseded").length, 1);
  const survivor = h.item().value.requestId;
  const loser = survivor === REQUEST ? OTHER_REQUEST : REQUEST;
  assertPending(results.find(value => value.ok), survivor);
  assertFailure(await h.run(status(loser)), "request_superseded", loser);
  assertFailure(await h.run(submit("leave", {}, { requestId: loser })), "request_busy", loser);
  h.options.afterWrite = null;
  h.seedSlot(done('{"winner":true}', 200, { ...h.item().value, state: "done", response: { status: 200, body: '{"winner":true}' } }), "worker-completed-lock");
  assertPending(await h.run(submit("leave", {}, { requestId: loser })), loser);
  assert.equal(h.calls.writes.at(-1).body.writeLock, "worker-completed-lock");
  assert.equal(h.item().value.requestId, loser);
});

test("a delayed status snapshot never erases a newer worker completion", async () => {
  const captured = deferred(), release = deferred();
  let held = false;
  const h = harness({ afterSnapshot: async ({ keys }) => {
    if (!held && keys.includes(keyFor(PLAYER))) { held = true; captured.resolve(); await release.promise; }
  } });
  h.seedSlot(slot(), "pending-lock");
  const pending = h.run(status());
  await captured.promise;
  h.seedSlot(done('{"worker":"finished"}'), "worker-lock");
  release.resolve();
  const oldRead = await pending;
  assert.equal(oldRead.ok, true);
  assert.equal(["pending", "done"].includes(oldRead.state), true);
  assert.equal(h.calls.writes.length, 0);
  assert.equal(h.item().writeLock, "worker-lock");
  assert.equal((await h.run(status())).body, '{"worker":"finished"}');
});

test("a worker lease that expires during an awaited slot read prevents both status delivery and submit", async t => {
  for (const action of ["submit", "status"]) await t.test(action, async () => {
    const snapshot = deferred(), release = deferred();
    let blocked = false;
    const h = harness({ afterSnapshot: async ({ keys }) => {
      if (!blocked && keys.includes(keyFor(PLAYER))) { blocked = true; snapshot.resolve(); await release.promise; }
    } });
    if (action === "status") h.seedSlot(done('{"private":"must-not-return-after-lease-expiry"}'));
    const request = h.run(action === "submit" ? submit() : status());
    await snapshot.promise;
    h.clock.now = NOW + 15000;
    release.resolve();
    assertFailure(await request, "bridge_unavailable");
    assert.equal(h.calls.writes.length, 0);
  });
});

test("lease expiry during a write leaves an uncertain committed request that a fresh status can recover", async () => {
  const h = harness();
  h.options.afterWrite = () => { h.clock.now = NOW + 15000; };
  assertFailure(await h.run(submit()), "bridge_unavailable");
  assert.equal(h.calls.writes.length, 1);
  assert.equal(h.item().value.requestId, REQUEST);
  h.seedLease(lease({ expiresAt: h.clock.now + 15000 }));
  assertPending(await h.run(status()));
  assert.equal(h.calls.writes.length, 1);
});

test("a request that ages out while reading is not returned even when the worker lease was renewed", async () => {
  const h = harness();
  h.seedSlot(done('{"private":"expired-response"}'));
  h.clock.now = NOW + 30000;
  h.seedLease(lease({ expiresAt: h.clock.now + 15000 }));
  assertFailure(await h.run(status()), "request_expired");
  assert.equal(h.calls.writes.length, 0);
});

test("different players never share pending slots or returned result bodies", async () => {
  const h = harness();
  assertPending(await h.run(submit("create")));
  assertPending(await h.run(submit("join", { partyId: "9".repeat(32) }), context(OTHER_PLAYER)));
  assert.equal(h.item(keyFor(PLAYER)).value.playerId, PLAYER);
  assert.equal(h.item(keyFor(OTHER_PLAYER)).value.playerId, OTHER_PLAYER);
  h.seedSlot(done('{"private":"A"}'));
  h.seedSlot(done('{"private":"B"}', 200, { playerId: OTHER_PLAYER, operation: "join", args: { partyId: "9".repeat(32) } }), "b-lock", OTHER_PLAYER);
  assert.equal((await h.run(status())).body, '{"private":"A"}');
  assert.equal((await h.run(status(), context(OTHER_PLAYER))).body, '{"private":"B"}');
});

test("UTF-8 request limit rejects malicious payloads around and beyond 4 KB without SDK work", async t => {
  for (const [name, username] of [
    ["ASCII over 4KB", "x".repeat(4097)],
    ["multibyte over 4KB but fewer than 4096 JS characters", "界".repeat(1400)],
    ["surrogate pairs over 4KB", "🎮".repeat(1100)]
  ]) await t.test(name, async () => {
    const h = harness();
    const request = { ...submit(), username };
    assert.equal(bytes(request) > 4096, true);
    assertFailure(await h.run(request), "invalid_request");
    assert.equal(h.calls.reads.length, 0);
    assert.equal(h.calls.writes.length, 0);
  });
});

test("UTF-8 response slots enforce 64 KB, including non-ASCII worker bodies", async t => {
  for (const [name, character, targetBytes, accepted] of [
    ["ASCII just below", "x", 65535, true], ["ASCII exact limit", "x", 65536, true],
    ["ASCII one byte over", "x", 65537, false], ["CJK just below", "界", 65535, true],
    ["CJK over in bytes but not characters", "界", 65539, false], ["emoji over in bytes", "🎮", 65540, false]
  ]) await t.test(name, async () => {
    const h = harness();
    const value = done(JSON.stringify({ padding: "" }));
    const padding = targetBytes - bytes(value);
    const width = Buffer.byteLength(character, "utf8");
    value.response.body = JSON.stringify({ padding: character.repeat(Math.floor(padding / width)) + "x".repeat(padding % width) });
    assert.equal(bytes(value), targetBytes);
    h.seedSlot(value);
    const result = await h.run(status());
    if (accepted) {
      assert.equal(result.ok, true);
      assert.equal(result.state, "done");
      assert.equal(result.body, value.response.body);
    } else {
      assertFailure(result, "service_unavailable");
      assert.equal(result.body, "");
    }
    assert.equal(h.calls.writes.length, 0);
  });
});
