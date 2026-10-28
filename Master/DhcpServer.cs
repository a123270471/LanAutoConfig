using LanAutoConfig.Common;

namespace LanAutoConfig.Master
{
    /// <summary>DHCP 服务配置（地址池按模板的 IP 段自动推导）。</summary>
    public class DhcpConfig
    {
        public string ServerIp { get; set; }
        public string Mask { get; set; }
        public string Gateway { get; set; }
        public string Dns1 { get; set; }
        public string Dns2 { get; set; }
        public string PoolStart { get; set; }
        public string PoolEnd { get; set; }
        /// <summary>子网定向广播地址（Start 时按 ServerIp + Mask 自动算），回包发往这里以保证走对网卡。</summary>
        public string Broadcast { get; set; }
        public int LeaseSeconds { get; set; } = 7200;

        /// <summary>
        /// 用模板参数推导地址池：在 IP 前缀所在的子网内取 .100 ~ .250，
        /// 排除网关和控制端自己。地址池不需要手工填。
        /// </summary>
        public static bool TryDerive(string ipPrefix, string mask, string gateway, string serverIp, out DhcpConfig cfg, out string error)
        {
            cfg = null; error = null;
            if (!Validators.IsValidIpPrefix(ipPrefix)) { error = "IP 前缀格式错误，无法推导 DHCP 地址池"; return false; }
            if (!Validators.IsValidMask(mask)) { error = "子网掩码格式错误，无法推导 DHCP 地址池"; return false; }

            string prefix = ipPrefix.Trim();
            string first = null, last = null;
            for (int host = 100; host <= 250; host++)
            {
                string cand = prefix + host;
                if (!Validators.IsValidIp(cand)) continue;
                if (!NetworkHelper.IsSameSubnet(cand, prefix + "1", mask)) continue;   // 掩码比 /24 小时自动裁剪
                if (string.Equals(cand, gateway, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(cand, serverIp, StringComparison.OrdinalIgnoreCase)) continue;
                if (first == null) first = cand;
                last = cand;
            }
            if (first == null) { error = "按当前 IP 前缀/掩码推导不出可用的 DHCP 地址池（请检查掩码是否过小）"; return false; }

            cfg = new DhcpConfig
            {
                ServerIp = serverIp,
                Mask = mask.Trim(),
                Gateway = gateway,
                Dns1 = null,
                Dns2 = null,
                PoolStart = first,
                PoolEnd = last,
                LeaseSeconds = 7200
            };
            return true;
        }
    }

    /// <summary>
    /// 极简 DHCP 服务器（UDP 67）：在没有 DHCP 的机房环境里，让被控端先拿到一个模板网段的地址，
    /// 从而能上报、能被下发。地址池直接用下发模板的 IP 前缀/掩码/网关/DNS。
    /// 需要管理员权限才能绑定 67 端口。
    /// </summary>
    public class DhcpServer : IDisposable
    {
        public const int ServerPort = 67;
        public const int ClientPort = 68;

        private UdpClient _udp;
        private CancellationTokenSource _cts;
        private readonly object _lock = new object();
        private readonly Dictionary<string, string> _leases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _busy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private DhcpConfig _cfg;

        public event Action<string> Log;

        public bool Running { get; private set; }
        public int LeaseCount { get { lock (_lock) return _leases.Count; } }
        public string PoolText => _cfg == null ? "-" : $"{_cfg.PoolStart} ~ {_cfg.PoolEnd}";

        public bool Start(DhcpConfig cfg, out string error)
        {
            error = null;
            if (cfg == null) { error = "DHCP 配置为空"; return false; }
            if (Running) return true;
            _cfg = cfg;
            cfg.Broadcast = ComputeBroadcast(cfg.ServerIp, cfg.Mask);
            try
            {
                _udp = CreateBoundSocket();
                _cts = new CancellationTokenSource();
                Running = true;
                _ = Task.Run(() => LoopAsync(_cts.Token));
                Log?.Invoke($"DHCP 服务已启动（0.0.0.0:{ServerPort} 收包，回发 {cfg.Broadcast ?? "255.255.255.255"}:{ClientPort}），" +
                            $"地址池 {PoolText}，网关 {cfg.Gateway}，租期 {cfg.LeaseSeconds / 60} 分钟");
                return true;
            }
            catch (Exception ex)
            {
                error = $"DHCP 服务启动失败（端口 {ServerPort} 需要管理员权限）：{ex.Message}";
                try { _udp?.Close(); } catch { }
                _udp = null;
                Running = false;
                return false;
            }
        }

        /// <summary>
        /// 收包必须绑定 0.0.0.0:67 —— 实测 Windows 不会把发往 255.255.255.255 的包交给绑定具体 IP 的套接字。
        /// 回包则发往“子网定向广播”（如 192.168.1.255），由路由表选中正确的网卡，避免多网卡机器从虚拟网卡发出去。
        /// </summary>
        private static UdpClient CreateBoundSocket()
        {
            var udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.EnableBroadcast = true;
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, ServerPort));
            return udp;
        }

        /// <summary>由 IP + 掩码算出子网定向广播地址（192.168.1.7 / 255.255.255.0 → 192.168.1.255）。</summary>
        public static string ComputeBroadcast(string ip, string mask)
        {
            if (!Validators.TryParseIpv4(ip, out byte[] a)) return null;
            if (!Validators.TryParseIpv4(mask, out byte[] m)) return null;
            var b = new byte[4];
            for (int i = 0; i < 4; i++) b[i] = (byte)(a[i] | (byte)~m[i]);
            return new IPAddress(b).ToString();
        }

        public void Stop()
        {
            Running = false;
            try { _cts?.Cancel(); } catch { }
            try { _udp?.Close(); } catch { }
            _udp = null;
            lock (_lock) { _leases.Clear(); _busy.Clear(); }
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            int recvErrors = 0;
            while (Running && !ct.IsCancellationRequested)
            {
                UdpReceiveResult res;
                try
                {
                    res = await _udp.ReceiveAsync();
                    recvErrors = 0;
                }
                catch (Exception ex)
                {
                    if (!Running || ct.IsCancellationRequested) break;   // 正常停止
                    // 不能直接 break：Windows 上收到 ICMP 端口不可达后，下一次 Receive 会报
                    // WSAECONNRESET(10054)，若就此退出，DHCP 静默停服而界面仍显示“已开启”，
                    // 新开机的被控端拿不到地址，整批装机卡在第一步还查不出原因。
                    if (recvErrors++ == 0)
                        Log?.Invoke($"[DHCP] 接收报文出错（继续服务，后续同类错误不再重复记录）：{ex.Message}");
                    try { await Task.Delay(200, ct); } catch { break; }
                    continue;
                }
                try
                {
                    byte[] reply;
                    string note;
                    lock (_lock) reply = HandlePacket(res.Buffer, _cfg, _leases, _busy, out note);
                    if (!string.IsNullOrEmpty(note)) Log?.Invoke(note);
                    if (reply != null && _udp != null)
                    {
                        // 回给“子网定向广播”（如 192.168.1.255）：路由表必然选中该网段的网卡，
                        // 不会像 255.255.255.255 那样可能从虚拟网卡发出去。
                        IPAddress target = IPAddress.Broadcast;
                        if (!string.IsNullOrEmpty(_cfg.Broadcast) && IPAddress.TryParse(_cfg.Broadcast, out IPAddress bc)) target = bc;
                        await _udp.SendAsync(reply, reply.Length, new IPEndPoint(target, ClientPort));
                    }
                }
                catch (Exception ex) { Log?.Invoke("DHCP 处理异常：" + ex.Message); }
            }
        }

        /// <summary>
        /// 纯逻辑处理一个 DHCP 报文，返回要回发的报文（null 表示不回复）。
        /// 独立成静态方法便于离线自检。
        /// </summary>
        public static byte[] HandlePacket(byte[] req, DhcpConfig cfg,
            IDictionary<string, string> leases, ISet<string> busy, out string note)
        {
            note = null;
            if (req == null || req.Length < 240 || cfg == null) return null;
            if (req[0] != 1) return null;                      // 只处理 BOOTREQUEST
            if (req[236] != 0x63 || req[237] != 0x82 || req[238] != 0x53 || req[239] != 0x63) return null;

            byte[] typeOpt = ReadOption(req, 53);
            if (typeOpt == null || typeOpt.Length == 0) return null;
            byte msgType = typeOpt[0];
            if (msgType == 0) return null;

            string mac = FormatMac(req, 28, req[2]);
            if (string.IsNullOrEmpty(mac)) return null;

            string requested = null;
            byte[] opt50 = ReadOption(req, 50);
            if (opt50 != null && opt50.Length == 4) requested = new IPAddress(opt50).ToString();

            string assigned = null;
            switch (msgType)
            {
                case 1: // DISCOVER → OFFER
                    assigned = PickAddress(cfg, mac, requested, leases, busy);
                    if (assigned == null) { note = $"DHCP 地址池已耗尽（{cfg.PoolStart}~{cfg.PoolEnd}）"; return null; }
                    note = $"DHCP 分配：{mac} → {assigned}（已提供）";
                    return BuildReply(req, cfg, assigned, 2);

                case 3: // REQUEST → ACK / NAK
                    assigned = PickAddress(cfg, mac, requested, leases, busy);
                    if (assigned == null) { note = $"DHCP 地址池已耗尽，拒绝 {mac}"; return null; }
                    if (requested != null && !string.Equals(requested, assigned, StringComparison.OrdinalIgnoreCase))
                    {
                        note = $"DHCP 拒绝：{mac} 请求 {requested}，只能提供 {assigned}";
                        return BuildReply(req, cfg, null, 6);   // NAK
                    }
                    leases[mac] = assigned;
                    note = $"DHCP 已分配：{mac} → {assigned}";
                    return BuildReply(req, cfg, assigned, 5);

                case 4: // DECLINE：客户端说地址冲突
                    if (leases.TryGetValue(mac, out string dec)) { busy.Add(dec); leases.Remove(mac); note = $"DHCP 收到冲突声明：{mac} 放弃 {dec}"; }
                    return null;

                case 7: // RELEASE
                    if (leases.TryGetValue(mac, out string rel)) { leases.Remove(mac); busy.Remove(rel); note = $"DHCP 释放：{mac} → {rel}"; }
                    return null;

                case 8: // INFORM → ACK（只给配置，不分配地址）
                    note = $"DHCP 配置应答：{mac}";
                    return BuildReply(req, cfg, null, 5);

                default:
                    return null;
            }
        }

        private static string PickAddress(DhcpConfig cfg, string mac, string requested,
            IDictionary<string, string> leases, ISet<string> busy)
        {
            if (leases.TryGetValue(mac, out string exist)) return exist;
            if (!string.IsNullOrEmpty(requested) && InPool(cfg, requested) && !busy.Contains(requested) &&
                !leases.Values.Contains(requested, StringComparer.OrdinalIgnoreCase))
                return requested;

            foreach (string ip in Pool(cfg))
            {
                if (busy.Contains(ip)) continue;
                if (leases.Values.Contains(ip, StringComparer.OrdinalIgnoreCase)) continue;
                return ip;
            }
            return null;
        }

        private static IEnumerable<string> Pool(DhcpConfig cfg)
        {
            string start = cfg.PoolStart, end = cfg.PoolEnd;
            if (!Validators.TryParseIpv4(start, out byte[] a) || !Validators.TryParseIpv4(end, out byte[] b)) yield break;
            uint from = ToUInt(a), to = ToUInt(b);
            for (uint v = from; v <= to && v != 0; v++)
            {
                byte[] x = { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
                yield return new IPAddress(x).ToString();
            }
            yield return end;   // 兜底（end 已包含在上面的循环里，这里防止空区间）
        }

        private static bool InPool(DhcpConfig cfg, string ip)
        {
            if (!Validators.TryParseIpv4(ip, out byte[] x)) return false;
            if (!Validators.TryParseIpv4(cfg.PoolStart, out byte[] a)) return false;
            if (!Validators.TryParseIpv4(cfg.PoolEnd, out byte[] b)) return false;
            uint v = ToUInt(x), s = ToUInt(a), e = ToUInt(b);
            return v >= s && v <= e;
        }

        private static byte[] BuildReply(byte[] req, DhcpConfig cfg, string yiaddr, byte msgType)
        {
            var opts = new List<byte>();
            void Opt(byte code, params byte[] data) { opts.Add(code); opts.Add((byte)data.Length); opts.AddRange(data); }

            Opt(53, msgType);
            Opt(54, Bytes(cfg.ServerIp));
            Opt(51, new byte[] { (byte)(cfg.LeaseSeconds >> 24), (byte)(cfg.LeaseSeconds >> 16), (byte)(cfg.LeaseSeconds >> 8), (byte)cfg.LeaseSeconds });
            Opt(1, Bytes(cfg.Mask));
            if (!string.IsNullOrEmpty(cfg.Gateway)) Opt(3, Bytes(cfg.Gateway));
            var dns = new List<byte>();
            if (!string.IsNullOrEmpty(cfg.Dns1)) dns.AddRange(Bytes(cfg.Dns1));
            if (!string.IsNullOrEmpty(cfg.Dns2)) dns.AddRange(Bytes(cfg.Dns2));
            if (dns.Count > 0) Opt(6, dns.ToArray());
            opts.Add(255);

            byte[] reply = new byte[240 + opts.Count];
            Array.Copy(req, 0, reply, 0, 240);
            reply[0] = 2;                         // BOOTREPLY
            reply[10] = 0x80; reply[11] = 0x00;   // 广播标志，保证无地址的客户端能收到
            if (!string.IsNullOrEmpty(yiaddr)) Array.Copy(Bytes(yiaddr), 0, reply, 16, 4);
            else Array.Clear(reply, 16, 4);
            Array.Clear(reply, 12, 4);            // ciaddr
            Array.Copy(Bytes(cfg.ServerIp), 0, reply, 20, 4);   // siaddr
            for (int i = 236; i < 240; i++) reply[i] = 0;
            reply[236] = 0x63; reply[237] = 0x82; reply[238] = 0x53; reply[239] = 0x63;
            Array.Copy(opts.ToArray(), 0, reply, 240, opts.Count);
            return reply;
        }

        private static byte[] ReadOption(byte[] buf, byte code)
        {
            int i = 240;
            while (i < buf.Length)
            {
                byte c = buf[i++];
                if (c == 0) continue;
                if (c == 255) break;
                if (i >= buf.Length) break;
                int len = buf[i++];
                if (i + len > buf.Length) break;
                if (c == code)
                {
                    var data = new byte[len];
                    Array.Copy(buf, i, data, 0, len);
                    return data;
                }
                i += len;
            }
            return null;
        }

        private static string FormatMac(byte[] buf, int offset, int hlen)
        {
            if (hlen != 6 || offset + 6 > buf.Length) return null;
            return string.Join("-", Enumerable.Range(0, 6).Select(i => buf[offset + i].ToString("X2")));
        }

        private static byte[] Bytes(string ip)
        {
            if (!Validators.TryParseIpv4(ip, out byte[] b)) return new byte[4];
            return b;
        }

        private static uint ToUInt(byte[] b) => ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];

        public void Dispose() => Stop();
    }
}
