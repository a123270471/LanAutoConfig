using System.Text.Json;
using LanAutoConfig.Common;

namespace LanAutoConfig.Master
{
    /// <summary>
    /// 控制端持久化配置：上次的模板参数、广播网卡、当前会话 ID 与终端列表。
    /// 默认存 %AppData%\LanAutoConfig\master-settings.json（%AppData% 不可写时随数据目录回退）。
    /// </summary>
    public class MasterSettings
    {
        public string SessionId { get; set; }
        public string IpPrefix { get; set; } = "192.168.1.";
        public string SubnetMask { get; set; } = "255.255.255.0";
        public string Gateway { get; set; } = "192.168.1.1";
        public string Dns1 { get; set; } = "223.5.5.5";
        public string Dns2 { get; set; } = "223.6.6.6";
        public string HostNamePrefix { get; set; } = "PC-ROOM01-";
        public bool IsDhcp { get; set; }
        public bool DhcpServer { get; set; }
        public string Workgroup { get; set; }
        public string TimeZoneId { get; set; }
        public bool SyncTime { get; set; }
        public string AdapterAlias { get; set; }
        public DateTime SavedAt { get; set; }
        public List<SlaveSessionItem> Items { get; set; } = new List<SlaveSessionItem>();

        private static readonly JsonSerializerOptions Opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public static MasterSettings Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var s = JsonSerializer.Deserialize<MasterSettings>(File.ReadAllText(path, Encoding.UTF8), Opts);
                s.Items ??= new List<SlaveSessionItem>();
                return s;
            }
            catch { return null; }
        }

        public void Save(string path)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                SavedAt = DateTime.Now;
                // 先写临时文件再替换，避免断电/崩溃留下半个 JSON
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts), Encoding.UTF8);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch { }
        }
    }
}
