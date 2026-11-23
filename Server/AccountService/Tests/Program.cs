using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Nodes;

// Real HTTP/process tests; no NuGet test runner, mocks, credentials, or external services required.
var root = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "../../../../"));
var service = Path.Combine(root, "bin/Debug/net10.0/AccountService.dll");
if (!File.Exists(service)) throw new FileNotFoundException("Build AccountService in Debug first.", service);
var runFolder = Path.Combine(root, "test-results", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(runFolder);
var database = Path.Combine(runFolder, "accounts.json");
var password = "Integration-only_" + Guid.NewGuid().ToString("N");
var checks = new List<string>();
using var host = new Host(service, root, runFolder);
await host.Start(database);

async Task<(int status, JsonObject body)> Send(string method, string route, object? body = null, string? token = null)
{
    using var request = new HttpRequestMessage(new HttpMethod(method), route);
    if (body != null) request.Content = JsonContent.Create(body);
    if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    using var response = await host.Client.SendAsync(request);
    var text = await response.Content.ReadAsStringAsync();
    return ((int)response.StatusCode, string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text)!.AsObject());
}
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + message);
    checks.Add(message);
    Console.WriteLine("PASS: " + message);
}

PartyRulesTests.Run(Check);

var missingAccount = await Send("POST", "/api/auth/check-registration", new { username = "LoginSceneUser" });
Check(missingAccount.status == 200 && !missingAccount.body["registered"]!.GetValue<bool>(), "Registration check identifies an unregistered username");
Check(missingAccount.body.Count == 1, "Registration check exposes no account identity or profile");
Check((await Send("POST", "/api/auth/check-registration", new { username = "bad name" })).status == 400, "Registration check validates the username format");
var registrationOnly = await Send("POST", "/api/auth/register-account", new { username = "LoginSceneUser", password });
Check(registrationOnly.status == 200 && registrationOnly.body["username"]!.GetValue<string>() == "LoginSceneUser"
    && !registrationOnly.body.ContainsKey("accessToken") && !registrationOnly.body.ContainsKey("profile"), "Login scene registration returns no session or user data");
var registrationDatabase = JsonNode.Parse(await File.ReadAllTextAsync(database))!;
Check(registrationDatabase["accounts"]!.AsArray().Single(a => a!["username"]!.GetValue<string>() == "LoginSceneUser")!["sessions"]!.AsArray().Count == 0,
    "Registration alone persists no authenticated session");
Check((await Send("POST", "/api/auth/check-registration", new { username = "loginsceneuser" })).body["registered"]!.GetValue<bool>(),
    "Registration lookup is case-insensitive after account creation");
Check((await Send("POST", "/api/auth/register-account", new { username = "LOGINSCENEUSER", password })).status == 409,
    "Login scene registration rejects duplicate usernames");
var registrationLogin = await Send("POST", "/api/auth/login", new { username = "loginsceneuser", password });
Check(registrationLogin.status == 200 && (await Send("GET", "/api/profile", token: registrationLogin.body["accessToken"]!.GetValue<string>())).status == 200,
    "An explicitly registered account can subsequently log in and load user data");
// Independent process resets credential throttling for the existing regression suite.
host.Stop();
await host.Start(database);

var register = await Send("POST", "/api/auth/register", new { username = "CloudPlayer", password });
Check(register.status == 200, "Register creates an account and authenticated session");
string token = register.body["accessToken"]!.GetValue<string>();
string accountId = register.body["profile"]!["accountId"]!.GetValue<string>();
Check((await Send("POST", "/api/auth/register", new { username = "cloudplayer", password })).status == 409, "Duplicate usernames are rejected case-insensitively");
Check((await Send("POST", "/api/auth/login", new { username = "CloudPlayer", password = "incorrect-password" })).status == 401, "Wrong password is rejected");
Check((await Send("GET", "/api/profile")).status == 401, "Unauthenticated profile read is rejected");
Check((await Send("POST", "/api/auth/validate", token: token)).body["accountId"]!.GetValue<string>() == accountId, "Game server validation returns trusted account identity");

object Profile(int revision, byte outfit = 2, byte sidearmSight = 3) => new
{
    revision,
    appearance = new { outfitId = outfit, faceId = 4, clothColorId = 4, hairColorId = 3, hat = true, accessories = false },
    loadout = new { primaryWeaponId = 2, primarySightId = 5, sidearmSightId = sidearmSight }
};
var saved = await Send("PUT", "/api/profile", Profile(0), token);
Check(saved.status == 200 && saved.body["revision"]!.GetValue<int>() == 1, "Valid customization and loadout are saved with a new revision");
Check((await Send("PUT", "/api/profile", Profile(0), token)).status == 409, "Stale save cannot overwrite another device's profile");
Check((await Send("PUT", "/api/profile", Profile(1, 255), token)).status == 400, "Invalid appearance value is rejected");
Check((await Send("PUT", "/api/profile", Profile(1, sidearmSight: 5), token)).status == 400, "Incompatible pistol scope is rejected");
var wardrobeRequest = System.Text.Json.JsonSerializer.SerializeToNode(Profile(1))!;
wardrobeRequest["appearance"]!["wardrobeItems"] = 65536 + (2 << 6) + 1;
wardrobeRequest["appearance"]!["wardrobeColors"] = 3 << 9;
var concurrent = await Task.WhenAll(Send("PUT", "/api/profile", wardrobeRequest, token), Send("PUT", "/api/profile", wardrobeRequest, token));
Check(concurrent.Count(r => r.status == 200) == 1 && concurrent.Count(r => r.status == 409) == 1, "Concurrent saves admit exactly one revision winner");
var wardrobeRead = (await Send("GET", "/api/profile", token: token)).body["appearance"]!;
Check(wardrobeRead["wardrobeItems"]!.GetValue<int>() == 65536 + (2 << 6) + 1 &&
    wardrobeRead["wardrobeColors"]!.GetValue<int>() == (3 << 9), "Saved wardrobe slots and item colors round-trip over HTTP");
Directory.CreateDirectory(database + ".pending");
Check((await Send("PUT", "/api/profile", Profile(2), token)).status == 500, "A storage failure is reported instead of falsely acknowledging a save");
Directory.Delete(database + ".pending");
Check((await Send("GET", "/api/profile", token: token)).body["revision"]!.GetValue<int>() == 2, "Failed disk write leaves the authoritative in-memory profile unchanged");

var second = await Send("POST", "/api/auth/register", new { username = "AnotherPlayer", password });
var secondToken = second.body["accessToken"]!.GetValue<string>();
var secondProfile = await Send("GET", "/api/profile", token: secondToken);
Check(secondProfile.body["accountId"]!.GetValue<string>() != accountId && secondProfile.body["revision"]!.GetValue<int>() == 0, "Accounts cannot read or overwrite each other's profile");
Check((await Send("PUT", "/api/profile/avatar", new { avatarId = 3 }, secondToken)).body["avatarId"]!.GetValue<int>() == 3,
    "A selected preset avatar is saved to the authenticated account");
Check((await Send("PUT", "/api/profile/avatar", new { avatarId = 255 }, secondToken)).status == 400,
    "Out-of-range avatar indices are rejected");
Check((await Send("POST", "/api/party/create")).status == 401, "Unauthenticated party creation is rejected");
string partyId = (await Send("POST", "/api/party/create", token: token)).body["party"]!["id"]!.GetValue<string>();
Check((await Send("POST", "/api/party/join", new { partyId }, secondToken)).body["party"]!["members"]![1]!["profile"]!["avatarId"]!.GetValue<int>() == 3,
    "Joined-party member metadata includes that member's own saved avatar");
Check((await Send("POST", "/api/party/queue", token: token)).status == 409, "HTTP matching checks readiness on the server");
Check((await Send("POST", "/api/party/queue", token: secondToken)).status == 403, "HTTP matching checks owner identity on the server");
Check((await Send("POST", "/api/party/ready", new { ready = true }, secondToken)).status == 200,
    "A member can prepare through the authenticated HTTP endpoint");
Check((await Send("POST", "/api/party/queue", token: token)).body["party"]!["state"]!.GetValue<string>() == "Matching",
    "Owner starts the queue after all members are ready");
await Send("POST", "/api/party/ready", new { ready = false }, secondToken);
Check((await Send("GET", "/api/party", token: token)).body["party"]!["state"]!.GetValue<string>() == "Waiting",
    "Cancel Ready is visible to the owner");
await Send("POST", "/api/party/leave", token: token);
Check((await Send("GET", "/api/party", token: secondToken)).body["party"] == null,
    "Owner Leave dissolves the HTTP party rather than transferring ownership");

var issued = await Send("POST", "/api/game-ticket", token: token);
var ticket = issued.body["ticket"]!.GetValue<string>();
Check(ticket != token && (await Send("POST", "/api/game-ticket/redeem", new { ticket })).body["accountId"]!.GetValue<string>() == accountId, "Short-lived game ticket redeems for the correct profile");
Check((await Send("POST", "/api/game-ticket/redeem", new { ticket })).status == 401, "A game ticket cannot be replayed");
var oldTicket = (await Send("POST", "/api/game-ticket", token: token)).body["ticket"]!.GetValue<string>();
var liveTicket = (await Send("POST", "/api/game-ticket", token: token)).body["ticket"]!.GetValue<string>();
Check((await Send("POST", "/api/game-ticket/redeem", new { ticket = oldTicket })).status == 401, "New join attempt revokes the previous ticket");
Check((await Send("POST", "/api/auth/logout", token: token)).status == 204, "Logout revokes the current session");
Check((await Send("GET", "/api/profile", token: token)).status == 401, "Logged-out bearer token is unusable");
Check((await Send("POST", "/api/game-ticket/redeem", new { ticket = liveTicket })).status == 401, "Logout also invalidates outstanding game tickets");

var login = await Send("POST", "/api/auth/login", new { username = "CLOUDPLAYER", password });
string restartedToken = login.body["accessToken"]!.GetValue<string>();
var persisted = await File.ReadAllTextAsync(database);
Check(!persisted.Contains(password) && !persisted.Contains(restartedToken) && !persisted.Contains(token), "Passwords and bearer tokens are never stored in plaintext");
host.Stop();
await host.Start(database);
var restored = await Send("GET", "/api/profile", token: restartedToken);
Check(restored.status == 200 && restored.body["revision"]!.GetValue<int>() == 2
    && restored.body["appearance"]!["faceId"]!.GetValue<int>() == 4
    && restored.body["loadout"]!["primaryWeaponId"]!.GetValue<int>() == 2, "Process restart restores account, session, clothing, and loadout");
Check((await Send("POST", "/api/auth/login", new { username = "CloudPlayer", password })).status == 200, "Password login still works after a service restart");
Check((await Send("POST", "/api/auth/check-registration", new { username = "LoginSceneUser" })).body["registered"]!.GetValue<bool>(),
    "Registration state survives a service restart");
Check((await Send("GET", "/api/profile", token: token)).status == 401, "Session revocation survives a service restart");

bool limited = false;
for (int i = 0; i < 12; i++)
    if ((await Send("POST", "/api/auth/login", new { username = "CloudPlayer", password = "incorrect-password" })).status == 429) limited = true;
Check(limited, "Repeated credential attempts are rate limited");
bool checksLimited = false;
for (int i = 0; i < 12; i++)
    if ((await Send("POST", "/api/auth/check-registration", new { username = "LoginSceneUser" })).status == 429) checksLimited = true;
Check(checksLimited, "Registration lookup is rate limited independently of password attempts");
host.Stop();
await host.Start(Path.Combine(runFolder, "expiry.json"), lifetimeMinutes: "0.01");
var expiring = await Send("POST", "/api/auth/register", new { username = "ExpiryPlayer", password });
await Task.Delay(900);
Check((await Send("GET", "/api/profile", token: expiring.body["accessToken"]!.GetValue<string>())).status == 401, "Expired sessions are rejected");

host.Stop();
await host.Start(database, production: true);
Check((await Send("POST", "/api/auth/login", new { username = "CloudPlayer", password })).status == 400, "Production rejects plaintext password requests");
Check((await Send("POST", "/api/auth/check-registration", new { username = "LoginSceneUser" })).status == 400,
    "Production registration checks require HTTPS");
using (var spoof = new HttpRequestMessage(HttpMethod.Post, "/api/auth/check-registration"))
{
    spoof.Headers.Add("X-Forwarded-Proto", "https");
    spoof.Content = JsonContent.Create(new { username = "LoginSceneUser" });
    Check((int)(await host.Client.SendAsync(spoof)).StatusCode == 400, "Unconfigured forwarded headers cannot bypass HTTPS");
}
var localHealth = await Send("GET", "/health");
Check(localHealth.status == 200 && localHealth.body["service"]!.GetValue<string>() == "fps-account"
    && localHealth.body["apiVersion"]!.GetValue<int>() == 2, "Production loopback health identifies the current service without exposing account routes over HTTP");
Check((await Send("POST", "/api/auth/validate", token: restartedToken)).status == 200, "Production allows dedicated server validation over loopback only");
host.Stop();
await File.WriteAllTextAsync(Path.Combine(runFolder, "results.txt"), string.Join(Environment.NewLine, checks.Select(c => "PASS: " + c)));
Console.WriteLine($"Completed {checks.Count} integration assertions. Results: {runFolder}");

sealed class Host(string service, string root, string runFolder) : IDisposable
{
    private Process? process;
    private int run;
    public HttpClient Client { get; private set; } = null!;

    public async Task Start(string database, bool production = false, string lifetimeMinutes = "720")
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(service);
        start.ArgumentList.Add("--urls=http://127.0.0.1:" + port);
        start.ArgumentList.Add("--AccountStorePath=" + database);
        start.ArgumentList.Add("--SessionLifetimeMinutes=" + lifetimeMinutes);
        start.Environment["ASPNETCORE_ENVIRONMENT"] = production ? "Production" : "Development";
        process = Process.Start(start)!;
        var log = Path.Combine(runFolder, "service-" + ++run + ".log");
        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (this) File.AppendAllText(log, e.Data + Environment.NewLine); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (this) File.AppendAllText(log, e.Data + Environment.NewLine); };
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        Client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port), Timeout = TimeSpan.FromSeconds(10) };
        for (int i = 0; i < 100; i++)
        {
            if (process.HasExited) throw new InvalidOperationException("Service exited; inspect " + log);
            try { using var response = await Client.GetAsync("/health"); return; }
            catch (HttpRequestException) { await Task.Delay(100); }
        }
        throw new TimeoutException("Service did not start.");
    }

    public void Stop()
    {
        if (process != null)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(); process.Dispose(); process = null;
        }
        Client?.Dispose();
    }
    public void Dispose() => Stop();
}
