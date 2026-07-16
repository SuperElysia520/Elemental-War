using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 提示菜单
/// </summary>
public class TipMenuUI : UIBase<TipMenuUI>
{
    public Button btnNo;

    protected override void Awake()
    {
        base.Awake();
        btnNo.onClick.AddListener(() =>
        {
            Exit(() =>
            {
                MainMenuUI.instance.Enter();
            });
        });
    }
    
    protected override void DisableButtons()
    {
        btnNo.interactable = false;
    }

    protected override void ResumeButtons()
    {
        btnNo.interactable = true;
    }
}
