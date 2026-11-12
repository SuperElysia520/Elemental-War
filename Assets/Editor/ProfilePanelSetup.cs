using System;
using System.IO;
using System.Linq;
using FPS.UI;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Connects the redesigned profile prefab without recreating the user's panels.</summary>
public static class ProfilePanelSetup
{
    private const string Path = "Assets/Resource/UI/Prefab/Mvc/ProfilePanel.prefab";
    public const string Output = "tmp/ProfilePanel20261110";

    [MenuItem("Tools/FPS/Lobby/Bind Redesigned Profile Panel")]
    public static void Apply()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode before binding the profile.");
        Directory.CreateDirectory(Output);
        if (!File.Exists(Output + "/ProfilePanel.before.prefab")) File.Copy(Path, Output + "/ProfilePanel.before.prefab");
        var scene = SceneManager.GetActiveScene();
        if (!string.IsNullOrEmpty(scene.path) && !File.Exists(Output + "/OpenScene.before.unity"))
            EditorSceneManager.SaveScene(scene, Output + "/OpenScene.before.unity", true);
        var root = PrefabUtility.LoadPrefabContents(Path);
        try
        {
            var view = root.GetComponent<ProfilePanelView>();
            var t = root.transform;
            view.overview = t.Find("FirstPagePanel").gameObject;
            view.information = t.Find("InformationPanel").gameObject;
            view.avatarPicker = t.Find("InformationPanel/ReviseAvatarPanel").gameObject;
            view.firstPageButton = At<UnityEngine.UI.Button>(t, "SideBar/FirstPage");
            view.informationButton = At<UnityEngine.UI.Button>(t, "SideBar/Information");
            view.editNameButton = At<UnityEngine.UI.Button>(t, "InformationPanel/Name/ReviseName");
            view.confirmNameButton = At<UnityEngine.UI.Button>(t, "InformationPanel/ReviseNamePanel/Panel/Confirm");
            view.closeNameButton = At<UnityEngine.UI.Button>(t, "InformationPanel/ReviseNamePanel/Close");
            view.nameEditor = t.Find("InformationPanel/ReviseNamePanel").gameObject;
            view.nameInput = At<UnityEngine.UI.InputField>(t, "InformationPanel/ReviseNamePanel/Panel/newName");
            view.nameInput.characterLimit = 20; view.nameInput.lineType = UnityEngine.UI.InputField.LineType.SingleLine;
            view.signatureInput = At<TMP_InputField>(t, "FirstPagePanel/UP/Signature");
            view.signatureInput.characterLimit = 80; view.signatureInput.lineType = TMP_InputField.LineType.SingleLine;
            view.signatureInput.richText = false;
            view.maleToggle = At<UnityEngine.UI.Toggle>(t, "InformationPanel/Sex/boy");
            view.femaleToggle = At<UnityEngine.UI.Toggle>(t, "InformationPanel/Sex/girl");
            view.maleToggle.SetIsOnWithoutNotify(false); view.femaleToggle.SetIsOnWithoutNotify(false);
            view.genderIcon = At<UnityEngine.UI.Image>(t, "FirstPagePanel/UP/SexIcon");
            view.maleIcon = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Resource/UI/Profile/Boy.png");
            view.femaleIcon = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Resource/UI/Profile/Girl.png");
            view.genderIcon.raycastTarget = false;
            view.matchesText = Stat(t, "Session"); view.winsText = Stat(t, "Victory matches"); view.killsText = Stat(t, "Kill count");
            view.kdText = Stat(t, "KD"); view.deathsText = Stat(t, "Session (4)"); view.headshotRateText = Stat(t, "Headshot Rate");
            foreach (var label in new[] { view.overviewName, view.playerName }) if (label != null) label.richText = false;
            view.nameEditor.SetActive(false); view.avatarPicker.SetActive(false);
            view.overview.SetActive(true); view.information.SetActive(false);
            ConfigureScroll(view);
            EditorUtility.SetDirty(view);
            PrefabUtility.SaveAsPrefabAsset(root, Path);
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
        AssetDatabase.Refresh();
        File.WriteAllText(Output + "/setup.json", "{\"success\":true,\"prefab\":\"" + Path + "\"}");
    }

    private static T At<T>(Transform root, string path) where T : Component
        => root.Find(path)?.GetComponent<T>() ?? throw new InvalidOperationException("Missing " + typeof(T).Name + ": " + path);
    private static UnityEngine.UI.Text Stat(Transform root, string name) => At<UnityEngine.UI.Text>(root, "FirstPagePanel/Down/DataPanel/" + name + "/number");

    private static void ConfigureScroll(ProfilePanelView view)
    {
        var scroll = At<UnityEngine.UI.ScrollRect>(view.transform, "InformationPanel/ReviseAvatarPanel/AvatarOptions");
        var content = (RectTransform)scroll.transform.Find("Content");
        if (content == null) content = (RectTransform)scroll.transform.Find("Viewport/Content");
        var viewport = scroll.transform.Find("Viewport") as RectTransform;
        if (viewport == null)
        {
            viewport = (RectTransform)new GameObject("Viewport", typeof(RectTransform), typeof(UnityEngine.UI.RectMask2D)).transform;
            viewport.SetParent(scroll.transform, false);
            viewport.anchorMin = content.anchorMin; viewport.anchorMax = content.anchorMax;
            viewport.pivot = content.pivot; viewport.sizeDelta = content.sizeDelta; viewport.anchoredPosition = content.anchoredPosition;
            viewport.SetAsFirstSibling();
        }
        if (viewport.GetComponent<UnityEngine.UI.RectMask2D>() == null) viewport.gameObject.AddComponent<UnityEngine.UI.RectMask2D>();
        content.SetParent(viewport, false);
        content.anchorMin = new Vector2(0, 1); content.anchorMax = Vector2.one; content.pivot = new Vector2(0.5f, 1);
        content.anchoredPosition = Vector2.zero; content.sizeDelta = Vector2.zero;
        var fitter = content.GetComponent<UnityEngine.UI.ContentSizeFitter>() ?? content.gameObject.AddComponent<UnityEngine.UI.ContentSizeFitter>();
        fitter.horizontalFit = UnityEngine.UI.ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
        scroll.content = content; scroll.viewport = viewport; scroll.horizontal = false; scroll.vertical = true;
        scroll.movementType = UnityEngine.UI.ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 40;
        scroll.verticalScrollbarVisibility = UnityEngine.UI.ScrollRect.ScrollbarVisibility.Permanent;
        if (scroll.verticalScrollbar == null) throw new InvalidOperationException("Avatar list is missing its vertical scrollbar.");
        scroll.verticalScrollbar.direction = UnityEngine.UI.Scrollbar.Direction.BottomToTop;
        scroll.verticalScrollbar.gameObject.SetActive(true);
        var track = scroll.verticalScrollbar.GetComponent<UnityEngine.UI.Image>();
        if (track != null && track.color.a == 0) track.color = new Color(0.08f, 0.14f, 0.28f, 0.55f);
        var handle = scroll.verticalScrollbar.targetGraphic;
        if (handle != null && handle.color.a == 0) handle.color = Color.white;
        var bar = (RectTransform)scroll.verticalScrollbar.transform;
        bar.anchorMin = bar.anchorMax = new Vector2(0.5f, 0.5f); bar.pivot = new Vector2(0.5f, 0.5f);
        bar.anchoredPosition = viewport.anchoredPosition + new Vector2(viewport.sizeDelta.x * 0.5f + 18, 0);
        bar.sizeDelta = new Vector2(20, viewport.sizeDelta.y);
        view.avatarScroll = scroll;
        UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(content);
    }
}
