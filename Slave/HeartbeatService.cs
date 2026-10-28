using LanAutoConfig.Common;

namespace LanAutoConfig.Slave
{
    /// <summary>
    /// 被控端心跳：收到控制端模板后，每 <see cref="AppConfig.HeartbeatIntervalMs"/> 毫秒
    /// 给控制端发一条 SlaveHeartbeat，用于控制端显示在线/离线与执行进度。
    ///
    /// 这里刻意不复用 TcpSend（它有 5 秒超时 + 重试，心跳不需要这么重），
    /// 单独用 2 秒超时的轻量发送，失败只记一次日志，不堆积连接。
    /// </summary>
    public class HeartbeatService : IDisposable
    {
        private readonly object _lock = new object();
        private System.Threading.Timer _timer;
        private string _masterIp, _sessionId, _mac, _hostName, _localIp;
        /// <summary>网卡别名与掩码：用于每条心跳重新算“当前该报哪个本机地址”。</summary>
        private string _adapterAlias, _mask;
        private volatile string _state = "idle";
        private volatile bool _running;   // 定时器是否在跑（Stop 后不要再补发）
        private int _failing;          // 连续失败次数，用于避免刷屏

        public event Action<string> Log;

        public string LastError { get; private set; }

        /// <summary>设置上报目标（收到新模板时调用）。</summary>
        public void Configure(string masterIp, string sessionId, string mac, string hostName, string localIp,
            string adapterAlias = null, string mask = null)
        {
            lock (_lock)
            {
                _masterIp = masterIp;
                _sessionId = sessionId;
                _mac = mac;
                _hostName = hostName;
                _localIp = localIp;
                _adapterAlias = adapterAlias;
                _mask = mask;
            }
        }

        /// <summary>
        /// 状态变化（已上报 / 执行中 / 成功 / 失败）立刻补推一次心跳，
        /// 不再等下一个 10 秒周期，否则控制端的进度看板会明显滞后。
        /// </summary>
        public void SetState(string state)
        {
            string s = string.IsNullOrEmpty(state) ? "idle" : state;
            if (string.Equals(_state, s, StringComparison.Ordinal)) return;
            _state = s;
            if (_running) Task.Run(() => Tick());
        }

        /// <summary>
        /// 启动心跳。<b>第一条立即发</b>（dueTime = 0）—— 首位触发时间决定控制端多久能看到这台机“上线”，
        /// 若按周期等，被控端要过整整一个心跳周期（10 秒）才会被控制端发现。
        /// 重复调用（换网卡、换会话、控制端重发模板）会把计时器重置成立即触发，保证新目标马上生效。
        /// </summary>
        public void Start()
        {
            lock (_lock)
            {
                if (_timer == null)
                {
                    _running = true;
                    _timer = new System.Threading.Timer(_ => Tick(), null, 0, AppConfig.HeartbeatIntervalMs);
                }
                else
                {
                    _running = true;
                    try { _timer.Change(0, AppConfig.HeartbeatIntervalMs); } catch { }
                }
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                _running = false;
                _timer?.Dispose();
                _timer = null;
            }
        }

        private void Tick()
        {
            // 定时器回调跑在线程池上：这里任何未捕获异常都会**直接终止进程**
            // （.NET 的行为），而界面上只会看到“程序莫名没了”。所以整个方法体必须兜住。
            if (!_running) return;   // Stop() 之后仍可能有已排队的回调，别再发
            try
            {
                DoTick();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                // 记一条就够，别让坏掉的网卡状态每 10 秒刷一行
                if (System.Threading.Interlocked.CompareExchange(ref _failing, 1, 0) == 0)
                {
                    try { Log?.Invoke("心跳发送异常（已忽略，继续运行）：" + ex.Message); } catch { }
                }
            }
        }

        private void DoTick()
        {
            string ip, sid, mac, host, localIp, alias, mask;
            lock (_lock)
            {
                ip = _masterIp; sid = _sessionId; mac = _mac; host = _hostName; localIp = _localIp;
                alias = _adapterAlias; mask = _mask;
            }
            if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(sid) || string.IsNullOrEmpty(mac)) return;

            // 每条心跳都重新算一次本机地址：加了临时同网段地址、或刚执行完改 IP 之后，
            // 原来那次 Configure 记下的地址已经过时（控制端若拿它当目标地址就会打空）。
            if (!string.IsNullOrEmpty(alias))
            {
                string fresh = PickLocalIp(alias, ip, mask);
                if (!string.IsNullOrEmpty(fresh)) localIp = fresh;
            }

            var msg = new SlaveHeartbeatMsg(sid)
            {
                LocalMac = mac,
                HostName = host,
                LocalIp = localIp,
                State = _state
            };
            bool ok = SendLight(ip, AppConfig.TcpPort, msg, out string err);
            LastError = ok ? null : err;
            if (!ok)
            {
                // 只在“从正常变成失败”时记一条，避免 10 秒一条刷满日志
                if (System.Threading.Interlocked.CompareExchange(ref _failing, 1, 0) == 0)
                    Log?.Invoke($"心跳发送失败（{ip}）：{err}");
            }
            else if (System.Threading.Interlocked.Exchange(ref _failing, 0) == 1)
            {
                Log?.Invoke("心跳已恢复");
            }
        }

        /// <summary>
        /// 从网卡现有 IPv4 中挑“最可能到得了控制端的那个”：与控制端同网段优先，否则取第一个非 APIPA。
        /// 只用 NetworkHelper（纯 P/Invoke + 托管枚举），不碰 WinForms 控件，可在定时器线程安全调用。
        /// </summary>
        private static string PickLocalIp(string alias, string masterIp, string mask)
        {
            string first = null;
            foreach (string a in NetworkHelper.GetIpv4List(alias))
            {
                if (NetworkHelper.IsApipa(a)) continue;
                if (first == null) first = a;
                if (!string.IsNullOrEmpty(mask) && NetworkHelper.IsSameSubnet(a, masterIp, mask)) return a;
            }
            return first;
        }

        /// <summary>轻量发送：2 秒超时、不重试、不绑定源地址。</summary>
        private static bool SendLight(string ip, int port, NetMessage msg, out string error)
        {
            error = null;
            if (!IPAddress.TryParse(ip, out IPAddress dest)) { error = "目标 IP 不合法"; return false; }
            TcpClient client = null;
            try
            {
                client = new TcpClient();
                Task connect = client.ConnectAsync(dest, port);
                if (!connect.Wait(2000)) { try { client.Close(); } catch { } error = "连接超时"; return false; }
                connect.GetAwaiter().GetResult();

                string json = JsonHelper.Serialize(msg);
                byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
                var stream = client.GetStream();
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                try { client?.Close(); } catch { }
            }
        }

        public void Dispose() => Stop();
    }
}
