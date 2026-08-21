using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

/// <summary>
/// 局域网房间发现：房主周期广播房间信息，客户端监听并组装房间列表。
/// 纯 UDP 广播，不依赖网络会话（未连接状态下也能发现房间）。
/// </summary>
public class LanDiscovery : MonoBehaviour
{
    public const int DiscoveryPort = 47777;
    public const int DedicatedDiscoveryPortOffset = 41000;

    // 广播/监听固定使用的标识头，用于过滤无关 UDP 包
    private const string MagicHeader = "FPS-LAN-DISC";
    private const string DedicatedQueryHeader = "FPS-PVP-DISC-QUERY";
    private const string DedicatedResponseHeader = "FPS-PVP-DISC-RESPONSE";

    [Tooltip("房主广播间隔（秒）")]
    public float broadcastInterval = 1f;

    [Tooltip("房间在多少秒内没有收到心跳就视为失效")]
    public float roomTimeout = 4f;

    [Tooltip("公网 PVP 服务器状态查询间隔（秒）")]
    public float dedicatedQueryInterval = 1f;

    // 房主侧：本次要广播的房间信息（由 LobbyManager 填充）
    private string m_RoomName = "";
    private int m_CurrentPlayers = 0;
    private int m_MaxPlayers = 3;
    private bool m_IsStarted = false;
    private int m_GamePort = 7777;
    private LobbyGameMode m_GameMode = LobbyGameMode.PVE;

    private UdpClient m_BroadcastClient;
    private UdpClient m_ListenClient;
    private UdpClient m_DedicatedResponderClient;
    private UdpClient m_DedicatedQueryClient;
    private bool m_IsBroadcasting;
    private bool m_IsListening;
    private bool m_IsDedicatedResponderRunning;
    private bool m_IsDedicatedQuerying;
    private float m_NextBroadcastTime;
    private float m_NextDedicatedQueryTime;

    private IPAddress m_DedicatedServerAddress;
    private int m_DedicatedServerStartPort;
    private int m_DedicatedServerPortCount;

    private string m_DedicatedRoomName = "";
    private int m_DedicatedCurrentPlayers;
    private int m_DedicatedMaxPlayers = 10;
    private bool m_DedicatedIsStarted;
    private bool m_DedicatedHasRoom;
    private bool m_DedicatedCanCreate;
    private int m_DedicatedGamePort;

    // 客户端侧：收到的房间字典（key = hostEndPoint 字符串，value = 房间信息 + 最后心跳时间）
    private readonly Dictionary<string, RoomEntry> m_Rooms = new Dictionary<string, RoomEntry>();

    /// <summary>客户端发现到的房间列表（每次 Update 后刷新）</summary>
    public IReadOnlyList<RoomEntry> Rooms => m_RoomList;
    private readonly List<RoomEntry> m_RoomList = new List<RoomEntry>();

    public int DedicatedPvpServerCount => CountDedicatedServers(false);
    public int DedicatedPvpIdleServerCount => CountDedicatedServers(true);

    /// <summary>房间列表发生变化时回调</summary>
    public event Action OnRoomsChanged;

    [Serializable]
    public struct RoomPayload
    {
        public string roomName;
        public int currentPlayers;
        public int maxPlayers;
        public bool isStarted;
        public int gamePort;   // 游戏连接端口（UnityTransport 监听端口）
        public byte gameMode;  // LobbyGameMode；旧广播缺省为 0（PVE）
        public bool hasRoom;   // Dedicated PVP：该服务器槽位是否已经创建房间
        public bool canCreate; // Dedicated PVP：该服务器槽位当前是否接受建房
        public string serverVersion; // Dedicated PVP：用于在连接前拦截不兼容客户端
    }

    public class RoomEntry
    {
        public string roomName;
        public int currentPlayers;
        public int maxPlayers;
        public bool isStarted;
        public int gamePort;
        public LobbyGameMode gameMode;
        public IPEndPoint hostEndPoint;
        public float lastSeenTime;
        public bool isDedicatedPvp;
        public bool hasRoom;
        public bool canCreate;
        public string serverVersion;
    }

    // ---- 房主侧 API ----

    public void StartBroadcast(
        string roomName,
        int currentPlayers,
        int maxPlayers,
        bool isStarted,
        int gamePort,
        LobbyGameMode gameMode)
    {
        m_RoomName = roomName;
        m_CurrentPlayers = currentPlayers;
        m_MaxPlayers = maxPlayers;
        m_IsStarted = isStarted;
        m_GamePort = gamePort;
        m_GameMode = gameMode;

        if (m_IsBroadcasting)
            return;

        m_BroadcastClient = new UdpClient();
        m_BroadcastClient.EnableBroadcast = true;
        m_IsBroadcasting = true;
        m_NextBroadcastTime = 0f;
    }

    public void UpdateBroadcastInfo(int currentPlayers, bool isStarted)
    {
        m_CurrentPlayers = currentPlayers;
        m_IsStarted = isStarted;
    }

    public void StopBroadcast()
    {
        m_IsBroadcasting = false;
        if (m_BroadcastClient != null)
        {
            m_BroadcastClient.Close();
            m_BroadcastClient = null;
        }
    }

    // ---- Dedicated PVP 服务器状态响应 API ----

    public bool StartDedicatedPvpResponder(int gamePort, int maxPlayers)
    {
        StopDedicatedPvpResponder();

        int queryPort = gamePort + DedicatedDiscoveryPortOffset;
        if (gamePort <= 0 || gamePort > ushort.MaxValue || queryPort > ushort.MaxValue)
        {
            Debug.LogError("[PVP Discovery] 无效的游戏端口：" + gamePort);
            return false;
        }

        try
        {
            m_DedicatedResponderClient = new UdpClient(new IPEndPoint(IPAddress.Any, queryPort));
            m_DedicatedGamePort = gamePort;
            m_DedicatedMaxPlayers = Mathf.Max(1, maxPlayers);
            m_DedicatedRoomName = string.Empty;
            m_DedicatedCurrentPlayers = 0;
            m_DedicatedIsStarted = false;
            m_DedicatedHasRoom = false;
            m_DedicatedCanCreate = true;
            m_IsDedicatedResponderRunning = true;
            Debug.Log("[PVP Discovery] 状态查询监听 0.0.0.0:" + queryPort +
                      "，对应游戏端口 " + gamePort);
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError("[PVP Discovery] 无法监听状态查询端口 " + queryPort + "：" + e.Message);
            StopDedicatedPvpResponder();
            return false;
        }
    }

    public void UpdateDedicatedPvpStatus(
        string roomName,
        int currentPlayers,
        int maxPlayers,
        bool isStarted,
        bool hasRoom,
        bool canCreate)
    {
        m_DedicatedRoomName = roomName ?? string.Empty;
        m_DedicatedCurrentPlayers = Mathf.Max(0, currentPlayers);
        m_DedicatedMaxPlayers = Mathf.Max(1, maxPlayers);
        m_DedicatedIsStarted = isStarted;
        m_DedicatedHasRoom = hasRoom;
        m_DedicatedCanCreate = canCreate;
    }

    public void StopDedicatedPvpResponder()
    {
        m_IsDedicatedResponderRunning = false;
        if (m_DedicatedResponderClient != null)
        {
            m_DedicatedResponderClient.Close();
            m_DedicatedResponderClient = null;
        }
    }

    // ---- 客户端侧 API ----

    public void StartListening()
    {
        if (m_IsListening)
            return;

        try
        {
            m_ListenClient = new UdpClient();
            m_ListenClient.EnableBroadcast = true;
            // 允许多个实例（本机双开）同时绑定同一端口
            m_ListenClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            m_ListenClient.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            m_IsListening = true;
        }
        catch (Exception e)
        {
            Debug.LogError("[LanDiscovery] 监听失败: " + e.Message);
        }
    }

    public void StopListening()
    {
        m_IsListening = false;
        if (m_ListenClient != null)
        {
            m_ListenClient.Close();
            m_ListenClient = null;
        }
        m_Rooms.Clear();
        m_RoomList.Clear();
    }

    // ---- Dedicated PVP 客户端定向查询 API ----

    public bool StartDedicatedPvpDiscovery(string serverAddress, int startPort, int portCount)
    {
        StopDedicatedPvpDiscovery();

        if (!TryResolveIpv4(serverAddress, out IPAddress resolvedAddress))
        {
            Debug.LogError("[PVP Discovery] 无法解析服务器地址：" + serverAddress);
            return false;
        }
        if (startPort <= 0 || startPort > ushort.MaxValue || portCount <= 0 ||
            startPort + portCount - 1 + DedicatedDiscoveryPortOffset > ushort.MaxValue)
        {
            Debug.LogError("[PVP Discovery] 无效的服务器端口范围：" + startPort + "，数量=" + portCount);
            return false;
        }

        try
        {
            m_DedicatedQueryClient = new UdpClient(AddressFamily.InterNetwork);
            m_DedicatedQueryClient.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            m_DedicatedServerAddress = resolvedAddress;
            m_DedicatedServerStartPort = startPort;
            m_DedicatedServerPortCount = portCount;
            m_IsDedicatedQuerying = true;
            m_NextDedicatedQueryTime = 0f;
            RemoveDedicatedPvpRooms();
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError("[PVP Discovery] 启动服务器查询失败：" + e.Message);
            StopDedicatedPvpDiscovery();
            return false;
        }
    }

    public void RefreshDedicatedPvpDiscovery()
    {
        if (m_IsDedicatedQuerying)
            m_NextDedicatedQueryTime = 0f;
    }

    public void StopDedicatedPvpDiscovery()
    {
        m_IsDedicatedQuerying = false;
        if (m_DedicatedQueryClient != null)
        {
            m_DedicatedQueryClient.Close();
            m_DedicatedQueryClient = null;
        }
        m_DedicatedServerAddress = null;
        m_DedicatedServerPortCount = 0;
        RemoveDedicatedPvpRooms();
    }

    public bool TryGetIdleDedicatedPvpServer(out RoomEntry server)
    {
        server = null;
        foreach (RoomEntry entry in m_Rooms.Values)
        {
            if (!entry.isDedicatedPvp || !entry.canCreate)
                continue;
            if (server == null || entry.gamePort < server.gamePort)
                server = entry;
        }
        return server != null;
    }

    public void ClearRooms()
    {
        m_Rooms.Clear();
        m_RoomList.Clear();
        OnRoomsChanged?.Invoke();
    }

    private void Update()
    {
        if (m_IsBroadcasting && Time.time >= m_NextBroadcastTime)
        {
            m_NextBroadcastTime = Time.time + broadcastInterval;
            BroadcastOnce();
        }

        if (m_IsListening)
        {
            ReceivePending();
            PruneStaleRooms();
        }

        if (m_IsDedicatedResponderRunning)
            ReceiveDedicatedPvpQueries();

        if (m_IsDedicatedQuerying)
        {
            if (Time.unscaledTime >= m_NextDedicatedQueryTime)
            {
                m_NextDedicatedQueryTime = Time.unscaledTime + Mathf.Max(0.25f, dedicatedQueryInterval);
                SendDedicatedPvpQueries();
            }
            ReceiveDedicatedPvpResponses();
            PruneStaleRooms();
        }
    }

    private void BroadcastOnce()
    {
        try
        {
            var payload = new RoomPayload
            {
                roomName = m_RoomName,
                currentPlayers = m_CurrentPlayers,
                maxPlayers = m_MaxPlayers,
                isStarted = m_IsStarted,
                gamePort = m_GamePort,
                gameMode = (byte)m_GameMode,
                hasRoom = true,
                canCreate = false,
            };
            string json = JsonUtility.ToJson(payload);
            byte[] data = Encoding.UTF8.GetBytes(MagicHeader + "|" + json);

            m_BroadcastClient.Send(data, data.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
        }
        catch (Exception e)
        {
            Debug.LogWarning("[LanDiscovery] 广播失败: " + e.Message);
        }
    }

    private void ReceivePending()
    {
        if (m_ListenClient == null)
            return;

        while (m_ListenClient.Available > 0)
        {
            IPEndPoint remote = null;
            byte[] data = m_ListenClient.Receive(ref remote);
            HandleIncoming(data, remote, MagicHeader, false);
        }
    }

    private void ReceiveDedicatedPvpQueries()
    {
        if (m_DedicatedResponderClient == null)
            return;

        while (m_DedicatedResponderClient.Available > 0)
        {
            IPEndPoint remote = null;
            byte[] data = m_DedicatedResponderClient.Receive(ref remote);
            string text;
            try
            {
                text = Encoding.UTF8.GetString(data);
            }
            catch
            {
                continue;
            }

            if (!string.Equals(text, DedicatedQueryHeader, StringComparison.Ordinal))
                continue;

            var payload = new RoomPayload
            {
                roomName = m_DedicatedRoomName,
                currentPlayers = m_DedicatedCurrentPlayers,
                maxPlayers = m_DedicatedMaxPlayers,
                isStarted = m_DedicatedIsStarted,
                gamePort = m_DedicatedGamePort,
                gameMode = (byte)LobbyGameMode.PVP,
                hasRoom = m_DedicatedHasRoom,
                canCreate = m_DedicatedCanCreate,
                serverVersion = Application.version,
            };
            string json = JsonUtility.ToJson(payload);
            byte[] response = Encoding.UTF8.GetBytes(DedicatedResponseHeader + "|" + json);
            try
            {
                m_DedicatedResponderClient.Send(response, response.Length, remote);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PVP Discovery] 返回服务器状态失败：" + e.Message);
            }
        }
    }

    private void SendDedicatedPvpQueries()
    {
        if (m_DedicatedQueryClient == null || m_DedicatedServerAddress == null)
            return;

        byte[] query = Encoding.UTF8.GetBytes(DedicatedQueryHeader);
        for (int i = 0; i < m_DedicatedServerPortCount; i++)
        {
            int gamePort = m_DedicatedServerStartPort + i;
            int queryPort = gamePort + DedicatedDiscoveryPortOffset;
            try
            {
                m_DedicatedQueryClient.Send(
                    query,
                    query.Length,
                    new IPEndPoint(m_DedicatedServerAddress, queryPort));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PVP Discovery] 查询端口 " + queryPort + " 失败：" + e.Message);
            }
        }
    }

    private void ReceiveDedicatedPvpResponses()
    {
        if (m_DedicatedQueryClient == null)
            return;

        while (m_DedicatedQueryClient.Available > 0)
        {
            IPEndPoint remote = null;
            byte[] data = m_DedicatedQueryClient.Receive(ref remote);
            HandleIncoming(data, remote, DedicatedResponseHeader, true);
        }
    }

    private void HandleIncoming(byte[] data, IPEndPoint remote, string expectedHeader, bool isDedicatedPvp)
    {
        string text;
        try
        {
            text = Encoding.UTF8.GetString(data);
        }
        catch
        {
            return;
        }

        if (!text.StartsWith(expectedHeader + "|", StringComparison.Ordinal))
            return;

        string json = text.Substring((expectedHeader + "|").Length);
        RoomPayload payload;
        try
        {
            payload = JsonUtility.FromJson<RoomPayload>(json);
        }
        catch
        {
            return;
        }

        string key = isDedicatedPvp
            ? "PVP|" + remote.Address + ":" + payload.gamePort
            : "LAN|" + remote.Address + ":" + remote.Port;
        if (!m_Rooms.TryGetValue(key, out var entry))
        {
            entry = new RoomEntry();
            m_Rooms[key] = entry;
        }

        bool changed =
            entry.roomName != payload.roomName ||
            entry.currentPlayers != payload.currentPlayers ||
            entry.maxPlayers != payload.maxPlayers ||
            entry.isStarted != payload.isStarted ||
            entry.gamePort != payload.gamePort ||
            entry.gameMode != (LobbyGameMode)payload.gameMode ||
            entry.isDedicatedPvp != isDedicatedPvp ||
            entry.hasRoom != (isDedicatedPvp ? payload.hasRoom : true) ||
            entry.canCreate != (isDedicatedPvp && payload.canCreate) ||
            entry.serverVersion != (isDedicatedPvp ? payload.serverVersion : string.Empty);

        entry.roomName = payload.roomName;
        entry.currentPlayers = payload.currentPlayers;
        entry.maxPlayers = payload.maxPlayers;
        entry.isStarted = payload.isStarted;
        entry.gamePort = payload.gamePort;
        entry.gameMode = (LobbyGameMode)payload.gameMode;
        entry.hostEndPoint = isDedicatedPvp
            ? new IPEndPoint(remote.Address, payload.gamePort)
            : remote;
        entry.lastSeenTime = Time.unscaledTime;
        entry.isDedicatedPvp = isDedicatedPvp;
        entry.hasRoom = isDedicatedPvp ? payload.hasRoom : true;
        entry.canCreate = isDedicatedPvp && payload.canCreate;
        entry.serverVersion = isDedicatedPvp ? payload.serverVersion : string.Empty;

        if (changed)
        {
            OnRoomsChanged?.Invoke();
        }
    }

    private void PruneStaleRooms()
    {
        bool removed = false;
        var staleKeys = new List<string>();
        foreach (var kv in m_Rooms)
        {
            if (Time.unscaledTime - kv.Value.lastSeenTime > roomTimeout)
                staleKeys.Add(kv.Key);
        }
        foreach (var key in staleKeys)
        {
            m_Rooms.Remove(key);
            removed = true;
        }

        if (removed)
            OnRoomsChanged?.Invoke();
    }

    /// <summary>刷新 Rooms 视图列表（供 UI 读取）</summary>
    public void RefreshRoomList()
    {
        m_RoomList.Clear();
        foreach (var kv in m_Rooms)
        {
            m_RoomList.Add(kv.Value);
        }
        m_RoomList.Sort((a, b) => string.Compare(a.roomName, b.roomName, StringComparison.Ordinal));
    }

    private int CountDedicatedServers(bool idleOnly)
    {
        int count = 0;
        foreach (RoomEntry entry in m_Rooms.Values)
        {
            if (!entry.isDedicatedPvp)
                continue;
            if (idleOnly && !entry.canCreate)
                continue;
            count++;
        }
        return count;
    }

    private void RemoveDedicatedPvpRooms()
    {
        var keys = new List<string>();
        foreach (KeyValuePair<string, RoomEntry> pair in m_Rooms)
        {
            if (pair.Value.isDedicatedPvp)
                keys.Add(pair.Key);
        }

        if (keys.Count == 0)
            return;

        foreach (string key in keys)
            m_Rooms.Remove(key);
        RefreshRoomList();
        OnRoomsChanged?.Invoke();
    }

    private static bool TryResolveIpv4(string address, out IPAddress resolvedAddress)
    {
        resolvedAddress = null;
        if (string.IsNullOrWhiteSpace(address))
            return false;

        string normalizedAddress = address.Trim();
        if (IPAddress.TryParse(normalizedAddress, out IPAddress parsedAddress))
        {
            if (parsedAddress.AddressFamily != AddressFamily.InterNetwork)
                return false;
            resolvedAddress = parsedAddress;
            return true;
        }

        try
        {
            foreach (IPAddress candidate in Dns.GetHostAddresses(normalizedAddress))
            {
                if (candidate.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                resolvedAddress = candidate;
                return true;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[PVP Discovery] DNS 解析失败：" + e.Message);
        }
        return false;
    }

    private void OnDestroy()
    {
        StopBroadcast();
        StopListening();
        StopDedicatedPvpResponder();
        StopDedicatedPvpDiscovery();
    }
}
