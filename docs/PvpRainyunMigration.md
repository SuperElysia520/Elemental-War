# PvP 专用服务器迁移到雨云

状态：雨云实例曾显示公网 IPv4 `114.66.59.53` 与「宝塔面板10.0 [Debian12/LNMP]」；用户随后报告已能通过远程桌面连接。Windows 客户端和单房间专用服务器已在 `Builds/RainyunTrial/` 重新构建，尚待核对重装后系统与 IP、上传服务端及验证公网联机。

## 当前实现

- PvP 使用 Unity Netcode for GameObjects + Unity Transport，服务器为 Windows Dedicated Server，每个进程承载一个房间，最多 10 人。
- 试运行配置已将房间数改为 1。游戏端口为 **UDP 7777**，房间查询端口为 **UDP 48777**（`LanDiscovery.DedicatedDiscoveryPortOffset = 41000`）。PvE 的 UDP 47777 是局域网广播端口，本次云迁移无需开放。
- `Assets/Scenes/GameStart.unity` 的 `LobbyManager.dedicatedServerAddress` 已改为雨云公网 IPv4 `114.66.59.53`，并已生成新版客户端。旧客户端仍会访问阿里云旧地址，试运行必须使用 `Builds/RainyunTrial/Client/` 中的新版。
- 项目没有发现阿里云 SDK、数据库或对象存储依赖；房间状态保存在服务器进程内，切换时正在进行的对局会中断。

## 购买与实例准备

当前实例截图显示 **2 vCPU、2 GB 内存、约 30 GB 系统盘、10 Mbps 带宽、宁波节点**。这低于下文建议的 Windows 单房间试运行起点；如控制台不提供 Windows Server 2022 镜像，或重装后系统空间、内存紧张，先按雨云套餐管理页面升级到至少 4 GB 内存和 50 GB 系统盘，再进行游戏负载测试。不要把「公网 IP 地址」旁的「切换 / 丢弃」当作更换系统入口，以免改变客户端已配置的 IP。

2026-09-25 的首次实例截图显示 Debian 12，故当时 Windows 远程桌面无法连接；截图中的 `root` 也是 Linux 账号。用户确认系统盘无须保留数据，随后报告已能通过远程桌面连接；仍需在控制台复核实际系统与公网 IP。雨云文档规定 Linux 用 SSH 或 VNC，Windows 用远程桌面；重装会清空系统盘并恢复默认设置。[雨云更换/重装系统](https://www.rainyun.com/docs/products/rcs/manual/system)、[雨云远程连接](https://www.rainyun.com/docs/products/rcs/practice/connect)

1. 选择雨云 **RCS 云服务器、Windows Server、独立公网 IPv4**。本机仅安装了 Unity Windows Dedicated Server 构建模块；Linux 迁移还需安装 Linux 构建模块并重新出包。独立 IP 可直接使用现有固定端口；雨云共享 IP/NAT 的外网端口范围为 10000–60000，无法原样暴露游戏端口 7777，且会引入端口映射改造。[雨云 RCS 系统说明](https://www.rainyun.com/docs/products/rcs/overview)、[公网 IP/NAT 说明](https://www.rainyun.com/docs/products/rcs/manual/ip)
2. 单房间试运行可先按 **2 核 CPU、4 GB 内存、50 GB SSD 系统盘** 选购，再依据实际 CPU、内存、延迟和流量调整；这是试运行起点，不代表已测得 10 人满房容量。雨云旧版文档给出的 Windows **2 GB** 仅是系统选购下限；其 SSD 默认 30 GB，而微软把 Windows Server 的 **32 GB** 系统分区称为安装最低值，因此建议在下单时增加磁盘空间。[雨云 Windows 内存说明](https://www.rainyun.com/docs/rcs/buy/rcs)、[雨云磁盘选项](https://www.rainyun.com/docs/products/rcs/buy/rcs)、[微软系统要求](https://learn.microsoft.com/en-us/windows-server/get-started/hardware-requirements)
3. 机房选靠近主要玩家的地区；确认购买页的带宽/流量模式及超额限速规则。尽量先使用可调整的短期实例，完成实际联机负载测试。[雨云流量说明](https://www.rainyun.com/docs/products/rcs/manual/traffic)
4. 云安全组如启用白名单，放行入站 **UDP 7777、48777**，并为远程维护按需放行 RDP **TCP 3389**；Windows Defender 防火墙也放行两条 UDP 规则。只开放需要的端口，不关闭整个防火墙。[雨云安全组说明](https://www.rainyun.com/docs/products/rcs/manual/security-groups)

## 从同一份项目重新出包

1. 在 Unity 中核对 `Assets/Scenes/GameStart.unity` 的 `LobbyManager`：**Dedicated Server Address = 114.66.59.53**、**Dedicated Server Port = 7777**、**Dedicated Server Room Count = 1**。如果此场景在修改前已打开，先让 Unity 重新导入磁盘上的版本，再保存场景；不要把旧的未保存场景覆盖到磁盘。
2. 使用项目的 Unity **2022.3.62f3c1**，执行 **Tools → PVP Dedicated Server → Build Windows Client**。新客户端在 `Builds/RainyunTrial/Client/`，构建器明确包含 `GameStart`、`GamePVP` 和 `GamePVE` 三个场景。
3. 执行 **Tools → PVP Dedicated Server → Build Windows Server**。新服务器在 `Builds/RainyunTrial/Server/`，同目录生成 `Start_1_PVP_Server.bat` 和 `Stop_1_PVP_Server.bat`。此试运行目录与旧的五房间构建分开。
4. 记录客户端和服务器的版本号（当前 `Application.version` 为 `0.1.0`），确认二者来自同一份源码。版本检查只比较这个字符串，不能替代同源构建验证。

## 本地出包结果

- 新客户端完整目录：`Builds/RainyunTrial/Client/`；分发压缩包：`Builds/RainyunTrial/Rainyun_PVP_Client_Windows.zip`。客户端场景数据中有新 IP `114.66.59.53`，没有旧阿里云 IP。
- 新服务器完整目录：`Builds/RainyunTrial/Server/`；上传压缩包：`Builds/RainyunTrial/Rainyun_PVP_Server_Windows_1Room.zip`。压缩包已排除 Unity 标记为 `DoNotShip` 的调试信息目录。
- 本机以 `-port 17777` 启动新版服务器，日志确认游戏监听 `0.0.0.0:17777`，状态查询监听 `0.0.0.0:58777`；发送房间查询报文后收到 `gamePort=17777`、`canCreate=true`、`serverVersion=0.1.0`、`0/10` 人的响应。这是本机验证；雨云公网连接仍需部署后验证。
- 本机还执行了压缩包对应的 `Start_1_PVP_Server.bat`：脚本返回后服务器进程仍在运行，日志确认游戏监听 `0.0.0.0:7777`、查询监听 `0.0.0.0:48777`；测试结束后已停止该进程。
- 编辑器若仍停留在 Dedicated Server 子目标，原 `DedicatedServerBootstrap` 会因 `UNITY_SERVER` 在 Play 模式自动启动服务器，导致在同一编辑器中点击创建/加入 PVP 时被 `NetworkManager.IsListening` 拒绝，提示「当前已经在一个联机房间中」。已将自动启动条件限定为 `UNITY_SERVER && !UNITY_EDITOR`：编辑器默认作为客户端，显式传入 `-dedicatedPvpServer` 才模拟服务端；正式 Server 和 Client 分别维持原有启动分支。Unity 脚本程序集已重新编译，`ShouldRunDedicatedServer()` 不再是无条件返回 true；用户已在编辑器 Play 中成功创建 PVP 房间。
- 服务器压缩包 SHA-256：`5DD2DEB2BD0052BCFC2724024D6F3B5C2ADFA4755A246504558062F67D2EB7AF`；客户端压缩包 SHA-256：`FFCAFAD2589C2BBC9F498F68C1CF717B666D533F3C465FE606DFABF4098D76C7`。

## 部署、验收与切换

1. 用户已能远程桌面登录。先确认控制台「系统」栏为 Windows Server 2022，且远程桌面所用 IP 仍为 `114.66.59.53`；若 IP 发生变化，需重新构建写入新 IP 的客户端。可通过 RDP 文件复制或磁盘重定向，把 `Rainyun_PVP_Server_Windows_1Room.zip` 复制到服务器，解压到例如 `C:\FPS-PvP\`。解压后该目录内应有 `FPS_PVP_Server.exe`、`Start_1_PVP_Server.bat`、`FPS_PVP_Server_Data/`、`UnityPlayer.dll`、`MonoBleedingEdge/` 等配套文件；不要只复制 `.exe`。[雨云远程连接说明](https://www.rainyun.com/docs/products/rcs/practice/connect)、[磁盘重定向上传说明](https://www.rainyun.com/docs/rgs/practice/mstsc/)
2. 如果雨云安全组或云防火墙启用了白名单，应允许入站 **UDP 7777、48777**；雨云文档说明默认无安全组、云防火墙为无规则黑名单时不拦截端口，无须为此新建规则。在服务器上以管理员身份打开 PowerShell，执行以下 Windows Defender 防火墙规则：[雨云安全组说明](https://www.rainyun.com/docs/products/rcs/manual/security-groups)

   ```powershell
   New-NetFirewallRule -DisplayName "FPS PvP Game UDP 7777" -Direction Inbound -Action Allow -Protocol UDP -LocalPort 7777
   New-NetFirewallRule -DisplayName "FPS PvP Discovery UDP 48777" -Direction Inbound -Action Allow -Protocol UDP -LocalPort 48777
   ```

3. 在解压目录运行 `Start_1_PVP_Server.bat`。查看 `server_7777.log`，确认游戏端口监听 `0.0.0.0:7777`、查询端口监听 `0.0.0.0:48777`，且没有启动失败。服务器当前脚本没有开机自启或崩溃自动重启，试运行期间需留意进程状态。
4. 在**服务器以外的网络**启动新版客户端：刷新 PvP 房间列表，应看到 1 个空闲服务器；创建房间后再用第二个客户端加入，进入 `GamePVP`，验证移动、战斗、离开房间以及服务器恢复空闲。若客户端显示版本不一致，重新检查双方构建来源与版本号。
5. 完成公网联机验收后分发新版客户端，再停用阿里云服务器。切换前保留旧实例和旧客户端作为回退手段；正在进行的房间不会迁移。

## 尚待确认

- 用户报告远程桌面已可连接，仍需核对控制台重装后的系统、登录目标 IP 是否仍为 `114.66.59.53`。旧截图显示实例为 2 vCPU、2 GB 内存、约 30 GB 系统盘、10 Mbps，仍需按控制台核对详细套餐和安全组。
- 雨云公网的实际双客户端联机结果。
