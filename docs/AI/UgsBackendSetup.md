# UGS 注册、登录和云存档配置

这套接入使用 Unity Authentication 管账号、Cloud Code 校验存档操作、Cloud Save 保存角色数据。账号功能由 Unity 托管，不再要求为它部署雨云账号服务、申请域名或配置 HTTPS 证书。现有 Unity Cloud 项目可以继续使用，**不要另外新建第二个项目**。

这份指南开通注册、登录和角色云存档。队伍匹配与雨云专服已增加 UGS 桥接接入，后续按 [UGS PvP 与雨云部署指南](UgsPvpRainyunSetup.md) 发布 `FpsPvp`、安装 worker 并更新两端；UGS 登录令牌不会直接发送给游戏专服。

## 1. 让 Unity 工程关联已有云项目

1. 在 Unity Hub 登录拥有该云项目权限的 Unity 账号，然后打开本工程。
2. 在 Editor 打开 **Edit > Project Settings > Services**。
3. 确认组织和项目就是你已有的 Unity Cloud 项目。如果尚未关联，选择 **Use an existing cloud project / Use an existing Unity project ID**，选中已有组织、项目，再点 **Link Unity project to cloud project / Link project ID**。不同 Editor 版本的按钮文字略有区别。
4. 到 [Unity Dashboard](https://cloud.unity.com) 打开同一个项目，在 **Settings** 查看 **Project ID**，与 Editor 中显示的 ID 核对。工程里已有 Project ID 也需要确认关联正确，并完成 Services 页要求的启用或设置步骤。

Project ID 用于标识云项目，可以保存在工程配置中；它不是服务账号密码或管理密钥。本次接入不需要把 Service Account Secret、访问令牌或玩家密码填入 Inspector。[官方项目关联步骤](https://docs.unity.com/en-us/cloud/projects/configure-project-for-unity-cloud)

Package Manager 完成解析后，应能找到这些依赖；不需要导入它们的示例场景：

| 包 | Package ID |
| --- | --- |
| Services Core | `com.unity.services.core` |
| Authentication | `com.unity.services.authentication` |
| Cloud Code | `com.unity.services.cloudcode` |
| Cloud Save | `com.unity.services.cloudsave` |

## 2. 建立同一个测试环境

1. 在 Dashboard 打开项目，进入 **Development > Environments**。
2. 在环境列表核对 **Name / 环境名称**。如果已有 `development`，继续使用它；如果没有，点 **Add Environment**，输入 `development`，再点 **Add**。不要把 **Environment ID** 那一串 ID 填成环境名称。
3. 后面打开 Cloud Code、Cloud Save 时，都把页面的环境选择器设为 **development**。

本指南固定使用下面两个值，大小写也照填：

| 设置 | 值 |
| --- | --- |
| Environment / 环境 | `development` |
| Cloud Code Script / 脚本名 | `FpsProfile` |

Cloud Code 脚本和 Cloud Save 存档按环境隔离；客户端填 `development`，脚本也必须发布到 `development`。Authentication 的玩家身份和 Player ID 属于同一个云项目，切换环境并不等于重新注册了一套账号；玩家昵称是按环境保存的例外。[环境官方说明](https://docs.unity.com/en-us/services/service-environments)、[玩家身份与环境的区别](https://docs.unity.com/en-us/services/player-management/get-started)

工程中的 `development` 是客户端将发送的环境名称，不会自动在云项目里创建同名环境。如果希望使用已有的其他环境，应主动选择它，在第 6 步把本地**环境名称**改成环境列表中的准确名称，并在同一环境发布 `FpsProfile`、检查 Cloud Save。不要通过自动切换到 `production` 来绕过环境错误；本工程当前默认仍是 `development`。

## 3. 开通用户名和密码登录

1. 在 Dashboard 选择 **Development > Products > Player Authentication**。
2. 打开 **Identity Providers**。
3. 点 **Add Identity Provider**，选择 **Username Password / Username & Password**。
4. 再点 **Add Identity Provider** 完成添加。如果已经有这一项，确认它处于启用状态。

这里只需要用户名密码提供方；**Unity Player Accounts** 是另一种登录方式，不是本次要选的选项。[官方 Dashboard 操作步骤](https://docs.unity.com/en-us/services-web-apis/client-auth)

UGS 的原生规则如下，旧系统里过短的密码不能直接用于这里注册：

| 字段 | 规则 |
| --- | --- |
| 用户名 | 3–20 个字符，只支持英文字母、数字和 `. - @ _`，不区分大小写，不支持中文或空格 |
| 密码 | 8–30 个字符，至少包含一个大写字母、一个小写字母、一个数字和一个符号；区分大小写 |

用户名与游戏里的显示昵称不同。[用户名和密码官方规则](https://docs.unity.com/en-us/authentication/platform-signin/username-password)

## 4. 打开 Cloud Save

1. 在 **Development > Products** 找到并打开 **Cloud Save**。
2. 如果显示首次使用的引导，按页面完成开始使用；已能进入数据页面则继续。
3. 确认环境是 **development**。

不需要手动创建数据库、角色记录或索引。本工程的脚本会把角色档案写到 **Player Data > Protected**，键名是 `fps_profile_v1`。Protected 的含义是：玩家可以读取自己的数据，只有服务端代码可以写入；本工程通过 Cloud Code 写入，不把它改成 Public。[Cloud Save 访问权限说明](https://docs.unity.com/en-us/cloud-save/concepts/player-data)

**注册和首次读取默认角色不会立即生成这条存档。** 第一次修改角色并成功保存后，Dashboard 中才会出现 `fps_profile_v1`。

## 5. 创建并发布 FpsProfile 脚本

1. 进入 **Development > Products > Cloud Code**，完成页面显示的首次启用步骤。
2. 选择 **JS Scripts**，确认环境是 **development**。
3. 点 **Create your first script / Create script**，脚本名输入 **FpsProfile**。这是 JavaScript 脚本，不是 C# Module。
4. 添加下表中的参数。只有 `operation` 勾选 **Required**；其他参数不要勾选，由脚本根据具体操作检查。

| Parameter name | Type | Required | 用途 |
| --- | --- | --- | --- |
| `operation` | String | 是 | `load`、`save` 或 `avatar` |
| `username` | String | 否 | 新角色默认资料使用的用户名 |
| `revision` | Numeric | 否 | 保存时使用的存档版本号 |
| `appearance` | JSON | 否 | 角色外观 |
| `loadout` | JSON | 否 | 角色装备配置 |
| `avatarId` | Numeric | 否 | 头像编号 |

5. 在 Unity 中打开 **Tools > FPS > UGS Backend > Configure**，点 **复制 FpsProfile 云脚本**；也可以直接打开工程里的 [FpsProfile.js](../../Server/UgsCloudCode/FpsProfile.js) 并复制全部内容。窗口里的 **复制云脚本参数表** 可以辅助核对上面这 6 个参数；可选参数不要添加默认值。
6. 回到浏览器脚本编辑器，把默认示例代码**全部替换**为复制的内容。参数列表不能替代代码，代码也不能替代参数列表，两处都需要配置。
7. 点 **Save script**，再点 **Publish version** 并确认发布。只保存草稿，游戏客户端仍然无法使用这份新代码。
8. 确认脚本列表中 `FpsProfile` 有已发布的版本，所在环境为 **development**。以后更新本地脚本，也需要重新粘贴、保存、发布。

菜单、参数类型以及保存和发布的区别可对照 [Cloud Code 官方控制台指南](https://docs.unity.com/en-us/cloud-code/scripts/how-to-guides/write-scripts/unity-dashboard)。这个脚本从已登录玩家的上下文取得 Player ID；不需要为它配置管理密钥，也不接收密码参数。

## 6. 在本工程里选择 UGS 后端

1. 退出 Play 模式。
2. 打开 **Tools > FPS > UGS Backend > Configure**。
3. **环境名称** 填 `development`，**Cloud Code 脚本名** 填 `FpsProfile`，点 **保存 UGS 设置**。这个按钮会将账号后端切为 `UnityGamingServices`。
4. 点 **检查本地配置**，处理它提示的缺少项目关联或配置问题。这个检查不能代替 Dashboard 的开通和发布操作。窗口里的 **打开 Project Settings > Services** 和 **打开 Unity Cloud 控制台** 可直接打开对应页面。
5. 从 **Login** 场景开始测试。

这一步使用 UGS 的服务地址，不需要填写旧雨云账号服务器的公网 IP。原有 UI 仍由 Login 场景的登录、注册按钮触发相应功能。

## 7. 按完整流程测试

1. 在 Login 选择注册，用一个符合规则、尚未使用的用户名注册测试账号。
2. 注册成功后会返回登录页面，再输入该账号和密码登录。UGS 注册 API 本身会建立登录状态，本工程会在注册成功后主动退出，以保留“先注册、再登录”的流程。
3. 当前 Login 场景关闭了自动跳转：登录成功先显示已登录的 **Ready** 状态，点击 **进入游戏** 后到 **GameStart**，才读取该用户的角色档案；没有存档时显示默认角色。如果希望登录后自动进入，可在 **LoginSceneController** 组件上勾选 **Enter Game After Login**。
4. 修改角色外观、装备或头像并保存，退出登录，再登录同一账号，确认更改仍在。
5. 在 Dashboard 的 Authentication 玩家管理中查到该玩家，记下 **Player ID**；然后在 Cloud Save 的 **development** 环境查同一个 Player ID，查看 **Protected** 下的 `fps_profile_v1`。注册后只有 Authentication 记录、没有 Cloud Save 记录是正常的，先完成一次修改并保存。

需要检查读取结果时，在已登录并进入 GameStart 后，可以打开配置窗口，点 **读取真实 UGS 存档（需已登录）**。这个按钮会实际访问云端；它与 **检查本地配置** 的用途不同。

UGS SDK 会管理本机会话缓存，工程不保存明文密码。注册成功后和退出、切换账号时会退出 SDK 会话并清除缓存；本轮要求用户主动登录，不自动恢复上次的登录状态。

UGS 没有供未登录客户端单独查询用户名是否存在的公开接口。本次流程由新用户主动选择注册，已有用户直接登录；不能把“密码错误”“网络错误”或“提供方未开通”都判断成未注册，也不会因一次登录失败自动创建账号。

## 常见卡点

| 现象 | 先检查 |
| --- | --- |
| 用户名或密码不符合规则 | 对照第 3 步，尤其是密码的大写、小写、数字和符号 |
| 提供方未启用或注册、登录被拒绝 | 同一项目内是否添加并启用了 Username Password；Project ID 是否一致 |
| 提示“UGS 环境名无效，请检查环境配置。”或“UGS 请求参数或服务配置无效（10002）。” | 10002 不只表示账号密码错误。本次真实响应为 HTTP 400、`INVALID_PARAMETERS`、`Invalid environment name provided`：先按第 2 步检查同一云项目的环境列表，创建缺少的 `development`，或主动把本地设置改成已有环境的准确 **Name**；不要填 Environment ID。脚本必须发布在同一环境 |
| 账号或密码错误 | 确认输入正确；旧雨云账号尚未迁移，首次使用 UGS 需要注册新账号 |
| 登录后提示找不到云存档脚本（9002） | 本次真实 Console 为 Cloud Code HTTP 404、`Script could not be found`；按第 5 步在 `development` 创建并发布 JS 脚本 `FpsProfile`。在 `production` 发布或只保存草稿均不足以供当前环境调用 |
| 登录显示通用 UGS 错误码 0 | 2026-10-23 的 Editor 与 Windows 客户端日志实际为 Authentication HTTP 400、`WRONG_USERNAME_PASSWORD`。Authentication 3.6.1 未映射该标题，因此包装为普通 `RequestFailedException(0)`。客户端现仅对这一明确认证响应提示“用户名或密码不正确，请核对后重试。”；其他未知错误 0 保持独立，不据此断言网络或 TLS 故障。请核对当前项目中已注册的 UGS 账号及密码，旧自建后端账号不会自动迁移。 |
| Missing/Invalid parameter | 对照第 5 步参数名、类型和 Required，尤其 avatarId 是 Numeric，appearance/loadout 是 JSON |
| 登录后看到默认角色 | 是否在相同环境、是否已成功保存过；首次加载默认档案不会落盘 |
| 提示存档已被其他操作更新 | 重新读取最新档案后再修改、保存；不要重复提交旧版本 |
| UGS 登录正常但匹配或进房失败 | 按 [PvP 部署指南](UgsPvpRainyunSetup.md) 检查同环境的 FpsPvp、雨云 worker 与服务账号权限，以及客户端/专服是否均为协议 5 |

旧自建后端的账号、密码和角色文件不会自动迁入 UGS；相同用户名也不会自动对应同一个 Player ID。在线开通、脚本发布和真实账号联调需要在你自己的 Dashboard 中完成，本地配置检查通过不代表云端已经完成这些步骤。

用户名 `abc` 是合法的三个半角英文字母。可在停止运行时执行 **Tools > FPS > Login > Check Input Bindings**，检查注册输入框引用、该示例的本地校验，以及服务错误提示是否能完整显示；报告位于 `tmp/UgsIntegration/login-input-audit.json`，不会导出用户名、密码或令牌。真实注册失败仍需根据 UGS 的错误码判断，不能只根据一行被截断的规则提示归因。

2026-10-20 已从实际失败响应确认上述环境名称错误，不能继续把这次 10002 归因为 `abc` 不合法或密码提供方未启用。此证据只定位了失败原因，尚未证明云端环境已创建、脚本已发布或真实注册已成功。诊断时只提供 HTTP 状态、错误类别和请求 ID，不发送密码、访问令牌或完整请求体。
