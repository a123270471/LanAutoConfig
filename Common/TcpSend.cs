namespace LanAutoConfig.Common
{
    /// <summary>
    /// TCP 可靠发送助手：控制端发指令到被控端 / 被控端发上报到控制端。超时 5 秒，失败重试 1 次。
    /// 注意：这里只保证“字节已写入 socket”，没有应用层 ACK；调用方不要把它当成“对方已执行”。
    /// </summary>
    public static class TcpSend
    {
        /// <summary>
        /// 快速探测 ip:port 是否能建立 TCP 连接（不发业务数据）。
        /// 用于“先判断常规路径通不通，再决定要不要临时换网段”以及验证临时地址是否真的可用。
        /// </summary>
        public static bool CanConnect(string ip, int port, string bindLocalIp, int timeoutMs)
            => CanConnect(ip, port, bindLocalIp, timeoutMs, out _);

        /// <summary>同 CanConnect，并返回失败原因（超时 / socket 错误码）。</summary>
        public static bool CanConnect(string ip, int port, string bindLocalIp, int timeoutMs, out string error)
        {
            error = null;
            if (!IPAddress.TryParse(ip, out IPAddress dest)) { error = $"目标地址不合法：{ip}"; return false; }
            if (timeoutMs <= 0) timeoutMs = AppConfig.TcpTimeoutMs;
            TcpClient client = null;
            try
            {
                client = CreateClient(bindLocalIp, out string bindError);
                if (bindError != null) error = bindError;

                Task connectTask = client.ConnectAsync(dest, port);
                if (!WaitWithTimeout(connectTask, timeoutMs))
                {
                    ObserveFault(connectTask);
                    try { client.Close(); } catch { }
                    error = $"连接 {ip}:{port} 超时（{timeoutMs} 毫秒无响应，通常是对方防火墙丢包或路由不通）";
                    return false;
                }
                connectTask.GetAwaiter().GetResult();   // 失败时抛出 SocketException
                return true;
            }
            catch (SocketException sex)
            {
                error = $"连接 {ip}:{port} 失败：{sex.SocketErrorCode}（{sex.Message}）";
                try { client?.Close(); } catch { }
                return false;
            }
            catch (Exception ex)
            {
                error = $"连接 {ip}:{port} 失败：{ex.Message}";
                try { client?.Close(); } catch { }
                return false;
            }
        }

        /// <summary>
        /// 向指定 IP:port 发送一条消息。返回是否成功。
        /// bindLocalIp 非空时会从该本机 IPv4 发起连接（确保对端看到的来源 IP 与预期一致）。
        /// </summary>
        public static bool SendTo(string ip, int port, NetMessage msg, string bindLocalIp = null)
            => SendTo(ip, port, msg, bindLocalIp, out _);

        /// <summary>
        /// 同上，但把失败原因（socket 错误码/超时/绑定失败）通过 error 带回，便于日志定位。
        /// </summary>
        public static bool SendTo(string ip, int port, NetMessage msg, string bindLocalIp, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(ip)) { error = "目标 IP 为空"; return false; }
            if (!IPAddress.TryParse(ip, out IPAddress dest)) { error = $"目标地址不合法：{ip}"; return false; }
            if (msg == null) { error = "消息为空"; return false; }

            for (int attempt = 0; attempt <= AppConfig.TcpRetry; attempt++)
            {
                TcpClient client = null;
                try
                {
                client = CreateClient(bindLocalIp, out string bindError);
                if (bindError != null) error = bindError;

                    Task connectTask = client.ConnectAsync(dest, port);
                    if (!WaitWithTimeout(connectTask, AppConfig.TcpTimeoutMs))
                    {
                        ObserveFault(connectTask);
                        try { client.Close(); } catch { }
                        error = $"连接 {ip}:{port} 超时（{AppConfig.TcpTimeoutMs} 毫秒无响应，通常是对方防火墙丢包或路由不对）";
                        continue;                              // 超时：下一轮重试
                    }
                    connectTask.GetAwaiter().GetResult();      // 失败时抛出 SocketException

                    client.NoDelay = true;
                    client.SendTimeout = AppConfig.TcpTimeoutMs;

                    string json = JsonHelper.Serialize(msg);
                    byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
                    NetworkStream stream = client.GetStream();
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                    return true;
                }
                catch (SocketException sex)
                {
                    error = $"连接 {ip}:{port} 失败：{sex.SocketErrorCode}（{sex.Message}）";
                    try { client?.Close(); } catch { }
                    if (attempt == AppConfig.TcpRetry) return false;
                }
                catch (Exception ex)
                {
                    error = $"连接 {ip}:{port} 失败：{ex.Message}";
                    try { client?.Close(); } catch { }
                    if (attempt == AppConfig.TcpRetry) return false;
                }
                finally
                {
                    try { client?.Close(); } catch { }         // 发送完成后不再需要该连接
                }
            }
            return false;
        }

        private static TcpClient CreateClient(string bindLocalIp, out string bindError)
        {
            bindError = null;
            if (!string.IsNullOrWhiteSpace(bindLocalIp) && IPAddress.TryParse(bindLocalIp, out IPAddress localAddr))
            {
                try { return new TcpClient(new IPEndPoint(localAddr, 0)); }
                catch (Exception bindEx)
                {
                    // 常见于临时地址还在 DAD 探测（Tentative）阶段：绑定失败，退回系统自动选源再试
                    bindError = $"绑定源地址 {bindLocalIp} 失败：{bindEx.Message}";
                }
            }
            return new TcpClient();
        }

        /// <summary>等待任务完成，超时返回 false（不抛异常）。</summary>
        private static bool WaitWithTimeout(Task task, int timeoutMs)
        {
            try { return Task.WhenAny(task, Task.Delay(timeoutMs)).GetAwaiter().GetResult() == task; }
            catch { return false; }
        }

        /// <summary>超时放弃连接后，消费掉任务异常，避免未观察异常刷到控制台。</summary>
        private static void ObserveFault(Task task)
            => task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }
}
