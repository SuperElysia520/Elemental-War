using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 联机大厅 UI：创建房间 / 浏览并加入房间 / 显示房内成员 / 房主开始游戏。
/// 普通 MonoBehaviour，用 SetActive 管理视图切换（不依赖 Animator，便于场景自动生成）。
/// </summary>
public class LobbyUI : MonoBehaviour
{
    public static LobbyUI Instance { get; private set; }

    [Header("浏览房间视图")]
    public GameObject panelBrowse;
    public TMP_InputField inputRoomName;   // 创建房间时输入的房名
    public TMP_InputField inputPlayerName; // 本地玩家名
    public Button btnCreateRoom;
    public Button btnRefresh;
    public Button btnJoinRoom;             // 加入第一个可加入的房间
    public TMP_Text txtRoomList;           // 房间列表（多行文本）
    public TMP_Text txtStatus;             // 状态提示

    [Header("房间内视图")]
    public GameObject panelInRoom;
    public TMP_Text txtMemberList;         // 成员列表
    public Button btnStartGame;            // 房主专用：开始游戏
    public Button btnLeaveRoom;            // 离开房间

    private LobbyManager Lobby => LobbyManager.Instance;
    private LanDiscovery Discovery => Lobby != null ? Lobby.discovery : null;

    void Awake()
    {
        Instance = this;
    }

    void Start()
    {
        btnCreateRoom.onClick.AddListener(OnCreateRoomClicked);
        btnRefresh.onClick.AddListener(OnRefreshClicked);
        btnJoinRoom.onClick.AddListener(OnJoinRoomClicked);
        btnStartGame.onClick.AddListener(OnStartGameClicked);
        btnLeaveRoom.onClick.AddListener(OnLeaveRoomClicked);

        if (inputPlayerName != null)
            inputPlayerName.text = SystemInfo.deviceName;

        ShowBrowseView();
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    private void OnEnable()
    {
        if (Lobby != null) Lobby.OnLobbyChanged += RefreshLobbyView;
        if (Discovery != null) Discovery.OnRoomsChanged += RefreshRoomListView;
    }

    private void OnDisable()
    {
        if (Lobby != null) Lobby.OnLobbyChanged -= RefreshLobbyView;
        if (Discovery != null) Discovery.OnRoomsChanged -= RefreshRoomListView;
    }

    void Update()
    {
        // 浏览视图下每帧刷新（处理心跳超时剔除）
        if (Discovery != null && panelBrowse != null && panelBrowse.activeSelf)
        {
            RefreshRoomListView();
        }
    }

    // ---- 按钮回调 ----

    private void OnCreateRoomClicked()
    {
        string roomName = inputRoomName != null && !string.IsNullOrEmpty(inputRoomName.text)
            ? inputRoomName.text.Trim()
            : "我的房间";

        ApplyPlayerName();
        Lobby.CreateRoom(roomName);
        ShowInRoomView();
        SetStatus("已创建房间，等待玩家加入...");
    }

    private void OnRefreshClicked()
    {
        if (Discovery != null)
        {
            Discovery.ClearRooms();
            Discovery.StartListening();
        }
        RefreshRoomListView();
        SetStatus("正在刷新...");
    }

    private void OnJoinRoomClicked()
    {
        if (Discovery == null)
            return;

        Discovery.RefreshRoomList();
        // 加入第一个「未满且未开局」的房间（验证场景简化，正式大厅浏览器再做点选）
        LanDiscovery.RoomEntry target = null;
        foreach (var room in Discovery.Rooms)
        {
            if (!room.isStarted && room.currentPlayers < room.maxPlayers)
            {
                target = room;
                break;
            }
        }

        if (target == null)
        {
            SetStatus("没有可加入的房间");
            return;
        }

        ApplyPlayerName();
        Lobby.JoinRoom(target);
        ShowInRoomView();
        SetStatus("正在连接...");
    }

    private void OnStartGameClicked()
    {
        if (Lobby != null)
            Lobby.StartGame();
    }

    private void OnLeaveRoomClicked()
    {
        Lobby.LeaveRoom();
        ShowBrowseView();
        OnRefreshClicked();
    }

    // ---- 视图切换 ----

    private void ShowInRoomView()
    {
        if (panelBrowse != null) panelBrowse.SetActive(false);
        if (panelInRoom != null) panelInRoom.SetActive(true);
        bool isHost = Lobby != null && Lobby.IsHost;
        if (btnStartGame != null) btnStartGame.gameObject.SetActive(isHost);
        RefreshLobbyView();
    }

    private void ShowBrowseView()
    {
        if (panelBrowse != null) panelBrowse.SetActive(true);
        if (panelInRoom != null) panelInRoom.SetActive(false);
        if (btnStartGame != null) btnStartGame.gameObject.SetActive(false);
    }

    // ---- 刷新 ----

    private void ApplyPlayerName()
    {
        if (Lobby == null) return;
        if (inputPlayerName != null && !string.IsNullOrEmpty(inputPlayerName.text))
        {
            Lobby.PlayerName = inputPlayerName.text.Trim();
        }
    }

    private void RefreshRoomListView()
    {
        if (Discovery == null || txtRoomList == null)
            return;

        Discovery.RefreshRoomList();
        if (Discovery.Rooms.Count == 0)
        {
            txtRoomList.text = "（未发现房间）";
            return;
        }

        var sb = new StringBuilder();
        foreach (var r in Discovery.Rooms)
        {
            string state = r.isStarted ? "对局中" : $"{r.currentPlayers}/{r.maxPlayers}人";
            sb.AppendLine($"{r.roomName}  [{state}]  {r.hostEndPoint.Address}:{r.gamePort}");
        }
        txtRoomList.text = sb.ToString();
    }

    private void RefreshLobbyView()
    {
        if (Lobby == null)
            return;

        if (panelInRoom != null && panelInRoom.activeSelf && txtMemberList != null)
        {
            var names = Lobby.GetPlayerNames();
            var sb = new StringBuilder();
            for (int i = 0; i < names.Count; i++)
            {
                string role = i == 0 ? "（房主）" : "";
                sb.AppendLine($"{i + 1}. {names[i]} {role}");
            }
            txtMemberList.text = sb.ToString();
        }
    }

    private void SetStatus(string msg)
    {
        if (txtStatus != null)
            txtStatus.text = msg;
    }
}
