using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fps.AccountService;

public sealed class UgsProfiles(IUgsCloudStore cloud)
{
    private readonly object gate = new();
    private readonly Dictionary<string, (Profile Profile, string Username)> cache = new(StringComparer.Ordinal);
    private readonly HashSet<string> invalid = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static bool IsPlayerId(string value) => Regex.IsMatch(value, "^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);
    public static bool IsUsername(string value) => Regex.IsMatch(value, "^[A-Za-z0-9.@_-]{3,20}$", RegexOptions.CultureInvariant);

    public async Task<Profile> LoadAsync(string playerId, string username, CancellationToken cancel)
    {
        if (!IsPlayerId(playerId) || !IsUsername(username)) throw new ApiException(400, "invalid_profile", "玩家资料参数无效。");
        JsonElement? data = await cloud.GetProtectedProfileAsync(playerId, cancel);
        Profile value;
        try { value = data == null ? new(playerId, username, 0, new Appearance(), new Loadout()) : Parse(data.Value, playerId); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException or ApiException)
        {
            lock (gate) invalid.Add(playerId);
            throw new ApiException(409, "profile_corrupt", "云端存档格式异常，已停止匹配，请联系管理员。");
        }
        lock (gate) { cache[playerId] = (value, value.Username); invalid.Remove(playerId); }
        return value;
    }

    public Profile GetCached(string playerId)
    {
        lock (gate)
        {
            if (!invalid.Contains(playerId) && cache.TryGetValue(playerId, out var entry)) return entry.Profile;
            throw new ApiException(409, "profile_unavailable", "角色资料暂不可用，请重新读取。");
        }
    }

    public async Task RefreshAsync(IEnumerable<string> playerIds, CancellationToken cancel)
    {
        foreach (string playerId in playerIds.Distinct(StringComparer.Ordinal))
        {
            string? username;
            lock (gate) username = cache.TryGetValue(playerId, out var entry) ? entry.Username : null;
            if (username == null) continue;
            try { await LoadAsync(playerId, username, cancel); }
            catch (Exception error) when (error is ApiException or UgsCloudException or HttpRequestException ||
                (error is OperationCanceledException && !cancel.IsCancellationRequested))
            {
                // One unavailable/corrupt profile blocks its own party, rather
                // than aborting refresh/matchmaking for every other party.
                lock (gate) invalid.Add(playerId);
            }
        }
    }

    public static Profile Parse(JsonElement value, string expectedId)
    {
        RequireFields(value, "accountId", "username", "revision", "avatarId", "appearance", "loadout", "match");
        if (value.GetProperty("accountId").ValueKind != JsonValueKind.String || value.GetProperty("accountId").GetString() != expectedId ||
            value.GetProperty("username").ValueKind != JsonValueKind.String || !IsUsername(value.GetProperty("username").GetString()!) ||
            !Integer(value.GetProperty("revision"), int.MaxValue) || !Integer(value.GetProperty("avatarId"), 31) ||
            value.GetProperty("match").ValueKind != JsonValueKind.Null) throw new JsonException("Invalid profile.");
        JsonElement appearance = value.GetProperty("appearance"), loadout = value.GetProperty("loadout");
        RequireFields(appearance, "outfitId", "faceId", "clothColorId", "hairColorId", "hat", "accessories");
        RequireFields(loadout, "primaryWeaponId", "primarySightId", "sidearmSightId");
        if (!Integer(appearance.GetProperty("outfitId"), 2) || !Integer(appearance.GetProperty("faceId"), 4) ||
            !Integer(appearance.GetProperty("clothColorId"), 4) || !Integer(appearance.GetProperty("hairColorId"), 3) ||
            appearance.GetProperty("hat").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            appearance.GetProperty("accessories").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !Integer(loadout.GetProperty("primaryWeaponId"), 2) || !Integer(loadout.GetProperty("primarySightId"), 5) ||
            !Integer(loadout.GetProperty("sidearmSightId"), 3)) throw new JsonException("Invalid profile.");
        Profile profile = value.Deserialize<Profile>(Json) ?? throw new JsonException("Invalid profile.");
        if (!profile.Appearance.IsValid || !profile.Loadout.IsValid) throw new JsonException("Invalid profile.");
        return profile;
    }

    private static bool Integer(JsonElement value, int maximum) => value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int number) && number >= 0 && number <= maximum;
    private static void RequireFields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != names.Length ||
            names.Any(name => !value.TryGetProperty(name, out _))) throw new JsonException("Invalid profile schema.");
    }
}
