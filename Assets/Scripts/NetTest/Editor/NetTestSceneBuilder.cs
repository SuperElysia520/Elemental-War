using System.IO;
using Unity.Netcode;
using Unity.Netcode.Components;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 阶段0网络验证：一键创建最小网络验证场景
/// 菜单: NetTest > 创建网络验证场景
/// </summary>
public static class NetTestSceneBuilder
{
    private const string ScenePath = "Assets/Scenes/NetTest.unity";
    private const string PrefabPath = "Assets/Resource/NetTest/NetTestCube.prefab";
    // NGO 官方默认预制体列表（带 NetworkObject 的预制体统一注册到这里）
    private const string DefaultPrefabsPath = "Assets/DefaultNetworkPrefabs.asset";

    [MenuItem("NetTest/创建网络验证场景")]
    public static void CreateNetTestScene()
    {
        // 1. 创建方块预制体（NetworkObject + NetworkTransform + NetTestCube）
        GameObject cubeGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cubeGo.name = "NetTestCube";
        cubeGo.AddComponent<NetworkObject>();
        cubeGo.AddComponent<NetworkTransform>();
        cubeGo.AddComponent<NetTestCube>();

        Directory.CreateDirectory("Assets/Resource/NetTest");
        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(cubeGo, PrefabPath);
        Object.DestroyImmediate(cubeGo);

        // 2. 复用 NGO 默认预制体列表，确保方块只注册一次
        NetworkPrefabsList defaultList = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(DefaultPrefabsPath);
        if (defaultList == null)
        {
            defaultList = ScriptableObject.CreateInstance<NetworkPrefabsList>();
            AssetDatabase.CreateAsset(defaultList, DefaultPrefabsPath);
        }
        if (!defaultList.Contains(prefab))
        {
            defaultList.Add(new NetworkPrefab { Prefab = prefab, Override = NetworkPrefabOverride.None });
        }
        EditorUtility.SetDirty(defaultList);

        // 3. 创建验证场景
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // 相机
        GameObject cameraGo = new GameObject("Main Camera");
        cameraGo.tag = "MainCamera";
        Camera camera = cameraGo.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.15f, 0.15f, 0.2f);
        cameraGo.AddComponent<AudioListener>();
        cameraGo.transform.position = new Vector3(0, 6, -12);
        cameraGo.transform.rotation = Quaternion.Euler(22, 0, 0);

        // 方向光
        GameObject lightGo = new GameObject("Directional Light");
        Light light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        lightGo.transform.rotation = Quaternion.Euler(50, -30, 0);

        // 地面
        GameObject groundGo = GameObject.CreatePrimitive(PrimitiveType.Plane);
        groundGo.name = "Ground";
        groundGo.transform.position = Vector3.zero;
        groundGo.transform.localScale = new Vector3(4, 1, 4);

        // 4. 网络管理器：NetworkManager + UnityTransport
        GameObject networkGo = new GameObject("NetworkManager");
        NetworkManager networkManager = networkGo.AddComponent<NetworkManager>();
        UnityTransport transport = networkGo.AddComponent<UnityTransport>();

        // 显式配置，避免依赖编辑器 Reset 钩子的隐式行为
        var config = new NetworkConfig
        {
            NetworkTransport = transport,
            EnableSceneManagement = false,
        };
        config.Prefabs.NetworkPrefabsLists.Add(defaultList);
        networkManager.NetworkConfig = config;

        NetTestManager netTestManager = networkGo.AddComponent<NetTestManager>();
        netTestManager.cubePrefab = prefab;

        EditorSceneManager.SaveScene(scene, ScenePath);
        AssetDatabase.SaveAssets();
        Debug.Log("[NetTest] 验证场景创建完成: " + ScenePath + "\n" +
                  "请打开该场景: File > Open Scene > Assets/Scenes/NetTest.unity\n" +
                  "原工程实例运行 = 房主，ParrelSync 克隆实例运行 = 客机");
    }
}
