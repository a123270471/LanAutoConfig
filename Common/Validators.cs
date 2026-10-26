using System.Text.RegularExpressions;

namespace LanAutoConfig.Common
{
    /// <summary>参数合法性校验（文档 11 节第 5 条：后缀、主机名、IP 格式）。</summary>
    public static class Validators
    {
        /// <summary>
        /// 严格 IPv4 校验：必须 4 段十进制、无前导零、每段 0-255。
        /// 不用 IPAddress.TryParse，因为它会接受 "1"、"192.168.1" 这类缩写形式（会被 PowerShell 拒绝或解释成别的地址）。
        /// </summary>
        public static bool IsValidIp(string s) => TryParseIpv4(s, out _);

        /// <summary>严格解析 IPv4 并输出地址字节。</summary>
        public static bool TryParseIpv4(string s, out byte[] bytes)
        {
            bytes = null;
            if (string.IsNullOrWhiteSpace(s)) return false;
            string[] segs = s.Trim().Split('.');
            if (segs.Length != 4) return false;

            var octets = new int[4];
            for (int i = 0; i < 4; i++)
            {
                string seg = segs[i];
                if (seg.Length == 0 || seg.Length > 3) return false;
                if (seg.Length > 1 && seg[0] == '0') return false; // 拒绝前导零（192.168.01.1）
                int v = 0;
                foreach (char c in seg)
                {
                    if (c < '0' || c > '9') return false;
                    v = v * 10 + (c - '0');
                }
                if (v > 255) return false;
                octets[i] = v;
            }
            bytes = new byte[] { (byte)octets[0], (byte)octets[1], (byte)octets[2], (byte)octets[3] };
            return true;
        }

        /// <summary>子网掩码校验（必须是合法的连续 1 掩码）。</summary>
        public static bool IsValidMask(string s)
        {
            if (!TryParseIpv4(s, out byte[] b)) return false;
            bool zeroSeen = false;
            foreach (byte x in b)
            {
                for (int i = 7; i >= 0; i--)
                {
                    bool bit = (x & (1 << i)) != 0;
                    if (!bit) zeroSeen = true;
                    else if (zeroSeen) return false;
                }
            }
            return true;
        }

        /// <summary>后缀：只允许数字与短横线（首尾空格会被忽略）。</summary>
        public static bool IsValidSuffix(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            string t = s.Trim();
            if (t.Length == 0) return false;
            foreach (char c in t)
            {
                if (!((c >= '0' && c <= '9') || c == '-')) return false;
            }
            return true;
        }

        /// <summary>
        /// 主机名校验：最大 15 字符，仅 ASCII 字母/数字/短横线，首尾不能是短横线，不能全为数字。
        /// （中文等非 ASCII 字符无法作为 Windows 计算机名，这里直接拒绝，避免下发后才失败。）
        /// </summary>
        public static bool IsValidHostName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            string t = s.Trim();
            if (t.Length == 0 || t.Length > 15) return false;
            if (t[0] == '-' || t[t.Length - 1] == '-') return false;

            bool allDigits = true;
            foreach (char c in t)
            {
                bool digit = c >= '0' && c <= '9';
                bool letter = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
                if (!digit && !letter && c != '-') return false;
                if (!digit) allDigits = false;
            }
            return !allDigits; // 全数字不是合法主机名
        }

        /// <summary>IP 前缀：如 "192.168.1."，末尾必须带点且前三段合法。</summary>
        public static bool IsValidIpPrefix(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            string t = s.Trim();
            if (!t.EndsWith(".")) return false;
            return IsValidIp(t + "1");
        }

        /// <summary>子网掩码转前缀长度（255.255.255.0 -> 24）。非法掩码回退 24。</summary>
        public static int MaskToPrefixLength(string mask)
        {
            if (!TryParseIpv4(mask, out byte[] b)) return 24;
            int n = 0;
            foreach (byte x in b)
            {
                byte v = x;
                while (v != 0) { n++; v = (byte)(v << 1); }
            }
            return n;
        }

        private static readonly Regex MacRegex = new Regex("^([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}$");

        /// <summary>MAC 格式校验。</summary>
        public static bool IsValidMac(string s) => s != null && MacRegex.IsMatch(s.Trim());
    }
}
