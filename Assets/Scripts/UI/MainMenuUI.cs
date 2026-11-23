using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

public class MainMenuUI : UIBase<MainMenuUI>
{
    public Button btnEnterLobby;
    public Button btnSinglePlayer;
    public Button btnTrainingRange;
    public Button btnCustomRoom;
    public Button btnCharacter;
    public Button btnClothes;
    public Button btnSettings;
    public Button btnAchievements;
    public Button btnAuthor;
    public Button btnLanguage;
    public Button btnVoice;
    public Button btnExit;
    private bool loadingLobby;
    private GameStartProfileLoader profileLoader;

    protected override void Awake()
    {
        base.Awake();
        btnEnterLobby.onClick.AddListener(ShowPVPLobbyMenu);
        if (btnSinglePlayer != null) btnSinglePlayer.gameObject.SetActive(false);
        if (btnTrainingRange != null) btnTrainingRange.onClick.AddListener(ShowCustomRoom);
        btnCustomRoom.onClick.AddListener(() =>
        {
            SceneManager.LoadScene("Game");
        });
        if (btnCharacter != null) btnCharacter.gameObject.SetActive(false);
        btnClothes.onClick.AddListener(ShowCharacterMenu);
        btnSettings.onClick.AddListener(ShowTipMenu);
        btnAchievements.onClick.AddListener(ShowTipMenu);
        btnAuthor.onClick.AddListener(ShowTipMenu);
        btnLanguage.onClick.AddListener(ShowTipMenu);
        btnVoice.onClick.AddListener(ShowTipMenu); // 先Exit主菜单，在Enter提示菜单
        btnExit.onClick.AddListener((() =>
        {
            Exit(() =>
            {
                ExitMenuUI.instance.Enter();
                btnExit.gameObject.SetActive(false);
            });
        }));
    }

    protected override void Start()
    {
        base.Start();
        profileLoader = LoginSceneBootstrap.FindLoader(gameObject.scene);
        if (DedicatedServerBootstrap.IsDedicatedServerProcess || profileLoader == null || profileLoader.IsReady)
            Enter();
        else
        {
            // Loader lives on a separate root, so hiding this menu cannot stop the profile request.
            gameObject.SetActive(false);
            profileLoader.Ready += ShowLoadedMenu;
        }
    }

    private void ShowLoadedMenu()
    {
        profileLoader.Ready -= ShowLoadedMenu;
        Enter();
    }

    protected override void OnDestroy()
    {
        if (profileLoader != null) profileLoader.Ready -= ShowLoadedMenu;
        base.OnDestroy();
    }
    
    private void ShowTipMenu()
    {
        Exit(() => // 主菜单先播放FadeOut
        {
            TipMenuUI.instance.Enter(); // 之后提示菜单播放FadeIn
            btnExit.gameObject.SetActive(false); // 隐藏退出按钮
        });
    }

    private void ShowPVPLobbyMenu()
    {
        if (loadingLobby)
            return;
        if (!GameFlowScenes.TryValidateSceneAvailable(GameFlowScenes.MultiplayerLobby, out string message))
        {
            Debug.LogWarning("[Main Menu] " + message);
            Exit(() =>
            {
                btnExit.gameObject.SetActive(false);
                TipMenuUI.instance.EnterMessage(message);
            });
            return;
        }
        loadingLobby = true;
        Exit(() =>
        {
            StartCoroutine(LoadMultiplayerLobby());
        });
    }

    public void ShowCustomRoom()
    {
        if (PartyLobbyClient.Instance != null && PartyLobbyClient.Instance.Party != null)
        { TipMenuUI.instance.EnterMessage("请先返回大厅离开匹配队伍，再进入自定义房间。"); return; }
        Exit(() =>
        {
            LobbyMenuUI.instance.Enter(LobbyGameMode.PVP);
            btnExit.gameObject.SetActive(false);
        });
    }

    private IEnumerator LoadMultiplayerLobby()
    {
        Time.timeScale = 1f;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        yield return SceneManager.LoadSceneAsync(GameFlowScenes.MultiplayerLobby, LoadSceneMode.Single);
    }

    private void ShowCharacterMenu()
    {
        Exit(() =>
        {
            btnExit.gameObject.SetActive(false);
            CharacterCustomizationUI.Show(() => { Enter(); btnExit.gameObject.SetActive(true); }, "返回主菜单");
        });
    }

    protected override void DisableButtons()
    {
        btnEnterLobby.interactable = false;
    }

    protected override void ResumeButtons()
    {
        btnEnterLobby.interactable = true;
    }
}
