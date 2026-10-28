using System.Windows.Forms;
using LanAutoConfig.Common;

namespace LanAutoConfig.Master
{
    /// <summary>
    /// 自定义命令/脚本下发对话框：编辑脚本，选择 PowerShell / CMD，向选中（或全部在线）终端下发，
    /// 被控端回传的结果实时显示在下方列表里。
    /// </summary>
    public class CustomCmdForm : Form
    {
        private readonly MasterForm _master;
        private readonly TextBox _txtScript;
        private readonly ComboBox _cmbShell;
        private readonly NumericUpDown _numTimeout;
        private readonly ListView _lv;
        private readonly Label _lblStatus;
        private readonly Dictionary<string, ListViewItem> _rows = new Dictionary<string, ListViewItem>(StringComparer.OrdinalIgnoreCase);

        public CustomCmdForm(MasterForm master)
        {
            _master = master;
            Text = "下发自定义命令 / 脚本";
            Size = new System.Drawing.Size(720, 540);
            MinimumSize = new System.Drawing.Size(560, 400);
            StartPosition = FormStartPosition.CenterParent;
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.None;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(10) };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));   // 工具栏
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));   // 脚本
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));  // 状态
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));   // 结果

            // 工具栏
            var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0), WrapContents = false };
            bar.Controls.Add(new Label { Text = "执行器：", AutoSize = true, Margin = new Padding(0, 5, 4, 0) });
            _cmbShell = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110, Margin = new Padding(0, 2, 10, 0) };
            _cmbShell.Items.Add("PowerShell"); _cmbShell.Items.Add("CMD"); _cmbShell.SelectedIndex = 0;
            bar.Controls.Add(_cmbShell);
            bar.Controls.Add(new Label { Text = "超时(秒)：", AutoSize = true, Margin = new Padding(0, 5, 4, 0) });
            _numTimeout = new NumericUpDown { Minimum = 5, Maximum = 600, Value = 60, Width = 70, Margin = new Padding(0, 2, 10, 0) };
            bar.Controls.Add(_numTimeout);
            var btnSel = new Button { Text = "下发到选中", Width = 110, Height = 28 };
            btnSel.Click += (s, e) => Dispatch(false);
            var btnAll = new Button { Text = "下发到全部在线", Width = 130, Height = 28 };
            btnAll.Click += (s, e) => Dispatch(true);
            bar.Controls.Add(btnSel); bar.Controls.Add(btnAll);
            root.Controls.Add(bar, 0, 0);

            // 脚本框
            _txtScript = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new System.Drawing.Font("Consolas", 10F),
                Text = "# 在被控端本地执行，例如：\r\nGet-NetIPAddress -AddressFamily IPv4 | Select-Object IPAddress, InterfaceAlias\r\n# 或 cmd： ipconfig /all"
            };
            root.Controls.Add(_txtScript, 0, 1);

            _lblStatus = new Label { Dock = DockStyle.Fill, Text = "先选中终端（Ctrl/Shift 多选），或点“下发到全部在线”。", TextAlign = System.Drawing.ContentAlignment.MiddleLeft };
            root.Controls.Add(_lblStatus, 0, 2);

            // 结果列表
            _lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
            _lv.Columns.Add("MAC", 140); _lv.Columns.Add("结果", 70); _lv.Columns.Add("退出码", 60); _lv.Columns.Add("输出 / 错误", 380);
            root.Controls.Add(_lv, 0, 3);

            Controls.Add(root);
            UiScale.Apply(this);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _master.RegisterCustomResultHandler(OnResult);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _master.UnregisterCustomResultHandler(OnResult);
            base.OnFormClosing(e);
        }

        private void Dispatch(bool all)
        {
            var targets = all ? _master.SelectedItemsOrAllOnline() : _master.GetSelectedItems();
            if (targets.Count == 0)
            {
                _lblStatus.Text = all ? "没有在线终端可下发。" : "请先在终端列表里选中终端。";
                return;
            }
            string shell = _cmbShell.SelectedItem?.ToString() == "CMD" ? "cmd" : "powershell";
            string script = _txtScript.Text;
            int timeout = (int)_numTimeout.Value;
            _rows.Clear();
            _lv.Items.Clear();
            foreach (var it in targets)
            {
                var item = new ListViewItem(it.Mac) { SubItems = { "发送中…", "", "" } };
                _rows[it.Mac] = item;
                _lv.Items.Add(item);
                _master.SendCustomCommandTo(it, shell, script, timeout);
            }
            _lblStatus.Text = $"已向 {targets.Count} 台下发 {shell} 命令，等待回传…";
        }

        private void OnResult(SlaveCustomResultMsg m)
        {
            if (InvokeRequired) { BeginInvoke(new Action<SlaveCustomResultMsg>(OnResult), m); return; }
            if (_rows.TryGetValue(m.LocalMac, out var item))
            {
                item.SubItems[1].Text = m.Success ? "成功" : "失败";
                item.SubItems[2].Text = m.ExitCode.ToString();
                string detail = string.IsNullOrEmpty(m.StdOut) ? "" : m.StdOut;
                if (!string.IsNullOrEmpty(m.StdErr)) detail += (detail.Length > 0 ? "\n" : "") + "ERR: " + m.StdErr;
                item.SubItems[3].Text = detail.Length > 600 ? detail.Substring(0, 600) + "…" : detail;
                item.ForeColor = m.Success ? System.Drawing.Color.DarkGreen : System.Drawing.Color.Red;
            }
        }
    }
}
