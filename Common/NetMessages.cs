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
        public const string MasterRebootCmd    = "MasterRebootCmd";    // TCP：控制端下发“重启本机”
        public const string MasterAutostartCmd = "MasterAutostartCmd"; // TCP：控制端批量开关“开机自启动”
        public const string MasterShutdownCmd  = "MasterShutdownCmd";  // TCP：控制端下发“关闭本机”
        public const string MasterAssignSuffix = "MasterAssignSuffixCmd"; // TCP：控制端下发后缀编号，被控端自动上报
        public const string MasterCustomCmd   = "MasterCustomCmd";   // TCP：控制端下发自定义命令/脚本
        public const string SlaveCustomResult = "SlaveCustomResult"; // TCP：被控端回传自定义命令执行结果
        public const string MasterVerifyCmd   = "MasterVerifyCmd";   // TCP：控制端要求被控端上报当前实际配置（用于重启后自动验收）
        public const string SlaveVerifyResult = "SlaveVerifyResult"; // TCP：被控端回传当前实际配置
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

        protected NetMessage() { }
        protected NetMessage(string msgType, string sessionId) { MsgType = msgType; SessionId = sessionId; }

        /// <summary>参与校验的稳定字段（子类实现），用于简单校验码计算。</summary>
        protected abstract string StableBody();

        public void ComputeCheck() => Check = Fnv1a($"{MsgType}|{SessionId}|{Magic}|{StableBody()}");

        /// <summary>校验 Magic 与 Check，过滤无关程序/伪造消息。</summary>
        public bool Validate()
        {
            if (Magic != AppConfig.Magic) return false;
            return Check == Fnv1a($"{MsgType}|{SessionId}|{Magic}|{StableBody()}");
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
        /// <summary>执行后加入的工作组（空表示不改）。</summary>
        public string Workgroup { get; set; }
        /// <summary>时区 ID（空表示不改），如 "China Standard Time"。</summary>
        public string TimeZoneId { get; set; }
        /// <summary>是否与 NTP 服务器对时。</summary>
        public bool SyncTime { get; set; }

        public BroadcastTemplateMsg() : base() { }
        public BroadcastTemplateMsg(string sessionId, string ipPrefix, string mask, string gw,
            string dns1, string dns2, string hnPrefix, bool isDhcp,
            string workgroup = null, string timeZoneId = null, bool syncTime = false)
            : base(MsgTypes.BroadcastTemplate, sessionId)
        {
            IpPrefix = ipPrefix; SubnetMask = mask; Gateway = gw;
            Dns1 = dns1; Dns2 = dns2; HostNamePrefix = hnPrefix; IsDhcp = isDhcp;
            Workgroup = workgroup; TimeZoneId = timeZoneId; SyncTime = syncTime;
        }
        protected override string StableBody()
            => $"{IpPrefix}|{SubnetMask}|{Gateway}|{Dns1}|{Dns2}|{HostNamePrefix}|{IsDhcp}|{Workgroup}|{TimeZoneId}|{SyncTime}";
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
        /// <summary>执行后加入的工作组（空表示不改）。</summary>
        public string Workgroup { get; set; }
        /// <summary>时区 ID（空表示不改）。</summary>
        public string TimeZoneId { get; set; }
        /// <summary>是否与 NTP 对时。</summary>
        public bool SyncTime { get; set; }

        public MasterExecuteCmdMsg() : base() { }
        public MasterExecuteCmdMsg(string sessionId) : base(MsgTypes.MasterExecuteCmd, sessionId) { }
        protected override string StableBody()
            => $"{LocalMac}|{TargetHostName}|{TargetIp}|{SubnetMask}|{Gateway}|{Dns1}|{Dns2}|{IsDhcp}|{NeedAutoReboot}|{Workgroup}|{TimeZoneId}|{SyncTime}";
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
                    MsgTypes.MasterRebootCmd    => JsonSerializer.Deserialize<MasterRebootCmdMsg>(json, Opts),
                    MsgTypes.MasterAutostartCmd => JsonSerializer.Deserialize<MasterAutostartCmdMsg>(json, Opts),
                    MsgTypes.MasterShutdownCmd  => JsonSerializer.Deserialize<MasterShutdownCmdMsg>(json, Opts),
                    MsgTypes.MasterAssignSuffix => JsonSerializer.Deserialize<MasterAssignSuffixCmdMsg>(json, Opts),
                    MsgTypes.MasterCustomCmd    => JsonSerializer.Deserialize<MasterCustomCmdMsg>(json, Opts),
                    MsgTypes.SlaveCustomResult  => JsonSerializer.Deserialize<SlaveCustomResultMsg>(json, Opts),
                    MsgTypes.MasterVerifyCmd    => JsonSerializer.Deserialize<MasterVerifyCmdMsg>(json, Opts),
                    MsgTypes.SlaveVerifyResult  => JsonSerializer.Deserialize<SlaveVerifyResultMsg>(json, Opts),
                    _ => null
                };
                return msg;
            }
            catch { return null; }
        }
    }
}
