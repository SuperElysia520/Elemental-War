using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

/// <summary>Verify authored Login startup and ensure misbound notification callbacks cannot recurse.</summary>
[InitializeOnLoad]
public static class LoginStartupValidation
{
    private const string Prefix = "FPS.LoginStartupValidation.";
    private static readonly string Output = Path.GetFullPath(Path.Combine(Application.dataPath, "../tmp/LoginFlow/startup-validation.json"));
    [Serializable] private sealed class Report
    {
        public bool success;
        public int completedRuns;
        public List<string> checks = new List<string>();
        public List<string> errors = new List<string>();
    }
    private static Report report;
    private static bool Running => SessionState.GetBool(Prefix + "running", false);
    private static string Stage { get => SessionState.GetString(Prefix + "stage", ""); set => SessionState.SetString(Prefix + "stage", value); }
    static LoginStartupValidation()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += PlayModeChanged;
        Application.logMessageReceived += Log;
    }
    [MenuItem("Tools/FPS/Login/Validate Startup And Event Reentrancy")]
    public static void Run()
    {
        Require(!Running && !EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling, "请先停止运行并等待编译完成。");
        Scene scene = SceneManager.GetActiveScene();
        Require(SceneManager.sceneCount == 1 && scene.name == GameFlowScenes.Login && !scene.isDirty, "请先保存并打开唯一的 Login 场景。");
        report = new Report();
        Store();
        SessionState.SetBool(Prefix + "running", true);
        SessionState.SetString(Prefix + "start", DateTime.UtcNow.Ticks.ToString());
        Stage = "starting";
        EditorApplication.isPlaying = true;
    }
    /// <summary>Restart only this Editor, preserving all scenes; enable native allocation stacks for verification.</summary>
    public static void RestartWithDiagnostics() => Restart("-diag-temp-memory-leak-validation");
    public static void RestartWithoutDiagnostics() => Restart();
    private static void Restart(params string[] arguments)
    {
        Require(!Running && !EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling, "请先停止运行并等待编译完成。");
        for (int i = 0; i < SceneManager.sceneCount; i++)
            Require(!SceneManager.GetSceneAt(i).isDirty, "场景有未保存改动，未重启编辑器。");
        AssetDatabase.SaveAssets();
        EditorApplication.OpenProject(Path.GetFullPath(Path.Combine(Application.dataPath, "..")), arguments);
    }
    private static void PlayModeChanged(PlayModeStateChange state)
    {
        if (!Running) return;
        if (state == PlayModeStateChange.EnteredPlayMode)
        { Stage = "entered"; SessionState.SetFloat(Prefix + "phaseTime", (float)EditorApplication.timeSinceStartup); }
        else if (state == PlayModeStateChange.ExitingPlayMode && Stage != "stopping")
        { Error("Play Mode 被提前停止。"); Stage = "stopping"; }
    }
    private static void Tick()
    {
        if (!Running) return;
        if (long.TryParse(SessionState.GetString(Prefix + "start", "0"), out long ticks) && DateTime.UtcNow.Ticks - ticks > TimeSpan.FromMinutes(2).Ticks)
        { Error("登录启动验证超时。"); Stop(); }
        if (Stage == "entered" && EditorApplication.isPlaying && EditorApplication.timeSinceStartup - SessionState.GetFloat(Prefix + "phaseTime", 0) > 1.5)
        {
            Stage = "testing";
            try { Verify(); }
            catch (Exception exception) { Error(exception.Message); }
            Stage = "observing";
            SessionState.SetFloat(Prefix + "phaseTime", (float)EditorApplication.timeSinceStartup);
        }
        if (Stage == "observing" && EditorApplication.timeSinceStartup - SessionState.GetFloat(Prefix + "phaseTime", 0) > 1.5) Stop();
        if (Stage != "stopping" || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        Read().completedRuns++;
        Store();
        Read().success = Read().errors.Count == 0 && Read().completedRuns == 1;
        Directory.CreateDirectory(Path.GetDirectoryName(Output));
        File.WriteAllText(Output, JsonUtility.ToJson(Read(), true));
        SessionState.SetBool(Prefix + "running", false);
        Stage = "done";
        Debug.Log("[LoginStartupValidation] " + (Read().success ? "PASS" : "FAILED") + "：" + Read().completedRuns + " 次启动，" + Read().checks.Count + " 项检查；" + Output);
    }
    private static void Verify()
    {
        Require(SceneManager.GetActiveScene().name == GameFlowScenes.Login, "Login 启动场景不正确。");
        LoginSceneController flow = UnityEngine.Object.FindObjectOfType<LoginSceneController>();
        Check(flow != null && flow.ViewState == LoginViewState.Login && !flow.IsBusy, "真实 Login 场景正常启动");
        int loginNotifications = 0, registrationNotifications = 0;
        UnityAction login = () => { if (++loginNotifications < 5) flow.Login("", ""); };
        UnityAction registration = () => { if (++registrationNotifications < 5) flow.Register("test", "short", "different"); };
        UnityAction changeView = () => flow.ShowRegistration();
        flow.onShowLogin.AddListener(login);
        flow.onShowLogin.AddListener(changeView);
        flow.onShowRegistration.AddListener(registration);
        try
        {
            flow.ShowRegistration();
            Check(registrationNotifications == 1 && flow.ViewState == LoginViewState.Registration, "注册通知误绑注册操作不会重复进入通知");
            flow.ShowLogin();
            Check(loginNotifications == 1 && flow.ViewState == LoginViewState.Login, "登录通知误绑登录或面板切换操作不会递归");
            Check(flow.Message == "", "通知回调不会覆盖外层状态和提示");
            flow.Login("", "");
            Check(flow.Message.Contains("请输入") && loginNotifications == 1, "普通输入校验仍提示错误且不会再次触发显示事件");
            Check(!flow.IsBusy && !AccountClient.EnsureInstance().HasSession, "错误事件绑定不会发起账号请求或创建会话");
        }
        finally
        {
            flow.onShowLogin.RemoveListener(login);
            flow.onShowLogin.RemoveListener(changeView);
            flow.onShowRegistration.RemoveListener(registration);
        }
        int changes = 0;
        Action changed = () => { if (++changes < 5) flow.ShowRegistration(); };
        flow.Changed += changed;
        try { flow.ShowLogin(); Check(changes == 1 && flow.ViewState == LoginViewState.Login, "Changed 订阅者不能递归改变正在通知的状态"); }
        finally { flow.Changed -= changed; }
    }
    private static void Stop() { Stage = "stopping"; if (EditorApplication.isPlayingOrWillChangePlaymode) EditorApplication.isPlaying = false; }
    private static void Log(string message, string trace, LogType type)
    {
        if (!Running) return;
        if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert || message.Contains("unfreed allocations"))
            Error(type + ": " + (message.Contains("unfreed allocations") ? "临时内存未释放" : "Unity Console 出现运行错误"));
    }
    private static void Check(bool passed, string message) { Require(passed, message); Read().checks.Add(message); Store(); }
    private static void Require(bool passed, string message) { if (!passed) throw new InvalidOperationException(message); }
    private static void Error(string message) { Read().errors.Add(message); Store(); }
    private static Report Read() => report ?? (report = JsonUtility.FromJson<Report>(SessionState.GetString(Prefix + "report", "{}")));
    private static void Store() => SessionState.SetString(Prefix + "report", JsonUtility.ToJson(Read()));
}
