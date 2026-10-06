using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>在已保存的 GameStart 中执行真实本地账号与展示验证；不创建或载入大厅。</summary>
[InitializeOnLoad]
public static class MainMenuFlowValidation
{
    private const string ScenePath = "Assets/Scenes/GameStart.unity";
    private const string EnvironmentKey = "FPS_ACCOUNT_CLIENT_URL";
    private const string Prefix = "FPS.MainMenuFlowValidation.";
    private const string Endpoint = "http://127.0.0.1:5086";
    private static readonly string OutputDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "../tmp/MainMenuFlow"));
    private static Report report;

    [Serializable]
    private sealed class Report
    {
        public bool success, environmentRestored, httpOptionRestored;
        public string startedUtc, finishedUtc, scene = ScenePath, screenshot;
        public List<string> checks = new List<string>();
        public List<string> errors = new List<string>();
    }

    static MainMenuFlowValidation()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += PlayModeChanged;
        EditorApplication.quitting += EditorQuitting;
        Application.logMessageReceived += RuntimeLog;
        if (Running)
        {
            ReadReport();
            // 正常进入/退出 Play 的 Domain Reload 都有独立阶段；验证中途重编译则停止。
            if (Stage == "testing")
                EditorApplication.delayCall += () => Complete("验证期间发生 Domain Reload，已停止本次验证并恢复设置。");
        }
    }

    private static bool Running => SessionState.GetBool(Prefix + "running", false);
    private static string Stage
    {
        get => SessionState.GetString(Prefix + "stage", "");
        set => SessionState.SetString(Prefix + "stage", value);
    }

    [MenuItem("Tools/FPS/Main Menu/Validate Runtime Account Appearance")]
    public static void Run()
    {
        Require(!Running, "主菜单运行时验证正在进行。");
        Require(!EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling && !EditorApplication.isUpdating,
            "请在编译完成后的 Edit Mode 启动验证。");
        Scene scene = SceneManager.GetActiveScene();
        Require(SceneManager.sceneCount == 1 && scene.IsValid() && scene.isLoaded && scene.path == ScenePath,
            "验证只运行当前已打开的单个 GameStart 场景，不会自动打开或关闭场景。");
        Require(!scene.isDirty, "GameStart 有未保存改动，请先保存后再执行运行时验证。");
        Require(SceneComponents<AccountCharacterPreview>(scene).Length == 1 && SceneComponents<RifleGirlAppearance>(scene).Length == 1,
            "请先在 GameStart 接入唯一的 RifleGirl 展示角色与 AccountCharacterPreview。");

        string previousEnvironment = Environment.GetEnvironmentVariable(EnvironmentKey);
        SessionState.SetBool(Prefix + "hadEnvironment", previousEnvironment != null);
        SessionState.SetString(Prefix + "previousEnvironment", previousEnvironment ?? "");
        SessionState.SetInt(Prefix + "previousHttp", (int)PlayerSettings.insecureHttpOption);
        SessionState.SetInt(Prefix + "previousEditorWindow", EditorWindow.focusedWindow != null ? EditorWindow.focusedWindow.GetInstanceID() : 0);
        SessionState.SetString(Prefix + "startedTicks", DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
        report = new Report { startedUtc = DateTime.UtcNow.ToString("O") };
        StoreReport();
        Stage = "starting";
        SessionState.SetBool(Prefix + "running", true);
        try
        {
            // 仅此隔离的 localhost 验证允许 HTTP；退出 Play 后恢复原环境与设置。
            Environment.SetEnvironmentVariable(EnvironmentKey, Endpoint);
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
            EditorApplication.isPlaying = true;
        }
        catch (Exception exception)
        {
            AddError("启动验证失败：" + exception.Message);
            Stage = "stopping";
            RestoreSettings();
            throw;
        }
    }

    private static void Tick()
    {
        if (!Running) return;
        if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode &&
            Stage != "starting" && Stage != "entering")
        {
            if (Stage == "testing") AddError("Play Mode 被提前退出，验证未完成。");
            if (!EditorApplication.isCompiling && !EditorApplication.isUpdating) RestoreSettings();
            return;
        }
        if (long.TryParse(SessionState.GetString(Prefix + "startedTicks", "0"), out long start) &&
            DateTime.UtcNow.Ticks - start > TimeSpan.FromSeconds(60).Ticks && Stage != "stopping")
        {
            Complete("主菜单运行时验证超过 60 秒，已停止验证。");
            return;
        }
        if (EditorApplication.isPlaying && Stage == "entered")
        {
            Stage = "testing";
            // WaitForEndOfFrame 在 GameView 绘制时继续；只聚焦现有窗口，不创建场景或游戏界面。
            foreach (EditorWindow window in Resources.FindObjectsOfTypeAll<EditorWindow>())
                if (window.GetType().Name == "GameView") { window.Focus(); break; }
            AccountClient.EnsureInstance().StartCoroutine(ProtectedProbe());
        }
    }

    private static void PlayModeChanged(PlayModeStateChange state)
    {
        if (!Running) return;
        if (state == PlayModeStateChange.ExitingEditMode) Stage = "entering";
        else if (state == PlayModeStateChange.EnteredPlayMode) Stage = "entered";
        else if (state == PlayModeStateChange.ExitingPlayMode)
        {
            if (Stage != "stopping") AddError("Play Mode 被提前退出，验证未完成。");
            Stage = "stopping";
        }
        else if (state == PlayModeStateChange.EnteredEditMode && !EditorApplication.isCompiling && !EditorApplication.isUpdating)
            RestoreSettings();
    }

    private static IEnumerator ProtectedProbe()
    {
        IEnumerator probe = Probe();
        while (Running && Stage == "testing")
        {
            object next = null;
            bool more = false;
            Exception failure = null;
            try
            {
                more = probe.MoveNext();
                if (more) next = probe.Current;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            if (failure != null)
            {
                Complete(failure.Message);
                yield break;
            }
            if (!more) yield break;
            yield return next;
        }
    }

    private static IEnumerator Probe()
    {
        yield return new WaitForSecondsRealtime(0.9f);
        Scene scene = SceneManager.GetActiveScene();
        Check(scene.path == ScenePath, "启动后仍为 GameStart");
        RifleGirlAppearance display = SceneComponents<RifleGirlAppearance>(scene)[0];
        MainMenuUI[] menus = SceneComponents<MainMenuUI>(scene);
        Check(menus.Length == 1 && menus[0].gameObject.activeInHierarchy, "主菜单启动并显示");
        if (!Application.CanStreamedLevelBeLoaded(GameFlowScenes.MultiplayerLobby))
        {
            Require(menus[0].btnEnterLobby != null, "主菜单进入大厅按钮未绑定。");
            menus[0].btnEnterLobby.onClick.Invoke();
            yield return new WaitForSecondsRealtime(0.9f);
            Check(SceneManager.GetActiveScene().path == ScenePath, "大厅未制作时保留主菜单场景");
            TipMenuUI tip = TipMenuUI.instance;
            Check(tip != null && tip.gameObject.activeInHierarchy, "缺失大厅时显示已有提示面板");
            bool messageVisible = false;
            foreach (TMPro.TMP_Text text in tip.GetComponentsInChildren<TMPro.TMP_Text>())
                if (text.text.Contains(GameFlowScenes.MultiplayerLobby) && text.text.Contains("Build Settings")) messageVisible = true;
            foreach (Text text in tip.GetComponentsInChildren<Text>())
                if (text.text.Contains(GameFlowScenes.MultiplayerLobby) && text.text.Contains("Build Settings")) messageVisible = true;
            Check(messageVisible, "缺失大厅的实际提示文字说明 GameLobby 和 Build Settings");
            Require(tip.btnNo != null, "提示面板返回按钮未绑定。");
            tip.btnNo.onClick.Invoke();
            yield return new WaitForSecondsRealtime(0.9f);
            Check(menus[0].gameObject.activeInHierarchy && menus[0].btnExit.gameObject.activeSelf,
                "缺失大厅提示可通过实际按钮返回主菜单");
        }
        AccountClient account = AccountClient.EnsureInstance();
        Check(!account.IsAuthenticated, "隔离验证从未登录状态启动");
        ValidateDisplay(display, CharacterAppearanceData.Default, "未登录默认外观");

        bool completed = false, successful = false;
        string error = "";
        // 随机测试账号只写入本地隔离后端；密码、令牌不写入日志或验证结果。
        string username = "menu_" + Guid.NewGuid().ToString("N").Substring(0, 16);
        string password = Guid.NewGuid().ToString("N") + "Aa1!";
        account.Register(username, password, (ok, message) => { successful = ok; error = message; completed = true; });
        password = null;
        while (!completed) yield return null;
        Check(successful && account.IsAuthenticated && account.Profile != null, "真实本地账号注册并登录", error);
        Check(account.Profile.appearance.Equals(CharacterAppearanceData.Default), "新账号使用默认外观");
        ValidateDisplay(display, account.Profile.appearance, "登录档案同步");

        CharacterAppearanceData edited = CharacterAppearanceData.Default;
        edited.outfitId = 1; edited.faceId = 3; edited.clothColorId = 2; edited.hairColorId = 1;
        edited.hat = false; edited.accessories = false;
        int previousRevision = account.Profile.revision;
        completed = false;
        account.SaveProfile(edited, account.Profile.loadout, (ok, message) => { successful = ok; error = message; completed = true; });
        while (!completed) yield return null;
        Check(successful && account.Profile != null && account.Profile.appearance.Equals(edited) && account.Profile.revision > previousRevision,
            "云端确认保存自定义服装、脸型与配色", error);
        ValidateDisplay(display, account.Profile.appearance, "保存后主菜单同步");

        completed = false;
        account.RefreshProfile((ok, message) => { successful = ok; error = message; completed = true; });
        while (!completed) yield return null;
        Check(successful && account.Profile != null && account.Profile.appearance.Equals(edited), "重新获取云端档案仍保留外观", error);
        ValidateDisplay(display, account.Profile.appearance, "重新载入后主菜单同步");

        Check(menus[0].btnCharacter != null, "主菜单角色按钮已绑定");
        menus[0].btnCharacter.onClick.Invoke();
        CharacterCustomizationUI wardrobe = null;
        while (wardrobe == null)
        {
            CharacterCustomizationUI[] panels = SceneComponents<CharacterCustomizationUI>(scene);
            if (panels.Length != 0) wardrobe = panels[0];
            yield return null;
        }
        Button returnButton = null;
        foreach (Button button in wardrobe.GetComponentsInChildren<Button>(true))
        {
            Text label = button.GetComponentInChildren<Text>();
            if (label != null && label.text == "返回主菜单") returnButton = button;
        }
        Check(returnButton != null && returnButton.interactable, "主菜单角色界面显示返回主菜单");
        returnButton.onClick.Invoke();
        while (wardrobe != null) yield return null;
        yield return new WaitForSecondsRealtime(0.9f);
        Check(menus[0].gameObject.activeInHierarchy && menus[0].btnExit.gameObject.activeSelf, "角色界面真实返回按钮恢复主菜单");
        ValidateDisplay(display, edited, "关闭角色界面后外观保持");
        yield return new WaitForEndOfFrame();
        MainMenuCharacterSetup.CaptureRuntimePreview();
        ReadReport().screenshot = Path.Combine(OutputDirectory, "main-menu-runtime-preview.png");
        Check(File.Exists(ReadReport().screenshot), "保存实际运行时主菜单与相机堆栈截图");

        completed = false;
        account.Logout((ok, message) => { successful = ok; error = message; completed = true; });
        while (!completed) yield return null;
        Check(successful && !account.IsAuthenticated && account.Profile == null, "退出真实测试账号", error);
        ValidateDisplay(display, CharacterAppearanceData.Default, "退出账号恢复默认外观");
        Complete(null);
    }

    private static void ValidateDisplay(RifleGirlAppearance display, CharacterAppearanceData expected, string stage)
    {
        Check(display != null && display.gameObject.activeInHierarchy && !display.IsLocalFirstPerson && display.Current.Equals(expected),
            stage + "：展示档案与完整角色可见性");
        SerializedObject serialized = new SerializedObject(display);
        serialized.Update();
        CheckExclusive(serialized.FindProperty("outfitRoots"), expected.outfitId, stage + "：实际衣装网格");
        CheckExclusive(serialized.FindProperty("faceRoots"), expected.faceId, stage + "：实际脸型网格");
        GameObject hat = serialized.FindProperty("hatRoot").objectReferenceValue as GameObject;
        GameObject accessories = serialized.FindProperty("accessoryRoot").objectReferenceValue as GameObject;
        Check(hat != null && hat.activeSelf == expected.hat && accessories != null &&
            accessories.activeSelf == (expected.accessories && expected.outfitId != 2), stage + "：实际帽子与配件");
        Material cloth = serialized.FindProperty("clothMaterials").GetArrayElementAtIndex(expected.clothColorId).objectReferenceValue as Material;
        SerializedProperty clothes = serialized.FindProperty("clothRenderers");
        Require(cloth != null && clothes.arraySize > 0, stage + "：衣装材质配置缺失。");
        for (int i = 0; i < clothes.arraySize; i++)
        {
            Renderer renderer = clothes.GetArrayElementAtIndex(i).objectReferenceValue as Renderer;
            Require(renderer != null && renderer.sharedMaterial == cloth, stage + "：实际衣装材质未同步。");
        }
        Check(true, stage + "：实际衣装配色材质");
        Renderer hairRenderer = serialized.FindProperty("hairRenderer").objectReferenceValue as Renderer;
        Material hair = serialized.FindProperty("hairMaterials").GetArrayElementAtIndex(expected.hairColorId).objectReferenceValue as Material;
        Check(hairRenderer != null && hair != null && hairRenderer.sharedMaterial == hair, stage + "：实际发色材质");
    }

    private static void CheckExclusive(SerializedProperty roots, int selected, string label)
    {
        Require(roots != null && roots.isArray && selected < roots.arraySize, label + "配置缺失。");
        for (int i = 0; i < roots.arraySize; i++)
        {
            GameObject root = roots.GetArrayElementAtIndex(i).objectReferenceValue as GameObject;
            Require(root != null && root.activeSelf == (i == selected), label + "未按档案互斥切换。");
            if (i == selected)
            {
                bool visibleRenderer = false;
                foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>())
                    if (renderer.enabled && renderer.gameObject.activeInHierarchy) visibleRenderer = true;
                Require(visibleRenderer, label + "没有活动且启用的 Renderer。");
            }
        }
        Check(true, label);
    }

    private static void Check(bool condition, string label, string detail = null)
    {
        Require(condition, label + "失败。" + (string.IsNullOrEmpty(detail) ? "" : " " + detail));
        ReadReport().checks.Add(label);
        StoreReport();
    }

    private static void Complete(string failure)
    {
        if (!Running || Stage == "stopping") return;
        if (!string.IsNullOrEmpty(failure)) AddError(failure);
        ReadReport().finishedUtc = DateTime.UtcNow.ToString("O");
        ReadReport().success = ReadReport().errors.Count == 0;
        Stage = "stopping";
        StoreReport();
        WriteReport();
        if (EditorApplication.isPlayingOrWillChangePlaymode) EditorApplication.isPlaying = false;
        else RestoreSettings();
    }

    private static void RestoreSettings()
    {
        if (!Running) return;
        Stage = "restoring";
        Report current = ReadReport();
        try
        {
            string previous = SessionState.GetBool(Prefix + "hadEnvironment", false)
                ? SessionState.GetString(Prefix + "previousEnvironment", "") : null;
            Environment.SetEnvironmentVariable(EnvironmentKey, previous);
            current.environmentRestored = Environment.GetEnvironmentVariable(EnvironmentKey) == previous;
        }
        catch (Exception exception) { current.errors.Add("恢复账号验证环境失败：" + exception.Message); }
        try
        {
            InsecureHttpOption previous = (InsecureHttpOption)SessionState.GetInt(Prefix + "previousHttp", (int)InsecureHttpOption.NotAllowed);
            PlayerSettings.insecureHttpOption = previous;
            AssetDatabase.SaveAssets();
            current.httpOptionRestored = PlayerSettings.insecureHttpOption == previous;
        }
        catch (Exception exception) { current.errors.Add("恢复 HTTP 项目设置失败：" + exception.Message); }
        if (string.IsNullOrEmpty(current.finishedUtc)) current.finishedUtc = DateTime.UtcNow.ToString("O");
        current.success = current.success && current.errors.Count == 0 && current.environmentRestored && current.httpOptionRestored;
        Scene mainMenu = SceneManager.GetSceneByPath(ScenePath);
        if (!EditorApplication.isPlaying && mainMenu.IsValid() && mainMenu.isLoaded && SceneManager.GetActiveScene() != mainMenu)
            SceneManager.SetActiveScene(mainMenu);
        EditorWindow previousWindow = EditorUtility.InstanceIDToObject(SessionState.GetInt(Prefix + "previousEditorWindow", 0)) as EditorWindow;
        if (!EditorApplication.isPlaying && previousWindow != null) previousWindow.Focus();
        StoreReport();
        SessionState.SetBool(Prefix + "running", false);
        Stage = "done";
        WriteReport();
        Debug.Log("[MainMenuFlowValidation] " + (current.success ? "PASS" : "FAILED") + "：" +
            current.checks.Count + " 项检查；结果：" + Path.Combine(OutputDirectory, "runtime-validation.json"));
    }

    private static void EditorQuitting()
    {
        if (!Running) return;
        AddError("编辑器在验证完成前退出。");
        RestoreSettings();
    }

    private static void RuntimeLog(string message, string trace, LogType type)
    {
        if (!Running || Stage == "restoring" || (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)) return;
        // 不收集调用参数、请求体或完整堆栈，避免凭据被带入验证产物。
        string firstLine = (message ?? "").Split('\n')[0];
        if (firstLine.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0 ||
            firstLine.IndexOf("accessToken", StringComparison.OrdinalIgnoreCase) >= 0 ||
            firstLine.IndexOf("Bearer ", StringComparison.OrdinalIgnoreCase) >= 0)
            firstLine = "账号相关运行错误（详细响应不写入验证产物）。";
        AddError(type + ": " + firstLine);
    }

    private static void AddError(string message)
    {
        Report current = ReadReport();
        if (current.errors.Count < 30) current.errors.Add(message);
        current.success = false;
        StoreReport();
    }

    private static Report ReadReport()
    {
        if (report != null) return report;
        string json = SessionState.GetString(Prefix + "report", "");
        report = string.IsNullOrEmpty(json) ? new Report() : JsonUtility.FromJson<Report>(json);
        if (report == null) report = new Report();
        return report;
    }

    private static void StoreReport() => SessionState.SetString(Prefix + "report", JsonUtility.ToJson(ReadReport()));

    private static void WriteReport()
    {
        Directory.CreateDirectory(OutputDirectory);
        File.WriteAllText(Path.Combine(OutputDirectory, "runtime-validation.json"), JsonUtility.ToJson(ReadReport(), true));
    }

    private static T[] SceneComponents<T>(Scene scene) where T : Component
    {
        var result = new List<T>();
        foreach (GameObject root in scene.GetRootGameObjects()) result.AddRange(root.GetComponentsInChildren<T>(true));
        return result.ToArray();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
