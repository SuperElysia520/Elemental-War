using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fps.AccountService;

public sealed record CareerPlayerResult(string AccountId, byte Team, int Kills, int Deaths, int HeadshotKills);
public sealed record CareerMatchResult(string MatchId, int ServerPort, byte WinnerTeam, CareerPlayerResult[] Players);
public sealed record CareerTotals(int Matches = 0, int Wins = 0, int Kills = 0, int Deaths = 0, int HeadshotKills = 0, long LastSequence = 0);
public interface IUgsCareerCloudStore
{
    Task<UgsCloudItem?> GetCareerAsync(string playerId, CancellationToken cancel);
    Task SetCareerAsync(string playerId, CareerTotals value, string? writeLock, CancellationToken cancel);
}

/// <summary>Single bridge writer. Durable receipts assign an ordered checkpoint to every accepted server result.</summary>
public sealed class UgsCareerStore
{
    private sealed record Receipt(long Sequence, string Fingerprint, CareerMatchResult Result, bool Completed);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string directory;
    private readonly IUgsCareerCloudStore cloud;
    private readonly PartyStore parties;
    private long nextSequence = 1;
    private readonly SortedDictionary<long, (string Path, Receipt Receipt)> pending = new();

    public UgsCareerStore(IUgsCloudStore cloud, PartyStore parties, IConfiguration configuration, IWebHostEnvironment environment)
    {
        this.cloud = cloud as IUgsCareerCloudStore ?? throw new InvalidOperationException("Cloud store cannot write career records.");
        this.parties = parties;
        string store = Path.GetFullPath(configuration["AccountStorePath"] ?? "data/accounts.json", environment.ContentRootPath);
        directory = Path.Combine(Path.GetDirectoryName(store)!, "career-results");
        Directory.CreateDirectory(directory);
        foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
        {
            var receipt = Read(path);
            nextSequence = Math.Max(nextSequence, checked(receipt.Sequence + 1));
            if (!receipt.Completed) pending.Add(receipt.Sequence, (path, receipt));
        }
    }

    public async Task AcceptAsync(CareerMatchResult result, CancellationToken cancel)
    {
        Validate(result);
        await gate.WaitAsync(cancel);
        try
        {
            string path = Path.Combine(directory, result.MatchId + ".json");
            string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result, Json))));
            if (File.Exists(path))
            {
                if (Read(path).Fingerprint != fingerprint) throw new ApiException(409, "result_conflict", "该对局已提交不同的结算结果。");
                return;
            }
            var receipt = new Receipt(nextSequence, fingerprint, result, false);
            Persist(path, receipt);
            pending.Add(nextSequence, (path, receipt)); nextSequence = checked(nextSequence + 1);
        }
        finally { gate.Release(); }
    }

    public async Task ProcessOnceAsync(CancellationToken cancel)
    {
        await gate.WaitAsync(cancel);
        try
        {
            foreach (var item in pending.Values.ToArray())
            {
                foreach (var player in item.Receipt.Result.Players)
                {
                    var stored = await cloud.GetCareerAsync(player.AccountId, cancel);
                    CareerTotals current = stored == null ? new() : ReadTotals(stored.Value);
                    // A crash after some players were committed resumes only those still missing this sequence.
                    if (current.LastSequence >= item.Receipt.Sequence) continue;
                    var next = new CareerTotals(checked(current.Matches + 1), checked(current.Wins + (player.Team == item.Receipt.Result.WinnerTeam ? 1 : 0)),
                        checked(current.Kills + player.Kills), checked(current.Deaths + player.Deaths), checked(current.HeadshotKills + player.HeadshotKills), item.Receipt.Sequence);
                    await cloud.SetCareerAsync(player.AccountId, next, stored?.WriteLock, cancel);
                }
                Persist(item.Path, item.Receipt with { Completed = true });
                pending.Remove(item.Receipt.Sequence);
            }
        }
        finally { gate.Release(); }
    }

    private void Validate(CareerMatchResult value)
    {
        if (value == null || !Guid.TryParseExact(value.MatchId, "N", out _) || value.ServerPort < parties.StartPort || value.ServerPort >= parties.StartPort + parties.ServerCount ||
            value.WinnerTeam is not (1 or 2) || value.Players == null || value.Players.Length is < 2 or > 10 ||
            value.Players.Any(p => p == null || !UgsProfiles.IsPlayerId(p.AccountId) || p.Team is not (1 or 2) ||
                p.Kills is < 0 or > 100000 || p.Deaths is < 0 or > 100000 || p.HeadshotKills < 0 || p.HeadshotKills > p.Kills) ||
            value.Players.Select(p => p.AccountId).Distinct(StringComparer.Ordinal).Count() != value.Players.Length ||
            value.Players.Count(p => p.Team == 1) is < 1 or > 5 || value.Players.Count(p => p.Team == 2) is < 1 or > 5 ||
            value.Players.Sum(p => p.Kills) != value.Players.Sum(p => p.Deaths))
            throw new ApiException(400, "invalid_result", "专服对局结算数据无效。");
    }
    private static CareerTotals ReadTotals(JsonElement value)
    {
        string[] fields = { "matches", "wins", "kills", "deaths", "headshotKills", "lastSequence" };
        if (value.ValueKind != JsonValueKind.Object || fields.Any(key => !value.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out _)))
            throw new InvalidDataException("Career record schema is invalid.");
        var record = value.Deserialize<CareerTotals>(Json) ?? throw new InvalidDataException("Career record is empty.");
        if (record.Matches < 0 || record.Wins < 0 || record.Wins > record.Matches || record.Kills < 0 || record.Deaths < 0 ||
            record.HeadshotKills < 0 || record.HeadshotKills > record.Kills || record.LastSequence < 0) throw new InvalidDataException("Career totals are invalid.");
        return record;
    }
    private static Receipt Read(string path) => JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException("Career receipt is empty.");
    private static void Persist(string path, Receipt value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { stream.Write(bytes); stream.Flush(true); }
        File.Move(path + ".tmp", path, true);
    }
}

public sealed class UgsCareerUploader(UgsCareerStore store, UgsPvpWorker worker, ILogger<UgsCareerUploader> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!worker.IsReady) continue;
            try { await store.ProcessOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning("Career sync deferred; {Type}. Durable results will retry.", error.GetType().Name); }
        }
    }
}
