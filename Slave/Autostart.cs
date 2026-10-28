using Microsoft.Win32;

namespace LanAutoConfig.Slave
{
    /// <summary>
    /// 开机自启（注册表 Run 项）。先写 HKCU（不需要额外权限），失败再写 HKLM。
    /// 命令里会带上安装时的附加参数（如 --silent --suffix 12），保证开机后仍是同一套静默流程。
    /// </summary>
    public static class Autostart
    {
        private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "LanAutoConfigSlave";

        /// <summary>安装/更新开机自启，返回实际写入的位置（HKCU/HKLM）。</summary>
        public static bool Install(string extraArgs, out string where, out string error)
        {
            where = null; error = null;
            string args = string.IsNullOrWhiteSpace(extraArgs) ? "" : " " + extraArgs.Trim();
            string command = $"\"{Application.ExecutablePath}\"{args}";

            var errors = new List<string>();

            // 先 HKCU 再 HKLM；每个位置都先“打开已存在的 Run 项来写”，不行再“创建后写”。
            // 有些受限令牌 / 组策略会拒绝 CreateSubKey，但对已存在的键允许写入，两条路都试才稳。
            foreach (var (label, root) in new (string, RegistryKey)[] { ("HKCU", Registry.CurrentUser), ("HKLM", Registry.LocalMachine) })
            {
                foreach (var open in new Func<RegistryKey>[]
                {
                    () => root.OpenSubKey(RunKey, true),
                    () => root.CreateSubKey(RunKey, RegistryKeyPermissionCheck.ReadWriteSubTree)
                })
                {
                    try
                    {
                        using var key = open();
                        if (key == null) { errors.Add($"{label}: 无法打开 Run 项"); continue; }
                        key.SetValue(ValueName, command, RegistryValueKind.String);
                        where = label;
                        return true;
                    }
                    catch (Exception ex) { errors.Add($"{label}: {ex.Message}"); }
                }
            }

            error = string.Join("；", errors.Distinct());
            return false;
        }

        /// <summary>移除开机自启（两个位置都清）。</summary>
        public static bool Remove(out string error)
        {
            error = null;
            bool removed = false;
            var errors = new List<string>();
            foreach (var (name, root) in new (string, RegistryKey)[] { ("HKCU", Registry.CurrentUser), ("HKLM", Registry.LocalMachine) })
            {
                try
                {
                    using var key = root.OpenSubKey(RunKey, true);
                    if (key?.GetValue(ValueName) != null)
                    {
                        key.DeleteValue(ValueName, false);
                        removed = true;
                    }
                }
                catch (Exception ex) { errors.Add(name + ": " + ex.Message); }
            }
            if (errors.Count > 0) error = string.Join("；", errors);
            return removed;
        }

        /// <summary>当前是否已设置自启（返回命令原文，没有则 null）。</summary>
        public static string Query()
        {
            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using var key = root.OpenSubKey(RunKey, false);
                    if (key?.GetValue(ValueName) is string s) return s;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// 把本次的静默参数拼回命令行写进 Run 项：开机后仍是同一套流程（静默 / 预置后缀 / 指定网卡）。
        /// 故意不带 --install-autostart，否则每次开机都会重写一遍注册表。
        /// </summary>
        public static string BuildArgs(SlaveOptions o)
        {
            if (o == null) return "";
            var sb = new StringBuilder();
            if (o.Silent) sb.Append("--silent ");
            if (o.HasSuffix) sb.Append("--suffix ").Append(o.Suffix).Append(' ');
            if (!string.IsNullOrEmpty(o.AdapterAlias)) sb.Append("--adapter \"").Append(o.AdapterAlias).Append("\" ");
            return sb.ToString().Trim();
        }

        /// <summary>Run 项里的命令是否指向当前 exe（程序被移动/换目录后需要刷新）。</summary>
        public static bool PointsToCurrentExe(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;
            string cmd = command.Trim();
            string target = cmd.StartsWith("\"", StringComparison.Ordinal)
                ? cmd.Substring(1, Math.Max(0, cmd.IndexOf('"', 1) - 1))
                : cmd.Split(' ')[0];
            try
            {
                return string.Equals(Path.GetFullPath(target), Path.GetFullPath(Application.ExecutablePath),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}
