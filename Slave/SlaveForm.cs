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
        private readonly LocalNetChanger _changer = new LocalNetChanger();
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
        private TextBox _txtLog;

        public SlaveForm()
        {
            Text = "局域网配置工具 - 被控端";
            Size = new Size(680, 620);
            MinimumSize = new Size(600, 520);
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

            bool okU = _udp.Start();
            bool okT = _tcp.Start();
            if (!okU || !okT)
            {
                MessageBox.Show($"监听服务启动失败（端口可能被占用）：UDP {AppConfig.UdpPort} / TCP {AppConfig.TcpPort}。\n" +
                                "请确认已以管理员身份运行，且没有其他实例在运行。",
                    "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            LoadAdapters();
            FormClosing += (s, e) =>
            {
                try { _reach.Cleanup(); } catch { }   // 退出时把临时同网段地址删掉
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
        }

        /// <summary>比较两份模板内容是否一致（忽略重复广播）。</summary>
        private static bool TemplateEquals(TemplateModel a, TemplateModel b)
            => string.Equals(a.IpPrefix, b.IpPrefix, StringComparison.Ordinal)
            && string.Equals(a.SubnetMask, b.SubnetMask, StringComparison.Ordinal)
            && string.Equals(a.Gateway, b.Gateway, StringComparison.Ordinal)
            && string.Equals(a.Dns1, b.Dns1, StringComparison.Ordinal)
            && string.Equals(a.Dns2, b.Dns2, StringComparison.Ordinal)
            && string.Equals(a.HostNamePrefix, b.HostNamePrefix, StringComparison.Ordinal)
            && a.IsDhcp == b.IsDhcp;

        private void OnReset()
        {
            _template = null; _masterIp = null; _sessionId = null; _reportedMac = null; _lastTargetIp = null;
            _executing = false;
            try { _reach.Cleanup(); } catch { }   // 会话重置，临时同网段地址也不再需要
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
                MessageBox.Show("无法与控制端建立 TCP 连接：" + _masterIp + ":" + AppConfig.TcpPort +
                                "\n\n探测结果：" + probeErr +
                                (string.IsNullOrEmpty(reachNote) ? "" : "\n互连结果：" + reachNote) +
                                "\n\n排查顺序：\n" +
                                "1) 控制端是否已点【开始发布模板】（TCP 服务随广播启动）；\n" +
                                "2) 控制端防火墙是否放行 TCP 48889；\n" +
                                "3) 两台机器是否在同一网段（跨网段需要路由器放通，虚拟机请用桥接模式）；\n" +
                                "4) 详细路由/本机地址已写入被控端日志。",
                    "无法连接控制端", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                IsDhcp = _template.IsDhcp
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
                LogLine($"已上报：{host} @ {(string.IsNullOrEmpty(ip) ? "DHCP" : ip)}（MAC={adp.Mac} 来源={sendIp}）到控制端 {_masterIp}");
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
        }

        // ---------- 日志 / UI 线程 ----------

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
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(10) };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 176));   // 模板信息
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));    // 本机网卡
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));   // 后缀与预览
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
            root.Controls.Add(gbLog, 0, 3);
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
