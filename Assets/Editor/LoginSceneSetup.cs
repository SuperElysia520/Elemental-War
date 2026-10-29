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
