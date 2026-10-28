using System;
using System.Runtime.InteropServices;
using System.Text;

namespace LanAutoConfig.Common
{
    /// <summary>
    /// 控制台程序输出解码。
    ///
    /// 背景：cmd.exe 及绝大多数命令行工具把输出写到控制台时用的是「OEM 代码页」
    /// （简体中文系统是 936/GBK），而不是 UTF-8。之前统一按 UTF-8 解码，
    /// 中文就成了乱码（"锟斤拷"、"?" 之类），被控端日志、控制端输出列全跟着烂。
    ///
    /// 这里改成「先按严格 UTF-8 试解，失败再按本机 OEM 代码页解」：
    /// - 现代工具（powershell、git、python 等按 UTF-8 输出的）走第一条分支；
    /// - cmd 内建命令 / ipconfig / netsh / systeminfo 等走 OEM 分支；
    /// - 纯 ASCII 两种情况结果一致，不影响英文输出。
    /// </summary>
    public static class ConsoleText
    {
        [DllImport("kernel32.dll")]
        private static extern uint GetOEMCP();

        [DllImport("kernel32.dll")]
        private static extern uint GetACP();

        private static Encoding _oem;
        private static Encoding _ansi;
        private static bool _providerTried;

        /// <summary>注册代码页提供程序（.NET Core 默认只带 UTF-8/UTF-16/ASCII，936 之类需要它）。</summary>
        private static void EnsureProvider()
        {
            if (_providerTried) return;
            _providerTried = true;
            try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
        }

        /// <summary>本机 OEM 代码页编码（cmd.exe 控制台输出用的就是这个）。</summary>
        public static Encoding Oem
        {
            get
            {
                if (_oem != null) return _oem;
                EnsureProvider();
                try { _oem = Encoding.GetEncoding((int)GetOEMCP()); }
                catch { _oem = Encoding.UTF8; }
                return _oem;
            }
        }

        /// <summary>本机 ANSI 代码页编码（部分老工具用这个；OEM 解不出来时兜底）。</summary>
        public static Encoding Ansi
        {
            get
            {
                if (_ansi != null) return _ansi;
                EnsureProvider();
                try { _ansi = Encoding.GetEncoding((int)GetACP()); }
                catch { _ansi = Encoding.UTF8; }
                return _ansi;
            }
        }

        /// <summary>
        /// 把命令行程序输出的原始字节解码成字符串（自动识别 UTF-8 / OEM / ANSI）。
        /// 用字节流而不是 StreamReader，就是为了在这里做二次判定。
        /// </summary>
        public static string Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return "";

            // 带 BOM 的一律按 BOM 走
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

            // 纯 ASCII：两种解码结果一样，直接返回
            bool ascii = true;
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] >= 0x80) { ascii = false; break; }
            }
            if (ascii) return Encoding.ASCII.GetString(bytes);

            // 严格 UTF-8（遇非法字节抛异常，不产生替换字符）
            try
            {
                var s = new UTF8Encoding(false, true).GetString(bytes);
                if (s.IndexOf('\uFFFD') < 0) return s;
            }
            catch { }

            // 退回本机 OEM 代码页（中文系统 936）
            try
            {
                var s = Oem.GetString(bytes);
                if (s.IndexOf('\uFFFD') < 0) return s;
            }
            catch { }

            try { return Ansi.GetString(bytes); }
            catch { return Encoding.UTF8.GetString(bytes); }
        }

        /// <summary>
        /// 把管道里读到的文本再修一遍：若已按 UTF-8 读成字符串且含替换字符，
        /// 说明原始字节不是 UTF-8（StreamReader 已丢失原始字节，无法救回，仅作兜底提示）。
        /// </summary>
        public static string Cleanup(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Replace("\0", "").Trim();
        }
    }
}
