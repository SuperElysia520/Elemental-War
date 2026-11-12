using UnityEngine;

/// <summary>Optional bindings for hand-authored Login UI. All references may be left empty until the UI is ready.</summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(LoginSceneController))]
public sealed class LoginUI : MonoBehaviour
{
    [Header("面板（脚本挂在始终启用的独立对象上）")]
    public GameObject loginPanel;
    public GameObject registrationPanel;
    public GameObject readyPanel;
    public GameObject loadingPanel;
    public InitialNamePanelView initialNamePanel;
    [Header("场景加载进度")]
    public UnityEngine.UI.Image loadingProgressImage;
    public UnityEngine.UI.Text loadingProgressText;
    public TMPro.TMP_Text loadingProgressTmpText;
    [Header("加载状态")]
    public TMPro.TMP_Text loadingStatusText;
    [Header("登录输入")]
    public UnityEngine.UI.InputField loginUsername;
    public UnityEngine.UI.InputField loginPassword;
    [Header("注册输入")]
    public UnityEngine.UI.InputField registrationUsername;
    public UnityEngine.UI.InputField registrationPassword;
    public UnityEngine.UI.InputField confirmPassword;
    [Header("账号与提示")]
    public UnityEngine.UI.Text usernameText;
    public UnityEngine.UI.Text uidText;
    public UnityEngine.UI.Text messageText;
    [Tooltip("可选：多个面板各自的提示文字。")]
    public UnityEngine.UI.Text[] additionalMessageTexts;
    [Tooltip("提交类按钮；请求期间自动禁用。按钮 OnClick 手工绑定下方公开方法。")]
    public UnityEngine.UI.Button[] requestButtons;

    private LoginSceneController controller;
    private LoginViewState previousState;

    private void Awake()
    {
        controller = GetComponent<LoginSceneController>();
        if (loadingProgressImage != null)
        {
            loadingProgressImage.type = UnityEngine.UI.Image.Type.Filled;
            loadingProgressImage.fillMethod = UnityEngine.UI.Image.FillMethod.Horizontal;
            loadingProgressImage.fillOrigin = (int)UnityEngine.UI.Image.OriginHorizontal.Left;
        }
        if (loginPassword != null) loginPassword.contentType = UnityEngine.UI.InputField.ContentType.Password;
        if (registrationPassword != null) registrationPassword.contentType = UnityEngine.UI.InputField.ContentType.Password;
        if (confirmPassword != null) confirmPassword.contentType = UnityEngine.UI.InputField.ContentType.Password;
    }
    private void OnEnable()
    {
        controller.Changed += Refresh;
        controller.LoadingProgressChanged += RefreshLoadingProgress;
        if (initialNamePanel != null) initialNamePanel.Confirmed += controller.ConfirmInitialName;
        Refresh();
    }
    private void OnDisable()
    {
        controller.Changed -= Refresh;
        controller.LoadingProgressChanged -= RefreshLoadingProgress;
        if (initialNamePanel != null) initialNamePanel.Confirmed -= controller.ConfirmInitialName;
        ClearPasswords();
    }

    public void Login()
    {
        controller.Login(Value(loginUsername), Value(loginPassword));
        ClearPasswords();
    }
    public void Register()
    {
        controller.Register(Value(registrationUsername), Value(registrationPassword), Value(confirmPassword));
        ClearPasswords();
    }
    public void EnterGame() => controller.EnterGame();
    public void SwitchAccount() { ClearPasswords(); controller.SwitchAccount(); }
    public void ShowLogin() => controller.ShowLogin();
    public void ShowRegistration() => controller.ShowRegistration();

    private void Refresh()
    {
        LoginViewState state = controller.ViewState;
        if (previousState != state)
        {
            ClearPasswords();
            if (state == LoginViewState.Registration && registrationUsername != null)
                registrationUsername.SetTextWithoutNotify(controller.SuggestedUsername);
            if (state == LoginViewState.Login && loginUsername != null)
                loginUsername.SetTextWithoutNotify(controller.SuggestedUsername);
        }
        previousState = state;
        SetPanel(loginPanel, state == LoginViewState.Login);
        SetPanel(registrationPanel, state == LoginViewState.Registration);
        SetPanel(readyPanel, state == LoginViewState.Ready);
        SetPanel(loadingPanel, state == LoginViewState.Loading);
        if (initialNamePanel != null) initialNamePanel.Present(state == LoginViewState.InitialName, controller.IsBusy);
        RefreshLoadingProgress(controller.LoadingProgress);
        if (loadingStatusText != null) loadingStatusText.text = controller.Message;
        if (usernameText != null) usernameText.text = controller.Username;
        if (uidText != null) uidText.text = controller.Uid == "" ? "" : "UID: " + controller.Uid;
        if (messageText != null) messageText.text = controller.Message;
        if (additionalMessageTexts != null)
            foreach (UnityEngine.UI.Text label in additionalMessageTexts) if (label != null) label.text = controller.Message;
        if (requestButtons != null)
            foreach (UnityEngine.UI.Button button in requestButtons) if (button != null) button.interactable = !controller.IsBusy;
    }

    private void RefreshLoadingProgress(float progress)
    {
        if (loadingProgressImage != null) loadingProgressImage.fillAmount = progress;
        if (loadingProgressText != null || loadingProgressTmpText != null)
        {
            string percentage = Mathf.FloorToInt(progress * 100f) + "%";
            if (loadingProgressText != null && loadingProgressText.text != percentage) loadingProgressText.text = percentage;
            if (loadingProgressTmpText != null && loadingProgressTmpText.text != percentage) loadingProgressTmpText.text = percentage;
        }
    }

    private void ClearPasswords()
    {
        if (loginPassword != null) loginPassword.SetTextWithoutNotify("");
        if (registrationPassword != null) registrationPassword.SetTextWithoutNotify("");
        if (confirmPassword != null) confirmPassword.SetTextWithoutNotify("");
    }
    private static string Value(UnityEngine.UI.InputField input) => input != null ? input.text : "";
    private void SetPanel(GameObject panel, bool visible)
    {
        // Never let a mistaken binding disable the controller and interrupt its own flow.
        if (panel != null && !transform.IsChildOf(panel.transform) && panel.activeSelf != visible) panel.SetActive(visible);
    }
}
