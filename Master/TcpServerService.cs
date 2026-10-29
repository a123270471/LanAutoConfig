using LanAutoConfig.Common;

namespace LanAutoConfig.Master
{
    /// <summary>
    /// 控制端 TCP 服务端：监听 48889，接收被控端的 SlaveReport 与 SlaveResultReport。
    /// 每消息以换行结尾，UTF-8 编码，读取到完整行后反序列化分派。
    /// </summary>
    public class TcpServerService : IDisposable
    {
        private TcpListener _listener;
        private CancellationTokenSource _cts;

        public event Action<SlaveReportMsg> OnSlaveReport;
        public event Action<SlaveResultReportMsg> OnResultReport;
        /// <summary>收到被控端心跳（在线状态）。</summary>
        public event Action<SlaveHeartbeatMsg> OnHeartbeat;
        /// <summary>被控端正常退出前的“告别”通知：收到就立刻置离线，不用等心跳超时。</summary>
        public event Action<SlaveOfflineMsg> OnSlaveOffline;
        /// <summary>收到被控端自定义命令执行结果。</summary>
        public event Action<SlaveCustomResultMsg> OnCustomResult;
        /// <summary>收到被控端验收（当前实际配置）回传。</summary>
        public event Action<SlaveVerifyResultMsg> OnVerifyResult;
        /// <summary>收到文件下发/自升级的执行结果。</summary>
        public event Action<SlaveFileResultMsg> OnFileResult;
        /// <summary>收到截屏回传。</summary>
        public event Action<SlaveScreenshotResultMsg> OnScreenshot;
        /// <summary>
        /// 按 MAC 查“该终端的身份令牌”。返回空表示还没记录过（首次上报/心跳，放行并稍后登记）。
        /// 由主窗体接到 SessionManager 上。
        /// </summary>
        public Func<string, string> TokenResolver;
        public event Action<string> Log;

        public bool Start()
        {
            try
            {
                _listener = new TcpListener(IPAddress.Any, AppConfig.TcpPort);
                // 允许复用地址：上一次运行留下的 TIME_WAIT 连接（本地 48889）会让新监听 Bind 失败，
                // 表现为“关掉控制端立刻重开就提示端口被占用”，其实并没有别的程序在监听。
                _listener.Server.ExclusiveAddressUse = false;
                _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listener.Start();
                _cts = new CancellationTokenSource();
                _ = Task.Run(() => AcceptLoop(_cts.Token));
                Log?.Invoke($"TCP 服务端已启动，监听端口 {AppConfig.TcpPort}");
                return true;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"TCP 服务端启动失败：{ex.Message}");
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
                    // 正常停止（Stop 里 listener.Stop()）会走到这里，不是错误
                    if (ct.IsCancellationRequested || _listener == null) break;
                    // 其它异常（句柄耗尽、网卡重配…）不能直接跳出：那样服务端就永久不接新连接了，
                    // 而且日志一个字都没有，现场只会看到“被控端全部连不上”。
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

                    // 身份令牌校验：该终端登记过的令牌必须与消息里的一致。
                    // 没登记过（首次上报/心跳）就放行，由业务处理负责登记。
                    string macOfMsg = MacOf(msg);
                    if (!string.IsNullOrEmpty(macOfMsg) && TokenResolver != null)
                    {
                        string expect = TokenResolver(macOfMsg);
                        if (!string.IsNullOrEmpty(expect) && !string.Equals(msg.Token, expect, StringComparison.Ordinal))
                        {
                            Log?.Invoke($"[拒绝] 来自 {remoteIp} 的 {msg.MsgType} 令牌不符（MAC={macOfMsg}，疑似伪造），已丢弃");
                            return;
                        }
                    }
                    // 把“这条 TCP 连接的真实来源地址”附着到消息上：这是唯一确定可达的地址，
                    // 控制端回发指令时以它为准（心跳/上报自带的 LocalIp 可能已过期）。
                    msg.RemoteIp = remoteIp;
                    if (msg is SlaveReportMsg r)
                    {
                        Log?.Invoke($"收到上报：{remoteIp} MAC={r.LocalMac}");
                        OnSlaveReport?.Invoke(r);
                    }
                    else if (msg is SlaveResultReportMsg rr)
                    {
                        Log?.Invoke($"收到结果：{remoteIp} MAC={rr.LocalMac} 成功={rr.Success}");
                        OnResultReport?.Invoke(rr);
                    }
                    else if (msg is SlaveHeartbeatMsg hb)
                    {
                        // 心跳很频繁（每台 10 秒一条），默认不写日志，只更新在线状态
                        OnHeartbeat?.Invoke(hb);
                    }
                    else if (msg is SlaveOfflineMsg off)
                    {
                        Log?.Invoke($"被控端下线：{remoteIp} MAC={off.LocalMac}（{off.Reason}）");
                        OnSlaveOffline?.Invoke(off);
                    }
                    else if (msg is SlaveCustomResultMsg cr)
                    {
                        Log?.Invoke($"收到自定义命令结果：{remoteIp} MAC={cr.LocalMac} 成功={cr.Success} 退出码={cr.ExitCode}");
                        OnCustomResult?.Invoke(cr);
                    }
                    else if (msg is SlaveVerifyResultMsg vr)
                    {
                        Log?.Invoke($"收到验收结果：{remoteIp} MAC={vr.LocalMac} 匹配={vr.Match} 实际={vr.CurrentHostName}/{vr.CurrentIp}");
                        OnVerifyResult?.Invoke(vr);
                    }
                    else if (msg is SlaveFileResultMsg fr)
                    {
                        Log?.Invoke($"收到文件结果：{remoteIp} MAC={fr.LocalMac} {fr.FileName} 成功={fr.Success}");
                        OnFileResult?.Invoke(fr);
                    }
                    else if (msg is SlaveScreenshotResultMsg sr)
                    {
                        Log?.Invoke($"收到截屏：{remoteIp} MAC={sr.LocalMac} 成功={sr.Success} {sr.Width}x{sr.Height}");
                        OnScreenshot?.Invoke(sr);
                    }
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"[丢弃] 处理 {remoteIp} 的连接失败：{ex.Message}");
                }
            }
        }

        /// <summary>取消息里的终端 MAC（所有回传类消息都带 LocalMac）。</summary>
        private static string MacOf(NetMessage m) => m switch
        {
            SlaveReportMsg x => x.LocalMac,
            SlaveHeartbeatMsg x => x.LocalMac,
            SlaveOfflineMsg x => x.LocalMac,
            SlaveResultReportMsg x => x.LocalMac,
            SlaveCustomResultMsg x => x.LocalMac,
            SlaveVerifyResultMsg x => x.LocalMac,
            SlaveFileResultMsg x => x.LocalMac,
            SlaveScreenshotResultMsg x => x.LocalMac,
            _ => null
        };

        public void Dispose() => Stop();
    }
}
