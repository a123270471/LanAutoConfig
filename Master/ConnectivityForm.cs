using System.Windows.Forms;
using LanAutoConfig.Common;

namespace LanAutoConfig.Master
{
    /// <summary>
    /// 连通性矩阵：一台一台看“能不能 ping 通 / 被控端端口能不能连”，
    /// 用来快速区分三类故障：
    ///   · ping 通 + 端口通  → 正常
    ///   · ping 通 + 端口不通 → 机器活着，但被控端没跑，或被防火墙拦了 48889
    ///   · ping 不通 + 端口通 → ICMP 被策略封了，但端口能用（照样能下发）
    ///   · 都不通            → 掉线/没插网线/IP 配错
    ///
    /// 全部并发探测（并发 24），一个 /24 十几秒出结果，不会卡界面。
    /// </summary>
    public class ConnectivityForm : Form
    {
        private readonly MasterForm _master;
        private readonly DataGridView _grid;
        private readonly Label _lblStatus;
        private readonly Button _btnProbe, _btnCopy;
        private readonly Dictionary<string, SlaveSessionItem> _byIp = new Dictionary<string, SlaveSessionItem>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _rowByIp = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public ConnectivityForm(MasterForm master)
        {
            _master = master;
            Text = "连通性检测";
            Size = new Size(900, 580);
            MinimumSize = new Size(700, 440);
            StartPosition = FormStartPosition.CenterParent;
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false;
            MinimizeBox = false;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

            var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
            _btnProbe = new Button { Text = "开始检测", Width = 100, Height = 30 };
            _btnProbe.Click += (s, e) => StartProbe();
            _btnCopy = new Button { Text = "复制结果", Width = 100, Height = 30, Enabled = false };
            _btnCopy.Click += (s, e) => CopyResult();
            bar.Controls.Add(_btnProbe);
            bar.Controls.Add(_btnCopy);
            bar.Controls.Add(new Label
            {
                Text = "  检测“ping 通不通”和“被控端端口 48889 能不能连”，两者一起看才能分清是掉线还是程序没跑。",
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
                BackgroundColor = SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle,
                EnableHeadersVisualStyles = false,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight = UiScale.S(26)
            };
            _grid.ColumnHeadersDefaultCellStyle.BackColor = System.Drawing.Color.FromArgb(243, 244, 246);
            _grid.ColumnHeadersDefaultCellStyle.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Bold);
            _grid.Columns.Add("state", "在线");
            _grid.Columns.Add("host", "主机名");
            _grid.Columns.Add("src", "来源 IP");
            _grid.Columns.Add("target", "目标 IP");
            _grid.Columns.Add("ping", "Ping");
            _grid.Columns.Add("tcp", "端口 48889");
            _grid.Columns.Add("note", "结论 / 建议");
            _grid.Columns["state"].FillWeight = 9;
            _grid.Columns["host"].FillWeight = 20;
            _grid.Columns["src"].FillWeight = 16;
            _grid.Columns["target"].FillWeight = 16;
            _grid.Columns["ping"].FillWeight = 11;
            _grid.Columns["tcp"].FillWeight = 13;
            _grid.Columns["note"].FillWeight = 60;
            root.Controls.Add(_grid, 0, 1);

            _lblStatus = new Label
            {
                Dock = DockStyle.Fill,
                Text = "点【开始检测】。表里先按列表当前数据填好，检测结果逐台回填。",
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                ForeColor = System.Drawing.Color.DimGray
            };
            root.Controls.Add(_lblStatus, 0, 2);

            Controls.Add(root);
            UiScale.Apply(this);
            LoadRows();
        }

        /// <summary>先按当前终端列表铺好行，检测完只更新单元格（不整表重画，避免闪烁）。</summary>
        private void LoadRows()
        {
            _grid.Rows.Clear();
            _byIp.Clear();
            _rowByIp.Clear();

            var items = _master.GetAllItems()
                .OrderByDescending(x => x.Online)
                .ThenBy(x => x.TargetHostName)
                .ToList();

            foreach (var it in items)
            {
                string ip = !string.IsNullOrEmpty(it.SourceIp) ? it.SourceIp : it.TargetIp;
                if (!Validators.IsValidIp(ip)) ip = "";     // 没有可用地址的只显示，不探测
                int i = _grid.Rows.Add(it.Online ? "在线" : "离线", it.TargetHostName ?? it.Mac, it.SourceIp ?? "", it.TargetIp ?? "", "", "", "");
                _grid.Rows[i].Tag = it;
                if (ip.Length > 0 && !_byIp.ContainsKey(ip))
                {
                    _byIp[ip] = it;
                    _rowByIp[ip] = i;
                }
                _grid.Rows[i].Cells["state"].Style.ForeColor = it.Online
                    ? System.Drawing.Color.Green
                    : System.Drawing.Color.Gray;
            }
            _lblStatus.Text = $"共 {_grid.Rows.Count} 台，其中有可用地址可探测的 {_byIp.Count} 台。点【开始检测】。";
        }

        private async void StartProbe()
        {
            if (_byIp.Count == 0) { MessageBox.Show(this, "没有可探测的地址（终端都还没有来源 IP / 目标 IP）。", "提示"); return; }

            _btnProbe.Enabled = false;
            _lblStatus.Text = $"正在探测 {_byIp.Count} 个地址（并发 24）…";

            int ok = 0, onlyPing = 0, onlyTcp = 0, dead = 0;
            var ips = _byIp.Keys.ToList();
            try
            {
                await Task.Run(() => NetProbe.ProbeAll(ips, AppConfig.TcpPort, r =>
                {
                    if (IsDisposed) return;
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            if (_rowByIp.TryGetValue(r.Ip, out int row) && row < _grid.Rows.Count && !_grid.Rows[row].IsNewRow)
                                FillRow(row, r);
                            if (r.PingOk && r.TcpOk) ok++;
                            else if (r.PingOk) onlyPing++;
                            else if (r.TcpOk) onlyTcp++;
                            else dead++;
                            _lblStatus.Text = $"已探测 {ok + onlyPing + onlyTcp + dead}/{_byIp.Count}…";
                        }));
                    }
                    catch { }
                }));

                _lblStatus.Text = $"检测完成：正常 {ok} 台；ping 通但端口不通 {onlyPing} 台（多半是被控端没运行/防火墙拦了）；" +
                                  $"只有端口通 {onlyTcp} 台（ICMP 被封，仍可下发）；完全不通 {dead} 台（掉线/IP 配错/没插网线）。";
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "探测失败：" + ex.Message;
            }
            finally
            {
                _btnProbe.Enabled = true;
                _btnCopy.Enabled = true;
            }
        }

        private void FillRow(int row, ProbeResult r)
        {
            var cells = _grid.Rows[row].Cells;
            cells["ping"].Value = r.PingOk ? (r.PingMs >= 0 ? r.PingMs + "ms" : "通") : "不通";
            cells["tcp"].Value = r.TcpOk ? "通" : "不通";

            string note;
            System.Drawing.Color color;
            if (r.PingOk && r.TcpOk) { note = "正常"; color = System.Drawing.Color.Green; }
            else if (r.PingOk) { note = "机器在，但被控端没响应 → 看程序是否在跑 / 防火墙是否放通 48889"; color = System.Drawing.Color.FromArgb(197, 15, 31); }
            else if (r.TcpOk) { note = "ICMP 被策略封了，但端口能连 → 照样能下发"; color = System.Drawing.Color.FromArgb(150, 90, 0); }
            else { note = "完全不通 → 检查网线/交换机端口/IP 是否配错"; color = System.Drawing.Color.FromArgb(197, 15, 31); }

            cells["note"].Value = note;
            cells["note"].Style.ForeColor = color;
            cells["ping"].Style.ForeColor = r.PingOk ? System.Drawing.Color.Green : System.Drawing.Color.FromArgb(197, 15, 31);
            cells["tcp"].Style.ForeColor = r.TcpOk ? System.Drawing.Color.Green : System.Drawing.Color.FromArgb(197, 15, 31);
        }

        private void CopyResult()
        {
            var sb = new StringBuilder();
            sb.AppendLine("在线\t主机名\t来源IP\t目标IP\tPing\t端口\t结论");
            foreach (DataGridViewRow row in _grid.Rows)
            {
                var v = new List<string>();
                foreach (DataGridViewCell c in row.Cells) v.Add(c.Value as string ?? "");
                sb.AppendLine(string.Join("\t", v));
            }
            try
            {
                Clipboard.SetText(sb.ToString());
                _lblStatus.Text = "结果已复制到剪贴板，可直接粘到 Excel / 工单里。";
            }
            catch (Exception ex) { _lblStatus.Text = "复制失败：" + ex.Message; }
        }
    }
}
