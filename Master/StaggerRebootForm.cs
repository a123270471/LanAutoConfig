using System.Windows.Forms;
using LanAutoConfig.Common;

namespace LanAutoConfig.Master
{
    /// <summary>分批错峰重启参数对话框：每批台数 + 批次间隔秒数。</summary>
    public class StaggerRebootForm : Form
    {
        private readonly NumericUpDown _numBatch;
        private readonly NumericUpDown _numInterval;
        private readonly Label _lblHint;

        public int BatchSize => (int)_numBatch.Value;
        public int IntervalSeconds => (int)_numInterval.Value;

        public StaggerRebootForm(int onlineCount)
        {
            Text = "分批错峰重启";
            Size = new System.Drawing.Size(380, 230);
            StartPosition = FormStartPosition.CenterParent;
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Padding = new Padding(14) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 4; i++) root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

            root.Controls.Add(new Label { Text = "每批台数：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            _numBatch = new NumericUpDown { Minimum = 1, Maximum = 1000, Value = 5, Width = 90 };
            root.Controls.Add(_numBatch, 1, 0);

            root.Controls.Add(new Label { Text = "批次间隔(秒)：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            _numInterval = new NumericUpDown { Minimum = 1, Maximum = 600, Value = 10, Width = 90 };
            root.Controls.Add(_numInterval, 1, 1);

            _lblHint = new Label
            {
                Text = $"将分多批重启（当前选中 {onlineCount} 台；未选中则全部在线）。每台重启后延迟 10 秒执行，上线后控制端会自动验收。",
                Dock = DockStyle.Fill, AutoSize = true
            };
            root.SetColumnSpan(_lblHint, 2);
            root.Controls.Add(_lblHint, 0, 2);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0), WrapContents = false };
            var btnOk = new Button { Text = "开始分批重启", Width = 130, Height = 30, DialogResult = DialogResult.OK };
            var btnCancel = new Button { Text = "取消", Width = 90, Height = 30, DialogResult = DialogResult.Cancel };
            bar.Controls.Add(btnOk); bar.Controls.Add(btnCancel);
            root.Controls.Add(bar, 0, 3);
            root.SetColumnSpan(bar, 2);

            Controls.Add(root);
            AcceptButton = btnOk;
            CancelButton = btnCancel;
            UiScale.Apply(this);
        }
    }
}
