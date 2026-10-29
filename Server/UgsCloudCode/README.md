# FpsProfile Cloud Code

将 `FpsProfile.js` 的完整内容复制到 Unity Dashboard → Cloud Code → Scripts，新建名称为 **FpsProfile** 的 JavaScript 脚本并发布。脚本使用内置 `@unity-services/cloud-save-1.4`，不需要在 Unity 客户端配置服务端密钥。

Dashboard 手工编辑器不会从 `module.exports.params` 自动生成参数表。请在参数页配置以下参数，然后保存、发布；其中只有 `operation` 勾选 Required，其余参数不填默认值。

| 名称 | 类型 | 用途 |
| --- | --- | --- |
| operation | String | 必填，`load`、`save` 或 `avatar` |
| username | String | 可选，首次档案显示名；ASCII 3–20 字符，允许字母、数字、`. - @ _` |
| revision | Numeric | `save` / `avatar` 必填，最近一次读取的版本号 |
| appearance | JSON | `save` 必填，现有 `CharacterAppearanceData` 的完整字段 |
| loadout | JSON | `save` 必填，现有 `PvpLoadoutData` 的完整字段 |
| avatarId | Numeric | `avatar` 必填，整数 0–31 |

`save` 保留头像，`avatar` 保留外观和配装。不要发送 `accountId`、`match`、令牌或其他参数。脚本只从已登录调用上下文读取 `playerId`，用 `context.serviceToken` 读写 Cloud Save 的 **Protected** 数据项 `fps_profile_v1`。不要使用客户端可以直接修改的 Default 数据项。所有调用都返回 `{ok:true,profile:{...}}` 或 `{ok:false,code,message}`；异常详情不会返回给客户端。

首次 `load` 返回默认档案、`revision:0`，**不会创建数据项**。首次 `save` / `avatar` 需要版本 0，创建版本 1。此后每次修改必须同时通过版本号检查和 Cloud Save 的真实 `writeLock` 检查；冲突返回 `profile_conflict`，客户端应重新读取后让玩家重试，不得自动使用旧内容覆盖。已有损坏档案返回 `profile_corrupt`，不会重建或覆盖。`username` 只在首次创建时确定，之后请求中的同名参数不会修改它；恢复登录时未传用户名会生成 `Player_...` 显示名。

**并发限制：Cloud Save 文档没有为不存在的数据项提供原子 create-if-absent 写锁。两个设备同时进行第一次保存时，都可能读取到缺失、以版本 1 保存成功，最后一次写入生效。** 当前脚本有意不在 `load` 中初始化，避免迟到的默认档案读取覆盖已保存数据；已有存档的并发修改使用真实写锁保护。若业务必须连首次创建也严格原子，需要另加具备条件创建/事务能力的服务。离线测试明确复现了这一限制，不能将它当作已解决的场景。

该脚本只管理注册登录后的玩家外观、配装和头像，不签发旧 .NET 后端的专服入场票据，也不迁移旧账号密码。必须在 Unity 项目中使用相同 UGS 项目与环境，并发布脚本后进行真实端到端验证；离线测试不代表云端配置已经生效。

离线测试（只使用 Node.js 内置模块）：

```powershell
node --test Server/UgsCloudCode/Tests/FpsProfile.test.js
```

测试执行实际脚本，替换 Cloud Save SDK 网络边界，检查校验、身份隔离、损坏数据、错误脱敏、写锁、延迟读取和首次保存竞争；不连接 Unity、不使用真实凭据。

官方接口依据：[DataApi 1.4](https://cloud-code-sdk-documentation.cloud.unity3d.com/cloud-save/v1.4/dataapi)、[SetItemBody](https://cloud-code-sdk-documentation.cloud.unity3d.com/cloud-save/v1.4/setitembody)、[Cloud Save 写锁](https://docs.unity.com/en-us/cloud-save/concepts/write-locks)、[Cloud Code 服务令牌](https://docs.unity.com/en-us/cloud-code/scripts/how-to-guides/token-support)、[脚本参数](https://docs.unity.com/en-us/cloud-code/scripts/how-to-guides/script-structure)。
