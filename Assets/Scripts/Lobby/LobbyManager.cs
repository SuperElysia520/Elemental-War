using System;
using System.Collections;
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
    public int dedicatedServerRoomCount = 10;

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

    [Header("PVP 结算")]
    [Tooltip("胜负确定后显示结算的秒数；结束后全员回 GameLobby，并保留原房间。")]
    [Min(1f)]
    public float pvpSettlementDuration = 8f;

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
    public bool IsConnecting => m_IsConnecting;
    public bool IsReturningToLobby => m_IsReturningToLobby || m_IsPvpMatchReturning;
    public bool IsLocalPvpProfileSyncInProgress => m_LocalProfileRefreshRoutine != null || m_LocalProfileValidationPending;
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
    public string ConnectionStatus { get; private set; } = "";
    private bool m_RequestingTicket;
    private bool m_IsConnecting;
    private bool m_IsReturningToLobby;
    private bool m_IsApplicationQuitting;
    private int m_ConnectionAttempt;
    private AccountClient m_ProfileAccount;
    private string m_LocalPvpAccountId;
    private Coroutine m_LocalProfileRefreshRoutine;
    private bool m_LocalProfileRefreshRequested;
    private bool m_LocalProfileValidationPending;
    private int m_LocalProfileRequestId;
    private int m_LocalStartPreparationId;
    private int m_PvpStartPreparationId;
    private Coroutine m_PvpStartPreparationRoutine;
    private string m_PvpStartPreparationError;
    private readonly HashSet<ulong> m_PvpStartWaitingClients = new HashSet<ulong>();
    private readonly HashSet<ulong> m_PvpStartParticipants = new HashSet<ulong>();
    private readonly Dictionary<ulong, int> m_ProfileValidationRequests = new Dictionary<ulong, int>();
    private readonly Dictionary<ulong, PlayerProfileData> m_ClientProfiles = new Dictionary<ulong, PlayerProfileData>();
    private readonly Dictionary<ulong, NetworkManager.ConnectionApprovalResponse> m_AuthenticatingClients =
        new Dictionary<ulong, NetworkManager.ConnectionApprovalResponse>();

    [Serializable]
    private struct ConnectionPayload
    {
        public string playerName;
        public byte gameMode;
        public byte operation;
        public string roomName;
        public string clientVersion;
        public int protocolVersion;
        public string gameTicket;
    }

    private enum ConnectionOperation : byte
    {
        LanJoin = 0,
        CreateDedicatedPvp = 1,
        JoinDedicatedPvp = 2,
        JoinMatchedPvp = 3,
    }

    private bool m_IsInLobby;
    private bool m_IsDedicatedPvpServer;
    private bool m_IsDedicatedPvpSession;
    private bool m_IsDedicatedRoomResetting;
    private bool m_IsPvpMatchLoading;
    private bool m_IsPvpMatchReturning;
    private bool m_DedicatedLobbyReturnLoadPending;
    private Coroutine m_DedicatedLobbyReturnRoutine;
    private bool m_NetworkCallbacksRegistered;
    private NetworkSceneManager m_ClientNetworkSceneManager;
    private bool m_IsClientPvpSceneLoading;
    private bool m_PvpDisconnectedDuringLoad;
    private ulong m_PendingDedicatedCreatorClientId = LobbyState.NoRoomOwner;
    private string m_PendingRoomName;
    private LobbyGameMode m_PendingRoomMode = LobbyGameMode.PVE;
    private LobbyGameMode m_ActiveGameMode = LobbyGameMode.PVE;
    private MatchAdmissionData m_MatchedAdmission;
    private Coroutine m_MatchedStartRoutine;
    private bool m_AbortingMatchedSession;

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
        UnbindLocalPvpProfile();
        if (m_IsReturningToLobby && !m_IsApplicationQuitting)
            LogPvpReturnState("owner-destroyed-during-return");
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

    private void OnApplicationQuit() => m_IsApplicationQuitting = true;

    private void Update()
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        LobbyState lobby = CurrentLobby;
        if (!m_IsDedicatedPvpServer || networkManager == null || !networkManager.IsServer ||
            lobby == null || !lobby.IsSpawned || !lobby.IsPvpMatchOver || m_IsDedicatedRoomResetting ||
            SceneManager.GetActiveScene().name != GameFlowScenes.PvpMatch)
            return;

        double now = networkManager.ServerTime.Time;
        if (lobby.PvpReturnToLobbyAtServerTime.Value == 0d)
        {
            float duration = float.IsNaN(pvpSettlementDuration) || float.IsInfinity(pvpSettlementDuration)
                ? 8f : Mathf.Max(1f, pvpSettlementDuration);
            lobby.BeginPvpSettlement(now + duration);
        }
        if (lobby.HasPvpSettlementCountdown && now >= lobby.PvpReturnToLobbyAtServerTime.Value)
        {
            m_IsPvpMatchReturning = true;
            RequestDedicatedReturnToLobby();
        }
    }

    public void SetBrowsingGameMode(LobbyGameMode gameMode)
    {
        if (m_IsInLobby || m_IsConnecting)
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
        if (networkManager == null || networkManager.IsListening || m_IsConnecting)
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
        if (SceneManager.GetActiveScene().name != GameFlowScenes.MultiplayerLobby ||
            !GameFlowScenes.TryValidateSceneAvailable(GameFlowScenes.MultiplayerLobby, out _))
        {
            Debug.LogError("[Dedicated PVP] 必须先载入已制作且加入 Build Settings 的 GameLobby 场景，再启动服务器。");
            return false;
        }
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
        ConfigureNetworkForLobby(networkManager, usesAccountApproval: true);
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
        // A headless room needs simulation ticks, not an unlimited menu/render loop on the cloud CPU.
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 60;
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

        // Spawn serializes the initial NetworkVariables immediately. The creator must not receive the prefab's PVE default.
        if (m_IsDedicatedPvpServer)
            lobby.GameModeValue.Reset((byte)LobbyGameMode.PVP);
        if (m_MatchedAdmission != null)
            lobby.MatchedSessionId.Reset(new Unity.Collections.FixedString64Bytes(m_MatchedAdmission.matchId));
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
        if (networkManager == null || networkManager.IsListening || m_IsConnecting || room == null)
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

    public bool TryJoinMatchedPvp(string address, int port, out string message)
        => TryConnectDedicatedPvp(ConnectionOperation.JoinMatchedPvp, "团队对抗", address, port, out message);

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
        if (!GameFlowScenes.TryValidateSceneAvailable(GameFlowScenes.MultiplayerLobby, out message))
            return false;
        AccountClient account = AccountClient.EnsureInstance();
        if (!account.IsAuthenticated) { message = "请先在角色页面登录账号。"; return false; }
        if (m_IsConnecting || m_RequestingTicket) { message = "正在验证账号并连接，请稍候。"; return false; }
        if (account.IsBusy) { message = "账号正在同步，请稍候再加入房间。"; return false; }
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
        m_LocalPvpAccountId = account.Profile.accountId;
        m_PvpDisconnectedDuringLoad = false;

        transport.SetConnectionData(serverAddress.Trim(), port);
        ConfigureNetworkForLobby(networkManager, usesAccountApproval: true);
        int attempt = ++m_ConnectionAttempt;
        m_RequestingTicket = true;
        m_IsConnecting = true;
        ConnectionStatus = message = "正在验证账号并连接 PVP 房间…";
        Action<Action<string, string>> requestTicket = operation == ConnectionOperation.JoinMatchedPvp
            ? account.RequestMatchedGameTicket : account.RequestGameTicket;
        requestTicket((ticket, error) =>
        {
            if (this == null || attempt != m_ConnectionAttempt) return;
            m_RequestingTicket = false;
            if (string.IsNullOrEmpty(ticket))
            {
                m_IsConnecting = false;
                m_IsDedicatedPvpSession = false;
                ConnectionStatus = string.IsNullOrEmpty(error) ? "账号验证失败，请重新登录。" : error;
                OnLobbyChanged?.Invoke();
                return;
            }
            if (networkManager == null || networkManager.IsListening || networkManager.ShutdownInProgress)
            {
                m_IsConnecting = false;
                m_IsDedicatedPvpSession = false;
                ConnectionStatus = "网络状态已改变，请重新加入房间。";
                OnLobbyChanged?.Invoke();
                return;
            }
            ConfigureClientPayload(networkManager, LobbyGameMode.PVP, operation, roomName, ticket);
            if (!networkManager.StartClient())
            {
                m_IsConnecting = false;
                m_IsDedicatedPvpSession = false;
                ConnectionStatus = "连接专用服务器失败。";
            }
            else
            {
                m_IsInLobby = true;
                StopDedicatedPvpRoomDiscovery();
                ConnectionStatus = "正在连接 PVP 房间…";
            }
            OnLobbyChanged?.Invoke();
        });
        // Configuration failures may complete synchronously before StartCoroutine returns.
        message = ConnectionStatus;
        return m_IsConnecting || m_IsInLobby;
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
        if (room.accountProtocolVersion != LanDiscovery.AccountProtocolVersion)
        {
            message = "PVP 服务器尚未更新为当前账号版本，请同时更新客户端与专用服务器。";
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
        if (m_IsDedicatedPvpServer)
        {
            Debug.LogWarning("[Dedicated PVP] 服务器进程不能通过玩家离开流程关闭");
            return;
        }

        if (discovery != null)
            discovery.StopBroadcast();

        ReturnPvpClientToLobbyIfNeeded("leave-request");
        ResetLocalConnectionState("");
        if (networkManager != null && networkManager.IsListening)
            networkManager.Shutdown();
        ConnectionStatus = "";
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

        if (!ValidateStartConditions(out string message))
        {
            CurrentLobby.SendStartGameResult(requesterClientId, message);
            return;
        }
        if (m_PvpStartPreparationRoutine != null)
        {
            CurrentLobby.SendStartGameResult(requesterClientId, "全员角色与配装正在验证，请稍候。");
            return;
        }
        LobbyState lobby = CurrentLobby;
        int preparationId = ++m_PvpStartPreparationId;
        m_PvpStartPreparationError = null;
        m_PvpStartWaitingClients.Clear();
        m_PvpStartParticipants.Clear();
        foreach (LobbyPlayerData player in lobby.GetPlayers())
        {
            m_PvpStartWaitingClients.Add(player.ClientId);
            m_PvpStartParticipants.Add(player.ClientId);
        }
        lobby.PvpIsPreparingMatch.Value = true;
        m_PvpStartPreparationRoutine = StartCoroutine(PreparePvpMatchProfiles(lobby, preparationId, requesterClientId));
        lobby.SendStartGameResult(requesterClientId, "正在验证全员最新角色与配装…");
        lobby.PrepareMatchProfilesClientRpc(preparationId);
    }

    public void DisplayServerStartGameResult(string message)
    {
        ConnectionStatus = message;
        OnLobbyChanged?.Invoke();
    }

    private IEnumerator PreparePvpMatchProfiles(LobbyState lobby, int preparationId, ulong requesterClientId)
    {
        yield return null;
        float timeoutAt = Time.realtimeSinceStartup + 90f;
        try
        {
            while (CurrentLobby == lobby && lobby != null && lobby.IsSpawned &&
                   m_PvpStartWaitingClients.Count > 0 && string.IsNullOrEmpty(m_PvpStartPreparationError) &&
                   Time.realtimeSinceStartup < timeoutAt && !m_IsApplicationQuitting)
                yield return null;
            if (m_IsApplicationQuitting || CurrentLobby != lobby || lobby == null || !lobby.IsSpawned)
                yield break;
            string message = m_PvpStartPreparationError;
            if (string.IsNullOrEmpty(message) && m_PvpStartWaitingClients.Count > 0)
                message = "全员角色与配装验证超时，开局已取消。请重新登录或加入房间后重试。";
            if (string.IsNullOrEmpty(message) && lobby.IsMatchmaking)
            {
                bool confirmed = false;
                yield return NotifyMatchedServer("started", lobby.MatchedSessionId.Value.ToString(), success => confirmed = success);
                if (!confirmed) message = "匹配已取消或匹配服务不可用，请重新准备。";
            }
            if (string.IsNullOrEmpty(message))
                TryStartGameOnServer(out message, preparationComplete: true);
            ulong recipient = lobby.IsRoomOwner(requesterClientId) ? requesterClientId : lobby.RoomOwnerClientId.Value;
            NetworkManager networkManager = NetworkManager.Singleton;
            if (networkManager != null && networkManager.ConnectedClients.ContainsKey(recipient))
                lobby.SendStartGameResult(recipient, message);
        }
        finally
        {
            if (lobby != null && lobby.IsSpawned && !m_IsApplicationQuitting)
            {
                lobby.PvpIsPreparingMatch.Value = false;
                lobby.CancelMatchPreparationClientRpc(preparationId);
            }
            m_PvpStartWaitingClients.Clear();
            m_PvpStartParticipants.Clear();
            m_PvpStartPreparationRoutine = null;
        }
    }

    public void HandlePvpMatchPreparationFailure(ulong clientId, int preparationId, string error)
    {
        if (!m_IsDedicatedPvpServer || CurrentLobby == null || !CurrentLobby.PvpIsPreparingMatch.Value ||
            preparationId != m_PvpStartPreparationId || !m_PvpStartWaitingClients.Contains(clientId))
            return;
        string detail = string.IsNullOrWhiteSpace(error) ? "账号或档案验证失败。" : error;
        if (detail.Length > 240) detail = detail.Substring(0, 240);
        m_PvpStartPreparationError = "有玩家的角色与配装验证失败，开局已取消：" + detail;
    }

    private bool ValidateStartConditions(out string message, bool preparationComplete = false)
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
        if (m_IsDedicatedRoomResetting || m_IsPvpMatchLoading)
        {
            message = "场景正在切换，请稍后再开始游戏";
            return false;
        }
        if (CurrentLobby.PvpIsPreparingMatch.Value && !preparationComplete)
        {
            message = "全员角色与配装正在验证，请稍候";
            return false;
        }
        if (CurrentLobby.GameMode == LobbyGameMode.PVP &&
            (m_ProfileValidationRequests.Count > 0 || IsLocalPvpProfileSyncInProgress ||
             (!m_IsDedicatedPvpServer && AccountClient.Instance != null && AccountClient.Instance.IsBusy)))
        {
            message = "角色或配装正在同步，请同步完成后再开始游戏";
            return false;
        }

        LobbyGameMode gameMode = CurrentLobby.GameMode;
        if (gameMode == LobbyGameMode.PVP)
        {
            if (CurrentLobby.IsMatchmaking && m_IsDedicatedPvpServer && (m_MatchedAdmission == null || CurrentLobby.PlayerCount != m_MatchedAdmission.roster.Length))
            { message = "正在等待匹配名单中的全部玩家连接。"; return false; }
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

    private bool TryStartGameOnServer(out string message, bool preparationComplete = false)
    {
        message = string.Empty;
        NetworkManager networkManager = NetworkManager.Singleton;
        if (networkManager == null || !networkManager.IsServer)
        {
            message = "只有服务器可以执行场景切换";
            return false;
        }
        if (!ValidateStartConditions(out message, preparationComplete))
            return false;

        if (networkManager.SceneManager == null)
        {
            message = "网络场景管理器未就绪";
            return false;
        }

        LobbyGameMode gameMode = CurrentLobby.GameMode;
        m_ActiveGameMode = gameMode;
        string sceneName = GetGameSceneName(gameMode);
        if (!GameFlowScenes.TryValidateSceneAvailable(sceneName, out message))
            return false;

        // PVE 保持原有顺序：先锁定房间，再请求进入 GamePVE。
        if (gameMode == LobbyGameMode.PVE)
            CurrentLobby.SetStarted(true);

        if (gameMode == LobbyGameMode.PVP)
            m_IsPvpMatchLoading = true;
        SceneEventProgressStatus status = networkManager.SceneManager.LoadScene(sceneName, LoadSceneMode.Single);
        if (status != SceneEventProgressStatus.Started)
        {
            m_IsPvpMatchLoading = false;
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
            if (payload.protocolVersion != LanDiscovery.AccountProtocolVersion || string.IsNullOrEmpty(payload.gameTicket))
            {
                RejectConnection(response, "请使用支持 RifleGirl 账号系统的新版客户端并登录。");
                return;
            }
            response.Pending = true;
            ulong clientId = request.ClientNetworkId;
            m_AuthenticatingClients[clientId] = response;
            StartCoroutine(AccountClient.RedeemServerTicket(payload.gameTicket, (profile, error) =>
            {
                if (this == null || !m_AuthenticatingClients.ContainsKey(clientId)) return;
                if (profile == null)
                {
                    RejectConnection(response, string.IsNullOrEmpty(error) ? "账号验证失败，请重新登录。" : error);
                    m_AuthenticatingClients.Remove(clientId);
                    response.Pending = false;
                    return;
                }
                foreach (var connectedProfile in m_ClientProfiles.Values)
                {
                    if (connectedProfile.accountId == profile.accountId)
                    {
                        RejectConnection(response, "此账号已在当前房间中。");
                        m_AuthenticatingClients.Remove(clientId);
                        response.Pending = false;
                        return;
                    }
                }
                payload.playerName = profile.username;
                payload.gameTicket = null;
                StartCoroutine(ApproveDedicatedReservation(clientId, payload, response, profile));
            }));
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
        if (m_IsDedicatedRoomResetting || SceneManager.GetActiveScene().name != GameFlowScenes.MultiplayerLobby)
        {
            RejectConnection(response, "服务器正在切换场景，请稍后重试");
            return;
        }
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
            if (CurrentLobby.IsStarted.Value || CurrentLobby.PvpIsPreparingMatch.Value)
            {
                RejectConnection(response, "对局已开始或全员配装正在验证，请稍后加入");
                return;
            }
            // Approval is asynchronous; reserve capacity for accepted clients whose connection event has not run yet.
            if (CurrentLobby.PlayerCount + m_PendingConnections.Count >= GetMaxPlayers(LobbyGameMode.PVP))
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

    private IEnumerator ApproveDedicatedReservation(ulong clientId, ConnectionPayload payload,
        NetworkManager.ConnectionApprovalResponse response, PlayerProfileData profile)
    {
        string reservation = null, error = null;
        yield return AccountClient.ReadSlotReservation(GetGamePort(), (id, message) => { reservation = id; error = message; });
        if (!m_AuthenticatingClients.Remove(clientId)) yield break;
        bool matched = (ConnectionOperation)payload.operation == ConnectionOperation.JoinMatchedPvp;
        if (!string.IsNullOrEmpty(error)) RejectConnection(response, error);
        else if (matched) ApprovalCheckMatched(clientId, payload, response, profile, reservation);
        else if (!string.IsNullOrEmpty(reservation) || (CurrentLobby != null && CurrentLobby.IsMatchmaking))
            RejectConnection(response, "此服务器已分配给匹配对局，请选择其他空闲房间。");
        else ApprovalCheckDedicatedPvp(clientId, payload, response);
        if (response.Approved)
        {
            foreach (PlayerProfileData existing in m_ClientProfiles.Values)
                if (existing.accountId == profile.accountId)
                { RejectConnection(response, "此账号已在当前房间中。"); m_PendingConnections.Remove(clientId); break; }
            if (response.Approved) m_ClientProfiles[clientId] = profile;
        }
        response.Pending = false;
    }

    private void ApprovalCheckMatched(ulong clientId, ConnectionPayload payload,
        NetworkManager.ConnectionApprovalResponse response, PlayerProfileData profile, string reservation)
    {
        MatchAdmissionData admission = profile.match;
        if (m_IsDedicatedRoomResetting || SceneManager.GetActiveScene().name != GameFlowScenes.MultiplayerLobby
            || !string.Equals(payload.clientVersion, Application.version, StringComparison.Ordinal)
            || admission == null || admission.serverPort != GetGamePort() || admission.matchId != reservation
            || admission.roster == null || admission.roster.Length < 2 || admission.roster.Length > 10
            || string.IsNullOrEmpty(reservation))
        { RejectConnection(response, "匹配凭证无效或对局已取消，请重新匹配。"); return; }
        if (CurrentLobby != null && (!CurrentLobby.IsMatchmaking || CurrentLobby.IsStarted.Value || CurrentLobby.PvpIsPreparingMatch.Value)
            || m_PendingDedicatedCreatorClientId != LobbyState.NoRoomOwner
            || m_MatchedAdmission != null && m_MatchedAdmission.matchId != admission.matchId)
        { RejectConnection(response, "服务器已有其他对局，请重新匹配。"); return; }
        bool found = false;
        var accounts = new HashSet<string>();
        int red = 0, blue = 0;
        foreach (MatchRosterMemberData member in admission.roster)
        {
            if (member == null || string.IsNullOrEmpty(member.accountId) || !accounts.Add(member.accountId))
            { RejectConnection(response, "匹配名单无效。"); return; }
            if (member.team == (byte)LobbyTeam.Red) red++;
            else if (member.team == (byte)LobbyTeam.Blue) blue++;
            else { RejectConnection(response, "匹配阵营无效。"); return; }
            if (member.accountId == profile.accountId) found = true;
        }
        if (!found || red < 1 || blue < 1 || red > 5 || blue > 5)
        { RejectConnection(response, "账号不在有效匹配名单中。"); return; }
        m_MatchedAdmission = admission;
        response.Approved = true; response.CreatePlayerObject = false;
        m_PendingConnections[clientId] = payload;
        RefreshDedicatedPvpServerStatus();
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
        if (networkManager != null && !networkManager.IsServer && clientId == networkManager.LocalClientId)
        {
            m_IsConnecting = false;
            m_IsInLobby = true;
            ConnectionStatus = CurrentLobby != null ? "已加入房间。" : "已连接，正在同步房间…";
            if (m_IsDedicatedPvpSession)
            {
                BindLocalPvpProfile();
                if (m_ClientNetworkSceneManager == null && networkManager.SceneManager != null)
                {
                    m_ClientNetworkSceneManager = networkManager.SceneManager;
                    m_ClientNetworkSceneManager.OnSceneEvent += OnClientNetworkSceneEvent;
                }
            }
        }
        if (networkManager != null && networkManager.IsServer &&
            m_PendingConnections.TryGetValue(clientId, out ConnectionPayload payload))
        {
            m_PendingConnections.Remove(clientId);
            ConnectionOperation operation = (ConnectionOperation)payload.operation;
            if (m_IsDedicatedPvpServer && (operation == ConnectionOperation.CreateDedicatedPvp
                || operation == ConnectionOperation.JoinMatchedPvp && CurrentLobby == null))
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
            if (m_IsDedicatedPvpServer && (CurrentLobby.IsStarted.Value || CurrentLobby.PvpIsPreparingMatch.Value))
            {
                networkManager.DisconnectClient(clientId, "对局已经开始，请等待下一局");
                return;
            }

            string name = string.IsNullOrWhiteSpace(payload.playerName) ? "玩家" : payload.playerName;
            m_ClientNames[clientId] = name;
            if (CurrentLobby.GameMode == LobbyGameMode.PVP)
                CurrentLobby.AddPvpPlayer(clientId, name, m_ClientProfiles.TryGetValue(clientId, out var profile) ? profile : null);
            else
                CurrentLobby.AddPlayer(name);

            if (!m_JoinOrder.Contains(clientId))
                m_JoinOrder.Add(clientId);

            if (m_IsDedicatedPvpServer && !CurrentLobby.HasRoomOwner)
                CurrentLobby.SetRoomOwner(clientId);

            if (operation == ConnectionOperation.JoinMatchedPvp && m_MatchedAdmission != null)
            {
                string id = m_ClientProfiles[clientId].accountId;
                foreach (MatchRosterMemberData member in m_MatchedAdmission.roster)
                    if (member.accountId == id) CurrentLobby.SetMatchedTeam(clientId, (LobbyTeam)member.team);
                if (m_MatchedStartRoutine == null) m_MatchedStartRoutine = StartCoroutine(StartMatchedSession());
            }

            if (IsHost && discovery != null)
                discovery.UpdateBroadcastInfo(CurrentLobby.PlayerCount, CurrentLobby.IsStarted.Value);
            if (m_IsDedicatedPvpServer)
                RefreshDedicatedPvpServerStatus();
        }

        OnLobbyChanged?.Invoke();
    }

    private void OnClientDisconnected(ulong clientId)
    {
        m_AuthenticatingClients.Remove(clientId);
        m_ClientProfiles.Remove(clientId);
        m_ProfileValidationRequests.Remove(clientId);
        if (NetworkManager.Singleton != null && !NetworkManager.Singleton.IsServer)
            ConnectionStatus = NetworkManager.Singleton.DisconnectReason;
        NetworkManager networkManager = NetworkManager.Singleton;
        bool isServer = networkManager != null && networkManager.IsServer;
        bool wasPendingCreator = clientId == m_PendingDedicatedCreatorClientId;
        m_PendingConnections.Remove(clientId);
        if (wasPendingCreator)
            m_PendingDedicatedCreatorClientId = LobbyState.NoRoomOwner;

        if (isServer && CurrentLobby != null && CurrentLobby.IsSpawned)
        {
            if (CurrentLobby.PvpIsPreparingMatch.Value && m_PvpStartParticipants.Contains(clientId))
                m_PvpStartPreparationError = "有玩家离开房间，开局已取消。请确认成员和队伍后重新开始。";
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
                else if (wasRoomOwner && !CurrentLobby.IsMatchmaking)
                {
                    DissolveDedicatedRoom();
                }
                else if (wasRoomOwner && m_JoinOrder.Count > 0)
                {
                    CurrentLobby.SetRoomOwner(m_JoinOrder[0]);
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

        // NGO reports LocalClientId here after a client has connected, not the server's reserved zero ID.
        if (!isServer)
        {
            m_PvpDisconnectedDuringLoad |= m_IsClientPvpSceneLoading;
            ReturnPvpClientToLobbyIfNeeded("client-disconnected");
            ResetLocalConnectionState(string.IsNullOrWhiteSpace(ConnectionStatus) ? "连接已断开，请重新加入房间。" : ConnectionStatus);
        }

        if (isServer && m_IsDedicatedPvpServer)
            RefreshDedicatedPvpServerStatus();

        OnLobbyChanged?.Invoke();
    }

    private void OnPreShutdown()
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        if (networkManager != null && !networkManager.IsServer)
        {
            m_PvpDisconnectedDuringLoad |= m_IsClientPvpSceneLoading;
            ReturnPvpClientToLobbyIfNeeded("network-shutdown");
            string reason = networkManager.DisconnectReason;
            ResetLocalConnectionState(string.IsNullOrWhiteSpace(reason) ? "连接已断开，请重新加入房间。" : reason);
            OnLobbyChanged?.Invoke();
        }
        // NGO 关闭期间不能再写 NetworkList；提前解绑断线回调，下一次建房/加入时会重新注册。
        RemoveNetworkCallbacks();
    }

    private IEnumerator StartMatchedSession()
    {
        yield return null;
        string matchId = m_MatchedAdmission != null ? m_MatchedAdmission.matchId : "";
        bool started = false;
        try
        {
            float deadline = Time.unscaledTime + 50f;
            while (!m_AbortingMatchedSession && CurrentLobby != null && m_MatchedAdmission != null
                && CurrentLobby.PlayerCount < m_MatchedAdmission.roster.Length && Time.unscaledTime < deadline)
                yield return null;
            if (m_AbortingMatchedSession || CurrentLobby == null || m_MatchedAdmission == null
                || CurrentLobby.PlayerCount != m_MatchedAdmission.roster.Length) yield break;
            bool extended = false;
            yield return NotifyMatchedServer("connected", matchId, success => extended = success);
            if (!extended || CurrentLobby == null) yield break;
            HandleDedicatedStartGameRequest(CurrentLobby.RoomOwnerClientId.Value);
            while (m_PvpStartPreparationRoutine != null && !m_AbortingMatchedSession) yield return null;
            started = CurrentLobby != null && CurrentLobby.IsStarted.Value;
            if (started) yield return NotifyMatchedServer("started", matchId);
        }
        finally
        {
            m_MatchedStartRoutine = null;
            if (!started && !string.IsNullOrEmpty(matchId) && !m_IsApplicationQuitting && !m_AbortingMatchedSession)
                StartCoroutine(AbortMatchedSession(matchId));
        }
    }

    private IEnumerator NotifyMatchedServer(string eventName, string id, Action<bool> completed = null)
    {
        string error = "";
        for (int attempt = 0; attempt < 3; attempt++)
        {
            yield return AccountClient.SendMatchServerEvent(id, GetGamePort(), eventName, message => error = message);
            if (string.IsNullOrEmpty(error)) { completed?.Invoke(true); yield break; }
            yield return new WaitForSecondsRealtime(1f);
        }
        Debug.LogWarning("[PVP Matchmaking] 对局状态同步失败：" + error);
        completed?.Invoke(false);
    }

    private IEnumerator AbortMatchedSession(string id)
    {
        if (m_AbortingMatchedSession) yield break;
        m_AbortingMatchedSession = true;
        yield return NotifyMatchedServer("failed", id);
        DissolveDedicatedRoom();
        m_AbortingMatchedSession = false;
    }

    private void DissolveDedicatedRoom()
    {
        NetworkManager network = NetworkManager.Singleton;
        if (network == null || !network.IsServer) return;
        m_IsDedicatedRoomResetting = true;
        if (CurrentLobby != null) CurrentLobby.SetRoomOwner(LobbyState.NoRoomOwner);
        var clients = new List<ulong>(network.ConnectedClientsIds);
        foreach (ulong id in clients)
            if (id != NetworkManager.ServerClientId && network.ConnectedClients.ContainsKey(id))
                network.DisconnectClient(id, "队伍或房间已解散，请返回大厅重新组队。");
        if (CurrentLobby != null) ResetDedicatedPvpRoom();
    }

    private void ReturnPvpClientToLobbyIfNeeded(string source)
    {
        // Only a PVP client's local scene may be unloaded here. Dedicated-server and PVE scene flows stay network-controlled.
        // Use the actual scene rather than a cached mode: an early lobby spawn can precede its GameModeValue update.
        if (SceneManager.GetActiveScene().name == GameFlowScenes.PvpMatch && !m_IsApplicationQuitting)
            LogPvpReturnState(source);
        if (m_IsApplicationQuitting || m_IsReturningToLobby || m_IsDedicatedPvpServer ||
            SceneManager.GetActiveScene().name != GameFlowScenes.PvpMatch)
            return;
        NetworkManager networkManager = NetworkManager.Singleton;
        if (networkManager != null && networkManager.IsServer)
            return;
        m_IsReturningToLobby = true;
        StartCoroutine(ReturnPvpClientToLobby());
    }

    private IEnumerator ReturnPvpClientToLobby()
    {
        // Disconnect callbacks can fire before NGO schedules its shutdown. Wait until it has released scene/network objects.
        yield return null;
        float nextDiagnostic = Time.realtimeSinceStartup + 2f;
        while (!m_IsApplicationQuitting && NetworkManager.Singleton != null &&
               (NetworkManager.Singleton.IsListening || NetworkManager.Singleton.ShutdownInProgress))
        {
            if (Time.realtimeSinceStartup >= nextDiagnostic)
            {
                LogPvpReturnState("waiting-for-network-shutdown");
                nextDiagnostic = Time.realtimeSinceStartup + 2f;
            }
            yield return null;
        }
        if (m_IsApplicationQuitting)
            yield break;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        if (!GameFlowScenes.TryValidateSceneAvailable(GameFlowScenes.MultiplayerLobby, out string message))
        {
            ConnectionStatus = message;
            Debug.LogError("[PVP Exit] " + message);
            m_IsReturningToLobby = false;
            OnLobbyChanged?.Invoke();
            yield break;
        }
        if (SceneManager.GetActiveScene().name != GameFlowScenes.MultiplayerLobby)
        {
            LogPvpReturnState("loading-" + GameFlowScenes.MultiplayerLobby);
            yield return SceneManager.LoadSceneAsync(GameFlowScenes.MultiplayerLobby, LoadSceneMode.Single);
        }
        m_IsReturningToLobby = false;
        LogPvpReturnState("return-complete");
        OnLobbyChanged?.Invoke();
    }

    private void LogPvpReturnState(string stage)
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        Debug.Log("[PVP Exit] " + stage + " scene=" + SceneManager.GetActiveScene().name +
                  " cachedMode=" + m_ActiveGameMode + " dedicated=" + m_IsDedicatedPvpServer +
                  " session=" + m_IsDedicatedPvpSession + " returning=" + m_IsReturningToLobby +
                  " active=" + isActiveAndEnabled +
                  " listening=" + (networkManager != null && networkManager.IsListening) +
                  " shutdown=" + (networkManager != null && networkManager.ShutdownInProgress));
    }

    private void ResetLocalConnectionState(string status)
    {
        UnbindLocalPvpProfile();
        m_ConnectionAttempt++;
        m_RequestingTicket = false;
        m_IsConnecting = false;
        m_IsInLobby = false;
        m_IsDedicatedPvpSession = false;
        ConnectionStatus = status;
        if (CurrentLobby != null) CurrentLobby.OnChanged -= HandleCurrentLobbyChanged;
        CurrentLobby = null;
        ResetSessionCollections();
    }

    private void BindLocalPvpProfile()
    {
        if (m_ProfileAccount != null || m_IsDedicatedPvpServer) return;
        m_ProfileAccount = AccountClient.EnsureInstance();
        m_ProfileAccount.ProfileChanged += HandleLocalPvpProfileChanged;
    }

    private void UnbindLocalPvpProfile()
    {
        if (m_ProfileAccount != null)
            m_ProfileAccount.ProfileChanged -= HandleLocalPvpProfileChanged;
        m_ProfileAccount = null;
        m_LocalPvpAccountId = null;
        m_LocalProfileRefreshRequested = false;
        m_LocalProfileValidationPending = false;
        m_LocalStartPreparationId = 0;
        m_LocalProfileRequestId++;
        if (m_LocalProfileRefreshRoutine != null)
            StopCoroutine(m_LocalProfileRefreshRoutine);
        m_LocalProfileRefreshRoutine = null;
    }

    private void HandleLocalPvpProfileChanged(PlayerProfileData profile)
    {
        if (profile == null || !string.Equals(profile.accountId, m_LocalPvpAccountId, StringComparison.Ordinal))
        {
            ConnectionStatus = "房间身份仍是入房账号。请退出房间后再使用另一个账号加入。";
            OnLobbyChanged?.Invoke();
            return;
        }
        if (CurrentLobby != null && !CurrentLobby.IsStarted.Value)
            RefreshLocalPvpProfile();
    }

    /// <summary>Refresh the connected player's saved loadout without replacing their authenticated room identity.</summary>
    public void RefreshLocalPvpProfile()
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        if (m_IsDedicatedPvpServer || !m_IsDedicatedPvpSession || networkManager == null ||
            !networkManager.IsConnectedClient || CurrentLobby == null || CurrentLobby.IsStarted.Value ||
            SceneManager.GetActiveScene().name != GameFlowScenes.MultiplayerLobby || CurrentLobby.PvpIsPreparingMatch.Value)
            return;
        m_LocalProfileRefreshRequested = true;
        if (m_LocalProfileRefreshRoutine == null)
            m_LocalProfileRefreshRoutine = StartCoroutine(RefreshLocalPvpProfileRoutine());
    }

    public void PrepareLocalPvpMatchProfile(int preparationId)
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        if (m_IsDedicatedPvpServer || preparationId <= 0 || networkManager == null ||
            !networkManager.IsConnectedClient || CurrentLobby == null || CurrentLobby.IsStarted.Value)
            return;
        m_LocalStartPreparationId = preparationId;
        m_LocalProfileRefreshRequested = true;
        if (m_LocalProfileRefreshRoutine == null)
            m_LocalProfileRefreshRoutine = StartCoroutine(RefreshLocalPvpProfileRoutine());
    }

    public void CancelLocalPvpMatchPreparation(int preparationId)
    {
        if (preparationId == m_LocalStartPreparationId)
            m_LocalStartPreparationId = 0;
    }

    private IEnumerator RefreshLocalPvpProfileRoutine()
    {
        // ProfileChanged is emitted before AccountClient finishes the cloud request and clears IsBusy.
        yield return null;
        try
        {
            while (m_LocalProfileRefreshRequested)
            {
                m_LocalProfileRefreshRequested = false;
                AccountClient account = m_ProfileAccount;
                while (account != null && account.IsBusy) yield return null;
                NetworkManager networkManager = NetworkManager.Singleton;
                LobbyState lobby = CurrentLobby;
                if (account == null || !account.IsAuthenticated ||
                    !string.Equals(account.Profile.accountId, m_LocalPvpAccountId, StringComparison.Ordinal))
                {
                    ConnectionStatus = "请使用入房时的账号同步配装；换账号需要先退出房间。";
                    if (m_LocalStartPreparationId > 0 && lobby != null && lobby.IsSpawned)
                        lobby.FailMatchPreparationServerRpc(m_LocalStartPreparationId, ConnectionStatus);
                    OnLobbyChanged?.Invoke();
                    yield break;
                }
                if (networkManager == null || !networkManager.IsConnectedClient || lobby == null ||
                    !lobby.IsSpawned || lobby.IsStarted.Value ||
                    SceneManager.GetActiveScene().name != GameFlowScenes.MultiplayerLobby)
                    yield break;

                int requestId = ++m_LocalProfileRequestId;
                int preparationId = m_LocalStartPreparationId;
                // A preparation request arriving during an existing cloud save is satisfied by this fresh ticket.
                m_LocalProfileRefreshRequested = false;
                int connectionAttempt = m_ConnectionAttempt;
                string ticket = null;
                string ticketError = null;
                bool ticketFinished = false;
                ConnectionStatus = "正在同步已保存的角色与配装…";
                OnLobbyChanged?.Invoke();
                account.RequestGameTicket((value, error) =>
                {
                    if (this == null || connectionAttempt != m_ConnectionAttempt || requestId != m_LocalProfileRequestId)
                        return;
                    ticket = value;
                    ticketError = error;
                    ticketFinished = true;
                });
                while (!ticketFinished) yield return null;
                if (string.IsNullOrEmpty(ticket))
                {
                    ConnectionStatus = string.IsNullOrEmpty(ticketError) ? "配装同步失败，请重新登录后重试。" : ticketError;
                    if (preparationId > 0) lobby.FailMatchPreparationServerRpc(preparationId, ConnectionStatus);
                    OnLobbyChanged?.Invoke();
                    continue;
                }
                if (CurrentLobby != lobby || !lobby.IsSpawned || lobby.IsStarted.Value)
                    yield break;
                if (preparationId > 0 && preparationId != m_LocalStartPreparationId)
                    continue;
                m_LocalProfileValidationPending = true;
                lobby.RequestRefreshProfileServerRpc(ticket, requestId, preparationId);
                float timeoutAt = Time.realtimeSinceStartup + 60f;
                while (m_LocalProfileValidationPending && Time.realtimeSinceStartup < timeoutAt)
                    yield return null;
                if (m_LocalProfileValidationPending)
                {
                    m_LocalProfileValidationPending = false;
                    ConnectionStatus = "服务器同步配装超时，请重试或重新加入房间。";
                    if (preparationId > 0 && lobby.IsSpawned)
                        lobby.FailMatchPreparationServerRpc(preparationId, ConnectionStatus);
                    OnLobbyChanged?.Invoke();
                }
            }
        }
        finally
        {
            m_LocalProfileValidationPending = false;
            m_LocalProfileRefreshRoutine = null;
            OnLobbyChanged?.Invoke();
        }
    }

    public void CompleteLocalPvpProfileRefresh(int requestId, string error)
    {
        if (requestId != m_LocalProfileRequestId || !m_LocalProfileValidationPending) return;
        m_LocalProfileValidationPending = false;
        ConnectionStatus = string.IsNullOrEmpty(error) ? "角色与配装已同步到当前房间。" : error;
        OnLobbyChanged?.Invoke();
    }

    public void HandlePvpProfileRefreshRequest(ulong clientId, string gameTicket, int requestId, int preparationId = 0)
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        LobbyState lobby = CurrentLobby;
        if (!m_IsDedicatedPvpServer || networkManager == null || !networkManager.IsServer || lobby == null)
            return;
        string error = null;
        if (m_IsDedicatedRoomResetting || lobby.IsStarted.Value ||
            SceneManager.GetActiveScene().name != GameFlowScenes.MultiplayerLobby)
            error = "当前对局已开始或场景正在切换，请在大厅重新同步。";
        else if (!networkManager.ConnectedClients.ContainsKey(clientId) ||
                 !m_ClientProfiles.TryGetValue(clientId, out _) || lobby.GetPlayerCharacterIndex(clientId) == byte.MaxValue)
            error = "房间身份不存在，请重新加入房间。";
        else if (requestId <= 0 || string.IsNullOrEmpty(gameTicket) || gameTicket.Length > 512)
            error = "无效的配装验证凭证。";
        else if (m_ProfileValidationRequests.ContainsKey(clientId))
            error = "服务器正在同步此玩家的配装，请稍候。";
        else if (preparationId > 0 && (!lobby.PvpIsPreparingMatch.Value ||
                 preparationId != m_PvpStartPreparationId || !m_PvpStartWaitingClients.Contains(clientId)))
            error = "本次开局验证已取消，请在大厅重试。";
        if (error != null)
        {
            lobby.SendProfileRefreshResult(clientId, requestId, error);
            if (preparationId > 0) HandlePvpMatchPreparationFailure(clientId, preparationId, error);
            return;
        }

        string expectedAccountId = m_ClientProfiles[clientId].accountId;
        m_ProfileValidationRequests[clientId] = requestId;
        StartCoroutine(AccountClient.RedeemServerTicket(gameTicket, (profile, validationError) =>
        {
            if (this == null || !m_ProfileValidationRequests.TryGetValue(clientId, out int pendingId) || pendingId != requestId)
                return;
            m_ProfileValidationRequests.Remove(clientId);
            if (CurrentLobby != lobby || !lobby.IsSpawned ||
                !networkManager.ConnectedClients.ContainsKey(clientId) ||
                !m_ClientProfiles.TryGetValue(clientId, out PlayerProfileData previousProfile))
                return;
            if (profile == null)
                validationError = string.IsNullOrEmpty(validationError) ? "服务器无法验证已保存的配装。" : validationError;
            else if (!string.Equals(profile.accountId, expectedAccountId, StringComparison.Ordinal) ||
                     !string.Equals(previousProfile.accountId, expectedAccountId, StringComparison.Ordinal))
                validationError = "配装账号与入房账号不一致，请退出后重新加入。";
            else if (m_IsDedicatedRoomResetting || lobby.IsStarted.Value ||
                     SceneManager.GetActiveScene().name != GameFlowScenes.MultiplayerLobby)
                validationError = "对局状态已改变，请返回大厅后重试同步。";
            else if (preparationId > 0 && (!lobby.PvpIsPreparingMatch.Value ||
                     preparationId != m_PvpStartPreparationId))
                validationError = "本次开局验证已取消，请在大厅重试。";
            else if (profile.revision < previousProfile.revision)
                validationError = "配装档案已更新，请重新同步。";
            else if (lobby.UpdatePvpPlayerProfile(clientId, profile))
            {
                m_ClientProfiles[clientId] = profile;
                validationError = string.Empty;
                if (preparationId > 0 && preparationId == m_PvpStartPreparationId)
                    m_PvpStartWaitingClients.Remove(clientId);
            }
            else
                validationError = "未找到当前房间中的玩家，请重新加入。";
            lobby.SendProfileRefreshResult(clientId, requestId, validationError);
            if (!string.IsNullOrEmpty(validationError) && preparationId > 0)
                HandlePvpMatchPreparationFailure(clientId, preparationId, validationError);
            OnLobbyChanged?.Invoke();
        }));
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
        if (m_ClientNetworkSceneManager != null)
        {
            m_ClientNetworkSceneManager.OnSceneEvent -= OnClientNetworkSceneEvent;
            m_ClientNetworkSceneManager = null;
        }
        m_IsClientPvpSceneLoading = false;
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
        if (m_MatchedAdmission != null)
            StartCoroutine(NotifyMatchedServer("failed", m_MatchedAdmission.matchId));
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
        m_MatchedAdmission = null;
        m_PendingDedicatedCreatorClientId = LobbyState.NoRoomOwner;
        m_IsInLobby = false;
        m_PendingRoomName = string.Empty;
        m_PendingRoomMode = LobbyGameMode.PVP;
        m_ActiveGameMode = LobbyGameMode.PVP;

        // The last player may leave while GamePVP or GameLobby is still loading.
        // Reuse the pending return rather than issuing a competing NGO scene operation.
        RequestDedicatedReturnToLobby();

        RefreshDedicatedPvpServerStatus();
        Debug.Log("[Dedicated PVP] 房间已清空，服务器恢复为空闲状态");
        OnLobbyChanged?.Invoke();
    }

    private void RequestDedicatedReturnToLobby()
    {
        m_IsDedicatedRoomResetting = true;
        RefreshDedicatedPvpServerStatus();
        if (m_DedicatedLobbyReturnRoutine == null && !m_DedicatedLobbyReturnLoadPending)
            m_DedicatedLobbyReturnRoutine = StartCoroutine(ReturnDedicatedServerToLobby());
    }

    private IEnumerator ReturnDedicatedServerToLobby()
    {
        // StartCoroutine runs immediately; yield before callbacks can request the same transition again.
        yield return null;
        try
        {
            NetworkManager networkManager = NetworkManager.Singleton;
            while (m_IsPvpMatchLoading && networkManager != null && networkManager.IsServer &&
                   networkManager.IsListening && !m_IsApplicationQuitting)
                yield return null;
            if (networkManager == null || !networkManager.IsServer || !networkManager.IsListening ||
                m_IsApplicationQuitting)
                yield break;

            if (!GameFlowScenes.TryValidateSceneAvailable(GameFlowScenes.MultiplayerLobby, out string message))
            {
                Debug.LogError("[Dedicated PVP] " + message);
                if (CurrentLobby != null) CurrentLobby.MarkPvpReturnUnavailable();
                yield break;
            }
            if (SceneManager.GetActiveScene().name == GameFlowScenes.MultiplayerLobby)
            {
                CompleteDedicatedLobbyReturn();
                yield break;
            }
            if (networkManager.SceneManager == null)
            {
                Debug.LogError("[Dedicated PVP] 网络场景管理器未就绪，无法返回 GameLobby。");
                if (CurrentLobby != null) CurrentLobby.MarkPvpReturnUnavailable();
                yield break;
            }

            DespawnPvpMatchPlayers(networkManager);
            while (!m_IsApplicationQuitting && networkManager.IsServer && networkManager.IsListening)
            {
                SceneEventProgressStatus status = networkManager.SceneManager.LoadScene(
                    GameFlowScenes.MultiplayerLobby, LoadSceneMode.Single);
                if (status == SceneEventProgressStatus.Started)
                {
                    m_DedicatedLobbyReturnLoadPending = true;
                    Debug.Log("[Dedicated PVP] 正在返回 GameLobby，保留房间=" + (CurrentLobby != null));
                    yield break;
                }
                if (status != SceneEventProgressStatus.SceneEventInProgress)
                {
                    Debug.LogError("[Dedicated PVP] 返回 GameLobby 失败：" + status);
                    if (CurrentLobby != null) CurrentLobby.MarkPvpReturnUnavailable();
                    yield break;
                }
                // NGO may still be completing the preceding load event even after its callback runs.
                yield return new WaitForSecondsRealtime(0.2f);
            }
        }
        finally
        {
            m_DedicatedLobbyReturnRoutine = null;
        }
    }

    private static void DespawnPvpMatchPlayers(NetworkManager networkManager)
    {
        if (networkManager.SpawnManager == null)
            return;
        // Snapshot because Despawn mutates SpawnedObjectsList. LobbyState survives the scene change.
        var spawnedObjects = new List<NetworkObject>(networkManager.SpawnManager.SpawnedObjectsList);
        foreach (NetworkObject networkObject in spawnedObjects)
        {
            if (networkObject != null && networkObject.IsSpawned &&
                (networkObject.IsPlayerObject || networkObject.GetComponent<PvpPlayerCombat>() != null))
                networkObject.Despawn(true);
        }
    }

    private void CompleteDedicatedLobbyReturn()
    {
        m_DedicatedLobbyReturnLoadPending = false;
        m_IsPvpMatchLoading = false;
        m_IsPvpMatchReturning = false;
        m_IsDedicatedRoomResetting = false;
        // Keeping IsStarted until loading completes prevents mid-transition team changes or another start request.
        if (CurrentLobby != null && CurrentLobby.IsSpawned)
            CurrentLobby.ResetPvpMatchForLobby();
        if (CurrentLobby != null && CurrentLobby.IsMatchmaking && m_MatchedAdmission != null)
            StartCoroutine(NotifyMatchedServer("completed", m_MatchedAdmission.matchId));
        RefreshDedicatedPvpServerStatus();
        OnLobbyChanged?.Invoke();
        Debug.Log("[Dedicated PVP] GameLobby 已就绪，保留房间=" + (CurrentLobby != null));
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

        if (m_IsDedicatedPvpServer && sceneName == GameFlowScenes.MultiplayerLobby)
        {
            // Disconnect synchronously while the return is still marked pending. The existing
            // disconnect path transfers room ownership or clears an empty room without another scene load.
            DisconnectPvpLoadTimeouts(networkManager, clientsTimedOut);
            CompleteDedicatedLobbyReturn();
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

        if (sceneName != GameFlowScenes.PvpMatch)
            return;
        m_IsPvpMatchLoading = false;
        if (m_IsDedicatedPvpServer)
            DisconnectPvpLoadTimeouts(networkManager, clientsTimedOut);
        if (m_IsDedicatedRoomResetting || CurrentLobby == null)
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

    private static void DisconnectPvpLoadTimeouts(NetworkManager networkManager, List<ulong> clientsTimedOut)
    {
        if (clientsTimedOut == null || clientsTimedOut.Count == 0) return;
        // NGO's disconnect notifications may update its original scene-progress collections.
        foreach (ulong clientId in new List<ulong>(clientsTimedOut))
        {
            if (clientId == NetworkManager.ServerClientId || !networkManager.ConnectedClients.ContainsKey(clientId))
                continue;
            Debug.LogWarning("[Dedicated PVP] 玩家场景加载超时，断开 client=" + clientId);
            networkManager.DisconnectClient(clientId, "场景加载超时，请返回大厅后重新加入房间。");
        }
    }

    private void SpawnPlayerFor(ulong clientId, LobbyGameMode gameMode)
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        if (networkManager == null || !networkManager.IsServer ||
            !networkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient client) ||
            (client.PlayerObject != null && client.PlayerObject.IsSpawned))
            return;
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

        string prefabPath = gameMode == LobbyGameMode.PVP ? "Players/PVP/RifleGirl_PVP_Net" : prefabPaths[prefabIndex];
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
            var characterProfile = instance.GetComponent<PvpCharacterProfile>();
            if (characterProfile != null && m_ClientProfiles.TryGetValue(clientId, out var savedProfile))
                characterProfile.ConfigureBeforeSpawn(savedProfile);
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
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsClient)
        {
            m_IsConnecting = false;
            m_IsInLobby = true;
            ConnectionStatus = "已加入房间。";
        }
        m_ActiveGameMode = lobby.GameMode;
        lobby.OnChanged += HandleCurrentLobbyChanged;
        RefreshDedicatedPvpServerStatus();
        OnLobbyChanged?.Invoke();
    }

    private void HandleCurrentLobbyChanged()
    {
        // Keep the cached mode in sync when the authoritative initial values arrive after OnNetworkSpawn.
        if (CurrentLobby != null)
            m_ActiveGameMode = CurrentLobby.GameMode;
        if (!m_IsDedicatedPvpServer && CurrentLobby != null && CurrentLobby.GameMode == LobbyGameMode.PVP &&
            !CurrentLobby.IsStarted.Value && m_ProfileAccount != null && m_ProfileAccount.IsAuthenticated &&
            string.Equals(m_ProfileAccount.Profile.accountId, m_LocalPvpAccountId, StringComparison.Ordinal))
        {
            NetworkManager networkManager = NetworkManager.Singleton;
            if (networkManager != null)
            {
                foreach (LobbyPlayerData player in CurrentLobby.GetPlayers())
                {
                    if (player.ClientId == networkManager.LocalClientId &&
                        (!player.Appearance.Equals(m_ProfileAccount.Profile.appearance.Sanitized()) ||
                         !player.Loadout.Equals(m_ProfileAccount.Profile.loadout.Sanitized())))
                    {
                        RefreshLocalPvpProfile();
                        break;
                    }
                }
            }
        }
        if (IsHost && discovery != null && CurrentLobby != null)
            discovery.UpdateBroadcastInfo(CurrentLobby.PlayerCount, CurrentLobby.IsStarted.Value);
        RefreshDedicatedPvpServerStatus();
        OnLobbyChanged?.Invoke();
    }

    private void OnLocalSceneLoaded(Scene scene, LoadSceneMode loadSceneMode)
    {
        if (m_IsDedicatedPvpServer)
            return;
        if (scene.name == GameFlowScenes.MultiplayerLobby)
        {
            m_PvpDisconnectedDuringLoad = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            OnLobbyChanged?.Invoke();
            return;
        }
        if (scene.name == GameFlowScenes.PvpMatch && m_PvpDisconnectedDuringLoad)
        {
            m_PvpDisconnectedDuringLoad = false;
            ReturnPvpClientToLobbyIfNeeded("disconnected-during-scene-load");
            return;
        }
        if (scene.name == GameFlowScenes.PvpMatch)
            m_IsClientPvpSceneLoading = false;
        if (scene.name != GameFlowScenes.PvpMatch || CurrentLobby == null || CurrentLobby.GameMode != LobbyGameMode.PVP)
            return;

        PvpGameHudUI.AttachTo(GameObject.Find("GamePanel"));
        PvpScoreboardUI.AttachTo(GameObject.Find("RecordPanel"));
    }

    private void OnClientNetworkSceneEvent(SceneEvent sceneEvent)
    {
        if (sceneEvent.SceneEventType == SceneEventType.Load && sceneEvent.SceneName == GameFlowScenes.PvpMatch)
            m_IsClientPvpSceneLoading = true;
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
        return gameMode == LobbyGameMode.PVP ? GameFlowScenes.PvpMatch : "GamePVE";
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
        bool canCreate = !hasRoom && m_MatchedAdmission == null && !m_IsDedicatedRoomResetting &&
                         m_PendingDedicatedCreatorClientId == LobbyState.NoRoomOwner;
        discovery.UpdateDedicatedPvpStatus(
            hasRoom ? CurrentLobby.GetRoomName() : string.Empty,
            hasRoom ? CurrentLobby.PlayerCount : 0,
            GetMaxPlayers(LobbyGameMode.PVP),
            hasRoom && (CurrentLobby.IsStarted.Value || CurrentLobby.PvpIsPreparingMatch.Value),
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
        string roomName = "",
        string gameTicket = "")
    {
        var payload = new ConnectionPayload
        {
            playerName = PlayerName,
            gameMode = (byte)gameMode,
            operation = (byte)operation,
            roomName = roomName,
            clientVersion = Application.version,
            protocolVersion = LanDiscovery.AccountProtocolVersion,
            gameTicket = gameTicket,
        };
        networkManager.NetworkConfig.ConnectionData = Encoding.UTF8.GetBytes(JsonUtility.ToJson(payload));
    }

    private static void ConfigureNetworkForLobby(NetworkManager networkManager, bool usesAccountApproval = false)
    {
        networkManager.NetworkConfig.ConnectionApproval = true;
        networkManager.NetworkConfig.EnableSceneManagement = true;
        if (usesAccountApproval)
            networkManager.NetworkConfig.ClientConnectionBufferTimeout = Mathf.Max(networkManager.NetworkConfig.ClientConnectionBufferTimeout, 45);
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
        m_ProfileValidationRequests.Clear();
        m_PendingConnections.Clear();
        m_ClientNames.Clear();
        m_JoinOrder.Clear();
        m_ClientProfiles.Clear();
        // A room can reset while HTTPS validation is in flight; finish NGO responses instead of leaving them pending until timeout.
        foreach (var response in m_AuthenticatingClients.Values)
        {
            RejectConnection(response, "房间状态已改变，请重新加入。");
            response.Pending = false;
        }
        m_AuthenticatingClients.Clear();
    }
}
