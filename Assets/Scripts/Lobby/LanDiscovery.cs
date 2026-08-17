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

    // 广播/监听固定使用的标识头，用于过滤无关 UDP 包
    private const string MagicHeader = "FPS-LAN-DISC";

    [Tooltip("房主广播间隔（秒）")]
    public float broadcastInterval = 1f;

    [Tooltip("房间在多少秒内没有收到心跳就视为失效")]
    public float roomTimeout = 4f;

    // 房主侧：本次要广播的房间信息（由 LobbyManager 填充）
    private string m_RoomName = "";
    private int m_CurrentPlayers = 0;
    private int m_MaxPlayers = 3;
    private bool m_IsStarted = false;
    private int m_GamePort = 7777;

    private UdpClient m_BroadcastClient;
    private UdpClient m_ListenClient;
    private bool m_IsBroadcasting;
    private bool m_IsListening;
    private float m_NextBroadcastTime;

    // 客户端侧：收到的房间字典（key = hostEndPoint 字符串，value = 房间信息 + 最后心跳时间）
    private readonly Dictionary<string, RoomEntry> m_Rooms = new Dictionary<string, RoomEntry>();

    /// <summary>客户端发现到的房间列表（每次 Update 后刷新）</summary>
    public IReadOnlyList<RoomEntry> Rooms => m_RoomList;
    private readonly List<RoomEntry> m_RoomList = new List<RoomEntry>();

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
    }

    public class RoomEntry
    {
        public string roomName;
        public int currentPlayers;
        public int maxPlayers;
        public bool isStarted;
        public int gamePort;
        public IPEndPoint hostEndPoint;
        public float lastSeenTime;
    }

    // ---- 房主侧 API ----

    public void StartBroadcast(string roomName, int currentPlayers, int maxPlayers, bool isStarted, int gamePort)
    {
        m_RoomName = roomName;
        m_CurrentPlayers = currentPlayers;
        m_MaxPlayers = maxPlayers;
        m_IsStarted = isStarted;
        m_GamePort = gamePort;

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
            HandleIncoming(data, remote);
        }
    }

    private void HandleIncoming(byte[] data, IPEndPoint remote)
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

        if (!text.StartsWith(MagicHeader + "|"))
            return;

        string json = text.Substring((MagicHeader + "|").Length);
        RoomPayload payload;
        try
        {
            payload = JsonUtility.FromJson<RoomPayload>(json);
        }
        catch
        {
            return;
        }

        string key = remote.Address.ToString() + ":" + remote.Port;
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
            entry.gamePort != payload.gamePort;

        entry.roomName = payload.roomName;
        entry.currentPlayers = payload.currentPlayers;
        entry.maxPlayers = payload.maxPlayers;
        entry.isStarted = payload.isStarted;
        entry.gamePort = payload.gamePort;
        entry.hostEndPoint = remote;
        entry.lastSeenTime = Time.time;

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
            if (Time.time - kv.Value.lastSeenTime > roomTimeout)
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

    private void OnDestroy()
    {
        StopBroadcast();
        StopListening();
    }
}
