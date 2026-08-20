using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>为本地视角中的敌方玩家创建一层背面扩张描边，不改变原角色材质。</summary>
[DisallowMultipleComponent]
public class PvpEnemyOutline : MonoBehaviour
{
    private readonly List<GameObject> m_OutlineObjects = new List<GameObject>();
    private Material m_OutlineMaterial;

    public void Apply(Color color, float width = 0.018f)
    {
        if (m_OutlineMaterial != null)
            return;

        Shader shader = Resources.Load<Shader>("PvpEnemyOutline");
        if (shader == null)
            shader = Shader.Find("FPS/PvpEnemyOutline");
        if (shader == null)
        {
            Debug.LogWarning("[PVP] 找不到敌方描边 Shader");
            return;
        }

        m_OutlineMaterial = new Material(shader)
        {
            name = "PVP Enemy Outline (Runtime)",
        };
        m_OutlineMaterial.SetColor("_OutlineColor", color);
        m_OutlineMaterial.SetFloat("_OutlineWidth", width);

        SkinnedMeshRenderer[] skinnedRenderers = GetComponentsInChildren<SkinnedMeshRenderer>(true);
        foreach (SkinnedMeshRenderer source in skinnedRenderers)
            CreateSkinnedOutline(source);

        MeshRenderer[] meshRenderers = GetComponentsInChildren<MeshRenderer>(true);
        foreach (MeshRenderer source in meshRenderers)
            CreateMeshOutline(source);
    }

    private void CreateSkinnedOutline(SkinnedMeshRenderer source)
    {
        if (source == null || source.sharedMesh == null || source.gameObject.name.StartsWith("__PvpOutline"))
            return;

        GameObject outlineObject = CreateOutlineObject(source.transform);
        SkinnedMeshRenderer outline = outlineObject.AddComponent<SkinnedMeshRenderer>();
        outline.sharedMesh = source.sharedMesh;
        outline.rootBone = source.rootBone;
        outline.bones = source.bones;
        outline.localBounds = source.localBounds;
        outline.quality = source.quality;
        outline.updateWhenOffscreen = source.updateWhenOffscreen;
        ConfigureRenderer(outline, source.sharedMesh.subMeshCount);
    }

    private void CreateMeshOutline(MeshRenderer source)
    {
        if (source == null || source.gameObject.name.StartsWith("__PvpOutline"))
            return;

        MeshFilter sourceFilter = source.GetComponent<MeshFilter>();
        if (sourceFilter == null || sourceFilter.sharedMesh == null)
            return;

        GameObject outlineObject = CreateOutlineObject(source.transform);
        MeshFilter outlineFilter = outlineObject.AddComponent<MeshFilter>();
        outlineFilter.sharedMesh = sourceFilter.sharedMesh;
        MeshRenderer outline = outlineObject.AddComponent<MeshRenderer>();
        ConfigureRenderer(outline, sourceFilter.sharedMesh.subMeshCount);
    }

    private GameObject CreateOutlineObject(Transform sourceTransform)
    {
        GameObject go = new GameObject("__PvpOutline_" + sourceTransform.name);
        go.layer = sourceTransform.gameObject.layer;
        go.transform.SetParent(sourceTransform, false);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;
        m_OutlineObjects.Add(go);
        return go;
    }

    private void ConfigureRenderer(Renderer renderer, int subMeshCount)
    {
        int materialCount = Mathf.Max(1, subMeshCount);
        Material[] materials = new Material[materialCount];
        for (int i = 0; i < materialCount; i++)
            materials[i] = m_OutlineMaterial;
        renderer.sharedMaterials = materials;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
    }

    private void OnDestroy()
    {
        foreach (GameObject outlineObject in m_OutlineObjects)
        {
            if (outlineObject != null)
                Destroy(outlineObject);
        }
        if (m_OutlineMaterial != null)
            Destroy(m_OutlineMaterial);
    }
}
