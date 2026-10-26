# LanAutoConfig — 局域网 PC 批量更名 & IP 配置工具

控制端 / 被控端拆分为 **两个独立程序**，面向机房批量装机、工位批量部署 Windows 终端。

- **技术栈**：C# / .NET 6 WinForms，SQLite（控制端日志），UDP 广播发现 + TCP 可靠传输，本地 PowerShell 修改网卡与计算机名
- **产物**：两个单文件独立 exe（内置 .NET 6 运行时 + SQLite，无需目标机安装运行时），Windows 10/11 / Server2016+
  - 控制端：`bin\master\局域网配置工具-控制端.exe`
  - 被控端：`bin\slave\局域网配置工具-被控端.exe`
  - 精简版（体积小，需目标机装 .NET 6 桌面运行时）：`bin\master-slim\局域网配置工具-控制端-精简版.exe`、`bin\slave-slim\局域网配置工具-被控端-精简版.exe`
- **通信**：UDP 48888（广播发现 + 模板推送），TCP 48889（上报 / 执行指令 / 结果回传）；UTF-8 JSON，MsgType + SessionId 会话隔离 + Magic/Check 校验
- **共享**：协议消息类、网络工具、校验器、AppConfig（端口/目录）、TcpSend 集中在 `Common` 类库，两端引用同一份实现，保证互通

---

## 工作模式（相比旧版的核心差异）

不是控制端一次性导入全部 IP 清单，而是 **下发前缀模板 → 被控端填后缀 → 自动拼接 → 上报待配置 → 控制端汇总审核 → 一键统一下发执行**。

核心关键点：**修改动作在被控端本地执行**（本地 PowerShell），不是控制端远程调用 CIM/WMI，规避远程 WMI/WinRM 防火墙、UAC、权限问题；不需要远程账号密码。

## 标准业务流程

1. 控制端填写模板（IP 前缀 / 掩码 / 网关 / DNS / 主机名前缀），点【开始发布模板】，每 2.5 秒 UDP 广播模板 + 会话 ID
2. 被控端（管理员运行）自动监听广播，收到模板
3. 被控端输入后缀编号，自动拼接预览完整主机名与 IP
   - **拼接规则：主机名 = 前缀 + 后缀原文；IP 末段 = 后缀编号 + 1**（后缀 `1` → `.2`，把 `.1` 留给网关；后缀 `002` → 主机名 `PC-ROOM01-002`、IP `.3`）
   - 末段为 0（网段地址）/ 255（广播地址）、等于网关、或后缀含非数字时直接拒绝并在界面上提示原因
4. 被控端【提交上报】，TCP 上报本机 MAC / 当前 IP / 目标配置
5. 控制端加入终端列表，自动做 IP、主机名冲突检测（冲突标红，禁止下发）
6. 管理员可手动修改单台目标主机名 / IP、删除终端、标记跳过
7. 确认后【下发执行命令】——**以控制端审核后的配置为准**，覆盖被控端本地预览值
8. 被控端本地执行修改网卡 IP/DNS、重命名计算机，回传结果
9. 控制端更新状态、写 SQLite 日志；被控端提示重启（可选自动重启，重启后计算机名完全生效）

## 使用

- **控制端**：直接运行 `局域网配置工具-控制端.exe`（无需管理员权限）。控制端界面提供「广播网卡(本机IP)」下拉框，可在多网卡机器上选择用哪块网卡广播模板（被控端会回连到该网卡 IP）
- **被控端**：直接运行 `局域网配置工具-被控端.exe`，**必须以管理员权限运行**（程序内会检测，无权限弹窗提示）
- 支持 DHCP 模式（模板勾选后，被控端不再拼接 IP，直接恢复 DHCP）
- 控制端黑名单（MAC/IP）拒绝指定终端上报

## 构建

一键构建（推荐，双击即可，日志写到仓库根目录 `build.log`）：

```
D:\ip\build.cmd
```

它等价于：

```bash
dotnet build LanAutoConfig.sln -c Release
dotnet publish Master/Master.csproj -c Release -r win-x64 --self-contained true -o bin\master
dotnet publish Slave/Slave.csproj -c Release -r win-x64 --self-contained true -o bin\slave
# 精简版（框架依赖，体积小，需目标机装 .NET 6 桌面运行时）
dotnet publish Master/Master.csproj -c Release -r win-x64 --self-contained false -p:SlimBuild=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -o bin\master-slim
dotnet publish Slave/Slave.csproj -c Release -r win-x64 --self-contained false -p:SlimBuild=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -o bin\slave-slim
```

产物（exe 名字已中文化，由 csproj 的 `PublishedExeName` + 发布后重命名目标完成）：

| 版本 | 文件 | 大小 | 目标机要求 |
|---|---|---|---|
| 完整版 | `bin\master\局域网配置工具-控制端.exe` | ~61.6 MB | 无（自带运行时） |
| 完整版 | `bin\slave\局域网配置工具-被控端.exe` | ~60.6 MB | 无 |
| 精简版 | `bin\master-slim\局域网配置工具-控制端-精简版.exe` | ~2.1 MB | 需 .NET 6 桌面运行时 |
| 精简版 | `bin\slave-slim\局域网配置工具-被控端-精简版.exe` | ~0.3 MB | 需 .NET 6 桌面运行时 |

**体积说明**：单文件自包含的 WinForms 应用体积下限就在这里（内置了整个 Windows 桌面运行时）。已做的优化：
- `EnableCompressionInSingleFile=true`（压缩，原本 ~150 MB）；
- `SatelliteResourceLanguages=en` 去掉其他语言的卫星程序集（约 -5.5 MB）；
- 不再打包 PDB 进 exe。

**裁剪（PublishTrimmed）走不通**：SDK 直接报 `NETSDK1175: Windows Forms is not supported or recommended with trimming enabled`，强行绕过会导致运行期反射失效，不建议。

**要更小只能用精简版**：它只含自己的代码 + `e_sqlite3.dll`，运行时用系统安装的 .NET 6 桌面运行时。目标机安装一次即可：

```powershell
winget install Microsoft.DotNet.DesktopRuntime.6
# 或下载：https://dotnet.microsoft.com/download/dotnet/6.0/runtime  （Desktop Runtime x64）
```


### 构建环境注意（CET / "Unknown Hard Error"）

.NET 9 起 `apphost`/`singlefilehost` 默认带 CET 兼容标记（`/CETCOMPAT`），**未打 CET 补丁的 Windows 上这类程序一启动就 FailFast**，表现为系统弹窗 `Unknown Hard Error` 或 `Fatal error. Your Windows doesn't fully support CET`。

- 本项目是 net6.0，产出的 exe **不受影响**；出问题的是构建工具链：`pwsh.exe`（PowerShell 7 基于 .NET 10）和 .NET 9/10 版 `dotnet.exe` 都会中招。
- 因此请用 **.NET 6 SDK** 构建（本机 `C:\Program Files\dotnet` 里就是 6.0.428，可以直接用），不要用 .NET 9/10 SDK；`build.cmd` 只走 `cmd.exe`，不依赖 pwsh。
- 两个 exe 工程已加 `<CETCompat>false</CETCompat>`，即使以后用 .NET 9+ SDK 重新构建，产出的 apphost 也不会带 CET 标记。
- 彻底修复本机：安装 Windows 更新；或在「Windows 安全中心 → 应用和浏览器控制 → 漏洞防护 → 程序设置」里给 `pwsh.exe` 关闭「硬件强制堆栈保护」；或直接用 Windows PowerShell 5.1。

## 目录结构

```
LanAutoConfig/
├── LanAutoConfig.sln             # 解决方案：Common + Master + Slave
├── app.ico                       # 程序图标（exe 资源 + 窗口/任务栏）
├── tools/make_icon.py            # 图标生成脚本（Pillow，改配色/文字后重跑即可）
├── Common/                       # 共享类库（两端共用的协议与工具）
│   ├── Common.csproj
│   ├── AppConfig.cs              # 端口/校验/目录常量、管理员检测
│   ├── NetMessages.cs            # MsgType 消息类 + JSON 序列化（协议）
│   ├── Models.cs                 # SlaveSessionItem / TemplateModel / OpLogRecord
│   ├── Validators.cs             # 后缀/主机名/IP/掩码校验
│   ├── NetworkHelper.cs          # 网卡枚举、本机 IPv4 / MAC
│   ├── TextLogger.cs             # 线程安全文本日志
│   └── TcpSend.cs                # TCP 发送（超时5s、重试1次）
├── Master/                       # 控制端独立程序（局域网配置工具-控制端.exe）
│   ├── Master.csproj / Program.cs
│   ├── MasterForm.cs             # 控制端 UI：模板/终端列表/日志 + 冲突 + 下发
│   ├── UdpBroadcastService.cs    # UDP 广播模板
│   ├── TcpServerService.cs       # TCP 接收上报/结果
│   ├── SessionManager.cs         # 会话与终端列表
│   ├── ConflictValidator.cs      # IP/主机名冲突
│   ├── SqliteLogStore.cs         # OpLog 表
│   └── Blacklist.cs              # MAC/IP 黑名单
├── Slave/                        # 被控端独立程序（局域网配置工具-被控端.exe）
│   ├── Slave.csproj / Program.cs
│   ├── SlaveForm.cs              # 被控端 UI：后缀输入/预览/上报/执行
│   ├── UdpTemplateListener.cs    # 监听广播、自动发现控制端
│   ├── TcpCommandListener.cs     # 接收执行指令/会话重置
│   ├── ConfigGenerator.cs        # 后缀 → 主机名/IP
│   └── LocalNetChanger.cs        # 本地 PowerShell：静态IP/DHCP/改名/重启
└── bin/
    ├── master\局域网配置工具-控制端.exe             # 完整版（自带运行时，~62 MB）
    ├── slave\局域网配置工具-被控端.exe
    ├── master-slim\局域网配置工具-控制端-精简版.exe  # 需 .NET 6 桌面运行时
    └── slave-slim\局域网配置工具-被控端-精简版.exe
```

## 数据与日志

- 控制端 SQLite：`%AppData%\LanAutoConfig\master_logs.db`，表 `OpLog`（SessionId / OpTime / Mac / SourceIp / TargetHostName / TargetIp / Result / ErrorMsg），支持导出 CSV
- 双方文本日志：`%AppData%\LanAutoConfig\logs\master.log` / `slave.log`
- 黑名单：`%AppData%\LanAutoConfig\blacklist.txt`
- **若 `%AppData%` 不可写**（受限令牌、安全软件拦截、只读漫游配置），上述路径自动回退到 **程序目录下的 `data\`**（如 `bin\master\data\logs\master.log`），启动日志里会标注 `（%AppData% 不可写，已回退到程序目录）`。

## 说明与偏离记录

- 文档约定“脚本调用 System.Management.Automation”。本实现为兼容**单文件自包含发布**（该 SDK 包体积大、含原生库，单文件发布麻烦），改为调用本地 `powershell.exe -EncodedCommand` 执行脚本，功能与结果等价（本地执行、无远程凭据）。若你确实需要改为 System.Management.Automation，告诉我即可切换。
- 修改计算机名仍需重启才完全生效；程序在执行成功后提示重启（可勾选自动重启）。
- 仅限同一局域网 / 工作组（域环境也兼容，仅改本机本地主机名，不做 AD 操作）；不做公网穿透、不做端口扫描、不自动抓全网主机。

## 协议与安全边界（重要）

- 协议本身**没有身份认证**：`Magic` 是公开常量，`Check` 只是 FNV-1a 明文哈希，`SessionId` 每 2.5 秒明文广播。
  因此本轮加固只做“来源约束”，不能替代真正的认证：
  - 被控端只接受**当前配对控制端 IP** 发来的指令（收到模板时记录，来源不符直接拒绝并写日志）；
  - 被控端校验指令里的 `LocalMac` 必须等于本机已上报的网卡 MAC；
  - 未收到模板（无会话）时拒绝一切指令。
  同网段的攻击者仍可嗅探 SessionId，若要真正防伪造，需要引入 HMAC 共享密钥 + 时间戳防重放（属于后续改造项）。

## 部署注意事项

- 防火墙需放行：入站 **UDP 48888**（模板广播）、入站 **TCP 48889**（上报/指令）。被防火墙拦截时的现象是“被控端一直收不到模板”。
- 两端都用 48889，**同一台机器不能同时运行控制端和被控端**（联调请用两台机器或虚拟机）。
- 控制端“广播网卡”选择哪块网卡，下发指令也从该网卡发出；被控端上报也绑定所选网卡，多网卡机器请确认选中的是接在同一局域网的那块。
- **双端必须一起更新**：本次协议升到 `LANAC-V2`（`SlaveReport` 增加 `IsDhcp` 且参与校验），新旧版本混用会在校验处被拒绝，日志里会写 `[丢弃] ... 校验失败`。
- 程序对 `%TEMP%` / `%AppData%` 不可写的情况做了降级：`EnableVisualStyles` 失败只是失去主题外观（日志里有 WARN），数据目录自动回退到程序目录，**不会因此起不来**。如果日志里出现这两条，说明该机器上有安全软件/受限令牌在拦截这个 exe 写用户目录，建议加白名单。
- **跨网段但同一广播域可自动互通**：被控端当前 IP 与控制端不同网段（例：被控端还在 `192.168.2.5`、控制端在 `192.168.1.7`）但两台机器接在同一交换机/VLAN 时，被控端会自动**临时**给自己网卡加一个控制端网段的地址（从 `.254` 往下找空闲地址，先 ARP 探测占用），使上报/下发/回传全部走二层直连；执行配置时该临时地址随 `Remove-NetIPAddress` 一并清除，退出或重置会话时也会主动删除。
  - 控制端**不需要管理员权限**、不需要加路由。
  - 若中间有路由器/VLAN 隔离（ARP 探不到控制端），会立即回滚临时地址并提示「不在同一广播域」，这种情况只能先把被控端接到控制端网段。
  - 说明：只给被控端加静态路由的单边方案不可行——控制端的回包仍会按自己的路由表发给网关，TCP 握手完不成。

## 本次修复记录（v1.1）

功能级（会让流程失效的）：

1. **DHCP 模式修复**：`SlaveReportMsg` 增加 `IsDhcp` 并贯通 `ApplyReport`/下发/执行。此前控制端拿不到该字段，DHCP 会走成“静态 + 空 IP”，必然失败。
2. **恢复 DHCP 真正启用 DHCP**：脚本补 `Set-NetIPInterface -Dhcp Enabled`（静态配置过的网卡 EnableDHCP=0，只删地址会掉进 169.254.x.x 失联），切换后等待并校验是否拿到地址；静态模式补 `-Dhcp Disabled`。
3. **DNS2 留空不再炸**：DNS 服务器列表按非空项拼接，空 DNS2 不再把 `''` 传给 `Set-DnsClientServerAddress`（原先会导致 IP 已改完却报失败）。
4. **多网卡上报源 IP 修正**：改用“选中网卡”的 IPv4（原来用 `GetLocalIpv4()`，多网卡机器常选到虚拟网卡/APIPA，控制端会往错误 IP 下发）；`GetAdapters()` 改为物理网卡优先、跳过 APIPA、标注虚拟网卡。
5. **结果回传可靠性**：被控端改 IP/改名后网络可能短暂不可用，结果回传改为最多 3 次重试；控制端对“执行中”超过 120 秒没结果自动判失败并写库，不再永久卡在“执行中”；下发连不上时退回“待执行”（可重试）而不是直接判死。

流程与交互：

6. **「标记跳过」能解除冲突**：冲突统计排除已跳过/跳过的终端，不再出现“跳过一台后仍然禁止下发”的死锁；右键菜单改为可切换跳过 / 取消跳过。
7. **下发防重入**：新增 `_dispatching` 标志，下发过程中刷新列表不会再把该按钮点开。
8. **后台线程不再直接摸 UI**：下发/重置里的日志改走 `SafeUi`；重置会话的逐台通知移到后台线程（原先在 UI 线程同步等待 5s×N）。
9. **半启动回滚**：先起 TCP 再起 UDP，任一失败都 Stop 两端，不再出现“提示失败但端口已被占”的状态。
10. **状态栏/杂项**：`广播:` 状态实时更新；黑名单框在右键添加后刷新；「查看黑名单」用 `SelectedTab` 正确切页；删掉无用的“起始 IP 编号”输入框；终端表格改为列级只读，只有目标主机名 / 目标 IP 可编辑且**编辑即校验**。

健壮性：

11. **TCP 读行改 UTF-8 解码**：原实现逐字节 `(char)buf[0]`，只要载荷不是纯 ASCII（例如换了 JSON 编码器或非 .NET 客户端）就会破坏字节流、导致校验失败并静默丢弃；现在按字节缓冲后整体 UTF-8 解码，超长行截断。
12. **消息丢弃不再静默**：解析失败 / 校验失败 / 来源不符都会写日志并说明原因。
13. **校验收紧**：`IsValidIp` 改为严格 4 段 IPv4（不再接受 `1`、`192.168.1` 这类缩写）；主机名限 ASCII、≤15 字符、首尾不能是短横线；后缀编号 >255 给出明确提示。
14. **PowerShell 调用加固**：stdout/stderr 并发读取（原来顺序读有管道死锁风险）、60 秒超时后强制结束进程并给出明确错误；执行前对网卡 / 掩码 / 网关 / 主机名做本地校验。
15. **启动不再被用户目录“一票否决”**：`Application.EnableVisualStyles()` 需要往 `%TEMP%` 写主题清单，`TextLogger`/SQLite 需要写 `%AppData%`。这两处不可写时以前是**直接弹「错误」框无法启动**，现在：
    - `EnableVisualStyles` 失败只降级为经典外观（写 WARN 日志），继续启动；
    - `AppConfig.DataDir` 实测可写性，不可写则回退到 `程序目录\data`，SQLite / 日志 / 黑名单一起跟着走；
    - 未处理异常额外兜底写一份到 `%TEMP%\lanac-master-error.log` / `lanac-slave-error.log`，并带上启动面包屑日志（便于定位“启动即崩”）。

v1.2（本轮需求）：

16. **IP 生成规则改为“后缀编号 + 1”**：`IP 末段 = 后缀编号 + IpOctetOffset(1)`，编号 `1` → `.2`（把 `.1` 留给网关），主机名仍为「前缀 + 后缀原文」；末段 255、超出 0-255、等于网关都直接拒绝并说明原因。
17. **跨网段自动互连**（`Slave/MasterReach.cs`）：不同网段但同一广播域时，临时给被控端网卡加一个控制端网段的二级地址，实现双向 TCP；候选地址先 ARP 探测是否被占用，加完再 ARP 探测控制端是否二层可达，探不到立即回滚。
    - 修的过程中发现 `SendARP` 的 IP 参数必须按 `inet_addr` 的字节序传（`b0 | b1<<8 | b2<<16 | b3<<24`），用大端数值会永远返回失败——已用「探测网关」作为正样本验证。
18. **结果回传顺带刷新来源地址**：`SlaveResultReportMsg` 增加 `LocalSourceIp` 参与校验，控制端收到结果后更新列表里的 `SourceIp`，改完 IP 再下发不会打到旧地址。
19. **可离线验证的规则测试**：`_gentest` 覆盖后缀→IP 映射、子网判定、临时地址候选、ARP 探测（含网关正样本），改规则后可随时 `dotnet run --project _gentest` 复核。

v1.3（本轮需求）：

20. **程序图标**：新增 `app.ico`（蓝色圆角 + “IP”字样，含 16/24/32/48/64/128/256 多尺寸），两个 exe 通过 `<ApplicationIcon>` 嵌入，窗口和任务栏图标在运行时用 `Icon.ExtractAssociatedIcon` 从 exe 自身读取，单文件发布同样有效。
21. **临时同网段地址不再固定 `.254`**（解决多台同时上报撞地址）：
    - 起始候选按**本机 MAC 派生**（FNV-1a 取模），各机器错开取址；
    - 仍保留 ARP 占用探测，并新增 **DAD 冲突检测**：加完地址等它从 `Tentative` 变 `Preferred`，若变成 `Duplicate` 立即删除并换下一个候选（最多试 4 次）；
    - 候选池 60 个地址，用完会明确提示手动配置。
22. **模板页去掉无用的只读编辑框**：原来面板下方那块只读的黑名单文本框（看着像输入框、实际不能编辑）已移除，改成一行摘要「黑名单：N 条 MAC / M 条 IP」+「查看黑名单」按钮；加黑名单仍在「终端列表」右键操作，功能未删减。同时修正了模板页各区块的自上而下顺序（按钮栏 → 广播网卡 → 模板参数 → 黑名单摘要）。

v1.4（回归修复）： 

23. **修复“添加临时地址失败”的回归**（23:22 之后的版本引入）：原来要求地址在 6 秒内从 `Tentative` 变成 `Preferred`，否则判失败；但 `netsh` 添加的地址 `DuplicateAddressDetectionState` 是**空值**、虚拟机网卡也可能长期 `Tentative`，于是“能加”变成“加失败”。现在**只有 `Duplicate`（真冲突）才算失败**，其余状态继续交给真实 TCP 握手判断；地址已存在（已存在/幂等）也算成功。
24. **`netsh` 兜底**：部分机器上 WMI/CIM 写入被安全软件拦截（`New-NetIPAddress` 报“拒绝访问”），此时自动改用 `netsh interface ipv4 add address`；删除临时地址同样有 `netsh` 兜底。
25. **新增“允许临时改网卡互连”开关**（被控端界面，默认勾选，悬停有说明）：取消勾选后，跨网段时**绝不修改本机网卡**，只报原因。改动网卡可能让本机网络短暂中断数秒，介意时取消勾选即可。
26. **自检工具默认不改网卡**：`_gentest` 里所有 `AddTempIp/RemoveTempIp` 用例需显式设置 `LANAC_ALLOW_NIC_CHANGE=1` 才会执行，默认跑完前后网卡地址完全一致（已验证）。

v1.5：

27. **CSV 导出不再被“没有管理员权限”卡住**：保存对话框默认打开**当前用户可写**的目录（桌面 / 文档 / 用户目录）；若用户选的位置不可写（如 `C:\` 根目录、`Program Files`），自动依次改存到桌面 → 文档 → 用户目录 → 程序数据目录 → 临时目录，并明确弹窗告知**实际保存路径**；彻底失败时才报错并给出最后一条错误原因。
28. **黑名单可删除**：新增黑名单管理窗口（[BlacklistForm](Master/BlacklistForm.cs)），支持多选「移除选中」和「清空全部」，改动即时写回 `blacklist.txt` 并刷新模板页摘要。`Blacklist` 增加 `RemoveMac/RemoveIp/Clear`。
29. **临时互连地址优先用“预览里的目标 IP”**：原来从控制端网段随机挑（如 `.254`），多台同时上报会撞；现在直接优先用本机即将使用的目标 IP（后缀 5 → 目标 `192.168.1.6`，临时地址也用 `.6`），每台天然不同；只有当该地址已被占用（DAD 判冲突）时，才按 MAC 派生其他备用地址。

v1.6：

30. **两个 CSV 导出各归各位**：原来「终端列表」和「日志报表」两个按钮都导出 SQLite 日志。现在终端列表页导出**当前终端列表**（MAC/来源IP/后缀/目标主机名/目标IP/掩码/网关/DNS/模式/状态/是否跳过/冲突/错误信息），日志页导出**日志报表**（OpLog）。
31. **控制端界面重做**：改为分组布局——「广播服务」（网卡 + 开始/停止/重置 + 会话状态）、「下发模板参数」（两列表单 + 规则提示 + 模板 DHCP 勾选）、「DHCP 服务」（开关 + 自动推导的地址池）、「黑名单」（条数 + 管理入口）；状态栏用分隔符分组计数。详见 [docs/master-ui.png](docs/master-ui.png)。
32. **内置 DHCP 服务器**（`Master/DhcpServer.cs`，可选）：勾选后随【开始发布模板】一起启动，监听 UDP 67，完整处理 DISCOVER→OFFER、REQUEST→ACK/NAK、DECLINE、RELEASE、INFORM。
    - **地址池直接用模板参数推导**：在 IP 前缀所在子网内取 `.100 ~ .250`，自动排除网关和控制端自己的地址，无需手工填写；
    - 租期 2 小时，同一 MAC 稳定拿同一个地址；
    - 需要**控制端以管理员身份运行**（绑定 67）；没有管理员权限时给出明确提示，且不影响模板广播；
    - 配合「被控端使用 DHCP」勾选，可以在完全没有 DHCP 的机房里让被控端先拿到地址、完成上报。
33. **安全警告 / 未知发布者**：[tools/sign.ps1](tools/sign.ps1) 做两件事——`Unblock-File` 去掉“来自网络”的 MOTW 标记（这一步就能消掉截图里的「打开文件 - 安全警告」），以及可选的 Authenticode 签名（`-SelfSigned` 自签名 / `-Pfx` 正规证书），并导出 `.cer` 供部署机导入“受信任的根证书颁发机构”。`build.cmd` 每次构建后也会自动 Unblock。
    - 说明：**真正的“无提示”只能用受信任的证书**（购买 OV/EV 证书，或内网自签名 + 每台机器导入该证书）。没有证书时，Windows 一定会显示“未知发布者”。

v1.7：

34. **修复 DHCP 在真实局域网里拿不到地址**（关键 bug）：原来回包发往 `255.255.255.255`，在多网卡机器上会按默认路由从**虚拟网卡**发出——实测回包源地址是 `192.168.56.1`（VirtualBox），而不是 `192.168.1.7`，所以真实终端永远收不到。
    - 现在回包发往**子网定向广播**（如 `192.168.1.255`），路由表必然选中该网段那块网卡，实测回包源恢复为 `192.168.1.7:67`；
    - 收包仍绑定 `0.0.0.0:67`（实测 Windows 不会把 `255.255.255.255` 交给绑定具体 IP 的套接字）；
    - 启动日志会打印「收包地址 / 回发地址 / 地址池」，便于现场核对。
    - 注意：**同一网段只能有一个 DHCP**。局域网里已有路由器 DHCP 时请勿勾选，否则会互相抢答。
35. **日志 CSV 列按报表习惯重排**：`序号,时间,结果,目标主机名,目标IP,MAC,原始IP,错误信息,会话ID(前 8 位)`（原来第一列是 32 位会话 ID，看起来很乱）。
36. **「打开日志目录」修好**：先 `CreateDirectory` 确保目录存在，再交给 Shell 打开（`UseShellExecute=true`），失败时弹窗给出完整路径与原因，并写入日志。
37. **被控端界面重做**：分成「模板信息 / 本机网卡 / 后缀编号与预览 / 状态与本地日志」四块，字段左对齐、预览加粗显示，详见 [docs/slave-ui.png](docs/slave-ui.png)。

v1.8：

38. **程序名改为中文**：
    - 控制端 `局域网配置工具-控制端.exe`、被控端 `局域网配置工具-被控端.exe`（精简版带 `-精简版` 后缀）；
    - 实现方式：程序集名保持 ASCII（`LanAutoConfigMaster`/`Slave`，避免工具链兼容问题），csproj 里用 `PublishedExeName` + `AfterTargets="Publish"` 的 `Move` 目标在发布后改名；
    - 窗口标题同步改为「局域网配置工具 - 控制端 / 被控端」。
39. **体积优化**：`SatelliteResourceLanguages=en` 去掉其他语言的卫星程序集，控制端 67.1 MB → **61.6 MB**、被控端 66.2 MB → **60.6 MB**；另提供**精简版**（框架依赖）控制端 2.1 MB / 被控端 0.3 MB，需目标机安装 .NET 6 桌面运行时。
    - `PublishTrimmed` 不可用：SDK 直接报 `NETSDK1175`（WinForms 不支持裁剪），强行绕过会造成运行期反射失效。
40. **build.cmd 一次产出四份**：完整版 + 精简版，并自动 Unblock；`tools/sign.ps1` 改为自动遍历 `bin` 下所有 exe（不再写死文件名）。

v1.9：

41. **去掉「打开日志目录」按钮**：日志页工具条现在只有「导出日志 CSV / 清空显示 / 管理黑名单」。
42. **被控端执行完不再弹任何确认框**（无人值守）：
    - 删掉了原来的「配置已成功，即将自动重启…」提示框和「是否立即重启？」询问框，失败也不再弹错误框；
    - 勾选「执行完成后自动重启（默认开）」时，回传结果后直接执行 `shutdown /r /t 5 /f`（5 秒后重启）；
    - 取消勾选则只更新状态栏与日志（提示需手动重启），不弹框；
    - 失败原因通过状态栏第一行 + 本地日志 + 结果回传给控制端，控制端列表里能看到。


