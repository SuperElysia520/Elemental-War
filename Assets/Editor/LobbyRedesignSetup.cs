using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;

/// <summary>Wire only existing authored objects. Missing UI is reported for the user to build.</summary>
public static class LobbyRedesignSetup
{
    private const string CatalogPath = "Assets/Resources/Customization/PlayerAvatars.asset";
    [Serializable] private class Report { public string scene; public string backup; public string[] missing; }

    [MenuItem("Tools/FPS/Lobby/Bind Existing Matchmaking UI")]
    public static void BindExisting()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || SceneManager.GetActiveScene().path != "Assets/Scenes/GameLobby.unity")
            throw new InvalidOperationException("Only the open, stopped GameLobby can be wired.");
        Scene scene = SceneManager.GetActiveScene();
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../tmp/LobbyRedesign"));
        Directory.CreateDirectory(folder);
        string backup = Path.Combine(folder, "game-lobby-before-binding-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".unity");
        if (!EditorSceneManager.SaveScene(scene, backup, true)) throw new InvalidOperationException("Could not preserve the authored scene.");
        MultiplayerLobbyUI home = UnityEngine.Object.FindObjectOfType<MultiplayerLobbyUI>(true);
        if (home == null) throw new InvalidOperationException("MultiplayerLobbyUI is missing.");
        Transform canvas = Find(scene, "OverlayCanvas");
        if (canvas == null) throw new InvalidOperationException("Authored OverlayCanvas is missing.");
        var ui = home.GetComponent<PartyLobbyUI>();
        if (ui == null) ui = Undo.AddComponent<PartyLobbyUI>(home.gameObject);
        Undo.RecordObject(home, "Bind matchmaking home");
        Undo.RecordObject(ui, "Bind authored party UI");
        home.partyUI = ui; ui.home = home;
        home.btnRoomList = ButtonAt(canvas, "HomePanel/GameMenu/Role");
        if (home.btnCharacter == home.btnRoomList) home.btnCharacter = null;
        ui.partyPanel = ObjectAt(canvas, "LobbyPanel");
        ui.roomPanel = ObjectAt(canvas, "RoomPanel");
        ui.primaryButton = ButtonAt(canvas, "LobbyPanel/ButtomMenu/Start");
        ui.leaveButton = ButtonAt(canvas, "LobbyPanel/ButtomMenu/Leave");
        ui.returnButton = ButtonAt(canvas, "LobbyPanel/TopMenu/Left/Return");
        ui.closeRoomsButton = ButtonAt(canvas, "RoomPanel/Hide");
        if (ui.primaryButton != null) ui.primaryLabel = ui.primaryButton.GetComponentInChildren<Text>(true);
        if (ui.leaveButton != null) ui.leaveLabel = ui.leaveButton.GetComponentInChildren<Text>(true);
        if (ui.avatarCatalog == null)
        {
            ui.avatarCatalog = AssetDatabase.LoadAssetAtPath<PlayerAvatarCatalog>(CatalogPath);
            if (ui.avatarCatalog == null)
            {
                ui.avatarCatalog = ScriptableObject.CreateInstance<PlayerAvatarCatalog>();
                AssetDatabase.CreateAsset(ui.avatarCatalog, CatalogPath);
            }
        }
        Transform portrait = canvas.Find("HomePanel/TopMenu/Self/ProfilePicture");
        if (portrait != null)
        {
            var avatar = portrait.GetComponent<PlayerAvatarView>();
            if (avatar == null) avatar = Undo.AddComponent<PlayerAvatarView>(portrait.gameObject);
            Undo.RecordObject(avatar, "Bind existing avatar");
            avatar.rawImage = portrait.GetComponent<RawImage>(); avatar.image = portrait.GetComponent<Image>();
            ui.homeAvatar = avatar;
        }
        AccountCharacterPreview preview = UnityEngine.Object.FindObjectOfType<AccountCharacterPreview>(true);
        if (preview != null) ui.homeCharacter = preview.gameObject;
        const string prefabPath = "Assets/Prefabs/RoomEntry.prefab";
        File.Copy(Path.GetFullPath(prefabPath), Path.Combine(folder, "RoomEntry-before-binding-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".prefab"));
        GameObject prefab = PrefabUtility.LoadPrefabContents(prefabPath);
        try
        {
            RoomEntryUI entry = prefab.GetComponent<RoomEntryUI>();
            if (entry == null) entry = prefab.AddComponent<RoomEntryUI>();
            if (entry.roomName == null) entry.roomName = prefab.GetComponentInChildren<TMP_Text>(true);
            if (entry.joinButton == null) entry.joinButton = prefab.GetComponent<Button>();
            PrefabUtility.SaveAsPrefabAsset(prefab, prefabPath);
        }
        finally { PrefabUtility.UnloadPrefabContents(prefab); }
        ui.roomEntryPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath).GetComponent<RoomEntryUI>();
        EditorUtility.SetDirty(home); EditorUtility.SetDirty(ui);
        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("Could not save the bound GameLobby.");
        AssetDatabase.SaveAssets();
        WriteReport(ui, folder, backup);
    }

    [MenuItem("Tools/FPS/Lobby/Audit Matchmaking UI Bindings")]
    public static void Audit()
    {
        PartyLobbyUI ui = UnityEngine.Object.FindObjectOfType<PartyLobbyUI>(true);
        if (ui == null) throw new InvalidOperationException("PartyLobbyUI is not bound yet.");
        WriteReport(ui, Path.GetFullPath(Path.Combine(Application.dataPath, "../tmp/LobbyRedesign")), "");
    }

    public static void ValidateWireJson()
    {
        const string noParty = "{\"party\":null,\"message\":\"\"}";
        bool rawNullMaterialized = JsonUtility.FromJson<PartyStateData>(noParty).party != null;
        if (PartyStateData.FromJson(noParty).party != null) throw new InvalidOperationException("No-party response must not create a phantom party.");
        const string waiting = "{\"party\":{\"id\":\"party-1\",\"state\":\"Waiting\",\"match\":null,\"members\":[]}}";
        PartyData party = PartyStateData.FromJson(waiting).party;
        if (party == null || party.id != "party-1" || party.match != null) throw new InvalidOperationException("Waiting party must survive without a phantom match.");
        const string matched = "{\"party\":{\"id\":\"party-1\",\"state\":\"Matched\",\"match\":{\"matchId\":\"match-1\",\"serverPort\":7777,\"team\":1},\"members\":[]}}";
        if (PartyStateData.FromJson(matched).party.match.matchId != "match-1") throw new InvalidOperationException("Match assignment was lost.");
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../tmp/LobbyRedesign"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "json-validation.txt"), "PASS: no-party, waiting-party and matched-party responses. JsonUtility materialized raw null: " + rawNullMaterialized);
    }

    private static void WriteReport(PartyLobbyUI ui, string folder, string backup)
    {
        var missing = new List<string>();
        if (ui.title == null) missing.Add("LobbyPanel: title (TMP_Text)");
        if (ui.mapName == null) missing.Add("LobbyPanel: mapName (TMP_Text)");
        if (ui.status == null) missing.Add("LobbyPanel: status (TMP_Text)");
        if (ui.members == null || ui.members.Length != 5) missing.Add("LobbyPanel: exactly five PartyMemberSlot references");
        else for (int i = 0; i < 5; i++) if (ui.members[i] == null) missing.Add("LobbyPanel: PartyMemberSlot " + (i + 1));
        if (ui.roomListContainer == null) missing.Add("RoomPanel: roomListContainer (scroll-view Content with VerticalLayoutGroup)");
        if (ui.refreshRoomsButton == null) missing.Add("RoomPanel: refreshRoomsButton");
        if (ui.emptyRooms == null) missing.Add("RoomPanel: emptyRooms (TMP_Text, outside Content)");
        if (ui.roomEntryPrefab != null)
        {
            if (ui.roomEntryPrefab.mapName == null) missing.Add("RoomEntry: mapName (TMP_Text)");
            if (ui.roomEntryPrefab.roomState == null) missing.Add("RoomEntry: roomState (TMP_Text)");
            if (ui.roomEntryPrefab.joinButton == ui.roomEntryPrefab.GetComponent<Button>()) missing.Add("RoomEntry: independent joinButton (root button works as fallback)");
            for (int i = 0; i < ui.roomEntryPrefab.avatars.Length; i++) if (ui.roomEntryPrefab.avatars[i] == null) missing.Add("RoomEntry: avatar " + (i + 1));
        }
        if (ui.avatarCatalog == null || ui.avatarCatalog.avatars.Length == 0) missing.Add("PlayerAvatars catalog: assign 1-32 preset sprites");
        if (UnityEngine.Object.FindObjectOfType<PresetAvatarUI>(true) == null) missing.Add("PresetAvatarUI: authored avatar selection panel and buttons");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "missing-ui.json"), JsonUtility.ToJson(new Report
        { scene = SceneManager.GetActiveScene().path, backup = backup, missing = missing.ToArray() }, true));
    }
    private static Transform Find(Scene scene, string name)
    { foreach (GameObject root in scene.GetRootGameObjects()) if (root.name == name) return root.transform; return null; }
    private static GameObject ObjectAt(Transform root, string path) { Transform t = root.Find(path); return t != null ? t.gameObject : null; }
    private static Button ButtonAt(Transform root, string path) { GameObject go = ObjectAt(root, path); return go != null ? go.GetComponent<Button>() : null; }
}
