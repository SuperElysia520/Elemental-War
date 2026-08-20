using System.Collections.Generic;
using System.Text;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// GameStart 内共用的联机大厅面板。PVE 显示普通成员列表；PVP 显示红蓝各五个可点击队伍位置。
/// </summary>
public class LobbyMenuUI : UIBase<LobbyMenuUI>
{
    [Header("浏览房间")]
    public GameObject panelBrowse;
    public TMP_InputField inputRoomName;
    public TMP_InputField inputPlayerName;
    public Button btnCreateRoom;
    public Button btnRefresh;
    public Button btnJoin;
    public Transform roomListContainer;
    public GameObject roomEntryPrefab;
    public Text txtEmpty;
    public Text txtStatus;

    [Header("选中高亮（可选）")]
    public Color selectedLabelColor = new Color(1f, 0.85f, 0.2f, 1f);
    public Color normalLabelColor = Color.white;

    [Header("房间内")]
    public GameObject panelInRoom;
    public Text txtTitle;
    public Text txtMemberList;
    public Button btnStartGame;
    public Button btnLeaveRoom;

    [Header("返回")]
    public Button btnBack;

    private LobbyManager Lobby => LobbyManager.Instance;
    private LanDiscovery Discovery => Lobby != null ? Lobby.discovery : null;

    private class RoomEntryView
    {
        public GameObject go;
        public LanDiscovery.RoomEntry room;
        public Graphic label;
    }

    private class TeamSlotView
    {
        public Button button;
        public Image background;
        public Text label;
        public LobbyTeam team;
    }

    private class CharacterButtonView
    {
        public Button button;
        public Image background;
        public Text label;
        public byte characterIndex;
    }

    private readonly List<RoomEntryView> m_EntryViews = new List<RoomEntryView>();
    private readonly List<TeamSlotView> m_RedSlots = new List<TeamSlotView>();
    private readonly List<TeamSlotView> m_BlueSlots = new List<TeamSlotView>();
    private readonly List<CharacterButtonView> m_CharacterButtons = new List<CharacterButtonView>();

    private LanDiscovery.RoomEntry m_SelectedRoom;
    private bool m_ButtonsInteractable = true;
    private bool m_EventsBound;
    private LobbyGameMode m_RequestedGameMode = LobbyGameMode.PVE;

    private GameObject m_PvpTeamRoot;
    private Text m_RedHeader;
    private Text m_BlueHeader;
    private Text m_PvpHint;

    protected override void Awake()
    {
        base.Awake();

        if (btnCreateRoom != null) btnCreateRoom.onClick.AddListener(OnCreateRoomClicked);
        if (btnRefresh != null) btnRefresh.onClick.AddListener(OnRefreshClicked);
        if (btnJoin != null) btnJoin.onClick.AddListener(OnJoinClicked);
        if (btnStartGame != null) btnStartGame.onClick.AddListener(OnStartGameClicked);
        if (btnLeaveRoom != null) btnLeaveRoom.onClick.AddListener(OnLeaveRoomClicked);
        if (btnBack != null) btnBack.onClick.AddListener(OnBackClicked);

        BuildPvpTeamView();
    }

    protected override void Start()
    {
        base.Start();
        if (inputPlayerName != null)
            inputPlayerName.text = SystemInfo.deviceName;
    }

    /// <summary>由主菜单的 PVE/PVP 入口指定本次只浏览和创建对应模式的房间。</summary>
    public void Enter(LobbyGameMode gameMode)
    {
        EnterInternal(gameMode);
    }

    public override void Enter()
    {
        // 参数为空时保持原有 PVE 大厅入口语义。
        EnterInternal(LobbyGameMode.PVE);
    }

    private void EnterInternal(LobbyGameMode gameMode)
    {
        m_RequestedGameMode = gameMode;
        if (Lobby != null)
            Lobby.SetBrowsingGameMode(m_RequestedGameMode);

        BindEvents();
        base.Enter();
        ShowBrowseView();
        UpdateModePresentation();

        if (Discovery != null)
            Discovery.StartListening();
        RefreshRoomListView();
    }

    private void OnEnable()
    {
        BindEvents();
    }

    private void OnDisable()
    {
        UnbindEvents();
    }

    private void BindEvents()
    {
        if (m_EventsBound || Lobby == null)
            return;

        Lobby.OnLobbyChanged += RefreshLobbyView;
        if (Discovery != null)
            Discovery.OnRoomsChanged += RefreshRoomListView;
        m_EventsBound = true;
    }

    private void UnbindEvents()
    {
        if (!m_EventsBound)
            return;

        if (Lobby != null)
            Lobby.OnLobbyChanged -= RefreshLobbyView;
        if (Discovery != null)
            Discovery.OnRoomsChanged -= RefreshRoomListView;
        m_EventsBound = false;
    }

    // ---- 按钮回调 ----

    private void OnCreateRoomClicked()
    {
        if (Lobby == null)
            return;

        string defaultName = m_RequestedGameMode == LobbyGameMode.PVP ? "PVP房间" : "PVE房间";
        string roomName = inputRoomName != null && !string.IsNullOrWhiteSpace(inputRoomName.text)
            ? inputRoomName.text.Trim()
            : defaultName;

        ApplyPlayerName();
        Lobby.CreateRoom(roomName, m_RequestedGameMode);
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
        if (m_SelectedRoom.gameMode != m_RequestedGameMode)
        {
            SetStatus("该房间玩法模式不匹配");
            return;
        }
        if (m_SelectedRoom.isStarted || m_SelectedRoom.currentPlayers >= m_SelectedRoom.maxPlayers)
        {
            SetStatus("该房间已开局或已满，无法加入");
            return;
        }

        ApplyPlayerName();
        Lobby.JoinRoom(m_SelectedRoom);
        ShowInRoomView();
        SetStatus("正在连接...");
    }

    private void OnStartGameClicked()
    {
        if (Lobby == null)
            return;

        bool started = Lobby.TryStartGame(out string message);
        if (!started && m_PvpHint != null && IsPvpRoom())
            m_PvpHint.text = message;
        else
            SetStatus(message);
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

    private void SelectRoom(LanDiscovery.RoomEntry room)
    {
        m_SelectedRoom = room;
        UpdateJoinButtonState();
        RefreshSelectionHighlight();
        if (room != null)
            SetStatus("已选中房间：" + room.roomName);
    }

    private void SelectTeam(LobbyTeam team)
    {
        if (Lobby == null || Lobby.CurrentLobby == null)
            return;
        if (Lobby.CurrentLobby.GetTeamCount(team) >= LobbyState.PvpTeamCapacity)
        {
            if (m_PvpHint != null)
                m_PvpHint.text = team == LobbyTeam.Red ? "红队已满" : "蓝队已满";
            return;
        }

        Lobby.SelectTeam(team);
    }

    private void SelectCharacter(byte characterIndex)
    {
        if (Lobby == null || Lobby.CurrentLobby == null)
            return;
        Lobby.SelectCharacter(characterIndex);
    }

    // ---- 视图切换 ----

    private void ShowInRoomView()
    {
        if (panelBrowse != null) panelBrowse.SetActive(false);
        if (panelInRoom != null) panelInRoom.SetActive(true);

        bool isPvp = IsPvpRoom();
        if (txtMemberList != null) txtMemberList.gameObject.SetActive(!isPvp);
        if (m_PvpTeamRoot != null) m_PvpTeamRoot.SetActive(isPvp);
        if (btnStartGame != null) btnStartGame.gameObject.SetActive(Lobby != null && Lobby.IsHost);
        RefreshLobbyView();
    }

    private void ShowBrowseView()
    {
        if (panelBrowse != null) panelBrowse.SetActive(true);
        if (panelInRoom != null) panelInRoom.SetActive(false);
        if (btnStartGame != null) btnStartGame.gameObject.SetActive(false);
        ClearSelection();
        UpdateModePresentation();
    }

    private bool IsPvpRoom()
    {
        return Lobby != null && Lobby.CurrentLobby != null
            ? Lobby.CurrentLobby.GameMode == LobbyGameMode.PVP
            : m_RequestedGameMode == LobbyGameMode.PVP;
    }

    private void UpdateModePresentation()
    {
        // 复用现有 PVE 面板资源，在进入时替换带 PVE/PVP 的浏览标题。
        if (panelBrowse == null)
            return;

        string target = m_RequestedGameMode == LobbyGameMode.PVP
            ? "创建PVP联机房间"
            : "创建PVE联机房间";
        foreach (Text text in panelBrowse.GetComponentsInChildren<Text>(true))
        {
            if (text.text.Contains("创建PVE") || text.text.Contains("创建PVP"))
            {
                text.text = target;
                break;
            }
        }
        foreach (TMP_Text text in panelBrowse.GetComponentsInChildren<TMP_Text>(true))
        {
            if (text.text.Contains("创建PVE") || text.text.Contains("创建PVP"))
            {
                text.text = target;
                break;
            }
        }
    }

    // ---- 房间浏览 ----

    private void ApplyPlayerName()
    {
        if (Lobby == null)
            return;
        if (inputPlayerName != null && !string.IsNullOrWhiteSpace(inputPlayerName.text))
            Lobby.PlayerName = inputPlayerName.text.Trim();
    }

    private void RefreshRoomListView()
    {
        if (Discovery == null || roomListContainer == null || roomEntryPrefab == null)
            return;
        if (panelBrowse != null && !panelBrowse.activeSelf)
            return;

        Discovery.RefreshRoomList();
        foreach (RoomEntryView view in m_EntryViews)
        {
            if (view.go != null)
                Destroy(view.go);
        }
        m_EntryViews.Clear();

        int matchingRooms = 0;
        foreach (LanDiscovery.RoomEntry room in Discovery.Rooms)
        {
            if (room.gameMode != m_RequestedGameMode)
                continue;

            matchingRooms++;
            GameObject go = Instantiate(roomEntryPrefab, roomListContainer);
            string state = room.isStarted ? "对局中" : room.currentPlayers + "/" + room.maxPlayers + "人";
            string entryText = room.roomName + "  [" + state + "]";

            Graphic label = null;
            TMP_Text tmpLabel = go.GetComponentInChildren<TMP_Text>();
            if (tmpLabel != null)
            {
                tmpLabel.text = entryText;
                label = tmpLabel;
            }
            else
            {
                Text text = go.GetComponentInChildren<Text>();
                if (text != null)
                {
                    text.text = entryText;
                    label = text;
                }
            }

            Button button = go.GetComponent<Button>();
            if (button != null)
            {
                LanDiscovery.RoomEntry captured = room;
                button.onClick.AddListener(() => SelectRoom(captured));
            }
            m_EntryViews.Add(new RoomEntryView { go = go, room = room, label = label });
        }

        if (txtEmpty != null)
            txtEmpty.gameObject.SetActive(matchingRooms == 0);

        if (m_SelectedRoom != null)
        {
            bool stillExists = false;
            foreach (RoomEntryView view in m_EntryViews)
            {
                if (view.room == m_SelectedRoom)
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

    private void UpdateJoinButtonState()
    {
        if (btnJoin == null)
            return;
        bool canJoin = m_SelectedRoom != null
            && m_SelectedRoom.gameMode == m_RequestedGameMode
            && !m_SelectedRoom.isStarted
            && m_SelectedRoom.currentPlayers < m_SelectedRoom.maxPlayers;
        btnJoin.interactable = m_ButtonsInteractable && canJoin;
    }

    private void RefreshSelectionHighlight()
    {
        foreach (RoomEntryView view in m_EntryViews)
        {
            if (view.label != null)
                view.label.color = view.room == m_SelectedRoom ? selectedLabelColor : normalLabelColor;
        }
    }

    // ---- 房间内刷新 ----

    private void RefreshLobbyView()
    {
        if (Lobby == null)
            return;

        LobbyState state = Lobby.CurrentLobby;
        bool isPvp = state != null ? state.GameMode == LobbyGameMode.PVP : m_RequestedGameMode == LobbyGameMode.PVP;

        if (txtMemberList != null) txtMemberList.gameObject.SetActive(!isPvp);
        if (m_PvpTeamRoot != null) m_PvpTeamRoot.SetActive(isPvp && panelInRoom != null && panelInRoom.activeSelf);

        if (state == null)
        {
            RefreshStartButtonState();
            return;
        }

        if (txtTitle != null)
            txtTitle.text = (state.GameMode == LobbyGameMode.PVP ? "[PVP] " : "[PVE] ") + state.GetRoomName();

        if (panelInRoom != null && panelInRoom.activeSelf)
        {
            if (isPvp)
                RefreshPvpTeamView(state);
            else if (txtMemberList != null)
                RefreshPveMemberList(state);
        }

        if (btnStartGame != null)
            btnStartGame.gameObject.SetActive(Lobby.IsHost);
        RefreshStartButtonState();
    }

    private void RefreshPveMemberList(LobbyState state)
    {
        List<string> names = state.GetPlayerNames();
        var builder = new StringBuilder();
        for (int i = 0; i < names.Count; i++)
        {
            string role = i == 0 ? "（房主）" : string.Empty;
            builder.AppendLine((i + 1) + ". " + names[i] + " " + role);
        }
        txtMemberList.text = builder.ToString();
    }

    private void RefreshPvpTeamView(LobbyState state)
    {
        List<LobbyPlayerData> redPlayers = state.GetTeamPlayers(LobbyTeam.Red);
        List<LobbyPlayerData> bluePlayers = state.GetTeamPlayers(LobbyTeam.Blue);

        if (m_RedHeader != null) m_RedHeader.text = "红队  " + redPlayers.Count + "/5";
        if (m_BlueHeader != null) m_BlueHeader.text = "蓝队  " + bluePlayers.Count + "/5";
        RefreshTeamSlots(m_RedSlots, redPlayers, state);
        RefreshTeamSlots(m_BlueSlots, bluePlayers, state);
        RefreshCharacterButtons(state);

        var unassigned = new List<string>();
        var missingCharacter = new List<string>();
        foreach (LobbyPlayerData player in state.GetPlayers())
        {
            if (player.Team == LobbyTeam.None)
                unassigned.Add(player.PlayerName.ToString());
            if (player.CharacterIndex >= LobbyState.PvpCharacterCount)
                missingCharacter.Add(player.PlayerName.ToString());
        }

        if (m_PvpHint != null)
        {
            if (unassigned.Count > 0)
                m_PvpHint.text = "未选队：" + string.Join("、", unassigned) + "（点击任一空位加入）";
            else if (missingCharacter.Count > 0)
                m_PvpHint.text = "未选角色：" + string.Join("、", missingCharacter);
            else if (!state.HasMinimumPvpTeams())
                m_PvpHint.text = "红蓝双方都至少需要 1 人";
            else
                m_PvpHint.text = "队伍已就绪，开局前可点击另一队空位换队";
        }
    }

    private void RefreshTeamSlots(
        List<TeamSlotView> slots,
        List<LobbyPlayerData> players,
        LobbyState state)
    {
        ulong localClientId = NetworkManager.Singleton != null
            ? NetworkManager.Singleton.LocalClientId
            : ulong.MaxValue;
        LobbyTeam localTeam = state.GetPlayerTeam(localClientId);

        for (int i = 0; i < slots.Count; i++)
        {
            TeamSlotView slot = slots[i];
            bool occupied = i < players.Count;
            if (occupied)
            {
                LobbyPlayerData player = players[i];
                bool isLocal = player.ClientId == localClientId;
                bool isHost = player.ClientId == NetworkManager.ServerClientId;
                slot.label.text = (i + 1) + ". " + player.PlayerName + " [" +
                                  GetCharacterDisplayName(player.CharacterIndex) + "]" +
                                  (isLocal ? "（我）" : isHost ? "（房主）" : string.Empty);
                slot.label.color = isLocal ? new Color(0.45f, 1f, 0.55f, 1f) : Color.white;
                slot.background.color = slot.team == LobbyTeam.Red
                    ? new Color(0.55f, 0.12f, 0.12f, 0.82f)
                    : new Color(0.10f, 0.25f, 0.58f, 0.82f);
                slot.button.interactable = false;
            }
            else
            {
                string teamName = slot.team == LobbyTeam.Red ? "红队" : "蓝队";
                slot.label.text = (i + 1) + ". 空位（点击加入" + teamName + "）";
                slot.label.color = new Color(1f, 1f, 1f, 0.72f);
                slot.background.color = slot.team == LobbyTeam.Red
                    ? new Color(0.35f, 0.08f, 0.08f, 0.58f)
                    : new Color(0.06f, 0.14f, 0.34f, 0.58f);
                slot.button.interactable = m_ButtonsInteractable
                    && !state.IsStarted.Value
                    && localTeam != slot.team;
            }
        }
    }

    private void RefreshCharacterButtons(LobbyState state)
    {
        ulong localClientId = NetworkManager.Singleton != null
            ? NetworkManager.Singleton.LocalClientId
            : ulong.MaxValue;
        byte selectedCharacter = state.GetPlayerCharacterIndex(localClientId);

        foreach (CharacterButtonView view in m_CharacterButtons)
        {
            bool selected = view.characterIndex == selectedCharacter;
            view.background.color = selected
                ? new Color(0.18f, 0.65f, 0.32f, 0.92f)
                : new Color(0.12f, 0.12f, 0.16f, 0.82f);
            view.label.text = GetCharacterDisplayName(view.characterIndex) + (selected ? " ✓" : string.Empty);
            view.button.interactable = m_ButtonsInteractable && !state.IsStarted.Value && !selected;
        }
    }

    private void RefreshStartButtonState()
    {
        if (btnStartGame == null)
            return;

        bool canStart = m_ButtonsInteractable && Lobby != null && Lobby.IsHost && Lobby.CurrentLobby != null;
        LobbyState state = Lobby != null ? Lobby.CurrentLobby : null;
        if (canStart && state.GameMode == LobbyGameMode.PVP)
        {
            canStart = state.HasMinimumPvpTeams();
            if (canStart)
            {
                foreach (LobbyPlayerData player in state.GetPlayers())
                {
                    if (player.Team == LobbyTeam.None)
                    {
                        canStart = false;
                        break;
                    }
                    if (player.CharacterIndex >= LobbyState.PvpCharacterCount)
                    {
                        canStart = false;
                        break;
                    }
                }
            }
        }
        btnStartGame.interactable = canStart;
    }

    // ---- 运行时构建 PVP 两队房间样式 ----

    private void BuildPvpTeamView()
    {
        if (txtMemberList == null || txtMemberList.transform.parent == null || m_PvpTeamRoot != null)
            return;

        m_PvpTeamRoot = CreateUiObject("PVPTeamRoom", txtMemberList.transform.parent);
        RectTransform rootRect = m_PvpTeamRoot.GetComponent<RectTransform>();
        rootRect.anchorMin = new Vector2(0.5f, 0.5f);
        rootRect.anchorMax = new Vector2(0.5f, 0.5f);
        rootRect.pivot = new Vector2(0.5f, 0.5f);
        rootRect.anchoredPosition = new Vector2(0f, -15f);
        rootRect.sizeDelta = new Vector2(780f, 520f);

        CreateLabel(
            "CharacterHeader",
            m_PvpTeamRoot.transform,
            new Vector2(-285f, 245f),
            new Vector2(160f, 38f),
            22,
            Color.white,
            TextAnchor.MiddleRight).text = "选择角色：";
        for (byte i = 0; i < LobbyState.PvpCharacterCount; i++)
            m_CharacterButtons.Add(CreateCharacterButton(i, new Vector2(-120f + i * 145f, 245f)));

        BuildTeamColumn(LobbyTeam.Red, -200f, new Color(0.48f, 0.07f, 0.07f, 0.34f), m_RedSlots, out m_RedHeader);
        BuildTeamColumn(LobbyTeam.Blue, 200f, new Color(0.04f, 0.16f, 0.48f, 0.34f), m_BlueSlots, out m_BlueHeader);

        m_PvpHint = CreateLabel(
            "PvpHint",
            m_PvpTeamRoot.transform,
            new Vector2(0f, -205f),
            new Vector2(760f, 34f),
            20,
            Color.white,
            TextAnchor.MiddleCenter);
        m_PvpTeamRoot.SetActive(false);
    }

    private CharacterButtonView CreateCharacterButton(byte characterIndex, Vector2 anchoredPosition)
    {
        GameObject go = CreateUiObject("Character" + characterIndex, m_PvpTeamRoot.transform);
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = new Vector2(132f, 40f);

        Image image = go.AddComponent<Image>();
        image.color = new Color(0.12f, 0.12f, 0.16f, 0.82f);
        Button button = go.AddComponent<Button>();
        button.targetGraphic = image;
        byte capturedIndex = characterIndex;
        button.onClick.AddListener(() => SelectCharacter(capturedIndex));

        Text label = CreateLabel(
            "Label",
            go.transform,
            Vector2.zero,
            new Vector2(124f, 36f),
            19,
            Color.white,
            TextAnchor.MiddleCenter);
        label.text = GetCharacterDisplayName(characterIndex);
        label.raycastTarget = false;

        return new CharacterButtonView
        {
            button = button,
            background = image,
            label = label,
            characterIndex = characterIndex,
        };
    }

    private static string GetCharacterDisplayName(byte characterIndex)
    {
        switch (characterIndex)
        {
            case 0: return "Lumine";
            case 1: return "Furina";
            case 2: return "Aether";
            default: return "未选择";
        }
    }

    private void BuildTeamColumn(
        LobbyTeam team,
        float x,
        Color panelColor,
        List<TeamSlotView> targetSlots,
        out Text header)
    {
        string teamName = team == LobbyTeam.Red ? "RedTeam" : "BlueTeam";
        GameObject panel = CreateUiObject(teamName, m_PvpTeamRoot.transform);
        RectTransform panelRect = panel.GetComponent<RectTransform>();
        panelRect.anchorMin = panelRect.anchorMax = new Vector2(0.5f, 0.5f);
        panelRect.pivot = new Vector2(0.5f, 0.5f);
        panelRect.anchoredPosition = new Vector2(x, 12f);
        panelRect.sizeDelta = new Vector2(360f, 402f);
        Image panelImage = panel.AddComponent<Image>();
        panelImage.color = panelColor;
        panelImage.raycastTarget = false;

        Color headerColor = team == LobbyTeam.Red
            ? new Color(1f, 0.42f, 0.42f, 1f)
            : new Color(0.42f, 0.68f, 1f, 1f);
        header = CreateLabel(
            teamName + "Header",
            panel.transform,
            new Vector2(0f, 166f),
            new Vector2(330f, 48f),
            30,
            headerColor,
            TextAnchor.MiddleCenter);

        for (int i = 0; i < LobbyState.PvpTeamCapacity; i++)
        {
            float y = 112f - i * 58f;
            TeamSlotView slot = CreateTeamSlot(panel.transform, team, i, new Vector2(0f, y));
            targetSlots.Add(slot);
        }
    }

    private TeamSlotView CreateTeamSlot(Transform parent, LobbyTeam team, int index, Vector2 anchoredPosition)
    {
        GameObject go = CreateUiObject(team + "Slot" + (index + 1), parent);
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = new Vector2(322f, 47f);

        Image image = go.AddComponent<Image>();
        Button button = go.AddComponent<Button>();
        button.targetGraphic = image;
        ColorBlock colors = button.colors;
        colors.highlightedColor = new Color(1f, 1f, 1f, 0.95f);
        colors.pressedColor = new Color(0.72f, 0.72f, 0.72f, 1f);
        colors.disabledColor = Color.white;
        colors.colorMultiplier = 1f;
        button.colors = colors;

        LobbyTeam capturedTeam = team;
        button.onClick.AddListener(() => SelectTeam(capturedTeam));

        Text label = CreateLabel(
            "Label",
            go.transform,
            Vector2.zero,
            new Vector2(306f, 43f),
            20,
            Color.white,
            TextAnchor.MiddleLeft);
        label.raycastTarget = false;

        return new TeamSlotView
        {
            button = button,
            background = image,
            label = label,
            team = team,
        };
    }

    private Text CreateLabel(
        string objectName,
        Transform parent,
        Vector2 anchoredPosition,
        Vector2 size,
        int fontSize,
        Color color,
        TextAnchor alignment)
    {
        GameObject go = CreateUiObject(objectName, parent);
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = size;

        Text label = go.AddComponent<Text>();
        label.font = txtMemberList.font;
        label.fontStyle = txtMemberList.fontStyle;
        label.fontSize = fontSize;
        label.alignment = alignment;
        label.color = color;
        label.supportRichText = true;
        label.horizontalOverflow = HorizontalWrapMode.Wrap;
        label.verticalOverflow = VerticalWrapMode.Truncate;
        return label;
    }

    private GameObject CreateUiObject(string objectName, Transform parent)
    {
        GameObject go = new GameObject(objectName, typeof(RectTransform));
        go.layer = panelInRoom != null ? panelInRoom.layer : gameObject.layer;
        go.transform.SetParent(parent, false);
        return go;
    }

    private void SetStatus(string message)
    {
        if (txtStatus != null)
            txtStatus.text = message;
    }

    // ---- UIBase ----

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
        if (btnLeaveRoom != null) btnLeaveRoom.interactable = interactable;
        if (btnBack != null) btnBack.interactable = interactable;
        UpdateJoinButtonState();
        RefreshLobbyView();
    }
}
