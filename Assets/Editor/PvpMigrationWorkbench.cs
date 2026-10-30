using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Local editor validation inbox. Requests use a fixed operation allowlist and never execute arbitrary source.</summary>
[InitializeOnLoad]
public static class PvpMigrationWorkbench
{
    [Serializable] private class Request { public string id; public string operation; }
    [Serializable] private class Result
    {
        public string id, operation, project, error;
        public bool success, playing, compiling, paused;
        public string insecureHttpOption;
        public string[] scenes, recentErrors;
        public string[] missingSceneScripts, missingScenePrefabs;
    }
    private static readonly List<string> Errors = new List<string>();
    private static double nextPoll;
    private static readonly string DirectoryPath = Path.GetFullPath(Path.Combine(Application.dataPath, "../tmp/PvpMigration"));
    static PvpMigrationWorkbench()
    {
        EditorApplication.update += Poll;
        Application.logMessageReceived += RecordLog;
    }
    private static void RecordLog(string message, string trace, LogType type)
    {
        if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
        if (Errors.Count >= 30) Errors.RemoveAt(0);
        Errors.Add(message + "\n" + trace);
    }
    private static void Poll()
    {
        if (EditorApplication.timeSinceStartup < nextPoll) return;
        nextPoll = EditorApplication.timeSinceStartup + 0.5;
        string requestPath = Path.Combine(DirectoryPath, "request.json");
        if (!File.Exists(requestPath) || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        Request request;
        try { request = JsonUtility.FromJson<Request>(File.ReadAllText(requestPath)); }
        catch { return; }
        if (request == null || string.IsNullOrEmpty(request.id)) return;
        File.Delete(requestPath);
        var result = new Result { id = request.id, operation = request.operation, project = Application.dataPath };
        try
        {
            switch (request.operation)
            {
                case "status": break;
                case "inspect-open-scene": InspectOpenScenes(result); break;
                case "audit-lobby-layout": Invoke("LobbyLayoutAudit", "Run"); break;
                case "audit-lobby-mvc": Invoke("LobbyMvcAudit", "Baseline"); break;
                case "setup-lobby-mvc": Invoke("LobbyMvcSceneSetup", "Apply"); break;
                case "audit-lobby-mvc-assets": Invoke("LobbyMvcValidation", "Audit"); break;
                case "validate-lobby-mvc": Invoke("LobbyMvcValidation", "Run"); break;
                case "finalize-lobby-mvc": Invoke("LobbyMvcSceneSetup", "FinalizeBindings"); break;
                case "fix-lobby-mvc-typography": Invoke("LobbyMvcTypography", "Apply"); break;
                case "bind-party-lobby": Invoke("LobbyRedesignSetup", "BindExisting"); break;
                case "audit-party-lobby": Invoke("LobbyRedesignSetup", "Audit"); break;
                case "validate-party-json": Invoke("LobbyRedesignSetup", "ValidateWireJson"); break;
                case "save-project": AssetDatabase.SaveAssets(); break;
                case "refresh": AssetDatabase.Refresh(); break;
                case "build-assets":
                    if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode before generating assets.");
                    Invoke("RifleGirlAssetBuilder", "Build");
                    Invoke("PvpWeaponAssetBuilder", "Build");
                    Invoke("PvpRifleGirlPrefabBuilder", "Build");
                    break;
                case "validate":
                    Invoke("PvpRifleGirlPrefabBuilder", "Validate");
                    Invoke("PvpWeaponAssetBuilder", "Validate");
                    break;
                case "build-weapons": Invoke("PvpWeaponAssetBuilder", "Build"); break;
                case "preview": Invoke("PvpRifleGirlPrefabBuilder", "CapturePreview"); break;
                case "foot-preview": Invoke("PvpRifleGirlPrefabBuilder", "CaptureFootPreview"); break;
                case "skinning-diagnostic": Invoke("RifleGirlAssetBuilder", "InspectSkinning"); break;
                case "skinning-comparison": Invoke("PvpRifleGirlPrefabBuilder", "CaptureSkinningComparison"); break;
                case "hands-diagnostic": Invoke("PvpRifleGirlPrefabBuilder", "InspectHands"); break;
                case "optics-diagnostic": Invoke("PvpWeaponAssetBuilder", "DiagnoseOptics"); break;
                case "lobby-preview":
                    Invoke("PvpRifleGirlPrefabBuilder", "BuildLobbyPreview");
                    Invoke("PvpRifleGirlPrefabBuilder", "CaptureLobbyPreview");
                    break;
                case "build-client": Invoke("PvpMigrationBuildValidation", "BuildClient"); break;
                case "build-server": Invoke("PvpMigrationBuildValidation", "BuildServer"); break;
                case "build-lobby-client": Invoke("PvpMigrationBuildValidation", "BuildLobbyClient"); break;
                case "build-lobby-server": Invoke("PvpMigrationBuildValidation", "BuildLobbyServer"); break;
                case "build-ugs-pvp-client": Invoke("PvpMigrationBuildValidation", "BuildUgsClient"); break;
                case "build-ugs-pvp-server": Invoke("PvpMigrationBuildValidation", "BuildUgsServer"); break;
                case "setup-main-menu-character": Invoke("MainMenuCharacterSetup", "Apply"); break;
                case "capture-main-menu": Invoke("MainMenuCharacterSetup", "CapturePreview"); break;
                case "save-main-menu":
                    if (EditorApplication.isPlayingOrWillChangePlaymode || SceneManager.GetActiveScene().path != "Assets/Scenes/GameStart.unity")
                        throw new InvalidOperationException("Only the open, stopped GameStart scene may be saved.");
                    if (!EditorSceneManager.SaveScene(SceneManager.GetActiveScene()))
                        throw new InvalidOperationException("Main menu scene could not be saved.");
                    break;
                case "validate-main-menu-runtime": Invoke("MainMenuFlowValidation", "Run"); break;
                case "setup-login-scene": Invoke("LoginSceneSetup", "Apply"); break;
                case "bind-login-loading": Invoke("LoginSceneSetup", "BindLoadingProgress"); break;
                case "open-backend-setup": Invoke("HostedBackendSetupWindow", "Open"); break;
                case "install-ugs-packages": Invoke("UgsPackageInstaller", "Install"); break;
                case "open-ugs-setup": Invoke("UgsBackendSetupWindow", "Open"); break;
                case "validate-ugs-config": Invoke("UgsBackendSetupWindow", "WriteConfigurationReport"); break;
                case "validate-ugs-runtime": Invoke("UgsFlowValidation", "Run"); break;
                case "audit-login-inputs": Invoke("LoginInputValidationAudit", "Run"); break;
                case "validate-login-runtime": Invoke("LoginFlowValidation", "Run"); break;
                case "validate-login-startup": Invoke("LoginStartupValidation", "Run"); break;
                case "restart-temp-allocator-diagnostics": Invoke("LoginStartupValidation", "RestartWithDiagnostics"); break;
                case "restart-after-temp-allocator-diagnostics": Invoke("LoginStartupValidation", "RestartWithoutDiagnostics"); break;
                default: throw new InvalidOperationException("Unknown operation: " + request.operation);
            }
            result.success = true;
        }
        catch (Exception exception) { result.error = (exception.InnerException ?? exception).ToString(); }
        result.playing = EditorApplication.isPlaying;
        result.paused = EditorApplication.isPaused;
        result.insecureHttpOption = PlayerSettings.insecureHttpOption.ToString();
        result.compiling = EditorApplication.isCompiling;
        var scenes = new List<string>();
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            scenes.Add(scene.path + (scene.isDirty ? " [UNSAVED]" : ""));
        }
        result.scenes = scenes.ToArray();
        result.recentErrors = Errors.ToArray();
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(Path.Combine(DirectoryPath, "result.json"), JsonUtility.ToJson(result, true));
    }
    private static void Invoke(string typeName, string method)
    {
        Type type = typeof(PvpMigrationWorkbench).Assembly.GetType(typeName);
        if (type == null) throw new InvalidOperationException("Tool not compiled: " + typeName);
        type.GetMethod(method, BindingFlags.Static | BindingFlags.Public).Invoke(null, null);
    }

    private static void InspectOpenScenes(Result result)
    {
        var missingScripts = new List<string>();
        var missingPrefabs = new List<string>();
        for (int index = 0; index < SceneManager.sceneCount; index++)
        {
            Scene scene = SceneManager.GetSceneAt(index);
            foreach (GameObject root in scene.GetRootGameObjects())
            foreach (Transform item in root.GetComponentsInChildren<Transform>(true))
            {
                GameObject target = item.gameObject;
                string label = scene.name + "/" + target.name;
                if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(target) > 0)
                    missingScripts.Add(label);
                if (PrefabUtility.IsAnyPrefabInstanceRoot(target) &&
                    PrefabUtility.GetPrefabAssetType(target) == PrefabAssetType.MissingAsset)
                    missingPrefabs.Add(label);
            }
        }
        result.missingSceneScripts = missingScripts.ToArray();
        result.missingScenePrefabs = missingPrefabs.ToArray();
    }
}
