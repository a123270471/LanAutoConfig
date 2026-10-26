using LanAutoConfig.Common;

namespace LanAutoConfig.Slave
{
    /// <summary>
    /// 控制端互连助手：处理“被控端与控制端不在同一网段、但在同一广播域（同一台交换机/VLAN）”的情况。
    ///
    /// 原理：临时给被控端选中网卡追加一个控制端网段的 IPv4（二级地址），这样两端的 TCP 往返都走二层直连，
    /// 控制端**不需要管理员权限、不需要加路由**；执行配置时原脚本会 Remove 掉该网卡全部 IPv4，临时地址随之消失。
    ///
    /// 安全措施：
    ///  - 加之前用 ARP 探测候选地址是否被占用，占用就换下一个；
    ///  - 加之后用 ARP 探测控制端是否二层可达，探不到（说明中间有路由器/VLAN 隔离）立即回滚，不留任何网络改动。
    /// </summary>
    public class MasterReach
    {
        private const int MaxCandidates = 60;
        private readonly LocalNetChanger _changer;
        private readonly object _lock = new object();

        public MasterReach(LocalNetChanger changer) { _changer = changer; }

        /// <summary>当前临时地址所在网卡；无则 null。</summary>
        public string Alias { get; private set; }

        /// <summary>当前临时地址；无则 null。</summary>
        public string TempIp { get; private set; }

        /// <summary>
        /// 解析“本次发往控制端应使用的本机源地址”。
        /// 同网段 → 直接用现地址；不同网段 → 临时加一个控制端网段地址，并**用 canReach 做真实 TCP 验证**后返回它。
        /// preferIp 传“预览里的目标 IP”（优先用它当临时地址）；seed 传本机 MAC（目标 IP 不可用时按它错开取址）。
        /// 返回 null 表示无法互通（note 说明原因，且不会留下网络改动）。
        /// </summary>
        public string Resolve(string ifAlias, string seed, string masterIp, string mask, string preferIp,
            Func<string, bool> canReach, out string note)
        {
            note = null;
            lock (_lock)
            {
                string current = NetworkHelper.GetIpv4ByAdapter(ifAlias);
                if (!string.IsNullOrEmpty(current) && NetworkHelper.IsSameSubnet(current, masterIp, mask))
                {
                    DropTemp(); // 已经在同网段，之前的临时地址不再需要
                    return current;
                }

                // 之前加的临时地址还在（没被执行脚本清掉）→ 先验证仍能连通再复用
                if (!string.IsNullOrEmpty(TempIp) && string.Equals(Alias, ifAlias, StringComparison.OrdinalIgnoreCase))
                {
                    if (NetworkHelper.HasIpv4(ifAlias, TempIp) && NetworkHelper.IsSameSubnet(TempIp, masterIp, mask) &&
                        (canReach == null || canReach(TempIp)))
                    {
                        note = $"沿用临时同网段地址 {TempIp}（网卡 {ifAlias}）";
                        return TempIp;
                    }
                    DropTemp(); // 已被清除或已不通（多半是刚执行过配置），重新判断
                }

                TempIp = TryAdd(ifAlias, seed, masterIp, mask, preferIp, current, canReach, out note);
                Alias = TempIp != null ? ifAlias : null;
                if (TempIp != null && string.IsNullOrEmpty(note))
                    note = $"控制端 {masterIp} 与本机不同网段，已临时为网卡 [{ifAlias}] 添加同网段地址 {TempIp} 用于互连（执行配置时自动清除）";
                return TempIp;
            }
        }

        /// <summary>删除临时地址（执行完成 / 会话重置 / 退出时调用）。</summary>
        public void Cleanup()
        {
            lock (_lock) { DropTemp(); }
        }

        private string TryAdd(string ifAlias, string seed, string masterIp, string mask, string preferIp, string currentIp,
            Func<string, bool> canReach, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(ifAlias)) { error = "未选择网卡"; return null; }

            int prefix = Validators.MaskToPrefixLength(mask);

            // 候选顺序：优先用“预览里的目标 IP”（它本来就要分配给本机、且在控制端网段内），
            // 目标 IP 不可用时再按 MAC 派生序号错开取其他地址，避免多台终端抢同一个。
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(preferIp) && Validators.IsValidIp(preferIp) &&
                NetworkHelper.IsSameSubnet(preferIp.Trim(), masterIp, mask))
                candidates.Add(preferIp.Trim());

            // 网卡上已有的同网段地址（上次没清掉的）也优先复用
            foreach (string exist in NetworkHelper.GetIpv4List(ifAlias))
            {
                if (string.Equals(exist, currentIp, StringComparison.OrdinalIgnoreCase)) continue;
                if (!NetworkHelper.IsSameSubnet(exist, masterIp, mask)) continue;
                if (!candidates.Exists(x => string.Equals(x, exist, StringComparison.OrdinalIgnoreCase))) candidates.Add(exist);
            }

            int start = SeedIndex(seed, MaxCandidates);
            for (int k = 0; k < MaxCandidates && candidates.Count < 6; k++)
            {
                string cand = CandidateInSubnet(masterIp, mask, ((start - 1 + k) % MaxCandidates) + 1);
                if (cand == null) continue;
                if (string.Equals(cand, masterIp, StringComparison.OrdinalIgnoreCase)) continue;
                if (!candidates.Exists(x => string.Equals(x, cand, StringComparison.OrdinalIgnoreCase))) candidates.Add(cand);
            }

            foreach (string cand in candidates)
            {
                // 已经挂在网卡上就不重复添加，直接验证连通性
                if (!NetworkHelper.HasIpv4(ifAlias, cand))
                {
                    var r = _changer.AddTempIp(ifAlias, cand, prefix);
                    if (!r.Success)
                    {
                        // 地址被别的终端占了（DAD 检测到冲突）或权限不足 → 换下一个候选
                        error = "添加临时同网段地址失败：" + (string.IsNullOrEmpty(r.Error) ? r.Output : r.Error);
                        continue;
                    }
                }

                // 唯一可信的判据：真实 TCP 握手。地址可能还在 DAD 收敛，失败就稍等重试一次
                bool reachable = canReach == null || canReach(cand);
                if (!reachable)
                {
                    Thread.Sleep(1200);
                    reachable = canReach == null || canReach(cand);
                }
                if (reachable) return cand;

                _changer.RemoveTempIp(ifAlias, cand);
                error = $"临时地址 {cand} 已加上，但 TCP {masterIp}:{AppConfig.TcpPort} 仍然连不上（临时地址已回滚）。" +
                        "说明不是地址问题：请检查控制端是否已点【开始发布模板】、防火墙是否放行 TCP 48889、" +
                        "以及两台机器之间是否有路由（跨网段要路由器放通，虚拟机请用桥接模式）。";
                return null;
            }

            error = $"控制端所在网段（掩码 {mask}）内没能加成功临时地址，请手动给被控端配置一个该网段地址后再试。";
            return null;
        }

        private void DropTemp()
        {
            if (string.IsNullOrEmpty(TempIp) || string.IsNullOrEmpty(Alias)) { TempIp = null; Alias = null; return; }
            try
            {
                if (NetworkHelper.HasIpv4(Alias, TempIp)) _changer.RemoveTempIp(Alias, TempIp);
            }
            catch { }
            TempIp = null; Alias = null;
        }

        /// <summary>在控制端所在子网内，从主机位最大的地址往下取第 index 个（跳过网络地址/广播地址）。</summary>
        public static string CandidateInSubnet(string ip, string mask, int index)
        {
            if (!Validators.TryParseIpv4(ip, out byte[] a)) return null;
            if (!Validators.TryParseIpv4(mask, out byte[] m)) return null;

            uint ipBits = ToUInt(a), maskBits = ToUInt(m);
            uint hostMask = ~maskBits;
            if (hostMask < (uint)index) return null;          // 借位越界（例如 /31、/32）
            uint hostPart = hostMask - (uint)index;
            if (hostPart == 0 || hostPart == hostMask) return null; // 网络地址 / 广播地址

            uint value = (ipBits & maskBits) | hostPart;
            return $"{(value >> 24) & 0xFF}.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}";
        }

        private static uint ToUInt(byte[] b)
            => ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];

        /// <summary>用种子（本机 MAC）派生 1..max 的起始序号：不同机器错开取址，减少并发上报时的地址冲突。</summary>
        private static int SeedIndex(string seed, int max)
        {
            string key = string.IsNullOrEmpty(seed) ? Environment.MachineName : seed;
            uint h = 2166136261u;                       // FNV-1a 32
            foreach (char c in key) { h ^= c; h *= 16777619u; }
            return (int)(h % (uint)max) + 1;
        }
    }
}
