# 独立 Login 场景接线

注册、登录、切换账号全部由 `Login` 场景负责。UI 由开发者手工制作，不生成面板或调整布局。

2026-10-20：当前资源默认选择 **UnityGamingServices**。控制台开通、`FpsProfile` 脚本发布和环境设置见 [UGS 配置指南](UgsBackendSetup.md)。UGS 模式不需要旧账号服务的公网地址，用户名密码由 Authentication 管理，角色档案保存在 Cloud Save Protected，修改经 Cloud Code 校验。旧 SelfHosted 模式保留用于历史服务和本机回归测试。

## 已接入的流程

1. 客户端从 `Login` 启动。UGS 不提供未登录的用户名存在性查询：已有账号直接验证用户名和密码，新用户主动打开注册面板；登录失败不推断未注册、不自动创建账号。SelfHosted 模式仍先向旧后端检查注册状态。
2. 注册必须输入一致的确认密码；成功后回到登录面板，不自动进入游戏或载入角色档案。UGS 注册 API 自动建立的临时会话会立即退出并清除缓存。
3. 登录成功后按控制器的 `Enter Game After Login` 设置进入 `GameStart`。当前用户场景此项为关闭，先展示账号名、UID，点击“进入游戏”后继续；勾选后可自动进入。
4. `GameStartProfileLoader` 在到达 `GameStart` 后请求最新档案。主菜单等待请求成功后显示；`AccountCharacterPreview`、头像、外观、武器配装继续使用 `AccountClient.Profile` / `ProfileChanged`。
5. 档案请求失败返回 Login 并显示原因。网络或配置失败保留会话，可再次进入；服务端 401 清空会话与旧档案，要求重新登录。
6. 返回 Login 时，本次运行的有效会话可显示账号名/UID并直接点击进入游戏；切换账号吊销令牌并清空旧档案，再显示登录面板。

专用服务器仍从 GameStart 启动，不经过客户端登录流程。客户端构建包含 Login 在首位，服务端场景列表不包含 Login。GamePVE 的现有 GUID 指向开发者移动后的 `Assets/Scenes/Test/GamePVE.unity`。

## Login 场景中绑定 UI

场景中已经添加始终启用的 `LoginFlow` 对象，包含 `LoginSceneController` 和 `LoginUI`。不要把它放在将被关闭的登录/注册面板内部。

在 `LoginUI` 的 Inspector 拖入对应的对象，全部字段均可暂时留空：

| 字段 | 绑定对象 |
| --- | --- |
| Login Panel | 登录表单根对象 |
| Registration Panel | 注册表单根对象 |
| Ready Panel | 已登录账号展示/进入游戏区域 |
| Loading Panel | 可选的忙碌/加载提示根对象 |
| Login Username / Login Password | 登录的旧版 uGUI `InputField` 输入框 |
| Registration Username / Registration Password / Confirm Password | 注册的三个旧版 uGUI `InputField` 输入框 |
| Username Text / Uid Text | 账号名与 UID 的旧版 uGUI `Text` 文字 |
| Message Text | 错误/流程状态的旧版 uGUI `Text` 文字 |
| Additional Message Texts | 可选的其他面板提示文字 |
| Request Buttons | 请求期间需要禁用的按钮，包括登录、注册、进入游戏、切换账号、表单切换等 |

按钮的 `On Click()` 手工拖入 `LoginFlow` 并绑定下表方法。脚本不会自动添加按钮监听，因此不要重复绑定。

| 按钮 | 方法 |
| --- | --- |
| 登录 | `LoginUI.Login()` |
| 注册提交 | `LoginUI.Register()` |
| 打开注册 | `LoginUI.ShowRegistration()` |
| 返回登录 | `LoginUI.ShowLogin()` |
| 进入游戏 | `LoginUI.EnterGame()` |
| 切换账号 | `LoginUI.SwitchAccount()` |

密码输入框自动使用密码显示模式，提交及表单切换时清空密码。账号名在未注册跳转和注册成功后自动带入对应表单。

`Ready Panel` 仅在存在有效会话时显示。公共背景图、版权文字、音乐等放在这些切换面板之外。主提示文字需要放在始终可见的对象上，或在 `Additional Message Texts` 为各个面板提供文字。切换账号按钮可放在 Ready Panel 中。

UID 使用后端稳定的 `accountId`；UGS 模式使用 Unity Authentication 的 Player ID，不假设其格式或长度，也不使用截图中的数字 1。界面不需要本机注册标记。

## 自定义 UI 的接口

不用 `LoginUI` 时，可直接通过脚本调用 `LoginSceneController.Login(username, password)`、`Register(username, password, confirmation)`、`EnterGame()`、`SwitchAccount()`、`ShowLogin()` 和 `ShowRegistration()`。

可读取 `ViewState`、`Message`、`SuggestedUsername`、`IsBusy`、`AccountId`、`Username`，订阅 C# `Changed` 事件。Inspector 事件包括 `On Show Login`、`On Show Registration`、`On Show Ready`、`On Message Changed(string)`、`On Busy Changed(bool)`、`On Registration Required(string username)`、`On Login Succeeded(string accountId)`。动态参数应选择 Inspector 的 Dynamic 项。

`On Show Login/Registration/Ready` 只用于状态切换时显示面板或播放动画；使用 `LoginUI` 自动控制面板时可以全部留空。登录、注册、进入游戏、切换账号等操作应绑定按钮的 `Button.onClick`。控制器现在会阻止通知回调重入这些操作，并且同一面板内的提示更新不会重复触发显示事件。

GameStart 会自动创建数据加载组件。若要在 Inspector 绑定 `On User Data Loaded(PlayerProfileData)`、`On Message Changed(string)`、`On Loading Changed(bool)`，可在 GameStart 手工添加一个独立的 `GameStartProfileLoader` 对象，自动入口会复用它。该对象必须始终启用，且不能挂在启动时隐藏的 MainMenu 面板内。`LoadUserData()` 重新读取档案；`ReturnToLogin()` 返回登录界面。

账号客户端保留原有 `Login/Register` 接口；正式 Login 控制器使用 `RegisterAccount`、`LoginSession`，仅 SelfHosted 会调用 `CheckRegistration`。UGS 的 `Register` 与 `RegisterAccount` 均只注册并退出，不加载档案。`HasSession` 表示有效身份会话；`IsAuthenticated` 表示有效会话且档案已经载入，其他系统使用后者避免提前访问空档案。

UGS 模式中密码不由游戏保存；SDK 管理会话缓存，注册完成、退出账号及正常销毁客户端时清除。本轮不自动恢复缓存登录。UGS 账号与旧自建账号不自动迁移。旧队伍、匹配和专服票据 API 在 UGS 模式明确拒绝，不把 UGS 令牌发送给旧服务。

## 后端配置及更新

`Assets/Scripts/Account/Resources/AccountServiceSettings.asset` 的 `publicBaseUrl` 当前仍为空，需要使用实际账号服务 HTTPS 地址，或由现有启动器设置 `FPS_ACCOUNT_CLIENT_URL`。未配置时界面显示错误，不会创建假账号或宣称登录成功。

2026-10-20：用户选择继续使用已有雨云 Windows 服务器，简化部署，不迁移到 UGS 或其他托管平台。安装与更新入口见 [雨云部署说明](RainyunBackendDeployment.md)。Unity 新增 `Tools > FPS > Cloud Backend > Configure`，安装成功后只需粘贴公开 HTTPS 地址，保存并检查连接。窗口只修改 `publicBaseUrl`，游戏专服仍通过本机回环地址访问账号服务；没有修改 Login UI 或场景。

连接检查要求 `/health` 返回 `status=ok`、`service=fps-account` 和 `apiVersion>=2`，可识别误填网页地址或未更新的旧后端。检查不会提交密码、创建账号或更改用户档案。

本轮增加的接口需要将更新后的 `Server/AccountService` 发布到账号服务器：

| 接口 | 输入 | 输出 |
| --- | --- | --- |
| POST `/api/auth/check-registration` | `{username}` | `{registered}` |
| POST `/api/auth/register-account` | `{username,password}` | `{username}`，不创建登录会话 |

原有 `/api/auth/login`、`/api/profile`、注销和游戏票据接口兼容保留。注册检查独立每源 IP 每分钟 10 次，注册与登录沿用共享每分钟 10 次限制；公网均要求 HTTPS。

令牌沿用现有架构，只保存在本次进程内存，不写 PlayerPrefs 或存档；关闭游戏后需要重新登录。云端注册记录和用户档案持续保留。

## 验证

2026-10-20：雨云部署简化后，Unity 编译完成、真实 Login → GameStart 回归 44 项通过，测试退出后恢复环境变量和 HTTP 设置。验证器现显式设置自动进场分支，并临时允许后台运行后还原，避免用户在场景选择手动“进入游戏”时导致自动测试等待超时。产品中的 `enterGameAfterLogin` 设置和用户场景均未修改。

2026-10-19：排查进入 Play 后持续出现的 249 字节 TLS 临时分配警告，原始日志在首次警告前记录了 `On Show Login → LoginUI.Login → SetView` 的递归和 `StackOverflowException`。控制器已加入状态通知防重入，当前场景错误事件绑定已清空；完整重启编辑器后，启用原生临时分配堆栈诊断，三次独立 Play 启动及误绑定回归共 21 项检查通过；恢复普通启动方式后，额外一次启动的 7 项检查通过。共四次启动，无运行错误和 TLS 警告。回归入口为 `Tools > FPS > Login > Validate Startup And Event Reentrancy`，不连接账号服务、不修改场景资产。

本轮实际验证：Unity Editor 编译完成；隔离后端 HTTP/进程回归 73 项通过；真实 Play Mode 流程 44 项检查通过、无运行错误，并恢复了测试环境和 HTTP 设置。未执行本轮 Windows 客户端/专用服务器重建或公网部署，手工 UI 的视觉效果待制作后验收。

后端回归命令：`dotnet run --project Server/AccountService/Tests/AccountService.IntegrationTests.csproj -- E:/Unity资产/FPS/Server/AccountService`。测试使用隔离数据库，不修改正式账号。

Unity 运行时检查入口：`Tools > FPS > Login > Validate Runtime Flow (Local Service 5097)`。先在本机回环 5097 启动更新后的 Development 账号服务并指定独立测试数据库，再保存并打开 Login。验证进入真实 GameStart、未注册/重名/错误密码、会话复用、不同账号档案隔离、加载失败重试和 401；完成后退出 Play Mode，恢复账号地址环境变量和 HTTP 设置，结果写入 `tmp/LoginFlow/runtime-validation.json`。

重新接入场景的工具为 `Tools > FPS > Login > Bind Scene And Configure Startup`。修改前保存当前 Login 的完整副本到 `tmp/LoginFlow/login-before-时间.unity`，保留开发者正在编辑的场景内容，再仅添加功能对象并配置构建入口。
