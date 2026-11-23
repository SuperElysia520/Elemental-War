# 主菜单与大厅合并首页（A 方案）

2026-11-18。用户选择 `output/ui-references/merged-home-20261118/a-blue-white-v1.png` 的蓝白黄方向，要求把最初主菜单与大厅合并，并取消单机模式。

## 当前流程

`Login / LoadingPanel → GameLobby / UnifiedHome → 匹配或自定义 PvP → GamePVP → GameLobby / UnifiedHome`。

- 登录读取用户档案后直接进入 Home，没有“进入大厅”中转页；从对局返回也回到同一个首页，复用已有认证和档案。
- Home 展示账号头像、昵称、UID，中央为真实 RifleGirl 模型。右侧拆为独立的 SquadCard（队伍）与 MatchCard（模式/匹配），通过 Operations 上的原生 VerticalLayoutGroup 自动排列：无队伍时 MatchCard 占据右侧顶部；创建或加入成功后，上方显示 SquadCard，MatchCard 自动下移，卡片间距24；离队/解散后 MatchCard 再次上移，创建等待或失败不会留下空白队伍区域。外观、主武器和瞄具沿用已确认的账号配装。
- “开始匹配”在无队伍时先请求创建，确认成功且满足准备条件才排队；重复点击受原有服务忙状态保护，失败保留首页和原因。已有队伍按队长/成员、等待/匹配状态显示开始匹配、取消匹配、准备或取消准备。
- 下方卡片始终显示，新增“创建队伍”按钮，可先建队而不立即匹配；已有队伍时该按钮禁用。已有队伍时点击上方空槽打开好友面板，可从在线好友中发出组队邀请。“房间列表”沿用队伍查询与加入。离队/解散按钮归属上方队伍卡。
- 打开设置、个人资料、服装衣橱或武器配装后直接返回首页。已有队伍不因进入这些页面而离开。首页 Escape 请求退出确认；离队/解散使用队伍区的独立按钮。
- 自定义 PvP 仍使用已有房间与十个红蓝队伍位置。训练靶场和邮件保留未开放提示。右上好友按钮与左下聊天按钮打开社交面板。
- 正式页面不再绑定 Pve，也不显示“单机模式”；旧 MainMenuUI 同时取消单机入口监听并隐藏原按钮。枚举旧序号保留，防止已有 Prefab 的后续 action 值整体偏移；旧 Pve 意图不再开启对局。

## 资源与结构

- `Assets/Resource/UI/UnifiedLobby/UnifiedHome.prefab`：真实 uGUI 控件、透明白面板、黄色主操作、可缩放图标与私有中文字体。
- `Assets/Resource/UI/UnifiedLobby/TrainingStation.prefab`：可编辑的蓝白训练基地几何体、独立相机、灯光及原有账号展示角色；没有将概念图当成整张不可点击的界面。
- `Assets/Resource/UI/UnifiedLobby/MapPreview.png`：从展示基地拍摄的示意预览，不是新增的对战地图。
- `Assets/Resource/UI/UnifiedLobby/HomepageIdle.controller`：独立的大厅待机控制器，开启手部 IK，不修改共用战斗控制器。展示枪械没有输入、开火或网络战斗逻辑。
- `LobbyUIRoot` 绑定七个页面：Home、Rooms、CustomRoom、Customization（武器）、Profile、Settings、Wardrobe（服装）。原有 MainMenu/Home/Party 三个场景页面由 UnifiedHome 替代；原 Prefab 资产保留。
- `LobbyModel` 将旧 MainMenu、Party 导航请求归一到 Home，保留原序列化枚举序号。`UnifiedLobbyView` 仅展示数据、发出意图；账号和队伍权威状态仍由现有服务管理。
- 好友与聊天是大厅上方的 uGUI 面板。返回或按 Esc 会回到当前大厅。好友可按昵称或 UID 查找，收到的申请可接受或拒绝。好友私聊的邀请卡会加入现有匹配队伍，队伍聊天仅供当前成员使用。
- `UnifiedLobbyLayout` 同步适配 UI 和展示相机视野，在 4:3 窄屏中也保持角色头脚与上下控件分离。页内没有外部场景对象引用；场景相机由组合入口运行时传入。

## 服装衣橱

大厅的「服装」打开场景中绑定的 `Assets/Resources/Customization/Wardrobe.prefab`。发型、帽子、上装、外套、下装、鞋靴、手套和配饰独立试穿。切换全身或第一人称使用同一份草稿，单品详情显示实际模型的配色缩略图。保存成功后大厅展示和对局读取账号档案。取消或撤销试穿恢复已保存外观。

旧档案继续按原整装读取。首次修改单品后，`wardrobeItems` 的第 16 位标志分部位数据，低 16 位按上述部位各用 2 位记录款式，`wardrobeColors` 按相同顺序各用 3 位记录配色。可选部位的 0 表示取下。账号服务与 FpsProfile Cloud Code 均接受旧格式和扩展格式。上线需配套更新 Cloud Code、账号服务、客户端和使用账号协议 6 的 PvP 专服。

在打开的 GameLobby 中运行 `Tools/FPS/Lobby/Build Wardrobe` 可用现有角色资源重建服装网格、缩略图和衣橱 Prefab。现有大厅离线运行验证覆盖换装、染色、双视角预览、保存、撤销与网络序列化。

## 编辑与验证

- 在 Unity 打开 `Assets/Scenes/GameLobby.unity` 即可编辑首页及训练基地；正式运行从 Login 开始。
- `Tools/FPS/Lobby/Validate Unified Homepage` 检查绑定、页面、单机入口移除与导航。
- `Tools/FPS/Lobby/Validate Unified Homepage Runtime (Offline)` 在真实 Login/GameLobby 场景运行，只有 UGS 传输边界是假实现，不会访问线上账号或匹配服务。验证后恢复停止状态的原场景、启动场景及环境变量。
- 原 MVC 验证入口识别合并首页后转到上述验证；Login、设置和 UGS 旧回归检查已适配同屏组队状态。
- `tmp/UnifiedLobby20261118/asset-validation.json` 为资源检查，`runtime-validation.json` 为实际运行检查；包含鼠标射线、重复提交、失败重试、直接匹配、取消、加入、准备、离队、子页面返回和场景重入。
- `homepage-1920x1080.png`、`homepage-1280x1024.png`、`homepage-2560x1080.png` 为布局检查图；`runtime-homepage-final.png` 为实际运行截图。已有 Visual Scripting 初始化异常单列 baseline，不吞掉新错误。
- 最终验证：15 项资源/导航检查、55 项真实场景运行交互、103 项原 UGS 账号与组队回归均通过，新运行错误为 0。原设置 Prefab 验证也通过，34 个设置项保留。测试均为离线传输替身，没有访问线上账号或发起生产匹配。
- 实际运行效果图已交付到 `output/ui-references/merged-home-20261118/unity-homepage-implemented-v1.png`。Unity 已恢复打开、停止运行的 GameLobby。

## 备份

右侧拆卡的迁移备份位于 `tmp/UnifiedLobby20261118/before/split-cards-*`，包含修改前的完整 GameLobby 与 UnifiedHome.prefab。此次资源检查 18 项、运行检查 61 项通过；实际图见 `runtime-homepage.png`（未建队）与 `runtime-party-created.png`（已建队）。迁移保留现有好友/聊天接线及其他场景内容。

原打开且未保存的场景已先保存完整副本到 `tmp/UnifiedLobby20261118/before/GameLobby-open-20261118-163629.unity`。迁移前另有 `GameLobby-before-apply.unity` 与原页面 Prefab 文件副本。用户先前的源代码和其他面板修改继续保留。本轮未部署后端、未重新构建分发包。
