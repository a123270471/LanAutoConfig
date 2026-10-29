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

        /// <summary>
        /// 本机身份令牌（随机生成一次后固定不变）。控制端下发指令时必须带回它，
        /// 同网段的其它机器不知道这个值，因此无法伪造指令。见 Common/NetMessages.cs 的 Token 说明。
        /// </summary>
        public string Token { get; set; }

        /// <summary>取令牌，没有就生成一个。生成结果需要调用方 Save()。</summary>
        public string EnsureToken()
        {
            if (string.IsNullOrWhiteSpace(Token))
                Token = Guid.NewGuid().ToString("N");
            return Token;
        }

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
