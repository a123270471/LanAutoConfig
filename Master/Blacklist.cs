using LanAutoConfig.Common;

namespace LanAutoConfig.Master
{
    /// <summary>MAC/IP 黑名单：拒绝接收该终端上报（持久化到文本文件）。</summary>
    public class Blacklist
    {
        private readonly HashSet<string> _macs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _ips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();
        private readonly string _filePath;

        public Blacklist(string filePath)
        {
            _filePath = filePath;
            Load();
        }

        public void Load()
        {
            lock (_lock)
            {
                _macs.Clear(); _ips.Clear();
                if (!File.Exists(_filePath)) return;
                foreach (var line in File.ReadAllLines(_filePath))
                {
                    var s = line.Trim();
                    if (s.Length == 0 || s.StartsWith("#")) continue;
                    if (Validators.IsValidMac(s)) _macs.Add(s.Replace(":", "-"));
                    else if (Validators.IsValidIp(s)) _ips.Add(s);
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
                    var sb = new StringBuilder("# LanAutoConfig blacklist: MAC or IP per line" + Environment.NewLine);
                    foreach (var m in _macs.OrderBy(x => x)) sb.AppendLine(m);
                    foreach (var i in _ips.OrderBy(x => x)) sb.AppendLine(i);
                    File.WriteAllText(_filePath, sb.ToString(), Encoding.UTF8);
                }
                catch { }
            }
        }

        public bool IsBlocked(string mac, string ip)
        {
            lock (_lock)
            {
                return (!string.IsNullOrEmpty(mac) && _macs.Contains(mac.Replace(":", "-")))
                    || (!string.IsNullOrEmpty(ip) && _ips.Contains(ip));
            }
        }

        public bool AddMac(string mac)
        {
            lock (_lock) { if (!_macs.Add(mac.Replace(":", "-"))) return false; Save(); return true; }
        }

        public bool AddIp(string ip)
        {
            lock (_lock) { if (!_ips.Add(ip)) return false; Save(); return true; }
        }

        /// <summary>移除一条 MAC 黑名单。</summary>
        public bool RemoveMac(string mac)
        {
            if (string.IsNullOrWhiteSpace(mac)) return false;
            lock (_lock) { if (!_macs.Remove(mac.Trim().Replace(":", "-"))) return false; Save(); return true; }
        }

        /// <summary>移除一条 IP 黑名单。</summary>
        public bool RemoveIp(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return false;
            lock (_lock) { if (!_ips.Remove(ip.Trim())) return false; Save(); return true; }
        }

        /// <summary>清空全部黑名单。</summary>
        public void Clear()
        {
            lock (_lock) { _macs.Clear(); _ips.Clear(); Save(); }
        }

        public IReadOnlyList<string> Macs { get { lock (_lock) return _macs.ToList(); } }
        public IReadOnlyList<string> Ips { get { lock (_lock) return _ips.ToList(); } }
    }
}
