using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Dedicated Server 从 GameStart 保留网络根对象，先载入用户制作的 GameLobby，再启动 NGO Server。
/// 普通客户端和编辑器 Play 模式默认不会执行；开发阶段可添加 -dedicatedPvpServer 参数模拟服务器进程。
/// </summary>
public static class DedicatedServerBootstrap
{
    private const string ServerFlag = "-dedicatedPvpServer";
    private const string PortArgument = "-port";
    private static bool s_Started;
    private static bool s_Starting;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        s_Started = false;
        s_Starting = false;
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Initialize()
    {
        if (!ShouldRunDedicatedServer())
            return;

        if (!TryStartServer())
            SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (TryStartServer())
            SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private static bool TryStartServer()
    {
        if (s_Started || s_Starting)
            return true;
        if (LobbyManager.Instance == null)
            return false;

        if (!GameFlowScenes.TryValidateSceneAvailable(GameFlowScenes.MultiplayerLobby, out string message))
        {
            Debug.LogError("[Dedicated PVP] 服务器未启动：" + message);
            // Missing authored content cannot be repaired by loading the main menu or creating a substitute lobby.
            return true;
        }
        s_Starting = true;
        LobbyManager.Instance.StartCoroutine(LoadLobbyAndStartServer(LobbyManager.Instance));
        return true;
    }

    private static IEnumerator LoadLobbyAndStartServer(LobbyManager manager)
    {
        try
        {
            if (SceneManager.GetActiveScene().name != GameFlowScenes.MultiplayerLobby)
                yield return SceneManager.LoadSceneAsync(GameFlowScenes.MultiplayerLobby, LoadSceneMode.Single);
            if (manager == null)
            {
                Debug.LogError("[Dedicated PVP] GameStart 的 NetworkManager 未保留，服务器未启动。");
                yield break;
            }
            s_Started = manager.StartDedicatedPvpServer(GetIntArgument(PortArgument, 0));
            if (!s_Started)
                Debug.LogError("[Dedicated PVP] GameLobby 已载入，但服务器启动失败。请检查网络根对象和端口。");
        }
        finally
        {
            s_Starting = false;
        }
    }

    private static bool ShouldRunDedicatedServer()
    {
#if UNITY_SERVER && !UNITY_EDITOR
        return true;
#else
        string[] arguments = Environment.GetCommandLineArgs();
        foreach (string argument in arguments)
        {
            if (string.Equals(argument, ServerFlag, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
#endif
    }

    private static int GetIntArgument(string name, int fallback)
    {
        string[] arguments = Environment.GetCommandLineArgs();
        for (int i = 0; i < arguments.Length; i++)
        {
            string argument = arguments[i];
            if (string.Equals(argument, name, StringComparison.OrdinalIgnoreCase) && i + 1 < arguments.Length &&
                int.TryParse(arguments[i + 1], out int separatedValue))
            {
                return separatedValue;
            }

            string prefix = name + "=";
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(argument.Substring(prefix.Length), out int inlineValue))
            {
                return inlineValue;
            }
        }

        return fallback;
    }
}
