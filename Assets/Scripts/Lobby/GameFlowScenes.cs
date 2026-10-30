using UnityEngine;

/// <summary>Authored scene destinations shared by menu, lobby and dedicated-server flows.</summary>
public static class GameFlowScenes
{
    public const string Login = "Login";
    // Both client menu stages are pages inside the authored GameLobby scene.
    public const string MainMenu = "GameLobby";
    public const string MultiplayerLobby = "GameLobby";
    public const string PvpMatch = "GamePVP";
    public const string Match = PvpMatch;

    public static bool TryValidateSceneAvailable(string sceneName, out string message)
    {
        if (!string.IsNullOrWhiteSpace(sceneName) && Application.CanStreamedLevelBeLoaded(sceneName))
        {
            message = string.Empty;
            return true;
        }

        message = "场景 " + sceneName + " 尚未就绪。请在编辑器中制作并保存该场景，" +
                  "然后加入 File > Build Settings 的 Scenes In Build 并勾选。";
        return false;
    }
}
