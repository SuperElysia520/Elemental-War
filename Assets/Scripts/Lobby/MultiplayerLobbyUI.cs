using System.Collections;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>只连接编辑器中手工制作的 GameLobby 主界面，不生成大厅场景或 UI。</summary>
public sealed class MultiplayerLobbyUI : MonoBehaviour
{
    [Header("手工制作的大厅主界面")]
    [Tooltip("大厅首页的子物体。房间面板应放在它外面，控制器应保持激活。")]
    public GameObject homePanel;
    public Button btnStartGame;
    public Button btnRoomList;
    public Button btnCharacter;
    public Button btnClothes;
    public Button btnLoadout;
    public Button btnAccount;
    public Button btnReturnMainMenu;

    [Header("沿用现有房间界面")]
    [Tooltip("复制 GameStart 中现有的 LobbyMenuUI 面板并拖入此处，初始关闭。")]
    public LobbyMenuUI roomMenu;
    [Header("五人匹配队伍")]
    public PartyLobbyUI partyUI;

    [Header("玩家名")]
    public TMP_Text tmpPlayerName;

    [Header("状态提示")]
    public TMP_Text tmpStatus;

    private AccountClient account;
    private LobbyManager boundLobby;
    private PartyLobbyClient boundParty;
    private bool eventsBound;
    private bool leavingScene;
    private string actionStatus = "";

    private void Awake()
    {
        if (btnStartGame != null) btnStartGame.onClick.AddListener(StartParty);
        if (btnRoomList != null) btnRoomList.onClick.AddListener(OpenRooms);
        if (btnCharacter != null) btnCharacter.onClick.AddListener(OpenCustomization);
        if (btnClothes != null) btnClothes.onClick.AddListener(OpenCustomization);
        if (btnLoadout != null) btnLoadout.onClick.AddListener(OpenCustomization);
        if (btnAccount != null) btnAccount.onClick.AddListener(OpenCustomization);
        if (btnReturnMainMenu != null) btnReturnMainMenu.onClick.AddListener(ReturnToMainMenu);
        if (roomMenu != null)
        {
            roomMenu.show = false;
            roomMenu.SetBackHandler(LeaveCustomRoom);
        }
    }

    private void OnEnable()
    {
        BindEvents();
        UnlockCursor();
        RefreshPresentation();
    }

    private void Start()
    {
        // All scene Awake methods have completed, including an authored network root.
        BindEvents();
        if (roomMenu != null) roomMenu.gameObject.SetActive(false);
        ShowHome();
    }

    private void OnDisable() => UnbindEvents();

    private void OnDestroy()
    {
        UnbindEvents();
        if (btnStartGame != null) btnStartGame.onClick.RemoveListener(StartParty);
        if (btnRoomList != null) btnRoomList.onClick.RemoveListener(OpenRooms);
        if (btnCharacter != null) btnCharacter.onClick.RemoveListener(OpenCustomization);
        if (btnClothes != null) btnClothes.onClick.RemoveListener(OpenCustomization);
        if (btnLoadout != null) btnLoadout.onClick.RemoveListener(OpenCustomization);
        if (btnAccount != null) btnAccount.onClick.RemoveListener(OpenCustomization);
        if (btnReturnMainMenu != null) btnReturnMainMenu.onClick.RemoveListener(ReturnToMainMenu);
        if (roomMenu != null) roomMenu.SetBackHandler(null);
    }

    private void BindEvents()
    {
        if (!eventsBound)
        {
            account = AccountClient.EnsureInstance();
            account.StateChanged += RefreshPresentation;
            account.ProfileChanged += OnProfileChanged;
            eventsBound = true;
        }
        if (boundLobby == null && LobbyManager.Instance != null)
        {
            boundLobby = LobbyManager.Instance;
            boundLobby.OnLobbyChanged += RefreshPresentation;
        }
        if (boundParty == null)
        {
            boundParty = PartyLobbyClient.EnsureInstance();
            boundParty.Changed += RefreshPresentation;
        }
    }

    private void UnbindEvents()
    {
        if (eventsBound && account != null)
        {
            account.StateChanged -= RefreshPresentation;
            account.ProfileChanged -= OnProfileChanged;
        }
        eventsBound = false;
        if (boundLobby != null) boundLobby.OnLobbyChanged -= RefreshPresentation;
        boundLobby = null;
        if (boundParty != null) boundParty.Changed -= RefreshPresentation;
        boundParty = null;
    }

    /// <summary>独立房间列表入口；大厅开始游戏由 StartParty 自动建队。</summary>
    public void OpenRooms()
    {
        if (!ValidatePartyEntry()) return;
        partyUI.OpenRooms();
        if (partyUI.roomPanel != null && partyUI.roomPanel.activeSelf) SetHomeVisible(false);
    }

    public void StartParty()
    {
        if (!ValidatePartyEntry()) return;
        if (!partyUI.ValidateParty(out string error)) { SetStatus(error); return; }
        actionStatus = "";
        PartyLobbyClient client = PartyLobbyClient.EnsureInstance();
        if (client.Party != null) { SetHomeVisible(false); partyUI.ShowParty(); }
        else partyUI.CreateAndShow();
        UnlockCursor();
    }

    private bool ValidatePartyEntry()
    {
        if (leavingScene) return false;
        BindEvents();
        if (!account.IsAuthenticated)
        { actionStatus = "请先登录账号。"; OpenCustomization(); return false; }
        if (account.IsBusy) { SetStatus("正在同步账号，请稍候。"); return false; }
        if (boundParty != null && boundParty.IsBusy) { RefreshPresentation(); return false; }
        if (LobbyManager.Instance == null) { SetStatus("请从 GameStart 进入大厅，确保 NetworkManager / LobbyManager 已就绪。"); return false; }
        if (LobbyManager.Instance.IsInLobby && LobbyManager.Instance.CurrentLobby != null && !LobbyManager.Instance.CurrentLobby.IsMatchmaking)
        { SetStatus("请先退出当前自定义房间。"); return false; }
        if (partyUI == null) { SetStatus("请绑定 PartyLobbyUI，接入已制作的 LobbyPanel 和 RoomPanel。"); return false; }
        return true;
    }

    private void LeaveCustomRoom()
    {
        if (LobbyManager.Instance != null) LobbyManager.Instance.LeaveRoom();
        ShowHome();
    }

    public void HideHomeForParty() => SetHomeVisible(false);
    public void DisplayStatus(string message) { actionStatus = message ?? ""; RefreshPresentation(); }

    /// <summary>GameStart 自定义房间同步进入此场景时，继续显示原有双方十人选队界面。</summary>
    private void OpenCustomRoom()
    {
        if (leavingScene) return;
        BindEvents();
        LobbyManager lobby = LobbyManager.Instance;
        bool hasRoom = lobby != null && (lobby.IsInLobby || lobby.IsConnecting);
        if (!account.IsAuthenticated && !hasRoom)
        {
            actionStatus = "请先登录账号，再点击开始游戏选择房间。";
            RefreshPresentation();
            OpenCustomization();
            return;
        }
        if (account.IsBusy && !hasRoom)
        {
            SetStatus("正在同步账号，请稍候。");
            return;
        }
        if (roomMenu == null)
        {
            SetStatus("大厅尚未绑定房间面板，请在 Inspector 设置 Room Menu。");
            return;
        }
        if (LobbyManager.Instance == null)
        {
            SetStatus("大厅未配置联机组件，请先绑定已有的 NetworkManager / LobbyManager。");
            return;
        }
        if (homePanel != null && roomMenu.transform.IsChildOf(homePanel.transform))
        {
            SetStatus("房间面板需要放在大厅首页外面，不能作为 Home Panel 的子物体。");
            return;
        }
        actionStatus = "";
        roomMenu.SetBackHandler(LeaveCustomRoom);
        SetHomeVisible(false);
        roomMenu.Enter(LobbyGameMode.PVP);
        UnlockCursor();
    }

    /// <summary>继续使用现有账号、服装和枪械配装界面。</summary>
    public void OpenCustomization()
    {
        if (leavingScene) return;
        if (!CanOpenCustomization(out string message))
        {
            SetStatus(message);
            return;
        }
        SetHomeVisible(false);
        CharacterCustomizationUI.Show(() =>
        {
            if (this != null) ShowHome();
        }, "返回大厅");
    }

    public void ShowHome()
    {
        if (this == null || leavingScene) return;
        SetHomeVisible(true);
        UnlockCursor();
        RefreshPresentation();
    }

    public void ReturnToMainMenu()
    {
        if (leavingScene) return;
        if (boundParty != null && boundParty.IsBusy) { SetStatus("正在处理队伍请求，请稍候。"); return; }
        if (!GameFlowScenes.TryValidateSceneAvailable(GameFlowScenes.MainMenu, out string error))
        {
            SetStatus(error);
            return;
        }
        leavingScene = true;
        RefreshPresentation();
        StartCoroutine(LeaveAndLoadMainMenu());
    }

    private IEnumerator LeaveAndLoadMainMenu()
    {
        PartyLobbyClient party = PartyLobbyClient.Instance;
        if (party != null && party.Party != null)
        {
            while (party.IsBusy) yield return null;
            bool done = false, success = false;
            party.Leave(result => { success = result; done = true; });
            while (!done) yield return null;
            if (!success) { leavingScene = false; SetStatus(party.Status); yield break; }
        }
        LobbyManager lobby = LobbyManager.Instance;
        if (lobby != null)
        {
            lobby.StopDedicatedPvpRoomDiscovery();
            if (lobby.discovery != null) lobby.discovery.StopListening();
            if (lobby.IsInLobby || lobby.IsConnecting) lobby.LeaveRoom();
        }
        NetworkManager network = NetworkManager.Singleton;
        while (network != null && network.ShutdownInProgress)
            yield return null;
        if (this == null) yield break;
        SceneManager.LoadScene(GameFlowScenes.MainMenu);
    }

    private void OnProfileChanged(PlayerProfileData profile)
    {
        actionStatus = profile != null ? "账号档案已同步，角色展示已更新。" : "已退出账号。";
        RefreshPresentation();
    }

    private void RefreshPresentation()
    {
        if (account == null) return;
        string playerName = account.IsAuthenticated ? account.Profile.username : "未登录";
        if (tmpPlayerName != null) tmpPlayerName.text = playerName;
        LobbyManager lobby = LobbyManager.Instance;
        bool partyBusy = boundParty != null && boundParty.IsBusy;
        bool hasRoom = lobby != null && (lobby.IsInLobby || lobby.IsConnecting);
        if (btnStartGame != null) btnStartGame.interactable = !leavingScene && !partyBusy && (!account.IsBusy || hasRoom);
        if (btnRoomList != null) btnRoomList.interactable = !leavingScene && !account.IsBusy && !partyBusy;
        if (btnReturnMainMenu != null) btnReturnMainMenu.interactable = !leavingScene && !partyBusy;
        bool canEdit = !leavingScene && !partyBusy && CanOpenCustomization(out _);
        if (btnCharacter != null) btnCharacter.interactable = canEdit;
        if (btnClothes != null) btnClothes.interactable = canEdit;
        if (btnLoadout != null) btnLoadout.interactable = canEdit;
        if (btnAccount != null) btnAccount.interactable = canEdit;

        string message;
        if (leavingScene) message = "正在返回主菜单…";
        else if (account.IsBusy) message = "正在同步账号…";
        else if (partyBusy) message = boundParty.Party == null ? "正在创建队伍…" : "正在处理队伍请求…";
        else if (!string.IsNullOrEmpty(account.LastError)) message = account.LastError;
        else if (lobby != null && (lobby.IsInLobby || lobby.IsConnecting)
            && !string.IsNullOrWhiteSpace(lobby.ConnectionStatus)) message = lobby.ConnectionStatus;
        else if (!string.IsNullOrEmpty(actionStatus)) message = actionStatus;
        else if (!account.IsAuthenticated) message = "请登录账号，载入角色与配装。";
        else if (lobby != null && lobby.IsInLobby) message = "当前房间：" + lobby.GetRoomName();
        else if (lobby != null && !string.IsNullOrWhiteSpace(lobby.ConnectionStatus)) message = lobby.ConnectionStatus;
        else message = "开始游戏自动创建队伍；房间列表加入已有队伍。";
        SetStatus(message);
        if (!leavingScene && lobby != null && lobby.IsInLobby && lobby.CurrentLobby != null
            && !lobby.CurrentLobby.IsMatchmaking && roomMenu != null && !roomMenu.gameObject.activeSelf)
            OpenCustomRoom();
    }

    private static bool CanOpenCustomization(out string message)
    {
        PartyData party = PartyLobbyClient.Instance != null ? PartyLobbyClient.Instance.Party : null;
        if (party != null && party.state != "Waiting")
        { message = "正在匹配或进入对局，请取消匹配后再编辑。"; return false; }
        LobbyState state = LobbyManager.Instance != null ? LobbyManager.Instance.CurrentLobby : null;
        if (state != null && state.GameMode == LobbyGameMode.PVP
            && (state.PvpIsPreparingMatch.Value || state.IsStarted.Value))
        {
            message = state.PvpIsPreparingMatch.Value
                ? "正在确认全员配装，请开局检查结束后再编辑。"
                : "对局正在开始，请在返回大厅后编辑角色与配装。";
            return false;
        }
        message = "";
        return true;
    }

    private void SetHomeVisible(bool visible)
    {
        if (homePanel == null) return;
        if (transform.IsChildOf(homePanel.transform))
        {
            Debug.LogError("[GameLobby] MultiplayerLobbyUI 应放在 Home Panel 外，保持激活以同步账号和房间。", this);
            return;
        }
        homePanel.SetActive(visible);
    }

    private void SetStatus(string message)
    {
        if (tmpStatus != null) tmpStatus.text = message;
    }

    private static void UnlockCursor()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }
}
