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
        public event Action<string> Log;

        public bool Start()
        {
            try
            {
                _listener = new TcpListener(IPAddress.Any, AppConfig.TcpPort);
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
