using UnityEngine;
using UnityEngine.EventSystems;

namespace FPS.UI
{
    /// <summary>Owns the offscreen renderer, not the customization draft or account service.</summary>
    public sealed class CharacterPreviewView : MonoBehaviour, IDragHandler
    {
        public UnityEngine.UI.RawImage image;
        public GameObject characterPrefab;
        public RuntimeAnimatorController idle;
        private GameObject stage;
        private RifleGirlAppearance appearance;
        private RenderTexture texture;
        private CharacterAppearanceData applied;
        private bool hasApplied;
        private void OnEnable() { if (stage != null) stage.SetActive(true); }
        private void OnDisable() { if (stage != null) stage.SetActive(false); }
        public void Apply(CharacterAppearanceData data)
        {
            if (stage == null) Create();
            if (appearance == null || hasApplied && applied.Equals(data)) return;
            appearance.Apply(data); applied = data; hasApplied = true;
        }
        public void OnDrag(PointerEventData data)
        { if (appearance != null) appearance.transform.Rotate(Vector3.up, -data.delta.x * 0.35f, Space.World); }
        private void Create()
        {
            if (characterPrefab == null || image == null) return;
            stage = new GameObject("WardrobePreviewStage");
            stage.transform.position = new Vector3(10000, -10000, 10000);
            var body = Instantiate(characterPrefab, stage.transform);
            body.transform.localPosition = Vector3.zero; body.transform.localRotation = Quaternion.Euler(0, 180, 0);
            SetLayer(body.transform, 31);
            appearance = body.GetComponentInChildren<RifleGirlAppearance>();
            var animator = body.GetComponent<Animator>();
            if (animator != null) { animator.runtimeAnimatorController = idle; animator.applyRootMotion = false; animator.Rebind(); animator.Update(0f); }
            var camera = new GameObject("PreviewCamera", typeof(Camera)).GetComponent<Camera>();
            camera.transform.SetParent(stage.transform, false);
            camera.fieldOfView = 35f; camera.cullingMask = 1 << 31;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0.055f, 0.078f, 0.12f);
            camera.nearClipPlane = 0.05f; camera.farClipPlane = 10f;
            texture = new RenderTexture(700, 1024, 24) { antiAliasing = 2 };
            camera.targetTexture = texture; image.texture = texture;
            Bounds bounds = new Bounds(body.transform.position + Vector3.up * 0.85f, new Vector3(1f, 1.7f, 0.5f));
            bool hasGeometry = false;
            var baked = new Mesh();
            foreach (var skin in body.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (!skin.enabled) continue;
                baked.Clear(); skin.BakeMesh(baked); Bounds local = baked.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = local.center + Vector3.Scale(local.extents,
                        new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var point = skin.transform.TransformPoint(corner);
                    if (!hasGeometry) { bounds = new Bounds(point, Vector3.zero); hasGeometry = true; } else bounds.Encapsulate(point);
                }
            }
            Destroy(baked);
            float halfHeight = Mathf.Max(bounds.extents.y, bounds.extents.x / (700f / 1024f));
            float distance = halfHeight * 1.15f / Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f) + bounds.extents.z;
            camera.transform.position = bounds.center + Vector3.back * distance; camera.transform.LookAt(bounds.center);
            var light = new GameObject("PreviewKey", typeof(Light)).GetComponent<Light>();
            light.transform.SetParent(stage.transform, false); light.type = LightType.Directional;
            light.transform.localRotation = Quaternion.Euler(35, -30, 0); light.cullingMask = 1 << 31; light.intensity = 1.4f;
        }
        private static void SetLayer(Transform target, int layer)
        { target.gameObject.layer = layer; foreach (Transform child in target) SetLayer(child, layer); }
        private void OnDestroy()
        {
            if (stage != null) Destroy(stage);
            if (texture != null) { texture.Release(); Destroy(texture); }
        }
    }
}
