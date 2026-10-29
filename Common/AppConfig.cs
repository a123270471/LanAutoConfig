namespace LanAutoConfig
{
    /// <summary>全局配置常量（端口、校验标记、目录等）。</summary>
    public static class AppConfig
    {
        public const int UdpPort = 48888;          // UDP：广播发现、模板推送
        public const int TcpPort = 48889;          // TCP：可靠传输上报、指令、结果
        /// <summary>控制端内置文件服务端口（HTTP）：被控端从这里下载安装包/脚本/新 exe。</summary>
        public const int FileServerPort = 48890;
        /// <summary>
        /// 校验标记，过滤无关 UDP/TCP 包。
        /// V4 起：消息基类新增「本机身份令牌」并纳入校验码 —— 只有真正与控制端配对过的终端
        /// 才能被下发指令（此前任何人只要知道协议格式就能伪造改 IP / 执行脚本 / 重启）。
        /// 两端必须一起升级；混用时会在 Magic 校验处被明确拒绝，并提示“协议版本不一致”。
        /// </summary>
        public const string Magic = "LANAC-V4";
        /// <summary>协议版本号，仅用于日志排查。</summary>
        public const string ProtocolVersion = "4";

        public const int BroadcastIntervalMs = 2500; // 控制端广播一次模板的常规周期
        /// <summary>
        /// 刚点【开始发布模板】/【重置会话】后的快速广播周期：被控端开机后不必等满一个常规周期，
        /// 300ms 内就能收到模板，配合“首条心跳立即发”把上线时间压到 1 秒内。
        /// </summary>
        public const int BroadcastFastIntervalMs = 300;
        /// <summary>快速广播只持续这么久，之后回到常规周期（长期 300ms 广播会白白唤醒全网终端）。</summary>
        public const int BroadcastFastWindowMs = 10000;
        public const int TcpTimeoutMs = 5000;        // TCP 超时 5 秒
        public const int TcpRetry = 1;               // 失败重试 1 次

        /// <summary>被控端心跳间隔（毫秒）。</summary>
        public const int HeartbeatIntervalMs = 10000;
        /// <summary>
        /// 超过这个时间没收到心跳就算离线（2 个心跳周期 + 余量，约 22 秒）。
        /// 原来是 3 个周期 + 余量（32 秒），现场觉得“程序都关了列表还显示在线”太迟钝。
        /// 正常退出另有 SlaveOffline 即时通知，这里只兜住断电/蓝屏/拔网线这类没有告别消息的情况。
        /// </summary>
        public const int OfflineAfterMs = HeartbeatIntervalMs * 2 + 2000;

        /// <summary>
        /// 数据目录：优先 %AppData%\LanAutoConfig（控制端 SQLite + 双方日志）。
        /// 若该目录不可写（受限令牌 / 安全软件 / 只读漫游配置等），自动回退到程序目录下的 data 子目录，
        /// 避免“日志和数据库全写不进去、连启动都失败”的死局。
        /// </summary>
        public static string DataDir => _dataDir.Value;

        /// <summary>是否已回退到程序目录（true 表示 %AppData% 不可写）。</summary>
        public static bool DataDirIsFallback { get; private set; }

        private static readonly Lazy<string> _dataDir = new Lazy<string>(ResolveDataDir);

        private static string ResolveDataDir()
        {
            string preferred = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LanAutoConfig");
            if (CanWrite(preferred)) return preferred;

            string fallback = Path.Combine(AppContext.BaseDirectory, "data");
            if (CanWrite(fallback)) { DataDirIsFallback = true; return fallback; }

            // 两个都不可写：仍返回首选路径，后续写入失败由各处 try/catch 兜住
            DataDirIsFallback = true;
            return preferred;
        }

        /// <summary>实测目录是否可创建并写入（避免只看权限位、实际被安全软件拦截）。</summary>
        private static bool CanWrite(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".write-test");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        /// <summary>控制端 SQLite 数据库路径。</summary>
        public static string MasterDbPath => Path.Combine(DataDir, "master_logs.db");

        /// <summary>日志目录。</summary>
        public static string LogDir => Path.Combine(DataDir, "logs");

        /// <summary>黑名单文件。</summary>
        public static string BlacklistPath => Path.Combine(DataDir, "blacklist.txt");

        /// <summary>MAC 白名单文件（预登记终端）。</summary>
        public static string WhitelistPath => Path.Combine(DataDir, "whitelist.txt");

        /// <summary>配置/模板持久化文件（控制端记住上次参数与终端列表）。</summary>
        public static string MasterSettingsPath => Path.Combine(DataDir, "master-settings.json");

        /// <summary>被控端界面偏好（开机自启开关等），与命令行参数分开存。</summary>
        public static string SlavePrefsPath => Path.Combine(DataDir, "slave-prefs.json");

        /// <summary>控制端「文件分发」目录：放在这里的文件才能被下发（避免把整块硬盘暴露成下载站）。</summary>
        public static string FileShareDir => Path.Combine(DataDir, "files");

        /// <summary>判断当前进程是否以管理员权限运行（被控端必须管理员）。</summary>
        public static bool IsAdministrator()
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }
}
