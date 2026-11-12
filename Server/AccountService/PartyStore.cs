using System.Security.Cryptography;

namespace Fps.AccountService;

public sealed record PartyMember(Profile Profile, bool Ready);
public sealed record MatchMember(string AccountId, byte Team, int Revision);
public sealed record MatchAdmission(string MatchId, int ServerPort, MatchMember[] Roster);
public sealed record PartyMatch(string MatchId, string ServerAddress, int ServerPort, byte Team);
public sealed record PartyView(string Id, string Name, string MapName, string OwnerAccountId,
    string State, PartyMember[] Members, PartyMatch? Match);
public sealed record PartyState(PartyView? Party, string Message = "");
public sealed record PartyList(PartyView[] Rooms);
public sealed record JoinPartyRequest(string? PartyId);
public sealed record ReadyPartyRequest(bool Ready);
public sealed record MatchServerEvent(string? MatchId, int ServerPort, string? Event);
public sealed record SlotReservation(string MatchId);

/// <summary>Authoritative pre-match parties. Gameplay remains on the existing NGO dedicated servers.</summary>
public sealed class PartyStore
{
    public const int Capacity = 5;
    private sealed class Member(Profile profile, DateTimeOffset now)
    {
        public Profile Profile = profile;
        public bool Ready;
        public DateTimeOffset SeenAt = now;
    }
    private sealed class Party(string id, Profile owner, DateTimeOffset now)
    {
        public readonly string Id = id;
        public readonly string Owner = owner.AccountId;
        public readonly List<Member> Members = [new(owner, now)];
        public DateTimeOffset? QueuedAt;
        public string? MatchId;
    }
    private sealed class Match(string id, int port, MatchMember[] roster, string[] partyIds, DateTimeOffset now)
    {
        public readonly string Id = id;
        public readonly int Port = port;
        public readonly MatchMember[] Roster = roster;
        public readonly string[] PartyIds = partyIds;
        public DateTimeOffset ExpiresAt = now.AddSeconds(60);
        public bool Started;
    }
    private readonly object gate = new();
    private readonly Dictionary<string, Party> parties = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Match> matches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> messages = new(StringComparer.Ordinal);
    private readonly TimeProvider clock;
    public string MapName { get; }
    public string ServerAddress { get; }
    public int StartPort { get; }
    public int ServerCount { get; }
    public bool RequireFullTeams { get; }

    public PartyStore(IConfiguration configuration, TimeProvider clock)
    {
        this.clock = clock;
        MapName = configuration["PvpMatchmaking:MapName"] ?? "测试地图1";
        ServerAddress = configuration["PvpMatchmaking:ServerAddress"] ?? "";
        StartPort = configuration.GetValue("PvpMatchmaking:StartPort", 7777);
        ServerCount = configuration.GetValue("PvpMatchmaking:ServerCount", 10);
        RequireFullTeams = configuration.GetValue("PvpMatchmaking:RequireFullTeams", false);
        if (StartPort < 1 || ServerCount is < 1 or > 20 || StartPort + ServerCount - 1 + 41000 > 65535)
            throw new InvalidOperationException("Invalid PvpMatchmaking port pool.");
    }

    public PartyState Create(Profile actor)
    {
        lock (gate)
        {
            Sweep();
            Party? existing = Find(actor.AccountId);
            if (existing != null) return State(existing, actor);
            if (parties.Count >= 1000) throw Error("party_limit", "队伍数量已达到上限，请稍后再试。");
            Party party = new(Guid.NewGuid().ToString("N"), actor, clock.GetUtcNow());
            parties.Add(party.Id, party);
            return State(party, actor);
        }
    }

    public PartyState Current(Profile actor)
    {
        lock (gate)
        {
            Sweep();
            Party? party = Find(actor.AccountId);
            messages.Remove(actor.AccountId, out string? message);
            return party != null ? State(party, actor, message ?? "") : new(null, message ?? "");
        }
    }

    public PartyList List(Profile actor)
    {
        lock (gate)
        {
            Sweep();
            Party? own = Find(actor.AccountId);
            if (own != null) Touch(own, actor);
            // An assigned match is private. Full and queued parties remain visible but cannot be joined.
            return new(parties.Values.Where(p => p.MatchId == null).Select(View).ToArray());
        }
    }

    public PartyState Join(Profile actor, string? id)
    {
        lock (gate)
        {
            Sweep();
            if (Find(actor.AccountId) != null) throw Error("already_in_party", "请先离开当前队伍。");
            if (id == null || !parties.TryGetValue(id, out Party? party)) throw Error("party_gone", "队伍已解散，请刷新房间列表。");
            if (party.MatchId != null || party.QueuedAt != null) throw Error("party_matching", "队伍正在匹配，暂时不能加入。");
            if (party.Members.Count >= Capacity) throw Error("party_full", "队伍已满。");
            party.Members.Add(new(actor, clock.GetUtcNow()));
            ResetReady(party);
            return State(party, actor);
        }
    }

    public PartyState Ready(Profile actor, bool ready)
    {
        lock (gate)
        {
            Sweep();
            Party party = Require(actor);
            if (party.MatchId != null) throw Error("match_assigned", "匹配已成功，正在进入对局。");
            if (party.Owner == actor.AccountId) throw Error("owner_ready", "房主通过开始匹配确认准备。");
            if (!ready) party.QueuedAt = null;
            Touch(party, actor).Ready = ready;
            return State(party, actor);
        }
    }

    public PartyState Queue(Profile actor)
    {
        lock (gate)
        {
            Sweep();
            Party party = RequireOwner(actor);
            if (party.MatchId != null) throw Error("match_assigned", "匹配已成功，正在进入对局。");
            Touch(party, actor);
            if (party.Members.Any(m => m.Profile.AccountId != party.Owner && !m.Ready))
                throw Error("not_ready", "还有成员未准备，无法开始匹配。");
            party.QueuedAt ??= clock.GetUtcNow();
            return State(party, actor);
        }
    }

    public PartyState CancelQueue(Profile actor)
    {
        lock (gate)
        {
            Sweep();
            Party party = RequireOwner(actor);
            if (party.MatchId != null) throw Error("match_assigned", "匹配已成功，正在进入对局。");
            party.QueuedAt = null;
            return State(party, actor);
        }
    }

    public PartyState Leave(Profile actor)
    {
        lock (gate)
        {
            Sweep();
            Party? party = Find(actor.AccountId);
            if (party == null) return new(null);
            if (party.MatchId != null && matches.TryGetValue(party.MatchId, out Match? match) && !match.Started)
                End(match, "有玩家离开，匹配已取消，请重新准备。");
            if (party.Owner == actor.AccountId) Dissolve(party, "房主已离开，队伍已解散。");
            else
            {
                party.Members.RemoveAll(m => m.Profile.AccountId == actor.AccountId);
                party.QueuedAt = null;
                ResetReady(party);
            }
            messages.Remove(actor.AccountId);
            return new(null);
        }
    }

    public MatchAdmission Admission(Profile actor)
    {
        lock (gate)
        {
            Sweep();
            Party party = Require(actor);
            Touch(party, actor);
            if (party.MatchId == null || !matches.TryGetValue(party.MatchId, out Match? match))
                throw Error("no_match", "尚未匹配到对局。");
            return new(match.Id, match.Port, match.Roster.ToArray());
        }
    }

    public PartyState Fail(Profile actor)
    {
        lock (gate)
        {
            Party party = Require(actor);
            if (party.MatchId != null && matches.TryGetValue(party.MatchId, out Match? match) && !match.Started)
                End(match, "有玩家连接失败，请重新准备。");
            return State(party, actor);
        }
    }

    public void ValidateAdmission(Profile actor)
    {
        if (actor.Match == null) return;
        lock (gate)
        {
            Sweep();
            if (!matches.TryGetValue(actor.Match.MatchId, out Match? match) || match.Started
                || match.Port != actor.Match.ServerPort || !match.Roster.Any(m => m.AccountId == actor.AccountId)
                || Find(actor.AccountId)?.MatchId != match.Id)
                throw Error("match_expired", "匹配已取消或失效，请重新开始匹配。");
            if (match.Roster.Single(m => m.AccountId == actor.AccountId).Revision != actor.Revision)
            { End(match, "有成员修改了档案，请重新准备。"); throw Error("profile_changed", "配装或头像已变更，请重新准备。"); }
        }
    }

    public SlotReservation Reservation(int port)
    {
        lock (gate) { Sweep(); return new(matches.Values.FirstOrDefault(m => m.Port == port)?.Id ?? ""); }
    }

    public void ServerEvent(MatchServerEvent body)
    {
        lock (gate)
        {
            Sweep();
            if (body.MatchId == null || !matches.TryGetValue(body.MatchId, out Match? match))
            {
                if (body.Event is "started" or "connected") throw Error("match_expired", "匹配已失效。");
                return;
            }
            if (match.Port != body.ServerPort) throw Error("wrong_server", "对局服务器不匹配。");
            if (body.Event == "connected") match.ExpiresAt = clock.GetUtcNow().AddSeconds(120);
            else if (body.Event == "started") { match.Started = true; match.ExpiresAt = clock.GetUtcNow().AddHours(2); }
            else if (body.Event == "completed" || body.Event == "failed")
                End(match, body.Event == "completed" ? "对局已结束，请重新准备。" : "对局连接失败，请重新准备。");
            else throw new ApiException(400, "invalid_event", "无效的对局事件。");
        }
    }

    /// <summary>Only the server worker supplies freshly probed idle ports; clients cannot assign a server or team.</summary>
    public void MatchQueued(IReadOnlyCollection<int> idlePorts)
    {
        lock (gate)
        {
            Sweep();
            foreach (int port in idlePorts.Order())
            {
                if (port < StartPort || port >= StartPort + ServerCount || matches.Values.Any(m => m.Port == port)) continue;
                var queue = parties.Values.Where(p => p.QueuedAt != null && p.MatchId == null)
                    .OrderBy(p => p.QueuedAt).ToArray();
                var red = new List<Party>();
                var blue = new List<Party>();
                if (RequireFullTeams)
                {
                    var failed = new HashSet<(int index, int red, int blue)>();
                    if (!Pack(queue, 0, Capacity, Capacity, red, blue, failed)) break;
                }
                else
                {
                    if (queue.Length < 2) break;
                    red.Add(queue[0]); blue.Add(queue[1]);
                }
                if (RandomNumberGenerator.GetInt32(2) == 1) (red, blue) = (blue, red);
                MatchMember[] roster = red.SelectMany(p => p.Members.Select(m => new MatchMember(m.Profile.AccountId, 1, m.Profile.Revision)))
                    .Concat(blue.SelectMany(p => p.Members.Select(m => new MatchMember(m.Profile.AccountId, 2, m.Profile.Revision)))).ToArray();
                string id = Guid.NewGuid().ToString("N");
                matches.Add(id, new(id, port, roster, red.Concat(blue).Select(p => p.Id).ToArray(), clock.GetUtcNow()));
                foreach (Party party in red.Concat(blue)) { party.MatchId = id; party.QueuedAt = null; }
            }
        }
    }

    public void RefreshProfiles(Func<string, Profile> readProfile, bool skipUnavailable = false)
    {
        lock (gate)
            foreach (Party party in parties.Values.Where(p => p.MatchId == null))
                foreach (Member member in party.Members)
                {
                    Profile saved;
                    try { saved = readProfile(member.Profile.AccountId); }
                    catch (ApiException) when (skipUnavailable)
                    { ResetReady(party); party.QueuedAt = null; continue; }
                    if (saved.Revision == member.Profile.Revision)
                    {
                        // Cosmetic identity changes do not revoke already confirmed loadouts or matching readiness.
                        member.Profile = member.Profile with { Personal = saved.Personal, PersonalRevision = saved.PersonalRevision };
                        continue;
                    }
                    member.Profile = saved; ResetReady(party); party.QueuedAt = null;
                }
    }

    public string[] ActivePlayerIds()
    {
        lock (gate) return parties.Values.SelectMany(p => p.Members.Select(m => m.Profile.AccountId))
            .Distinct(StringComparer.Ordinal).ToArray();
    }

    public string[] PartyPlayerIds(string playerId)
    {
        lock (gate) return Find(playerId)?.Members.Select(m => m.Profile.AccountId).ToArray() ?? [];
    }

    private static bool Pack(Party[] queue, int index, int redLeft, int blueLeft,
        List<Party> red, List<Party> blue, HashSet<(int, int, int)> failed)
    {
        if (redLeft == 0 && blueLeft == 0) return true;
        if (index >= queue.Length || failed.Contains((index, redLeft, blueLeft))) return false;
        int size = queue[index].Members.Count;
        if (size <= redLeft)
        {
            red.Add(queue[index]);
            if (Pack(queue, index + 1, redLeft - size, blueLeft, red, blue, failed)) return true;
            red.RemoveAt(red.Count - 1);
        }
        if (size <= blueLeft)
        {
            blue.Add(queue[index]);
            if (Pack(queue, index + 1, redLeft, blueLeft - size, red, blue, failed)) return true;
            blue.RemoveAt(blue.Count - 1);
        }
        if (Pack(queue, index + 1, redLeft, blueLeft, red, blue, failed)) return true;
        failed.Add((index, redLeft, blueLeft));
        return false;
    }

    private Party? Find(string account) => parties.Values.FirstOrDefault(p => p.Members.Any(m => m.Profile.AccountId == account));
    private Party Require(Profile actor) => Find(actor.AccountId) ?? throw Error("no_party", "当前不在队伍中。");
    private Party RequireOwner(Profile actor)
    {
        Party party = Require(actor);
        if (party.Owner != actor.AccountId) throw new ApiException(403, "owner_only", "只有房主可以执行此操作。");
        return party;
    }
    private Member Touch(Party party, Profile actor)
    {
        Member member = party.Members.Single(m => m.Profile.AccountId == actor.AccountId);
        // Saved profile changes revoke ready/queue, so a teammate cannot silently change loadout after confirmation.
        if (member.Profile.Revision != actor.Revision && party.MatchId == null)
        { ResetReady(party); party.QueuedAt = null; }
        member.Profile = actor with { Match = null };
        member.SeenAt = clock.GetUtcNow();
        return member;
    }
    private PartyState State(Party party, Profile actor, string message = "") { Touch(party, actor); return new(View(party), message); }
    private PartyView View(Party party) => new(party.Id, "团队对抗", MapName, party.Owner,
        party.MatchId != null ? "Matched" : party.QueuedAt != null ? "Matching" : "Waiting",
        party.Members.Select(m => new PartyMember(m.Profile, m.Profile.AccountId == party.Owner || m.Ready)).ToArray(),
        party.MatchId != null && matches.TryGetValue(party.MatchId, out Match? match)
            ? new(match.Id, ServerAddress, match.Port, match.Roster.Single(m => m.AccountId == party.Owner).Team) : null);
    private static void ResetReady(Party party) { foreach (Member member in party.Members) member.Ready = false; }
    private void Dissolve(Party party, string message)
    {
        foreach (Member member in party.Members) messages[member.Profile.AccountId] = message;
        parties.Remove(party.Id);
    }
    private void End(Match match, string message)
    {
        matches.Remove(match.Id);
        foreach (string id in match.PartyIds)
            if (parties.TryGetValue(id, out Party? party))
            {
                party.MatchId = null; party.QueuedAt = null; ResetReady(party);
                foreach (Member member in party.Members) messages[member.Profile.AccountId] = message;
            }
    }
    private void Sweep()
    {
        DateTimeOffset now = clock.GetUtcNow();
        foreach (Match match in matches.Values.Where(m => m.ExpiresAt <= now).ToArray()) End(match, "匹配连接超时，请重新准备。");
        foreach (Party party in parties.Values.ToArray())
        {
            // Polling during the match keeps the original party alive; if its owner goes away it is dissolved.
            Member[] expired = party.Members.Where(m => now - m.SeenAt > TimeSpan.FromSeconds(45)).ToArray();
            if (expired.Length == 0) continue;
            if (party.MatchId != null && matches.TryGetValue(party.MatchId, out Match? match) && !match.Started)
                End(match, "有成员掉线，匹配已取消。");
            if (expired.Any(m => m.Profile.AccountId == party.Owner)) Dissolve(party, "房主已掉线，队伍已解散。");
            else { party.Members.RemoveAll(m => expired.Contains(m)); party.QueuedAt = null; ResetReady(party); }
        }
        // Notifications are bounded even if clients never reconnect.
        if (messages.Count > 5000) messages.Clear();
    }
    private static ApiException Error(string code, string message) => new(409, code, message);
}
