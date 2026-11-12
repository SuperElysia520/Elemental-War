using System.Text.Json;
using Fps.AccountService;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

await BridgeTests.RunAsync();

static class BridgeTests
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Alice = "player_Alice_001", Bravo = "player_Bravo_002";
    private static int checks, cases;

    public static async Task RunAsync()
    {
        await Case("two real parties match, roster tickets redeem once", MatchRoundTrip);
        await Case("forged, malformed, replaced and expired tickets reject", TicketFailures);
        await Case("saved revision change cancels matched admission", RevisionChange);
        await Case("custom-room ticket loads latest profile without admission", CustomRoomLatest);
        await Case("personal names flow to party members and redeemed tickets without changing login names or readiness", PersonalNames);
        await Case("wrong epoch, expired and mismatched identity slots never execute", UntrustedSlots);
        await Case("join response CAS conflict does not repeat side effects", JoinConflict);
        await Case("ticket response CAS conflict does not mint another ticket", TicketConflict);
        await Case("reused request ID with different command does not execute", RequestFingerprint);
        await Case("live worker lease refuses another worker", LeaseOwnership);
        await Case("health expires independently of ownership and recovers after renewal", LeaseHealth);
        await Case("failed cloud lease writes never extend reported health", LeaseWriteFailure);
        await Case("initial cloud write must succeed before a worker becomes healthy", InitialLeaseWriteFailure);
        await Case("corrupt Protected data invalidates cache and blocks creation", CorruptProfile);
        await Case("teammate profile update revokes readiness before queue", QueueRefresh);
        await Case("missing Protected profile gives default without persistence", MissingProfile);
        await Case("private mailbox uses actual write locks and pagination", StorageSemantics);
        await Case("fifty full rooms fit the complete mailbox slot limit or reject safely", LargeRooms);
        await Case("startup survives a transient TLS failure and recovers on the same worker", StartupNetworkRecovery);
        await Case("startup retries timeout, throttling and temporary cloud failures", StartupTransientRecovery);
        await Case("startup recovers when its lease POST committed but the response was lost", StartupLostWriteResponse);
        await Case("stopping during bootstrap delay cancels all future retries", StartupCancelledDelay);
        await Case("stopping during the first lease POST prevents late healthy publication", StartupCancelledWrite);
        await Case("startup permission denial is fatal without retry", StartupPermissionDenied);
        await Case("startup refuses another live epoch without retry", StartupForeignLease);
        Console.WriteLine($"UGS bridge offline integration: PASS; {cases} scenarios, {checks} assertions. No HTTP, cloud credentials or system changes.");
    }

    private static async Task Case(string name, Func<Task> action)
    {
        try { await action(); cases++; Console.WriteLine("PASS " + name); }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL " + name + ": " + error);
            Environment.ExitCode = 1;
            throw;
        }
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        checks++;
    }

    private static async Task<ApiException> ApiFailure(int status, Func<Task> action)
    {
        try { await action(); }
        catch (ApiException error) { Check(error.StatusCode == status, $"Expected API {status}, received {error.StatusCode}/{error.Code}."); return error; }
        throw new InvalidOperationException($"Expected API {status}, operation succeeded.");
    }

    private static async Task<TException> Failure<TException>(Func<Task> action) where TException : Exception
    {
        try { await action(); }
        catch (TException error) { checks++; return error; }
        throw new InvalidOperationException("Expected " + typeof(TException).Name);
    }

    private static T Body<T>(UgsPvpResponse response) => JsonSerializer.Deserialize<T>(response.Body, Json)
        ?? throw new InvalidOperationException("Response body is empty.");

    private static async Task MatchRoundTrip()
    {
        using var h = await Harness.Create();
        await h.Match(Alice, Bravo);
        var first = Body<GameTicketResponse>(await h.Command(Alice, "ticket"));
        var second = Body<GameTicketResponse>(await h.Command(Bravo, "ticket"));
        Check(first.Ticket.Length == 64 && second.Ticket.Length == 64 && first.Ticket != second.Ticket, "Tickets must be opaque, unique 64-hex strings.");
        var a = await h.Tickets.RedeemAsync(first.Ticket, default);
        var b = await h.Tickets.RedeemAsync(second.Ticket, default);
        Check(a.AccountId == Alice && b.AccountId == Bravo, "Admission must retain authenticated player identity.");
        Check(a.Match != null && b.Match != null && a.Match.MatchId == b.Match.MatchId && a.Match.ServerPort == 7777, "Both tickets must identify the same actual match and reserved port.");
        var roster = a.Match!.Roster;
        Check(roster.Length == 2 && roster.Select(x => x.AccountId).Order().SequenceEqual(new[] { Alice, Bravo }.Order()), "Match roster must contain exactly the two server-matched players.");
        Check(roster.Select(x => x.Team).Distinct().Count() == 2 && roster.All(x => x.Team is 1 or 2), "Opponents must occupy distinct valid teams.");
        Check(roster.All(x => x.Revision == 1), "Admission roster must record authoritative revisions.");
        Check(h.Parties.Reservation(7777).MatchId == a.Match.MatchId, "Port reservation must identify the admitted match.");
        await ApiFailure(401, async () => await h.Tickets.RedeemAsync(first.Ticket, default));
        Check(h.Cloud.ListCalls > 2, "Worker scans must exercise mailbox pagination.");
    }

    private static async Task TicketFailures()
    {
        using var h = await Harness.Create();
        await ApiFailure(401, async () => await h.Tickets.RedeemAsync(null, default));
        await ApiFailure(401, async () => await h.Tickets.RedeemAsync("not-a-ticket", default));
        await ApiFailure(401, async () => await h.Tickets.RedeemAsync(new string('A', 64), default));
        var older = Body<GameTicketResponse>(await h.Command(Alice, "game-ticket"));
        var newer = Body<GameTicketResponse>(await h.Command(Alice, "game-ticket"));
        await ApiFailure(401, async () => await h.Tickets.RedeemAsync(older.Ticket, default));
        var valid = await h.Tickets.RedeemAsync(newer.Ticket, default);
        Check(valid.AccountId == Alice && valid.Match == null, "Latest custom ticket must be valid and have no forged match.");
        var expiring = Body<GameTicketResponse>(await h.Command(Alice, "game-ticket"));
        h.Clock.Advance(TimeSpan.FromSeconds(61));
        await ApiFailure(401, async () => await h.Tickets.RedeemAsync(expiring.Ticket, default));
    }

    private static async Task RevisionChange()
    {
        using var h = await Harness.Create();
        await h.Match(Alice, Bravo);
        var a = Body<GameTicketResponse>(await h.Command(Alice, "ticket"));
        var b = Body<GameTicketResponse>(await h.Command(Bravo, "ticket"));
        h.Cloud.SetProfile(Alice, 2, new Appearance(FaceId: 3));
        var changed = await ApiFailure(409, async () => await h.Tickets.RedeemAsync(a.Ticket, default));
        Check(changed.Code == "profile_changed", "Changed authoritative revision must reject the old roster.");
        Check(h.Parties.Reservation(7777).MatchId == "", "Revision failure must cancel the reserved match.");
        await ApiFailure(409, async () => await h.Tickets.RedeemAsync(b.Ticket, default));
        await ApiFailure(401, async () => await h.Tickets.RedeemAsync(a.Ticket, default));
    }

    private static async Task PersonalNames()
    {
        using var h = await Harness.Create();
        h.Cloud.SetPersonal(Alice, 1, "旅行者甲");
        h.Cloud.SetPersonal(Bravo, 1, "旅行者乙");
        var created = Body<PartyState>(await h.Command(Alice, "create"));
        Check(created.Party!.Members[0].Profile.DisplayName == "旅行者甲" && created.Party.Members[0].Profile.Username == "Alice_01", "Party response carries the saved display name separately from login username.");
        await h.Command(Bravo, "join", new { partyId = created.Party.Id });
        await h.Command(Bravo, "ready", new { ready = true });
        int previousRevision = h.Profiles.GetCached(Bravo).Revision;
        h.Cloud.SetPersonal(Bravo, 2, "跨设备新名称");
        await h.Profiles.RefreshAsync([Bravo], default);
        h.Parties.RefreshProfiles(h.Profiles.GetCached);
        var updated = h.Parties.Current(h.Profiles.GetCached(Alice)).Party!;
        var bravo = updated.Members.Single(m => m.Profile.AccountId == Bravo);
        Check(bravo.Profile.DisplayName == "跨设备新名称" && bravo.Profile.PersonalRevision == 2 && bravo.Ready, "Refreshing a name updates every party member view without revoking ready state.");
        Check(bravo.Profile.Username == "Bravo_01" && bravo.Profile.Revision == previousRevision, "Cosmetic name refresh preserves login username and character revision.");
        var ticket = h.Tickets.Create(h.Profiles.GetCached(Bravo), null);
        var redeemed = await h.Tickets.RedeemAsync(ticket.Ticket, default);
        Check(redeemed.DisplayName == "跨设备新名称" && redeemed.Username == "Bravo_01", "Redeemed dedicated-server ticket contains the authoritative display name.");
        h.Cloud.Personal.Remove(Bravo);
        Check((await h.Profiles.LoadAsync(Bravo, "Bravo_01", default)).DisplayName == "Bravo_01", "Legacy missing personal data falls back to the login name.");
        h.Cloud.SetPersonal(Bravo, 3, "<b>bad</b>");
        await ApiFailure(409, async () => { await h.Profiles.LoadAsync(Bravo, "Bravo_01", default); });
        try { h.Profiles.GetCached(Bravo); throw new Exception("Expected corrupt personal data to invalidate cache."); }
        catch (ApiException error) { Check(error.StatusCode == 409, "Malformed personal data invalidates cached admission rather than accepting a forged display name."); }
    }

    private static async Task CustomRoomLatest()
    {
        using var h = await Harness.Create();
        var issued = Body<GameTicketResponse>(await h.Command(Alice, "game-ticket"));
        h.Cloud.SetProfile(Alice, 8, new Appearance(FaceId: 4), avatar: 6);
        var result = await h.Tickets.RedeemAsync(issued.Ticket, default);
        Check(result.Match == null && result.Revision == 8 && result.Appearance.FaceId == 4 && result.AvatarId == 6,
            "Custom-room admission must refresh current appearance, revision and avatar without assigning a match.");
        await ApiFailure(401, async () => await h.Tickets.RedeemAsync(issued.Ticket, default));
    }

    private static async Task UntrustedSlots()
    {
        using var h = await Harness.Create();
        var wrongEpoch = h.MakeSlot(Alice, "create") with { Epoch = new string('f', 32) };
        var expired = h.MakeSlot(Bravo, "create") with { CreatedAt = h.Now - 31000, ExpiresAt = h.Now - 1000 };
        h.Cloud.Seed("p_" + Alice, wrongEpoch);
        h.Cloud.Seed("p_" + Bravo, expired);
        h.Cloud.Seed("p_forged", h.MakeSlot(Alice, "create"));
        await h.Worker.ProcessOnceAsync(default);
        Check(h.Parties.ActivePlayerIds().Length == 0, "Untrusted/stale slots must not create parties.");
        Check(h.Cloud.ProtectedReads == 0, "Invalid slots must be rejected before any player-profile access.");
        Check(!h.Cloud.Contains("p_" + Alice) && !h.Cloud.Contains("p_" + Bravo), "Expired and prior-epoch slots must be deleted with their real locks.");
        Check(h.Cloud.Contains("p_forged"), "Identity-mismatched record must not be processed as another player.");
        Check(h.Cloud.Deletes == 2, "Only the two expired/stale keys may be removed.");
    }

    private static async Task JoinConflict()
    {
        using var h = await Harness.Create();
        var owner = Body<PartyState>(await h.Command(Alice, "create"));
        h.Submit(Bravo, "join", new { partyId = owner.Party!.Id });
        h.Cloud.ConflictDoneKey = "p_" + Bravo;
        await h.Worker.ProcessOnceAsync(default);
        Check(h.Cloud.Slot(Bravo).State == "pending", "Injected response CAS conflict must leave the command pending.");
        Check(h.Parties.Current(h.Profiles.GetCached(Bravo)).Party!.Members.Length == 2, "Join side effect must happen before response CAS retry.");
        int reads = h.Cloud.ProtectedReads;
        await h.Worker.ProcessOnceAsync(default);
        var retried = h.Cloud.Slot(Bravo);
        Check(retried.State == "done" && retried.Response!.Status == 200, "Retry must preserve original successful join, not return already_in_party.");
        Check(h.Cloud.ProtectedReads == reads && Body<PartyState>(retried.Response!).Party!.Members.Length == 2, "Cached retry must not execute Join or profile lookup twice.");
        Check(h.Cloud.Conflicts == 1, "The fake must inject exactly one 409 response conflict.");
    }

    private static async Task TicketConflict()
    {
        using var h = await Harness.Create();
        await h.Match(Alice, Bravo);
        h.Submit(Alice, "ticket");
        h.Cloud.ConflictDoneKey = "p_" + Alice;
        await h.Worker.ProcessOnceAsync(default);
        var attempted = h.Cloud.LastConflictingDone!.Value.Deserialize<UgsPvpSlot>(Json)!;
        string firstTicket = Body<GameTicketResponse>(attempted.Response!).Ticket;
        int reads = h.Cloud.ProtectedReads;
        await h.Worker.ProcessOnceAsync(default);
        var completed = h.Cloud.Slot(Alice);
        Check(completed.Response!.Status == 200 && Body<GameTicketResponse>(completed.Response).Ticket == firstTicket, "Response retry must return exactly the ticket originally minted.");
        Check(reads == h.Cloud.ProtectedReads, "Cached ticket response must not rerun admission or profile lookup.");
        var admitted = await h.Tickets.RedeemAsync(firstTicket, default);
        Check(admitted.Match != null, "Original ticket must remain valid; minting another would have invalidated it.");
    }

    private static async Task RequestFingerprint()
    {
        using var h = await Harness.Create();
        var slot = h.Submit(Alice, "create");
        await h.Worker.ProcessOnceAsync(default);
        h.Cloud.Seed("p_" + Alice, slot with { Operation = "leave" });
        await h.Worker.ProcessOnceAsync(default);
        var result = h.Cloud.Slot(Alice).Response!;
        Check(result.Status == 400 && Body<ApiError>(result).Code == "request_reused", "Reusing a request identity with a new operation must be rejected.");
        Check(h.Parties.ActivePlayerIds().SequenceEqual(new[] { Alice }), "Rejected replay must not execute the substituted leave.");
    }

    private static async Task LeaseOwnership()
    {
        using var h = await Harness.Create();
        using var second = h.NewWorker();
        await Failure<InvalidOperationException>(() => second.InitializeAsync(default));
        Check(h.Cloud.Item("bridge").Value.GetProperty("epoch").GetString() == h.Worker.Epoch, "Second worker must not overwrite a live lease.");
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.Cloud.Seed("bridge", new UgsPvpLease(1, new string('b', 32), h.Now + 15000));
        await Failure<InvalidOperationException>(() => h.Worker.ProcessOnceAsync(default));
        Check(!h.Worker.IsReady && !h.Worker.HasLiveLease, "Lost lease must revoke ownership and live-lease health together.");
    }

    private static async Task LeaseHealth()
    {
        using var h = await Harness.Create();
        Check(h.Worker.IsReady && h.Worker.HasLiveLease && h.Worker.LeaseSecondsRemaining is > 0 and <= 15,
            "Successful initialization must report a live, bounded confirmed lease.");
        long initialExpiry = h.Cloud.Item("bridge").Value.GetProperty("expiresAt").GetInt64();
        h.Clock.Advance(TimeSpan.FromSeconds(16));
        Check(h.Worker.IsReady && !h.Worker.HasLiveLease && h.Worker.LeaseSecondsRemaining == 0,
            "Without polling for sixteen seconds, ownership remains recoverable but health expires and clamps to zero.");
        Check(h.Cloud.Item("bridge").Value.GetProperty("expiresAt").GetInt64() == initialExpiry,
            "Reading health must not renew or rewrite a cloud lease.");
        await h.Worker.ProcessOnceAsync(default);
        Check(h.Worker.IsReady && h.Worker.HasLiveLease && h.Worker.LeaseSecondsRemaining is > 0 and <= 15,
            "Expired health must not prevent the existing owner from renewing through the real polling path.");
        Check(h.Cloud.Item("bridge").Value.GetProperty("expiresAt").GetInt64() == h.Now + 15000,
            "Recovered health must correspond to the successfully written new cloud expiry.");
    }

    private static async Task LeaseWriteFailure()
    {
        using var h = await Harness.Create();
        UgsCloudItem original = h.Cloud.Item("bridge");
        h.Clock.Advance(TimeSpan.FromSeconds(6));
        var remaining = h.Worker.LeaseSecondsRemaining;
        h.Cloud.FailNextLeaseWrite = true;
        var failure = await Failure<UgsCloudException>(() => h.Worker.ProcessOnceAsync(default));
        Check(failure.StatusCode == 503 && h.Worker.IsReady && h.Worker.HasLiveLease,
            "A transient renewal failure must preserve ownership and the still-live previously confirmed lease.");
        Check(h.Worker.LeaseSecondsRemaining == remaining && remaining is > 0 and <= 9,
            "Failed renewal must not optimistically reset the reported lifetime to fifteen seconds.");
        Check(h.Cloud.Item("bridge") == original, "The injected failed cloud write must preserve both lease value and write lock.");
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        h.Cloud.FailNextLeaseWrite = true;
        await Failure<UgsCloudException>(() => h.Worker.ProcessOnceAsync(default));
        Check(h.Worker.IsReady && !h.Worker.HasLiveLease && h.Worker.LeaseSecondsRemaining == 0,
            "A repeated failed renewal after confirmed expiry must report unhealthy without disabling future retries.");
        Check(h.Cloud.Item("bridge") == original, "Failed retries must leave the expired authoritative cloud item untouched.");
        await h.Worker.ProcessOnceAsync(default);
        Check(h.Worker.IsReady && h.Worker.HasLiveLease && h.Worker.LeaseSecondsRemaining is > 0 and <= 15,
            "A later successful cloud write must restore live health without recreating the worker.");
    }

    private static async Task InitialLeaseWriteFailure()
    {
        using var h = await Harness.Create();
        h.Clock.Advance(TimeSpan.FromSeconds(16));
        using var next = h.NewWorker();
        UgsCloudItem original = h.Cloud.Item("bridge");
        h.Cloud.FailNextLeaseWrite = true;
        await Failure<UgsCloudException>(() => next.InitializeAsync(default));
        Check(!next.IsReady && !next.HasLiveLease && next.LeaseSecondsRemaining == 0,
            "A new worker must not claim ownership or health until its first cloud lease write succeeds.");
        Check(h.Cloud.Item("bridge") == original, "Failed initialization must preserve the prior expired lease for diagnosis.");
        await next.InitializeAsync(default);
        Check(next.IsReady && next.HasLiveLease && next.LeaseSecondsRemaining is > 0 and <= 15,
            "Retrying initialization after a failed cloud write may become healthy only on confirmed success.");
    }

    private static async Task CorruptProfile()
    {
        using var h = await Harness.Create();
        await h.Profiles.LoadAsync(Alice, "Alice_01", default);
        h.Cloud.Protected[Alice] = JsonSerializer.SerializeToElement(new { accountId = Alice, username = "Alice_01", revision = 1 }, Json);
        string corrupt = h.Cloud.Protected[Alice].GetRawText();
        var error = await ApiFailure(409, async () => await h.Profiles.LoadAsync(Alice, "Alice_01", default));
        Check(error.Code == "profile_corrupt", "Missing required schema must not be disguised by DTO defaults.");
        await ApiFailure(409, () => { h.Profiles.GetCached(Alice); return Task.CompletedTask; });
        var response = await h.Command(Alice, "create", expected: 409);
        Check(Body<ApiError>(response).Code == "profile_corrupt" && h.Parties.ActivePlayerIds().Length == 0,
            "A malformed Protected profile must block party creation rather than recreate an account.");
        Check(h.Cloud.Protected[Alice].GetRawText() == corrupt, "Invalid authoritative data must remain untouched for recovery.");
    }

    private static async Task QueueRefresh()
    {
        using var h = await Harness.Create();
        var owner = Body<PartyState>(await h.Command(Alice, "create"));
        await h.Command(Bravo, "join", new { partyId = owner.Party!.Id });
        await h.Command(Bravo, "ready", new { ready = true });
        h.Cloud.SetProfile(Bravo, 2, new Appearance(FaceId: 2));
        var response = await h.Command(Alice, "queue", expected: 409);
        Check(Body<ApiError>(response).Code == "not_ready", "Server must re-read teammate revisions before accepting queue.");
        var party = h.Parties.Current(h.Profiles.GetCached(Alice)).Party!;
        Check(party.State == "Waiting" && !party.Members.Single(x => x.Profile.AccountId == Bravo).Ready,
            "Changing teammate data must revoke prior readiness and queue eligibility.");
    }

    private static async Task MissingProfile()
    {
        using var h = await Harness.Create();
        h.Cloud.Protected.Remove(Alice);
        var ticket = Body<GameTicketResponse>(await h.Command(Alice, "game-ticket"));
        var profile = await h.Tickets.RedeemAsync(ticket.Ticket, default);
        Check(profile.AccountId == Alice && profile.Revision == 0 && profile.Match == null && profile.Appearance.IsValid,
            "Missing data may use an explicitly validated virtual default.");
        Check(!h.Cloud.Protected.ContainsKey(Alice), "Read and admission must not persist a late default over another device's save.");
    }

    private static async Task StorageSemantics()
    {
        var cloud = new FakeCloud { PageSize = 2 };
        cloud.Seed("a", new { n = 1 }); cloud.Seed("b", new { n = 2 }); cloud.Seed("c", new { n = 3 });
        var first = await cloud.ListPrivateAsync(null, default);
        var second = await cloud.ListPrivateAsync(first.After, default);
        Check(first.Results.Length == 2 && first.After == "b" && second.Results.Single().Key == "c" && second.After == null, "Fake pagination must preserve the official after-key boundary.");
        string originalLock = cloud.Item("a").WriteLock;
        await cloud.SetPrivateAsync("a", new { n = 4 }, originalLock, default);
        var conflict = await Failure<UgsCloudException>(() => cloud.SetPrivateAsync("a", new { n = 5 }, originalLock, default));
        Check(conflict.StatusCode == 409 && cloud.Item("a").Value.GetProperty("n").GetInt32() == 4, "CAS mismatch must preserve existing data.");
        await cloud.SetPrivateAsync("a", new { n = 6 }, null, default);
        Check(cloud.Item("a").Value.GetProperty("n").GetInt32() == 6, "Null/omitted lock deliberately models unconditional official upsert, not fake create-only CAS.");
    }

    private static async Task LargeRooms()
    {
        using var h = await Harness.Create();
        for (int room = 0; room < 50; room++)
        {
            Profile Make(int member) => new(new string('p', 58) + (room * 5 + member).ToString("D6"),
                "Name" + (room * 5 + member).ToString("D16"), 1, new Appearance(), new Loadout());
            var party = h.Parties.Create(Make(0)).Party!;
            for (int member = 1; member < 5; member++) h.Parties.Join(Make(member), party.Id);
        }
        Check(h.Parties.ActivePlayerIds().Length == 250, "Limit test must exercise 50 actual five-member PartyStore parties.");
        h.Submit(Alice, "rooms");
        await h.Worker.ProcessOnceAsync(default);
        var done = h.Cloud.Slot(Alice);
        Check(done.State == "done" && done.Response != null, "Large room-list operation must return a controlled completion.");
        Check(JsonSerializer.SerializeToUtf8Bytes(done, Json).Length <= 65536,
            "The entire serialized slot, including escaped response JSON and metadata, must fit the mailbox bound.");
        Check(done.Response!.Status is 200 or 503, "Large response must be returned within bounds or rejected as unavailable.");
        if (done.Response.Status == 503)
            Check(Body<ApiError>(done.Response).Code == "response_limit", "Oversized response must use the fixed response_limit error.");
        else Check(Body<PartyList>(done.Response).Rooms.Length == 50, "Successful bounded response must retain the intended room limit.");
    }

    private static async Task WaitUntil(Func<bool> predicate, string label, int timeoutMilliseconds = 7500)
    {
        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (!predicate() && Environment.TickCount64 < deadline) await Task.Delay(20);
        Check(predicate(), label);
    }

    private static async Task StopWorker(UgsPvpWorker worker)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await worker.StopAsync(timeout.Token);
    }

    private static HttpRequestException SyntheticTlsFailure() => new("Synthetic TLS handshake failure.",
        new IOException("Synthetic connection reset.", new System.Net.Sockets.SocketException(10054)));

    private static Task StartupNetworkRecovery() => StartupRecovery(SyntheticTlsFailure());

    private static async Task StartupRecovery(Exception initialFailure)
    {
        using var h = Harness.Unstarted();
        h.Cloud.NextBridgeReadFailure = initialFailure;
        try
        {
            Exception? rejected = null;
            try { await h.Worker.StartAsync(default); }
            catch (Exception error) { rejected = error; }
            Check(rejected == null, "A transient TLS failure must not reject service startup; observed " + rejected?.GetType().Name + ".");
            await WaitUntil(() => h.Cloud.BridgeReads == 1, "Startup must attempt its first real cloud read.");
            Check(!h.Worker.IsReady && !h.Worker.HasLiveLease && h.Worker.LeaseSecondsRemaining == 0,
                "Failed initial TLS transport must leave the running worker unhealthy and without ownership.");
            await WaitUntil(() => h.Worker.HasLiveLease, "The same worker must acquire a confirmed lease after the network recovers.");
            Check(h.Cloud.BridgeReads == 2 && h.Cloud.Item("bridge").Value.GetProperty("epoch").GetString() == h.Worker.Epoch,
                "A successful retry must use this instance's epoch and avoid launching a second worker.");
        }
        finally { await StopWorker(h.Worker); }
    }

    private static async Task StartupTransientRecovery()
    {
        foreach (Exception failure in new Exception[]
        {
            new TaskCanceledException("Synthetic request timeout."),
            new UgsCloudException(408), new UgsCloudException(429), new UgsCloudException(503)
        })
            await StartupRecovery(failure);
    }

    private static async Task StartupLostWriteResponse()
    {
        using var h = Harness.Unstarted();
        h.Cloud.LoseNextLeaseWriteResponse = true;
        try
        {
            await h.Worker.StartAsync(default);
            await WaitUntil(() => h.Cloud.LostLeaseResponses == 1, "The fake cloud must commit one lease before losing its HTTP response.");
            Check(!h.Worker.IsReady && !h.Worker.HasLiveLease && h.Worker.LeaseSecondsRemaining == 0,
                "An unconfirmed POST must not publish ready or healthy status even when the cloud committed it.");
            Check(h.Cloud.Item("bridge").Value.GetProperty("epoch").GetString() == h.Worker.Epoch,
                "The lost response fixture must actually leave this instance's live epoch in cloud storage.");
            await WaitUntil(() => h.Worker.HasLiveLease, "The same instance must recover its own committed epoch after a lost response.");
            Check(h.Cloud.BridgeReads == 2 && h.Cloud.BridgeWrites == 2 && h.Worker.IsReady,
                "Retry must confirm ownership without treating its own existing epoch as a foreign live worker.");
        }
        finally { await StopWorker(h.Worker); }
    }

    private static async Task StartupCancelledDelay()
    {
        using var h = Harness.Unstarted();
        h.Cloud.FailAllBridgeReads = true;
        try
        {
            await h.Worker.StartAsync(default);
            await WaitUntil(() => h.Cloud.BridgeReads == 1, "Cancellation fixture must enter the bootstrap retry delay.");
            await StopWorker(h.Worker);
            int reads = h.Cloud.BridgeReads;
            await Task.Delay(250);
            Check(h.Worker.ExecuteTask?.IsCompleted == true && h.Cloud.BridgeReads == reads && h.Cloud.BridgeWrites == 0,
                "Stopping bootstrap must finish its execution task with no extra retry or unowned cleanup cloud call.");
            Check(!h.Worker.IsReady && !h.Worker.HasLiveLease && h.Worker.LeaseSecondsRemaining == 0,
                "A stopped, uninitialized worker must remain unhealthy.");
        }
        finally { await StopWorker(h.Worker); }
    }

    private static async Task StartupCancelledWrite()
    {
        using var h = Harness.Unstarted();
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Cloud.HoldLeaseWriteCompletion = completion;
        try
        {
            await h.Worker.StartAsync(default);
            await WaitUntil(() => h.Cloud.PendingLeaseWrites == 1, "Stop-race fixture must await a committed initial POST response.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            Task stopping = h.Worker.StopAsync(timeout.Token);
            Check(!h.Worker.IsReady && !h.Worker.HasLiveLease, "Stop must invalidate health before an outstanding cloud call finishes.");
            completion.TrySetResult("synthetic-confirmed-write-lock");
            await stopping;
            int reads = h.Cloud.BridgeReads;
            await Task.Delay(250);
            Check(h.Worker.ExecuteTask?.IsCompleted == true && !h.Worker.IsReady && !h.Worker.HasLiveLease &&
                h.Worker.LeaseSecondsRemaining == 0 && h.Cloud.BridgeReads == reads && h.Cloud.BridgeWrites == 1,
                "A POST response arriving after cancellation must not republish healthy state or start another retry.");
        }
        finally { completion.TrySetResult("synthetic-confirmed-write-lock"); await StopWorker(h.Worker); }
    }

    private static async Task<Exception> FatalStartup(UgsPvpWorker worker)
    {
        try { await worker.StartAsync(default); }
        catch (Exception error) { return error; }
        await WaitUntil(() => worker.ExecuteTask?.IsCompleted == true, "A fatal initialization error must terminate worker execution.", 2000);
        try { await worker.ExecuteTask!; }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Fatal startup unexpectedly completed without an exception.");
    }

    private static async Task StartupPermissionDenied()
    {
        using var h = Harness.Unstarted();
        h.Cloud.NextBridgeReadFailure = new UgsCloudException(403);
        try
        {
            Exception failure = await FatalStartup(h.Worker);
            Check(failure is UgsCloudException { StatusCode: 403 }, "Cloud permission denial must remain a fatal, identifiable failure.");
            await Task.Delay(250);
            Check(h.Cloud.BridgeReads == 1 && h.Cloud.BridgeWrites == 0 && !h.Worker.IsReady && !h.Worker.HasLiveLease,
                "A 403 must not retry or overwrite cloud data, and must never advertise readiness.");
        }
        finally { await StopWorker(h.Worker); }
    }

    private static async Task StartupForeignLease()
    {
        using var h = Harness.Unstarted();
        const string foreignEpoch = "ffffffffffffffffffffffffffffffff";
        h.Cloud.Seed("bridge", new UgsPvpLease(1, foreignEpoch, h.Now + 15000));
        try
        {
            Exception failure = await FatalStartup(h.Worker);
            Check(failure is InvalidOperationException, "An existing live foreign epoch must fail initialization.");
            await Task.Delay(250);
            Check(h.Cloud.BridgeReads == 1 && h.Cloud.BridgeWrites == 0 && !h.Worker.IsReady && !h.Worker.HasLiveLease,
                "Foreign ownership must not be retried, overwritten or advertised as healthy.");
            Check(h.Cloud.Item("bridge").Value.GetProperty("epoch").GetString() == foreignEpoch,
                "Fatal ownership refusal must preserve the current owner's lease.");
        }
        finally { await StopWorker(h.Worker); }
    }

    private sealed class Harness : IDisposable
    {
        public ManualClock Clock { get; } = new();
        public FakeCloud Cloud { get; } = new() { PageSize = 2 };
        public UgsProfiles Profiles { get; }
        public PartyStore Parties { get; }
        public UgsTicketStore Tickets { get; }
        public UgsPvpWorker Worker { get; }
        private int requestSequence;
        public long Now => Clock.GetUtcNow().ToUnixTimeMilliseconds();
        private readonly UgsBridgeOptions options = new() { Enabled = true, PollSeconds = 1 };

        private Harness()
        {
            Cloud.SetProfile(Alice, 1); Cloud.SetProfile(Bravo, 1);
            Profiles = new(Cloud);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["PvpMatchmaking:ServerAddress"] = "203.0.113.10", ["PvpMatchmaking:StartPort"] = "7777", ["PvpMatchmaking:ServerCount"] = "2" }).Build();
            Parties = new(config, Clock); Tickets = new(Clock, Profiles, Parties); Worker = NewWorker();
        }
        public static async Task<Harness> Create()
        {
            var value = new Harness(); await value.Worker.InitializeAsync(default); return value;
        }
        public static Harness Unstarted() => new();
        public UgsPvpWorker NewWorker() => new(options, Cloud, Profiles, Tickets, Parties, Clock, NullLogger<UgsPvpWorker>.Instance);
        public UgsPvpSlot MakeSlot(string id, string command, object? args = null) => new(1, Worker.Epoch, id,
            (++requestSequence).ToString("x32"), id == Alice ? "Alice_01" : "Bravo_01", command,
            JsonSerializer.SerializeToElement(args ?? new { }, Json), Now, Now + 30000, "pending", null);
        public UgsPvpSlot Submit(string id, string command, object? args = null)
        {
            var slot = MakeSlot(id, command, args); Cloud.Seed("p_" + id, slot); return slot;
        }
        public async Task<UgsPvpResponse> Command(string id, string command, object? args = null, int expected = 200)
        {
            var submitted = Submit(id, command, args); await Worker.ProcessOnceAsync(default);
            var done = Cloud.Slot(id);
            Check(done.RequestId == submitted.RequestId && done.State == "done" && done.Response != null,
                $"{command} must complete only its own request.");
            Check(done.Response!.Status == expected, $"{command} returned {done.Response.Status}: {done.Response.Body}");
            return done.Response;
        }
        public async Task Match(string a, string b)
        {
            await Command(a, "create"); await Command(b, "create");
            await Command(a, "queue"); await Command(b, "queue");
            Parties.MatchQueued(new[] { 7777 });
            Check(Parties.Reservation(7777).MatchId.Length == 32, "The actual PartyStore must reserve a matched server.");
        }
        public void Dispose() => Worker.Dispose();
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset value = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => value;
        public void Advance(TimeSpan amount) => value += amount;
    }

    private sealed class FakeCloud : IUgsCloudStore, IUgsPersonalCloudStore
    {
        private readonly SortedDictionary<string, UgsCloudItem> items = new(StringComparer.Ordinal);
        private long lockSequence;
        private int bridgeReads;
        private int bridgeWrites, lostLeaseResponses, pendingLeaseWrites;
        public int BridgeReads => Volatile.Read(ref bridgeReads);
        public int BridgeWrites => Volatile.Read(ref bridgeWrites);
        public int LostLeaseResponses => Volatile.Read(ref lostLeaseResponses);
        public int PendingLeaseWrites => Volatile.Read(ref pendingLeaseWrites);
        public Exception? NextBridgeReadFailure { get; set; }
        public bool FailAllBridgeReads { get; set; }
        public bool LoseNextLeaseWriteResponse { get; set; }
        public TaskCompletionSource<string>? HoldLeaseWriteCompletion { get; set; }
        public Dictionary<string, JsonElement> Protected { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, JsonElement> Personal { get; } = new(StringComparer.Ordinal);
        public void SetPersonal(string id, int revision, string name)
            => Personal[id] = JsonSerializer.SerializeToElement(new { revision, personal = new { displayName = name, gender = 0, signature = "" } }, Json);
        public Task<JsonElement?> GetProtectedPersonalAsync(string playerId, CancellationToken cancel)
            => Task.FromResult(Personal.TryGetValue(playerId, out var value) ? (JsonElement?)value : null);
        public int PageSize { get; init; } = 20;
        public int ProtectedReads { get; private set; }
        public int ListCalls { get; private set; }
        public int Deletes { get; private set; }
        public int Conflicts { get; private set; }
        public string? ConflictDoneKey { get; set; }
        public JsonElement? LastConflictingDone { get; private set; }
        public bool FailNextLeaseWrite { get; set; }

        public void SetProfile(string id, int revision, Appearance? appearance = null, byte avatar = 0)
        {
            Protected[id] = JsonSerializer.SerializeToElement(new
            {
                accountId = id, username = id == Alice ? "Alice_01" : "Bravo_01", revision, avatarId = avatar,
                appearance = appearance ?? new Appearance(), loadout = new Loadout(), match = (object?)null
            }, Json);
        }
        public void Seed(string key, object value) => items[key] = new(key, JsonSerializer.SerializeToElement(value, value.GetType(), Json), (++lockSequence).ToString());
        public bool Contains(string key) => items.ContainsKey(key);
        public UgsCloudItem Item(string key) => items[key];
        public UgsPvpSlot Slot(string id) => items["p_" + id].Value.Deserialize<UgsPvpSlot>(Json)!;
        public Task<UgsCloudItem?> GetPrivateAsync(string key, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            if (key == "bridge")
            {
                Interlocked.Increment(ref bridgeReads);
                if (FailAllBridgeReads) throw SyntheticTlsFailure();
                if (NextBridgeReadFailure is { } failure)
                {
                    NextBridgeReadFailure = null;
                    throw failure;
                }
            }
            return Task.FromResult(items.TryGetValue(key, out var item) ? item : null);
        }
        public Task<UgsCloudPage> ListPrivateAsync(string? after, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested(); ListCalls++;
            var remaining = items.Values.Where(x => after == null || string.CompareOrdinal(x.Key, after) > 0).ToArray();
            var page = remaining.Take(PageSize).ToArray();
            return Task.FromResult(new UgsCloudPage(page, remaining.Length > page.Length ? page[^1].Key : null));
        }
        public Task<string> SetPrivateAsync(string key, object value, string? writeLock, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            if (key == "bridge") Interlocked.Increment(ref bridgeWrites);
            if (key == "bridge" && FailNextLeaseWrite)
            {
                FailNextLeaseWrite = false;
                throw new UgsCloudException(503);
            }
            var json = JsonSerializer.SerializeToElement(value, value.GetType(), Json);
            if (key == ConflictDoneKey && json.TryGetProperty("state", out var state) && state.GetString() == "done")
            {
                ConflictDoneKey = null; Conflicts++; LastConflictingDone = json;
                // A concurrent mailbox touch keeps this same pending request, but advances
                // its real lock. A retry must fetch the new lock as well as reuse its result.
                if (items.TryGetValue(key, out var concurrent))
                    items[key] = concurrent with { WriteLock = (++lockSequence).ToString() };
                throw new UgsCloudException(409);
            }
            if (writeLock != null && (!items.TryGetValue(key, out var old) || old.WriteLock != writeLock)) throw new UgsCloudException(409);
            string next = (++lockSequence).ToString(); items[key] = new(key, json, next);
            if (key == "bridge" && LoseNextLeaseWriteResponse)
            {
                LoseNextLeaseWriteResponse = false;
                Interlocked.Increment(ref lostLeaseResponses);
                throw SyntheticTlsFailure();
            }
            if (key == "bridge" && HoldLeaseWriteCompletion is { } completion)
            {
                HoldLeaseWriteCompletion = null;
                Interlocked.Increment(ref pendingLeaseWrites);
                return completion.Task;
            }
            return Task.FromResult(next);
        }
        public Task DeletePrivateAsync(string key, string writeLock, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            if (!items.TryGetValue(key, out var old)) throw new UgsCloudException(404);
            if (old.WriteLock != writeLock) throw new UgsCloudException(409);
            items.Remove(key); Deletes++; return Task.CompletedTask;
        }
        public Task<JsonElement?> GetProtectedProfileAsync(string playerId, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested(); ProtectedReads++;
            return Task.FromResult(Protected.TryGetValue(playerId, out var value) ? (JsonElement?)value.Clone() : null);
        }
    }
}
