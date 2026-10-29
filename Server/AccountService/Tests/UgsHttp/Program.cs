using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fps.AccountService;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

await HttpChecks.Run();

static class HttpChecks
{
    private static int checks;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static void Check(bool condition, string label)
    { if (!condition) throw new InvalidOperationException(label); checks++; Console.WriteLine("PASS " + label); }

    public static async Task Run()
    {
        await RestTransport();
        await ResponseBodyDeadline(tokenBody: true);
        await ResponseBodyDeadline(tokenBody: false);
        await LocalEndpoints();
        await HealthEndpoints();
        await HostedStartupRecovery();
        Console.WriteLine($"UGS HTTP/REST boundary: PASS; {checks} assertions. Cloud transport faked; local HTTP is real.");
    }

    private static async Task RestTransport()
    {
        int tokens = 0, reads = 0, mutations = 0;
        bool unauthorized = false, conflict = false, malformed = false;
        var wire = new FakeHttp(async request =>
        {
            Check(request.RequestUri!.Scheme == "https" && request.RequestUri.Host is "services.api.unity.com" or "cloud-save.services.api.unity.com", "REST sends only to fixed official HTTPS hosts");
            if (request.RequestUri.Host == "services.api.unity.com")
            {
                tokens++;
                Check(request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/auth/v2/token-exchange" &&
                    request.RequestUri.Query.Contains("projectId=") && request.RequestUri.Query.Contains("environmentId="), "Token exchange v2 scopes project and environment UUIDs");
                Check(request.Headers.Authorization?.Scheme == "Basic" && request.Headers.Authorization.Parameter ==
                    Convert.ToBase64String(Encoding.UTF8.GetBytes("fake-key-123:fake-secret-123")), "Service credentials use UTF8 Basic only on token exchange");
                return Reply("{\"accessToken\":\"fake-token-" + tokens + "\"}", 201);
            }
            Check(request.Headers.Authorization?.Scheme == "Bearer", "Cloud Save receives bearer rather than service-account Basic credentials");
            if (request.Method == HttpMethod.Post)
            {
                mutations++;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Check(body.RootElement.GetProperty("writeLock").GetString() == "stored-lock" &&
                    !body.RootElement.GetProperty("value").TryGetProperty("writeLock", out _), "Real writeLock is top-level in Cloud Save mutation body");
                return conflict ? Reply("{\"secret\":\"must never enter exception\"}", 409) : Reply("{\"writeLock\":\"new-lock\"}");
            }
            if (request.Method == HttpMethod.Delete)
            {
                Check(request.RequestUri.Query.Contains("writeLock=stored-lock"), "Delete is conditional on the actual fetched write lock");
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            reads++;
            if (unauthorized) { unauthorized = false; return Reply("{}", 401); }
            if (malformed) return Reply("{\"results\":[{\"key\":\"bridge\",\"value\":{}}]}");
            if (request.RequestUri.AbsolutePath.Contains("/players/"))
            {
                Check(request.RequestUri.AbsolutePath.EndsWith("/protected/items") && request.RequestUri.Query == "?keys=fps_profile_v1", "Profiles read only the Protected bucket and exact profile key");
                return Reply("{\"results\":[{\"key\":\"fps_profile_v1\",\"value\":{\"accountId\":\"player-1\"},\"writeLock\":\"stored-lock\"}]}");
            }
            Check(request.RequestUri.AbsolutePath.EndsWith("/custom/fps_pvp_bridge_v1/private/items"), "Mailbox uses Private Custom endpoint");
            return Reply("{\"results\":[{\"key\":\"bridge\",\"value\":{\"version\":1},\"writeLock\":\"stored-lock\"}],\"links\":{\"next\":\"https://untrusted.invalid/never-follow\"}}");
        });
        var options = new UgsBridgeOptions
        { Enabled = true, ProjectId = Guid.NewGuid().ToString(), EnvironmentId = Guid.NewGuid().ToString(), ServiceAccountKeyId = "fake-key-123", ServiceAccountSecretKey = "fake-secret-123" };
        options.Validate();
        using var http = new HttpClient(wire);
        var store = new UgsCloudStore(http, options, TimeProvider.System);
        Check((await store.GetPrivateAsync("bridge", default))!.WriteLock == "stored-lock", "201 Token Exchange is accepted and the subsequent Private GET retains its opaque write lock");
        Check(await store.SetPrivateAsync("bridge", new { version = 1 }, "stored-lock", default) == "new-lock", "Mutation verifies returned write lock");
        conflict = true;
        try { await store.SetPrivateAsync("bridge", new { version = 1 }, "stored-lock", default); throw new InvalidOperationException("Expected CAS conflict"); }
        catch (UgsCloudException error) { Check(error.StatusCode == 409 && !error.ToString().Contains("secret"), "CAS conflict is safe and is never retried without a lock"); }
        Check(mutations == 2 && tokens == 1, "Service token is cached and conflicts perform exactly one attempted mutation");
        conflict = false;
        unauthorized = true;
        await store.GetPrivateAsync("bridge", default);
        Check(tokens == 2 && reads == 3, "401 refreshes service token once and repeats the authorized GET");
        var page = await store.ListPrivateAsync(null, default);
        Check(page.After == "bridge", "Pagination uses the last returned key, never a supplied URL");
        await store.ListPrivateAsync(page.After, default);
        Check((await store.GetProtectedProfileAsync("player-1", default))!.Value.GetProperty("accountId").GetString() == "player-1", "Protected response is cloned before HTTP JSON is disposed");
        await store.DeletePrivateAsync("p_player-1", "stored-lock", default);
        malformed = true;
        try { await store.GetPrivateAsync("bridge", default); throw new InvalidOperationException("Expected malformed-response rejection"); }
        catch (UgsCloudException error) { Check(error.StatusCode == 502, "Missing Cloud Save writeLock is rejected instead of becoming an unconditional update"); }
    }

    private static async Task ResponseBodyDeadline(bool tokenBody)
    {
        const string fakeSecret = "fake-deadline-secret";
        int tokens = 0, reads = 0;
        using var stalled = new PendingBodyStream();
        using var wire = new FakeHttp(request =>
        {
            bool stall;
            if (request.RequestUri!.Host == "services.api.unity.com")
            {
                tokens++;
                stall = tokenBody && tokens == 1;
                return Task.FromResult(stall
                    ? new HttpResponseMessage(HttpStatusCode.Created) { Content = new StreamContent(stalled) }
                    : Reply("{\"accessToken\":\"fake-deadline-token\"}", 201));
            }
            reads++;
            stall = !tokenBody && reads == 1;
            return Task.FromResult(stall
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stalled) }
                : Reply("{\"results\":[]}"));
        });
        using var http = new HttpClient(wire) { Timeout = TimeSpan.FromMilliseconds(80) };
        var store = new UgsCloudStore(http, new UgsBridgeOptions
        {
            Enabled = true, ProjectId = Guid.NewGuid().ToString(), EnvironmentId = Guid.NewGuid().ToString(),
            ServiceAccountKeyId = "fake-deadline-key", ServiceAccountSecretKey = fakeSecret
        }, TimeProvider.System);
        bool cancelled = false;
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            // The watchdog is only a test failure bound. Production must cancel
            // the actual body read through its own linked operation deadline.
            await store.GetPrivateAsync("bridge", watchdog.Token);
        }
        catch (OperationCanceledException error)
        {
            cancelled = true;
            Check(!watchdog.IsCancellationRequested, "The cloud operation deadline fires before the test watchdog");
            Check(!error.ToString().Contains(fakeSecret, StringComparison.Ordinal), "Body timeout exception does not expose fake service credentials");
        }
        Check(cancelled && stalled.ReadCalls > 0 && stalled.CancellationObserved,
            tokenBody ? "Token response body is cancelled after headers by the real store deadline" : "Cloud Save response body is cancelled after headers by the real store deadline");
        Check(stalled.WasDisposed, "Timed-out response stream is disposed");
        Check(tokens == 1 && reads == (tokenBody ? 0 : 1), "Timed-out operation does not silently retry or issue further cloud requests");
        using var recoveryWatchdog = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Check(await store.GetPrivateAsync("bridge", recoveryWatchdog.Token) == null,
            tokenBody ? "A new request recovers after token-body timeout and releases the token gate" : "A new request recovers after Cloud Save body timeout");
        Check(tokens == (tokenBody ? 2 : 1) && reads == (tokenBody ? 1 : 2),
            "Recovery exchanges only the needed token and performs one new Private GET");
    }

    private static async Task LocalEndpoints()
    {
        var cloud = new NoProfiles();
        var profiles = new UgsProfiles(cloud);
        var parties = new PartyStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["PvpMatchmaking:ServerAddress"] = "127.0.0.1", ["PvpMatchmaking:ServerCount"] = "1" }).Build(), TimeProvider.System);
        var tickets = new UgsTicketStore(TimeProvider.System, profiles, parties);
        var a = await profiles.LoadAsync("player-A", "Alice_1", default);
        var b = await profiles.LoadAsync("player-B", "Bravo_1", default);
        parties.Create(a); parties.Create(b); parties.Queue(a); parties.Queue(b); parties.MatchQueued([7777]);
        var admission = parties.Admission(a);
        var ticket = tickets.Create(a, admission);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
        builder.Services.AddSingleton(parties); builder.Services.AddSingleton(tickets);
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            // Test-only simulation of a non-loopback socket; production never
            // trusts this header. Actual route filters are under test below.
            if (context.Request.Headers.ContainsKey("X-Test-Remote")) context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
            try { await next(context); }
            catch (ApiException error) { context.Response.StatusCode = error.StatusCode; await context.Response.WriteAsJsonAsync(new ApiError(error.Code, error.Message)); }
        });
        app.MapUgsPvpEndpoints();
        await app.StartAsync();
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        var reservation = await client.GetFromJsonAsync<SlotReservation>("/api/ugs/matches/server/7777");
        Check(reservation!.MatchId == admission.MatchId, "Real local HTTP reservation path returns the actual matched slot");
        var connected = await client.PostAsJsonAsync("/api/ugs/matches/server", new MatchServerEvent(admission.MatchId, 7777, "connected"));
        Check(connected.StatusCode == HttpStatusCode.NoContent, "Real local HTTP event path accepts a matching server event");
        var redeemed = await client.PostAsJsonAsync("/api/ugs/game-ticket/redeem", new { ticket = ticket.Ticket });
        Profile player = (await redeemed.Content.ReadFromJsonAsync<Profile>())!;
        Check(redeemed.IsSuccessStatusCode && player.AccountId == a.AccountId && player.Match!.Roster.Length == 2, "Real local ticket redemption returns trusted UGS identity and roster");
        Check((await client.PostAsJsonAsync("/api/ugs/game-ticket/redeem", new { ticket = ticket.Ticket })).StatusCode == HttpStatusCode.Unauthorized, "Real HTTP ticket replay is 401");
        Check((await client.PostAsJsonAsync("/api/ugs/game-ticket/redeem", new { ticket = new string('A', 64) })).StatusCode == HttpStatusCode.Unauthorized, "Real HTTP forged ticket is 401");
        Check((await client.PostAsJsonAsync("/api/ugs/game-ticket/redeem", new { ticket = "", playerId = a.AccountId })).StatusCode == HttpStatusCode.BadRequest, "Real HTTP rejects forged identity parameters");
        foreach (var path in new[] { "/api/ugs/matches/server/7777", "/api/ugs/game-ticket/redeem", "/api/ugs/matches/server" })
        {
            using var request = new HttpRequestMessage(path.EndsWith("7777") ? HttpMethod.Get : HttpMethod.Post, path);
            request.Headers.Add("X-Test-Remote", "1"); request.Headers.Add("X-Forwarded-For", "127.0.0.1");
            if (request.Method == HttpMethod.Post) request.Content = JsonContent.Create(path.EndsWith("redeem") ? (object)new { ticket = ticket.Ticket } : new MatchServerEvent(admission.MatchId, 7777, "connected"));
            Check((await client.SendAsync(request)).StatusCode == HttpStatusCode.Forbidden, "Each UGS server route rejects remote peers despite forged forwarded-loopback header");
        }
        await app.StopAsync();
    }

    private static async Task HealthEndpoints()
    {
        // SelfHosted has no UgsPvpWorker registration at all.
        var legacyBuilder = WebApplication.CreateBuilder();
        legacyBuilder.Logging.ClearProviders();
        legacyBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        legacyBuilder.Services.AddSingleton(new UgsBridgeOptions { Enabled = false });
        await using (var legacy = legacyBuilder.Build())
        {
            legacy.MapAccountHealthEndpoint();
            await legacy.StartAsync();
            using var legacyClient = new HttpClient { BaseAddress = BoundAddress(legacy) };
            using var response = await legacyClient.GetAsync("/health");
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Check(response.StatusCode == HttpStatusCode.OK && body.RootElement.EnumerateObject().Count() == 3 &&
                body.RootElement.GetProperty("status").GetString() == "ok" &&
                body.RootElement.GetProperty("service").GetString() == "fps-account" &&
                body.RootElement.GetProperty("apiVersion").GetInt32() == 2,
                "SelfHosted health keeps its original three-field 200 response without resolving an unregistered worker");
            await legacy.StopAsync();
        }

        var clock = new HealthClock();
        var cloud = new LeaseCloud();
        var options = new UgsBridgeOptions { Enabled = true, EnvironmentName = "development" };
        var profiles = new UgsProfiles(cloud);
        var parties = new PartyStore(new ConfigurationBuilder().Build(), clock);
        using var worker = new UgsPvpWorker(options, cloud, profiles, new UgsTicketStore(clock, profiles, parties),
            parties, clock, NullLogger<UgsPvpWorker>.Instance);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(worker);
        await using var app = builder.Build();
        app.MapAccountHealthEndpoint();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = BoundAddress(app) };
        using (var response = await client.GetAsync("/health"))
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Check(response.StatusCode == HttpStatusCode.ServiceUnavailable &&
                body.RootElement.GetProperty("status").GetString() == "degraded" &&
                !body.RootElement.GetProperty("ugsBridgeReady").GetBoolean() && cloud.Calls == 0,
                "An uninitialized UGS worker reports real HTTP 503 without health initiating cloud IO");
        }
        await worker.InitializeAsync(default);
        int cloudCalls = cloud.Calls;
        using (var response = await client.GetAsync("/health"))
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Check(response.StatusCode == HttpStatusCode.OK && body.RootElement.GetProperty("ugsBridgeReady").GetBoolean() &&
                body.RootElement.GetProperty("ugsLeaseSecondsRemaining").GetDouble() == 15 && cloud.Calls == cloudCalls,
                "A confirmed lease reports real HTTP 200 and health reads only the worker snapshot");
        }
        clock.Advance(TimeSpan.FromSeconds(15));
        using (var response = await client.GetAsync("/health"))
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Check(response.StatusCode == HttpStatusCode.ServiceUnavailable &&
                body.RootElement.GetProperty("status").GetString() == "degraded" &&
                !body.RootElement.GetProperty("ugsBridgeReady").GetBoolean() &&
                body.RootElement.GetProperty("ugsLeaseSecondsRemaining").GetDouble() == 0 &&
                worker.IsReady && cloud.Calls == cloudCalls,
                "Lease expiry changes real health to 503 without revoking the owner's ability to renew");
        }
        await worker.ProcessOnceAsync(default);
        cloudCalls = cloud.Calls;
        using (var response = await client.GetAsync("/health"))
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Check(response.StatusCode == HttpStatusCode.OK && body.RootElement.GetProperty("ugsBridgeReady").GetBoolean() &&
                cloud.Calls == cloudCalls, "The same expired worker renews successfully and real health recovers to 200");
        }
        await app.StopAsync();
    }

    private static async Task HostedStartupRecovery()
    {
        // Exercise the production hosted-service path and real HTTP health,
        // with only the outbound Unity transport replaced. No cloud account.
        var initialReset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveryRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRecovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0, revision = 0;
        JsonElement? lease = null;
        using var wire = new FakeHttp(async request =>
        {
            if (request.RequestUri!.Host == "services.api.unity.com")
                return Reply("{\"accessToken\":\"offline-startup-token\"}", 201);
            if (request.Method == HttpMethod.Post)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                lease = body.RootElement.GetProperty("value").Clone();
                revision++;
                return Reply(JsonSerializer.Serialize(new { writeLock = revision.ToString() }));
            }
            if (Interlocked.Increment(ref reads) == 1)
            {
                initialReset.TrySetResult();
                throw new HttpRequestException("Offline TLS handshake reset.",
                    new IOException("Offline transport reset.", new System.Net.Sockets.SocketException(10054)));
            }
            if (!allowRecovery.Task.IsCompleted)
            {
                recoveryRead.TrySetResult();
                await allowRecovery.Task;
            }
            return Reply(JsonSerializer.Serialize(new { results = lease == null ? Array.Empty<object>() :
                new object[] { new { key = "bridge", value = lease.Value, writeLock = revision.ToString() } } }));
        });
        using var outbound = new HttpClient(wire) { Timeout = TimeSpan.FromSeconds(10) };
        var options = new UgsBridgeOptions { Enabled = true };
        var cloud = new UgsCloudStore(outbound, options, TimeProvider.System);
        var profiles = new UgsProfiles(cloud);
        var parties = new PartyStore(new ConfigurationBuilder().Build(), TimeProvider.System);
        var worker = new UgsPvpWorker(options, cloud, profiles, new UgsTicketStore(TimeProvider.System, profiles, parties),
            parties, TimeProvider.System, NullLogger<UgsPvpWorker>.Instance);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(worker);
        builder.Services.AddHostedService(services => services.GetRequiredService<UgsPvpWorker>());
        await using var app = builder.Build();
        app.MapAccountHealthEndpoint();
        try
        {
            await app.StartAsync().WaitAsync(TimeSpan.FromSeconds(3));
            await initialReset.Task.WaitAsync(TimeSpan.FromSeconds(3));
            using var client = new HttpClient { BaseAddress = BoundAddress(app) };
            using (var response = await client.GetAsync("/health"))
                Check(response.StatusCode == HttpStatusCode.ServiceUnavailable && !worker.IsReady,
                    "An initial TLS reset does not prevent the actual Host and Kestrel from serving 503 health");
            await recoveryRead.Task.WaitAsync(TimeSpan.FromSeconds(8));
            using (var response = await client.GetAsync("/health"))
                Check(response.StatusCode == HttpStatusCode.ServiceUnavailable && revision == 0,
                    "A startup retry waiting for its cloud response never reports a speculative healthy lease");
            allowRecovery.TrySetResult();
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (!worker.HasLiveLease && DateTime.UtcNow < deadline) await Task.Delay(10);
            using (var response = await client.GetAsync("/health"))
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Check(response.StatusCode == HttpStatusCode.OK && body.RootElement.GetProperty("ugsBridgeReady").GetBoolean() &&
                    body.RootElement.GetProperty("ugsLeaseSecondsRemaining").GetDouble() > 0 &&
                    lease?.GetProperty("epoch").GetString() == worker.Epoch && revision == 1,
                    "The same hosted worker retries after five seconds and changes real HTTP health to 200 only after a confirmed lease write");
            }
        }
        finally
        {
            allowRecovery.TrySetResult();
            await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(4));
        }
        Check(!worker.IsReady && !worker.HasLiveLease,
            "Stopping the actual hosted worker immediately invalidates readiness");
    }

    private static Uri BoundAddress(WebApplication app) => new(
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());

    private static HttpResponseMessage Reply(string json, int status = 200) => new((HttpStatusCode)status)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class FakeHttp(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) => respond(request); }
    private sealed class PendingBodyStream : Stream
    {
        private readonly CancellationTokenSource disposed = new();
        public int ReadCalls { get; private set; }
        public bool CancellationObserved { get; private set; }
        public bool WasDisposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancel) =>
            ReadAsync(buffer.AsMemory(offset, count), cancel).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
        {
            ReadCalls++;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, disposed.Token);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token); return 0; }
            catch (OperationCanceledException) { CancellationObserved = cancel.IsCancellationRequested; throw; }
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing && !WasDisposed) { WasDisposed = true; disposed.Cancel(); disposed.Dispose(); }
            base.Dispose(disposing);
        }
    }
    private sealed class HealthClock : TimeProvider
    {
        private DateTimeOffset current = DateTimeOffset.FromUnixTimeMilliseconds(1800000000000);
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan amount) => current += amount;
    }
    private sealed class LeaseCloud : IUgsCloudStore
    {
        private UgsCloudItem? lease;
        private int revision;
        public int Calls { get; private set; }
        public Task<UgsCloudItem?> GetPrivateAsync(string key, CancellationToken cancel)
        { Calls++; return Task.FromResult(key == "bridge" ? lease : null); }
        public Task<string> SetPrivateAsync(string key, object value, string? writeLock, CancellationToken cancel)
        {
            Calls++;
            if (key != "bridge") throw new InvalidOperationException("Health fixture supports only its lease.");
            if (writeLock != null && writeLock != lease?.WriteLock) throw new UgsCloudException(409);
            string next = (++revision).ToString();
            lease = new(key, JsonSerializer.SerializeToElement(value, Json), next);
            return Task.FromResult(next);
        }
        public Task<UgsCloudPage> ListPrivateAsync(string? after, CancellationToken cancel)
        { Calls++; return Task.FromResult(new UgsCloudPage(lease == null ? [] : [lease!], null)); }
        public Task<JsonElement?> GetProtectedProfileAsync(string playerId, CancellationToken cancel) => throw new NotSupportedException();
        public Task DeletePrivateAsync(string key, string writeLock, CancellationToken cancel) => throw new NotSupportedException();
    }
    private sealed class NoProfiles : IUgsCloudStore
    {
        public Task<JsonElement?> GetProtectedProfileAsync(string playerId, CancellationToken cancel) => Task.FromResult<JsonElement?>(null);
        public Task<UgsCloudItem?> GetPrivateAsync(string key, CancellationToken cancel) => throw new NotSupportedException();
        public Task<UgsCloudPage> ListPrivateAsync(string? after, CancellationToken cancel) => throw new NotSupportedException();
        public Task<string> SetPrivateAsync(string key, object value, string? writeLock, CancellationToken cancel) => throw new NotSupportedException();
        public Task DeletePrivateAsync(string key, string writeLock, CancellationToken cancel) => throw new NotSupportedException();
    }
}
