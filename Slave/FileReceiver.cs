using System.Security.Cryptography;
using LanAutoConfig.Common;

namespace LanAutoConfig.Slave
{
    /// <summary>文件接收/执行结果。</summary>
    public class FileReceiveResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public long Bytes { get; set; }
        public string Sha256 { get; set; }
        /// <summary>下载后是否又执行了（界面提示用）。</summary>
        public bool Executed { get; set; }
        public int ExitCode { get; set; }
    }

    /// <summary>
    /// 从控制端的文件服务下载文件、校验 SHA256、落盘，必要时静默执行。
    /// 下载走 HTTP（控制端 48890），与指令通道分开：大文件不占指令连接，失败可单独重试。
    /// </summary>
    public static class FileReceiver
    {
        private static readonly Lazy<HttpClient> _http = new Lazy<HttpClient>(() =>
            new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
            {
                Timeout = TimeSpan.FromMinutes(10)
            });

        /// <summary>默认落盘位置：%TEMP%\LanAC\文件名。</summary>
        public static string DefaultTargetPath(string fileName)
            => Path.Combine(Path.GetTempPath(), "LanAC", Path.GetFileName(fileName ?? "download.bin"));

        /// <summary>展开 %TEMP% 之类的环境变量；空值走默认目录。</summary>
        public static string ResolveTargetPath(string targetPath, string fileName)
        {
            if (string.IsNullOrWhiteSpace(targetPath)) return DefaultTargetPath(fileName);
            string expanded = Environment.ExpandEnvironmentVariables(targetPath.Trim());
            // 只给了目录（以 \ 结尾）就补上文件名
            if (expanded.EndsWith("\\") || expanded.EndsWith("/")) expanded = Path.Combine(expanded, Path.GetFileName(fileName ?? ""));
            return expanded;
        }

        /// <summary>
        /// 下载 + 校验 + 落盘（不执行）。expectedSha 为空时跳过校验（仍会回报实际 SHA256）。
        /// </summary>
        public static FileReceiveResult Download(string url, string fileName, string targetPath,
            string expectedSha, long expectedSize, Action<string> log = null)
        {
            var r = new FileReceiveResult();
            string tmp = null;
            try
            {
                if (string.IsNullOrWhiteSpace(url)) { r.Message = "下载地址为空"; return r; }
                if (string.IsNullOrWhiteSpace(fileName))
                    fileName = Path.GetFileName(new Uri(url).AbsolutePath);

                string target = ResolveTargetPath(targetPath, fileName);
                string dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                tmp = target + ".part";
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }

                log?.Invoke($"开始下载 {url} → {target}");
                using var resp = _http.Value.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                    .GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode)
                {
                    r.Message = $"下载失败：HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
                    return r;
                }

                long total = 0;
                using (var src = resp.Content.ReadAsStream())
                using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var buf = new byte[64 * 1024];
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        dst.Write(buf, 0, n);
                        hash.AppendData(buf, 0, n);
                        total += n;
                    }
                    r.Bytes = total;
                    r.Sha256 = ToHex(hash.GetHashAndReset());
                }

                if (expectedSize > 0 && total != expectedSize)
                {
                    TryDelete(tmp);
                    r.Message = $"大小校验失败：应为 {expectedSize} 字节，实际 {total} 字节";
                    return r;
                }
                if (!string.IsNullOrWhiteSpace(expectedSha) &&
                    !string.Equals(r.Sha256, expectedSha.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(tmp);
                    r.Message = $"SHA256 校验失败（文件可能损坏或被替换）：期望 {Short(expectedSha)}，实际 {Short(r.Sha256)}";
                    return r;
                }

                // 覆盖前先把旧文件挪开：目标可能是正在运行的程序，直接 Move 会失败并留下半截文件
                if (File.Exists(target))
                {
                    string old = target + ".old";
                    TryDelete(old);
                    try { File.Move(target, old); } catch { }
                }
                File.Move(tmp, target);
                TryDelete(target + ".old");

                r.Success = true;
                r.Message = $"已保存到 {target}（{total / 1024} KB）";
                log?.Invoke(r.Message);
                return r;
            }
            catch (Exception ex)
            {
                r.Success = false;
                r.Message = "下载异常：" + ex.Message;
                TryDelete(tmp);
                return r;
            }
        }

        /// <summary>
        /// 静默执行下载下来的文件。mode: auto / msi / exe / ps1 / bat / none。
        /// 3010（MSI 要求重启）与 1641 都算成功。
        /// </summary>
        public static FileReceiveResult Run(string path, string mode, string args, int timeoutSeconds,
            Action<string> log = null)
        {
            var r = new FileReceiveResult();
            try
            {
                if (!File.Exists(path)) { r.Message = "文件不存在，无法执行：" + path; return r; }

                string m = (mode ?? "auto").Trim().ToLowerInvariant();
                if (m == "auto" || m.Length == 0) m = GuessMode(path);
                if (m == "none") { r.Success = true; r.Message = "按配置不执行"; return r; }

                string exe, argLine;
                switch (m)
                {
                    case "msi":
                        exe = "msiexec.exe";
                        argLine = $"/i \"{path}\" /qn /norestart {(args ?? "").Trim()}".Trim();
                        break;
                    case "ps1":
                        exe = "powershell.exe";
                        argLine = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{path}\" {(args ?? "").Trim()}".Trim();
                        break;
                    case "bat":
                    case "cmd":
                        exe = "cmd.exe";
                        argLine = $"/c \"\"{path}\" {(args ?? "").Trim()}\"".Trim();
                        break;
                    default:   // exe
                        exe = path;
                        argLine = (args ?? "").Trim();
                        break;
                }

                int timeoutMs = Math.Max(5, Math.Min(1800, timeoutSeconds <= 0 ? 300 : timeoutSeconds)) * 1000;
                log?.Invoke($"执行：{exe} {argLine}（超时 {timeoutMs / 1000} 秒）");

                var psi = new ProcessStartInfo(exe, argLine)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(path) ?? Path.GetTempPath()
                };
                using var p = Process.Start(psi);
                if (p == null) { r.Message = "无法启动进程：" + exe; return r; }

                using var outBuf = new MemoryStream();
                using var errBuf = new MemoryStream();
                Task oc = p.StandardOutput.BaseStream.CopyToAsync(outBuf);
                Task ec = p.StandardError.BaseStream.CopyToAsync(errBuf);

                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(true); } catch { }
                    r.Message = $"执行超时（{timeoutMs / 1000} 秒），已强制结束";
                    return r;
                }
                try { Task.WaitAll(new[] { oc, ec }, 3000); } catch { }

                r.ExitCode = p.ExitCode;
                r.Executed = true;
                string stdout = ConsoleText.Decode(outBuf.ToArray()).Trim();
                string stderr = ConsoleText.Decode(errBuf.ToArray()).Trim();
                bool ok = p.ExitCode == 0 || p.ExitCode == 3010 || p.ExitCode == 1641;
                r.Success = ok;
                r.Message = ok
                    ? $"已执行完成，退出码 {p.ExitCode}" + (p.ExitCode == 3010 ? "（需要重启才生效）" : "")
                    : $"执行失败，退出码 {p.ExitCode}：{FirstLine(stderr, stdout)}";
                log?.Invoke(r.Message);
                if (!string.IsNullOrEmpty(stdout)) log?.Invoke("安装输出：" + FirstLine(stdout, ""));
                return r;
            }
            catch (Exception ex)
            {
                r.Success = false;
                r.Message = "执行异常：" + ex.Message;
                return r;
            }
        }

        private static string GuessMode(string path)
        {
            switch (Path.GetExtension(path ?? "").ToLowerInvariant())
            {
                case ".msi": return "msi";
                case ".ps1": return "ps1";
                case ".bat":
                case ".cmd": return "bat";
                default: return "exe";
            }
        }

        private static string FirstLine(string a, string b)
        {
            string s = string.IsNullOrWhiteSpace(a) ? (b ?? "") : a;
            int i = s.IndexOfAny(new[] { '\r', '\n' });
            if (i > 0) s = s.Substring(0, i);
            return s.Length > 300 ? s.Substring(0, 300) + "…" : s.Trim();
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static string Short(string s) => string.IsNullOrEmpty(s) ? "" : s.Substring(0, Math.Min(16, s.Length)) + "…";

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { }
        }

        /// <summary>计算文件 SHA256（控制端上传前用，两边算法一致）。</summary>
        public static string ComputeSha256(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
            using var sha = SHA256.Create();
            return ToHex(sha.ComputeHash(fs));
        }
    }
}
