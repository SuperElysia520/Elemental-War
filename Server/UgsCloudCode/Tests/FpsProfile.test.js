// Offline contract tests of the actual publishable script. The Cloud Save SDK
// transport is fake; no Unity account, network, credentials, or CA is involved.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const { test } = require("node:test");

const KEY = "fps_profile_v1";
const SCRIPT = fs.readFileSync(path.join(__dirname, "..", "FpsProfile.js"), "utf8");
const clone = value => value === undefined ? undefined : JSON.parse(JSON.stringify(value));
const context = playerId => ({ playerId, projectId: "project-1", serviceToken: "server-token-private", accessToken: "player-token-private" });
const appearance = () => ({ outfitId: 0, faceId: 0, clothColorId: 0, hairColorId: 0, hat: true, accessories: true });
const loadout = () => ({ primaryWeaponId: 0, primarySightId: 0, sidearmSightId: 0 });
const profile = (playerId = "player-1", revision = 1) => ({
  accountId: playerId, username: "Account_1", revision, avatarId: 0,
  appearance: appearance(), loadout: loadout(), match: null
});
const save = (revision = 0) => ({ operation: "save", username: "Account_1", revision, appearance: appearance(), loadout: loadout() });

function harness(options = {}) {
  const state = new Map();
  const calls = { constructors: [], reads: [], writes: [], logs: [] };
  let sequence = 0;
  class FakeDataApi {
    constructor(config) { calls.constructors.push(clone(config)); }
    async getProtectedItems(projectId, playerId, keys) {
      calls.reads.push({ projectId, playerId, keys: clone(keys) });
      if (options.readError) throw options.readError;
      if (options.readResponse) return clone(options.readResponse);
      const item = clone(state.get(projectId + "/" + playerId + (keys[0] === KEY ? "" : "/" + keys[0])));
      if (options.afterSnapshot) await options.afterSnapshot({ projectId, playerId, item });
      return { data: { results: item ? [item] : [] } };
    }
    async setProtectedItem(projectId, playerId, body) {
      calls.writes.push({ projectId, playerId, body: clone(body) });
      if (options.writeError) throw options.writeError;
      const id = projectId + "/" + playerId + (body.key === KEY ? "" : "/" + body.key);
      const current = state.get(id);
      if (Object.prototype.hasOwnProperty.call(body, "writeLock") &&
          (!current || current.writeLock !== body.writeLock)) {
        const conflict = new Error("SECRET SDK token and request payload must never escape");
        conflict.response = { status: 409 };
        throw conflict;
      }
      const writeLock = "lock-" + (++sequence);
      state.set(id, { key: body.key, value: clone(body.value), writeLock });
      return options.writeResponse ? clone(options.writeResponse) : { data: { writeLock } };
    }
  }
  const sandbox = {
    module: { exports: {} },
    require(name) {
      assert.equal(name, "@unity-services/cloud-save-1.4");
      return { DataApi: FakeDataApi };
    }
  };
  vm.runInNewContext(SCRIPT, sandbox, { filename: "FpsProfile.js" });
  const script = sandbox.module.exports;
  return {
    options, calls, state,
    params: clone(script.params),
    seed(value = profile(), writeLock = "seed-lock") {
      state.set("project-1/" + value.accountId, { key: KEY, value: clone(value), writeLock });
    },
    async run(params, requestContext = context("player-1")) {
      return clone(await script({ params, context: requestContext, logger: { error: (...args) => calls.logs.push(clone(args)) } }));
    }
  };
}

function gate(participants) {
  let arrived = 0;
  let release;
  const ready = new Promise(resolve => { release = resolve; });
  return async () => { if (++arrived === participants) release(); await ready; };
}

test("publish metadata lists the seven Dashboard parameters", () => {
  const h = harness();
  assert.deepEqual(h.params, {
    operation: { type: "String", required: true }, username: { type: "String", required: false },
    revision: { type: "Numeric", required: false }, appearance: { type: "JSON", required: false },
    loadout: { type: "JSON", required: false }, avatarId: { type: "Numeric", required: false },
    personal: { type: "JSON", required: false }
  });
});

const personal = () => ({ displayName: "旅行者", gender: 2, signature: "今天也要赢！" });

test("new display names use Han=2 and ASCII letters/digits=1 with a maximum of 12", async t => {
  for (const displayName of ["名字测试一二", "Abc123Xyz789", "名字测试一AB", "旅行者123456", "𠮷𠮷𠮷𠮷𠮷𠮷", "A", "名"]) {
    await t.test("accept " + displayName, async () => {
      const h = harness();
      const result = await h.run({ operation: "personal", revision: 0, personal: { ...personal(), displayName } });
      assert.equal(result.ok, true);
      assert.equal(result.profile.personal.displayName, displayName);
    });
  }
  for (const displayName of ["名字测试一二三", "Abc123Xyz7890", "名字测试一ABC", "名字测试一二A", "a b", "name_1", "🙂", "\uD840"]) {
    await t.test("reject " + displayName, async () => {
      const h = harness();
      const result = await h.run({ operation: "personal", revision: 0, personal: { ...personal(), displayName } });
      assert.equal(result.code, "invalid_profile");
      assert.equal(h.calls.writes.length, 0);
    });
  }
});

test("legacy long names survive signature/gender edits but a rename must follow the new limit", async () => {
  const h = harness();
  const legacy = { ...personal(), displayName: "LegacyLongName123" };
  h.state.set("project-1/player-1/fps_personal_v1", { key: "fps_personal_v1", value: { revision: 1, personal: legacy }, writeLock: "old-personal-lock" });
  const kept = await h.run({ operation: "personal", revision: 1, personal: { ...legacy, signature: "新签名", gender: 1 } });
  assert.equal(kept.ok, true);
  assert.equal(kept.profile.personal.displayName, legacy.displayName);
  assert.equal((await h.run({ operation: "personal", revision: 2, personal: { ...legacy, displayName: "OtherLongName123" } })).code, "invalid_profile");
  assert.equal((await h.run({ operation: "personal", revision: 2, personal: { ...legacy, displayName: "" } })).code, "invalid_profile");
  const renamed = await h.run({ operation: "personal", revision: 2, personal: { ...legacy, displayName: "名字测试一AB" } });
  assert.equal(renamed.ok, true);
  assert.equal(renamed.profile.personalRevision, 3);
});

test("personal details use a separate protected key and leave identity, character and revision unchanged", async () => {
  const h = harness(); h.seed();
  const initial = clone(h.state.get("project-1/player-1"));
  const result = await h.run({ operation: "personal", revision: 0, personal: personal() });
  assert.equal(result.ok, true);
  assert.deepEqual(result.profile.personal, personal());
  assert.equal(result.profile.username, "Account_1");
  assert.equal(result.profile.revision, 1);
  assert.equal(result.profile.personalRevision, 1);
  assert.deepEqual(h.state.get("project-1/player-1"), initial);
  assert.deepEqual(h.state.get("project-1/player-1/fps_personal_v1").value, { revision: 1, personal: personal() });
  const saved = await h.run(save(1));
  assert.equal(saved.ok, true);
  assert.equal(h.state.get("project-1/player-1/fps_personal_v1").value.personal.signature, personal().signature);
});

test("personal details can be saved before a character profile exists", async () => {
  const h = harness();
  const result = await h.run({ operation: "personal", revision: 0, personal: personal() });
  assert.equal(result.ok, true);
  assert.equal(h.state.has("project-1/player-1"), false);
  assert.equal(result.profile.revision, 0);
});

test("personal revisions reject stale updates and preserve the other device's changes", async () => {
  const h = harness();
  await h.run({ operation: "personal", revision: 0, personal: personal() });
  const stale = await h.run({ operation: "personal", revision: 0, personal: { ...personal(), gender: 1 } });
  assert.equal(stale.code, "profile_conflict");
  const updated = await h.run({ operation: "personal", revision: 1, personal: { ...personal(), signature: "" } });
  assert.equal(updated.ok, true);
  assert.equal(updated.profile.personalRevision, 2);
  assert.equal(updated.profile.personal.signature, "");
  assert.equal(h.calls.writes[1].body.writeLock, "lock-1");
});

test("invalid personal details and forged fields are rejected before any storage calls", async t => {
  for (const invalid of [null, {}, { ...personal(), gender: 3 }, { ...personal(), gender: 1.5 },
    { ...personal(), displayName: "a".repeat(21) }, { ...personal(), displayName: " bad " },
    { ...personal(), displayName: "<b>bad</b>" }, { ...personal(), signature: "a".repeat(81) },
    { ...personal(), signature: "line\nbreak" }, { ...personal(), accountId: "other-player" }]) {
    await t.test(JSON.stringify(invalid), async () => {
      const h = harness();
      assert.equal((await h.run({ operation: "personal", revision: 0, personal: invalid })).code, "invalid_profile");
      assert.equal(h.calls.reads.length, 0); assert.equal(h.calls.writes.length, 0);
    });
  }
});

test("missing load returns virtual defaults without creating Cloud Save data", async () => {
  const h = harness();
  const result = await h.run({ operation: "load", username: "Account_1" });
  assert.deepEqual(result, { ok: true, profile: profile("player-1", 0) });
  assert.equal(h.calls.writes.length, 0);
  assert.equal(h.state.size, 0);
  assert.deepEqual(h.calls.constructors, [{ accessToken: "server-token-private" }]);
  assert.deepEqual(h.calls.reads, [{ projectId: "project-1", playerId: "player-1", keys: [KEY] }]);
});

test("restored session without cached username gets a deterministic valid display name", async () => {
  const h = harness();
  const first = await h.run({ operation: "load" });
  const second = await h.run({ operation: "load" });
  assert.equal(first.profile.username, "Player_player1");
  assert.deepEqual(first, second);
  assert.equal(h.calls.writes.length, 0);
});

test("first save creates revision 1 without a fictional writeLock", async () => {
  const h = harness();
  const request = save();
  request.appearance = { outfitId: 2, faceId: 4, clothColorId: 4, hairColorId: 3, hat: false, accessories: false };
  request.loadout = { primaryWeaponId: 2, primarySightId: 5, sidearmSightId: 3 };
  const result = await h.run(request);
  assert.equal(result.ok, true);
  assert.equal(result.profile.revision, 1);
  assert.deepEqual(result.profile.appearance, request.appearance);
  assert.deepEqual(result.profile.loadout, request.loadout);
  assert.equal(result.profile.match, null);
  assert.equal(Object.prototype.hasOwnProperty.call(h.calls.writes[0].body, "writeLock"), false);
  assert.deepEqual(h.state.get("project-1/player-1").value, result.profile);
});

test("existing save and avatar use fresh real locks and preserve unrelated fields", async () => {
  const h = harness();
  const initial = profile("player-1", 7);
  initial.avatarId = 9;
  h.seed(initial);
  const saved = await h.run({ ...save(7), username: "different_name" });
  assert.equal(saved.ok, true);
  assert.equal(saved.profile.username, "Account_1");
  assert.equal(saved.profile.avatarId, 9);
  assert.equal(saved.profile.revision, 8);
  assert.equal(h.calls.writes[0].body.writeLock, "seed-lock");
  const avatar = await h.run({ operation: "avatar", revision: 8, avatarId: 31 });
  assert.equal(avatar.ok, true);
  assert.equal(avatar.profile.avatarId, 31);
  assert.equal(avatar.profile.revision, 9);
  assert.deepEqual(avatar.profile.appearance, saved.profile.appearance);
  assert.deepEqual(avatar.profile.loadout, saved.profile.loadout);
  assert.equal(h.calls.writes[1].body.writeLock, "lock-1");
});

test("avatar may create the first save and load never mutates an existing profile", async () => {
  const h = harness();
  const avatar = await h.run({ operation: "avatar", username: "User@01", revision: 0, avatarId: 12 });
  assert.equal(avatar.ok, true);
  assert.equal(avatar.profile.username, "User@01");
  assert.equal(avatar.profile.revision, 1);
  const loaded = await h.run({ operation: "load", username: "Renamed" });
  assert.deepEqual(loaded.profile, avatar.profile);
  assert.equal(h.calls.writes.length, 1);
});

test("player identity and storage scope come only from authenticated context", async () => {
  const h = harness();
  h.seed();
  const other = await h.run({ operation: "load", username: "Other_1" }, context("player-2"));
  assert.equal(other.profile.accountId, "player-2");
  assert.equal(other.profile.revision, 0);
  assert.deepEqual(h.state.get("project-1/player-1").value, profile());
  assert.equal(h.calls.writes.length, 0);
});

test("bad authentication or server context is rejected before SDK calls", async t => {
  for (const [name, requestContext, code] of [
    ["no context", undefined, "authentication_required"],
    ["no player", { ...context("player-1"), playerId: "" }, "authentication_required"],
    ["no project", { ...context("player-1"), projectId: "" }, "service_misconfigured"],
    ["no service token", { ...context("player-1"), serviceToken: "" }, "service_misconfigured"]
  ]) {
    await t.test(name, async () => {
      const h = harness();
      // null bypasses the harness default parameter, representing no context.
      const result = await h.run({ operation: "load" }, requestContext === undefined ? null : requestContext);
      assert.equal(result.code, code);
      assert.equal(h.calls.constructors.length, 0);
    });
  }
});

test("invalid request shapes and forged authority fields fail before SDK access", async t => {
  const cases = [
    ["no params", null], ["array params", []], ["unknown operation", { operation: "delete" }],
    ["forged account", { operation: "load", accountId: "victim" }],
    ["forged match", { ...save(), match: { matchId: "injected" } }],
    ["forged token", { operation: "load", serviceToken: "attacker" }],
    ["avatar in save", { ...save(), avatarId: 2 }],
    ["no revision", { operation: "avatar", avatarId: 2 }],
    ["fractional revision", { ...save(), revision: 0.5 }],
    ["negative revision", { ...save(), revision: -1 }],
    ["overflow revision", { ...save(), revision: 2147483648 }],
    ["string revision", { ...save(), revision: "0" }],
    ["non-string username", { operation: "load", username: 123 }]
  ];
  for (const [name, params] of cases) await t.test(name, async () => {
    const h = harness();
    const result = await h.run(params);
    assert.equal(result.ok, false);
    assert.equal(result.code, "invalid_request");
    assert.equal(h.calls.constructors.length, 0);
  });
});

test("new display name obeys UGS ASCII username range", async t => {
  for (const username of ["ab", "a".repeat(21), "中文账号", "has space", "bad/name"]) {
    await t.test(JSON.stringify(username), async () => {
      const h = harness();
      assert.equal((await h.run({ operation: "load", username })).code, "invalid_request");
      assert.equal(h.calls.writes.length, 0);
    });
  }
  const h = harness();
  assert.equal((await h.run({ operation: "load", username: "Ab.09-_@" })).ok, true);
});

test("appearance and loadout reject invalid data instead of silently sanitizing", async t => {
  const cases = [
    ["missing appearance", request => { delete request.appearance; }],
    ["array appearance", request => { request.appearance = []; }],
    ["outfit limit", request => { request.appearance.outfitId = 3; }],
    ["face limit", request => { request.appearance.faceId = 5; }],
    ["cloth limit", request => { request.appearance.clothColorId = 5; }],
    ["hair limit", request => { request.appearance.hairColorId = 4; }],
    ["negative", request => { request.appearance.outfitId = -1; }],
    ["fraction", request => { request.appearance.faceId = 0.5; }],
    ["numeric string", request => { request.appearance.faceId = "0"; }],
    ["hat numeric", request => { request.appearance.hat = 1; }],
    ["accessories numeric", request => { request.appearance.accessories = 0; }],
    ["outfit accessories rule", request => { request.appearance.outfitId = 2; }],
    ["unknown appearance field", request => { request.appearance.coins = 9999; }],
    ["missing loadout", request => { delete request.loadout; }],
    ["weapon unavailable", request => { request.loadout.primaryWeaponId = 1; }],
    ["sight limit", request => { request.loadout.primarySightId = 6; }],
    ["sidearm unavailable", request => { request.loadout.sidearmSightId = 2; }],
    ["unknown loadout field", request => { request.loadout.damage = 9999; }]
  ];
  for (const [name, mutate] of cases) await t.test(name, async () => {
    const h = harness();
    const request = save();
    mutate(request);
    assert.equal((await h.run(request)).code, "invalid_profile");
    assert.equal(h.calls.reads.length, 0);
    assert.equal(h.calls.writes.length, 0);
  });
});

test("avatar accepts only integers 0 through 31", async t => {
  for (const avatarId of [-1, 32, 1.5, "1", null]) await t.test(JSON.stringify(avatarId), async () => {
    const h = harness();
    assert.equal((await h.run({ operation: "avatar", revision: 0, avatarId })).code, "invalid_profile");
    assert.equal(h.calls.writes.length, 0);
  });
});

test("stale or absent expected revisions never overwrite data", async () => {
  const h = harness();
  h.seed(profile("player-1", 5));
  assert.equal((await h.run(save(4))).code, "profile_conflict");
  assert.equal((await h.run({ operation: "avatar", revision: 4, avatarId: 3 })).code, "profile_conflict");
  assert.equal(h.calls.writes.length, 0);
  assert.equal(h.state.get("project-1/player-1").value.revision, 5);
  const empty = harness();
  assert.equal((await empty.run(save(1))).code, "profile_conflict");
  assert.equal(empty.calls.writes.length, 0);
});

test("revision overflow is rejected without writing", async () => {
  const h = harness();
  h.seed(profile("player-1", 2147483647));
  assert.equal((await h.run(save(2147483647))).code, "revision_exhausted");
  assert.equal(h.calls.writes.length, 0);
});

test("corrupted persisted values are never reset or overwritten", async t => {
  const cases = [
    ["wrong account", value => { value.accountId = "victim"; }],
    ["invalid username", value => { value.username = "中文"; }],
    ["negative revision", value => { value.revision = -1; }],
    ["avatar limit", value => { value.avatarId = 32; }],
    ["appearance rule", value => { value.appearance.outfitId = 2; }],
    ["missing loadout", value => { delete value.loadout; }],
    ["forged admission", value => { value.match = { matchId: "bad" }; }],
    ["unknown field", value => { value.currency = 999; }]
  ];
  for (const [name, mutate] of cases) await t.test(name, async () => {
    const h = harness();
    const value = profile();
    mutate(value);
    h.state.set("project-1/player-1", { key: KEY, value: clone(value), writeLock: "saved-lock" });
    const before = clone(h.state.get("project-1/player-1"));
    for (const request of [{ operation: "load" }, save(1), { operation: "avatar", revision: 1, avatarId: 1 }]) {
      assert.equal((await h.run(request)).code, "profile_corrupt");
    }
    assert.equal(h.calls.writes.length, 0);
    assert.deepEqual(h.state.get("project-1/player-1"), before);
  });
});

test("malformed read responses and missing stored locks fail closed", async t => {
  for (const [name, readResponse] of [
    ["no results", { data: {} }],
    ["wrong key", { data: { results: [{ key: "other", value: profile(), writeLock: "a" }] } }],
    ["missing lock", { data: { results: [{ key: KEY, value: profile() }] } }],
    ["empty lock", { data: { results: [{ key: KEY, value: profile(), writeLock: "" }] } }],
    ["duplicate results", { data: { results: [{ key: KEY }, { key: KEY }] } }]
  ]) await t.test(name, async () => {
    const h = harness({ readResponse });
    assert.equal((await h.run(save(1))).code, "service_unavailable");
    assert.equal(h.calls.writes.length, 0);
  });
});

test("SDK errors have stable safe codes and never expose tokens or request text", async t => {
  for (const [status, code] of [[409, "profile_conflict"], [429, "rate_limited"], [403, "service_misconfigured"], [401, "service_misconfigured"], [503, "service_unavailable"]]) {
    await t.test(String(status), async () => {
      const error = new Error("SECRET server-token-private player-token-private request body");
      error.response = { status, data: "SECRET" };
      const h = harness({ writeError: error });
      const result = await h.run(save());
      assert.equal(result.ok, false);
      assert.equal(result.code, code);
      assert.equal(h.calls.writes.length, 1);
      assert.equal(JSON.stringify([result, h.calls.logs]).includes("SECRET"), false);
      assert.equal(JSON.stringify([result, h.calls.logs]).includes("token-private"), false);
      assert.deepEqual(h.calls.logs, [["FpsProfile Cloud Save request failed", { category: code, status }]]);
    });
  }
});

test("malformed write confirmation is reported as uncertain, never retried", async () => {
  const h = harness({ writeResponse: { data: {} } });
  assert.equal((await h.run(save())).code, "service_unavailable");
  assert.equal(h.calls.writes.length, 1);
  assert.equal(h.state.get("project-1/player-1").value.revision, 1);
  // A caller must reload after an uncertain response; another blind create
  // would overwrite a save. The script reads first and uses the real revision.
  assert.equal((await h.run(save(0))).code, "profile_conflict");
  assert.equal(h.calls.writes.length, 1);
});

test("existing cross-device updates use CAS: one wins and conflict is never retried unlocked", async () => {
  const h = harness({ afterSnapshot: gate(2) });
  h.seed(profile("player-1", 5));
  const results = await Promise.all([
    h.run({ operation: "avatar", revision: 5, avatarId: 11 }),
    h.run({ operation: "avatar", revision: 5, avatarId: 12 })
  ]);
  assert.equal(results.filter(value => value.ok).length, 1);
  assert.equal(results.filter(value => value.code === "profile_conflict").length, 1);
  assert.equal(h.calls.writes.length, 2);
  assert.equal(h.calls.writes.every(call => call.body.writeLock === "seed-lock"), true);
  assert.equal(h.state.get("project-1/player-1").value.revision, 6);
});

test("a delayed missing load cannot erase a save created while it waits", async () => {
  let captured;
  let release;
  const snapshotReady = new Promise(resolve => { captured = resolve; });
  const loadWait = new Promise(resolve => { release = resolve; });
  let first = true;
  const h = harness({ afterSnapshot: async () => { if (first) { first = false; captured(); await loadWait; } } });
  const pendingLoad = h.run({ operation: "load", username: "Account_1" });
  await snapshotReady;
  const saved = await h.run({ operation: "avatar", revision: 0, avatarId: 18, username: "Account_1" });
  release();
  const loaded = await pendingLoad;
  assert.equal(loaded.profile.revision, 0);
  assert.equal(saved.ok, true);
  assert.equal(h.state.get("project-1/player-1").value.avatarId, 18);
  assert.equal(h.calls.writes.length, 1);
});

test("documented limitation: simultaneous FIRST saves can both succeed without create-if-absent", async () => {
  const h = harness({ afterSnapshot: gate(2) });
  const results = await Promise.all([
    h.run({ operation: "avatar", revision: 0, avatarId: 11, username: "Account_1" }),
    h.run({ operation: "avatar", revision: 0, avatarId: 12, username: "Account_1" })
  ]);
  // This intentionally proves the boundary of the documented Cloud Save API,
  // rather than claiming that an omitted lock is an atomic create-only write.
  assert.equal(results.every(value => value.ok && value.profile.revision === 1), true);
  assert.equal(h.calls.writes.every(call => !Object.prototype.hasOwnProperty.call(call.body, "writeLock")), true);
  assert.equal(h.state.get("project-1/player-1").value.revision, 1);
  assert.equal([11, 12].includes(h.state.get("project-1/player-1").value.avatarId), true);
});
