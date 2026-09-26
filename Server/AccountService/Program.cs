using System.Net;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Fps.AccountService;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);
// Console capture works under a low-privilege service account without Windows Event Log permissions.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 8192);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
builder.Services.AddSingleton<AccountStore>();
var trustedProxies = builder.Configuration.GetSection("TrustedProxyAddresses").Get<string[]>() ?? [];
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
    bool localServerEndpoint = loopback && (context.Request.Path == "/api/game-ticket/redeem" || context.Request.Path == "/api/auth/validate");
    if (!context.Request.IsHttps && !(app.Environment.IsDevelopment() && loopback) && !localServerEndpoint)
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
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapPost("/api/auth/register", (Credentials body, AccountStore store) => store.Register(body)).RequireRateLimiting("credentials");
app.MapPost("/api/auth/login", (Credentials body, AccountStore store) => store.Login(body)).RequireRateLimiting("credentials");
app.MapPost("/api/auth/logout", (HttpContext context, AccountStore store) =>
{
    store.Logout(ReadToken(context));
    return Results.NoContent();
});
app.MapPost("/api/auth/validate", (HttpContext context, AccountStore store) => store.GetProfile(ReadToken(context)));
app.MapPost("/api/game-ticket", (HttpContext context, AccountStore store) => store.CreateGameTicket(ReadToken(context)));
app.MapPost("/api/game-ticket/redeem", (RedeemTicketRequest body, AccountStore store) => store.RedeemGameTicket(body.Ticket));
app.MapGet("/api/profile", (HttpContext context, AccountStore store) => store.GetProfile(ReadToken(context)));
app.MapPut("/api/profile", (SaveProfileRequest body, HttpContext context, AccountStore store) => store.SaveProfile(ReadToken(context), body));
app.Run();

static string ReadToken(HttpContext context)
{
    var header = context.Request.Headers.Authorization.ToString();
    if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        throw new ApiException(401, "unauthorized", "请先登录账号。");
    return header[7..].Trim();
}
