namespace LanAutoConfig.Common
{
    /// <summary>终端上报状态。</summary>
    public enum SlaveStatus
    {
        Reported = 0,  // 已上报待审核
        Executing = 1, // 已下发执行
        Success = 2,   // 成功
        Failed = 3,    // 失败
        Skipped = 4,   // 标记跳过执行
        Online = 5     // 只收到心跳、还没上报过（在线未上报）
    }

    /// <summary>
    /// 被控端硬件/资产信息（批量装机后自动建资产库用）。
    /// 字段都做成“取不到就空”，不要让某一项 WMI 失败影响整条上报。
    /// </summary>
    public class HardwareInfo
    {
        public string Cpu { get; set; }            // 型号，如 Intel(R) Core(TM) i5-10400 CPU @ 2.90GHz
        public int CpuCores { get; set; }          // 物理核数
        public long MemoryBytes { get; set; }      // 物理内存总量
        public long DiskBytes { get; set; }        // 系统盘容量
        public string DiskModel { get; set; }      // 系统盘型号
        public string BaseBoard { get; set; }      // 主板型号
        public string SerialNumber { get; set; }   // 整机/主板序列号（优先 BIOS 里的整机 SN）
        public string BiosVersion { get; set; }
        public string BiosDate { get; set; }
        public string Manufacturer { get; set; }
        public string Model { get; set; }
        public string OsVersion { get; set; }
        public string MachineName { get; set; }

        public string MemoryDisplay => MemoryBytes <= 0 ? "" : (MemoryBytes / 1024d / 1024d / 1024d).ToString("0.#") + " GB";
        public string DiskDisplay => DiskBytes <= 0 ? "" : (DiskBytes / 1024d / 1024d / 1024d).ToString("0") + " GB";

        /// <summary>参与 Check 计算的稳定摘要（两端算法一致）。</summary>
        public string Signature()
            => $"{Cpu}|{CpuCores}|{MemoryBytes}|{DiskBytes}|{DiskModel}|{BaseBoard}|{SerialNumber}|{BiosVersion}|{BiosDate}|{Manufacturer}|{Model}|{OsVersion}|{MachineName}";
    }

    /// <summary>验收状态（重启后自动验收 / 手动验收的结论）。</summary>
    public enum VerifyStatus
    {
        None = 0,   // 未验收
        Pass = 1,   // 验收通过（实际配置与期望一致）
        Fail = 2    // 验收不符（实际配置与期望不一致）
    }

    /// <summary>控制端内存会话实体（对应文档 7 节 SlaveSessionItem）。</summary>
    public class SlaveSessionItem
    {
        public string Mac { get; set; }
        public string SourceIp { get; set; }
        public string Suffix { get; set; }
        public string TargetHostName { get; set; }
        public string TargetIp { get; set; }
        public string SubnetMask { get; set; }
        public string Gateway { get; set; }
        public string Dns1 { get; set; }
        public string Dns2 { get; set; }
        public bool IsDhcp { get; set; }
        /// <summary>状态：Reported / Executing / Success / Failed / Skipped。</summary>
        public SlaveStatus Status { get; set; } = SlaveStatus.Reported;
        public string ErrorMsg { get; set; }
        /// <summary>是否标记跳过执行。</summary>
        public bool Skip { get; set; }
        /// <summary>下发执行的时刻（用于回收“执行中”卡死的记录）；未下发或已回收为 null。</summary>
        public DateTime? DispatchTime { get; set; }

        /// <summary>硬件/资产信息（随上报带上来，可能为空）。</summary>
        public HardwareInfo Hardware { get; set; }

        /// <summary>最后一次收到心跳/上报的时间（本地时间）。</summary>
        public DateTime LastSeen { get; set; } = DateTime.MinValue;

        /// <summary>在线：最近一个心跳周期内收到过心跳或上报。</summary>
        public bool Online => LastSeen != DateTime.MinValue
                              && (DateTime.Now - LastSeen).TotalMilliseconds < AppConfig.OfflineAfterMs;

        public string OnlineDisplay => Online ? "在线" : "离线";

        public string StatusDisplay => Status switch
        {
            SlaveStatus.Reported => "已上报待审核",
            SlaveStatus.Executing => "执行中",
            SlaveStatus.Success => "成功",
            SlaveStatus.Failed => "失败",
            SlaveStatus.Skipped => "已跳过",
            SlaveStatus.Online => "在线未上报",
            _ => Status.ToString()
        };

        /// <summary>资产摘要（列表里一列显示，详情另看）。</summary>
        public string HardwareSummary
        {
            get
            {
                if (Hardware == null) return "";
                string cpu = string.IsNullOrEmpty(Hardware.Cpu) ? "" : Hardware.Cpu.Split('@')[0].Trim();
                if (cpu.Length > 34) cpu = cpu.Substring(0, 34) + "…";
                var parts = new List<string>();
                if (cpu.Length > 0) parts.Add(cpu);
                if (!string.IsNullOrEmpty(Hardware.MemoryDisplay)) parts.Add("内存 " + Hardware.MemoryDisplay);
                if (!string.IsNullOrEmpty(Hardware.DiskDisplay)) parts.Add("磁盘 " + Hardware.DiskDisplay);
                return string.Join(" / ", parts);
            }
        }

        /// <summary>最后一次验收结论（None/Pass/Fail）。</summary>
        public VerifyStatus VerifyStatus { get; set; } = VerifyStatus.None;
        /// <summary>验收时回传的实际计算机名 / IP（用于不符时排查）。</summary>
        public string VerifyActual { get; set; }
        /// <summary>验收备注（如“主机名不符：实际 A / 期望 B”）。</summary>
        public string VerifyNote { get; set; }
        /// <summary>失败原因归类（由 FailureClassifier 产出，空表示成功或尚未归类）。</summary>
        public string FailCategory { get; set; }
        /// <summary>失败归类对应的修复建议。</summary>
        public string FailSuggestion { get; set; }

        /// <summary>
        /// 该终端的身份令牌（第一次收到它的上报/心跳时记下来，之后每条指令都带回）。
        /// 被控端收到指令会校验“令牌是否为本机令牌”，不符直接丢弃 —— 见 NetMessage.Token。
        /// </summary>
        public string AuthToken { get; set; }
        /// <summary>最近一次连通性探测结果（“12ms”/“不通”），仅界面展示用。</summary>
        public string ProbeText { get; set; }
        /// <summary>最近一次 IP 冲突扫描结论（如“该 IP 已被 00-11-22-33-44-55 占用”）。</summary>
        public string IpConflictNote { get; set; }

        /// <summary>从被控端上报消息填充（保留已存在项的状态与错误，状态由 SessionManager 决定）。</summary>
        public void ApplyReport(SlaveReportMsg m)
        {
            // 优先用控制端实测到的来源 IP（TCP 连接的 remote endpoint）——它一定是可达的；
            // 上报里的 LocalSourceIp 是发送方自己算的，断网/换地址时可能不准。
            SourceIp = !string.IsNullOrEmpty(m.RemoteIp) ? m.RemoteIp : m.LocalSourceIp;
            Suffix = m.Suffix;
            TargetHostName = m.TargetHostName;
            TargetIp = m.TargetIp;
            SubnetMask = m.SubnetMask;
            Gateway = m.Gateway;
            Dns1 = m.Dns1;
            Dns2 = m.Dns2;
            IsDhcp = m.IsDhcp;
            if (m.Hardware != null) Hardware = m.Hardware;
            // 上报里带着该终端的身份令牌：记下来，之后每条指令都原样带回，
            // 被控端据此确认“这条指令确实来自和它对过话的控制端”。
            if (!string.IsNullOrEmpty(m.Token)) AuthToken = m.Token;
            LastSeen = DateTime.Now;
        }
    }

    /// <summary>控制端下发的模板（由 BroadcastTemplateMsg 落地）。</summary>
    public class TemplateModel
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

        public static TemplateModel From(BroadcastTemplateMsg m) => new TemplateModel
        {
            IpPrefix = m.IpPrefix, SubnetMask = m.SubnetMask, Gateway = m.Gateway,
            Dns1 = m.Dns1, Dns2 = m.Dns2, HostNamePrefix = m.HostNamePrefix, IsDhcp = m.IsDhcp,
            TimeZoneId = m.TimeZoneId, SyncTime = m.SyncTime
        };
    }

    /// <summary>控制端 SQLite OpLog 记录。</summary>
    public class OpLogRecord
    {
        public long LogId { get; set; }
        public string SessionId { get; set; }
        public DateTime OpTime { get; set; }
        public string Mac { get; set; }
        public string SourceIp { get; set; }
        public string TargetHostName { get; set; }
        public string TargetIp { get; set; }
        public string Result { get; set; }
        public string ErrorMsg { get; set; }
    }

    /// <summary>网卡信息（被控端网卡选择用）。</summary>
    public class NetworkAdapterInfo
    {
        public string Alias { get; set; }
        public string Mac { get; set; }
        public bool Up { get; set; }
        /// <summary>是否虚拟/隧道类网卡（Hyper-V、VMware 等），列表里排在后面。</summary>
        public bool Virtual { get; set; }
        public string StatusText => Up ? "已连接" : "未连接";
        public override string ToString() => $"{Alias}  [{Mac}]  {StatusText}" + (Virtual ? "  (虚拟)" : "");
    }
}
