using System.Windows.Forms;
using LanAutoConfig.Common;

namespace LanAutoConfig.Master
{
    /// <summary>
    /// 文件分发 / 静默装软件。
    ///
    /// 流程：选好本地文件 → 复制进分发目录并算 SHA256 → 控制端内置文件服务给出下载地址
    ///      → 把“下载地址 + 校验值 + 落盘路径 + 是否执行”发给被控端 → 被控端边下边校验、可选静默执行 → 回传结果。
    /// </summary>
    public class FilePushForm : Form
    {
        private readonly MasterForm _master;

        private readonly TextBox _txtFile;
        private readonly TextBox _txtTargetPath;
        private readonly TextBox _txtArgs;
        private readonly NumericUpDown _numTimeout;
        private readonly ComboBox _cmbMode;
        private readonly CheckBox _chkRun, _chkSelfUpdate;
        private readonly RadioButton _rbSelected, _rbOnline, _rbAll;
        private readonly Label _lblFileInfo, _lblProgress;
        private readonly DataGridView _grid;
        private readonly Button _btnSend;
        private readonly Action<SlaveFileResultMsg> _onResult;

        // 已就绪的文件（点“发送”时用；换文件要重新 Stage）
        private string _fileName, _url, _sha256;
        private long _size;

        public FilePushForm(MasterForm master)
        {
            _master = master;
            Text = "文件分发 / 静默装软件";
            Size = new Size(760, 640);
            MinimumSize = new Size(700, 580);
            StartPosition = FormStartPosition.CenterParent;
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false;
            MinimizeBox = false;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12) };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));   // 选文件
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));   // 落盘路径
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));  // 执行选项
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));   // 目标范围
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 结果表 + 按钮

            // ---- 选文件 ----
            var row1 = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = new Padding(0) };
            row1.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
            row1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row1.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            row1.Controls.Add(new Label { Text = "本地文件：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            _txtFile = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Anchor = AnchorStyles.Left | AnchorStyles.Right };
            row1.Controls.Add(_txtFile, 1, 0);
            var btnPick = new Button { Text = "选择文件…", Width = 92, Height = 28, Anchor = AnchorStyles.Left };
            btnPick.Click += (s, e) => PickFile();
            row1.Controls.Add(btnPick, 2, 0);
            root.Controls.Add(row1, 0, 0);

            // ---- 落盘路径 ----
            var row2 = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = new Padding(0) };
            row2.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
            row2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row2.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            row2.Controls.Add(new Label { Text = "存到：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            _txtTargetPath = new TextBox { Dock = DockStyle.Fill, Anchor = AnchorStyles.Left | AnchorStyles.Right };
            row2.Controls.Add(_txtTargetPath, 1, 0);
            _lblFileInfo = new Label { Text = "留空 = %TEMP%\\LanAC", AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = System.Drawing.Color.DimGray };
            row2.Controls.Add(_lblFileInfo, 2, 0);
            root.Controls.Add(row2, 0, 1);

            // ---- 执行选项 ----
            var grpRun = new GroupBox { Text = "下载完是否执行（静默装软件）", Dock = DockStyle.Fill };
            var tblRun = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Padding = new Padding(8, 2, 8, 2) };
            tblRun.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
            tblRun.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
            tblRun.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            tblRun.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tblRun.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            tblRun.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            tblRun.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));

            _chkRun = new CheckBox
            {
                Text = "下载后静默执行（msi 走 msiexec /qn；exe 直接跑；ps1/bat 走解释器）",
                Checked = true,
                AutoSize = true,
                Anchor = AnchorStyles.Left
            };
            _chkRun.CheckedChanged += (s, e) => SyncRunUi();
            tblRun.Controls.Add(_chkRun, 0, 0);
            tblRun.SetColumnSpan(_chkRun, 2);

            tblRun.Controls.Add(new Label { Text = "方式：", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 0);
            _cmbMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120, Anchor = AnchorStyles.Left };
            _cmbMode.Items.AddRange(new object[] { "auto（按扩展名）", "msi", "exe", "ps1", "bat", "none（只下载）" });
            _cmbMode.SelectedIndex = 0;
            tblRun.Controls.Add(_cmbMode, 3, 0);

            tblRun.Controls.Add(new Label { Text = "参数：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            _txtArgs = new TextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, Dock = DockStyle.Fill };
            tblRun.Controls.Add(_txtArgs, 1, 1);
            tblRun.Controls.Add(new Label { Text = "执行超时(秒)：", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 1);
            _numTimeout = new NumericUpDown { Minimum = 10, Maximum = 7200, Value = 600, Width = 80, Anchor = AnchorStyles.Left };
            tblRun.Controls.Add(_numTimeout, 3, 1);

            _chkSelfUpdate = new CheckBox
            {
                Text = "把这台文件当作被控端新版本，让被控端“换掉自己”（自升级，仅限 .exe）",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                ForeColor = System.Drawing.Color.FromArgb(0, 90, 170)
            };
            _chkSelfUpdate.CheckedChanged += (s, e) => SyncRunUi();
            tblRun.Controls.Add(_chkSelfUpdate, 0, 2);
            tblRun.SetColumnSpan(_chkSelfUpdate, 4);

            grpRun.Controls.Add(tblRun);
            root.Controls.Add(grpRun, 0, 2);

            // ---- 目标范围 ----
            var grpTarget = new GroupBox { Text = "发给谁", Dock = DockStyle.Fill };
            var barTarget = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(8, 4, 8, 4) };
            _rbSelected = new RadioButton { Text = "列表里选中的", AutoSize = true };
            _rbOnline = new RadioButton { Text = "全部在线", AutoSize = true, Checked = true };
            _rbAll = new RadioButton { Text = "全部（含离线的，会失败）", AutoSize = true };
            barTarget.Controls.Add(_rbSelected);
            barTarget.Controls.Add(_rbOnline);
            barTarget.Controls.Add(_rbAll);
            grpTarget.Controls.Add(barTarget);
            root.Controls.Add(grpTarget, 0, 3);

            // ---- 结果 + 按钮 ----
            var pnlBottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0) };
            pnlBottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            pnlBottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            pnlBottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

            _lblProgress = new Label { Text = "① 先选文件 ② 设好落盘/执行选项 ③ 点【开始分发】", Dock = DockStyle.Fill, ForeColor = System.Drawing.Color.DimGray };
            pnlBottom.Controls.Add(_lblProgress, 0, 0);

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle,
                EnableHeadersVisualStyles = false,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight = UiScale.S(26)
            };
            _grid.ColumnHeadersDefaultCellStyle.BackColor = System.Drawing.Color.FromArgb(243, 244, 246);
            _grid.ColumnHeadersDefaultCellStyle.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Bold);
            _grid.Columns.Add("colHost", "主机名");
            _grid.Columns.Add("colMac", "MAC");
            _grid.Columns.Add("colState", "状态");
            _grid.Columns.Add("colMsg", "说明");
            _grid.Columns["colHost"].FillWeight = 22;
            _grid.Columns["colMac"].FillWeight = 20;
            _grid.Columns["colState"].FillWeight = 10;
            _grid.Columns["colMsg"].FillWeight = 60;
            pnlBottom.Controls.Add(_grid, 0, 1);

            var barBtn = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
            _btnSend = new Button { Text = "开始分发", Width = 120, Height = 32 };
            _btnSend.Click += (s, e) => Send();
            var btnClose = new Button { Text = "关闭", Width = 90, Height = 32, DialogResult = DialogResult.Cancel };
            barBtn.Controls.Add(_btnSend);
            barBtn.Controls.Add(btnClose);
            pnlBottom.Controls.Add(barBtn, 0, 2);

            root.Controls.Add(pnlBottom, 0, 4);

            Controls.Add(root);
            CancelButton = btnClose;
            UiScale.Apply(this);

            _onResult = OnFileResult;
            _master.RegisterFileResultHandler(_onResult);

            SyncRunUi();
        }

        private void SyncRunUi()
        {
            bool self = _chkSelfUpdate.Checked;
            _chkRun.Enabled = !self;
            _cmbMode.Enabled = !self && _chkRun.Checked;
            _txtArgs.Enabled = !self && _chkRun.Checked;
            _numTimeout.Enabled = !self && _chkRun.Checked;
            _txtTargetPath.Enabled = !self;
            _lblFileInfo.Text = self ? "自升级不用设路径（自动替换自己）" : "留空 = %TEMP%\\LanAC";
            if (self && _cmbMode.SelectedIndex > 0) _cmbMode.SelectedIndex = 0;
        }

        private void PickFile()
        {
            using var dlg = new OpenFileDialog
            {
                Title = "选择要分发的文件",
                Filter = "安装包/程序 (*.exe;*.msi;*.ps1;*.bat;*.cmd)|*.exe;*.msi;*.ps1;*.bat;*.cmd|所有文件 (*.*)|*.*"
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            if (!_master.StageFileForPush(dlg.FileName, out string name, out string url, out string sha, out long size, out string err))
            {
                MessageBox.Show(this, err ?? "准备文件失败", "文件分发", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            _fileName = name; _url = url; _sha256 = sha; _size = size;
            _txtFile.Text = dlg.FileName;
            _lblProgress.Text = $"文件已就绪：{name}（{size / 1024} KB，SHA256 {sha.Substring(0, 16)}…）";

            // 默认勾选“执行”只在能执行时；选中的是 exe 且文件名像被控端，则提示可自升级
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                _chkSelfUpdate.Text = $"把这台文件当作被控端新版本，让被控端“换掉自己”（自升级，仅限 .exe）";
        }

        private void Send()
        {
            if (string.IsNullOrEmpty(_url))
            {
                MessageBox.Show(this, "还没选文件。先点【选择文件…】。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var targets = _rbSelected.Checked ? _master.GetSelectedItems()
                        : _rbOnline.Checked ? _master.GetAllItems().Where(x => x.Online).ToList()
                        : _master.GetAllItems();
            if (targets.Count == 0)
            {
                MessageBox.Show(this, "没有符合条件的目标终端。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            bool self = _chkSelfUpdate.Checked;
            if (self && !_fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "自升级只能推 .exe。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (self && MessageBox.Show(this,
                    $"让 {targets.Count} 台终端把自己的程序替换成 {_fileName}？\n\n" +
                    "被控端会：下载 → 校验 SHA256 → 退出自己 → 批处理等进程结束后替换 → 重新启动。\n" +
                    "推错文件会导致这些机器再也连不上，建议先拿 1 台试。", "自升级",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

            bool run = _chkRun.Checked && !self;
            string mode = ModeText();
            string targetPath = _txtTargetPath.Text.Trim();
            string args = _txtArgs.Text.Trim();
            int timeout = (int)_numTimeout.Value;

            // 铺结果表
            _grid.Rows.Clear();
            foreach (var it in targets)
            {
                int i = _grid.Rows.Add(it.TargetHostName ?? it.Mac, it.Mac, "待发送", "");
                _grid.Rows[i].Tag = it.Mac;
            }

            _btnSend.Enabled = false;
            _lblProgress.Text = $"正在下发到 {targets.Count} 台…";
            SetAllState("发送中");

            string fileName = _fileName, url = _url, sha = _sha256;
            long size = _size;
            Task.Run(() =>
            {
                int ok = 0, fail = 0;
                foreach (var it in targets)
                {
                    bool sent;
                    string err;
                    if (self) sent = _master.PushSelfUpdateTo(it, url, sha, size, out err);
                    else sent = _master.PushFileTo(it, fileName, url, sha, size, targetPath, run, mode, args, timeout, out err);

                    if (sent) ok++; else fail++;
                    string mac = it.Mac;
                    string e2 = err;
                    SafeUi(() => SetState(mac, sent ? "已下发" : "失败", sent ? "等待被控端下载" : e2, sent));
                }
                SafeUi(() =>
                {
                    _btnSend.Enabled = true;
                    _lblProgress.Text = $"指令已全部下发：成功 {ok} 台，失败 {fail} 台。" +
                                        (run || self ? "（下载/执行结果会陆续回填到上表）" : "");
                });
            });
        }

        private string ModeText()
        {
            switch (_cmbMode.SelectedIndex)
            {
                case 1: return "msi";
                case 2: return "exe";
                case 3: return "ps1";
                case 4: return "bat";
                case 5: return "none";
                default: return "auto";
            }
        }

        private void SetAllState(string state)
        {
            foreach (DataGridViewRow row in _grid.Rows)
            {
                row.Cells["colState"].Value = state;
                row.Cells["colState"].Style.ForeColor = System.Drawing.Color.DimGray;
            }
        }

        private void SetState(string mac, string state, string msg, bool ok)
        {
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (!string.Equals(row.Tag as string, mac, StringComparison.OrdinalIgnoreCase)) continue;
                row.Cells["colState"].Value = state;
                row.Cells["colMsg"].Value = msg ?? "";
                row.Cells["colState"].Style.ForeColor = ok ? System.Drawing.Color.Green : System.Drawing.Color.FromArgb(197, 15, 31);
                break;
            }
        }

        private void OnFileResult(SlaveFileResultMsg m)
        {
            if (IsDisposed) return;
            // 只处理本窗口在等的那些（表格里有这台的 MAC 才更新）
            bool mine = _grid.Rows.Cast<DataGridViewRow>()
                .Any(r => string.Equals(r.Tag as string, m.LocalMac, StringComparison.OrdinalIgnoreCase));
            if (!mine) return;
            SetState(m.LocalMac, m.Success ? "成功" : "失败", m.Message, m.Success);
            int done = _grid.Rows.Cast<DataGridViewRow>().Count(r => (r.Cells["colState"].Value as string) is "成功" or "失败");
            _lblProgress.Text = $"已回传 {done}/{_grid.Rows.Count} 台";
        }

        private void SafeUi(Action a)
        {
            if (IsDisposed || _closed) return;
            if (InvokeRequired) { try { BeginInvoke(a); } catch { } }
            else a();
        }

        private bool _closed;

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _closed = true;
            _master.UnregisterFileResultHandler(_onResult);
            base.OnFormClosing(e);
        }
    }
}
