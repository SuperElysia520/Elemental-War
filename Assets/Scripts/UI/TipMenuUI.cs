using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// 提示菜单
/// </summary>
public class TipMenuUI : UIBase<TipMenuUI>
{
    public Button btnNo;
    [Tooltip("可选：提示正文。留空时从当前面板的非按钮文字中查找。")]
    [SerializeField] private Text messageLabel;
    [SerializeField] private TMP_Text tmpMessageLabel;
    private string defaultMessage;

    protected override void Awake()
    {
        base.Awake();
        if (messageLabel == null && tmpMessageLabel == null)
        {
            foreach (TMP_Text label in GetComponentsInChildren<TMP_Text>(true))
            {
                if (label.GetComponentInParent<Button>() != null) continue;
                tmpMessageLabel = label;
                break;
            }
        }
        if (messageLabel == null && tmpMessageLabel == null)
        {
            foreach (Text label in GetComponentsInChildren<Text>(true))
            {
                if (label.GetComponentInParent<Button>() != null) continue;
                messageLabel = label;
                break;
            }
        }
        defaultMessage = tmpMessageLabel != null ? tmpMessageLabel.text : messageLabel != null ? messageLabel.text : "";
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

    public override void Enter()
    {
        SetMessage(defaultMessage);
        base.Enter();
    }

    public void EnterMessage(string message)
    {
        SetMessage(message);
        base.Enter();
    }

    private void SetMessage(string message)
    {
        if (messageLabel != null) messageLabel.text = message;
        if (tmpMessageLabel != null) tmpMessageLabel.text = message;
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
