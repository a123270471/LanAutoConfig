using LanAutoConfig.Common;

namespace LanAutoConfig.Slave
{
    /// <summary>被控端主窗体：接收模板、输入后缀、预览、上报、接收指令后本地执行。</summary>
    public class SlaveForm : Form
    {
        private const int ResultReportAttempts = 3;
        private const int ResultReportRetryDelayMs = 1500;

        private readonly UdpTemplateListener _udp = new UdpTemplateListener();
        private readonly TcpCommandListener _tcp = new TcpCommandListener();
        private readonly HeartbeatService _hb = new HeartbeatService();
        private readonly LocalNetChanger _changer = new LocalNetChanger();
        /// <summary>静默部署参数（后缀/自动上报/静默/指定网卡）。</summary>
        private readonly SlaveOptions _options;
        private readonly MasterReach _reach;
        private readonly TextLogger _logger;

        private TemplateModel _template;
        private string _masterIp;
        private string _sessionId;
        /// <summary>已上报的网卡 MAC（用于校验收到的执行指令是否发给本机）。</summary>
        private string _reportedMac;
        /// <summary>本次上报的目标 IP（选临时地址时避开它）。</summary>
        private string _lastTargetIp;
        private bool _executing;

        // UI
        private Label _lblStatus, _lblTmplIp, _lblTmplMask, _lblTmplGw, _lblTmplDns, _lblTmplHn, _lblTmplDhcp, _lblSession;
        private ComboBox _cmbAdapter;
        private TextBox _txtSuffix;
        private Label _lblPreviewHost, _lblPreviewIp;
        private Button _btnSubmit;
        private CheckBox _chkAutoReboot;
        /// <summary>跨网段时是否允许临时修改本机网卡（加一个同网段地址）来实现互连。</summary>
        private CheckBox _chkAutoReach;
        /// <summary>开机自动启动（注册表 Run 项）。</summary>
        private CheckBox _chkAutostart;
        private Label _lblAutostartHint;
        /// <summary>程序内部改勾选状态时用，避免又触发一次安装/删除。</summary>
        private bool _suppressAutostart;
        private readonly SlavePrefs _prefs = SlavePrefs.Load();
        private TextBox _txtLog;

        public SlaveForm(SlaveOptions options = null)
        {
            _options = options ?? new SlaveOptions();
            Text = "局域网配置工具 - 被控端";
            Size = new Size(680, 668);
            MinimumSize = new Size(600, 568);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.None;   // 布局尺寸由 UiScale 按 DPI 统一换算，避免与 WinForms 自动缩放叠加
            if (UiScale.Simulated)                // 仅调试：在 100% 屏幕上模拟高 DPI
                Font = new System.Drawing.Font(Font.FontFamily, Font.Size * UiScale.Factor, Font.Style, Font.Unit);
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Directory.CreateDirectory(AppConfig.LogDir);
            _logger = new TextLogger(Path.Combine(AppConfig.LogDir, "slave.log"));
            _reach = new MasterReach(_changer);

            BuildUi();

            _udp.Log += m => SafeUi(() => LogLine(m));
            _udp.OnTemplate += (t, ip, sid) => SafeUi(() => OnTemplate(t, ip, sid));
            _tcp.Log += m => SafeUi(() => LogLine(m));
            _tcp.OnExecute += c => SafeUi(() => OnExecuteCommand(c));
            _tcp.OnReset += () => SafeUi(() => OnReset());
            _tcp.OnReboot += c => SafeUi(() => OnRebootCommand(c));
            _tcp.OnAutostart += c => SafeUi(() => OnAutostartCommand(c));
            _tcp.OnShutdown += c => SafeUi(() => OnShutdownCommand(c));
            _tcp.OnAssignSuffix += c => SafeUi(() => OnAssignSuffixCommand(c));
            _tcp.OnCustomCmd += c => SafeUi(() => OnCustomCommand(c));
            _tcp.OnVerify += c => SafeUi(() => OnVerifyCommand(c));
            _hb.Log += m => SafeUi(() => LogLine(m));

            // 硬件/资产信息后台采集（WMI 偶尔很慢，不能卡住启动）
            HardwareCollector.EnsureStarted();

            bool okU = _udp.Start();
            bool okT = _tcp.Start();
            if (!okU || !okT)
            {
                // 不能用模态框：调试/同机演示时端口常被占用，弹窗会把整个界面和心跳都卡住
                // （静默部署 --silent 更是绝对不能弹，否则装机流程停在这里等人点确定）
                Warn($"监听服务启动失败（端口可能被占用）：UDP {AppConfig.UdpPort} / TCP {AppConfig.TcpPort}。\n" +
                     "请确认已以管理员身份运行，且没有其他实例在运行。", "错误", MessageBoxIcon.Error);
            }
            LoadAdapters();
            InitAutostart();   // 开机自启：界面勾选状态与注册表实际内容对齐（默认开启）
            FormClosing += (s, e) =>
            {
                try { _reach.Cleanup(); } catch { }   // 退出时把临时同网段地址删掉
                try { _hb.Stop(); } catch { }
                _udp.Stop();
                _tcp.Stop();
            };
        }

        private static string ShortId(string sid)
            => string.IsNullOrEmpty(sid) ? "-" : (sid.Length > 8 ? sid.Substring(0, 8) + "…" : sid);

        // ---------- 事件 ----------

        private void OnTemplate(TemplateModel t, string masterIp, string sessionId)
        {
            // 控制端每 2.5s 重复广播同一模板。只有模板/会话真正变化时才重置界面，
            // 否则会每几秒清空一次被控端已输入的后缀编号。
            bool isNewTemplate = _sessionId != sessionId || _template == null || !TemplateEquals(_template, t);

            _template = t;
            _masterIp = masterIp;
            _sessionId = sessionId;
            _tcp.SetSessionId(sessionId);
            _tcp.SetAllowedMaster(masterIp);   // 只接受该控制端的指令

            _lblSession.Text = $"控制端: {masterIp}   会话: {ShortId(sessionId)}";
            _lblTmplIp.Text = t.IsDhcp ? "（DHCP 模式）" : t.IpPrefix;
            _lblTmplMask.Text = t.SubnetMask;
            _lblTmplGw.Text = t.Gateway;
            _lblTmplDns.Text = string.IsNullOrEmpty(t.Dns2) ? t.Dns1 : $"{t.Dns1} / {t.Dns2}";
            _lblTmplHn.Text = t.HostNamePrefix;
            _lblTmplDhcp.Text = t.IsDhcp ? "是（直接恢复 DHCP）" : "否（静态 IP）";

            if (!isNewTemplate) return; // 重复广播，保持已输入的后缀与上报状态

            _txtSuffix.Enabled = true;
            _btnSubmit.Enabled = true;
            _txtSuffix.Text = "";
            _lblStatus.Text = "已收到模板，请输入后缀编号并上报";
            LogLine($"收到模板：控制端={masterIp} 主机名前缀={t.HostNamePrefix} IP前缀={(t.IsDhcp ? "DHCP" : t.IpPrefix)}");

            // 有了控制端地址就能发心跳了（在线状态/进度看板用）
            _hb.SetState("idle");
            UpdateHeartbeatTarget();

            // 静默部署：配置了后缀就自动填写并上报，全程不用人工点
            if (_options.HasSuffix)
            {
                _txtSuffix.Text = _options.Suffix;
                RefreshPreview();
                LogLine($"静默部署：已自动填入后缀 {_options.Suffix}，预览 {_lblPreviewHost.Text} / {_lblPreviewIp.Text}");
                if (_options.AutoReport)
                    BeginInvoke(new Action(() =>
                    {
                        if (_btnSubmit.Enabled)
                        {
                            LogLine("静默部署：自动提交上报");
                            _btnSubmit.PerformClick();
                        }
                    }));
            }
        }

        /// <summary>把“控制端地址 + 会话 + 本机网卡”同步给心跳服务（换网卡/换会话都要更新）。</summary>
        private void UpdateHeartbeatTarget()
        {
            if (string.IsNullOrEmpty(_masterIp) || string.IsNullOrEmpty(_sessionId)) return;
            var adp = _cmbAdapter?.SelectedItem as NetworkAdapterInfo;
            if (adp == null || string.IsNullOrEmpty(adp.Mac)) return;
            string ip = NetworkHelper.GetIpv4ByAdapter(adp.Alias);
            _hb.Configure(_masterIp, _sessionId, adp.Mac, Environment.MachineName, ip);
            _hb.Start();
        }

        /// <summary>控制端要求重启本机（批量重启用）。</summary>
        private void OnRebootCommand(MasterRebootCmdMsg cmd)
        {
            if (!IsForThisMachine(cmd.LocalMac, "重启"))
                return;
            int delay = Math.Max(0, Math.Min(600, cmd.DelaySeconds));
            try
            {
                Process.Start(new ProcessStartInfo("shutdown", $"/r /t {delay} /f")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                _lblStatus.Text = $"已收到重启指令，{delay} 秒后重启";
                LogLine($"已执行重启：{delay} 秒后重启（shutdown /r /t {delay} /f）");
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "重启指令执行失败";
                LogLine("重启失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 控制端批量开关开机自启动：只改注册表 Run 项 + 本地偏好，不动网络配置、不退出程序。
        /// 删除时以“再查一遍还在不在”为准 —— 某些受限令牌下 Remove 会漏掉 HKLM 那一份。
        /// </summary>
        private void OnAutostartCommand(MasterAutostartCmdMsg cmd)
        {
            if (!IsForThisMachine(cmd.LocalMac, "开机自启"))
                return;

            bool ok;
            string detail;
            if (cmd.Enable)
            {
                ok = TryInstallAutostart(out string where, out string err);
                detail = ok ? $"已写入开机自启（{where}）" : "写入失败：" + err;
            }
            else
            {
                Autostart.Remove(out string err);
                string left = Autostart.Query();
                ok = string.IsNullOrEmpty(left);
                detail = ok
                    ? "已删除注册表 Run 项"
                    : "删除失败，自启项仍在（" + (string.IsNullOrEmpty(err) ? left : err) + "）";
            }

            if (ok)
            {
                _prefs.Autostart = cmd.Enable;
                _prefs.Save();
            }

            // 界面按注册表实际状态回填，避免“显示已取消但开机还会跑”
            SetAutostartChecked(!string.IsNullOrEmpty(Autostart.Query()));
            UpdateAutostartHint();
            _lblStatus.Text = ok
                ? (cmd.Enable ? "已按控制端指令设置开机自启" : "已按控制端指令取消开机自启")
                : (cmd.Enable ? "设置开机自启失败" : "取消开机自启失败");
            LogLine($"控制端指令：{(cmd.Enable ? "开启" : "取消")}开机自启 → {detail}");
        }

        /// <summary>
        /// 控制端批量关机：执行 shutdown /s /t N /f。
        /// 与重启共用同一套“目标 MAC 必须匹配”的校验，避免同网段误伤。
        /// </summary>
        private void OnShutdownCommand(MasterShutdownCmdMsg cmd)
        {
            if (!IsForThisMachine(cmd.LocalMac, "关机"))
                return;
            int delay = Math.Max(0, Math.Min(600, cmd.DelaySeconds));
            try
            {
                Process.Start(new ProcessStartInfo("shutdown", $"/s /t {delay} /f")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                _lblStatus.Text = $"已收到关机指令，{delay} 秒后关机";
                LogLine($"已执行关机：{delay} 秒后关机（shutdown /s /t {delay} /f）。如需取消：" +
                        $"在本机执行 shutdown /a");
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "关机指令执行失败";
                LogLine("关机失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 控制端下发后缀编号（按上线顺序统一编号）：填进输入框后自动提交上报。
        /// 只在“已收到模板且还没上报过”时接受 —— 已上报的机器改编号会和控制端列表打架。
        /// </summary>
        private void OnAssignSuffixCommand(MasterAssignSuffixCmdMsg cmd)
        {
            if (!IsForThisMachine(cmd.LocalMac, "编号"))
                return;
            if (_template == null || string.IsNullOrEmpty(_masterIp))
            {
                LogLine("[拒绝] 编号指令：尚未收到模板，无法计算主机名/IP");
                return;
            }
            if (!_btnSubmit.Enabled)
            {
                LogLine($"[拒绝] 编号指令：本机已提交过上报，忽略新编号 {(cmd.Suffix ?? "").Trim()}（如需改号请先让控制端重置会话）");
                _lblStatus.Text = "已上报过，忽略控制端下发的新编号";
                return;
            }

            string suffix = (cmd.Suffix ?? "").Trim();
            if (!Validators.IsValidSuffix(suffix))
            {
                LogLine($"[拒绝] 编号指令：后缀 \"{cmd.Suffix}\" 不合法（仅允许数字或短横线）");
                return;
            }
            if (!ConfigGenerator.Generate(_template, suffix, out string host, out string ip, out string err))
            {
                LogLine($"[拒绝] 编号指令：后缀 {suffix} 无法生成合法配置（{err}）");
                _lblStatus.Text = $"控制端下发的编号 {suffix} 不可用";
                return;
            }

            _txtSuffix.Text = suffix;      // 触发 TextChanged → RefreshPreview
            RefreshPreview();
            _lblStatus.Text = $"已收到控制端编号 {suffix}，正在自动上报…";
            LogLine($"控制端指令：分配后缀编号 {suffix} → {host} @ {(string.IsNullOrEmpty(ip) ? "DHCP" : ip)}，准备自动上报");

            // 延后一拍再点上报：上报过程要做连通性探测（阻塞 UI），不能卡在 TCP 回调里
            BeginInvoke(new Action(() =>
            {
                if (!_btnSubmit.Enabled)
                {
                    LogLine("编号指令：上报按钮已禁用（可能已在别处提交），跳过自动上报");
                    return;
                }
                LogLine("编号指令：自动提交上报");
                _btnSubmit.PerformClick();
            }));
        }

        /// <summary>把一条消息回传给控制端（结果/验收结论等）。</summary>
        private bool SendToMaster(NetMessage msg)
        {
            if (string.IsNullOrEmpty(_masterIp))
            {
                LogLine("[拒绝] 尚未收到模板，不知道控制端地址，无法回传");
                return false;
            }
            string sendIp = null;
            var adp = _cmbAdapter?.SelectedItem as NetworkAdapterInfo;
            if (adp != null) sendIp = NetworkHelper.GetIpv4ByAdapter(adp.Alias);
            bool ok = TcpSend.SendTo(_masterIp, AppConfig.TcpPort, msg, sendIp, out string err);
            if (!ok) LogLine("回传控制端失败：" + err);
            return ok;
        }

        /// <summary>
        /// 控制端下发的自定义命令/脚本：本地执行（powershell 或 cmd），把标准输出/错误/退出码回传。
        /// 无论成功失败都会回传，便于控制端汇总。
        /// </summary>
        private void OnCustomCommand(MasterCustomCmdMsg cmd)
        {
            if (!IsForThisMachine(cmd.LocalMac, "自定义命令"))
                return;
            if (_template == null || string.IsNullOrEmpty(_masterIp))
            {
                LogLine("[拒绝] 自定义命令：尚未收到模板/控制端地址，忽略");
                return;
            }

            int timeout = Math.Max(5, Math.Min(600, cmd.TimeoutSeconds <= 0 ? 60 : cmd.TimeoutSeconds));
            _lblStatus.Text = $"正在执行自定义{(string.Equals(cmd.Shell, "cmd", StringComparison.OrdinalIgnoreCase) ? "命令" : "脚本")}（超时 {timeout}s）…";
            LogLine($"收到自定义命令：shell={cmd.Shell} 超时={timeout}s（长度 {cmd.Script?.Length ?? 0}）");

            PsResult r;
            try
            {
                // 在后台线程执行，避免大脚本卡死 UI；超时由执行器兜底（60s 硬上限）
                r = Task.Run(() => _changer.ExecShell(cmd.Shell, cmd.Script ?? "")).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                r = new PsResult { Success = false, Error = "执行异常：" + ex.Message, ExitCode = -1 };
            }

            var res = new SlaveCustomResultMsg(_sessionId)
            {
                LocalMac = cmd.LocalMac,
                Success = r.Success,
                ExitCode = r.ExitCode,
                StdOut = r.Output ?? "",
                StdErr = r.Error ?? ""
            };
            SendToMaster(res);

            _lblStatus.Text = r.Success ? "自定义命令执行完成" : "自定义命令执行失败";
            LogLine($"自定义命令结果：{(r.Success ? "成功" : "失败")} 退出码={r.ExitCode}");
            if (!string.IsNullOrEmpty(r.Output)) LogLine("命令输出：" + r.Output.Replace(Environment.NewLine, " "));
            if (!string.IsNullOrEmpty(r.Error)) LogLine("命令错误：" + r.Error.Replace(Environment.NewLine, " "));
        }

        /// <summary>
        /// 控制端下发的“验收”指令：上报当前实际的计算机名与 IP，并与期望值比对，
        /// 判断“重启后配置是否已真正生效”。比对失败会写明差异原因。
        /// </summary>
        private void OnVerifyCommand(MasterVerifyCmdMsg cmd)
        {
            if (!IsForThisMachine(cmd.LocalMac, "验收"))
                return;
            if (_template == null || string.IsNullOrEmpty(_masterIp))
            {
                LogLine("[拒绝] 验收指令：尚未收到模板/控制端地址，忽略");
                return;
            }

            string curHost = Environment.MachineName;
            string curIp = "";
            var adp = _cmbAdapter?.SelectedItem as NetworkAdapterInfo;
            if (adp != null) curIp = NetworkHelper.GetIpv4ByAdapter(adp.Alias) ?? "";
            if (string.IsNullOrEmpty(curIp))
            {
                // 没选网卡或取不到，退而用本机首个非 APIPA 地址兜底
                curIp = NetworkHelper.GetLocalIpv4() ?? "";
            }

            bool match = true;
            var notes = new List<string>();
            string expHost = (cmd.ExpectedHostName ?? "").Trim();
            string expIp = (cmd.ExpectedIp ?? "").Trim();
            if (!string.IsNullOrEmpty(expHost) &&
                !string.Equals(curHost, expHost, StringComparison.OrdinalIgnoreCase))
            {
                match = false;
                notes.Add($"主机名不符：实际 {curHost} / 期望 {expHost}");
            }
            if (!string.IsNullOrEmpty(expIp) && !string.Equals(curIp, expIp, StringComparison.OrdinalIgnoreCase))
            {
                match = false;
                notes.Add($"IP 不符：实际 {curIp} / 期望 {expIp}");
            }

            var res = new SlaveVerifyResultMsg(_sessionId)
            {
                LocalMac = cmd.LocalMac,
                CurrentHostName = curHost,
                CurrentIp = curIp,
                ExpectedHostName = expHost,
                ExpectedIp = expIp,
                Match = match,
                Note = string.Join("；", notes)
            };
            SendToMaster(res);
            _lblStatus.Text = match ? "验收通过" : "验收不符";
            LogLine($"验收结果：{(match ? "通过" : "不符")}（实际 {curHost}/{curIp}）{(match ? "" : " - " + string.Join("；", notes))}");
        }

        /// <summary>指令里的目标 MAC 是否是本机（还没上报过时不做限制，与旧行为一致）。</summary>
        private bool IsForThisMachine(string targetMac, string what)
        {
            string target = NetworkHelper.NormalizeMac(targetMac);
            string mine = NetworkHelper.NormalizeMac(_reportedMac);
            if (!string.IsNullOrEmpty(mine) && !string.Equals(target, mine, StringComparison.OrdinalIgnoreCase))
            {
                LogLine($"[拒绝] {what}指令的目标 MAC={targetMac} 与本机不符，已忽略");
                return false;
            }
            return true;
        }

        /// <summary>比较两份模板内容是否一致（忽略重复广播）。</summary>
        private static bool TemplateEquals(TemplateModel a, TemplateModel b)
            => string.Equals(a.IpPrefix, b.IpPrefix, StringComparison.Ordinal)
            && string.Equals(a.SubnetMask, b.SubnetMask, StringComparison.Ordinal)
            && string.Equals(a.Gateway, b.Gateway, StringComparison.Ordinal)
            && string.Equals(a.Dns1, b.Dns1, StringComparison.Ordinal)
            && string.Equals(a.Dns2, b.Dns2, StringComparison.Ordinal)
            && string.Equals(a.HostNamePrefix, b.HostNamePrefix, StringComparison.Ordinal)
            && a.IsDhcp == b.IsDhcp
            && string.Equals(a.Workgroup, b.Workgroup, StringComparison.Ordinal)
            && string.Equals(a.TimeZoneId, b.TimeZoneId, StringComparison.Ordinal)
            && a.SyncTime == b.SyncTime;

        private void OnReset()
        {
            _template = null; _masterIp = null; _sessionId = null; _reportedMac = null; _lastTargetIp = null;
            _executing = false;
            try { _reach.Cleanup(); } catch { }   // 会话重置，临时同网段地址也不再需要
            try { _hb.Stop(); } catch { }
            _tcp.SetSessionId("");
            _tcp.SetAllowedMaster(null);
            _txtSuffix.Enabled = false; _btnSubmit.Enabled = false;
            _txtSuffix.Text = ""; _lblPreviewHost.Text = "-"; _lblPreviewIp.Text = "-";
            _lblStatus.Text = "控制端已重置会话，等待新模板…";
            LogLine("会话已重置，本地状态已清空。");
        }

        private void _txtSuffix_TextChanged(object sender, EventArgs e)
        {
            RefreshPreview();
        }

        private void RefreshPreview()
        {
            if (_template == null || string.IsNullOrWhiteSpace(_txtSuffix.Text))
            {
                _lblPreviewHost.Text = "-";
                _lblPreviewIp.Text = "-";
                return;
            }
            if (ConfigGenerator.Generate(_template, _txtSuffix.Text, out string host, out string ip, out _))
            {
                _lblPreviewHost.Text = host;
                _lblPreviewIp.Text = string.IsNullOrEmpty(ip) ? "（DHCP）" : ip;
            }
            else
            {
                _lblPreviewHost.Text = "非法";
                _lblPreviewIp.Text = "非法";
            }
        }

        private void _btnSubmit_Click(object sender, EventArgs e)
        {
            if (_template == null || string.IsNullOrEmpty(_masterIp))
            {
                _lblStatus.Text = "尚未收到模板，无法上报";
                return;
            }
            string suffix = _txtSuffix.Text.Trim();
            if (!ConfigGenerator.Generate(_template, suffix, out string host, out string ip, out string err))
            {
                MessageBox.Show(string.IsNullOrEmpty(err) ? "后缀不合法或生成的主机名/IP 非法。" : err, "校验",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (_cmbAdapter.SelectedItem is not NetworkAdapterInfo adp || string.IsNullOrEmpty(adp.Mac))
            {
                MessageBox.Show("请选择正确的网卡。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 先判断常规路径通不通（1.5 秒快速探测）；不通且网段不同才临时加同网段地址。
            // 注意：临时地址是否可用必须用真实 TCP 握手验证——ARP 能通不代表端口能通（路由/防火墙都可能挡）。
            _lastTargetIp = ip;
            string currentIp = NetworkHelper.GetIpv4ByAdapter(adp.Alias);
            string mask = _template.SubnetMask ?? "255.255.255.0";
            string sendIp = currentIp;
            bool reachable = TcpSend.CanConnect(_masterIp, AppConfig.TcpPort, currentIp, 1500, out string probeErr);
            string reachNote = null;

            if (!reachable)
            {
                LogLine($"直连控制端 {_masterIp}:{AppConfig.TcpPort} 不通（源 {currentIp ?? "系统自动"}）：{probeErr}");
                if (_chkAutoReach.Checked)
                {
                    LogLine("尝试临时在网卡上添加同网段地址以完成互连…");
                    Func<string, bool> probe = cand => TcpSend.CanConnect(_masterIp, AppConfig.TcpPort, cand, 1500);
                    string punched = _reach.Resolve(adp.Alias, adp.Mac, _masterIp, mask, ip, probe, out reachNote);
                    if (!string.IsNullOrEmpty(reachNote)) LogLine("互连：" + reachNote);
                    if (punched != null) { sendIp = punched; reachable = true; }
                }
                else
                {
                    reachNote = "已关闭“允许临时改网卡互连”，未修改网卡。";
                    LogLine(reachNote);
                }
            }

            if (!reachable)
            {
                // 最后再试一次“系统自动选源”，有些环境绑定主地址反而失败
                if (TcpSend.CanConnect(_masterIp, AppConfig.TcpPort, null, 2000, out string probeErr2))
                {
                    sendIp = currentIp;
                    reachable = true;
                    LogLine("改用系统默认源地址后可以连通。");
                }
                else
                {
                    LogLine($"系统默认源地址也连不上：{probeErr2}");
                }
            }

            if (!reachable)
            {
                try
                {
                    var diag = _changer.DiagnoseRoute(_masterIp);
                    if (!string.IsNullOrEmpty(diag.Output)) LogLine("连通性诊断：\n" + diag.Output);
                }
                catch { }
                string detail = "无法与控制端建立 TCP 连接：" + _masterIp + ":" + AppConfig.TcpPort +
                                "\n\n探测结果：" + probeErr +
                                (string.IsNullOrEmpty(reachNote) ? "" : "\n互连结果：" + reachNote) +
                                "\n\n排查顺序：\n" +
                                "1) 控制端是否已点【开始发布模板】（TCP 服务随广播启动）；\n" +
                                "2) 控制端防火墙是否放行 TCP 48889；\n" +
                                "3) 两台机器是否在同一网段（跨网段需要路由器放通，虚拟机请用桥接模式）；\n" +
                                "4) 详细路由/本机地址已写入被控端日志。";
                _lblStatus.Text = "无法连接控制端（详见日志）";
                LogLine(detail.Replace("\n", " "));
                if (!_options.Silent)
                    MessageBox.Show(detail, "无法连接控制端", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var msg = new SlaveReportMsg(_sessionId)
            {
                LocalMac = adp.Mac,
                LocalSourceIp = sendIp,
                Suffix = suffix,
                TargetHostName = host,
                TargetIp = ip,
                SubnetMask = _template.SubnetMask,
                Gateway = _template.Gateway,
                Dns1 = _template.Dns1,
                Dns2 = _template.Dns2,
                IsDhcp = _template.IsDhcp,
                Hardware = HardwareCollector.Current
            };
            LogLine($"上报目标 {_masterIp}:{AppConfig.TcpPort}，源地址 {sendIp}，网卡 [{adp.Alias}]");
            bool ok = TcpSend.SendTo(_masterIp, AppConfig.TcpPort, msg, sendIp, out string sendErr);
            if (!ok)
            {
                LogLine($"绑定源地址上报失败：{sendErr}，改用系统默认源地址重试…");
                ok = TcpSend.SendTo(_masterIp, AppConfig.TcpPort, msg, null, out string sendErr2);
                if (!ok)
                {
                    sendErr = sendErr2;
                    LogLine($"系统默认源地址也失败：{sendErr2}");
                    try
                    {
                        var diag = _changer.DiagnoseRoute(_masterIp);
                        if (!string.IsNullOrEmpty(diag.Output)) LogLine("连通性诊断：\n" + diag.Output);
                    }
                    catch { }
                }
                else LogLine("改用系统默认源地址后上报成功。");
            }
            if (ok)
            {
                _reportedMac = adp.Mac;
                _btnSubmit.Enabled = false;
                _lblStatus.Text = "已上报，等待管理员下发指令…";
                _hb.SetState("reported");
                LogLine($"已上报：{host} @ {(string.IsNullOrEmpty(ip) ? "DHCP" : ip)}（MAC={adp.Mac} 来源={sendIp}）到控制端 {_masterIp}");
                var hw = HardwareCollector.Current;
                if (hw != null)
                    LogLine($"已附带硬件信息：{hw.Cpu} / 内存 {hw.MemoryDisplay} / 磁盘 {hw.DiskDisplay} / SN {hw.SerialNumber}");
            }
            else
            {
                _lblStatus.Text = "上报失败（无法连接控制端），请重试";
                LogLine($"上报失败：{sendErr}");
                LogLine("排查建议：在被控端执行 Test-NetConnection " + _masterIp + " -Port " + AppConfig.TcpPort +
                        "；并确认控制端已点【开始发布模板】且防火墙放行 TCP 48889。");
            }
        }

        /// <summary>校验指令是否确实发给本机，避免被同网段伪造的广播/指令误伤。</summary>
        private void OnExecuteCommand(MasterExecuteCmdMsg cmd)
        {
            string target = NetworkHelper.NormalizeMac(cmd.LocalMac);
            string mine = NetworkHelper.NormalizeMac(_reportedMac);
            bool match;
            if (!string.IsNullOrEmpty(mine))
            {
                match = string.Equals(target, mine, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                var localMacs = NetworkHelper.GetLocalMacs();
                match = localMacs.Count == 0 || localMacs.Contains(target);
            }
            if (!match)
            {
                LogLine($"[拒绝] 指令的目标 MAC={cmd.LocalMac} 与本机不符，已忽略");
                return;
            }
            _ = RunExecuteAsync(cmd);
        }

        /// <summary>包装 ExecuteAsync：async void 式的“发射后不管”不能让异常悄悄消失。</summary>
        private async Task RunExecuteAsync(MasterExecuteCmdMsg cmd)
        {
            try
            {
                await ExecuteAsync(cmd);
            }
            catch (Exception ex)
            {
                _executing = false;
                _lblStatus.Text = "执行过程异常";
                LogLine("执行过程异常：" + ex.Message);
            }
        }

        private async Task ExecuteAsync(MasterExecuteCmdMsg cmd)
        {
            if (_executing) return;
            _executing = true;

            // 固化本次执行的会话与控制端地址：执行期间模板可能变化，结果必须回传到发起方那一次会话
            string execSession = _sessionId ?? "";
            string masterIp = _masterIp;
            string ifAlias = (_cmbAdapter.SelectedItem as NetworkAdapterInfo)?.Alias;
            var sb = new StringBuilder();
            bool success = true;

            _lblStatus.Text = "收到执行指令，正在本机执行修改…";
            _hb.SetState("executing");
            LogLine($"开始执行：主机名={cmd.TargetHostName} 地址={(cmd.IsDhcp ? "DHCP" : cmd.TargetIp)} 网卡={ifAlias ?? "(未选择)"}");

            try
            {
                if (string.IsNullOrEmpty(ifAlias))
                {
                    success = false;
                    sb.AppendLine("未选择网卡，无法执行");
                }
                else
                {
                    PsResult r1 = null, r2 = null;
                    await Task.Run(() =>
                    {
                        r1 = cmd.IsDhcp
                            ? _changer.SetDhcp(ifAlias)
                            : _changer.SetStaticIp(ifAlias, cmd.TargetIp, cmd.SubnetMask, cmd.Gateway, cmd.Dns1, cmd.Dns2);
                        r2 = _changer.RenameComputer(cmd.TargetHostName);
                    });

                    if (r1 == null || r2 == null) { success = false; sb.AppendLine("内部错误：执行结果为空"); }
                    else
                    {
                        if (r1.Success) sb.AppendLine("设置 IP/DNS 成功");
                        else { success = false; sb.AppendLine("设置 IP/DNS 失败：" + r1.Error); }

                        if (r2.Success) sb.AppendLine("重命名成功（重启后完全生效）");
                        else { success = false; sb.AppendLine("重命名失败：" + r2.Error); }

                        // PowerShell 的警告/输出一并写到本地日志，便于排查
                        if (!string.IsNullOrEmpty(r1.Output)) sb.AppendLine("PS 输出：" + r1.Output);
                        if (!string.IsNullOrEmpty(r1.Error) && r1.Success) sb.AppendLine("PS 警告：" + r1.Error);
                    }
                }

                string actualIp = cmd.IsDhcp
                    ? (NetworkHelper.GetIpv4ByAdapter(ifAlias) ?? "")
                    : (cmd.TargetIp ?? "");

                var resultMsg = new SlaveResultReportMsg(execSession)
                {
                    LocalMac = cmd.LocalMac,
                    Success = success,
                    ErrorMsg = success ? "" : sb.ToString().Trim(),
                    AfterHostName = cmd.TargetHostName,
                    AfterIp = actualIp
                };

                // 改 IP / 改名后本机网络可能短暂不可用：先重新解析互连地址（必要时再次临时同网段），再重试回传
                bool reported = false;
                bool autoReach = _chkAutoReach.Checked;   // 在 UI 线程取值，后台线程不再碰控件
                if (!string.IsNullOrEmpty(masterIp))
                {
                    string mask = _template?.SubnetMask ?? "255.255.255.0";
                    string preferIp = _lastTargetIp ?? cmd.TargetIp;   // 优先用目标 IP 当临时地址
                    reported = await Task.Run(() =>
                    {
                        string sendIp = null;
                        if (autoReach)
                        {
                            Func<string, bool> probe = cand => TcpSend.CanConnect(masterIp, AppConfig.TcpPort, cand, 1500);
                            sendIp = _reach.Resolve(ifAlias, _reportedMac, masterIp, mask, preferIp, probe, out string note);
                            if (!string.IsNullOrEmpty(note)) SafeUi(() => LogLine("互连：" + note));
                        }
                        if (string.IsNullOrEmpty(sendIp)) sendIp = NetworkHelper.GetIpv4ByAdapter(ifAlias);
                        resultMsg.LocalSourceIp = sendIp;   // 控制端据此刷新列表里的来源地址
                        return ReportResultWithRetry(masterIp, sendIp, resultMsg);
                    });
                }

                _lblStatus.Text = success ? "执行成功" : "执行失败";
                _hb.SetState(success ? "success" : "failed");
                string summary = success ? "执行成功。" : $"执行失败：{resultMsg.ErrorMsg}";
                LogLine(summary + (reported ? " 结果已回传控制端。" : " 结果回传控制端失败（请人工确认）。"));
                if (!string.IsNullOrEmpty(sb.ToString())) LogLine("执行明细：" + sb.ToString().Replace(Environment.NewLine, " "));

                // 无人值守语义：执行完不弹任何对话框，成功且勾选了自动重启就直接重启。
                if (success)
                {
                    if (_chkAutoReboot.Checked)
                    {
                        _lblStatus.Text = "执行成功，正在自动重启…";
                        LogLine("已按设置自动重启（5 秒后）。");
                        await Task.Run(() => _changer.Reboot(true));
                    }
                    else
                    {
                        _lblStatus.Text = "执行成功（未勾选自动重启，请手动重启生效）";
                        LogLine("未勾选“执行完成后自动重启”，跳过重启；计算机名需重启后生效。");
                    }
                }
                else
                {
                    _lblStatus.Text = "执行失败：" + (resultMsg.ErrorMsg ?? "").Split('\n')[0];
                }
            }
            finally
            {
                _executing = false;
            }
        }

        private bool ReportResultWithRetry(string masterIp, string sendIp, SlaveResultReportMsg msg)
        {
            string lastError = null;
            for (int i = 0; i < ResultReportAttempts; i++)
            {
                if (TcpSend.SendTo(masterIp, AppConfig.TcpPort, msg, sendIp, out lastError)) return true;
                if (i < ResultReportAttempts - 1) Thread.Sleep(ResultReportRetryDelayMs);
            }
            if (!string.IsNullOrEmpty(lastError)) SafeUi(() => LogLine("结果回传失败原因：" + lastError));
            return false;
        }

        // ---------- 网卡 ----------

        private void LoadAdapters()
        {
            var adapters = NetworkHelper.GetAdapters();
            _cmbAdapter.Items.Clear();
            foreach (var a in adapters) _cmbAdapter.Items.Add(a);
            // 默认选中带本机 IP 的网卡（物理网卡优先，由 NetworkHelper 排序）
            string localIp = NetworkHelper.GetLocalIpv4();
            for (int i = 0; i < _cmbAdapter.Items.Count; i++)
            {
                if (_cmbAdapter.Items[i] is NetworkAdapterInfo a &&
                    string.Equals(NetworkHelper.GetIpv4ByAdapter(a.Alias), localIp, StringComparison.Ordinal))
                { _cmbAdapter.SelectedIndex = i; break; }
            }
            if (_cmbAdapter.SelectedIndex < 0 && _cmbAdapter.Items.Count > 0) _cmbAdapter.SelectedIndex = 0;

            // 静默部署：指定了网卡就用指定的（按别名精确匹配）
            if (!string.IsNullOrEmpty(_options.AdapterAlias))
            {
                for (int i = 0; i < _cmbAdapter.Items.Count; i++)
                {
                    if (_cmbAdapter.Items[i] is NetworkAdapterInfo a &&
                        string.Equals(a.Alias, _options.AdapterAlias, StringComparison.OrdinalIgnoreCase))
                    { _cmbAdapter.SelectedIndex = i; break; }
                }
            }
        }

        // ---------- 开机自启 ----------

        /// <summary>启动时对齐界面与注册表：默认开启；操作员取消过就保持取消。</summary>
        private void InitAutostart()
        {
            // 优先级：--remove-autostart > --install-autostart > 上次在界面上的选择 > 默认开启
            bool want;
            if (_options.RemoveAutostart)
            {
                want = false;
                _prefs.Autostart = false;   // 记下来，免得下次启动又被默认值装回去
                _prefs.Save();
            }
            else want = _options.InstallAutostart || (_prefs.Autostart ?? true);

            string cmd = Autostart.Query();
            bool installed = !string.IsNullOrEmpty(cmd);
            if (installed && !Autostart.PointsToCurrentExe(cmd))
            {
                LogLine($"开机自启原指向 {cmd}（不是本程序当前位置），按当前路径重写");
                installed = false;
            }

            if (want && !installed)
            {
                if (TryInstallAutostart(out _, out string err)) installed = true;
                else
                {
                    LogLine("开机自启写入失败，勾选状态置为关闭：" + err);
                    want = false;
                }
            }
            else if (!want && installed)
            {
                // 操作员上次主动关掉了：以界面偏好为准，把残留的 Run 项清掉
                Autostart.Remove(out string err);
                installed = false;
                LogLine("按上次的选择关闭开机自启" + (string.IsNullOrEmpty(err) ? "" : "（" + err + "）"));
            }

            SetAutostartChecked(installed);
            UpdateAutostartHint();
        }

        /// <summary>程序内部改勾选状态，不触发安装/删除。</summary>
        private void SetAutostartChecked(bool value)
        {
            _suppressAutostart = true;
            try { _chkAutostart.Checked = value; }
            finally { _suppressAutostart = false; }
        }

        private void _chkAutostart_CheckedChanged(object sender, EventArgs e)
        {
            if (_suppressAutostart) return;

            if (_chkAutostart.Checked)
            {
                if (!TryInstallAutostart(out _, out string err))
                {
                    LogLine("设置开机自启失败：" + err);
                    if (!_options.Silent)
                        MessageBox.Show("写入开机自启失败（可能被安全软件拦截，或当前账户没有注册表权限）：\n\n" + err +
                                        "\n\n可以试试右键“以管理员身份运行”后重新勾选。",
                            "开机自启", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    SetAutostartChecked(false);
                    _prefs.Autostart = false;
                    _prefs.Save();
                    UpdateAutostartHint();
                    return;
                }
            }
            else
            {
                bool removed = Autostart.Remove(out string err);
                LogLine(removed
                    ? "已取消开机自启（删除注册表 Run 项）"
                    : "已取消开机自启（自启项本来就不存在）" + (string.IsNullOrEmpty(err) ? "" : "：" + err));
            }

            _prefs.Autostart = _chkAutostart.Checked;
            _prefs.Save();
            UpdateAutostartHint();
        }

        private bool TryInstallAutostart(out string where, out string error)
        {
            string args = Autostart.BuildArgs(_options);
            bool ok = Autostart.Install(args, out where, out error);
            if (ok)
                LogLine($"已设置开机自启（{where}）：\"{Application.ExecutablePath}\"" +
                        (string.IsNullOrEmpty(args) ? "" : " " + args));
            return ok;
        }

        private void UpdateAutostartHint()
        {
            if (_lblAutostartHint == null || _lblAutostartHint.IsDisposed) return;
            string cmd = Autostart.Query();
            _lblAutostartHint.Text = string.IsNullOrEmpty(cmd)
                ? "当前未设置：开机不会自动运行（勾选即写入）"
                : "开机命令：" + cmd;
        }

        // ---------- 日志 / UI 线程 ----------

        /// <summary>提示操作员；静默部署（--silent）时只写日志，避免装机流程被弹窗卡住。</summary>
        private void Warn(string msg, string title = "提示", MessageBoxIcon icon = MessageBoxIcon.Warning)
        {
            if (_options != null && _options.Silent)
            {
                LogLine("[提示] " + (msg ?? "").Replace("\r", " ").Replace("\n", " "));
                return;
            }
            MessageBox.Show(msg, title, MessageBoxButtons.OK, icon);
        }

        private void LogLine(string msg)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            _txtLog.AppendText(line + Environment.NewLine);
            _logger.Info(msg);
        }

        private void SafeUi(Action a)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke(a); } catch { } }
            else a();
        }

        // ---------- UI 构建 ----------

        private void BuildUi()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(10) };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 176));   // 模板信息
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));    // 本机网卡
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));   // 后缀与预览
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));    // 开机启动
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));    // 状态与日志

            // ---------- 模板信息 ----------
            var gbTmpl = new GroupBox { Text = "模板信息（来自控制端广播）", Dock = DockStyle.Fill };
            var tbl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 4, Padding = new Padding(12, 6, 12, 4) };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            for (int i = 0; i < 4; i++) tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

            _lblSession = NewValueLabel();
            _lblTmplHn = NewValueLabel();
            _lblTmplMask = NewValueLabel();
            _lblTmplIp = NewValueLabel();
            _lblTmplGw = NewValueLabel();
            _lblTmplDns = NewValueLabel();
            _lblTmplDhcp = NewValueLabel();

            AddInfo(tbl, 0, 0, "控制端 / 会话：", _lblSession);
            tbl.SetColumnSpan(_lblSession, 3);
            AddInfo(tbl, 1, 0, "计算机名前缀：", _lblTmplHn);
            AddInfo(tbl, 1, 2, "子网掩码：", _lblTmplMask);
            AddInfo(tbl, 2, 0, "IP 前缀：", _lblTmplIp);
            AddInfo(tbl, 2, 2, "网关：", _lblTmplGw);
            AddInfo(tbl, 3, 0, "DNS：", _lblTmplDns);
            AddInfo(tbl, 3, 2, "DHCP：", _lblTmplDhcp);
            gbTmpl.Controls.Add(tbl);

            // ---------- 本机网卡 ----------
            var gbNic = new GroupBox { Text = "本机网卡", Dock = DockStyle.Fill };
            var pnlNic = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 7, 12, 0), WrapContents = false };
            pnlNic.Controls.Add(new Label { Text = "选择网卡：", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
            _cmbAdapter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
            _cmbAdapter.SelectedIndexChanged += (s, e) => UpdateHeartbeatTarget();
            pnlNic.Controls.Add(_cmbAdapter);
            _chkAutoReach = new CheckBox
            {
                Text = "允许临时改网卡互连",
                AutoSize = true,
                Checked = true,
                Margin = new Padding(10, 6, 0, 0)
            };
            var tip = new ToolTip();
            tip.SetToolTip(_chkAutoReach,
                "勾选后：当本机与控制端不在同一网段且直连不通时，会临时在本机网卡上追加一个控制端网段的地址，" +
                "用于完成上报/接收指令；执行配置时该地址会随网卡重配一起清除，退出程序或重置会话也会删除。\n" +
                "改动网卡可能使本机网络短暂中断（几秒），不需要该功能时请取消勾选。");
            pnlNic.Controls.Add(_chkAutoReach);
            gbNic.Controls.Add(pnlNic);

            // ---------- 后缀编号与预览 ----------
            var gbSuffix = new GroupBox { Text = "后缀编号与预览（主机名 = 前缀 + 后缀；IP 末段 = 后缀 + 1）", Dock = DockStyle.Fill };
            var pnlSuffix = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(12, 9, 12, 0), WrapContents = false };
            pnlSuffix.Controls.Add(new Label { Text = "后缀编号：", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
            _txtSuffix = new TextBox { Width = 110, Enabled = false };
            _txtSuffix.TextChanged += _txtSuffix_TextChanged;
            pnlSuffix.Controls.Add(_txtSuffix);
            _btnSubmit = new Button { Text = "提交上报", Width = 100, Height = 30, Enabled = false, Margin = new Padding(10, 0, 0, 0) };
            _btnSubmit.Click += _btnSubmit_Click;
            pnlSuffix.Controls.Add(_btnSubmit);
            _chkAutoReboot = new CheckBox { Text = "执行完成后自动重启（默认开，无人值守用）", AutoSize = true, Checked = true, Margin = new Padding(14, 7, 0, 0) };
            pnlSuffix.Controls.Add(_chkAutoReboot);

            var pnlPreview = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(12, 4, 12, 0), WrapContents = false };
            pnlPreview.Controls.Add(new Label { Text = "预览 → 完整主机名：", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
            _lblPreviewHost = new Label
            {
                Text = "-", AutoSize = true,
                Font = new System.Drawing.Font("Microsoft YaHei UI", 10F, System.Drawing.FontStyle.Bold),
                ForeColor = System.Drawing.Color.DarkBlue,
                Margin = new Padding(0, 4, 24, 0)
            };
            pnlPreview.Controls.Add(_lblPreviewHost);
            pnlPreview.Controls.Add(new Label { Text = "目标 IP：", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
            _lblPreviewIp = new Label
            {
                Text = "-", AutoSize = true,
                Font = new System.Drawing.Font("Microsoft YaHei UI", 10F, System.Drawing.FontStyle.Bold),
                ForeColor = System.Drawing.Color.DarkGreen,
                Margin = new Padding(0, 4, 0, 0)
            };
            pnlPreview.Controls.Add(_lblPreviewIp);
            gbSuffix.Controls.Add(pnlPreview);
            gbSuffix.Controls.Add(pnlSuffix);

            // ---------- 开机启动 ----------
            var gbStartup = new GroupBox { Text = "开机启动（写注册表 Run 项：登录后自动运行本程序）", Dock = DockStyle.Fill };
            var pnlStartup = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 8, 12, 0), WrapContents = false };
            _chkAutostart = new CheckBox
            {
                Text = "开机自动启动",
                AutoSize = true,
                Checked = true,          // 默认开启；实际状态以注册表为准，由 InitAutostart() 校正
                Margin = new Padding(0, 5, 14, 0)
            };
            _chkAutostart.CheckedChanged += _chkAutostart_CheckedChanged;
            var tipStartup = new ToolTip();
            tipStartup.SetToolTip(_chkAutostart,
                "勾选：写入开机自启（优先 HKCU，失败再写 HKLM），登录 Windows 后自动运行本程序，" +
                "并带上本次启动的参数（--silent / --suffix / --adapter），重启后仍是同一套流程。\n" +
                "取消勾选：删除自启项，开机不再自动运行。\n" +
                "提交上报本身不会关闭程序，程序会继续等待控制端下发执行指令。");
            pnlStartup.Controls.Add(_chkAutostart);

            _lblAutostartHint = new Label
            {
                Text = "（读取中…）",
                AutoSize = false,
                AutoEllipsis = true,
                Width = 430,
                Height = 20,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                ForeColor = System.Drawing.Color.DimGray,
                Margin = new Padding(0, 6, 0, 0)
            };
            pnlStartup.Controls.Add(_lblAutostartHint);
            gbStartup.Controls.Add(pnlStartup);

            // ---------- 状态与日志 ----------
            var gbLog = new GroupBox { Text = "状态与本地日志", Dock = DockStyle.Fill };
            var pnlStatus = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(12, 6, 12, 0), WrapContents = false };
            _lblStatus = new Label
            {
                Text = "正在监听控制端广播…",
                AutoSize = true,
                Font = new System.Drawing.Font("Microsoft YaHei UI", 9.5F, System.Drawing.FontStyle.Bold)
            };
            pnlStatus.Controls.Add(_lblStatus);
            _txtLog = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                ReadOnly = true,
                WordWrap = false,
                Dock = DockStyle.Fill,
                BackColor = System.Drawing.Color.White,
                Font = new System.Drawing.Font("Consolas", 9F)
            };
            gbLog.Controls.Add(_txtLog);
            gbLog.Controls.Add(pnlStatus);

            root.Controls.Add(gbTmpl, 0, 0);
            root.Controls.Add(gbNic, 0, 1);
            root.Controls.Add(gbSuffix, 0, 2);
            root.Controls.Add(gbStartup, 0, 3);
            root.Controls.Add(gbLog, 0, 4);
            Controls.Add(root);

            UiScale.Apply(this);   // 高 DPI：把上面写死的像素常量按系统缩放比例放大
        }

        private static Label NewValueLabel() => new Label
        {
            Text = "-",
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft
        };

        /// <summary>向 4 列表格按显式 (row, col) 添加“标签 + 值”一对。</summary>
        private static void AddInfo(TableLayoutPanel t, int row, int col, string label, Label value)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = false, Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleLeft }, col, row);
            t.Controls.Add(value, col + 1, row);
            value.AutoSize = false; value.Dock = DockStyle.Fill; value.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
        }
    }
}
