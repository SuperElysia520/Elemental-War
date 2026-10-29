using System.Net;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Fps.AccountService;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);
// Only the path crosses the process boundary; service-account credentials stay
// in an external ACL-restricted file installed on the Rainyun server.
string? ugsConfigPath = Environment.GetEnvironmentVariable("FPS_UGS_PVP_CONFIG");
var ugsOptions = new UgsBridgeOptions();
if (!string.IsNullOrWhiteSpace(ugsConfigPath))
{
    if (!Path.IsPathFullyQualified(ugsConfigPath)) throw new InvalidOperationException("FPS_UGS_PVP_CONFIG must be an absolute external config path.");
    string configFile = Path.GetFullPath(ugsConfigPath);
    string appDirectory = Path.GetFullPath(builder.Environment.ContentRootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (configFile.StartsWith(appDirectory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        throw new InvalidOperationException("UGS bridge credential config must be outside the application directory.");
    // Read credentials only from this file, never from command-line settings or
    // UgsPvpBridge__* environment overrides inherited by the server process.
    var external = new ConfigurationBuilder().AddJsonFile(configFile, optional: false, reloadOnChange: false).Build();
    ugsOptions = external.GetSection("UgsPvpBridge").Get<UgsBridgeOptions>() ?? new();
    builder.Configuration.AddConfiguration(external);
}
ugsOptions.Validate();
if (ugsOptions.Enabled)
{
    if (string.IsNullOrWhiteSpace(ugsConfigPath)) throw new InvalidOperationException("UGS bridge credentials require FPS_UGS_PVP_CONFIG external configuration.");
    builder.WebHost.UseUrls("http://127.0.0.1:5080");
}
builder.Services.AddSingleton(ugsOptions);
// Console capture works under a low-privilege service account without Windows Event Log permissions.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 8192);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
builder.Services.AddSingleton<AccountStore>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PartyStore>();
builder.Services.AddHostedService<PartyMatchmaker>();
if (ugsOptions.Enabled)
{
    builder.Services.AddSingleton(new HttpClient(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        // The observed Rainyun-to-Cloud-Save path resets with default TLS;
        // its tested TLS 1.2 path retains normal certificate and hostname checks.
        SslProtocols = System.Security.Authentication.SslProtocols.Tls12
    }) { Timeout = TimeSpan.FromSeconds(10) });
    builder.Services.AddSingleton<IUgsCloudStore, UgsCloudStore>();
    builder.Services.AddSingleton<UgsProfiles>();
    builder.Services.AddSingleton<UgsTicketStore>();
    builder.Services.AddSingleton<UgsPvpWorker>();
    builder.Services.AddHostedService(services => services.GetRequiredService<UgsPvpWorker>());
}
// UGS local server calls must use the socket's actual peer address; forwarded
// headers have no purpose in this loopback-only process.
var trustedProxies = ugsOptions.Enabled ? [] : builder.Configuration.GetSection("TrustedProxyAddresses").Get<string[]>() ?? [];
if (trustedProxies.Length > 0)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var address in trustedProxies) options.KnownProxies.Add(IPAddress.Parse(address));
    });
}
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new ApiError("rate_limited", "请求过于频繁，请稍后再试。"), token);
    };
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 180, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("credentials", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("registration-check", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();
// Fail startup if the durable store is corrupt or another process owns it.
_ = app.Services.GetRequiredService<AccountStore>();
if (trustedProxies.Length > 0) app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    bool loopback = context.Connection.RemoteIpAddress != null && IPAddress.IsLoopback(context.Connection.RemoteIpAddress);
    bool ugsServerEndpoint = context.Request.Path.StartsWithSegments("/api/ugs");
    if ((ugsServerEndpoint || ugsOptions.Enabled) && !loopback)
    {
        context.Response.StatusCode = 403;
        await context.Response.WriteAsJsonAsync(new ApiError("local_server_only", "此接口仅供同机游戏服务器使用。"));
        return;
    }
    bool serverEndpoint = context.Request.Path.StartsWithSegments("/api/matches/server");
    if (serverEndpoint && !loopback)
    {
        context.Response.StatusCode = 403;
        await context.Response.WriteAsJsonAsync(new ApiError("local_server_only", "此接口仅供同机游戏服务器使用。"));
        return;
    }
    bool localServerEndpoint = loopback && (context.Request.Path == "/api/game-ticket/redeem" || context.Request.Path == "/api/auth/validate" || serverEndpoint || ugsServerEndpoint);
    bool localHealth = loopback && HttpMethods.IsGet(context.Request.Method) && context.Request.Path == "/health";
    if (!context.Request.IsHttps && !(app.Environment.IsDevelopment() && loopback) && !localServerEndpoint && !localHealth)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new ApiError("https_required", "账号服务要求 HTTPS 连接。"));
        return;
    }
    try { await next(context); }
    catch (ApiException exception)
    {
        context.Response.StatusCode = exception.StatusCode;
        await context.Response.WriteAsJsonAsync(new ApiError(exception.Code, exception.Message));
    }
    catch (BadHttpRequestException)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new ApiError("invalid_request", "请求格式不正确。"));
    }
    catch (Exception exception)
    {
        // Do not include request bodies, Authorization headers, or secrets in application logs.
        app.Logger.LogError("Account request failed: {ExceptionType}", exception.GetType().Name);
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new ApiError("server_error", "账号服务暂时无法保存或读取，请稍后重试。"));
    }
});
app.UseRateLimiter();
app.MapAccountHealthEndpoint();
if (ugsOptions.Enabled) app.MapUgsPvpEndpoints();
app.MapPost("/api/auth/register", (Credentials body, AccountStore store) => store.Register(body)).RequireRateLimiting("credentials");
app.MapPost("/api/auth/register-account", (Credentials body, AccountStore store) => store.RegisterAccount(body)).RequireRateLimiting("credentials");
app.MapPost("/api/auth/check-registration", (RegistrationCheckRequest body, AccountStore store) => store.CheckRegistration(body)).RequireRateLimiting("registration-check");
app.MapPost("/api/auth/login", (Credentials body, AccountStore store) => store.Login(body)).RequireRateLimiting("credentials");
app.MapPost("/api/auth/logout", (HttpContext context, AccountStore store) =>
{
    store.Logout(ReadToken(context));
    return Results.NoContent();
});
app.MapPost("/api/auth/validate", (HttpContext context, AccountStore store) => store.GetProfile(ReadToken(context)));
app.MapPost("/api/game-ticket", (HttpContext context, AccountStore store) => store.CreateGameTicket(ReadToken(context)));
app.MapPost("/api/game-ticket/redeem", (RedeemTicketRequest body, AccountStore store, PartyStore parties) =>
{
    Profile profile = store.RedeemGameTicket(body.Ticket);
    parties.ValidateAdmission(profile);
    return profile;
});
app.MapGet("/api/profile", (HttpContext context, AccountStore store) => store.GetProfile(ReadToken(context)));
app.MapPut("/api/profile", (SaveProfileRequest body, HttpContext context, AccountStore store) => store.SaveProfile(ReadToken(context), body));
app.MapPut("/api/profile/avatar", (SaveAvatarRequest body, HttpContext context, AccountStore store) => store.SaveAvatar(ReadToken(context), body.AvatarId));
app.MapGet("/api/party", (HttpContext context, AccountStore accounts, PartyStore parties) => parties.Current(accounts.GetProfile(ReadToken(context))));
app.MapGet("/api/party/rooms", (HttpContext context, AccountStore accounts, PartyStore parties) => parties.List(accounts.GetProfile(ReadToken(context))));
app.MapPost("/api/party/create", (HttpContext context, AccountStore accounts, PartyStore parties) => parties.Create(accounts.GetProfile(ReadToken(context))));
app.MapPost("/api/party/join", (JoinPartyRequest body, HttpContext context, AccountStore accounts, PartyStore parties) => parties.Join(accounts.GetProfile(ReadToken(context)), body.PartyId));
app.MapPost("/api/party/ready", (ReadyPartyRequest body, HttpContext context, AccountStore accounts, PartyStore parties) => parties.Ready(accounts.GetProfile(ReadToken(context)), body.Ready));
app.MapPost("/api/party/queue", (HttpContext context, AccountStore accounts, PartyStore parties) => parties.Queue(accounts.GetProfile(ReadToken(context))));
app.MapPost("/api/party/cancel", (HttpContext context, AccountStore accounts, PartyStore parties) => parties.CancelQueue(accounts.GetProfile(ReadToken(context))));
app.MapPost("/api/party/leave", (HttpContext context, AccountStore accounts, PartyStore parties) => parties.Leave(accounts.GetProfile(ReadToken(context))));
app.MapPost("/api/party/fail", (HttpContext context, AccountStore accounts, PartyStore parties) => parties.Fail(accounts.GetProfile(ReadToken(context))));
app.MapPost("/api/party/ticket", (HttpContext context, AccountStore accounts, PartyStore parties) =>
{
    string token = ReadToken(context);
    return accounts.CreateGameTicket(token, parties.Admission(accounts.GetProfile(token)));
});
app.MapGet("/api/matches/server/{port:int}", (int port, PartyStore parties) => parties.Reservation(port));
app.MapPost("/api/matches/server", (MatchServerEvent body, PartyStore parties) => { parties.ServerEvent(body); return Results.NoContent(); });
app.Run();

static string ReadToken(HttpContext context)
{
    var header = context.Request.Headers.Authorization.ToString();
    if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        throw new ApiException(401, "unauthorized", "请先登录账号。");
    return header[7..].Trim();
}
