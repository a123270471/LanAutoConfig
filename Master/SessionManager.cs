using LanAutoConfig.Common;

namespace LanAutoConfig.Master
{
    /// <summary>控制端会话管理器：持有当前 SessionId 与终端列表（内存为主）。</summary>
    public class SessionManager
    {
        private readonly object _lock = new object();

        public string SessionId { get; private set; } = "";
        public List<SlaveSessionItem> Items { get; } = new List<SlaveSessionItem>();

        public event Action Changed;

        /// <summary>新建会话：全新 GUID，清空旧终端列表（任务隔离，防止多批次混淆）。</summary>
        public void NewSession()
        {
            lock (_lock)
            {
                SessionId = Guid.NewGuid().ToString("N");
                Items.Clear();
            }
            Changed?.Invoke();
        }

        /// <summary>从持久化配置恢复会话（重启程序后终端列表还在）。</summary>
        public void Restore(string sessionId, IEnumerable<SlaveSessionItem> items)
        {
            lock (_lock)
            {
                if (!string.IsNullOrEmpty(sessionId)) SessionId = sessionId;
                Items.Clear();
                if (items != null) Items.AddRange(items);
            }
            Changed?.Invoke();
        }

        /// <summary>
        /// 新增或更新一条上报记录（同一 MAC 去重更新）。
        /// 任何一次重新上报都视为“重新进入待审核”，状态复位为 Reported，允许再次下发。
        /// </summary>
        public void AddOrUpdate(SlaveReportMsg m)
        {
            lock (_lock)
            {
                if (m.SessionId != SessionId) return; // 旧批次丢弃
                var it = Items.FirstOrDefault(x => string.Equals(x.Mac, m.LocalMac, StringComparison.OrdinalIgnoreCase));
                if (it == null)
                {
                    it = new SlaveSessionItem { Mac = m.LocalMac };
                    Items.Add(it);
                }
                else
                {
                    it.Status = SlaveStatus.Reported;
                    it.ErrorMsg = "";
                    it.Skip = false;
                }
                it.ApplyReport(m);
                it.DispatchTime = null;
            }
            Changed?.Invoke();
        }

        public void Remove(string mac)
        {
            lock (_lock)
            {
                Items.RemoveAll(x => string.Equals(x.Mac, mac, StringComparison.OrdinalIgnoreCase));
            }
            Changed?.Invoke();
        }

        /// <summary>批量删除（只触发一次刷新）。</summary>
        public void RemoveMany(IEnumerable<string> macs)
        {
            var set = new HashSet<string>(macs ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            if (set.Count == 0) return;
            lock (_lock) Items.RemoveAll(x => set.Contains(x.Mac));
            Changed?.Invoke();
        }

        /// <summary>批量标记跳过 / 取消跳过（只触发一次刷新）。</summary>
        public void SetSkipMany(IEnumerable<string> macs, bool skip)
        {
            var set = new HashSet<string>(macs ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            if (set.Count == 0) return;
            lock (_lock)
            {
                foreach (var it in Items)
                {
                    if (!set.Contains(it.Mac)) continue;
                    it.Skip = skip;
                    it.Status = skip ? SlaveStatus.Skipped : SlaveStatus.Reported;
                    it.DispatchTime = null;
                    if (!skip) it.ErrorMsg = "";
                }
            }
            Changed?.Invoke();
        }

        /// <summary>
        /// IP 池分配：取一个没被别的终端占用的后缀（1..254）。
        /// forMac 对应的终端自身的后缀不算“占用”，这样重复上报不会一直换号。
        /// </summary>
        public int NextFreeSuffix(string forMac)
        {
            lock (_lock)
            {
                var used = new HashSet<int>();
                foreach (var it in Items)
                {
                    if (!string.IsNullOrEmpty(forMac) &&
                        string.Equals(it.Mac, forMac, StringComparison.OrdinalIgnoreCase)) continue;
                    if (int.TryParse(it.Suffix, out int n)) used.Add(n);
                }
                for (int n = 1; n < 255; n++) if (!used.Contains(n)) return n;
                return -1;
            }
        }

        public SlaveSessionItem GetByMac(string mac)
        {
            lock (_lock)
                return Items.FirstOrDefault(x => string.Equals(x.Mac, mac, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 收到心跳：刷新在线时间；名单里没有的 MAC 就以“在线未上报”建一条（只在第一次建行时返回 true）。
        /// 心跳很频繁，调用方只在返回 true（列表结构变了）时才整表刷新，其余情况只刷在线列。
        /// </summary>
        public bool TouchHeartbeat(SlaveHeartbeatMsg hb)
        {
            bool added = false;
            lock (_lock)
            {
                if (hb.SessionId != SessionId) return false;
                var it = Items.FirstOrDefault(x => string.Equals(x.Mac, hb.LocalMac, StringComparison.OrdinalIgnoreCase));
                if (it == null)
                {
                    it = new SlaveSessionItem { Mac = hb.LocalMac, Status = SlaveStatus.Online, Skip = true };
                    Items.Add(it);
                    added = true;
                }
                it.LastSeen = DateTime.Now;
                // 心跳同样带令牌：老配置恢复出来的记录（没有令牌）靠这一条补上
                if (!string.IsNullOrEmpty(hb.Token)) it.AuthToken = hb.Token;
                // 心跳包里的 LocalIp 是发送方在“收模板那一刻”算的，加临时地址/改 IP 后不会更新，
                // 用它覆盖 SourceIp 会让控制端把指令发到一个已失效的地址（实测：上报来自 .9，下发却打 .5）。
                // 因此优先用 TCP 连接的实测来源地址；没有实测值时，仅在本机还没记录过地址时兜底采用。
                if (!string.IsNullOrEmpty(hb.RemoteIp)) it.SourceIp = hb.RemoteIp;
                else if (string.IsNullOrEmpty(it.SourceIp) && !string.IsNullOrEmpty(hb.LocalIp)) it.SourceIp = hb.LocalIp;
                if (string.IsNullOrEmpty(it.TargetHostName) && !string.IsNullOrEmpty(hb.HostName))
                    it.TargetHostName = hb.HostName;
            }
            return added;
        }

        /// <summary>在线台数（心跳/上报在 30 秒内）。</summary>
        public int OnlineCount
        {
            get { lock (_lock) return Items.Count(x => x.Online); }
        }

        public void MarkSkip(string mac)
        {
            lock (_lock)
            {
                var it = GetByMac(mac);
                if (it != null) { it.Skip = true; it.Status = SlaveStatus.Skipped; it.DispatchTime = null; }
            }
            Changed?.Invoke();
        }

        /// <summary>取消跳过，恢复为待执行。</summary>
        public void UnmarkSkip(string mac)
        {
            lock (_lock)
            {
                var it = GetByMac(mac);
                if (it != null) { it.Skip = false; it.Status = SlaveStatus.Reported; it.ErrorMsg = ""; }
            }
            Changed?.Invoke();
        }

        public void MarkExecuting(string mac)
        {
            lock (_lock)
            {
                var it = GetByMac(mac);
                if (it != null)
                {
                    it.Status = SlaveStatus.Executing;
                    it.ErrorMsg = "";
                    it.DispatchTime = DateTime.Now;
                }
            }
            Changed?.Invoke();
        }

        /// <summary>下发失败（连不上被控端）：退回待执行，允许重试，并把原因显示在列表里。</summary>
        public void MarkDispatchFailed(string mac, string error)
        {
            lock (_lock)
            {
                var it = GetByMac(mac);
                if (it != null)
                {
                    it.Status = SlaveStatus.Reported;
                    it.ErrorMsg = error ?? "";
                    it.DispatchTime = null;
                }
            }
            Changed?.Invoke();
        }

        /// <summary>根据被控端结果回传更新状态，并生成一条入库日志记录。</summary>
        public OpLogRecord MarkResult(SlaveResultReportMsg m)
        {
            OpLogRecord rec = null;
            bool changed = false;
            lock (_lock)
            {
                if (m.SessionId != SessionId) return null;
                var it = GetByMac(m.LocalMac);
                if (it != null)
                {
                    it.Status = m.Success ? SlaveStatus.Success : SlaveStatus.Failed;
                    it.ErrorMsg = m.Success ? "" : (m.ErrorMsg ?? "");
                    it.DispatchTime = null;
                    // 改完 IP 后旧地址已失效，用回传里的实际源地址刷新，后续再下发才不会打到死地址。
                    // 同样优先用实测来源地址（改 IP 后这条连接就是新地址发起的）。
                    string observed = !string.IsNullOrEmpty(m.RemoteIp) ? m.RemoteIp : m.LocalSourceIp;
                    if (!string.IsNullOrEmpty(observed)) it.SourceIp = observed;
                    changed = true;
                    rec = new OpLogRecord
                    {
                        SessionId = SessionId,
                        OpTime = DateTime.Now,
                        Mac = m.LocalMac,
                        SourceIp = it.SourceIp,
                        TargetHostName = it.TargetHostName,
                        TargetIp = it.TargetIp,
                        Result = m.Success ? "成功" : "失败",
                        ErrorMsg = m.Success ? "" : m.ErrorMsg
                    };
                }
            }
            if (changed) Changed?.Invoke();
            return rec;
        }

        /// <summary>
        /// 回收长时间没有结果的「执行中」记录（被控端改 IP/重启后可能回传不上来）。
        /// 返回被回收的条目，供调用方写日志。
        /// </summary>
        public List<SlaveSessionItem> RecoverStaleExecuting(TimeSpan timeout)
        {
            var recovered = new List<SlaveSessionItem>();
            lock (_lock)
            {
                DateTime now = DateTime.Now;
                foreach (var it in Items)
                {
                    if (it.Status != SlaveStatus.Executing) continue;
                    if (!it.DispatchTime.HasValue) { it.DispatchTime = now; continue; }
                    if (now - it.DispatchTime.Value < timeout) continue;

                    it.Status = SlaveStatus.Failed;
                    it.ErrorMsg = "超时未收到执行结果（可能已断网/重启，或被控端未能回传）";
                    it.DispatchTime = null;
                    recovered.Add(it);
                }
            }
            if (recovered.Count > 0) Changed?.Invoke();
            return recovered;
        }

        /// <summary>获取待下发执行的终端（已上报、非跳过）。</summary>
        public List<SlaveSessionItem> GetPendingExecute()
        {
            lock (_lock)
                return Items.Where(x => x.Status == SlaveStatus.Reported && !x.Skip).ToList();
        }
    }
}
