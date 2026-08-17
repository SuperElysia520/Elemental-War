using System.IO;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 阶段0网络验证：自动生成/连接网络管理器
/// 自动识别角色：原工程 = Host(房主)，ParrelSync 克隆 = Client(客机)
/// 也可在 Inspector 手动指定角色。
/// </summary>
public class NetTestManager : MonoBehaviour
{
    public enum RoleMode
    {
        Auto,   // 检测 .clone 文件：克隆=Client，原工程=Host
        Host,
        Client,
    }

    [Tooltip("网络角色：Auto 自动识别（推荐）")]
    public RoleMode roleMode = RoleMode.Auto;

    [Tooltip("验证用方块预制体（Assets/Resource/NetTest/NetTestCube.prefab）")]
    public GameObject cubePrefab;

    void Start()
    {
        bool isHost = ResolveIsHost();
        if (isHost)
            StartHost();
        else
            StartClient();
    }

    private bool ResolveIsHost()
    {
        switch (roleMode)
        {
            case RoleMode.Host:
                return true;
            case RoleMode.Client:
                return false;
            case RoleMode.Auto:
            default:
                // ParrelSync 克隆目录含 ".clone" 文件，原工程没有
                string projectRoot = Directory.GetParent(Application.dataPath).FullName;
                bool isClone = File.Exists(Path.Combine(projectRoot, ".clone"));
                return !isClone;
        }
    }

    private void StartHost()
    {
        NetworkManager.Singleton.OnServerStarted += OnServerStarted;
        NetworkManager.Singleton.StartHost();
    }

    private void OnServerStarted()
    {
        var cube = Instantiate(cubePrefab, Vector3.zero, Quaternion.identity);
        cube.GetComponent<NetworkObject>().Spawn();
        Debug.Log("[NetTest] Host 启动，同步方块已生成");
    }

    private void StartClient()
    {
        NetworkManager.Singleton.OnClientConnectedCallback += (id) =>
        {
            Debug.Log("[NetTest] Client 已连接，ClientId=" + id);
        };
        NetworkManager.Singleton.StartClient();
    }

    void OnGUI()
    {
        if (NetworkManager.Singleton == null) return;
        string role = NetworkManager.Singleton.IsHost ? "Host(房主)"
            : NetworkManager.Singleton.IsClient ? "Client(客机)" : "未连接";
        GUI.Label(new Rect(10, 10, 300, 30), "网络角色: " + role);
    }
}
