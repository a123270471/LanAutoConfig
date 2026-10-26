using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanAutoConfig.Common
{
    /// <summary>消息类型枚举（字符串常量，与文档 MsgType 枚举一致）。</summary>
    public static class MsgTypes
    {
        public const string BroadcastTemplate  = "BroadcastTemplate";  // UDP广播：控制端下发模板
        public const string SlaveReport        = "SlaveReport";        // TCP：被控端上报预览配置
        public const string MasterExecuteCmd   = "MasterExecuteCmd";   // TCP：控制端下发最终执行配置
        public const string SlaveResultReport  = "SlaveResultReport";  // TCP：被控端上报执行结果
        public const string MasterResetSession = "MasterResetSession"; // TCP：控制端重置会话，通知终端清空本地
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

        public BroadcastTemplateMsg() : base() { }
        public BroadcastTemplateMsg(string sessionId, string ipPrefix, string mask, string gw,
            string dns1, string dns2, string hnPrefix, bool isDhcp)
            : base(MsgTypes.BroadcastTemplate, sessionId)
        {
            IpPrefix = ipPrefix; SubnetMask = mask; Gateway = gw;
            Dns1 = dns1; Dns2 = dns2; HostNamePrefix = hnPrefix; IsDhcp = isDhcp;
        }
        protected override string StableBody() => $"{IpPrefix}|{SubnetMask}|{Gateway}|{Dns1}|{Dns2}|{HostNamePrefix}|{IsDhcp}";
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

        public SlaveReportMsg() : base() { }
        public SlaveReportMsg(string sessionId) : base(MsgTypes.SlaveReport, sessionId) { }
        protected override string StableBody() => $"{LocalMac}|{LocalSourceIp}|{Suffix}|{TargetHostName}|{TargetIp}|{SubnetMask}|{Gateway}|{Dns1}|{Dns2}|{IsDhcp}";
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

        public MasterExecuteCmdMsg() : base() { }
        public MasterExecuteCmdMsg(string sessionId) : base(MsgTypes.MasterExecuteCmd, sessionId) { }
        protected override string StableBody() => $"{LocalMac}|{TargetHostName}|{TargetIp}|{SubnetMask}|{Gateway}|{Dns1}|{Dns2}|{IsDhcp}|{NeedAutoReboot}";
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
                    _ => null
                };
                return msg;
            }
            catch { return null; }
        }
    }
}
