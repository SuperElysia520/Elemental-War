# 新注册账号的数字 UID

2026-11-10 确认规则：**只对新注册账号启用九位数字 UID，从 111100001 开始递增**。用户已取消两个现有账号的补号要求；不再需要它们的用户名、Player ID 或注册时间，也不执行旧账号迁移。

## 已实现

- 内部 Authentication Player ID、登录密码、角色档案主键和联机身份保持原有实现，数字 UID 是独立的公开显示号码。
- 以固定启用时刻 **2026-11-10 01:21:16 UTC（北京时间 09:21:16）** 区分旧账号。`UgsAccountGateway` 从 Authentication SDK 的 `PlayerInfo.CreatedAt` 读取实际注册时间；缺少时间时调用 `GetPlayerInfoAsync`，无法确认时不猜测。启用前注册的账号使用原 UID，登录时不调用 UID 分配服务，不新增其 UID 数据。这个时间在以后的构建中保持固定。
- 新注册流程在 Authentication 成功后、退出临时会话之前调用 `FpsAccountUid` 分配号码，未登录过的账号也占有自己的号码。新账号之后登录会读取相同号码并恢复中断的分配。编号先后是注册流程中服务端确认分配的先后，不按首次登录排序。Authentication 与 Cloud Save 不能组成同一个事务，因此认证成功但 UID 暂未确认时会明确提示账号已注册，重新登录可重试。
- `AccountClient.PublicUid` 和 `PlayerProfileData.uid` 提供数字；`AccountClient.DisplayUid` 为旧账号保留原显示身份。Login、大厅、个人资料和角色/配装界面统一使用这一规则。
- 每个新账号在 Cloud Save Protected 中新增 `fps_uid_v1`，格式为 `{"version":1,"playerId":"该账号原PlayerID","uid":111100001}`。角色键 `fps_profile_v1` 不增加字段，不覆盖已有角色、头像和配装，不要求更新 FpsProfile、FpsPvp 或雨云桥接服务。
- 全局 Private Custom Item `fps_account_uid_v1` 的 `sequence` 使用真实 writeLock 控制并发，并通过 pending 保留分配。并发、重复请求、延迟空读取、响应丢失和中断恢复都不会为同一账号重复占号。号码不回收；最大值为 999999999。

## 需要在 Unity Dashboard 做的两步

本地工程不能替代云端发布；以下两步尚未由 Codex 在云端执行。它们不需要修改两个现有账号。

1. 在当前 Unity 项目的 **development** 环境进入 **Cloud Save → Game Data**，创建 Custom ID 为 **fps_account_uid_v1** 的条目，在其 **Private** 数据中创建键 **sequence**，值为 JSON 对象：

   ```json
   {"version":1,"nextUid":111100001,"pending":null}
   ```

   这只是首次初始化。已有 sequence 时不要覆盖或重置，避免复用已经分配的号码。不要为两个旧账号添加 fps_uid_v1，也不要修改 fps_profile_v1。

2. 在同一环境进入 **Cloud Code → JS Scripts**，创建脚本 **FpsAccountUid**，完整复制 [FpsAccountUid.js](../../Server/UgsCloudCode/FpsAccountUid.js)。添加唯一参数 **operation**：类型 **String**、勾选 **Required**、不设默认值。保存后执行 **Publish**。

完成后，从 Login 注册新账号，第一、第二个新账号应显示 **111100001、111100002**；退出再登录号码不变。原来的两个账号正常登录时继续保留原 UID 和角色数据。

## 验证

```powershell
node --test Server/UgsCloudCode/Tests/FpsAccountUid.test.js Server/UgsCloudCode/Tests/FpsProfile.test.js Server/UgsCloudCode/Tests/FpsPvp.test.js
```

离线测试执行真实云脚本，以假传输覆盖并发注册、同账号并发、延迟空读取、响应丢失、恢复分配、九位上限和伪造参数。Unity 的离线 UI/账号回归覆盖数字显示、新旧账号兼容、账号切换、持久身份和原登录读档流程。离线检查不代表云端已发布或真实新账号已分配。

官方依据：[Authentication PlayerInfo 与注册时间](https://docs.unity.cn/Packages/com.unity.services.authentication%403.3/api/Unity.Services.Authentication.IAuthenticationService.html)、[Cloud Save Protected 与 Game Data](https://docs.unity.com/en-us/cloud-save/tutorials/cloud-code)、[写锁与首次创建语义](https://docs.unity.com/en-us/cloud-save/concepts/write-locks)。当前工程 Authentication 3.6.1 的 CreatedAt 定义和 UTC 解析也已经从本地 SDK 核对。

本次 Unity Editor 编译通过；71 项离线 UI 检查、296 项云脚本测试通过；账号回归完成 103 项功能断言。账号报告仍因两条已有 Visual Scripting 初始化异常标记失败，没有新增功能断言失败，已恢复编辑器到停止状态的 Login。证据位于 tmp/UgsIntegration/Uid20261110，云端配置与真实新账号注册尚需完成上文两步后验收。
