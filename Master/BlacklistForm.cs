using LanAutoConfig.Common;

namespace LanAutoConfig.Master
{
    /// <summary>黑名单管理窗口：查看、逐条移除、清空。</summary>
    public class BlacklistForm : Form
    {
        private readonly Blacklist _blacklist;
        private ListBox _list;
        private Label _lblCount;

        /// <summary>内容发生变化时通知调用方刷新摘要。</summary>
        public event Action Changed;

        private sealed class Entry
        {
            public bool IsMac;
            public string Value;
            public override string ToString() => (IsMac ? "MAC   " : "IP    ") + Value;
        }

        public BlacklistForm(Blacklist blacklist)
        {
            _blacklist = blacklist;

            Text = "黑名单管理";
            Size = new Size(470, 400);
            MinimumSize = new Size(380, 300);
            StartPosition = FormStartPosition.CenterParent;
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.None;   // 布局尺寸由 UiScale 按 DPI 统一换算
            if (UiScale.Simulated)                // 仅调试：在 100% 屏幕上模拟高 DPI
                Font = new System.Drawing.Font(Font.FontFamily, Font.Size * UiScale.Factor, Font.Style, Font.Unit);
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var hint = new Label
            {
                Dock = DockStyle.Top,
                Height = 42,
                Padding = new Padding(8, 6, 8, 0),
                ForeColor = System.Drawing.Color.DimGray,
                Text = "黑名单用于拒绝被控端上报（按 MAC 或 IP）。选中条目后点“移除选中”即可删除。"
            };

            _list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, SelectionMode = SelectionMode.MultiExtended };

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(8, 8, 8, 0), WrapContents = false };
            var btnRemove = new Button { Text = "移除选中", Width = 100, Height = 30 };
            var btnClear = new Button { Text = "清空全部", Width = 100, Height = 30 };
            var btnClose = new Button { Text = "关闭", Width = 80, Height = 30 };
            btnRemove.Click += (s, e) => RemoveSelected();
            btnClear.Click += (s, e) => ClearAll();
            btnClose.Click += (s, e) => Close();
            bar.Controls.Add(btnRemove);
            bar.Controls.Add(btnClear);
            bar.Controls.Add(btnClose);

            _lblCount = new Label { Dock = DockStyle.Bottom, Height = 24, Padding = new Padding(10, 4, 0, 0), ForeColor = System.Drawing.Color.DimGray };

            Controls.Add(_list);     // Fill 先加，后加的是外圈
            Controls.Add(bar);
            Controls.Add(_lblCount);
            Controls.Add(hint);

            Reload();

            UiScale.Apply(this);   // 高 DPI：按系统缩放比例换算像素常量
        }

        private void Reload()
        {
            _list.Items.Clear();
            foreach (var mac in _blacklist.Macs) _list.Items.Add(new Entry { IsMac = true, Value = mac });
            foreach (var ip in _blacklist.Ips) _list.Items.Add(new Entry { IsMac = false, Value = ip });
            _lblCount.Text = $"共 {_blacklist.Macs.Count} 条 MAC、{_blacklist.Ips.Count} 条 IP";
        }

        private void RemoveSelected()
        {
            if (_list.SelectedItems.Count == 0)
            {
                MessageBox.Show("请先在列表里选中要移除的条目（可按住 Ctrl 多选）。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var entries = new List<Entry>();
            foreach (var item in _list.SelectedItems) if (item is Entry e) entries.Add(e);

            int removed = 0;
            foreach (var e in entries)
            {
                bool ok = e.IsMac ? _blacklist.RemoveMac(e.Value) : _blacklist.RemoveIp(e.Value);
                if (ok) removed++;
            }
            Reload();
            Changed?.Invoke();
            if (removed > 0) _lblCount.Text += $"　（已移除 {removed} 条）";
        }

        private void ClearAll()
        {
            if (_blacklist.Macs.Count + _blacklist.Ips.Count == 0)
            {
                MessageBox.Show("黑名单已经是空的。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show("确认清空全部黑名单？", "确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;
            _blacklist.Clear();
            Reload();
            Changed?.Invoke();
            _lblCount.Text += "　（已清空）";
        }
    }
}
