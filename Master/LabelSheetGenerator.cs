using System.Text;

namespace LanAutoConfig.Master
{
    /// <summary>单张资产标签的数据。</summary>
    public class LabelItem
    {
        public string HostName { get; set; }
        public string Ip { get; set; }
        public string Mac { get; set; }
        public string Serial { get; set; }
    }

    /// <summary>
    /// 资产标签网页生成器：生成可在浏览器里直接打印的 HTML（A4 + 多列网格）。
    /// 用浏览器“打印”即可输出到标签纸或 A4；不依赖任何第三方库 / 打印驱动。
    /// </summary>
    public static class LabelSheetGenerator
    {
        /// <summary>
        /// 生成标签网页 HTML。
        /// perRow：每行标签数（2 / 3 / 4 常用）。
        /// title：页眉标题（可为空）。
        /// </summary>
        public static string GenerateHtml(IEnumerable<LabelItem> items, int perRow, string title)
        {
            var list = items?.ToList() ?? new List<LabelItem>();
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
            sb.AppendLine("<title>资产标签</title>");
            sb.AppendLine("<style>");
            sb.AppendLine("  * { box-sizing: border-box; }");
            sb.AppendLine("  body { font-family: 'Microsoft YaHei UI', 'Segoe UI', sans-serif; margin: 0; padding: 8mm; color: #111; }");
            if (!string.IsNullOrEmpty(title))
                sb.AppendLine($"  h1 {{ font-size: 13pt; margin: 0 0 6mm 0; }}");
            // 标签网格：用 CSS grid 均分一行
            sb.AppendLine($"  .sheet {{ display: grid; grid-template-columns: repeat({perRow}, 1fr); gap: 4mm; }}");
            sb.AppendLine("  .label {");
            sb.AppendLine("    border: 1px solid #333; border-radius: 4px; padding: 3mm 4mm;");
            sb.AppendLine("    min-height: 24mm; display: flex; flex-direction: column; justify-content: center;");
            sb.AppendLine("    page-break-inside: avoid; break-inside: avoid;");
            sb.AppendLine("  }");
            sb.AppendLine("  .hn { font-size: 15pt; font-weight: 700; color: #0a3d91; word-break: break-all; }");
            sb.AppendLine("  .row { font-size: 9.5pt; margin-top: 1.5mm; word-break: break-all; }");
            sb.AppendLine("  .k { color: #666; }");
            sb.AppendLine("  .mac { font-family: Consolas, 'Courier New', monospace; }");
            sb.AppendLine("  @media print { body { padding: 6mm; } h1 { display: none; } }");
            sb.AppendLine("</style></head><body>");
            if (!string.IsNullOrEmpty(title))
                sb.AppendLine($"  <h1>{Escape(title)}</h1>");
            sb.AppendLine("  <div class=\"sheet\">");
            if (list.Count == 0)
            {
                sb.AppendLine("    <div class=\"label\"><div class=\"hn\">（没有可导出的终端）</div></div>");
            }
            foreach (var it in list)
            {
                sb.AppendLine("    <div class=\"label\">");
                sb.AppendLine($"      <div class=\"hn\">{Escape(it.HostName)}</div>");
                sb.AppendLine($"      <div class=\"row\"><span class=\"k\">IP：</span>{Escape(it.Ip)}</div>");
                sb.AppendLine($"      <div class=\"row mac\"><span class=\"k\">MAC：</span>{Escape(it.Mac)}</div>");
                if (!string.IsNullOrEmpty(it.Serial))
                    sb.AppendLine($"      <div class=\"row\"><span class=\"k\">SN：</span>{Escape(it.Serial)}</div>");
                sb.AppendLine("    </div>");
            }
            sb.AppendLine("  </div>");
            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }
    }
}
