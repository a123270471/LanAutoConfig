using LanAutoConfig.Common;

namespace LanAutoConfig.Master
{
    /// <summary>
    /// 冲突校验（文档 10 节）：目标 IP 重复 / 目标计算机名重复 → 标红并禁止下发。
    /// 被标记“跳过”的终端不参与冲突判定，否则用户无法通过跳过一台来解决冲突。
    /// </summary>
    public static class ConflictValidator
    {
        /// <summary>只统计真正会被下发的终端（跳过的不算）。</summary>
        private static IEnumerable<SlaveSessionItem> Active(IEnumerable<SlaveSessionItem> items)
            => items.Where(x => !x.Skip && x.Status != SlaveStatus.Skipped);

        /// <summary>返回列表内重复的目标 IP 集合（忽略大小写）。</summary>
        public static HashSet<string> GetConflictIps(IEnumerable<SlaveSessionItem> items)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in Active(items).Where(x => !string.IsNullOrWhiteSpace(x.TargetIp))
                     .GroupBy(x => x.TargetIp.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                if (g.Count() > 1) set.Add(g.Key);
            }
            return set;
        }

        /// <summary>返回列表内重复的目标主机名集合。</summary>
        public static HashSet<string> GetConflictHosts(IEnumerable<SlaveSessionItem> items)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in Active(items).Where(x => !string.IsNullOrWhiteSpace(x.TargetHostName))
                     .GroupBy(x => x.TargetHostName.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                if (g.Count() > 1) set.Add(g.Key);
            }
            return set;
        }

        /// <summary>是否存在冲突（禁止下发）。</summary>
        public static bool HasConflict(IEnumerable<SlaveSessionItem> items)
            => GetConflictIps(items).Count > 0 || GetConflictHosts(items).Count > 0;
    }
}
