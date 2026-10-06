using Fps.AccountService;
using Microsoft.Extensions.Configuration;

internal static class PartyRulesTests
{
    public static void Run(Action<bool, string> check)
    {
        var clock = new ManualClock();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var store = new PartyStore(config, clock);
        Profile[] players = Enumerable.Range(0, 20).Select(i => new Profile(Guid.NewGuid().ToString("N"), "Player" + i, 0, new(), new())).ToArray();
        bool Rejected(Action action, string code)
        { try { action(); return false; } catch (ApiException error) { return error.Code == code; } }

        var a = store.Create(players[0]).Party!;
        var b = store.Create(players[5]).Party!;
        check(a.Name == "团队对抗" && a.MapName == "测试地图1", "Party uses the agreed mode and map names");
        check(store.List(players[0]).Rooms.Length == 2, "Multiple separately owned parties are listed");
        check(store.Create(players[0]).Party!.Id == a.Id, "Repeated Start Game restores the existing party");
        for (int i = 1; i < 5; i++) store.Join(players[i], a.Id);
        check(Rejected(() => store.Join(players[6], a.Id), "party_full"), "A pre-match party has exactly five available member positions");
        check(Rejected(() => store.Join(players[1], b.Id), "already_in_party"), "One account cannot join two parties");
        check(Rejected(() => store.Queue(players[1]), "owner_only"), "Members cannot initiate matching");
        check(Rejected(() => store.Queue(players[0]), "not_ready"), "Owner cannot match before every member is ready");
        for (int i = 1; i < 5; i++) store.Ready(players[i], true);
        store.Queue(players[0]);
        check(Rejected(() => store.Join(players[6], a.Id), "party_matching"), "A queued party rejects late joiners");
        store.Ready(players[1], false);
        check(store.Current(players[0]).Party!.State == "Waiting", "Cancel Ready removes the party from the queue");
        store.Ready(players[1], true); store.Queue(players[0]); store.Queue(players[5]);
        store.MatchQueued([7777, 7778]);
        var redOrBlue = store.Current(players[0]).Party!;
        var other = store.Current(players[5]).Party!;
        check(redOrBlue.Match!.MatchId == other.Match!.MatchId && redOrBlue.Match.Team != other.Match.Team,
            "Two prepared parties share one match and receive opposite randomly assigned sides");
        MatchAdmission admission = store.Admission(players[0]);
        check(admission.Roster.Length == 6 && admission.Roster.Count(m => m.Team == redOrBlue.Match.Team) == 5,
            "A five-player party can match a one-player party without splitting teammates");
        check(Enumerable.Range(0, 5).All(i => admission.Roster.Single(m => m.AccountId == players[i].AccountId).Team == redOrBlue.Match.Team),
            "All original party members remain together");
        check(store.Reservation(7777).MatchId == admission.MatchId && store.Reservation(7778).MatchId == "",
            "A matched server slot is reserved without claiming unrelated ports");
        store.ValidateAdmission(players[0] with { Match = admission });
        check(Rejected(() => store.ValidateAdmission(players[6] with { Match = admission }), "match_expired"),
            "An account outside the roster cannot redeem matched admission");
        check(Rejected(() => store.ServerEvent(new(admission.MatchId, 7778, "started")), "wrong_server"),
            "An unrelated gameplay port cannot start or complete the match");
        store.ServerEvent(new(admission.MatchId, 7777, "connected"));
        store.ServerEvent(new(admission.MatchId, 7777, "started"));
        check(Rejected(() => store.ValidateAdmission(players[0] with { Match = admission }), "match_expired"),
            "Late match admission is rejected after the game starts");
        store.ServerEvent(new(admission.MatchId, 7777, "completed"));
        check(store.Current(players[0]).Party!.Members.Length == 5 && store.Current(players[0]).Party!.State == "Waiting"
            && !store.Current(players[1]).Party!.Members.Single(m => m.Profile.AccountId == players[1].AccountId).Ready,
            "Settlement restores the original party and resets member readiness");
        store.Leave(players[0]);
        check(store.Current(players[1]).Party == null && store.Current(players[4]).Party == null,
            "Owner departure dissolves the party for every remaining member");

        var soloParties = players.Skip(6).Take(4).Select(p => store.Create(p).Party!).ToArray();
        foreach (Profile p in players.Skip(6).Take(4)) store.Queue(p);
        Parallel.For(0, 5, _ => store.MatchQueued([7777, 7778]));
        var assigned = players.Skip(6).Take(4).Select(p => store.Current(p).Party!.Match!).ToArray();
        check(assigned.Select(m => m.MatchId).Distinct().Count() == 2 && assigned.GroupBy(m => m.MatchId).All(g => g.Count() == 2)
            && assigned.Select(m => m.ServerPort).Distinct().Count() == 2,
            "Concurrent matching creates two separate games without double-booking either server");
        var cancelled = store.Admission(players[6]);
        store.Fail(players[6]);
        check(store.Current(players[6]).Party!.State == "Waiting" && store.Reservation(cancelled.ServerPort).MatchId == "",
            "Connection failure returns both parties to waiting and releases the reservation");
        check(Rejected(() => store.ValidateAdmission(players[6] with { Match = cancelled }), "match_expired"),
            "A cancelled match credential cannot be used again");

        var changing = store.Create(players[10]).Party!;
        store.Join(players[11], changing.Id); store.Ready(players[11], true); store.Queue(players[10]);
        store.RefreshProfiles(id => players.Single(p => p.AccountId == id) with { Revision = id == players[11].AccountId ? 1 : 0 });
        check(store.Current(players[10]).Party!.State == "Waiting" && !store.Current(players[10]).Party!.Members[1].Ready,
            "A saved profile change revokes readiness and cancels the queued party");
        clock.Advance(TimeSpan.FromSeconds(46));
        check(store.Current(players[10]).Party == null, "An absent owner dissolves the party after the heartbeat timeout");

        var fullConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["PvpMatchmaking:RequireFullTeams"] = "true" }).Build();
        var full = new PartyStore(fullConfig, new ManualClock());
        for (int i = 0; i < 10; i++) { full.Create(players[i]); full.Queue(players[i]); }
        full.MatchQueued([7777]);
        check(full.Admission(players[0]).Roster.Length == 10 && full.Admission(players[0]).Roster.Count(m => m.Team == 1) == 5,
            "Optional full-team policy packs ten solos into two five-player sides");
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan delta) => now += delta;
    }
}
