using System.Windows.Forms;
using LanAutoConfig.Common;

namespace LanAutoConfig.Master
{
    /// <summary>
    /// 远程截图：让被控端截一张当前桌面回传，用来判断它卡在哪个界面（登录框、报错框、桌面没起来）。
    /// 图片只放内存 + 手动另存，不自动落盘（截图里可能有密码、聊天窗口之类的敏感内容）。
    /// </summary>
    public class ScreenshotForm : Form
    {
        private readonly MasterForm _master;

        private readonly ListBox _list;
        private readonly PictureBox _pic;
        private readonly NumericUpDown _numWidth;
        private readonly Label _lblStatus;
        private readonly Button _btnShotOne, _btnShotAll, _btnSave;

        private readonly List<SlaveSessionItem> _items = new List<SlaveSessionItem>();
        /// <summary>已收到的截图（MAC → 图片）。换目标时从缓存里取，不用重拍。</summary>
        private readonly Dictionary<string, Image> _images = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        private readonly Action<SlaveScreenshotResultMsg> _onShot;

        public ScreenshotForm(MasterForm master)
        {
            _master = master;
            Text = "远程截图";
            Size = new Size(900, 640);
            MinimumSize = new Size(760, 520);
            StartPosition = FormStartPosition.CenterParent;
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false;
            MinimizeBox = false;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

            // ---- 工具条 ----
            var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 4, 0, 4) };
            bar.Controls.Add(new Label { Text = "最大宽度(像素)：", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 4, 0) });
            _numWidth = new NumericUpDown { Minimum = 320, Maximum = 3840, Value = 1600, Width = 80, Increment = 160, Margin = new Padding(0, 5, 10, 0) };
            bar.Controls.Add(_numWidth);
            _btnShotOne = new Button { Text = "截取这张", Width = 100, Height = 30 };
            _btnShotOne.Click += (s, e) => ShotOne();
            _btnShotAll = new Button { Text = "全部在线各截一张", Width = 150, Height = 30 };
            _btnShotAll.Click += (s, e) => ShotAll();
            _btnSave = new Button { Text = "另存为…", Width = 100, Height = 30, Enabled = false };
            _btnSave.Click += (s, e) => SaveCurrent();
            bar.Controls.Add(_btnShotOne);
            bar.Controls.Add(_btnShotAll);
            bar.Controls.Add(_btnSave);
            root.Controls.Add(bar, 0, 0);

            // ---- 左列表 + 右图片 ----
            var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 220, FixedPanel = FixedPanel.Panel1, BorderStyle = BorderStyle.FixedSingle };
            _list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None };
            _list.SelectedIndexChanged += (s, e) => ShowCurrent();
            split.Panel1.Controls.Add(_list);

            _pic = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = System.Drawing.Color.FromArgb(30, 30, 30)
            };
            split.Panel2.Controls.Add(_pic);
            root.Controls.Add(split, 0, 1);

            _lblStatus = new Label
            {
                Text = "选一台，然后点【截取这张】。图片只在内存里，需要留存用【另存为】。",
                Dock = DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                ForeColor = System.Drawing.Color.DimGray
            };
            root.Controls.Add(_lblStatus, 0, 2);

            Controls.Add(root);
            UiScale.Apply(this);

            _onShot = OnShot;
            _master.RegisterScreenshotHandler(_onShot);

            LoadTargets();
        }

        private void LoadTargets()
        {
            _items.Clear();
            _list.Items.Clear();
            foreach (var it in _master.GetAllItems()
                         .OrderByDescending(x => x.Online)
                         .ThenBy(x => x.TargetHostName))
            {
                _items.Add(it);
                _list.Items.Add(Describe(it));
            }
            int online = _items.Count(x => x.Online);
            _lblStatus.Text = $"共 {_items.Count} 台，在线 {online} 台。选一台点【截取这张】。";
            if (_list.Items.Count > 0) _list.SelectedIndex = 0;
            _btnShotOne.Enabled = _items.Count > 0;
        }

        private static string Describe(SlaveSessionItem it)
        {
            string name = string.IsNullOrWhiteSpace(it.TargetHostName) ? it.Mac : it.TargetHostName;
            string ip = string.IsNullOrEmpty(it.SourceIp) ? "?" : it.SourceIp;
            return $"{name}  {ip}{(it.Online ? "" : "（离线）")}";
        }

        private SlaveSessionItem Current()
        {
            int i = _list.SelectedIndex;
            if (i < 0 || i >= _items.Count) return null;
            return _items[i];
        }

        private void ShotOne()
        {
            var it = Current();
            if (it == null) { MessageBox.Show(this, "先选一台终端。", "提示"); return; }
            if (!it.Online)
            {
                if (MessageBox.Show(this, $"{Describe(it)} 当前离线，指令可能发不到。还会试吗？", "离线",
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            }
            int w = (int)_numWidth.Value;
            if (_master.RequestScreenshot(it, w, out string err))
                _lblStatus.Text = $"已请求 {Describe(it)} 截图，等它回传…";
            else
                _lblStatus.Text = $"请求失败：{err}";
        }

        private void ShotAll()
        {
            var targets = _items.Where(x => x.Online).ToList();
            if (targets.Count == 0) { MessageBox.Show(this, "没有在线终端。", "提示"); return; }
            if (MessageBox.Show(this, $"对 {targets.Count} 台在线终端各截一张？\n\n截图只是桌面画面，不会干扰对方操作。",
                    "远程截图", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            int w = (int)_numWidth.Value;
            int ok = 0;
            foreach (var it in targets)
                if (_master.RequestScreenshot(it, w, out _)) ok++;
            _lblStatus.Text = $"已请求 {ok}/{targets.Count} 台截图，收到后左侧列表会打上 ● 标记。";
        }

        private void OnShot(SlaveScreenshotResultMsg m)
        {
            if (IsDisposed) return;
            if (!m.Success)
            {
                _lblStatus.Text = $"{m.LocalMac} 截图失败：{m.Message}";
                return;
            }
            Image img = null;
            try
            {
                byte[] data = Convert.FromBase64String(m.Base64);
                using var ms = new MemoryStream(data);
                img = Image.FromStream(ms);
            }
            catch (Exception ex)
            {
                _lblStatus.Text = $"{m.LocalMac} 回传的图片解不开：{ex.Message}";
                return;
            }

            if (_images.TryGetValue(m.LocalMac, out var old)) { try { old.Dispose(); } catch { } }
            _images[m.LocalMac] = img;
            MarkReceived(m.LocalMac);
            _lblStatus.Text = $"已收到 {m.LocalMac} 的截图（{m.Width}x{m.Height}）共 {_images.Count} 张";

            var cur = Current();
            if (cur != null && string.Equals(cur.Mac, m.LocalMac, StringComparison.OrdinalIgnoreCase))
                _pic.Image = img;
        }

        private void MarkReceived(string mac)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (!string.Equals(_items[i].Mac, mac, StringComparison.OrdinalIgnoreCase)) continue;
                string text = "● " + Describe(_items[i]);
                if (!Equals(_list.Items[i], text)) _list.Items[i] = text;
                break;
            }
        }

        private void ShowCurrent()
        {
            var it = Current();
            if (it == null) { _pic.Image = null; _btnSave.Enabled = false; return; }
            if (_images.TryGetValue(it.Mac, out var img))
            {
                _pic.Image = img;
                _btnSave.Enabled = true;
            }
            else
            {
                _pic.Image = null;
                _btnSave.Enabled = false;
            }
        }

        private void SaveCurrent()
        {
            var it = Current();
            if (it == null || !_images.TryGetValue(it.Mac, out var img)) return;
            string safe = string.Concat((it.TargetHostName ?? it.Mac).Split(Path.GetInvalidFileNameChars()));
            using var dlg = new SaveFileDialog
            {
                Title = "保存截图",
                FileName = $"{safe}_{DateTime.Now:yyyyMMdd_HHmmss}.png",
                Filter = "PNG 图片 (*.png)|*.png|JPEG 图片 (*.jpg)|*.jpg"
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                img.Save(dlg.FileName, dlg.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                    ? System.Drawing.Imaging.ImageFormat.Jpeg
                    : System.Drawing.Imaging.ImageFormat.Png);
                _lblStatus.Text = "已保存：" + dlg.FileName;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存失败：" + ex.Message, "截图", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _master.UnregisterScreenshotHandler(_onShot);
            _pic.Image = null;
            foreach (var kv in _images) { try { kv.Value.Dispose(); } catch { } }
            _images.Clear();
            base.OnFormClosing(e);
        }
    }
}
