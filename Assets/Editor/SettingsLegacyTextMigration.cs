using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FPS.Settings;
using TMPro;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Migrates components in place, retaining the authored hierarchy and settings bindings.</summary>
public static class SettingsLegacyTextMigration
{
    public const string Output = "tmp/SettingsLegacy20261118";
    public const string BodyFontPath = SettingsPanelBuilder.Folder + "/SettingsBody.ttf";
    private const string SnapshotPath = Output + "/bindings-before.json";
    [Serializable] private sealed class Binding
    {
        public string owner, property, target, kind;
        public bool panel;
    }
    [Serializable] private sealed class Snapshot
    {
        public List<Binding> bindings = new List<Binding>();
        public string[] rowIds;
        public int textCount, dropdownCount;
    }

    public static void Prepare()
    {
        Require(!EditorApplication.isPlayingOrWillChangePlaymode, "Stop Play Mode before migration.");
        var root = PrefabUtility.LoadPrefabContents(SettingsPanelBuilder.PrefabPath);
        try
        {
            var view = root.GetComponent<SettingsPanelView>();
            var snapshot = new Snapshot {
                rowIds = view.rows.Select(r => r.settingId).ToArray(),
                textCount = root.GetComponentsInChildren<TMP_Text>(true).Length,
                dropdownCount = root.GetComponentsInChildren<TMP_Dropdown>(true).Length
            };
            Require(snapshot.textCount > 0 && snapshot.rowIds.Length == 34, "Expected the existing TMP settings panel.");
            foreach (var owner in root.GetComponentsInChildren<MonoBehaviour>(true).Where(c => c is SettingsPanelView || c is SettingsRowView))
            {
                var serialized = new SerializedObject(owner);
                var property = serialized.GetIterator();
                while (property.Next(true))
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                    var target = property.objectReferenceValue as Component;
                    if (!(target is TMP_Text) && !(target is TMP_Dropdown)) continue;
                    snapshot.bindings.Add(new Binding {
                        panel = owner is SettingsPanelView,
                        owner = AnimationUtility.CalculateTransformPath(owner.transform, root.transform),
                        property = property.propertyPath,
                        target = AnimationUtility.CalculateTransformPath(target.transform, root.transform),
                        kind = target is TMP_Dropdown ? "dropdown" : "text"
                    });
                }
            }
            Directory.CreateDirectory(Output);
            File.WriteAllText(SnapshotPath, JsonUtility.ToJson(snapshot, true));
            Debug.Log("[Settings] Preserved " + snapshot.bindings.Count + " bindings for the legacy Text migration.");
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }

    public static Font EnsureBodyFont()
    {
        var font = AssetDatabase.LoadAssetAtPath<Font>(BodyFontPath);
        if (font != null) return font;
        string source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyh.ttc");
        Require(File.Exists(source), "The existing Microsoft YaHei body font source is unavailable.");
        if (!File.Exists(BodyFontPath)) File.Copy(source, BodyFontPath);
        AssetDatabase.ImportAsset(BodyFontPath, ImportAssetOptions.ForceSynchronousImport);
        var importer = AssetImporter.GetAtPath(BodyFontPath) as TrueTypeFontImporter;
        Require(importer != null, "Could not import the original body font as a legacy Font.");
        if (!importer.includeFontData || importer.fontTextureCase != FontTextureCase.Dynamic)
        {
            importer.includeFontData = true; importer.fontTextureCase = FontTextureCase.Dynamic; importer.SaveAndReimport();
        }
        font = AssetDatabase.LoadAssetAtPath<Font>(BodyFontPath);
        Require(font != null, "The legacy body font could not be loaded.");
        return font;
    }

    private static Font OriginalFont(TMP_FontAsset asset, Font body)
    {
        if (asset == null) return body;
        if (asset.sourceFontFile != null) return asset.sourceFontFile;
        var serialized = new SerializedObject(asset);
        var source = serialized.FindProperty("m_SourceFontFile_EditorRef")?.objectReferenceValue as Font;
        if (source != null) return source;
        string guid = serialized.FindProperty("m_SourceFontFileGUID")?.stringValue;
        if (!string.IsNullOrEmpty(guid))
        {
            source = AssetDatabase.LoadAssetAtPath<Font>(AssetDatabase.GUIDToAssetPath(guid));
            if (source != null) return source;
        }
        return asset.name == "SettingsHeading"
            ? AssetDatabase.LoadAssetAtPath<Font>("Assets/Resource/Fonts/印品抹茶体.ttf") ?? body : body;
    }

    private static TextAnchor Alignment(TMP_Text source)
    {
        string value = source.alignment.ToString();
        bool right = value.Contains("Right"), left = value.Contains("Left") || value == "Justified" || value == "Flush";
        int row = value.StartsWith("Top") ? 0 : value.StartsWith("Bottom") ? 2 : 1;
        return (TextAnchor)(row * 3 + (left ? 0 : right ? 2 : 1));
    }

    public static void Convert()
    {
        Require(!EditorApplication.isPlayingOrWillChangePlaymode, "Stop Play Mode before migration.");
        Require(File.Exists(SnapshotPath), "Capture the existing bindings before migrating the view field types.");
        var snapshot = JsonUtility.FromJson<Snapshot>(File.ReadAllText(SnapshotPath));
        var bodyFont = EnsureBodyFont();
        var root = PrefabUtility.LoadPrefabContents(SettingsPanelBuilder.PrefabPath);
        try
        {
            bool wasActive = root.activeSelf;
            root.SetActive(false);
            var oldTexts = root.GetComponentsInChildren<TMP_Text>(true);
            Require(oldTexts.Length == snapshot.textCount, "The prefab changed after the migration snapshot; inspect it before continuing.");
            var originalFonts = oldTexts.ToDictionary(t => t.gameObject, t => OriginalFont(t.font, bodyFont));
            var newTexts = new Dictionary<GameObject, UnityEngine.UI.Text>();
            foreach (var old in oldTexts)
            {
                var go = old.gameObject;
                string content = old.text;
                int fontSize = Mathf.RoundToInt(old.fontSize);
                Color color = old.color; var alignment = Alignment(old);
                bool wrap = old.enableWordWrapping, rich = old.richText, raycast = old.raycastTarget;
                bool enabled = old.enabled; float spacing = old.lineSpacing;
                var style = old.fontStyle;
                Object.DestroyImmediate(old);
                var text = go.AddComponent<UnityEngine.UI.Text>();
                text.font = originalFonts[go]; text.fontSize = fontSize; text.text = content;
                text.color = color; text.alignment = alignment; text.supportRichText = rich;
                text.horizontalOverflow = wrap ? HorizontalWrapMode.Wrap : HorizontalWrapMode.Overflow;
                text.verticalOverflow = VerticalWrapMode.Overflow;
                text.fontStyle = (style & FontStyles.Bold) != 0 ? FontStyle.Bold : FontStyle.Normal;
                if ((style & FontStyles.Italic) != 0) text.fontStyle = text.fontStyle == FontStyle.Bold ? FontStyle.BoldAndItalic : FontStyle.Italic;
                text.lineSpacing = Mathf.Max(.5f, 1 + spacing / Mathf.Max(1, fontSize));
                text.raycastTarget = raycast; text.enabled = enabled;
                newTexts.Add(go, text);
            }
            foreach (var mesh in root.GetComponentsInChildren<TMP_SubMeshUI>(true)) Object.DestroyImmediate(mesh.gameObject);
            // The base dropdown keeps its own caption/item references; preserve those separately.
            var newDropdowns = new Dictionary<GameObject, Component>();
            foreach (var old in root.GetComponentsInChildren<TMP_Dropdown>(true))
            {
                var go = old.gameObject;
                // Text references on the old component become null after replacement, so use its authored template paths.
                var template = old.template; var captionImage = old.captionImage; var itemImage = old.itemImage;
                var target = old.targetGraphic; var colors = old.colors; var navigation = old.navigation;
                var sprites = old.spriteState; var triggers = old.animationTriggers; var transition = old.transition;
                bool interactable = old.interactable; int value = old.value; float fade = old.alphaFadeSpeed;
                var options = old.options.Select(o => new UnityEngine.UI.Dropdown.OptionData(o.text, o.image)).ToList();
                string listeners = JsonUtility.ToJson(old.onValueChanged);
                Object.DestroyImmediate(old);
                var type = typeof(SettingsPanelView).Assembly.GetType("FPS.Settings.SettingsDropdown", true);
                var dropdown = (UnityEngine.UI.Dropdown)go.AddComponent(type);
                dropdown.template = template; dropdown.captionImage = captionImage; dropdown.itemImage = itemImage;
                dropdown.captionText = go.transform.Find("Label").GetComponent<UnityEngine.UI.Text>();
                dropdown.itemText = template.Find("Viewport/Content/Item/ItemLabel").GetComponent<UnityEngine.UI.Text>();
                dropdown.targetGraphic = target; dropdown.colors = colors; dropdown.navigation = navigation;
                dropdown.spriteState = sprites; dropdown.animationTriggers = triggers; dropdown.transition = transition;
                dropdown.interactable = interactable; dropdown.alphaFadeSpeed = fade;
                dropdown.options = options; JsonUtility.FromJsonOverwrite(listeners, dropdown.onValueChanged);
                dropdown.SetValueWithoutNotify(value); dropdown.RefreshShownValue();
                newDropdowns.Add(go, dropdown);
            }
            foreach (var binding in snapshot.bindings)
            {
                var ownerTransform = string.IsNullOrEmpty(binding.owner) ? root.transform : root.transform.Find(binding.owner);
                Component owner = binding.panel ? (Component)ownerTransform.GetComponent<SettingsPanelView>() : ownerTransform.GetComponent<SettingsRowView>();
                var serialized = new SerializedObject(owner);
                string name = binding.panel && binding.property == "playerName" ? "headerPlayerName" : binding.property;
                int arrayMarker = name.IndexOf(".Array.data[", StringComparison.Ordinal);
                if (arrayMarker >= 0)
                {
                    var array = serialized.FindProperty(name.Substring(0, arrayMarker));
                    int index = int.Parse(name.Substring(arrayMarker + 12).TrimEnd(']'));
                    if (array.arraySize <= index) array.arraySize = index + 1;
                }
                var property = serialized.FindProperty(name);
                Require(property != null, "Missing migrated field: " + name);
                var target = root.transform.Find(binding.target);
                property.objectReferenceValue = binding.kind == "dropdown" ? newDropdowns[target.gameObject] : newTexts[target.gameObject];
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            var view = root.GetComponent<SettingsPanelView>();
            // Other lobby pages retain their TMP base-class fields; the settings page renders its own legacy header.
            var inherited = new SerializedObject(view);
            foreach (string name in new[] { "playerName", "status", "uid" }) inherited.FindProperty(name).objectReferenceValue = null;
            inherited.ApplyModifiedPropertiesWithoutUndo();
            Require(snapshot.rowIds.SequenceEqual(view.rows.Select(r => r.settingId)), "All 34 setting IDs and rows must remain unchanged.");
            Require(root.GetComponentsInChildren<TMP_Text>(true).Length == 0 && root.GetComponentsInChildren<TMP_Dropdown>(true).Length == 0, "A TMP component remains in settings.");
            Require(newTexts.Count == snapshot.textCount && newDropdowns.Count == snapshot.dropdownCount, "Migration component counts do not match.");
            root.SetActive(wasActive);
            PrefabUtility.SaveAsPrefabAsset(root, SettingsPanelBuilder.PrefabPath);
            File.WriteAllText(Output + "/migration-result.json", "{\"success\":true,\"texts\":" + newTexts.Count + ",\"dropdowns\":" + newDropdowns.Count + ",\"bindings\":" + snapshot.bindings.Count + "}");
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
        AssetDatabase.SaveAssets();
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    public static void RestoreUnrelatedFontCache()
    {
        foreach (string path in new[] {
            "Assets/Resource/UI/Fonts/Mvc/LobbyCjkFallback.asset",
            "Assets/Resource/UI/Fonts/方圆行书书法_爱给网_aigei_com SDF.asset",
            "Assets/Resource/UI/Fonts/腾祥魅黑字体_爱给网_aigei_com SDF.asset",
            "Assets/Resource/UI/Fonts/锐字云字库粗圆体GBK_爱给网_aigei_com SDF.asset" })
        {
            string snapshot = Output + "/before/" + Path.GetFileName(path);
            Require(File.Exists(snapshot), "The pre-migration font snapshot is missing: " + path);
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path)) EditorUtility.ClearDirty(asset);
            File.WriteAllBytes(path, File.ReadAllBytes(snapshot));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
        }
    }
}
