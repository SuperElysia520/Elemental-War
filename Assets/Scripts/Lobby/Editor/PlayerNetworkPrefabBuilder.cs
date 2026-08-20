using System.IO;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 一键从现有角色预制体分别生成 PVE 与 PVP 网络玩家预制体。
/// PVE 只包含通用网络组件；PVP 副本额外包含 PvpPlayerCombat。
/// 两组预制体都会注册进 DefaultNetworkPrefabs.asset。
/// 菜单: 联机 > 生成联机玩家预制体
/// 说明：原预制体不动；单机路径照旧用原预制体实例。
/// </summary>
public static class PlayerNetworkPrefabBuilder
{
    private const string SourceDir = "Assets/Prefabs";
    // 必须是 Assets/Resources/（复数），否则 Resources.Load 找不到
    private const string OutDir = "Assets/Resources/Players";
    private const string PvpOutDir = "Assets/Resources/Players/PVP";
    private const string DefaultPrefabsPath = "Assets/DefaultNetworkPrefabs.asset";
    private const string BodyBloodImpactPath =
        "Assets/Resource/Effects/Hurt/prefab/Red/Blood_Impact_Small_Red.prefab";
    private const string HeadBloodImpactPath =
        "Assets/Resource/Effects/Hurt/prefab/Red/Blood_Impact_Medium_Red.prefab";
    private const string HeadshotReceivedSoundPath =
        "Assets/Resource/Audio/爆头 击中头盔 钢盔命中 射击反馈.mp3";

    private static readonly string[] Names = { "Lumine", "Furina", "Aether" };

    [MenuItem("联机/生成联机玩家预制体")]
    public static void Build()
    {
        Directory.CreateDirectory(OutDir);
        Directory.CreateDirectory(PvpOutDir);
        var list = LoadOrCreateList();

        foreach (var name in Names)
        {
            string srcPath = $"{SourceDir}/{name}.prefab";
            GameObject src = AssetDatabase.LoadAssetAtPath<GameObject>(srcPath);
            if (src == null)
            {
                Debug.LogError("[联机] 找不到源预制体: " + srcPath);
                continue;
            }

            // 1. 给源预制体加 HideInMultiplayer：联机时隐藏 Game 场景里固定摆放的单机角色实例，
            //    单机（无网络）时是空操作。网络副本因带 NetworkObject 会被组件内部跳过。
            GameObject srcInst = (GameObject)PrefabUtility.InstantiatePrefab(src);
            if (srcInst.GetComponent<HideInMultiplayer>() == null)
                srcInst.AddComponent<HideInMultiplayer>();
            PrefabUtility.SaveAsPrefabAsset(srcInst, srcPath);
            Object.DestroyImmediate(srcInst);
            src = AssetDatabase.LoadAssetAtPath<GameObject>(srcPath);

            // 2. 生成 PVE 网络副本，不加入任何 PVP 专用组件。
            GameObject inst = (GameObject)PrefabUtility.InstantiatePrefab(src);
            inst.name = name + "_Net";
            ConfigureNetworkPlayer(inst);
            PvpPlayerCombat stalePvpCombat = inst.GetComponent<PvpPlayerCombat>();
            if (stalePvpCombat != null)
                Object.DestroyImmediate(stalePvpCombat);

            string outPath = $"{OutDir}/{name}_Net.prefab";
            PrefabUtility.SaveAsPrefabAsset(inst, outPath);
            Object.DestroyImmediate(inst);

            GameObject outPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(outPath);
            AddToList(list, outPrefab);

            // 3. 从同一源角色单独生成 PVP 副本，避免改动 PVE 预制体与行为。
            GameObject pvpInst = (GameObject)PrefabUtility.InstantiatePrefab(src);
            pvpInst.name = name + "_PVP_Net";
            ConfigureNetworkPlayer(pvpInst);
            PvpPlayerCombat pvpCombat = pvpInst.GetComponent<PvpPlayerCombat>();
            if (pvpCombat == null)
                pvpCombat = pvpInst.AddComponent<PvpPlayerCombat>();
            ConfigurePvpHitFeedback(pvpCombat);

            string pvpOutPath = $"{PvpOutDir}/{name}_PVP_Net.prefab";
            PrefabUtility.SaveAsPrefabAsset(pvpInst, pvpOutPath);
            Object.DestroyImmediate(pvpInst);

            GameObject pvpOutPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(pvpOutPath);
            AddToList(list, pvpOutPrefab);
        }

        EditorUtility.SetDirty(list);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[联机] PVE/PVP 玩家网络预制体已分别生成并注册。\n（源预制体已加 HideInMultiplayer，联机时会自动隐藏单机角色实例）");
    }

    private static void ConfigureNetworkPlayer(GameObject instance)
    {
        if (instance.GetComponent<NetworkObject>() == null)
            instance.AddComponent<NetworkObject>();
        if (instance.GetComponent<OwnerNetworkTransform>() == null)
            instance.AddComponent<OwnerNetworkTransform>();

        PlayerNetworkSync sync = instance.GetComponent<PlayerNetworkSync>();
        if (sync == null)
            sync = instance.AddComponent<PlayerNetworkSync>();
        sync.playerModel = instance.GetComponent<PlayerModel>();
    }

    private static void ConfigurePvpHitFeedback(PvpPlayerCombat combat)
    {
        if (combat == null)
            return;

        combat.bodyBloodImpactPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(BodyBloodImpactPath);
        combat.headBloodImpactPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(HeadBloodImpactPath);
        combat.headshotReceivedSound = AssetDatabase.LoadAssetAtPath<AudioClip>(HeadshotReceivedSoundPath);
        combat.bloodImpactLifetime = 3f;
        combat.headshotReceivedVolume = 1f;
    }

    private static NetworkPrefabsList LoadOrCreateList()
    {
        var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(DefaultPrefabsPath);
        if (list == null)
        {
            list = ScriptableObject.CreateInstance<NetworkPrefabsList>();
            AssetDatabase.CreateAsset(list, DefaultPrefabsPath);
        }
        return list;
    }

    private static void AddToList(NetworkPrefabsList list, GameObject prefab)
    {
        if (prefab == null)
            return;
        if (!list.Contains(prefab))
            list.Add(new NetworkPrefab { Prefab = prefab, Override = NetworkPrefabOverride.None });
    }
}
