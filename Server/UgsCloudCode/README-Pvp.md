# FpsPvp Cloud Code 桥接

`FpsPvp.js` 是客户端的 UGS 队伍、匹配和短票据入口。它只读写 Cloud Save 的 **Private Game Data / Private Custom**，不请求雨云公网 HTTP。雨云上的单个 .NET worker 主动通过 HTTPS 访问 UGS，执行既有 PartyStore 匹配规则；同机 NGO 专服仍通过回环地址兑换短票、检查端口预约和上报对局事件。

账号和角色存档仍由 `FpsProfile` 管理。这两个脚本都要在同一个 Unity Cloud 项目的同一个环境发布；本指南中的 `development` 是环境名称，不是自动存在的默认环境。worker 使用的 Environment ID 也必须指向这个环境。

## 发布步骤

1. 在 Unity Dashboard 打开已有项目，选择 **Development > Products > Cloud Code > JS Scripts**。
2. 切换到客户端和 worker 使用的环境，例如已经创建的 **development**。
3. 创建 JavaScript 脚本，名称填 **FpsPvp**，将本目录 `FpsPvp.js` 的完整内容替换默认示例。
4. 手工添加下列 **5 个参数**。只有前两个勾选 Required；其余均不勾选，也不添加默认值。
5. 点 **Save script**，再点 **Publish version**。保存草稿不等于发布。

| 名称 | 类型 | Required | 内容 |
| --- | --- | --- | --- |
| `action` | String | 是 | `submit` 或 `status` |
| `requestId` | String | 是 | 每次新请求使用新的 32 位十六进制 ID |
| `operation` | String | 否 | submit 使用的固定命令 |
| `username` | String | 否 | 合法的 3–20 位 ASCII 用户名，仅作未保存角色的显示名 |
| `args` | JSON | 否 | 命令参数对象，默认 `{}` |

不要为脚本添加 `playerId`、`profile`、`team`、`serverPort`、`epoch`、密码或令牌参数。玩家身份只取经过 UGS 验证的 `context.playerId`；权威档案由 worker 从 Protected `fps_profile_v1` 读取。

客户端命令：`current`、`rooms`、`create`、`join`、`ready`、`queue`、`cancel`、`leave`、`fail`、`ticket`、`game-ticket`。只有 `join` 接收 `{partyId:<32hex>}`，`ready` 接收 `{ready:true/false}`，其他命令参数必须为空对象。`ticket` 用于匹配入场，`game-ticket` 用于普通房间入场和已连接玩家的配装验证。

控制台导航及手工参数说明见 [Unity Cloud Code 官方指南](https://docs.unity.com/en-us/cloud-code/scripts/how-to-guides/write-scripts/unity-dashboard)。

## worker 与安全边界

脚本固定访问 Custom ID `fps_pvp_bridge_v1`，只读取 `bridge` 租约及当前玩家的 `p_<playerId>` 项。它不会替 worker 创建、续期或删除租约，也不会删除邮箱数据。租约持续 15 秒，由 worker 至少每 5 秒刷新；每次启动使用新的 epoch。未启动 worker、环境不一致或租约过期时，脚本返回 `bridge_unavailable`。

每个玩家同时只保留一个请求。submit 写入后立即返回 `pending` 或已经完成的 `done`；客户端用相同 requestId 调用 status。**Cloud Code 不等待 worker 完成**，不会在一次脚本执行中循环轮询。请求有效期为 30 秒，客户端不得通过重复 submit 延长期限。收到业务错误时，返回仍是 `ok:true/state:done`，其 `status` 和 `body` 保留 HTTP 错误语义；这表示请求已处理，不表示业务成功。

已有邮箱项的修改必须带 Cloud Save 返回的真实 writeLock。冲突时不会去掉锁覆盖或自动重放变更。Cloud Save 没有为不存在的数据项提供原子 create-if-absent：同一玩家在两台设备同时第一次提交时，可能都曾得到 pending，随后其中一个被替换。脚本会写后重读并核对归属、epoch、requestId 和命令；它绝不会把另一个请求的结果当作本次结果。收到 `request_superseded` 时先重新读取队伍现状，再决定是否用新的 ID 重试，不要盲目重放旧变更。[官方 writeLock 语义](https://docs.unity.com/en-us/cloud-save/concepts/write-locks)

每次请求限制为 4 KiB，完整邮箱项及结果限制为 64 KiB。实际部署必须每个 UGS 环境只有一个 worker；邮箱、PartyStore 和票据存储不是支持多实例竞争的分布式队列。worker 服务账号的密钥只保存在雨云服务器的受限外部配置中，不能放入脚本、Unity 客户端、Resources、仓库或日志。Cloud Code 使用平台提供的 serviceToken，不需要嵌入这份密钥。

完整字段、状态、错误码、超时与本机接口见 [PvpBridgeProtocol.md](PvpBridgeProtocol.md)。Private Custom 只能从服务端权限上下文访问，依据 [Cloud Save Game Data 访问权限](https://docs.unity.com/en-us/cloud-save/concepts/game-data) 和 [Cloud Save DataApi 1.4](https://cloud-code-sdk-documentation.cloud.unity3d.com/cloud-save/v1.4/dataapi)。

## 离线验证

```powershell
node --check Server/UgsCloudCode/FpsPvp.js
node --test Server/UgsCloudCode/Tests/FpsPvp.test.js
```

测试执行实际发布脚本，仅替换 Cloud Save SDK 的网络边界；不需要真实 UGS 账号、密钥或网络。它验证身份隔离、租约、请求归属、业务错误、数据损坏、错误脱敏、写锁竞争和首次创建边界。离线通过不能代替真实的 Cloud Code 发布、worker 配置以及客户端和专服联机验证。
