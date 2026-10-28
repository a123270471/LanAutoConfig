using LanAutoConfig.Common;

namespace LanAutoConfig.Slave
{
    /// <summary>本地 PowerShell 执行结果。</summary>
    public class PsResult
    {
        public bool Success { get; set; }
        public string Output { get; set; }
        public string Error { get; set; }
        /// <summary>进程退出码（0 = 正常；自定义命令/脚本可借此判断业务结果）。</summary>
        public int ExitCode { get; set; }
        public override string ToString() => Success ? "成功" : $"失败: {Error}";
    }

    /// <summary>
    /// 被控端本地配置执行模块（文档 8 节 LocalNetChanger）。
    /// 修改动作在本机本地执行（调用本地 PowerShell），不需要远程凭据、不碰远程 WMI/WinRM。
    /// 说明：为兼容单文件发布，通过 powershell.exe -EncodedCommand 调用，效果与 System.Management.Automation 一致。
    /// </summary>
    public class LocalNetChanger
    {
        /// <summary>设置网卡为静态 IP（含 DNS）。返回执行结果。</summary>
        public PsResult SetStaticIp(string ifAlias, string ip, string mask, string gateway, string dns1, string dns2)
        {
            if (string.IsNullOrWhiteSpace(ifAlias)) return Fail("未指定网卡，无法修改 IP");
            if (!Validators.IsValidIp(ip)) return Fail($"目标 IP 不合法：{ip}");
            if (!Validators.IsValidMask(mask)) return Fail($"子网掩码不合法：{mask}");
            if (!Validators.IsValidIp(gateway)) return Fail($"网关不合法：{gateway}");

            int prefix = Validators.MaskToPrefixLength(mask);

            // DNS2 允许为空：之前无条件拼 ('dns1','dns2') 会把 '' 传给 cmdlet，导致整段脚本在 DNS 步骤报错中止
            var dns = new List<string>();
            if (!string.IsNullOrWhiteSpace(dns1)) dns.Add(dns1.Trim());
            if (!string.IsNullOrWhiteSpace(dns2)) dns.Add(dns2.Trim());

            string dnsCmd;
            if (dns.Count > 0)
            {
                var quoted = dns.Select(d => "'" + EscapePs(d) + "'");
                dnsCmd = "Set-DnsClientServerAddress -InterfaceAlias $if -ServerAddresses (" +
                         string.Join(",", quoted) + ") -ErrorAction Stop";
            }
            else
            {
                dnsCmd = "Set-DnsClientServerAddress -InterfaceAlias $if -ResetServerAddresses -ErrorAction Stop";
            }

            string gwArg = string.IsNullOrWhiteSpace(gateway) ? "" : $" -DefaultGateway '{EscapePs(gateway.Trim())}'";

            string script = $@"
$ErrorActionPreference='Stop'
$if='{EscapePs(ifAlias)}'
Set-NetIPInterface -InterfaceAlias $if -Dhcp Disabled -ErrorAction Stop
Remove-NetIPAddress -InterfaceAlias $if -AddressFamily IPv4 -Confirm:$false -ErrorAction SilentlyContinue
Remove-NetRoute -InterfaceAlias $if -AddressFamily IPv4 -Confirm:$false -ErrorAction SilentlyContinue
New-NetIPAddress -InterfaceAlias $if -IPAddress '{EscapePs(ip.Trim())}' -PrefixLength {prefix}{gwArg} -ErrorAction Stop
{dnsCmd}
";
            return RunPs(script);
        }

        /// <summary>恢复网卡为 DHCP（IP + DNS）。</summary>
        public PsResult SetDhcp(string ifAlias)
        {
            if (string.IsNullOrWhiteSpace(ifAlias)) return Fail("未指定网卡，无法恢复 DHCP");

            // 关键：静态配置过的网卡 EnableDHCP=0，只删地址并不会自动去要 DHCP，
            // 否则网卡会掉进 169.254.x.x 失联。必须先 Set-NetIPInterface -Dhcp Enabled。
            string script = $@"
$ErrorActionPreference='Stop'
$if='{EscapePs(ifAlias)}'
Set-NetIPInterface -InterfaceAlias $if -Dhcp Enabled -ErrorAction Stop
Remove-NetIPAddress -InterfaceAlias $if -AddressFamily IPv4 -Confirm:$false -ErrorAction SilentlyContinue
Remove-NetRoute -InterfaceAlias $if -AddressFamily IPv4 -Confirm:$false -ErrorAction SilentlyContinue
Set-DnsClientServerAddress -InterfaceAlias $if -ResetServerAddresses -ErrorAction Stop
ipconfig /renew $if | Out-Null
$ok=$false
for ($i=0; $i -lt 15; $i++) {{
    if (Get-NetIPAddress -InterfaceAlias $if -AddressFamily IPv4 -ErrorAction SilentlyContinue) {{ $ok=$true; break }}
    Start-Sleep -Seconds 1
}}
if (-not $ok) {{ Write-Warning '已切换为 DHCP，但 15 秒内未取得 IPv4 地址（请检查 DHCP 服务器/网线）' }}
";
            return RunPs(script);
        }

        /// <summary>
        /// 给网卡追加一个临时 IPv4（不设网关、不改 DHCP 状态）。
        /// 用于“被控端与控制端不同网段但在同一广播域”时临时互通；执行配置时原脚本会清除该网卡全部 IPv4，临时地址随之消失。
        /// </summary>
        public PsResult AddTempIp(string ifAlias, string ip, int prefixLength)
        {
            if (string.IsNullOrWhiteSpace(ifAlias)) return Fail("未指定网卡，无法添加临时地址");
            if (!Validators.IsValidIp(ip)) return Fail($"临时 IP 不合法：{ip}");
            if (prefixLength <= 0 || prefixLength > 32) return Fail($"前缀长度不合法：{prefixLength}");

            string mask = MaskFromPrefix(prefixLength);
            string script = $@"
$ErrorActionPreference='Stop'
$if='{EscapePs(ifAlias)}'
$ip='{EscapePs(ip.Trim())}'
$done=$false
try {{
    New-NetIPAddress -InterfaceAlias $if -IPAddress $ip -PrefixLength {prefixLength} -ErrorAction Stop | Out-Null
    $done=$true
}} catch {{
    if ($_.Exception.Message -like '*already exists*' -or $_.Exception.Message -like '*已存在*') {{
        # 幂等：地址已经在这个网卡上（上次没清理干净/重复点击）
        $done=$true
    }} else {{
        # 兜底：有些机器上 WMI/CIM 写入被安全软件拦截（New-NetIPAddress 报“拒绝访问”），
        # netsh 走的是另一套接口，通常仍可用。
        $primaryErr = $_.Exception.Message
        netsh interface ipv4 add address $if $ip {mask} | Out-Null
        if ($LASTEXITCODE -ne 0) {{
            throw ('New-NetIPAddress 失败（' + $primaryErr + '），netsh 兜底也失败（退出码 ' + $LASTEXITCODE + '）')
        }}
        $done=$true
    }}
}}
if (-not $done) {{ throw ('添加临时地址 ' + $ip + ' 失败') }}
# 等 DAD 收敛：Preferred 最好；Duplicate 是真冲突，必须回滚。
# 但 Tentative/空值不能当失败——某些网卡（虚拟机、无线、netsh 添加）DAD 状态长期不收敛或为空，
# 只要地址已挂到网卡上就交给后续“真实 TCP 握手”判断，避免误报“添加失败”。
$state=$null
for ($i=0; $i -lt 16; $i++) {{
    $a = Get-NetIPAddress -InterfaceAlias $if -IPAddress $ip -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($a) {{ $state = [string]$a.DuplicateAddressDetectionState }}
    if ($state -eq 'Preferred' -or $state -eq 'NotSupported' -or $state -eq 'Duplicate') {{ break }}
    Start-Sleep -Milliseconds 250
}}
if ($state -eq 'Duplicate') {{
    Remove-NetIPAddress -InterfaceAlias $if -IPAddress $ip -Confirm:$false -ErrorAction SilentlyContinue
    netsh interface ipv4 delete address $if $ip | Out-Null
    throw ('地址冲突：' + $ip + ' 已被占用，已回滚')
}}
if (-not (Get-NetIPAddress -InterfaceAlias $if -IPAddress $ip -ErrorAction SilentlyContinue)) {{
    throw ('临时地址 ' + $ip + ' 没有成功挂到网卡 ' + $if + ' 上')
}}
if ($state -ne 'Preferred' -and $state -ne 'NotSupported') {{
    Write-Output ('WARN: 临时地址 ' + $ip + ' 状态=' + $state + '（继续，交给 TCP 验证）')
}}
";
            return RunPs(script);
        }

        /// <summary>
        /// 连通性诊断：输出“到目标的路由/源地址”以及本机全部非 APIPA 地址，用于排查上报失败。
        /// </summary>
        public PsResult DiagnoseRoute(string targetIp)
        {
            if (!Validators.IsValidIp(targetIp)) return Fail("目标 IP 不合法");

            string script = $@"
$ErrorActionPreference='Continue'
'到 ' + '{EscapePs(targetIp.Trim())}' + ' 的路由：'
(Find-NetRoute -RemoteIPAddress '{EscapePs(targetIp.Trim())}' -ErrorAction SilentlyContinue |
    Format-List IPAddress,InterfaceAlias,InterfaceIndex,NextHop,DestinationPrefix | Out-String).Trim()
'本机 IPv4：'
Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object {{ $_.IPAddress -notlike '169.254.*' -and $_.IPAddress -ne '127.0.0.1' }} |
    ForEach-Object {{ '  ' + $_.IPAddress + '/' + $_.PrefixLength + '  [' + $_.InterfaceAlias + ']  ' + $_.DuplicateAddressDetectionState }}
";
            return RunPs(script);
        }

        /// <summary>删除之前追加的临时 IPv4（回滚/退出时用）。</summary>
        public PsResult RemoveTempIp(string ifAlias, string ip)
        {
            if (string.IsNullOrWhiteSpace(ifAlias) || !Validators.IsValidIp(ip)) return Fail("参数不合法，无法删除临时地址");

            string script = $@"
$ErrorActionPreference='Stop'
$if='{EscapePs(ifAlias)}'
$ip='{EscapePs(ip.Trim())}'
Remove-NetIPAddress -InterfaceAlias $if -IPAddress $ip -Confirm:$false -ErrorAction SilentlyContinue
if (Get-NetIPAddress -InterfaceAlias $if -IPAddress $ip -ErrorAction SilentlyContinue) {{
    # WMI/CIM 被拦时用 netsh 兜底
    netsh interface ipv4 delete address $if $ip | Out-Null
}}
";
            return RunPs(script);
        }

        /// <summary>前缀长度转点分掩码（24 → 255.255.255.0）。</summary>
        private static string MaskFromPrefix(int prefixLength)
        {
            if (prefixLength < 0) prefixLength = 0;
            if (prefixLength > 32) prefixLength = 32;
            uint m = prefixLength == 0 ? 0u : 0xFFFFFFFFu << (32 - prefixLength);
            return $"{(m >> 24) & 0xFF}.{(m >> 16) & 0xFF}.{(m >> 8) & 0xFF}.{m & 0xFF}";
        }

        /// <summary>重命名本机计算机名（重启后完全生效）。</summary>
        public PsResult RenameComputer(string newName)
        {
            if (string.IsNullOrWhiteSpace(newName)) return Fail("目标主机名为空");
            if (!Validators.IsValidHostName(newName)) return Fail($"目标主机名不合法：{newName}");

            // 必须用真实换行 \n 分隔两条语句；若用逐字字符串里的 `n，PowerShell 会报 ParserError
            string script = $"[Console]::OutputEncoding=[Text.Encoding]::UTF8\nRename-Computer -NewName '{EscapePs(newName.Trim())}' -Force";
            return RunPs(script);
        }

        /// <summary>重启本机。force=true 时强制关闭应用程序。</summary>
        public PsResult Reboot(bool force)
        {
            string args = force ? "/r /t 5 /f" : "/r /t 5";
            return RunPs($"shutdown {args}");
        }

        /// <summary>
        /// 以 powershell.exe -EncodedCommand 调用本地 PowerShell，捕获输出。
        /// 注意：stdout/stderr 必须并发读取，否则对端写满管道缓冲区时会互相等待造成死锁。
        /// </summary>
        private static PsResult RunPs(string script)
        {
            var result = new PsResult();
            try
            {
                // PowerShell 直接跑会在 stderr 输出 CLIXML（且中文按 ANSI 乱码）。
                // 这里在脚本内 try/catch 把异常转成一行 ERROR 文本走 stdout；注意不能在管道里 exit
                // （`... | Out-String` 会因 exit 中止管道而丢掉尚未输出的内容，导致错误信息变空）。
                string full =
                    "[Console]::OutputEncoding=[Text.Encoding]::UTF8\n" +
                    "$ProgressPreference='SilentlyContinue'\n" +
                    "try {\n" + script + "\n  exit 0\n} catch {\n" +
                    "  Write-Output ('ERROR: ' + $_.Exception.Message)\n" +
                    "  exit 1\n}\n";
                string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
                var psi = new ProcessStartInfo("powershell.exe")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false),
                    Arguments = "-NoProfile -NonInteractive -OutputFormat Text -ExecutionPolicy Bypass -EncodedCommand " + encoded
                };
                using var p = Process.Start(psi);
                if (p == null)
                {
                    result.Success = false;
                    result.Error = "无法启动 powershell.exe";
                    return result;
                }

                Task<string> outTask = p.StandardOutput.ReadToEndAsync();
                Task<string> errTask = p.StandardError.ReadToEndAsync();

                if (!p.WaitForExit(60000))
                {
                    try { p.Kill(true); } catch { }
                    result.Success = false;
                    result.Error = "PowerShell 执行超时（60 秒），已强制结束";
                    return result;
                }

                result.Output = (outTask.GetAwaiter().GetResult() ?? "").Trim();
                result.Error = (errTask.GetAwaiter().GetResult() ?? "").Trim();
                result.Success = p.ExitCode == 0;
                // 脚本用 try/catch 把错误转成了 stdout 文本（避免 CLIXML），这里补回到 Error，保证调用方能看到原因
                if (!result.Success && string.IsNullOrEmpty(result.Error) && !string.IsNullOrEmpty(result.Output))
                    result.Error = result.Output;
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = "启动 PowerShell 失败：" + ex.Message;
                return result;
            }
        }

        private static PsResult Fail(string msg) => new PsResult { Success = false, Error = msg };

        /// <summary>
        /// 执行控制端下发的自定义命令/脚本。
        /// shell="powershell" 走本地 PowerShell（与原改网卡逻辑同一套执行器）；
        /// 其它（如 "cmd"）走 cmd.exe /c。超时由调用方（被控端指令处理器）控制。
        /// </summary>
        public PsResult ExecShell(string shell, string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return Fail("命令内容为空");
            if (string.Equals(shell, "powershell", StringComparison.OrdinalIgnoreCase))
                return RunPs(code);
            return RunCmd(code);
        }

        /// <summary>
        /// 用 cmd.exe /c 执行一段命令（用于“命令(cmd)”方式下发的脚本）。
        ///
        /// 编码说明：cmd.exe 及 ipconfig/netsh/systeminfo 这类程序把文本写到控制台时用的是
        /// OEM 代码页（简体中文 = 936/GBK），不是 UTF-8。之前这里按 UTF-8 解码，
        /// 中文输出到控制端/被控端日志就成了乱码。现在改成读「原始字节」，
        /// 交给 <see cref="ConsoleText.Decode"/> 判定 UTF-8 还是 OEM 代码页。
        /// </summary>
        private PsResult RunCmd(string command)
        {
            var result = new PsResult();
            try
            {
                var psi = new ProcessStartInfo("cmd.exe")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    // 不用 StreamReader 的编码解码，改为直接读 BaseStream 原始字节（见下）
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false),
                    Arguments = "/c " + command
                };
                using var p = Process.Start(psi);
                if (p == null) { result.Success = false; result.Error = "无法启动 cmd.exe"; return result; }

                using var outBuf = new MemoryStream();
                using var errBuf = new MemoryStream();
                Task outCopy = p.StandardOutput.BaseStream.CopyToAsync(outBuf);
                Task errCopy = p.StandardError.BaseStream.CopyToAsync(errBuf);

                if (!p.WaitForExit(60000))
                {
                    try { p.Kill(true); } catch { }
                    result.Success = false;
                    result.Error = "命令执行超时（60 秒），已强制结束";
                    result.ExitCode = -1;
                    // 超时也把已产生的输出带回去，方便判断卡在哪一步
                    try { Task.WaitAll(new[] { outCopy, errCopy }, 2000); } catch { }
                    result.Output = ConsoleText.Decode(outBuf.ToArray()).Trim();
                    result.Error += result.Output.Length > 0 ? "\n（超时前输出见“输出”列）" : "";
                    return result;
                }

                // 进程已退出，管道可能还有残余数据，给读取任务一点时间收尾（不能无限等）
                try { Task.WaitAll(new[] { outCopy, errCopy }, 5000); } catch { }

                result.Output = ConsoleText.Decode(outBuf.ToArray()).Trim();
                result.Error = ConsoleText.Decode(errBuf.ToArray()).Trim();
                result.ExitCode = p.ExitCode;
                // cmd 的“成功”以退出码为准；但某些脚本即便有 stderr 仍算“跑完了”，所以只要退出码 0 就成功
                result.Success = p.ExitCode == 0;
                if (!result.Success && string.IsNullOrEmpty(result.Error) && !string.IsNullOrEmpty(result.Output))
                    result.Error = result.Output;
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = "启动命令失败：" + ex.Message;
                return result;
            }
        }

        /// <summary>转义 PowerShell 单引号字符串内文本。</summary>
        private static string EscapePs(string s) => (s ?? "").Replace("'", "''");
    }
}
