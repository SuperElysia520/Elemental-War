using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class ExitMenuUI : UIBase<ExitMenuUI>
{
    public Button btnYes;
    public Button btnNo;

    protected override void Awake()
    {
        base.Awake();
        btnYes.onClick.AddListener(Application.Quit);
        btnNo.onClick.AddListener(() =>
        {
            Exit(() =>
            {
                MainMenuUI.instance.Enter();
                StartCoroutine(DisplayBtnExit());
                StopCoroutine(DisplayBtnExit());
            });
        });
    }
    
    protected override void DisableButtons()
    {
        btnYes.interactable = false;
        btnNo.interactable = false;
    }

    protected override void ResumeButtons()
    {
        btnYes.interactable = true;
        btnNo.interactable = true;
    }
}
