using System;
using FPS.Settings;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FPS.UI
{
    internal sealed class LobbySocialStyle
    {
        internal static readonly Color Blue = new Color32(8, 118, 197, 255);
        internal static readonly Color Header = new Color32(7, 84, 147, 255);
        internal static readonly Color Panel = new Color32(50, 142, 204, 255);
        internal static readonly Color Row = new Color32(71, 155, 211, 255);
        internal static readonly Color Selected = new Color32(83, 170, 224, 255);
        internal static readonly Color Rim = new Color32(194, 230, 254, 225);
        internal static readonly Color Red = new Color32(231, 70, 62, 255);
        private readonly TMP_FontAsset body;
        private readonly Font heading;
        private readonly PlayerAvatarCatalog avatars;

        internal LobbySocialStyle(TMP_FontAsset body, Font heading, PlayerAvatarCatalog avatars)
        {
            this.body = body; this.heading = heading; this.avatars = avatars;
        }

        internal static RectTransform Rect(Transform parent, string name, float x, float y, float width, float height)
        {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
            return rect;
        }

        internal static void Fill(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        internal static SettingsShape Shape(RectTransform rect, Color color, float radius, bool border = false)
        {
            var shape = rect.gameObject.AddComponent<SettingsShape>();
            shape.shape = radius > 0 ? SettingsShape.ShapeKind.RoundedPanel : SettingsShape.ShapeKind.Panel;
            shape.bevel = radius;
            shape.color = color;
            shape.raycastTarget = false;
            if (border)
            {
                var rim = Shape(Rect(rect, "Outline", 0, 0, 0, 0), Rim, radius);
                Fill(rim.rectTransform);
                rim.outline = true;
                rim.stroke = 1.5f;
            }
            return shape;
        }

        internal static RectTransform Box(Transform parent, string name, float x, float y, float w, float h,
            Color color, float radius = 16, bool border = false)
        {
            var rect = Rect(parent, name, x, y, w, h);
            Shape(rect, color, radius, border);
            return rect;
        }

        internal TextMeshProUGUI Label(Transform parent, string value, float x, float y, float w, float h,
            int size = 23, Color? color = null, TextAlignmentOptions alignment = TextAlignmentOptions.MidlineLeft)
        {
            var label = Rect(parent, "Text", x, y, w, h).gameObject.AddComponent<TextMeshProUGUI>();
            label.font = body;
            label.fontSize = size;
            label.text = value ?? "";
            label.color = color ?? SettingsPalette.White;
            label.alignment = alignment;
            label.richText = false;
            label.raycastTarget = false;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
            return label;
        }

        internal void Heading(Transform parent, string value, float x, float y, float w, float h, int size = 38)
        {
            if (heading == null) { Label(parent, value, x, y, w, h, size); return; }
            var label = Rect(parent, "Heading", x, y, w, h).gameObject.AddComponent<UnityEngine.UI.Text>();
            label.font = heading;
            label.fontSize = size;
            label.text = value;
            label.alignment = TextAnchor.MiddleLeft;
            label.color = SettingsPalette.White;
            label.raycastTarget = false;
        }

        internal static UnityEngine.UI.Button Click(RectTransform rect, Action action)
        {
            var button = rect.gameObject.AddComponent<UnityEngine.UI.Button>();
            var graphic = rect.GetComponent<SettingsShape>();
            graphic.raycastTarget = true;
            button.targetGraphic = graphic;
            var colors = button.colors;
            colors.highlightedColor = new Color(.9f, .96f, 1);
            colors.pressedColor = new Color(.73f, .86f, 1);
            colors.selectedColor = Color.white;
            colors.disabledColor = new Color(.65f, .76f, .84f, .68f);
            button.colors = colors;
            button.onClick.AddListener(() => action());
            return button;
        }

        internal UnityEngine.UI.Button Button(Transform parent, string value, float x, float y, float w, float h,
            Action action, bool primary = false, bool enabled = true, int size = 22)
        {
            var rect = Box(parent, value, x, y, w, h, primary ? SettingsPalette.Gold : SettingsPalette.Control, 14, true);
            var button = Click(rect, action);
            button.interactable = enabled;
            Label(rect, value, 0, 0, w, h, size, primary ? SettingsPalette.Navy : SettingsPalette.White, TextAlignmentOptions.Center);
            return button;
        }

        internal void Avatar(Transform parent, byte id, float x, float y, float size)
        {
            var img = Rect(parent, "Avatar", x, y, size, size).gameObject.AddComponent<UnityEngine.UI.Image>();
            img.sprite = avatars?.Get(id);
            img.enabled = img.sprite != null;
            img.preserveAspect = true;
            img.raycastTarget = false;
        }

        internal static void Icon(Transform parent, UnifiedLobbyIcon.Symbol symbol, float x, float y, float size, Color? color = null)
        {
            var graphic = Rect(parent, symbol.ToString(), x, y, size, size).gameObject.AddComponent<UnifiedLobbyIcon>();
            graphic.symbol = symbol;
            graphic.color = color ?? SettingsPalette.White;
            graphic.raycastTarget = false;
        }

        internal TMP_InputField Input(Transform parent, string placeholder, float x, float y, float w, float h, int max)
        {
            var rect = Box(parent, "Input", x, y, w, h, Header, 13, true);
            var input = rect.gameObject.AddComponent<TMP_InputField>();
            input.targetGraphic = rect.GetComponent<SettingsShape>();
            input.targetGraphic.raycastTarget = true;
            var viewport = Rect(rect, "Text Area", 0, 0, 0, 0);
            Fill(viewport);
            viewport.offsetMin = new Vector2(16, 4);
            viewport.offsetMax = new Vector2(-16, -4);
            viewport.gameObject.AddComponent<RectMask2D>();
            input.textViewport = viewport;
            var edit = Label(viewport, "", 0, 0, w - 32, h - 8);
            edit.overflowMode = TextOverflowModes.Overflow;
            Fill(edit.rectTransform);
            input.textComponent = edit;
            var hint = Label(viewport, placeholder, 0, 0, w - 32, h - 8, 22, SettingsPalette.Secondary);
            Fill(hint.rectTransform);
            input.placeholder = hint;
            input.characterLimit = max;
            input.richText = false;
            return input;
        }

        internal static ScrollRect Scroll(Transform parent, float x, float y, float w, float h)
        {
            var root = Rect(parent, "Scroll", x, y, w, h);
            var scroll = root.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 44;
            var viewport = Rect(root, "Viewport", 0, 0, 0, 0);
            Fill(viewport);
            var hit = viewport.gameObject.AddComponent<UnityEngine.UI.Image>();
            hit.color = Color.clear;
            viewport.gameObject.AddComponent<RectMask2D>();
            scroll.viewport = viewport;
            scroll.content = Rect(viewport, "Content", 0, 0, w, 0);
            return scroll;
        }

        internal static void SizeContent(ScrollRect scroll, float height, float position)
        {
            scroll.content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
            scroll.StopMovement();
            scroll.content.anchoredPosition = new Vector2(0, Mathf.Max(0, height - scroll.viewport.rect.height) * (1 - position));
        }
    }
}
