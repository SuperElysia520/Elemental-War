using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 联机大厅菜单面板：与 MainMenu / TipMenu / ExitMenu 同级的 UIBase 面板。
/// 视觉布局由使用者自行设计，本脚本只负责逻辑：
/// 创建房间 / 刷新房间列表 / 加入房间 / 显示房内成员 / 房主开始游戏 / 离开房间 / 返回主菜单。
/// 依赖场景中的 LobbyManager 与 LanDiscovery（随 NetworkManager + UnityTransport 一起存在）。
/// </summary>
public class LobbyMenuUI : UIBase<LobbyMenuUI>
{
    [Header("浏览房间")]
    public GameObject panelBrowse;          // 浏览/建房视图（子面板）
    public TMP_InputField inputRoomName;    // 创建房间时输入的房名
    public TMP_InputField inputPlayerName;  // 本地玩家名
    public Button btnCreateRoom;            // 创建房间
    public Button btnRefresh;               // 刷新房间列表
    public Button btnJoin;                  // 加入选中房间
    public Transform roomListContainer;     // 房间条目按钮的父节点（建议挂 VerticalLayoutGroup）
    public GameObject roomEntryPrefab;      // 房间条目预制体（根节点 Button，子节点 Text）
    public Text txtEmpty;                   // 空房间列表提示（可选，无房间时显示）
    public Text txtStatus;              // 状态提示

    [Header("选中高亮（可选）")]
    public Color selectedLabelColor = new Color(1f, 0.85f, 0.2f, 1f); // 选中条目文字色
    public Color normalLabelColor = Color.white;                      // 未选中条目文字色

    [Header("房间内")]
    public GameObject panelInRoom; // 房间内视图（子面板）
    public Text txtTitle; // 房间标题（显示当前房名）
    public Text txtMemberList; // 成员列表
    public Button btnStartGame; // 房主专用：开始游戏
    public Button btnLeaveRoom; // 离开房间

    [Header("返回")]
    public Button btnBack;                  // 返回主菜单

    private LobbyManager Lobby => LobbyManager.Instance;
    private LanDiscovery Discovery => Lobby != null ? Lobby.discovery : null;

    protected override void Awake()
    {
        base.Awake();

        if (btnCreateRoom != null) btnCreateRoom.onClick.AddListener(OnCreateRoomClicked);
        if (btnRefresh != null) btnRefresh.onClick.AddListener(OnRefreshClicked);
        if (btnJoin != null) btnJoin.onClick.AddListener(OnJoinClicked);
        if (btnStartGame != null) btnStartGame.onClick.AddListener(OnStartGameClicked);
        if (btnLeaveRoom != null) btnLeaveRoom.onClick.AddListener(OnLeaveRoomClicked);
        if (btnBack != null) btnBack.onClick.AddListener(OnBackClicked);
    }

    protected override void Start()
    {
        base.Start();
        if (inputPlayerName != null)
            inputPlayerName.text = SystemInfo.deviceName;
    }

    /// <summary>
    /// 进入大厅：显示浏览视图并开始监听局域网房间。
    /// </summary>
    public override void Enter()
    {
        base.Enter();
        ShowBrowseView();
        if (Discovery != null)
            Discovery.StartListening();
        RefreshRoomListView();
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

    // ---- 按钮回调 ----

    private void OnCreateRoomClicked()
    {
        if (Lobby == null) return;

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

    private void OnJoinClicked()
    {
        if (m_SelectedRoom == null)
        {
            SetStatus("请先选择一个房间");
            return;
        }
        if (m_SelectedRoom.isStarted || m_SelectedRoom.currentPlayers >= m_SelectedRoom.maxPlayers)
        {
            SetStatus("该房间已开局或已满，无法加入");
            return;
        }
        JoinRoom(m_SelectedRoom);
    }

    private void SelectRoom(LanDiscovery.RoomEntry room)
    {
        m_SelectedRoom = room;
        UpdateJoinButtonState();
        RefreshSelectionHighlight();
        if (room != null)
            SetStatus($"已选中房间：{room.roomName}");
    }

    /// <summary>加入按钮同时受「全局可交互（动画期间禁用）」与「是否选中可加入房间」两个条件约束。</summary>
    private void UpdateJoinButtonState()
    {
        if (btnJoin == null)
            return;
        bool canJoin = m_SelectedRoom != null
            && !m_SelectedRoom.isStarted
            && m_SelectedRoom.currentPlayers < m_SelectedRoom.maxPlayers;
        btnJoin.interactable = m_ButtonsInteractable && canJoin;
    }

    private void JoinRoom(LanDiscovery.RoomEntry room)
    {
        if (Discovery == null || Lobby == null)
            return;

        ApplyPlayerName();
        Lobby.JoinRoom(room);
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
        if (Lobby != null)
            Lobby.LeaveRoom();
        ShowBrowseView();
        OnRefreshClicked();
    }

    private void OnBackClicked()
    {
        // 返回主菜单前，若仍在房间内则退出，并停止监听广播
        if (Lobby != null)
            Lobby.LeaveRoom();
        if (Discovery != null)
            Discovery.StopListening();

        Exit(() =>
        {
            MainMenuUI.instance.Enter();
            MainMenuUI.instance.StartCoroutine(MainMenuUI.instance.DisplayBtnExit());
            gameObject.SetActive(false);
        });
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
        ClearSelection();
    }

    // ---- 刷新 ----

    private void ApplyPlayerName()
    {
        if (Lobby == null) return;
        if (inputPlayerName != null && !string.IsNullOrEmpty(inputPlayerName.text))
            Lobby.PlayerName = inputPlayerName.text.Trim();
    }

    private class RoomEntryView
    {
        public GameObject go;
        public LanDiscovery.RoomEntry room;
        public Graphic label; // 用于选中高亮着色的文字（TMP_Text 或 Text）
    }

    private readonly List<RoomEntryView> m_EntryViews = new List<RoomEntryView>();
    private LanDiscovery.RoomEntry m_SelectedRoom;
    private bool m_ButtonsInteractable = true;

    private void RefreshRoomListView()
    {
        if (Discovery == null || roomListContainer == null || roomEntryPrefab == null)
            return;

        // 房内视图下不重建列表
        if (panelBrowse != null && !panelBrowse.activeSelf)
            return;

        Discovery.RefreshRoomList();

        // 清空旧条目
        foreach (var v in m_EntryViews)
        {
            if (v.go != null)
                Destroy(v.go);
        }
        m_EntryViews.Clear();

        // 空态提示
        if (txtEmpty != null)
            txtEmpty.gameObject.SetActive(Discovery.Rooms.Count == 0);

        foreach (var room in Discovery.Rooms)
        {
            var go = Instantiate(roomEntryPrefab, roomListContainer);

            string state = room.isStarted ? "对局中" : $"{room.currentPlayers}/{room.maxPlayers}人";
            string entryText = $"{room.roomName}  [{state}]";

            Graphic label = null;
            var tmpLabel = go.GetComponentInChildren<TMP_Text>();
            if (tmpLabel != null)
            {
                tmpLabel.text = entryText;
                label = tmpLabel;
            }
            else
            {
                var text = go.GetComponentInChildren<Text>();
                if (text != null)
                {
                    text.text = entryText;
                    label = text;
                }
            }

            var btn = go.GetComponent<Button>();
            if (btn != null)
            {
                LanDiscovery.RoomEntry captured = room;
                btn.onClick.AddListener(() => SelectRoom(captured));
            }

            m_EntryViews.Add(new RoomEntryView { go = go, room = room, label = label });
        }

        // 刷新后校验选中状态：选中的房间若已消失则清除；若还在则按最新人数/开局状态刷新「加入」按钮
        if (m_SelectedRoom != null)
        {
            bool stillExists = false;
            foreach (var room in Discovery.Rooms)
            {
                if (room == m_SelectedRoom)
                {
                    stillExists = true;
                    break;
                }
            }

            if (!stillExists)
                m_SelectedRoom = null;
        }

        UpdateJoinButtonState();
        RefreshSelectionHighlight();
    }

    private void ClearSelection()
    {
        m_SelectedRoom = null;
        UpdateJoinButtonState();
        RefreshSelectionHighlight();
    }

    private void RefreshSelectionHighlight()
    {
        foreach (var v in m_EntryViews)
        {
            if (v.label != null)
                v.label.color = v.room == m_SelectedRoom ? selectedLabelColor : normalLabelColor;
        }
    }

    private void RefreshLobbyView()
    {
        if (Lobby == null)
            return;

        // 标题：显示当前房名（客机同步完成后 RoomName 由 NetworkVariable 同步过来）
        if (txtTitle != null)
            txtTitle.text = Lobby.GetRoomName();

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

    // ---- UIBase 抽象实现 ----

    protected override void DisableButtons()
    {
        SetButtonsInteractable(false);
    }

    protected override void ResumeButtons()
    {
        SetButtonsInteractable(true);
    }

    private void SetButtonsInteractable(bool interactable)
    {
        m_ButtonsInteractable = interactable;
        if (btnCreateRoom != null) btnCreateRoom.interactable = interactable;
        if (btnRefresh != null) btnRefresh.interactable = interactable;
        if (btnStartGame != null) btnStartGame.interactable = interactable;
        if (btnLeaveRoom != null) btnLeaveRoom.interactable = interactable;
        if (btnBack != null) btnBack.interactable = interactable;
        UpdateJoinButtonState();
    }
}
