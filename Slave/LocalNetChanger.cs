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
        /// <summary>
        /// 系统是否已经在重启/关机中。为 true 时一律不再拉起新进程。
        ///
        /// 现场症状：下发执行命令后自动重启，屏幕上弹出「powershell.exe - 应用程序错误 0xc0000142」。
        /// 原因是关机一开始，Windows 就会拆掉用户会话（win32k/user32 不再可用），
        /// 这期间**任何**新建进程都会以 STATUS_DLL_INIT_FAILED(0xc0000142) 失败并弹框；
        /// 而我们这边“关机 → 窗体关闭 → 清临时 IP → 又调一次 PowerShell”正好撞上这个时间窗。
        /// 关机/重启一经提交就置位，后续的 RunPs/RunCmd 直接跳过，不再去踩这个坑。
        /// </summary>
        public static volatile bool ShutdownInProgress;

        /// <summary>是否应该放弃拉起新进程（关机已提交 或 系统正在关机）。</summary>
        public static bool SuppressLaunch
        {
            get
            {
                if (ShutdownInProgress) return true;
                try { return Environment.HasShutdownStarted; } catch { return false; }
            }
        }

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

            // 关键点（现场踩过）：Set-NetIPInterface -Dhcp Disabled 只是关掉"继续要租约"，
            // 已经拿到的 DHCP 租约地址**不会自动消失**，会和新静态地址并存（网卡同时挂两个 IPv4）。
            // 所以必须逐个删掉本网卡全部 IPv4；Remove-NetIPAddress 在部分机器上会静默失败
            // （之前用 -ErrorAction SilentlyContinue 把失败吞了，残留地址无人知晓），这里对每个地址
            // 单独删除、失败再用 netsh 兜底，最后校验一遍并把结果写回日志。
            string script = $@"
$ErrorActionPreference='Stop'
$if='{EscapePs(ifAlias)}'
Set-NetIPInterface -InterfaceAlias $if -Dhcp Disabled -ErrorAction Stop
Get-NetIPAddress -InterfaceAlias $if -AddressFamily IPv4 -ErrorAction SilentlyContinue |
  ForEach-Object {{
    $a = [string]$_.IPAddress
    try {{ Remove-NetIPAddress -InterfaceAlias $if -IPAddress $a -Confirm:$false -ErrorAction Stop }}
    catch {{ netsh interface ipv4 delete address $if $a | Out-Null }}
  }}
Remove-NetRoute -InterfaceAlias $if -AddressFamily IPv4 -Confirm:$false -ErrorAction SilentlyContinue
$rest = @()
for ($i=0; $i -lt 10; $i++) {{
    $rest = @(Get-NetIPAddress -InterfaceAlias $if -AddressFamily IPv4 -ErrorAction SilentlyContinue)
    if ($rest.Count -eq 0) {{ break }}
    Start-Sleep -Milliseconds 500
}}
if ($rest.Count -gt 0) {{
    Write-Output ('WARN: 以下旧地址未能清除: ' + (($rest | ForEach-Object {{ [string]$_.IPAddress }}) -join ', '))
}}
New-NetIPAddress -InterfaceAlias $if -IPAddress '{EscapePs(ip.Trim())}' -PrefixLength {prefix}{gwArg} -ErrorAction Stop
{dnsCmd}
$final = @(Get-NetIPAddress -InterfaceAlias $if -AddressFamily IPv4 -ErrorAction SilentlyContinue |
           ForEach-Object {{ [string]$_.IPAddress }})
Write-Output ('配置完成，网卡当前 IPv4: ' + ($final -join ', '))
";
            return RunPs(script);
        }

        /// <summary>恢复网卡为 DHCP（IP + DNS）。</summary>
        public PsResult SetDhcp(string ifAlias)
        {
            if (string.IsNullOrWhiteSpace(ifAlias)) return Fail("未指定网卡，无法恢复 DHCP");

            // 关键：静态配置过的网卡 EnableDHCP=0，只删地址并不会自动去要 DHCP，
            // 否则网卡会掉进 169.254.x.x 失联。必须先 Set-NetIPInterface -Dhcp Enabled。
            // 清旧地址时只删非 DHCP 来源的（静态/临时残留）；如果此刻租约刚下来，
            // 删了它 ipconfig /renew 也能再要到，但不做无谓的折腾。
            string script = $@"
$ErrorActionPreference='Stop'
$if='{EscapePs(ifAlias)}'
Set-NetIPInterface -InterfaceAlias $if -Dhcp Enabled -ErrorAction Stop
Get-NetIPAddress -InterfaceAlias $if -AddressFamily IPv4 -ErrorAction SilentlyContinue |
  Where-Object {{ $_.PrefixOrigin -ne 'Dhcp' }} |
  ForEach-Object {{
    $a = [string]$_.IPAddress
    try {{ Remove-NetIPAddress -InterfaceAlias $if -IPAddress $a -Confirm:$false -ErrorAction Stop }}
    catch {{ netsh interface ipv4 delete address $if $a | Out-Null }}
  }}
Remove-NetRoute -InterfaceAlias $if -AddressFamily IPv4 -Confirm:$false -ErrorAction SilentlyContinue
Set-DnsClientServerAddress -InterfaceAlias $if -ResetServerAddresses -ErrorAction Stop
ipconfig /renew $if | Out-Null
$ok=$false
for ($i=0; $i -lt 15; $i++) {{
    if (Get-NetIPAddress -InterfaceAlias $if -AddressFamily IPv4 -ErrorAction SilentlyContinue) {{ $ok=$true; break }}
    Start-Sleep -Seconds 1
}}
if (-not $ok) {{ Write-Warning '已切换为 DHCP，但 15 秒内未取得 IPv4 地址（请检查 DHCP 服务器/网线）' }}
$final = @(Get-NetIPAddress -InterfaceAlias $if -AddressFamily IPv4 -ErrorAction SilentlyContinue |
           ForEach-Object {{ [string]$_.IPAddress }})
Write-Output ('配置完成，网卡当前 IPv4: ' + ($final -join ', '))
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
            => RunShutdown(force ? "/r /t 5 /f" : "/r /t 5");

        /// <summary>
        /// 重启 / 关机：delay 秒后执行。返回是否成功提交，失败时 error 带原因。
        /// 被控端所有“重启电脑 / 关机”都走这里，统一不经 PowerShell（见 RunShutdown 注释）。
        /// </summary>
        public static bool Shutdown(int delaySeconds, bool reboot, out string error)
        {
            int d = Math.Max(0, Math.Min(600, delaySeconds));
            var r = RunShutdown(reboot ? $"/r /t {d} /f" : $"/s /t {d} /f");
            error = r.Success ? null : (string.IsNullOrEmpty(r.Error) ? "shutdown 未完成" : r.Error);
            return r.Success;
        }

        /// <summary>
        /// 直接调 shutdown.exe，**不经过 PowerShell**。
        ///
        /// 之前是 RunPs("shutdown ...")：PowerShell 作为父进程活着等子进程返回，
        /// 系统几秒后开始关机时它还没退干净，于是屏幕上弹出 PowerShell 的错误窗
        /// （现场看到的就是“重启时蹦出一个 PowerShell 报错”）。
        /// shutdown.exe 自己拉起、自己退出，没有这个中间层。
        /// </summary>
        private static PsResult RunShutdown(string args)
        {
            var result = new PsResult();
            try
            {
                var psi = new ProcessStartInfo("shutdown.exe", args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = ConsoleText.Oem,
                    StandardErrorEncoding = ConsoleText.Oem
                };
                using var p = Process.Start(psi);
                if (p == null) { result.Error = "无法启动 shutdown.exe"; return result; }

                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(15000))
                {
                    try { p.Kill(true); } catch { }
                    result.Success = true;   // 关机已在进行，shutdown 挂住是正常的
                    result.Output = "shutdown 已提交（进程未在 15 秒内返回，按成功处理）";
                    ShutdownInProgress = true;   // 关机窗口已打开：后面不要再拉起任何进程
                    return result;
                }

                result.Output = (outTask.GetAwaiter().GetResult() ?? "").Trim();
                result.Error = (errTask.GetAwaiter().GetResult() ?? "").Trim();
                result.ExitCode = p.ExitCode;

                // 0 = 成功；1190 = 已有计划中的关机；1115 = 关机已在进行
                result.Success = p.ExitCode == 0 || p.ExitCode == 1190 || p.ExitCode == 1115;
                if (result.Success) ShutdownInProgress = true;   // 同上：进入关机倒计时，禁止再拉起新进程
                if (!result.Success && string.IsNullOrEmpty(result.Error))
                    result.Error = $"shutdown 退出码 {p.ExitCode}";
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }
        }

        /// <summary>
        /// 以 powershell.exe -EncodedCommand 调用本地 PowerShell，捕获输出。
        /// 注意：stdout/stderr 必须并发读取，否则对端写满管道缓冲区时会互相等待造成死锁。
        /// </summary>
        private static PsResult RunPs(string script, int timeoutMs = 60000)
        {
            var result = new PsResult();
            // 关机/重启已经开始：此时拉起 powershell.exe 必然 0xc0000142 并弹错误框，直接放弃
            if (SuppressLaunch)
            {
                result.Success = false;
                result.Error = "系统正在重启/关机，已跳过这条 PowerShell 命令";
                return result;
            }
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

                if (!p.WaitForExit(Math.Max(1000, timeoutMs)))
                {
                    try { p.Kill(true); } catch { }
                    result.Success = false;
                    result.Error = $"PowerShell 执行超时（{Math.Max(1, timeoutMs / 1000)} 秒），已强制结束";
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
        public PsResult ExecShell(string shell, string code, int timeoutSeconds = 60)
        {
            if (string.IsNullOrWhiteSpace(code)) return Fail("命令内容为空");
            // 控制端对话框里设的超时真正生效（之前这里写死 60 秒，设 5 秒也要等满 60 秒）
            int timeoutMs = Math.Max(5, Math.Min(600, timeoutSeconds <= 0 ? 60 : timeoutSeconds)) * 1000;
            if (string.Equals(shell, "powershell", StringComparison.OrdinalIgnoreCase))
                return RunPs(code, timeoutMs);
            return RunCmd(code, timeoutMs);
        }

        /// <summary>
        /// 用 cmd.exe /c 执行一段命令（用于“命令(cmd)”方式下发的脚本）。
        ///
        /// 编码说明：cmd.exe 及 ipconfig/netsh/systeminfo 这类程序把文本写到控制台时用的是
        /// OEM 代码页（简体中文 = 936/GBK），不是 UTF-8。之前这里按 UTF-8 解码，
        /// 中文输出到控制端/被控端日志就成了乱码。现在改成读「原始字节」，
        /// 交给 <see cref="ConsoleText.Decode"/> 判定 UTF-8 还是 OEM 代码页。
        /// </summary>
        private PsResult RunCmd(string command, int timeoutMs = 60000)
        {
            var result = new PsResult();
            if (SuppressLaunch)
            {
                result.Success = false;
                result.Error = "系统正在重启/关机，已跳过这条命令";
                return result;
            }
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

                if (!p.WaitForExit(Math.Max(1000, timeoutMs)))
                {
                    try { p.Kill(true); } catch { }
                    result.Success = false;
                    result.Error = $"命令执行超时（{Math.Max(1, timeoutMs / 1000)} 秒），已强制结束";
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
