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
        /// <summary>收到被控端自定义命令执行结果。</summary>
        public event Action<SlaveCustomResultMsg> OnCustomResult;
        /// <summary>收到被控端验收（当前实际配置）回传。</summary>
        public event Action<SlaveVerifyResultMsg> OnVerifyResult;
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
                    if (!msg.Validate())
                    {
                        Log?.Invoke($"[丢弃] 来自 {remoteIp} 的 {msg.MsgType} 校验失败（Magic/Check 不匹配）");
                        return;
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
