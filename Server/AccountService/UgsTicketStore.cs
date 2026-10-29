using System.Security.Cryptography;
using System.Text;

namespace Fps.AccountService;

/// <summary>Opaque, short-lived UGS admission tickets. No legacy session or player JWT is accepted.</summary>
public sealed class UgsTicketStore(TimeProvider clock, UgsProfiles profiles, PartyStore parties)
{
    private sealed record Entry(Profile Profile, MatchAdmission? Match, DateTimeOffset Expires);
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> tickets = new(StringComparer.Ordinal);

    public GameTicketResponse Create(Profile profile, MatchAdmission? match)
    {
        lock (gate)
        {
            DateTimeOffset now = clock.GetUtcNow();
            foreach (string key in tickets.Where(t => t.Value.Expires <= now || t.Value.Profile.AccountId == profile.AccountId).Select(t => t.Key).ToArray())
                tickets.Remove(key);
            if (tickets.Count >= 2000) throw new ApiException(503, "ticket_limit", "入场请求过多，请稍后重试。");
            string ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            DateTimeOffset expires = now.AddSeconds(60);
            tickets.Add(Hash(ticket), new(profile with { Match = null }, match, expires));
            return new(ticket, expires.ToUnixTimeSeconds());
        }
    }

    public async Task<Profile> RedeemAsync(string? ticket, CancellationToken cancel)
    {
        Entry entry;
        lock (gate)
        {
            if (ticket == null || ticket.Length != 64 || !ticket.All(Uri.IsHexDigit) ||
                !tickets.Remove(Hash(ticket), out var saved) || saved.Expires <= clock.GetUtcNow())
                throw new ApiException(401, "invalid_ticket", "入场凭证已失效，请重新加入房间。");
            entry = saved;
        }
        // Load before entering PartyStore's lock. A failed network check consumes
        // this one-use attempt; the client may obtain a new ticket and retry.
        Profile current = await profiles.LoadAsync(entry.Profile.AccountId, entry.Profile.Username, cancel);
        current = current with { Match = entry.Match };
        parties.ValidateAdmission(current);
        return current;
    }

    private static string Hash(string ticket) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ticket)));
}
