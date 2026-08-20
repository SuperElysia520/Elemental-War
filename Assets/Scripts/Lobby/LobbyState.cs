using System;
using System.Collections.Generic;
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

    public LobbyPlayerData(ulong clientId, string playerName, LobbyTeam team)
    {
        ClientId = clientId;
        PlayerName = new FixedString64Bytes(playerName);
        Team = team;
        CharacterIndex = byte.MaxValue;
    }

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref ClientId);
        serializer.SerializeValue(ref PlayerName);

        byte teamValue = (byte)Team;
        serializer.SerializeValue(ref teamValue);
        serializer.SerializeValue(ref CharacterIndex);
        if (serializer.IsReader)
            Team = (LobbyTeam)teamValue;
    }

    public bool Equals(LobbyPlayerData other)
    {
        return ClientId == other.ClientId && PlayerName.Equals(other.PlayerName) &&
               Team == other.Team && CharacterIndex == other.CharacterIndex;
    }
}

/// <summary>
/// 大厅状态（NetworkBehaviour）：房间成员、玩法模式、PVP 分队与开局状态均由服务器权威。
/// </summary>
public class LobbyState : NetworkBehaviour
{
    public const int PvpTeamCapacity = 5;
    public const int PvpCharacterCount = 3;
    public const int PvpKillTarget = 40;

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

    /// <summary>PVP 团队击杀比分及胜者，不参与 PVE。</summary>
    public NetworkVariable<int> RedKills = new NetworkVariable<int>(0);
    public NetworkVariable<int> BlueKills = new NetworkVariable<int>(0);
    public NetworkVariable<byte> WinnerTeamValue = new NetworkVariable<byte>((byte)LobbyTeam.None);

    /// <summary>本地 UI 刷新回调（成员、队伍、房间名或状态变化时触发）。</summary>
    public event Action OnChanged;

    public int PlayerCount => GameMode == LobbyGameMode.PVP ? PvpPlayers.Count : PlayerNames.Count;
    public LobbyGameMode GameMode => (LobbyGameMode)GameModeValue.Value;
    public LobbyTeam WinnerTeam => (LobbyTeam)WinnerTeamValue.Value;
    public bool IsPvpMatchOver => WinnerTeam != LobbyTeam.None;

    public override void OnNetworkSpawn()
    {
        PlayerNames.OnListChanged += HandlePlayerNamesChanged;
        PvpPlayers.OnListChanged += HandlePvpPlayersChanged;
        IsStarted.OnValueChanged += HandleBoolChanged;
        RoomName.OnValueChanged += HandleStringChanged;
        GameModeValue.OnValueChanged += HandleByteChanged;
        RedKills.OnValueChanged += HandleIntChanged;
        BlueKills.OnValueChanged += HandleIntChanged;
        WinnerTeamValue.OnValueChanged += HandleByteChanged;

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
        RedKills.OnValueChanged -= HandleIntChanged;
        BlueKills.OnValueChanged -= HandleIntChanged;
        WinnerTeamValue.OnValueChanged -= HandleByteChanged;
    }

    private void HandlePlayerNamesChanged(NetworkListEvent<FixedString64Bytes> changeEvent) => OnChanged?.Invoke();
    private void HandlePvpPlayersChanged(NetworkListEvent<LobbyPlayerData> changeEvent) => OnChanged?.Invoke();
    private void HandleBoolChanged(bool previousValue, bool newValue) => OnChanged?.Invoke();
    private void HandleStringChanged(FixedString64Bytes previousValue, FixedString64Bytes newValue) => OnChanged?.Invoke();
    private void HandleByteChanged(byte previousValue, byte newValue) => OnChanged?.Invoke();
    private void HandleIntChanged(int previousValue, int newValue) => OnChanged?.Invoke();

    // ---- 服务器端操作 ----

    public void Initialize(string roomName, LobbyGameMode gameMode)
    {
        if (!IsServer)
            return;

        RoomName.Value = new FixedString64Bytes(roomName);
        GameModeValue.Value = (byte)gameMode;
        IsStarted.Value = false;
        if (gameMode == LobbyGameMode.PVP)
        {
            RedKills.Value = 0;
            BlueKills.Value = 0;
            WinnerTeamValue.Value = (byte)LobbyTeam.None;
        }
    }

    /// <summary>PVE 原有房间名设置接口。</summary>
    public void SetRoomName(string roomName)
    {
        if (IsServer)
            RoomName.Value = new FixedString64Bytes(roomName);
    }

    /// <summary>PVE 原有添加成员接口。</summary>
    public void AddPlayer(string playerName)
    {
        if (!IsServer)
            return;
        PlayerNames.Add(new FixedString64Bytes(playerName));
    }

    /// <summary>PVE 原有移除成员接口。</summary>
    public void RemovePlayer(string playerName)
    {
        if (!IsServer)
            return;
        PlayerNames.Remove(new FixedString64Bytes(playerName));
    }

    /// <summary>PVP 专用添加成员接口。</summary>
    public void AddPvpPlayer(ulong clientId, string playerName)
    {
        if (!IsServer || FindPlayerIndex(clientId) >= 0)
            return;

        PvpPlayers.Add(new LobbyPlayerData(clientId, SanitizePlayerName(playerName), LobbyTeam.None));
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

    /// <summary>任意客户端请求给自己换队；服务器验证身份、状态和 5 人容量。</summary>
    [ServerRpc(RequireOwnership = false)]
    public void RequestSetTeamServerRpc(byte requestedTeamValue, ServerRpcParams rpcParams = default)
    {
        if (GameMode != LobbyGameMode.PVP || IsStarted.Value)
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
        if (GameMode != LobbyGameMode.PVP || IsStarted.Value || characterIndex >= PvpCharacterCount)
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
        string value = string.IsNullOrWhiteSpace(playerName) ? "玩家" : playerName.Trim();
        return value.Length <= 24 ? value : value.Substring(0, 24);
    }
}
