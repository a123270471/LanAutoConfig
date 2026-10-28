using System.Diagnostics;
using System.Windows.Forms;
using LanAutoConfig.Common;

namespace LanAutoConfig.Master
{
    /// <summary>资产标签打印对话框：选范围 + 每行标签数，生成 HTML 并用浏览器打开打印。</summary>
    public class LabelPrintForm : Form
    {
        private readonly MasterForm _master;
        private readonly RadioButton _rdoSelected, _rdoOnline, _rdoAll;
        private readonly ComboBox _cmbPerRow;
        private readonly Label _lblStatus;

        public LabelPrintForm(MasterForm master)
        {
            _master = master;
            Text = "生成资产标签 / 打印";
            Size = new System.Drawing.Size(420, 240);
            StartPosition = FormStartPosition.CenterParent;
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(14) };
            for (int i = 0; i < 4; i++) root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var grp = new GroupBox { Text = "范围", Dock = DockStyle.Fill };
            var p = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 4), WrapContents = false };
            _rdoSelected = new RadioButton { Text = "选中终端", AutoSize = true, Margin = new Padding(0, 4, 14, 0) };
            _rdoOnline = new RadioButton { Text = "全部在线", AutoSize = true, Margin = new Padding(0, 4, 14, 0) };
            _rdoAll = new RadioButton { Text = "全部终端", AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
            _rdoOnline.Checked = true;
            p.Controls.Add(_rdoSelected); p.Controls.Add(_rdoOnline); p.Controls.Add(_rdoAll);
            grp.Controls.Add(p);
            root.Controls.Add(grp, 0, 0);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30, Padding = new Padding(0), WrapContents = false };
            bar.Controls.Add(new Label { Text = "每行标签数：", AutoSize = true, Margin = new Padding(0, 5, 4, 0) });
            _cmbPerRow = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90, Margin = new Padding(0, 2, 0, 0) };
            _cmbPerRow.Items.Add("2"); _cmbPerRow.Items.Add("3"); _cmbPerRow.Items.Add("4");
            _cmbPerRow.SelectedIndex = 1;
            bar.Controls.Add(_cmbPerRow);
            root.Controls.Add(bar, 0, 1);

            var btnGen = new Button { Text = "生成 HTML 并打开", Width = 160, Height = 28 };
            btnGen.Click += (s, e) => Generate();
            root.Controls.Add(btnGen, 0, 2);

            _lblStatus = new Label { Dock = DockStyle.Fill, AutoSize = true, Text = "生成后会在默认浏览器打开，用“打印”输出到标签纸 / A4。" };
            root.Controls.Add(_lblStatus, 0, 3);

            Controls.Add(root);
            UiScale.Apply(this);
        }

        private List<SlaveSessionItem> PickTargets()
        {
            if (_rdoSelected.Checked) return _master.GetSelectedItems();
            if (_rdoAll.Checked) return _master.GetAllItems();
            return _master.GetAllItems().Where(x => x.Online).ToList();
        }

        private void Generate()
        {
            var targets = PickTargets();
            if (targets.Count == 0) { _lblStatus.Text = "当前范围没有可导出的终端。"; return; }
            var labels = targets.Select(it => new LabelItem
            {
                HostName = it.TargetHostName ?? "",
                Ip = it.TargetIp ?? (it.IsDhcp ? "DHCP" : ""),
                Mac = it.Mac ?? "",
                Serial = it.Hardware?.SerialNumber ?? ""
            }).ToList();
            int perRow = int.Parse(_cmbPerRow.SelectedItem.ToString());
            string html = LabelSheetGenerator.GenerateHtml(labels, perRow, "局域网资产标签");
            string path = Path.Combine(Path.GetTempPath(), "LanAutoConfig_labels.html");
            try
            {
                File.WriteAllText(path, html, Encoding.UTF8);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                _lblStatus.Text = $"已生成 {labels.Count} 张标签并打开浏览器，请选择标签纸后打印。";
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "打开失败：" + ex.Message;
            }
        }
    }
}
