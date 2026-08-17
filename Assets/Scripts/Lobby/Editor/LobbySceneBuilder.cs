using System.IO;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 阶段1大厅验证：一键创建最小大厅验证场景。
/// 菜单: Lobby > 创建大厅验证场景
/// </summary>
public static class LobbySceneBuilder
{
    private const string ScenePath = "Assets/Scenes/Lobby.unity";
    private const string LobbyStatePrefabPath = "Assets/Resource/Lobby/LobbyState.prefab";

    [MenuItem("Lobby/创建大厅验证场景")]
    public static void CreateLobbyScene()
    {
        // 1. 创建 LobbyState 预制体（NetworkObject + LobbyState）
        GameObject lobbyGo = new GameObject("LobbyState");
        lobbyGo.AddComponent<NetworkObject>();
        lobbyGo.AddComponent<LobbyState>();

        Directory.CreateDirectory("Assets/Resource/Lobby");
        GameObject lobbyPrefab = PrefabUtility.SaveAsPrefabAsset(lobbyGo, LobbyStatePrefabPath);
        Object.DestroyImmediate(lobbyGo);

        // 2. 创建场景
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // 相机（OnGUI 验证 UI 不需要相机，但保留一个空的避免 Scene 报错）
        GameObject cameraGo = new GameObject("Main Camera");
        cameraGo.tag = "MainCamera";
        cameraGo.AddComponent<Camera>();

        // 3. NetworkManager + UnityTransport + LobbyManager + LanDiscovery
        GameObject networkGo = new GameObject("NetworkManager");
        NetworkManager networkManager = networkGo.AddComponent<NetworkManager>();
        UnityTransport transport = networkGo.AddComponent<UnityTransport>();

        // 显式配置（复用 NGO 默认预制体列表）
        var config = new NetworkConfig
        {
            NetworkTransport = transport,
            EnableSceneManagement = false,
        };
        var defaultList = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>("Assets/DefaultNetworkPrefabs.asset");
        if (defaultList == null)
        {
            defaultList = ScriptableObject.CreateInstance<NetworkPrefabsList>();
            AssetDatabase.CreateAsset(defaultList, "Assets/DefaultNetworkPrefabs.asset");
        }
        config.Prefabs.NetworkPrefabsLists.Add(defaultList);
        networkManager.NetworkConfig = config;

        LobbyManager lobbyManager = networkGo.AddComponent<LobbyManager>();
        lobbyManager.transport = transport;
        lobbyManager.lobbyStatePrefab = lobbyPrefab;
        lobbyManager.maxPlayers = 3;

        LanDiscovery discovery = networkGo.AddComponent<LanDiscovery>();
        lobbyManager.discovery = discovery;

        // 4. OnGUI 验证 UI
        GameObject guiGo = new GameObject("LobbyTestGUI");
        guiGo.AddComponent<LobbyTestGUI>();

        EditorSceneManager.SaveScene(scene, ScenePath);
        AssetDatabase.SaveAssets();
        Debug.Log("[Lobby] 大厅验证场景创建完成: " + ScenePath + "\n" +
                  "请打开该场景: File > Open Scene > Assets/Scenes/Lobby.unity\n" +
                  "原工程实例运行 = 房主，ParrelSync 克隆实例运行 = 客机");
    }
}
