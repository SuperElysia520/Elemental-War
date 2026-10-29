// Publish as the JavaScript Cloud Code script FpsPvp in the same environment
// as FpsProfile and the single Rainyun outbound bridge. No external HTTP calls.
const { DataApi } = require("@unity-services/cloud-save-1.4");

const MAILBOX_ID = "fps_pvp_bridge_v1";
const LEASE_KEY = "bridge";
const TTL = 30000;
const MAX_REQUEST_BYTES = 4096;
const MAX_SLOT_BYTES = 65536;
const HEX_ID = /^[a-fA-F0-9]{32}$/;
const PLAYER_ID = /^[A-Za-z0-9_-]{1,64}$/;
const USERNAME = /^[A-Za-z0-9.@_-]{3,20}$/;
const COMMANDS = new Set(["current", "rooms", "create", "join", "ready", "queue", "cancel", "leave", "fail", "ticket", "game-ticket"]);
const FAILURE_STATUS = {
  auth_required: 401,
  invalid_request: 400,
  request_busy: 409,
  request_expired: 410,
  request_superseded: 409,
  bridge_unavailable: 503,
  bridge_restarted: 409,
  service_unavailable: 503
};

class BridgeError extends Error {
  constructor(code) { super(code); this.code = code; }
}
function fail(code) { throw new BridgeError(code); }
function object(value) { return value !== null && typeof value === "object" && !Array.isArray(value); }
function fields(value, names) {
  return object(value) && Object.keys(value).length === names.length &&
    names.every(name => Object.prototype.hasOwnProperty.call(value, name));
}
function safeTime(value) { return Number.isSafeInteger(value) && value >= 0; }
function validId(value) { return typeof value === "string" && HEX_ID.test(value); }
function copy(value) { return JSON.parse(JSON.stringify(value)); }
function utf8Length(text) {
  let size = 0;
  for (let i = 0; i < text.length; i++) {
    const code = text.charCodeAt(i);
    if (code < 0x80) size++;
    else if (code < 0x800) size += 2;
    else if (code >= 0xd800 && code <= 0xdbff && i + 1 < text.length &&
      text.charCodeAt(i + 1) >= 0xdc00 && text.charCodeAt(i + 1) <= 0xdfff) { size += 4; i++; }
    else size += 3;
  }
  return size;
}
function withinBytes(value, maximum) {
  try { return utf8Length(JSON.stringify(value)) <= maximum; }
  catch { return false; }
}
function validateArgs(operation, args) {
  if (operation === "join") return fields(args, ["partyId"]) && validId(args.partyId);
  if (operation === "ready") return fields(args, ["ready"]) && typeof args.ready === "boolean";
  return fields(args, []);
}
function defaultUsername(playerId) {
  return "Player_" + playerId.replace(/[^A-Za-z0-9]/g, "").slice(0, 12);
}
function validateParams(params, playerId) {
  if (!object(params) || !validId(params.requestId) || !withinBytes(params, MAX_REQUEST_BYTES)) fail("invalid_request");
  if (params.action === "status") {
    if (!fields(params, ["action", "requestId"])) fail("invalid_request");
    return null;
  }
  if (params.action !== "submit" || typeof params.operation !== "string" || !COMMANDS.has(params.operation) ||
    Object.keys(params).some(key => !["action", "requestId", "operation", "username", "args"].includes(key))) fail("invalid_request");
  const username = params.username === undefined ? defaultUsername(playerId) : params.username;
  const args = params.args === undefined ? {} : params.args;
  if (typeof username !== "string" || !USERNAME.test(username) || !validateArgs(params.operation, args)) fail("invalid_request");
  return { operation: params.operation, username, args: copy(args) };
}

async function readItem(api, projectId, key) {
  const response = await api.getPrivateCustomItems(projectId, MAILBOX_ID, [key]);
  if (!response || !response.data || !Array.isArray(response.data.results)) fail("service_unavailable");
  const rows = response.data.results;
  if (rows.length === 0) return null;
  if (rows.length !== 1 || !object(rows[0]) || rows[0].key !== key) fail("service_unavailable");
  return rows[0];
}
function validateLease(item, now) {
  const value = item && item.value;
  if (!fields(value, ["version", "epoch", "expiresAt"]) || value.version !== 1 || !validId(value.epoch) ||
    !safeTime(value.expiresAt) || value.expiresAt <= now) fail("bridge_unavailable");
  return value;
}
function validateSlot(item, playerId) {
  const slot = item && item.value;
  if (!fields(slot, ["version", "epoch", "playerId", "requestId", "username", "operation", "args", "createdAt", "expiresAt", "state", "response"]) ||
    slot.version !== 1 || !validId(slot.epoch) || slot.playerId !== playerId || !validId(slot.requestId) ||
    typeof slot.username !== "string" || !USERNAME.test(slot.username) || !COMMANDS.has(slot.operation) ||
    !validateArgs(slot.operation, slot.args) || !safeTime(slot.createdAt) || !safeTime(slot.expiresAt) ||
    slot.expiresAt !== slot.createdAt + TTL || !withinBytes(slot, MAX_SLOT_BYTES) ||
    typeof item.writeLock !== "string" || !item.writeLock) fail("service_unavailable");
  if (slot.state === "pending") {
    if (slot.response !== null) fail("service_unavailable");
  } else if (slot.state === "done") {
    const response = slot.response;
    if (!fields(response, ["status", "body"]) || !Number.isInteger(response.status) || response.status < 200 || response.status > 599 ||
      typeof response.body !== "string") fail("service_unavailable");
    if (!(response.status === 204 && response.body === "")) {
      try { JSON.parse(response.body); }
      catch { fail("service_unavailable"); }
    }
  } else fail("service_unavailable");
  return slot;
}
function sameCommand(slot, command) {
  return slot.operation === command.operation && slot.username === command.username &&
    JSON.stringify(slot.args) === JSON.stringify(command.args);
}
function ownResult(slot, requestId, epoch, now) {
  if (slot.epoch !== epoch) fail("bridge_restarted");
  if (slot.requestId !== requestId) fail("request_superseded");
  if (slot.expiresAt <= now) fail("request_expired");
  return {
    ok: true, code: "", state: slot.state, requestId,
    body: slot.state === "done" ? slot.response.body : "",
    status: slot.state === "done" ? slot.response.status : 202
  };
}
function httpStatus(error) {
  const status = error && (error.status || (error.response && error.response.status));
  return Number.isInteger(status) ? status : 0;
}

module.exports = async ({ params, context, logger }) => {
  const requestId = params && validId(params.requestId) ? params.requestId : "";
  try {
    if (!context || typeof context.playerId !== "string" || !PLAYER_ID.test(context.playerId)) fail("auth_required");
    if (typeof context.projectId !== "string" || !context.projectId.trim() ||
      typeof context.serviceToken !== "string" || !context.serviceToken.trim()) fail("service_unavailable");
    const playerId = context.playerId;
    const command = validateParams(params, playerId);
    const api = new DataApi({ accessToken: context.serviceToken });
    const lease = validateLease(await readItem(api, context.projectId, LEASE_KEY), Date.now());
    const key = "p_" + playerId;
    const previous = await readItem(api, context.projectId, key);
    const now = Date.now();
    if (lease.expiresAt <= now) fail("bridge_unavailable");
    const existing = previous ? validateSlot(previous, playerId) : null;
    if (params.action === "status") {
      if (!existing) fail("request_expired");
      return ownResult(existing, requestId, lease.epoch, now);
    }
    if (existing && existing.epoch === lease.epoch) {
      if (existing.requestId === requestId) {
        if (!sameCommand(existing, command)) fail("invalid_request");
        return ownResult(existing, requestId, lease.epoch, now);
      }
      if (existing.state === "pending" && existing.expiresAt > now) fail("request_busy");
    }
    const slot = {
      version: 1, epoch: lease.epoch, playerId, requestId, username: command.username,
      operation: command.operation, args: command.args, createdAt: now, expiresAt: now + TTL,
      state: "pending", response: null
    };
    if (!withinBytes(slot, MAX_REQUEST_BYTES)) fail("invalid_request");
    const body = { key, value: slot };
    if (previous) body.writeLock = previous.writeLock;
    const written = await api.setPrivateCustomItem(context.projectId, MAILBOX_ID, body);
    if (!written || !written.data || typeof written.data.writeLock !== "string" || !written.data.writeLock) fail("service_unavailable");
    // Missing-item creation has no documented create-if-absent lock. Another
    // first request may supersede this one; never deliver a different result.
    const confirmed = await readItem(api, context.projectId, key);
    if (!confirmed) fail("request_superseded");
    const current = validateSlot(confirmed, playerId);
    if (current.epoch !== lease.epoch || current.requestId !== requestId || !sameCommand(current, command)) fail("request_superseded");
    if (lease.expiresAt <= Date.now()) fail("bridge_unavailable");
    return ownResult(current, requestId, lease.epoch, Date.now());
  } catch (error) {
    let code;
    if (error instanceof BridgeError) code = error.code;
    else {
      const status = httpStatus(error);
      code = status === 409 ? "request_busy" : "service_unavailable";
      // SDK errors may contain service tokens, payloads or private item values.
      // Emit only fixed categories and the numeric HTTP status.
      if (logger && typeof logger.error === "function") logger.error("FpsPvp Cloud Save request failed", { category: code, status });
    }
    return { ok: false, code, state: "", requestId, body: "", status: FAILURE_STATUS[code] || 503 };
  }
};

module.exports.params = {
  action: { type: "String", required: true },
  requestId: { type: "String", required: true },
  operation: { type: "String", required: false },
  username: { type: "String", required: false },
  args: { type: "JSON", required: false }
};
