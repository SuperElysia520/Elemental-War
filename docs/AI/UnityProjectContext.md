# Unity 项目上下文

<!-- unity-onboarding:generated:start -->

## 项目概况

- 项目根目录：`E:/Unity资产/FPS`
- 核查日期：2026-10-03；本轮基线提交：`3f386f4`；主菜单/独立大厅流程及用户制作的 GameLobby 随分支冲突修复纳入 main。编辑器原有未保存 GameStart 已在改动前保留完整副本。
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

- Build Settings 中启用 `GameStart`、`GameLobby`、`GamePVP`、`GamePVE`，入口为 `GameStart`。
- PvE 由玩家 `StartHost()`，通过局域网 UDP 广播发现房间。
- PvP 由云端每个房间槽位启动一个 `StartServer()` 进程；客户端向固定地址的状态查询端口发送 UDP 请求，再连接对应游戏端口。服务器通过 NGO 场景管理加载 `GamePVP`。
- 当前场景与构建工具已调整为 1 个 PvP 试运行槽位：游戏 UDP 7777，查询 UDP 48777。公网地址保存在 `Assets/Scenes/GameStart.unity` 的 `LobbyManager.dedicatedServerAddress` 字段；迁移云厂商需更新此值和客户端构建。不要在上下文文档中复制公网地址。
- `LobbyManager` 使用 `Application.version` 和账号发现协议版本 3 拦截旧客户端/服务器；PVP 通过一次性账号票据取得服务端批准的角色与配装快照。

## 2026-10-03 独立前端场景

- 用户将手工制作 `GameLobby` 大厅主页；不会在代码中生成大厅场景/布局。`GameStart` 为第一张主菜单，进入大厅改为加载 `GameLobby`，其开始游戏按钮打开沿用的房间列表。角色/服装/配装暂时沿用现有界面。
- 主菜单展示改为 RifleGirl 并绑定 `AccountCharacterPreview`，与用户将摆入大厅的同一 prefab 读取账号已保存外观。旧 Lumine 展示实例禁用保留，修改前完整编辑器场景副本在 `tmp/MainMenuFlow/main-menu-before.unity`。
- 专服启动和空房回收也使用 `GameLobby`。正常结算默认 8 秒后全员返回，保留成员、房主、队伍和批准的档案；下一局重新生成玩家。主动离开/掉线回正式大厅，但不留在原房间。
- 保留房间后保存外观/配装使用新账号票据更新原身份快照；开局前全员核实最新档案，失败/成员退出/90 秒超时取消并提示房主。联机协议已升级，需在大厅制作完成后同步重建两端。
- 前次实际 Unity Play Mode 的主菜单账号、外观和缺失大厅提示检查 52 项通过、0 运行错误；HTTP 设置及进程环境恢复。当时 GameLobby 尚未制作；本次分支合并已保留用户制作的场景及 Build Settings 配置，关键脚本、角色与按钮引用静态检查通过。完整双客户端赛后返回与再开局仍需运行验证。接线步骤见 [MainMenuGameLobbyFlow.md](MainMenuGameLobbyFlow.md)。

## 2026-09-26 PVP 改造

- PVP 使用同一副 RifleGirl 全身骨架呈现本地第一人称和远端角色；大厅角色/服装界面配置外观及主副武器。PVE 保持既有流程。
- `Assets/Scripts/Customization`、`Account`、`PvpWeapons` 及 `PvpFullBodyController`、`PvpLoadoutCombat` 分别负责档案、账号、枪械表现、全身控制和专服射击结算。三把参考枪为步枪、手枪和狙击枪，不含霰弹枪；未引入参考项目的 Mirror。
- `Server/AccountService` 为 .NET 10 账号和持久档案后端；Windows Server 2022 IP HTTPS 部署包已准备，尚未在雨云上线。客户端生产地址由交付启动器注入，设置资源默认留空。
- 具体实现、限制与最终证据见 [PvpRifleGirlImplementation.md](PvpRifleGirlImplementation.md)；后端部署见 [AccountService.md](AccountService.md)。动态布料因当前 Collections 与已安装 Magica Cloth 的版本条件不符而未启用。

## 代码风格与验证

- 第一方脚本多数无命名空间，使用 `MonoBehaviour`/`NetworkBehaviour`，公开字段作 Inspector 配置，并使用中文注释及 UI 文本。
- 项目已安装 Unity Test Framework；本次新增实际账号 HTTP 集成测试、Windows 部署脚本验证及仅 Editor/Development 启用的双客户端 PVP 流程检查，尚无 CI 配置。
- 最终 Windows 客户端与 Dedicated Server 构建均 0 错误，本机两客户端真实联机流程 `6b46b942` 两端 PASS、0 运行错误；包括账号存档、配装同步、开火换弹、击杀复活及返回大厅。尚未执行雨云公网部署或跨电脑公网验收。当前会话没有 Unity Editor MCP，通过固定操作白名单的编辑器工作台调用实际构建与校验。

## 迁移约束与未知项

- 旧 `Builds/PvpServer/Windows/`、`Builds/RainyunTrial/` 保留历史构建，均未包含这次角色/账号协议。匹配的交付客户端、单房间专服和账号包在 `Builds/RifleGirlMigration/Packages/`。Linux 构建需要先安装对应 Unity 模块并重新构建。
- 构建服务端后，编辑器活动子目标可能留在 Dedicated Server，原 `DedicatedServerBootstrap` 因 `UNITY_SERVER` 在 Play 模式自动启动服务端，使编辑器中建房提示「当前已经在一个联机房间中」。已改为仅正式 Server 构建默认自动启动；编辑器需显式 `-dedicatedPvpServer` 参数才模拟服务端。Unity 已重新编译，用户已在 Play 中成功创建 PVP 房间。
- 2026-09-26 用户确认雨云当前为 Windows Server 2022、无域名、通过远程桌面管理。已准备上传与 IP HTTPS 部署说明，但当前工具没有该服务器的远程管理连接；服务器资源余量、端口占用和公网连通仍须在实际部署时检查。
- `Builds/` 是 Git 忽略目录；源码与历史构建产物可能不完全同步，应从同一提交重新构建客户端和服务器。
- 本轮开始时 Git 工作树干净，但编辑器 GameStart 有用户未保存改动；已先保存副本再接线并保留原改动。字体资产随 Unity 动态字形加入有序列化变化；不要用清理操作丢弃用户内容。

## 核查依据

`ProjectSettings/ProjectVersion.txt`、`ProjectSettings/ProjectSettings.asset`、`ProjectSettings/EditorBuildSettings.asset`、`Packages/manifest.json`、`README.md`、`Assets/Scenes/GameStart.unity`、`Assets/Scripts/Lobby/LobbyManager.cs`、`Assets/Scripts/Lobby/LanDiscovery.cs`、`Assets/Scripts/Lobby/DedicatedServerBootstrap.cs`、`Assets/Editor/PvpDedicatedServerBuilder.cs`。

<!-- unity-onboarding:generated:end -->
