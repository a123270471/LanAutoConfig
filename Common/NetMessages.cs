using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanAutoConfig.Common
{
    /// <summary>消息类型枚举（字符串常量，与文档 MsgType 枚举一致）。</summary>
    public static class MsgTypes
    {
        public const string BroadcastTemplate  = "BroadcastTemplate";  // UDP广播：控制端下发模板
        public const string SlaveReport        = "SlaveReport";        // TCP：被控端上报预览配置（含硬件信息）
        public const string MasterExecuteCmd   = "MasterExecuteCmd";   // TCP：控制端下发最终执行配置
        public const string SlaveResultReport  = "SlaveResultReport";  // TCP：被控端上报执行结果
        public const string MasterResetSession = "MasterResetSession"; // TCP：控制端重置会话，通知终端清空本地
        public const string SlaveHeartbeat     = "SlaveHeartbeat";     // TCP：被控端心跳（在线状态）
        public const string SlaveOffline       = "SlaveOffline";       // TCP：被控端正常退出，主动告知控制端“我下线了”
        public const string MasterRebootCmd    = "MasterRebootCmd";    // TCP：控制端下发“重启本机”
        public const string MasterAutostartCmd = "MasterAutostartCmd"; // TCP：控制端批量开关“开机自启动”
        public const string MasterShutdownCmd  = "MasterShutdownCmd";  // TCP：控制端下发“关闭本机”
        public const string MasterAssignSuffix = "MasterAssignSuffixCmd"; // TCP：控制端下发后缀编号，被控端自动上报
        public const string MasterCustomCmd   = "MasterCustomCmd";   // TCP：控制端下发自定义命令/脚本
        public const string SlaveCustomResult = "SlaveCustomResult"; // TCP：被控端回传自定义命令执行结果
        public const string MasterVerifyCmd   = "MasterVerifyCmd";   // TCP：控制端要求被控端上报当前实际配置（用于重启后自动验收）
        public const string SlaveVerifyResult = "SlaveVerifyResult"; // TCP：被控端回传当前实际配置
        public const string MasterFilePush    = "MasterFilePushCmd";  // TCP：控制端下发文件（安装包/脚本/驱动）
        public const string SlaveFileResult   = "SlaveFileResult";    // TCP：被控端回传文件下载/执行结果
        public const string MasterSelfUpdate  = "MasterSelfUpdateCmd";// TCP：控制端下发改程序（自升级）
        public const string MasterScreenshot  = "MasterScreenshotCmd";// TCP：控制端要求截屏
        public const string SlaveScreenshot   = "SlaveScreenshotResult";// TCP：被控端回传截屏
        public const string MasterProcCmd     = "MasterProcCmd";      // TCP：控制端要求被控端程序重启/退出
    }

    /// <summary>
    /// 消息基类：统一携带 MsgType / SessionId（会话隔离）/ Magic（校验标记）/ Check（简单校验码）。
    /// 统一通信编码 UTF-8，序列化/反序列化由 JsonHelper 完成。
    /// </summary>
    public abstract class NetMessage
    {
        public string MsgType { get; set; }
        public string SessionId { get; set; }
        public string Magic { get; set; } = AppConfig.Magic;
        public long Check { get; set; }

        /// <summary>
        /// 本机身份令牌（每台被控端一个随机值，首次运行生成并写进本地偏好，之后固定不变）。
        ///
        /// 为什么不是“会话令牌”：会话令牌必须在广播模板里下发，而 UDP 广播同网段每台机器都能收到，
        /// 拿到令牌就能伪造成控制端 —— 等于没有防护。这里的令牌恰好相反：
        ///   · 被控端只在“上报/心跳”里把它<b>单播</b>告诉控制端，从不在广播里出现；
        ///   · 控制端把它记在终端列表里，之后每条指令都原样带回；
        ///   · 被控端收到指令时校验“带回来的令牌 == 本机令牌”，不符直接丢弃。
        /// 于是同网段其它机器（包括同网段的另一台被控端）即使知道协议格式、能收到广播，
        /// 也不知道这台的令牌，无法伪造“改 IP / 执行脚本 / 重启”这类破坏性指令。
        /// 局限：交换机端口镜像/ARP 欺骗能看到单播流量，这是明文协议的固有边界。
        /// </summary>
        public string Token { get; set; }

        /// <summary>
        /// 接收方观测到的“对端真实来源 IP”，由 TCP 服务端在收到消息时填入。
        /// 不参与序列化、也不参与 Check 校验（[JsonIgnore]），只是本进程内的附加信息：
        /// 用它作为“回发指令的目标地址”最可靠 —— 心跳/上报里带的 LocalIp 是发送方自己猜的，
        /// 本机加了临时同网段地址后往往不会更新（历史 bug：控制端按这个旧地址下发，指令打空）。
        /// </summary>
        [JsonIgnore]
        public string RemoteIp { get; set; }

        protected NetMessage() { }
        protected NetMessage(string msgType, string sessionId) { MsgType = msgType; SessionId = sessionId; }

        /// <summary>参与校验的稳定字段（子类实现），用于简单校验码计算。</summary>
        protected abstract string StableBody();

        public void ComputeCheck() => Check = Fnv1a($"{MsgType}|{SessionId}|{Magic}|{Token}|{StableBody()}");

        /// <summary>校验 Magic 与 Check，过滤无关程序/伪造消息。</summary>
        public bool Validate() => ValidateEx(out _);

        /// <summary>
        /// 同 <see cref="Validate()"/>，并给出人能看懂的原因（现场排查用：
        /// “校验失败”三个字没法区分是版本不一致还是消息被改过）。
        /// </summary>
        public bool ValidateEx(out string reason)
        {
            reason = null;
            if (Magic != AppConfig.Magic)
            {
                reason = $"协议版本不一致（对方 {Magic ?? "空"}，本机 {AppConfig.Magic}）—— 请把两端都换成同一版本的程序";
                return false;
            }
            long expect = Fnv1a($"{MsgType}|{SessionId}|{Magic}|{Token}|{StableBody()}");
            if (Check != expect)
            {
                reason = "校验码不匹配（版本不一致，或消息内容被修改/丢包截断）";
                return false;
            }
            return true;
        }

        private static long Fnv1a(string s)
        {
            ulong hash = 0xcbf29ce484222325UL;
            foreach (char c in s) { hash ^= c; hash *= 0x100000001b3UL; }
            return (long)hash;
        }
    }

    /// <summary>6.1 BroadcastTemplate（UDP 广播包）：控制端下发模板。</summary>
    public class BroadcastTemplateMsg : NetMessage
    {
        public string IpPrefix { get; set; }
        public string SubnetMask { get; set; }
        public string Gateway { get; set; }
        public string Dns1 { get; set; }
        public string Dns2 { get; set; }
        public string HostNamePrefix { get; set; }
        public bool IsDhcp { get; set; }
        /// <summary>时区 ID（空表示不改），如 "China Standard Time"。</summary>
        public string TimeZoneId { get; set; }
        /// <summary>是否与 NTP 服务器对时。</summary>
        public bool SyncTime { get; set; }

        public BroadcastTemplateMsg() : base() { }
        public BroadcastTemplateMsg(string sessionId, string ipPrefix, string mask, string gw,
            string dns1, string dns2, string hnPrefix, bool isDhcp,
            string timeZoneId = null, bool syncTime = false)
            : base(MsgTypes.BroadcastTemplate, sessionId)
        {
            IpPrefix = ipPrefix; SubnetMask = mask; Gateway = gw;
            Dns1 = dns1; Dns2 = dns2; HostNamePrefix = hnPrefix; IsDhcp = isDhcp;
            TimeZoneId = timeZoneId; SyncTime = syncTime;
        }
        protected override string StableBody()
            => $"{IpPrefix}|{SubnetMask}|{Gateway}|{Dns1}|{Dns2}|{HostNamePrefix}|{IsDhcp}|{TimeZoneId}|{SyncTime}";
    }

    /// <summary>6.2 SlaveReport：被控端上报预览配置。</summary>
    public class SlaveReportMsg : NetMessage
    {
        public string LocalMac { get; set; }
        public string LocalSourceIp { get; set; }
        public string Suffix { get; set; }
        public string TargetHostName { get; set; }
        public string TargetIp { get; set; }
        public string SubnetMask { get; set; }
        public string Gateway { get; set; }
        public string Dns1 { get; set; }
        public string Dns2 { get; set; }
        /// <summary>DHCP 模式（模板勾选），必须上报给控制端，否则控制端无法区分“静态空 IP”与“DHCP”。</summary>
        public bool IsDhcp { get; set; }
        /// <summary>硬件/资产信息（CPU/内存/磁盘/主板/BIOS/序列号）。</summary>
        public HardwareInfo Hardware { get; set; }

        public SlaveReportMsg() : base() { }
        public SlaveReportMsg(string sessionId) : base(MsgTypes.SlaveReport, sessionId) { }
        protected override string StableBody()
            => $"{LocalMac}|{LocalSourceIp}|{Suffix}|{TargetHostName}|{TargetIp}|{SubnetMask}|{Gateway}|{Dns1}|{Dns2}|{IsDhcp}|{Hardware?.Signature()}";
    }

    /// <summary>6.3 MasterExecuteCmd：控制端下发最终执行指令（被控端以此为准，覆盖本地预览值）。</summary>
    public class MasterExecuteCmdMsg : NetMessage
    {
        public string LocalMac { get; set; }
        public string TargetHostName { get; set; }
        public string TargetIp { get; set; }
        public string SubnetMask { get; set; }
        public string Gateway { get; set; }
        public string Dns1 { get; set; }
        public string Dns2 { get; set; }
        public bool IsDhcp { get; set; }
        public bool NeedAutoReboot { get; set; }
        /// <summary>时区 ID（空表示不改）。</summary>
        public string TimeZoneId { get; set; }
        /// <summary>是否与 NTP 对时。</summary>
        public bool SyncTime { get; set; }

        public MasterExecuteCmdMsg() : base() { }
        public MasterExecuteCmdMsg(string sessionId) : base(MsgTypes.MasterExecuteCmd, sessionId) { }
        protected override string StableBody()
            => $"{LocalMac}|{TargetHostName}|{TargetIp}|{SubnetMask}|{Gateway}|{Dns1}|{Dns2}|{IsDhcp}|{NeedAutoReboot}|{TimeZoneId}|{SyncTime}";
    }

    /// <summary>6.4 SlaveResultReport：被控端回传执行结果。</summary>
    public class SlaveResultReportMsg : NetMessage
    {
        public string LocalMac { get; set; }
        public bool Success { get; set; }
        public string ErrorMsg { get; set; }
        public string AfterHostName { get; set; }
        public string AfterIp { get; set; }
        /// <summary>回传时本机实际源地址（改完 IP 后控制端列表里的 SourceIp 会过期，用它刷新）。</summary>
        public string LocalSourceIp { get; set; }

        public SlaveResultReportMsg() : base() { }
        public SlaveResultReportMsg(string sessionId) : base(MsgTypes.SlaveResultReport, sessionId) { }
        protected override string StableBody() => $"{LocalMac}|{Success}|{ErrorMsg}|{AfterHostName}|{AfterIp}|{LocalSourceIp}";
    }

    /// <summary>6.5 MasterResetSession：控制端重置会话，通知终端清空本地。</summary>
    public class MasterResetSessionMsg : NetMessage
    {
        public MasterResetSessionMsg() : base() { }
        public MasterResetSessionMsg(string sessionId) : base(MsgTypes.MasterResetSession, sessionId) { }
        protected override string StableBody() => "reset";
    }

    /// <summary>6.6 SlaveHeartbeat：被控端心跳（TCP，每 10 秒一次），用于控制端显示在线/离线。</summary>
    public class SlaveHeartbeatMsg : NetMessage
    {
        public string LocalMac { get; set; }
        public string HostName { get; set; }
        public string LocalIp { get; set; }
        /// <summary>当前状态摘要（idle / reported / executing / success / failed），仅用于看板显示。</summary>
        public string State { get; set; }

        public SlaveHeartbeatMsg() : base() { }
        public SlaveHeartbeatMsg(string sessionId) : base(MsgTypes.SlaveHeartbeat, sessionId) { }
        protected override string StableBody() => $"{LocalMac}|{HostName}|{LocalIp}|{State}";
    }

    /// <summary>
    /// 被控端正常退出时发的一条“告别”消息。
    /// 没有它，控制端只能等心跳超时（几十秒）才知道这台机器下线了；
    /// 有了它，点关闭 / 程序重启 / 升级替换的瞬间列表就变离线。仅“正常退出”能发，
    /// 断电、蓝屏、拔网线仍然只能靠心跳超时兜底。
    /// </summary>
    public class SlaveOfflineMsg : NetMessage
    {
        public string LocalMac { get; set; }
        public string HostName { get; set; }
        /// <summary>下线原因（正常退出 / 重启程序 / 升级替换），仅用于日志。</summary>
        public string Reason { get; set; }

        public SlaveOfflineMsg() : base() { }
        public SlaveOfflineMsg(string sessionId) : base(MsgTypes.SlaveOffline, sessionId) { }
        protected override string StableBody() => $"{LocalMac}|{HostName}|{Reason}";
    }

    /// <summary>6.7 MasterRebootCmd：控制端要求某台终端重启（批量重启用）。</summary>
    public class MasterRebootCmdMsg : NetMessage
    {
        public string LocalMac { get; set; }
        /// <summary>延迟秒数（给操作员留出取消/保存的时间）。</summary>
        public int DelaySeconds { get; set; }

        public MasterRebootCmdMsg() : base() { }
        public MasterRebootCmdMsg(string sessionId) : base(MsgTypes.MasterRebootCmd, sessionId) { }
        protected override string StableBody() => $"{LocalMac}|{DelaySeconds}";
    }

    /// <summary>
    /// 6.8 MasterAutostartCmd：控制端要求某台终端开启 / 取消开机自启动。
    /// 只动注册表 Run 项与本地偏好，不触碰网络配置，也不要求管理员权限（HKCU 即可）。
    /// </summary>
    public class MasterAutostartCmdMsg : NetMessage
    {
        public string LocalMac { get; set; }
        /// <summary>true = 写入开机自启；false = 删除 Run 项。</summary>
        public bool Enable { get; set; }

        public MasterAutostartCmdMsg() : base() { }
        public MasterAutostartCmdMsg(string sessionId, string localMac, bool enable)
            : base(MsgTypes.MasterAutostartCmd, sessionId)
        {
            LocalMac = localMac; Enable = enable;
        }
        protected override string StableBody() => $"{LocalMac}|{Enable}";
    }

    /// <summary>6.9 MasterShutdownCmd：控制端要求某台终端关机（批量关机用）。</summary>
    public class MasterShutdownCmdMsg : NetMessage
    {
        public string LocalMac { get; set; }
        /// <summary>延迟秒数（给操作员留出取消/保存的时间）。</summary>
        public int DelaySeconds { get; set; }

        public MasterShutdownCmdMsg() : base() { }
        public MasterShutdownCmdMsg(string sessionId) : base(MsgTypes.MasterShutdownCmd, sessionId) { }
        protected override string StableBody() => $"{LocalMac}|{DelaySeconds}";
    }

    /// <summary>
    /// 6.10 MasterAssignSuffixCmd：控制端把后缀编号直接下发到终端，终端自动填好并提交上报。
    /// 用于「新机上电 → 控制端按上线顺序统一编号」的批量装机流程，省掉逐台手输编号。
    /// </summary>
    public class MasterAssignSuffixCmdMsg : NetMessage
    {
        public string LocalMac { get; set; }
        /// <summary>后缀编号原文（保留前导零，如 "01"）。主机名 = 前缀 + 原文，IP 末段 = 数字 + 1。</summary>
        public string Suffix { get; set; }

        public MasterAssignSuffixCmdMsg() : base() { }
        public MasterAssignSuffixCmdMsg(string sessionId, string localMac, string suffix)
            : base(MsgTypes.MasterAssignSuffix, sessionId)
        {
            LocalMac = localMac; Suffix = suffix;
        }
        protected override string StableBody() => $"{LocalMac}|{Suffix}";
    }

    /// <summary>
    /// 6.11 MasterCustomCmd：控制端向被控端下发自定义命令 / 脚本（运维用，操作员主动触发）。
    /// Shell 区分 powershell / cmd；脚本内容原样下发，被控端本地执行后回传标准输出/错误/退出码。
    /// </summary>
    public class MasterCustomCmdMsg : NetMessage
    {
        public string LocalMac { get; set; }
        /// <summary>"powershell" 或 "cmd"，被控端据此选择执行器。</summary>
        public string Shell { get; set; }
        /// <summary>要执行的脚本/命令原文（UTF-8）。</summary>
        public string Script { get; set; }
        /// <summary>超时秒数（被控端据此限制执行时长，默认 60）。</summary>
        public int TimeoutSeconds { get; set; }

        public MasterCustomCmdMsg() : base() { }
        public MasterCustomCmdMsg(string sessionId) : base(MsgTypes.MasterCustomCmd, sessionId) { }
        protected override string StableBody() => $"{LocalMac}|{Shell}|{Script?.Length ?? 0}|{TimeoutSeconds}";
    }

    /// <summary>6.12 SlaveCustomResult：被控端回传自定义命令执行结果。</summary>
    public class SlaveCustomResultMsg : NetMessage
    {
        public string LocalMac { get; set; }
        public bool Success { get; set; }
        public int ExitCode { get; set; }
        public string StdOut { get; set; }
        public string StdErr { get; set; }

        public SlaveCustomResultMsg() : base() { }
        public SlaveCustomResultMsg(string sessionId) : base(MsgTypes.SlaveCustomResult, sessionId) { }
        protected override string StableBody() => $"{LocalMac}|{Success}|{ExitCode}|{StdOut?.Length ?? 0}|{StdErr?.Length ?? 0}";
    }

    /// <summary>
    /// 6.13 MasterVerifyCmd：控制端要求被控端上报“当前实际的计算机名与 IP”，用于重启后自动验收。
    /// 被控端比对实际值与期望值后回传 Match，控制端据此标记验收通过/不符。
    /// </summary>
    public class MasterVerifyCmdMsg : NetMessage
    {
        public string LocalMac { get; set; }
        public string ExpectedHostName { get; set; }
        public string ExpectedIp { get; set; }

        public MasterVerifyCmdMsg() : base() { }
        public MasterVerifyCmdMsg(string sessionId) : base(MsgTypes.MasterVerifyCmd, sessionId) { }
        protected override string StableBody() => $"{LocalMac}|{ExpectedHostName}|{ExpectedIp}";
    }

    /// <summary>6.14 SlaveVerifyResult：被控端回传当前实际配置与验收结论。</summary>
    public class SlaveVerifyResultMsg : NetMessage
    {
        public string LocalMac { get; set; }
        public string CurrentHostName { get; set; }
        public string CurrentIp { get; set; }
        public string ExpectedHostName { get; set; }
        public string ExpectedIp { get; set; }
        public bool Match { get; set; }
        public string Note { get; set; }

        public SlaveVerifyResultMsg() : base() { }
        public SlaveVerifyResultMsg(string sessionId) : base(MsgTypes.SlaveVerifyResult, sessionId) { }
        protected override string StableBody() => $"{LocalMac}|{CurrentHostName}|{CurrentIp}|{ExpectedHostName}|{ExpectedIp}|{Match}|{Note?.Length ?? 0}";
    }

    /// <summary>
    /// 6.15 MasterFilePushCmd：控制端让被控端从内置文件服务下载一个文件（安装包/脚本/驱动）。
    /// 文件走 HTTP（TCP 48890），指令走 TCP 48889：大文件不占指令通道，失败也能单独重试。
    /// </summary>
    public class MasterFilePushCmdMsg : NetMessage
    {
        public string LocalMac { get; set; }
        /// <summary>文件名（只取文件名，不含路径）。</summary>
        public string FileName { get; set; }
        /// <summary>下载地址 http://控制端IP:48890/f/随机令牌/文件名。</summary>
        public string Url { get; set; }
        /// <summary>文件 SHA256（十六进制小写），下载后校验，防止传输出错或被替换。</summary>
        public string Sha256 { get; set; }
        public long SizeBytes { get; set; }
        /// <summary>保存到被控端的哪个路径（支持 %TEMP% / %ProgramData% 等环境变量；空 = %TEMP%\LanAC\文件名）。</summary>
        public string TargetPath { get; set; }
        /// <summary>下载完成后是否执行。</summary>
        public bool RunAfter { get; set; }
        /// <summary>执行参数（如 msiexec 的 /qn）。</summary>
        public string RunArgs { get; set; }
        /// <summary>执行方式：auto / msi / exe / ps1 / bat / none。</summary>
        public string RunMode { get; set; }
        public int TimeoutSeconds { get; set; }

        public MasterFilePushCmdMsg() : base() { }
        public MasterFilePushCmdMsg(string sessionId) : base(MsgTypes.MasterFilePush, sessionId) { }
        protected override string StableBody()
            => $"{LocalMac}|{FileName}|{Url}|{Sha256}|{SizeBytes}|{TargetPath}|{RunAfter}|{RunArgs}|{RunMode}|{TimeoutSeconds}";
    }

    /// <summary>6.16 SlaveFileResult：文件下发/自升级的结果回传。</summary>
    public class SlaveFileResultMsg : NetMessage
    {
        public string LocalMac { get; set; }
        public string FileName { get; set; }
        public bool Success { get; set; }
        public string Message { get; set; }
        public long Bytes { get; set; }
        public string Sha256 { get; set; }

        public SlaveFileResultMsg() : base() { }
        public SlaveFileResultMsg(string sessionId) : base(MsgTypes.SlaveFileResult, sessionId) { }
        protected override string StableBody()
            => $"{LocalMac}|{FileName}|{Success}|{Message?.Length ?? 0}|{Bytes}|{Sha256}";
    }

    /// <summary>6.17 MasterSelfUpdateCmd：控制端让被控端用新 exe 替换自己并重启程序（不需要跑现场）。</summary>
    public class MasterSelfUpdateCmdMsg : NetMessage
    {
        public string LocalMac { get; set; }
        public string Url { get; set; }
        public string Sha256 { get; set; }
        public long SizeBytes { get; set; }

        public MasterSelfUpdateCmdMsg() : base() { }
        public MasterSelfUpdateCmdMsg(string sessionId) : base(MsgTypes.MasterSelfUpdate, sessionId) { }
        protected override string StableBody() => $"{LocalMac}|{Url}|{Sha256}|{SizeBytes}";
    }

    /// <summary>6.18 MasterScreenshotCmd：控制端要求被控端截一张屏（排障用，看卡在哪个界面）。</summary>
    public class MasterScreenshotCmdMsg : NetMessage
    {
        public string LocalMac { get; set; }
        /// <summary>最大宽度（像素），超出则等比缩小；0 = 1280。缩小是为了让一张图能塞进一条消息。</summary>
        public int MaxWidth { get; set; }

        public MasterScreenshotCmdMsg() : base() { }
        public MasterScreenshotCmdMsg(string sessionId) : base(MsgTypes.MasterScreenshot, sessionId) { }
        protected override string StableBody() => $"{LocalMac}|{MaxWidth}";
    }

    /// <summary>6.19 SlaveScreenshotResult：截屏回传（JPEG 的 Base64）。</summary>
    public class SlaveScreenshotResultMsg : NetMessage
    {
        public string LocalMac { get; set; }
        public bool Success { get; set; }
        public string Base64 { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string Message { get; set; }

        public SlaveScreenshotResultMsg() : base() { }
        public SlaveScreenshotResultMsg(string sessionId) : base(MsgTypes.SlaveScreenshot, sessionId) { }
        protected override string StableBody()
            => $"{LocalMac}|{Success}|{Width}|{Height}|{Base64?.Length ?? 0}|{Message?.Length ?? 0}";
    }

    /// <summary>6.20 MasterProcCmd：控制端要求被控端程序自己重启（加载新配置）或退出（换成新 exe 前清场）。</summary>
    public class MasterProcCmdMsg : NetMessage
    {
        public string LocalMac { get; set; }
        /// <summary>"restart" 或 "exit"。</summary>
        public string Action { get; set; }
        public int DelaySeconds { get; set; }

        public MasterProcCmdMsg() : base() { }
        public MasterProcCmdMsg(string sessionId) : base(MsgTypes.MasterProcCmd, sessionId) { }
        protected override string StableBody() => $"{LocalMac}|{Action}|{DelaySeconds}";
    }

    /// <summary>JSON 序列化 / 反序列化助手（统一 UTF-8，PascalCase 字段与文档一致）。</summary>
    public static class JsonHelper
    {
        private static readonly JsonSerializerOptions Opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };

        /// <summary>序列化消息（自动计算校验码），返回单行 JSON（不含换行）。</summary>
        public static string Serialize(NetMessage msg)
        {
            msg.ComputeCheck();
            return JsonSerializer.Serialize(msg, msg.GetType(), Opts);
        }

        /// <summary>反序列化消息：根据 MsgType 分派到具体类型；无法识别返回 null。</summary>
        public static NetMessage Deserialize(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("MsgType", out var t)) return null;
                string mt = t.GetString();
                NetMessage msg = mt switch
                {
                    MsgTypes.BroadcastTemplate  => JsonSerializer.Deserialize<BroadcastTemplateMsg>(json, Opts),
                    MsgTypes.SlaveReport        => JsonSerializer.Deserialize<SlaveReportMsg>(json, Opts),
                    MsgTypes.MasterExecuteCmd   => JsonSerializer.Deserialize<MasterExecuteCmdMsg>(json, Opts),
                    MsgTypes.SlaveResultReport  => JsonSerializer.Deserialize<SlaveResultReportMsg>(json, Opts),
                    MsgTypes.MasterResetSession => JsonSerializer.Deserialize<MasterResetSessionMsg>(json, Opts),
                    MsgTypes.SlaveHeartbeat     => JsonSerializer.Deserialize<SlaveHeartbeatMsg>(json, Opts),
                    MsgTypes.SlaveOffline       => JsonSerializer.Deserialize<SlaveOfflineMsg>(json, Opts),
                    MsgTypes.MasterRebootCmd    => JsonSerializer.Deserialize<MasterRebootCmdMsg>(json, Opts),
                    MsgTypes.MasterAutostartCmd => JsonSerializer.Deserialize<MasterAutostartCmdMsg>(json, Opts),
                    MsgTypes.MasterShutdownCmd  => JsonSerializer.Deserialize<MasterShutdownCmdMsg>(json, Opts),
                    MsgTypes.MasterAssignSuffix => JsonSerializer.Deserialize<MasterAssignSuffixCmdMsg>(json, Opts),
                    MsgTypes.MasterCustomCmd    => JsonSerializer.Deserialize<MasterCustomCmdMsg>(json, Opts),
                    MsgTypes.SlaveCustomResult  => JsonSerializer.Deserialize<SlaveCustomResultMsg>(json, Opts),
                    MsgTypes.MasterVerifyCmd    => JsonSerializer.Deserialize<MasterVerifyCmdMsg>(json, Opts),
                    MsgTypes.SlaveVerifyResult  => JsonSerializer.Deserialize<SlaveVerifyResultMsg>(json, Opts),
                    MsgTypes.MasterFilePush     => JsonSerializer.Deserialize<MasterFilePushCmdMsg>(json, Opts),
                    MsgTypes.SlaveFileResult    => JsonSerializer.Deserialize<SlaveFileResultMsg>(json, Opts),
                    MsgTypes.MasterSelfUpdate   => JsonSerializer.Deserialize<MasterSelfUpdateCmdMsg>(json, Opts),
                    MsgTypes.MasterScreenshot   => JsonSerializer.Deserialize<MasterScreenshotCmdMsg>(json, Opts),
                    MsgTypes.SlaveScreenshot    => JsonSerializer.Deserialize<SlaveScreenshotResultMsg>(json, Opts),
                    MsgTypes.MasterProcCmd      => JsonSerializer.Deserialize<MasterProcCmdMsg>(json, Opts),
                    _ => null
                };
                return msg;
            }
            catch { return null; }
        }
    }
}
