using LanAutoConfig.Common;

namespace LanAutoConfig.Slave
{
    /// <summary>被控端 TCP 指令监听：监听 48889，接收控制端的执行指令 / 会话重置。</summary>
    public class TcpCommandListener : IDisposable
    {
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private volatile string _currentSessionId = "";
        /// <summary>只接受来自该 IP 的指令（控制端广播网卡地址）；为空时不做来源限制。</summary>
        private volatile string _allowedMasterIp;

        /// <summary>收到执行指令（仅当会话 ID 与当前一致时触发）。</summary>
        public event Action<MasterExecuteCmdMsg> OnExecute;
        /// <summary>收到会话重置。</summary>
        public event Action OnReset;
        public event Action<string> Log;

        public void SetSessionId(string sid) => _currentSessionId = sid ?? "";

        /// <summary>设置允许下发指令的控制端 IP（收到模板时由窗体写入）。</summary>
        public void SetAllowedMaster(string ip)
            => _allowedMasterIp = string.IsNullOrWhiteSpace(ip) ? null : ip.Trim();

        public bool Start()
        {
            try
            {
                _listener = new TcpListener(IPAddress.Any, AppConfig.TcpPort);
                _listener.Start();
                _cts = new CancellationTokenSource();
                _ = Task.Run(() => AcceptLoop(_cts.Token));
                Log?.Invoke($"已监听 TCP 指令端口 {AppConfig.TcpPort}");
                return true;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"TCP 指令监听启动失败：{ex.Message}");
                return false;
            }
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(); }
                catch { break; }
                _ = HandleAsync(client);
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                string remoteIp = "";
                try
                {
                    client.ReceiveTimeout = 10000;
                    client.SendTimeout = 10000;
                    remoteIp = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
                    string json = await TcpLineReader.ReadLineAsync(client.GetStream());
                    if (string.IsNullOrEmpty(json)) return;

                    var msg = JsonHelper.Deserialize(json);
                    if (msg == null)
                    {
                        Log?.Invoke($"[丢弃] 来自 {remoteIp} 的消息无法解析（JSON/MsgType 非法）");
                        return;
                    }
                    if (!msg.Validate())
                    {
                        Log?.Invoke($"[丢弃] 来自 {remoteIp} 的 {msg.MsgType} 校验失败（Magic/Check 不匹配）");
                        return;
                    }

                    // 来源限制：只认当前配对的这台控制端
                    string allowed = _allowedMasterIp;
                    if (!string.IsNullOrEmpty(allowed) && !string.Equals(remoteIp, allowed, StringComparison.Ordinal))
                    {
                        Log?.Invoke($"[拒绝] 忽略来自 {remoteIp} 的指令（当前配对的控制端是 {allowed}）");
                        return;
                    }

                    string sid = _currentSessionId;
                    if (string.IsNullOrEmpty(sid))
                    {
                        Log?.Invoke($"[拒绝] 尚未收到模板，忽略来自 {remoteIp} 的指令");
                        return;
                    }

                    if (msg is MasterExecuteCmdMsg cmd && cmd.SessionId == sid)
                    {
                        Log?.Invoke($"收到控制端执行指令：主机名={cmd.TargetHostName} " +
                                    $"地址={(cmd.IsDhcp ? "DHCP" : cmd.TargetIp)} MAC={cmd.LocalMac}");
                        OnExecute?.Invoke(cmd);
                    }
                    else if (msg is MasterResetSessionMsg && msg.SessionId == sid)
                    {
                        Log?.Invoke("收到控制端会话重置指令");
                        OnReset?.Invoke();
                    }
                    else
                    {
                        Log?.Invoke($"[忽略] 会话不一致的 {msg.MsgType}（本地会话 {sid}）");
                    }
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"[丢弃] 处理 {remoteIp} 的连接失败：{ex.Message}");
                }
            }
        }

        public void Dispose() => Stop();
    }
}
