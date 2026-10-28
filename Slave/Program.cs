using LanAutoConfig.Common;
using LanAutoConfig.Slave;

namespace LanAutoConfig
{
    /// <summary>被控端独立程序入口（需管理员权限，直接进入被控端窗体）。</summary>
    internal static class Program
    {
        /// <summary>
        /// 单实例互斥体。整台机器只允许一个被控端在跑：
        /// 两个实例会抢同一张网卡、同一份日志文件，还会各自响应控制端指令互相打架
        /// （最典型的是双份心跳、双份执行，排查起来极难）。
        /// </summary>
        private static Mutex _singleMutex;

        [STAThread]
        private static void Main(string[] args)
        {
            var logger = new TextLogger(Path.Combine(AppConfig.LogDir, "slave.log"));
            var options = SlaveOptions.Parse(args);
            try { logger.Info("启动参数：" + options.Describe()); } catch { }

            // 单实例检查：已经在跑就直接提示并退出（不能起第二个）
            if (!AcquireSingleInstance(logger, options)) return;

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

                if (!AppConfig.IsAdministrator())
                {
                    logger.Error("未以管理员身份运行：无法修改网卡与计算机名");
                    if (!options.Silent)
                        MessageBox.Show("被控端必须以管理员权限运行，否则无法修改网卡与计算机名。\n请右键“以管理员身份运行”本程序。",
                            "权限不足", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // 静默部署：安装/移除开机自启（命令里带上本次的静默参数，开机后仍是同一套流程）
                if (options.InstallAutostart || options.RemoveAutostart)
                {
                    try
                    {
                        if (options.InstallAutostart)
                        {
                            string extra = Autostart.BuildArgs(options);
                            if (Autostart.Install(extra, out string where, out string insErr))
                                logger.Info($"已安装开机自启（{where}）：\"{Application.ExecutablePath}\" {extra}");
                            else
                                logger.Error("安装开机自启失败：" + insErr);
                        }
                        else
                        {
                            bool removed = Autostart.Remove(out string delErr);
                            logger.Info(removed ? "已移除开机自启" : "开机自启不存在或已被移除" +
                                        (string.IsNullOrEmpty(delErr) ? "" : "：" + delErr));
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Error("处理开机自启失败：" + ex.Message);
                    }
                }

                logger.Info($"=== 被控端启动 pid={Environment.ProcessId} 版本={Application.ProductVersion} ===");
                logger.Info($"数据目录={AppConfig.DataDir}" +
                            (AppConfig.DataDirIsFallback ? "（%AppData% 不可写，已回退到程序目录）" : ""));

                var form = new SlaveForm(options);
                logger.Info("=== 主窗体创建完成，进入消息循环 ===");
                Application.Run(form);
                logger.Info("=== 被控端退出 ===");
            }
            catch (Exception ex)
            {
                Report(logger, ex);
            }
            finally
            {
                try { _singleMutex?.ReleaseMutex(); } catch { }   // 不是持有者会抛，忽略
                try { _singleMutex?.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// 单实例控制：拿到互斥体返回 true；已有实例在跑则提示并返回 false。
        /// 优先用 Global\（同一台机器所有登录会话共用），没权限时退回 Local\。
        /// </summary>
        private static bool AcquireSingleInstance(TextLogger logger, SlaveOptions options)
        {
            string[] names =
            {
                @"Global\LanAutoConfigSlave.SingleInstance",
                @"Local\LanAutoConfigSlave.SingleInstance"
            };

            foreach (string name in names)
            {
                try
                {
                    var m = new Mutex(true, name, out bool created);
                    if (created)
                    {
                        _singleMutex = m;
                        return true;
                    }
                    m.Dispose();   // 名字已存在 = 另一个实例正在跑
                    AlreadyRunning(logger, options, name);
                    return false;
                }
                catch (UnauthorizedAccessException)
                {
                    // Global\ 需要权限，受限账户/跨会话时退到 Local\ 再试
                }
                catch (Exception ex)
                {
                    // 检查本身失败不该拦住启动（宁可多起一个，也不能让装机卡住）
                    try { logger.Warn("单实例检查失败，忽略并继续启动：" + ex.Message); } catch { }
                    return true;
                }
            }
            return true;
        }

        private static void AlreadyRunning(TextLogger logger, SlaveOptions options, string mutexName)
        {
            try { logger.Warn($"已有被控端实例在运行（互斥体 {mutexName} 已存在），本次启动直接退出，不再启动第二个"); } catch { }

            // 静默部署（--silent，一般由开机自启触发）时不能弹窗，否则会把无人值守的流程卡住
            if (options != null && options.Silent) return;
            try
            {
                MessageBox.Show(
                    "被控端程序已启动，不再启动第二个。\n\n" +
                    "已有一个被控端窗口在运行（同一时间只允许一个，避免两个实例抢网卡、重复执行控制端指令）。\n" +
                    "请切换到已经打开的那个窗口操作；确实需要重开时，先在任务管理器里结束现有的“被控端”进程。",
                    "程序已在运行", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch { }
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
