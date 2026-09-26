using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 把外观档案应用到同一副 RifleGirl 骨架。衣装与对应身体网格始终成套互斥。
/// 此组件只负责显示；账号保存和联机同步由调用方负责。
/// </summary>
[DisallowMultipleComponent]
public sealed class RifleGirlAppearance : MonoBehaviour
{
    public const string ResourcePath = "Customization/RifleGirlBody";
    public const int OutfitCount = 3;
    public const int FaceCount = 5;
    public const int ClothColorCount = 5;
    public const int HairColorCount = 4;

    [SerializeField] private Animator characterAnimator;
    [SerializeField] private GameObject[] outfitRoots;
    [SerializeField] private GameObject[] faceRoots;
    [SerializeField] private GameObject hatRoot;
    [SerializeField] private GameObject accessoryRoot;
    [SerializeField] private Renderer[] clothRenderers;
    [SerializeField] private Renderer hairRenderer;
    [SerializeField] private Material[] clothMaterials;
    [SerializeField] private Material[] hairMaterials;
    [SerializeField] private Renderer[] firstPersonHiddenRenderers;
    [SerializeField] private SkinnedMeshRenderer[] firstPersonBodyRenderers;
    [SerializeField] private Mesh[] thirdPersonBodyMeshes;
    [SerializeField] private Mesh[] firstPersonBodyTemplates;
    [SerializeField] private CharacterAppearanceData currentAppearance;

    private ShadowCastingMode[] originalShadowModes;
    private bool localFirstPerson;
    private Mesh[] privateFirstPersonMeshes;

    public Animator CharacterAnimator => characterAnimator;
    public CharacterAppearanceData Current => currentAppearance;
    public bool IsLocalFirstPerson => localFirstPerson;

    private void Awake()
    {
        CacheShadowModes();
        Apply(currentAppearance);
    }

    public void Apply(CharacterAppearanceData data)
    {
        currentAppearance = data.Sanitized();
        SetExclusive(outfitRoots, currentAppearance.outfitId);
        SetExclusive(faceRoots, currentAppearance.faceId);

        if (hatRoot != null)
            hatRoot.SetActive(currentAppearance.hat);

        // 原资源的战斗装配件没有运动装适配，沿用资源作者的组合限制。
        if (accessoryRoot != null)
            accessoryRoot.SetActive(currentAppearance.accessories && currentAppearance.outfitId != 2);

        Material clothMaterial = GetMaterial(clothMaterials, currentAppearance.clothColorId);
        if (clothMaterial != null && clothRenderers != null)
        {
            foreach (Renderer renderer in clothRenderers)
            {
                if (renderer != null)
                    renderer.sharedMaterial = clothMaterial;
            }
        }

        Material hairMaterial = GetMaterial(hairMaterials, currentAppearance.hairColorId);
        if (hairRenderer != null && hairMaterial != null)
            hairRenderer.sharedMaterial = hairMaterial;

        // sharedMaterial 只切换该 Renderer 的引用，绝不改共享材质属性或分配材质副本。
        ApplyFirstPersonVisibility();
    }

    public void SetLocalFirstPerson(bool enabled)
    {
        CacheShadowModes();
        localFirstPerson = enabled;
        ApplyFirstPersonVisibility();
    }

    private void CacheShadowModes()
    {
        int count = firstPersonHiddenRenderers != null ? firstPersonHiddenRenderers.Length : 0;
        if (originalShadowModes != null && originalShadowModes.Length == count)
            return;

        originalShadowModes = new ShadowCastingMode[count];
        for (int i = 0; i < count; i++)
        {
            Renderer renderer = firstPersonHiddenRenderers[i];
            originalShadowModes[i] = renderer != null ? renderer.shadowCastingMode : ShadowCastingMode.On;
        }
    }

    private void ApplyFirstPersonVisibility()
    {
        CacheShadowModes();
        for (int i = 0; i < originalShadowModes.Length; i++)
        {
            Renderer renderer = firstPersonHiddenRenderers[i];
            if (renderer != null)
            {
                // 保留头部骨骼、爆头命中区及阴影；身体与手臂仍是网络角色本身。
                renderer.shadowCastingMode = localFirstPerson
                    ? ShadowCastingMode.ShadowsOnly
                    : originalShadowModes[i];
            }
        }
        int count = firstPersonBodyRenderers != null ? firstPersonBodyRenderers.Length : 0;
        if (privateFirstPersonMeshes == null) privateFirstPersonMeshes = new Mesh[count];
        for (int i = 0; i < count; i++)
        {
            SkinnedMeshRenderer body = firstPersonBodyRenderers[i];
            if (body == null || thirdPersonBodyMeshes == null || i >= thirdPersonBodyMeshes.Length) continue;
            if (!localFirstPerson)
            {
                body.sharedMesh = thirdPersonBodyMeshes[i];
                continue;
            }
            if (!body.gameObject.activeInHierarchy) continue;
            if (firstPersonBodyTemplates == null || i >= firstPersonBodyTemplates.Length || firstPersonBodyTemplates[i] == null) continue;
            if (privateFirstPersonMeshes[i] == null)
            {
                // 模板由编辑器按实际 Head 骨权重生成，避免运行时读取不可读 FBX。
                // 每位本地玩家只替换同一 Renderer 的私有网格；远端仍用完整源网格。
                privateFirstPersonMeshes[i] = Instantiate(firstPersonBodyTemplates[i]);
                privateFirstPersonMeshes[i].name = body.name + "_LocalFirstPerson";
                privateFirstPersonMeshes[i].hideFlags = HideFlags.DontSave;
            }
            body.sharedMesh = privateFirstPersonMeshes[i];
        }
    }

    private void OnDestroy() => DisposeFirstPersonMeshes();

    private void DisposeFirstPersonMeshes()
    {
        if (privateFirstPersonMeshes == null) return;
        for (int i = 0; i < privateFirstPersonMeshes.Length; i++)
        {
            Mesh mesh = privateFirstPersonMeshes[i];
            if (mesh == null) continue;
            if (firstPersonBodyRenderers != null && i < firstPersonBodyRenderers.Length && firstPersonBodyRenderers[i] != null &&
                firstPersonBodyRenderers[i].sharedMesh == mesh && thirdPersonBodyMeshes != null && i < thirdPersonBodyMeshes.Length)
                firstPersonBodyRenderers[i].sharedMesh = thirdPersonBodyMeshes[i];
            if (Application.isPlaying) Destroy(mesh); else DestroyImmediate(mesh);
        }
        privateFirstPersonMeshes = null;
    }

    private static void SetExclusive(GameObject[] objects, int selected)
    {
        if (objects == null)
            return;
        for (int i = 0; i < objects.Length; i++)
        {
            if (objects[i] != null)
                objects[i].SetActive(i == selected);
        }
    }

    private static Material GetMaterial(Material[] materials, int index)
    {
        return materials != null && index >= 0 && index < materials.Length ? materials[index] : null;
    }

#if UNITY_EDITOR
    public void ConfigureForEditor(
        Animator animator,
        GameObject[] outfits,
        GameObject[] faces,
        GameObject hat,
        GameObject accessories,
        Renderer[] recoloredClothes,
        Renderer hair,
        Material[] clothesPalette,
        Material[] hairPalette,
        Renderer[] localHiddenRenderers,
        SkinnedMeshRenderer[] bodyRenderers,
        Mesh[] firstPersonTemplates)
    {
        DisposeFirstPersonMeshes();
        characterAnimator = animator;
        outfitRoots = outfits;
        faceRoots = faces;
        hatRoot = hat;
        accessoryRoot = accessories;
        clothRenderers = recoloredClothes;
        hairRenderer = hair;
        clothMaterials = clothesPalette;
        hairMaterials = hairPalette;
        firstPersonHiddenRenderers = localHiddenRenderers;
        firstPersonBodyRenderers = bodyRenderers;
        firstPersonBodyTemplates = firstPersonTemplates;
        thirdPersonBodyMeshes = new Mesh[bodyRenderers.Length];
        for (int i = 0; i < bodyRenderers.Length; i++) thirdPersonBodyMeshes[i] = bodyRenderers[i].sharedMesh;
        originalShadowModes = null;
        localFirstPerson = false;
        Apply(CharacterAppearanceData.Default);
    }
#endif
}
