using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

public class MainMenuUI : UIBase<MainMenuUI>
{
    public Button btnOnline;
    public Button btnContinue;
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
        btnOnline.onClick.AddListener(ShowTipMenu);
        btnContinue.onClick.AddListener(ShowTipMenu);
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
        btnVoice.onClick.AddListener(ShowTipMenu);
        btnExit.onClick.AddListener((() =>
        {
            ExitMenuUI.instance.Enter();
        }));
    }

    protected override void Start()
    {
        base.Start();
        Enter();
    }
    
    private void ShowTipMenu()
    {
        Exit(() =>
        {
            TipMenuUI.instance.Enter();
        });
    }

    protected override void DisableButtons()
    {
        btnOnline.interactable = false;
    }

    protected override void ResumeButtons()
    {
        btnOnline.interactable = true;
    }
}
