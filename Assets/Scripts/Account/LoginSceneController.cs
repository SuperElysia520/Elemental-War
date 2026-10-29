using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

public enum LoginViewState { Login, Registration, Ready, Loading }

[Serializable] public sealed class AccountStringEvent : UnityEvent<string> { }
[Serializable] public sealed class AccountBoolEvent : UnityEvent<bool> { }
[Serializable] public sealed class AccountProfileEvent : UnityEvent<PlayerProfileData> { }

/// <summary>Login scene flow only. UI can bind LoginUI or these methods/events without a generated panel.</summary>
[DisallowMultipleComponent]
public sealed class LoginSceneController : MonoBehaviour
{
    [Tooltip("登录成功自动进入 GameStart；关闭后显示账号信息，等待点击进入游戏。")]
    public bool enterGameAfterLogin = true;
    [Header("状态通知（显示/动画用；登录、注册等按钮请绑定 Button.onClick）")]
    public UnityEvent onShowLogin = new UnityEvent();
    public UnityEvent onShowRegistration = new UnityEvent();
    public UnityEvent onShowReady = new UnityEvent();
    public AccountStringEvent onMessageChanged = new AccountStringEvent();
    public AccountBoolEvent onBusyChanged = new AccountBoolEvent();
    public AccountStringEvent onRegistrationRequired = new AccountStringEvent();
    public AccountStringEvent onLoginSucceeded = new AccountStringEvent();

    public LoginViewState ViewState { get; private set; }
    public string Message { get; private set; } = "";
    public string SuggestedUsername { get; private set; } = "";
    public string AccountId => account != null && account.HasSession ? account.AccountId : "";
    public string Username => account != null && account.HasSession ? account.Username : "";
    public bool IsBusy => processing || (account != null && account.IsBusy);
    public event Action Changed;
    private AccountClient account;
    private bool processing;
    private bool notifyingView;
    private bool viewInitialized;

    private void Awake() => account = AccountClient.EnsureInstance();
    private void OnEnable() => account.StateChanged += Publish;
    private void OnDisable() => account.StateChanged -= Publish;
    private void Start()
    {
        Time.timeScale = 1f;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        SetView(account.HasSession ? LoginViewState.Ready : LoginViewState.Login,
            LoginSceneBootstrap.TakeMessage());
    }

    public void ShowLogin()
    {
        if (IsBusy || notifyingView) return;
        SetView(account.HasSession ? LoginViewState.Ready : LoginViewState.Login, "");
    }

    public void ShowRegistration()
    {
        if (IsBusy || notifyingView || account.HasSession) return;
        SetView(LoginViewState.Registration, "请先注册账号，再登录游戏。");
    }

    public void Login(string username, string password)
    {
        if (!CanSubmit()) return;
        username = (username ?? "").Trim();
        if (!(account.UsesUgs ? UgsCredentialRules.IsValidUsername(username) : ValidateUsername(username)))
        { SetView(LoginViewState.Login, account.UsesUgs ? UgsCredentialRules.UsernameHelp : "用户名需为 3–24 位字母、数字或下划线。"); return; }
        if (string.IsNullOrEmpty(password))
        { SetView(LoginViewState.Login, "请输入密码。"); return; }
        if (password.Length > (account.UsesUgs ? 30 : 128))
        { SetView(LoginViewState.Login, account.UsesUgs ? "密码不能超过 30 位。" : "密码不能超过 128 位。"); return; }
        SuggestedUsername = username;
        processing = true;
        if (!account.SupportsRegistrationLookup)
        {
            SubmitLogin(username, password);
            return;
        }
        SetView(LoginViewState.Login, "正在检查账号注册状态…");
        account.CheckRegistration(username, (ok, registered, error) =>
        {
            if (this == null) return;
            if (!ok) { processing = false; SetView(LoginViewState.Login, error); return; }
            if (!registered)
            {
                processing = false;
                SetView(LoginViewState.Registration, "该账号尚未注册，请先注册，再进行登录。");
                onRegistrationRequired.Invoke(username);
                return;
            }
            SubmitLogin(username, password);
        });
    }

    private void SubmitLogin(string username, string password)
    {
        SetView(LoginViewState.Login, "正在登录…");
        account.LoginSession(username, password, (success, message) =>
        {
            if (this == null) return;
            processing = false;
            if (!success) { SetView(LoginViewState.Login, message); return; }
            SetView(LoginViewState.Ready, "登录成功。");
            onLoginSucceeded.Invoke(AccountId);
            if (this != null && enterGameAfterLogin) EnterGame();
        });
    }

    public void Register(string username, string password, string confirmation)
    {
        if (!CanSubmit()) return;
        username = (username ?? "").Trim();
        if (!(account.UsesUgs ? UgsCredentialRules.IsValidUsername(username) : ValidateUsername(username)))
        { SetView(LoginViewState.Registration, account.UsesUgs ? UgsCredentialRules.UsernameHelp : "用户名需为 3–24 位字母、数字或下划线。"); return; }
        if (account.UsesUgs ? !UgsCredentialRules.IsValidPassword(password) : password == null || password.Length < 10 || password.Length > 128)
        { SetView(LoginViewState.Registration, account.UsesUgs ? UgsCredentialRules.PasswordHelp : "密码需为 10–128 个字符。"); return; }
        if (password != confirmation)
        { SetView(LoginViewState.Registration, "两次输入的密码不一致。"); return; }
        SuggestedUsername = username;
        processing = true;
        SetView(LoginViewState.Registration, "正在注册…");
        account.RegisterAccount(username, password, (ok, error) =>
        {
            if (this == null) return;
            processing = false;
            SetView(ok ? LoginViewState.Login : LoginViewState.Registration,
                ok ? "注册成功，请输入密码进行登录。" : error);
        });
    }

    public void EnterGame()
    {
        if (IsBusy || notifyingView || !IsLoginScene()) return;
        if (!account.HasSession) { SetView(LoginViewState.Login, "请先登录账号。"); return; }
        if (!GameFlowScenes.TryValidateSceneAvailable(GameFlowScenes.MainMenu, out string error))
        { SetView(LoginViewState.Ready, error); return; }
        processing = true;
        SetView(LoginViewState.Loading, "正在进入游戏…");
        SceneManager.LoadSceneAsync(GameFlowScenes.MainMenu, LoadSceneMode.Single);
    }

    public void SwitchAccount()
    {
        if (IsBusy || notifyingView || !IsLoginScene()) return;
        processing = true;
        SetView(LoginViewState.Loading, "正在退出当前账号…");
        account.Logout((ok, error) =>
        {
            if (this == null) return;
            processing = false;
            SuggestedUsername = "";
            SetView(LoginViewState.Login, ok ? "请登录其他账号。" : "本地账号已退出。" + error);
        });
    }

    private bool CanSubmit()
    {
        if (IsBusy || notifyingView || !IsLoginScene()) return false;
        if (!account.HasSession) return true;
        SetView(LoginViewState.Ready, "请先切换账号，再登录或注册其他账号。");
        return false;
    }

    private bool IsLoginScene() => gameObject.scene.name == GameFlowScenes.Login &&
        SceneManager.GetActiveScene().name == GameFlowScenes.Login;

    private static bool ValidateUsername(string username)
    {
        if (username.Length < 3 || username.Length > 24) return false;
        foreach (char c in username)
            if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '_') return false;
        return true;
    }

    private void SetView(LoginViewState state, string message)
    {
        if (notifyingView) return;
        bool changed = !viewInitialized || ViewState != state;
        viewInitialized = true;
        ViewState = state;
        Message = message ?? "";
        notifyingView = true;
        try
        {
            PublishValues();
            if (!changed) return;
            if (state == LoginViewState.Login) onShowLogin.Invoke();
            else if (state == LoginViewState.Registration) onShowRegistration.Invoke();
            else if (state == LoginViewState.Ready) onShowReady.Invoke();
        }
        finally { notifyingView = false; }
    }

    private void Publish()
    {
        if (notifyingView) return;
        if (!processing && ViewState == LoginViewState.Ready && !account.HasSession)
        { SetView(LoginViewState.Login, account.LastError); return; }
        notifyingView = true;
        try { PublishValues(); }
        finally { notifyingView = false; }
    }

    private void PublishValues()
    {
        Changed?.Invoke();
        onMessageChanged.Invoke(Message);
        onBusyChanged.Invoke(IsBusy);
    }
}
