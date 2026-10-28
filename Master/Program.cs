using LanAutoConfig.Common;
using LanAutoConfig.Master;

namespace LanAutoConfig
{
    /// <summary>控制端独立程序入口（无需模式选择，直接进入控制端窗体）。</summary>
    internal static class Program
    {
        /// <summary>
        /// 单实例互斥体。控制端监听 48888(广播)/48889(指令)，两个实例同时跑会互相抢同一个端口
        /// （因为监听套接字开了 SO_REUSEADDR 以便“关掉立刻重开”，后绑定的那个会把连接抢走），
        /// 表现为“指令明明发了，被控端没反应”。
        /// </summary>
        private static Mutex _singleMutex;

        [STAThread]
        private static void Main()
        {
            var logger = new TextLogger(Path.Combine(AppConfig.LogDir, "master.log"));

            if (!AcquireSingleInstance(logger)) return;

            // 任何未处理异常都记日志并提示，避免程序直接闪退且什么都不留
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Report(logger, e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Report(logger, e.ExceptionObject as Exception);

            try
            {
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                UiScale.Init();   // 读取系统 DPI 缩放比例，供界面按比例换算像素常量

                // EnableVisualStyles 会把主题清单写到 %TEMP%。在某些环境（受限令牌、安全软件、
                // 只读漫游配置）这一步会 Access Denied，不能让它把整个程序带崩，降级为经典外观即可。
                try { Application.EnableVisualStyles(); }
                catch (Exception ex) { logger.Warn("启用视觉样式失败（%TEMP% 可能不可写），已降级为经典外观：" + ex.Message); }

                Application.SetCompatibleTextRenderingDefault(false);

                logger.Info($"=== 控制端启动 pid={Environment.ProcessId} 版本={Application.ProductVersion} ===");
                logger.Info($"数据目录={AppConfig.DataDir}" +
                            (AppConfig.DataDirIsFallback ? "（%AppData% 不可写，已回退到程序目录）" : ""));

                var form = new MasterForm();
                logger.Info("=== 主窗体创建完成，进入消息循环 ===");
                Application.Run(form);
                logger.Info("=== 控制端退出 ===");
            }
            catch (Exception ex)
            {
                Report(logger, ex);
            }
            finally
            {
                try { _singleMutex?.ReleaseMutex(); } catch { }
                try { _singleMutex?.Dispose(); } catch { }
            }
        }

        /// <summary>单实例控制：拿到互斥体返回 true；已有实例在跑则提示并返回 false。</summary>
        private static bool AcquireSingleInstance(TextLogger logger)
        {
            string[] names =
            {
                @"Global\LanAutoConfigMaster.SingleInstance",
                @"Local\LanAutoConfigMaster.SingleInstance"
            };
            foreach (string name in names)
            {
                try
                {
                    var m = new Mutex(true, name, out bool created);
                    if (created) { _singleMutex = m; return true; }
                    m.Dispose();
                    try { logger.Warn($"已有控制端实例在运行（互斥体 {name} 已存在），本次启动直接退出"); } catch { }
                    try
                    {
                        MessageBox.Show(
                            "控制端已经在运行，不再启动第二个。\n\n" +
                            "两个控制端会抢同一个端口（48888/48889），导致指令发给别人、被控端收不到。\n" +
                            "请切换到已经打开的那个窗口；确实要重开，先在任务管理器里结束现有的控制端进程。",
                            "程序已在运行", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch { }
                    return false;
                }
                catch (UnauthorizedAccessException)
                {
                    // Global\ 没权限（受限账户/跨会话）时退到 Local\ 再试
                }
                catch (Exception ex)
                {
                    // 检查本身失败不拦启动（宁可多开一个，也不能让控制端起不来）
                    try { logger.Warn("单实例检查失败，忽略并继续启动：" + ex.Message); } catch { }
                    return true;
                }
            }
            return true;
        }

        private static void Report(TextLogger logger, Exception ex)
        {
            try { logger.Error("未处理异常：" + ex); } catch { }
            // 日志文件被占用/权限不足时的兜底：写临时目录，保证异常不丢
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "lanac-master-error.log"),
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
