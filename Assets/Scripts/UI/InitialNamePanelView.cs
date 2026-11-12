using System;
using UnityEngine;

/// <summary>Displays the authored first-name panel and forwards confirmation to the login flow.</summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(CanvasGroup))]
public sealed class InitialNamePanelView : MonoBehaviour
{
    public UnityEngine.UI.InputField nameInput;
    public UnityEngine.UI.Button confirmButton;
    private CanvasGroup group;
    public event Action<string> Confirmed;

    private void Awake()
    {
        group = GetComponent<CanvasGroup>();
        confirmButton.onClick.AddListener(Submit);
        nameInput.characterLimit = DisplayNameRules.MaxLength;
        nameInput.lineType = UnityEngine.UI.InputField.LineType.SingleLine;
        nameInput.onEndEdit.AddListener(SubmitText);
    }
    private void OnDestroy()
    {
        if (confirmButton != null) confirmButton.onClick.RemoveListener(Submit);
        if (nameInput != null) nameInput.onEndEdit.RemoveListener(SubmitText);
    }
    private void Submit() => Confirmed?.Invoke(nameInput.text);
    private void SubmitText(string value)
    {
        // Legacy onEndEdit also fires when clicking elsewhere. Only Enter submits.
        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) Submit();
    }

    public void Present(bool visible, bool busy)
    {
        bool opening = visible && !gameObject.activeSelf;
        gameObject.SetActive(visible);
        if (!visible) return;
        if (group == null) group = GetComponent<CanvasGroup>();
        group.interactable = !busy; group.blocksRaycasts = true;
        nameInput.interactable = !busy; confirmButton.interactable = !busy;
        if (opening)
        {
            nameInput.SetTextWithoutNotify("");
            nameInput.Select(); nameInput.ActivateInputField();
        }
    }
}
