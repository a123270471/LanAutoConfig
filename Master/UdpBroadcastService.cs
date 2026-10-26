using LanAutoConfig.Common;

namespace LanAutoConfig.Master
{
    /// <summary>控制端 UDP 广播服务：周期性（2.5s）广播 BroadcastTemplate 模板 + SessionId。</summary>
    public class UdpBroadcastService : IDisposable
    {
        private UdpClient _udp;
        private CancellationTokenSource _cts;
        private string _sessionId;
        private TemplateModel _template;
        private volatile bool _running;

        public event Action<string> Log;

        /// <summary>启动广播。绑定到指定本机 IPv4（broadcastIp）以确保广播从对应网卡发出；未指定时自动取第一块有 IPv4 的网卡。</summary>
        public bool Start(string sessionId, TemplateModel template, string broadcastIp = null)
        {
            _sessionId = sessionId;
            _template = template;
            var localIp = broadcastIp;
            if (string.IsNullOrEmpty(localIp)) localIp = NetworkHelper.GetLocalIpv4();
            if (string.IsNullOrEmpty(localIp)) localIp = "127.0.0.1";
            try
            {
                _udp = new UdpClient();
                _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udp.EnableBroadcast = true;
                _udp.Client.Bind(new IPEndPoint(IPAddress.Parse(localIp), 0));
                _running = true;
                _cts = new CancellationTokenSource();
                _ = Task.Run(() => BroadcastLoop(_cts.Token));
                Log?.Invoke($"UDP 广播已启动，本机 IP={localIp}，端口 {AppConfig.UdpPort}，会话 {sessionId.Substring(0, 8)}…");
                return true;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"UDP 广播启动失败：{ex.Message}");
                return false;
            }
        }

        public void Stop()
        {
            _running = false;
            try { _cts?.Cancel(); } catch { }
            try { _udp?.Close(); } catch { }
            _udp = null;
        }

        private async Task BroadcastLoop(CancellationToken ct)
        {
            var ep = new IPEndPoint(IPAddress.Broadcast, AppConfig.UdpPort);
            while (_running && !ct.IsCancellationRequested)
            {
                try
                {
                    var msg = new BroadcastTemplateMsg(_sessionId, _template.IpPrefix, _template.SubnetMask,
                        _template.Gateway, _template.Dns1, _template.Dns2, _template.HostNamePrefix, _template.IsDhcp);
                    string json = JsonHelper.Serialize(msg);
                    byte[] bytes = Encoding.UTF8.GetBytes(json);
                    await _udp.SendAsync(bytes, bytes.Length, ep);
                }
                catch { }
                try { await Task.Delay(AppConfig.BroadcastIntervalMs, ct); }
                catch { break; }
            }
        }

        public void Dispose() => Stop();
    }
}
