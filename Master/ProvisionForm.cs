using System.Windows.Forms;
using LanAutoConfig.Common;

namespace LanAutoConfig.Master
{
    /// <summary>
    /// 一键装机流水线窗口。
    ///
    /// 它本身不跑状态机 —— 流水线在 <see cref="MasterForm"/> 里，关掉这个窗口也不会中止。
    /// 这里只负责：① 设置参数 ② 选目标 ③ 显示跑到哪一步、卡了多久 ④ 中止。
    /// </summary>
    public class ProvisionForm : Form
    {
        private readonly MasterForm _master;
        private readonly NumericUpDown _numReport, _numExec, _numRetry, _numRebootDelay, _numOnline, _numVerify;
        private readonly CheckBox _chkAutoReboot, _chkVerify;
        private readonly RadioButton _rbAll, _rbReported, _rbSelected;
        private readonly Label _lblStage, _lblTime;
        private readonly TextBox _txtLog;
        private readonly Button _btnStart, _btnAbort;
        private readonly System.Windows.Forms.Timer _uiTimer = new System.Windows.Forms.Timer();
        private readonly Action<string> _onLog;
        private readonly Action<ProvisionSummary> _onDone;
        private bool _closed;

        public ProvisionForm(MasterForm master)
        {
            _master = master;
            Text = "一键装机流水线";
            Size = new Size(680, 620);
            MinimumSize = new Size(620, 560);
            StartPosition = FormStartPosition.CenterParent;
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false;
            MinimizeBox = false;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(12) };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));   // 说明
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 176));  // 参数
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));   // 目标范围
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));   // 进度
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 日志
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));   // 按钮

            // ---- 说明 ----
            var lblWhat = new Label
            {
                Dock = DockStyle.Fill,
                Text = "一条流水线跑完：等上报 → 自动下发 → 失败自动重试 → 自动重启 → 等上线 → 自动验收 → 出汇总。\n" +
                       "跑起来后关掉本窗口不影响它，主界面【一键装机】按钮上会显示当前阶段。",
                ForeColor = System.Drawing.Color.FromArgb(70, 70, 70)
            };
            root.Controls.Add(lblWhat, 0, 0);

            // ---- 参数 ----
            var grp = new GroupBox { Text = "参数（现场按规模调）", Dock = DockStyle.Fill };
            var tbl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Padding = new Padding(8, 4, 8, 4) };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 5; i++) tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

            _numReport = NewNum(10, 3600, 180);
            AddRow(tbl, 0, "等上报最长(秒)：", _numReport, out _);
            _numExec = NewNum(30, 7200, 300);
            tbl.Controls.Add(new Label { Text = "等执行结果(秒)：", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 0);
            tbl.Controls.Add(_numExec, 3, 0);

            _numRetry = NewNum(0, 10, 2);
            AddRow(tbl, 1, "失败重试次数：", _numRetry, out _);
            _numOnline = NewNum(60, 7200, 420);
            tbl.Controls.Add(new Label { Text = "等上线最长(秒)：", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 1);
            tbl.Controls.Add(_numOnline, 3, 1);

            _numRebootDelay = NewNum(0, 300, 10);
            AddRow(tbl, 2, "重启延迟(秒)：", _numRebootDelay, out _);
            _numVerify = NewNum(10, 3600, 120);
            tbl.Controls.Add(new Label { Text = "等验收结果(秒)：", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 2);
            tbl.Controls.Add(_numVerify, 3, 2);

            _chkAutoReboot = new CheckBox { Text = "执行成功后自动重启（让新主机名/IP 真正生效）", Checked = true, AutoSize = true, Anchor = AnchorStyles.Left };
            tbl.Controls.Add(_chkAutoReboot, 0, 3);
            tbl.SetColumnSpan(_chkAutoReboot, 2);
            _chkVerify = new CheckBox { Text = "最后做验收（比对实际主机名/IP）", Checked = true, AutoSize = true, Anchor = AnchorStyles.Left };
            tbl.Controls.Add(_chkVerify, 2, 3);
            tbl.SetColumnSpan(_chkVerify, 2);

            var lblTip = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                Text = "提示：下发后 20 秒内没进入“执行中”的，按下发失败处理并自动重试（多半是那台机器连不上控制端）。",
                ForeColor = System.Drawing.Color.DimGray
            };
            tbl.Controls.Add(lblTip, 0, 4);
            tbl.SetColumnSpan(lblTip, 4);

            grp.Controls.Add(tbl);
            root.Controls.Add(grp, 0, 1);

            // ---- 目标范围 ----
            var grpTarget = new GroupBox { Text = "跑哪些机器", Dock = DockStyle.Fill };
            var barTarget = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(8, 4, 8, 4) };
            _rbAll = new RadioButton { Text = "全部（含还没上报的，推荐）", Checked = true, AutoSize = true };
            _rbReported = new RadioButton { Text = "仅已上报待执行的", AutoSize = true };
            _rbSelected = new RadioButton { Text = "仅列表里选中的", AutoSize = true };
            barTarget.Controls.Add(_rbAll);
            barTarget.Controls.Add(_rbReported);
            barTarget.Controls.Add(_rbSelected);
            grpTarget.Controls.Add(barTarget);
            root.Controls.Add(grpTarget, 0, 2);

            // ---- 进度 ----
            var pnlProgress = new Panel { Dock = DockStyle.Fill };
            _lblStage = new Label
            {
                Text = "未开始",
                Dock = DockStyle.Top,
                Height = 30,
                Font = new System.Drawing.Font("Microsoft YaHei UI", 11F, System.Drawing.FontStyle.Bold),
                ForeColor = System.Drawing.Color.FromArgb(0, 90, 170)
            };
            _lblTime = new Label
            {
                Text = "",
                Dock = DockStyle.Fill,
                ForeColor = System.Drawing.Color.DimGray
            };
            pnlProgress.Controls.Add(_lblTime);
            pnlProgress.Controls.Add(_lblStage);
            root.Controls.Add(pnlProgress, 0, 3);

            // ---- 日志 ----
            _txtLog = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                ReadOnly = true,
                WordWrap = false,
                BackColor = System.Drawing.Color.White,
                Font = new System.Drawing.Font("Consolas", 9F)
            };
            root.Controls.Add(_txtLog, 0, 4);

            // ---- 按钮 ----
            var barBtn = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
            _btnStart = new Button { Text = "开始装机", Width = 120, Height = 32 };
            _btnStart.Click += (s, e) => StartJob();
            var btnClose = new Button { Text = "关闭", Width = 90, Height = 32, DialogResult = DialogResult.Cancel };
            _btnAbort = new Button { Text = "中止流水线", Width = 120, Height = 32, Enabled = false };
            _btnAbort.ForeColor = System.Drawing.Color.FromArgb(197, 15, 31);
            _btnAbort.Click += (s, e) =>
            {
                if (MessageBox.Show("确定中止本轮装机？已下发的配置不会回滚，只是不再自动往后走。", "中止",
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
                _master.AbortProvision("操作员在中止按钮上停止");
                AppendLog("已中止本轮流水线");
                SyncUi();
            };
            barBtn.Controls.Add(_btnStart);
            barBtn.Controls.Add(_btnAbort);
            barBtn.Controls.Add(btnClose);
            root.Controls.Add(barBtn, 0, 5);

            Controls.Add(root);
            CancelButton = btnClose;
            UiScale.Apply(this);

            // 回填上次用过的参数（从主窗体读，主窗体关掉前会存盘 —— 这里只回填不存）
            var job = _master.Provision;
            if (job != null)
            {
                _numReport.Value = Clamp(_numReport, job.Options.ReportTimeoutSec);
                _numExec.Value = Clamp(_numExec, job.Options.ExecuteTimeoutSec);
                _numRetry.Value = Clamp(_numRetry, job.Options.MaxExecuteRetry);
                _numRebootDelay.Value = Clamp(_numRebootDelay, job.Options.RebootDelaySec);
                _numOnline.Value = Clamp(_numOnline, job.Options.OnlineTimeoutSec);
                _numVerify.Value = Clamp(_numVerify, job.Options.VerifyTimeoutSec);
                _chkAutoReboot.Checked = job.Options.AutoReboot;
                _chkVerify.Checked = job.Options.VerifyAtEnd;
            }

            _onLog = AppendLog;
            _onDone = OnDone;
            _master.RegisterProvisionHandlers(_onLog, _onDone);

            // 每 500 毫秒刷一次阶段文字（状态机本身由主窗体的 1 秒定时器推进）
            _uiTimer.Interval = 500;
            _uiTimer.Tick += (s, e) => { RefreshStage(); SyncUi(); };
            _uiTimer.Start();

            SyncUi();
            RefreshStage();
        }

        private static NumericUpDown NewNum(int min, int max, int val)
            => new NumericUpDown { Minimum = min, Maximum = max, Value = val, Width = 90, Anchor = AnchorStyles.Left };

        private static void AddRow(TableLayoutPanel tbl, int row, string text, Control c, out Label lbl)
        {
            lbl = new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left };
            tbl.Controls.Add(lbl, 0, row);
            tbl.Controls.Add(c, 1, row);
        }

        private static decimal Clamp(NumericUpDown n, int v)
            => Math.Min(n.Maximum, Math.Max(n.Minimum, v));

        private void AppendLog(string msg)
        {
            if (_closed || _txtLog.IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke(new Action(() => AppendLog(msg))); } catch { } return; }
            _txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
        }

        private void OnDone(ProvisionSummary s)
        {
            AppendLog("完成：" + s.Describe());
            SyncUi();
            RefreshStage();
            MessageBox.Show(this, s.Describe(), "本轮装机结果", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>把界面上的参数写回状态机（每轮开始时重新写一遍，改了就生效）。</summary>
        private void ApplyOptions()
        {
            var o = _master.Provision.Options;
            o.ReportTimeoutSec = (int)_numReport.Value;
            o.ExecuteTimeoutSec = (int)_numExec.Value;
            o.MaxExecuteRetry = (int)_numRetry.Value;
            o.RebootDelaySec = (int)_numRebootDelay.Value;
            o.OnlineTimeoutSec = (int)_numOnline.Value;
            o.VerifyTimeoutSec = (int)_numVerify.Value;
            o.AutoReboot = _chkAutoReboot.Checked;
            o.VerifyAtEnd = _chkVerify.Checked;
        }

        private void StartJob()
        {
            if (_master.Provision.Running)
            {
                MessageBox.Show("上一轮还在跑，先等它走完或点【中止流水线】。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            ApplyOptions();

            var all = _master.GetAllItems();
            List<SlaveSessionItem> targets;
            if (_rbSelected.Checked)
            {
                targets = _master.GetSelectedItems();
                if (targets.Count == 0)
                {
                    MessageBox.Show("列表里没有选中的终端。\n\n要么先在列表里选几台，要么改成“全部”。", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }
            else if (_rbReported.Checked)
                targets = all.Where(x => x.Status == SlaveStatus.Reported && !x.Skip).ToList();
            else
                targets = all.Where(x => !x.Skip).ToList();

            if (targets.Count == 0)
            {
                MessageBox.Show("没有可处理的终端。\n\n新机器先开机，等被控端起来（列表里出现）再点开始。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int alreadyDone = targets.Count(x => x.Status == SlaveStatus.Success);
            if (alreadyDone > 0)
            {
                if (MessageBox.Show($"其中 {alreadyDone} 台已经是“成功”状态，要连它们一起重跑吗？\n\n" +
                                    "选【否】只会跳过它们（想只跑成功的机器请用“跳过”标记）。", "确认",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.No)
                {
                    targets = targets.Where(x => x.Status != SlaveStatus.Success).ToList();
                    if (targets.Count == 0) { MessageBox.Show("去掉已成功的机器后没有可跑的了。", "提示"); return; }
                }
            }

            if (MessageBox.Show(
                    $"开始跑 {targets.Count} 台？\n\n" +
                    $"· 自动下发配置{( _chkAutoReboot.Checked ? "，成功后自动重启" : "，不自动重启")}\n" +
                    $"· 失败自动重试 {_numRetry.Value} 次\n" +
                    (_chkVerify.Checked ? "· 最后自动验收\n" : "") +
                    "\n过程中机器会断网重启，属正常现象。", "开始装机",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            _txtLog.Clear();
            AppendLog($"开始：目标 {targets.Count} 台");
            _master.StartProvision(targets.Select(x => x.Mac));
            SyncUi();
        }

        private void RefreshStage()
        {
            var job = _master.Provision;
            if (job == null) return;
            var now = DateTime.Now;
            _lblStage.Text = job.StageText(now);
            if (job.Running)
                _lblTime.Text = $"本阶段 {(int)job.StageElapsed(now).TotalSeconds} 秒 · 总耗时 {Fmt(job.TotalElapsed(now))} · 重试 {job.RetryCount} 次";
            else if (job.Stage == ProvisionStage.Done || job.Stage == ProvisionStage.Aborted)
                _lblTime.Text = (job.LastMessage ?? "") + $" · 总耗时 {Fmt(job.TotalElapsed(now))}";
            else
                _lblTime.Text = "";
        }

        private static string Fmt(TimeSpan t)
            => $"{(int)t.TotalMinutes} 分 {t.Seconds} 秒";

        /// <summary>跑起来就把参数和目标锁住，避免跑到一半被人改。</summary>
        private void SyncUi()
        {
            bool running = _master.Provision != null && _master.Provision.Running;
            _btnStart.Enabled = !running;
            _btnAbort.Enabled = running;
            foreach (Control c in new Control[] { _numReport, _numExec, _numRetry, _numRebootDelay, _numOnline, _numVerify,
                                                  _chkAutoReboot, _chkVerify, _rbAll, _rbReported, _rbSelected })
                c.Enabled = !running;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_master.Provision != null && _master.Provision.Running)
            {
                if (MessageBox.Show("流水线还在跑。\n\n关闭本窗口不会中止它（会继续在后台跑，主界面【一键装机】按钮上能看进度）。\n关闭窗口？",
                        "还在运行", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                {
                    e.Cancel = true;
                    return;
                }
            }
            _closed = true;
            _uiTimer.Stop();
            _master.UnregisterProvisionHandlers(_onLog, _onDone);
            base.OnFormClosing(e);
        }
    }
}
