using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Creates separate Windows release artifacts for the initial-name and profile update.</summary>
public static class InitialNameReleaseBuild
{
    private const string Output = "Builds/UgsPvp/InitialName-20261112";
    private const string Evidence = "tmp/BuildInitialName20261112";
    [Serializable] private sealed class Result
    {
        public bool success, server, development;
        public string output, unityVersion, duration, result;
        public string[] scenes, warnings, errors;
        public ulong bytes;
    }

    public static void BuildClient() => Build(false);
    public static void BuildServer() => Build(true);
    public static void BuildDirectLobbyClient() => Build(false, "tmp/LobbyDirect-20261120", "tmp/LobbyDirect-20261120");

    private static void Build(bool server, string outputRoot = Output, string evidence = Evidence)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode before packaging.");
        Directory.CreateDirectory(evidence);
        string[] scenes = GameFlowBuildScenes.Get(server);
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (!scene.isDirty || !scenes.Contains(scene.path)) continue;
            string backup = evidence + "/" + scene.name;
            if (!File.Exists(backup + ".disk-before.unity")) File.Copy(scene.path, backup + ".disk-before.unity");
            if (!File.Exists(backup + ".open-before.unity") && !EditorSceneManager.SaveScene(scene, backup + ".open-before.unity", true))
                throw new IOException("Could not back up the open scene: " + scene.path);
            if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Could not save the scene for the requested build: " + scene.path);
        }
        PvpRifleGirlPrefabBuilder.Validate();
        PvpWeaponAssetBuilder.Validate();
        var previousSubtarget = EditorUserBuildSettings.standaloneBuildSubtarget;
        var previousHttp = PlayerSettings.insecureHttpOption;
        string folder = server ? "Server" : "Client";
        string output = outputRoot + "/" + folder + "/" + (server ? "FPS_PVP_Server.exe" : "FPS.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        try
        {
            PlayerSettings.insecureHttpOption = server ? InsecureHttpOption.AlwaysAllowed : InsecureHttpOption.NotAllowed;
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes, locationPathName = output, target = BuildTarget.StandaloneWindows64,
                subtarget = (int)(server ? StandaloneBuildSubtarget.Server : StandaloneBuildSubtarget.Player),
                options = BuildOptions.None
            });
            var messages = report.steps.SelectMany(step => step.messages).ToArray();
            var result = new Result
            {
                success = report.summary.result == BuildResult.Succeeded, server = server, development = false,
                output = Path.GetFullPath(output), unityVersion = Application.unityVersion, duration = report.summary.totalTime.ToString(),
                result = report.summary.result.ToString(), scenes = scenes, bytes = report.summary.totalSize,
                warnings = messages.Where(message => message.type == LogType.Warning).Select(message => message.content).Distinct().ToArray(),
                errors = messages.Where(message => message.type == LogType.Error || message.type == LogType.Exception || message.type == LogType.Assert).Select(message => message.content).Distinct().ToArray()
            };
            File.WriteAllText(evidence + "/build-" + folder.ToLowerInvariant() + ".json", JsonUtility.ToJson(result, true));
            if (!result.success) throw new InvalidOperationException("Windows " + folder + " build failed; inspect " + evidence);
        }
        finally
        {
            PlayerSettings.insecureHttpOption = previousHttp;
            EditorUserBuildSettings.standaloneBuildSubtarget = previousSubtarget;
            AssetDatabase.SaveAssets();
        }
    }
}
