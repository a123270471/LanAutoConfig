namespace LanAutoConfig.Common
{
    /// <summary>
    /// TCP 按行读取（以 '\n' 结束），统一 UTF-8 解码。
    /// 旧实现是“读 1 字节再 (char)buf[0]”，对任何非 ASCII 载荷都会破坏字节流并导致 Check 校验失败、消息被静默丢弃；
    /// 这里改为按字节缓冲后整体用 UTF-8 解码。
    /// </summary>
    public static class TcpLineReader
    {
        /// <summary>单行最大字节数，超出即截断（防止恶意超长行耗尽内存）。</summary>
        public const int MaxLineBytes = 65536;

        public static async Task<string> ReadLineAsync(NetworkStream stream)
        {
            using var ms = new MemoryStream(256);
            var buf = new byte[4096];
            while (true)
            {
                int n = await stream.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false);
                if (n <= 0) break;                       // 对端关闭
                int idx = Array.IndexOf(buf, (byte)'\n', 0, n);
                if (idx >= 0)
                {
                    ms.Write(buf, 0, idx);               // 只取 '\n' 之前的内容
                    break;
                }
                ms.Write(buf, 0, n);
                if (ms.Length > MaxLineBytes) break;
            }

            byte[] raw = ms.ToArray();
            int len = raw.Length;
            // 去掉尾部的 \r / 空白，避免 JSON 解析被干扰
            while (len > 0 && (raw[len - 1] == (byte)'\r' || raw[len - 1] == (byte)' ' ||
                               raw[len - 1] == (byte)'\t' || raw[len - 1] == (byte)'\n')) len--;
            int start = 0;
            // 去掉可能的 UTF-8 BOM
            if (len >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF) start = 3;
            if (len <= start) return "";
            return Encoding.UTF8.GetString(raw, start, len - start).Trim();
        }
    }
}
