using System;
using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using Unity.Netcode;

/// <summary>大厅对应的玩法模式。</summary>
public enum LobbyGameMode : byte
{
    PVE = 0,
    PVP = 1,
}

/// <summary>PVP 队伍。PVE 玩家和尚未选队的 PVP 玩家使用 None。</summary>
public enum LobbyTeam : byte
{
    None = 0,
    Red = 1,
    Blue = 2,
}

/// <summary>
/// 网络同步的大厅成员数据。ClientId 是稳定身份，避免同名玩家在换队/离开时误操作。
/// </summary>
public struct LobbyPlayerData : INetworkSerializable, IEquatable<LobbyPlayerData>
{
    public ulong ClientId;
    public FixedString64Bytes PlayerName;
    public LobbyTeam Team;
    public byte CharacterIndex;
    public CharacterAppearanceData Appearance;
    public PvpLoadoutData Loadout;

    public LobbyPlayerData(ulong clientId, string playerName, LobbyTeam team)
    {
        ClientId = clientId;
        PlayerName = new FixedString64Bytes(playerName);
        Team = team;
        CharacterIndex = 0;
        Appearance = CharacterAppearanceData.Default;
        Loadout = PvpLoadoutData.Default;
    }

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref ClientId);
        serializer.SerializeValue(ref PlayerName);

        byte teamValue = (byte)Team;
        serializer.SerializeValue(ref teamValue);
        serializer.SerializeValue(ref CharacterIndex);
        serializer.SerializeValue(ref Appearance);
        serializer.SerializeValue(ref Loadout);
        if (serializer.IsReader)
            Team = (LobbyTeam)teamValue;
    }

    public bool Equals(LobbyPlayerData other)
    {
        return ClientId == other.ClientId && PlayerName.Equals(other.PlayerName) &&
               Team == other.Team && CharacterIndex == other.CharacterIndex && Appearance.Equals(other.Appearance) && Loadout.Equals(other.Loadout);
    }
}

/// <summary>
/// 大厅状态（NetworkBehaviour）：房间成员、玩法模式、PVP 分队与开局状态均由服务器权威。
/// </summary>
public class LobbyState : NetworkBehaviour
{
    public const int PvpTeamCapacity = 5;
    public const int PvpCharacterCount = 1;
    public const int PvpKillTarget = 40;
    public const ulong NoRoomOwner = ulong.MaxValue;

    /// <summary>PVE 原有成员列表（按加入顺序，索引 0 = 房主）。</summary>
    public NetworkList<FixedString64Bytes> PlayerNames = new NetworkList<FixedString64Bytes>();

    /// <summary>是否已开局（开局后拒绝加入和换队）。</summary>
    public NetworkVariable<bool> IsStarted = new NetworkVariable<bool>(false);

    /// <summary>房间名。</summary>
    public NetworkVariable<FixedString64Bytes> RoomName = new NetworkVariable<FixedString64Bytes>();

    /// <summary>玩法模式，使用 byte 保持网络序列化简单稳定。</summary>
    public NetworkVariable<byte> GameModeValue = new NetworkVariable<byte>((byte)LobbyGameMode.PVE);

    /// <summary>PVP 专用成员及分队数据，不参与 PVE 房间逻辑。</summary>
    public NetworkList<LobbyPlayerData> PvpPlayers = new NetworkList<LobbyPlayerData>();

    /// <summary>
    /// PVP 房间管理员。Dedicated Server 才是网络服务器，创建房间的玩家只通过这个 ClientId 获得开局权限。
    /// </summary>
    public NetworkVariable<ulong> RoomOwnerClientId = new NetworkVariable<ulong>(NoRoomOwner);

    /// <summary>PVP 团队击杀比分及胜者，不参与 PVE。</summary>
    public NetworkVariable<int> RedKills = new NetworkVariable<int>(0);
    public NetworkVariable<int> BlueKills = new NetworkVariable<int>(0);
    public NetworkVariable<byte> WinnerTeamValue = new NetworkVariable<byte>((byte)LobbyTeam.None);

    /// <summary>Server-time deadline for the shared settlement screen; -1 indicates a return configuration error.</summary>
    public NetworkVariable<double> PvpReturnToLobbyAtServerTime = new NetworkVariable<double>(0d);
    public NetworkVariable<bool> PvpIsPreparingMatch = new NetworkVariable<bool>(false);
    public NetworkVariable<FixedString64Bytes> MatchedSessionId = new NetworkVariable<FixedString64Bytes>();
    public bool IsMatchmaking => !MatchedSessionId.Value.IsEmpty;

    /// <summary>本地 UI 刷新回调（成员、队伍、房间名或状态变化时触发）。</summary>
    public event Action OnChanged;

    public int PlayerCount => GameMode == LobbyGameMode.PVP ? PvpPlayers.Count : PlayerNames.Count;
    public LobbyGameMode GameMode => (LobbyGameMode)GameModeValue.Value;
    public LobbyTeam WinnerTeam => (LobbyTeam)WinnerTeamValue.Value;
    public bool IsPvpMatchOver => WinnerTeam != LobbyTeam.None;
    public bool HasPvpSettlementCountdown => IsPvpMatchOver && PvpReturnToLobbyAtServerTime.Value > 0d;
    public bool HasRoomOwner => RoomOwnerClientId.Value != NoRoomOwner;

    public override void OnNetworkSpawn()
    {
        PlayerNames.OnListChanged += HandlePlayerNamesChanged;
        PvpPlayers.OnListChanged += HandlePvpPlayersChanged;
        IsStarted.OnValueChanged += HandleBoolChanged;
        RoomName.OnValueChanged += HandleStringChanged;
        GameModeValue.OnValueChanged += HandleByteChanged;
        RoomOwnerClientId.OnValueChanged += HandleUlongChanged;
        RedKills.OnValueChanged += HandleIntChanged;
        BlueKills.OnValueChanged += HandleIntChanged;
        WinnerTeamValue.OnValueChanged += HandleByteChanged;
        PvpReturnToLobbyAtServerTime.OnValueChanged += HandleDoubleChanged;
        PvpIsPreparingMatch.OnValueChanged += HandleBoolChanged;
        MatchedSessionId.OnValueChanged += HandleStringChanged;

        if (LobbyManager.Instance != null)
            LobbyManager.Instance.RegisterLobby(this);

        OnChanged?.Invoke();
    }

    public override void OnNetworkDespawn()
    {
        PlayerNames.OnListChanged -= HandlePlayerNamesChanged;
        PvpPlayers.OnListChanged -= HandlePvpPlayersChanged;
        IsStarted.OnValueChanged -= HandleBoolChanged;
        RoomName.OnValueChanged -= HandleStringChanged;
        GameModeValue.OnValueChanged -= HandleByteChanged;
        RoomOwnerClientId.OnValueChanged -= HandleUlongChanged;
        RedKills.OnValueChanged -= HandleIntChanged;
        BlueKills.OnValueChanged -= HandleIntChanged;
        WinnerTeamValue.OnValueChanged -= HandleByteChanged;
        PvpReturnToLobbyAtServerTime.OnValueChanged -= HandleDoubleChanged;
        PvpIsPreparingMatch.OnValueChanged -= HandleBoolChanged;
        MatchedSessionId.OnValueChanged -= HandleStringChanged;
    }

    private void HandlePlayerNamesChanged(NetworkListEvent<FixedString64Bytes> changeEvent) => OnChanged?.Invoke();
    private void HandlePvpPlayersChanged(NetworkListEvent<LobbyPlayerData> changeEvent) => OnChanged?.Invoke();
    private void HandleBoolChanged(bool previousValue, bool newValue) => OnChanged?.Invoke();
    private void HandleStringChanged(FixedString64Bytes previousValue, FixedString64Bytes newValue) => OnChanged?.Invoke();
    private void HandleByteChanged(byte previousValue, byte newValue) => OnChanged?.Invoke();
    private void HandleUlongChanged(ulong previousValue, ulong newValue) => OnChanged?.Invoke();
    private void HandleIntChanged(int previousValue, int newValue) => OnChanged?.Invoke();
    private void HandleDoubleChanged(double previousValue, double newValue) => OnChanged?.Invoke();

    // ---- 服务器端操作 ----

    public void Initialize(string roomName, LobbyGameMode gameMode)
    {
        if (!IsServer)
            return;

        RoomName.Value = new FixedString64Bytes(SanitizeFixedStringText(roomName, "PVP公开房间"));
        GameModeValue.Value = (byte)gameMode;
        IsStarted.Value = false;
        RoomOwnerClientId.Value = NoRoomOwner;
        if (gameMode == LobbyGameMode.PVP)
        {
            RedKills.Value = 0;
            BlueKills.Value = 0;
            WinnerTeamValue.Value = (byte)LobbyTeam.None;
            PvpReturnToLobbyAtServerTime.Value = 0d;
            PvpIsPreparingMatch.Value = false;
        }
    }

    public void BeginPvpSettlement(double returnAtServerTime)
    {
        if (IsServer && GameMode == LobbyGameMode.PVP && IsPvpMatchOver &&
            PvpReturnToLobbyAtServerTime.Value == 0d)
            PvpReturnToLobbyAtServerTime.Value = returnAtServerTime;
    }

    public void MarkPvpReturnUnavailable()
    {
        if (IsServer && GameMode == LobbyGameMode.PVP)
            PvpReturnToLobbyAtServerTime.Value = -1d;
    }

    /// <summary>Clear only this match; the room owner, roster, teams and authenticated profile snapshots remain.</summary>
    public void ResetPvpMatchForLobby()
    {
        if (!IsServer || GameMode != LobbyGameMode.PVP)
            return;
        RedKills.Value = 0;
        BlueKills.Value = 0;
        WinnerTeamValue.Value = (byte)LobbyTeam.None;
        PvpReturnToLobbyAtServerTime.Value = 0d;
        PvpIsPreparingMatch.Value = false;
        IsStarted.Value = false;
    }

    public int GetPvpReturnSecondsRemaining()
    {
        if (!HasPvpSettlementCountdown || NetworkManager == null)
            return 0;
        return Math.Max(0, (int)Math.Ceiling(PvpReturnToLobbyAtServerTime.Value - NetworkManager.ServerTime.Time));
    }

    /// <summary>PVE 原有房间名设置接口。</summary>
    public void SetRoomName(string roomName)
    {
        if (IsServer)
            RoomName.Value = new FixedString64Bytes(SanitizeFixedStringText(roomName, "我的房间"));
    }

    /// <summary>PVE 原有添加成员接口。</summary>
    public void AddPlayer(string playerName)
    {
        if (!IsServer)
            return;
        PlayerNames.Add(new FixedString64Bytes(SanitizePlayerName(playerName)));
    }

    /// <summary>PVE 原有移除成员接口。</summary>
    public void RemovePlayer(string playerName)
    {
        if (!IsServer)
            return;
        PlayerNames.Remove(new FixedString64Bytes(SanitizePlayerName(playerName)));
    }

    /// <summary>PVP 专用添加成员接口。</summary>
    public void AddPvpPlayer(ulong clientId, string playerName, PlayerProfileData profile = null)
    {
        if (!IsServer || FindPlayerIndex(clientId) >= 0)
            return;

        var player = new LobbyPlayerData(clientId, SanitizePlayerName(playerName), LobbyTeam.None);
        if (profile != null) { player.Appearance = profile.appearance.Sanitized(); player.Loadout = profile.loadout.Sanitized(); }
        PvpPlayers.Add(player);
    }

    /// <summary>PVP 专用移除成员接口。</summary>
    public void RemovePvpPlayer(ulong clientId)
    {
        if (!IsServer)
            return;

        int index = FindPlayerIndex(clientId);
        if (index >= 0)
            PvpPlayers.RemoveAt(index);
    }

    public void SetStarted(bool started)
    {
        if (IsServer)
            IsStarted.Value = started;
    }

    public void SetRoomOwner(ulong clientId)
    {
        if (IsServer && GameMode == LobbyGameMode.PVP)
            RoomOwnerClientId.Value = clientId;
    }

    public bool IsRoomOwner(ulong clientId)
    {
        return HasRoomOwner && RoomOwnerClientId.Value == clientId;
    }

    public void SetMatchedTeam(ulong clientId, LobbyTeam team)
    {
        if (!IsServer || !IsMatchmaking || IsStarted.Value) return;
        int index = FindPlayerIndex(clientId);
        if (index < 0) return;
        LobbyPlayerData player = PvpPlayers[index];
        player.Team = team;
        PvpPlayers[index] = player;
    }

    /// <summary>任意客户端请求给自己换队；服务器验证身份、状态和 5 人容量。</summary>
    [ServerRpc(RequireOwnership = false)]
    public void RequestSetTeamServerRpc(byte requestedTeamValue, ServerRpcParams rpcParams = default)
    {
        if (GameMode != LobbyGameMode.PVP || IsMatchmaking || IsStarted.Value || PvpIsPreparingMatch.Value)
            return;

        LobbyTeam requestedTeam = (LobbyTeam)requestedTeamValue;
        if (requestedTeam != LobbyTeam.Red && requestedTeam != LobbyTeam.Blue)
            return;

        ulong senderClientId = rpcParams.Receive.SenderClientId;
        int playerIndex = FindPlayerIndex(senderClientId);
        if (playerIndex < 0)
            return;

        LobbyPlayerData player = PvpPlayers[playerIndex];
        if (player.Team == requestedTeam)
            return;
        if (GetTeamCount(requestedTeam) >= PvpTeamCapacity)
            return;

        player.Team = requestedTeam;
        PvpPlayers[playerIndex] = player;
    }

    /// <summary>玩家选择本局角色；允许多人选择同一个角色。</summary>
    [ServerRpc(RequireOwnership = false)]
    public void RequestSetCharacterServerRpc(byte characterIndex, ServerRpcParams rpcParams = default)
    {
        if (GameMode != LobbyGameMode.PVP || IsStarted.Value || PvpIsPreparingMatch.Value || characterIndex >= PvpCharacterCount)
            return;

        int playerIndex = FindPlayerIndex(rpcParams.Receive.SenderClientId);
        if (playerIndex < 0)
            return;

        LobbyPlayerData player = PvpPlayers[playerIndex];
        if (player.CharacterIndex == characterIndex)
            return;

        player.CharacterIndex = characterIndex;
        PvpPlayers[playerIndex] = player;
    }

    /// <summary>Dedicated PVP 的房间管理员请求开局；最终条件仍由服务器再次验证。</summary>
    [ServerRpc(RequireOwnership = false)]
    public void RequestStartPvpGameServerRpc(ServerRpcParams rpcParams = default)
    {
        if (GameMode != LobbyGameMode.PVP || IsStarted.Value ||
            !IsRoomOwner(rpcParams.Receive.SenderClientId))
        {
            return;
        }

        if (LobbyManager.Instance != null)
            LobbyManager.Instance.HandleDedicatedStartGameRequest(rpcParams.Receive.SenderClientId);
    }

    /// <summary>The backend ticket, rather than client-provided appearance fields, authorizes a saved-profile refresh.</summary>
    [ServerRpc(RequireOwnership = false)]
    public void RequestRefreshProfileServerRpc(string gameTicket, int requestId, int preparationId = 0, ServerRpcParams rpcParams = default)
    {
        if (LobbyManager.Instance != null)
            LobbyManager.Instance.HandlePvpProfileRefreshRequest(rpcParams.Receive.SenderClientId, gameTicket, requestId, preparationId);
    }

    [ClientRpc]
    public void PrepareMatchProfilesClientRpc(int preparationId)
    {
        if (LobbyManager.Instance != null)
            LobbyManager.Instance.PrepareLocalPvpMatchProfile(preparationId);
    }

    [ClientRpc]
    public void CancelMatchPreparationClientRpc(int preparationId)
    {
        if (LobbyManager.Instance != null)
            LobbyManager.Instance.CancelLocalPvpMatchPreparation(preparationId);
    }

    [ServerRpc(RequireOwnership = false)]
    public void FailMatchPreparationServerRpc(int preparationId, string error, ServerRpcParams rpcParams = default)
    {
        if (LobbyManager.Instance != null)
            LobbyManager.Instance.HandlePvpMatchPreparationFailure(rpcParams.Receive.SenderClientId, preparationId, error);
    }

    public bool UpdatePvpPlayerProfile(ulong clientId, PlayerProfileData profile)
    {
        if (!IsServer || GameMode != LobbyGameMode.PVP || IsStarted.Value || profile == null)
            return false;
        int index = FindPlayerIndex(clientId);
        if (index < 0) return false;
        LobbyPlayerData player = PvpPlayers[index];
        player.Appearance = profile.appearance.Sanitized();
        player.Loadout = profile.loadout.Sanitized();
        PvpPlayers[index] = player;
        return true;
    }

    public void SendProfileRefreshResult(ulong clientId, int requestId, string error)
    {
        if (!IsServer || !IsSpawned) return;
        var rpcParams = new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
        };
        ProfileRefreshResultClientRpc(requestId, error ?? string.Empty, rpcParams);
    }

    public void SendStartGameResult(ulong clientId, string message)
    {
        if (!IsServer || !IsSpawned) return;
        var rpcParams = new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
        };
        StartGameResultClientRpc(message ?? string.Empty, rpcParams);
    }

    [ClientRpc]
    private void StartGameResultClientRpc(string message, ClientRpcParams rpcParams = default)
    {
        if (LobbyManager.Instance != null)
            LobbyManager.Instance.DisplayServerStartGameResult(message);
    }

    [ClientRpc]
    private void ProfileRefreshResultClientRpc(int requestId, string error, ClientRpcParams rpcParams = default)
    {
        if (LobbyManager.Instance != null)
            LobbyManager.Instance.CompleteLocalPvpProfileRefresh(requestId, error);
    }

    /// <summary>服务器登记一次有效击杀，先到 40 杀的队伍获胜。</summary>
    public void RegisterPvpKill(LobbyTeam scoringTeam)
    {
        if (!IsServer || GameMode != LobbyGameMode.PVP || IsPvpMatchOver)
            return;

        if (scoringTeam == LobbyTeam.Red)
        {
            RedKills.Value++;
            if (RedKills.Value >= PvpKillTarget)
                WinnerTeamValue.Value = (byte)LobbyTeam.Red;
        }
        else if (scoringTeam == LobbyTeam.Blue)
        {
            BlueKills.Value++;
            if (BlueKills.Value >= PvpKillTarget)
                WinnerTeamValue.Value = (byte)LobbyTeam.Blue;
        }
    }

    // ---- 查询 ----

    public string GetRoomName() => RoomName.Value.ToString();

    public List<LobbyPlayerData> GetPlayers()
    {
        var result = new List<LobbyPlayerData>(PvpPlayers.Count);
        foreach (var player in PvpPlayers)
            result.Add(player);
        return result;
    }

    public List<string> GetPlayerNames()
    {
        if (GameMode == LobbyGameMode.PVP)
        {
            var pvpNames = new List<string>(PvpPlayers.Count);
            foreach (var player in PvpPlayers)
                pvpNames.Add(player.PlayerName.ToString());
            return pvpNames;
        }

        var names = new List<string>(PlayerNames.Count);
        foreach (var playerName in PlayerNames)
            names.Add(playerName.ToString());
        return names;
    }

    public List<LobbyPlayerData> GetTeamPlayers(LobbyTeam team)
    {
        var result = new List<LobbyPlayerData>(PvpTeamCapacity);
        foreach (var player in PvpPlayers)
        {
            if (player.Team == team)
                result.Add(player);
        }
        return result;
    }

    public int GetTeamCount(LobbyTeam team)
    {
        int count = 0;
        foreach (var player in PvpPlayers)
        {
            if (player.Team == team)
                count++;
        }
        return count;
    }

    public LobbyTeam GetPlayerTeam(ulong clientId)
    {
        int index = FindPlayerIndex(clientId);
        return index >= 0 ? PvpPlayers[index].Team : LobbyTeam.None;
    }

    public byte GetPlayerCharacterIndex(ulong clientId)
    {
        int index = FindPlayerIndex(clientId);
        return index >= 0 ? PvpPlayers[index].CharacterIndex : byte.MaxValue;
    }

    public bool HasMinimumPvpTeams()
    {
        return GetTeamCount(LobbyTeam.Red) > 0 && GetTeamCount(LobbyTeam.Blue) > 0;
    }

    private int FindPlayerIndex(ulong clientId)
    {
        for (int i = 0; i < PvpPlayers.Count; i++)
        {
            if (PvpPlayers[i].ClientId == clientId)
                return i;
        }
        return -1;
    }

    private static string SanitizePlayerName(string playerName)
    {
        return SanitizeFixedStringText(playerName, "玩家");
    }

    private static string SanitizeFixedStringText(string value, string fallback)
    {
        string result = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        const int maxUtf8Bytes = 60;
        while (result.Length > 0 && Encoding.UTF8.GetByteCount(result) > maxUtf8Bytes)
            result = result.Substring(0, result.Length - 1);
        if (result.Length > 0 && char.IsHighSurrogate(result[result.Length - 1]))
            result = result.Substring(0, result.Length - 1);
        return string.IsNullOrEmpty(result) ? fallback : result;
    }
}
