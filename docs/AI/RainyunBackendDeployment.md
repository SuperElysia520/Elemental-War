# 雨云账号后端：简化安装

你已有的雨云 Windows Server 2022 能运行账号后端程序。这个包包含更新后的注册登录接口和自带运行时的 `AccountService.exe`，服务器不用另装 .NET；公网能否访问还取决于节点的 HTTP 接入条件。

## 先确认节点与公网接入条件

**当前安装器使用“纯 IP + HTTP-01 证书验证”，不能作为雨云所有区域通用的上线方案。** [雨云官方 FAQ](https://www.rainyun.com/docs/products/rcs/faq) 说明：中国大陆服务器的公网网站访问需使用域名，完成备案、白名单及正确的接入信息。只放行 Windows 防火墙的 80/443，无法代替这些接入条件。账号接口使用 HTTP/HTTPS，也应先向雨云核实该节点对这类业务的要求。

| 当前条件 | 后续方式 |
| --- | --- |
| 中国大陆节点，尚无完成备案及过白的域名 | 先处理域名与雨云接入条件，不要反复申请纯 IP 证书 |
| 已有备案、过白并解析到本机的域名 | 为该域名部署 HTTPS；当前 IP 安装脚本尚未实现域名模式，不能直接把域名填进公网 IP 提示 |
| 节点确认允许纯 IP 的公网 HTTP/HTTPS，80/443 实际可达 | 可以继续下文的 IP 证书安装流程，仍需从外部验证 |

域名证书可使用 HTTP-01 或 DNS-01：前者要求公网 80 能正确返回验证内容；后者通过域名 TXT 记录验证，不要求网站开放 80，但不能用来签发当前的纯 IP 证书，也不代替平台的备案或白名单要求。[Let's Encrypt 验证方式说明](https://letsencrypt.org/docs/challenge-types/)

选择由其他平台托管账号 HTTPS 也是后续方案，但本包的游戏专服接口仍仅允许同机访问。如果保留雨云上的 Unity 专服、只把账号服务搬到另一台机器，需要先增加跨机专服鉴权等适配，不能仅修改一个地址就宣称完整匹配已可用。

## 首次安装（节点允许纯 IP HTTP 验证时）

1. 雨云控制台为这台服务器放行 **TCP 80、443**；保留原有远程桌面和游戏 UDP 端口，**不要开放 5080**。
2. 把新的 `RainyunBackend-*.zip` 传到雨云服务器。若文件属性有“解除锁定”，先勾选再解压到独立目录，例如 `C:\FpsAccount\Upload`。保留整个解压目录。
3. 远程桌面登录服务器，双击解压根目录的 **`Install-RainyunBackend.cmd`**，同意管理员提权，输入服务器公网 IPv4 地址。
4. 首次会安装 Posh-ACME 证书工具、测试证书验证、再申请正式 HTTPS 证书。阅读显示的 CA 条款，愿意接受时亲自输入 **`I AGREE`**。正式账号与测试账号会分别询问。邮箱不是必须项。
5. 创建后台任务时，输入当前 Windows 运行账号的密码。密码交给 Windows 计划任务保管，不写入脚本或命令行，也不要发到聊天里。
6. 出现绿色“安装完成”后，在**另一台电脑**打开 `https://服务器公网IP/health`，应看到 `status: ok`、`service: fps-account`、`apiVersion: 2`。

脚本默认把程序安装到 `C:\FpsAccount\App`，账号和证书放在运行账号的 `%LOCALAPPDATA%\FpsAccountState`，两者分开。证书每 6 小时自动检查续期，账号服务随服务器开机运行。需要长期保留 TCP 80 的放行供证书续期验证使用。

## Unity 中配置

在 Unity 菜单打开 **`Tools → FPS → Cloud Backend → Configure`**，将账号服务地址填为 **`https://服务器公网IP`**，不要加 `/api` 或 `/health`，保存配置。然后重新运行 Login，验证注册 → 登录 → 进入 GameStart 读取档案。构建客户端前也要保存同一地址配置；窗口保留同机专服的回环地址。

账号后端和 Unity 游戏专服是两个进程；本安装器负责前者，原游戏 UDP 端口和游戏专服仍需正常运行。同机游戏服继续访问内部 `http://127.0.0.1:5080`。

## 以后更新

上传并解压新版包，再双击相同的安装入口。已有正式部署时会读取原任务和 `deployment.json`，在原程序目录更新，先备份程序及账号文件，保留原账号数据库、证书、自动续期和 Windows 任务身份。失败时尝试恢复旧程序并重启原任务。**不要手动清空 State 目录，也不要把本地测试数据库覆盖上去。**

安装器输出本次备份目录；备份含账号哈希和档案，仅保存在权限受限的服务器目录，不作为玩家下载内容分享。没有匹配部署配置的旧目录或任务会停止安装并保留原文件，避免误覆盖。

## 上次首次安装中断时重试

如果旧版安装器在“ACME URL reservation already exists for another identity”处中断，先关闭旧安装窗口，保留服务器上的程序目录和 State 数据目录。此提示可能来自旧版对 Windows 空查询结果的误判，不需要手动删除 URL 保留项。

如果已用 `netsh http show urlacl url=http://+:80/.well-known/acme-challenge/` 确认该项查询结果为空，可以不重传文件：在**原 Windows 账号的管理员 PowerShell** 添加这条缺失授权，再双击原上传目录的安装入口继续。

```powershell
$fpsWindowsAccount = [Security.Principal.WindowsIdentity]::GetCurrent().Name
& netsh http add urlacl 'url=http://+:80/.well-known/acme-challenge/' ('user=' + $fpsWindowsAccount) listen=yes
```

这条命令只添加缺失项；如果查询并非空列表，或添加时提示已存在/拒绝访问，请保留原配置，不要删除其他程序的授权。

也可以使用修复版安装器：

- 收到 **Hotfix ZIP**：解压并覆盖**原上传包目录**中同名文件，即含 `AccountService.exe` 和 `Install-RainyunBackend.cmd` 的那一层；不要覆盖 `C:\FpsAccount\App` 或 State 目录。
- 收到**完整新版 ZIP**：解压到一个新的上传目录，使用新目录的安装入口。

重新双击上传目录的 `Install-RainyunBackend.cmd`，以原来的 Windows 账号继续。修复版会核对已安装文件是否属于完整的当前包或已知旧官方包，先备份再继续安装；无法识别的文件会保留并停止。不要为了绕过提示清空目录、修改账号数据库或删除其他程序的 URL 保留项。

## 常见失败

- `Authorization invalid`、`Error reading HTTP response body` 或 `reader size limit exceeded`：证书机构没有读到预期的短验证内容。先核实节点是否允许纯 IP HTTP、验证路径是否被重定向或返回拦截页面；若属于大陆节点接入限制，应按上面的域名路线处理，而不是反复重试正式签发。日志中出现另一个 IP 需要结合实际响应或验证记录确认，不能仅凭这个 IP 判定来源。
- `POSHACME_HOME` 目录不存在并回退默认目录：这是证书状态目录的独立问题。使用已修复为导入模块前创建 `acme-staging` / `acme-production` 目录的新脚本；修复目录本身不能解决公网验证响应错误。保留旧的默认 ACME 目录，不要自行删除账号或密钥。
- 证书验证失败：先核对公网 IP、雨云控制台 TCP 80 和 Windows 防火墙；已有 IIS 或其他程序占用 80/443 时需要先处理端口冲突，脚本不会删除其他程序配置。
- 提示脚本被阻止：检查 ZIP 的“解除锁定”，使用 Windows PowerShell 5.1；有公司组策略时遵循签名策略，安装器不修改机器执行策略。
- 账号服务尚未配置：Unity 仍未填写上面的 HTTPS 地址。
- 本机 `/health` 正常但另一台电脑打不开：公网防火墙或服务器网络尚未打通。

包已在本机准备和验证；未连接你的雨云服务器、代开端口或申请公网证书。上线成功以服务器安装输出和另一台电脑的实际登录结果为准。底层脚本及更多说明见 [AccountService.md](AccountService.md)。
