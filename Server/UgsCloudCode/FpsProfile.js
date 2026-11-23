// Publish this file as the UGS Cloud Code JavaScript script "FpsProfile".
// Configure the parameter table as well when publishing through the Dashboard.
const { DataApi } = require("@unity-services/cloud-save-1.4");

const PROFILE_KEY = "fps_profile_v1";
const PERSONAL_KEY = "fps_personal_v1";
const MAX_REVISION = 2147483647;
const USERNAME = /^[A-Za-z0-9.@_-]{3,20}$/;
const MESSAGES = {
  authentication_required: "请先登录账号。",
  invalid_request: "存档请求参数不正确。",
  invalid_profile: "外观、配装或头像参数不正确。",
  profile_conflict: "存档已在其他设备更新，请重新读取后再保存。",
  profile_corrupt: "云端存档格式异常，请联系开发者处理。",
  revision_exhausted: "存档版本已达上限，请联系开发者处理。",
  service_misconfigured: "云存档服务尚未正确配置，请联系开发者。",
  rate_limited: "操作过于频繁，请稍后再试。",
  service_unavailable: "暂时无法连接云存档，请稍后重试。"
};

class ProfileError extends Error {
  constructor(code) {
    super(code);
    this.code = code;
  }
}

function fail(code) { throw new ProfileError(code); }
function object(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}
function fields(value, expected) {
  return object(value) && Object.keys(value).length === expected.length &&
    expected.every(key => Object.prototype.hasOwnProperty.call(value, key));
}
function integer(value, maximum) {
  return Number.isInteger(value) && value >= 0 && value <= maximum;
}
function validAppearance(value) {
  const legacy = ["outfitId", "faceId", "clothColorId", "hairColorId", "hat", "accessories"];
  const extended = fields(value, [...legacy, "wardrobeItems", "wardrobeColors"]);
  if (extended) {
    const { wardrobeItems: items, wardrobeColors: colors } = value;
    if (!integer(items, 131071) || !integer(colors, 16777215)) return false;
    if (items === 0 ? colors !== 0 : items < 65536) return false;
    for (let i = 0; i < 8; i++) {
      if (((items >> (2*i)) & 3) > (i === 3 ? 3 : 1) ||
          ((colors >> (3*i)) & 7) > (i === 0 ? 3 : 4)) return false;
    }
  }
  return (extended || fields(value, legacy)) &&
    integer(value.outfitId, 2) && integer(value.faceId, 4) &&
    integer(value.clothColorId, 4) && integer(value.hairColorId, 3) &&
    typeof value.hat === "boolean" && typeof value.accessories === "boolean" &&
    (value.outfitId !== 2 || !value.accessories);
}
function validLoadout(value) {
  return fields(value, ["primaryWeaponId", "primarySightId", "sidearmSightId"]) &&
    (value.primaryWeaponId === 0 || value.primaryWeaponId === 2) &&
    integer(value.primarySightId, 5) &&
    (value.sidearmSightId === 0 || value.sidearmSightId === 3);
}
function copy(value) { return JSON.parse(JSON.stringify(value)); }

function validPersonal(value) {
  // Keep existing saves readable; newly chosen names use the weighted rule below.
  return fields(value, ["displayName", "gender", "signature"]) && integer(value.gender, 2) &&
    typeof value.displayName === "string" && value.displayName.length <= 20 && value.displayName === value.displayName.trim() &&
    typeof value.signature === "string" && value.signature.length <= 80 &&
    !/[\x00-\x1f\x7f-\x9f<>]/.test(value.displayName + value.signature);
}

function validDisplayName(name) {
  if (typeof name !== "string" || !name.length) return false;
  let length = 0;
  for (const character of name) {
    const code = character.codePointAt(0);
    if ((code >= 65 && code <= 90) || (code >= 97 && code <= 122) || (code >= 48 && code <= 57)) length++;
    else if ((code >= 0x3400 && code <= 0x4DBF) || (code >= 0x4E00 && code <= 0x9FFF) ||
             (code >= 0xF900 && code <= 0xFAFF) || (code >= 0x20000 && code <= 0x2FFFF) ||
             (code >= 0x30000 && code <= 0x3FFFF)) length += 2;
    else return false;
    if (length > 12) return false;
  }
  return true;
}

function defaultProfile(playerId, username) {
  // A restored session may not have a cached account name. This is only a
  // display-name fallback; the authenticated playerId is always the identity.
  if (username === undefined || username === "") {
    const suffix = playerId.replace(/[^A-Za-z0-9]/g, "").slice(0, 12);
    username = suffix ? "Player_" + suffix : "Player";
  }
  if (typeof username !== "string" || !USERNAME.test(username)) fail("invalid_request");
  return {
    accountId: playerId,
    username,
    revision: 0,
    avatarId: 0,
    appearance: { outfitId: 0, faceId: 0, clothColorId: 0, hairColorId: 0, hat: true, accessories: true },
    loadout: { primaryWeaponId: 0, primarySightId: 0, sidearmSightId: 0 },
    match: null
  };
}

function validStoredProfile(value, playerId) {
  return fields(value, ["accountId", "username", "revision", "avatarId", "appearance", "loadout", "match"]) &&
    value.accountId === playerId && typeof value.username === "string" && USERNAME.test(value.username) &&
    integer(value.revision, MAX_REVISION) && integer(value.avatarId, 31) &&
    validAppearance(value.appearance) && validLoadout(value.loadout) && value.match === null;
}

function validateRequest(params) {
  if (!object(params) || !["load", "save", "avatar", "personal"].includes(params.operation)) fail("invalid_request");
  const allowed = ["operation", "username"];
  if (params.operation === "save") allowed.push("revision", "appearance", "loadout");
  if (params.operation === "avatar") allowed.push("revision", "avatarId");
  if (params.operation === "personal") allowed.push("revision", "personal");
  if (Object.keys(params).some(key => !allowed.includes(key))) fail("invalid_request");
  if (params.username !== undefined && typeof params.username !== "string") fail("invalid_request");
  if (params.operation !== "load" && !integer(params.revision, MAX_REVISION)) fail("invalid_request");
  if (params.operation === "save" && (!validAppearance(params.appearance) || !validLoadout(params.loadout))) {
    fail("invalid_profile");
  }
  if (params.operation === "avatar" && !integer(params.avatarId, 31)) fail("invalid_profile");
  if (params.operation === "personal" && !validPersonal(params.personal)) fail("invalid_profile");
}

async function readProfile(api, projectId, playerId) {
  const response = await api.getProtectedItems(projectId, playerId, [PROFILE_KEY]);
  if (!response || !response.data || !Array.isArray(response.data.results)) fail("service_unavailable");
  const results = response.data.results;
  // The query requests exactly one key. Fail closed on malformed SDK responses.
  if (results.length === 0) return null;
  if (results.length !== 1 || !object(results[0]) || results[0].key !== PROFILE_KEY) fail("service_unavailable");
  const item = results[0];
  if (!validStoredProfile(item.value, playerId)) fail("profile_corrupt");
  if (typeof item.writeLock !== "string" || item.writeLock.length === 0) fail("service_unavailable");
  return { profile: copy(item.value), writeLock: item.writeLock };
}

function serviceStatus(error) {
  const status = error && (error.status || (error.response && error.response.status));
  return Number.isInteger(status) ? status : 0;
}

module.exports = async ({ params, context, logger }) => {
  try {
    if (!context || typeof context.playerId !== "string" || !context.playerId.trim()) fail("authentication_required");
    if (typeof context.projectId !== "string" || !context.projectId.trim() ||
        typeof context.serviceToken !== "string" || !context.serviceToken.trim()) fail("service_misconfigured");
    validateRequest(params);
    // Cloud Save Protected writes require server authority. Never use a token
    // or accountId supplied in params, or embed a service-account secret here.
    const api = new DataApi({ accessToken: context.serviceToken });
    const stored = await readProfile(api, context.projectId, context.playerId);
    const profile = stored ? stored.profile : defaultProfile(context.playerId, params.username);
    if (params.operation === "personal") {
      const response = await api.getProtectedItems(context.projectId, context.playerId, [PERSONAL_KEY]);
      const results = response && response.data && response.data.results;
      if (!Array.isArray(results) || results.length > 1) fail("service_unavailable");
      const item = results[0];
      if (item && (!object(item) || item.key !== PERSONAL_KEY || !fields(item.value, ["revision", "personal"]) ||
          !integer(item.value.revision, MAX_REVISION) || !validPersonal(item.value.personal))) fail("profile_corrupt");
      if (item && (typeof item.writeLock !== "string" || !item.writeLock)) fail("service_unavailable");
      const revision = item ? item.value.revision : 0;
      if (params.revision !== revision) fail("profile_conflict");
      if (revision === MAX_REVISION) fail("revision_exhausted");
      const previousName = item ? item.value.personal.displayName : "";
      if (params.personal.displayName !== previousName && !validDisplayName(params.personal.displayName)) fail("invalid_profile");
      const value = { revision: revision + 1, personal: copy(params.personal) };
      const body = { key: PERSONAL_KEY, value };
      if (item) body.writeLock = item.writeLock;
      const saved = await api.setProtectedItem(context.projectId, context.playerId, body);
      if (!saved || !saved.data || typeof saved.data.writeLock !== "string" || !saved.data.writeLock) fail("service_unavailable");
      // Keep the character schema unchanged so existing PvP workers and dedicated servers remain compatible.
      profile.personal = value.personal;
      profile.personalRevision = value.revision;
      return { ok: true, profile };
    }
    if (params.operation === "load") return { ok: true, profile };
    if (params.revision !== profile.revision) fail("profile_conflict");
    if (profile.revision === MAX_REVISION) fail("revision_exhausted");
    if (params.operation === "save") {
      profile.appearance = copy(params.appearance);
      profile.loadout = copy(params.loadout);
    } else {
      profile.avatarId = params.avatarId;
    }
    profile.revision += 1;
    const body = { key: PROFILE_KEY, value: profile };
    if (stored) body.writeLock = stored.writeLock;
    // An absent key must omit writeLock. Cloud Save does not document an
    // atomic create-if-absent lock: simultaneous first saves can both succeed.
    // Existing saves always include the actual lock and never retry unlocked.
    const saved = await api.setProtectedItem(context.projectId, context.playerId, body);
    if (!saved || !saved.data || typeof saved.data.writeLock !== "string" || !saved.data.writeLock) {
      fail("service_unavailable");
    }
    return { ok: true, profile };
  } catch (error) {
    if (error instanceof ProfileError) return { ok: false, code: error.code, message: MESSAGES[error.code] };
    const status = serviceStatus(error);
    const code = status === 409 ? "profile_conflict" : status === 429 ? "rate_limited" :
      status === 401 || status === 403 ? "service_misconfigured" : "service_unavailable";
    // Log only a category and HTTP status. SDK errors can contain bearer tokens
    // and request bodies, so do not log the error object/message/stack.
    if (logger && typeof logger.error === "function") {
      logger.error("FpsProfile Cloud Save request failed", { category: code, status });
    }
    return { ok: false, code, message: MESSAGES[code] };
  }
};

module.exports.params = {
  operation: { type: "String", required: true },
  username: { type: "String", required: false },
  revision: { type: "Numeric", required: false },
  appearance: { type: "JSON", required: false },
  loadout: { type: "JSON", required: false },
  avatarId: { type: "Numeric", required: false },
  personal: { type: "JSON", required: false }
};
