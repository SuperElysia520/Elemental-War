# PVP 账号与云档案服务

实施日期：2026-09-26。账号服务实现、本机 HTTP 集成验证及真实 Unity 双客户端功能联调已完成；已按用户确认的 Windows Server 2022、公网 IP `114.66.59.53` 准备无域名 HTTPS 部署脚本。尚未连接雨云、安装服务器运行时、申请证书或执行远程部署；公网连通与跨电脑客户端验收仍需部署后进行。账号服务不依赖 PVE。

## 实现范围

- 用户名/密码注册登录；用户名为 3–24 位字母、数字、下划线，不区分大小写，保留注册时的显示大小写；密码为 10–128 字符。
- 密码由 ASP.NET Core Identity `PasswordHasher` 生成带独立随机盐的版本化 PBKDF2 哈希（Identity V3、210,000 次）；不保存原始密码。
- 32 字节随机登录令牌，默认有效期 12 小时，每账号最多 5 个有效会话；服务端仅保存 SHA-256 令牌摘要，注销立即吊销。Unity 令牌只在内存，关闭游戏后重新登录。
- 角色外观及武器/瞄具档案绑定稳定 `accountId`，服务端验证值域与兼容性。`revision` 乐观并发保护：其他设备更新后，旧档案保存返回 409，必须重新加载再编辑。
- 客户端通过账号 HTTPS 接口领取 60 秒有效的一次性入场票据，NGO 连接负载只携带该票据。游戏服兑换成功才取得可信账号和档案；重复兑换、退出后的票据和重新领取前的旧票据均失败。服务重启后尚未兑换的票据失效，重新加入即可。
- 注册和登录共享每源 IP 每分钟 10 次限制；所有接口每源 IP 每分钟 180 次。429 有 `Retry-After: 60`。注册/登录负载与授权头不写应用日志。

## 本地启动与验证

需要 .NET 10 SDK；正式服务只需要当前受支持且已更新安全补丁的 .NET 10 ASP.NET Core Runtime，或发布为对应服务器平台的独立可执行程序。不引用外部 NuGet 包。

在项目根目录执行：

```powershell
dotnet restore Server/AccountService/AccountService.csproj --configfile Server/AccountService/NuGet.Config
dotnet build Server/AccountService/AccountService.csproj --no-restore
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project Server/AccountService/AccountService.csproj --no-build
```

默认监听 `http://127.0.0.1:5080`。仅 Development 环境允许注册/登录等客户端接口通过回环 HTTP 使用；网络上的 HTTP 请求始终被拒绝。若当前工具沙盒不能读取个人 NuGet 配置，可仅在该命令进程临时设置 `APPDATA` 为工作区内的测试目录，再使用项目内 `NuGet.Config`，无需修改个人配置或安装全局包。

Unity 资源 `Assets/Scripts/Account/Resources/AccountServiceSettings.asset` 的 `publicBaseUrl` 默认留空，UI 会显示“账号云服务尚未配置”，不会声称云存档成功。仅本机联调时可临时填入 `http://127.0.0.1:5080`，上线应改成实际 HTTPS 地址。`serverBaseUrl` 默认回环地址；Dedicated Server 可用环境变量 `FPS_ACCOUNT_SERVER_URL` 覆盖。

本机多客户端测试也可以在启动 Unity Editor/客户端的 PowerShell 进程中设置 `$env:FPS_ACCOUNT_CLIENT_URL='http://127.0.0.1:5080'`，再从同一个进程启动客户端，使两个进程都访问真实账号服务；无需更改生产资源。该覆盖仍只接受 HTTPS 或回环 HTTP，不绕过注册登录。Unity 的 Player Settings 还必须允许开发环境 HTTP；若项目完全禁止 HTTP，请用 HTTPS 地址或仅针对 Development 构建允许 HTTP。

已准备 Windows 上由 .NET Runtime 承载的 Release 发布目录：`Server/AccountService/publish/WindowsServer2022/`，压缩包为 `Server/AccountService/publish/AccountService-WindowsServer2022.zip`（生成产物已忽略）。先前 `WindowsFrameworkDependent/` 目录保留用于进行中的本机联调，避免覆盖正在运行的 DLL。其中 `start-account-service.ps1` 要求显式指定独立数据目录。例如本机真实服务测试：

```powershell
& ./Server/AccountService/publish/WindowsFrameworkDependent/start-account-service.ps1 -DataDirectory E:/Unity资产/FPS/Server/AccountService/data/local-playtest -Environment Development
```

该命令前台运行服务，按 Ctrl+C 停止，档案保留在指定目录。生产部署使用下文 Windows IP HTTPS 流程；基本启动脚本本身不会申请证书或开启公网 HTTP。复现发布命令：`dotnet publish Server/AccountService/AccountService.csproj -c Release --no-restore -p:UseAppHost=false -o Server/AccountService/publish/WindowsServer2022`。

集成测试：

```powershell
dotnet restore Server/AccountService/Tests/AccountService.IntegrationTests.csproj --configfile Server/AccountService/NuGet.Config
dotnet run --project Server/AccountService/Tests/AccountService.IntegrationTests.csproj --no-restore -- E:/Unity资产/FPS/Server/AccountService
```

测试会启动独立本机进程及临时端口，不修改正式数据库。`Server/AccountService/test-results/` 下保存报告、测试数据库和不含凭证的运行日志；这些目录由局部 `.gitignore` 排除。验证覆盖注册/重名/错误密码、权限、档案兼容性、并发冲突、存储失败不假报成功、一次性票据、注销、令牌到期、进程重启持久化、登录限速及生产明文拒绝。

## 接口

成功返回 JSON；失败为 `{ "code": "...", "message": "中文可显示原因" }`。除注册、登录、票据兑换和 `/health` 外，接口需要 `Authorization: Bearer <accessToken>`。

| 方法与路径 | 请求 | 成功响应 |
| --- | --- | --- |
| POST `/api/auth/register` | `{username,password}` | `{accessToken,expiresAtUnixSeconds,profile}` |
| POST `/api/auth/login` | `{username,password}` | 同注册 |
| POST `/api/auth/logout` | 无正文 | 204 |
| POST `/api/auth/validate` | 无正文 | profile；仅供确有需要的服务端验证 |
| GET `/api/profile` | 无正文 | profile |
| PUT `/api/profile` | `{revision,appearance,loadout}` | 保存后 profile |
| POST `/api/game-ticket` | 无正文 | `{ticket,expiresAtUnixSeconds}` |
| POST `/api/game-ticket/redeem` | `{ticket}` | profile；票据一次性消费 |
| GET `/health` | 无正文 | `{status:"ok"}` |

`profile` 字段与 Unity `PlayerProfileData` 一致：`accountId`、`username`、`revision`、`appearance`、`loadout`。`appearance` 为 `outfitId` 0–2、`faceId` 0–4、`clothColorId` 0–4、`hairColorId` 0–3、`hat`、`accessories`；服装 2 无配件，`accessories` 必须为 false。`loadout` 为 `primaryWeaponId` 0（步枪）或 2（狙击枪）、`primarySightId` 0–5、`sidearmSightId` 0 或 3。没有客户端设置 `accountId` 或用户名来访问他人档案的接口。

Unity 接入：`AccountClient.EnsureInstance()`，调用 `Register/Login/Logout/RefreshProfile/SaveProfile`；`StateChanged` 用于忙碌/错误提示，`ProfileChanged` 用于刷新外观。`RequestGameTicket(Action<string,string>)` 的两个回调参数是票据和错误信息。游戏服用 `StartCoroutine(AccountClient.RedeemServerTicket(ticket, (profile,error) => ...))` 并在取得可信 profile 后批准连接。不要把 `AccessToken` 写入 NGO 数据、PlayerPrefs、日志或文件。

## 持久化与部署约束

首版使用单服务进程的原子 JSON 快照，不需要另外维护数据库进程。每次修改先刷盘临时文件，再在同目录原子替换；只有成功替换后才更新内存状态。进程独占锁拒绝两个实例写同一文件。数据库缺损、格式不支持或记录无效时启动失败，不自动覆盖数据。此实现适合当前试运行的小规模账户；大量账号/多实例时应迁移为数据库，并保持现有 API 与版本冲突规则。

已知服务器系统为 Windows Server 2022；服务器运行时、实际端口占用与公网可达性尚未核实。本次没有修改雨云环境，无域名部署采用下一节的 IP 证书。

1. 发布：`dotnet publish Server/AccountService/AccountService.csproj -c Release -o <发布目录>`。部署更新服务程序时，档案文件放在独立持久目录；通过环境变量 `AccountStorePath` 指定绝对路径。不要将数据目录放在会被发布覆盖的文件夹。
2. 服务进程以专用低权限系统账号运行，仅它能读写数据目录。备份 `accounts.json` 到受控位置；停止服务后恢复备份，先验证格式再恢复上线。文件包含密码哈希和个人档案，不作为下载资源公开。
3. 默认绑定回环地址，前面使用已配置证书的 HTTPS 反向代理；也可直接配置 Kestrel HTTPS。生产环境用 `ASPNETCORE_ENVIRONMENT=Production`。客户端接口拒绝 HTTP，不依赖重定向保护密码。
4. 反向代理仅在明确设置 `TrustedProxyAddresses` 后启用转发头处理，例如同机代理环境变量 `TrustedProxyAddresses__0=127.0.0.1`。代理必须覆盖 `X-Forwarded-For` 和 `X-Forwarded-Proto`；不将 5080 暴露公网。不信任任意来源的转发头。
5. 同机 Dedicated Server 可通过回环 HTTP 调用 `/api/game-ticket/redeem` 和 `/api/auth/validate`；此例外仅针对这两个内部端点。跨机服务器必须配置 HTTPS。
6. 生产 HTTPS 地址填写进 Unity `publicBaseUrl`，构建匹配版本的客户端和 Dedicated Server，再验证真实两设备登录、云保存、加入和重生流程。

尚未实现密码找回、更改密码、账号删除、邮件验证、封禁后台和跨区多实例；这些不是已确认首版范围。Unity 账号请求、档案同步、异步票据审批及退出对局后的账号保留已通过本机真实双客户端联调，证据见下文。一次性短票据避免长期账号令牌通过游戏 UDP 暴露；它不等同于对游戏传输本身加密。

实现依据：微软官方 [Identity 密码哈希指导](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/consumer-apis/password-hashing)、[ASP.NET Core 限速](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit)、[可信代理及转发头](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer)。

## Windows Server 2022：通过公网 IP 部署 HTTPS

这是供 RDP 登录服务器后审核执行的部署包，**目前未部署**。不需要购买域名：Let's Encrypt 已支持 IPv4/IPv6 地址证书，必须使用 `shortlived` 配置，有效期为 160 小时。因此这里同时准备自动续期，不能只签发一次后放置不管。[Let's Encrypt 官方公告](https://letsencrypt.org/2026/01/15/6day-and-ip-general-availability)

服务直接通过 Kestrel 提供 `https://114.66.59.53:443`，另保留 `http://127.0.0.1:5080` 给同机游戏服。TCP 80 仅在签发/续期时临时提供 HTTP-01 验证，不承载账号密码接口。证书使用完整 PEM 链和 PEM 私钥，Kestrel 按 `Certificate:Path` / `Certificate:KeyPath` 读取。[Kestrel 官方配置](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-10.0)

### 准备文件与身份

1. 将 `publish/WindowsServer2022/` 的内容（或对应 ZIP 包解压内容）上传到固定程序目录，例如 `C:\FpsAccount\App`。证书、账户档案、ACME 状态必须放在程序目录之外；后续示例采用实际运行账号自己的 `%LOCALAPPDATA%\FpsAccountState`。不要上传本地 `test-results` 或测试数据库。
2. 在服务器安装微软当前 .NET 10 **ASP.NET Core Runtime / Windows x64** 安全更新版本，检查 `dotnet --list-runtimes` 中有 `Microsoft.AspNetCore.App 10.x`。[官方下载](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
3. 选定一个 Windows 运行账号，建议专用普通账号。此账号需要能读取程序目录、写入自己的 State 目录，并允许运行计划任务。证书初始化、后续续期必须是同一账号；管理员负责防火墙和任务注册。Posh-ACME 的敏感配置与 Windows 用户/计算机绑定，切换身份不能直接复用加密设置。[配置目录说明](https://poshac.me/docs/v4/Guides/Using-an-Alternate-Config-Location/)
4. 以下脚本要求 **Windows PowerShell 5.1**，不是 PowerShell 7。需要时用 `powershell.exe -NoProfile -ExecutionPolicy RemoteSigned` 启动本次操作窗口；只对这个进程生效，不修改机器执行策略。若组策略要求签名，应按服务器策略签署脚本，不修改组策略。下载的脚本需先审核，再按当地执行策略解除文件下载标记或签名。

在选定运行账号的 Windows PowerShell 中安装证书工具。此命令会走 PowerShell Gallery 的正常安装/信任提示，不下载并直接执行远程安装脚本：

```powershell
Install-Module Posh-ACME -MinimumVersion 4.34.0 -Scope CurrentUser
```

要求 4.34.0 或兼容更新版，包含已发布的 IP 证书与续期修复；脚本显式检查 CA 提供 `shortlived`，以免工具忽略不支持的 Profile 后签发失败。签发参数使用 `-Plugin WebSelfHost -Profile shortlived`，每次续期重新生成私钥，并为额外 PFX 文件设置随机密码。[版本记录](https://github.com/rmbolger/Posh-ACME/blob/main/CHANGELOG.md)、[签发参数](https://poshac.me/docs/v4/Functions/New-PACertificate/)、[Profile 查询](https://poshac.me/docs/v4/Functions/Get-PAProfile/)

### 打通证书验证与 HTTPS 端口

在管理员 Windows PowerShell 中运行，`WindowsAccount` 必须改成上一步的真实运行账号：

```powershell
& C:\FpsAccount\App\Deployment\Prepare-WindowsIpHttpsHost.ps1 -WindowsAccount '服务器计算机名\运行账号'
```

它只添加指定的 TCP 80/443 入站规则，并给该账号保留 `http://+:80/.well-known/acme-challenge/`。雨云控制台的防火墙/安全组也需放行 TCP 80/443。不要对公网开放 5080。若已有 IIS、代理或其他进程使用 80/443，应先检查端口和 URL 保留；脚本遇到冲突不会删除别人的配置。HTTP-01 验证需要外网实际访问 80，不能仅检查 RDP 能否连接。[WebSelfHost 官方实现与参数](https://github.com/rmbolger/Posh-ACME/blob/main/Posh-ACME/Plugins/WebSelfHost.ps1)

### 先验证，再签发正式证书

回到选定运行账号的 Windows PowerShell，先运行测试签发：

```powershell
$fpsState = Join-Path $env:LOCALAPPDATA 'FpsAccountState'
& C:\FpsAccount\App\Deployment\Initialize-WindowsIpHttps.ps1 -PublishDirectory C:\FpsAccount\App -StateDirectory $fpsState -PublicIp 114.66.59.53
```

首次连接测试 CA 时，脚本显示实时服务条款链接，必须由操作者阅读并手动输入 `I AGREE` 后才创建 ACME 账号；直接回车会停止。脚本没有预先替用户接受条款。默认只使用 Let's Encrypt 测试环境，测试证书不受客户端信任，也不会配置给账号服务。[ACME 账号与条款参数](https://poshac.me/docs/v4/Functions/New-PAAccount/)

测试通过后，在同一窗口执行正式签发；正式 CA 独立显示条款并要求确认：

```powershell
& C:\FpsAccount\App\Deployment\Initialize-WindowsIpHttps.ps1 -PublishDirectory C:\FpsAccount\App -StateDirectory $fpsState -PublicIp 114.66.59.53 -Production
```

脚本将 State 目录权限限制为运行账号、SYSTEM、管理员，子文件继承权限。`tls/private.key` 是供 Kestrel 使用的无口令 PEM 私钥，依赖这些权限保护；不放入仓库、共享下载目录、客户端或日志。测试与正式 ACME 状态分开保存；已有正式配置时拒绝重新初始化，后续使用续期脚本。该步骤仍不启动服务。

### 注册后台运行与自动续期

记下初始化输出中的 State 绝对路径，然后在管理员 Windows PowerShell 中运行（示例账号路径需替换）：

```powershell
& C:\FpsAccount\App\Deployment\Register-WindowsIpHttpsTasks.ps1 -StateDirectory 'C:\Users\运行账号\AppData\Local\FpsAccountState'
```

注册器交互请求证书所属 Windows 账号的密码，交给 Windows 计划任务保管，不写进脚本、任务命令行或配置 JSON。两个任务均以该账号的普通权限执行：`FPS-AccountService` 随系统启动运行实际 `dotnet.exe`，失败可重启；`FPS-AccountService-Certificate` 每 6 小时检查续期，首次检查在注册后约 5 分钟。已存在同名任务时拒绝静默覆盖。[微软计划任务注册文档](https://learn.microsoft.com/en-us/powershell/module/scheduledtasks/register-scheduledtask?view=windowsserver2025-ps)

服务首次启动后，注册器连接本机 443，同时按公网 IP 校验正常 Windows 信任链、证书指纹及 `/health`。此检查没有忽略证书错误。随后应从另一台电脑访问 `https://114.66.59.53/health` 并完成真实注册、云档案保存和重新登录，再配置/发布正式 Unity 客户端；仅服务器自身检查成功不代表云防火墙已经通。

续期脚本使用 `Submit-Renewal` 的正常续期窗口，周期运行不使用 `-Force`。未产生新证书时不打断服务；换证时保留旧证书对，停止服务、更换证书、启动并验证 TLS；新证书启动失败时恢复旧证书并报错。进程重新启动不会丢失账号档案或登录会话，但未消费的入场票据会失效，玩家可重新加入。[续期参数与行为](https://poshac.me/docs/v4/Functions/Submit-Renewal/)

运维检查入口：

```powershell
Get-ScheduledTaskInfo -TaskName FPS-AccountService
Get-ScheduledTaskInfo -TaskName FPS-AccountService-Certificate
Get-Content -Raw -Encoding UTF8 -LiteralPath 'C:\Users\运行账号\AppData\Local\FpsAccountState\renewal-status.json'
```

`renewal-status.json` 记录 `unchanged`、`renewed` 或 `failed`、检查时间及证书到期时间。应接入现有监控或定期查看：状态过 12 小时未更新、任务失败或证书不足 24 小时均应排查，不能依靠证书到期后再修复。服务器重启后仍需检查任务恢复；修改 Windows 账号密码后更新两个任务保存的凭据。保留 TCP 80 的云端放行供后续验证。

程序更新时先停止 `FPS-AccountService`，替换 `App` 内程序文件，然后启动并重新检查 `/health`。State 目录保留，尤其 `data/accounts.json`、`acme-production`、`tls`、`deployment.json`；不要从本地测试目录覆盖它们。账号数据库单独备份并限制访问，证书私钥及 ACME 账号状态也只进入受控备份。若更换 Windows 运行账号或服务器，应重新规划证书身份和 ACL，再迁移账号数据。

### 已验证与尚待服务器验证

本机真实双客户端、独立 PVP 专服及账号服务的联调批次 `88039dc3`：host/join 均为 `PASS / complete`，最终场景均为 `GameStart`，两端 `runtimeErrors` 均为 0。报告位于 `tmp/PvpMigration/Smoke/88039dc3/host-report.json` 与 `join-report.json`。已验证真实登录、外观/配装云保存及重新读取、一次性票据审批、建房/加入/选队/开局、双方外观配装同步、射击弹药/换弹/切枪、击杀重生，以及退出返回大厅后保留账号与档案。这是本机自动功能与联机流程验证，不代表全部画面与人工操作体验已经验收。

所有部署脚本已完成 PowerShell 语法解析。本机 `Tests/DeploymentScripts.Tests.ps1` 在 Windows PowerShell 5.1 下通过 7 项模拟测试：未到续期窗口不重启、正常替换、数据保留、启动失败返回错误、旧证书恢复、锁释放、错误 IP SAN 拒绝。ACME、计划任务和 TLS 激活在此测试中使用明确的替身，**并未请求公网证书、修改本机防火墙或创建计划任务**。

真实的证书签发、Windows Server 2022 任务权限、公网 80/443、生产 HTTPS 和远程游戏客户端仍需在服务器按上述步骤验收。部署包不能作为已经上线或已经跨电脑验证的证明。

## 与最终游戏交付包一起发放

最终客户端与专服在 `Builds/RifleGirlMigration/` 中由主任务构建验收。通过后先执行 `Deployment/Prepare-GameDelivery.ps1` 生成真实 HTTPS 启动入口和配套说明，再执行 `Deployment/Package-GameDelivery.ps1 -ReleaseId '<同一交付批次>'`。默认产物位于 `Builds/RifleGirlMigration/Packages/`，包含配套客户端/专服 ZIP、账号服务 ZIP、SHA256 清单和文件清单。必须同时替换匹配的新客户端与新专服完整目录，不能混用旧 RainyunTrial 构建。

打包器会从已有账号 ZIP 生成去除 PDB 的交付副本，不修改原发布包；账号数据、证书私钥和本地测试数据不进入交付包。工具仅在指定输出目录创建或原子替换生成文件，不递归删除目录。`Tests/PackageGameDelivery.Tests.ps1` 使用临时小型替身目录验证运行文件保留、排除规则、同批说明、校验和、重复执行和路径边界，不读取或打包实际 Unity 构建。
