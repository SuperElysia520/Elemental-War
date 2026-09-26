using System;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Dedicated Server 构建进入 GameStart 后自动启动 NGO Server。
/// 普通客户端和编辑器 Play 模式默认不会执行；开发阶段可添加 -dedicatedPvpServer 参数模拟服务器进程。
/// </summary>
public static class DedicatedServerBootstrap
{
    private const string ServerFlag = "-dedicatedPvpServer";
    private const string PortArgument = "-port";
    private static bool s_Started;

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
        if (s_Started)
            return true;
        if (LobbyManager.Instance == null)
            return false;

        int portOverride = GetIntArgument(PortArgument, 0);
        s_Started = LobbyManager.Instance.StartDedicatedPvpServer(portOverride);
        return s_Started;
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
