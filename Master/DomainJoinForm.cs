using LanAutoConfig.Common;

namespace LanAutoConfig.Master
{
    /// <summary>
    /// 加域 / 工作组设置。这些参数随【下发执行】一起发到被控端，在改完 IP、改完主机名之后执行：
    /// 工作组和加入域二选一（填了域名就以加域为准）。
    ///
    /// 安全提醒：域凭据在局域网内是明文传输的，且会明文保存在控制端配置文件里。
    /// 现场务必使用「只能把计算机加入指定 OU」的专用账号，不要用域管理员。
    /// </summary>
    public class DomainJoinForm : Form
    {
        private readonly TextBox _txtWorkgroup, _txtDomain, _txtUser, _txtPassword, _txtOu;
        private readonly CheckBox _chkDomain;

        public string Workgroup => string.IsNullOrWhiteSpace(_txtWorkgroup.Text) ? null : _txtWorkgroup.Text.Trim();
        public string DomainName => _chkDomain.Checked && !string.IsNullOrWhiteSpace(_txtDomain.Text) ? _txtDomain.Text.Trim() : null;
        public string DomainUser => DomainName == null ? null : _txtUser.Text.Trim();
        public string DomainPassword => DomainName == null ? null : _txtPassword.Text;
        public string OuPath => string.IsNullOrWhiteSpace(_txtOu.Text) ? null : _txtOu.Text.Trim();

        public DomainJoinForm(string workgroup, string domain, string user, string password, string ou)
        {
            Text = "加域 / 工作组设置";
            Size = new Size(600, 400);
            MinimumSize = new Size(520, 380);
            StartPosition = FormStartPosition.CenterParent;
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.None;

            var tbl = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 7,
                Padding = new Padding(16, 14, 16, 8)
            };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 6; i++) tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            tbl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _txtWorkgroup = new TextBox { Dock = DockStyle.Fill, Text = workgroup ?? "" };
            _chkDomain = new CheckBox { Text = "执行配置时加入域（域里有域控才用）", AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            _txtDomain = new TextBox { Dock = DockStyle.Fill, Text = domain ?? "", Enabled = domain != null };
            _txtUser = new TextBox { Dock = DockStyle.Fill, Text = user ?? "", Enabled = domain != null };
            _txtPassword = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true, Text = password ?? "", Enabled = domain != null };
            _txtOu = new TextBox { Dock = DockStyle.Fill, Text = ou ?? "", Enabled = domain != null };

            _chkDomain.Checked = !string.IsNullOrEmpty(domain);
            _chkDomain.CheckedChanged += (s, e) =>
            {
                bool on = _chkDomain.Checked;
                _txtDomain.Enabled = _txtUser.Enabled = _txtPassword.Enabled = _txtOu.Enabled = on;
                if (on) _txtWorkgroup.Enabled = false;
                else _txtWorkgroup.Enabled = true;
            };
            _txtWorkgroup.Enabled = !_chkDomain.Checked;

            AddRow(tbl, 0, "工作组", _txtWorkgroup);
            tbl.Controls.Add(_chkDomain, 0, 1);
            tbl.SetColumnSpan(_chkDomain, 2);
            AddRow(tbl, 2, "域名", _txtDomain);
            AddRow(tbl, 3, "域账号", _txtUser);
            AddRow(tbl, 4, "域密码", _txtPassword);
            AddRow(tbl, 5, "目标 OU（可空）", _txtOu);

            var hint = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                ForeColor = System.Drawing.Color.FromArgb(150, 60, 0),
                Text = "说明：\r\n" +
                       "• 加域在“改 IP + 改主机名”之后执行，重启后生效；主机名要先改好，域里才会按新名字建计算机对象。\r\n" +
                       "• 域密码在局域网内明文传输，并明文保存在控制端配置里。请使用只能把机器加入指定 OU 的专用低权限账号。\r\n" +
                       "• 只想改工作组就勾掉“加入域”，只填工作组名。两项都留空表示不动。"
            };
            tbl.Controls.Add(hint, 0, 6);
            tbl.SetColumnSpan(hint, 2);

            Controls.Add(tbl);

            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 48,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 8, 16, 0)
            };
            var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Width = 90, Height = 30 };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 90, Height = 30 };
            bar.Controls.Add(ok);
            bar.Controls.Add(cancel);
            Controls.Add(bar);
            AcceptButton = ok;
            CancelButton = cancel;

            UiScale.Apply(this);
        }

        private static void AddRow(TableLayoutPanel t, int row, string label, Control input)
        {
            t.Controls.Add(new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                AutoSize = false
            }, 0, row);
            t.Controls.Add(input, 1, row);
        }
    }
}
