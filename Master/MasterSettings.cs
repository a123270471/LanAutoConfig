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
        /// <summary>加域用的域名（空表示不加域）。</summary>
        public string Domain { get; set; }
        /// <summary>加域账号。</summary>
        public string DomainUser { get; set; }
        /// <summary>
        /// 加域密码。以明文保存在本机配置文件里（%AppData%），同机的管理员能读到；
        /// 现场请使用「只能把计算机加入指定 OU」的专用低权限账号，不要用域管理员账号。
        /// </summary>
        public string DomainPassword { get; set; }
        /// <summary>目标 OU（可空）。</summary>
        public string OuPath { get; set; }
        /// <summary>列表里被隐藏的列（列名），下次启动保持同样的显示。</summary>
        public List<string> HiddenColumns { get; set; } = new List<string>();
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
                s.HiddenColumns ??= new List<string>();
                return s;
            }
            catch { return null; }
        }

        /// <summary>最近一次保存失败的原因（成功时为 null），供界面提示用。</summary>
        public string LastSaveError { get; private set; }

        public bool Save(string path)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                SavedAt = DateTime.Now;
                // 先写临时文件再替换：File.Move(overwrite) 走的是 MoveFileEx 的原子替换。
                // 原来写的是 Delete + Move —— 两步之间进程被杀/断电，配置就直接没了（终端列表全丢）。
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts), Encoding.UTF8);
                File.Move(tmp, path, overwrite: true);
                LastSaveError = null;
                return true;
            }
            catch (Exception ex)
            {
                // 原来是空 catch：磁盘满/被安全软件锁文件时静默失败，用户只觉得“配置怎么老记不住”
                LastSaveError = ex.Message;
                try { File.Delete(path + ".tmp"); } catch { }
                return false;
            }
        }
    }
}
