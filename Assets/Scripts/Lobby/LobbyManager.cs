using System;
using System.Collections.Generic;
using System.Text;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

/// <summary>
/// 大厅管理器（普通 MonoBehaviour，非 NetworkBehaviour）：
/// 负责建房（StartHost）/ 加入（StartClient）/ 连接审批 / 广播房间信息 / 开始游戏。
/// 房间内玩家列表与开局状态由 LobbyState（NetworkBehaviour）承担并同步。
/// </summary>
public class LobbyManager : MonoBehaviour
{
    public static LobbyManager Instance { get; private set; }

    [Header("网络")]
    [Tooltip("UnityTransport 组件引用（挂在 NetworkManager 同一物体上）")]
    public UnityTransport transport;

    [Tooltip("大厅状态预制体（含 LobbyState + NetworkObject），建房后 Spawn")]
    public GameObject lobbyStatePrefab;

    [Tooltip("最大玩家数（含房主）")]
    public int maxPlayers = 3;

    [Header("广播")]
    public LanDiscovery discovery;

    [Header("玩家")]
    [Tooltip("本地玩家名（默认用机器名，UI 可覆盖）")]
    public string playerName = "";

    /// <summary>本地玩家名（供 UI 读取，建房/加入前由 UI 写入）</summary>
    public string PlayerName
    {
        get
        {
            if (string.IsNullOrEmpty(playerName))
                playerName = SystemInfo.deviceName;
            return playerName;
        }
        set => playerName = value;
    }

    /// <summary>当前 LobbyState（客机在同步后由 RegisterLobby 填充）</summary>
    public LobbyState CurrentLobby { get; private set; }

    /// <summary>是否房主</summary>
    public bool IsHost => NetworkManager.Singleton != null && NetworkManager.Singleton.IsHost;

    /// <summary>房间内玩家列表变化回调（三端均触发）</summary>
    public event Action OnLobbyChanged;

    private bool m_IsInLobby;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    void Start()
    {
        if (transport == null)
            transport = FindObjectOfType<UnityTransport>();
        if (discovery == null)
            discovery = GetComponent<LanDiscovery>();

        NetworkManager.Singleton.ConnectionApprovalCallback = ApprovalCheck;
        NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
        NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
    }

    void OnDestroy()
    {
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.ConnectionApprovalCallback = null;
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
        }
        if (Instance == this)
            Instance = null;
    }

    // ---- 建房 / 加入 ----

    public void CreateRoom(string roomName)
    {
        if (NetworkManager.Singleton.IsListening)
            return;

        // Host 显式监听所有接口（0.0.0.0），否则默认只监听回环地址，局域网其他机器连不进来。
        // 需在 StartHost 之前设置，StartHost 会初始化 transport 并 bind。
        int port = GetGamePort();
        if (transport != null)
            transport.SetConnectionData("0.0.0.0", (ushort)port, "0.0.0.0");

        NetworkManager.Singleton.NetworkConfig.ConnectionApproval = true;
        m_PendingRoomName = roomName;

        // 先注册再 StartHost：StartHost 同步触发 OnServerStarted，顺序反了会漏掉
        NetworkManager.Singleton.OnServerStarted += OnHostServerStarted;
        NetworkManager.Singleton.StartHost();
        m_IsInLobby = true;
    }

    private string m_PendingRoomName;

    private void OnHostServerStarted()
    {
        NetworkManager.Singleton.OnServerStarted -= OnHostServerStarted;

        // 生成 LobbyState，把房主自己加进去
        if (lobbyStatePrefab != null)
        {
            var go = Instantiate(lobbyStatePrefab);
            var lobby = go.GetComponent<LobbyState>();
            go.GetComponent<NetworkObject>().Spawn();
            lobby.SetRoomName(m_PendingRoomName);
            lobby.AddPlayer(PlayerName);
            // RegisterLobby 由 OnNetworkSpawn 触发（Host 同步），这里不重复赋值
        }

        // 开始广播房间信息
        if (discovery != null)
        {
            discovery.StartBroadcast(m_PendingRoomName, 1, maxPlayers, false, GetGamePort());
        }

        OnLobbyChanged?.Invoke();
    }

    public void JoinRoom(LanDiscovery.RoomEntry room)
    {
        if (NetworkManager.Singleton.IsListening)
            return;

        // 连接目标 = 广播来源 IP + 广播里携带的游戏端口
        transport.SetConnectionData(room.hostEndPoint.Address.ToString(), (ushort)room.gamePort);

        // 把玩家名放进 ConnectionData，供服务器连接审批时读取
        NetworkManager.Singleton.NetworkConfig.ConnectionData = Encoding.UTF8.GetBytes(PlayerName);
        NetworkManager.Singleton.NetworkConfig.ConnectionApproval = true;

        NetworkManager.Singleton.StartClient();
    }

    public void LeaveRoom()
    {
        if (!NetworkManager.Singleton.IsListening)
            return;

        if (discovery != null)
            discovery.StopBroadcast();

        NetworkManager.Singleton.Shutdown();
        m_IsInLobby = false;
        CurrentLobby = null;
        m_PendingJoinNames.Clear();
        m_ClientNames.Clear();
        OnLobbyChanged?.Invoke();
    }

    // ---- 开始游戏（阶段 1 先做占位，阶段 2 接入场景切换）----

    public void StartGame()
    {
        if (!IsHost || CurrentLobby == null)
            return;
        CurrentLobby.SetStarted(true);
        // 阶段 2 在这里接入 NetworkSceneManager 切换到 Game 场景
        Debug.Log("[Lobby] 房主开始游戏（阶段2接入场景切换）");
    }

    // ---- 连接审批 ----

    private void ApprovalCheck(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
    {
        // 房主自己的本地客户端（ServerClientId = 0）在 StartHost 时也会走审批，
        // 此时不应记录进待加入名单（房主真名已在 OnHostServerStarted 里加过），否则会重复。
        if (request.ClientNetworkId == NetworkManager.ServerClientId)
        {
            response.Approved = true;
            response.CreatePlayerObject = false;
            return;
        }

        int currentPlayers = NetworkManager.Singleton.ConnectedClientsIds.Count;
        bool roomFull = currentPlayers >= maxPlayers;
        bool alreadyStarted = CurrentLobby != null && CurrentLobby.IsStarted.Value;

        if (roomFull)
        {
            response.Approved = false;
            response.Reason = "房间已满";
            return;
        }

        if (alreadyStarted)
        {
            response.Approved = false;
            response.Reason = "对局进行中";
            return;
        }

        response.Approved = true;
        response.CreatePlayerObject = false; // 大厅阶段不生成玩家对象

        // 记录申请者名字（按 ClientId），连接建立后再加入列表
        string name = DecodeName(request.Payload);
        m_PendingJoinNames[request.ClientNetworkId] = name;
    }

    private readonly Dictionary<ulong, string> m_PendingJoinNames = new Dictionary<ulong, string>();

    /// <summary>已连接客机的 ClientId -> 名字 映射，断开时据此移除成员。</summary>
    private readonly Dictionary<ulong, string> m_ClientNames = new Dictionary<ulong, string>();

    private void OnClientConnected(ulong clientId)
    {
        // 客机连接成功，服务器端把新玩家加进列表
        if (IsHost && CurrentLobby != null && m_PendingJoinNames.TryGetValue(clientId, out var name))
        {
            m_PendingJoinNames.Remove(clientId);
            m_ClientNames[clientId] = name;
            CurrentLobby.AddPlayer(name);

            // 更新广播里的人数
            if (discovery != null)
                discovery.UpdateBroadcastInfo(CurrentLobby.PlayerCount, CurrentLobby.IsStarted.Value);
        }

        OnLobbyChanged?.Invoke();
    }

    private void OnClientDisconnected(ulong clientId)
    {
        // 服务器端：房主收到客机断开（无主机迁移，房主断开则全散）
        if (IsHost && CurrentLobby != null)
        {
            // 从列表中移除断开的客机
            if (m_ClientNames.TryGetValue(clientId, out var name))
            {
                m_ClientNames.Remove(clientId);
                CurrentLobby.RemovePlayer(name);
            }

            if (discovery != null)
                discovery.UpdateBroadcastInfo(CurrentLobby.PlayerCount, CurrentLobby.IsStarted.Value);
        }

        OnLobbyChanged?.Invoke();
    }

    private string DecodeName(byte[] payload)
    {
        if (payload == null || payload.Length == 0)
            return "玩家";
        try
        {
            return Encoding.UTF8.GetString(payload);
        }
        catch
        {
            return "玩家";
        }
    }

    private int GetGamePort()
    {
        if (transport != null)
            return transport.ConnectionData.Port;
        return 7777;
    }

    /// <summary>客机：LobbyState 同步完成后由 LobbyState 调用，登记到本地</summary>
    public void RegisterLobby(LobbyState lobby)
    {
        if (CurrentLobby == lobby)
            return;
        CurrentLobby = lobby;
        lobby.OnChanged += () => OnLobbyChanged?.Invoke();
        OnLobbyChanged?.Invoke();
    }

    /// <summary>获取房间玩家名列表（无 Lobby 时返回空）</summary>
    public List<string> GetPlayerNames()
    {
        return CurrentLobby != null ? CurrentLobby.GetPlayerNames() : new List<string>();
    }

    /// <summary>获取当前房间名（无 Lobby 时返回空字符串）</summary>
    public string GetRoomName()
    {
        return CurrentLobby != null ? CurrentLobby.GetRoomName() : "";
    }
}
