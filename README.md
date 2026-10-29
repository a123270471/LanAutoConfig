# LanAutoConfig — 局域网 PC 批量更名 & IP 配置工具

控制端 / 被控端拆分为 **两个独立程序**，面向机房批量装机、工位批量部署 Windows 终端。

- **技术栈**：C# / .NET 6 WinForms，SQLite（控制端日志），UDP 广播发现 + TCP 可靠传输，本地 PowerShell 修改网卡与计算机名
- **产物**：两个单文件 exe（内置 SQLite；**不内置 .NET 运行时**，目标机需先装 .NET 6 桌面运行时 x64），Windows 10/11 / Server2016+
  - 控制端：`bin\master\局域网配置工具-控制端.exe`
  - 被控端：`bin\slave\局域网配置工具-被控端.exe`
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
   - **输完编号按回车就等于点【提交上报】**，现场装机不用把鼠标挪到按钮上
4. 被控端【提交上报】，TCP 上报本机 MAC / 当前 IP / 目标配置
5. 控制端加入终端列表，自动做 IP、主机名冲突检测（冲突标红，禁止下发）
6. 管理员可手动修改单台目标主机名 / IP、删除终端、标记跳过
7. 确认后【下发执行命令】——**以控制端审核后的配置为准**，覆盖被控端本地预览值
8. 被控端本地执行修改网卡 IP/DNS、重命名计算机，回传结果
9. 控制端更新状态、写 SQLite 日志；被控端提示重启（可选自动重启，重启后计算机名完全生效）

下发前可以先用【IP占用扫描】看看目标地址是不是被老机器占着；重复的改名/IP 工作交给终端列表页的
【自动编号】勾选框：勾上后按上线先后自动发号（01、02、03…），起始号可预先设定。

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
dotnet publish Master/Master.csproj -c Release -r win-x64 --self-contained false -o bin\master
dotnet publish Slave/Slave.csproj -c Release -r win-x64 --self-contained false -o bin\slave
```

产物（exe 名字已中文化，由 csproj 的 `PublishedExeName` + 发布后重命名目标完成）：

| 文件 | 大小 | 目标机要求 |
|---|---|---|
| `bin\master\局域网配置工具-控制端.exe` | ~2.4 MB | .NET 6 桌面运行时（x64） |
| `bin\slave\局域网配置工具-被控端.exe` | ~0.7 MB | .NET 6 桌面运行时（x64） |

**只出框架依赖版（不带运行时），exe 名就是正式产品名**：两个工程的 `SelfContained` 都是 `false`，exe 里不带运行时，体积因此从 60 MB 级降到 2 MB 级；`PublishedExeName` 不带任何后缀。代价是**每台目标机必须先装一次 .NET 6 桌面运行时**：

```powershell
winget install Microsoft.DotNet.DesktopRuntime.6
# 或下载：https://dotnet.microsoft.com/download/dotnet/6.0/runtime  （Desktop Runtime x64）
```

装运行时的两条实操路径：
- **装机前**：随镜像一起装，或新机开箱后用 `winget` / 组策略一次性铺开；
- **装机中**：控制端「文件分发」可以直接把这台机器的离线安装包（`windowsdesktop-runtime-6.0.x-win-x64.exe`）推给被控端静默安装，不用人工跑一圈。

**体积说明**：`PublishSingleFile` 把自身程序集 + `e_sqlite3.dll` 打进一个 exe，其余依赖系统运行时。

**裁剪（PublishTrimmed）走不通**：SDK 直接报 `NETSDK1175: Windows Forms is not supported or recommended with trimming enabled`，强行绕过会导致运行期反射失效，不建议。


### 构建环境注意（务必用 .NET 6 SDK）

.NET 9 起 `apphost`/`singlefilehost` 默认带 CET 兼容标记（`/CETCOMPAT`），**在没打 CET 补丁的 Windows 上**这类程序一启动就 FailFast，表现为系统弹窗 `Unknown Hard Error` 或 `Fatal error. Your Windows doesn't fully support CET`。

注意这是**目标机**的兼容性问题，不是本机的构建限制 —— 本机已装 **pwsh 7.6.6**（`C:\Program Files\PowerShell\7`），启动和脚本执行都正常。

- 本项目是 net6.0，产出的 exe **不受影响**，可以直接放到未打补丁的现场机上。
- 请用 **.NET 6 SDK** 构建。本机 SDK 在 **`D:\ip\.dotnet`（6.0.428）**，`build.cmd` 按「仓库内 `.dotnet` → `C:\Program Files\dotnet` → 用户目录」的顺序查找；这台机器上 `C:\Program Files\dotnet` 并不存在，别照抄旧文档里的路径。
- 不要用 .NET 9/10 SDK 构建：工具链本身能跑，但产出的 exe 在没打补丁的现场机上会起不来。
- 构建主流程只用 `cmd.exe`，**不依赖任何 PowerShell 版本**（现场新装的机器未必有 pwsh），这样同一份脚本在哪台机器上都能跑。需要日志 tail / Unblock-File 时优先用 pwsh 7、缺失才回退系统自带的 `powershell` 5.1 —— 这不是可有可无的讲究：5.1 按 OEM 代码页读 UTF-8 日志，中文会整段变成乱码（实测 `鍏佽…`），只有 pwsh 7 读得对。
- 两个 exe 工程已加 `<CETCompat>false</CETCompat>`，即使以后用 .NET 9+ SDK 重新构建，产出的 apphost 也不会带 CET 标记。

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
│   ├── TcpSend.cs                # TCP 发送（超时5s、重试1次）
│   ├── NetProbe.cs               # ping / TCP 探测 / 读 ARP 表（连通性与 IP 占用扫描）
│   ├── FailureClassifier.cs      # 执行失败原因归类 + 修复建议
│   ├── ConsoleText.cs            # 识别命令输出编码（修中文乱码）
│   └── UiScale.cs                # 高 DPI 布局换算
├── Master/                       # 控制端独立程序（局域网配置工具-控制端.exe）
│   ├── Master.csproj / Program.cs
│   ├── MasterForm.cs             # 控制端 UI：模板/终端列表/日志 + 冲突 + 下发
│   ├── UdpBroadcastService.cs    # UDP 广播模板
│   ├── TcpServerService.cs       # TCP 接收上报/结果
│   ├── SessionManager.cs         # 会话与终端列表
│   ├── ConflictValidator.cs      # IP/主机名冲突
│   ├── SqliteLogStore.cs         # OpLog 表
│   ├── HttpFileServer.cs         # 内置文件服务（TCP 48890，给被控端下载安装包）
│   ├── FilePushForm.cs           # 文件分发 / 静默装软件 / 自升级
│   ├── ScreenshotForm.cs         # 远程截图
│   ├── ConnectivityForm.cs       # 连通性矩阵（ping + 48889 端口）
│   ├── IpScanForm.cs             # 真实 IP 占用扫描（读 ARP 表）
│   ├── ImpactPreviewForm.cs      # 高风险操作的影响预览
│   ├── Blacklist.cs              # MAC/IP 黑名单
│   └── Whitelist.cs              # MAC 白名单（默认关闭）
├── Slave/                        # 被控端独立程序（局域网配置工具-被控端.exe）
│   ├── Slave.csproj / Program.cs
│   ├── SlaveForm.cs              # 被控端 UI：后缀输入/预览/上报/执行
│   ├── UdpTemplateListener.cs    # 监听广播、自动发现控制端
│   ├── TcpCommandListener.cs     # 接收执行指令/会话重置
│   ├── ConfigGenerator.cs        # 后缀 → 主机名/IP
│   ├── FileReceiver.cs           # 下载 + SHA256 校验 + 静默执行
│   ├── SelfUpdater.cs            # 自升级（批处理等进程退出后替换 exe）
│   ├── ScreenCapture.cs          # 桌面截图（JPEG 压缩回传）
│   ├── HeartbeatService.cs       # 心跳（每 10 秒，带身份令牌）；退出前发“下线通知”
│   └── LocalNetChanger.cs        # 本地 PowerShell：静态IP/DHCP/改名/重启
└── bin/
    ├── master\局域网配置工具-控制端.exe   # 框架依赖，~2.4 MB（需 .NET 6 桌面运行时）
    └── slave\局域网配置工具-被控端.exe    # 框架依赖，~0.7 MB
```

## 数据与日志

- 控制端 SQLite：`%AppData%\LanAutoConfig\master_logs.db`，表 `OpLog`（SessionId / OpTime / Mac / SourceIp / TargetHostName / TargetIp / Result / ErrorMsg），支持导出 CSV
- 双方文本日志：`%AppData%\LanAutoConfig\logs\master.log` / `slave.log`
- 黑名单：`%AppData%\LanAutoConfig\blacklist.txt`
- **若 `%AppData%` 不可写**（受限令牌、安全软件拦截、只读漫游配置），上述路径自动回退到 **程序目录下的 `data\`**（如 `bin\master\data\logs\master.log`），启动日志里会标注 `（%AppData% 不可写，已回退到程序目录）`。

## 说明与偏离记录

- 文档约定“脚本调用 System.Management.Automation”。本实现为兼容**单文件发布**（该 SDK 包体积大、含原生库，单文件发布麻烦），改为调用本地 `powershell.exe -EncodedCommand` 执行脚本，功能与结果等价（本地执行、无远程凭据）。若你确实需要改为 System.Management.Automation，告诉我即可切换。
- 修改计算机名仍需重启才完全生效；程序在执行成功后提示重启（可勾选自动重启）。
- 仅限同一局域网 / 工作组（域环境也兼容，仅改本机本地主机名，不做 AD 操作）；不做公网穿透、不做端口扫描、不自动抓全网主机。

## 协议与安全边界（重要）

- 协议是**明文 + 轻量校验**：`Magic` 是公开常量，`Check` 只是 FNV-1a 哈希，`SessionId` 每 2.5 秒明文广播。
  真正挡伪造的是**每台被控端自己的身份令牌**（`NetMessage.Token`，v4 起）：
  - 令牌由**被控端本地生成**（随机 GUID，存 `slave-prefs.json`，重装程序才会变）；
  - 只在被控端的**单播**上报/心跳里带给控制端，**绝不出现在 UDP 广播里** —— 否则同网段人人可收，等于没防护；
  - 控制端记在终端列表（`SlaveSessionItem.AuthToken`），之后每条指令原样带回；
  - 被控端收到指令先比“令牌 == 本机令牌”，不符直接丢弃并写日志 `[拒绝] ... 令牌不符（疑似伪造指令）`；
  - 令牌参与校验码计算（`FNV1a(MsgType|SessionId|Magic|Token|StableBody)`），改令牌 / 改任何业务字段都会校验失败。
  - 代价：**列表里还没收到过心跳的终端没有令牌，下发会直接拒绝**并提示“等它下一次心跳（约 10 秒内自动获取）”。
- 其余来源约束：
  - 被控端只接受**当前配对控制端 IP** 发来的指令（收到模板时记录，来源不符直接拒绝并写日志）；
  - 被控端校验指令里的 `LocalMac` 必须等于本机已上报的网卡 MAC；
  - 未收到模板（无会话）时拒绝一切指令。
- **边界（必须知道的局限）**：令牌只能防“同网段随手伪造”，防不住交换机端口镜像 / ARP 欺骗下抓明文再看懂协议的人。
  要真正抗中间人，需要 HMAC 共享密钥 + 时间戳防重放（属于后续改造项）。现场建议：把控制端和被控端放在同一个可信 VLAN。

## 部署注意事项

- 防火墙需放行：入站 **UDP 48888**（模板广播）、入站 **TCP 48889**（上报/指令）。被防火墙拦截时的现象是“被控端一直收不到模板”。
- 用**文件分发 / 自升级**时，还要在**控制端**放行入站 **TCP 48890**（内置文件服务）：被控端是从控制端下载文件的，
  挡住的典型现象是“指令下发成功，但回传 `下载失败：连接超时`”。
- 两端都用 48889，**同一台机器不能同时运行控制端和被控端**（联调请用两台机器或虚拟机）。
- 控制端“广播网卡”选择哪块网卡，下发指令也从该网卡发出；被控端上报也绑定所选网卡，多网卡机器请确认选中的是接在同一局域网的那块。
- **双端必须一起更新**：本次协议升到 `LANAC-V4`（新增身份令牌并纳入校验码、文件分发/自升级/截图/程序控制指令）。
  新旧版本混用会在校验处被拒绝，日志里会写 `[丢弃] ... 协议版本不一致（对方 LANAC-V3，本机 LANAC-V4）—— 请把两端都换成同一版本的程序`；
  历史上 V3（硬件信息/工作组/时区/心跳）同样要求两端同版本。**换版本时先换控制端再换被控端，或者干脆一起换**。
- 程序对 `%TEMP%` / `%AppData%` 不可写的情况做了降级：`EnableVisualStyles` 失败只是失去主题外观（日志里有 WARN），数据目录自动回退到程序目录，**不会因此起不来**。如果日志里出现这两条，说明该机器上有安全软件/受限令牌在拦截这个 exe 写用户目录，建议加白名单。
- **跨网段但同一广播域可自动互通**：被控端当前 IP 与控制端不同网段（例：被控端还在 `192.168.2.5`、控制端在 `192.168.1.7`）但两台机器接在同一交换机/VLAN 时，被控端会自动**临时**给自己网卡加一个控制端网段的地址（从 `.254` 往下找空闲地址，先 ARP 探测占用），使上报/下发/回传全部走二层直连；执行配置时该临时地址随 `Remove-NetIPAddress` 一并清除，退出或重置会话时也会主动删除。
  - 控制端**不需要管理员权限**、不需要加路由。
  - 若中间有路由器/VLAN 隔离（ARP 探不到控制端），会立即回滚临时地址并提示「不在同一广播域」，这种情况只能先把被控端接到控制端网段。
  - 说明：只给被控端加静态路由的单边方案不可行——控制端的回包仍会按自己的路由表发给网关，TCP 握手完不成。

### 高 DPI / 系统缩放（125% / 150%）

Windows 把「设置 → 系统 → 屏幕 → 缩放与布局」调大后，字体按 DPI 放大，但代码里写死的像素常量（行高、控件宽高、内边距）不会跟着变，界面就会出现标签压字、控件被裁。v2.0 起由 [Common/UiScale.cs](Common/UiScale.cs) 处理：

- 程序启动时读系统 DPI（`GetDpiForSystem`），`Factor = DPI / 96`；界面构建完成后按这个比例统一换算表里布局的行列高、控件尺寸、`Padding` / `Margin` 和窗体大小；
- **100% 缩放下 `Factor = 1`，`Apply()` 直接返回**，界面与原来逐像素一致，不影响现有机器；
- 字号不缩放：`Font` 以“点”为单位，系统已经按 DPI 放大过一次，再乘就会过大；
- 缩放后窗体若超出屏幕工作区，会自动收缩到工作区内（否则标题栏跑到屏幕外，连拖都拖不回来）；内容确实放不下时窗口本身仍可缩小，只是底部日志区会被压缩。
- 调试/验证：`set LANAC_UI_SCALE=1.5` 再启动，可以在 100% 的机器上模拟 150% 的布局（此时字体也一起放大），用来在没有高分屏的环境里验证界面。


