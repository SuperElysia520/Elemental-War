using UnityEditor;
using UnityEngine;

/// <summary>
/// 从 Low Poly FPS Pack 生成第一人称 viewmodel 预制体（手臂+枪）。
/// 取示例预制体里的 Assault_Rifle_01_Arms 子树，去掉多余相机，挂 FirstPersonViewmodel，存到 Resources。
/// 菜单: 联机 > 生成第一人称手臂预制体
/// 生成后把该预制体拖到 Game 场景 Player 物体上的 PlayerController.viewmodelPrefab。
/// </summary>
public static class ViewmodelPrefabBuilder
{
    private const string SourcePrefabPath = "Assets/Plugins/Low Poly FPS Pack/Prefabs/Example_Prefabs/Arms/Assault_Rifle_01_Example_Prefab/Assault_Rifle_01_FPSController.prefab";
    private const string OutDir = "Assets/Resources/Players";
    private const string OutName = "Viewmodel_AssaultRifle";

    [MenuItem("联机/生成第一人称手臂预制体")]
    public static void Build()
    {
        if (!System.IO.Directory.Exists(OutDir))
            System.IO.Directory.CreateDirectory(OutDir);

        GameObject src = AssetDatabase.LoadAssetAtPath<GameObject>(SourcePrefabPath);
        if (src == null)
        {
            Debug.LogError("[Viewmodel] 找不到 FPS Pack 示例预制体: " + SourcePrefabPath);
            return;
        }

        GameObject inst = (GameObject)PrefabUtility.InstantiatePrefab(src);
        Transform arms = inst.transform.Find("Assault_Rifle_01_Arms");
        if (arms == null)
        {
            Debug.LogError("[Viewmodel] 找不到 Assault_Rifle_01_Arms 节点");
            Object.DestroyImmediate(inst);
            return;
        }

        // 复制手臂子树为独立根
        GameObject copy = Object.Instantiate(arms.gameObject);
        copy.name = OutName;

        // 去掉多余的相机节点（Gun Camera / camera 下的 Main Camera），本地主相机由 PlayerController 驱动
        foreach (var cam in copy.GetComponentsInChildren<Camera>(true))
        {
            if (cam.gameObject != copy)
                Object.DestroyImmediate(cam.gameObject);
        }

        // 去掉示例场景的教程文字、弹药等 UI。
        foreach (var canvas in copy.GetComponentsInChildren<Canvas>(true))
            Object.DestroyImmediate(canvas.gameObject);

        // 该示例子树同时包含刀和多种瞄具。保留 PVP 固定使用的 Scope 02 全息瞄具，
        // 移除其余附件；运行时由 FirstPersonViewmodel 决定 PVE 铁瞄/PVP 全息的可见性。
        foreach (var renderer in copy.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null)
                continue;
            string objectName = renderer.gameObject.name.ToLowerInvariant();
            bool unusedScope = objectName.StartsWith("scope") &&
                !FirstPersonViewmodel.IsHolographicSightRendererName(objectName);
            if (objectName.Contains("knife") || unusedScope || objectName == "silencer")
                Object.DestroyImmediate(renderer.gameObject);
        }

        // 去掉 FPS Pack 遗留驱动脚本（AutomaticGunScriptLPFP 等），
        // 它们读旧版 Input Manager，会与 FirstPersonViewmodel 抢 Animator。
        foreach (var mb in copy.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (mb.GetType().Name.EndsWith("LPFP"))
                Object.DestroyImmediate(mb);
        }

        // 挂 FirstPersonViewmodel 并接线
        var vm = copy.GetComponent<FirstPersonViewmodel>();
        if (vm == null)
            vm = copy.AddComponent<FirstPersonViewmodel>();
        vm.bulletSpawnPoint = FindRecursive(copy.transform, "Bullet Spawn Point");
        var muzzle = FindRecursive(copy.transform, "Muzzleflash Particles");
        if (muzzle != null) vm.muzzleFlash = muzzle.GetComponent<ParticleSystem>();
        var spark = FindRecursive(copy.transform, "SparkParticles");
        if (spark != null) vm.sparkParticles = spark.GetComponent<ParticleSystem>();
        vm.pvpHolographicReticle = AssetDatabase.LoadAssetAtPath<Sprite>(
            "Assets/Plugins/Low Poly FPS Pack/Components/Textures_&_Sprites/Scope_Textures/Red_Dot_Sight_2_Texture.png");
        vm.aimInSound = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Resource/Audio/aim_in.mp3");

        string outPath = $"{OutDir}/{OutName}.prefab";
        PrefabUtility.SaveAsPrefabAsset(copy, outPath);

        Object.DestroyImmediate(copy);
        Object.DestroyImmediate(inst);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[Viewmodel] 生成完成: " + outPath);
    }

    private static Transform FindRecursive(Transform root, string name)
    {
        if (root.name == name)
            return root;
        foreach (Transform child in root)
        {
            var hit = FindRecursive(child, name);
            if (hit != null)
                return hit;
        }
        return null;
    }
}
