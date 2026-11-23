using UnityEngine;

namespace FPS.UI
{
    /// <summary>Fits the authored PC layout into narrow and ultrawide screens without hiding controls.</summary>
    [ExecuteAlways]
    public sealed class UnifiedLobbyLayout : MonoBehaviour
    {
        public RectTransform stage;
        [SerializeField] private float referenceFieldOfView = 32f;
        private Camera presentationCamera;
        public void BindCamera(Camera camera) { presentationCamera = camera; Refresh(); }
        private void LateUpdate() => Refresh();
        public void Refresh()
        {
            if (stage == null || !(transform is RectTransform area)) return;
            float scale = Mathf.Min(area.rect.width / 1920f, area.rect.height / 1080f);
            stage.localScale = Vector3.one * Mathf.Max(.01f, scale);
            // The 3D character must shrink with the fitted UI on a 4:3 display as well.
            // This leaves the head below the header and the boots above the customization dock.
            if (presentationCamera != null && area.rect.height > 0)
            {
                float heightFraction = Mathf.Clamp01(1080f * scale / area.rect.height);
                presentationCamera.fieldOfView = 2f * Mathf.Atan(Mathf.Tan(referenceFieldOfView * Mathf.Deg2Rad * .5f) / Mathf.Max(.1f, heightFraction)) * Mathf.Rad2Deg;
            }
        }
    }
}
