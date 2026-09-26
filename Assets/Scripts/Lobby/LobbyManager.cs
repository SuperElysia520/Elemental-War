using System;
using System.Collections.Generic;
using System.Text;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 联机大厅总控：PVE 保留 LAN Host；PVP 使用固定端口池中的一房间一进程 Dedicated Server，
/// 并负责审批、分队、切场景与玩家生成。
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

    [Header("PVP Dedicated Server")]
    [Tooltip("客户端连接的专用服务器地址。本机测试使用 127.0.0.1，发布前改为云服务器公网 IP。")]
    public string dedicatedServerAddress = "127.0.0.1";

    [Tooltip("PVP 专用服务器起始 UDP 端口。每个服务器进程占用一个连续端口。")]
    public int dedicatedServerPort = 7777;

    [Tooltip("同时开放的 PVP 房间槽位数量；需要启动相同数量、不同端口的服务器进程。")]
    [Range(1, 20)]
    public int dedicatedServerRoomCount = 1;

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
    public bool IsDedicatedPvpServer => m_IsDedicatedPvpServer;
    public bool IsDedicatedPvpSession => m_IsDedicatedPvpSession;
    public bool CanManageRoom
    {
        get
        {
            NetworkManager networkManager = NetworkManager.Singleton;
            if (networkManager == null || CurrentLobby == null)
                return false;
            if (CurrentLobby.GameMode == LobbyGameMode.PVP && m_IsDedicatedPvpSession)
            {
                return networkManager.IsClient &&
                       CurrentLobby.IsRoomOwner(networkManager.LocalClientId);
            }
            return networkManager.IsHost;
        }
    }

    public string DedicatedPvpEndpoint
    {
        get
        {
            int startPort = GetDedicatedServerPort();
            int endPort = startPort + GetDedicatedServerRoomCount() - 1;
            return NormalizeDedicatedServerAddress() + ":" + startPort + "-" + endPort;
        }
    }
    public int DedicatedPvpRoomCount => GetDedicatedServerRoomCount();

    public event Action OnLobbyChanged;

    [Serializable]
    private struct ConnectionPayload
    {
        public string playerName;
        public byte gameMode;
        public byte operation;
        public string roomName;
        public string clientVersion;
    }

    private enum ConnectionOperation : byte
    {
        LanJoin = 0,
        CreateDedicatedPvp = 1,
        JoinDedicatedPvp = 2,
    }

    private bool m_IsInLobby;
    private bool m_IsDedicatedPvpServer;
    private bool m_IsDedicatedPvpSession;
    private bool m_IsDedicatedRoomResetting;
    private bool m_NetworkCallbacksRegistered;
    private ulong m_PendingDedicatedCreatorClientId = LobbyState.NoRoomOwner;
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
            NetworkManager.Singleton.OnServerStarted -= OnDedicatedServerStarted;
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
        if (gameMode == LobbyGameMode.PVP)
        {
            if (!TryCreateDedicatedPvpRoom(roomName, out string message))
                Debug.LogWarning("[Dedicated PVP] " + message);
            return;
        }

        NetworkManager networkManager = NetworkManager.Singleton;
        if (networkManager == null || networkManager.IsListening)
            return;

        EnsureNetworkCallbacks();
        ResetSessionCollections();
        ConfigurePendingRoom(roomName, gameMode);
        m_IsDedicatedPvpServer = false;
        m_IsDedicatedPvpSession = false;

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

    /// <summary>由 DedicatedServerBootstrap 调用；服务器启动后保持空闲，直到首位玩家请求创建 PVP 房间。</summary>
    public bool StartDedicatedPvpServer(int portOverride = 0)
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        if (networkManager == null || networkManager.IsListening)
            return false;
        if (transport == null)
            transport = networkManager.GetComponent<UnityTransport>();
        if (transport == null)
        {
            Debug.LogError("[Dedicated PVP] 未找到 UnityTransport");
            return false;
        }

        int requestedPort = portOverride > 0 ? portOverride : dedicatedServerPort;
        int maximumGamePort = ushort.MaxValue - LanDiscovery.DedicatedDiscoveryPortOffset;
        ushort port = (ushort)Mathf.Clamp(requestedPort, 1, maximumGamePort);

        EnsureNetworkCallbacks();
        ResetSessionCollections();
        m_IsDedicatedPvpServer = true;
        m_IsDedicatedPvpSession = true;
        m_IsInLobby = false;
        m_IsDedicatedRoomResetting = false;
        m_PendingDedicatedCreatorClientId = LobbyState.NoRoomOwner;
        BrowsingGameMode = LobbyGameMode.PVP;
        m_PendingRoomMode = LobbyGameMode.PVP;
        m_ActiveGameMode = LobbyGameMode.PVP;

        if (discovery != null)
        {
            discovery.StopBroadcast();
            discovery.StopListening();
            discovery.StopDedicatedPvpDiscovery();
        }

        transport.SetConnectionData("127.0.0.1", port, "0.0.0.0");
        ConfigureNetworkForLobby(networkManager);
        networkManager.OnServerStarted -= OnDedicatedServerStarted;
        networkManager.OnServerStarted += OnDedicatedServerStarted;

        if (!networkManager.StartServer())
        {
            networkManager.OnServerStarted -= OnDedicatedServerStarted;
            m_IsDedicatedPvpServer = false;
            m_IsDedicatedPvpSession = false;
            Debug.LogError("[Dedicated PVP] StartServer 返回 false");
            return false;
        }

        Application.runInBackground = true;
        return true;
    }

    private void OnDedicatedServerStarted()
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        networkManager.OnServerStarted -= OnDedicatedServerStarted;
        SubscribeSceneLoadCompleted(networkManager);
        if (discovery != null)
        {
            discovery.StartDedicatedPvpResponder(GetGamePort(), GetMaxPlayers(LobbyGameMode.PVP));
            RefreshDedicatedPvpServerStatus();
        }
        Debug.Log("[Dedicated PVP] 服务器已启动，版本 " + Application.version +
                  "，监听 0.0.0.0:" + GetGamePort() + "，等待玩家创建房间");
    }

    private void OnHostServerStarted()
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        networkManager.OnServerStarted -= OnHostServerStarted;

        SubscribeSceneLoadCompleted(networkManager);

        LobbyState lobby = SpawnLobbyState();
        if (lobby == null)
            return;
        // PVE 保持原有 LobbyState 数据和建房流程；PVP 不会进入 Host 分支。
        lobby.SetRoomName(m_PendingRoomName);
        lobby.AddPlayer(PlayerName);

        m_JoinOrder.Add(NetworkManager.ServerClientId);
        m_ClientNames[NetworkManager.ServerClientId] = PlayerName;

        int capacity = GetMaxPlayers(m_PendingRoomMode);
        if (discovery != null)
            discovery.StartBroadcast(m_PendingRoomName, 1, capacity, false, GetGamePort(), m_PendingRoomMode);

        OnLobbyChanged?.Invoke();
    }

    private void SubscribeSceneLoadCompleted(NetworkManager networkManager)
    {
        if (networkManager != null && networkManager.SceneManager != null)
        {
            networkManager.SceneManager.OnLoadEventCompleted -= OnLoadEventCompleted;
            networkManager.SceneManager.OnLoadEventCompleted += OnLoadEventCompleted;
        }
    }

    private LobbyState SpawnLobbyState()
    {
        if (lobbyStatePrefab == null)
        {
            Debug.LogError("[Lobby] 未配置 LobbyState 预制体");
            return null;
        }

        GameObject go = Instantiate(lobbyStatePrefab);
        LobbyState lobby = go.GetComponent<LobbyState>();
        NetworkObject networkObject = go.GetComponent<NetworkObject>();
        if (lobby == null || networkObject == null)
        {
            Debug.LogError("[Lobby] LobbyState 预制体必须同时包含 LobbyState 与 NetworkObject");
            Destroy(go);
            return null;
        }

        networkObject.Spawn();
        return lobby;
    }

    private bool CreateDedicatedPvpLobby(ulong creatorClientId, ConnectionPayload payload)
    {
        if (!m_IsDedicatedPvpServer || CurrentLobby != null)
            return false;

        LobbyState lobby = SpawnLobbyState();
        if (lobby == null)
            return false;

        string roomName = string.IsNullOrWhiteSpace(payload.roomName)
            ? "PVP公开房间"
            : payload.roomName.Trim();
        ConfigurePendingRoom(roomName, LobbyGameMode.PVP);
        lobby.Initialize(roomName, LobbyGameMode.PVP);
        lobby.SetRoomOwner(creatorClientId);
        RefreshDedicatedPvpServerStatus();
        Debug.Log("[Dedicated PVP] 房间已创建：" + roomName + "，owner=" + creatorClientId);
        return true;
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
        ConfigureClientPayload(networkManager, room.gameMode);
        ConfigureNetworkForLobby(networkManager);

        if (networkManager.StartClient())
            m_IsInLobby = true;
        else
            Debug.LogError("[Lobby] 加入房间失败：StartClient 返回 false");
    }

    public bool TryCreateDedicatedPvpRoom(string roomName, out string message)
    {
        if (discovery == null || !discovery.TryGetIdleDedicatedPvpServer(out LanDiscovery.RoomEntry server))
        {
            message = discovery != null && discovery.DedicatedPvpServerCount > 0
                ? "所有 PVP 房间槽位都已被占用"
                : "尚未发现可用的 PVP 服务器，请先刷新房间列表";
            return false;
        }
        return TryCreateDedicatedPvpRoom(roomName, server, out message);
    }

    public bool TryCreateDedicatedPvpRoom(
        string roomName,
        LanDiscovery.RoomEntry server,
        out string message)
    {
        if (!ValidateDedicatedPvpServerEntry(server, true, out message))
            return false;

        string normalizedRoomName = string.IsNullOrWhiteSpace(roomName) ? "PVP公开房间" : roomName.Trim();
        if (normalizedRoomName.Length > 32)
            normalizedRoomName = normalizedRoomName.Substring(0, 32);
        return TryConnectDedicatedPvp(
            ConnectionOperation.CreateDedicatedPvp,
            normalizedRoomName,
            server.hostEndPoint.Address.ToString(),
            server.gamePort,
            out message);
    }

    public bool TryJoinDedicatedPvpRoom(out string message)
    {
        if (discovery != null)
            discovery.RefreshRoomList();
        if (discovery != null)
        {
            foreach (LanDiscovery.RoomEntry room in discovery.Rooms)
            {
                if (room.isDedicatedPvp && room.hasRoom && !room.isStarted &&
                    room.currentPlayers < room.maxPlayers)
                {
                    return TryJoinDedicatedPvpRoom(room, out message);
                }
            }
        }

        message = "请先从房间列表选择一个可加入的 PVP 房间";
        return false;
    }

    public bool TryJoinDedicatedPvpRoom(LanDiscovery.RoomEntry room, out string message)
    {
        if (!ValidateDedicatedPvpServerEntry(room, false, out message))
            return false;
        return TryConnectDedicatedPvp(
            ConnectionOperation.JoinDedicatedPvp,
            string.Empty,
            room.hostEndPoint.Address.ToString(),
            room.gamePort,
            out message);
    }

    public bool StartDedicatedPvpRoomDiscovery(out string message)
    {
        message = string.Empty;
        if (discovery == null)
        {
            message = "未找到房间发现组件";
            return false;
        }

        string address = NormalizeDedicatedServerAddress();
        if (string.IsNullOrEmpty(address))
        {
            message = "尚未配置 PVP 专用服务器地址";
            return false;
        }

        if (!discovery.StartDedicatedPvpDiscovery(
                address,
                GetDedicatedServerPort(),
                GetDedicatedServerRoomCount()))
        {
            message = "无法启动 PVP 房间查询";
            return false;
        }

        message = "正在查询 PVP 服务器 ";
        return true;
    }

    public void RefreshDedicatedPvpRoomDiscovery()
    {
        if (discovery != null)
            discovery.RefreshDedicatedPvpDiscovery();
    }

    public void StopDedicatedPvpRoomDiscovery()
    {
        if (discovery != null)
            discovery.StopDedicatedPvpDiscovery();
    }

    private bool TryConnectDedicatedPvp(
        ConnectionOperation operation,
        string roomName,
        string serverAddress,
        int serverPort,
        out string message)
    {
        message = string.Empty;
        NetworkManager networkManager = NetworkManager.Singleton;
        if (networkManager == null)
        {
            message = "NetworkManager 尚未就绪";
            return false;
        }
        if (networkManager.IsListening)
        {
            message = "当前已经在一个联机房间中";
            return false;
        }
        if (transport == null)
        {
            message = "未找到 UnityTransport";
            return false;
        }

        if (string.IsNullOrWhiteSpace(serverAddress))
        {
            message = "尚未配置 PVP 专用服务器地址";
            return false;
        }
        ushort port = (ushort)Mathf.Clamp(serverPort, 1, ushort.MaxValue);

        EnsureNetworkCallbacks();
        ResetSessionCollections();
        BrowsingGameMode = LobbyGameMode.PVP;
        m_ActiveGameMode = LobbyGameMode.PVP;
        m_IsDedicatedPvpServer = false;
        m_IsDedicatedPvpSession = true;

        transport.SetConnectionData(serverAddress.Trim(), port);
        ConfigureClientPayload(networkManager, LobbyGameMode.PVP, operation, roomName);
        ConfigureNetworkForLobby(networkManager);

        if (!networkManager.StartClient())
        {
            m_IsDedicatedPvpSession = false;
            message = "连接专用服务器失败：StartClient 返回 false";
            return false;
        }

        m_IsInLobby = true;
        StopDedicatedPvpRoomDiscovery();
        message = operation == ConnectionOperation.CreateDedicatedPvp
            ? "正在连接端口 " + port + " 并创建 PVP 房间..."
            : "正在连接 PVP 房间（端口 " + port + "）...";
        return true;
    }

    private static bool ValidateDedicatedPvpServerEntry(
        LanDiscovery.RoomEntry room,
        bool forCreate,
        out string message)
    {
        message = string.Empty;
        if (room == null || room.hostEndPoint == null || !room.isDedicatedPvp ||
            room.gameMode != LobbyGameMode.PVP)
        {
            message = "PVP 服务器信息无效，请刷新房间列表";
            return false;
        }
        if (!string.Equals(room.serverVersion, Application.version, StringComparison.Ordinal))
        {
            string serverVersion = string.IsNullOrWhiteSpace(room.serverVersion)
                ? "未知/旧版"
                : room.serverVersion;
            message = "PVP版本不一致：客户端 " + Application.version +
                      "，服务器 " + serverVersion;
            return false;
        }
        if (forCreate)
        {
            if (!room.canCreate)
            {
                message = "这个服务器槽位已被占用，请刷新后重试";
                return false;
            }
            return true;
        }
        if (!room.hasRoom)
        {
            message = "该服务器槽位还没有创建房间";
            return false;
        }
        if (room.isStarted)
        {
            message = "该房间已经开始游戏";
            return false;
        }
        if (room.currentPlayers >= room.maxPlayers)
        {
            message = "该房间已满";
            return false;
        }
        return true;
    }

    public void LeaveRoom()
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        if (networkManager == null || !networkManager.IsListening)
            return;
        if (m_IsDedicatedPvpServer)
        {
            Debug.LogWarning("[Dedicated PVP] 服务器进程不能通过玩家离开流程关闭");
            return;
        }

        if (discovery != null)
            discovery.StopBroadcast();

        networkManager.Shutdown();
        m_IsInLobby = false;

        if (CurrentLobby != null)
            CurrentLobby.OnChanged -= HandleCurrentLobbyChanged;
        CurrentLobby = null;
        ResetSessionCollections();
        m_IsDedicatedPvpSession = false;
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
        if (CurrentLobby == null || !CanManageRoom)
        {
            message = "只有房主可以开始游戏";
            return false;
        }

        if (!ValidateStartConditions(out message))
            return false;

        if (m_IsDedicatedPvpSession && !IsHost)
        {
            CurrentLobby.RequestStartPvpGameServerRpc();
            message = "正在请求服务器开始游戏...";
            return true;
        }

        return TryStartGameOnServer(out message);
    }

    /// <summary>LobbyState 仅在服务器验证 RPC 发送者是当前房主后调用。</summary>
    public void HandleDedicatedStartGameRequest(ulong requesterClientId)
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        if (!m_IsDedicatedPvpServer || networkManager == null || !networkManager.IsServer ||
            CurrentLobby == null || !CurrentLobby.IsRoomOwner(requesterClientId))
        {
            return;
        }

        if (!TryStartGameOnServer(out string message))
            Debug.LogWarning("[Dedicated PVP] 开局请求被拒绝：" + message);
    }

    private bool ValidateStartConditions(out string message)
    {
        message = string.Empty;
        if (CurrentLobby == null)
        {
            message = "房间尚未就绪";
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

        return true;
    }

    private bool TryStartGameOnServer(out string message)
    {
        message = string.Empty;
        NetworkManager networkManager = NetworkManager.Singleton;
        if (networkManager == null || !networkManager.IsServer)
        {
            message = "只有服务器可以执行场景切换";
            return false;
        }
        if (!ValidateStartConditions(out message))
            return false;

        if (networkManager.SceneManager == null)
        {
            message = "网络场景管理器未就绪";
            return false;
        }

        LobbyGameMode gameMode = CurrentLobby.GameMode;
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
        if (IsHost && discovery != null)
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
        if (m_IsDedicatedPvpServer)
        {
            ApprovalCheckDedicatedPvp(request.ClientNetworkId, payload, response);
            return;
        }

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

    private void ApprovalCheckDedicatedPvp(
        ulong clientId,
        ConnectionPayload payload,
        NetworkManager.ConnectionApprovalResponse response)
    {
        LobbyGameMode requestedMode = (LobbyGameMode)payload.gameMode;
        ConnectionOperation operation = (ConnectionOperation)payload.operation;
        if (!string.Equals(payload.clientVersion, Application.version, StringComparison.Ordinal))
        {
            string clientVersion = string.IsNullOrWhiteSpace(payload.clientVersion)
                ? "未知/旧版"
                : payload.clientVersion;
            RejectConnection(
                response,
                "PVP版本不一致：客户端 " + clientVersion + "，服务器 " + Application.version);
            return;
        }
        if (requestedMode != LobbyGameMode.PVP)
        {
            RejectConnection(response, "该服务器仅提供 PVP 房间");
            return;
        }

        if (operation == ConnectionOperation.CreateDedicatedPvp)
        {
            if (m_IsDedicatedRoomResetting)
            {
                RejectConnection(response, "服务器正在重置上一局，请稍后重试");
                return;
            }
            if (CurrentLobby != null || m_PendingDedicatedCreatorClientId != LobbyState.NoRoomOwner)
            {
                RejectConnection(response, "公开 PVP 房间已经存在，请点击加入房间");
                return;
            }

            m_PendingDedicatedCreatorClientId = clientId;
            RefreshDedicatedPvpServerStatus();
        }
        else if (operation == ConnectionOperation.JoinDedicatedPvp)
        {
            if (CurrentLobby == null)
            {
                RejectConnection(response, "当前还没有 PVP 房间，请先创建房间");
                return;
            }
            if (CurrentLobby.IsStarted.Value)
            {
                RejectConnection(response, "对局进行中");
                return;
            }
            if (CurrentLobby.PlayerCount >= GetMaxPlayers(LobbyGameMode.PVP))
            {
                RejectConnection(response, "房间已满");
                return;
            }
        }
        else
        {
            RejectConnection(response, "无效的 PVP 房间操作");
            return;
        }

        response.Approved = true;
        response.CreatePlayerObject = false;
        m_PendingConnections[clientId] = payload;
    }

    private static void RejectConnection(
        NetworkManager.ConnectionApprovalResponse response,
        string reason)
    {
        response.Approved = false;
        response.CreatePlayerObject = false;
        response.Reason = reason;
    }

    private void OnClientConnected(ulong clientId)
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        if (networkManager != null && networkManager.IsServer &&
            m_PendingConnections.TryGetValue(clientId, out ConnectionPayload payload))
        {
            m_PendingConnections.Remove(clientId);
            ConnectionOperation operation = (ConnectionOperation)payload.operation;
            if (m_IsDedicatedPvpServer && operation == ConnectionOperation.CreateDedicatedPvp)
            {
                if (!CreateDedicatedPvpLobby(clientId, payload))
                {
                    m_PendingDedicatedCreatorClientId = LobbyState.NoRoomOwner;
                    RefreshDedicatedPvpServerStatus();
                    networkManager.DisconnectClient(clientId, "创建 PVP 房间失败");
                    return;
                }
                m_PendingDedicatedCreatorClientId = LobbyState.NoRoomOwner;
            }

            if (CurrentLobby == null)
            {
                networkManager.DisconnectClient(clientId, "房间状态不存在");
                return;
            }

            string name = string.IsNullOrWhiteSpace(payload.playerName) ? "玩家" : payload.playerName;
            m_ClientNames[clientId] = name;
            if (CurrentLobby.GameMode == LobbyGameMode.PVP)
                CurrentLobby.AddPvpPlayer(clientId, name);
            else
                CurrentLobby.AddPlayer(name);

            if (!m_JoinOrder.Contains(clientId))
                m_JoinOrder.Add(clientId);

            if (m_IsDedicatedPvpServer && !CurrentLobby.HasRoomOwner)
                CurrentLobby.SetRoomOwner(clientId);

            if (IsHost && discovery != null)
                discovery.UpdateBroadcastInfo(CurrentLobby.PlayerCount, CurrentLobby.IsStarted.Value);
            if (m_IsDedicatedPvpServer)
                RefreshDedicatedPvpServerStatus();
        }

        OnLobbyChanged?.Invoke();
    }

    private void OnClientDisconnected(ulong clientId)
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        bool isServer = networkManager != null && networkManager.IsServer;
        bool wasPendingCreator = clientId == m_PendingDedicatedCreatorClientId;
        m_PendingConnections.Remove(clientId);
        if (wasPendingCreator)
            m_PendingDedicatedCreatorClientId = LobbyState.NoRoomOwner;

        if (isServer && CurrentLobby != null && CurrentLobby.IsSpawned)
        {
            bool wasRoomOwner = CurrentLobby.IsRoomOwner(clientId);
            m_ClientNames.TryGetValue(clientId, out string disconnectedName);
            m_ClientNames.Remove(clientId);
            m_JoinOrder.Remove(clientId);
            if (CurrentLobby.GameMode == LobbyGameMode.PVP)
                CurrentLobby.RemovePvpPlayer(clientId);
            else if (!string.IsNullOrEmpty(disconnectedName))
                CurrentLobby.RemovePlayer(disconnectedName);

            if (m_IsDedicatedPvpServer && CurrentLobby.GameMode == LobbyGameMode.PVP)
            {
                if (CurrentLobby.PlayerCount == 0 && m_PendingConnections.Count == 0)
                {
                    ResetDedicatedPvpRoom();
                }
                else if (wasRoomOwner && m_JoinOrder.Count > 0)
                {
                    CurrentLobby.SetRoomOwner(m_JoinOrder[0]);
                    Debug.Log("[Dedicated PVP] 房主已退出，权限转移给 client=" + m_JoinOrder[0]);
                }
                else if (wasRoomOwner)
                {
                    // 已审批但尚未完成连接的玩家到达后，会自动接管这个暂时无主的房间。
                    CurrentLobby.SetRoomOwner(LobbyState.NoRoomOwner);
                }
            }
            else if (IsHost && discovery != null)
            {
                discovery.UpdateBroadcastInfo(CurrentLobby.PlayerCount, CurrentLobby.IsStarted.Value);
            }
        }

        if (!isServer && clientId == NetworkManager.ServerClientId)
        {
            m_IsInLobby = false;
            m_IsDedicatedPvpSession = false;
            if (CurrentLobby != null)
                CurrentLobby.OnChanged -= HandleCurrentLobbyChanged;
            CurrentLobby = null;
        }

        if (isServer && m_IsDedicatedPvpServer)
            RefreshDedicatedPvpServerStatus();

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

    private void ResetDedicatedPvpRoom()
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        if (!m_IsDedicatedPvpServer || networkManager == null || !networkManager.IsServer)
            return;

        LobbyState lobby = CurrentLobby;
        if (lobby != null)
        {
            lobby.OnChanged -= HandleCurrentLobbyChanged;
            CurrentLobby = null;
            NetworkObject networkObject = lobby.NetworkObject;
            if (networkObject != null && networkObject.IsSpawned)
                networkObject.Despawn(true);
            else
                Destroy(lobby.gameObject);
        }

        ResetSessionCollections();
        m_PendingDedicatedCreatorClientId = LobbyState.NoRoomOwner;
        m_IsInLobby = false;
        m_PendingRoomName = string.Empty;
        m_PendingRoomMode = LobbyGameMode.PVP;
        m_ActiveGameMode = LobbyGameMode.PVP;

        if (SceneManager.GetActiveScene().name == "GamePVP" && networkManager.SceneManager != null)
        {
            m_IsDedicatedRoomResetting = true;
            SceneEventProgressStatus status = networkManager.SceneManager.LoadScene("GameStart", LoadSceneMode.Single);
            if (status != SceneEventProgressStatus.Started)
            {
                m_IsDedicatedRoomResetting = false;
                Debug.LogError("[Dedicated PVP] 返回 GameStart 失败：" + status);
            }
        }
        else
        {
            m_IsDedicatedRoomResetting = false;
        }

        RefreshDedicatedPvpServerStatus();
        Debug.Log("[Dedicated PVP] 房间已清空，服务器恢复为空闲状态");
        OnLobbyChanged?.Invoke();
    }

    // ---- 场景切换完成后生成玩家 ----

    private void OnLoadEventCompleted(
        string sceneName,
        LoadSceneMode loadSceneMode,
        List<ulong> clientsCompleted,
        List<ulong> clientsTimedOut)
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        if (networkManager == null || !networkManager.IsServer)
            return;

        if (m_IsDedicatedPvpServer && sceneName == "GameStart")
        {
            m_IsDedicatedRoomResetting = false;
            RefreshDedicatedPvpServerStatus();
            Debug.Log("[Dedicated PVP] GameStart 已重新载入，可以创建下一间房");
            return;
        }

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
        RefreshDedicatedPvpServerStatus();
        OnLobbyChanged?.Invoke();
    }

    private void HandleCurrentLobbyChanged()
    {
        if (IsHost && discovery != null && CurrentLobby != null)
            discovery.UpdateBroadcastInfo(CurrentLobby.PlayerCount, CurrentLobby.IsStarted.Value);
        RefreshDedicatedPvpServerStatus();
        OnLobbyChanged?.Invoke();
    }

    private void OnLocalSceneLoaded(Scene scene, LoadSceneMode loadSceneMode)
    {
        if (m_IsDedicatedPvpServer)
            return;
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

    private string NormalizeDedicatedServerAddress()
    {
        return string.IsNullOrWhiteSpace(dedicatedServerAddress)
            ? string.Empty
            : dedicatedServerAddress.Trim();
    }

    private int GetDedicatedServerPort()
    {
        int maximumStartPort = ushort.MaxValue - LanDiscovery.DedicatedDiscoveryPortOffset;
        return Mathf.Clamp(dedicatedServerPort, 1, maximumStartPort);
    }

    private int GetDedicatedServerRoomCount()
    {
        int maximumCount = ushort.MaxValue -
                           (GetDedicatedServerPort() + LanDiscovery.DedicatedDiscoveryPortOffset) + 1;
        return Mathf.Clamp(dedicatedServerRoomCount, 1, Mathf.Max(1, maximumCount));
    }

    private void RefreshDedicatedPvpServerStatus()
    {
        if (!m_IsDedicatedPvpServer || discovery == null)
            return;

        bool hasRoom = CurrentLobby != null && CurrentLobby.GameMode == LobbyGameMode.PVP;
        bool canCreate = !hasRoom && !m_IsDedicatedRoomResetting &&
                         m_PendingDedicatedCreatorClientId == LobbyState.NoRoomOwner;
        discovery.UpdateDedicatedPvpStatus(
            hasRoom ? CurrentLobby.GetRoomName() : string.Empty,
            hasRoom ? CurrentLobby.PlayerCount : 0,
            GetMaxPlayers(LobbyGameMode.PVP),
            hasRoom && CurrentLobby.IsStarted.Value,
            hasRoom,
            canCreate);
    }

    private void ConfigurePendingRoom(string roomName, LobbyGameMode gameMode)
    {
        BrowsingGameMode = gameMode;
        m_PendingRoomMode = gameMode;
        m_ActiveGameMode = gameMode;
        m_PendingRoomName = string.IsNullOrWhiteSpace(roomName) ? "我的房间" : roomName.Trim();
    }

    private void ConfigureClientPayload(
        NetworkManager networkManager,
        LobbyGameMode gameMode,
        ConnectionOperation operation = ConnectionOperation.LanJoin,
        string roomName = "")
    {
        var payload = new ConnectionPayload
        {
            playerName = PlayerName,
            gameMode = (byte)gameMode,
            operation = (byte)operation,
            roomName = roomName,
            clientVersion = Application.version,
        };
        networkManager.NetworkConfig.ConnectionData = Encoding.UTF8.GetBytes(JsonUtility.ToJson(payload));
    }

    private static void ConfigureNetworkForLobby(NetworkManager networkManager)
    {
        networkManager.NetworkConfig.ConnectionApproval = true;
        networkManager.NetworkConfig.EnableSceneManagement = true;
    }

    private static ConnectionPayload DecodeConnectionPayload(byte[] payload)
    {
        if (payload == null || payload.Length == 0)
            return new ConnectionPayload
            {
                playerName = "玩家",
                gameMode = (byte)LobbyGameMode.PVE,
                operation = (byte)ConnectionOperation.LanJoin,
            };

        try
        {
            ConnectionPayload result = JsonUtility.FromJson<ConnectionPayload>(Encoding.UTF8.GetString(payload));
            if (string.IsNullOrWhiteSpace(result.playerName))
                result.playerName = "玩家";
            return result;
        }
        catch
        {
            return new ConnectionPayload
            {
                playerName = "玩家",
                gameMode = (byte)LobbyGameMode.PVE,
                operation = (byte)ConnectionOperation.LanJoin,
            };
        }
    }

    private void ResetSessionCollections()
    {
        m_PendingConnections.Clear();
        m_ClientNames.Clear();
        m_JoinOrder.Clear();
    }
}
