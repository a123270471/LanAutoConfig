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
        /// <summary>等上报的最长时长：到点就用手上已有的机器继续跑。</summary>
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
        /// <summary>下发后多久还没进入“执行中”，就当作下发失败（连不上/被丢包）。</summary>
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
    /// 这里只做“什么时候该做下一步”的判断，真正的发送动作通过 <see cref="IProvisionOps"/> 交给控制端。
    /// 时间由调用方传入（<c>Tick(now)</c>），因此整套流转可以离线单测，不需要真的等 5 分钟。
    /// </summary>
    public class ProvisionJob
    {
        private readonly Func<IReadOnlyList<SlaveSessionItem>> _allItems;
        private readonly IProvisionOps _ops;
        private readonly List<string> _targetMacs = new List<string>();

        private DateTime _startedAt;
        private DateTime _stageSince;
        /// <summary>重启阶段是否已经观测到“掉线”（用来区分“还没重启”和“已经重启回来”）。</summary>
        private bool _sawOffline;
        /// <summary>本轮已验收的 MAC（重启后自动验收也会改 VerifyStatus，先记下本轮目标）。</summary>
        private readonly List<string> _verifyMacs = new List<string>();

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
        public IReadOnlyList<string> TargetMacs => _targetMacs;

        public TimeSpan StageElapsed(DateTime now) => now - _stageSince;
        public TimeSpan TotalElapsed(DateTime now) => now - _startedAt;

        /// <summary>阶段的中文说明 + 当前进度，直接显示在界面上。</summary>
        public string StageText(DateTime now)
        {
            var items = Targets();
            string left = Stage switch
            {
                ProvisionStage.WaitingReport => $"等上报 {Count(items, x => x.Status == SlaveStatus.Online)}/{items.Count} 台未上报",
                ProvisionStage.WaitingExecute => $"执行中 {Count(items, x => x.Status == SlaveStatus.Executing)} 台，已完成 {Count(items, x => x.Status == SlaveStatus.Success || x.Status == SlaveStatus.Failed)}/{items.Count}",
                ProvisionStage.WaitingOnline => $"已上线 {Count(items, x => x.Online)}/{items.Count}",
                ProvisionStage.WaitingVerify => $"已回传 {Count(items, x => x.VerifyStatus != VerifyStatus.None)}/{_verifyMacs.Count}",
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

        /// <summary>开始一轮：macs 为要处理的终端（通常是“已上报的全部”或“选中的”）。</summary>
        public void Start(IEnumerable<string> macs, DateTime now)
        {
            _targetMacs.Clear();
            if (macs != null)
                foreach (string m in macs)
                    if (!string.IsNullOrWhiteSpace(m) && !_targetMacs.Contains(m)) _targetMacs.Add(m);

            _startedAt = now;
            _stageSince = now;
            RetryCount = 0;
            Rounds = 1;
            _sawOffline = false;
            _verifyMacs.Clear();

            if (_targetMacs.Count == 0) { Stage = ProvisionStage.Aborted; LastMessage = "没有可处理的终端"; return; }

            Stage = ProvisionStage.WaitingReport;
            _ops.Log($"流水线开始：目标 {_targetMacs.Count} 台，等上报最长 {Options.ReportTimeoutSec} 秒");
        }

        public void Abort(string why = "操作员中止")
        {
            if (!Running) return;
            Stage = ProvisionStage.Aborted;
            LastMessage = why;
            _ops.Log("流水线已中止：" + why);
            _ops.Finished(BuildSummary());
        }

        /// <summary>由控制端的 1 秒定时器驱动。</summary>
        public void Tick(DateTime now)
        {
            if (!Running) return;

            var items = Targets();
            if (items.Count == 0)
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
                    int unreported = Count(items, x => x.Status == SlaveStatus.Online);
                    if (unreported == 0) { EnterStage(ProvisionStage.Dispatching, now); break; }
                    if (elapsed >= Options.ReportTimeoutSec)
                    {
                        _ops.Log($"等上报超时（{Options.ReportTimeoutSec} 秒）：还有 {unreported} 台没上报，先用已上报的继续");
                        EnterStage(ProvisionStage.Dispatching, now);
                    }
                    break;
                }

                case ProvisionStage.Dispatching:
                {
                    var pending = items.Where(x => x.Status == SlaveStatus.Reported && !x.Skip).ToList();
                    if (pending.Count == 0)
                    {
                        _ops.Log("没有“已上报待执行”的终端，跳过下发阶段");
                        EnterStage(ProvisionStage.WaitingExecute, now);
                        break;
                    }
                    _ops.Log($"下发执行：{pending.Count} 台");
                    _ops.Dispatch(pending);
                    EnterStage(ProvisionStage.WaitingExecute, now);
                    break;
                }

                case ProvisionStage.WaitingExecute:
                {
                    int running = Count(items, x => x.Status == SlaveStatus.Executing);
                    int pending = Count(items, x => x.Status == SlaveStatus.Reported && !x.Skip);
                    int finished = Count(items, x => x.Status == SlaveStatus.Success || x.Status == SlaveStatus.Failed);

                    // 下发后一段时间还是“待执行”，说明根本没送达（连不上/会话不一致/丢包）
                    bool dispatchStalled = running == 0 && pending > 0 && elapsed >= Options.DispatchSettleSec;
                    bool allSettled = running == 0 && pending == 0;
                    bool timeout = elapsed >= Options.ExecuteTimeoutSec;

                    if (!allSettled && !dispatchStalled && !timeout) break;

                    if (dispatchStalled)
                        _ops.Log($"{pending} 台在 {Options.DispatchSettleSec} 秒内没有进入“执行中”，按下发失败处理");

                    var failed = items.Where(x => (x.Status == SlaveStatus.Failed ||
                                                   (x.Status == SlaveStatus.Reported && !x.Skip)) && !x.Skip).ToList();
                    if (failed.Count > 0 && RetryCount < Options.MaxExecuteRetry)
                    {
                        RetryCount++;
                        Rounds++;
                        _ops.Log($"第 {RetryCount} 次重试：对 {failed.Count} 台失败终端重新下发");
                        _ops.Dispatch(failed);
                        EnterStage(ProvisionStage.WaitingExecute, now);
                        break;
                    }

                    int success = Count(items, x => x.Status == SlaveStatus.Success);
                    if (success == 0 && finished == 0)
                    {
                        Abort("没有任何终端进入执行状态（可能都连不上控制端）");
                        break;
                    }
                    if (Options.AutoReboot && success > 0) EnterStage(ProvisionStage.Rebooting, now);
                    else if (Options.VerifyAtEnd) EnterStage(ProvisionStage.Verifying, now);
                    else Finish(now);
                    break;
                }

                case ProvisionStage.Rebooting:
                {
                    var ok = items.Where(x => x.Status == SlaveStatus.Success).ToList();
                    if (ok.Count == 0)
                    {
                        _ops.Log("没有执行成功的终端，跳过重启");
                        if (Options.VerifyAtEnd) EnterStage(ProvisionStage.Verifying, now);
                        else Finish(now);
                        break;
                    }
                    _ops.Log($"下发重启：{ok.Count} 台（{Options.RebootDelaySec} 秒后重启）");
                    _ops.Reboot(ok);
                    _sawOffline = false;
                    EnterStage(ProvisionStage.WaitingOnline, now);
                    break;
                }

                case ProvisionStage.WaitingOnline:
                {
                    var rebooted = items.Where(x => x.Status == SlaveStatus.Success).ToList();
                    if (rebooted.Count == 0) { Finish(now); break; }

                    int offline = Count(rebooted, x => !x.Online);
                    if (offline > 0) _sawOffline = true;

                    int online = Count(rebooted, x => x.Online);
                    // 起来后要“全部在线”才算这一阶段过去；没见过掉线时给 90 秒缓冲，
                    // 避免刚下发完（10 秒延迟还没关）就误判“已全部上线”。
                    bool allBack = online == rebooted.Count && (_sawOffline || elapsed >= 90);
                    if (allBack)
                    {
                        _ops.Log($"重启后已全部上线（{online}/{rebooted.Count}）");
                        if (Options.VerifyAtEnd) EnterStage(ProvisionStage.Verifying, now);
                        else Finish(now);
                        break;
                    }
                    if (elapsed >= Options.OnlineTimeoutSec)
                    {
                        _ops.Log($"等待上线超时（{Options.OnlineTimeoutSec} 秒）：{online}/{rebooted.Count} 在线，继续走验收");
                        if (Options.VerifyAtEnd) EnterStage(ProvisionStage.Verifying, now);
                        else Finish(now);
                    }
                    break;
                }

                case ProvisionStage.Verifying:
                {
                    _verifyMacs.Clear();
                    var targets = items.Where(x => x.Status == SlaveStatus.Success).ToList();
                    if (targets.Count == 0 || !Options.VerifyAtEnd) { Finish(now); break; }

                    foreach (var it in targets)
                    {
                        it.VerifyStatus = VerifyStatus.None;   // 清掉上一轮的结论，否则会立刻判定“已回传”
                        it.VerifyNote = "";
                        _verifyMacs.Add(it.Mac);
                    }
                    _ops.Log($"下发验收：{targets.Count} 台");
                    _ops.Verify(targets);
                    EnterStage(ProvisionStage.WaitingVerify, now);
                    break;
                }

                case ProvisionStage.WaitingVerify:
                {
                    var targets = _verifyMacs
                        .Select(m => items.FirstOrDefault(x => string.Equals(x.Mac, m, StringComparison.OrdinalIgnoreCase)))
                        .Where(x => x != null).ToList();
                    if (targets.Count == 0) { Finish(now); break; }

                    int got = Count(targets, x => x.VerifyStatus != VerifyStatus.None);
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
            var items = Targets();
            var s = new ProvisionSummary
            {
                Total = _targetMacs.Count,
                Success = Count(items, x => x.Status == SlaveStatus.Success),
                Failed = Count(items, x => x.Status == SlaveStatus.Failed),
                Skipped = Count(items, x => x.Skip || x.Status == SlaveStatus.Skipped),
                VerifyPass = Count(items, x => x.VerifyStatus == VerifyStatus.Pass),
                VerifyFail = Count(items, x => x.VerifyStatus == VerifyStatus.Fail),
                Retries = RetryCount,
                Elapsed = DateTime.Now - _startedAt
            };
            s.VerifyMissing = Math.Max(0, s.Success - s.VerifyPass - s.VerifyFail);
            return s;
        }

        /// <summary>把 MAC 快照映射回当前列表项（被删除的自动忽略）。</summary>
        private List<SlaveSessionItem> Targets()
        {
            var all = _allItems() ?? (IReadOnlyList<SlaveSessionItem>)Array.Empty<SlaveSessionItem>();
            var map = new Dictionary<string, SlaveSessionItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in all)
                if (it?.Mac != null && !map.ContainsKey(it.Mac)) map[it.Mac] = it;

            var list = new List<SlaveSessionItem>(_targetMacs.Count);
            foreach (var m in _targetMacs)
                if (map.TryGetValue(m, out var it)) list.Add(it);
            return list;
        }

        private static int Count(IEnumerable<SlaveSessionItem> items, Func<SlaveSessionItem, bool> p)
            => items.Count(p);
    }
}
