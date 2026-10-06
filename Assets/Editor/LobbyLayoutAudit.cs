using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;

/// <summary>Read-only inspection of authored UI, including unsaved scene changes.</summary>
public static class LobbyLayoutAudit
{
    [Serializable] private class Report { public string scene; public bool dirty; public List<Item> items = new List<Item>(); }
    [Serializable] private class Item { public string path; public string[] components; public string text; public bool active; }

    public static void Run()
    {
        var report = new Report();
        Scene scene = SceneManager.GetActiveScene();
        report.scene = scene.path;
        report.dirty = scene.isDirty;
        foreach (GameObject root in scene.GetRootGameObjects())
        foreach (Transform target in root.GetComponentsInChildren<Transform>(true))
        {
            if (!(target is RectTransform) && target.GetComponent<MultiplayerLobbyUI>() == null) continue;
            var names = new List<string>();
            foreach (Component component in target.GetComponents<Component>())
                names.Add(component != null ? component.GetType().Name : "MissingScript");
            TMP_Text tmp = target.GetComponent<TMP_Text>();
            Text text = target.GetComponent<Text>();
            report.items.Add(new Item { path = GetPath(target), components = names.ToArray(),
                active = target.gameObject.activeSelf, text = tmp != null ? tmp.text : text != null ? text.text : "" });
        }
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../tmp/LobbyRedesign"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "layout-audit.json"), JsonUtility.ToJson(report, true));
    }

    private static string GetPath(Transform target)
    {
        string path = target.name;
        while (target.parent != null) { target = target.parent; path = target.name + "/" + path; }
        return path;
    }
}
