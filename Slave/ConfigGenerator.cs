using LanAutoConfig.Common;

namespace LanAutoConfig.Slave
{
    /// <summary>配置生成器：后缀编号 → 完整主机名 / 完整 IP（文档 2.2 节拼接逻辑）。</summary>
    public static class ConfigGenerator
    {
        /// <summary>后缀编号与 IP 末段的偏移：IP 末段 = 后缀编号 + 1（编号 1 → .2，把 .1 留给网关）。</summary>
        public const int IpOctetOffset = 1;

        /// <summary>
        /// 拼接计算。成功返回 true；失败返回 false 并通过 error 给出具体原因。
        /// 规则：
        ///  1. 主机名 = 前缀 + 后缀原文（如 PC-ROOM01-002，保留前导零）
        ///  2. IP 末段 = 后缀数字 + 1（后缀 "5" → .6，"002" → .3），把 .1 留给网关/交换设备
        ///  3. 末段不能为 0（网段地址）或 255（广播地址），不能等于网关
        /// </summary>
        public static bool Generate(TemplateModel t, string suffix,
            out string hostName, out string targetIp, out string error)
        {
            hostName = null; targetIp = null; error = null;
            if (t == null) { error = "尚未收到模板"; return false; }
            if (!Validators.IsValidSuffix(suffix)) { error = "后缀仅允许数字或短横线"; return false; }
            suffix = suffix.Trim();

            hostName = t.HostNamePrefix + suffix;
            if (!Validators.IsValidHostName(hostName)) { error = "完整主机名超长或含非法字符"; return false; }

            if (t.IsDhcp)
            {
                targetIp = ""; // DHCP 模式不拼接 IP
                return true;
            }

            // 后缀里的纯数字编号（去前导零）；含非数字则无法组成 IP
            string digits = NormalizeOctet(suffix);
            if (digits == null) { error = "后缀含非数字，无法生成 IP，请使用纯数字编号"; return false; }
            if (!int.TryParse(digits, out int number)) { error = $"后缀编号 {digits} 不是合法数字"; return false; }

            int octet = number + IpOctetOffset;
            if (octet > 255)
            {
                error = $"后缀编号 {number} + 1 = {octet}，超出 IP 末段范围（0-255），请更换编号";
                return false;
            }

            targetIp = t.IpPrefix + octet.ToString();
            if (!Validators.IsValidIp(targetIp)) { error = "生成的 IP 不合法"; return false; }

            if (octet == 0) { error = "IP 末段不能为 0（网段地址），请更换后缀编号"; return false; } // 偏移 1 后实际不会出现，保留兜底
            if (octet == 255) { error = $"后缀编号 {number} + 1 = 255 是广播地址，请更换后缀编号"; return false; }
            if (string.Equals(targetIp, t.Gateway, StringComparison.OrdinalIgnoreCase))
            { error = $"后缀编号 {number} + 1 得到 {targetIp}，与网关相同，请更换后缀编号"; return false; }

            return true;
        }

        /// <summary>取后缀的纯数字并去前导零；含非数字返回 null。</summary>
        private static string NormalizeOctet(string suffix)
        {
            if (string.IsNullOrEmpty(suffix) || !suffix.All(char.IsDigit)) return null;
            string trimmed = suffix.TrimStart('0');
            return trimmed.Length == 0 ? "0" : trimmed;
        }
    }
}
