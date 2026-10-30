using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using FPS.UI;

/// <summary>Real Login → GameLobby HTTP smoke test against an isolated local service, without authoring UI.</summary>
[InitializeOnLoad]
public static class LoginFlowValidation
{
    private const string Prefix = "FPS.LoginFlowValidation.";
    private const string EnvironmentKey = "FPS_ACCOUNT_CLIENT_URL";
    private const string Endpoint = "http://127.0.0.1:5097";
    private static readonly string Output = Path.GetFullPath(Path.Combine(Application.dataPath, "../tmp/LoginFlow/runtime-validation.json"));
    [Serializable] private sealed class Report
    {
        public bool success, restored;
        public List<string> checks = new List<string>();
        public List<string> errors = new List<string>();
    }
    private static Report report;
    private static bool Running => SessionState.GetBool(Prefix + "running", false);
    private static string Stage { get => SessionState.GetString(Prefix + "stage", ""); set => SessionState.SetString(Prefix + "stage", value); }
    static LoginFlowValidation()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += PlayModeChanged;
        Application.logMessageReceived += Log;
        if (Running && Stage == "testing") EditorApplication.delayCall += () => Complete("验证中发生重新编译。");
    }
    [MenuItem("Tools/FPS/Login/Validate Runtime Flow (Local Service 5097)")]
    public static void Run()
    {
        if (Running || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
            throw new InvalidOperationException("请等待编辑器停止运行并完成编译。");
        Scene scene = SceneManager.GetActiveScene();
        if (scene.name != GameFlowScenes.Login || scene.isDirty || SceneManager.sceneCount != 1)
            throw new InvalidOperationException("请先保存并打开唯一的 Login 场景。");
        GameFlowBuildScenes.Get(false);
        SessionState.SetBool(Prefix + "hadEnvironment", Environment.GetEnvironmentVariable(EnvironmentKey) != null);
        SessionState.SetString(Prefix + "backend", Environment.GetEnvironmentVariable("FPS_ACCOUNT_BACKEND") ?? "");
        SessionState.SetBool(Prefix + "hadBackend", Environment.GetEnvironmentVariable("FPS_ACCOUNT_BACKEND") != null);
        SessionState.SetString(Prefix + "environment", Environment.GetEnvironmentVariable(EnvironmentKey) ?? "");
        SessionState.SetInt(Prefix + "http", (int)PlayerSettings.insecureHttpOption);
        SessionState.SetString(Prefix + "start", DateTime.UtcNow.Ticks.ToString());
        report = new Report();
        Store();
        Stage = "starting";
        SessionState.SetBool(Prefix + "running", true);
        Environment.SetEnvironmentVariable(EnvironmentKey, Endpoint);
        Environment.SetEnvironmentVariable("FPS_ACCOUNT_BACKEND", "SelfHosted");
        PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
        EditorApplication.isPlaying = true;
    }
    private static void PlayModeChanged(PlayModeStateChange state)
    {
        if (!Running) return;
        if (state == PlayModeStateChange.EnteredPlayMode) Stage = "entered";
        if (state == PlayModeStateChange.ExitingPlayMode)
        {
            if (Stage != "stopping") AddError("Play Mode 被提前停止。");
            Stage = "stopping";
        }
    }
    private static void Tick()
    {
        if (!Running) return;
        if (!EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling && Stage == "stopping")
        { Restore(); return; }
        if (long.TryParse(SessionState.GetString(Prefix + "start", "0"), out long ticks) && DateTime.UtcNow.Ticks - ticks > TimeSpan.FromSeconds(90).Ticks)
        { Complete("登录流程验证超时（阶段：" + Stage + "）。"); return; }
        if (EditorApplication.isPlaying && Stage == "entered")
        {
            Stage = "testing";
            // The Editor can lose focus to the automation client while the scene coroutine is running.
            SessionState.SetBool(Prefix + "runInBackground", Application.runInBackground);
            Application.runInBackground = true;
            foreach (EditorWindow window in Resources.FindObjectsOfTypeAll<EditorWindow>())
                if (window.GetType().Name == "GameView") { window.Focus(); break; }
            AccountClient.EnsureInstance().StartCoroutine(Protect());
        }
    }
    private static IEnumerator Protect()
    {
        IEnumerator probe = Probe();
        while (Running && Stage == "testing")
        {
            bool more = false;
            object next = null;
            string error = null;
            try { more = probe.MoveNext(); if (more) next = probe.Current; }
            catch (Exception exception) { error = exception.Message; }
            if (error != null) { Complete(error); yield break; }
            if (!more) yield break;
            yield return next;
        }
    }
    private static IEnumerator Probe()
    {
        yield return null;
        AccountClient account = AccountClient.EnsureInstance();
        LoginSceneController flow = UnityEngine.Object.FindObjectOfType<LoginSceneController>();
        Check(flow != null && !account.HasSession && account.Profile == null, "Login 启动且未绑定 UI 引用时正常运行");
        string username = "login_" + Guid.NewGuid().ToString("N").Substring(0, 14);
        string password = Guid.NewGuid().ToString("N") + "Aa1!";
        flow.Login(username, password);
        Check(flow.IsBusy, "登录检查期间锁定重复提交");
        flow.Login("IgnoredUser", password);
        Check(flow.SuggestedUsername == username, "重复提交不会覆盖正在处理的账号");
        while (flow.IsBusy) yield return null;
        Check(flow.ViewState == LoginViewState.Registration && !account.HasSession, "未注册账号进入注册状态而不自动创建账号");
        flow.Register(username, password, "different");
        Check(!flow.IsBusy && flow.Message.Contains("不一致"), "确认密码不一致时停止注册");
        flow.Register(username, password, password);
        while (flow.IsBusy) yield return null;
        Check(flow.ViewState == LoginViewState.Login && !account.HasSession && account.Profile == null, "注册完成返回登录并且未读取用户数据");
        flow.Register(username, password, password);
        while (flow.IsBusy) yield return null;
        Check(flow.ViewState == LoginViewState.Registration && flow.Message.Contains("已被使用"), "重名注册显示后端错误");
        flow.Login(username, "wrong-password");
        while (flow.IsBusy) yield return null;
        Check(flow.ViewState == LoginViewState.Login && !account.HasSession && flow.Message.Contains("不正确"), "已注册账号密码错误时保留登录状态");
        flow.enterGameAfterLogin = false;
        flow.Login(username, password);
        while (flow.IsBusy) yield return null;
        Check(flow.ViewState == LoginViewState.Ready && account.HasSession && !account.IsAuthenticated && account.Profile == null,
            "登录成功只建立会话，进入 GameLobby 前未应用云档案");
        string firstId = account.AccountId;
        Check(flow.AccountId == firstId && flow.Username == username, "Login 暴露真实 UID 和账号名");
        bool prematureProfile = false;
        Action<PlayerProfileData> observe = profile => { if (profile != null && SceneManager.GetActiveScene().name != GameFlowScenes.MainMenu) prematureProfile = true; };
        account.ProfileChanged += observe;
        flow.EnterGame();
        while (SceneManager.GetActiveScene().name != GameFlowScenes.MainMenu) yield return null;
        GameStartProfileLoader loader = LoginSceneBootstrap.FindLoader(SceneManager.GetActiveScene());
        Check(loader != null, "进入真实 GameLobby 场景并创建数据读取组件");
        while (!loader.IsReady) yield return null;
        Check(account.IsAuthenticated && account.Profile.accountId == firstId && !prematureProfile, "到达 GameLobby 后才读取当前账号档案");
        yield return new WaitForSecondsRealtime(0.3f);
        var mvc = UnityEngine.Object.FindObjectOfType<LobbyUIRoot>();
        while (mvc.Controller == null) yield return null;
        Check(mvc.Controller.Model.Page == LobbyPage.MainMenu, "读取成功后才显示主菜单");
        bool done = false, ok = false;
        CharacterAppearanceData appearance = CharacterAppearanceData.Default;
        appearance.faceId = 3;
        account.SaveProfile(appearance, PvpLoadoutData.Default, (success, error) => { done = true; ok = success; });
        while (!done) yield return null;
        Check(ok && account.Profile.appearance.faceId == 3, "当前账号的角色外观仍可保存");
        mvc.Controller.Execute(LobbyAction.Character);
        var customization = mvc.customizationView;
        Check(customization != null && customization.GetComponentsInChildren<UnityEngine.UI.InputField>(true).Length == 0,
            "角色定制界面已移除注册登录输入框");
        foreach (UnityEngine.UI.Button button in customization.GetComponentsInChildren<UnityEngine.UI.Button>(true))
        {
            var label = button.GetComponentInChildren<UnityEngine.UI.Text>();
            Check(label == null || (label.text != "登录" && label.text != "注册" && label.text != "退出账号"), "角色定制按钮只保留档案功能：" + (label != null ? label.text : ""));
        }
        mvc.Controller.Back();
        loader.ReturnToLogin();
        while (SceneManager.GetActiveScene().name != GameFlowScenes.Login) yield return null;
        yield return null;
        flow = UnityEngine.Object.FindObjectOfType<LoginSceneController>();
        Check(flow.ViewState == LoginViewState.Ready && flow.AccountId == firstId, "返回 Login 可复用本次运行的登录会话");
        Environment.SetEnvironmentVariable(EnvironmentKey, "ftp://127.0.0.1");
        flow.EnterGame();
        while (SceneManager.GetActiveScene().name != GameFlowScenes.MainMenu) yield return null;
        while (SceneManager.GetActiveScene().name != GameFlowScenes.Login) yield return null;
        yield return null;
        flow = UnityEngine.Object.FindObjectOfType<LoginSceneController>();
        Check(flow.ViewState == LoginViewState.Ready && account.HasSession && flow.Message.Contains("HTTPS"), "档案读取失败返回 Login、显示原因并保留会话供重试");
        Environment.SetEnvironmentVariable(EnvironmentKey, Endpoint);
        flow.EnterGame();
        while (SceneManager.GetActiveScene().name != GameFlowScenes.MainMenu) yield return null;
        loader = LoginSceneBootstrap.FindLoader(SceneManager.GetActiveScene());
        while (!loader.IsReady) yield return null;
        Check(account.Profile.accountId == firstId && account.Profile.appearance.faceId == 3, "重新进入后读取同一账号的已保存数据");
        loader.ReturnToLogin();
        while (SceneManager.GetActiveScene().name != GameFlowScenes.Login) yield return null;
        yield return null;
        flow = UnityEngine.Object.FindObjectOfType<LoginSceneController>();
        flow.SwitchAccount();
        while (flow.IsBusy) yield return null;
        Check(!account.HasSession && account.Profile == null && flow.AccountId == "" && flow.ViewState == LoginViewState.Login,
            "切换账号清空令牌、UID 和旧档案");
        string secondUsername = "other_" + Guid.NewGuid().ToString("N").Substring(0, 14);
        flow.Register(secondUsername, password, password);
        while (flow.IsBusy) yield return null;
        bool correctLoginOrder = false;
        flow.enterGameAfterLogin = true;
        flow.onLoginSucceeded.AddListener(id => correctLoginOrder = account.Profile == null && SceneManager.GetActiveScene().name == GameFlowScenes.Login);
        flow.Login(secondUsername, password);
        while (SceneManager.GetActiveScene().name != GameFlowScenes.MainMenu) yield return null;
        loader = LoginSceneBootstrap.FindLoader(SceneManager.GetActiveScene());
        while (!loader.IsReady) yield return null;
        Check(correctLoginOrder && account.Profile.accountId != firstId && account.Profile.appearance.faceId == 0,
            "另一个账号自动进入 GameLobby，读取自己的档案而不沿用前一账号数据");
        using (var revoke = new UnityWebRequest(Endpoint + "/api/auth/logout", "POST"))
        {
            revoke.downloadHandler = new DownloadHandlerBuffer();
            revoke.SetRequestHeader("Authorization", "Bearer " + account.AccessToken);
            yield return revoke.SendWebRequest();
            Check(revoke.responseCode == 204, "服务端单独撤销当前会话以验证失效处理");
        }
        loader.LoadUserData();
        while (SceneManager.GetActiveScene().name != GameFlowScenes.Login) yield return null;
        yield return null;
        flow = UnityEngine.Object.FindObjectOfType<LoginSceneController>();
        Check(!account.HasSession && account.Profile == null && flow.ViewState == LoginViewState.Login, "HTTP 401 清空失效账号并返回登录");
        SceneManager.LoadSceneAsync(GameFlowScenes.MainMenu);
        while (SceneManager.GetActiveScene().name != GameFlowScenes.MainMenu) yield return null;
        while (SceneManager.GetActiveScene().name != GameFlowScenes.Login) yield return null;
        Check(account.Profile == null, "未登录直接进入 GameLobby 会返回 Login");
        account.ProfileChanged -= observe;
        Complete(null);
    }
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label + "失败。");
        Read().checks.Add(label);
        Store();
    }
    private static void Complete(string error)
    {
        if (!Running || Stage == "stopping") return;
        if (error != null) AddError(error);
        Read().success = Read().errors.Count == 0;
        Store();
        if (EditorApplication.isPlaying && Stage == "testing")
            Application.runInBackground = SessionState.GetBool(Prefix + "runInBackground", false);
        Stage = "stopping";
        EditorApplication.isPlaying = false;
    }
    private static void Restore()
    {
        Environment.SetEnvironmentVariable("FPS_ACCOUNT_BACKEND", SessionState.GetBool(Prefix + "hadBackend", false) ? SessionState.GetString(Prefix + "backend", "") : null);
        Environment.SetEnvironmentVariable(EnvironmentKey, SessionState.GetBool(Prefix + "hadEnvironment", false) ? SessionState.GetString(Prefix + "environment", "") : null);
        PlayerSettings.insecureHttpOption = (InsecureHttpOption)SessionState.GetInt(Prefix + "http", 0);
        AssetDatabase.SaveAssets();
        Read().restored = true;
        Store();
        Directory.CreateDirectory(Path.GetDirectoryName(Output));
        File.WriteAllText(Output, JsonUtility.ToJson(Read(), true));
        SessionState.SetBool(Prefix + "running", false);
        Stage = "done";
        Debug.Log("[LoginFlowValidation] " + (Read().success ? "PASS" : "FAILED") + "：" + Read().checks.Count + " 项检查；" + Output);
    }
    private static void Log(string message, string trace, LogType type)
    {
        if (Running && Stage != "stopping" && (type == LogType.Error || type == LogType.Exception || type == LogType.Assert))
            AddError("运行时出现 " + type + "（详细内容见 Unity Console）。");
    }
    private static void AddError(string error) { Read().errors.Add(error); Store(); }
    private static Report Read() => report ?? (report = JsonUtility.FromJson<Report>(SessionState.GetString(Prefix + "report", "{}")));
    private static void Store() => SessionState.SetString(Prefix + "report", JsonUtility.ToJson(Read()));
}
