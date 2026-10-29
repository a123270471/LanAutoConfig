using LanAutoConfig.Common;

namespace LanAutoConfig.Master
{
    /// <summary>
    /// 控制端内置的只读文件服务（极简 HTTP/1.1 GET），给被控端下载安装包 / 脚本 / 新版本 exe。
    ///
    /// 为什么不用 HttpListener：它要 `netsh http add urlacl` 或管理员权限才能监听
    /// `http://+:端口/`，现场经常卡在这一步；直接开 TcpListener 自己写十来行 HTTP 反而省事。
    /// 只允许下载「分发目录」里的文件（不含子目录、不接受 ..），并且 URL 里带随机路径令牌。
    /// </summary>
    public class HttpFileServer : IDisposable
    {
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private readonly SemaphoreSlim _slots = new SemaphoreSlim(16, 16);   // 同时最多 16 个下载
        private string _rootDir;
        private string _urlToken;

        public event Action<string> Log;

        public bool Running => _listener != null;
        public int Port { get; private set; } = AppConfig.FileServerPort;
        public string RootDir => _rootDir;

        public bool Start(string rootDir, int port, string urlToken)
        {
            Stop();
            try
            {
                Directory.CreateDirectory(rootDir);
                _rootDir = Path.GetFullPath(rootDir);
                _urlToken = string.IsNullOrWhiteSpace(urlToken) ? "f" : urlToken.Trim();
                Port = port <= 0 ? AppConfig.FileServerPort : port;

                _listener = new TcpListener(IPAddress.Any, Port);
                _listener.Start();
                _cts = new CancellationTokenSource();
                _ = Task.Run(() => AcceptLoop(_cts.Token));
                Log?.Invoke($"文件服务已启动：端口 {Port}，分发目录 {_rootDir}");
                return true;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"文件服务启动失败（端口 {port} 可能被占用）：{ex.Message}");
                Stop();
                return false;
            }
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            _listener = null;
            _cts = null;
        }

        /// <summary>被控端用的下载地址：http://本机IP:端口/f/随机令牌/文件名。</summary>
        public string BuildUrl(string masterIp, string fileName)
            => $"http://{masterIp}:{Port}/f/{_urlToken}/{Uri.EscapeDataString(fileName ?? "")}";

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(); }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested || _listener == null) break;
                    Log?.Invoke($"[文件服务] 接收连接出错，稍后重试：{ex.Message}");
                    try { await Task.Delay(500, ct); } catch { break; }
                    continue;
                }
                _ = HandleAsync(client);
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                string remote = "";
                bool leased = false;
                try
                {
                    remote = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
                    client.ReceiveTimeout = 10000;
                    client.SendTimeout = 60000;

                    string requestLine = await ReadRequestLineAsync(client.GetStream());
                    if (string.IsNullOrEmpty(requestLine)) return;

                    var parts = requestLine.Split(' ');
                    if (parts.Length < 2) { await WriteStatusAsync(client, "400 Bad Request"); return; }

                    string method = parts[0].ToUpperInvariant();
                    string path = parts[1];
                    if (method != "GET" && method != "HEAD") { await WriteStatusAsync(client, "405 Method Not Allowed"); return; }

                    // 期望形状：/f/<令牌>/<文件名>
                    var seg = path.TrimStart('/').Split('/');
                    if (seg.Length < 3 || !string.Equals(seg[0], "f", StringComparison.OrdinalIgnoreCase))
                    { await WriteStatusAsync(client, "404 Not Found"); return; }

                    if (!string.Equals(seg[1], _urlToken, StringComparison.Ordinal))
                    {
                        Log?.Invoke($"[文件服务] 拒绝 {remote}：下载令牌不正确（{path}）");
                        await WriteStatusAsync(client, "403 Forbidden");
                        return;
                    }

                    string name = Uri.UnescapeDataString(seg[2]);
                    string full = ResolveSafePath(name);
                    if (full == null || !File.Exists(full))
                    {
                        Log?.Invoke($"[文件服务] {remote} 请求的文件不存在：{name}");
                        await WriteStatusAsync(client, "404 Not Found");
                        return;
                    }

                    long len = new FileInfo(full).Length;
                    await WriteHeaderAsync(client, "200 OK", "application/octet-stream", len);
                    if (method == "HEAD") return;

                    await _slots.WaitAsync();
                    leased = true;
                    using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
                    await fs.CopyToAsync(client.GetStream());
                    Log?.Invoke($"[文件服务] {remote} 已下载 {name}（{len / 1024} KB）");
                }
                catch (Exception ex)
                {
                    // 对被控端提前断线（换了 IP、重启）很常见，不当错误刷屏
                    Log?.Invoke($"[文件服务] 处理 {remote} 的下载请求失败：{ex.Message}");
                }
                finally
                {
                    if (leased) { try { _slots.Release(); } catch { } }
                }
            }
        }

        /// <summary>把 URL 里的文件名限制在分发目录内（拒绝路径穿越与子目录）。</summary>
        private string ResolveSafePath(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            string bare = Path.GetFileName(name);                 // 去掉任何目录成分
            if (!string.Equals(bare, name, StringComparison.Ordinal)) return null;
            if (bare.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            string full = Path.GetFullPath(Path.Combine(_rootDir, bare));
            if (!full.StartsWith(_rootDir, StringComparison.OrdinalIgnoreCase)) return null;
            return full;
        }

        private static async Task<string> ReadRequestLineAsync(NetworkStream stream)
        {
            var buf = new byte[8192];
            int total = 0;
            while (total < buf.Length)
            {
                int n = await stream.ReadAsync(buf, total, buf.Length - total);
                if (n <= 0) return null;
                total += n;
                int idx = IndexOfDoubleCrlf(buf, total);
                if (idx >= 0)
                {
                    string head = Encoding.ASCII.GetString(buf, 0, idx);
                    int eol = head.IndexOf('\n');
                    return (eol > 0 ? head.Substring(0, eol) : head).Trim();
                }
            }
            return null;
        }

        private static int IndexOfDoubleCrlf(byte[] buf, int len)
        {
            for (int i = 3; i < len; i++)
                if (buf[i - 3] == 13 && buf[i - 2] == 10 && buf[i - 1] == 13 && buf[i] == 10)
                    return i - 1;
            return -1;
        }

        private static Task WriteStatusAsync(TcpClient client, string status)
            => WriteHeaderAsync(client, status, "text/plain", 0);

        private static async Task WriteHeaderAsync(TcpClient client, string status, string contentType, long length)
        {
            string head = $"HTTP/1.1 {status}\r\n" +
                          $"Content-Type: {contentType}\r\n" +
                          $"Content-Length: {length}\r\n" +
                          "Cache-Control: no-store\r\n" +
                          "Connection: close\r\n\r\n";
            byte[] bytes = Encoding.ASCII.GetBytes(head);
            await client.GetStream().WriteAsync(bytes, 0, bytes.Length);
        }

        public void Dispose() => Stop();
    }
}
