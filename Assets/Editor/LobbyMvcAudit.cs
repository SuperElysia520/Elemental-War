using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class LobbyMvcAudit
{
    [Serializable] private sealed class Report { public List<string> roots = new List<string>(); public List<string> ui = new List<string>(); public List<string> buttons = new List<string>(); public List<string> slots = new List<string>(); }
    public static void Baseline()
    {
        var scene = SceneManager.GetActiveScene();
        if (scene.path != "Assets/Scenes/GameLobby.unity")
            throw new InvalidOperationException("Open GameLobby before inspecting the authored UI.");
        if (scene.GetRootGameObjects().Any(root => root.GetComponent<FPS.UI.LobbyUIRoot>() != null))
            throw new InvalidOperationException("The pre-migration baseline is retained; do not overwrite it after migration.");
        Directory.CreateDirectory("tmp/LobbyMvc/before");
        if (!EditorSceneManager.SaveScene(scene, Path.GetFullPath("tmp/LobbyMvc/before/GameLobby-open.unity"), true))
            throw new InvalidOperationException("Could not preserve the open scene including unsaved changes.");
        var report = new Report();
        foreach (var root in scene.GetRootGameObjects())
        {
            string description = root.name + " [" + GlobalObjectId.GetGlobalObjectIdSlow(root) + "] active=" + root.activeSelf;
            var camera = root.GetComponent<Camera>();
            if (camera != null) description += " camera depth=" + camera.depth + " mask=" + camera.cullingMask + " position=" + camera.transform.position;
            var canvas = root.GetComponent<Canvas>();
            if (canvas != null) description += " canvas=" + canvas.renderMode + " order=" + canvas.sortingOrder + " camera=" + (canvas.worldCamera != null ? GlobalObjectId.GetGlobalObjectIdSlow(canvas.worldCamera).ToString() : "none");
            report.roots.Add(description);
            foreach (var target in root.GetComponentsInChildren<RectTransform>(true))
            {
                string path = target.name; Transform parent = target.parent;
                while (parent != null) { path = parent.name + "/" + path; parent = parent.parent; }
                report.ui.Add(path + "|" + target.anchorMin + "|" + target.anchorMax + "|" + target.anchoredPosition + "|" + target.sizeDelta);
                var button = target.GetComponent<UnityEngine.UI.Button>();
                if (button != null) report.buttons.Add(path);
            }
            foreach (var slot in root.GetComponentsInChildren<PartyMemberSlot>(true))
                report.slots.Add(slot.name + " appearance=" + (slot.appearance != null ? slot.appearance.transform.root.name : "none")
                    + " occupied=" + (slot.occupiedRoot != null ? slot.occupiedRoot.transform.root.name + "/" + slot.occupiedRoot.name : "none"));
        }
        Directory.CreateDirectory("tmp/LobbyMvc");
        File.WriteAllText("tmp/LobbyMvc/baseline.json", JsonUtility.ToJson(report, true));
        Debug.Log("[Lobby MVC] Baseline inspected; scene was not saved.");
    }
}
