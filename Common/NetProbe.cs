using System.Net.NetworkInformation;

namespace LanAutoConfig.Common
{
    /// <summary>单台终端的连通性探测结果（连通性矩阵用）。</summary>
    public class ProbeResult
    {
        public string Ip { get; set; }
        public bool PingOk { get; set; }
        public int PingMs { get; set; } = -1;
        public bool TcpOk { get; set; }
        public string Error { get; set; }

        /// <summary>界面上一格显示的文字：12ms / 通 / 不通。</summary>
        public string Text
            => PingOk ? $"{PingMs}ms" : (TcpOk ? "仅TCP通" : "不通");
    }

    /// <summary>IP 占用情况（真实冲突扫描用）。</summary>
    public class IpOccupancy
    {
        public string Ip { get; set; }
        /// <summary>该 IP 在 ARP 表里对应的 MAC（空 = 没测到占用）。</summary>
        public string ActualMac { get; set; }
        /// <summary>期望占用它的终端 MAC。</summary>
        public string ExpectedMac { get; set; }
        /// <summary>期望占用它的主机名（仅用于提示）。</summary>
        public string Owner { get; set; }
        public bool PingOk { get; set; }

        /// <summary>冲突：ARP 里这个 IP 对应的不是期望的那台机器。</summary>
        public bool Conflict => !string.IsNullOrEmpty(ActualMac)
                                && !string.Equals(NetworkHelper.NormalizeMac(ActualMac),
                                                  NetworkHelper.NormalizeMac(ExpectedMac),
                                                  StringComparison.OrdinalIgnoreCase);

        /// <summary>该 IP 有人应答、但不是我们这台机器 —— 典型的“网段里已有老机器占着地址”。</summary>
        public bool Free => string.IsNullOrEmpty(ActualMac) && !PingOk;

        public string Describe()
        {
            if (Conflict) return $"该 IP 已被 {ActualMac} 占用（不是本机 {ExpectedMac}）";
            if (!string.IsNullOrEmpty(ActualMac)) return "已在本机 ARP 表中，MAC 匹配";
            if (PingOk) return "有主机应答但拿不到 MAC";
            return "未被占用";
        }
    }

    /// <summary>
    /// 网络探测小工具：ICMP ping、TCP 端口探测、读本机 ARP 表。
    /// 全部只用系统自带能力（System.Net.NetworkInformation / arp.exe），不引第三方库。
    /// </summary>
    public static class NetProbe
    {
        /// <summary>ping 一次（同步，带超时）。失败不抛异常。</summary>
        public static (bool Ok, int Ms, string Error) Ping(string ip, int timeoutMs = 900)
        {
            if (!IPAddress.TryParse(ip, out IPAddress addr)) return (false, -1, "IP 不合法");
            try
            {
                using var ping = new System.Net.NetworkInformation.Ping();
                var reply = ping.Send(addr, timeoutMs, new byte[32]);
                if (reply != null && reply.Status == IPStatus.Success)
                    return (true, (int)reply.RoundtripTime, null);
                return (false, -1, reply?.Status.ToString() ?? "无响应");
            }
            catch (Exception ex) { return (false, -1, ex.Message); }
        }

        /// <summary>探测 TCP 端口是否可连（被控端 48889 起来了吗）。</summary>
        public static bool TcpProbe(string ip, int port, int timeoutMs = 900)
            => TcpSend.CanConnect(ip, port, null, timeoutMs);

        /// <summary>
        /// 并发探测一批 IP（ping + TCP 端口），每台完成就回调一次。
        /// 并发度刻意压在 24：再高会把自己的 ARP 表/DHCP 打拥塞，反而测不准。
        /// </summary>
        public static void ProbeAll(IEnumerable<string> ips, int port, Action<ProbeResult> onEach)
        {
            var list = (ips ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
            if (list.Count == 0) return;

            var opts = new ParallelOptions { MaxDegreeOfParallelism = 24 };
            Parallel.ForEach(list, opts, ip =>
            {
                var r = new ProbeResult { Ip = ip };
                var (ok, ms, err) = Ping(ip, 900);
                r.PingOk = ok; r.PingMs = ms; r.Error = err;
                if (port > 0) r.TcpOk = TcpProbe(ip, port, 900);
                onEach?.Invoke(r);
            });
        }

        /// <summary>
        /// 真实 IP 冲突扫描：先 ping 一遍把 ARP 缓存“喂”出来，再读 ARP 表比对 MAC。
        ///
        /// 为什么不只比模板里的重复：模板只能发现“两台机器被分配了同一个 IP”，
        /// 而现场真正踩的坑是“这个 IP 早就被网段里某台老机器/打印机/摄像头占着”，
        /// 那种冲突只有从 ARP 表里才看得见。
        /// </summary>
        public static List<IpOccupancy> ScanConflicts(
            IEnumerable<(string Ip, string ExpectedMac, string Owner)> candidates, Action<string> log = null)
        {
            var list = (candidates ?? Enumerable.Empty<(string, string, string)>())
                .Where(c => Validators.IsValidIp(c.Ip))
                .GroupBy(c => c.Ip).Select(g => g.First())
                .ToList();
            var result = new List<IpOccupancy>();
            if (list.Count == 0) return result;

            log?.Invoke($"开始扫描 {list.Count} 个目标 IP 的占用情况（先 ping 一遍填充 ARP 表）…");

            // 只 ping 目标地址：并发 24，单个 600ms，一个 /24 也就十几秒
            var pings = new Dictionary<string, bool>();
            var gate = new object();
            Parallel.ForEach(list, new ParallelOptions { MaxDegreeOfParallelism = 24 }, c =>
            {
                var (ok, _, _) = Ping(c.Ip, 600);
                lock (gate) pings[c.Ip] = ok;
            });

            var arp = ReadArpTable();
            log?.Invoke($"ARP 表读到 {arp.Count} 条记录");

            foreach (var c in list)
            {
                arp.TryGetValue(c.Ip, out string mac);
                result.Add(new IpOccupancy
                {
                    Ip = c.Ip,
                    ExpectedMac = c.ExpectedMac,
                    Owner = c.Owner,
                    ActualMac = mac,
                    PingOk = pings.TryGetValue(c.Ip, out bool p) && p
                });
            }
            return result;
        }

        /// <summary>
        /// 读本机 ARP 表（ip → MAC）。走 arp.exe，输出按 OEM 代码页解码
        /// （中文系统的表头是中文，直接用 UTF-8 解会乱码，但 IP/MAC 是 ASCII，其实不影响解析）。
        /// </summary>
        public static Dictionary<string, string> ReadArpTable()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var psi = new ProcessStartInfo("arp.exe", "-a")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using var p = Process.Start(psi);
                if (p == null) return map;

                using var buf = new MemoryStream();
                Task copy = p.StandardOutput.BaseStream.CopyToAsync(buf);
                if (!p.WaitForExit(8000)) { try { p.Kill(true); } catch { } }
                try { copy.Wait(2000); } catch { }

                string text = ConsoleText.Decode(buf.ToArray());
                foreach (string raw in text.Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.Length == 0) continue;
                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;
                    if (!Validators.IsValidIp(parts[0])) continue;             // 只认以 IP 开头的行
                    string mac = parts[1].Replace(':', '-');
                    if (!Validators.IsValidMac(mac)) continue;                  // 广播/组播行也带 MAC，照样收下
                    map[parts[0]] = mac.ToUpperInvariant();
                }
            }
            catch (Exception ex) { Debug.WriteLine("读取 ARP 表失败：" + ex.Message); }
            return map;
        }
    }
}
