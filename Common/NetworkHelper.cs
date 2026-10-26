using System.Net.NetworkInformation;

namespace LanAutoConfig.Common
{
    /// <summary>本机网络信息工具：枚举网卡、获取本机 IPv4、MAC。</summary>
    public static class NetworkHelper
    {
        /// <summary>常见虚拟/隧道网卡关键字：这些网卡不应作为默认上报/广播网卡。</summary>
        private static readonly string[] VirtualHints =
        {
            "virtual", "vmware", "virtualbox", "hyper-v", "vethernet", "tap-", "tap adapter",
            "wintun", "wireguard", "openvpn", "vpn", "docker", "npcap", "loopback", "bluetooth"
        };

        /// <summary>判断是否为虚拟/隧道类网卡（按描述与名称做关键字匹配）。</summary>
        public static bool IsVirtualAdapter(NetworkInterface ni)
        {
            string text = ((ni.Description ?? "") + " " + (ni.Name ?? "")).ToLowerInvariant();
            foreach (var h in VirtualHints)
                if (text.Contains(h)) return true;
            return false;
        }

        /// <summary>是否为自动专用地址（169.254.x.x，说明没拿到 DHCP/静态配置）。</summary>
        public static bool IsApipa(string ip)
            => !string.IsNullOrEmpty(ip) && ip.StartsWith("169.254.", StringComparison.Ordinal);

        /// <summary>枚举已启用网卡（排除回环/隧道），物理网卡排在虚拟网卡之前。</summary>
        public static List<NetworkAdapterInfo> GetAdapters()
        {
            var list = new List<NetworkAdapterInfo>();
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                if (ni.OperationalStatus != OperationalStatus.Up) continue;

                var info = new NetworkAdapterInfo
                {
                    Alias = ni.Name,
                    Mac = ni.GetPhysicalAddress().ToString() ?? "",
                    Up = true,
                    Virtual = IsVirtualAdapter(ni)
                };
                if (info.Mac.Length == 12)
                    info.Mac = string.Join("-", Enumerable.Range(0, 6).Select(i => info.Mac.Substring(i * 2, 2)));
                list.Add(info);
            }
            return list.OrderBy(a => a.Virtual ? 1 : 0).ToList();
        }

        /// <summary>获取指定网卡别名对应的 IPv4 地址（优先非 APIPA；无则返回 null）。</summary>
        public static string GetIpv4ByAdapter(string alias)
        {
            if (string.IsNullOrEmpty(alias)) return null;
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!string.Equals(ni.Name, alias, StringComparison.OrdinalIgnoreCase)) continue;
                string first = null;
                foreach (UnicastIPAddressInformation u in ni.GetIPProperties().UnicastAddresses)
                {
                    if (u.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    string ip = u.Address.ToString();
                    if (!IsApipa(ip)) return ip;   // 优先返回可用地址
                    if (first == null) first = ip;
                }
                return first;
            }
            return null;
        }

        /// <summary>获取本机默认 IPv4（物理网卡优先，跳过 APIPA；都为 APIPA 时才兜底返回）。</summary>
        public static string GetLocalIpv4()
        {
            var adapters = GetAdapters();
            foreach (var a in adapters)
            {
                string ip = GetIpv4ByAdapter(a.Alias);
                if (!string.IsNullOrEmpty(ip) && !IsApipa(ip)) return ip;
            }
            foreach (var a in adapters)
            {
                string ip = GetIpv4ByAdapter(a.Alias);
                if (!string.IsNullOrEmpty(ip)) return ip;
            }
            return null;
        }

        /// <summary>本机所有网卡 MAC（统一 '-' 分隔），用于校验执行指令是否发给本机。</summary>
        public static HashSet<string> GetLocalMacs()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in GetAdapters())
                if (!string.IsNullOrEmpty(a.Mac)) set.Add(NormalizeMac(a.Mac));
            return set;
        }

        /// <summary>MAC 归一化：统一分隔符与大小写，便于比较（AA:BB:.. / aa-bb-.. 视为同一个）。</summary>
        public static string NormalizeMac(string mac)
            => string.IsNullOrEmpty(mac) ? "" : mac.Trim().Replace(":", "-").ToUpperInvariant();

        /// <summary>判断两个 IPv4 是否在同一子网（按给定掩码）。</summary>
        public static bool IsSameSubnet(string a, string b, string mask)
        {
            if (!Validators.TryParseIpv4(a, out byte[] ba)) return false;
            if (!Validators.TryParseIpv4(b, out byte[] bb)) return false;
            if (!Validators.TryParseIpv4(mask, out byte[] bm)) return false;
            for (int i = 0; i < 4; i++)
                if ((ba[i] & bm[i]) != (bb[i] & bm[i])) return false;
            return true;
        }

        /// <summary>列出指定网卡上的全部 IPv4 地址。</summary>
        public static List<string> GetIpv4List(string alias)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(alias)) return list;
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!string.Equals(ni.Name, alias, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (UnicastIPAddressInformation u in ni.GetIPProperties().UnicastAddresses)
                {
                    if (u.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    list.Add(u.Address.ToString());
                }
            }
            return list;
        }

        /// <summary>指定网卡上是否存在该 IPv4（用于确认临时地址还在，避免绑定到已删除的地址）。</summary>
        public static bool HasIpv4(string alias, string ip)
        {
            if (string.IsNullOrEmpty(alias) || string.IsNullOrEmpty(ip)) return false;
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!string.Equals(ni.Name, alias, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (UnicastIPAddressInformation u in ni.GetIPProperties().UnicastAddresses)
                {
                    if (u.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (string.Equals(u.Address.ToString(), ip, StringComparison.Ordinal)) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 候选地址是否已被占用（ARP 探测：有应答说明同一链路上已有主机使用它）。
        /// </summary>
        public static bool IsAddressInUse(string srcIp, string candidateIp)
            => IsOnLink(srcIp, candidateIp);

        /// <summary>
        /// ARP 探测：从 srcIp 所在链路看，targetIp 是否二层可达（同一广播域内有主机应答）。
        /// 不依赖路由表，因此“同交换机不同网段”也能探到。
        /// </summary>
        public static bool IsOnLink(string srcIp, string targetIp)
        {
            if (!Validators.TryParseIpv4(srcIp, out _)) return false;
            if (!Validators.TryParseIpv4(targetIp, out _)) return false;
            try
            {
                var mac = new byte[6];
                int len = mac.Length;
                int rc = SendARP(IpToInt(targetIp), IpToInt(srcIp), mac, ref len);
                return rc == 0; // NO_ERROR
            }
            catch { return false; }
        }

        private static int IpToInt(string ip)
        {
            // SendARP 的 IPAddr 参数与 inet_addr 一致：按内存里的网络字节序直接当 32 位整数用，
            // 在小端机器上就是 BitConverter.ToInt32（写成 b0 | b1<<8 | b2<<16 | b3<<24）。
            byte[] b = IPAddress.Parse(ip.Trim()).GetAddressBytes();
            return b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24);
        }

        [System.Runtime.InteropServices.DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern int SendARP(int destIp, int srcIp, byte[] macAddr, ref int phyAddrLen);
    }
}
