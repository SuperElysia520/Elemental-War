using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

/// <summary>Uses UPM's asynchronous resolver in the running Editor.</summary>
public static class UgsPackageInstaller
{
    private static AddAndRemoveRequest request;
    private static double deadline;
    private const string ReportPath = "tmp/UgsIntegration/packages.txt";

    [MenuItem("Tools/FPS/UGS Backend/Install Required Packages")]
    public static void Install()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Exit Play Mode before installing UGS packages.");
        if (request != null) return;
        Directory.CreateDirectory("tmp/UgsIntegration");
        File.WriteAllText(ReportPath, "Installing UGS packages with Unity Package Manager...");
        request = Client.AddAndRemove(new[]
        {
            "com.unity.services.core@1.16.0",
            "com.unity.services.authentication@3.6.1",
            "com.unity.services.cloudsave@3.4.0",
            "com.unity.services.cloudcode@2.10.3"
        });
        deadline = EditorApplication.timeSinceStartup + 600;
        EditorApplication.update += Poll;
    }

    private static void Poll()
    {
        if (request == null) return;
        if (!request.IsCompleted && EditorApplication.timeSinceStartup < deadline) return;
        EditorApplication.update -= Poll;
        string report = !request.IsCompleted ? "FAIL: UPM request timed out." :
            request.Status == StatusCode.Success ? "PASS\n" + string.Join("\n", request.Result
                .Where(p => p.name.StartsWith("com.unity.services.")).Select(p => p.name + "@" + p.version)) :
                "FAIL: " + request.Error?.message;
        File.WriteAllText(ReportPath, report);
        request = null;
        if (report.StartsWith("PASS")) Debug.Log("[UGS] Required packages installed.");
        else Debug.LogError("[UGS] " + report);
    }
}
