namespace LanAutoConfig.Common
{
    /// <summary>线程安全的本地文本日志（被控端本地日志 / 控制端日志持久化）。</summary>
    public class TextLogger
    {
        private readonly object _lock = new object();
        private readonly string _filePath;

        /// <summary>构造。目录不存在会自动创建。</summary>
        public TextLogger(string filePath)
        {
            _filePath = filePath;
            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            }
            catch { }
        }

        public void Info(string msg) => Write("INFO", msg);
        public void Warn(string msg) => Write("WARN", msg);
        public void Error(string msg) => Write("ERROR", msg);

        public void Write(string level, string msg)
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}";
            lock (_lock)
            {
                try { File.AppendAllText(_filePath, line + Environment.NewLine, Encoding.UTF8); }
                catch { }
            }
        }
    }
}
