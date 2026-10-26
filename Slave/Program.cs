using LanAutoConfig.Common;
using LanAutoConfig.Slave;

namespace LanAutoConfig
{
    /// <summary>被控端独立程序入口（需管理员权限，直接进入被控端窗体）。</summary>
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            var logger = new TextLogger(Path.Combine(AppConfig.LogDir, "slave.log"));

            // 任何未处理异常都记日志并提示，避免程序直接闪退且什么都不留
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Report(logger, e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Report(logger, e.ExceptionObject as Exception);

            try
            {
                Application.SetHighDpiMode(HighDpiMode.SystemAware);

                // EnableVisualStyles 会把主题清单写到 %TEMP%。在某些环境（受限令牌、安全软件、
                // 只读漫游配置）这一步会 Access Denied，不能让它把整个程序带崩，降级为经典外观即可。
                try { Application.EnableVisualStyles(); }
                catch (Exception ex) { logger.Warn("启用视觉样式失败（%TEMP% 可能不可写），已降级为经典外观：" + ex.Message); }

                Application.SetCompatibleTextRenderingDefault(false);

                if (!AppConfig.IsAdministrator())
                {
                    MessageBox.Show("被控端必须以管理员权限运行，否则无法修改网卡与计算机名。\n请右键“以管理员身份运行”本程序。",
                        "权限不足", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                logger.Info($"=== 被控端启动 pid={Environment.ProcessId} 版本={Application.ProductVersion} ===");
                logger.Info($"数据目录={AppConfig.DataDir}" +
                            (AppConfig.DataDirIsFallback ? "（%AppData% 不可写，已回退到程序目录）" : ""));

                var form = new SlaveForm();
                logger.Info("=== 主窗体创建完成，进入消息循环 ===");
                Application.Run(form);
                logger.Info("=== 被控端退出 ===");
            }
            catch (Exception ex)
            {
                Report(logger, ex);
            }
        }

        private static void Report(TextLogger logger, Exception ex)
        {
            try { logger.Error("未处理异常：" + ex); } catch { }
            // 日志文件被占用/权限不足时的兜底：写临时目录，保证异常不丢
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "lanac-slave-error.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}{Environment.NewLine}", Encoding.UTF8);
            }
            catch { }
            try
            {
                MessageBox.Show("程序遇到未处理异常：\n" + (ex?.Message ?? "未知错误") +
                                "\n\n详情已写入日志：" + AppConfig.LogDir,
                    "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }
    }
}
