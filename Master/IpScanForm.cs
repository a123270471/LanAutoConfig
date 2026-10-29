using LanAutoConfig.Common;

namespace LanAutoConfig.Master
{
    /// <summary>
    /// 真实 IP 占用扫描：在下发之前先看看这些目标 IP 在网段里是不是已经被别的机器占了。
    ///
    /// 和“冲突检测”不是一回事：模板冲突只能发现“两台机器被分配了同一个 IP”；
    /// 现场真正踩的坑是“这个 IP 早被一台老机器/打印机/摄像头占着”，
    /// 只有 ping 一遍再看 ARP 表才看得出来。带上 `mac/ip` 一起扫，命中就直接告诉你是谁占的。
    /// </summary>
    public class IpScanForm : Form
    {
        private readonly MasterForm _master;
        private readonly DataGridView _grid;
        private readonly Label _lblStatus;
        private readonly Button _btnScan;
        private readonly Button _btnSkip;
        private readonly Dictionary<string, string> _macByIp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private List<IpOccupancy> _results = new List<IpOccupancy>();

        public IpScanForm(MasterForm master)
        {
            _master = master;
            Text = "IP 占用扫描";
            Size = new Size(860, 560);
            MinimumSize = new Size(660, 420);
            StartPosition = FormStartPosition.CenterParent;
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.None;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

            var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
            _btnScan = new Button { Text = "开始扫描", Width = 100, Height = 30 };
            _btnScan.Click += (s, e) => StartScan();
            _btnSkip = new Button { Text = "把冲突的标为跳过", Width = 150, Height = 30, Enabled = false };
            _btnSkip.Click += (s, e) => SkipConflicts();
            bar.Controls.Add(_btnScan);
            bar.Controls.Add(_btnSkip);
            bar.Controls.Add(new Label
            {
                Text = "  扫描会先 ping 每个目标 IP（填充 ARP 表），再比对 MAC；一个 /24 网段约 10~20 秒。",
                AutoSize = true,
                ForeColor = System.Drawing.Color.DimGray,
                Margin = new Padding(6, 8, 0, 0)
            });
            root.Controls.Add(bar, 0, 0);

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = System.Drawing.SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle
            };
            _grid.Columns.Add("ip", "目标 IP");
            _grid.Columns.Add("owner", "归属主机名");
            _grid.Columns.Add("exp", "期望 MAC（本机记录）");
            _grid.Columns.Add("act", "ARP 里实际的 MAC");
            _grid.Columns.Add("ping", "Ping");
            _grid.Columns.Add("note", "结论");
            _grid.Columns["ping"].FillWeight = 45;
            _grid.Columns["note"].FillWeight = 200;
            root.Controls.Add(_grid, 0, 1);

            _lblStatus = new Label { Dock = DockStyle.Fill, Text = "点【开始扫描】检查目标 IP 是否已被占用。", TextAlign = System.Drawing.ContentAlignment.MiddleLeft };
            root.Controls.Add(_lblStatus, 0, 2);

            Controls.Add(root);
            UiScale.Apply(this);
        }

        private async void StartScan()
        {
            var items = _master.GetAllItems()
                .Where(x => Validators.IsValidIp(x.TargetIp))
                .ToList();
            if (items.Count == 0)
            {
                MessageBox.Show("当前没有带目标 IP 的终端（DHCP 模式没有静态 IP，不需要扫）。", "提示");
                return;
            }

            _macByIp.Clear();
            var cands = new List<(string Ip, string ExpectedMac, string Owner)>();
            foreach (var it in items)
            {
                if (!_macByIp.ContainsKey(it.TargetIp)) _macByIp[it.TargetIp] = it.Mac;
                cands.Add((it.TargetIp, it.Mac, it.TargetHostName));
            }

            _btnScan.Enabled = false;
            _btnSkip.Enabled = false;
            _lblStatus.Text = $"正在扫描 {cands.Count} 个 IP…";
            _grid.Rows.Clear();

            try
            {
                var results = await Task.Run(() => NetProbe.ScanConflicts(cands, m =>
                {
                    if (IsDisposed) return;
                    try { BeginInvoke(new Action(() => _lblStatus.Text = m)); } catch { }
                }));
                _results = results;
                Fill(results);
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "扫描失败：" + ex.Message;
            }
            finally
            {
                _btnScan.Enabled = true;
            }
        }

        private void Fill(List<IpOccupancy> results)
        {
            _grid.Rows.Clear();
            int conflict = 0, free = 0, ok = 0;
            foreach (var r in results.OrderBy(x => x.Ip))
            {
                string note = r.Describe();
                if (r.Conflict) { conflict++; note += "  ← 会被这台机器抢走地址"; }
                else if (r.Free) free++;
                else ok++;

                int i = _grid.Rows.Add(r.Ip, r.Owner ?? "", r.ExpectedMac ?? "", r.ActualMac ?? "",
                    r.PingOk ? "通" : "不通", note);
                if (r.Conflict) _grid.Rows[i].DefaultCellStyle.BackColor = System.Drawing.Color.LightSalmon;
                else if (r.Free) _grid.Rows[i].DefaultCellStyle.ForeColor = System.Drawing.Color.DimGray;
            }
            _lblStatus.Text = $"扫描完成：共 {results.Count} 个 IP，冲突 {conflict}，未被占用 {free}，MAC 匹配 {ok}。" +
                              (conflict > 0 ? "建议先处理冲突（加黑名单/换编号/把冲突的标为跳过）再下发。" : "");
            _btnSkip.Enabled = conflict > 0;
        }

        private void SkipConflicts()
        {
            var macs = _results.Where(x => x.Conflict)
                .Select(x => _macByIp.TryGetValue(x.Ip, out string m) ? m : null)
                .Where(x => !string.IsNullOrEmpty(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (macs.Count == 0) { MessageBox.Show("没有需要跳过的冲突终端。", "提示"); return; }
            if (MessageBox.Show($"把 {macs.Count} 台存在 IP 冲突的终端标为“跳过”？\n\n" +
                                "标跳过后它们不会参与下发，等处理完冲突再右键取消跳过即可。",
                    "标记跳过", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            _master.MarkSkipMacs(macs, true);
            _lblStatus.Text = $"已把 {macs.Count} 台冲突终端标为跳过。";
            _btnSkip.Enabled = false;
        }
    }
}
