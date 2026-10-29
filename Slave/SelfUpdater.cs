namespace LanAutoConfig.Slave
{
    /// <summary>
    /// 被控端自升级：控制端下发新 exe → 下载校验 → 交给一个批处理“等本进程退出后替换并重新启动”。
    ///
    /// 为什么要批处理：正在运行的 exe 不能被覆盖（Windows 会锁文件），
    /// 只能先退出、再由另一个进程完成替换。批处理由 cmd.exe 跑，与本进程同权限，
    /// 且能在本进程完全退出后再动手。
    /// </summary>
    public static class SelfUpdater
    {
        /// <summary>把新 exe 落到临时目录并校验（不替换）。返回新文件路径。</summary>
        public static string DownloadNewVersion(string url, string expectedSha, long expectedSize, Action<string> log)
        {
            string name = "LanAutoConfigSlave.new_" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".exe";
            string target = Path.Combine(Path.GetTempPath(), "LanAC", name);
            var r = FileReceiver.Download(url, name, target, expectedSha, expectedSize, log);
            if (!r.Success) throw new InvalidOperationException(r.Message);
            return target;
        }

        /// <summary>
        /// 生成替换脚本并启动它（调用方随后应立刻 Application.Exit()）。
        /// 返回脚本路径，便于日志记录。
        /// </summary>
        public static string LaunchReplaceAndRestart(string newExePath, string startArgs)
        {
            string curExe = Application.ExecutablePath;
            int pid = Environment.ProcessId;
            string script = Path.Combine(Path.GetTempPath(), $"lanac_update_{pid}.cmd");

            // 用 %~f0 之外全写死绝对路径：cmd 的工作目录不可靠
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("setlocal");
            sb.AppendLine("set n=0");
            sb.AppendLine(":wait");
            sb.AppendLine($"tasklist /fi \"PID eq {pid}\" 2>nul | find /i \"{pid}\" >nul");
            sb.AppendLine("if errorlevel 1 goto move");
            sb.AppendLine("set /a n+=1");
            sb.AppendLine("if %n% geq 120 goto move");
            sb.AppendLine("timeout /t 1 /nobreak >nul");
            sb.AppendLine("goto wait");
            sb.AppendLine(":move");
            sb.AppendLine($"move /y \"{newExePath}\" \"{curExe}\" >nul");
            sb.AppendLine("if errorlevel 1 (");
            sb.AppendLine("  timeout /t 2 /nobreak >nul");
            sb.AppendLine($"  move /y \"{newExePath}\" \"{curExe}\" >nul");
            sb.AppendLine(")");
            sb.AppendLine($"start \"\" \"{curExe}\" {startArgs ?? ""}".TrimEnd());
            sb.AppendLine("del \"%~f0\"");
            File.WriteAllText(script, sb.ToString(), Encoding.Default);

            var psi = new ProcessStartInfo("cmd.exe", $"/c \"\"{script}\"\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetTempPath()
            };
            Process.Start(psi);
            return script;
        }
    }
}
