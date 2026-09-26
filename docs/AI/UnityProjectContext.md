# Unity 项目上下文

<!-- unity-onboarding:generated:start -->

## 项目概况

- 项目根目录：`E:/Unity资产/FPS`
- 核查日期：2026-09-25；提交：`987caa2`（2026-08-22）
- 第一人称射击游戏；单机玩法、局域网合作 PvE 和公网专用服务器 PvP 共存。`README.md` 的玩法简介未覆盖后续加入的 PvP 联机实现。

## 已确认环境

- Unity 2022.3.62f3c1；URP 14.0.12。
- Input System 包位于 `Packages/com.unity.inputsystem`，`activeInputHandler: 2` 表示旧、新输入同时启用。
- Netcode for GameObjects 1.15.1 + Unity Transport，PvP 通过 UDP 连接专用服务器。
- 客户端为 Windows；`Assets/Editor/PvpDedicatedServerBuilder.cs` 提供 Windows 客户端及 Windows/Linux Dedicated Server 构建入口。本机已安装 Windows Server 构建模块，未发现 Linux Server 构建模块。

## 主要目录与程序集

| 位置 | 用途 |
| --- | --- |
| `Assets/Scripts/Lobby` | 房间状态、PvE 局域网发现、PvP 专用服务器查询及启动 |
| `Assets/Scripts/Player`、`Enemy`、`Combat` | 玩家、敌人及战斗逻辑 |
| `Assets/Scripts/UI` | 菜单及游戏 HUD |
| `Assets/Editor`、`Assets/Scripts/Lobby/Editor` | 专用服务器构建及场景编辑器工具 |
| `Assets/Scenes`、`Assets/Resources` | 场景和运行时加载的网络玩家资源 |

第一方运行时代码主要编入默认 `Assembly-CSharp`；编辑器代码编入默认 `Assembly-CSharp-Editor`。发现的 `.asmdef` 主要属于导入包和插件。

## 启动与联机流程

- Build Settings 中启用 `GameStart`、`GamePVP`、`GamePVE`，入口为 `GameStart`。
- PvE 由玩家 `StartHost()`，通过局域网 UDP 广播发现房间。
- PvP 由云端每个房间槽位启动一个 `StartServer()` 进程；客户端向固定地址的状态查询端口发送 UDP 请求，再连接对应游戏端口。服务器通过 NGO 场景管理加载 `GamePVP`。
- 当前场景与构建工具已调整为 1 个 PvP 试运行槽位：游戏 UDP 7777，查询 UDP 48777。公网地址保存在 `Assets/Scenes/GameStart.unity` 的 `LobbyManager.dedicatedServerAddress` 字段；迁移云厂商需更新此值和客户端构建。不要在上下文文档中复制公网地址。
- `LobbyManager` 使用 `Application.version` 拦截客户端与服务器版本不一致的连接。

## 代码风格与验证

- 第一方脚本多数无命名空间，使用 `MonoBehaviour`/`NetworkBehaviour`，公开字段作 Inspector 配置，并使用中文注释及 UI 文本。
- 项目已安装 Unity Test Framework，但未找到第一方 EditMode/PlayMode 测试或 CI 配置。
- 本次已在运行中的 Unity Editor 构建 Windows 客户端与单房间 Dedicated Server，并在本机用独立端口启动服务器，日志确认游戏与查询端口监听。尚未在雨云公网部署及双客户端联机验证。当前 Codex 会话没有 Unity Editor MCP 工具。

## 迁移约束与未知项

- 旧 `Builds/PvpServer/Windows/` 仍是迁移前的 5 进程构建；新客户端与单房间服务器在 `Builds/RainyunTrial/`。Linux 构建需要先安装对应 Unity 模块并重新构建。
- 构建服务端后，编辑器活动子目标可能留在 Dedicated Server，原 `DedicatedServerBootstrap` 因 `UNITY_SERVER` 在 Play 模式自动启动服务端，使编辑器中建房提示「当前已经在一个联机房间中」。已改为仅正式 Server 构建默认自动启动；编辑器需显式 `-dedicatedPvpServer` 参数才模拟服务端。Unity 已重新编译，用户已在 Play 中成功创建 PVP 房间。
- 2026-09-25 雨云控制台旧截图显示「宝塔面板10.0 [Debian12/LNMP]」，公网 IPv4 `114.66.59.53`，配置 2 vCPU、2 GB 内存、约 30 GB 系统盘、10 Mbps。用户确认系统盘无需保留数据，随后报告已能通过远程桌面连接。仍需复核重装后的系统版本与公网 IP，上传现有 Windows Dedicated Server 构建并验证公网联机。
- `Builds/` 是 Git 忽略目录；源码与历史构建产物可能不完全同步，应从同一提交重新构建客户端和服务器。
- 工作区已有用户未提交的字体资产、`.claude/` 和 `tmp/` 变动；处理服务器迁移时保留这些内容。

## 核查依据

`ProjectSettings/ProjectVersion.txt`、`ProjectSettings/ProjectSettings.asset`、`ProjectSettings/EditorBuildSettings.asset`、`Packages/manifest.json`、`README.md`、`Assets/Scenes/GameStart.unity`、`Assets/Scripts/Lobby/LobbyManager.cs`、`Assets/Scripts/Lobby/LanDiscovery.cs`、`Assets/Scripts/Lobby/DedicatedServerBootstrap.cs`、`Assets/Editor/PvpDedicatedServerBuilder.cs`。

<!-- unity-onboarding:generated:end -->
