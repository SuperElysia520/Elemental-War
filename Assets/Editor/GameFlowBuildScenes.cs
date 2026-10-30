using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

/// <summary>客户端和专服使用同一份正式大厅场景，避免 NGO 初始同步加载回主菜单。</summary>
public static class GameFlowBuildScenes
{
    public static string[] Get(bool server)
    {
        var enabled = EditorBuildSettings.scenes.Where(scene => scene.enabled).ToArray();
        var required = new List<string>
        {
            GameFlowScenes.MultiplayerLobby,
            GameFlowScenes.PvpMatch,
        };
        // PVE is optional while only the Login/Lobby/PVP flow is enabled by the author.
        if (!server && enabled.Any(scene => Path.GetFileNameWithoutExtension(scene.path) == "GamePVE")) required.Add("GamePVE");
        var result = new List<string>();
        if (!server) required.Insert(0, GameFlowScenes.Login);
        foreach (string name in required)
        {
            var matching = enabled.Where(scene => Path.GetFileNameWithoutExtension(scene.path) == name).ToArray();
            if (matching.Length != 1 || !File.Exists(matching[0].path))
                throw new InvalidOperationException("请在编辑器中完成 " + name + " 场景，并将唯一的正式场景加入 Build Settings。大厅必须由开发者手工搭建。当前缺失或重名：" + name);
            result.Add(matching[0].path);
        }
        return result.ToArray();
    }
}
