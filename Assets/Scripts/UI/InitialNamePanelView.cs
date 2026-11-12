using System;
using TMPro;
using UnityEngine;

/// <summary>Displays the authored first-name panel and forwards confirmation to the login flow.</summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(CanvasGroup))]
public sealed class InitialNamePanelView : MonoBehaviour
{
    public TMP_InputField nameInput;
    public UnityEngine.UI.Button confirmButton;
    public TMP_Text status;
    private CanvasGroup group;
    public event Action<string> Confirmed;

    private void Awake()
    {
        group = GetComponent<CanvasGroup>();
        confirmButton.onClick.AddListener(Submit);
        nameInput.onSubmit.AddListener(SubmitText);
    }
    private void OnDestroy()
    {
        if (confirmButton != null) confirmButton.onClick.RemoveListener(Submit);
        if (nameInput != null) nameInput.onSubmit.RemoveListener(SubmitText);
    }
    private void Submit() => Confirmed?.Invoke(nameInput.text);
    private void SubmitText(string value) => Submit();

    public void Present(bool visible, bool busy, string message)
    {
        bool opening = visible && !gameObject.activeSelf;
        gameObject.SetActive(visible);
        if (!visible) return;
        if (group == null) group = GetComponent<CanvasGroup>();
        group.interactable = !busy; group.blocksRaycasts = true;
        nameInput.interactable = !busy; confirmButton.interactable = !busy;
        if (status != null) status.text = message;
        if (opening)
        {
            nameInput.SetTextWithoutNotify("");
            nameInput.Select(); nameInput.ActivateInputField();
        }
    }
}
