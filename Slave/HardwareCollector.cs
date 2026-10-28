using System.Management;
using LanAutoConfig.Common;

namespace LanAutoConfig.Slave
{
    /// <summary>
    /// 通过 WMI 采集本机硬件/资产信息（CPU、内存、系统盘、主板、BIOS、序列号）。
    ///
    /// 批量装机后控制端要靠这些字段自动建资产台账，所以：
    /// - 单项失败不影响整体（每个查询单独 try/catch，取不到就留空）；
    /// - 采集放在后台线程做，且只做一次，不阻塞界面/上报；
    /// - WMI 在个别机器上会很慢甚至挂住，这里给整体加了超时兜底。
    /// </summary>
    public static class HardwareCollector
    {
        private static readonly object _lock = new object();
        private static HardwareInfo _info;
        private static Task _task;

        /// <summary>取缓存结果（可能为 null，表示还没采完）。</summary>
        public static HardwareInfo Current
        {
            get { lock (_lock) return _info; }
        }

        /// <summary>启动后台采集（幂等，多次调用只跑一次）。</summary>
        public static void EnsureStarted()
        {
            lock (_lock)
            {
                if (_task != null) return;
                _task = Task.Run(() =>
                {
                    var hw = CollectSafe();
                    lock (_lock) _info = hw;
                });
            }
        }

        /// <summary>同步采集（带整体超时，超时就返回已有部分）。</summary>
        public static HardwareInfo Collect(TimeSpan timeout)
        {
            EnsureStarted();
            var t = _task;
            try { t?.Wait(timeout); } catch { }
            return Current ?? new HardwareInfo();
        }

        private static HardwareInfo CollectSafe()
        {
            var hw = new HardwareInfo();
            try { FillCpu(hw); } catch { }
            try { FillMemory(hw); } catch { }
            try { FillDisk(hw); } catch { }
            try { FillBoard(hw); } catch { }
            try { FillBios(hw); } catch { }
            try { FillComputer(hw); } catch { }
            try { FillOs(hw); } catch { }
            if (string.IsNullOrEmpty(hw.MachineName)) hw.MachineName = Environment.MachineName;
            return hw;
        }

        private static void FillCpu(HardwareInfo hw)
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
            foreach (ManagementObject o in s.Get())
            {
                hw.Cpu = (o["Name"] as string)?.Trim() ?? "";
                hw.CpuCores = ToInt(o["NumberOfCores"]);
                if (hw.CpuCores <= 0) hw.CpuCores = ToInt(o["NumberOfLogicalProcessors"]);
                break;   // 多路 CPU 只取第一颗做型号代表
            }
        }

        private static void FillMemory(HardwareInfo hw)
        {
            using (var cs = new ManagementObjectSearcher(
                       "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem"))
            {
                foreach (ManagementObject o in cs.Get())
                {
                    hw.MemoryBytes = ToLong(o["TotalPhysicalMemory"]);
                    break;
                }
            }
            if (hw.MemoryBytes > 0) return;

            long total = 0;
            using var pm = new ManagementObjectSearcher("SELECT Capacity FROM Win32_PhysicalMemory");
            foreach (ManagementObject o in pm.Get()) total += ToLong(o["Capacity"]);
            hw.MemoryBytes = total;
        }

        private static void FillDisk(HardwareInfo hw)
        {
            string sysDrive = (Environment.GetFolderPath(Environment.SpecialFolder.Windows) is string win
                               && win.Length >= 2) ? win.Substring(0, 2) : "C:";
            using (var ld = new ManagementObjectSearcher(
                       "SELECT DeviceID, Size FROM Win32_LogicalDisk WHERE DriveType=3"))
            {
                foreach (ManagementObject o in ld.Get())
                {
                    if (!string.Equals(o["DeviceID"] as string, sysDrive, StringComparison.OrdinalIgnoreCase)) continue;
                    hw.DiskBytes = ToLong(o["Size"]);
                    break;
                }
            }
            using var dd = new ManagementObjectSearcher(
                "SELECT Model, Size FROM Win32_DiskDrive WHERE MediaType LIKE '%Fixed%'");
            foreach (ManagementObject o in dd.Get())
            {
                hw.DiskModel = (o["Model"] as string)?.Trim() ?? "";
                break;
            }
        }

        private static void FillBoard(HardwareInfo hw)
        {
            using var bb = new ManagementObjectSearcher(
                "SELECT Product, SerialNumber FROM Win32_BaseBoard");
            foreach (ManagementObject o in bb.Get())
            {
                hw.BaseBoard = (o["Product"] as string)?.Trim() ?? "";
                hw.SerialNumber = (o["SerialNumber"] as string)?.Trim() ?? "";
                break;
            }
        }

        private static void FillBios(HardwareInfo hw)
        {
            using var b = new ManagementObjectSearcher(
                "SELECT SMBIOSBIOSVersion, ReleaseDate, SerialNumber FROM Win32_BIOS");
            foreach (ManagementObject o in b.Get())
            {
                hw.BiosVersion = (o["SMBIOSBIOSVersion"] as string)?.Trim() ?? "";
                hw.BiosDate = FormatWmiDate(o["ReleaseDate"] as string);
                // 整机序列号优先用 BIOS 里的（笔记本/品牌机 OEM 会写机器 SN）
                string sn = (o["SerialNumber"] as string)?.Trim();
                if (!string.IsNullOrEmpty(sn)) hw.SerialNumber = sn;
                break;
            }
        }

        private static void FillComputer(HardwareInfo hw)
        {
            using var cs = new ManagementObjectSearcher(
                "SELECT Manufacturer, Model, Name, TotalPhysicalMemory FROM Win32_ComputerSystem");
            foreach (ManagementObject o in cs.Get())
            {
                hw.Manufacturer = (o["Manufacturer"] as string)?.Trim() ?? "";
                hw.Model = (o["Model"] as string)?.Trim() ?? "";
                hw.MachineName = (o["Name"] as string)?.Trim() ?? "";
                if (hw.MemoryBytes <= 0) hw.MemoryBytes = ToLong(o["TotalPhysicalMemory"]);
                break;
            }
        }

        private static void FillOs(HardwareInfo hw)
        {
            using var os = new ManagementObjectSearcher(
                "SELECT Caption, Version, BuildNumber FROM Win32_OperatingSystem");
            foreach (ManagementObject o in os.Get())
            {
                string caption = (o["Caption"] as string)?.Trim() ?? "";
                string ver = (o["Version"] as string)?.Trim() ?? "";
                string build = (o["BuildNumber"] as string)?.Trim() ?? "";
                hw.OsVersion = string.IsNullOrEmpty(build) ? $"{caption} {ver}".Trim() : $"{caption} {ver} (build {build})".Trim();
                break;
            }
        }

        /// <summary>WMI 的 CIM_DATETIME（20240101000000.000000+480）转成 2024-01-01。</summary>
        private static string FormatWmiDate(string raw)
        {
            if (string.IsNullOrEmpty(raw) || raw.Length < 8) return "";
            return $"{raw.Substring(0, 4)}-{raw.Substring(4, 2)}-{raw.Substring(6, 2)}";
        }

        private static int ToInt(object v) => v == null ? 0 : (int)Convert.ToInt64(v);
        private static long ToLong(object v)
        {
            if (v == null) return 0;
            try { return Convert.ToInt64(v); } catch { return 0; }
        }
    }
}
