using LanAutoConfig.Common;

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
            string curDir = Path.GetDirectoryName(curExe) ?? AppContext.BaseDirectory;

            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("setlocal");
            sb.AppendLine("set n=0");

            // 1) 等旧进程真正退出（不是“发过退出请求”，是 PID 从任务列表里消失）
            sb.AppendLine(":wait");
            sb.AppendLine($"tasklist /fi \"PID eq {pid}\" 2>nul | find /i \"{pid}\" >nul");
            sb.AppendLine("if errorlevel 1 goto move");
            sb.AppendLine("set /a n+=1");
            sb.AppendLine("if %n% geq 120 goto move");
            sb.AppendLine("timeout /t 1 /nobreak >nul");
            sb.AppendLine("goto wait");

            // 2) 替换：失败就再等 2 秒重试，最多 10 次（杀毒软件扫描新文件时会短暂占用）
            sb.AppendLine(":move");
            sb.AppendLine("set m=0");
            sb.AppendLine(":move2");
            sb.AppendLine($"move /y \"{newExePath}\" \"{curExe}\" >nul 2>&1");
            sb.AppendLine($"if exist \"{curExe}\" if not exist \"{newExePath}\" goto start");
            sb.AppendLine("set /a m+=1");
            sb.AppendLine("if %m% geq 10 goto start");
            sb.AppendLine("timeout /t 2 /nobreak >nul");
            sb.AppendLine("goto move2");

            // 3) 拉起新版本（工作目录设成程序目录，避免相对路径读不到配置）
            sb.AppendLine(":start");
            sb.AppendLine($"cd /d \"{curDir}\"");
            sb.AppendLine($"start \"\" \"{curExe}\" {startArgs ?? ""}".TrimEnd());
            sb.AppendLine("del \"%~f0\"");
            sb.AppendLine("exit");

            // ===== 这一行是本 bug 的关键 =====
            // 必须按本机 OEM 代码页（中文系统 936）写脚本。
            // cmd.exe 是按 OEM 解码 .cmd 的，而 .NET 里 Encoding.Default 是 UTF-8：
            // 上一版用 Encoding.Default，脚本里的中文路径（"局域网配置工具-被控端.exe"）
            // 被解成乱码 → move 找不到文件、start 也找不到 → 屏幕上就是
            // “Windows 找不到文件 'C:\...\鍖?...exe'” 那种乱码报错，升级后程序再也起不来。
            // 已实测：同一份脚本用 GBK 写能替换成功，用 UTF-8 写必然失败。
            Encoding enc;
            try { enc = ConsoleText.Oem; } catch { enc = Encoding.Default; }
            File.WriteAllText(script, sb.ToString(), enc);

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
