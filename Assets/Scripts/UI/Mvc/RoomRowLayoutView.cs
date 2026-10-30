using UnityEngine;

namespace FPS.UI
{
    /// <summary>Fits the authored room-card artwork to its list without moving its internal controls.</summary>
    public sealed class RoomRowLayoutView : MonoBehaviour
    {
        public RectTransform visuals;
        public UnityEngine.UI.LayoutElement layout;
        public Vector2 nativeSize = new Vector2(800, 150);
        private bool fitting;
        private void OnEnable() => Fit();
        private void OnRectTransformDimensionsChange() => Fit();
        private void Fit()
        {
            if (fitting || visuals == null || layout == null || !(transform.parent is RectTransform parent) || parent.rect.width <= 0) return;
            fitting = true;
            float available = parent.rect.width;
            var scroll = GetComponentInParent<UnityEngine.UI.ScrollRect>();
            if (scroll != null && scroll.viewport != null && scroll.viewport.rect.width > 0) available = Mathf.Min(available, scroll.viewport.rect.width);
            available = Mathf.Max(1f, available - 24f);
            float scale = Mathf.Min(1f, available / nativeSize.x);
            visuals.localScale = new Vector3(scale, scale, 1);
            visuals.anchoredPosition = new Vector2(-(parent.rect.width - nativeSize.x * scale) * 0.5f, 0);
            layout.preferredHeight = nativeSize.y * scale;
            layout.preferredWidth = parent.rect.width;
            ((RectTransform)transform).SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, parent.rect.width);
            ((RectTransform)transform).SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, layout.preferredHeight);
            fitting = false;
        }
    }
}
