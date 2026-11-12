using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Fps.AccountService;

public sealed record UgsPvpLease(int Version, string Epoch, long ExpiresAt);
public sealed record UgsPvpResponse(int Status, string Body);
public sealed record UgsPvpSlot(int Version, string Epoch, string PlayerId, string RequestId, string Username,
    string Operation, JsonElement Args, long CreatedAt, long ExpiresAt, string State, UgsPvpResponse? Response);

/// <summary>One Rainyun authority consumes Cloud Code-authenticated per-player mailboxes.</summary>
public sealed class UgsPvpWorker(UgsBridgeOptions options, IUgsCloudStore cloud, UgsProfiles profiles,
    UgsTicketStore tickets, PartyStore parties, TimeProvider clock, ILogger<UgsPvpWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly Dictionary<string, (string Fingerprint, UgsPvpResponse Response, long Expires)> completed = new(StringComparer.Ordinal);
    private long renewAt;
    private long refreshAt;
    private long leaseExpiresAt;
    private volatile bool isReady;
    private volatile bool isStopping;
    public string Epoch { get; } = Guid.NewGuid().ToString("N");
    // Ownership gates work; expiry only gates health. An expired lease must
    // still be allowed to renew after a temporary cloud failure.
    public bool IsReady { get => isReady && !isStopping; private set => isReady = value; }
    public bool HasLiveLease => IsReady && Interlocked.Read(ref leaseExpiresAt) > Now;
    public double LeaseSecondsRemaining => Math.Max(0, (Interlocked.Read(ref leaseExpiresAt) - Now) / 1000.0);
    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();

    public async Task InitializeAsync(CancellationToken cancel)
    {
        UgsCloudItem? existing = await cloud.GetPrivateAsync("bridge", cancel);
        if (existing != null)
        {
            UgsPvpLease prior = ReadLease(existing.Value);
            if (prior.ExpiresAt > Now && prior.Epoch != Epoch)
                throw new InvalidOperationException("Another live UGS PvP bridge owns this environment. Wait for its lease to expire before starting.");
        }
        long expiresAt = Now + 15000;
        await cloud.SetPrivateAsync("bridge", new UgsPvpLease(1, Epoch, expiresAt), existing?.WriteLock, cancel);
        cancel.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref leaseExpiresAt, expiresAt);
        renewAt = Math.Min(Now + 5000, expiresAt - 5000);
        IsReady = true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Acquiring the cloud lease must not prevent Kestrel from starting, nor
        // crash the host on one TLS reset. Health stays 503 until a confirmed
        // lease write succeeds. Keep this separate from polling: a worker that
        // later loses ownership must fail rather than reacquire another epoch.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await InitializeAsync(stoppingToken);
                logger.LogInformation("UGS bridge acquired its initial lease.");
                break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (HttpRequestException)
            { logger.LogWarning("UGS bridge startup network unavailable; retrying in 5 seconds."); }
            catch (OperationCanceledException)
            { logger.LogWarning("UGS bridge startup request timed out; retrying in 5 seconds."); }
            catch (UgsCloudException error) when (error.StatusCode is 408 or 429 or >= 500 and <= 599)
            { logger.LogWarning("UGS bridge startup unavailable; HTTP status {Status}; retrying in 5 seconds.", error.StatusCode); }
            catch (UgsCloudException error)
            {
                logger.LogError("UGS bridge startup refused; HTTP status {Status}.", error.StatusCode);
                throw;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.PollSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await ProcessOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (UgsCloudException error) { logger.LogWarning("UGS bridge poll unavailable; HTTP status {Status}.", error.StatusCode); }
            catch (HttpRequestException) { logger.LogWarning("UGS bridge network unavailable."); }
            catch (OperationCanceledException) { logger.LogWarning("UGS bridge request timed out."); }
        }
    }

    public async Task ProcessOnceAsync(CancellationToken cancel)
    {
        if (!IsReady) throw new InvalidOperationException("UGS bridge has not acquired its lease.");
        await RenewLeaseAsync(cancel);
        foreach (string key in completed.Where(x => x.Value.Expires <= Now).Select(x => x.Key).ToArray()) completed.Remove(key);
        string? after = null;
        for (int page = 0; page < 101; page++)
        {
            UgsCloudPage items = await cloud.ListPrivateAsync(after, cancel);
            foreach (UgsCloudItem item in items.Results)
            {
                if (!item.Key.StartsWith("p_", StringComparison.Ordinal)) continue;
                await RenewLeaseAsync(cancel);
                await ProcessItemAsync(item, cancel);
            }
            if (items.After == null) break;
            if (page == 100 || items.After == after) throw new UgsCloudException(502);
            after = items.After;
        }
        if (Now >= refreshAt)
        {
            // Cloud IO happens outside PartyStore's gate. Admission checks also
            // reload the authoritative profile immediately before a player joins.
            await profiles.RefreshAsync(parties.ActivePlayerIds(), cancel);
            refreshAt = Now + 5000;
        }
    }

    private async Task ProcessItemAsync(UgsCloudItem item, CancellationToken cancel)
    {
        UgsPvpSlot? slot;
        try { slot = item.Value.Deserialize<UgsPvpSlot>(Json); }
        catch (JsonException) { return; }
        if (slot == null || slot.Version != 1 || slot.PlayerId == null || !UgsProfiles.IsPlayerId(slot.PlayerId) ||
            item.Key != "p_" + slot.PlayerId || !HexId(slot.Epoch) || !HexId(slot.RequestId) ||
            slot.Username == null || !UgsProfiles.IsUsername(slot.Username) || slot.CreatedAt <= 0 || slot.CreatedAt > Now + 5000 ||
            slot.ExpiresAt - slot.CreatedAt != 30000 || slot.Args.ValueKind != JsonValueKind.Object ||
            slot.State is not ("pending" or "done")) return;
        if (slot.Epoch != Epoch || slot.ExpiresAt <= Now)
        {
            try { await cloud.DeletePrivateAsync(item.Key, item.WriteLock, cancel); }
            catch (UgsCloudException error) when (error.StatusCode is 404 or 409) { }
            return;
        }
        if (slot.State != "pending" || slot.Response != null) return;
        string identity = slot.PlayerId + "/" + slot.RequestId;
        string fingerprint = slot.Username + "\n" + slot.Operation + "\n" + slot.Args.GetRawText();
        UgsPvpResponse response;
        if (completed.TryGetValue(identity, out var previous))
            response = previous.Fingerprint == fingerprint ? previous.Response : Error(400, "request_reused", "请求编号已用于其他操作。");
        else
        {
            try
            {
                if (completed.Count >= 5000) throw new ApiException(503, "request_limit", "匹配请求过多，请稍后重试。");
                ValidateCommand(slot);
                Profile actor = await profiles.LoadAsync(slot.PlayerId, slot.Username, cancel);
                await RenewLeaseAsync(cancel);
                if (slot.ExpiresAt <= Now) throw new ApiException(408, "request_expired", "匹配请求已超时，请重新操作。");
                if (slot.Operation == "queue")
                {
                    await profiles.RefreshAsync(parties.PartyPlayerIds(actor.AccountId), cancel);
                    parties.RefreshProfiles(profiles.GetCached, skipUnavailable: true);
                }
                response = ExecuteCommand(slot, actor);
            }
            catch (ApiException error) { response = Error(error.StatusCode, error.Code, error.Message); }
            catch (UgsCloudException) { response = Error(503, "cloud_unavailable", "云端角色资料暂不可用，请稍后重试。"); }
            catch (HttpRequestException) { response = Error(503, "cloud_unavailable", "云端角色资料暂不可用，请稍后重试。"); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                logger.LogWarning("UGS bridge command failed: {Type}.", error.GetType().Name);
                response = Error(500, "server_error", "匹配服务暂不可用，请稍后重试。");
            }
            // The body is itself a JSON string, so quotes/backslashes expand a
            // second time in the stored slot. Bound the full on-wire value.
            if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(slot with { State = "done", Response = response }, Json)) > 65536)
                response = Error(503, "response_limit", "房间数据过多，请稍后刷新。");
            if (completed.Count < 5000) completed.Add(identity, (fingerprint, response, Now + 60000));
        }
        try
        {
            // A failed CAS must never overwrite a newer command from this player.
            await cloud.SetPrivateAsync(item.Key, slot with { State = "done", Response = response }, item.WriteLock, cancel);
        }
        catch (UgsCloudException error) when (error.StatusCode == 409) { }
    }

    private UgsPvpResponse ExecuteCommand(UgsPvpSlot slot, Profile actor)
    {
        object value = slot.Operation switch
        {
            "current" => parties.Current(actor),
            "rooms" => new PartyList(parties.List(actor).Rooms.Take(50).ToArray()),
            "create" => parties.Create(actor),
            "join" => parties.Join(actor, slot.Args.GetProperty("partyId").GetString()),
            "ready" => parties.Ready(actor, slot.Args.GetProperty("ready").GetBoolean()),
            "queue" => parties.Queue(actor),
            "cancel" => parties.CancelQueue(actor),
            "leave" => parties.Leave(actor),
            "fail" => parties.Fail(actor),
            "ticket" => tickets.Create(actor, parties.Admission(actor)),
            "game-ticket" => tickets.Create(actor, null),
            _ => throw new ApiException(400, "invalid_request", "无效的匹配操作。")
        };
        return new(200, JsonSerializer.Serialize(value, value.GetType(), Json));
    }

    private static void ValidateCommand(UgsPvpSlot slot)
    {
        if (slot.Args.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(slot.Args.GetRawText()) > 4096)
            throw new ApiException(400, "invalid_request", "匹配请求格式不正确。");
        string[] keys = slot.Args.EnumerateObject().Select(p => p.Name).ToArray();
        bool valid = slot.Operation switch
        {
            "join" => keys.Length == 1 && keys[0] == "partyId" && slot.Args.GetProperty("partyId").ValueKind == JsonValueKind.String && HexId(slot.Args.GetProperty("partyId").GetString()),
            "ready" => keys.Length == 1 && keys[0] == "ready" && slot.Args.GetProperty("ready").ValueKind is JsonValueKind.True or JsonValueKind.False,
            "current" or "rooms" or "create" or "queue" or "cancel" or "leave" or "fail" or "ticket" or "game-ticket" => keys.Length == 0,
            _ => false
        };
        if (!valid) throw new ApiException(400, "invalid_request", "匹配请求格式不正确。");
    }

    private async Task RenewLeaseAsync(CancellationToken cancel)
    {
        if (Now < renewAt) return;
        UgsCloudItem? item = await cloud.GetPrivateAsync("bridge", cancel);
        if (item == null || ReadLease(item.Value).Epoch != Epoch)
        {
            IsReady = false;
            throw new InvalidOperationException("UGS bridge lease ownership changed; refusing to process commands.");
        }
        long expiresAt = Now + 15000;
        await cloud.SetPrivateAsync("bridge", new UgsPvpLease(1, Epoch, expiresAt), item.WriteLock, cancel);
        Interlocked.Exchange(ref leaseExpiresAt, expiresAt);
        renewAt = Math.Min(Now + 5000, expiresAt - 5000);
    }

    private static UgsPvpLease ReadLease(JsonElement value)
    {
        UgsPvpLease? lease;
        try { lease = value.Deserialize<UgsPvpLease>(Json); }
        catch (JsonException) { throw new InvalidOperationException("UGS bridge lease is corrupt; refusing to overwrite it."); }
        if (lease == null || lease.Version != 1 || !HexId(lease.Epoch) || lease.ExpiresAt < 0)
            throw new InvalidOperationException("UGS bridge lease is corrupt; refusing to overwrite it.");
        return lease;
    }

    private static bool HexId(string? value) => value != null && Regex.IsMatch(value, "^[a-fA-F0-9]{32}$", RegexOptions.CultureInvariant);
    private static UgsPvpResponse Error(int status, string code, string message) => new(status, JsonSerializer.Serialize(new ApiError(code, message), Json));

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        bool ownedLease = IsReady;
        isStopping = true;
        IsReady = false;
        await base.StopAsync(cancellationToken);
        if (!ownedLease) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            UgsCloudItem? item = await cloud.GetPrivateAsync("bridge", deadline.Token);
            if (item != null && ReadLease(item.Value).Epoch == Epoch)
                await cloud.SetPrivateAsync("bridge", new UgsPvpLease(1, Epoch, 0), item.WriteLock, deadline.Token);
        }
        catch (Exception) { /* A crashed/offline worker's lease expires in 15 seconds. */ }
    }
}

public static class UgsPvpEndpoints
{
    public static void MapAccountHealthEndpoint(this WebApplication app)
    {
        app.MapGet("/health", (UgsBridgeOptions options, IServiceProvider services) =>
        {
            if (!options.Enabled)
                return Results.Json(new { status = "ok", service = "fps-account", apiVersion = 2 });
            UgsPvpWorker worker = services.GetRequiredService<UgsPvpWorker>();
            bool ready = worker.HasLiveLease;
            return Results.Json(new
            {
                status = ready ? "ok" : "degraded", service = "fps-account", apiVersion = 2,
                ugsBridgeEnabled = true, ugsBridgeReady = ready,
                ugsLeaseSecondsRemaining = worker.LeaseSecondsRemaining,
                ugsEnvironment = options.EnvironmentName
            }, statusCode: ready ? 200 : 503);
        });
    }

    public static void MapUgsPvpEndpoints(this WebApplication app)
    {
        var local = app.MapGroup("/api/ugs");
        local.AddEndpointFilter(async (context, next) =>
        {
            var address = context.HttpContext.Connection.RemoteIpAddress;
            if (address == null || !System.Net.IPAddress.IsLoopback(address))
                return Results.Json(new ApiError("local_server_only", "此接口仅供同机游戏服务器使用。"), statusCode: 403);
            return await next(context);
        });
        local.MapPost("/game-ticket/redeem", async (RedeemTicketRequest body, UgsTicketStore tickets, HttpContext context) =>
            await tickets.RedeemAsync(body.Ticket, context.RequestAborted));
        local.MapPost("/career/result", async (CareerMatchResult body, HttpContext context) =>
        {
            await context.RequestServices.GetRequiredService<UgsCareerStore>().AcceptAsync(body, context.RequestAborted);
            return Results.NoContent();
        });
        local.MapGet("/matches/server/{port:int}", (int port, PartyStore parties) => parties.Reservation(port));
        local.MapPost("/matches/server", (MatchServerEvent body, PartyStore parties) =>
        { parties.ServerEvent(body); return Results.NoContent(); });
    }
}
