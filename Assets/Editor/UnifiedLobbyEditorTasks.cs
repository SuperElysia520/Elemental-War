using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Local, allowlisted inbox for this project's Unity 2022 editor (Pipeline needs Unity 6).
[InitializeOnLoad]
public static class UnifiedLobbyEditorTasks
{
    public const string Output = "tmp/UnifiedLobby20261118";
    private static double nextPoll;
    static UnifiedLobbyEditorTasks() { EditorApplication.update += Poll; }
    private static void Poll()
    {
        if (EditorApplication.timeSinceStartup < nextPoll || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        nextPoll = EditorApplication.timeSinceStartup + .5;
        var path = Output + "/request.txt";
        if (!File.Exists(path)) return;
        var command = File.ReadAllText(path).Trim(); File.Delete(path);
        try
        {
            if (command == "inspect") Inspect();
            else
            {
                string method = command == "apply" ? "Apply" : command == "split-cards" ? "SplitCards" : command == "save" ? "Save" : command == "refine" ? "Refine" : command == "repair-character" ? "RepairCharacter" : command == "capture" ? "Capture" : command == "validate" ? "Validate" : command == "runtime" ? "Run" : null;
                if (method == null) throw new InvalidOperationException("Unknown unified lobby task: " + command);
                var type = typeof(UnifiedLobbyEditorTasks).Assembly.GetType(command == "runtime" ? "UnifiedLobbyValidation" : "UnifiedLobbyBuilder", true);
                type.GetMethod(method).Invoke(null, null);
            }
            File.WriteAllText(Output + "/task-status.json", "{\"success\":true,\"command\":\"" + command + "\"}");
        }
        catch (Exception e)
        {
            var error = e.InnerException ?? e;
            Debug.LogException(error);
            File.WriteAllText(Output + "/task-status.json", JsonUtility.ToJson(new Failure { command = command, error = error.ToString() }, true));
        }
    }
    [Serializable] private class Failure { public bool success; public string command, error; }
    public static void Inspect()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Stop Play Mode before inspecting the authored scene.");
        var scene = SceneManager.GetActiveScene(); Directory.CreateDirectory(Output + "/before");
        string backup = Output + "/before/GameLobby-open-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".unity";
        if (!EditorSceneManager.SaveScene(scene, Path.GetFullPath(backup), true)) throw new IOException("Could not back up the open scene.");
        var lines = new StringBuilder();
        lines.AppendLine("scene=" + scene.path + " dirty=" + scene.isDirty + " backup=" + backup);
        var root = UnityEngine.Object.FindObjectOfType<FPS.UI.LobbyUIRoot>(true);
        if (root != null)
        {
            lines.AppendLine("homeCharacter=" + PathOf(root.homeCharacter));
            lines.AppendLine("mainCharacter=" + PathOf(root.mainCharacter));
            lines.AppendLine("mainEnvironment=" + string.Join(", ", root.mainEnvironment.Select(PathOf)));
            lines.AppendLine("lobbyEnvironment=" + string.Join(", ", root.lobbyEnvironment.Select(PathOf)));
            foreach (var page in root.pages) lines.AppendLine("page=" + page.page + " path=" + PathOf(page.gameObject) + " prefab=" + AssetDatabase.GetAssetPath(PrefabUtility.GetCorrespondingObjectFromSource(page)));
        }
        foreach (var item in scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Transform>(true)))
        {
            if (item.GetComponent<Camera>() == null && item.GetComponent<Canvas>() == null && item.GetComponent<RifleGirlAppearance>() == null && item.GetComponent<Light>() == null && item.GetComponent<Canvas>() == null) continue;
            lines.AppendLine(PathOf(item.gameObject) + " active=" + item.gameObject.activeSelf + " pos=" + item.position + " rot=" + item.eulerAngles + " scale=" + item.lossyScale);
            var camera = item.GetComponent<Camera>();
            if (camera != null) lines.AppendLine("  camera fov=" + camera.fieldOfView + " depth=" + camera.depth + " mask=" + camera.cullingMask + " clear=" + camera.clearFlags);
        }
        File.WriteAllText(Output + "/inspection.txt", lines.ToString());
    }
    private static string PathOf(GameObject item)
    {
        if (item == null) return "null";
        string path = item.name;
        for (var p = item.transform.parent; p != null; p = p.parent) path = p.name + "/" + path;
        return path;
    }
}
