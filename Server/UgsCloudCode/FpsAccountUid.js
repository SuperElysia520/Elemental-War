// Publish as FpsAccountUid. Parameter: operation (String, Required), value "ensure".
// Provision the sequence once in Dashboard before accepting new registrations.
const { DataApi } = require("@unity-services/cloud-save-1.4");
const CUSTOM_ID = "fps_account_uid_v1";
const SEQUENCE_KEY = "sequence";
const UID_KEY = "fps_uid_v1";
const FIRST_UID = 111100001;
const LAST_UID = 999999999;
const PLAYER = /^[A-Za-z0-9_-]{1,64}$/;

class UidError extends Error { constructor(code) { super(code); this.code = code; } }
function fail(code) { throw new UidError(code); }
function object(value) { return value !== null && typeof value === "object" && !Array.isArray(value); }
function fields(value, names) { return object(value) && Object.keys(value).length === names.length && names.every(key => Object.prototype.hasOwnProperty.call(value, key)); }
function validUid(value) { return Number.isInteger(value) && value >= FIRST_UID && value <= LAST_UID; }
function status(error) { return error && (error.status || (error.response && error.response.status)) || 0; }
function rows(response, key) {
  const values = response && response.data && response.data.results;
  if (!Array.isArray(values) || values.length > 1) fail("uid_unavailable");
  if (!values.length) return null;
  if (!object(values[0]) || values[0].key !== key || typeof values[0].writeLock !== "string" || !values[0].writeLock) fail("uid_unavailable");
  return values[0];
}
async function identity(api, projectId, playerId) {
  const item = rows(await api.getProtectedItems(projectId, playerId, [UID_KEY]), UID_KEY);
  if (item && (!fields(item.value, ["version", "playerId", "uid"]) || item.value.version !== 1 ||
      item.value.playerId !== playerId || !validUid(item.value.uid))) fail("uid_corrupt");
  return item;
}
async function sequence(api, projectId) {
  const item = rows(await api.getPrivateCustomItems(projectId, CUSTOM_ID, [SEQUENCE_KEY]), SEQUENCE_KEY);
  // Cloud Save does not offer atomic create-if-absent. Never race to initialize this counter.
  if (!item) fail("uid_setup_required");
  const value = item.value;
  if (!fields(value, ["version", "nextUid", "pending"]) || value.version !== 1 ||
      !Number.isInteger(value.nextUid) || value.nextUid < FIRST_UID || value.nextUid > LAST_UID + 1 ||
      (value.pending !== null && (!fields(value.pending, ["playerId", "uid"]) ||
      typeof value.pending.playerId !== "string" || !PLAYER.test(value.pending.playerId) ||
      !validUid(value.pending.uid) || value.pending.uid !== value.nextUid - 1))) fail("uid_corrupt");
  return item;
}
async function saveSequence(api, projectId, value, writeLock) {
  const result = await api.setPrivateCustomItem(projectId, CUSTOM_ID, { key: SEQUENCE_KEY, value, writeLock });
  if (!result || !result.data || typeof result.data.writeLock !== "string" || !result.data.writeLock) fail("uid_unavailable");
}
async function finishPending(api, projectId, item) {
  const pending = item.value.pending;
  const existing = await identity(api, projectId, pending.playerId);
  if (existing && existing.value.uid !== pending.uid) fail("uid_corrupt");
  if (!existing) {
    // The locked pending reservation is the sole authority for this player's immutable value.
    // Concurrent helpers can only write the same number. No profile fields are overwritten.
    const result = await api.setProtectedItem(projectId, pending.playerId, {
      key: UID_KEY, value: { version: 1, playerId: pending.playerId, uid: pending.uid }
    });
    if (!result || !result.data || typeof result.data.writeLock !== "string" || !result.data.writeLock) fail("uid_unavailable");
  }
  await saveSequence(api, projectId, { version: 1, nextUid: item.value.nextUid, pending: null }, item.writeLock);
}

module.exports = async ({ params, context, logger }) => {
  try {
    if (!context || typeof context.playerId !== "string" || !PLAYER.test(context.playerId)) fail("authentication_required");
    if (typeof context.projectId !== "string" || !context.projectId || typeof context.serviceToken !== "string" || !context.serviceToken) fail("uid_unavailable");
    if (!fields(params, ["operation"]) || params.operation !== "ensure") fail("invalid_request");
    const api = new DataApi({ accessToken: context.serviceToken });
    for (let attempt = 0; attempt < 24; attempt++) {
      const own = await identity(api, context.projectId, context.playerId);
      if (own) return { ok: true, uid: own.value.uid };
      const current = await sequence(api, context.projectId);
      try {
        if (current.value.pending !== null) {
          await finishPending(api, context.projectId, current);
          continue;
        }
        // Pair a fresh identity read with this counter version. The first read
        // may have been delayed while another request finished this same player.
        const confirmed = await identity(api, context.projectId, context.playerId);
        if (confirmed) return { ok: true, uid: confirmed.value.uid };
        if (current.value.nextUid > LAST_UID) fail("uid_exhausted");
        await saveSequence(api, context.projectId, {
          version: 1, nextUid: current.value.nextUid + 1,
          pending: { playerId: context.playerId, uid: current.value.nextUid }
        }, current.writeLock);
        // Re-read the real lock before committing the protected identity. An interrupted
        // reservation is completed by the next request, without wasting or reusing a UID.
      } catch (error) {
        if (status(error) !== 409) throw error;
      }
    }
    fail("uid_busy");
  } catch (error) {
    const code = error instanceof UidError ? error.code : status(error) === 429 ? "uid_busy" : "uid_unavailable";
    if (!(error instanceof UidError) && logger && typeof logger.error === "function")
      logger.error("FpsAccountUid request failed", { category: code, status: status(error) });
    return { ok: false, code };
  }
};
module.exports.params = { operation: { type: "String", required: true } };
