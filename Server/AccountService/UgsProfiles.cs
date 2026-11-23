using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fps.AccountService;

public sealed class UgsProfiles(IUgsCloudStore cloud, TimeProvider clock)
{
    private readonly object gate = new();
    private readonly Dictionary<string, (Profile Profile, string Username, DateTimeOffset LoadedAt)> cache = new(StringComparer.Ordinal);
    private readonly HashSet<string> invalid = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim[] readers = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static bool IsPlayerId(string value) => Regex.IsMatch(value, "^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);
    public static bool IsUsername(string value) => Regex.IsMatch(value, "^[A-Za-z0-9.@_-]{3,20}$", RegexOptions.CultureInvariant);

    public Task<Profile> LoadForSocialDisplayAsync(string playerId, string username, CancellationToken cancel)
    {
        lock (gate)
        {
            if (!invalid.Contains(playerId) && cache.TryGetValue(playerId, out var entry) &&
                entry.Username == username && entry.LoadedAt.AddSeconds(30) > clock.GetUtcNow())
                return Task.FromResult(entry.Profile);
        }
        return LoadAsync(playerId, username, cancel);
    }

    public async Task<Profile> LoadAsync(string playerId, string username, CancellationToken cancel)
    {
        if (!IsPlayerId(playerId) || !IsUsername(username)) throw new ApiException(400, "invalid_profile", "玩家资料参数无效。");
        var reader = readers[(uint)StringComparer.Ordinal.GetHashCode(playerId) % readers.Length];
        await reader.WaitAsync(cancel);
        try { return await ReadProfileAsync(playerId, username, cancel); }
        finally { reader.Release(); }
    }

    private async Task<Profile> ReadProfileAsync(string playerId, string username, CancellationToken cancel)
    {
        var data = await cloud.GetProtectedProfileAsync(playerId, cancel);
        Profile value;
        try
        {
            value = data.Profile == null ? new(playerId, username, 0, new Appearance(), new Loadout()) : Parse(data.Profile.Value, playerId);
            if (data.Personal is JsonElement personal)
            {
                RequireFields(personal, "revision", "personal");
                if (!Integer(personal.GetProperty("revision"), int.MaxValue)) throw new JsonException("Invalid personal revision.");
                var details = personal.GetProperty("personal");
                RequireFields(details, "displayName", "gender", "signature");
                if (details.GetProperty("displayName").ValueKind != JsonValueKind.String || details.GetProperty("signature").ValueKind != JsonValueKind.String ||
                    !Integer(details.GetProperty("gender"), 2)) throw new JsonException("Invalid personal details.");
                var parsed = details.Deserialize<PersonalProfile>(Json);
                if (parsed == null || !parsed.IsValid) throw new JsonException("Invalid personal details.");
                value = value with { Personal = parsed, PersonalRevision = personal.GetProperty("revision").GetInt32() };
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException or ApiException)
        {
            lock (gate) invalid.Add(playerId);
            throw new ApiException(409, "profile_corrupt", "云端存档格式异常，已停止匹配，请联系管理员。");
        }
        lock (gate) { cache[playerId] = (value, value.Username, clock.GetUtcNow()); invalid.Remove(playerId); }
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

    public Task RefreshAsync(IEnumerable<string> playerIds, CancellationToken cancel, int concurrency = 1)
    {
        return Parallel.ForEachAsync(playerIds.Distinct(StringComparer.Ordinal),
            new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = cancel }, async (playerId, token) =>
        {
            string? username;
            lock (gate) username = cache.TryGetValue(playerId, out var entry) ? entry.Username : null;
            if (username == null) return;
            try { await LoadAsync(playerId, username, token); }
            catch (Exception error) when (error is ApiException or UgsCloudException or HttpRequestException ||
                (error is OperationCanceledException && !cancel.IsCancellationRequested))
            {
                // One unavailable/corrupt profile blocks its own party, rather
                // than aborting refresh/matchmaking for every other party.
                lock (gate) invalid.Add(playerId);
            }
        });
    }

    public static Profile Parse(JsonElement value, string expectedId)
    {
        RequireFields(value, "accountId", "username", "revision", "avatarId", "appearance", "loadout", "match");
        if (value.GetProperty("accountId").ValueKind != JsonValueKind.String || value.GetProperty("accountId").GetString() != expectedId ||
            value.GetProperty("username").ValueKind != JsonValueKind.String || !IsUsername(value.GetProperty("username").GetString()!) ||
            !Integer(value.GetProperty("revision"), int.MaxValue) || !Integer(value.GetProperty("avatarId"), 31) ||
            value.GetProperty("match").ValueKind != JsonValueKind.Null) throw new JsonException("Invalid profile.");
        JsonElement appearance = value.GetProperty("appearance"), loadout = value.GetProperty("loadout");
        if (appearance.TryGetProperty("wardrobeItems", out var items))
        {
            RequireFields(appearance, "outfitId", "faceId", "clothColorId", "hairColorId", "hat", "accessories", "wardrobeItems", "wardrobeColors");
            if (!Integer(items, 131071) || !Integer(appearance.GetProperty("wardrobeColors"), 16777215)) throw new JsonException("Invalid wardrobe.");
        }
        else RequireFields(appearance, "outfitId", "faceId", "clothColorId", "hairColorId", "hat", "accessories");
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
