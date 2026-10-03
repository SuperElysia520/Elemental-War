using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

public class MainMenuUI : UIBase<MainMenuUI>
{
    public Button btnOnlinePVP;
    public Button btnContinuePVE;
    public Button btnNewGame;
    public Button btnRead;
    public Button btnCharacter;
    public Button btnClothes;
    public Button btnSettings;
    public Button btnAchievements;
    public Button btnAuthor;
    public Button btnLanguage;
    public Button btnVoice;
    public Button btnExit;
    private bool loadingLobby;

    protected override void Awake()
    {
        base.Awake();
        btnOnlinePVP.onClick.AddListener(ShowPVPLobbyMenu);
        btnContinuePVE.onClick.AddListener(ShowPVELobbyMenu);
        btnNewGame.onClick.AddListener(() =>
        {
            SceneManager.LoadScene("Game");
        });
        btnRead.onClick.AddListener(ShowTipMenu);
        btnCharacter.onClick.AddListener(ShowCharacterMenu);
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
        Enter();
    }
    
    private void ShowTipMenu()
    {
        Exit(() => // 主菜单先播放FadeOut
        {
            TipMenuUI.instance.Enter(); // 之后提示菜单播放FadeIn
            btnExit.gameObject.SetActive(false); // 隐藏退出按钮
        });
    }

    private void ShowPVELobbyMenu()
    {
        Exit(() => // 主菜单先播放FadeOut
        {
            LobbyMenuUI.instance.Enter(); // 保持原有 PVE 大厅入口
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
        btnOnlinePVP.interactable = false;
    }

    protected override void ResumeButtons()
    {
        btnOnlinePVP.interactable = true;
    }
}
