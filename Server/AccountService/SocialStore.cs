using System.Text.Encodings.Web;
using System.Text.Json;

namespace Fps.AccountService;

public sealed record SocialRequest(string? Command, string? Target = null, string? Text = null, string? Nonce = null);
public sealed record SocialPlayer(string Id, string Uid, string Name, byte Avatar, string Signature);
public sealed record SocialContact(SocialPlayer Player, string Relation, string Presence, int Unread, string Preview, long Activity);
public sealed record SocialInvite(string PartyId, string MapName, int Members, long Expires, string State);
public sealed record SocialMessage(string Id, string Sender, string Name, byte Avatar, string Text, long Time, SocialInvite? Invite = null);
public sealed record SocialSnapshot(SocialContact[] Contacts, SocialMessage[] Messages, string PartyId, int Requests, int Unread, int Invites);
public sealed record SocialLink(string From, string To, bool Accepted);
public sealed record SocialThread(List<SocialMessage> Messages, Dictionary<string, string> Read);
public sealed record SocialDatabase(Dictionary<string, SocialPlayer> Players, List<SocialLink> Links,
    Dictionary<string, HashSet<string>> Blocks, Dictionary<string, SocialThread> Threads);

/// <summary>Single authority for both transports. All checks and changes to social state are serialized here.</summary>
public sealed class SocialStore
{
    private const int FriendLimit = 100;
    private const int RequestLimit = 30;
    private const int HistoryLimit = 40;
    private const int ReturnedMessages = 16;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly object gate = new();
    private readonly string path;
    private readonly PartyStore parties;
    private readonly TimeProvider clock;
    private readonly Dictionary<string, long> presence = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> lastSend = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<SocialMessage>> teamMessages = new(StringComparer.Ordinal);
    private SocialDatabase data;

    public SocialStore(IConfiguration configuration, IWebHostEnvironment environment, PartyStore parties, TimeProvider clock)
    {
        string accountPath = Path.GetFullPath(configuration["AccountStorePath"] ?? "data/accounts.json", environment.ContentRootPath);
        path = Path.GetFullPath(configuration["SocialStorePath"] ?? Path.Combine(Path.GetDirectoryName(accountPath)!, "social-v1.json"),
            environment.ContentRootPath);
        this.parties = parties;
        this.clock = clock;
        data = Load();
    }

    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();

    public object Execute(Profile actor, string uid, SocialRequest request, bool returnConversation = false)
    {
        Validate(request);
        lock (gate)
        {
            string me = actor.AccountId;
            SocialPlayer player = new(me, uid, actor.DisplayName, actor.AvatarId, actor.Personal?.Signature ?? "");
            if (data.Players.GetValueOrDefault(me) != player)
            {
                data.Players[me] = player;
                Save();
            }
            presence[me] = Now;
            string target = request.Target ?? "";
            switch (request.Command)
            {
                case "sync": return Snapshot(me, target);
                case "search": return Search(me, request.Text!.Trim());
                case "team-send": SendTeam(player, request); break;
                case "accept-invite": RespondInvite(actor, target, request.Nonce!, true); break;
                case "decline-invite": RespondInvite(actor, target, request.Nonce!, false); break;
                default: ChangeRelationshipOrSend(actor, player, target, request); break;
            }
            if (returnConversation && request.Command is "send" or "team-send")
                return new { ok = true, snapshot = Snapshot(me, request.Command == "team-send" ? "@team" : target, markRead: false) };
            return new { ok = true };
        }
    }

    private void ChangeRelationshipOrSend(Profile actor, SocialPlayer player, string target, SocialRequest request)
    {
        string me = actor.AccountId;
        if (target == me || !data.Players.ContainsKey(target)) throw Reject("player_missing", "找不到该玩家。");
        SocialLink? link = Link(me, target);
        if (request.Command is not ("block" or "unblock") && IsBlocked(me, target))
            throw Reject("player_unavailable", "暂时无法与该玩家互动。");
        switch (request.Command)
        {
            case "request":
                if (link != null)
                {
                    if (!link.Accepted && link.To == me) throw Reject("incoming_request", "对方已发送申请，请在好友申请中处理。");
                    return;
                }
                if (data.Links.Count(x => !x.Accepted && x.From == me) >= RequestLimit ||
                    data.Links.Count(x => !x.Accepted && x.To == target) >= RequestLimit)
                    throw Reject("request_limit", "待处理好友申请已达上限。");
                RequireFriendCapacity(me, target);
                data.Links.Add(new(me, target, false));
                break;
            case "accept":
                if (link?.Accepted == true) return;
                if (link?.To != me) throw Reject("request_missing", "这条好友申请已失效。");
                RequireFriendCapacity(me, target);
                data.Links[data.Links.IndexOf(link)] = link with { Accepted = true };
                break;
            case "reject":
                if (link?.Accepted == false) data.Links.Remove(link);
                break;
            case "remove":
                if (link?.Accepted == true) data.Links.Remove(link);
                data.Threads.Remove(ThreadKey(me, target));
                break;
            case "block":
                if (!data.Blocks.TryGetValue(me, out var blocked)) data.Blocks[me] = blocked = new(StringComparer.Ordinal);
                if (blocked.Count >= FriendLimit && !blocked.Contains(target)) throw Reject("block_limit", "黑名单已满。");
                blocked.Add(target);
                if (link != null) data.Links.Remove(link);
                data.Threads.Remove(ThreadKey(me, target));
                break;
            case "unblock":
                if (data.Blocks.TryGetValue(me, out var list)) list.Remove(target);
                break;
            case "send":
            case "invite":
                RequireFriend(me, target);
                SendPrivate(actor, player, target, request);
                return;
        }
        Save();
    }

    private void SendPrivate(Profile actor, SocialPlayer player, string target, SocialRequest request)
    {
        string key = ThreadKey(actor.AccountId, target);
        var thread = data.Threads.GetValueOrDefault(key);
        if (thread?.Messages.Any(x => x.Sender == actor.AccountId && x.Id == request.Nonce) == true) return;
        CheckSendRate(actor.AccountId);
        SocialInvite? invite = null;
        string text = request.Text?.Trim() ?? "";
        if (request.Command == "invite")
        {
            if (thread?.Messages.Any(x => x.Sender == actor.AccountId && x.Invite is { State: "pending" } i &&
                i.Expires > Now && parties.SocialParty(actor.AccountId)?.Id == i.PartyId) == true)
                throw Reject("invite_pending", "已有一条等待回应的组队邀请。");
            PartyView party = parties.InvitationParty(actor, target);
            invite = new(party.Id, party.MapName, party.Members.Length, Now + 120_000, "pending");
            text = "邀请你加入队伍";
        }
        if (thread == null) data.Threads[key] = thread = new([], new(StringComparer.Ordinal));
        Append(thread.Messages, new(request.Nonce!, actor.AccountId, player.Name, player.Avatar, text, Now, invite));
        Save();
        lastSend[actor.AccountId] = Now;
    }

    private void SendTeam(SocialPlayer player, SocialRequest request)
    {
        PartyView party = parties.SocialParty(player.Id) ?? throw Reject("no_party", "加入队伍后即可聊天。");
        if (party.Id != request.Target) throw Reject("party_changed", "当前队伍已变化，请重新发送。");
        if (!teamMessages.TryGetValue(party.Id, out var messages)) teamMessages[party.Id] = messages = [];
        if (messages.Any(x => x.Sender == player.Id && x.Id == request.Nonce)) return;
        CheckSendRate(player.Id);
        Append(messages, new(request.Nonce!, player.Id, player.Name, player.Avatar, request.Text!.Trim(), Now));
        lastSend[player.Id] = Now;
    }

    private void RespondInvite(Profile actor, string from, string id, bool accept)
    {
        RequireFriend(actor.AccountId, from);
        var thread = data.Threads.GetValueOrDefault(ThreadKey(actor.AccountId, from));
        int index = thread?.Messages.FindIndex(m => m.Id == id && m.Sender == from && m.Invite != null) ?? -1;
        if (thread == null || index < 0) throw Reject("invite_missing", "这条组队邀请已失效。");
        SocialMessage message = thread.Messages[index];
        SocialInvite invite = message.Invite!;
        string state = accept ? "accepted" : "declined";
        if (invite.State == state) return;
        if (invite.State != "pending" || invite.Expires <= Now) throw Reject("invite_expired", "这条组队邀请已失效。");
        if (accept) parties.JoinInvitation(actor, from, invite.PartyId);
        thread.Messages[index] = message with { Invite = invite with { State = state } };
        Save();
    }

    private SocialSnapshot Snapshot(string me, string target, bool markRead = true)
    {
        PartyView? party = parties.SocialParty(me);
        var activeParties = parties.SocialPartyIds();
        foreach (string id in teamMessages.Keys.Where(id => !activeParties.Contains(id)).ToArray()) teamMessages.Remove(id);
        var contacts = new List<SocialContact>();
        var messages = Array.Empty<SocialMessage>();
        int requests = 0, unread = 0, invites = 0;
        foreach (SocialLink link in data.Links.Where(l => l.From == me || l.To == me))
        {
            string other = link.From == me ? link.To : link.From;
            if (IsBlocked(me, other)) continue;
            string relation = link.Accepted ? "friend" : link.To == me ? "incoming" : "outgoing";
            if (relation == "incoming") requests++;
            var thread = link.Accepted ? data.Threads.GetValueOrDefault(ThreadKey(me, other)) : null;
            var last = thread?.Messages.LastOrDefault();
            int count = 0;
            if (thread != null && last != null)
            {
                if (target == other)
                {
                    messages = thread.Messages.TakeLast(ReturnedMessages).Select(DescribeInvite).ToArray();
                    string marker = Marker(last);
                    if (markRead && thread.Read.GetValueOrDefault(me) != marker)
                    {
                        thread.Read[me] = marker;
                        Save();
                    }
                }
                int readIndex = thread.Messages.FindIndex(x => Marker(x) == thread.Read.GetValueOrDefault(me));
                count = thread.Messages.Skip(readIndex + 1).Count(x => x.Sender == other);
                invites += thread.Messages.Count(x => x.Sender == other && x.Invite != null &&
                    x.Invite.State == "pending" && x.Invite.Expires > Now && activeParties.Contains(x.Invite.PartyId));
            }
            unread += count;
            contacts.Add(Contact(other, relation, count, last));
        }
        if (data.Blocks.TryGetValue(me, out var blocked))
            contacts.AddRange(blocked.Select(id => Contact(id, "blocked", 0, null)));
        if (target == "@team" && party != null && teamMessages.TryGetValue(party.Id, out var team))
            messages = team.TakeLast(ReturnedMessages).ToArray();
        return new(contacts.ToArray(), messages, party?.Id ?? "", requests, unread, invites);
    }

    private SocialMessage DescribeInvite(SocialMessage message)
    {
        var invite = message.Invite;
        if (invite is not { State: "pending" }) return message;
        var party = parties.SocialParty(message.Sender);
        bool active = invite.Expires > Now && party?.Id == invite.PartyId;
        return message with { Invite = invite with { State = active ? "pending" : "expired", Members = active ? party!.Members.Length : invite.Members } };
    }

    private SocialContact[] Search(string me, string query)
    {
        if (query.Length == 0) throw Reject("empty_search", "请输入好友昵称或 UID。");
        return data.Players.Values
            .Where(p => p.Id != me && !IsBlocked(me, p.Id) && (p.Uid == query || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(p => p.Uid == query).ThenBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Id, StringComparer.Ordinal)
            .Take(20).Select(p =>
            {
                var link = Link(me, p.Id);
                return Contact(p.Id, link == null ? "" : link.Accepted ? "friend" : link.To == me ? "incoming" : "outgoing", 0, null);
            }).ToArray();
    }

    private SocialContact Contact(string id, string relation, int unread, SocialMessage? last)
    {
        string state = "offline";
        if (relation != "blocked")
        {
            var party = parties.SocialParty(id);
            if (party?.State == "Matched") state = "playing";
            else if (presence.TryGetValue(id, out long seen) && Now - seen < 45_000)
                state = party == null ? "online" : "party";
        }
        string preview = last?.Text.Replace('\n', ' ') ?? "";
        return new(data.Players[id], relation, state, unread, Clip(preview, 24), last?.Time ?? 0);
    }

    private SocialLink? Link(string a, string b) => data.Links.Find(x => x.From == a && x.To == b || x.From == b && x.To == a);
    private bool IsBlocked(string a, string b) => data.Blocks.GetValueOrDefault(a)?.Contains(b) == true || data.Blocks.GetValueOrDefault(b)?.Contains(a) == true;
    private void RequireFriend(string a, string b)
    {
        if (Link(a, b)?.Accepted != true || IsBlocked(a, b)) throw Reject("friend_required", "仅可与已添加的好友互动。");
    }
    private void RequireFriendCapacity(string a, string b)
    {
        if (data.Links.Count(x => x.Accepted && (x.From == a || x.To == a)) >= FriendLimit ||
            data.Links.Count(x => x.Accepted && (x.From == b || x.To == b)) >= FriendLimit)
            throw Reject("friend_limit", "好友人数已达上限。");
    }
    private void CheckSendRate(string sender)
    {
        if (lastSend.TryGetValue(sender, out long last) && Now - last < 750)
            throw new ApiException(429, "send_rate", "发送太快，请稍后再试。");
    }
    private static string Marker(SocialMessage message) => message.Sender + "/" + message.Id;
    private static string ThreadKey(string a, string b) => string.CompareOrdinal(a, b) < 0 ? a + "/" + b : b + "/" + a;
    private static string Clip(string value, int length) => value.Length <= length ? value :
        value[..(char.IsHighSurrogate(value[length - 1]) ? length - 1 : length)] + "…";
    private static void Append(List<SocialMessage> messages, SocialMessage message)
    {
        messages.Add(message);
        if (messages.Count > HistoryLimit) messages.RemoveAt(0);
    }
    private SocialDatabase Load() => File.Exists(path)
        ? JsonSerializer.Deserialize<SocialDatabase>(File.ReadAllBytes(path), Json) ?? throw new InvalidDataException("Empty social database.")
        : new(new(StringComparer.Ordinal), [], new(StringComparer.Ordinal), new(StringComparer.Ordinal));
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, data, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            data = Load();
            throw;
        }
    }

    public static void Validate(SocialRequest request)
    {
        bool target = request.Target != null && UgsProfiles.IsPlayerId(request.Target);
        bool nonce = request.Nonce != null && Guid.TryParseExact(request.Nonce, "N", out _);
        bool text = request.Text is { Length: > 0 and <= 200 } value && !string.IsNullOrWhiteSpace(value) &&
            !value.Any(c => char.IsControl(c) && c != '\n' && c != '\r');
        bool valid = request.Command switch
        {
            "sync" => (request.Target == null || request.Target == "@team" || target) && request.Text == null && request.Nonce == null,
            "search" => request.Target == null && text && request.Text!.Length <= 64 && request.Nonce == null,
            "request" or "accept" or "reject" or "remove" or "block" or "unblock" => target && request.Text == null && request.Nonce == null,
            "send" or "team-send" => target && nonce && text,
            "invite" or "accept-invite" or "decline-invite" => target && nonce && request.Text == null,
            _ => false
        };
        if (!valid) throw new ApiException(400, "invalid_request", "社交请求参数不正确。");
    }

    private static ApiException Reject(string code, string message) => new(409, code, message);
}
