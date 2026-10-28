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
        /// <summary>收到“重启本机”指令（批量重启用）。</summary>
        public event Action<MasterRebootCmdMsg> OnReboot;
        /// <summary>收到“开关开机自启动”指令（批量运维用）。</summary>
        public event Action<MasterAutostartCmdMsg> OnAutostart;
        /// <summary>收到“关闭本机”指令（批量关机用）。</summary>
        public event Action<MasterShutdownCmdMsg> OnShutdown;
        /// <summary>收到“分配后缀编号”指令（批量装机用，收到后自动填编号并上报）。</summary>
        public event Action<MasterAssignSuffixCmdMsg> OnAssignSuffix;
        /// <summary>收到“自定义命令/脚本下发”指令（运维用）。</summary>
        public event Action<MasterCustomCmdMsg> OnCustomCmd;
        /// <summary>收到“验收”指令：要求被控端上报当前实际配置。</summary>
        public event Action<MasterVerifyCmdMsg> OnVerify;
        public event Action<string> Log;

        public void SetSessionId(string sid) => _currentSessionId = sid ?? "";

        /// <summary>设置允许下发指令的控制端 IP（收到模板时由窗体写入）。</summary>
        public void SetAllowedMaster(string ip)
            => _allowedMasterIp = string.IsNullOrWhiteSpace(ip) ? null : ip.Trim();

        public bool Start()
        {
            try
            {
                // 这里刻意不加 SO_REUSEADDR：Windows 上两个套接字都设了它时，后绑定的会“抢走”同一端口的连接，
                // 同机再开一个被控端就会把控制端下发的指令抢走。绑定失败按“已有实例在跑”处理更安全。
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
            _listener = null;
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(); }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested || _listener == null) break;   // 正常停止
                    // 其它异常不能直接 break：否则被控端“永久失聪”，界面却一切正常、日志空白
                    Log?.Invoke($"[TCP 监听] 接收连接出错，稍后重试：{ex.Message}");
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
                    else if (msg is MasterRebootCmdMsg rb && rb.SessionId == sid)
                    {
                        Log?.Invoke($"收到控制端重启指令（延迟 {rb.DelaySeconds} 秒）");
                        OnReboot?.Invoke(rb);
                    }
                    else if (msg is MasterAutostartCmdMsg ac && ac.SessionId == sid)
                    {
                        Log?.Invoke($"收到控制端开机自启指令：{(ac.Enable ? "开启" : "取消")} MAC={ac.LocalMac}");
                        OnAutostart?.Invoke(ac);
                    }
                    else if (msg is MasterShutdownCmdMsg sd && sd.SessionId == sid)
                    {
                        Log?.Invoke($"收到控制端关机指令（延迟 {sd.DelaySeconds} 秒）");
                        OnShutdown?.Invoke(sd);
                    }
                    else if (msg is MasterAssignSuffixCmdMsg asg && asg.SessionId == sid)
                    {
                        Log?.Invoke($"收到控制端编号指令：后缀 {asg.Suffix} MAC={asg.LocalMac}");
                        OnAssignSuffix?.Invoke(asg);
                    }
                    else if (msg is MasterCustomCmdMsg cc && cc.SessionId == sid)
                    {
                        Log?.Invoke($"收到控制端自定义命令：shell={cc.Shell} MAC={cc.LocalMac}");
                        OnCustomCmd?.Invoke(cc);
                    }
                    else if (msg is MasterVerifyCmdMsg vc && vc.SessionId == sid)
                    {
                        Log?.Invoke($"收到控制端验收指令：期望 {vc.ExpectedHostName}/{vc.ExpectedIp} MAC={vc.LocalMac}");
                        OnVerify?.Invoke(vc);
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
