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

        public SlaveSessionItem GetByMac(string mac)
        {
            lock (_lock)
                return Items.FirstOrDefault(x => string.Equals(x.Mac, mac, StringComparison.OrdinalIgnoreCase));
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
                    // 改完 IP 后旧地址已失效，用回传里的实际源地址刷新，后续再下发才不会打到死地址
                    if (!string.IsNullOrEmpty(m.LocalSourceIp)) it.SourceIp = m.LocalSourceIp;
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
