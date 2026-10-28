namespace LanAutoConfig.Common
{
    /// <summary>
    /// TCP 按行读取（以 '\n' 结束），统一 UTF-8 解码。
    /// 旧实现是“读 1 字节再 (char)buf[0]”，对任何非 ASCII 载荷都会破坏字节流并导致 Check 校验失败、消息被静默丢弃；
    /// 这里改为按字节缓冲后整体用 UTF-8 解码。
    /// </summary>
    public static class TcpLineReader
    {
        /// <summary>
        /// 单行最大字节数。原来取 64KB，但自定义命令的输出（`dir /s`、`systeminfo`、大日志 grep）
        /// 很容易超；超了会被静默截断 → JSON 解析失败 → 接收侧只记一条“无法解析”，
        /// 下发侧却显示“已下发成功”，非常难查。这里放宽到 1MB，并在超限时**明确报错**而不是悄悄截断。
        /// </summary>
        public const int MaxLineBytes = 1024 * 1024;

        /// <summary>单次读取的超时（毫秒）。对端“连上但没发完就断电”时靠它兜底，否则连接会永久占着。</summary>
        private const int ReadTimeoutMs = 15000;

        public static async Task<string> ReadLineAsync(NetworkStream stream)
        {
            using var ms = new MemoryStream(256);
            var buf = new byte[8192];
            while (true)
            {
                int n;
                try
                {
                    // 注意：TcpClient.ReceiveTimeout 对异步 ReadAsync 无效，必须在这里自己加超时
                    n = await stream.ReadAsync(buf, 0, buf.Length)
                                    .WaitAsync(TimeSpan.FromMilliseconds(ReadTimeoutMs))
                                    .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    throw new IOException($"读取消息超时（{ReadTimeoutMs}ms 内没收到完整的一条），已放弃该连接");
                }
                if (n <= 0) break;                       // 对端关闭
                int idx = Array.IndexOf(buf, (byte)'\n', 0, n);
                if (idx >= 0)
                {
                    ms.Write(buf, 0, idx);               // 只取 '\n' 之前的内容
                    break;
                }
                ms.Write(buf, 0, n);
                if (ms.Length > MaxLineBytes)
                    throw new InvalidDataException($"单条消息超过 {MaxLineBytes / 1024}KB 上限，已丢弃（对端发的内容过大）");
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
