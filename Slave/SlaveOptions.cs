using System.Text.Json;

namespace LanAutoConfig.Slave
{
    /// <summary>
    /// 静默部署参数：命令行 &gt; 配置文件 &gt; 默认值。
    ///
    /// 典型用法（装机流程/开机自启）：
    ///   LanAutoConfigSlave.exe --config slave.json
    ///   LanAutoConfigSlave.exe --suffix 12 --silent
    ///   LanAutoConfigSlave.exe --install-autostart --suffix 12 --silent
    ///
    /// 配置了后缀后，被控端收到模板会自动填好后缀并上报（AutoReport），全程不需要人工点。
    /// 是否在收到控制端指令后“直接执行”沿用原有语义（收到即执行），这里不额外加开关。
    /// </summary>
    public class SlaveOptions
    {
        /// <summary>预置后缀编号；为空表示不自动填写。</summary>
        public string Suffix { get; set; }

        /// <summary>收到模板后自动上报（默认开；只有配置了 Suffix 才生效）。</summary>
        public bool AutoReport { get; set; } = true;

        /// <summary>静默模式：不弹任何对话框，失败只写日志（无人值守）。</summary>
        public bool Silent { get; set; }

        /// <summary>指定用哪块网卡（按别名精确匹配，如 "以太网"）；为空则自动选。</summary>
        public string AdapterAlias { get; set; }

        /// <summary>安装开机自启（注册表 Run 项）后继续运行。</summary>
        public bool InstallAutostart { get; set; }

        /// <summary>移除开机自启后继续运行。</summary>
        public bool RemoveAutostart { get; set; }

        /// <summary>配置来源（日志里标注用）。</summary>
        public string Source { get; set; } = "命令行/默认";

        public bool HasSuffix => !string.IsNullOrWhiteSpace(Suffix);

        public static SlaveOptions Parse(string[] args)
        {
            var o = new SlaveOptions();

            // 先找 --config，其余参数覆盖配置文件里的值
            string configPath = GetArg(args, "--config", "-c");
            if (!string.IsNullOrEmpty(configPath))
            {
                try
                {
                    var fromFile = LoadFile(configPath);
                    if (fromFile != null)
                    {
                        o = fromFile;
                        o.Source = "配置文件 " + configPath;
                    }
                }
                catch (Exception ex)
                {
                    o.Source = "配置文件读取失败（" + ex.Message + "）";
                }
            }

            string suffix = GetArg(args, "--suffix", "-s");
            if (!string.IsNullOrEmpty(suffix)) { o.Suffix = suffix.Trim(); o.Source = "命令行"; }

            string adapter = GetArg(args, "--adapter", "-a");
            if (!string.IsNullOrEmpty(adapter)) { o.AdapterAlias = adapter.Trim(); o.Source = "命令行"; }

            if (HasFlag(args, "--silent")) o.Silent = true;
            if (HasFlag(args, "--auto-report")) o.AutoReport = true;
            if (HasFlag(args, "--no-auto-report")) o.AutoReport = false;
            if (HasFlag(args, "--install-autostart")) o.InstallAutostart = true;
            if (HasFlag(args, "--remove-autostart")) o.RemoveAutostart = true;

            return o;
        }

        public static SlaveOptions LoadFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            string json = File.ReadAllText(path, Encoding.UTF8);
            var opt = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            return JsonSerializer.Deserialize<SlaveOptions>(json, opt);
        }

        public string Describe()
            => $"来源={Source} 后缀={(HasSuffix ? Suffix : "(未配置)")} 自动上报={AutoReport} 静默={Silent}" +
               (string.IsNullOrEmpty(AdapterAlias) ? "" : $" 网卡={AdapterAlias}");

        private static string GetArg(string[] args, params string[] names)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i] ?? "";
                foreach (var n in names)
                {
                    if (string.Equals(a, n, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                        return args[i + 1];
                    if (a.StartsWith(n + "=", StringComparison.OrdinalIgnoreCase))
                        return a.Substring(n.Length + 1);
                }
            }
            return null;
        }

        private static bool HasFlag(string[] args, string name)
            => args != null && args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    }
}
