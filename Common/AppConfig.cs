namespace LanAutoConfig
{
    /// <summary>全局配置常量（端口、校验标记、目录等）。</summary>
    public static class AppConfig
    {
        public const int UdpPort = 48888;          // UDP：广播发现、模板推送
        public const int TcpPort = 48889;          // TCP：可靠传输上报、指令、结果
        /// <summary>
        /// 校验标记，过滤无关 UDP/TCP 包。
        /// V2 起 SlaveReport 增加 IsDhcp 字段（参与 Check 计算），两端必须一起升级；
        /// 混用时会在 Magic/Check 校验处被明确拒绝并记录日志。
        /// </summary>
        public const string Magic = "LANAC-V2";
        /// <summary>协议版本号，仅用于日志排查。</summary>
        public const string ProtocolVersion = "2";

        public const int BroadcastIntervalMs = 2500; // 控制端 2.5 秒广播一次模板
        public const int TcpTimeoutMs = 5000;        // TCP 超时 5 秒
        public const int TcpRetry = 1;               // 失败重试 1 次

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

        /// <summary>判断当前进程是否以管理员权限运行（被控端必须管理员）。</summary>
        public static bool IsAdministrator()
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }
}
