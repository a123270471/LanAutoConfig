using System;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LanAutoConfig.Common
{
    /// <summary>
    /// 高 DPI（Windows「缩放与布局」125% / 150% …）适配工具。
    ///
    /// 背景：把系统缩放调大后，字体按 DPI 自动放大，但代码里写死的像素常量
    /// （行高、控件宽高、内边距）不会跟着变，界面就会文字压文字、控件被裁掉。
    /// 这里在界面构建完成后，按 DPI 比例统一换算这些常量。
    ///
    /// - 字号不用管：Font 以“点”为单位，系统会按 DPI 自动放大，这里再乘一次就会过大；
    /// - 100% 缩放时 <see cref="Factor"/> = 1，<see cref="Apply"/> 什么都不做，界面与原来完全一致；
    /// - 调试用环境变量 <c>LANAC_UI_SCALE=1.25/1.5</c>：在 100% 的屏幕上模拟高 DPI 布局
    ///   （此时连字体一起放大，效果等价于真的把系统缩放调到该比例）。
    /// </summary>
    public static class UiScale
    {
        /// <summary>当前缩放比例（1 = 100%）。</summary>
        public static float Factor { get; private set; } = 1f;

        /// <summary>是否由 LANAC_UI_SCALE 模拟（仅调试）。</summary>
        public static bool Simulated { get; private set; }

        public static bool IsScaled => Factor > 1.0001f;

        private static bool _inited;

        /// <summary>程序启动时调用一次（须在 SetHighDpiMode 之后、建窗体之前）。</summary>
        public static void Init()
        {
            if (_inited) return;
            _inited = true;

            float f = DetectDpi() / 96f;

            string env = Environment.GetEnvironmentVariable("LANAC_UI_SCALE");
            if (float.TryParse(env, NumberStyles.Float, CultureInfo.InvariantCulture, out float k)
                && k >= 0.5f && k <= 4f)
            {
                f = k;
                Simulated = true;
            }

            Factor = Math.Min(3f, Math.Max(1f, f));
        }

        private static float DetectDpi()
        {
            try
            {
                uint dpi = GetDpiForSystem();
                if (dpi >= 72 && dpi <= 480) return dpi;
            }
            catch { }
            try
            {
                using (var g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX;
            }
            catch { }
            return 96f;
        }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();

        /// <summary>换算单个像素值。</summary>
        public static int S(int px) => (int)Math.Round(px * Factor, MidpointRounding.AwayFromZero);

        /// <summary>换算内边距。</summary>
        public static Padding P(int left, int top, int right, int bottom)
            => new Padding(S(left), S(top), S(right), S(bottom));

        /// <summary>按比例缩放一整棵控件树的布局尺寸（幂等：Scaled 标记保证只缩放一次）。</summary>
        public static void Apply(Control root)
        {
            if (root == null || !IsScaled) return;
            if (root.Tag is string t && t == "__uiscaled__") return;
            root.Tag = "__uiscaled__";
            ScaleTree(root, root.Font);
            root.PerformLayout();
        }

        private static void ScaleTree(Control c, Font parentFont)
        {
            // 仅调试模式需要手动放大字体；真实 DPI 下字体已按点值由系统放大
            if (Simulated && !ReferenceEquals(c.Font, parentFont))
                c.Font = ScaleFont(c.Font);

            c.Padding = ScalePadding(c.Padding);

            if (c is TableLayoutPanel tlp)
            {
                foreach (ColumnStyle cs in tlp.ColumnStyles)
                    if (cs.SizeType == SizeType.Absolute && cs.Width > 0)
                        cs.Width = S((int)Math.Round(cs.Width));
                foreach (RowStyle rs in tlp.RowStyles)
                    if (rs.SizeType == SizeType.Absolute && rs.Height > 0)
                        rs.Height = S((int)Math.Round(rs.Height));
            }

            if (c is Form form)
            {
                var want = new Size(S(form.Width), S(form.Height));
                var min = new Size(S(form.MinimumSize.Width), S(form.MinimumSize.Height));

                Rectangle wa = Rectangle.Empty;
                try { wa = Screen.PrimaryScreen.WorkingArea; } catch { }
                if (wa.Width > 0 && wa.Height > 0)
                {
                    int maxW = Math.Max(320, wa.Width - S(16));
                    int maxH = Math.Max(240, wa.Height - S(16));
                    want = new Size(Math.Min(want.Width, maxW), Math.Min(want.Height, maxH));
                    min = new Size(Math.Min(min.Width, maxW), Math.Min(min.Height, maxH));
                }
                // 先给 Size 再给 MinimumSize，且最小值不得超过目标尺寸，
                // 否则“最小值放大后”会把窗体强行撑大（旧实现就踩了这个坑）。
                min = new Size(Math.Min(min.Width, want.Width), Math.Min(min.Height, want.Height));
                form.MinimumSize = min;
                form.Size = want;
            }

            foreach (Control child in c.Controls)
            {
                child.Margin = ScalePadding(child.Margin);

                if (child is TabPage || child is TabControl) { /* 由 TabControl 自己排 */ }
                else if (child.AutoSize)
                {
                    // AutoSize 的标签/复选框由文字撑开，不能再改尺寸；
                    // 但 ComboBox / TextBox 虽然 AutoSize，宽度仍是我们写死的，需要按比例放大。
                    bool textDriven = child is Label || child is LinkLabel
                                      || child is CheckBox || child is RadioButton;
                    if (!textDriven && child.Dock == DockStyle.None)
                        child.Width = S(child.Width);
                }
                else
                {
                    switch (child.Dock)
                    {
                        case DockStyle.None:
                            child.Location = new Point(S(child.Left), S(child.Top));
                            child.Size = new Size(S(child.Width), S(child.Height));
                            break;
                        case DockStyle.Top:
                        case DockStyle.Bottom:
                            child.Height = S(child.Height);
                            break;
                        case DockStyle.Left:
                        case DockStyle.Right:
                            child.Width = S(child.Width);
                            break;
                        // DockStyle.Fill：尺寸由父容器决定，不用改
                    }
                }

                ScaleTree(child, c.Font);
            }
        }

        private static Font ScaleFont(Font f)
        {
            try
            {
                float size = f.Size * Factor;
                if (size < 1f || size > 200f) return f;
                return new Font(f.FontFamily, size, f.Style, f.Unit, f.GdiCharSet);
            }
            catch { return f; }
        }

        private static Padding ScalePadding(Padding p)
            => new Padding(S(p.Left), S(p.Top), S(p.Right), S(p.Bottom));
    }
}
