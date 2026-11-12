# Unity 项目上下文

<!-- unity-onboarding:generated:start -->

## 2026-11-12 首次游戏名称与组队名称同步

- 用户新增的 InitialNamePanel.prefab 已接入 Login。读取云端档案后，UGS 账号 personal.displayName 为空才进入 InitialName 状态；1–20字名称保存到既有 fps_personal_v1 成功后才加载 GameLobby。失败保留输入、提示重试，请求中禁用重复提交。已有云端名称的账号重复登录/换设备不再提示；登录用户名和 UID 不变。SelfHosted 流程保持原有行为。
- 新 InitialNamePanelView 只显示状态和发送确认意图；LoginSceneController 控制读取、命名、保存与场景加载；LoginUI 绑定现有面板。InitialNameSetup 已将用户 Prefab 放到 Login 现有 Canvas 并添加一行状态提示；原图像、输入框与按钮布局保留。备份位于 tmp/InitialName20261112。
- PartyMemberSlot 与旧 MultiplayerLobbyUI 使用 PlayerProfileData.DisplayName。bridge 的 UgsCloudStore / UgsProfiles 新增读取受保护 fps_personal_v1，服务端 Profile 携带独立 Personal / PersonalRevision；原 fps_profile_v1 格式和 Username 认证校验不变。纯名称/性别/签名刷新不会取消队员准备或队列；角色 revision 变化仍按原规则取消准备。
- 权威专服入场与准备刷新使用云端名称更新 m_ClientNames / LobbyState.PlayerName，随后对局角色和计分板沿用该显示名。入场身份和匹配名单仍使用 accountId。
- 100项真实场景离线 UI 检查通过，包括初次弹出、非法输入、实际鼠标射线命中、保存中禁止重复提交、失败重试、已有名称跳过及组队名称显示。bridge 25场景189断言、HTTP/REST87断言通过。证据 tmp/InitialName20261112；未部署线上或重新构建分发包。
- 编辑器原有未保存 GameLobby 已备份并保持未保存状态；只保存 Login 新接线。LobbyMvcValidation 改用 playModeStartScene 启动测试，退出后恢复原启动设置并核对原场景与 dirty 状态，避免为了验证而保存/丢弃用户编辑。运行前后三项资产哈希不变，已有两条 Visual Scripting 初始化异常单列 baseline。


## 2026-11-10 ProfilePanel 重设计接线与专服云端生涯

- ProfilePanel 使用用户重做的 FirstPagePanel / InformationPanel，HomePanel 头像每次进入都重置到首页并将首页按钮染黄；页签互斥显示，当前按钮持续黄色。沿用现有 MVC，View 不调用账号或网络单例。
- 首页接入显示昵称、性别图标、80字内联签名编辑，以及原六张战况卡（总场次、胜场、击杀、死亡、爆头率、K/D）。信息页沿用改名弹窗、男/女 Toggle 和头像选择；昵称最长20字，与登录用户名、UID 分开。保存只接受云端确认，失败保留输入，Escape 优先关闭改名/头像子面板。
- 新个人资料使用 Protected fps_personal_v1（独立 revision + personal），生涯使用 Protected fps_career_v1；原 fps_profile_v1 的身份和角色存档格式保留，旧账号未设置资料时回退原名字且不初始化云端数据。重新打开个人主页显式读取云端档案、个人资料与生涯，便于跨设备更新及冲突后重读；场景重入本身仍不重复读档。
- ReviseAvatarPanel 使用独立 Viewport + RectMask2D，原6列30项 Grid Content 使用 ContentSizeFitter 扩高，ScrollRect 仅垂直、Clamped，原滚动条改为可见并绑定。ProfilePanelSetup 编辑器工具先备份，再只接线用户 Prefab；未重新创建原面板或恢复旧布局。备份在 tmp/ProfilePanel20261110。
- 用户明确选择专服写入并跨设备保存生涯，未使用本机累计。专服在击杀和生成时采集权威计数，保留断线成员记录；已完成匹配/自定义 PvP 结算写入按端口隔离的 CareerOutbox，向既有回环 HTTP 服务提交。新增 UgsCareerStore + UgsCareerUploader 持久化回执、排序上传 Protected 统计、使用真实写锁和每玩家 lastSequence 去重，覆盖重启、部分写入及响应丢失。
- 部署必须保留专服 CareerOutbox 和 bridge AccountStorePath 同目录的 career-results 状态，不能在升级时重置结算序号。需要发布新版 FpsProfile（新增可选JSON参数personal），更新雨云 bridge 与战斗专服并使用新版客户端。历史无统计的对局不能补算；本轮未修改线上账号或部署服务，真实双客户端与跨设备验收仍需上线后执行。详见 UgsPvpRainyunSetup.md 的专服生涯战绩同步节。
- 验证证据放在 tmp/ProfilePanel20261110：90项 Unity 运行交互验证、98项 Cloud Code 检查、19项云端生涯断言、83项真实回环/假云端 HTTP 检查、24场景176项 bridge 断言。运行错误中的两条已知 Visual Scripting 初始化异常单列 baseline，场景恢复停止的 GameLobby。


## 2026-11-10 新注册账号数字 UID

- 用户确认采用九位数字，从 111100001 开始递增；随后明确取消两个现有账号的补号，旧账号保留原 UID 和角色数据。不要再索取它们的账号信息、迁移或自动补号。
- AccountUid 固定启用时刻为 2026-11-10 01:21:16 UTC（北京时间 09:21:16）。UgsAccountGateway 核对 Authentication SDK PlayerInfo.CreatedAt；启用前的账号标记 HasLegacyUid，不调用 UID 分配脚本，DisplayUid 显示原 Player ID。新的注册和之后登录使用独立的数字 UID。启用时刻不可随以后的构建更新。
- FpsAccountUid.js 使用认证上下文身份、Protected fps_uid_v1 和 Private Custom Item fps_account_uid_v1 / sequence。计数器 CAS 与 pending 可恢复中断分配，重复请求不会重复占号。原 fps_profile_v1 格式、Authentication Player ID、队伍与专服身份协议不变。
- 客户端在注册确认前分配 UID，普通新账号登录可恢复中断请求；外观/配装/头像载入和保存将同一数字合并进本地 PlayerProfileData.uid。界面统一显示数字；旧账号 uid=0 只在明确的 HasLegacyUid 会话下允许，并保留原显示。
- 用户选择自行操作 Unity Dashboard。云端尚未发布，也没有改写两个真实账号；只需在 development 的 Game Data Private 首次建立 sequence={version:1,nextUid:111100001,pending:null}，再发布 FpsAccountUid（必填 String 参数 operation）。不创建两个旧账号的 UID 键，不要求雨云更新。具体步骤见 docs/AI/AccountNumericUid.md。
- 初版旧账号迁移工具及其测试已经按用户取消范围删除；tmp/UgsIntegration/Uid20261110 里的旧迁移测试资料仅为假 HTTP 的历史证据，不代表真实云端修改。
- 验证：Unity Editor 编译通过，71 项离线 UI 检查、296 项云脚本测试通过，账号离线回归完成 103 项功能断言，包含旧账号不补号与新账号数字 UID。账号报告仍仅因两条已有 Visual Scripting 初始化异常标记失败；编辑器已恢复停止状态的 Login。证据见 tmp/UgsIntegration/Uid20261110。验证工具现等待 Application 与 Editor 都退出运行后恢复场景，并将可选进度文件的读取竞争隔离，避免清理阶段卡住。


## 2026-11-10 Login 合并用户数据读取

- 初始读档统一由 `LoginSceneController` 在 Login 的现有 LoadingPanel 内调用 `AccountClient.RefreshProfile`。成功后才创建 GameLobby 的异步场景加载任务；网络失败留在 Login 的 Ready 面板并保留会话，失效会话回到登录表单。读档及场景加载都受同一 processing 锁保护。
- `LoginUI.loadingStatusText` 已绑定用户新增的 TMP Status；读取、加载大厅、进入大厅分别显示状态。现有 Progress 也是 TMP，已接入 `loadingProgressTmpText`；保留旧 Text 可选字段。读档期间保持 0%，之后显示真实 `AsyncOperation.progress`，100% 显示一帧后激活场景。
- GameLobby 的 LoadingOverlay 实例及 `LobbyUIRoot.loading` 字段已移除，旧 Prefab 资产保留。`LoginSceneBootstrap` / `GameStartProfileLoader` 继续提供现有就绪接口，但正常大厅启动只复用已读档案；缺档案返回 Login，比赛返回大厅也不重复读档。显式旧接口 `LoadUserData()` 仍可刷新。
- `LoginSceneSetup.IntegrateProfileLoading` 已完成场景引用迁移，先备份开放场景（含未保存编辑）再保存，未更改用户现有布局或 LoadingPanel Prefab。备份、加载截图和验证报告在 `tmp/LoginFlow/IntegratedLoading20261110`。
- Unity Editor 编译通过；71 项真实 Login/GameLobby 离线 UI 验证通过，含延迟读档、失败重试、失效会话、禁止重复提交、三个 Status 阶段、TMP 百分比、大厅无重复读档与场景重入。运行报告将原有两条 Visual Scripting 初始化异常单列为 baselineErrors，无新增运行错误。场景和两个加载 Prefab 的验证前后 SHA256 一致。未请求线上 UGS、未重新构建客户端或专服。
- 账号离线回归的 94 项功能断言完成，证据 `tmp/LoginFlow/IntegratedLoading20261110/ugs-regression.json`；该旧报告仍因同样两条 Visual Scripting 异常标记 success=false。修正旧验证用例在会话过期已触发返回时再次加载 Login 的时序错误，改为等待现有跳转，避免销毁重新进入所用的控制器。验证恢复到原先已保存、停止运行的 Login。
- 用户已在迁移后继续调整首页布局；常规运行验证使用当前 Prefab 接线和交互，历史布局快照只由专门的迁移 Audit 比对，不能为通过旧快照而恢复用户布局。详细流程见 `docs/AI/LoginSceneFlow.md` 和 `docs/AI/LobbyMvcUI.md`。

## 2026-10-30 大厅 UI MVC 迁移

- 核查范围为登录入口、大厅 UI、Prefab 和场景构建引用；基于提交 `c85800a` 及用户尚未提交的场景/素材修改。以下现状优先于本文较早的流程记录。
- 用户已确认：`Login → GameLobby/MainMenu → GameLobby/HomePanel → 组队/匹配 → GamePVP → GameLobby`；MainMenu 是登录后的第一屏，联机按钮再打开 HomePanel。保留布局与素材，迁移大厅全部已有面板。GameStart 不参与正式构建，旧场景和旧脚本保留。
- 新 UI 位于 `Assets/Scripts/UI/Mvc`。`LobbyUIRoot` 组合场景依赖，`LobbyController` / `CustomRoomController` 处理操作，`LobbyModel` / `CustomizationModel` 保存导航和编辑草稿，各 View 仅显示数据与发送按钮意图。账号与网络权威状态沿用现有服务。战斗血条使用的旧 UIManager 未改动。
- `Assets/UI/Prefab/Mvc` 包含 MainMenu、Home、Party、Rooms、CustomRoom、Customization、Profile 七个页面，消息/确认弹窗、加载遮罩和身份显示。所有页面已作为 Prefab 实例绑定到场景，Prefab 内无外部场景对象引用；五个队伍角色留在场景显式绑定。原有两个 UI Prefab 资产未覆盖。
- 两套相机与背景分别归入 `MainMenuPresentation`、`LobbyPresentation`，按页面启用。原主菜单 Lumine 只在对应页面显示，避免与首页角色重叠。身份显示读取真实 UID，玩家名在原文字区域内自适应。个人主页保留原页签，头像选择使用目录已有的 30 张素材（协议上限 32）。
- `GameFlowScenes.MainMenu` 与 `MultiplayerLobby` 均指向 GameLobby；Build Settings 仍沿用用户启用的 Login/GameLobby/GamePVP。构建工具不再要求 GameStart；GamePVE 仅在用户启用时加入，否则菜单明确提示未就绪。专服从正式 GameLobby 启动并跳过账号/UI。
- `LoginSceneBootstrap` 在 GameLobby 组合档案读取组件；兼容已有序列化的 `GameStartProfileLoader` 类名保留。Login 主动进入时重新读取档案，场景重入可复用已认证档案；显式 `LoadUserData()` 始终重新读取。
- 开始建队在服务确认前保留首页，失败提示可见；统一返回与弹窗处理，编辑草稿只在云保存成功后提交，未保存返回需明确确认。房间页关闭停止查询，Controller 销毁时取消订阅并阻止迟到回调触碰旧 UI。
- 沙箱内看不到现有 Unity 进程；进一步确认本机 2022.3 编辑器实际打开了未保存 GameLobby。已通过项目既有白名单工作台保存包含未保存修改的完整副本，再在现有编辑器迁移；副本及验证证据位于 `tmp/LobbyMvc`。没有关闭或重启用户编辑器，没有线上云请求。
- 使用 `LobbyMvcValidation` 检查 Prefab 引用、原有布局以及真实 Login/GameLobby 中的按钮与生命周期；只替换 UGS 传输边界。原 LoginFlowValidation/UgsFlowValidation 的 UI 断言同步到 MVC 入口。完整说明见 [LobbyMvcUI.md](LobbyMvcUI.md)，最终结果以 `tmp/LobbyMvc/runtime-validation.json` 为准。
- 最终证据：Unity 实际编译通过；24 项资产检查、65 项场景离线检查通过，无新增运行错误，162 个原有 UI 子对象布局保留。原 UGS 的 94 项功能断言完成，但报告仍因两条已有 Visual Scripting 初始化异常为 success=false。场景/Prefab 运行前后哈希一致，编辑器恢复已保存、停止的 GameLobby；公网双端比赛和重新打包未执行。

## 项目概况

- 项目根目录：`E:/Unity资产/FPS`
- 核查日期：2026-10-05；五人队伍匹配已接入，用户手工制作的 GameLobby 布局在接线前保存完整副本。上一轮主菜单/独立大厅流程及 GameLobby 随分支冲突修复纳入 main。
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

- 2026-10-14 起客户端入口为 `Login`，登录成功进入 `GameStart` 后请求云端档案；主菜单等待读取成功后显示。注册登录已从角色定制界面移出，手工 UI 接线见 [LoginSceneFlow.md](LoginSceneFlow.md)。令牌仍仅在进程内存中保存。
- 客户端 Build Settings 启用 `Login`、`GameStart`、`GameLobby`、`GamePVP`、`GamePVE`；专用服务器构建保留 `GameStart` 入口并跳过登录。用户将 GamePVE/Lobby/NetTest 移到 `Assets/Scenes/Test`，GamePVE 构建引用沿用 GUID 更新路径。
- PvE 由玩家 `StartHost()`，通过局域网 UDP 广播发现房间。
- PvP 由云端每个房间槽位启动一个 `StartServer()` 进程；客户端向固定地址的状态查询端口发送 UDP 请求，再连接对应游戏端口。服务器通过 NGO 场景管理加载 `GamePVP`。
- 当前场景与构建工具默认使用 10 个 PvP 槽位：游戏 UDP 7777-7786，查询 UDP 48777-48786。公网地址保存在 `Assets/Scenes/GameStart.unity` 的 `LobbyManager.dedicatedServerAddress` 字段；迁移云厂商需更新此值和客户端构建。不要在上下文文档中复制公网地址。
- `LobbyManager` 使用 `Application.version` 和账号发现协议版本 4 拦截旧客户端/服务器；PVP 通过一次性账号票据取得服务端批准的角色、配装和匹配名单快照。

## 2026-10-20 UGS 账号与存档

- 用户已选择 UGS，并已有 Unity Cloud 项目。`AccountServiceSettings` 当前默认 `UnityGamingServices`，环境 `development`，Cloud Code JavaScript 脚本 `FpsProfile`；控制台实际开通和发布需由项目拥有者完成，不能视为已经上线。
- UGS Authentication 3.6.1、Cloud Save 3.4.0、Cloud Code 2.10.3、Services Core 1.16.0 经 UPM Client API 安装。Login 的旧版 InputField/Text 和手工布局沿用。
- `AccountClient` 保留外部接口，通过 `AccountClient.Ugs.cs` / `UgsAccountGateway` 接入原生用户名密码认证；注册完成立即退出，LoginSession 不读取角色，GameStart 才读取 Protected `fps_profile_v1`。UGS 没有公开的未登录用户名存在性查询，登录失败不自动引导为“确定未注册”。
- `Server/UgsCloudCode/FpsProfile.js` 校验身份、外观、配装和头像，已有存档更新使用 revision + writeLock。默认读取不落盘，首次修改才创建档案；Cloud Save 没有已核实的原子首次创建接口，两个设备同时首次保存可能最后写入生效，详见同目录 README。
- 用户名/密码规则按 UGS，旧自建账号不自动迁移。UGS PvP 后续已接入 Cloud Code `FpsPvp` + Cloud Save Private Custom 邮箱 + 雨云出站 worker；SelfHosted 模式保留。不得把 UGS JWT 当旧后端令牌或通过游戏 UDP 发送。
- 配置入口 `Tools > FPS > UGS Backend > Configure`，逐步操作见 [UgsBackendSetup.md](UgsBackendSetup.md)。本机测试可通过 `FPS_ACCOUNT_BACKEND` 强制选择后端；生产资源不填管理密钥。
- 验证：实际 Unity 编译通过；真实 Login → GameStart 场景配合可控 fake UGS 边界的 54 项检查通过，0 运行错误；Cloud Code 实际 JS 脚本替换云端 SDK 网络边界的 84 项检查通过；SelfHosted 真实本机 HTTP 的 44 项旧登录流程回归通过。UGS 控制台开通/发布、国内真实网络与真实账号联调尚未完成，不能称已云端上线。报告位于 `tmp/UgsIntegration/runtime-validation.json` 和 `tmp/LoginFlow/runtime-validation.json`。
- 后续注册提示修正：已确认 `abc` 本地合法且手工输入框绑定正确。旧 InvalidParameters 10002 拼接两段规则，在用户的 800×60 提示框中需 129 像素高度而被截断；改为简短通用拒绝提示，已知提供方未启用则提示开通，不凭通用错误断言用户名不合法；用户名/密码本地提示分开。61 项场景回归通过，新增真实旧版输入框和注册按钮提交 `abc`、提交后清密码不改请求副本、通用参数拒绝及提供方未启用的合成错误分流。新服务错误提示高度 43 像素，未调整用户场景布局。
- 实际注册 10002 的后续诊断：从既有在线失败响应安全提取到 HTTP 400、`INVALID_PARAMETERS`，详情为 `Invalid environment name provided`。这次拒绝指向环境名称，不是已证实的用户名、密码格式或提供方错误。客户端配置仍是 `development`；需由用户核对同一云项目的环境列表，缺少则创建 `development`，或主动选择现有环境并将其准确 **Name**（不是 Environment ID）填入本地设置，同时在同一环境发布 `FpsProfile`、检查 Cloud Save。没有自动改用 `production`，没有据此宣称云端已修复或真实注册通过；诊断不记录密码、令牌和完整请求体。
- 环境错误现单独提示“UGS 环境名无效，请检查环境配置。”，通用 10002 提示“UGS 请求参数或服务配置无效（10002）。”；Unity 编译 0 错误，输入审计的 7 项错误映射检查通过。环境提示高度 43 像素，适配用户现有提示区域，未改场景布局；这些验证不代表真实云端注册成功。

2026-10-20 后续联调：用户创建 `development` 后注册成功；登录后的云存档请求实际返回 Cloud Code HTTP 404、`Script could not be found`（9002），当前配置为 `development` / `FpsProfile`。已增加按 CloudCodeException 类型、NotFound 原因和 9002 错误码识别的明确提示；Unity 重新编译成功，9 项错误映射检查通过（包括脚本缺失不判为会话失效），新提示高度 43 像素且适配现有提示区域。云端需在同一环境创建并发布该 JS 脚本，不能把脚本缺失当成账号失败，不能自动切换存档环境。实际云端发布仍需用户完成。

## 2026-10-20 UGS PvP 与雨云桥接

- 客户端的既有 PartyLobbyClient/队伍 UI 接口保留；AccountClient 把队伍、普通票据、匹配票据和开局前配装同步路由到 `FpsPvp`。脚本用 context.playerId 认证请求，在 Private Custom `fps_pvp_bridge_v1` 的本人邮箱提交；客户端只向 UGS HTTPS 发请求。
- 雨云 .NET `UgsPvpWorker` 用受限外部配置中的服务账号主动访问 UGS，复用 PartyStore/PartyMatchmaker。权威角色来自 Protected fps_profile_v1；UGS 玩家不写入旧 accounts.json。真实 writeLock、本人 requestId、30秒TTL、15秒lease和进程epoch防止旧回包混入新请求；每环境只运行一个worker，首次同玩家邮箱创建并非原子，详见协议。
- NGO 专服只用本机 /api/ugs/game-ticket/redeem、/api/ugs/matches/server；64位随机短票据60秒单次消费，兑换时重读角色并校验匹配名单、版本和端口。UGS JWT不经过游戏UDP。协议升级为5，需要同步更新客户端、专服和桥接程序。
- 客户端/专服构建入口新增 build-ugs-pvp-client/server，输出 Builds/UgsPvp/Client/FPS.exe 和 Server/FPS_PVP_Server.exe。配置窗口可复制FpsPvp脚本及5参数表。部署见 [UgsPvpRainyunSetup.md](UgsPvpRainyunSetup.md)，不依赖公网HTTP、备案域名或IP证书。
- 本地证据：真实Login/GameStart配合假UGS边界76项检查通过；实际CloudCode JS配合假CloudSave SDK的268项检查通过（PvP184、Profile84）。后端真实规则14场景116断言、REST与本机HTTP51断言、旧SelfHosted74断言通过；Windows客户端/专服构建均0错误（分别3/5条警告），独立bridge发布程序本机启动与health通过。已生成Rainyun-UgsPvp.zip和WindowsClient.zip。真实UGS发布、雨云服务账号配置和公网两端联调尚未执行，不能据本地验证声称已上线。
## 2026-10-23 建队面板提前打开与错误隐藏

- 用户报告 GameLobby 点击开始游戏后短暂显示组队面板，随后无提示回首页。确认旧 UI 在异步 create 成功前直接打开空面板；失败后 Party 仍为空，Refresh 自动关面板，而首页没有呈现 PartyLobbyClient.Status。现有日志没有该次建队失败分类，不能据此断言 FpsPvp 未发布、桥接离线或 UDP 不通。
- 修复为创建中保留首页、显示进度并阻止重复提交/离开，服务确认非空队伍后才打开组队面板。失败原因通过首页既有 Tmp Status 保留到下一次操作；200 但没有队伍也明确判为创建未成功。保留原 Create() Inspector 接口和用户布局，没有编辑场景。
- 用户 PvP 操作失败现在记录固定 operation/status/category；不记录账号、ID、密码、令牌或回包，current/rooms 后台轮询不刷警告。用于下一次真实请求区分脚本缺失、桥接状态、超时和业务拒绝，不能代替真实云端联调。
- 真实 Login → GameStart → GameLobby 配合假 UGS 传输的复现：修复前原 77 项通过，延迟建队断言失败（home=False、panel=True、busy=True）；修复后共 86 项通过，0 错误，场景及测试工厂恢复成功。另有 24 项登录错误映射、9 项真实 SDK PvP 诊断审计通过。证据 `tmp/PartyFlow/runtime-before.json`、`runtime-after.json`、`diagnostic-audit.json`。实际 UGS 注册账号与雨云匹配仍需用户复试，不把假传输验证称为公网双端成功。
- Windows 客户端重新构建成功（0 错误、6 警告），已更新 `Builds/UgsPvp/Client` 与 `WindowsClient.zip`（SHA256 `75C929D1ABB9512C55F066B2926ECCA0A9464F8924DFBE85A160F900AD170A0A`）；未重建或更改雨云服务端。Login/GameStart/GameLobby 场景文件哈希保持一致，编辑器恢复为停止状态的 Login 场景。
- 后续真实复试显示 BridgeUnavailable。用户确认雨云任务 Running、health 为 status=ok/service=fps-account/apiVersion=2、配置 Enabled=true、development 名称和 Environment ID 与控制台完全一致、邮箱 fps_pvp_bridge_v1。尚无云端租约样本，不能据此确定网络、时钟或具体 worker 故障。health 当前是固定 HTTP 存活响应，不验证运行中的租约新鲜度。
- 新增单文件 `Server/AccountService/Deployment/Test-UgsPvpBridge.ps1`，交付副本在 `Builds/UgsPvp`：从服务器现有配置向固定官方 HTTPS 换令牌，三次只读采样 bridge 心跳，对比 UGS Date 与本机 UTC；只输出状态、有效期、续期和时差，不输出凭据、令牌、租约身份或响应原文，不写云数据、不改任务或配置。Windows PowerShell 5.1 隔离 16 类场景、108 项断言通过；生产与测试脚本解析通过、配置字节保持、TLS 设置恢复。诊断脚本本机测试替换 HTTP 边界，真实雨云采样仍需用户运行后提供 JSON。

## 2026-10-29 诊断脚本 Token HTTP 201 误报

- 用户在雨云运行 v1 诊断得到 TokenExchangeFailed / TokenHttp=201 / Samples=[]。这是诊断脚本把成功状态限定为 200 后提前退出，尚未形成租约采样证据；不能据此认定服务账号无效或桥接故障已解决。Unity Token Exchange v2 官方契约成功返回 201 Created。
- 脚本改为接受 200–299，并继续校验 JSON、accessToken 非空字符串与无换行；报告升级 DiagnosticVersion=2。正式 UgsCloudStore 原先已使用 IsSuccessStatusCode，这次不修改或重建服务程序。
- Windows PowerShell 5.1 隔离回归 147/147 通过，新增 201 有效令牌后采样、201 坏 JSON/缺令牌、204 空正文和 302 拒绝等；脱敏、TLS 恢复及配置不变验证保留。源码与 Builds/UgsPvp/Test-UgsPvpBridge.ps1、明确命名的 Test-UgsPvpBridge-v2.ps1 交付副本及 SHA256 清单同步。仍需用户在雨云运行 v2 后提供实际心跳报告。

## 2026-10-29 桥接心跳过期后的实际恢复

- v2 实际采样：TokenHttp=201、Private GET=200、lease 结构正确但过期约 6.7 天且不续期，服务器与云 UTC 差约 0.2–0.6 秒。5080 进程确认为 C:\FpsUgsPvp\App\AccountService.exe，自 2026-10-20 运行。定向停止固定任务、确认旧进程退出、等待旧租约窗口并重启后，三次云端采样恢复 LeaseHealthy / LeaseAdvancing=true / EpochStable=true，用户确认游戏可正常建队并停留。历史进程具体阻塞位置未知。
- 修正已复现的 HTTP 正文缺少超时和 health 固定存活响应：整个 UGS 操作使用至多 10 秒 linked deadline，包括令牌锁、正文读取和 401 重试；只有 cloud 写入成功后发布此次真实租约 expiry，健康过期返回 503/degraded，但保留所有权门禁允许后续续租恢复。SelfHosted 健康响应仍为原三个字段，协议与 Unity 客户端/专服无需为本次变化重建。
- 验证：worker 17 场景/133 断言、HTTP/REST 与真实回环 HTTP 70 断言通过；实际原 10.0.8 自包含 App 隔离副本仅换 DLL/PDB 后启动通过（tmp/UgsBridgeRecoverySmoke/result.json）。新增定向小补丁更新器，Windows PowerShell 5.1 隔离 29/29 场景通过，覆盖精确进程/任务归属、残留进程拒绝、备份与 ACL、严格的新健康字段；不读取 State 密钥或操作 Unity 服。
- 交付 Builds/UgsPvp/UgsBridgeRecoveryPatch.zip，SHA256=12B0C3706976C50095594D5A8D37FDEB6C2CA20601D1C2FD96FABB0631F837CA，含 DLL/PDB、严格清单、更新器、v2 只读诊断与说明。原程序的定向重启已实际恢复建队；这份防护补丁尚需用户自行安装和复测，不能称已部署。公网双账号匹配及入场尚未据本轮新证据验收。

## 2026-10-29 UGS 桥接启动网络失败恢复

- 用户确认上个恢复 DLL 已安装，但任务 Ready、无监听进程，health HTTP=0。15:09:32 的当前 .NET Runtime 事件确认首次 Cloud Save GET 在 TLS 握手时被重置（HttpRequestException/Socket10054），异常穿过 InitializeAsync/StartAsync 导致退出；不要再把当前现场当成存活进程中轮询阻塞。
- 首次租约初始化移入 ExecuteAsync 独立循环，网络/超时/408/429/5xx 每 5 秒重试，HTTP 程序保持可观察的 503；成功后才正常轮询。不可恢复权限/租约冲突保持失败，后续丢失 ownership 不会重新初始化。Stop 先使健康失效并取消，迟到首次写响应不能复活健康。
- 本地旧码复现后，新码 worker 24 场景/176 断言、HTTP 74 断言、更新脚本 29 隔离场景通过；原自包含 10.0.8 副本验证启动。新交付目录/包 Builds/UgsPvp/UgsBridgeStartupRecoveryPatch，避免覆盖旧包造成哈希混淆。尚需用户实际安装新包与诊断 LeaseHealthy，不能称已修复实际雨云出站链路。
- 新包 ZIP SHA256=15B9325C844F3E776F3ACB5684E4BE295A70224DA629B169A5749D593FBB1A43（99724 字节）；新 DLL SHA256=495C9461ABE1D2D22899BA251D3A01958CC8116E33998B71391A1F0F3991B629。原 10.0.8 自包含 UGS 模式用关闭的本机代理模拟持续网络失败，确认进程存活、至少两次重试、health503；SelfHosted 原健康接口也通过。真实云请求与真实凭据未用于此隔离验证。
- 后续用户先提供 v2 CloudReadFailed（Token201、首个 Private GET Http0），这仅代表该次诊断未获得可用读取结果，不足以确定持续网络中断或权限错误。紧接着服务器本机 health 实测 status=ok、ugsBridgeEnabled=true、ugsBridgeReady=true、有效期11.977秒；固定 Cloud Save 根地址无凭据、TLS1.2测试 HTTP404 / curl退出0。当前已有活跃确认租约、该次 HTTPS 连接成功，但仍待三次 LeaseHealthy/LeaseAdvancing 及游戏建队复测。不要因前次单个 Http0要求重装或更换密钥。

## 2026-10-05 五人队伍匹配（原协议）

- 大厅开始游戏自动创建最多五人的队伍，只展示自己的五个成员位置。房主开始匹配，其他成员准备/取消准备；两个已准备队伍可开局，允许人数不足五人且双方人数不同。服务端随机给整队分配红蓝阵营。
- 现有 .NET AccountService 的 PartyStore / PartyMatchmaker 保存临时队伍和匹配预约，通过回环 UDP 查询空闲 NGO 专服。队伍不占用游戏进程，多场实际对局使用不同端口进程。临时状态在账号服务重启后清空。
- GameLobby 新增 PartyLobbyUI 并绑定现有按钮、LobbyPanel、RoomPanel 和 RoomEntry。用户仍需手工补齐五个 PartyMemberSlot、模式/地图/状态文字、列表 Content、RoomEntry 五头像和预设头像选择面板；接线说明见 [LobbyMatchmakingFlow.md](LobbyMatchmakingFlow.md)。没有生成新布局。
- 玩家 AvatarId 通过预设头像目录和账号后端持久保存。模式固定“团队对抗”，地图“测试地图1”。返回大厅退出队伍；房主离开或心跳超时解散队伍。
- GameStart 的 MainMenuUI 新增自定义 PVP 按钮字段，需用户绑定；旧 LobbyMenuUI 保留用于红蓝双方十个位置和每边至少一人的自定义开局，PVE 入口保留。
- 后端 62 项 HTTP/进程与队伍规则检查通过，包含并发匹配的两个独立服务器预约。实际 Unity 双客户端验证自动建队、匹配、相反阵营、GamePVP 玩家生成及主动退出回 GameLobby 已通过；手工显示对象未补齐，视觉验收待完成。当前构建位于 Builds/LobbyRedesign，历史交付包未更新。
- 修复 JsonUtility 将 JSON null 队伍实例化为空对象造成的跳过建队问题，并开启组队客户端后台运行。当前没有 Unity MCP，使用固定操作白名单的本地编辑器工作台进行编译、接线和构建验证。

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

- 2026-10-29：Login → GameStart 场景加载由 `LoginSceneController` 协程读取真实 `AsyncOperation.progress`，归一化为 `LoadingProgress`（0–1），`LoginUI` 通过独立进度事件更新已有 `LoadingPanel/ProgressBar/FillImage` 和 `Progress`。显示 100% 一帧后允许激活，不改变手动“进入游戏”设置；云档案读取仍在 GameStart。场景已接好引用，可用 `Tools > FPS > Login > Bind Loading Progress` 备份并重新绑定。Unity 编译通过，离线真实场景回归的 94 项功能检查（新增 8 项加载检查）完成；完整报告仍记录两条修改前已有的 Visual Scripting 异常，`success=false`，详见 `tmp/LoginLoading/runtime-validation.json` 和 [LoginSceneFlow.md](LoginSceneFlow.md)。

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

## 2026-11-12 旧版命名输入框与加权名称长度

- InitialNamePanel 用户已换成旧版 InputField/Text；重新绑定现有 NameInput 与 Confirm，移除 InitialNamePanelView.status，保留作者 Status 的固定规则提示及原布局。输入框失去焦点不提交，回车和按钮确认仍走 LoginSceneController。
- 首次命名与 ProfilePanel 改名共用 DisplayNameRules：汉字计2，ASCII 字母/数字计1，加权总长1–12。输入原始字符上限12，提交时另做加权校验；空名、超限及其他字符不保存。AccountClient 与 FpsProfile 云端均检查新名称。
- 保留旧20字符云存档的读取契约。旧名称不自动截断，保持旧名称时仍可修改性别和签名；重新命名须遵守新规则。
- Unity 实际 Login/GameLobby 场景验证125项、FpsProfile 离线云脚本115项通过。编辑器恢复原 GameLobby，未新增运行错误；既有 Visual Scripting 初始化异常仍在基线中。证据与修改前资产副本：tmp/NameRules20261112。
- 本轮未更新真实 UGS 或雨云，也未重建上一轮压缩包。部署本轮改动需重建客户端并重新发布 FpsProfile.js，云端参数表不变；bridge/专服继续兼容这些更短的新名称。
