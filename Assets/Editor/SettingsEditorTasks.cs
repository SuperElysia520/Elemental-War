using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Small, file-based task dispatcher for this project's Unity 2022 editor.
// Only the settings builder/validation methods below can be called.
[InitializeOnLoad]
public static class SettingsEditorTasks
{
    public const string Output = "tmp/Settings20261116";
    private const string Request = Output + "/request.txt";
    private static double nextPoll;
    static SettingsEditorTasks() { EditorApplication.update += Poll; }
    private static void Poll()
    {
        if (EditorApplication.timeSinceStartup < nextPoll || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        nextPoll = EditorApplication.timeSinceStartup + 0.5;
        if (!File.Exists(Request)) return;
        string command = File.ReadAllText(Request).Trim();
        try
        {
            if(command!="inspect"&&command!="restore-shared-font"&&typeof(SettingsEditorTasks).Assembly.GetType("SettingsPanelBuilder",false)==null)return;
            File.Delete(Request);
            if (command == "inspect") Inspect();
            else if(command=="restore-shared-font")RestoreSharedFont();
            else if(command=="legacy-prepare")SettingsLegacyTextMigration.Prepare();
            else if(command=="legacy-convert")SettingsLegacyTextMigration.Convert();
            else if(command=="legacy-restore-cache")SettingsLegacyTextMigration.RestoreUnrelatedFontCache();
            else
            {
                string method = command == "lobby-style" ? "ApplyLobbyStyle" : command == "build" ? "Build" : command == "validate" ? "Validate" : command == "capture" ? "Capture" : command == "runtime" ? "ValidateRuntime" : command == "repair" ? "RepairRenderers" : command == "layout" ? "RefineLayout" : command == "typography" ? "RefineTypography" : command == "fallback" ? "ConfigurePrivateFallback" : null;
                if (method == null) throw new InvalidOperationException("Unknown settings task: " + command);
                var type = typeof(SettingsEditorTasks).Assembly.GetType("SettingsPanelBuilder", true);
                type.GetMethod(method, BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            }
            File.WriteAllText(Output + "/task-status.json", "{\"success\":true,\"command\":\"" + command + "\"}");
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            File.WriteAllText(Output + "/task-status.json", JsonUtility.ToJson(new Failure { command = command, error = exception.ToString() }, true));
        }
    }
    [Serializable] private sealed class Failure { public bool success; public string command, error; }
    private static void RestoreSharedFont()
    {
        // This shared asset was clean before settings work; runtime font fallback caching must
        // not leave incidental atlas changes in the existing lobby's typography.
        const string path="Assets/Resource/UI/Fonts/Mvc/LobbyCjkFallback.asset";
        var start=new System.Diagnostics.ProcessStartInfo("git","show HEAD:"+path)
        { WorkingDirectory=Directory.GetParent(Application.dataPath).FullName,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=System.Text.Encoding.UTF8 };
        string baseline;
        using(var process=System.Diagnostics.Process.Start(start))
        { baseline=process.StandardOutput.ReadToEnd();process.WaitForExit();if(process.ExitCode!=0)throw new InvalidOperationException("Cannot read the original shared font: "+process.StandardError.ReadToEnd()); }
        foreach(var asset in AssetDatabase.LoadAllAssetsAtPath(path))EditorUtility.ClearDirty(asset);
        File.WriteAllText(path,baseline,new System.Text.UTF8Encoding(false));
        AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceUpdate|ImportAssetOptions.ForceSynchronousImport);
        Inspect();
    }
    private static void Inspect()
    {
        var scene = SceneManager.GetActiveScene();
        var lines = new System.Text.StringBuilder();
        lines.AppendLine("Scene: " + scene.path + "; dirty=" + scene.isDirty + "; playing=" + EditorApplication.isPlaying);
        foreach (var root in scene.GetRootGameObjects())
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.GetComponent<Canvas>() == null && t.GetComponent<UnityEngine.EventSystems.EventSystem>() == null && t.GetComponent<FPS.UI.LobbyPageView>() == null && t.GetComponent<FPS.UI.LobbyUIRoot>() == null && t.GetComponent<AudioSource>() == null) continue;
                var path = t.name;
                for (var p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
                lines.AppendLine(path + " [" + string.Join(",", t.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name)) + "] active=" + t.gameObject.activeSelf);
                var page = t.GetComponent<FPS.UI.LobbyPageView>();
                if (page != null) foreach (var b in page.buttons) if (b.button != null) lines.AppendLine("  " + b.button.name + " => " + b.action);
                var a = t.GetComponent<AudioSource>();
                if (a != null) lines.AppendLine("  audio=" + (a.clip != null ? AssetDatabase.GetAssetPath(a.clip) : "null") + "; volume=" + a.volume + "; loop=" + a.loop);
            }
        Directory.CreateDirectory(Output);
        File.WriteAllText(Output + "/inspection.txt", lines.ToString());
    }
}
