using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>Isolated output for migration validation; never overwrites the Rainyun trial build.</summary>
public static class PvpMigrationBuildValidation
{
    public static void BuildClient() => Build(false);
    public static void BuildServer() => Build(true);
    public static void BuildLobbyClient() => Build(false, "LobbyRedesign");
    public static void BuildLobbyServer() => Build(true, "LobbyRedesign");
    public static void BuildUgsClient() => Build(false, "UgsPvp");
    public static void BuildUgsServer() => Build(true, "UgsPvp");
    private static void Build(bool server, string destination = "RifleGirlMigration")
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode before building.");
        string[] scenes = GameFlowBuildScenes.Get(server);
        PvpRifleGirlPrefabBuilder.Validate();
        PvpWeaponAssetBuilder.Validate();
        var previousSubtarget = EditorUserBuildSettings.standaloneBuildSubtarget;
        var previousHttp = PlayerSettings.insecureHttpOption;
        string folder = server ? "Server" : "Client";
        string output = "Builds/" + destination + "/" + folder + "/" + (server ? "FPS_PVP_Server.exe" : "FPS.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        try
        {
            // The account client validates HTTPS itself and only permits HTTP on the local loopback.
            // This is required for the same-machine server ticket exchange and the development smoke test.
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = output,
                target = BuildTarget.StandaloneWindows64,
                subtarget = (int)(server ? StandaloneBuildSubtarget.Server : StandaloneBuildSubtarget.Player),
                options = BuildOptions.Development,
            };
            BuildReport report = BuildPipeline.BuildPlayer(options);
            string evidence = $"Result: {report.summary.result}\nErrors: {report.summary.totalErrors}\nWarnings: {report.summary.totalWarnings}\nSize: {report.summary.totalSize}\nDuration: {report.summary.totalTime}\nOutput: {Path.GetFullPath(output)}\n";
            string evidenceDirectory = "tmp/" + (destination == "LobbyRedesign" || destination == "UgsPvp" ? destination : "PvpMigration");
            Directory.CreateDirectory(evidenceDirectory);
            File.WriteAllText(evidenceDirectory + "/build-" + folder.ToLowerInvariant() + ".txt", evidence);
            if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException(evidence);
            Debug.Log("[PVP Migration] " + evidence);
        }
        finally
        {
            PlayerSettings.insecureHttpOption = previousHttp;
            EditorUserBuildSettings.standaloneBuildSubtarget = previousSubtarget;
            AssetDatabase.SaveAssets();
        }
    }
}
