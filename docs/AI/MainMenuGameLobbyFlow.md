# 主菜单与手工联机大厅接入

日期：2026-10-03。用户确认：`GameStart → GameLobby → GamePVP → GameLobby`；大厅主页由用户手工制作，开始游戏打开现有房间列表，角色、服装和枪械配装暂时沿用原界面。比赛结束显示结算后全员自动返回并保留原房间。

## GameLobby 的手工搭建步骤

1. 在 Unity 中创建并保存场景，场景名必须为 **GameLobby**，建议路径 `Assets/Scenes/GameLobby.unity`。脚本不会创建此场景或其大厅布局。
2. 自己摆放大厅背景、光照、相机、Canvas、EventSystem 和按钮。把大厅首页的 UI 放在一个 `HomePanel` 子物体下。
3. 新建一个始终激活的普通物体 `LobbyController`，挂 `MultiplayerLobbyUI`。该物体不能放在 `HomePanel` 里面。
4. 将主菜单场景中带 `LobbyMenuUI` 的 **LobbyMenu** 完整面板复制到新大厅的 Canvas 下。保留它的按钮、列表和动画引用，把初始 `show` 设为 false，物体初始关闭。它必须放在 `HomePanel` 外。将该组件拖入控制器的 `Room Menu`。
5. 把 `HomePanel` 和对应按钮拖入下表字段。脚本会注册按钮监听，不需要再给同一按钮添加重复 OnClick。
6. 将 `Assets/Resources/Customization/RifleGirlBody.prefab` 拖入场景，自行调整展示位置、相机及大小。仅给这个场景实例的 Animator 设置 `Assets/Resources/Customization/RifleGirlLobby.controller`，取消 Apply Root Motion。在角色根节点挂 `AccountCharacterPreview`，将角色的 `RifleGirlAppearance` 拖入 `Appearance`。
7. File → Build Settings 加入并勾选 `GameStart`、`GameLobby`、`GamePVP`、`GamePVE`。启动入口为 `GameStart`。不要把旧的 `Lobby.unity` 测试场景当正式大厅。
8. 测试时从 `GameStart` 启动。网络根对象由现有主菜单保留进入新大厅；**不要在 GameLobby 再放第二个 NetworkManager / LobbyManager**。仅打开新大厅直接 Play 不会自动创建这些网络组件。

| MultiplayerLobbyUI 字段 | 拖入的手工对象 / 功能 |
| --- | --- |
| Home Panel | 大厅首页 UI 根物体，房间面板和控制器在其外面 |
| Room Menu | 复制来的完整 LobbyMenuUI 面板 |
| Btn Start Game | 开始游戏：打开房间列表；已在房间时恢复选队界面 |
| Btn Character / Btn Clothes / Btn Loadout | 角色、服装、配装按钮：沿用现有编辑界面 |
| Btn Account | 可选：打开现有登录、注册界面 |
| Btn Return Main Menu | 返回主菜单，同时离开当前房间 |
| Txt Player Name / Tmp Player Name | 玩家名，普通 Text 或 TMP 任择其一 |
| Txt Status / Tmp Status | 账号与联机状态提示，建议至少绑定一项 |

主界面的信箱、好友、聊天、排行榜等截图装饰尚未作为功能需求实现，用户可在手工布局中自行摆放。

## 已接入的行为

- 主菜单的进入大厅按钮加载 GameLobby。若场景尚未制作或未加入构建列表，使用已有提示面板显示缺失原因，保持可返回的主菜单。
- 主菜单和大厅的 `AccountCharacterPreview` 都读取持久的 AccountClient；云端保存成功后显示同一份外观。未登录显示默认 RifleGirl；未保存的试穿不会改变正式展示角色。
- 大厅开始游戏打开原房间列表；未登录先打开原账号界面，登录关闭后再次点开始游戏。
- 房间面板的返回按钮只关闭面板、回大厅首页，保留当前房间；离开房间按钮实际离开。再次打开房间面板会恢复成员、队伍和房主状态。
- 正常比赛结束默认显示 8 秒结算。`GameStart` 中持久 `LobbyManager` 的 `Pvp Settlement Duration` 可调整。专服使用同步服务器时间倒计时，然后统一加载 GameLobby。
- 正常返回保留房间名、房主、玩家、队伍、批准的外观和配装。旧对局玩家对象销毁，回大厅后清比分和胜者，房主可开始下一局；下一局重新生成角色并初始化血量、弹药和对局记录。
- 主动退出或掉线回 GameLobby；退出者不会留在原房间。所有玩家离开后，专服回 GameLobby 等待新房间。
- 保留房间期间保存服装或配装，会通过新的账号入场票据重新核实当前玩家档案。服务器核对原账号身份后更新快照；验证期间不能开局。换账号需先离开房间再加入。
- 房主请求开局后，专服还会让所有成员重新提供最新档案票据，等待正在进行的云端保存完成。全员核实成功才进入对局，避免另一位玩家保存时房主抢先开局使用旧配装。预检期间禁止新增云端保存或换队；失败、成员退出或 90 秒超时会取消本次开局并提示房主，房间保留。
- 账号服务失败时不会直接使用客户端自报外观作为服务器档案。修改后的本地云档案与房间档案可能暂时不同，需按提示重新同步。
- 专服也先进入 GameLobby 再监听，避免 NGO 首次场景同步把客户端带回 GameStart。
- 联机协议由 2 升为 3；大厅完成后要一起重新构建客户端和专服。旧交付包保留，不能验证此新流程。
- PVE 的原 GameStart 房间入口继续沿用。

## 主菜单展示角色

`Tools → FPS → Main Menu → Bind Account Character` 仅操作已打开的 GameStart，在 `tmp/MainMenuFlow/main-menu-before.unity` 保存修改前副本。新 RifleGirl 在编辑器中摆入并绑定账号外观，保留已有 UI、相机及独立 HeadAimTarget。旧 Lumine 为保护原组件与用户引用禁用保留。

鼠标转头的旧约束引用 Lumine 骨骼，不能直接驱动 RifleGirl；新展示角色需要独立配置头部约束。共享战斗 prefab 和控制器未为此改写。

## 验证边界

本轮已在实际 Unity Editor 编译，最终无脚本编译错误。真实 Play Mode 使用独立 localhost 账号服务完成 52 项主菜单检查：缺失大厅提示及返回、注册/登录、保存并重新读取外观、实际衣装与脸型网格及材质、从角色界面返回主菜单、登出恢复默认外观。结果在 `tmp/MainMenuFlow/runtime-validation.json`。验证后退出 Play、恢复账号环境变量和 HTTP 设置并停止本地测试服务。

初次改造验收时 GameLobby 尚未制作。2026-10-03 分支冲突修复已保留用户手工制作的 GameLobby 及其 Build Settings 配置，关键脚本、角色和按钮引用静态检查通过。完整新流程的双客户端联机、结算自动返回、保留房间后再次开局和配装刷新仍需运行验证；此前双客户端旧流程不能作为此次新流程的通过证据。构建入口仍会明确拒绝缺少 GameLobby 的构建。
