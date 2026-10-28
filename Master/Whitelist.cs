using LanAutoConfig.Common;

namespace LanAutoConfig.Master
{
    /// <summary>
    /// MAC 白名单：勾选“启用白名单”后，只接受名单里的 MAC 上报/心跳，其余一律拒绝并记日志。
    ///
    /// 与黑名单的关系：黑名单先判（明确拒绝），再判白名单（未登记就拒绝）。
    /// 白名单为空且启用时，等于“拒绝所有”，界面会提示这一点，避免误操作后什么都收不到。
    /// 持久化到 whitelist.txt（每行一个 MAC，# 开头为注释）。
    /// </summary>
    public class Whitelist
    {
        private readonly HashSet<string> _macs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();
        private readonly string _filePath;

        public Whitelist(string filePath)
        {
            _filePath = filePath;
            Load();
        }

        /// <summary>是否启用白名单（启用后只放行名单里的 MAC）。</summary>
        public bool Enabled { get; private set; }

        public void Load()
        {
            lock (_lock)
            {
                _macs.Clear();
                Enabled = false;
                if (!File.Exists(_filePath)) return;
                foreach (var line in File.ReadAllLines(_filePath))
                {
                    var s = line.Trim();
                    if (s.Length == 0) continue;
                    if (s.StartsWith("#"))
                    {
                        if (s.IndexOf("enabled", StringComparison.OrdinalIgnoreCase) >= 0) Enabled = true;
                        continue;
                    }
                    if (Validators.IsValidMac(s)) _macs.Add(Normalize(s));
                }
            }
        }

        public void Save()
        {
            lock (_lock)
            {
                try
                {
                    var dir = Path.GetDirectoryName(_filePath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    var sb = new StringBuilder();
                    sb.AppendLine("# LanAutoConfig whitelist: 每行一个 MAC；下面这行带 enabled 表示启用白名单");
                    sb.AppendLine(Enabled ? "# enabled" : "# disabled");
                    foreach (var m in _macs.OrderBy(x => x)) sb.AppendLine(m);
                    File.WriteAllText(_filePath, sb.ToString(), Encoding.UTF8);
                }
                catch { }
            }
        }

        public void SetEnabled(bool on)
        {
            lock (_lock) { Enabled = on; Save(); }
        }

        /// <summary>是否放行该 MAC（未启用白名单时一律放行）。</summary>
        public bool Allows(string mac)
        {
            lock (_lock)
            {
                if (!Enabled) return true;
                if (string.IsNullOrWhiteSpace(mac)) return false;
                return _macs.Contains(Normalize(mac));
            }
        }

        public bool AddMac(string mac)
        {
            if (!Validators.IsValidMac(mac)) return false;
            lock (_lock)
            {
                if (!_macs.Add(Normalize(mac))) return false;
                Save();
                return true;
            }
        }

        public bool RemoveMac(string mac)
        {
            if (string.IsNullOrWhiteSpace(mac)) return false;
            lock (_lock)
            {
                if (!_macs.Remove(Normalize(mac))) return false;
                Save();
                return true;
            }
        }

        public void Clear()
        {
            lock (_lock) { _macs.Clear(); Save(); }
        }

        public IReadOnlyList<string> Macs { get { lock (_lock) return _macs.OrderBy(x => x).ToList(); } }

        private static string Normalize(string mac) => mac.Trim().Replace(":", "-").ToUpperInvariant();
    }
}
