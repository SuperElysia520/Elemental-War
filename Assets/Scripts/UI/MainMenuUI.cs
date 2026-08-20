using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using UnityEngine;
using UnityEngine.Rendering;
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
        btnCharacter.onClick.AddListener(ShowTipMenu);
        btnClothes.onClick.AddListener(ShowTipMenu);
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
        Exit(() => // 主菜单先播放FadeOut
        {
            LobbyMenuUI.instance.Enter(LobbyGameMode.PVP); // 进入仅显示 PVP 房间的团队大厅
            btnExit.gameObject.SetActive(false); // 隐藏退出按钮
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
