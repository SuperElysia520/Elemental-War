using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Add invisible login components to the open, authored scene; never creates panels or changes their layout.</summary>
public static class LoginSceneSetup
{
    [MenuItem("Tools/FPS/Login/Integrate Profile Loading")]
    public static void IntegrateProfileLoading()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
            throw new InvalidOperationException("Stop Play Mode and wait for compilation before binding Login loading.");
        Scene original = SceneManager.GetActiveScene();
        if (SceneManager.sceneCount != 1 || (original.path != "Assets/Scenes/Login.unity" && original.path != "Assets/Scenes/GameLobby.unity"))
            throw new InvalidOperationException("Open only Login or GameLobby before binding.");
        string backupDirectory = Path.GetFullPath("tmp/LoginFlow/IntegratedLoading20261110/" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(backupDirectory);
        foreach (string path in new[] { "Assets/Scenes/Login.unity", "Assets/Scenes/GameLobby.unity" })
        {
            Scene scene = original.path == path ? original : EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            try
            {
                if (!EditorSceneManager.SaveScene(scene, Path.Combine(backupDirectory, scene.name + ".unity"), true))
                    throw new IOException("Scene backup failed: " + path);
                if (scene.name == "Login") BindProfileLoading(scene);
                else RemoveLobbyLoading(scene);
                EditorSceneManager.MarkSceneDirty(scene);
                if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Scene save failed: " + path);
            }
            finally { if (scene != original) EditorSceneManager.CloseScene(scene, true); }
        }
        SceneManager.SetActiveScene(original);
        Debug.Log("[LoginSceneSetup] Bound existing Status/progress controls; removed the lobby loading instance. Backups: " + backupDirectory);
    }

    private static void BindProfileLoading(Scene scene)
    {
        var ui = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<LoginUI>(true)).Single();
        if (ui.loadingPanel == null) throw new InvalidOperationException("LoginUI requires its existing LoadingPanel.");
        var status = ui.loadingPanel.transform.Find("Status")?.GetComponent<TMPro.TMP_Text>();
        var fill = ui.loadingPanel.transform.Find("ProgressBar/FillImage")?.GetComponent<UnityEngine.UI.Image>();
        var progress = ui.loadingPanel.transform.Find("Progress");
        if (status == null || fill == null) throw new InvalidOperationException("LoadingPanel requires its authored Status TMP text and progress Image.");
        Undo.RecordObjects(new UnityEngine.Object[] { ui, fill, status }, "Bind Login Profile Loading");
        ui.loadingStatusText = status;
        ui.loadingProgressImage = fill;
        ui.loadingProgressTmpText = progress != null ? progress.GetComponent<TMPro.TMP_Text>() : null;
        ui.loadingProgressText = progress != null ? progress.GetComponent<UnityEngine.UI.Text>() : null;
        fill.type = UnityEngine.UI.Image.Type.Filled;
        fill.fillMethod = UnityEngine.UI.Image.FillMethod.Horizontal;
        fill.fillOrigin = (int)UnityEngine.UI.Image.OriginHorizontal.Left;
        fill.fillAmount = 0f;
        status.text = "正在读取用户数据…";
        if (ui.loadingProgressTmpText != null) ui.loadingProgressTmpText.text = "0%";
        if (ui.loadingProgressText != null) ui.loadingProgressText.text = "0%";
        PrefabUtility.RecordPrefabInstancePropertyModifications(fill);
        PrefabUtility.RecordPrefabInstancePropertyModifications(status);
        if (ui.loadingProgressTmpText != null) PrefabUtility.RecordPrefabInstancePropertyModifications(ui.loadingProgressTmpText);
        if (ui.loadingProgressText != null) PrefabUtility.RecordPrefabInstancePropertyModifications(ui.loadingProgressText);
        Undo.FlushUndoRecordObjects();
    }

    private static void RemoveLobbyLoading(Scene scene)
    {
        var overlays = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Transform>(true))
            .Where(item => PrefabUtility.IsAnyPrefabInstanceRoot(item.gameObject) &&
                PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(item.gameObject) == "Assets/UI/Prefab/Mvc/LoadingOverlay.prefab")
            .Select(item => item.gameObject).ToArray();
        if (overlays.Length > 1) throw new InvalidOperationException("Multiple lobby loading instances found; inspect before removal.");
        foreach (var overlay in overlays) Undo.DestroyObjectImmediate(overlay);
    }

    [MenuItem("Tools/FPS/Login/Bind Loading Progress")]
    public static void BindLoadingProgress()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
            throw new InvalidOperationException("请在停止运行、编译完成后执行加载进度接线。");
        Scene scene = SceneManager.GetActiveScene();
        if (scene.path != "Assets/Scenes/Login.unity")
            throw new InvalidOperationException("请先打开你制作的 Login 场景。");
        string backup = Path.GetFullPath(Path.Combine(Application.dataPath, "../tmp/LoginLoading/login-before-binding-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".unity"));
        Directory.CreateDirectory(Path.GetDirectoryName(backup));
        if (!EditorSceneManager.SaveScene(scene, backup, true)) throw new IOException("无法备份 Login 场景，未修改。");
        BindProfileLoading(scene);
        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene)) throw new IOException("无法保存 Login 场景。");
        Debug.Log("[LoginSceneSetup] 已绑定现有加载进度 Image 和百分比文字；备份：" + backup);
    }

    [MenuItem("Tools/FPS/Login/Bind Scene And Configure Startup")]
    public static void Apply()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
            throw new InvalidOperationException("请在停止运行、编译完成后执行登录接线。");
        Scene scene = SceneManager.GetActiveScene();
        if (scene.path != "Assets/Scenes/Login.unity")
            throw new InvalidOperationException("请先打开你制作的 Login 场景。");
        var controllers = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<LoginSceneController>(true)).ToArray();
        if (controllers.Length > 1) throw new InvalidOperationException("Login 中存在多个登录控制器，请保留一个。");
        string backup = Path.GetFullPath(Path.Combine(Application.dataPath, "../tmp/LoginFlow/login-before-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".unity"));
        Directory.CreateDirectory(Path.GetDirectoryName(backup));
        if (!EditorSceneManager.SaveScene(scene, backup, true)) throw new IOException("无法备份当前 Login 场景，未修改。");
        LoginSceneController controller;
        if (controllers.Length == 0)
        {
            var root = new GameObject("LoginFlow");
            SceneManager.MoveGameObjectToScene(root, scene);
            Undo.RegisterCreatedObjectUndo(root, "Add Login Flow");
            controller = Undo.AddComponent<LoginSceneController>(root);
        }
        else controller = controllers[0];
        if (controller.GetComponent<LoginUI>() == null) Undo.AddComponent<LoginUI>(controller.gameObject);
        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene)) throw new IOException("无法保存 Login 场景。");

        var configured = new List<EditorBuildSettingsScene>
        { new EditorBuildSettingsScene(scene.path, true) };
        foreach (EditorBuildSettingsScene entry in EditorBuildSettings.scenes)
        {
            string path = AssetDatabase.GUIDToAssetPath(entry.guid.ToString());
            if (string.IsNullOrEmpty(path)) path = entry.path;
            if (Path.GetFileNameWithoutExtension(path) == GameFlowScenes.Login) continue;
            configured.Add(new EditorBuildSettingsScene(path, entry.enabled));
        }
        EditorBuildSettings.scenes = configured.ToArray();
        AssetDatabase.SaveAssets();
        GameFlowBuildScenes.Get(false);
        GameFlowBuildScenes.Get(true);
        Debug.Log("[LoginSceneSetup] 已接入登录功能，客户端入口为 Login。请在 LoginFlow/LoginUI 绑定手工 UI；备份：" + backup);
    }
}
