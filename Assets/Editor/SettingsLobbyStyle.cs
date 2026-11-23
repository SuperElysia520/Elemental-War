using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FPS.Settings;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

public static partial class SettingsPanelBuilder
{
    private static readonly Color LobbyWhite = SettingsPalette.White;
    private static Font lobbyHeading;

    [MenuItem("Tools/FPS/Settings/Apply Blue Lobby Style")]
    public static void ApplyLobbyStyle()
    {
        Require(!EditorApplication.isPlayingOrWillChangePlaymode, "Stop Play Mode before styling settings.");
        chinese = SettingsLegacyTextMigration.EnsureBodyFont();
        latin = chinese;
        Require(chinese != null, "The existing settings font is missing.");
        lobbyHeading = PrepareLobbyHeading();
        var go = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            var view = go.GetComponent<SettingsPanelView>();
            Require(view != null && view.rows.Length == 34, "Expected the existing settings view and all 34 controls.");
            var ids = view.rows.Select(r => r.settingId).ToArray();
            foreach (var text in go.GetComponentsInChildren<UnityEngine.UI.Text>(true))
            {
                // The dropdown uses a mesh chevron; clear its old font glyph as well as hiding it.
                if (text.name == "Caret") text.text = "";
                text.font = chinese; 
                text.color = LobbyWhite;
            }
            foreach (var button in go.GetComponentsInChildren<UnityEngine.UI.Button>(true))
            {
                var colors = button.colors;
                colors.normalColor = Color.white; colors.selectedColor = Color.white;
                colors.highlightedColor = new Color(1, 1, 1, 1); colors.pressedColor = new Color(.82f, .91f, 1, 1);
                colors.disabledColor = new Color(.92f, .96f, 1, .78f); colors.fadeDuration = .12f; button.colors = colors;
            }
            var background = go.transform.Find("Background").GetComponent<UnityEngine.UI.RawImage>();
            background.texture = null; background.color = C("0869B4");
            var stage = (RectTransform)go.transform.Find("Stage");
            var backdrop = FindOrShape("LobbyBackdrop", stage, 0, 0, 1920, 1080, C("0876C5"), 0);
            Stretch(backdrop.rectTransform); backdrop.transform.SetAsFirstSibling();
            backdrop.gradient = true; backdrop.verticalGradient = true; backdrop.endColor = C("0757A1");

            StyleLobbyHeader(stage, view);
            StyleLobbyNavigation(stage, view);
            var main = (RectTransform)stage.Find("ContentPanel");
            Place(main, 365, 134, 1515, 814);
            Round(main.GetComponent<SettingsShape>(), new Color(.32f, .68f, .92f, .43f), 24);
            StyleBorder(main, 24);
            Hide(main, "TopAccent", "InnerFrame", "Watermark", "WatermarkIndex", "DecorativeArc");
            var header = (RectTransform)main.Find("PageHeader"); Place(header, 0, 0, 1515, 110);
            Hide(header, "PageNumber", "ProfileCaption", "ProfileValue");
            TextAt(view.titleText, 36, 18, 620, 64, 44, true);
            TextAt(view.titleEnglish, 780, 36, 690, 30, 20); if(view.titleEnglish!=null)view.titleEnglish.alignment = TextAnchor.MiddleRight;
            var separator = header.Find("Separator").GetComponent<SettingsShape>();
            Place(separator.rectTransform, 36, 100, 1443, 2); separator.color = new Color(1, 1, 1, .6f);
            for (int i = 0; i < view.scrollViews.Length; i++) StyleLobbyPage(view.scrollViews[i], i);
            foreach (var row in view.rows) StyleLobbyRow(row, row.transform.parent.name == "Parameters" ? 700 : 1443, row.transform.parent.name == "Parameters" ? 350 : 650, row.transform.parent.name == "Parameters" ? 68 : 92);
            StyleLobbyCrosshair(view);
            StyleLobbyFooter(stage, view);
            StyleLobbyOverlays(stage, view);
            view.InitializeEditorPreview();
            Require(ids.SequenceEqual(view.rows.Select(r => r.settingId)), "Styling must preserve all existing setting bindings.");
            Require(go.GetComponentsInChildren<SettingsRowView>(true).Length == 34, "Styling must not add or remove settings.");
            PrefabUtility.SaveAsPrefabAsset(go, PrefabPath);
        }
        finally { PrefabUtility.UnloadPrefabContents(go); }
        AssetDatabase.SaveAssets();
        Validate();
        Debug.Log("[Settings] Blue lobby style applied to the existing prefab; all settings bindings retained.");
    }

    private static Font PrepareLobbyHeading()
    {
        var font=AssetDatabase.LoadAssetAtPath<Font>("Assets/Resource/Fonts/印品抹茶体.ttf");
        Require(font!=null,"The original lobby heading font is missing.");
        return font;
    }

    private static void StyleLobbyHeader(RectTransform stage, SettingsPanelView view)
    {
        var header = (RectTransform)stage.Find("Header"); Place(header, 0, 0, 1920, 96);
        foreach (Transform child in header) child.gameObject.SetActive(child == view.backButton.transform);
        var bar = FindOrShape("LobbyBar", header, 0, 0, 1920, 96, C("075493"), 0); bar.transform.SetAsFirstSibling();
        StyleLobbyButton(view.backButton, "返回大厅", 36, 17, 280, 62, false, true);
        view.backButton.targetGraphic.color = Color.clear;
        Hide(view.backButton.transform, "Border", "EscapeKey");
        var label = view.backButton.transform.Find("Label").GetComponent<UnityEngine.UI.Text>(); TextAt(label, 76, 0, 204, 62, 32, true);
        var arrow = FindOrRect("LobbyBackIcon", view.backButton.transform, 0, 6, 66, 50);
        var image = arrow.GetComponent<UnityEngine.UI.Image>() ?? arrow.gameObject.AddComponent<UnityEngine.UI.Image>();
        image.sprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Resource/UI/UI/back-icon.png"); image.color = Color.white; image.preserveAspect = true; image.raycastTarget = false;
        var divider = FindOrShape("LobbyDivider", header, 337, 23, 2, 50, new Color(1, 1, 1, .38f), 0);
        var title = FindOrText("LobbyTitle", header, "设置", 380, 10, 330, 74, 50, true);
        var gear = FindOrRect("LobbyGear", header, 1794, 19, 58, 58);
        var gearImage = gear.GetComponent<UnityEngine.UI.Image>() ?? gear.gameObject.AddComponent<UnityEngine.UI.Image>();
        gearImage.sprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Resource/UI/UI/settings.png"); gearImage.color = Color.white; gearImage.preserveAspect = true; gearImage.raycastTarget = false;
    }

    private static void StyleLobbyNavigation(RectTransform stage, SettingsPanelView view)
    {
        var nav = (RectTransform)stage.Find("Navigation"); Place(nav, 40, 134, 292, 814);
        nav.GetComponent<SettingsShape>().color = Color.clear;
        foreach (Transform child in nav) if (!view.tabButtons.Any(b => b.transform == child) && !child.name.StartsWith("Quick")) child.gameObject.SetActive(false);
        for (int i = 0; i < view.tabButtons.Length; i++)
        {
            var button = view.tabButtons[i]; Place((RectTransform)button.transform, 0, 12 + i * 116, 292, 96);
            Round(view.tabHighlights[i], i == 0 ? SettingsPalette.Gold : SettingsPalette.Tab, 20);
            StyleBorder(button.transform, 20);
            Hide(button.transform, "ActiveEdge", "Index", "English", "Separator", "Label");
            var icon = button.transform.Find("Icon")?.GetComponent<UnityEngine.UI.Graphic>(); if(icon!=null)Place(icon.rectTransform, 30, 24, 48, 48); if(icon is SettingsShape shape)shape.stroke = 3.6f;
            TextAt(view.tabLabels[i], 106, 12, 170, 72, 36, true);
        }
        var quickCaption = nav.Find("QuickCaption").GetComponent<UnityEngine.UI.Text>(); quickCaption.gameObject.SetActive(true);
        quickCaption.text = "快速预设"; TextAt(quickCaption, 10, 648, 260, 34, 22, true);
        Hide(nav, "QuickSeparator", "QuickEnglish");
        float width = (292 - (view.quickButtons.Length - 1) * 8f) / view.quickButtons.Length;
        for (int i = 0; i < view.quickButtons.Length; i++) StyleLobbyButton(view.quickButtons[i], SettingsSchema.QualityNames[i], i * (width + 8), 700, width, 48, false);
    }

    private static void StyleLobbyPage(UnityEngine.UI.ScrollRect scroll, int tab)
    {
        Place((RectTransform)scroll.transform, 36, 118, 1443, 668);
        Stretch(scroll.viewport); scroll.viewport.offsetMax = new Vector2(-14, 0);
        var layout = scroll.content.GetComponent<UnityEngine.UI.VerticalLayoutGroup>();
        layout.spacing = 10; layout.padding = new RectOffset(0, 0, 0, 8);
        foreach (Transform child in scroll.content)
        {
            if (child.name.StartsWith("Section"))
            {
                SetHeight(child.gameObject, 50);
                Hide(child, "Background", "TopAccent", "English", "Rule", "Number", "Separator");
                var glyph = child.Find("Glyph").GetComponent<SettingsShape>(); Round(glyph, C("50F2F3"), 4); Place(glyph.rectTransform, 0, 12, 7, 27);
                var label = child.Find("Chinese").GetComponent<UnityEngine.UI.Text>(); TextAt(label, 22, 3, 700, 44, 27, true);
            }
            if (child.name == "FixedKeysHint") { var text = child.GetComponent<UnityEngine.UI.Text>(); text.fontSize = 18; SetHeight(child.gameObject, 50); }
        }
        var bar = (RectTransform)scroll.verticalScrollbar.transform; Place(bar, 1433, 0, 6, 668);
        bar.GetComponent<UnityEngine.UI.Image>().color = new Color(1, 1, 1, .12f);
        var handle = scroll.verticalScrollbar.handleRect.GetComponent<SettingsShape>(); Round(handle, new Color(1, 1, 1, .72f), 3);
        Stretch(scroll.verticalScrollbar.handleRect.parent as RectTransform); Stretch(scroll.verticalScrollbar.handleRect);
    }

    private static void StyleLobbyRow(SettingsRowView row, float width, float controlWidth, float height)
    {
        SetHeight(row.gameObject, height); var rect = (RectTransform)row.transform; rect.sizeDelta = new Vector2(width, height);
        var bg = row.transform.Find("Background").GetComponent<SettingsShape>(); Round(bg, new Color(.57f, .83f, 1, .25f), 13); Stretch(bg.rectTransform);
        Hide(row.transform, "Separator", "English", "Leader", "Endpoint");
        bool compact = height < 80; float x = width - controlWidth - 18;
        var label = row.transform.Find("Label").GetComponent<UnityEngine.UI.Text>();
        bool hint = row.transform.Find("Hint") != null;
        TextAt(label, 22, hint ? 9 : (height - 42) / 2, x - 40, 42, compact ? 21 : 25);
        if (hint) TextAt(row.transform.Find("Hint").GetComponent<UnityEngine.UI.Text>(), 22, compact ? 43 : 53, x - 40, 26, compact ? 13 : 16);
        var control = (RectTransform)row.transform.Find("Control"); Place(control, x, (height - 54) / 2, controlWidth, 54);
        if (row.choices.Length > 0)
        {
            var border = control.Find("Border").GetComponent<SettingsShape>(); Round(border, new Color(1, 1, 1, .8f), 11); border.outline = true; border.stroke = 1.5f; Place(border.rectTransform, 0, 0, controlWidth, 52);
            float w = (controlWidth - 6) / row.choices.Length;
            for (int i = 0; i < row.choices.Length; i++)
            {
                Place((RectTransform)row.choices[i].transform, 3 + i * w, 3, w, 46);
                Round(row.choiceBackgrounds[i], Color.clear, 9);
                TextAt(row.choiceLabels[i], 0, 0, w, 46, compact ? 18 : 23); row.choiceLabels[i].alignment = TextAnchor.MiddleCenter;
            }
        }
        if (row.slider != null)
        {
            var slider = row.slider; float trackWidth = controlWidth - 110;
            Place((RectTransform)slider.transform, 8, 6, trackWidth, 42);
            var track = slider.transform.Find("Track").GetComponent<SettingsShape>(); Round(track, new Color(.68f, .88f, 1, .75f), 6); Place(track.rectTransform, 0, 15, trackWidth, 12);
            var area = (RectTransform)slider.transform.Find("FillArea"); Place(area, 0, 15, trackWidth, 12);
            Round(slider.fillRect.GetComponent<SettingsShape>(), SettingsPalette.Gold, 6); slider.fillRect.sizeDelta = Vector2.zero;
            Place((RectTransform)slider.handleRect.parent, 0, 21, trackWidth, 0);
            Round(slider.handleRect.GetComponent<SettingsShape>(), Color.white, 15); slider.handleRect.sizeDelta = new Vector2(28, 28); slider.handleRect.anchoredPosition = Vector2.zero;
            var knobBorder = FindOrShape("LobbyBorder", slider.handleRect, 0, 0, 28, 28, SettingsPalette.Gold, 14); knobBorder.outline = true; knobBorder.stroke = 2; Stretch(knobBorder.rectTransform);
            foreach (Transform child in slider.transform) if (child.name.StartsWith("Tick")) child.gameObject.SetActive(false);
            TextAt(row.valueText, controlWidth - 104, 3, 104, 46, compact ? 20 : 25); row.valueText.alignment = TextAnchor.MiddleRight;
        }
        if (row.toggle != null)
        {
            Place((RectTransform)row.toggle.transform, 8, 5, 190, 44);
            Round(row.toggleTrack, SettingsPalette.Control, 22); Place(row.toggleTrack.rectTransform, 0, 0, 86, 44);
            var border = row.toggle.transform.Find("Outline").GetComponent<SettingsShape>(); Round(border, new Color(1, 1, 1, .8f), 22); border.outline = true; Place(border.rectTransform, 0, 0, 86, 44);
            Round(row.toggleKnob.GetComponent<SettingsShape>(), Color.white, 17); row.toggleKnob.sizeDelta = new Vector2(34, 34);
            TextAt(row.valueText, 110, 0, 100, 44, compact ? 20 : 24);
        }
        if (row.dropdown != null)
        {
            var dropdown = row.dropdown; Place((RectTransform)dropdown.transform, 0, 0, controlWidth, 52);
            Round(dropdown.GetComponent<SettingsShape>(), SettingsPalette.Control, 11); StyleBorder(dropdown.transform, 11);
            TextAt(dropdown.captionText, 18, 2, controlWidth - 72, 48, compact ? 20 : 24);
            Hide(dropdown.transform, "Caret");
            var caret = FindOrShape("LobbyCaret", dropdown.transform, controlWidth - 43, 16, 24, 20, LobbyWhite, 0); caret.shape = SettingsShape.ShapeKind.Chevron; caret.stroke = 3;
            var template = dropdown.template; Round(template.GetComponent<SettingsShape>(), C("197ABC"), 12); StyleBorder(template, 12);
            template.sizeDelta = new Vector2(0, 260); template.anchoredPosition = new Vector2(0, -6);
            var content = (RectTransform)template.Find("Viewport/Content"); content.sizeDelta = new Vector2(0, 46);
            var item = (RectTransform)content.Find("Item"); item.sizeDelta = new Vector2(0, 46);
            var itemBg = item.Find("Background").GetComponent<SettingsShape>(); Round(itemBg, new Color(1, 1, 1, .12f), 6); Stretch(itemBg.rectTransform);
            var selected = item.Find("Selected").GetComponent<SettingsShape>(); selected.color = SettingsPalette.Gold; Place(selected.rectTransform, 0, 0, 5, 46);
            TextAt(dropdown.itemText, 18, 0, controlWidth - 42, 46, compact ? 20 : 23);
        }
        if (row.keyButton != null)
        {
            StyleLobbyButton(row.keyButton, row.valueText.text, 0, 0, controlWidth, 52, false);
            Hide(row.keyButton.transform, "Underline"); row.valueText.font = chinese; 
        }
        if (SettingsSchema.Find(row.settingId).kind == SettingKind.Info)
        {
            var border = control.Find("ReadOnlyBorder").GetComponent<SettingsShape>(); Round(border, new Color(1, 1, 1, .45f), 11); border.outline = true; Place(border.rectTransform, 0, 0, controlWidth, 52);
            TextAt(row.valueText, 18, 2, controlWidth - 36, 48, 24);
        }
    }

    private static void StyleLobbyCrosshair(SettingsPanelView view)
    {
        var body = view.preview.transform.parent.parent as RectTransform; SetHeight(body.gameObject, 598);
        var field = view.preview.transform.parent as RectTransform; Place(field, 0, 6, 700, 550);
        Round(field.GetComponent<SettingsShape>(), C("105D9D"), 18); StyleBorder(field, 18);
        var grid = field.Find("Grid").GetComponent<SettingsShape>(); grid.color = new Color(.7f, .88f, 1, .065f); Stretch(grid.rectTransform);
        foreach (Transform child in field) if (child.name.StartsWith("Ring") || child.name.StartsWith("Corner")) child.gameObject.SetActive(false);
        var range = field.Find("Range").GetComponent<UnityEngine.UI.Text>(); range.text = "准心预览"; TextAt(range, 22, 16, 500, 36, 24, true);
        view.preview.rectTransform.anchoredPosition = new Vector2(350, -264);
        var zoom = view.zoomPreview.transform.parent as RectTransform; Place(zoom, 22, 382, 150, 150);
        Round(zoom.GetComponent<SettingsShape>(), C("0A487F"), 14); StyleBorder(zoom, 14);
        var zoomLabel = zoom.Find("ZoomLabel").GetComponent<UnityEngine.UI.Text>(); zoomLabel.text = "放大 ×2.3"; TextAt(zoomLabel, 8, 119, 136, 24, 15); zoomLabel.alignment = TextAnchor.MiddleRight;
        TextAt(view.previewDescription, 186, 502, 490, 40, 17); view.previewDescription.alignment = TextAnchor.MiddleRight;
        var scale = body.Find("ScaleLabel").GetComponent<UnityEngine.UI.Text>(); TextAt(scale, 0, 566, 970, 30, 18);
        TextAt(view.previewMode, 1110, 566, 320, 30, 18); view.previewMode.alignment = TextAnchor.MiddleRight;
        var parameters = (RectTransform)body.Find("Parameters"); Place(parameters, 730, 6, 700, 550);
        int index = 0; foreach (var row in parameters.GetComponentsInChildren<SettingsRowView>(true)) Place((RectTransform)row.transform, 0, index++ * 68, 700, 64);
    }

    private static void StyleLobbyFooter(RectTransform stage, SettingsPanelView view)
    {
        var footer = (RectTransform)stage.Find("Footer"); Place(footer, 40, 970, 1840, 78);
        Hide(footer, "Separator", "Accent", "StatusOutline", "StatusDot");
        TextAt(view.saveStatus, 330, 5, 520, 30, 20);
        TextAt(view.saveTime, 330, 38, 490, 26, 16);
        var hint = footer.Find("KeyboardHint").GetComponent<UnityEngine.UI.Text>(); hint.text = "Ctrl + Enter 应用设置"; TextAt(hint, 875, 22, 430, 32, 18);
        StyleLobbyButton(view.resetButton, "恢复默认", 0, 2, 292, 64, false, true);
        StyleLobbyButton(view.cancelButton, "取消", 1320, 2, 200, 64, false, true);
        StyleLobbyButton(view.applyButton, "应用设置", 1550, 0, 290, 68, true, true);
    }

    private static void StyleLobbyOverlays(RectTransform stage, SettingsPanelView view)
    {
        var feedback = (RectTransform)view.feedback.transform; Place(feedback, 570, 876, 1040, 60);
        Round(feedback.GetComponent<SettingsShape>(), C("075493"), 16); StyleBorder(feedback, 16); Hide(feedback, "Accent");
        TextAt(view.feedbackText, 20, 9, 1000, 42, 23); view.feedbackText.alignment = TextAnchor.MiddleCenter;
        view.modal.GetComponent<UnityEngine.UI.Image>().color = new Color(.02f, .15f, .28f, .75f);
        var box = (RectTransform)view.modal.transform.Find("Dialog"); Place(box, 600, 340, 720, 390);
        Round(box.GetComponent<SettingsShape>(), C("237DC0"), 24); StyleBorder(box, 24);
        Hide(box, "Kicker"); TextAt(view.modalTitle, 36, 32, 648, 64, 32, true);
        TextAt(view.modalBody, 36, 112, 648, 140, 24); view.modalBody.horizontalOverflow = HorizontalWrapMode.Wrap;
        StyleLobbyButton(view.modalCancel, "取消", 286, 294, 160, 60, false);
        StyleLobbyButton(view.modalConfirm, "确认", 466, 294, 218, 60, true);
    }

    private static void StyleLobbyButton(UnityEngine.UI.Button button, string label, float x, float y, float width, float height, bool primary, bool heading = false)
    {
        Place((RectTransform)button.transform, x, y, width, height);
        var fill = button.targetGraphic as SettingsShape;
        if (fill != null)
        {
            Round(fill, primary ? SettingsPalette.Gold : SettingsPalette.Tab, 15);
            fill.gradient = primary; fill.verticalGradient = true; fill.endColor = C("FFCA2E");
        }
        StyleBorder(button.transform, 15);
        var text = button.transform.Find("Label").GetComponent<UnityEngine.UI.Text>(); text.text = label;
        TextAt(text, 6, 0, width - 12, height, heading ? 31 : 23, heading); text.alignment = TextAnchor.MiddleCenter; text.color = primary ? SettingsPalette.Navy : LobbyWhite;
    }
    private static void Round(SettingsShape shape, Color color, float radius)
    { shape.shape = SettingsShape.ShapeKind.RoundedPanel; shape.bevel = radius; shape.color = color; shape.gradient = false; shape.outline = false; shape.SetVerticesDirty(); }
    private static void StyleBorder(Transform parent, float radius)
    {
        var border = parent.Find("Border"); if (border == null) return;
        border.gameObject.SetActive(true); var shape = border.GetComponent<SettingsShape>();
        Round(shape, new Color(.78f, .94f, 1, .65f), radius); shape.outline = true; shape.stroke = 1.8f; Stretch(shape.rectTransform, 1);
    }
    private static void TextAt(UnityEngine.UI.Text text, float x, float y, float width, float height, float size, bool heading = false)
    { if(text==null)return; Place(text.rectTransform, x, y, width, height); text.font = heading ? lobbyHeading : chinese;  text.fontSize = Mathf.RoundToInt(size); text.color = LobbyWhite; text.horizontalOverflow = HorizontalWrapMode.Overflow; }
    private static void Place(RectTransform rect, float x, float y, float width, float height)
    { rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1); rect.anchoredPosition = new Vector2(x, -y); rect.sizeDelta = new Vector2(width, height); }
    private static void SetHeight(GameObject go, float height)
    { var layout = go.GetComponent<UnityEngine.UI.LayoutElement>() ?? go.AddComponent<UnityEngine.UI.LayoutElement>(); layout.minHeight = layout.preferredHeight = height; layout.flexibleHeight = 0; }
    private static void Hide(Transform parent, params string[] names)
    { foreach (string name in names) { var child = parent.Find(name); if (child != null) child.gameObject.SetActive(false); } }
    private static RectTransform FindOrRect(string name, Transform parent, float x, float y, float width, float height)
    { var rect = parent.Find(name) as RectTransform; if (rect == null) rect = R(name, parent, x, y, width, height); else { rect.gameObject.SetActive(true); Place(rect, x, y, width, height); } return rect; }
    private static SettingsShape FindOrShape(string name, Transform parent, float x, float y, float width, float height, Color color, float radius)
    { var rect = FindOrRect(name, parent, x, y, width, height); var shape = rect.GetComponent<SettingsShape>() ?? rect.gameObject.AddComponent<SettingsShape>(); Round(shape, color, radius); shape.raycastTarget = false; return shape; }
    private static UnityEngine.UI.Text FindOrText(string name, Transform parent, string value, float x, float y, float width, float height, float size, bool heading)
    { var rect = FindOrRect(name, parent, x, y, width, height); var text = rect.GetComponent<UnityEngine.UI.Text>() ?? rect.gameObject.AddComponent<UnityEngine.UI.Text>(); text.text = value; text.raycastTarget = false; TextAt(text, x, y, width, height, size, heading); return text; }
}
