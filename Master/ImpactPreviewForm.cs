using LanAutoConfig.Common;

namespace LanAutoConfig.Master
{
    /// <summary>
    /// 影响预览：下发/重启/关机前，把“会动哪些机器、有几台离线、几台冲突、几台会被跳过”先摆出来。
    /// 现场一次操作几十上百台，点错一次的代价很大（改错 IP 的机器要重新跑一趟），
    /// 所以关键操作都要先看一眼这张表。
    /// </summary>
    public class ImpactPreviewForm : Form
    {
        public ImpactPreviewForm(string operation, string description,
            IReadOnlyList<SlaveSessionItem> items, string extraNote = null)
        {
            var list = items ?? new List<SlaveSessionItem>();
            Text = "影响预览 - " + operation;
            Size = new Size(820, 560);
            MinimumSize = new Size(620, 400);
            StartPosition = FormStartPosition.CenterParent;
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.None;

            var confIps = ConflictValidator.GetConflictIps(list);
            var confHosts = ConflictValidator.GetConflictHosts(list);
            int online = list.Count(x => x.Online);
            int offline = list.Count - online;
            int conflict = list.Count(x => confIps.Contains(x.TargetIp ?? "") || confHosts.Contains(x.TargetHostName ?? ""));
            int skipped = list.Count(x => x.Skip);
            int noToken = list.Count(x => string.IsNullOrEmpty(x.AuthToken));
            int noAddr = list.Count(x => string.IsNullOrEmpty(x.SourceIp) && string.IsNullOrEmpty(x.TargetIp));

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12) };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));   // 操作名
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));   // 说明
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));   // 统计
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 表格

            root.Controls.Add(new Label
            {
                Text = "即将执行：" + operation,
                Dock = DockStyle.Fill,
                Font = new System.Drawing.Font("Microsoft YaHei UI", 11F, System.Drawing.FontStyle.Bold),
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft
            }, 0, 0);

            root.Controls.Add(new Label
            {
                Text = description ?? "",
                Dock = DockStyle.Fill,
                ForeColor = System.Drawing.Color.DimGray,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft
            }, 0, 1);

            var stat = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                Text = $"共 {list.Count} 台：在线 {online}，离线 {offline}；冲突 {conflict}；已跳过 {skipped}" +
                       (noToken > 0 ? $"；尚未取得令牌 {noToken}（等心跳）" : "") +
                       (noAddr > 0 ? $"；无可用地址 {noAddr}" : "") +
                       (string.IsNullOrEmpty(extraNote) ? "" : Environment.NewLine + extraNote)
            };
            if (conflict > 0 || noAddr > 0) stat.ForeColor = System.Drawing.Color.FromArgb(197, 15, 31);
            root.Controls.Add(stat, 0, 2);

            var grid = new DataGridView
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
            grid.Columns.Add("host", "目标主机名");
            grid.Columns.Add("ip", "目标IP");
            grid.Columns.Add("src", "当前来源IP");
            grid.Columns.Add("status", "状态");
            grid.Columns.Add("online", "在线");
            grid.Columns.Add("note", "备注");
            grid.Columns["online"].FillWeight = 45;
            grid.Columns["note"].FillWeight = 160;

            foreach (var it in list)
            {
                var notes = new List<string>();
                if (confIps.Contains(it.TargetIp ?? "") || confHosts.Contains(it.TargetHostName ?? "")) notes.Add("IP/主机名冲突");
                if (it.Skip) notes.Add("已标记跳过");
                if (string.IsNullOrEmpty(it.AuthToken)) notes.Add("待取令牌");
                if (string.IsNullOrEmpty(it.SourceIp) && string.IsNullOrEmpty(it.TargetIp)) notes.Add("无可用地址");
                if (!it.Online) notes.Add("离线");

                int i = grid.Rows.Add(it.TargetHostName ?? "(未上报)", it.TargetIp ?? "", it.SourceIp ?? "",
                    it.StatusDisplay, it.OnlineDisplay, string.Join("，", notes));
                if (notes.Contains("IP/主机名冲突")) grid.Rows[i].DefaultCellStyle.BackColor = System.Drawing.Color.LightSalmon;
                else if (!it.Online) grid.Rows[i].DefaultCellStyle.ForeColor = System.Drawing.Color.Gray;
            }
            root.Controls.Add(grid, 0, 3);
            Controls.Add(root);

            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 48,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 8, 12, 0)
            };
            var btnOk = new Button { Text = "确认执行", DialogResult = DialogResult.OK, Width = 110, Height = 30 };
            btnOk.FlatStyle = FlatStyle.Flat;
            btnOk.FlatAppearance.BorderSize = 0;
            btnOk.BackColor = System.Drawing.Color.FromArgb(0, 120, 212);
            btnOk.ForeColor = System.Drawing.Color.White;
            btnOk.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Bold);
            var btnCancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 90, Height = 30 };
            bar.Controls.Add(btnOk);
            bar.Controls.Add(btnCancel);
            Controls.Add(bar);

            AcceptButton = btnOk;
            CancelButton = btnCancel;
            UiScale.Apply(this);
        }
    }
}
