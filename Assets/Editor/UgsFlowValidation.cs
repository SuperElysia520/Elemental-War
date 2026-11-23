using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using FPS.UI;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Unity.Services.Authentication;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Exercises the real client and authored scenes with an in-memory UGS transport only.</summary>
[InitializeOnLoad]
public static class UgsFlowValidation
{
    private const string Prefix = "FPS.UgsFlowValidation.";
    private const string BackendEnvironmentKey = "FPS_ACCOUNT_BACKEND";
    private const string FirstUser = "abc", SecondUser = "ugs_validation_b";
    private const string TestPassword = "Validation9!Pass";
    private static readonly string Output = Path.GetFullPath(Path.Combine(Application.dataPath, "../tmp/UgsIntegration/runtime-validation.json"));
    private static readonly FieldInfo FactoryField = typeof(AccountClient).GetField("UgsGatewayFactory", BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly List<FakeGateway> Gateways = new List<FakeGateway>();
    private static IEnumerator probe;
    private static readonly Stack<IEnumerator> probeSteps = new Stack<IEnumerator>();
    private static object originalFactory;
    private static bool factoryInstalled, runtimeCleaned;
    private static Report report;
    private static FakeStore store;
    private static FakeGateway gateway;
    private static AccountClient account;

    [Serializable] private sealed class Report
    {
        public string scope = "Real AccountClient, Login, GameLobby and GameLobby UI; fake UGS transport; no online services.";
        public string startedUtc;
        public bool success, restored, factoryRestored, fakeGatewaysDisposed;
        public List<string> checks = new List<string>();
        public List<string> errors = new List<string>();
        public List<string> baselineErrors = new List<string>();
    }
    [Serializable] private sealed class SceneSnapshot { public SceneRecord[] scenes; }
    [Serializable] private sealed class SceneRecord { public string path; public bool loaded, active; }
    private static bool Running => SessionState.GetBool(Prefix + "running", false);
    private static string Stage { get => SessionState.GetString(Prefix + "stage", ""); set => SessionState.SetString(Prefix + "stage", value); }

    static UgsFlowValidation()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += PlayModeChanged;
        Application.logMessageReceived += Log;
        if (Running && Stage == "testing")
            EditorApplication.delayCall += () => Complete("验证期间脚本重新加载，测试已停止。");
    }

    [MenuItem("Tools/FPS/UGS Backend/Validate Runtime Flow (Offline Fake)")]
    public static void Run()
    {
        if (Running || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
            throw new InvalidOperationException("请等待编辑器退出 Play Mode 并完成编译。");
        if (FactoryField == null) throw new InvalidOperationException("找不到 UGS 测试边界，未开始验证。");
        SceneSetup[] setup = EditorSceneManager.GetSceneManagerSetup();
        var snapshot = new SceneSnapshot { scenes = new SceneRecord[setup.Length] };
        for (int i = 0; i < setup.Length; i++)
        {
            if (string.IsNullOrEmpty(setup[i].path) || SceneManager.GetSceneByPath(setup[i].path).isDirty)
                throw new InvalidOperationException("请先保存当前打开的场景；验证不会保存或丢弃场景修改。");
            snapshot.scenes[i] = new SceneRecord { path = setup[i].path, loaded = setup[i].isLoaded, active = setup[i].isActive };
        }
        string loginPath = EnabledScenePath(GameFlowScenes.Login);
        EnabledScenePath(GameFlowScenes.MainMenu);
        EnabledScenePath(GameFlowScenes.MultiplayerLobby);
        var settings = Resources.Load<AccountServiceSettings>("AccountServiceSettings");
        if (settings == null) throw new InvalidOperationException("缺少 AccountServiceSettings；未开始验证。");
        SessionState.SetString(Prefix + "scenes", JsonUtility.ToJson(snapshot));
        SessionState.SetInt(Prefix + "backend", (int)settings.backend);
        SessionState.SetBool(Prefix + "hadBackendEnvironment", Environment.GetEnvironmentVariable(BackendEnvironmentKey) != null);
        SessionState.SetString(Prefix + "backendEnvironment", Environment.GetEnvironmentVariable(BackendEnvironmentKey) ?? "");
        SessionState.SetBool(Prefix + "runInBackground", Application.runInBackground);
        SessionState.SetBool(Prefix + "paused", EditorApplication.isPaused);
        SessionState.SetString(Prefix + "start", DateTime.UtcNow.Ticks.ToString());
        report = new Report { startedUtc = DateTime.UtcNow.ToString("O") };
        Store();
        Stage = "starting";
        SessionState.SetBool(Prefix + "running", true);
        try
        {
            Environment.SetEnvironmentVariable(BackendEnvironmentKey, "UnityGamingServices");
            EditorSceneManager.OpenScene(loginPath, OpenSceneMode.Single);
            EditorApplication.isPaused = false;
            EditorApplication.isPlaying = true;
        }
        catch (Exception error) { Complete(error.Message); }
    }

    private static string EnabledScenePath(string name)
    {
        string path = null;
        foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
            if (scene.enabled && Path.GetFileNameWithoutExtension(scene.path) == name)
            {
                if (path != null) throw new InvalidOperationException("Build Settings 中有重复 " + name + " 场景。");
                path = scene.path;
            }
        if (path == null || !File.Exists(path)) throw new InvalidOperationException("请将正式 " + name + " 场景加入 Build Settings。");
        return path;
    }

    private static void PlayModeChanged(PlayModeStateChange state)
    {
        if (!Running) return;
        if (state == PlayModeStateChange.EnteredPlayMode) Stage = "entered";
        if (state != PlayModeStateChange.ExitingPlayMode) return;
        if (Stage != "stopping") AddError("Play Mode 被提前停止。");
        try { CleanupRuntime(); }
        finally { Stage = "stopping"; Store(); }
    }

    private static void Tick()
    {
        if (!Running) return;
        if (!EditorApplication.isPlayingOrWillChangePlaymode && !Application.isPlaying && !EditorApplication.isCompiling && Stage == "stopping")
        { Restore(); return; }
        if (long.TryParse(SessionState.GetString(Prefix + "start", "0"), out long start) &&
            DateTime.UtcNow.Ticks - start > TimeSpan.FromSeconds(90).Ticks)
        { Complete("UGS 验证超时（阶段：" + Stage + "）。"); return; }
        try
        {
            if (EditorApplication.isPlaying && Stage == "entered")
            {
                runtimeCleaned = false;
                originalFactory = FactoryField.GetValue(null);
                store = new FakeStore();
                Gateways.Clear();
                FactoryField.SetValue(null, new Func<IUgsAccountGateway>(() =>
                {
                    gateway = new FakeGateway(store);
                    Gateways.Add(gateway);
                    return gateway;
                }));
                factoryInstalled = true;
                Application.runInBackground = true;
                foreach (EditorWindow window in Resources.FindObjectsOfTypeAll<EditorWindow>())
                    if (window.GetType().Name == "GameView") { window.Focus(); break; }
                probe = Probe();
                probeSteps.Clear();
                probeSteps.Push(probe);
                Stage = "testing";
            }
            // Every yield is one editor tick. The runner survives destroying the AccountClient.
            if (EditorApplication.isPlaying && Stage == "testing" && !AdvanceProbe()) Complete(null);
        }
        catch (Exception error) { Complete(error.Message); }
    }

    private static IEnumerator Probe()
    {
        yield return null;
        Check(AccountUid.IsLegacyRegistration(AccountUid.EnabledAtUtc.AddTicks(-1)) && !AccountUid.IsLegacyRegistration(AccountUid.EnabledAtUtc),
            "启用时刻之前的账号保持原状，之后的账号使用数字 UID");
        Check(AccountUid.Format(0, "original-id") == "original-id" && AccountUid.Format(AccountUid.First, "original-id") == "111100001",
            "旧账号原 UID 与新账号九位 UID 格式兼容");
        account = AccountClient.EnsureInstance();
        var flow = UnityEngine.Object.FindObjectOfType<LoginSceneController>();
        Check(flow != null && account.UsesUgs && !account.SupportsRegistrationLookup && !account.HasSession && account.Profile == null,
            "真实 Login 场景选择 UGS 且没有预先创建会话或读取存档");
        var ui = flow.GetComponent<LoginUI>();
        var progressImage = ui.loadingProgressImage;
        var progressText = ui.loadingProgressText;
        var progressTmpText = ui.loadingProgressTmpText;
        Check(ui != null && progressImage != null &&
            progressImage.type == UnityEngine.UI.Image.Type.Filled && progressImage.fillMethod == UnityEngine.UI.Image.FillMethod.Horizontal &&
            progressImage.fillOrigin == (int)UnityEngine.UI.Image.OriginHorizontal.Left,
            "真实 LoadingPanel 已绑定水平从左填充的 Image 和百分比文字");
        Check(!ui.loadingPanel.activeSelf && flow.LoadingProgress == 0f && progressImage.fillAmount == 0f && (progressText == null || progressText.text == "0%"),
            "Login 启动隐藏 LoadingPanel，加载进度重置为 0%");
        flow.EnterGame();
        Check(flow.ViewState == LoginViewState.Login && !flow.IsBusy && !ui.loadingPanel.activeSelf,
            "未登录点击进入游戏不会开始加载或打开 LoadingPanel");
        var registrationUsername = SceneComponent<UnityEngine.UI.InputField>("Canvas/RegistrationPanel/UserName/InputField");
        var registrationPassword = SceneComponent<UnityEngine.UI.InputField>("Canvas/RegistrationPanel/UserPassword/InputField");
        var confirmationPassword = SceneComponent<UnityEngine.UI.InputField>("Canvas/RegistrationPanel/UserConfirmPassword/InputField");
        var registrationButton = SceneComponent<UnityEngine.UI.Button>("Canvas/RegistrationPanel/RegistrationBtn");
        Check(ui != null && ui.registrationUsername == registrationUsername && ui.registrationPassword == registrationPassword && ui.confirmPassword == confirmationPassword,
            "真实 LoginUI 绑定注册面板三个旧版 InputField，未读取隐藏登录面板");
        bool registrationIsBound = false;
        for (int i = 0; i < registrationButton.onClick.GetPersistentEventCount(); i++)
            if (registrationButton.onClick.GetPersistentTarget(i) == ui && registrationButton.onClick.GetPersistentMethodName(i) == "Register")
                registrationIsBound = true;
        Check(registrationIsBound, "真实注册按钮的持久事件调用当前 LoginUI.Register");
        flow.enterGameAfterLogin = false;
        int registrationRequired = 0;
        flow.onRegistrationRequired.AddListener(_ => registrationRequired++);
        flow.Register(FirstUser, TestPassword, "different");
        Check(!flow.IsBusy && flow.Message.Contains("不一致") && Gateways.Count == 0, "确认密码错误时不调用注册服务");
        flow.Register("中文", TestPassword, TestPassword);
        Check(!flow.IsBusy && Gateways.Count == 0, "不符合 UGS 规则的用户名在提交前被拒绝");
        flow.Register(FirstUser, "short", "short");
        Check(!flow.IsBusy && Gateways.Count == 0, "不符合 UGS 规则的注册密码在提交前被拒绝");

        // Create the injected gateway before pausing registration, without initializing any UGS SDK.
        gateway = null;
        var authGate = new TaskCompletionSource<bool>();
        store.NextRegistrationGate = authGate;
        registrationUsername.SetTextWithoutNotify(FirstUser);
        registrationPassword.SetTextWithoutNotify(TestPassword);
        confirmationPassword.SetTextWithoutNotify(TestPassword);
        registrationButton.onClick.Invoke();
        Check(flow.IsBusy && gateway != null && gateway.SignUpCalls == 1 && gateway.PendingSignUpUsername == FirstUser,
            "真实注册按钮提交合法三字符 abc，注册进行中显示 busy");
        Check(registrationUsername.text == FirstUser && registrationPassword.text.Length == 0 && confirmationPassword.text.Length == 0,
            "提交后仅清空密码框，注册用户名 abc 保留");
        flow.Register(SecondUser, TestPassword, TestPassword);
        flow.Login(SecondUser, TestPassword);
        Check(gateway.SignUpCalls == 1 && gateway.SignInCalls == 0 && flow.SuggestedUsername == FirstUser,
            "注册期间重复点击注册或登录不会发出另一请求");
        authGate.SetResult(true);
        while (flow.IsBusy) yield return null;
        Check(flow.ViewState == LoginViewState.Login && !account.HasSession && account.Profile == null && !gateway.IsSignedIn && gateway.SignOutCalls > 0,
            "注册成功退出 SDK 会话并返回 Login");
        Check(gateway.LastSignUpUsername == FirstUser && gateway.LastSignUpPasswordMatchesExpected,
            "UI 清空密码后，在途注册仍使用提交瞬间的用户名和密码副本");
        Check(gateway.LoadCalls == 0 && store.SavedProfiles == 0, "注册既不读取角色档案也不创建云存档");
        flow.Register(FirstUser, TestPassword, TestPassword);
        while (flow.IsBusy) yield return null;
        Check(flow.ViewState == LoginViewState.Registration && flow.Message.Contains("已被使用") && !gateway.IsSignedIn,
            "重名注册显示明确错误并清理注册会话");
        bool registeredByPublicApi = false;
        account.Register("ugs_public_api", TestPassword, (ok, error) => registeredByPublicApi = ok);
        while (account.IsBusy) yield return null;
        Check(registeredByPublicApi && !account.HasSession && !gateway.IsSignedIn && account.Profile == null && gateway.LoadCalls == 0 && store.SavedProfiles == 0,
            "公开 Register 入口也只注册并退出，不隐式登录或读取存档");

        gateway.NextSignUpError = AuthenticationException.Create(AuthenticationErrorCodes.InvalidParameters, "synthetic");
        registrationUsername.SetTextWithoutNotify(FirstUser);
        registrationPassword.SetTextWithoutNotify(TestPassword);
        confirmationPassword.SetTextWithoutNotify(TestPassword);
        ui.Register();
        while (flow.IsBusy) yield return null;
        Check(flow.ViewState == LoginViewState.Registration && flow.Message.Contains("10002") &&
            !flow.Message.Contains(UgsCredentialRules.UsernameHelp) && !flow.Message.Contains(UgsCredentialRules.PasswordHelp) &&
            !account.HasSession && !gateway.IsSignedIn && account.Profile == null && gateway.LoadCalls == 0 && store.SavedProfiles == 0,
            "UGS InvalidParameters 显示参数错误码，不把合法 abc 误报为用户名规则错误或拼接两段规则");

        gateway.NextSignUpError = AuthenticationException.Create(AuthenticationErrorCodes.InvalidParameters, "UsernamePassword provider is not enabled");
        registrationUsername.SetTextWithoutNotify(FirstUser);
        registrationPassword.SetTextWithoutNotify(TestPassword);
        confirmationPassword.SetTextWithoutNotify(TestPassword);
        ui.Register();
        while (flow.IsBusy) yield return null;
        Check(flow.ViewState == LoginViewState.Registration && (flow.Message.Contains("启用") || flow.Message.Contains("开通")) &&
            flow.Message.Contains("控制台") && flow.Message.Contains("用户名密码") && !flow.Message.Contains(UgsCredentialRules.UsernameHelp) &&
            !account.HasSession && !gateway.IsSignedIn && account.Profile == null && store.SavedProfiles == 0,
            "合成提供方未启用错误优先提示开通 Username Password，不误报用户名格式");

        int signInsBeforeEmptyPassword = gateway.SignInCalls;
        ui.loginUsername.SetTextWithoutNotify(FirstUser);
        ui.loginPassword.SetTextWithoutNotify("");
        ui.Login();
        Check(flow.ViewState == LoginViewState.Login && flow.Message.Contains("密码") && !flow.Message.Contains(UgsCredentialRules.UsernameHelp) &&
            gateway.SignInCalls == signInsBeforeEmptyPassword && !flow.IsBusy,
            "合法 abc 配空登录密码只提示密码问题，且不提交登录请求");

        int registrations = gateway.SignUpCalls;
        flow.Login(FirstUser, "wrong-password");
        while (flow.IsBusy) yield return null;
        Check(flow.ViewState == LoginViewState.Login && registrationRequired == 0 && !account.HasSession && gateway.SignUpCalls == registrations,
            "密码错误保留登录页面，不推断未注册或自动注册");
        gateway.NextSignInError = new InvalidOperationException("Simulated transport failure");
        flow.Login(FirstUser, TestPassword);
        while (flow.IsBusy) yield return null;
        Check(flow.ViewState == LoginViewState.Login && registrationRequired == 0 && gateway.SignUpCalls == registrations && !account.IsBusy,
            "登录传输失败不会误报未注册且解除 busy");

        authGate = gateway.HoldSignIn();
        int signIns = gateway.SignInCalls;
        flow.Login(FirstUser, TestPassword);
        bool rejectedDuplicate = false;
        account.LoginSession(SecondUser, TestPassword, (ok, error) => rejectedDuplicate = !ok && !string.IsNullOrEmpty(error));
        flow.Login(SecondUser, TestPassword);
        Check(rejectedDuplicate && gateway.SignInCalls == signIns + 1 && flow.SuggestedUsername == FirstUser,
            "真实 AccountClient 和 Login 控制器都阻止并发登录");
        authGate.SetResult(true);
        while (flow.IsBusy) yield return null;
        string firstId = account.AccountId;
        Check(flow.ViewState == LoginViewState.Ready && account.HasSession && !account.IsAuthenticated && account.Profile == null && gateway.LoadCalls == 0,
            "LoginSession 只建立身份会话，点击进入游戏后才在 Login 读档");
        int firstUid = account.PublicUid;
        Check(firstUid == AccountUid.First && flow.Uid == "111100001" && ui.uidText.text == "UID: 111100001" && account.AccountId != flow.Uid,
            "登录立即显示九位数字 UID，内部认证 Player ID 保持独立");
        bool callback = false, success = false;
        account.SaveProfile(CharacterAppearanceData.Default, PvpLoadoutData.Default, (ok, error) => { callback = true; success = ok; });
        Check(callback && !success && gateway.SaveCalls == 0, "未载入档案前不能提交角色保存");

        bool profileLoadedInLogin = false;
        Action<PlayerProfileData> observe = p => { if (p != null && SceneManager.GetActiveScene().name == GameFlowScenes.Login) profileLoadedInLogin = true; };
        account.ProfileChanged += observe;
        var loadGate = gateway.HoldLoad();
        float lastProgress = 0f;
        bool validProgress = true, progressUiMatches = true, completedBeforeActivation = false;
        int progressUpdates = 0;
        Action<float> loadingChanged = value =>
        {
            progressUpdates++;
            validProgress &= value >= lastProgress && value >= 0f && value <= 1f;
            progressUiMatches &= ui.loadingPanel.activeInHierarchy && Mathf.Approximately(progressImage.fillAmount, value) &&
                (progressText == null || progressText.text == Mathf.FloorToInt(value * 100f) + "%") &&
                (progressTmpText == null || progressTmpText.text == Mathf.FloorToInt(value * 100f) + "%");
            lastProgress = value;
            if (value == 1f) completedBeforeActivation = SceneManager.GetActiveScene().name == GameFlowScenes.Login && flow.IsBusy;
        };
        flow.LoadingProgressChanged += loadingChanged;
        float savedTimeScale = Time.timeScale;
        try
        {
            Time.timeScale = 0f;
            flow.EnterGame();
            Check(flow.IsBusy && flow.ViewState == LoginViewState.Loading && ui.loadingPanel.activeInHierarchy &&
                !ui.loginPanel.activeSelf && !ui.readyPanel.activeSelf && flow.LoadingProgress == 0f && (progressText == null || progressText.text == "0%"),
                "进入游戏立即显示 LoadingPanel 和 0%，隐藏登录及账号面板");
            flow.EnterGame();
            flow.SwitchAccount();
            flow.ShowLogin();
            Check(flow.IsBusy && flow.ViewState == LoginViewState.Loading && account.HasSession,
                "加载期间重复进入、切换账号及返回表单不会打断加载");
            while (gateway.LoadCalls == 0) yield return null;
            Check(SceneManager.GetActiveScene().name == GameFlowScenes.Login && account.Profile == null && account.IsBusy &&
                ui.loadingStatusText != null && ui.loadingStatusText.text == "正在读取用户数据…",
                "云档案返回前停留在 Login LoadingPanel，Status 显示读取状态");
            loadGate.SetResult(store.Load(firstId));
            while (SceneManager.GetActiveScene().name != GameFlowScenes.MainMenu) yield return null;
        }
        finally
        {
            Time.timeScale = savedTimeScale;
            if (flow != null) flow.LoadingProgressChanged -= loadingChanged;
        }
        Check(progressUpdates > 0 && validProgress && lastProgress == 1f && progressUiMatches,
            "真实异步场景加载进度单调到达 100%，Image 和百分比文字同步更新");
        Check(completedBeforeActivation, "暂停时间仍可异步加载，Login 显示 100% 后才激活 GameLobby");
        GameStartProfileLoader loader = LoginSceneBootstrap.FindLoader(SceneManager.GetActiveScene());
        while (!loader.IsReady) yield return null;
        Check(account.IsAuthenticated && account.Profile.accountId == firstId && profileLoadedInLogin && gateway.LastLoadScene == GameFlowScenes.Login && gateway.LoadCalls == 1,
            "Login 读取并应用当前玩家档案后才进入 GameLobby，大厅不重复读档");
        account.ProfileChanged -= observe;
        Check(store.SavedProfiles == 0, "首次默认档案读取不写入存储");

        string ticket = null, ticketError = "";
        gateway.PvpReplyBody = JsonConvert.SerializeObject(new { ticket = new string('a', 64), expiresAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60 });
        account.RequestGameTicket((value, error) => { ticket = value; ticketError = error; });
        while (account.IsBusy) yield return null;
        Check(ticket == new string('a', 64) && ticketError == "" && gateway.LastPvpOperation == "game-ticket", "UGS 普通专服与配装同步请求通过云脚本取得短票据");
        ticket = null;
        account.RequestMatchedGameTicket((value, error) => { ticket = value; ticketError = error; });
        while (account.IsBusy) yield return null;
        Check(ticket == new string('a', 64) && gateway.LastPvpOperation == "ticket", "UGS 匹配票据与普通房间票据使用不同操作");
        gateway.PvpReplyBody = "{\"party\":null,\"message\":\"\"}";
        bool partyReturned = false;
        long partyStatus = 0;
        string partyError = "";
        yield return account.PartyRequest("POST", "/api/party/create", "{}", (body, status, error) =>
        { partyReturned = true; partyStatus = status; partyError = error; });
        Check(partyReturned && partyStatus == 200 && partyError == "" && gateway.LastPvpOperation == "create", "UGS 组队请求沿用现有响应合同并路由到云网关");
        yield return account.PartyRequest("POST", "/api/party/join", "{\"partyId\":\"abcdef\"}", (body, status, error) => partyError = error);
        Check(gateway.LastPvpOperation == "join" && (string)gateway.LastPvpArgs["partyId"] == "abcdef", "加入队伍只传队伍标识，不传玩家身份或档案");
        yield return account.PartyRequest("POST", "/api/party/ready", "{\"ready\":true}", (body, status, error) => partyError = error);
        Check((bool)gateway.LastPvpArgs["ready"] && gateway.LastPvpUsername == FirstUser, "准备请求保留布尔类型和当前账号显示名");
        foreach (string command in new[] { "queue", "cancel", "leave", "fail" })
        {
            yield return account.PartyRequest("POST", "/api/party/" + command, null, (body, status, error) => partyError = error);
            Check(gateway.LastPvpOperation == command && partyError == "", "UGS 队伍操作路由 " + command);
        }
        yield return account.PartyRequest("GET", "/api/party", null, (body, status, error) => partyError = error);
        Check(gateway.LastPvpOperation == "current", "队伍心跳走 UGS 当前队伍读取");
        yield return account.PartyRequest("GET", "/api/party/rooms", null, (body, status, error) => partyError = error);
        Check(gateway.LastPvpOperation == "rooms", "房间列表走 UGS 读取");
        int pvpCalls = gateway.PvpCalls;
        yield return account.PartyRequest("POST", "/api/party/join", "{\"playerId\":\"spoof\"}", (body, status, error) => partyStatus = status);
        Check(partyStatus == 400 && gateway.PvpCalls == pvpCalls, "伪造身份参数在客户端边界拒绝且不调用网关");
        gateway.PvpStatus = 409; gateway.PvpReplyBody = "{\"code\":\"party_full\",\"message\":\"队伍已满。\"}";
        yield return account.PartyRequest("POST", "/api/party/join", "{\"partyId\":\"abcdef\"}", (body, status, error) => { partyStatus = status; partyError = error; });
        Check(partyStatus == 409 && partyError == "队伍已满。" && account.HasSession, "业务拒绝保留 UGS 会话并显示队伍原因");
        gateway.PvpStatus = 200; gateway.PvpReplyBody = "{\"ticket\":\"offline-validation-token\",\"expiresAtUnixSeconds\":9999999999}";
        account.RequestGameTicket((value, error) => { ticket = value; ticketError = error; });
        while (account.IsBusy) yield return null;
        Check(ticket == null && ticketError != "" && account.HasSession, "网关不能把长期令牌或非64位短票据送入游戏连接");
        gateway.NextPvpError = new UgsAccountException("雨云匹配桥接服务未就绪，请稍后重试。");
        yield return account.PartyRequest("GET", "/api/party", null, (body, status, error) => partyError = error);
        Check(partyError.Contains("桥接") && account.HasSession && !account.IsBusy, "桥接离线不会清除已成功登录的 UGS 身份");
        var stalePartyGate = gateway.HoldPvp();
        bool stalePartyReturned = false;
        account.StartCoroutine(account.PartyRequest("GET", "/api/party", null, (body, status, error) => stalePartyReturned = true));
        yield return null;
        callback = false; account.Logout((ok, error) => callback = ok);
        while (!callback) yield return null;
        callback = false; account.LoginSession(FirstUser, TestPassword, (ok, error) => callback = ok);
        while (!callback) yield return null;
        callback = false; account.RefreshProfile((ok, error) => callback = ok);
        while (!callback) yield return null;
        stalePartyGate.SetException(new UgsAccountException("synthetic stale unauthorized", true));
        while (!stalePartyReturned) yield return null;
        Check(account.HasSession && account.Profile != null && account.Profile.accountId == firstId,
            "旧登录代次的迟到队伍认证失败不会清除重新登录后的会话");
        var pvpGate = gateway.HoldPvp();
        account.RequestGameTicket((value, error) => { ticket = value; ticketError = error; });
        Check(account.IsBusy, "入场票据在途时阻止重复账号修改");
        gateway.Expire();
        pvpGate.SetResult(new UgsPvpReply { ok = true, state = "done", status = 200,
            body = JsonConvert.SerializeObject(new { ticket = new string('b', 64), expiresAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60 }) });
        while (account.IsBusy) yield return null;
        Check(ticket == null && ticketError != "" && !account.HasSession, "过期会话的迟到票据不可进入对局并释放 busy");
        callback = false; account.LoginSession(FirstUser, TestPassword, (ok, error) => callback = ok);
        while (!callback) yield return null;
        callback = false; account.RefreshProfile((ok, error) => callback = ok);
        while (!callback) yield return null;

        var appearance = CharacterAppearanceData.Default;
        appearance.faceId = 3;
        appearance.clothColorId = 4;
        var loadout = PvpLoadoutData.Default;
        loadout.primaryWeaponId = 2;
        loadout.primarySightId = 5;
        var saveGate = gateway.HoldSave();
        callback = success = false;
        account.SaveProfile(appearance, loadout, (ok, error) => { callback = true; success = ok; });
        rejectedDuplicate = false;
        int saves = gateway.SaveCalls;
        account.SaveProfile(CharacterAppearanceData.Default, PvpLoadoutData.Default, (ok, error) => rejectedDuplicate = !ok);
        Check(account.IsBusy && rejectedDuplicate && gateway.SaveCalls == saves && account.Profile.appearance.faceId == 0,
            "保存期间阻止重复提交，返回前不乐观覆盖当前档案");
        saveGate.SetResult(true);
        while (!callback) yield return null;
        Check(success && account.Profile.appearance.Equals(appearance) && account.Profile.loadout.Equals(loadout) && account.Profile.revision == 1 && store.SavedProfiles == 1,
            "外观和装备保存返回后应用，并创建首次持久档案",
            "success=" + success + "; revision=" + account.Profile?.revision + "; avatar=" + account.Profile?.avatarId +
            "; face=" + account.Profile?.appearance.faceId + "; loadout=" + JsonConvert.SerializeObject(account.Profile?.loadout) +
            "; savedProfiles=" + store.SavedProfiles + "; LastError=" + account.LastError);
        callback = success = false;
        account.SaveAvatar(7, (ok, error) => { callback = true; success = ok; });
        while (!callback) yield return null;
        Check(success && account.Profile.avatarId == 7 && account.Profile.appearance.Equals(appearance) && account.Profile.revision == 2,
            "保存头像保留外观装备并更新档案版本");
        int avatars = gateway.AvatarCalls;
        callback = success = false;
        account.SaveAvatar(32, (ok, error) => { callback = true; success = ok; });
        Check(callback && !success && gateway.AvatarCalls == avatars && !account.IsBusy, "非法头像编号在客户端拒绝且不写入云端");

        PlayerProfileData good = Clone(account.Profile);
        gateway.NextSaveError = new UgsAccountException("档案已在其他设备更新，请重新读取后再保存。");
        callback = success = false;
        account.SaveProfile(CharacterAppearanceData.Default, PvpLoadoutData.Default, (ok, error) => { callback = true; success = ok; });
        while (!callback) yield return null;
        Check(!success && account.HasSession && SameProfile(account.Profile, good) && !account.IsBusy,
            "版本冲突保留已读取档案和有效会话，解除 busy");
        gateway.NextLoadError = new InvalidOperationException("Simulated load failure");
        callback = success = false;
        account.RefreshProfile((ok, error) => { callback = true; success = ok; });
        while (!callback) yield return null;
        Check(!success && account.HasSession && SameProfile(account.Profile, good), "普通读档错误保留当前档案供重试");

        var badProfiles = new List<PlayerProfileData> { null };
        var bad = Clone(good); bad.accountId = "another-player"; badProfiles.Add(bad);
        bad = Clone(good); bad.revision = -1; badProfiles.Add(bad);
        bad = Clone(good); bad.username = "中文"; badProfiles.Add(bad);
        bad = Clone(good); bad.avatarId = 255; badProfiles.Add(bad);
        bad = Clone(good); bad.appearance.faceId = 255; badProfiles.Add(bad);
        bad = Clone(good); bad.loadout.primaryWeaponId = 1; badProfiles.Add(bad);
        bad = Clone(good); bad.match = new MatchAdmissionData { matchId = "legacy-ticket-data" }; badProfiles.Add(bad);
        bad = Clone(good); bad.uid = 0; badProfiles.Add(bad);
        bad = Clone(good); bad.uid = AccountUid.Last; badProfiles.Add(bad);
        string[] corruptions = { "空响应", "其他账号 ID", "负版本号", "非法用户名", "非法头像", "非法外观", "非法配装", "旧专服票据数据", "缺失数字 UID", "其他账号数字 UID" };
        for (int i = 0; i < badProfiles.Count; i++)
        {
            gateway.OverrideNextLoad = true;
            gateway.NextLoadResult = badProfiles[i];
            callback = success = false;
            account.RefreshProfile((ok, error) => { callback = true; success = ok; });
            while (!callback) yield return null;
            Check(!success && account.HasSession && SameProfile(account.Profile, good) && !account.IsBusy,
                "云档案包含" + corruptions[i] + "时拒绝应用并保留原档案");
        }

        // Session expiry above already requests Login through LobbyController.
        // Await that transition instead of queuing a second Login load.
        while (SceneManager.GetActiveScene().name != GameFlowScenes.Login) yield return null;
        yield return null;
        flow = UnityEngine.Object.FindObjectOfType<LoginSceneController>();
        ui = flow.GetComponent<LoginUI>();
        Check(flow.LoadingProgress == 0f && ui.loadingProgressImage.fillAmount == 0f && (ui.loadingProgressText == null || ui.loadingProgressText.text == "0%") &&
            !ui.loadingPanel.activeSelf, "返回 Login 后隐藏 LoadingPanel 并重置进度，支持再次进入");
        flow.SwitchAccount();
        while (flow.IsBusy) yield return null;
        Check(!account.HasSession && account.Profile == null && account.AccountId == "" && !gateway.IsSignedIn,
            "切换账号同时清空身份、令牌和角色档案");
        flow.enterGameAfterLogin = false;
        int loads = gateway.LoadCalls;
        flow.Login(FirstUser, TestPassword);
        while (flow.IsBusy) yield return null;
        Check(account.HasSession && account.Profile == null && gateway.LoadCalls == loads, "重登等待点击进入游戏后在 Login 读取档案");
        flow.EnterGame();
        double reentryDeadline = EditorApplication.timeSinceStartup + 10;
        while (SceneManager.GetActiveScene().name != GameFlowScenes.MainMenu)
        {
            if (flow == null) throw new InvalidOperationException("Reentry Login controller was replaced before GameLobby activation.");
            if (!flow.IsBusy || EditorApplication.timeSinceStartup > reentryDeadline)
                throw new InvalidOperationException("Reentry stopped in Login: " + flow.ViewState + "; " + flow.Message + "; profileLoads=" + gateway.LoadCalls);
            yield return null;
        }
        loader = LoginSceneBootstrap.FindLoader(SceneManager.GetActiveScene());
        while (!loader.IsReady) yield return null;
        Check(SameProfile(account.Profile, good), "重登后加载之前保存的外观、装备、头像和版本");
        Check(account.PublicUid == firstUid && account.Profile.uid == firstUid, "重新登录和读档不会改变已经分配的数字 UID");
        loader.ReturnToLogin();
        while (SceneManager.GetActiveScene().name != GameFlowScenes.Login) yield return null;
        yield return null;
        flow = UnityEngine.Object.FindObjectOfType<LoginSceneController>();
        flow.SwitchAccount();
        while (flow.IsBusy) yield return null;
        loads = gateway.LoadCalls;
        flow.Register(SecondUser, TestPassword, TestPassword);
        while (flow.IsBusy) yield return null;
        Check(gateway.LoadCalls == loads && !account.HasSession && store.SavedProfiles == 1,
            "第二个账号注册不预读或复制第一个账号的存档");
        bool correctLoginOrder = false;
        flow.onLoginSucceeded.AddListener(_ => correctLoginOrder = account.Profile == null && SceneManager.GetActiveScene().name == GameFlowScenes.Login);
        flow.enterGameAfterLogin = true;
        flow.Login(SecondUser, TestPassword);
        while (SceneManager.GetActiveScene().name != GameFlowScenes.MainMenu) yield return null;
        loader = LoginSceneBootstrap.FindLoader(SceneManager.GetActiveScene());
        while (!loader.IsReady) yield return null;
        string secondId = account.AccountId;
        Check(account.PublicUid == AccountUid.First + 2 && account.Profile.uid == account.PublicUid,
            "注册时分配连续数字 UID，未登录过的中间账号也占用自己的号码");
        Check(correctLoginOrder && secondId != firstId && account.Profile.appearance.faceId == 0 && account.Profile.avatarId == 0 && account.Profile.revision == 0,
            "另一账号在 Login 加载自己的默认档案后自动进入 GameLobby");
        callback = success = false;
        account.SaveAvatar(9, (ok, error) => { callback = true; success = ok; });
        while (!callback) yield return null;
        Check(success && store.Load(firstId).avatarId == 7 && store.Load(secondId).avatarId == 9 && store.SavedProfiles == 2,
            "不同账号的云存档独立，头像保存不会覆盖其他玩家");
        loader.ReturnToLogin();
        while (SceneManager.GetActiveScene().name != GameFlowScenes.Login) yield return null;
        yield return null;
        flow = UnityEngine.Object.FindObjectOfType<LoginSceneController>();
        flow.SwitchAccount();
        while (flow.IsBusy) yield return null;
        flow.enterGameAfterLogin = false;
        flow.Login(FirstUser, TestPassword);
        while (flow.IsBusy) yield return null;
        callback = false;
        account.RefreshProfile((ok, error) => { callback = true; success = ok; });
        while (!callback) yield return null;
        Check(success && SameProfile(account.Profile, good), "切回原账号重新读取自己的持久档案");

        loadGate = gateway.HoldLoad();
        int lateCallbacks = 0;
        bool lateSuccess = true;
        account.RefreshProfile((ok, error) => { lateCallbacks++; lateSuccess = ok; });
        gateway.Expire();
        Check(!account.HasSession && account.Profile == null && account.AccountId == "", "Expired 事件立即清除当前身份和角色数据");
        loadGate.SetResult(Clone(good));
        while (account.IsBusy) yield return null;
        Check(lateCallbacks == 1 && !lateSuccess && !account.HasSession && account.Profile == null && flow.ViewState == LoginViewState.Login,
            "会话过期后迟到的读档结果无效，只失败回调一次并释放 busy");

        callback = false;
        account.LoginSession(FirstUser, TestPassword, (ok, error) => callback = ok);
        while (!callback) yield return null;
        callback = false;
        account.RefreshProfile((ok, error) => callback = ok);
        while (!callback) yield return null;
        saveGate = gateway.HoldSave();
        appearance.faceId = 4;
        lateCallbacks = 0;
        account.SaveProfile(appearance, loadout, (ok, error) => { lateCallbacks++; lateSuccess = ok; });
        gateway.Expire();
        saveGate.SetResult(true);
        while (account.IsBusy) yield return null;
        Check(lateCallbacks == 1 && !lateSuccess && account.Profile == null && !account.HasSession,
            "会话过期后迟到的保存响应也不能恢复已退出的身份或档案");

        callback = false;
        account.LoginSession(FirstUser, TestPassword, (ok, error) => callback = ok);
        while (!callback) yield return null;
        callback = false;
        account.RefreshProfile((ok, error) => callback = ok);
        while (!callback) yield return null;
        var avatarGate = gateway.HoldAvatar();
        lateCallbacks = 0;
        account.SaveAvatar(11, (ok, error) => { lateCallbacks++; lateSuccess = ok; });
        gateway.Expire();
        avatarGate.SetResult(true);
        while (account.IsBusy) yield return null;
        Check(lateCallbacks == 1 && !lateSuccess && account.Profile == null && !account.HasSession,
            "会话过期后迟到的头像响应无效并释放 busy");

        callback = false;
        account.LoginSession(FirstUser, TestPassword, (ok, error) => callback = ok);
        while (!callback) yield return null;
        callback = false;
        account.RefreshProfile((ok, error) => callback = ok);
        while (!callback) yield return null;
        gateway.NextLoadError = new UgsAccountException("登录已失效，请重新登录。", true);
        callback = false;
        account.RefreshProfile((ok, error) => { callback = true; success = ok; });
        while (!callback) yield return null;
        Check(!success && !account.HasSession && account.Profile == null && !account.IsBusy,
            "服务端明确返回失效会话时清除身份，而不是当普通网络错误保留");

        callback = false;
        account.LoginSession(FirstUser, TestPassword, (ok, error) => callback = ok);
        while (!callback) yield return null;
        var destroyedAccount = account;
        var destroyedGateway = gateway;
        loadGate = gateway.HoldLoad();
        lateCallbacks = 0;
        int stateEvents = 0, profileEvents = 0;
        destroyedAccount.StateChanged += () => stateEvents++;
        destroyedAccount.ProfileChanged += _ => profileEvents++;
        destroyedAccount.RefreshProfile((ok, error) => lateCallbacks++);
        UnityEngine.Object.Destroy(destroyedAccount.gameObject);
        yield return null;
        Check(destroyedGateway.DisposeCalls == 1, "销毁 AccountClient 会释放网关并退订过期通知");
        int statesAfterDestroy = stateEvents, profilesAfterDestroy = profileEvents;
        account = AccountClient.EnsureInstance();
        callback = false;
        account.LoginSession(SecondUser, TestPassword, (ok, error) => callback = ok);
        while (!callback) yield return null;
        loadGate.SetResult(store.Load(firstId));
        destroyedGateway.Expire();
        yield return null;
        yield return null;
        Check(lateCallbacks == 0 && stateEvents == statesAfterDestroy && profileEvents == profilesAfterDestroy,
            "已销毁客户端的迟到读档响应和过期事件不触发回调或 UI 事件");
        Check(account.HasSession && account.AccountId == secondId && account.Profile == null && !account.IsBusy,
            "旧客户端迟到的 A 档案不会污染新客户端的 B 会话");

        account.Logout();
        destroyedAccount = account;
        destroyedGateway = gateway;
        authGate = gateway.HoldSignIn();
        lateCallbacks = 0;
        destroyedAccount.LoginSession(FirstUser, TestPassword, (ok, error) => lateCallbacks++);
        UnityEngine.Object.Destroy(destroyedAccount.gameObject);
        yield return null;
        account = AccountClient.EnsureInstance();
        callback = false;
        account.LoginSession(SecondUser, TestPassword, (ok, error) => callback = ok);
        while (!callback) yield return null;
        authGate.SetResult(true);
        yield return null;
        yield return null;
        Check(lateCallbacks == 0 && destroyedGateway.DisposeCalls == 1 && !destroyedGateway.IsSignedIn && account.AccountId == secondId,
            "销毁期间迟到的登录完成不会调用旧回调或更改新客户端的身份");
        callback = false;
        account.RefreshProfile((ok, error) => callback = ok);
        while (!callback) yield return null;
        Check(account.Profile.accountId == secondId && account.Profile.avatarId == 9,
            "客户端重建后 B 的持久存档仍能正常读取");

        destroyedAccount = account;
        destroyedGateway = gateway;
        loadGate = gateway.HoldLoad();
        lateCallbacks = 0;
        account.RefreshProfile((ok, error) => lateCallbacks++);
        loads = gateway.LoadCalls;
        SceneManager.LoadSceneAsync(GameFlowScenes.MainMenu);
        while (SceneManager.GetActiveScene().name != GameFlowScenes.MainMenu) yield return null;
        loader = LoginSceneBootstrap.FindLoader(SceneManager.GetActiveScene());
        yield return null;
        Check(loader != null && loader.IsLoading && !loader.IsReady && gateway.LoadCalls == loads,
            "GameLobby 等待已有在途账号请求，不同时发起第二次读取");
        UnityEngine.Object.Destroy(destroyedAccount.gameObject);
        yield return null;
        while (SceneManager.GetActiveScene().name != GameFlowScenes.Login) yield return null;
        loadGate.SetResult(store.Load(secondId));
        yield return null;
        account = AccountClient.EnsureInstance();
        Check(lateCallbacks == 0 && destroyedGateway.DisposeCalls == 1 && !account.HasSession && !account.IsBusy,
            "GameLobby 等待期间客户端被销毁会返回 Login，不永久卡住或应用旧响应");
        yield return ValidatePartyCreationUi();
        LoginSceneBootstrap.ReturnToLogin();
        while (SceneManager.GetActiveScene().name != GameFlowScenes.Login) yield return null;
        yield return null;
        flow = UnityEngine.Object.FindObjectOfType<LoginSceneController>();
        flow.SwitchAccount();
        while (flow.IsBusy) yield return null;
        var legacy = store.Register("legacy_uid", TestPassword); legacy.uid = 0;
        flow.enterGameAfterLogin = false;
        flow.Login("legacy_uid", TestPassword);
        while (flow.IsBusy) yield return null;
        Check(account.HasSession && gateway.HasLegacyUid && account.PublicUid == 0 && flow.Uid == account.AccountId,
            "既有账号保持原 UID，登录时不要求补号");
        flow.EnterGame();
        while (SceneManager.GetActiveScene().name != GameFlowScenes.MainMenu) yield return null;
        var legacyRoot = UnityEngine.Object.FindObjectOfType<LobbyUIRoot>();
        while (legacyRoot.Controller == null) yield return null;
        legacyRoot.Controller.Execute(LobbyAction.Multiplayer);
        string legacyLabel = legacyRoot.homeView != null ? legacyRoot.homeView.uid.text : legacyRoot.identity.uid != null ? legacyRoot.identity.uid.text : legacyRoot.identity.legacyUid.text;
        Check(account.IsAuthenticated && account.Profile.uid == 0 && legacyLabel == "UID: " + account.AccountId,
            "既有账号正常进入大厅，保留原显示身份与角色档案");
    }

    private static IEnumerator ValidatePartyCreationUi()
    {
        var flow = UnityEngine.Object.FindObjectOfType<LoginSceneController>();
        flow.enterGameAfterLogin = false;
        flow.Login(FirstUser, TestPassword);
        while (flow.IsBusy) yield return null;
        flow.EnterGame();
        while (SceneManager.GetActiveScene().name != GameFlowScenes.MainMenu) yield return null;
        var loader = LoginSceneBootstrap.FindLoader(SceneManager.GetActiveScene());
        while (!loader.IsReady) yield return null;
        gateway.PvpReplyBody = "{\"party\":null,\"message\":\"\"}";
        var root = UnityEngine.Object.FindObjectOfType<LobbyUIRoot>();
        while (root.Controller == null) yield return null;
        if (root.homeView == null)
            root.pages.First(page => page.page == LobbyPage.MainMenu).buttons.First(binding => binding.action == LobbyAction.Multiplayer).button.onClick.Invoke();
        while (SceneManager.GetActiveScene().name != GameFlowScenes.MultiplayerLobby) yield return null;
        yield return null;
        yield return null;
        var home = root.pages.First(page => page.page == LobbyPage.Home);
        var startButton = home.buttons.First(binding => binding.action == LobbyAction.CreateParty).button;
        var party = PartyLobbyClient.EnsureInstance();
        Func<bool> oldPartyHidden = () => root.homeView != null || !root.partyView.gameObject.activeSelf;
        Func<bool> squadVisible = () => root.homeView != null ? home.gameObject.activeInHierarchy : root.partyView.gameObject.activeInHierarchy;
        var primaryButton = root.homeView != null ? root.homeView.primaryButton : root.partyView.primaryButton;
        Func<string> primaryText = () => root.homeView != null ? root.homeView.primaryLabel.text : root.partyView.primaryLabel.text;
        Check(home != null && (root.homeView != null || root.partyView != null) && startButton != null &&
            home.status != null && home.gameObject.activeInHierarchy &&
            oldPartyHidden() && account.IsAuthenticated && party.Party == null,
            "真实 GameLobby 显示已登录首页，并绑定组队控件");
        TaskCompletionSource<UgsPvpReply> createGate = null;
        try
        {
            createGate = gateway.HoldPartyCreate();
            startButton.onClick.Invoke();
            while (gateway.PvpCreateCalls == 0) yield return null;
            yield return null;
            Check(party.IsBusy && home.gameObject.activeInHierarchy && oldPartyHidden() &&
                !startButton.IsInteractable() && home.status.text.Contains("正在创建队伍"),
                "延迟建队期间保留首页和创建提示，禁用开始按钮且不闪现空房间",
                "home=" + home.gameObject.activeInHierarchy + "; separatePanel=" + !oldPartyHidden() +
                "; busy=" + party.IsBusy + "; startEnabled=" + startButton.IsInteractable() +
                "; creatingHint=" + home.status.text.Contains("正在创建队伍"));
            int creates = gateway.PvpCreateCalls;
            startButton.onClick.Invoke();
            root.Controller.Execute(LobbyAction.CreateParty);
            Check(gateway.PvpCreateCalls == creates && party.IsBusy && home.gameObject.activeInHierarchy,
                "建队请求在途时重复点击不会重复提交或切换到空面板");
            const string failure = "合成建队失败，请稍后重试。";
            createGate.SetException(new UgsAccountException(failure));
            while (party.IsBusy) yield return null;
            yield return null;
            Check(home.gameObject.activeInHierarchy && oldPartyHidden() && party.Party == null &&
                home.status.text == failure && startButton.IsInteractable() && account.IsAuthenticated,
                "延迟建队失败后留在首页显示真实原因并恢复按钮，保留登录身份");
            int polls = gateway.PvpCurrentCalls;
            while (gateway.PvpCurrentCalls == polls) yield return null;
            yield return null;
            Check(home.gameObject.activeInHierarchy && home.status.text == failure && party.Party == null,
                "失败后的真实队伍轮询不会覆盖首页错误提示或再次闪现房间");

            createGate = gateway.HoldPartyCreate();
            startButton.onClick.Invoke();
            while (gateway.PvpCreateCalls == creates) yield return null;
            createGate.SetResult(new UgsPvpReply { ok = true, state = "done", status = 200, body = gateway.PvpReplyBody });
            while (party.IsBusy) yield return null;
            yield return null;
            Check(home.gameObject.activeInHierarchy && oldPartyHidden() && party.Party == null &&
                home.status.text.Contains("创建队伍未成功"),
                "建队返回成功状态但没有队伍时仍留首页并明确提示未成功");

            createGate = gateway.HoldPartyCreate();
            startButton.onClick.Invoke();
            while (gateway.PvpCreateCalls < creates + 2) yield return null;
            Check(home.gameObject.activeInHierarchy && oldPartyHidden() &&
                home.status.text.Contains("正在创建队伍") && !startButton.IsInteractable(),
                "失败后重试重新显示创建中状态，不保留上次错误或提前打开房间");
            var state = new PartyStateData { party = new PartyData {
                id = "0123456789abcdef0123456789abcdef", name = "团队对抗", mapName = "测试地图1",
                ownerAccountId = account.AccountId, state = "Waiting", match = null,
                members = new[] { new PartyMemberData { profile = Clone(account.Profile), ready = true } }
            }, message = "" };
            gateway.PvpReplyBody = JsonConvert.SerializeObject(state);
            createGate.SetResult(new UgsPvpReply { ok = true, state = "done", status = 200, body = gateway.PvpReplyBody });
            while (party.IsBusy) yield return null;
            yield return null;
            Check(squadVisible() && party.Party != null &&
                party.Party.state == "Waiting" && party.IsOwner && party.LocalReady && primaryButton.interactable &&
                primaryText() == "开始匹配",
                "建队成功后更新组队控件，保留队长准备及开始匹配状态");
            state.party.state = "Matching";
            gateway.PvpReplyBody = JsonConvert.SerializeObject(state);
            primaryButton.onClick.Invoke();
            while (party.IsBusy) yield return null;
            Check(party.Party.state == "Matching" && party.LocalReady && primaryText() == "取消匹配" &&
                squadVisible(),
                "成功建队后的原有匹配按钮和准备状态保持正常");
            gateway.PvpReplyBody = "{\"party\":null,\"message\":\"\"}";
            (root.homeView != null ? (LobbyPageView)root.homeView : root.partyView).buttons.First(binding => binding.action == LobbyAction.LeaveParty).button.onClick.Invoke();
            while (party.IsBusy) yield return null;
            Check(party.Party == null && home.gameObject.activeInHierarchy && oldPartyHidden(),
                "验证结束通过真实解散按钮返回首页并清理测试队伍");
        }
        finally { createGate?.TrySetCanceled(); }
    }

    private static bool AdvanceProbe()
    {
        while (probeSteps.Count > 0)
        {
            IEnumerator step = probeSteps.Peek();
            if (!step.MoveNext()) { (probeSteps.Pop() as IDisposable)?.Dispose(); continue; }
            if (step.Current is IEnumerator nested) probeSteps.Push(nested);
            return true;
        }
        return false;
    }

    private static T SceneComponent<T>(string path) where T : Component
    {
        int separator = path.IndexOf('/');
        string rootName = separator < 0 ? path : path.Substring(0, separator);
        foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
        {
            if (root.name != rootName) continue;
            Transform target = separator < 0 ? root.transform : root.transform.Find(path.Substring(separator + 1));
            T component = target != null ? target.GetComponent<T>() : null;
            if (component != null) return component;
        }
        throw new InvalidOperationException("验证需要的场景组件不存在：" + path + " (" + typeof(T).Name + ")。");
    }

    private static PlayerProfileData Clone(PlayerProfileData value) => value == null ? null : JsonConvert.DeserializeObject<PlayerProfileData>(JsonConvert.SerializeObject(value));
    private static bool SameProfile(PlayerProfileData left, PlayerProfileData right) => JsonConvert.SerializeObject(left) == JsonConvert.SerializeObject(right);
    private static void Check(bool condition, string label, string details = null)
    {
        if (!condition) throw new InvalidOperationException(label + "失败。" + (details == null ? "" : " " + details));
        Read().checks.Add(label);
        Store();
    }
    private static void Complete(string error)
    {
        if (!Running || Stage == "stopping") return;
        if (error != null) AddError(error);
        Stage = "stopping";
        try { CleanupRuntime(); }
        catch (Exception cleanupError) { AddError("运行态清理失败：" + cleanupError.Message); }
        finally
        {
            Read().success = Read().errors.Count == 0;
            try { Store(); }
            finally { EditorApplication.delayCall += EditorApplication.ExitPlaymode; }
        }
    }
    private static void CleanupRuntime()
    {
        if (runtimeCleaned) return;
        runtimeCleaned = true;
        try
        {
            while (probeSteps.Count > 0) (probeSteps.Pop() as IDisposable)?.Dispose();
            probe = null;
            if (EditorApplication.isPlaying && account != null) UnityEngine.Object.DestroyImmediate(account.gameObject);
        }
        finally
        {
            try
            {
                foreach (FakeGateway fake in Gateways) fake.Dispose();
                Read().fakeGatewaysDisposed = Gateways.TrueForAll(fake => fake.DisposeCalls == 1);
                if (factoryInstalled)
                {
                    FactoryField.SetValue(null, originalFactory);
                    Read().factoryRestored = ReferenceEquals(FactoryField.GetValue(null), originalFactory);
                }
            }
            finally
            {
                factoryInstalled = false;
                Gateways.Clear();
                gateway = null;
                account = null;
                store = null;
                Application.runInBackground = SessionState.GetBool(Prefix + "runInBackground", false);
            }
        }
    }
    private static void Restore()
    {
        bool restored = false;
        try
        {
            Environment.SetEnvironmentVariable(BackendEnvironmentKey,
                SessionState.GetBool(Prefix + "hadBackendEnvironment", false) ? SessionState.GetString(Prefix + "backendEnvironment", "") : null);
            Application.runInBackground = SessionState.GetBool(Prefix + "runInBackground", false);
            EditorApplication.isPaused = SessionState.GetBool(Prefix + "paused", false);
            var settings = Resources.Load<AccountServiceSettings>("AccountServiceSettings");
            if (settings == null) throw new InvalidOperationException("恢复时找不到账号配置。");
            var snapshot = JsonUtility.FromJson<SceneSnapshot>(SessionState.GetString(Prefix + "scenes", "{}"));
            var setup = new SceneSetup[snapshot.scenes.Length];
            for (int i = 0; i < setup.Length; i++)
                setup[i] = new SceneSetup { path = snapshot.scenes[i].path, isLoaded = snapshot.scenes[i].loaded, isActive = snapshot.scenes[i].active };
            EditorSceneManager.RestoreSceneManagerSetup(setup);
            SceneSetup[] actual = EditorSceneManager.GetSceneManagerSetup();
            bool scenesRestored = actual.Length == setup.Length;
            for (int i = 0; scenesRestored && i < setup.Length; i++)
                scenesRestored = actual[i].path == setup[i].path && actual[i].isLoaded == setup[i].isLoaded && actual[i].isActive == setup[i].isActive;
            string expectedEnvironment = SessionState.GetBool(Prefix + "hadBackendEnvironment", false) ? SessionState.GetString(Prefix + "backendEnvironment", "") : null;
            restored = settings.backend == (AccountBackend)SessionState.GetInt(Prefix + "backend", 0) &&
                Application.runInBackground == SessionState.GetBool(Prefix + "runInBackground", false) && scenesRestored &&
                Environment.GetEnvironmentVariable(BackendEnvironmentKey) == expectedEnvironment;
            if (!restored) AddError("恢复后场景、账号配置、环境变量或后台运行设置与原状态不一致。");
        }
        catch (Exception error) { AddError("编辑器状态恢复失败：" + error.Message); }
        finally
        {
            Read().restored = restored;
            Read().success = restored && Read().factoryRestored && Read().fakeGatewaysDisposed && Read().errors.Count == 0;
            Store();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Output));
                File.WriteAllText(Output, JsonUtility.ToJson(Read(), true));
            }
            catch (Exception error)
            {
                Read().success = false;
                AddError("无法写入验证报告：" + error.Message);
                Debug.LogError("[UgsFlowValidation] 无法写入验证报告，结果保留在 Editor SessionState。");
            }
            finally
            {
                SessionState.SetBool(Prefix + "running", false);
                Stage = "done";
            }
        }
        Debug.Log("[UgsFlowValidation] " + (Read().success ? "PASS" : "FAILED") + "：" + Read().checks.Count + " 项检查；" + Output);
    }
    private static void Log(string message, string trace, LogType type)
    {
        if (Running && Stage != "stopping" && (type == LogType.Error || type == LogType.Exception || type == LogType.Assert))
        {
            bool known = trace.Contains("Unity.VisualScripting.Codebase.IncludeInSettings") || trace.Contains("Unity.VisualScripting.XmlDocumentation.BackgroundWork") || message.Contains("Failed to late initialize plugin module 'VisualScripting.Core.BoltCoreConfiguration'");
            if (known) { if (!Read().baselineErrors.Contains("Existing Visual Scripting initialization issue")) Read().baselineErrors.Add("Existing Visual Scripting initialization issue"); Store(); return; }
            AddError("运行时出现 " + type + "（详情见 Unity Console，报告不复制玩家数据）。");
        }
    }
    private static void AddError(string error) { Read().errors.Add(error); Store(); }
    private static Report Read() => report ?? (report = JsonUtility.FromJson<Report>(SessionState.GetString(Prefix + "report", "{}")));
    private static void Store()
    {
        string json = JsonUtility.ToJson(Read());
        SessionState.SetString(Prefix + "report", json);
        Directory.CreateDirectory(Path.GetDirectoryName(Output));
        // Progress is optional; a simultaneous reader must not prevent Play Mode cleanup.
        try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(Output), "runtime-progress.json"), json); }
        catch (IOException) { }
    }

    private sealed class FakeStore
    {
        internal sealed class Player { public string id, username, password; public int uid; public PlayerProfileData profile; }
        private readonly Dictionary<string, Player> users = new Dictionary<string, Player>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Player> ids = new Dictionary<string, Player>();
        public TaskCompletionSource<bool> NextRegistrationGate;
        public int SavedProfiles { get { int count = 0; foreach (Player player in ids.Values) if (player.profile != null) count++; return count; } }
        public Player Register(string username, string password)
        {
            if (users.ContainsKey(username)) throw new UgsAccountException("该用户名已被使用，请换一个用户名，或返回登录。");
            var player = new Player { id = "offline-player-" + (users.Count + 1), uid = AccountUid.First + users.Count, username = username, password = password };
            users.Add(username, player);
            ids.Add(player.id, player);
            return player;
        }
        public Player SignIn(string username, string password)
        {
            if (!users.TryGetValue(username, out Player player) || player.password != password)
                throw new UgsAccountException("账号或密码错误；没有账号请先注册。");
            return player;
        }
        public PlayerProfileData Load(string id)
        {
            Player player = ids[id];
            return Clone(player.profile) ?? new PlayerProfileData { accountId = id, uid = player.uid, username = player.username, personal = new PersonalProfileData { displayName = "已有名称" } };
        }
        public PlayerProfileData Save(string id, int revision, CharacterAppearanceData? appearance, PvpLoadoutData? loadout, byte? avatar)
        {
            PlayerProfileData next = Load(id);
            if (next.revision != revision) throw new UgsAccountException("档案版本已更新，请重新读取。");
            if (appearance.HasValue) next.appearance = appearance.Value;
            if (loadout.HasValue) next.loadout = loadout.Value;
            if (avatar.HasValue) next.avatarId = avatar.Value;
            next.revision++;
            ids[id].profile = Clone(next);
            return Clone(next);
        }
    }

    private sealed class FakeGateway : IUgsAccountGateway, IUgsPvpGateway
    {
        private readonly FakeStore data;
        private bool disposed;
        private string playerId = "";
        private TaskCompletionSource<bool> signInGate, saveGate, avatarGate;
        private TaskCompletionSource<PlayerProfileData> loadGate;
        private TaskCompletionSource<UgsPvpReply> pvpGate, partyCreateGate;
        public int PvpCalls, PvpCreateCalls, PvpCurrentCalls, PvpStatus = 200;
        public string LastPvpOperation, LastPvpUsername, PvpReplyBody = "{}";
        public Dictionary<string, object> LastPvpArgs;
        public Exception NextPvpError;
        public int SignUpCalls, SignInCalls, SignOutCalls, LoadCalls, SaveCalls, AvatarCalls, DisposeCalls;
        public Exception NextSignUpError, NextSignInError, NextLoadError, NextSaveError;
        public string PendingSignUpUsername, LastSignUpUsername;
        public bool LastSignUpPasswordMatchesExpected;
        public bool OverrideNextLoad;
        public PlayerProfileData NextLoadResult;
        public string LastLoadScene;
        public bool IsSignedIn { get; private set; }
        public string PlayerId => IsSignedIn ? playerId : "";
        public string AccessToken => IsSignedIn ? "offline-validation-token" : "";
        public int PublicUid => IsSignedIn ? data.Load(playerId).uid : 0;
        public bool HasLegacyUid => IsSignedIn && PublicUid == 0;
        public event Action SessionExpired;
        public FakeGateway(FakeStore data) { this.data = data; }
        public Task InitializeAsync() => Task.CompletedTask;
        public Task<UgsPvpReply> ExecutePvpAsync(string operation, string username, Dictionary<string, object> args)
        {
            PvpCalls++; LastPvpOperation = operation; LastPvpUsername = username; LastPvpArgs = args ?? new Dictionary<string, object>();
            if (operation == "current") PvpCurrentCalls++;
            if (operation == "create")
            {
                PvpCreateCalls++;
                if (partyCreateGate != null) { var delay = partyCreateGate; partyCreateGate = null; return delay.Task; }
            }
            if (pvpGate != null) { var delay = pvpGate; pvpGate = null; return delay.Task; }
            if (NextPvpError != null) { var error = NextPvpError; NextPvpError = null; return Task.FromException<UgsPvpReply>(error); }
            return Task.FromResult(new UgsPvpReply { ok = true, state = "done", body = PvpReplyBody, status = PvpStatus });
        }
        public TaskCompletionSource<UgsPvpReply> HoldPvp() => pvpGate = new TaskCompletionSource<UgsPvpReply>();
        public TaskCompletionSource<UgsPvpReply> HoldPartyCreate() => partyCreateGate = new TaskCompletionSource<UgsPvpReply>();
        public async Task SignUpAsync(string username, string password)
        {
            SignUpCalls++;
            PendingSignUpUsername = username;
            var delay = data.NextRegistrationGate;
            data.NextRegistrationGate = null;
            if (delay != null) await delay.Task;
            if (disposed) throw new OperationCanceledException();
            LastSignUpUsername = username;
            LastSignUpPasswordMatchesExpected = password == TestPassword;
            if (NextSignUpError != null) { Exception error = NextSignUpError; NextSignUpError = null; throw error; }
            playerId = data.Register(username, password).id;
            IsSignedIn = true;
        }
        public async Task SignInAsync(string username, string password)
        {
            SignInCalls++;
            var delay = signInGate; signInGate = null;
            if (delay != null) await delay.Task;
            if (disposed) throw new OperationCanceledException();
            if (NextSignInError != null) { Exception error = NextSignInError; NextSignInError = null; throw error; }
            playerId = data.SignIn(username, password).id;
            IsSignedIn = true;
        }
        public void SignOut() { SignOutCalls++; IsSignedIn = false; playerId = ""; }
        public Task<PlayerProfileData> LoadProfileAsync(string username)
        {
            LoadCalls++;
            LastLoadScene = SceneManager.GetActiveScene().name;
            if (loadGate != null) { var delay = loadGate; loadGate = null; return delay.Task; }
            if (NextLoadError != null) { Exception error = NextLoadError; NextLoadError = null; return Task.FromException<PlayerProfileData>(error); }
            if (OverrideNextLoad) { OverrideNextLoad = false; return Task.FromResult(Clone(NextLoadResult)); }
            return Task.FromResult(data.Load(PlayerId));
        }
        public async Task<PlayerProfileData> SaveProfileAsync(string username, int revision, CharacterAppearanceData appearance, PvpLoadoutData loadout)
        {
            SaveCalls++;
            string id = PlayerId;
            var delay = saveGate; saveGate = null;
            if (delay != null) await delay.Task;
            if (NextSaveError != null) { Exception error = NextSaveError; NextSaveError = null; throw error; }
            return data.Save(id, revision, appearance, loadout, null);
        }
        public async Task<PlayerProfileData> SaveAvatarAsync(string username, int revision, byte avatarId)
        {
            AvatarCalls++;
            string id = PlayerId;
            var delay = avatarGate; avatarGate = null;
            if (delay != null) await delay.Task;
            return data.Save(id, revision, null, null, avatarId);
        }
        public TaskCompletionSource<bool> HoldSignIn() => signInGate = new TaskCompletionSource<bool>();
        public TaskCompletionSource<PlayerProfileData> HoldLoad() => loadGate = new TaskCompletionSource<PlayerProfileData>();
        public TaskCompletionSource<bool> HoldSave() => saveGate = new TaskCompletionSource<bool>();
        public TaskCompletionSource<bool> HoldAvatar() => avatarGate = new TaskCompletionSource<bool>();
        public void Expire() { IsSignedIn = false; playerId = ""; SessionExpired?.Invoke(); }
        public void Dispose() { if (disposed) return; disposed = true; DisposeCalls++; SignOut(); }
    }
}
