using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class PvpDedicatedServerBuilder
{
    private const int ServerStartPort = 7777;
    private const int ServerRoomCount = 1;

    private static readonly string[] ServerScenes =
    {
        "Assets/Scenes/GameStart.unity",
        "Assets/Scenes/GamePVP.unity",
    };

    private static readonly string[] ClientScenes =
    {
        "Assets/Scenes/GameStart.unity",
        "Assets/Scenes/GamePVP.unity",
        "Assets/Scenes/GamePVE.unity",
    };

    [MenuItem("Tools/PVP Dedicated Server/Build Windows Client")]
    public static void BuildWindowsClient()
    {
        const string outputPath = "Builds/RainyunTrial/Client/FPS.exe";
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

        var options = new BuildPlayerOptions
        {
            scenes = ClientScenes,
            locationPathName = outputPath,
            target = BuildTarget.StandaloneWindows64,
            subtarget = (int)StandaloneBuildSubtarget.Player,
            options = BuildOptions.None,
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        if (report.summary.result == BuildResult.Succeeded)
            Debug.Log("[PVP Client] 构建完成：" + outputPath + "，大小=" + report.summary.totalSize + " bytes。");
        else
            Debug.LogError("[PVP Client] 构建失败：" + report.summary.result + "。请查看 Unity Build 日志。");
    }

    [MenuItem("Tools/PVP Dedicated Server/Build Windows Server")]
    public static void BuildWindowsServer()
    {
        BuildServer(
            BuildTarget.StandaloneWindows64,
            "Builds/RainyunTrial/Server/FPS_PVP_Server.exe");
    }

    [MenuItem("Tools/PVP Dedicated Server/Build Linux Server")]
    public static void BuildLinuxServer()
    {
        BuildServer(
            BuildTarget.StandaloneLinux64,
            "Builds/PvpServer/Linux/FPS_PVP_Server.x86_64");
    }

    private static void BuildServer(BuildTarget target, string outputPath)
    {
        if (!EnsureDedicatedServerSupport(target))
            return;

        string directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var options = new BuildPlayerOptions
        {
            scenes = ServerScenes,
            locationPathName = outputPath,
            target = target,
            subtarget = (int)StandaloneBuildSubtarget.Server,
            options = BuildOptions.Development,
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        if (report.summary.result == BuildResult.Succeeded)
        {
            WriteServerLauncherScripts(target, outputPath);
            Debug.Log("[Dedicated PVP] 构建完成：" + outputPath +
                      "，大小=" + report.summary.totalSize + " bytes。已生成" +
                      ServerRoomCount + "房间启动脚本。");
        }
        else
        {
            Debug.LogError("[Dedicated PVP] 构建失败：" + report.summary.result +
                           "。请查看本条日志之前的 Unity Build 错误以确认具体原因。");
        }
    }

    private static bool EnsureDedicatedServerSupport(BuildTarget target)
    {
        string playbackEngineName;
        string platformName;
        string moduleName;

        switch (target)
        {
            case BuildTarget.StandaloneWindows64:
                playbackEngineName = "windowsstandalonesupport";
                platformName = "Windows";
                moduleName = "Dedicated Server Build Support (Windows)";
                break;
            case BuildTarget.StandaloneLinux64:
                playbackEngineName = "LinuxStandaloneSupport";
                platformName = "Linux";
                moduleName = "Dedicated Server Build Support (Linux)";
                break;
            default:
                Debug.LogError("[Dedicated PVP] 不支持的服务器构建平台：" + target);
                return false;
        }

        string variationsPath = Path.Combine(
            EditorApplication.applicationContentsPath,
            "PlaybackEngines",
            playbackEngineName,
            "Variations");

        if (Directory.Exists(variationsPath) &&
            Directory.GetDirectories(variationsPath, "*server*", SearchOption.TopDirectoryOnly).Length > 0)
        {
            return true;
        }

        string message =
            $"当前 Unity {Application.unityVersion} 没有安装 {platformName} Dedicated Server 构建模块。\n\n" +
            "请关闭 Unity Editor，然后打开 Unity Hub：\n" +
            $"安装 (Installs) → Unity {Application.unityVersion} 右侧齿轮/三个点 → 添加模块 (Add modules) → " +
            $"勾选 {moduleName} 并安装。\n\n" +
            "安装完成并重新打开项目后，再执行此构建菜单。";

        Debug.LogError("[Dedicated PVP] " + message.Replace("\n", " "));
        EditorUtility.DisplayDialog("缺少 Dedicated Server 构建模块", message, "确定");
        return false;
    }

    private static void WriteServerLauncherScripts(BuildTarget target, string outputPath)
    {
        string directory = Path.GetDirectoryName(outputPath);
        string executableName = Path.GetFileName(outputPath);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(executableName))
            return;

        int endPort = ServerStartPort + ServerRoomCount - 1;
        var utf8WithoutBom = new UTF8Encoding(false);

        if (target == BuildTarget.StandaloneWindows64)
        {
            string startScript =
                "@echo off\r\n" +
                "setlocal\r\n" +
                "set \"SERVER_EXE=%~dp0" + executableName + "\"\r\n" +
                "if not exist \"%SERVER_EXE%\" (\r\n" +
                "  echo Server executable not found: %SERVER_EXE%\r\n" +
                "  pause\r\n" +
                "  exit /b 1\r\n" +
                ")\r\n" +
                "for /L %%P in (" + ServerStartPort + ",1," + endPort + ") do (\r\n" +
                "  start \"PVP Server %%P\" /B \"%SERVER_EXE%\" -batchmode -nographics -port %%P -logFile \"%~dp0server_%%P.log\"\r\n" +
                ")\r\n" +
                "echo Started PVP server slots on UDP ports " + ServerStartPort + "-" + endPort + ".\r\n" +
                "echo Discovery query ports are " +
                (ServerStartPort + LanDiscovery.DedicatedDiscoveryPortOffset) + "-" +
                (endPort + LanDiscovery.DedicatedDiscoveryPortOffset) + ".\r\n" +
                "endlocal\r\n";
            string stopScript =
                "@echo off\r\n" +
                "taskkill /IM \"" + executableName + "\" /F\r\n" +
                "echo All PVP server processes have been stopped.\r\n";

            string roomSuffix = ServerRoomCount == 1 ? "1_PVP_Server" : ServerRoomCount + "_PVP_Servers";
            File.WriteAllText(Path.Combine(directory, "Start_" + roomSuffix + ".bat"), startScript, utf8WithoutBom);
            File.WriteAllText(Path.Combine(directory, "Stop_" + roomSuffix + ".bat"), stopScript, utf8WithoutBom);
            return;
        }

        if (target == BuildTarget.StandaloneLinux64)
        {
            string startScript =
                "#!/usr/bin/env bash\n" +
                "set -eu\n" +
                "server_dir=\"$(cd \"$(dirname \"$0\")\" && pwd)\"\n" +
                "pid_file=\"$server_dir/pvp_server_pids.txt\"\n" +
                ": > \"$pid_file\"\n" +
                "for port in $(seq " + ServerStartPort + " " + endPort + "); do\n" +
                "  nohup \"$server_dir/" + executableName + "\" -batchmode -nographics -port \"$port\" -logFile \"$server_dir/server_$port.log\" >/dev/null 2>&1 &\n" +
                "  echo $! >> \"$pid_file\"\n" +
                "done\n" +
                "echo \"Started PVP server slots on UDP ports " + ServerStartPort + "-" + endPort + ".\"\n";
            string stopScript =
                "#!/usr/bin/env bash\n" +
                "set -eu\n" +
                "server_dir=\"$(cd \"$(dirname \"$0\")\" && pwd)\"\n" +
                "pid_file=\"$server_dir/pvp_server_pids.txt\"\n" +
                "if [ -f \"$pid_file\" ]; then\n" +
                "  while IFS= read -r pid; do kill \"$pid\" 2>/dev/null || true; done < \"$pid_file\"\n" +
                "  rm -f \"$pid_file\"\n" +
                "fi\n";

            string roomSuffix = ServerRoomCount == 1 ? "1_pvp_server" : ServerRoomCount + "_pvp_servers";
            File.WriteAllText(Path.Combine(directory, "start_" + roomSuffix + ".sh"), startScript, utf8WithoutBom);
            File.WriteAllText(Path.Combine(directory, "stop_" + roomSuffix + ".sh"), stopScript, utf8WithoutBom);
        }
    }
}
