# GameLobby UI：MVC 与面板 Prefab

2026-10-30。正式客户端流程为 `Login → GameLobby/MainMenu → GameLobby/HomePanel → 组队/匹配 → GamePVP → GameLobby`。GameStart 保留为旧场景，不参与正式客户端或专服构建。

## 所有权与分工

- `Assets/Scripts/UI/Mvc/LobbyUIRoot.cs` 是场景组合入口，绑定页面实例、弹窗、展示角色和头像目录。云档案就绪后才初始化 Controller。
- `LobbyModel` 保存当前页面、返回历史、个人主页页签和自定义房间选择。`CustomizationModel` 单独保存未提交的外观、配装草稿。
- `LobbyController` 接收按钮意图，执行导航、组队、准备、匹配、档案保存和弹窗操作；`CustomRoomController` 负责原有自定义 PvP/PvE 房间操作。
- `LobbyPageView` 及其子类只持有 Prefab 内部控件引用，呈现数据、发送用户意图。View 不调用 AccountClient、PartyLobbyClient 或其他面板单例。
- 账号档案、队伍成员和匹配结果分别由现有 `AccountClient`、`PartyLobbyClient`、`LobbyManager/LobbyState` 持有。UI Model 不创建第二份权威网络状态。

## Prefab 与场景

所有新面板位于 `Assets/UI/Prefab/Mvc/`：

| Prefab | 用途 |
| --- | --- |
| MainMenuPanel | 登录后的第一屏，保留复制来的菜单 |
| HomePanel | 联机大厅首页 |
| PartyPanel | 五人队伍、准备、开始/取消匹配、离开 |
| RoomsPanel | 已有队伍列表与加入 |
| CustomRoomPanel | 原有自定义 PvP/PvE 房间；十个分队位置已在编辑器内保存，不再运行时生成 |
| CustomizationPanel | 原有角色/服装/武器配装界面，1600×900 布局保存为 Prefab |
| ProfilePanel | 原有个人主页、信息页签和头像选择 |
| MessageDialog / ConfirmDialog | 消息及确认弹窗，使用原提示/退出面板素材 |
| LoadingOverlay | 到达 GameLobby 后等待读取用户档案 |
| LobbyIdentity | 原有 UID 位置显示实际账号标识 |
| PartyRoomEntry | 沿用原房间卡片素材，绑定地图、状态、五头像和独立加入按钮，按容器宽度缩放 |

场景中的 `UIRoot` 持有这些 Prefab 实例。Prefab 不引用外部场景对象。五个成员的 3D 展示角色仍留在场景，显式由组合入口绑定；面板里的成员槽只负责名字、头像、准备状态。

`MainMenuPresentation`、`LobbyPresentation` 分别保留两套原有相机与背景布局，同一时间启用当前页面需要的一组。`UI` 下保留原菜单 Canvas、大厅 Canvas 和独立弹窗 Canvas，维持原分辨率、相机渲染和素材配置。

个人主页首页显示已有账号的名字、UID、头像，信息页沿用原排版。头像选项按目录数量生成，目前 30 项，上限 32。没有向后端添加性别等原本不存在的数据字段。

`Assets/UI/Fonts/Mvc` 保存大厅专用的字体回退配置；原字体、图集和材质保持引用，仅为缺字补充 1024 图集的动态中文回退。原共享字体资产不为此改写，回退字体已启用构建时清理动态数据。名字在原文字区域内自适应，房间卡片通过 RoomRowLayoutView 保持内部比例。

## 交互规则

- 点击主菜单联机按钮只切换到 HomePanel；HomePanel 返回只回 MainMenu，不重载场景。
- 建队期间保留 HomePanel 并阻止重复提交，服务确认队伍后才进入 PartyPanel。失败提示保留在当前页。
- 返回组队面板会执行离队；失败时保留当前面板和错误提示。房主/成员分别显示匹配与准备操作。
- 房间列表页面打开时开启队伍列表查询，关闭后停止；列表行继续使用已有 RoomEntry Prefab。
- 角色/服装共用原有配装页面。编辑只修改草稿，保存成功才接受云端确认的档案。返回或重新读取时，如有未保存修改，使用确认弹窗处理。
- 匹配和开局配装核查期间禁止保存外观、配装或头像。关闭弹窗返回原页面，不重新创建页面。
- Escape 优先关闭弹窗或头像选择，再处理页面返回；MainMenu 中打开退出确认。
- 原本未实现的设置、成就、语言等入口继续显示未开放提示，不伪造功能。
- 主菜单“自定义房间”打开已有自定义房间页面；训练靶场暂未实现，显示未开放提示。
- 当前 Build Settings 未启用 GamePVE，因此其入口显示明确提示；启用正式 GamePVE 后构建工具会自动包含它。
- 自定义服务器查询遇到 Windows UDP 的端口未开放响应时，按未收到服务器回包处理，继续查询其他槽位，不再向每帧更新抛出 ConnectionReset/ConnectionRefused。

## 维护与验证

修改布局时打开相应 Prefab。按钮在 View 的 `Buttons` 数组中绑定到 `LobbyAction`；行为在 Controller 内实现，不在 Inspector 中继续绑定旧单例脚本。

新页面需要在 `LobbyPage` 中定义、制作 View/Prefab，并在场景 UIRoot 的 Pages 数组中绑定。新面板只能引用自身子对象和项目资产；场景展示物由 UIRoot 显式传入。

`Tools > FPS > Lobby > Validate MVC Runtime (Offline)` 使用真实 Login/GameLobby 场景和按钮，替换 UGS 网络边界，验证账号加载、导航、草稿、保存、头像、建队失败、匹配、列表和场景重入。不会请求线上账号服务或修改生产云档案。

改造前含未保存改动的完整场景副本和验证报告位于 `tmp/LobbyMvc/`。`asset-validation.json` 包含 Prefab 引用和原有布局检查；`runtime-validation.json`、同目录 PNG 记录运行验证。GameStartProfileLoader 的类名为兼容旧序列化引用保留，实际由 LoginSceneBootstrap 在 GameLobby 创建。

2026-10-30 验证结果：Unity Editor 实际编译通过；24 项资产检查通过，162 个原有 UI 子对象的 RectTransform 布局保留（排除 TMP 自动生成的渲染子对象）；65 项真实场景离线检查通过，无新增运行错误。包括卡片加入按钮在屏幕内、账号 UID、草稿/保存/放弃、头像、建队失败与重试、匹配/取消、准备、加入/离队、重复开关和场景重入。验证前后场景及面板 Prefab 的 SHA256 保持一致，编辑器恢复为已保存、停止状态的 GameLobby。

原有 UGS 回归的 94 项功能断言完成，证据副本为 `tmp/LobbyMvc/ugs-regression.json`；该旧报告整体 `success=false`，因为仍记录两条改造前已有的 Visual Scripting 初始化异常。新 UI 验证报告将它们单列为 baselineErrors，并未修复或隐瞒此包问题。原登录布局删除了可选百分比文字，旧验证已改为检查实际绑定的进度 Image，并在百分比文字存在时验证它；没有为测试重新创建用户删除的控件。

本轮未请求线上 UGS、未做公网双客户端比赛联调，未重新输出客户端或专服安装包。对局完整网络流程仍需真实两端验收。
