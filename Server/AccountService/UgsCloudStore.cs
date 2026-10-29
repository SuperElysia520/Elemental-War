using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Fps.AccountService;

public sealed class UgsBridgeOptions
{
    public bool Enabled { get; init; }
    public string ProjectId { get; init; } = "";
    public string EnvironmentId { get; init; } = "";
    public string EnvironmentName { get; init; } = "development";
    public string ServiceAccountKeyId { get; init; } = "";
    public string ServiceAccountSecretKey { get; init; } = "";
    public string MailboxId { get; init; } = "fps_pvp_bridge_v1";
    public double PollSeconds { get; init; } = 1;

    public void Validate()
    {
        if (!Enabled) return;
        if (!Guid.TryParse(ProjectId, out _) || !Guid.TryParse(EnvironmentId, out _) ||
            !Regex.IsMatch(EnvironmentName, "^[a-zA-Z0-9_-]{1,64}$", RegexOptions.CultureInvariant) ||
            MailboxId != "fps_pvp_bridge_v1" || PollSeconds is < 0.5 or > 10 || !double.IsFinite(PollSeconds) ||
            !SecretPart(ServiceAccountKeyId) || !SecretPart(ServiceAccountSecretKey))
            throw new InvalidOperationException("UGS bridge configuration is invalid; check IDs, environment, credentials and poll interval.");
    }

    private static bool SecretPart(string value) => value.Length is >= 8 and <= 1024 &&
        value.All(c => c is >= '!' and <= '~' && c != ':');
}

public sealed record UgsCloudItem(string Key, JsonElement Value, string WriteLock);
public sealed record UgsCloudPage(UgsCloudItem[] Results, string? After);
public sealed class UgsCloudException(int statusCode) : Exception("UGS Cloud Save request failed.")
{
    public int StatusCode { get; } = statusCode;
}

public interface IUgsCloudStore
{
    Task<UgsCloudItem?> GetPrivateAsync(string key, CancellationToken cancel);
    Task<UgsCloudPage> ListPrivateAsync(string? after, CancellationToken cancel);
    Task<string> SetPrivateAsync(string key, object value, string? writeLock, CancellationToken cancel);
    Task DeletePrivateAsync(string key, string writeLock, CancellationToken cancel);
    Task<JsonElement?> GetProtectedProfileAsync(string playerId, CancellationToken cancel);
}

/// <summary>Fixed official HTTPS destinations; credentials never reach client or Rainyun public HTTP.</summary>
public sealed class UgsCloudStore(HttpClient http, UgsBridgeOptions options, TimeProvider clock) : IUgsCloudStore
{
    private const string CloudHost = "https://cloud-save.services.api.unity.com";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly SemaphoreSlim tokenGate = new(1, 1);
    private string token = "";
    private DateTimeOffset tokenExpires;
    private string PrivatePath => $"/v1/data/projects/{options.ProjectId}/custom/{options.MailboxId}/private/items";

    public async Task<UgsCloudItem?> GetPrivateAsync(string key, CancellationToken cancel)
    {
        using JsonDocument doc = await SendAsync(HttpMethod.Get, PrivatePath + "?keys=" + Uri.EscapeDataString(key), null, cancel);
        UgsCloudItem[] rows = ReadItems(doc.RootElement);
        if (rows.Length == 0) return null;
        if (rows.Length != 1 || rows[0].Key != key) throw new UgsCloudException(502);
        return rows[0];
    }

    public async Task<UgsCloudPage> ListPrivateAsync(string? after, CancellationToken cancel)
    {
        string path = PrivatePath + (after == null ? "" : "?after=" + Uri.EscapeDataString(after));
        using JsonDocument doc = await SendAsync(HttpMethod.Get, path, null, cancel);
        UgsCloudItem[] rows = ReadItems(doc.RootElement);
        string? next = null;
        if (doc.RootElement.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Object &&
            links.TryGetProperty("next", out var link) && link.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(link.GetString()))
        {
            // Never follow a server-provided URL with our bearer token. Paging uses
            // only the last returned key on the same fixed official endpoint.
            if (rows.Length == 0) throw new UgsCloudException(502);
            next = rows[^1].Key;
        }
        return new(rows, next);
    }

    public async Task<string> SetPrivateAsync(string key, object value, string? writeLock, CancellationToken cancel)
    {
        using JsonDocument doc = await SendAsync(HttpMethod.Post, PrivatePath, new { key, value, writeLock }, cancel);
        if (!doc.RootElement.TryGetProperty("writeLock", out var saved) || saved.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(saved.GetString())) throw new UgsCloudException(502);
        return saved.GetString()!;
    }

    public async Task DeletePrivateAsync(string key, string writeLock, CancellationToken cancel)
    {
        if (string.IsNullOrEmpty(writeLock)) throw new InvalidOperationException("A real Cloud Save write lock is required.");
        using JsonDocument _ = await SendAsync(HttpMethod.Delete, PrivatePath + "/" + Uri.EscapeDataString(key) +
            "?writeLock=" + Uri.EscapeDataString(writeLock), null, cancel);
    }

    public async Task<JsonElement?> GetProtectedProfileAsync(string playerId, CancellationToken cancel)
    {
        string path = $"/v1/data/projects/{options.ProjectId}/players/{Uri.EscapeDataString(playerId)}/protected/items?keys=fps_profile_v1";
        using JsonDocument doc = await SendAsync(HttpMethod.Get, path, null, cancel);
        UgsCloudItem[] rows = ReadItems(doc.RootElement);
        if (rows.Length == 0) return null;
        if (rows.Length != 1 || rows[0].Key != "fps_profile_v1") throw new UgsCloudException(502);
        return rows[0].Value;
    }

    private static UgsCloudItem[] ReadItems(JsonElement value)
    {
        if (!value.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            throw new UgsCloudException(502);
        return results.EnumerateArray().Select(item =>
        {
            if (!item.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("value", out var data) ||
                !item.TryGetProperty("writeLock", out var itemLock) || itemLock.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(itemLock.GetString())) throw new UgsCloudException(502);
            return new UgsCloudItem(key.GetString()!, data.Clone(), itemLock.GetString()!);
        }).ToArray();
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancel)
    {
        // ResponseHeadersRead ends HttpClient.Timeout at the headers. Bound the
        // whole operation, including token waits, response bodies and one retry.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        TimeSpan timeout = http.Timeout == Timeout.InfiniteTimeSpan || http.Timeout > TimeSpan.FromSeconds(10)
            ? TimeSpan.FromSeconds(10) : http.Timeout;
        deadline.CancelAfter(timeout);
        cancel = deadline.Token;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            string bearer = await GetTokenAsync(cancel);
            using var request = new HttpRequestMessage(method, CloudHost + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            if (body != null) request.Content = JsonContent.Create(body, options: Json);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            { token = ""; tokenExpires = default; continue; }
            if (!response.IsSuccessStatusCode) throw new UgsCloudException((int)response.StatusCode);
            if (method == HttpMethod.Delete || response.StatusCode == HttpStatusCode.NoContent)
                return JsonDocument.Parse("{}");
            // Do not include raw SDK/server bodies or exception messages in logs.
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancel);
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancel);
            }
            catch (JsonException) { throw new UgsCloudException(502); }
        }
        throw new UgsCloudException(401);
    }

    private async Task<string> GetTokenAsync(CancellationToken cancel)
    {
        await tokenGate.WaitAsync(cancel);
        try
        {
            if (token.Length > 0 && tokenExpires > clock.GetUtcNow()) return token;
            using var request = new HttpRequestMessage(HttpMethod.Post,
                "https://services.api.unity.com/auth/v2/token-exchange?projectId=" + options.ProjectId + "&environmentId=" + options.EnvironmentId);
            string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(options.ServiceAccountKeyId + ":" + options.ServiceAccountSecretKey));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            request.Content = JsonContent.Create(new { });
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
            if (!response.IsSuccessStatusCode) throw new UgsCloudException((int)response.StatusCode);
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancel);
                using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancel);
                if (!doc.RootElement.TryGetProperty("accessToken", out var access) || access.ValueKind != JsonValueKind.String ||
                    string.IsNullOrEmpty(access.GetString())) throw new UgsCloudException(502);
                token = access.GetString()!;
                tokenExpires = clock.GetUtcNow().AddMinutes(50); // Official service-token TTL is one hour.
                return token;
            }
            catch (JsonException) { throw new UgsCloudException(502); }
        }
        finally { tokenGate.Release(); }
    }
}
