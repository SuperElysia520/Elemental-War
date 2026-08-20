using System;
using System.Collections.Generic;
using System.Text;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 局域网大厅总控：建房、加入、连接审批、模式隔离、PVP 分队、切场景与玩家生成。
/// LobbyState 保存并同步服务器权威的房间数据。
/// </summary>
public class LobbyManager : MonoBehaviour
{
    public static LobbyManager Instance { get; private set; }

    [Header("网络")]
    [Tooltip("UnityTransport 组件引用（挂在 NetworkManager 同一物体上）")]
    public UnityTransport transport;

    [Tooltip("大厅状态预制体（含 LobbyState + NetworkObject），建房后 Spawn")]
    public GameObject lobbyStatePrefab;

    [Tooltip("PVE 最大玩家数（含房主）")]
    public int maxPlayers = 3;

    [Tooltip("PVP 最大玩家数；红蓝两队各 5 人")]
    public int pvpMaxPlayers = 10;

    [Header("广播")]
    public LanDiscovery discovery;

    [Header("玩家")]
    [Tooltip("本地玩家名（默认用机器名，UI 可覆盖）")]
    public string playerName = "";

    [Header("联机角色生成")]
    [Tooltip("PVE 网络玩家预制体，保持原来的按加入顺序分配方式。")]
    public string[] playerPrefabResourcePaths = new string[]
    {
        "Players/Lumine_Net",
        "Players/Furina_Net",
        "Players/Aether_Net",
    };

    [Tooltip("PVP 专用网络玩家预制体；与 PVE 预制体分离。")]
    public string[] pvpPlayerPrefabResourcePaths = new string[]
    {
        "Players/PVP/Lumine_PVP_Net",
        "Players/PVP/Furina_PVP_Net",
        "Players/PVP/Aether_PVP_Net",
    };

    [Tooltip("PVE 玩家生成点（按加入顺序）")]
    public Vector3[] playerSpawnPoints = new Vector3[]
    {
        new Vector3(2.934f, 0.121f, -21.072f),
        new Vector3(0.493f, 0.124f, -20.643f),
        new Vector3(-1.82f, 0.096f, -20.691f),
    };

    [Header("PVP 出生")]
    [Tooltip("GamePVP 中红方基地对象名")]
    public string redBaseObjectName = "基地-red";

    [Tooltip("GamePVP 中蓝方基地对象名")]
    public string blueBaseObjectName = "基地-blue";

    [Tooltip("同队相邻出生点的水平间距")]
    public float pvpSpawnSpacing = 1.5f;

    [Tooltip("基地内部的本地坐标中心。基地根节点不在建筑中心，当前地图安全中心约为 (13.65, -8.80, 2.65)。")]
    public Vector3 pvpBaseLocalSpawnCenter = new Vector3(13.65f, -8.80f, 2.65f);

    public string PlayerName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(playerName))
                playerName = SystemInfo.deviceName;
            return playerName;
        }
        set => playerName = string.IsNullOrWhiteSpace(value) ? SystemInfo.deviceName : value.Trim();
    }

    public LobbyState CurrentLobby { get; private set; }
    public LobbyGameMode BrowsingGameMode { get; private set; } = LobbyGameMode.PVE;
    public bool IsHost => NetworkManager.Singleton != null && NetworkManager.Singleton.IsHost;
    public bool IsInLobby => m_IsInLobby;

    public event Action OnLobbyChanged;

    [Serializable]
    private struct ConnectionPayload
    {
        public string playerName;
        public byte gameMode;
    }

    private bool m_IsInLobby;
    private bool m_NetworkCallbacksRegistered;
    private string m_PendingRoomName;
    private LobbyGameMode m_PendingRoomMode = LobbyGameMode.PVE;
    private LobbyGameMode m_ActiveGameMode = LobbyGameMode.PVE;

    private readonly Dictionary<ulong, ConnectionPayload> m_PendingConnections = new Dictionary<ulong, ConnectionPayload>();
    private readonly Dictionary<ulong, string> m_ClientNames = new Dictionary<ulong, string>();
    private readonly List<ulong> m_JoinOrder = new List<ulong>();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void Start()
    {
        if (transport == null)
            transport = FindObjectOfType<UnityTransport>();
        if (discovery == null)
            discovery = GetComponent<LanDiscovery>();

        EnsureNetworkCallbacks();
        SceneManager.sceneLoaded += OnLocalSceneLoaded;
    }

    private void OnDestroy()
    {
        RemoveNetworkCallbacks();
        SceneManager.sceneLoaded -= OnLocalSceneLoaded;

        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnServerStarted -= OnHostServerStarted;
            if (NetworkManager.Singleton.SceneManager != null)
                NetworkManager.Singleton.SceneManager.OnLoadEventCompleted -= OnLoadEventCompleted;
        }

        if (CurrentLobby != null)
            CurrentLobby.OnChanged -= HandleCurrentLobbyChanged;
        if (Instance == this)
            Instance = null;
    }

    public void SetBrowsingGameMode(LobbyGameMode gameMode)
    {
        if (m_IsInLobby)
            return;
        BrowsingGameMode = gameMode;
        OnLobbyChanged?.Invoke();
    }

    // ---- 建房 / 加入 ----

    public void CreateRoom(string roomName)
    {
        CreateRoom(roomName, BrowsingGameMode);
    }

    public void CreateRoom(string roomName, LobbyGameMode gameMode)
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        if (networkManager == null || networkManager.IsListening)
            return;

        EnsureNetworkCallbacks();
        ResetSessionCollections();
        BrowsingGameMode = gameMode;
        m_PendingRoomMode = gameMode;
        m_ActiveGameMode = gameMode;
        m_PendingRoomName = string.IsNullOrWhiteSpace(roomName) ? "我的房间" : roomName.Trim();

        int port = GetGamePort();
        if (transport != null)
            transport.SetConnectionData("0.0.0.0", (ushort)port, "0.0.0.0");

        networkManager.NetworkConfig.ConnectionApproval = true;
        networkManager.NetworkConfig.EnableSceneManagement = true;
        networkManager.OnServerStarted -= OnHostServerStarted;
        networkManager.OnServerStarted += OnHostServerStarted;

        if (!networkManager.StartHost())
        {
            networkManager.OnServerStarted -= OnHostServerStarted;
            Debug.LogError("[Lobby] 创建房间失败：StartHost 返回 false");
            return;
        }

        m_IsInLobby = true;
    }

    private void OnHostServerStarted()
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        networkManager.OnServerStarted -= OnHostServerStarted;

        if (networkManager.SceneManager != null)
        {
            networkManager.SceneManager.OnLoadEventCompleted -= OnLoadEventCompleted;
            networkManager.SceneManager.OnLoadEventCompleted += OnLoadEventCompleted;
        }

        if (lobbyStatePrefab == null)
        {
            Debug.LogError("[Lobby] 未配置 LobbyState 预制体");
            return;
        }

        GameObject go = Instantiate(lobbyStatePrefab);
        LobbyState lobby = go.GetComponent<LobbyState>();
        NetworkObject networkObject = go.GetComponent<NetworkObject>();
        if (lobby == null || networkObject == null)
        {
            Debug.LogError("[Lobby] LobbyState 预制体必须同时包含 LobbyState 与 NetworkObject");
            Destroy(go);
            return;
        }

        networkObject.Spawn();
        if (m_PendingRoomMode == LobbyGameMode.PVP)
        {
            lobby.Initialize(m_PendingRoomName, LobbyGameMode.PVP);
            lobby.AddPvpPlayer(NetworkManager.ServerClientId, PlayerName);
        }
        else
        {
            // PVE 保持原有 LobbyState 数据和建房流程。
            lobby.SetRoomName(m_PendingRoomName);
            lobby.AddPlayer(PlayerName);
        }

        m_JoinOrder.Add(NetworkManager.ServerClientId);
        m_ClientNames[NetworkManager.ServerClientId] = PlayerName;

        int capacity = GetMaxPlayers(m_PendingRoomMode);
        if (discovery != null)
            discovery.StartBroadcast(m_PendingRoomName, 1, capacity, false, GetGamePort(), m_PendingRoomMode);

        OnLobbyChanged?.Invoke();
    }

    public void JoinRoom(LanDiscovery.RoomEntry room)
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        if (networkManager == null || networkManager.IsListening || room == null)
            return;
        if (room.gameMode != BrowsingGameMode)
        {
            Debug.LogWarning("[Lobby] 房间模式与当前入口不一致，已拒绝加入");
            return;
        }

        EnsureNetworkCallbacks();
        ResetSessionCollections();
        m_ActiveGameMode = room.gameMode;

        if (transport == null)
        {
            Debug.LogError("[Lobby] 未找到 UnityTransport");
            return;
        }

        transport.SetConnectionData(room.hostEndPoint.Address.ToString(), (ushort)room.gamePort);
        var payload = new ConnectionPayload
        {
            playerName = PlayerName,
            gameMode = (byte)room.gameMode,
        };

        networkManager.NetworkConfig.ConnectionData = Encoding.UTF8.GetBytes(JsonUtility.ToJson(payload));
        networkManager.NetworkConfig.ConnectionApproval = true;
        networkManager.NetworkConfig.EnableSceneManagement = true;

        if (networkManager.StartClient())
            m_IsInLobby = true;
        else
            Debug.LogError("[Lobby] 加入房间失败：StartClient 返回 false");
    }

    public void LeaveRoom()
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        if (networkManager == null || !networkManager.IsListening)
            return;

        if (discovery != null)
            discovery.StopBroadcast();

        networkManager.Shutdown();
        m_IsInLobby = false;

        if (CurrentLobby != null)
            CurrentLobby.OnChanged -= HandleCurrentLobbyChanged;
        CurrentLobby = null;
        ResetSessionCollections();
        OnLobbyChanged?.Invoke();
    }

    // ---- PVP 分队与开始游戏 ----

    public void SelectTeam(LobbyTeam team)
    {
        if (CurrentLobby == null || CurrentLobby.GameMode != LobbyGameMode.PVP)
            return;
        if (team != LobbyTeam.Red && team != LobbyTeam.Blue)
            return;

        CurrentLobby.RequestSetTeamServerRpc((byte)team);
    }

    public void SelectCharacter(byte characterIndex)
    {
        if (CurrentLobby == null || CurrentLobby.GameMode != LobbyGameMode.PVP)
            return;
        if (pvpPlayerPrefabResourcePaths == null || characterIndex >= pvpPlayerPrefabResourcePaths.Length)
            return;

        CurrentLobby.RequestSetCharacterServerRpc(characterIndex);
    }

    public void StartGame()
    {
        if (!TryStartGame(out string reason))
            Debug.LogWarning("[Lobby] " + reason);
    }

    public bool TryStartGame(out string message)
    {
        message = string.Empty;
        if (!IsHost || CurrentLobby == null)
        {
            message = "只有房主可以开始游戏";
            return false;
        }
        if (CurrentLobby.IsStarted.Value)
        {
            message = "对局已经开始";
            return false;
        }

        LobbyGameMode gameMode = CurrentLobby.GameMode;
        if (gameMode == LobbyGameMode.PVP)
        {
            if (!CurrentLobby.HasMinimumPvpTeams())
            {
                message = "红队和蓝队都至少需要 1 名玩家";
                return false;
            }

            foreach (LobbyPlayerData player in CurrentLobby.GetPlayers())
            {
                if (player.Team == LobbyTeam.None)
                {
                    message = "还有玩家未选择队伍";
                    return false;
                }
                if (pvpPlayerPrefabResourcePaths == null || player.CharacterIndex >= pvpPlayerPrefabResourcePaths.Length)
                {
                    message = "还有玩家未选择角色";
                    return false;
                }
            }
        }

        NetworkManager networkManager = NetworkManager.Singleton;
        if (networkManager == null || networkManager.SceneManager == null)
        {
            message = "网络场景管理器未就绪";
            return false;
        }

        m_ActiveGameMode = gameMode;
        string sceneName = GetGameSceneName(gameMode);

        // PVE 保持原有顺序：先锁定房间，再请求进入 GamePVE。
        if (gameMode == LobbyGameMode.PVE)
            CurrentLobby.SetStarted(true);

        SceneEventProgressStatus status = networkManager.SceneManager.LoadScene(sceneName, LoadSceneMode.Single);
        if (status != SceneEventProgressStatus.Started)
        {
            message = "场景切换失败：" + status;
            return false;
        }

        if (gameMode == LobbyGameMode.PVP)
            CurrentLobby.SetStarted(true);
        if (discovery != null)
            discovery.UpdateBroadcastInfo(CurrentLobby.PlayerCount, true);

        message = "正在进入 " + sceneName + "...";
        Debug.Log("[Lobby] 房主开始游戏，场景切换请求状态: " + status);
        return true;
    }

    // ---- 连接审批 ----

    private void ApprovalCheck(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
    {
        if (request.ClientNetworkId == NetworkManager.ServerClientId)
        {
            response.Approved = true;
            response.CreatePlayerObject = false;
            return;
        }

        ConnectionPayload payload = DecodeConnectionPayload(request.Payload);
        LobbyGameMode requestedMode = (LobbyGameMode)payload.gameMode;
        LobbyGameMode roomMode = CurrentLobby != null ? CurrentLobby.GameMode : m_PendingRoomMode;
        int currentPlayers = NetworkManager.Singleton.ConnectedClientsIds.Count;

        if (requestedMode != roomMode)
        {
            response.Approved = false;
            response.Reason = "房间模式不匹配";
            return;
        }
        if (currentPlayers >= GetMaxPlayers(roomMode))
        {
            response.Approved = false;
            response.Reason = "房间已满";
            return;
        }
        if (CurrentLobby != null && CurrentLobby.IsStarted.Value)
        {
            response.Approved = false;
            response.Reason = "对局进行中";
            return;
        }

        response.Approved = true;
        response.CreatePlayerObject = false;
        m_PendingConnections[request.ClientNetworkId] = payload;
    }

    private void OnClientConnected(ulong clientId)
    {
        if (IsHost && CurrentLobby != null && m_PendingConnections.TryGetValue(clientId, out ConnectionPayload payload))
        {
            m_PendingConnections.Remove(clientId);
            string name = string.IsNullOrWhiteSpace(payload.playerName) ? "玩家" : payload.playerName;
            m_ClientNames[clientId] = name;
            if (CurrentLobby.GameMode == LobbyGameMode.PVP)
                CurrentLobby.AddPvpPlayer(clientId, name);
            else
                CurrentLobby.AddPlayer(name);

            if (!m_JoinOrder.Contains(clientId))
                m_JoinOrder.Add(clientId);

            if (discovery != null)
                discovery.UpdateBroadcastInfo(CurrentLobby.PlayerCount, CurrentLobby.IsStarted.Value);
        }

        OnLobbyChanged?.Invoke();
    }

    private void OnClientDisconnected(ulong clientId)
    {
        if (IsHost && CurrentLobby != null && CurrentLobby.IsSpawned)
        {
            m_ClientNames.TryGetValue(clientId, out string disconnectedName);
            m_ClientNames.Remove(clientId);
            m_PendingConnections.Remove(clientId);
            m_JoinOrder.Remove(clientId);
            if (CurrentLobby.GameMode == LobbyGameMode.PVP)
                CurrentLobby.RemovePvpPlayer(clientId);
            else if (!string.IsNullOrEmpty(disconnectedName))
                CurrentLobby.RemovePlayer(disconnectedName);

            if (discovery != null)
                discovery.UpdateBroadcastInfo(CurrentLobby.PlayerCount, CurrentLobby.IsStarted.Value);
        }

        if (!IsHost && clientId == NetworkManager.ServerClientId)
        {
            m_IsInLobby = false;
            CurrentLobby = null;
        }

        OnLobbyChanged?.Invoke();
    }

    private void OnPreShutdown()
    {
        // NGO 关闭期间不能再写 NetworkList；提前解绑断线回调，下一次建房/加入时会重新注册。
        RemoveNetworkCallbacks();
    }

    private void EnsureNetworkCallbacks()
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        if (networkManager == null || m_NetworkCallbacksRegistered)
            return;

        networkManager.ConnectionApprovalCallback = ApprovalCheck;
        networkManager.OnClientConnectedCallback += OnClientConnected;
        networkManager.OnClientDisconnectCallback += OnClientDisconnected;
        networkManager.OnPreShutdown += OnPreShutdown;
        m_NetworkCallbacksRegistered = true;
    }

    private void RemoveNetworkCallbacks()
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        if (networkManager == null || !m_NetworkCallbacksRegistered)
            return;

        if (networkManager.ConnectionApprovalCallback == ApprovalCheck)
            networkManager.ConnectionApprovalCallback = null;
        networkManager.OnClientConnectedCallback -= OnClientConnected;
        networkManager.OnClientDisconnectCallback -= OnClientDisconnected;
        networkManager.OnPreShutdown -= OnPreShutdown;
        m_NetworkCallbacksRegistered = false;
    }

    // ---- 场景切换完成后生成玩家 ----

    private void OnLoadEventCompleted(
        string sceneName,
        LoadSceneMode loadSceneMode,
        List<ulong> clientsCompleted,
        List<ulong> clientsTimedOut)
    {
        if (!IsHost)
            return;

        // PVE 仍按加入顺序生成，不把 PVP 基地规则带进去。
        if (m_ActiveGameMode == LobbyGameMode.PVE)
        {
            if (sceneName != "GamePVE")
                return;

            foreach (ulong clientId in m_JoinOrder)
                SpawnPlayerFor(clientId, LobbyGameMode.PVE);
            Debug.Log("[Lobby] 全员进入 GamePVE 场景，已按加入顺序生成 PVE 玩家角色");
            return;
        }

        if (sceneName != "GamePVP")
            return;

        if (clientsTimedOut != null && clientsTimedOut.Count > 0)
            Debug.LogWarning("[Lobby] 部分玩家加载场景超时，数量: " + clientsTimedOut.Count);

        foreach (ulong clientId in m_JoinOrder)
        {
            if (NetworkManager.Singleton.ConnectedClients.ContainsKey(clientId))
                SpawnPlayerFor(clientId, m_ActiveGameMode);
        }

        Debug.Log("[Lobby] 全员进入 " + sceneName + "，已按房间分队生成玩家角色");
    }

    private void SpawnPlayerFor(ulong clientId, LobbyGameMode gameMode)
    {
        int joinIndex = m_JoinOrder.IndexOf(clientId);
        string[] prefabPaths = gameMode == LobbyGameMode.PVP
            ? pvpPlayerPrefabResourcePaths
            : playerPrefabResourcePaths;
        if (joinIndex < 0 || prefabPaths == null || prefabPaths.Length == 0)
            return;

        int prefabIndex = gameMode == LobbyGameMode.PVP && CurrentLobby != null
            ? CurrentLobby.GetPlayerCharacterIndex(clientId)
            : joinIndex;
        if (prefabIndex < 0 || prefabIndex >= prefabPaths.Length)
        {
            Debug.LogWarning("[Lobby] " + gameMode + " 玩家 " + clientId + " 没有可分配的角色（index=" + prefabIndex + "）");
            return;
        }

        string prefabPath = prefabPaths[prefabIndex];
        GameObject prefab = Resources.Load<GameObject>(prefabPath);
        if (prefab == null)
        {
            Debug.LogError("[Lobby] 加载玩家预制体失败: " + prefabPath);
            return;
        }

        Vector3 position;
        Quaternion rotation;
        LobbyTeam assignedTeam = LobbyTeam.None;
        int assignedTeamSlot = -1;
        if (gameMode == LobbyGameMode.PVP)
        {
            assignedTeam = CurrentLobby != null
                ? CurrentLobby.GetPlayerTeam(clientId)
                : LobbyTeam.None;
            assignedTeamSlot = GetTeamSlotIndex(clientId, assignedTeam);
            if (!TryGetPvpSpawnForClient(clientId, assignedTeam, out position, out rotation))
            {
                Debug.LogError("[PVP] 无法解析玩家队伍或基地出生点: client=" + clientId + ", team=" + assignedTeam);
                return;
            }
        }
        else
        {
            position = joinIndex < playerSpawnPoints.Length ? playerSpawnPoints[joinIndex] : Vector3.zero;
            rotation = Quaternion.identity;
        }

        GameObject instance = Instantiate(prefab, position, rotation);
        NetworkObject networkObject = instance.GetComponent<NetworkObject>();
        if (networkObject == null)
        {
            Debug.LogError("[Lobby] 玩家预制体缺少 NetworkObject: " + prefab.name);
            Destroy(instance);
            return;
        }

        PvpPlayerCombat pvpCombat = null;
        if (gameMode == LobbyGameMode.PVP)
        {
            pvpCombat = instance.GetComponent<PvpPlayerCombat>();
            if (pvpCombat == null)
            {
                Debug.LogError("[PVP] 专用玩家预制体缺少 PvpPlayerCombat: " + prefab.name);
                Destroy(instance);
                return;
            }
            string displayName = m_ClientNames.TryGetValue(clientId, out string knownName)
                ? knownName
                : "玩家";
            pvpCombat.ConfigureBeforeSpawn(assignedTeam, assignedTeamSlot + 1, displayName);
        }

        networkObject.SpawnAsPlayerObject(clientId);
        if (pvpCombat != null)
        {
            pvpCombat.PlaceAtSpawnServer(position, rotation);
            Debug.Log("[PVP] 玩家生成完成 client=" + clientId + ", team=" + assignedTeam +
                      ", character=" + prefabIndex + ", position=" + position);
        }
    }

    private int GetTeamSlotIndex(ulong clientId, LobbyTeam team)
    {
        int teamSlot = 0;
        foreach (ulong joinedClientId in m_JoinOrder)
        {
            if (joinedClientId == clientId)
                return teamSlot;
            if (CurrentLobby != null && CurrentLobby.GetPlayerTeam(joinedClientId) == team)
                teamSlot++;
        }
        return teamSlot;
    }

    /// <summary>服务器按当前房间分队解析某位玩家的基地出生位置，供首次生成与复活共用。</summary>
    public bool TryGetPvpSpawnForClient(ulong clientId, out Vector3 position, out Quaternion rotation)
    {
        LobbyTeam team = CurrentLobby != null
            ? CurrentLobby.GetPlayerTeam(clientId)
            : LobbyTeam.None;
        return TryGetPvpSpawnForClient(clientId, team, out position, out rotation);
    }

    /// <summary>使用开局时写入玩家对象的队伍快照解析出生点，避免客户端房间列表更新顺序影响阵营。</summary>
    public bool TryGetPvpSpawnForClient(
        ulong clientId,
        LobbyTeam assignedTeam,
        out Vector3 position,
        out Quaternion rotation)
    {
        if (CurrentLobby == null || CurrentLobby.GameMode != LobbyGameMode.PVP)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            return false;
        }

        if (assignedTeam != LobbyTeam.Red && assignedTeam != LobbyTeam.Blue)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            return false;
        }

        int teamSlot = GetTeamSlotIndex(clientId, assignedTeam);
        ResolvePvpSpawn(assignedTeam, teamSlot, out position, out rotation);
        return true;
    }

    private void ResolvePvpSpawn(LobbyTeam team, int teamSlot, out Vector3 position, out Quaternion rotation)
    {
        string baseName = team == LobbyTeam.Blue ? blueBaseObjectName : redBaseObjectName;
        GameObject teamBase = GameObject.Find(baseName);
        int teamCount = CurrentLobby != null
            ? Mathf.Clamp(CurrentLobby.GetTeamCount(team), 1, LobbyState.PvpTeamCapacity)
            : 1;
        float centeredSlot = teamSlot - (teamCount - 1) * 0.5f;

        if (teamBase != null)
        {
            Vector3 localSpawn = pvpBaseLocalSpawnCenter +
                                 Vector3.right * (centeredSlot * pvpSpawnSpacing);
            position = teamBase.transform.TransformPoint(localSpawn);
            rotation = Quaternion.LookRotation(teamBase.transform.forward, Vector3.up);
            return;
        }

        // 地图对象被改名时仍给出安全的当前地图回退位置。
        bool isBlue = team == LobbyTeam.Blue;
        Vector3 fallbackBase = isBlue
            ? new Vector3(3.43f, 0.103f, 15.93f)
            : new Vector3(4.05f, 0.103f, -21.24f);
        Vector3 lateral = (isBlue ? Vector3.left : Vector3.right) *
                          (centeredSlot * pvpSpawnSpacing);
        position = fallbackBase + lateral;
        rotation = Quaternion.Euler(0f, isBlue ? 180f : 0f, 0f);
        Debug.LogWarning("[Lobby] 未找到 PVP 基地对象 " + baseName + "，已使用回退出生点");
    }

    // ---- 公共查询与内部工具 ----

    public void RegisterLobby(LobbyState lobby)
    {
        if (CurrentLobby == lobby)
            return;

        if (CurrentLobby != null)
            CurrentLobby.OnChanged -= HandleCurrentLobbyChanged;
        CurrentLobby = lobby;
        m_ActiveGameMode = lobby.GameMode;
        lobby.OnChanged += HandleCurrentLobbyChanged;
        OnLobbyChanged?.Invoke();
    }

    private void HandleCurrentLobbyChanged()
    {
        if (IsHost && discovery != null && CurrentLobby != null)
            discovery.UpdateBroadcastInfo(CurrentLobby.PlayerCount, CurrentLobby.IsStarted.Value);
        OnLobbyChanged?.Invoke();
    }

    private void OnLocalSceneLoaded(Scene scene, LoadSceneMode loadSceneMode)
    {
        if (scene.name != "GamePVP" || CurrentLobby == null || CurrentLobby.GameMode != LobbyGameMode.PVP)
            return;

        PvpGameHudUI.AttachTo(GameObject.Find("GamePanel"));
        PvpScoreboardUI.AttachTo(GameObject.Find("RecordPanel"));
    }

    public List<string> GetPlayerNames()
    {
        return CurrentLobby != null ? CurrentLobby.GetPlayerNames() : new List<string>();
    }

    public string GetRoomName()
    {
        return CurrentLobby != null ? CurrentLobby.GetRoomName() : string.Empty;
    }

    private int GetMaxPlayers(LobbyGameMode gameMode)
    {
        return gameMode == LobbyGameMode.PVP ? Mathf.Max(2, pvpMaxPlayers) : Mathf.Max(1, maxPlayers);
    }

    private static string GetGameSceneName(LobbyGameMode gameMode)
    {
        return gameMode == LobbyGameMode.PVP ? "GamePVP" : "GamePVE";
    }

    private int GetGamePort()
    {
        return transport != null ? transport.ConnectionData.Port : 7777;
    }

    private static ConnectionPayload DecodeConnectionPayload(byte[] payload)
    {
        if (payload == null || payload.Length == 0)
            return new ConnectionPayload { playerName = "玩家", gameMode = (byte)LobbyGameMode.PVE };

        try
        {
            ConnectionPayload result = JsonUtility.FromJson<ConnectionPayload>(Encoding.UTF8.GetString(payload));
            if (string.IsNullOrWhiteSpace(result.playerName))
                result.playerName = "玩家";
            return result;
        }
        catch
        {
            return new ConnectionPayload { playerName = "玩家", gameMode = (byte)LobbyGameMode.PVE };
        }
    }

    private void ResetSessionCollections()
    {
        m_PendingConnections.Clear();
        m_ClientNames.Clear();
        m_JoinOrder.Clear();
    }
}
