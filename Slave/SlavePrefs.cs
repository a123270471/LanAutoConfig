using System.Text.Json;

namespace LanAutoConfig.Slave
{
    /// <summary>
    /// 被控端界面偏好（目前只有“开机自动启动”开关）。
    /// 单独存一份，避免与 --install-autostart / --suffix 等命令行参数互相覆盖：
    /// 命令行管“这一次怎么跑”，这里管“界面上的开关记不记得住”。
    /// Autostart 为 null 表示操作员还没选过，按默认（开启）处理。
    /// </summary>
    public class SlavePrefs
    {
        /// <summary>开机自动启动：true/false = 操作员的选择；null = 没选过，按默认开启。</summary>
        public bool? Autostart { get; set; }

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        public static SlavePrefs Load()
        {
            try
            {
                string path = AppConfig.SlavePrefsPath;
                if (!File.Exists(path)) return new SlavePrefs();
                return JsonSerializer.Deserialize<SlavePrefs>(File.ReadAllText(path, Encoding.UTF8), JsonOpts)
                       ?? new SlavePrefs();
            }
            catch { return new SlavePrefs(); }
        }

        public bool Save()
        {
            try
            {
                string path = AppConfig.SlavePrefsPath;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts), new UTF8Encoding(false));
                return true;
            }
            catch { return false; }
        }
    }
}
