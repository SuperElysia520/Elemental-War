# UGS PvP 与雨云专用服务器

注册、登录和角色档案由 UGS 托管。雨云运行游戏战斗专服，以及一个只供本机使用的 PvP worker；worker 主动通过 HTTPS 访问 UGS，不向玩家开放 HTTP 接口。客户端通过 Cloud Code `FpsPvp` 发送组队和匹配命令。

2026-10-20 本机已生成交付包：`Builds/UgsPvp/Rainyun-UgsPvp.zip`（含专服、独立 Windows x64 bridge、云脚本及“先读我.md”）与 `Builds/UgsPvp/WindowsClient.zip`。首次部署可直接使用这两个包，按包内“先读我.md”操作；下面的源码发布命令供以后更新使用。修改代码后需要同步重建两端与 bridge，不能继续沿用旧包。

本流程**不需要域名、IP HTTPS 证书、TCP 80/443 入站规则**。不要在雨云安全组或 Windows 防火墙向公网开放 5080。游戏 UDP 端口沿用已有配置；默认 7777–7786，发现查询端口为它们加 41000，只用于本机发现。

## 先完成 UGS 控制台配置

1. 使用 [UgsBackendSetup.md](UgsBackendSetup.md) 中同一个 Unity Cloud 项目和环境，发布 `FpsProfile` 与 `FpsPvp`。不要另建第二个项目。
2. 在项目环境设置中找到 **Environment ID**。它是 UUID，与 `development` 这样的环境名称不同。项目 ID、环境 ID、客户端和 Cloud Code 的环境必须一致。
3. 在 **Manage organization > Administration > Service Accounts** 为 worker 创建独立账号，选择 **Manage project roles / Add project role**，限定当前项目，授予 **Cloud Save Editor**。如果控制台支持环境范围，优先选择 **Cloud Save Editor (environment)** 并限定当前环境；否则使用 **Cloud Save Editor (project)**。Editor 已包含读写，不需要再额外授予 Viewer。该 worker 要读取玩家 Protected 档案，以及读取、写入、删除 Private Custom 数据；不要授予组织 Owner，也不需要 Authentication 管理、Cloud Code 发布或其他项目权限。随后在 **Keys > Add key / Create key** 创建 Key ID 和 Secret Key。密钥只放在雨云 worker，**不要放进 Unity Resources、客户端、Git 或聊天消息**。[服务账号创建步骤](https://docs.unity.com/en-us/services-web-apis/service-account-auth)、[Cloud Save 官方角色与范围](https://docs.unity.com/en-us/oas-cloud-save-data-client/1.0.0)
4. worker 的 Private Custom ID 固定为 `fps_pvp_bridge_v1`，不得在同一项目环境运行第二台 worker。另一个仍有效的 worker lease 会导致启动失败。

协议与权限边界见 [PvpBridgeProtocol.md](../../Server/UgsCloudCode/PvpBridgeProtocol.md)。UGS 服务账号凭据说明见 [Unity 官方服务账号认证](https://docs.unity.com/en-us/services-web-apis/service-account-auth)；Cloud Save 服务端访问边界见 [Cloud Save 官方 API](https://docs.unity.com/en-us/oas-cloud-save-data-client/1.0.0)。

## 在开发电脑发布并上传

本轮已构建的客户端入口为 `Builds/UgsPvp/Client/FPS.exe`，专服入口为 `Builds/UgsPvp/Server/FPS_PVP_Server.exe`。雨云 bridge 发布目录为 `Builds/UgsPvp/Rainyun/Bridge`；需在本机完成发布后再上传目录中的全部文件。以下命令也可重新发布到单独的临时目录。

在工程根目录的 PowerShell 中运行：

```powershell
dotnet publish Server/AccountService/AccountService.csproj -c Release -r win-x64 --self-contained true -o tmp/UgsPvpPublish
```

将 `tmp/UgsPvpPublish` 中的全部内容上传到雨云的独立目录，例如 `C:\Uploads\UgsPvpPublish`。确认其中有 `AccountService.exe`、`AccountService.dll`、`.deps.json`、`.runtimeconfig.json`、`appsettings.json`，以及 `Deployment\Install-UgsPvpBridge.ps1`、同名 `.cmd`。若发布输出尚未包含安装器，将工程 `Deployment` 中这两个安装文件复制到上传目录的 `Deployment` 子目录。**不要上传原账号 data、State、证书或管理密钥。**

非自包含发布也支持，但雨云必须先安装 Windows x64 .NET 10 ASP.NET Core Runtime。

## 在雨云首次安装 worker

这是独立首次安装器。默认使用：

| 项目 | 默认位置 |
|---|---|
| 程序 | `C:\FpsUgsPvp\App` |
| 私有配置与启动器 | `C:\FpsUgsPvp\State` |
| 服务账号配置 | `C:\FpsUgsPvp\State\ugs-pvp-config.json` |
| 开机任务 | `FPS-UgsPvpBridge`，以 LocalService 运行 |
| 本机监听 | `http://127.0.0.1:5080` |

双击上传包中的 `Install-UgsPvpBridge.cmd`，确认安装并允许管理员权限。按提示填写 Project ID、Environment ID、服务账号 Key ID、雨云游戏服务器公网 IPv4。Secret Key 使用隐藏输入，不会写进命令行或启动脚本。默认环境名称为 `development`；其他环境应在管理员 PowerShell 中明确传入 `-EnvironmentName`。

也可先只做预检（下面的值均不是 Secret）：

```powershell
& 'C:\Uploads\UgsPvpPublish\Deployment\Install-UgsPvpBridge.ps1' `
  -PublishDirectory 'C:\Uploads\UgsPvpPublish' `
  -ProjectId '你的项目UUID' -EnvironmentId '你的环境UUID' `
  -ServiceAccountKeyId '你的KeyID' -ServerAddress '雨云公网IPv4'
```

**没有 `-Install` 时只预检，不创建目录、任务、ACL 或进程。**确认后在同一命令最后加 `-Install`。可以用 `-StartPort`、`-ServerCount` 修改游戏端口范围。

安装器会拒绝：非空或交叠的目标目录、重解析路径、缺失发布文件、包含账号或证书的上传包、已存在的账号服务任务、已存在的 bridge 任务、占用 5080 的进程。它不会停止旧程序、替换旧任务、删除账号文件或修改防火墙。遇到拒绝，先在任务计划程序和任务管理器核实旧服务，备份旧 State，再由管理员决定如何处理；不要直接删除旧数据。

配置目录禁用继承，只允许 SYSTEM、Administrators 和 LocalService。配置文件对 LocalService 只读，worker 状态子目录允许写入。需要复用已有配置时，使用 `-ExistingConfiguration '绝对路径'`；来源文件也必须只有上述受信任身份可以访问。安装器不会接受 Secret 命令行参数。

## 检查 worker，再启动新版专服

在雨云服务器本机运行：

```powershell
Invoke-RestMethod 'http://127.0.0.1:5080/health'
Get-ScheduledTask -TaskName 'FPS-UgsPvpBridge'
```

健康检查可确认 HTTP 进程运行；还需从客户端实际创建队伍，确认 UGS mailbox、服务账号权限和 worker 扫描正常。单独 `/health` 成功不等于 PvP 已联通。

### 创建队伍提示桥接未就绪时

先确认当前项目的 `development` Environment ID 与雨云配置相符。配置中的 EnvironmentName 仅供校验和说明，实际服务账号访问环境由 EnvironmentId 选择。

如果任务 Running、health 正常、环境 ID 也一致，使用单文件只读诊断 `Deployment/Test-UgsPvpBridge.ps1`（交付副本 `Builds/UgsPvp/Test-UgsPvpBridge.ps1`）。将文件复制到雨云 `C:\FPS\Test-UgsPvpBridge.ps1`，在管理员 PowerShell 执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File 'C:\FPS\Test-UgsPvpBridge.ps1'
```

2026-10-29 更新：请使用报告 `DiagnosticVersion: 2` 的版本（另有明确命名的 `Builds/UgsPvp/Test-UgsPvpBridge-v2.ps1` 交付副本）。旧 v1 诊断把 token exchange 的成功状态限定为 200，因而将真实 201 误报为 TokenExchangeFailed，并在采样心跳前退出。现在接受 200–299 并继续严格校验 accessToken；缺少令牌或损坏 JSON 仍拒绝，不输出响应原文。Unity [Token Exchange v2 官方文档](https://docs.unity.com/en-us/oas-auth-client/2.0.0) 明确成功返回 201 Created。正式 .NET worker 原先就使用 IsSuccessStatusCode，无需为此次诊断修正重装服务或更换密钥。

脚本在服务器内读取现有私有配置，以 TLS 1.2 向固定 Unity 官方地址换取临时令牌，然后三次只读查询 Private Custom 的 `bridge` 项（间隔七秒）。保留正常证书验证、禁止跳转、结束后恢复 PowerShell TLS 设置；不停止任务、不改配置或心跳、不输出密钥、令牌、epoch、writeLock、完整响应或请求地址。将最后的 JSON 报告用于排查，不要发送配置文件。

| Result / 字段 | 证据含义 |
| --- | --- |
| TokenExchangeFailed / TokenHttp | 本次管理员诊断无法取得服务账号临时令牌；HTTP 0 表示没有 HTTP 响应，401/403 需核对账号凭据或项目权限。不能凭 0 判定具体 TLS 原因。 |
| CloudReadFailed / Samples.Http | 本次诊断无法读取目标环境的 Private Custom 数据，按 HTTP 状态继续检查权限或网络。 |
| LeaseMissing / LeaseInvalid | 该配置指向的云端没有 bridge 项，或其结构不符合当前协议。不要手工创建或重写租约。 |
| LeaseExpired | UGS 响应时间所见的最后一次心跳已过期。 |
| LeaseNotAdvancing | 三次观察期间过期时间没有前进，需查实际 worker 是否持续运行。 |
| LeaseHealthy | 三次样本的结构、最后有效期和续期正常；若同刻客户端仍未就绪，继续核对实际云项目、Cloud Code 环境和发布内容。不能据此宣称匹配已成功。 |
| ClockSkewDetected / ServerUtcMinusCloudSeconds | 数值是服务器 UTC 减去 UGS HTTP Date 的估算秒差；负值表示服务器较慢。Date 精度和往返延迟会影响估算，不会自动改系统时间。 |
| CloudTimeUnavailable | 缺少可解析的 UGS Date，脚本不会用本机时间冒充云端时间来判定租约有效。 |

这是管理员 PowerShell 自己的只读访问证据，不等于已证明 LocalService 任务正在使用同一份配置或同一运行时网络路径。

**必须重新构建并上传 account protocol 5 的 Unity 专服和客户端。旧二进制不兼容。**不要让旧 pool 与新 pool 同时占用同一组 UDP 端口。

在服务器 PowerShell 使用安装器生成的 launcher：

```powershell
& 'C:\FpsUgsPvp\State\Start-UgsPvpServers.ps1' -ServerExe 'C:\FpsGame\Server\FPS_PVP_Server.exe'
```

它仅对所启动的专服进程设置：

```text
FPS_ACCOUNT_BACKEND=UnityGamingServices
FPS_ACCOUNT_SERVER_URL=http://127.0.0.1:5080
```

使用 `-Stop` 只停止原 pool launcher 记录且路径匹配的进程。worker 安装器不会自动启动或停止游戏服，也不会替换已有专服开机任务。需要开机启动 pool 时，让原专服任务执行这个新的 launcher，并以原本有权写入专服日志和 PID 文件的 Windows 身份运行；不要让它继续调用旧 pool 命令而漏掉 UGS 环境变量。

## 联调与维护

### 桥接停止续期后的恢复与更新（2026-10-29）

本次雨云实际诊断为 TokenHttp=201、Private GET=200、lease 结构有效，但过期约 6.7 天且三次不前进，时差不到一秒。监听程序确认为现有 App 的 AccountService.exe；只停止并重启固定任务、确认旧进程退出和旧租约窗口结束后，真实三次采样恢复 LeaseHealthy/LeaseAdvancing=true/EpochStable=true，用户确认已能创建并停留在队伍面板。无法凭这些证据追溯旧进程具体卡在哪一次请求。

源码另修正两个已复现的缺口：UGS HTTP 整次操作的 10 秒期限覆盖令牌等待、响应正文和一次 401 重试；健康检查读取本进程已成功写入的实际租约有效期，过期返回 HTTP 503 / degraded。健康过期不会禁止 worker 后续续租恢复。旧的 SelfHosted 健康响应保留原三个字段。正文超时行为依据见 [Microsoft HttpCompletionOption 说明](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption?view=net-10.0)。

已安装原 .NET 10.0.8 自包含 bridge 的服务器可使用 `Builds/UgsPvp/UgsBridgeRecoveryPatch.zip`。完整解压到雨云 `C:\FPS\UgsBridgeRecoveryPatch`，在管理员 PowerShell 执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File 'C:\FPS\UgsBridgeRecoveryPatch\Apply-UgsBridgeRecoveryPatch.ps1'
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File 'C:\FPS\UgsBridgeRecoveryPatch\Test-UgsPvpBridge-v2.ps1'
```

第一条会定向重启桥接任务、备份并原地替换 DLL/PDB；保留 State、密钥、原运行时及 Unity 专服。安装前结束当前匹配测试，临时队伍将在重启时清空。脚本核实任务工作目录、精确 App 进程与 5080 归属，等待旧进程退出；不强杀未确认的程序、不启动第二个 worker、不手写云端租约。成功的本机健康检查仍须由第二条命令的三次云端采样和游戏建队确认。

本地验证包括 worker 17 场景/133 断言、HTTP/REST 与真实回环 HTTP 70 断言，以及使用原 .NET 10.0.8 完整 App 副本仅换 DLL/PDB 的实际启动。补丁更新器另以隔离假任务/假进程边界测试；未把这些本地结果称为已在用户雨云安装完成。

### 启动时 TLS 重置导致进程退出（2026-10-29）

随后实际服务器证据：旧恢复补丁 DLL 哈希已匹配，但任务为 Ready、5080 无响应。最新 .NET Runtime 事件（2026-10-29 15:09:32）显示 `GetPrivateAsync → InitializeAsync → StartAsync` 的 TLS 握手遇到 HttpRequestException / SocketException 10054，异常导致进程退出。这个事件解释当前进程缺失，不足以认定此前长时间停更的具体原因或外部 TLS 重置来源。

首次获取租约移入 `BackgroundService.ExecuteAsync` 的独立初始化循环。只对临时网络异常、非停机超时、HTTP 408/429/5xx 每隔 5 秒重试；权限拒绝、活跃其他 epoch、CAS 冲突和损坏租约继续作为不可恢复失败。每次重新 GET，使用最新 writeLock，保留同一实例的 epoch；已进入轮询后失去所有权不会重新抢占。未确认心跳期间 Kestrel 可响应 `/health` HTTP 503，确认成功才转 200。停止时先使健康失效，取消请求与退避，防止迟到响应重新发布就绪。

新交付包是 `Builds/UgsPvp/UgsBridgeStartupRecoveryPatch.zip`，与旧恢复包区分。完整解压到雨云 `C:\FPS\UgsBridgeStartupRecoveryPatch`，运行随包 `Apply-UgsBridgeRecoveryPatch.ps1`，随后运行 `Test-UgsPvpBridge-v2.ps1`。更新器支持 Ready 且无进程的现场，保留 State/凭据，只备份更新 DLL/PDB。健康暂未就绪时不会回滚或停止自动重试；持续连接失败时不能据进程存活判定匹配可用。

旧码已在真实 StartAsync 路径复现首次 TLS 10054 退出；新码 worker 24 场景/176 断言、HTTP/REST 74 断言（含真实 Host/Kestrel 首次失败 503 → 同实例重试 200）、更新器 WinPS5.1 隔离 29 场景通过。原 10.0.8 自包含程序另做隔离启动验证。本地证据不代表用户服务器已安装新包或实际 TLS 链路已经恢复。

### 雨云出站 TLS 兼容补丁（2026-10-20）

实际联调中，Cloud Save 的 TCP 443 可达，但同一雨云服务器的默认 TLS 请求返回 `HTTP=000 / curl exit=35`；限定 TLS 1.2 后返回 `HTTP=404 / exit=0`。404 来自无凭据的根路径探测，只证明 HTTPS 握手走通，不代表账号权限或匹配业务通过。最新程序仅将 UGS 专用 HttpClient 限定为 TLS 1.2，继续执行正常证书和主机名验证。

已安装旧版自包含 .NET 10.0.8 bridge 的服务器，使用 `Builds/UgsPvp/UgsTls12Patch.zip`。将其直接解压到 `C:\FPS\UgsTls12Patch`，管理员 PowerShell 执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File 'C:\FPS\UgsTls12Patch\Apply-UgsTls12Patch.ps1'
```

补丁校验程序文件、备份旧 DLL/PDB、停止指定 bridge 任务后更新两个程序文件，再启动并检查 `/health`。已有 App 中的 .NET 运行时保持不变；State、服务账号配置、用户存档和游戏专服不在更新范围内。完成以雨云实际输出 `Health OK` 为准，随后仍需两客户端匹配进服验证。

本地已完成 51 项接口回归，并在原 .NET 10.0.8 自包含目录的隔离副本中替换 DLL/PDB、启动并取得健康响应。本地无凭据 UGS TLS 探针被网络沙箱阻止，不能把该探针作为实际云端握手通过的证据。

用两个新版客户端依次验证注册登录、GameStart 档案加载、创建与加入队伍、准备、匹配、进服、换装、开局、返回大厅。客户端只填写 UGS 项目环境和脚本名称，不填写雨云账号 HTTP 地址。

worker 重启会更新 epoch，旧 pending 命令和票据不可继续使用，玩家需要重试或重新匹配。异常重启后可能需要等待旧 15 秒 lease 到期。不要同时启动两个 worker。

更新安装包前先手动停止并检查所属任务与进程，保留独立 State 和密钥配置。本安装器不会自动覆盖已有安装；选择新的程序与 State 目录，并在确认旧服务已经停用后，用受限的 `-ExistingConfiguration` 复用配置。不要在服务运行中删除配置文件，也不要把它复制回开发工程。

本说明与安装器的本地验证不代表已在真实雨云或真实 UGS 账号完成部署；计划任务、ACL 和 UGS 出站请求必须在实际 Windows Server 环境核实。

已完成的后端本地验证包括：实际 worker、PartyStore 和票据逻辑的 14 个场景、116 项断言；Cloud Save REST 替代网络边界与真实回环 HTTP 的 51 项断言；原 SelfHosted HTTP 和组队规则的 74 项回归。前两组覆盖匹配名单、单次票据、档案版本变更、写锁冲突、过期与重启隔离、响应大小和专服接口拒绝公网来源。安装器另通过 PowerShell 5.1 语法及 10 项隔离预检；没有实际建立系统任务或改 ACL。

此 bridge 通过轮询传递命令，默认每秒扫描 mailbox、每 5 秒续租；不是实时推送，命令有效期为 30 秒。部署时先用两个客户端验收，再增加玩家数量，并关注 UGS 请求额度、服务限速和雨云到 UGS 的延迟。无法读取权威档案时会拒绝相关入场或取消该队伍准备，不会接收客户端自报身份、外观或阵营来绕过校验。
