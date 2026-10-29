using LanAutoConfig.Common;

namespace LanAutoConfig.Master
{
    /// <summary>
    /// 控制端 UDP 广播服务：周期性广播 BroadcastTemplate 模板 + SessionId。
    /// 开播后的前 <see cref="AppConfig.BroadcastFastWindowMs"/> 毫秒按 <see cref="AppConfig.BroadcastFastIntervalMs"/>
    /// 快发（让正在开机的被控端几乎立刻收到模板），之后回到 <see cref="AppConfig.BroadcastIntervalMs"/> 常规周期。
    /// </summary>
    public class UdpBroadcastService : IDisposable
    {
        private UdpClient _udp;
        private CancellationTokenSource _cts;
        private string _sessionId;
        private TemplateModel _template;
        private volatile bool _running;
        /// <summary>是否处于“刚开播”的快速广播窗口（300ms 一发）。</summary>
        private volatile bool _burst;

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
                _burst = true;   // 开播后先快发一阵，让正在开机的被控端几乎立刻收到模板
                _cts = new CancellationTokenSource();
                var myCts = _cts;
                _ = Task.Run(() => BroadcastLoop(myCts.Token));
                // 快发窗口结束后回到常规周期。必须校验“还是不是这一轮”：
                // 否则“停止 → 10 秒内又开始”时，上一轮遗留的延时任务会把新会话的快发也关掉。
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(AppConfig.BroadcastFastWindowMs, myCts.Token); } catch { return; }
                    if (ReferenceEquals(_cts, myCts) && _running) _burst = false;
                });
                Log?.Invoke($"UDP 广播已启动，本机 IP={localIp}，端口 {AppConfig.UdpPort}，会话 {sessionId.Substring(0, 8)}…" +
                            $"（前 {AppConfig.BroadcastFastWindowMs / 1000} 秒每 {AppConfig.BroadcastFastIntervalMs}ms 快发，之后每 {AppConfig.BroadcastIntervalMs}ms）");
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
            _burst = false;
            try { _cts?.Cancel(); } catch { }
            try { _udp?.Close(); } catch { }
            _udp = null;
        }

        private async Task BroadcastLoop(CancellationToken ct)
        {
            var ep = new IPEndPoint(IPAddress.Broadcast, AppConfig.UdpPort);
            int consecutiveErrors = 0;
            while (_running && !ct.IsCancellationRequested)
            {
                try
                {
                    var msg = new BroadcastTemplateMsg(_sessionId, _template.IpPrefix, _template.SubnetMask,
                        _template.Gateway, _template.Dns1, _template.Dns2, _template.HostNamePrefix, _template.IsDhcp,
                        _template.Workgroup, _template.TimeZoneId, _template.SyncTime, _template.Domain);
                    string json = JsonHelper.Serialize(msg);
                    byte[] bytes = Encoding.UTF8.GetBytes(json);
                    var udp = _udp;
                    if (udp == null) break;
                    await udp.SendAsync(bytes, bytes.Length, ep);
                    if (consecutiveErrors > 0)
                    {
                        consecutiveErrors = 0;
                        Log?.Invoke("UDP 广播已恢复正常");
                    }
                }
                catch (Exception ex)
                {
                    // 原来这里是空 catch：广播发不出去（选错网卡/网卡被禁用/被防火墙拦）时
                    // 界面照样显示“广播: 运行中”，被控端一台都收不到，日志一片空白。
                    if (ct.IsCancellationRequested || !_running) break;
                    if (consecutiveErrors++ == 0)
                        Log?.Invoke($"[UDP 广播] 发送失败（后续同类错误不再重复记录）：{ex.Message}");
                }
                int wait = _burst ? AppConfig.BroadcastFastIntervalMs : AppConfig.BroadcastIntervalMs;
                try { await Task.Delay(wait, ct); }
                catch { break; }
            }
        }

        public void Dispose() => Stop();
    }
}
