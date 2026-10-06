using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

[Serializable] public sealed class PartyMemberData { public PlayerProfileData profile; public bool ready; }
[Serializable] public sealed class PartyMatchData { public string matchId; public string serverAddress; public int serverPort; public byte team; }
[Serializable] public sealed class PartyData
{
    public string id, name, mapName, ownerAccountId, state;
    public PartyMemberData[] members;
    public PartyMatchData match;
}
[Serializable] public sealed class PartyStateData
{
    public PartyData party;
    public string message;
    public static PartyStateData FromJson(string body)
    {
        PartyStateData state = JsonUtility.FromJson<PartyStateData>(body);
        // JsonUtility may materialize JSON null as an empty serializable class. Identity determines existence.
        if (state != null && state.party != null)
        {
            if (string.IsNullOrEmpty(state.party.id)) state.party = null;
            else if (state.party.match != null && string.IsNullOrEmpty(state.party.match.matchId)) state.party.match = null;
        }
        return state;
    }
}
[Serializable] public sealed class PartyRoomListData { public PartyData[] rooms; }

/// <summary>Pre-match party requests use the existing authenticated account service, then hand off to LobbyManager/NGO.</summary>
public sealed class PartyLobbyClient : MonoBehaviour
{
    public static PartyLobbyClient Instance { get; private set; }
    public PartyData Party { get; private set; }
    public PartyData[] Rooms { get; private set; } = new PartyData[0];
    public string Status { get; private set; } = "";
    public bool IsBusy { get; private set; }
    public bool BrowseRooms { get; set; }
    public event Action Changed;
    public bool IsOwner => Party != null && Account.IsAuthenticated && Party.ownerAccountId == Account.Profile.accountId;
    public bool LocalReady
    {
        get
        {
            if (Party == null || Party.members == null || !Account.IsAuthenticated) return false;
            foreach (PartyMemberData member in Party.members)
                if (member.profile.accountId == Account.Profile.accountId) return member.ready;
            return false;
        }
    }
    private AccountClient Account => AccountClient.EnsureInstance();
    private string attemptedMatchId;
    private float connectionStartedAt;
    private bool matchWasStarted;
    private bool requesting;
    private int identityGeneration;
    private string accountId;
    [Serializable] private class JoinRequest { public string partyId; }
    [Serializable] private class ReadyRequest { public bool ready; }

    public static PartyLobbyClient EnsureInstance()
    {
        if (Instance == null)
        {
            AccountClient account = AccountClient.EnsureInstance();
            Instance = account.GetComponent<PartyLobbyClient>();
            if (Instance == null) Instance = account.gameObject.AddComponent<PartyLobbyClient>();
        }
        return Instance;
    }

    private void Awake()
    {
        // A minimized/unfocused multiplayer client must continue polling and keep its party heartbeat alive.
        Application.runInBackground = true;
        Instance = this; Account.ProfileChanged += OnProfileChanged; OnProfileChanged(Account.Profile); StartCoroutine(Poll());
    }
    private void OnDestroy() { if (AccountClient.Instance != null) AccountClient.Instance.ProfileChanged -= OnProfileChanged; if (Instance == this) Instance = null; }

    private void OnProfileChanged(PlayerProfileData profile)
    {
        string next = profile != null ? profile.accountId : null;
        if (next == accountId) return;
        accountId = next; identityGeneration++; Party = null; Rooms = new PartyData[0]; attemptedMatchId = null;
        Status = profile == null ? "请先登录账号。" : "";
        Changed?.Invoke();
    }

    public void Create() => Command("/api/party/create");
    public void Join(string id) => Command("/api/party/join", JsonUtility.ToJson(new JoinRequest { partyId = id }));
    public void ToggleReady() => Command("/api/party/ready", JsonUtility.ToJson(new ReadyRequest { ready = !LocalReady }));
    public void StartMatching() => Command("/api/party/queue");
    public void CancelMatching() => Command("/api/party/cancel");
    public void Leave(Action<bool> completed = null) => Command("/api/party/leave", null, completed);
    public void RefreshRooms() { if (!requesting) StartCoroutine(ReadRooms()); }

    private void Command(string route, string body = null, Action<bool> completed = null)
    {
        if (IsBusy || !Account.IsAuthenticated) { Status = !Account.IsAuthenticated ? "请先登录账号。" : "队伍请求正在处理中。"; Changed?.Invoke(); completed?.Invoke(false); return; }
        IsBusy = true;
        Changed?.Invoke();
        StartCoroutine(SendCommand(route, body, completed));
    }

    private IEnumerator SendCommand(string route, string body, Action<bool> completed)
    {
        // Serialize commands after an outstanding poll so an older response cannot overwrite the command result.
        while (requesting) yield return null;
        requesting = true;
        int generation = identityGeneration;
        bool success = false;
        yield return Account.PartyRequest("POST", route, body, (response, status, error) =>
        {
            if (generation != identityGeneration) return;
            success = ApplyState(response, error);
        });
        requesting = false; IsBusy = false;
        Changed?.Invoke();
        completed?.Invoke(success);
    }

    private IEnumerator Poll()
    {
        while (true)
        {
            if (Account.IsAuthenticated && !requesting && !IsBusy)
            {
                requesting = true;
                int generation = identityGeneration;
                yield return Account.PartyRequest("GET", "/api/party", null, (body, status, error) =>
                {
                    if (generation == identityGeneration) ApplyState(body, error);
                });
                requesting = false;
                if (BrowseRooms && !IsBusy) yield return ReadRooms();
                FollowMatch();
            }
            yield return new WaitForSecondsRealtime(10f);
        }
    }

    private IEnumerator ReadRooms()
    {
        requesting = true;
        int generation = identityGeneration;
        yield return Account.PartyRequest("GET", "/api/party/rooms", null, (body, status, error) =>
        {
            if (generation != identityGeneration) return;
            if (!string.IsNullOrEmpty(error)) Status = error;
            else
            {
                try { Rooms = JsonUtility.FromJson<PartyRoomListData>(body).rooms ?? new PartyData[0]; }
                catch (Exception) { Status = "房间列表数据不完整，请刷新重试。"; }
            }
            Changed?.Invoke();
        });
        requesting = false;
    }

    private bool ApplyState(string body, string error)
    {
        if (!string.IsNullOrEmpty(error)) { Status = error; Changed?.Invoke(); return false; }
        PartyStateData state;
        try { state = PartyStateData.FromJson(body); }
        catch (Exception) { Status = "队伍数据不完整，请重试。"; Changed?.Invoke(); return false; }
        if (state == null) { Status = "队伍数据不完整，请重试。"; Changed?.Invoke(); return false; }
        Party = state.party;
        if (!string.IsNullOrEmpty(state.message)) Status = state.message;
        else if (Party != null) Status = Party.state == "Matching" ? "正在寻找对手…" : Party.state == "Matched" ? "匹配成功，正在连接对局…" : "等待成员准备。";
        Changed?.Invoke();
        return true;
    }

    private void FollowMatch()
    {
        LobbyManager lobby = LobbyManager.Instance;
        if (lobby == null) return;
        if (lobby.CurrentLobby != null && lobby.CurrentLobby.IsMatchmaking && lobby.CurrentLobby.IsStarted.Value) matchWasStarted = true;
        if (Party == null || Party.match == null)
        {
            // The server completed/cancelled the match; release NGO after its return transition, keeping the original HTTP party.
            if (attemptedMatchId != null && SceneManager.GetActiveScene().name != GameFlowScenes.PvpMatch
                && !lobby.IsReturningToLobby && (lobby.CurrentLobby == null || !lobby.CurrentLobby.IsStarted.Value))
            {
                if (lobby.IsInLobby || lobby.IsConnecting) lobby.LeaveRoom();
                attemptedMatchId = null; matchWasStarted = false;
            }
            return;
        }
        if (attemptedMatchId == Party.match.matchId)
        {
            if (!matchWasStarted && !lobby.IsConnecting && !lobby.IsInLobby && Time.unscaledTime - connectionStartedAt > 3f && !IsBusy)
                Command("/api/party/fail");
            return;
        }
        if (Account.IsBusy || lobby.IsConnecting || lobby.IsInLobby) return;
        attemptedMatchId = Party.match.matchId;
        connectionStartedAt = Time.unscaledTime; matchWasStarted = false;
        string address = string.IsNullOrWhiteSpace(Party.match.serverAddress) ? lobby.dedicatedServerAddress : Party.match.serverAddress;
        if (!lobby.TryJoinMatchedPvp(address, Party.match.serverPort, out string message))
        { Status = message; Command("/api/party/fail"); }
        Changed?.Invoke();
    }
}
