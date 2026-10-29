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
    [MenuItem("Tools/FPS/Login/Bind Loading Progress")]
    public static void BindLoadingProgress()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
            throw new InvalidOperationException("请在停止运行、编译完成后执行加载进度接线。");
        Scene scene = SceneManager.GetActiveScene();
        if (scene.path != "Assets/Scenes/Login.unity")
            throw new InvalidOperationException("请先打开你制作的 Login 场景。");
        var views = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<LoginUI>(true)).ToArray();
        if (views.Length != 1 || views[0].loadingPanel == null)
            throw new InvalidOperationException("需要唯一的 LoginUI，并已绑定 Loading Panel。");
        LoginUI ui = views[0];
        Transform fill = ui.loadingPanel.transform.Find("ProgressBar/FillImage");
        Transform label = ui.loadingPanel.transform.Find("Progress");
        var image = fill != null ? fill.GetComponent<UnityEngine.UI.Image>() : null;
        var text = label != null ? label.GetComponent<UnityEngine.UI.Text>() : null;
        if (image == null || text == null)
            throw new InvalidOperationException("LoadingPanel 下需要现有 ProgressBar/FillImage 的 Image 和 Progress 的 Text。");

        string backup = Path.GetFullPath(Path.Combine(Application.dataPath, "../tmp/LoginLoading/login-before-binding-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".unity"));
        Directory.CreateDirectory(Path.GetDirectoryName(backup));
        if (!EditorSceneManager.SaveScene(scene, backup, true)) throw new IOException("无法备份 Login 场景，未修改。");
        Undo.RecordObjects(new UnityEngine.Object[] { ui, image, text }, "Bind Login Loading Progress");
        ui.loadingProgressImage = image;
        ui.loadingProgressText = text;
        image.type = UnityEngine.UI.Image.Type.Filled;
        image.fillMethod = UnityEngine.UI.Image.FillMethod.Horizontal;
        image.fillOrigin = (int)UnityEngine.UI.Image.OriginHorizontal.Left;
        image.fillAmount = 0f;
        text.text = "0%";
        Undo.FlushUndoRecordObjects();
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
