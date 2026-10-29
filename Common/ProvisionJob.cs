namespace LanAutoConfig.Common
{
    /// <summary>一键装机的阶段。</summary>
    public enum ProvisionStage
    {
        Idle = 0,
        /// <summary>等被控端陆续上报（新机开机 → 心跳 → 上报）。</summary>
        WaitingReport = 1,
        /// <summary>下发执行配置（瞬时阶段，下发动作由控制端完成）。</summary>
        Dispatching = 2,
        /// <summary>等执行结果回传。</summary>
        WaitingExecute = 3,
        /// <summary>下发重启（瞬时阶段）。</summary>
        Rebooting = 4,
        /// <summary>等机器重启后重新上线。</summary>
        WaitingOnline = 5,
        /// <summary>下发验收（瞬时阶段）。</summary>
        Verifying = 6,
        /// <summary>等验收结果回传。</summary>
        WaitingVerify = 7,
        Done = 8,
        Aborted = 9
    }

    /// <summary>流水线可调参数（现场按网络规模改）。</summary>
    public class ProvisionOptions
    {
        /// <summary>等上报的最长时长：到点就用手上已经就绪的机器继续跑。</summary>
        public int ReportTimeoutSec { get; set; } = 180;
        /// <summary>等执行结果的最长时长。</summary>
        public int ExecuteTimeoutSec { get; set; } = 300;
        /// <summary>失败自动重试次数（只重发失败的，不整车重来）。</summary>
        public int MaxExecuteRetry { get; set; } = 2;
        /// <summary>是否自动重启（真正让新主机名/IP 生效）。</summary>
        public bool AutoReboot { get; set; } = true;
        public int RebootDelaySec { get; set; } = 10;
        /// <summary>等重启上线的最长时长。</summary>
        public int OnlineTimeoutSec { get; set; } = 420;
        /// <summary>等验收结果的最长时长。</summary>
        public int VerifyTimeoutSec { get; set; } = 120;
        /// <summary>最后是否做验收（比对实际主机名/IP）。</summary>
        public bool VerifyAtEnd { get; set; } = true;
        /// <summary>下发后多久还没有任何结果，就把这台当“没送达”重发。</summary>
        public int DispatchSettleSec { get; set; } = 20;
    }

    /// <summary>流水线要控制端做的事（抽成接口，便于离线单测）。</summary>
    public interface IProvisionOps
    {
        void Dispatch(IReadOnlyList<SlaveSessionItem> items);
        void Reboot(IReadOnlyList<SlaveSessionItem> items);
        void Verify(IReadOnlyList<SlaveSessionItem> items);
        void Log(string msg);
        void Finished(ProvisionSummary summary);

        /// <summary>
        /// 控制端是否还在“发”的过程中。
        ///
        /// 下发是异步的（几十台逐台 TCP，可能要跑几十秒）。这期间列表里的状态还没更新，
        /// 如果状态机照常按“还没进入执行中”判定，就会在发到一半时误判“没送达”并重发 ——
        /// 这是上一版最难受的 bug：日志里一边在发、一边在重发，现场机器被连发两遍。
        /// 为 true 时状态机只等不做判定。
        /// </summary>
        bool Busy { get; }
    }

    /// <summary>一轮装机的最终汇总。</summary>
    public class ProvisionSummary
    {
        public int Total { get; set; }
        public int Success { get; set; }
        public int Failed { get; set; }
        public int Skipped { get; set; }
        public int VerifyPass { get; set; }
        public int VerifyFail { get; set; }
        public int VerifyMissing { get; set; }
        public int Retries { get; set; }
        public TimeSpan Elapsed { get; set; }

        public string Describe()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"共 {Total} 台：成功 {Success}，失败 {Failed}");
            if (Skipped > 0) sb.Append($"，跳过 {Skipped}");
            if (VerifyPass + VerifyFail + VerifyMissing > 0)
                sb.Append($"；验收 通过 {VerifyPass} / 不符 {VerifyFail} / 未回传 {VerifyMissing}");
            if (Retries > 0) sb.Append($"；重试 {Retries} 次");
            sb.Append($"；耗时 {(int)Elapsed.TotalMinutes} 分 {Elapsed.Seconds} 秒");
            return sb.ToString();
        }
    }

    /// <summary>
    /// 一键装机流水线的状态机：等上报 → 下发 → 等结果（失败自动重试）→ 重启 → 等上线 → 验收 → 汇总。
    ///
    /// 设计要点（上一版踩过的坑都在这里）：
    /// 1. **本轮状态自己记，不读列表状态**。列表里的 <see cref="SlaveSessionItem.Status"/> 会被心跳/重新上报改写
    ///    （重报一次就复位成 Reported）：流水线如果拿它判断“谁成功了”，机器一重启重报就会看到“0 台成功”，
    ///    直接把整轮判失败/跳过验收。这里改成“观测 + 粘住”：见过一次成功就记成功，之后列表怎么变都不影响。
    /// 2. **下发是异步的**，靠 <see cref="IProvisionOps.Busy"/> 保证这期间不做“没送达”的判定。
    /// 3. 时间由调用方传入（<c>Tick(now)</c>），整套流转可以离线单测，不用真的等 5 分钟。
    /// </summary>
    public class ProvisionJob
    {
        private sealed class TargetState
        {
            public string Mac;
            /// <summary>本轮是否已上报（拿到目标值）。心跳建出来的“在线未上报”行不算。</summary>
            public bool Reported;
            /// <summary>本轮下发过几次。</summary>
            public int Attempts;
            /// <summary>本轮曾经成功（粘性，不受重报复位影响）。</summary>
            public bool Success;
            /// <summary>最近一次明确失败（成功会清掉）。</summary>
            public bool Failed;
            public bool Skipped;
            /// <summary>是否已经给它下发过重启。</summary>
            public bool RebootIssued;
            /// <summary>重启阶段是否观测到它掉线。</summary>
            public bool SawOffline;
            /// <summary>重启后又观测到它上线。</summary>
            public bool BackOnline;
            /// <summary>最近一次下发的时间（判定“没送达”用）。</summary>
            public DateTime? DispatchedAt;
            /// <summary>本轮验收结论；None 表示还没回传。</summary>
            public VerifyStatus Verify = VerifyStatus.None;
        }

        private readonly Func<IReadOnlyList<SlaveSessionItem>> _allItems;
        private readonly IProvisionOps _ops;
        private readonly List<TargetState> _targets = new List<TargetState>();

        private DateTime _startedAt;
        private DateTime _stageSince;
        /// <summary>本轮“还在下发”的起始时间，用来提示下发耗时长（不是卡住）。</summary>
        private DateTime? _busySince;
        private int _busyLoggedAt;

        public ProvisionOptions Options { get; } = new ProvisionOptions();
        public ProvisionStage Stage { get; private set; } = ProvisionStage.Idle;
        public int RetryCount { get; private set; }
        public int Rounds { get; private set; }
        public string LastMessage { get; private set; }

        public ProvisionJob(Func<IReadOnlyList<SlaveSessionItem>> allItems, IProvisionOps ops)
        {
            _allItems = allItems ?? throw new ArgumentNullException(nameof(allItems));
            _ops = ops ?? throw new ArgumentNullException(nameof(ops));
        }

        public bool Running => Stage != ProvisionStage.Idle
                               && Stage != ProvisionStage.Done
                               && Stage != ProvisionStage.Aborted;

        /// <summary>本轮的终端 MAC 快照（开始后新上线的机器不纳入本轮）。</summary>
        public IReadOnlyList<string> TargetMacs => _targets.Select(x => x.Mac).ToList();

        public TimeSpan StageElapsed(DateTime now) => now - _stageSince;
        public TimeSpan TotalElapsed(DateTime now) => now - _startedAt;

        /// <summary>阶段的中文说明 + 当前进度，直接显示在界面上。</summary>
        public string StageText(DateTime now)
        {
            string left = Stage switch
            {
                ProvisionStage.WaitingReport => $"已就绪 {Count(x => x.Reported)}/{_targets.Count} 台",
                ProvisionStage.WaitingExecute => $"已完成 {Count(x => x.Success || x.Failed)}/{Count(x => !x.Skipped)} 台",
                ProvisionStage.WaitingOnline => $"已上线 {Count(x => x.RebootIssued && x.BackOnline)}/{Count(x => x.RebootIssued)} 台",
                ProvisionStage.WaitingVerify => $"已回传 {Count(x => x.Verify != VerifyStatus.None)}/{Count(x => x.Success)} 台",
                _ => ""
            };
            string t = Stage switch
            {
                ProvisionStage.Idle => "未开始",
                ProvisionStage.WaitingReport => "① 等待被控端上报",
                ProvisionStage.Dispatching => "② 正在下发配置",
                ProvisionStage.WaitingExecute => "③ 等待执行结果",
                ProvisionStage.Rebooting => "④ 下发重启",
                ProvisionStage.WaitingOnline => "⑤ 等待重启上线",
                ProvisionStage.Verifying => "⑥ 下发验收",
                ProvisionStage.WaitingVerify => "⑦ 等待验收结果",
                ProvisionStage.Done => "✔ 已完成",
                ProvisionStage.Aborted => "✖ 已中止",
                _ => Stage.ToString()
            };
            if (left.Length > 0) t += "（" + left + "，" + (int)StageElapsed(now).TotalSeconds + "s）";
            return t;
        }

        /// <summary>开始一轮：macs 为要处理的终端（通常是“全部”或“选中的”）。</summary>
        public void Start(IEnumerable<string> macs, DateTime now)
        {
            _targets.Clear();
            if (macs != null)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string m in macs)
                    if (!string.IsNullOrWhiteSpace(m) && seen.Add(m))
                        _targets.Add(new TargetState { Mac = m });
            }

            _startedAt = now;
            _stageSince = now;
            RetryCount = 0;
            Rounds = 1;
            _busySince = null;
            _busyLoggedAt = 0;

            if (_targets.Count == 0) { Stage = ProvisionStage.Aborted; LastMessage = "没有可处理的终端"; return; }

            Stage = ProvisionStage.WaitingReport;
            Sync();   // 先把“现在就已经上报/已经在线”的认下来，避免第一秒白等
            _ops.Log($"流水线开始：目标 {_targets.Count} 台，等上报最长 {Options.ReportTimeoutSec} 秒");
        }

        public void Abort(string why = "操作员中止")
        {
            if (!Running) return;
            Stage = ProvisionStage.Aborted;
            LastMessage = why;
            _ops.Log("流水线已中止：" + why);
            _ops.Finished(BuildSummary());
        }

        /// <summary>
        /// 被控端回传执行结果时调用。
        ///
        /// 比“每秒去列表里看状态”可靠：结果刚回传时状态是 Success，但机器重启后重新上报会把它复位成 Reported，
        /// 中间只要错过一次轮询就再也读不到成功。这里由控制端在收到结果的那一刻直接喂进来，一步到位。
        /// </summary>
        public void NotifyExecResult(string mac, bool success, string error = null)
        {
            var t = Find(mac);
            if (t == null || !Running) return;
            if (success) { t.Success = true; t.Failed = false; }
            else
            {
                // 已经成功过的就不再翻回失败（重试成功后又被旧结果覆盖的情况）
                if (!t.Success) t.Failed = true;
                if (!string.IsNullOrEmpty(error)) _ops.Log($"{MacText(mac)} 执行失败：{FirstLine(error)}");
            }
        }

        /// <summary>由控制端的 1 秒定时器驱动。</summary>
        public void Tick(DateTime now)
        {
            if (!Running) return;

            Sync();

            if (_targets.Count == 0 || _targets.All(x => FindItem(x.Mac) == null))
            {
                Stage = ProvisionStage.Aborted;
                LastMessage = "目标终端已全部从列表删除";
                _ops.Log("流水线中止：目标终端已全部从列表删除");
                _ops.Finished(BuildSummary());
                return;
            }

            double elapsed = (now - _stageSince).TotalSeconds;

            switch (Stage)
            {
                case ProvisionStage.WaitingReport:
                {
                    // “就绪”= 已经上报过（有目标值）且此刻在线。只上报过但已离线的机器要等它上线，
                    // 否则会对着一个旧地址下发，然后连不上、重试、再失败。
                    int notReady = Count(x => !x.Skipped && (!x.Reported || !IsOnline(x.Mac)));
                    if (notReady == 0) { EnterStage(ProvisionStage.Dispatching, now); break; }
                    if (elapsed >= Options.ReportTimeoutSec)
                    {
                        var ready = Count(x => !x.Skipped && x.Reported && IsOnline(x.Mac));
                        _ops.Log($"等上报超时（{Options.ReportTimeoutSec} 秒）：{notReady} 台还没就绪，先用已就绪的 {ready} 台继续");
                        EnterStage(ProvisionStage.Dispatching, now);
                    }
                    break;
                }

                case ProvisionStage.Dispatching:
                {
                    var pending = _targets
                        .Where(x => !x.Skipped && !x.Success && x.Reported && IsOnline(x.Mac))
                        .ToList();
                    if (pending.Count == 0)
                    {
                        _ops.Log("没有“已上报且在线”的终端可下发，跳过下发阶段");
                        EnterStage(ProvisionStage.WaitingExecute, now);
                        break;
                    }
                    _ops.Log($"下发执行：{pending.Count} 台");
                    DispatchTo(pending, now);
                    EnterStage(ProvisionStage.WaitingExecute, now);
                    break;
                }

                case ProvisionStage.WaitingExecute:
                {
                    // 还在发（几十台逐台发可能要几十秒）：这时列表状态还没更新，判定“没送达”一定是误判
                    if (_ops.Busy)
                    {
                        if (!_busySince.HasValue)
                        {
                            _busySince = now;
                        }
                        else
                        {
                            var spent = (int)(now - _busySince.Value).TotalSeconds;
                            if (spent >= 60 && spent / 30 != _busyLoggedAt / 30)
                            {
                                _busyLoggedAt = spent;
                                _ops.Log($"还在下发中（已 {spent} 秒）：目标多或有机器的 TCP 连接在超时，发完才开始计时，别急");
                            }
                        }
                        _stageSince = now;
                        break;
                    }
                    _busySince = null;
                    _busyLoggedAt = 0;

                    // 明确失败的先处理（被控端回传失败，或控制端直接发不出去）
                    var failed = _targets.Where(x => !x.Skipped && !x.Success && x.Failed).ToList();
                    if (failed.Count > 0)
                    {
                        if (RetryCount < Options.MaxExecuteRetry)
                        {
                            RetryCount++;
                            Rounds++;
                            _ops.Log($"第 {RetryCount} 次重试：{failed.Count} 台执行失败，重新下发");
                            DispatchTo(failed, now);
                            EnterStage(ProvisionStage.WaitingExecute, now);   // 新一轮，重新计时
                            break;
                        }
                        bool stillWaiting = _targets.Any(x => !x.Skipped && !x.Success && !x.Failed);
                        if (!stillWaiting) { AdvanceAfterExecute(now); break; }
                        break;   // 还有别的机器没出结果，继续等它们
                    }

                    var waiting = _targets.Where(x => !x.Skipped && !x.Success && !x.Failed).ToList();
                    if (waiting.Count == 0) { AdvanceAfterExecute(now); break; }

                    var stalled = waiting.Where(x => x.Attempts > 0 && Stalled(x, now)).ToList();
                    if (stalled.Count > 0)
                    {
                        if (RetryCount < Options.MaxExecuteRetry)
                        {
                            RetryCount++;
                            Rounds++;
                            _ops.Log($"第 {RetryCount} 次重试：{stalled.Count} 台在 {Options.DispatchSettleSec} 秒内没有结果，重新下发");
                            DispatchTo(stalled, now);
                            EnterStage(ProvisionStage.WaitingExecute, now);   // 新一轮，重新计时
                            break;
                        }

                        foreach (var x in stalled) x.Failed = true;
                        _ops.Log($"重试次数已用完（{Options.MaxExecuteRetry} 次）：{stalled.Count} 台按失败处理");
                        if (waiting.Count == stalled.Count) { AdvanceAfterExecute(now); break; }
                        break;   // 还有别的机器在排/在跑，不重置计时，继续等
                    }

                    if (elapsed < Options.ExecuteTimeoutSec) break;

                    // 超时：还没结果的按失败算
                    foreach (var x in waiting)
                    {
                        if (x.Attempts == 0)
                        {
                            _ops.Log($"{MacText(x.Mac)} 一直没就绪（没上报或不在线），本轮跳过");
                            x.Skipped = true;
                        }
                        else if (!x.Success) x.Failed = true;
                    }
                    _ops.Log($"等执行结果超时（{Options.ExecuteTimeoutSec} 秒）：{Count(x => !x.Success && !x.Skipped)} 台未成功");
                    AdvanceAfterExecute(now);
                    break;
                }

                case ProvisionStage.Rebooting:
                {
                    var ok = _targets.Where(x => x.Success && !x.Skipped).ToList();
                    if (ok.Count == 0)
                    {
                        _ops.Log("没有执行成功的终端，跳过重启");
                        AfterSuccess(now);
                        break;
                    }
                    foreach (var x in ok) { x.RebootIssued = true; x.SawOffline = false; x.BackOnline = false; }
                    _ops.Log($"下发重启：{ok.Count} 台（{Options.RebootDelaySec} 秒后重启）");
                    _ops.Reboot(ItemsOf(ok));
                    EnterStage(ProvisionStage.WaitingOnline, now);
                    break;
                }

                case ProvisionStage.WaitingOnline:
                {
                    var rb = _targets.Where(x => x.RebootIssued).ToList();
                    if (rb.Count == 0) { AfterSuccess(now); break; }

                    int online = rb.Count(x => IsOnline(x.Mac));
                    bool allOnline = online == rb.Count;
                    bool allSawOffline = rb.All(x => x.SawOffline);

                    // 全部在线 且（每台都见过掉线 或者 已经过了 90 秒缓冲）才算重启完成。
                    // 缓冲是为了避免“10 秒重启延迟还没执行，机器还连着”就被判成“已全部上线”。
                    if (allOnline && (allSawOffline || elapsed >= 90))
                    {
                        _ops.Log($"重启后已全部上线（{online}/{rb.Count}）");
                        AfterSuccess(now);
                        break;
                    }
                    if (elapsed >= Options.OnlineTimeoutSec)
                    {
                        _ops.Log($"等待上线超时（{Options.OnlineTimeoutSec} 秒）：{online}/{rb.Count} 在线，继续走验收");
                        AfterSuccess(now);
                    }
                    break;
                }

                case ProvisionStage.Verifying:
                {
                    var targets = _targets.Where(x => x.Success && !x.Skipped).ToList();
                    if (targets.Count == 0 || !Options.VerifyAtEnd) { Finish(now); break; }

                    foreach (var x in targets)
                    {
                        x.Verify = VerifyStatus.None;      // 清掉上一轮的结论，否则会立刻判定“已回传”
                        var it = FindItem(x.Mac);
                        if (it != null) { it.VerifyStatus = VerifyStatus.None; it.VerifyNote = ""; }
                    }
                    _ops.Log($"下发验收：{targets.Count} 台");
                    _ops.Verify(ItemsOf(targets));
                    EnterStage(ProvisionStage.WaitingVerify, now);
                    break;
                }

                case ProvisionStage.WaitingVerify:
                {
                    var targets = _targets.Where(x => x.Success && !x.Skipped).ToList();
                    if (targets.Count == 0) { Finish(now); break; }

                    int got = targets.Count(x => x.Verify != VerifyStatus.None);
                    if (got >= targets.Count) { Finish(now); break; }
                    if (elapsed >= Options.VerifyTimeoutSec)
                    {
                        _ops.Log($"等验收结果超时：{got}/{targets.Count} 台已回传");
                        Finish(now);
                    }
                    break;
                }
            }
        }

        // ---------- 内部 ----------

        /// <summary>把列表里的最新观测吸进来（成功/失败/跳过/在线/验收）。</summary>
        private void Sync()
        {
            foreach (var t in _targets)
            {
                var it = FindItem(t.Mac);
                if (it == null) continue;

                if (it.Status != SlaveStatus.Online) t.Reported = true;   // 有心跳但没上报 = 在线未上报
                t.Skipped = it.Skip;

                if (t.Attempts > 0)
                {
                    if (it.Status == SlaveStatus.Success) { t.Success = true; t.Failed = false; }
                    else if (it.Status == SlaveStatus.Failed && !t.Success) t.Failed = true;
                }

                if (it.VerifyStatus != VerifyStatus.None) t.Verify = it.VerifyStatus;

                if (t.RebootIssued)
                {
                    if (!it.Online) t.SawOffline = true;
                    else if (t.SawOffline) t.BackOnline = true;
                }
            }
        }

        private bool IsOnline(string mac)
        {
            var it = FindItem(mac);
            return it != null && it.Online;
        }

        /// <summary>下发过、还没有任何结果、且已经过了估算窗口 —— 大概率没送达。</summary>
        private bool Stalled(TargetState t, DateTime now)
            => t.Attempts > 0 && t.DispatchedAt.HasValue
               && (now - t.DispatchedAt.Value).TotalSeconds >= Options.DispatchSettleSec;

        private void DispatchTo(List<TargetState> list, DateTime now)
        {
            foreach (var x in list)
            {
                x.Attempts++;
                x.DispatchedAt = now;
                x.Failed = false;      // 本轮重新下发，清掉上一次的失败标记
            }
            _ops.Dispatch(ItemsOf(list));
        }

        private void AdvanceAfterExecute(DateTime now)
        {
            int success = Count(x => x.Success && !x.Skipped);
            if (success == 0)
            {
                Abort("没有一台终端执行成功（看日志里的失败原因，常见的是连不上或被防火墙拦）");
                return;
            }
            if (Options.AutoReboot) EnterStage(ProvisionStage.Rebooting, now);
            else AfterSuccess(now);
        }

        /// <summary>执行阶段之后的分支：要验收就进验收，否则直接收尾。</summary>
        private void AfterSuccess(DateTime now)
        {
            if (Options.VerifyAtEnd) EnterStage(ProvisionStage.Verifying, now);
            else Finish(now);
        }

        private void EnterStage(ProvisionStage s, DateTime now)
        {
            Stage = s;
            _stageSince = now;
        }

        private void Finish(DateTime now)
        {
            Stage = ProvisionStage.Done;
            var s = BuildSummary();
            s.Elapsed = now - _startedAt;
            LastMessage = s.Describe();
            _ops.Log("流水线完成：" + LastMessage);
            _ops.Finished(s);
        }

        public ProvisionSummary BuildSummary()
        {
            var s = new ProvisionSummary
            {
                Total = _targets.Count,
                Success = Count(x => x.Success && !x.Skipped),
                Skipped = Count(x => x.Skipped),
                VerifyPass = Count(x => x.Success && !x.Skipped && x.Verify == VerifyStatus.Pass),
                VerifyFail = Count(x => x.Success && !x.Skipped && x.Verify == VerifyStatus.Fail),
                Retries = RetryCount,
                Elapsed = DateTime.Now - _startedAt
            };
            s.Failed = Math.Max(0, s.Total - s.Success - s.Skipped);
            s.VerifyMissing = Math.Max(0, s.Success - s.VerifyPass - s.VerifyFail);
            return s;
        }

        private int Count(Func<TargetState, bool> p) => _targets.Count(p);

        private TargetState Find(string mac)
            => mac == null ? null : _targets.FirstOrDefault(x => string.Equals(x.Mac, mac, StringComparison.OrdinalIgnoreCase));

        private SlaveSessionItem FindItem(string mac)
        {
            if (mac == null) return null;
            var all = _allItems();
            if (all == null) return null;
            return all.FirstOrDefault(x => x != null && string.Equals(x.Mac, mac, StringComparison.OrdinalIgnoreCase));
        }

        private List<SlaveSessionItem> ItemsOf(IEnumerable<TargetState> states)
        {
            var list = new List<SlaveSessionItem>();
            foreach (var st in states)
            {
                var it = FindItem(st.Mac);
                if (it != null) list.Add(it);
            }
            return list;
        }

        private static string MacText(string mac) => string.IsNullOrEmpty(mac) ? "(未知)" : mac;

        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = s.IndexOfAny(new[] { '\r', '\n' });
            return i < 0 ? s : s.Substring(0, i);
        }
    }
}
