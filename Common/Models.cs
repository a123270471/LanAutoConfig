namespace LanAutoConfig.Common
{
    /// <summary>终端上报状态。</summary>
    public enum SlaveStatus
    {
        Reported = 0,  // 已上报待审核
        Executing = 1, // 已下发执行
        Success = 2,   // 成功
        Failed = 3,    // 失败
        Skipped = 4    // 标记跳过执行
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

        public string StatusDisplay => Status switch
        {
            SlaveStatus.Reported => "已上报待审核",
            SlaveStatus.Executing => "执行中",
            SlaveStatus.Success => "成功",
            SlaveStatus.Failed => "失败",
            SlaveStatus.Skipped => "已跳过",
            _ => Status.ToString()
        };

        /// <summary>从被控端上报消息填充（保留已存在项的状态与错误，状态由 SessionManager 决定）。</summary>
        public void ApplyReport(SlaveReportMsg m)
        {
            SourceIp = m.LocalSourceIp;
            Suffix = m.Suffix;
            TargetHostName = m.TargetHostName;
            TargetIp = m.TargetIp;
            SubnetMask = m.SubnetMask;
            Gateway = m.Gateway;
            Dns1 = m.Dns1;
            Dns2 = m.Dns2;
            IsDhcp = m.IsDhcp;
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

        public static TemplateModel From(BroadcastTemplateMsg m) => new TemplateModel
        {
            IpPrefix = m.IpPrefix, SubnetMask = m.SubnetMask, Gateway = m.Gateway,
            Dns1 = m.Dns1, Dns2 = m.Dns2, HostNamePrefix = m.HostNamePrefix, IsDhcp = m.IsDhcp
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
