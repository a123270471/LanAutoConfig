namespace LanAutoConfig.Common
{
    /// <summary>失败原因归类结果。</summary>
    public class FailureClass
    {
        /// <summary>归类（如 “IP 地址冲突”）。</summary>
        public string Category { get; set; }
        /// <summary>一句话修复建议。</summary>
        public string Suggestion { get; set; }
        /// <summary>命中的关键字（便于排查，空表示未命中具体规则）。</summary>
        public string Keyword { get; set; }
    }

    /// <summary>
    /// 失败原因归类：把被控端回传的 PowerShell / 系统错误原文，映射成可读的“问题类别 + 修复建议”。
    /// 纯字符串匹配，不依赖任何第三方库，可直接单测。
    /// 注意：PowerShell 默认英文，所以关键字以英文为主、中文为辅。
    /// </summary>
    public static class FailureClassifier
    {
        // 顺序敏感：越靠前越具体，先匹配到就返回。
        private static readonly (string Pattern, string Category, string Suggestion)[] Rules =
        {
            // —— IP 冲突 ——（注意：PowerShell 报“重名计算机”也是 “is already in use”，所以 IP 规则必须更具体，认 “ip address”）
            ("ip address", "IP 地址冲突",
                "该 IP 已被其它机器占用（或本机旧地址未释放）。请换一个未被占用的后缀编号，或在被控端先执行 `ipconfig /release && ipconfig /renew` 释放旧地址后重试。"),
            ("ip address conflict", "IP 地址冲突", "检测到 IP 冲突，请更换后缀编号或排查局域网内重复 IP。"),

            // —— 执行策略被组策略禁止 ——
            ("disabled by policy", "执行策略被组策略禁止",
                "本机 PowerShell 执行策略被组策略禁用（Set-ExecutionPolicy 受限）。脚本已用 -ExecutionPolicy Bypass 仍失败的话，需联系域管放宽策略，或改用“命令(cmd)”方式下发。"),
            ("execution policies", "执行策略被组策略禁止", "PowerShell 执行策略受限，请确认组策略是否禁止脚本执行。"),

            // —— 权限不足 ——
            ("access is denied", "权限不足", "操作需要管理员权限。请右键“以管理员身份运行”被控端后重试。"),
            ("requires elevated", "权限不足", "该操作需要提升的权限，请以管理员身份运行被控端。"),
            ("elevated privileges", "权限不足", "需要管理员权限，请“以管理员身份运行”被控端。"),

            // —— 网卡名变更 / 找不到网卡 ——
            ("cannot find interface", "网卡名变更/未找到网卡", "按模板里的网卡名(InterfaceAlias)找不到对应网卡，可能已改名或换槽。请在被控端“本机网卡”里重新选择正确的网卡后再上报。"),
            ("no matching interface", "网卡名变更/未找到网卡", "未找到匹配的网卡，请重新选择网卡。"),
            ("interfacealias", "网卡名变更/未找到网卡", "网卡别名(InterfaceAlias)不匹配，可能被重命名过，请在被控端重新选择网卡。"),
            ("the object is already bound", "网卡名变更/未找到网卡", "网卡绑定状态异常，请重新选择网卡后重试。"),

            // —— 目标已存在同名主机 ——
            ("already exists", "目标已存在同名主机", "局域网内已存在同名计算机。请更换主机名前缀/后缀编号，避免重名。"),
            ("the name", "目标已存在同名主机", "该计算机名已被占用（PowerShell 报错含 “The name 'X' is already in use”），请更换后缀编号。"),
            ("spn", "目标已存在同名主机", "计算机名冲突（可能与域账号 SPN 冲突），请更换主机名。"),

            // —— DNS 设置失败 ——
            ("set-dnsclientserveraddress", "DNS 设置失败", "DNS 服务器地址设置失败，检查 DNS 地址格式（如 223.5.5.5），并确认网卡别名正确。"),

            // —— 网络中断 / 连接失败（改 IP 后回传不上来最常见）——
            ("could not be reached", "网络中断或连接失败", "执行后网络中断、无法与控制端回传结果，通常是改 IP 后原地址失效或临时地址未清理。请确认控制端 IP 与网段配置，必要时用“允许临时改网卡互连”。"),
            ("no such host", "网络中断或连接失败", "主机不可达，检查网线与交换机端口。"),
            ("timed out", "网络中断或连接失败", "连接超时，可能网卡尚未就绪或网络抖动，稍后重试。"),
            ("the network path was not found", "网络中断或连接失败", "网络路径不存在，检查网段与路由。"),

            // —— 前缀/掩码非法 ——
            ("prefixlength", "IP/掩码参数非法", "前缀长度(PrefixLength)不合法，请检查子网掩码是否连续掩码（如 255.255.255.0）。"),
            ("invalid parameter", "IP/掩码参数非法", "参数非法，请检查填写的 IP / 掩码 / 网关格式。"),
        };

        /// <summary>对错误原文归类。空原文返回“未知”。</summary>
        public static FailureClass Classify(string errorMsg)
        {
            if (string.IsNullOrWhiteSpace(errorMsg))
                return new FailureClass
                {
                    Category = "未知（空错误）",
                    Suggestion = "被控端未返回具体错误，请查看被控端本地日志（%AppData%\\LanAutoConfig\\logs\\slave.log）获取详情。",
                    Keyword = ""
                };

            string lower = errorMsg.ToLowerInvariant();
            foreach (var r in Rules)
            {
                if (lower.Contains(r.Pattern.ToLowerInvariant()))
                    return new FailureClass { Category = r.Category, Suggestion = r.Suggestion, Keyword = r.Pattern };
            }
            return new FailureClass
            {
                Category = "其他",
                Suggestion = "未匹配到已知分类。请查看被控端本地日志与下方错误信息原文，必要时手动在被控端执行相同 PowerShell 命令复现。",
                Keyword = ""
            };
        }
    }
}
