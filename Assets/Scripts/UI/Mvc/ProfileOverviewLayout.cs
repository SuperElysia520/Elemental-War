using UnityEngine;

namespace FPS.UI
{
    /// <summary>Fits the profile controls and frames the lobby's live account character.</summary>
    [ExecuteAlways]
    public sealed class ProfileOverviewLayout : MonoBehaviour
    {
        public RectTransform[] contentLayers;
        private Camera stationCamera;
        private Transform character;
        private Vector3 homePosition;
        private Quaternion homeRotation;
        private float homeFov;
        private float framedAspect;

        private void OnEnable() => FitControls();
        private void OnRectTransformDimensionsChange() => FitControls();

        private void FitControls()
        {
            if (contentLayers == null) return;
            var size = ((RectTransform)transform).rect.size;
            float scale = Mathf.Min(size.x / 1920f, size.y / 1080f);
            if (scale <= 0) return;
            foreach (var layer in contentLayers)
                if (layer != null) layer.localScale = Vector3.one * scale;
        }

        public void FrameCharacter(Camera camera, Transform subject)
        {
            if (camera == null || subject == null || stationCamera == camera && character == subject) return;
            RestoreCamera();
            stationCamera = camera;
            character = subject;
            homePosition = camera.transform.localPosition;
            homeRotation = camera.transform.localRotation;
            homeFov = camera.fieldOfView;
            FitPortrait();
        }

        private void LateUpdate()
        {
            if (stationCamera != null && !Mathf.Approximately(framedAspect, stationCamera.aspect))
                FitPortrait();
        }

        private void FitPortrait()
        {
            framedAspect = stationCamera.aspect;
            // Frame the upper body, pulling back on narrower displays to keep the face clear of the sheet.
            float halfHeight = .515f * Mathf.Max(1f, 1.55f / framedAspect);
            stationCamera.fieldOfView = 30f;
            float distance = halfHeight / Mathf.Tan(15f * Mathf.Deg2Rad);
            var facing = Quaternion.LookRotation(-character.forward, character.up) * Quaternion.Euler(0, -7, 0);
            var center = character.position + character.up * 1.29f;
            var position = center - facing * new Vector3(.50f * halfHeight * framedAspect, 0, distance);
            stationCamera.transform.SetPositionAndRotation(position, facing);
        }

        private void OnDisable() => RestoreCamera();
        private void RestoreCamera()
        {
            if (stationCamera != null)
            {
                stationCamera.transform.localPosition = homePosition;
                stationCamera.transform.localRotation = homeRotation;
                stationCamera.fieldOfView = homeFov;
            }
            stationCamera = null;
            character = null;
        }
    }
}
