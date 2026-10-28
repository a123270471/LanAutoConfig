using LanAutoConfig.Common;

namespace LanAutoConfig.Master
{
    /// <summary>
    /// MAC 白名单管理窗口：启用开关、手工添加、从当前会话终端一键导入、移除/清空。
    /// 启用后控制端只接受名单里 MAC 的上报与心跳，其余一律拒绝并写日志。
    /// </summary>
    public class WhitelistForm : Form
    {
        private readonly Whitelist _whitelist;
        private readonly List<SlaveSessionItem> _sessionItems;

        private CheckBox _chkEnabled;
        private ListBox _list;
        private TextBox _txtMac;
        private Label _lblCount;

        public WhitelistForm(Whitelist whitelist, List<SlaveSessionItem> sessionItems)
        {
            _whitelist = whitelist;
            _sessionItems = sessionItems ?? new List<SlaveSessionItem>();

            Text = "MAC 白名单";
            Size = new Size(560, 460);
            MinimumSize = new Size(480, 360);
            StartPosition = FormStartPosition.CenterParent;
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.None;
            if (UiScale.Simulated)
                Font = new System.Drawing.Font(Font.FontFamily, Font.Size * UiScale.Factor, Font.Style, Font.Unit);
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var hint = new Label
            {
                Dock = DockStyle.Top,
                Height = 52,
                Padding = new Padding(10, 8, 10, 0),
                ForeColor = System.Drawing.Color.DimGray,
                Text = "启用后只接受名单内的终端上报/心跳（黑名单优先）。\n" +
                       "名单为空且启用 = 拒绝全部终端，请先添加或从当前终端导入。"
            };

            _chkEnabled = new CheckBox
            {
                Dock = DockStyle.Top,
                Height = 30,
                Padding = new Padding(10, 6, 10, 0),
                Text = "启用白名单（只接受名单内的 MAC）",
                Checked = whitelist.Enabled
            };
            _chkEnabled.CheckedChanged += (s, e) =>
            {
                _whitelist.SetEnabled(_chkEnabled.Checked);
                RefreshCount();
            };

            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 42,
                Padding = new Padding(10, 6, 10, 0),
                WrapContents = false
            };
            _txtMac = new TextBox { Width = 190, Margin = new Padding(0, 3, 6, 0) };
            var btnAdd = new Button { Text = "添加", Width = 70, Height = 28 };
            btnAdd.Click += (s, e) => AddTyped();
            var btnImport = new Button { Text = "从当前终端导入", Width = 130, Height = 28 };
            btnImport.Click += (s, e) => ImportFromSession();
            bar.Controls.Add(new Label { Text = "MAC：", AutoSize = true, Margin = new Padding(0, 7, 2, 0) });
            bar.Controls.Add(_txtMac);
            bar.Controls.Add(btnAdd);
            bar.Controls.Add(btnImport);

            _list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, SelectionMode = SelectionMode.MultiExtended };

            var bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 46,
                Padding = new Padding(10, 8, 10, 0),
                WrapContents = false
            };
            var btnRemove = new Button { Text = "移除选中", Width = 100, Height = 30 };
            btnRemove.Click += (s, e) => RemoveSelected();
            var btnClear = new Button { Text = "清空全部", Width = 100, Height = 30 };
            btnClear.Click += (s, e) => ClearAll();
            var btnClose = new Button { Text = "关闭", Width = 80, Height = 30 };
            btnClose.Click += (s, e) => Close();
            bottom.Controls.Add(btnRemove);
            bottom.Controls.Add(btnClear);
            bottom.Controls.Add(btnClose);

            _lblCount = new Label { Dock = DockStyle.Bottom, Height = 26, Padding = new Padding(12, 4, 0, 0), ForeColor = System.Drawing.Color.DimGray };

            Controls.Add(_list);
            Controls.Add(bottom);
            Controls.Add(_lblCount);
            Controls.Add(bar);
            Controls.Add(_chkEnabled);
            Controls.Add(hint);

            Reload();
            UiScale.Apply(this);
        }

        private void Reload()
        {
            _list.Items.Clear();
            foreach (var mac in _whitelist.Macs) _list.Items.Add(mac);
            RefreshCount();
        }

        private void RefreshCount()
        {
            _lblCount.Text = $"共 {_whitelist.Macs.Count} 条；当前{( _whitelist.Enabled ? "已启用" : "未启用")}" +
                             (_whitelist.Enabled && _whitelist.Macs.Count == 0 ? "　（启用但名单为空：会拒绝所有终端）" : "");
        }

        private void AddTyped()
        {
            string mac = (_txtMac.Text ?? "").Trim();
            if (!Validators.IsValidMac(mac))
            {
                MessageBox.Show("MAC 格式不对，示例：00-0C-29-9F-1B-EF（也接受 AA:BB:CC:DD:EE:FF）", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!_whitelist.AddMac(mac))
            {
                MessageBox.Show("这条 MAC 已经在名单里了。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _txtMac.Text = "";
            Reload();
        }

        private void ImportFromSession()
        {
            int added = 0;
            foreach (var it in _sessionItems)
                if (!string.IsNullOrEmpty(it.Mac) && _whitelist.AddMac(it.Mac)) added++;
            Reload();
            MessageBox.Show(added > 0 ? $"已从当前终端列表导入 {added} 条 MAC。" : "当前终端列表里没有新的 MAC 可导入。",
                "导入", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void RemoveSelected()
        {
            if (_list.SelectedItems.Count == 0)
            {
                MessageBox.Show("请先选中要移除的条目（可按住 Ctrl 多选）。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var macs = _list.SelectedItems.Cast<string>().ToList();
            int removed = 0;
            foreach (var mac in macs) if (_whitelist.RemoveMac(mac)) removed++;
            Reload();
            if (removed > 0) _lblCount.Text += $"　（已移除 {removed} 条）";
        }

        private void ClearAll()
        {
            if (_whitelist.Macs.Count == 0) return;
            if (MessageBox.Show("确认清空白名单？（启用状态下清空 = 拒绝所有终端）", "确认",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            _whitelist.Clear();
            Reload();
        }
    }
}
