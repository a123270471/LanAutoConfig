namespace LanAutoConfig.Common
{
    /// <summary>
    /// 后缀编号规划：按顺序发号，跳过已被占用的号，保证一批机器不会撞号。
    /// 规则与 ConfigGenerator 对齐 —— 编号 n 对应 IP 末段 n+1，所以 n 最大 253（254 是可用末段，255 是广播）。
    /// </summary>
    public static class SuffixPlanner
    {
        /// <summary>最大可分配编号（253 + 1 = 254，再往上就是广播地址了）。</summary>
        public const int MaxSuffix = 253;

        /// <summary>
        /// 规划 count 个连续、不与 usedSuffixes 冲突的编号。
        /// 返回的字符串保留两位前导零（1 → "01"，100 → "100"），因为主机名要用原文拼。
        /// 号段不够时返回能排出来的部分，调用方据此提示“编号已用尽”。
        /// </summary>
        public static List<string> Plan(IEnumerable<string> usedSuffixes, int count)
        {
            var used = new HashSet<int>();
            foreach (var s in usedSuffixes ?? Enumerable.Empty<string>())
                if (int.TryParse((s ?? "").Trim(), out int n) && n > 0) used.Add(n);

            var result = new List<string>();
            if (count <= 0) return result;

            int next = 1;
            while (result.Count < count)
            {
                while (next <= MaxSuffix && used.Contains(next)) next++;
                if (next > MaxSuffix) break;
                result.Add(next.ToString("00"));
                used.Add(next);
                next++;
            }
            return result;
        }
    }
}
