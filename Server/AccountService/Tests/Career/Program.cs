using System.Text.Json;
using Fps.AccountService;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
int checks = 0;
void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
string root = Path.GetFullPath("tmp/ProfilePanel20261110/Career-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var environment = new TestEnvironment { ContentRootPath = root };
var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AccountStorePath"] = Path.Combine(root, "accounts.json") }).Build();
var parties = new PartyStore(config, TimeProvider.System);
var cloud = new TestCloud();
UgsCareerStore Create() => new(cloud, parties, config, environment);
CareerMatchResult Result(string id) => new(id, parties.StartPort, 1, new[] { new CareerPlayerResult("player_Alpha_001", 1, 3, 1, 2), new CareerPlayerResult("player_Bravo_002", 2, 1, 3, 0) });
CareerTotals Totals(string id) => cloud.Items[id].Value.Deserialize<CareerTotals>(json)!;
var first = Result(Guid.NewGuid().ToString("N"));
var store = Create();
await store.AcceptAsync(first, default);
Check(cloud.Items.Count == 0, "Server acceptance durably queues without trusting client totals or waiting on cloud");
await store.ProcessOnceAsync(default);
Check(Totals(first.Players[0].AccountId) == new CareerTotals(1, 1, 3, 1, 2, 1), "Winner receives exact server counters");
Check(Totals(first.Players[1].AccountId) == new CareerTotals(1, 0, 1, 3, 0, 1), "Loser receives exact server counters");
await store.AcceptAsync(first, default); await store.ProcessOnceAsync(default);
Check(cloud.Writes == 2, "Repeated result never counts twice");
try { await store.AcceptAsync(first with { WinnerTeam = 2 }, default); throw new Exception("Expected conflict"); }
catch (ApiException e) { Check(e.StatusCode == 409, "Different payload for a settled match is rejected"); }
store = Create(); await store.AcceptAsync(first, default); await store.ProcessOnceAsync(default);
Check(cloud.Writes == 2, "Bridge restart preserves settled match receipts");

var second = Result(Guid.NewGuid().ToString("N"));
await store.AcceptAsync(second, default);
cloud.FailAfterNextCommit = true;
try { await store.ProcessOnceAsync(default); throw new Exception("Expected response loss"); }
catch (UgsCloudException) { Check(true, "Cloud response loss leaves a recoverable durable result"); }
store = Create(); await store.ProcessOnceAsync(default);
Check(Totals(first.Players[0].AccountId).Matches == 2 && Totals(first.Players[1].AccountId).Matches == 2, "Restart after partial cloud commit applies each player exactly once");
Check(cloud.Writes == 4, "Committed checkpoint prevents replay after lost acknowledgement");

var third = Result(Guid.NewGuid().ToString("N")); await store.AcceptAsync(third, default);
cloud.ConflictNext = true;
try { await store.ProcessOnceAsync(default); throw new Exception("Expected CAS conflict"); }
catch (UgsCloudException) { Check(true, "Cloud CAS conflict is surfaced without an unlocked retry"); }
await store.ProcessOnceAsync(default);
Check(Totals(first.Players[0].AccountId).Matches == 3 && Totals(first.Players[1].AccountId).Matches == 3, "CAS retry resumes from fresh cloud totals");
foreach (var invalid in new[] { first with { MatchId = "../escape" }, first with { ServerPort = 1 }, first with { WinnerTeam = 3 },
    first with { Players = new[] { first.Players[0], first.Players[0] } },
    first with { Players = new[] { first.Players[0] with { Kills = -1 }, first.Players[1] } },
    first with { Players = new[] { first.Players[0] with { HeadshotKills = 4 }, first.Players[1] } },
    first with { Players = new[] { first.Players[0] with { Deaths = 0 }, first.Players[1] } } })
{
    try { await store.AcceptAsync(invalid, default); throw new Exception("Expected invalid result"); }
    catch (ApiException e) { Check(e.StatusCode == 400, "Malformed, duplicate, wrong-port or inconsistent result rejected"); }
}
var fourth = Result(Guid.NewGuid().ToString("N")); await store.AcceptAsync(fourth, default);
cloud.Items[first.Players[0].AccountId] = new("fps_career_v1", JsonSerializer.SerializeToElement(new { matches = -1 }), "bad");
try { await store.ProcessOnceAsync(default); throw new Exception("Expected corrupt career"); }
catch (InvalidDataException) { Check(true, "Corrupt cloud totals fail closed and remain queued for repair"); }
Console.WriteLine($"Career integration: PASS; {checks} assertions. Fake cloud only. Durable evidence: {root}");

sealed class TestCloud : IUgsCloudStore, IUgsCareerCloudStore
{
    public readonly Dictionary<string, UgsCloudItem> Items = new();
    public int Writes;
    public bool FailAfterNextCommit, ConflictNext;
    public Task<UgsCloudItem?> GetCareerAsync(string playerId, CancellationToken cancel) => Task.FromResult(Items.GetValueOrDefault(playerId));
    public Task SetCareerAsync(string playerId, CareerTotals value, string? writeLock, CancellationToken cancel)
    {
        if (ConflictNext) { ConflictNext = false; throw new UgsCloudException(409); }
        var current = Items.GetValueOrDefault(playerId);
        if (current != null && current.WriteLock != writeLock) throw new UgsCloudException(409);
        Writes++; Items[playerId] = new("fps_career_v1", JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)), "lock-" + Writes);
        if (FailAfterNextCommit) { FailAfterNextCommit = false; throw new UgsCloudException(503); }
        return Task.CompletedTask;
    }
    public Task<UgsCloudItem?> GetPrivateAsync(string key, CancellationToken cancel) => throw new NotSupportedException();
    public Task<UgsCloudPage> ListPrivateAsync(string? after, CancellationToken cancel) => throw new NotSupportedException();
    public Task<string> SetPrivateAsync(string key, object value, string? writeLock, CancellationToken cancel) => throw new NotSupportedException();
    public Task DeletePrivateAsync(string key, string writeLock, CancellationToken cancel) => throw new NotSupportedException();
    public Task<JsonElement?> GetProtectedProfileAsync(string playerId, CancellationToken cancel) => throw new NotSupportedException();
}
sealed class TestEnvironment : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "CareerTests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = "";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
