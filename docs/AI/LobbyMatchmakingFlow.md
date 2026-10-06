# 五人队伍匹配与自定义房间

日期：2026-10-05。本文件替代旧 MainMenuGameLobbyFlow 中“大厅开始游戏打开房间列表”的行为说明。

## 已确定的行为

- GameLobby 开始游戏自动创建五人队伍；已有队伍时恢复组队界面。
- 只显示自己队伍的五个位置，不在组队阶段选择红蓝方。
- 房主：开始匹配 / 解散队伍；成员：准备（再次点击取消准备）/ 离开队伍。所有非房主成员必须准备，房主点击开始匹配确认自己的准备。
- 两个已准备队伍即可匹配，不要求凑满五人，允许双方人数不同。整队保持一起，服务端随机决定双方红蓝阵营。
- 返回大厅即离开队伍；房主离开或掉线会解散自己的队伍。
- 模式名固定为“团队对抗”，地图名为“测试地图1”。房间列表实际列出已创建的五人队伍；每一行实例化现有 RoomEntry，不显示段位。
- 匹配成功后向现有 NGO 专服交接；专服验证账号、匹配名单和阵营，等待所有成员连接并完成既有配装核查后自动开局。
- GameStart 的自定义 PVP 使用现有 LobbyMenuUI，红蓝各五个可点击位置，每边至少一人，房主手动开始；保留原 PVE 入口。
- 正常赛后回 GameLobby，恢复各自原来的匹配队伍，清除准备状态；自定义房间继续保留成员。主动退出和房主解散按离开规则处理。

## 已接好的对象

GameLobby 的 LobbyController 增加 PartyLobbyUI；MultiplayerLobbyUI 的 Party UI 指向它。

| 现有对象 | 已绑定的用途 |
| --- | --- |
| HomePanel/GameMenu/Start | 自动建队 |
| HomePanel/GameMenu/Role（文字“房间列表”） | 浏览队伍 |
| LobbyPanel | 五人队伍面板 |
| LobbyPanel/ButtomMenu/Start | 房主开始/取消匹配，成员准备/取消准备 |
| LobbyPanel/ButtomMenu/Leave | 房主解散队伍，成员离开队伍 |
| LobbyPanel/TopMenu/Left/Return | 离开队伍回大厅 |
| RoomPanel/Hide | 关闭房间列表 |
| HomePanel/TopMenu/Self/ProfilePicture | 显示账号保存的预设头像 |

未生成任何布局、按钮、文字、相机或展示模型。原 LobbyMenu 保留，用于自定义 PVP 与 PVE。

## 你需要手工补齐的界面

### 组队面板 LobbyPanel

1. 添加模式标题、地图名和状态提示，均用 TMP_Text，拖到 PartyLobbyUI 的 Title / Map Name / Status。
   大厅首页也需要状态 TMP_Text，拖到 MultiplayerLobbyUI 的 Tmp Status，显示未登录、接线缺失或账号请求失败原因。
2. 建立五个成员位置，分别挂 PartyMemberSlot，拖入 PartyLobbyUI 的 Members 数组（长度 5）。成员顺序为服务端入队顺序。
3. 每个位置准备姓名 TMP_Text、准备状态 TMP_Text、房主标识物体；分别绑定 Player Name / Ready State / Owner Badge。
4. 每个位置添加 Image 或 RawImage，挂 PlayerAvatarView，绑定相应图像组件；再拖到该位置的 Avatar。
5. 若需要图二的 3D 展示，在每个位置摆放 RifleGirlBody 展示实例，Animator 使用 RifleGirlLobby.controller，关闭 Root Motion；将 RifleGirlAppearance 拖到该位置的 Appearance。展示用实例不要挂本地账号 AccountCharacterPreview，否则会把其他成员的外观覆盖成自己的。
6. Occupied Root 对应有人时显示的对象，Empty Root 对应空位图标。相机、站位和名字投影由你手工制作；脚本只设置显示状态和外观。3D 展示对象可在 Canvas 外，但必须绑定到对应位置。

### 房间列表 RoomPanel

1. 添加 Scroll View，将 Content 配置 VerticalLayoutGroup 和需要的 ContentSizeFitter，拖入 PartyLobbyUI 的 Room List Container。
2. 添加刷新按钮，拖入 Refresh Rooms Button；不要再加重复的脚本 OnClick。
3. 添加“暂无队伍”的 TMP_Text，放在 Content 外面，拖入 Empty Rooms。
4. Room Entry Prefab 已绑定 Assets/Prefabs/RoomEntry.prefab 上的 RoomEntryUI。

### RoomEntry 预制体

已添加 RoomEntryUI 并绑定现有文字和根按钮。现在整行点击可进队伍；为了得到参考图一的样式，还需手工补：

- 地图名 TMP_Text → Map Name。
- 人数/匹配状态 TMP_Text → Room State。
- 五个头像位置，每个挂 PlayerAvatarView → Avatars 数组。空位图标由预制体布局提供，图像组件缺少成员时会关闭。
- 独立“进房间”按钮 → Join Button；绑定后根按钮不要再承担加入功能。
- 模式图标、背景、边框等由你设置。

### 预设头像

- 已创建 Assets/Resources/Customization/PlayerAvatars.asset。将预设头像 Sprite 拖入 Avatars 数组（1-32 个）。数组索引就是账号保存的 AvatarId，各客户端必须使用相同顺序。
- 手工创建头像选择面板，挂 PresetAvatarUI。Catalog 指向 PlayerAvatars，Preset Buttons 数组顺序与头像目录一致，Status 可绑定提示文字。
- 用你已有头像按钮打开这个面板，另加关闭按钮。选择头像后通过账号服务保存，再同步到首页、组队和列表。
- 首次账号 AvatarId 默认为 0；目前没有用户提供的预设图片，目录暂为空。

### GameStart 自定义房间入口

MainMenuUI 新增 Btn Custom Room。把你为自定义房间制作的按钮拖入此字段，不再给它绑定提示界面。它打开现有 LobbyMenuUI 的 PVP 模式。

原 Btn Continue PVE 仍打开局域网 PVE；原 Btn Online PVP 仍进入 GameLobby。专服初始大厅同步场景为 GameLobby，所以加入自定义房间后会在那里自动恢复旧十人选队面板。

## 服务端与多房间

五人队伍和匹配队列由现有 .NET AccountService 中的 PartyStore / PartyMatchmaker 管理，实际游戏继续使用一对局一进程 NGO Dedicated Server。

- 队伍不占用游戏进程，可以同时创建多个队伍。
- 默认对局端口池：UDP 7777-7786；专服查询端口：UDP 48777-48786。
- GameStart 的 LobbyManager 查询数量和 PvpDedicatedServerBuilder 启动脚本改为 10。
- 账号服务和游戏进程部署在同一台机器；协调服务通过回环查询端口检测空闲游戏进程。游戏进程使用 FPS_ACCOUNT_SERVER_URL 访问该账号服务的回环地址。
- 后端可用环境变量 PvpMatchmaking__StartPort、PvpMatchmaking__ServerCount、PvpMatchmaking__ServerAddress、PvpMatchmaking__MapName 配置。ServerAddress 为空时客户端沿用 LobbyManager 的专服地址。
- 客户端和专服协议升级到 4，必须同时重建，并更新账号服务；旧二进制不能用于验收。
- 稳定的账号和头像继续持久存储；临时队伍、匹配队列和预约在账号服务重启后清空。
- 大厅约每 10 秒同步远端队伍和列表，本机按钮操作立即提交。掉线约 45 秒后清理，匹配交接限时 60 秒。保留原账号接口的请求限流。
- 没有空闲游戏进程时继续排队；取消匹配、成员取消准备或离开可撤销等待。

部署时在更新后的账号服务旁启动专服池，例如：

```powershell
.\Start-PvpServerPool.ps1 -ServerExe 'D:\FPS\FPS_PVP_Server.exe' -ServerCount 10 -AccountServerUrl 'http://127.0.0.1:5080'
```

账号服务启动脚本的 MatchStartPort / MatchServerCount 必须对应专服池。这个启动器只管理它写入 PID 文件的进程；添加 -Stop 停止相同路径和端口池中的记录进程。部署前须使用正式构建入口生成两端；Builds/LobbyRedesign 中的 Development 构建用于本地回环验收，包含显式参数触发的测试探针。

可重复执行 Tools → FPS → Lobby → Audit Matchmaking UI Bindings，结果写入 tmp/LobbyRedesign/missing-ui.json。Bind Existing Matchmaking UI 只连接现有对象，执行前保存完整场景副本；补齐字段后优先使用 Audit，避免自动接线覆盖你新绑定的字段。

## 验证

后端编译 0 警告、0 错误；账号、队伍和匹配规则的实际 HTTP/进程及规则检查共 62 项通过，结果在 Server/AccountService/test-results/20261005-143258-a5a4cf。包含五人队伍对一人队伍、全员准备门槛、取消准备、房主解散、掉线清理和两个并发对局的独立预约。

Unity 实际 Windows 客户端和 Dedicated Server 构建均成功，各 0 错误、4 警告。最终两端构建的双客户端联机验收 36db4d4c 两端 PASS：使用已搭建按钮自动创建两个单人队伍，匹配到同一对局，分别分配蓝/红阵营，确认 GamePVP 已生成网络玩家、头像 ID 与原队伍保留，然后主动退出返回 GameLobby。结果在 tmp/LobbyRedesign/smoke/36db4d4c；构建摘要在 tmp/LobbyRedesign/build-client.txt 和 build-server.txt。

空队伍、等待队伍、已匹配队伍的实际 JsonUtility 解析检查通过，证据在 tmp/LobbyRedesign/json-validation.txt。修复了 Unity 把 JSON null 解析成空对象造成的跳过建队问题。测试脚本为 Server/AccountService/Tests/Start-LobbyMatchmakingSmoke.ps1；独立账号服务、测试账号和子进程在测试结束后停止，不修改正式账号库。

本轮未对公网部署、完整十人画面和正常结算后再次匹配作运行验收。无图形专服日志包含 Shader 不支持的输出，场景中另有 Missing Script 警告；没有把这些日志称为“零运行错误”。缺少的显示对象如上，界面视觉效果仍需补齐手工对象后验收。
