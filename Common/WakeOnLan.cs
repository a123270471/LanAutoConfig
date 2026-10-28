namespace LanAutoConfig.Common
{
    /// <summary>
    /// 网络唤醒（Wake-on-LAN）：向目标 MAC 发送魔术包（6×0xFF + MAC×16）。
    ///
    /// 说明：必须和终端在**同一广播域**（同交换机/VLAN），跨路由一般不通；
    /// 目标机还要在 BIOS/网卡驱动里开启“网络唤醒”，并关闭 Windows 快速启动，否则关机后网卡断电收不到包。
    /// </summary>
    public static class WakeOnLan
    {
        /// <summary>发送魔术包。mac 支持 AA-BB-CC-DD-EE-FF / AA:BB:.. 两种写法。</summary>
        public static bool Send(string mac, string broadcastIp, out string error)
        {
            error = null;
            if (!TryParseMac(mac, out byte[] macBytes))
            {
                error = "MAC 格式不合法：" + mac;
                return false;
            }

            var packet = new byte[6 + 16 * 6];
            for (int i = 0; i < 6; i++) packet[i] = 0xFF;
            for (int r = 0; r < 16; r++)
                Buffer.BlockCopy(macBytes, 0, packet, 6 + r * 6, 6);

            string target = string.IsNullOrWhiteSpace(broadcastIp) ? "255.255.255.255" : broadcastIp;
            try
            {
                using var udp = new UdpClient { EnableBroadcast = true };
                var ep = new IPEndPoint(IPAddress.Parse(target), 9);
                udp.Send(packet, packet.Length, ep);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool TryParseMac(string mac, out byte[] bytes)
        {
            bytes = null;
            if (string.IsNullOrWhiteSpace(mac)) return false;
            var parts = mac.Trim().Replace(':', '-').Split('-');
            if (parts.Length != 6) return false;
            var buf = new byte[6];
            for (int i = 0; i < 6; i++)
            {
                if (!byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out buf[i]))
                    return false;
            }
            bytes = buf;
            return true;
        }
    }
}
