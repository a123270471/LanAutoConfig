using LanAutoConfig.Common;

namespace LanAutoConfig.Slave
{
    /// <summary>被控端 UDP 模板监听：自动发现控制端，接收 BroadcastTemplate，记录控制端 IP。</summary>
    public class UdpTemplateListener : IDisposable
    {
        private UdpClient _udp;
        private CancellationTokenSource _cts;

        /// <summary>收到新模板：模板 + 控制端 IP + 会话 ID。</summary>
        public event Action<TemplateModel, string, string> OnTemplate;
        public event Action<string> Log;

        public bool Start()
        {
            try
            {
                _udp = new UdpClient();
                _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udp.EnableBroadcast = true;
                _udp.Client.Bind(new IPEndPoint(IPAddress.Any, AppConfig.UdpPort));
                _cts = new CancellationTokenSource();
                _ = Task.Run(() => ReceiveLoop(_cts.Token));
                Log?.Invoke($"已开始监听 UDP 广播（端口 {AppConfig.UdpPort}），等待控制端下发模板…");
                return true;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"UDP 监听启动失败：{ex.Message}");
                return false;
            }
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _udp?.Close(); } catch { }
        }

        private async Task ReceiveLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult res;
                try { res = await _udp.ReceiveAsync(); }
                catch (Exception ex)
                {
                    // 原来是一句空 catch + break：任何一次非停止类的接收异常（网卡被禁用后重新启用、
                    // 驱动瞬时错误）都会让这个循环永久退出，而界面上“已监听 UDP”的日志还在，
                    // 现场表现就是“控制端一直在广播，这台机器却再也收不到模板”。改成重试 + 记日志。
                    if (ct.IsCancellationRequested) break;
                    Log?.Invoke($"[UDP 监听] 接收出错，稍后重试：{ex.Message}");
                    try { await Task.Delay(500, ct); } catch { break; }
                    continue;
                }
                string masterIp = res.RemoteEndPoint.Address.ToString();
                string json;
                try { json = Encoding.UTF8.GetString(res.Buffer); }
                catch { continue; }

                var msg = JsonHelper.Deserialize(json);
                if (!(msg is BroadcastTemplateMsg t) || !t.Validate()) continue;

                // 订阅者（窗体）里的异常不能让接收循环挂掉：它一死，UDP 就再也收不到模板了。
                try { OnTemplate?.Invoke(TemplateModel.From(t), masterIp, t.SessionId); }
                catch (Exception ex) { Log?.Invoke($"[UDP 监听] 处理模板时出错（已忽略该条）：{ex.Message}"); }
            }
        }

        public void Dispose() => Stop();
    }
}
