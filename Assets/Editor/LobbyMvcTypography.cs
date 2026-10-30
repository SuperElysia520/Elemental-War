using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FPS.UI;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

/// <summary>Lobby-local fallback chains preserve authored font faces and material presets without changing shared source fonts.</summary>
public static class LobbyMvcTypography
{
    private const string Folder = "Assets/UI/Fonts/Mvc";
    private const string Characters = "正在创建队伍处理请求同步账号等待成员准备取消匹配成功失败请稍后重试房主离开解散队伍开始游戏暂无已创建的房间团队对抗测试地图未准备已满确认返回保存外观配装重新载入云端档案是否放弃尚有未更改当前用户资料";
    public static void Apply()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Stop Play Mode before typography migration.");
        var root = UnityEngine.Object.FindObjectOfType<LobbyUIRoot>(true);
        if (root == null) throw new InvalidOperationException("Open the migrated GameLobby.");
        Directory.CreateDirectory(Folder); AssetDatabase.Refresh();
        var source = AssetDatabase.LoadAssetAtPath<Font>("Assets/Resource/Fonts/印品抹茶体.ttf");
        var fallback = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(Folder + "/LobbyCjkFallback.asset");
        if (fallback == null)
        {
            fallback = TMP_FontAsset.CreateFontAsset(source, 48, 5, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true);
            if (fallback == null) throw new InvalidOperationException("Could not create the lobby CJK fallback.");
            fallback.name = "Lobby CJK Fallback";
            AssetDatabase.CreateAsset(fallback, Folder + "/LobbyCjkFallback.asset");
            fallback.material.name = "Lobby CJK Fallback Material"; AssetDatabase.AddObjectToAsset(fallback.material, fallback);
            foreach (var texture in fallback.atlasTextures) { texture.name = "Lobby CJK Atlas"; AssetDatabase.AddObjectToAsset(texture, fallback); }
            var serialized = new SerializedObject(fallback); serialized.FindProperty("m_ClearDynamicDataOnBuild").boolValue = true; serialized.ApplyModifiedPropertiesWithoutUndo();
        }
        if (fallback.sourceFontFile != source)
        {
            var serialized = new SerializedObject(fallback);
            serialized.FindProperty("m_SourceFontFile").objectReferenceValue = source;
            serialized.FindProperty("m_SourceFontFile_EditorRef").objectReferenceValue = source;
            serialized.FindProperty("m_SourceFontFileGUID").stringValue = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(source));
            serialized.ApplyModifiedPropertiesWithoutUndo(); fallback.atlasPopulationMode = AtlasPopulationMode.Dynamic;
            if (FontEngine.LoadFontFace(source, 48) != FontEngineError.Success) throw new InvalidOperationException("Could not load the authored CJK font face.");
            fallback.faceInfo = FontEngine.GetFaceInfo(); fallback.ClearFontAssetData(true);
        }
        // TMP 3.0 returns false from TryAddCharacters when every requested glyph already exists.
        fallback.TryAddCharacters(Characters);
        if (!fallback.HasCharacters(Characters)) throw new InvalidOperationException("The fallback font is missing required lobby characters.");
        var copies = new Dictionary<TMP_FontAsset, TMP_FontAsset>();
        foreach (var page in root.pages) ApplyTo(page.gameObject, fallback, copies);
        ApplyTo(root.messageDialog.gameObject, fallback, copies); ApplyTo(root.confirmDialog.gameObject, fallback, copies);
        foreach (var page in root.pages) PrefabUtility.ApplyPrefabInstance(page.gameObject, InteractionMode.AutomatedAction);
        PrefabUtility.ApplyPrefabInstance(root.messageDialog.gameObject, InteractionMode.AutomatedAction);
        PrefabUtility.ApplyPrefabInstance(root.confirmDialog.gameObject, InteractionMode.AutomatedAction);

        const string rowPath = "Assets/UI/Prefab/Mvc/PartyRoomEntry.prefab";
        var row = PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(root.roomsView.entryPrefab.gameObject));
        try { WireRoomEntry(row); ApplyTo(row, fallback, copies); PrefabUtility.SaveAsPrefabAsset(WrapRoomEntry(row), rowPath); }
        finally { PrefabUtility.UnloadPrefabContents(row); }
        root.roomsView.entryPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(rowPath).GetComponent<RoomEntryUI>();
        root.customRoomView.rowPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(rowPath);
        PrefabUtility.ApplyPrefabInstance(root.roomsView.gameObject, InteractionMode.AutomatedAction);
        PrefabUtility.ApplyPrefabInstance(root.customRoomView.gameObject, InteractionMode.AutomatedAction);
        EditorUtility.SetDirty(fallback); AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(root.gameObject.scene); EditorSceneManager.SaveScene(root.gameObject.scene);
    }
    private static void ApplyTo(GameObject item, TMP_FontAsset fallback, Dictionary<TMP_FontAsset, TMP_FontAsset> copies)
    {
        foreach (var text in item.GetComponentsInChildren<TMP_Text>(true))
        {
            var original = text.font;
            if (original == null || AssetDatabase.GetAssetPath(original).StartsWith(Folder, StringComparison.Ordinal)) continue;
            if (!copies.TryGetValue(original, out var replacement))
            {
                string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(original));
                string path = Folder + "/" + guid + "-Lobby.asset";
                replacement = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
                if (replacement == null)
                {
                    replacement = UnityEngine.Object.Instantiate(original); replacement.name = original.name + " (Lobby)";
                    replacement.atlasPopulationMode = AtlasPopulationMode.Static;
                    replacement.fallbackFontAssetTable = new List<TMP_FontAsset> { fallback };
                    if (original.fallbackFontAssetTable != null) replacement.fallbackFontAssetTable.AddRange(original.fallbackFontAssetTable.Where(font => font != null));
                    AssetDatabase.CreateAsset(replacement, path);
                }
                copies.Add(original, replacement);
            }
            var material = text.fontSharedMaterial;
            text.font = replacement; text.fontSharedMaterial = material; EditorUtility.SetDirty(text);
        }
    }
    private static void WireRoomEntry(GameObject row)
    {
        var entry = row.GetComponent<RoomEntryUI>();
        var visuals = row.GetComponent<RoomRowLayoutView>()?.visuals ?? row.transform;
        entry.mapName = visuals.Find("MapName")?.GetComponent<TMP_Text>();
        entry.roomState = visuals.Find("StateTip")?.GetComponent<TMP_Text>();
        entry.roomName = null; // This card has authored map/state labels, with no separate room-name label.
        entry.joinButton = visuals.Find("JoinBtn")?.GetComponent<UnityEngine.UI.Button>();
        var group = visuals.Find("AvatarGroup");
        entry.avatars = new PlayerAvatarView[group.childCount];
        for (int i = 0; i < group.childCount; i++)
        {
            var target = group.GetChild(i);
            var avatar = target.GetComponent<PlayerAvatarView>() ?? target.gameObject.AddComponent<PlayerAvatarView>();
            avatar.image = target.GetComponent<UnityEngine.UI.Image>(); avatar.rawImage = target.GetComponent<UnityEngine.UI.RawImage>();
            if (avatar.image != null && avatar.emptySprite == null) avatar.emptySprite = avatar.image.sprite;
            if (avatar.rawImage != null && avatar.emptyTexture == null) avatar.emptyTexture = avatar.rawImage.texture;
            entry.avatars[i] = avatar;
        }
    }
    private static GameObject WrapRoomEntry(GameObject row)
    {
        if (row.GetComponent<RoomRowLayoutView>() != null) return row;
        var wrapper = new GameObject("PartyRoomEntry", typeof(RectTransform), typeof(UnityEngine.UI.LayoutElement), typeof(UnityEngine.UI.Button));
        wrapper.transform.SetParent(row.transform, false); wrapper.transform.SetParent(null, false);
        var rect = wrapper.GetComponent<RectTransform>(); rect.anchorMin = new Vector2(0, 1); rect.anchorMax = Vector2.one; rect.pivot = new Vector2(0.5f, 1); rect.sizeDelta = new Vector2(0, 150);
        var original = row.GetComponent<RoomEntryUI>(); var entry = wrapper.AddComponent<RoomEntryUI>();
        entry.roomName = original.roomName; entry.mapName = original.mapName; entry.roomState = original.roomState; entry.joinButton = original.joinButton; entry.avatars = original.avatars;
        var oldButton = row.GetComponent<UnityEngine.UI.Button>(); var button = wrapper.GetComponent<UnityEngine.UI.Button>();
        if (oldButton != null) { button.targetGraphic = oldButton.targetGraphic; button.colors = oldButton.colors; UnityEngine.Object.DestroyImmediate(oldButton); }
        UnityEngine.Object.DestroyImmediate(original);
        var visual = row.GetComponent<RectTransform>(); Vector2 size = visual.sizeDelta;
        row.name = "Visuals"; row.transform.SetParent(wrapper.transform, false);
        visual.anchorMin = visual.anchorMax = visual.pivot = new Vector2(0.5f, 1); visual.anchoredPosition = Vector2.zero;
        var layout = wrapper.AddComponent<RoomRowLayoutView>(); layout.visuals = visual; layout.nativeSize = size; layout.layout = wrapper.GetComponent<UnityEngine.UI.LayoutElement>();
        return wrapper;
    }
}
