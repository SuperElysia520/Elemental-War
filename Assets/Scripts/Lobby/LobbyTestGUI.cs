using System.Text;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 阶段1大厅验证 UI（OnGUI 版）：零资源依赖，双开立即可用。
/// 正式版 LobbyUI.cs（TMP）接入 GameStart 场景后再替换。
/// </summary>
public class LobbyTestGUI : MonoBehaviour
{
    private LobbyManager Lobby => LobbyManager.Instance;
    private LanDiscovery Discovery => Lobby != null ? Lobby.discovery : null;

    void OnGUI()
    {
        if (Lobby == null)
        {
            GUI.Label(new Rect(10, 10, 400, 30), "LobbyManager 未找到");
            return;
        }

        GUILayout.BeginArea(new Rect(10, 10, 420, Screen.height - 20));
        GUILayout.BeginVertical("box");

        // 状态行
        string role = Lobby.IsHost ? "房主 Host" : "客机 Client";
        GUILayout.Label($"角色: {role}");

        // 连接前：浏览/建房
        if (!NetworkManager.Singleton.IsListening)
        {
            GUILayout.Space(5);
            GUILayout.Label("玩家名:");
            Lobby.PlayerName = GUILayout.TextField(Lobby.PlayerName, 20);

            GUILayout.Space(5);
            GUILayout.Label("房名:");
            m_RoomNameField = GUILayout.TextField(m_RoomNameField, 20);

            GUILayout.Space(5);
            if (GUILayout.Button("创建房间 (Host)"))
            {
                string name = string.IsNullOrEmpty(m_RoomNameField) ? "测试房间" : m_RoomNameField;
                Lobby.CreateRoom(name);
            }

            if (GUILayout.Button("刷新房间列表"))
            {
                if (Discovery != null)
                {
                    Discovery.ClearRooms();
                    Discovery.StartListening();
                }
            }

            GUILayout.Space(5);
            GUILayout.Label("已发现房间:");
            if (Discovery != null)
            {
                Discovery.RefreshRoomList();
                var sb = new StringBuilder();
                foreach (var r in Discovery.Rooms)
                {
                    string state = r.isStarted ? "对局中" : $"{r.currentPlayers}/{r.maxPlayers}人";
                    sb.AppendLine($"{r.roomName} [{state}] {r.hostEndPoint.Address}:{r.gamePort}");
                }
                string list = sb.Length == 0 ? "（无）" : sb.ToString();
                GUILayout.Label(list);

                if (GUILayout.Button("加入第一个可加入房间"))
                {
                    LanDiscovery.RoomEntry target = null;
                    foreach (var r in Discovery.Rooms)
                    {
                        if (!r.isStarted && r.currentPlayers < r.maxPlayers)
                        {
                            target = r;
                            break;
                        }
                    }
                    if (target != null)
                        Lobby.JoinRoom(target);
                }
            }
        }
        else
        {
            // 已在房间内
            GUILayout.Space(5);
            GUILayout.Label("房内成员:");
            var names = Lobby.GetPlayerNames();
            var sb = new StringBuilder();
            for (int i = 0; i < names.Count; i++)
            {
                string roleTag = i == 0 ? "（房主）" : "";
                sb.AppendLine($"{i + 1}. {names[i]} {roleTag}");
            }
            GUILayout.Label(sb.ToString());

            GUILayout.Space(5);
            if (Lobby.IsHost)
            {
                if (GUILayout.Button("开始游戏 (阶段2接入场景切换)"))
                {
                    Lobby.StartGame();
                }
            }

            if (GUILayout.Button("离开房间"))
            {
                Lobby.LeaveRoom();
            }
        }

        GUILayout.EndVertical();
        GUILayout.EndArea();
    }

    private string m_RoomNameField = "测试房间";
}
