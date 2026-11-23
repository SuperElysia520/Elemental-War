using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Fps.AccountService;

public sealed record LobbyGrant(string Url, string CertificateSha256, string Token, long ExpiresAt);
public sealed record LobbyCommand(string RequestId, string Operation, JsonElement Args);

// The authenticated player's existing private mailbox delivers the TLS pin and a
// short-lived lobby-only credential. No UGS player token reaches this listener.
public sealed class DirectLobby : IDisposable
{
    public const string Route = "/lobby/v1";
    public int Port { get; }
    public X509Certificate2 Certificate { get; }
    private readonly string url, pin;
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private readonly Dictionary<string, Session> byPlayer = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Session> byToken = new(StringComparer.Ordinal);
    private sealed record Session(string Player, string Username, LobbyGrant Grant)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Dictionary<string, (string Command, UgsPvpResponse Reply, long Until)> Completed { get; } = new();
        public long WindowEnd;
        public int Calls;
    }

    public DirectLobby(string host, int port, TimeProvider clock)
    {
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown || port is < 1 or > 65535 || port == 5080)
            throw new InvalidOperationException("Check the direct lobby host and TCP port.");
        this.clock = clock;
        Port = port;
        url = new UriBuilder("https", host, port, Route).Uri.AbsoluteUri;
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=FPS Lobby", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(host, out var ip)) names.AddIpAddress(ip); else names.AddDnsName(host);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        using var signed = request.CreateSelfSigned(clock.GetUtcNow().AddDays(-1), clock.GetUtcNow().AddYears(5));
        Certificate = X509CertificateLoader.LoadPkcs12(signed.Export(X509ContentType.Pfx), null);
        pin = Certificate.GetCertHashString(HashAlgorithmName.SHA256);
    }

    public LobbyGrant? Grant(string player, string username)
    {
        long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        lock (gate)
        {
            if (byPlayer.TryGetValue(player, out var existing) && existing.Grant.ExpiresAt > now + 30000)
                return existing.Grant;
            foreach (var stale in byPlayer.Values.Where(s => s.Grant.ExpiresAt <= now || s.Player == player).ToArray())
            {
                byPlayer.Remove(stale.Player);
                byToken.Remove(stale.Grant.Token);
            }
            if (byPlayer.Count >= 5000) return null;
            var grant = new LobbyGrant(url, pin, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), now + 20 * 60_000);
            var session = new Session(player, username, grant);
            byPlayer.Add(player, session);
            byToken.Add(grant.Token, session);
            return grant;
        }
    }

    public async Task<UgsPvpResponse> RunAsync(string bearer, LobbyCommand command, UgsPvpWorker worker, CancellationToken cancel)
    {
        long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        Session? session;
        lock (gate) byToken.TryGetValue(bearer, out session);
        if (session == null || session.Grant.ExpiresAt <= now)
            throw new ApiException(401, "lobby_session_expired", "大厅连接已过期，请重新连接。");
        if (!Guid.TryParseExact(command.RequestId, "N", out _) || string.IsNullOrEmpty(command.Operation) ||
            command.Args.ValueKind != JsonValueKind.Object)
            throw new ApiException(400, "invalid_request", "大厅请求格式错误。");
        await session.Gate.WaitAsync(cancel);
        try
        {
            now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            if (session.Grant.ExpiresAt <= now) throw new ApiException(401, "lobby_session_expired", "大厅连接已过期。");
            if (now >= session.WindowEnd) { session.WindowEnd = now + 60000; session.Calls = 0; }
            if (++session.Calls > 180) throw new ApiException(429, "rate_limited", "操作过于频繁。");
            bool readOnly = command.Operation is "current" or "rooms" || command.Operation == "social" &&
                command.Args.TryGetProperty("command", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() is "sync" or "search";
            string fingerprint = command.Operation + "\n" + command.Args.GetRawText();
            foreach (string id in session.Completed.Where(kv => kv.Value.Until <= now).Select(kv => kv.Key).ToArray())
                session.Completed.Remove(id);
            if (session.Completed.TryGetValue(command.RequestId, out var previous))
            {
                if (previous.Command != fingerprint) throw new ApiException(409, "request_reused", "请求编号已被使用。");
                return previous.Reply;
            }
            if (session.Completed.Count >= 96) throw new ApiException(429, "rate_limited", "操作过于频繁。");
            var slot = new UgsPvpSlot(1, worker.Epoch, session.Player, command.RequestId, session.Username,
                command.Operation, command.Args, now, now + 30000, "pending", null);
            var reply = await worker.ExecuteDirectAsync(slot, cancel);
            if (!readOnly) session.Completed.Add(command.RequestId, (fingerprint, reply, now + 30000));
            return reply;
        }
        finally { session.Gate.Release(); }
    }

    public void Dispose() => Certificate.Dispose();

    public bool Allows(PathString path, int localPort, bool https)
    {
        bool publicRoute = path == Route + "/command" || path == Route + "/ping";
        return localPort == Port ? https && publicRoute : !publicRoute;
    }

    public void MapEndpoints(WebApplication app)
    {
        app.MapGet(Route + "/ping", () => Results.Text("fps-lobby-v1"));
        app.MapPost(Route + "/command", async (HttpContext context, LobbyCommand command, UgsPvpWorker worker) =>
        {
            string header = context.Request.Headers.Authorization.ToString();
            if (header.Length != 71 || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                throw new ApiException(401, "lobby_session_expired", "大厅连接已过期。");
            var reply = await RunAsync(header[7..], command, worker, context.RequestAborted);
            return Results.Content(reply.Body, "application/json; charset=utf-8", statusCode: reply.Status);
        });
    }
}
