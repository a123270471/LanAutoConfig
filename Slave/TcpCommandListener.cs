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
        /// <summary>本机身份令牌：指令里带来的令牌必须与它一致，否则丢弃（防止同网段伪造指令）。</summary>
        private volatile string _token;

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
        /// <summary>收到“文件下发”指令（安装包/脚本）。</summary>
        public event Action<MasterFilePushCmdMsg> OnFilePush;
        /// <summary>收到“自升级”指令（用新 exe 替换自己）。</summary>
        public event Action<MasterSelfUpdateCmdMsg> OnSelfUpdate;
        /// <summary>收到“截屏”指令。</summary>
        public event Action<MasterScreenshotCmdMsg> OnScreenshot;
        /// <summary>收到“程序重启/退出”指令。</summary>
        public event Action<MasterProcCmdMsg> OnProcCmd;
        public event Action<string> Log;

        public void SetSessionId(string sid) => _currentSessionId = sid ?? "";

        /// <summary>设置本机令牌（启动时从本地偏好读出，之后不变）。</summary>
        public void SetToken(string token) => _token = token ?? "";

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
                    if (!msg.ValidateEx(out string why))
                    {
                        Log?.Invoke($"[丢弃] 来自 {remoteIp} 的 {msg.MsgType} 校验失败：{why}");
                        return;
                    }

                    // 身份令牌：控制端必须把“本机上报时带过去的令牌”原样带回。
                    // 同网段的其它机器即使知道协议格式、也能收到 UDP 广播，但不知道这台的令牌，
                    // 因此伪造不了“改 IP / 执行脚本 / 重启”这类指令。
                    string mine = _token;
                    if (!string.IsNullOrEmpty(mine) && !string.Equals(msg.Token, mine, StringComparison.Ordinal))
                    {
                        Log?.Invoke($"[拒绝] 来自 {remoteIp} 的 {msg.MsgType} 令牌不符（疑似伪造指令），已丢弃");
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
                    else if (msg is MasterFilePushCmdMsg fp && fp.SessionId == sid)
                    {
                        Log?.Invoke($"收到控制端文件下发：{fp.FileName}（{(fp.RunAfter ? "下载后执行" : "仅保存")}，{fp.SizeBytes / 1024} KB）MAC={fp.LocalMac}");
                        OnFilePush?.Invoke(fp);
                    }
                    else if (msg is MasterSelfUpdateCmdMsg su && su.SessionId == sid)
                    {
                        Log?.Invoke($"收到控制端自升级指令（{su.SizeBytes / 1024} KB）MAC={su.LocalMac}");
                        OnSelfUpdate?.Invoke(su);
                    }
                    else if (msg is MasterScreenshotCmdMsg sc && sc.SessionId == sid)
                    {
                        Log?.Invoke($"收到控制端截屏指令 MAC={sc.LocalMac}");
                        OnScreenshot?.Invoke(sc);
                    }
                    else if (msg is MasterProcCmdMsg pc && pc.SessionId == sid)
                    {
                        Log?.Invoke($"收到控制端程序指令：{pc.Action} MAC={pc.LocalMac}");
                        OnProcCmd?.Invoke(pc);
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
