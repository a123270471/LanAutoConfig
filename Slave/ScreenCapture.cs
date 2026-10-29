using System.Drawing.Imaging;

namespace LanAutoConfig.Slave
{
    /// <summary>
    /// 截屏（排障用）：把当前桌面截下来压成 JPEG 再 Base64，
    /// 控制端点一下就能看到这台机器卡在哪个界面（登录框/报错框/黑屏）。
    /// 只截“所有屏幕的合并区域”，超过指定宽度等比缩小（一张 1280 宽的 JPEG 约 100~200KB，能塞进一条消息）。
    /// </summary>
    public static class ScreenCapture
    {
        public static (bool Ok, string Base64, int Width, int Height, string Error) Capture(int maxWidth)
        {
            try
            {
                if (maxWidth <= 0) maxWidth = 1280;

                Rectangle bounds = SystemInformation.VirtualScreen;
                if (bounds.Width <= 0 || bounds.Height <= 0)
                    return (false, null, 0, 0, "取不到屏幕尺寸（可能没有交互式桌面）");

                using var full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(full))
                {
                    g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, full.Size, CopyPixelOperation.SourceCopy);
                }

                int outW = full.Width, outH = full.Height;
                if (full.Width > maxWidth)
                {
                    outW = maxWidth;
                    outH = (int)Math.Round(full.Height * (maxWidth / (double)full.Width));
                }

                using var scaled = new Bitmap(outW, outH, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.DrawImage(full, 0, 0, outW, outH);
                }

                using var ms = new MemoryStream();
                var codec = GetJpegCodec();
                if (codec == null) scaled.Save(ms, ImageFormat.Jpeg);
                else
                {
                    using var ps = new EncoderParameters(1);
                    ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 70L);
                    scaled.Save(ms, codec, ps);
                }

                return (true, Convert.ToBase64String(ms.ToArray()), outW, outH, null);
            }
            catch (Exception ex)
            {
                return (false, null, 0, 0, "截屏失败：" + ex.Message);
            }
        }

        private static ImageCodecInfo GetJpegCodec()
        {
            try
            {
                return ImageCodecInfo.GetImageEncoders()
                    .FirstOrDefault(c => string.Equals(c.MimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase));
            }
            catch { return null; }
        }
    }
}
