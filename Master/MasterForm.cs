using LanAutoConfig.Common;

namespace LanAutoConfig.Master
{
    /// <summary>控制端主窗体：模板配置、终端列表、日志报表、下发执行。</summary>
    public class MasterForm : Form
    {
        /// <summary>下发后多久没收到结果就判定超时（覆盖被控端 PowerShell 60s 超时 + 网络切换）。</summary>
        private const int SweepTimeoutSeconds = 120;
        private const int SweepIntervalMs = 5000;

        private readonly SessionManager _session = new SessionManager();
        private readonly SqliteLogStore _db;
        private readonly TextLogger _logger;
        private readonly Blacklist _blacklist;
        private readonly Whitelist _whitelist;
        private readonly UdpBroadcastService _broadcast = new UdpBroadcastService();
        private readonly TcpServerService _server = new TcpServerService();
        private readonly System.Windows.Forms.Timer _sweepTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _onlineTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _saveTimer = new System.Windows.Forms.Timer();

        private TabControl _tabs;
        private StatusStrip _status;
        private ToolStripStatusLabel _lblReported, _lblPending, _lblSuccess, _lblFailed, _lblConflict, _lblBroadcast, _lblOnline;

        // Tab1 模板配置
        private TabPage _tabTemplates;
        private TextBox _txtIpPrefix, _txtMask, _txtGw, _txtDns1, _txtDns2, _txtHnPrefix;
        private Label _lblBlacklist, _lblDhcpPool;
        private CheckBox _chkDhcp, _chkDhcpServer;
        private Button _btnStart, _btnStop, _btnReset, _btnManageBlacklist, _btnWhitelist;
        private Label _lblSession;
        private ComboBox _cmbAdapter;

        // Tab2 终端列表
        private DataGridView _grid;
        private Button _btnDispatch, _btnExportTerminals;
        private Label _lblGridHint;

        // Tab3 日志
        private TextBox _txtLog;
        private Button _btnExportLog, _btnBlacklist, _btnClearLog;

        // 模板方案库 UI 控件
        private ComboBox _cmbTemplateScheme;
        private Button _btnSchemeSave, _btnSchemeApply, _btnSchemeDelete;

        // DHCP 服务（可选）
        private readonly DhcpServer _dhcp = new DhcpServer();

        // ---- 本次新增功能相关字段 ----
        /// <summary>模板方案库（多套命名模板，落盘 JSON）。</summary>
        private readonly TemplateLibrary _templateLib = new TemplateLibrary(AppConfig.TemplateLibraryPath);
        /// <summary>已下发重启、等待其重新上线后自动验收的 MAC 集合。</summary>
        private readonly HashSet<string> _rebootSent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>分批错峰重启的定时器（按间隔逐批发重启指令）。</summary>
        private readonly System.Windows.Forms.Timer _staggerTimer = new System.Windows.Forms.Timer();
        /// <summary>每台终端的上一次“在线”状态（用于检测离线→上线跳变，触发重启后自动验收）。</summary>
        private readonly Dictionary<string, bool> _onlineState = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        /// <summary>名单拒绝日志的节流表：同一 MAC 60 秒内只记一条，避免心跳把日志刷满（曾实测一次 111KB 全是拒绝）。</summary>
        private readonly Dictionary<string, DateTime> _rejectLogAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        /// <summary>打开的“自定义命令”对话框订阅此事件以接收回传结果。</summary>
        private event Action<SlaveCustomResultMsg> _customResultHandlers;
        /// <summary>分批重启待发送队列（由 _staggerTimer 逐批取出）。</summary>
        private Queue<SlaveSessionItem> _rebootQueue;
        private int _rebootBatchSize;

        private volatile bool _broadcasting;
        private volatile bool _dispatching;
        /// <summary>本次广播使用的本机 IPv4：下发指令时也用它作为来源地址，保证被控端的来源校验能通过。</summary>
        private string _broadcastIp;

        public MasterForm()
        {
            Text = "局域网配置工具 - 控制端";
            Size = new Size(1020, 720);
            MinimumSize = new Size(900, 600);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.None;   // 布局尺寸由 UiScale 按 DPI 统一换算，避免与 WinForms 自动缩放叠加
            if (UiScale.Simulated)                // 仅调试：在 100% 屏幕上模拟高 DPI
                Font = new System.Drawing.Font(Font.FontFamily, Font.Size * UiScale.Factor, Font.Style, Font.Unit);
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Directory.CreateDirectory(AppConfig.LogDir);
            _db = new SqliteLogStore(AppConfig.MasterDbPath);
            _blacklist = new Blacklist(AppConfig.BlacklistPath);
            _whitelist = new Whitelist(AppConfig.WhitelistPath);
            _logger = new TextLogger(Path.Combine(AppConfig.LogDir, "master.log"));
            _session.NewSession(); // 初始化一个会话 ID

            BuildUi();
            LoadBroadcastAdapters();
            LoadSettings();   // 恢复上次的模板参数、会话与终端列表（重启不丢）

            // 模板参数改动也做合并保存（3 秒节流）
            foreach (var tb in new[] { _txtIpPrefix, _txtMask, _txtGw, _txtDns1, _txtDns2, _txtHnPrefix })
                tb.TextChanged += (s2, e2) => ScheduleSave();
            _chkDhcp.CheckedChanged += (s2, e2) => ScheduleSave();
            _chkDhcpServer.CheckedChanged += (s2, e2) => ScheduleSave();
            _cmbAdapter.SelectedIndexChanged += (s2, e2) => ScheduleSave();

            _session.Changed += () => SafeUi(() => { RefreshGrid(); UpdateStatusBar(); ScheduleSave(); });
            _broadcast.Log += m => SafeUi(() => LogLine(m));
            _server.Log += m => SafeUi(() => LogLine(m));
            _server.OnSlaveReport += r => SafeUi(() => OnSlaveReport(r));
            _server.OnResultReport += r => SafeUi(() => OnResultReport(r));
            _server.OnHeartbeat += r => SafeUi(() => OnHeartbeat(r));
            _server.OnCustomResult += r => SafeUi(() => OnCustomResult(r));
            _server.OnVerifyResult += r => SafeUi(() => OnVerifyResult(r));

            // 单独的心跳刷新定时器：只重画“在线”列与状态栏，不整表刷新（否则每 10 秒闪一次、还打断编辑）
            _onlineTimer.Interval = 5000;
            _onlineTimer.Tick += (s, e) => { RefreshOnlineCells(); UpdateStatusBar(); };
            _onlineTimer.Start();

            // 定时回收“执行中但一直没结果”的终端，避免永远卡在执行中
            _sweepTimer.Interval = SweepIntervalMs;
            _sweepTimer.Tick += (s, e) => SweepStaleExecuting();
            _sweepTimer.Start();

            // 配置/终端列表落盘：改动后 3 秒合并写一次，退出时再写一次
            _saveTimer.Interval = 3000;
            _saveTimer.Tick += (s, e) => { _saveTimer.Stop(); SaveSettings(); };

            FormClosing += (s, e) =>
            {
                try { _sweepTimer.Stop(); } catch { }
                try { _onlineTimer.Stop(); } catch { }
                try { _saveTimer.Stop(); } catch { }
                try { _staggerTimer.Stop(); } catch { }
                SaveSettings();   // 退出前把模板参数与终端列表记下来
                _broadcast.Stop();
                _server.Stop();
                _dhcp.Stop();
            };
        }

        private static string ShortId(string sid)
            => string.IsNullOrEmpty(sid) ? "-" : (sid.Length > 8 ? sid.Substring(0, 8) + "…" : sid);

        // ---------- 事件处理 ----------

        private void OnSlaveReport(SlaveReportMsg m)
        {
            if (m.SessionId != _session.SessionId)
            {
                LogLine($"[忽略] 会话不一致的上报：{m.LocalMac}（可能是上一批次遗留）");
                return;
            }
            if (_blacklist.IsBlocked(m.LocalMac, m.LocalSourceIp))
            {
                LogRejectThrottled("blk:" + m.LocalMac, $"[黑名单拒绝] MAC={m.LocalMac} IP={m.LocalSourceIp}");
                return;
            }
            if (!_whitelist.Allows(m.LocalMac))
            {
                LogRejectThrottled("wl:" + m.LocalMac, $"[白名单拒绝] MAC={m.LocalMac}（未在 whitelist.txt 登记）");
                return;
            }
            _session.AddOrUpdate(m);
            LogLine($"新增/更新终端：{m.TargetHostName} @ {(m.IsDhcp ? "DHCP" : m.TargetIp)}（MAC={m.LocalMac} 来源={m.LocalSourceIp}）");
            var hw = m.Hardware;
            if (hw != null)
                LogLine($"资产信息：{hw.Cpu} / 内存 {hw.MemoryDisplay} / 磁盘 {hw.DiskDisplay} / SN {hw.SerialNumber} / {hw.Manufacturer} {hw.Model}");
        }

        /// <summary>收到心跳：刷新在线时间；第一次见到的 MAC 建一行“在线未上报”。</summary>
        private void OnHeartbeat(SlaveHeartbeatMsg hb)
        {
            if (hb.SessionId != _session.SessionId) return;
            if (_blacklist.IsBlocked(hb.LocalMac, hb.LocalIp))
            {
                LogRejectThrottled("blk:" + hb.LocalMac, $"[黑名单拒绝] 心跳 MAC={hb.LocalMac} IP={hb.LocalIp}");
                return;
            }
            if (!_whitelist.Allows(hb.LocalMac))
            {
                LogRejectThrottled("wl:" + hb.LocalMac, $"[白名单拒绝] 心跳 MAC={hb.LocalMac}（未在 whitelist.txt 登记）");
                return;
            }
            if (_session.TouchHeartbeat(hb))
            {
                LogLine($"发现新终端（仅心跳）：MAC={hb.LocalMac} IP={hb.LocalIp} 主机名={hb.HostName}");
                RefreshGrid();
            }
            else
            {
                // 已有行：立刻把「在线」格刷出来。整表刷新会打断选择/编辑，只改这一格；
                // 不这么做就要等 5 秒的心跳刷新定时器，明明已经在线却显示“离线”。
                UpdateOnlineCell(hb.LocalMac);
                UpdateStatusBar();
            }

            // 重启后自动验收：本机刚下发过重启、现在又上线了（检测到这次心跳），自动补一条验收指令
            if (_rebootSent.Contains(hb.LocalMac))
            {
                _rebootSent.Remove(hb.LocalMac);
                var it = _session.GetByMac(hb.LocalMac);
                if (it != null)
                {
                    SendVerifyTo(it);
                }
            }
            // 记录在线状态，供后续检测“离线→上线”跳变（预留）
            _onlineState[hb.LocalMac] = true;
        }

        /// <summary>
        /// 名单拒绝日志节流：同一 MAC 60 秒内只写一条。
        /// 心跳每 10 秒一条、被拒绝时仍然会来，不节流会把日志刷成几千行，真正的问题反而被埋掉。
        /// </summary>
        private void LogRejectThrottled(string key, string text)
        {
            lock (_rejectLogAt)
            {
                if (_rejectLogAt.TryGetValue(key, out DateTime last) && (DateTime.Now - last).TotalSeconds < 60)
                    return;
                if (_rejectLogAt.Count > 2000) _rejectLogAt.Clear();   // 防止极端情况下无限增长
                _rejectLogAt[key] = DateTime.Now;
            }
            LogLine(text);
        }

        // ---------- 自定义命令 / 验收 ----------

        /// <summary>被控端回传的自定义命令执行结果：写日志并转发给打开的对话框。</summary>
        private void OnCustomResult(SlaveCustomResultMsg m)
        {
            var it = _session.GetByMac(m.LocalMac);
            string name = it?.TargetHostName ?? m.LocalMac;
            LogLine($"[自定义命令结果] {name}（{m.LocalMac}）：{(m.Success ? "成功" : "失败")} 退出码={m.ExitCode}");
            if (!string.IsNullOrEmpty(m.StdOut)) LogLine("  输出：" + m.StdOut.Replace("\r\n", " ").Replace("\n", " "));
            if (!string.IsNullOrEmpty(m.StdErr)) LogLine("  错误：" + m.StdErr.Replace("\r\n", " ").Replace("\n", " "));
            _customResultHandlers?.Invoke(m);
        }

        /// <summary>被控端回传的验收结果：更新列表的“验收”列并刷新。</summary>
        private void OnVerifyResult(SlaveVerifyResultMsg m)
        {
            var it = _session.GetByMac(m.LocalMac);
            if (it == null) return;
            it.VerifyStatus = m.Match ? VerifyStatus.Pass : VerifyStatus.Fail;
            it.VerifyActual = $"{(m.CurrentHostName ?? "")} / {(m.CurrentIp ?? "")}";
            it.VerifyNote = m.Note ?? "";
            RefreshGrid();
            string name = it.TargetHostName ?? m.LocalMac;
            LogLine($"[验收] {name}（{m.LocalMac}）：{(m.Match ? "通过" : "不符")}（实际 {m.CurrentHostName}/{m.CurrentIp}）{(m.Match ? "" : " - " + m.Note)}");
        }

        /// <summary>供“自定义命令”对话框订阅结果回传；关闭时取消订阅。</summary>
        internal void RegisterCustomResultHandler(Action<SlaveCustomResultMsg> h) => _customResultHandlers += h;
        internal void UnregisterCustomResultHandler(Action<SlaveCustomResultMsg> h) => _customResultHandlers -= h;

        /// <summary>当前选中的终端；未选中则取所有在线终端（用于“对全部在线下发”）。</summary>
        internal List<SlaveSessionItem> SelectedItemsOrAllOnline()
        {
            var sel = SelectedItems();
            if (sel.Count > 0) return sel;
            return _session.Items.Where(x => x.Online).ToList();
        }

        /// <summary>向单台终端下发自定义命令/脚本。</summary>
        internal void SendCustomCommandTo(SlaveSessionItem it, string shell, string script, int timeout)
        {
            var sid = _session.SessionId;
            var bindIp = SelectedBroadcastIp() ?? NetworkHelper.GetLocalIpv4();
            var cmd = new MasterCustomCmdMsg(sid) { LocalMac = it.Mac, Shell = shell, Script = script, TimeoutSeconds = timeout };
            string target = string.IsNullOrEmpty(it.SourceIp) ? it.TargetIp : it.SourceIp;
            if (TcpSend.SendTo(target, AppConfig.TcpPort, cmd, bindIp, out string err))
                LogLine($"已下发自定义命令到 {it.TargetHostName ?? it.Mac}（{it.Mac}）");
            else
                LogLine($"下发自定义命令失败：{it.Mac} - {err}");
        }

        /// <summary>向单台终端下发验收指令（期望配置来自该终端当前的目标值）。</summary>
        internal void SendVerifyTo(SlaveSessionItem it)
        {
            var sid = _session.SessionId;
            var bindIp = SelectedBroadcastIp() ?? NetworkHelper.GetLocalIpv4();
            var cmd = new MasterVerifyCmdMsg(sid)
            {
                LocalMac = it.Mac,
                ExpectedHostName = it.TargetHostName ?? "",
                ExpectedIp = it.TargetIp ?? ""
            };
            string target = string.IsNullOrEmpty(it.SourceIp) ? it.TargetIp : it.SourceIp;
            if (TcpSend.SendTo(target, AppConfig.TcpPort, cmd, bindIp, out string err))
                LogLine($"已下发验收指令：{it.TargetHostName ?? it.Mac}（期望 {it.TargetHostName}/{it.TargetIp}）");
            else
                LogLine($"下发验收失败：{it.Mac} - {err}");
        }

        /// <summary>手动验收选中终端（未选中则验收全部在线终端）。</summary>
        private void VerifySelected()
        {
            var targets = SelectedItems();
            if (targets.Count == 0) targets = _session.Items.Where(x => x.Online).ToList();
            if (targets.Count == 0) { MessageBox.Show("没有可验收的在线终端。", "提示"); return; }
            int n = 0;
            foreach (var it in targets)
            {
                if (string.IsNullOrEmpty(it.TargetHostName) && string.IsNullOrEmpty(it.TargetIp)) continue;
                SendVerifyTo(it);
                n++;
            }
            LogLine($"已向 {n} 台终端下发验收指令");
        }

        // ---------- 模板方案库 ----------

        /// <summary>刷新模板方案下拉框。</summary>
        private void RefreshSchemeList()
        {
            if (_cmbTemplateScheme == null) return;
            string cur = _cmbTemplateScheme.Text;
            _cmbTemplateScheme.Items.Clear();
            foreach (var s in _templateLib.List())
                _cmbTemplateScheme.Items.Add(s.Name);
            if (!string.IsNullOrEmpty(cur)) _cmbTemplateScheme.Text = cur;
        }

        /// <summary>把当前模板参数存成一个命名方案（弹出输入框要名字）。</summary>
        private void SchemeSave()
        {
            string name = Prompt("保存模板方案", "方案名称（如 “3楼A区-静态IP” / “5楼-DHCP”）：");
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            var t = BuildTemplateFromUi();
            if (t == null) return;   // 校验失败已弹窗提示
            _templateLib.Save(name, t);
            RefreshSchemeList();
            if (_cmbTemplateScheme != null) _cmbTemplateScheme.Text = name;
            LogLine($"已保存模板方案：{name}");
        }

        /// <summary>把下拉框选中的方案应用到当前模板参数。</summary>
        private void SchemeApply()
        {
            if (_cmbTemplateScheme == null) return;
            string name = _cmbTemplateScheme.Text?.Trim();
            if (string.IsNullOrEmpty(name)) { MessageBox.Show("请先在上方选择或输入一个方案名称。", "提示"); return; }
            var s = _templateLib.Get(name);
            if (s == null || s.Template == null) { MessageBox.Show($"找不到方案「{name}」。", "提示"); return; }
            var t = s.Template;
            _txtIpPrefix.Text = t.IpPrefix ?? "";
            _txtMask.Text = t.SubnetMask ?? "";
            _txtGw.Text = t.Gateway ?? "";
            _txtDns1.Text = t.Dns1 ?? "";
            _txtDns2.Text = t.Dns2 ?? "";
            _txtHnPrefix.Text = t.HostNamePrefix ?? "";
            _chkDhcp.Checked = t.IsDhcp;
            LogLine($"已应用模板方案：{name}");
            ScheduleSave();
        }

        /// <summary>删除下拉框选中的方案。</summary>
        private void SchemeDelete()
        {
            if (_cmbTemplateScheme == null) return;
            string name = _cmbTemplateScheme.Text?.Trim();
            if (string.IsNullOrEmpty(name)) { MessageBox.Show("请先选择要删除的方案。", "提示"); return; }
            if (MessageBox.Show($"确认删除方案「{name}」？", "删除方案", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            _templateLib.Delete(name);
            RefreshSchemeList();
            LogLine($"已删除模板方案：{name}");
        }

        /// <summary>只刷某台终端的「在线」单元格（收到心跳时立即调用）。</summary>
        private void UpdateOnlineCell(string mac)
        {
            if (_grid == null || _grid.IsDisposed) return;
            int col = _grid.Columns["colOnline"]?.Index ?? -1;
            if (col < 0) return;
            string norm = NetworkHelper.NormalizeMac(mac);
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.Tag is not SlaveSessionItem it) continue;
                if (!string.Equals(NetworkHelper.NormalizeMac(it.Mac), norm, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(row.Cells[col].Value as string, it.OnlineDisplay, StringComparison.Ordinal))
                {
                    row.Cells[col].Value = it.OnlineDisplay;
                    row.Cells[col].Style.ForeColor = it.Online ? System.Drawing.Color.Green : System.Drawing.Color.Gray;
                }
                break;
            }
        }

        private void OnResultReport(SlaveResultReportMsg m)
        {
            if (m.SessionId != _session.SessionId)
            {
                LogLine($"[忽略] 会话不一致的执行结果：{m.LocalMac}");
                return;
            }
            var rec = _session.MarkResult(m);
            // 成功后清掉上一次失败留下的归类，避免“已成功却还显示旧错误类别”
            if (m.Success)
            {
                var okIt = _session.GetByMac(m.LocalMac);
                if (okIt != null) { okIt.FailCategory = ""; okIt.FailSuggestion = ""; }
            }
            if (rec != null)
            {
                _db.Insert(rec);
                LogLine($"[结果] {rec.TargetHostName} @ {rec.TargetIp} -> {rec.Result} {rec.ErrorMsg}");
            }
            else
            {
                LogLine($"[结果] 收到未知终端 {m.LocalMac} 的执行结果，未匹配到列表项");
            }

            // 失败原因归类：把被控端回传的错误原文映射成“问题类别 + 修复建议”，落到列表“失败归类”列
            if (!m.Success)
            {
                var it = _session.GetByMac(m.LocalMac);
                if (it != null)
                {
                    var fc = FailureClassifier.Classify(m.ErrorMsg);
                    it.FailCategory = fc.Category;
                    it.FailSuggestion = fc.Suggestion;
                }
            }
        }

        /// <summary>回收超时未回传结果的「执行中」记录。</summary>
        private void SweepStaleExecuting()
        {
            List<SlaveSessionItem> stale;
            try { stale = _session.RecoverStaleExecuting(TimeSpan.FromSeconds(SweepTimeoutSeconds)); }
            catch { return; }
            foreach (var it in stale)
            {
                _db.Insert(new OpLogRecord
                {
                    SessionId = _session.SessionId,
                    OpTime = DateTime.Now,
                    Mac = it.Mac,
                    SourceIp = it.SourceIp,
                    TargetHostName = it.TargetHostName,
                    TargetIp = it.TargetIp,
                    Result = "失败",
                    ErrorMsg = it.ErrorMsg
                });
                LogLine($"[超时] {it.TargetHostName} @ {it.TargetIp} 超过 {SweepTimeoutSeconds} 秒未回传结果，已标记失败");
            }
        }

        private void btnStart_Click(object sender, EventArgs e)
        {
            if (_broadcasting) return;
            var t = BuildTemplateFromUi();
            if (t == null) return;

            // 重启后继续上一批次：模板参数和终端列表已经恢复，这里不要换会话，
            // 否则恢复出来的终端立刻变成“上一批次遗留”而被丢弃。要开新批次请点【重置会话】。
            if (string.IsNullOrEmpty(_session.SessionId) || _session.Items.Count == 0)
                _session.NewSession();
            string bindIp = SelectedBroadcastIp() ?? NetworkHelper.GetLocalIpv4();
            if (string.IsNullOrEmpty(bindIp))
            {
                MessageBox.Show("未找到可用的本机 IPv4 地址，无法广播。请检查网卡是否已连接。", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            bool okT = _server.Start();                       // 先起接收端，避免被控端上报时还没监听
            bool okB = okT && _broadcast.Start(_session.SessionId, t, bindIp);
            if (!okT || !okB)
            {
                // 回滚：半启动会占着端口，导致再点“开始”永远失败
                _broadcast.Stop();
                _server.Stop();
                _broadcasting = false;
                _broadcastIp = null;
                SetBroadcastUi(false);
                _lblBroadcast.Text = "广播: 启动失败";
                _lblSession.Text = "启动失败（端口可能被占用）";
                LogLine($"服务启动失败（TCP {AppConfig.TcpPort} / UDP {AppConfig.UdpPort}），已回滚。");
                MessageBox.Show($"服务启动失败，请检查端口是否被占用（TCP {AppConfig.TcpPort} / UDP {AppConfig.UdpPort}）。",
                    "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _broadcastIp = bindIp;
            _broadcasting = true;
            _lblSession.Text = $"会话ID: {ShortId(_session.SessionId)}  广播IP: {bindIp}";
            _lblBroadcast.Text = $"广播: 运行中（{bindIp}）";
            SetBroadcastUi(true);
            LogLine($"开始发布模板：主机名前缀={t.HostNamePrefix} IP前缀={(t.IsDhcp ? "DHCP" : t.IpPrefix)} 广播IP={bindIp}");

            // 可选：同时提供 DHCP 服务，让没有 DHCP 的机房里的被控端先拿到地址
            if (_chkDhcpServer.Checked)
            {
                if (DhcpConfig.TryDerive(t.IpPrefix, t.SubnetMask, t.Gateway, bindIp, out DhcpConfig cfg, out string dErr))
                {
                    cfg.Dns1 = t.Dns1;
                    cfg.Dns2 = t.Dns2;
                    if (_dhcp.Start(cfg, out string dhcpErr))
                    {
                        _dhcp.Log += m => SafeUi(() => LogLine(m));
                        LogLine($"DHCP 服务已开启，地址池 {cfg.PoolStart} ~ {cfg.PoolEnd}，网关 {cfg.Gateway}");
                        _lblBroadcast.Text = $"广播: 运行中（{bindIp}）  DHCP: 已开启";
                    }
                    else
                    {
                        LogLine(dhcpErr);
                        MessageBox.Show(dhcpErr + "\n\n提示：DHCP 需要监听 UDP 67，请以管理员身份运行控制端；" +
                                        "不开启 DHCP 不影响模板广播与被控端上报（只要局域网里已有 DHCP）。",
                            "DHCP 未开启", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        _chkDhcpServer.Checked = false;
                    }
                }
                else
                {
                    LogLine("DHCP 地址池推导失败：" + dErr);
                    MessageBox.Show("DHCP 地址池推导失败：" + dErr, "DHCP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _chkDhcpServer.Checked = false;
                }
            }
        }

        private void btnStop_Click(object sender, EventArgs e)
        {
            _broadcast.Stop();
            _server.Stop();
            _dhcp.Stop();
            _broadcasting = false;
            _broadcastIp = null;
            SetBroadcastUi(false);
            _lblSession.Text = "广播已停止";
            _lblBroadcast.Text = "广播: 未启动";
            LogLine("已停止广播与接收服务（含 DHCP）。");
        }

        private async void btnReset_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show("重置会话会清空当前终端列表，并通知在线终端清空本地状态。确认继续？",
                    "确认重置", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            _btnReset.Enabled = false;
            try
            {
                string oldSession = _session.SessionId;
                string bindIp = _broadcastIp;
                // 通知在线终端（尽力而为）——放到后台线程，避免 UI 线程被 5s×N 的超时卡死
                var targets = _session.Items.Where(x => !string.IsNullOrEmpty(x.SourceIp))
                                            .Select(x => x.SourceIp).Distinct().ToList();
                if (targets.Count > 0)
                {
                    LogLine($"正在通知 {targets.Count} 台终端清空本地状态…");
                    await Task.Run(() =>
                    {
                        foreach (var ip in targets)
                            TcpSend.SendTo(ip, AppConfig.TcpPort, new MasterResetSessionMsg(oldSession), bindIp);
                    });
                }

                _broadcast.Stop();
                _server.Stop();
                _dhcp.Stop();
                _broadcasting = false;
                _broadcastIp = null;
                _session.NewSession();
                SetBroadcastUi(false);
                _lblSession.Text = "会话已重置，重新发布模板即可开始新批次";
                _lblBroadcast.Text = "广播: 未启动";
                LogLine("已重置会话，旧终端列表清空。");
            }
            finally
            {
                _btnReset.Enabled = true;
                RefreshGrid();
                UpdateStatusBar();
            }
        }

        private async void btnDispatch_Click(object sender, EventArgs e)
        {
            if (_dispatching) return;
            var pending = _session.GetPendingExecute();
            if (pending.Count == 0)
            {
                MessageBox.Show("没有待下发的终端（请先等待被控端上报，或解除“跳过”标记）。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (ConflictValidator.HasConflict(_session.Items))
            {
                MessageBox.Show("存在目标 IP 或主机名冲突，请先修复冲突（或跳过其中一台）再下发。", "冲突",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var confirm = MessageBox.Show(
                $"将向 {pending.Count} 台终端统一下发执行指令（以控制端审核后的配置为准）。确认继续？",
                "确认下发", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (confirm != DialogResult.OK) return;

            _dispatching = true;
            _btnDispatch.Enabled = false;
            string sessionId = _session.SessionId;
            string bindIp = _broadcastIp;
            try
            {
                await Task.Run(() =>
                {
                    int ok = 0, fail = 0;
                    foreach (var it in pending)
                    {
                        var msg = new MasterExecuteCmdMsg(sessionId)
                        {
                            LocalMac = it.Mac,
                            TargetHostName = it.TargetHostName,
                            TargetIp = it.TargetIp,
                            SubnetMask = it.SubnetMask,
                            Gateway = it.Gateway,
                            Dns1 = it.Dns1,
                            Dns2 = it.Dns2,
                            IsDhcp = it.IsDhcp,
                            NeedAutoReboot = false
                        };
                        bool sent = TcpSend.SendTo(it.SourceIp, AppConfig.TcpPort, msg, bindIp);
                        if (sent)
                        {
                            _session.MarkExecuting(it.Mac);
                            ok++;
                            SafeUi(() => LogLine($"已下发执行：{it.TargetHostName} @ {it.SourceIp}"));
                        }
                        else
                        {
                            // 退回待执行，允许重试（直接标失败会让这条记录再也发不出去）
                            _session.MarkDispatchFailed(it.Mac, "下发失败（无法连接被控端）");
                            fail++;
                            SafeUi(() => LogLine($"[下发失败] {it.TargetHostName} @ {it.SourceIp}"));
                        }
                    }
                    _db.Insert(new OpLogRecord
                    {
                        SessionId = sessionId,
                        OpTime = DateTime.Now,
                        Mac = "", SourceIp = "", TargetHostName = "[批量下发]", TargetIp = "",
                        Result = $"成功{ok} 失败{fail}", ErrorMsg = ""
                    });
                });
            }
            finally
            {
                _dispatching = false;
                _btnDispatch.Enabled = true;
                RefreshGrid();
                UpdateStatusBar();
            }
        }

        // ---------- 网格 / 右键 ----------

        private void GridContextMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("标记跳过 / 取消跳过", null, (s, e) =>
            {
                var it = CurrentItem();
                if (it == null) return;
                if (it.Skip) _session.UnmarkSkip(it.Mac);
                else _session.MarkSkip(it.Mac);
            });
            menu.Items.Add("删除终端", null, (s, e) =>
            {
                var it = CurrentItem();
                if (it != null && MessageBox.Show($"删除 {it.TargetHostName}？", "确认", MessageBoxButtons.OKCancel) == DialogResult.OK)
                    _session.Remove(it.Mac);
            });
            menu.Items.Add("批量删除选中", null, (s, e) => BatchDeleteSelected());
            menu.Items.Add("批量标记跳过", null, (s, e) => BatchSkipSelected(true));
            menu.Items.Add("批量取消跳过", null, (s, e) => BatchSkipSelected(false));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("加入黑名单（MAC）", null, (s, e) =>
            {
                var it = CurrentItem();
                if (it == null) return;
                _blacklist.AddMac(it.Mac);
                RefreshBlacklistSummary();
                LogLine($"已将 MAC {it.Mac} 加入黑名单");
            });
            menu.Items.Add("加入黑名单（IP）", null, (s, e) =>
            {
                var it = CurrentItem();
                if (it == null || string.IsNullOrEmpty(it.SourceIp)) return;
                _blacklist.AddIp(it.SourceIp);
                RefreshBlacklistSummary();
                LogLine($"已将 IP {it.SourceIp} 加入黑名单");
            });
            _grid.ContextMenuStrip = menu;
        }

        private SlaveSessionItem CurrentItem()
        {
            if (_grid.CurrentRow == null || _grid.CurrentRow.Tag == null) return null;
            return _grid.CurrentRow.Tag as SlaveSessionItem;
        }

        /// <summary>手动刷新终端列表：重读会话数据、重算冲突与在线状态、刷新状态栏和名单摘要。</summary>
        private void RefreshTerminalList()
        {
            RefreshGrid();
            RefreshOnlineCells();
            UpdateStatusBar();
            RefreshBlacklistSummary();
            int online = _session.Items.Count(x => x.Online);
            LogLine($"已刷新终端列表：共 {_session.Items.Count} 台，在线 {online} 台");
        }

        /// <summary>当前选中的终端（多选）。</summary>
        private List<SlaveSessionItem> SelectedItems()
        {
            var list = new List<SlaveSessionItem>();
            foreach (DataGridViewRow row in _grid.SelectedRows)
                if (row.Tag is SlaveSessionItem it) list.Add(it);
            return list;
        }

        /// <summary>供对话框读取当前选中终端。</summary>
        internal List<SlaveSessionItem> GetSelectedItems() => SelectedItems();
        /// <summary>供对话框读取全部终端。</summary>
        internal List<SlaveSessionItem> GetAllItems() => _session.Items.ToList();

        private void ShowCustomCmdDialog()
        {
            using var dlg = new CustomCmdForm(this);
            dlg.ShowDialog(this);
        }

        private void ShowStaggerRebootDialog()
        {
            var targets = SelectedItems();
            int online = targets.Count > 0 ? targets.Count : _session.Items.Count(x => x.Online);
            using var dlg = new StaggerRebootForm(online);
            if (dlg.ShowDialog(this) == DialogResult.OK)
                BatchRebootStaggered(dlg.BatchSize, dlg.IntervalSeconds);
        }

        private void BatchDeleteSelected()
        {
            var items = SelectedItems();
            if (items.Count == 0) { MessageBox.Show("请先选中要删除的终端（Ctrl/Shift 多选）。", "提示"); return; }
            if (MessageBox.Show($"确认删除选中的 {items.Count} 台终端？", "确认",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            _session.RemoveMany(items.Select(x => x.Mac));
            LogLine($"已批量删除 {items.Count} 台终端");
        }

        private void BatchSkipSelected(bool skip)
        {
            var items = SelectedItems();
            if (items.Count == 0) { MessageBox.Show("请先选中终端（Ctrl/Shift 多选）。", "提示"); return; }
            _session.SetSkipMany(items.Select(x => x.Mac), skip);
            LogLine($"已批量{(skip ? "标记跳过" : "取消跳过")} {items.Count} 台终端");
        }

        /// <summary>按后缀区间筛选（支持 "10"、"10-20"、"，"分隔的多个区间）。</summary>
        private void FilterBySuffix()
        {
            string input = Prompt("按后缀区间筛选", "输入后缀区间，例如 10-20 或 5,8,12-15：");
            if (string.IsNullOrWhiteSpace(input)) return;
            var ranges = new List<(int lo, int hi)>();
            foreach (var part in input.Split(new[] { ',', '，', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var seg = part.Trim();
                int dash = seg.IndexOf('-');
                if (dash > 0 && int.TryParse(seg.Substring(0, dash), out int lo) && int.TryParse(seg.Substring(dash + 1), out int hi))
                    ranges.Add((Math.Min(lo, hi), Math.Max(lo, hi)));
                else if (int.TryParse(seg, out int one)) ranges.Add((one, one));
            }
            if (ranges.Count == 0) { MessageBox.Show("格式不对，示例：10-20 或 5,8,12-15"); return; }

            _grid.ClearSelection();
            int hit = 0;
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.Tag is not SlaveSessionItem it) continue;
                if (!int.TryParse(it.Suffix, out int n)) continue;
                if (!ranges.Any(r => n >= r.lo && n <= r.hi)) continue;
                row.Selected = true;
                hit++;
            }
            LogLine($"按后缀区间 [{input}] 筛出 {hit} 台终端");
            if (hit == 0) MessageBox.Show("没有匹配的终端。", "筛选");
        }

        /// <summary>导出资产台账（CSV）：批量装机后直接当资产库用。</summary>
        private void ExportAssetsCsv()
        {
            var items = _session.Items.ToList();
            if (items.Count == 0) { MessageBox.Show("终端列表为空，没有可导出的资产。", "提示"); return; }
            using var dlg = new SaveFileDialog
            {
                Filter = "CSV 文件 (*.csv)|*.csv",
                FileName = $"资产台账_{DateTime.Now:yyyyMMdd_HHmm}.csv"
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            var sb = new StringBuilder();
            sb.AppendLine("MAC,计算机名,当前IP,目标主机名,目标IP,CPU,核数,内存(GB),系统盘(GB),磁盘型号,主板,序列号,BIOS版本,BIOS日期,厂商,机型,操作系统,在线,状态,最后心跳");
            foreach (var it in items)
            {
                var hw = it.Hardware;
                sb.AppendLine(string.Join(",", new[]
                {
                    Q(it.Mac), Q(hw?.MachineName ?? it.TargetHostName), Q(it.SourceIp),
                    Q(it.TargetHostName), Q(it.TargetIp),
                    Q(hw?.Cpu), Q(hw?.CpuCores.ToString()), Q(hw?.MemoryDisplay?.Replace(" GB","")),
                    Q(hw?.DiskDisplay?.Replace(" GB","")), Q(hw?.DiskModel), Q(hw?.BaseBoard),
                    Q(hw?.SerialNumber), Q(hw?.BiosVersion), Q(hw?.BiosDate),
                    Q(hw?.Manufacturer), Q(hw?.Model), Q(hw?.OsVersion),
                    Q(it.Online ? "在线" : "离线"), Q(it.StatusDisplay),
                    Q(it.LastSeen == DateTime.MinValue ? "" : it.LastSeen.ToString("yyyy-MM-dd HH:mm:ss"))
                }));
            }
            try
            {
                File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true)); // 带 BOM，Excel 打开不乱码
                LogLine($"已导出资产台账：{items.Count} 台 → {dlg.FileName}");
                MessageBox.Show($"已导出 {items.Count} 台终端的资产台账：\n{dlg.FileName}", "导出完成",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("导出失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>CSV 字段转义。</summary>
        private static string Q(string s)
        {
            s ??= "";
            if (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        /// <summary>批量重启选中终端（给 10 秒缓冲，被控端收到后 shutdown /r）。</summary>
        private void BatchRebootSelected()
        {
            var items = SelectedItems();
            if (items.Count == 0) { MessageBox.Show("请先选中要重启的终端（Ctrl/Shift 多选）。", "提示"); return; }
            if (MessageBox.Show($"确认重启选中的 {items.Count} 台终端？（10 秒后重启）", "确认",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

            int ok = 0, fail = 0;
            foreach (var it in items)
            {
                if (SendRebootTo(it, 10)) ok++; else fail++;
            }
            LogLine($"批量重启：成功下发 {ok} 台，失败 {fail} 台");
            MessageBox.Show($"已下发重启：成功 {ok} 台，失败 {fail} 台（详见日志）。", "批量重启",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>向单台终端下发重启指令，并登记到“待自动验收”集合（重启上线后自动补验收）。</summary>
        private bool SendRebootTo(SlaveSessionItem it, int delaySeconds)
        {
            string sid = _session.SessionId;
            string bindIp = SelectedBroadcastIp() ?? NetworkHelper.GetLocalIpv4();
            string target = string.IsNullOrEmpty(it.SourceIp) ? it.TargetIp : it.SourceIp;
            if (string.IsNullOrEmpty(target))
            {
                LogLine($"[重启] {it.TargetHostName}（{it.Mac}）无可用目标地址，跳过");
                return false;
            }
            var cmd = new MasterRebootCmdMsg(sid) { LocalMac = it.Mac, DelaySeconds = Math.Max(0, Math.Min(600, delaySeconds)) };
            if (TcpSend.SendTo(target, AppConfig.TcpPort, cmd, bindIp, out string err))
            {
                _rebootSent.Add(it.Mac);
                LogLine($"[重启] 已下发：{it.TargetHostName}（{target}）");
                return true;
            }
            LogLine($"[重启] {it.TargetHostName}（{target}）下发失败：{err}");
            return false;
        }

        /// <summary>分批错峰重启：把选中的（或全部在线）终端分多批，每批间隔若干秒下发重启。</summary>
        private void BatchRebootStaggered(int batchSize, int intervalSec)
        {
            var targets = SelectedItems();
            if (targets.Count == 0) targets = _session.Items.Where(x => x.Online).ToList();
            if (targets.Count == 0) { MessageBox.Show("没有可选重启的在线终端。", "提示"); return; }
            if (MessageBox.Show($"将分批错峰重启 {targets.Count} 台：\n每批 {batchSize} 台，批次间隔 {intervalSec} 秒。确认？",
                    "分批错峰重启", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

            _rebootQueue = new Queue<SlaveSessionItem>(targets);
            _rebootBatchSize = Math.Max(1, batchSize);
            _staggerTimer.Interval = Math.Max(1000, intervalSec * 1000);
            _staggerTimer.Tick -= StaggerTick;
            _staggerTimer.Tick += StaggerTick;
            LogLine($"开始分批错峰重启：共 {targets.Count} 台，每批 {_rebootBatchSize} 台，间隔 {_staggerTimer.Interval / 1000} 秒");
            StaggerTick(null, null);   // 立即发第一批
            _staggerTimer.Start();
        }

        /// <summary>每到一个批次间隔，从队列里取出本批终端下发重启。</summary>
        private void StaggerTick(object sender, EventArgs e)
        {
            if (_rebootQueue == null) { _staggerTimer.Stop(); return; }
            int n = 0;
            while (_rebootQueue.Count > 0 && n < _rebootBatchSize)
            {
                var it = _rebootQueue.Dequeue();
                SendRebootTo(it, 10);
                n++;
            }
            LogLine($"本批已下发重启 {n} 台，剩余 {_rebootQueue.Count} 台");
            if (_rebootQueue.Count == 0)
            {
                _staggerTimer.Stop();
                _rebootQueue = null;
                LogLine("分批错峰重启已全部下发完毕（机器会按各自倒计时重启，上线后会自动验收）");
            }
        }

        /// <summary>
        /// 按上线顺序给「在线未上报」的终端分配后缀编号（01、02、03…），下发后由被控端自动填写并提交上报。
        ///
        /// 顺序取的是会话列表的插入顺序：每次心跳/上报发现新终端都追加到列表末尾，
        /// 所以这个顺序就是真实的上线先后（与网格的排序/筛选无关）。
        /// 已上报的终端占用的编号会被跳过，不会出现两台机器同号。
        /// </summary>
        private void AssignSuffixByOnlineOrder()
        {
            if (!_broadcasting)
            {
                MessageBox.Show("请先点【开始发布模板】。\n\n被控端要先收到本批次的模板，才会接受编号指令并把编号换算成主机名/IP。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var picked = SelectedItems();
            bool scoped = picked.Count > 0;
            var pool = scoped ? picked : _session.Items.ToList();
            var candidates = pool
                .Where(x => x.Status == SlaveStatus.Online && string.IsNullOrWhiteSpace(x.Suffix))
                .ToList();

            if (candidates.Count == 0)
            {
                MessageBox.Show(scoped
                        ? "选中的终端里没有可编号的机器。\n\n只处理“在线未上报、且还没有后缀编号”的终端。"
                        : "当前没有可编号的终端（在线未上报）。\n\n被控端启动后会先发心跳出现在列表里，状态为“在线未上报”，\n此时才能按顺序发号。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 已被占用的编号：候选之外的终端（已上报/已编号）现有的后缀
            var candidateSet = new HashSet<string>(candidates.Select(x => x.Mac), StringComparer.OrdinalIgnoreCase);
            var usedSuffixes = _session.Items
                .Where(x => !candidateSet.Contains(x.Mac))
                .Select(x => x.Suffix)
                .ToList();

            // 先预演一遍，把要发的号算出来给操作员确认（避免点下去就改了一堆机器的名字）
            var suffixes = SuffixPlanner.Plan(usedSuffixes, candidates.Count);
            var plan = new List<KeyValuePair<SlaveSessionItem, string>>();
            for (int i = 0; i < suffixes.Count; i++)
                plan.Add(new KeyValuePair<SlaveSessionItem, string>(candidates[i], suffixes[i]));
            if (plan.Count == 0)
            {
                MessageBox.Show("编号已用尽（后缀对应的 IP 末段最大 254）。请先重置会话，或手工调整部分终端的编号。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var preview = new StringBuilder();
            foreach (var kv in plan.Take(10))
            {
                string tgt = string.IsNullOrEmpty(kv.Key.SourceIp) ? kv.Key.TargetIp : kv.Key.SourceIp;
                preview.AppendLine($"  {kv.Value}  →  {kv.Key.Mac}  ({tgt})");
            }
            if (plan.Count > 10) preview.AppendLine($"  … 共 {plan.Count} 台");

            string scopeText = scoped ? $"选中的 {candidates.Count} 台" : $"全部 {candidates.Count} 台在线未上报终端";
            string tail = candidates.Count > plan.Count
                ? $"\n注意：可用编号不足，只处理前 {plan.Count} 台。"
                : "";
            if (MessageBox.Show($"按上线顺序对{scopeText}下发编号：\n\n{preview}{tail}\n" +
                                "被控端收到后会自己填好编号并立即提交上报（主机名 = 前缀 + 编号，IP 末段 = 编号 + 1）。\n" +
                                "上线早的在前（列表从上到下就是这个顺序）。确定下发？",
                    "按上线顺序编号", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;

            string sid = _session.SessionId;
            string bindIp = SelectedBroadcastIp() ?? NetworkHelper.GetLocalIpv4();
            int ok = 0, fail = 0;
            foreach (var kv in plan)
            {
                var it = kv.Key;
                string suffix = kv.Value;
                string target = string.IsNullOrEmpty(it.SourceIp) ? it.TargetIp : it.SourceIp;
                if (string.IsNullOrEmpty(target))
                {
                    fail++;
                    LogLine($"[编号] {it.Mac} 没有可用地址（既无来源 IP 也无目标 IP），跳过");
                    continue;
                }
                var cmd = new MasterAssignSuffixCmdMsg(sid, it.Mac, suffix);
                if (TcpSend.SendTo(target, AppConfig.TcpPort, cmd, bindIp, out string err))
                {
                    ok++;
                    it.Suffix = suffix;      // 先记下来让界面有反馈；被控端上报回来后以它为准
                    LogLine($"[编号] {it.Mac}（{target}）← {suffix}");
                }
                else
                {
                    fail++;
                    LogLine($"[编号] {it.Mac}（{target}）下发失败：{err}");
                }
            }

            RefreshGrid();
            UpdateStatusBar();
            ScheduleSave();      // 编号先落盘：控制端意外关掉再开，分配结果还在
            LogLine($"按上线顺序编号：成功下发 {ok} 台，失败 {fail} 台");
            MessageBox.Show($"已下发编号：成功 {ok} 台，失败 {fail} 台（详见日志）。\n\n" +
                            "被控端会自动填好编号并提交上报，列表里状态会变成“已上报待审核”。\n" +
                            "若某台一直停在“在线未上报”，说明它没收到指令（离线/会话不一致），可再点一次。",
                "按上线顺序编号", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>
        /// 批量关机选中终端（给 10 秒缓冲，被控端收到后 shutdown /s）。
        /// 关机后该终端会离线，只能靠 WOL 或现场开机。
        /// </summary>
        private void BatchShutdownSelected()
        {
            var items = SelectedItems();
            if (items.Count == 0) { MessageBox.Show("请先选中要关机的终端（Ctrl/Shift 多选）。", "提示"); return; }
            if (MessageBox.Show($"确认关闭选中的 {items.Count} 台终端？\n\n" +
                                "• 被控端执行 shutdown /s /t 10（10 秒后关机）；\n" +
                                "• 关机后这些终端会变成“离线”，要再操作只能 WOL 唤醒或现场开机；\n" +
                                "• 10 秒内可在被控端本机执行 shutdown /a 取消。",
                    "批量关机", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

            string sid = _session.SessionId;
            string bindIp = SelectedBroadcastIp() ?? NetworkHelper.GetLocalIpv4();
            int ok = 0, fail = 0;
            foreach (var it in items)
            {
                string target = string.IsNullOrEmpty(it.SourceIp) ? it.TargetIp : it.SourceIp;
                if (string.IsNullOrEmpty(target)) { fail++; continue; }
                var cmd = new MasterShutdownCmdMsg(sid) { LocalMac = it.Mac, DelaySeconds = 10 };
                if (TcpSend.SendTo(target, AppConfig.TcpPort, cmd, bindIp, out string err)) ok++;
                else { fail++; LogLine($"[关机] {it.TargetHostName}（{target}）下发失败：{err}"); }
            }
            LogLine($"批量关机：成功下发 {ok} 台，失败 {fail} 台");
            MessageBox.Show($"已下发关机：成功 {ok} 台，失败 {fail} 台（详见日志）。\n\n" +
                            "10 秒后关机，之后列表里会显示“离线”。",
                "批量关机", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>
        /// 批量开关选中终端的开机自启动（写/删被控端注册表 Run 项）。
        /// 只对“当前会话内收到过模板且在线”的终端有效；离线终端会被统计为发送失败。
        /// </summary>
        private void BatchSetAutostartSelected(bool enable)
        {
            var items = SelectedItems();
            if (items.Count == 0) { MessageBox.Show("请先选中要操作的终端（Ctrl/Shift 多选）。", "提示"); return; }
            if (!_broadcasting)
            {
                // 被控端只有收到模板后才会接受该会话的指令，没广播时下发必然被丢，先拦住
                MessageBox.Show("请先点【开始发布模板】。\n\n被控端要先收到本批次的模板，才会接受指令（会话 ID 必须一致）。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string what = enable ? "开启" : "取消";
            if (MessageBox.Show($"确认对选中的 {items.Count} 台终端{what}开机自启动？\n\n" +
                                "• 被控端需在线，且本批次已收到过模板（会话一致）才会执行；\n" +
                                "• 只改注册表 Run 项，不动网络配置、也不会关闭被控端程序；\n" +
                                "• 若被控端当前账户无权写 HKLM，会退到 HKCU 或记录失败原因。",
                    "批量" + what + "开机自启", MessageBoxButtons.OKCancel,
                    enable ? MessageBoxIcon.Question : MessageBoxIcon.Warning) != DialogResult.OK) return;

            string sid = _session.SessionId;
            string bindIp = SelectedBroadcastIp() ?? NetworkHelper.GetLocalIpv4();
            int ok = 0, fail = 0;
            foreach (var it in items)
            {
                string target = string.IsNullOrEmpty(it.SourceIp) ? it.TargetIp : it.SourceIp;
                if (string.IsNullOrEmpty(target))
                {
                    fail++;
                    LogLine($"[自启] {it.TargetHostName}（{it.Mac}）没有可用地址，跳过");
                    continue;
                }
                var cmd = new MasterAutostartCmdMsg(sid, it.Mac, enable);
                if (TcpSend.SendTo(target, AppConfig.TcpPort, cmd, bindIp, out string err)) ok++;
                else { fail++; LogLine($"[自启] {it.TargetHostName}（{target}）下发失败：{err}"); }
            }
            LogLine($"批量{what}开机自启：成功下发 {ok} 台，失败 {fail} 台");
            MessageBox.Show($"已下发“{what}开机自启”：成功 {ok} 台，失败 {fail} 台（详见日志）。\n\n" +
                            "被控端日志会出现“控制端指令：…”，界面上的勾选框会同步变化。",
                "批量" + what + "开机自启", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>对选中终端发 WOL 魔术包（关机状态也能唤醒，前提是同一广播域且 BIOS/网卡开了网络唤醒）。</summary>
        private void WakeSelected()
        {
            var items = SelectedItems();
            if (items.Count == 0) { MessageBox.Show("请先选中要唤醒的终端（可以选中离线终端）。", "提示"); return; }
            string broadcast = SelectedBroadcastIp();
            if (!string.IsNullOrEmpty(broadcast) && broadcast.EndsWith(".250")) { /* 用本机 IP 推子网广播 */ }
            string bip = BuildSubnetBroadcast(broadcast ?? NetworkHelper.GetLocalIpv4(), _txtMask.Text);

            int ok = 0, fail = 0;
            foreach (var it in items)
            {
                if (WakeOnLan.Send(it.Mac, bip, out string err)) ok++;
                else { fail++; LogLine($"[WOL] {it.Mac} 发送失败：{err}"); }
            }
            LogLine($"WOL：向 {bip}:9 发送魔术包，成功 {ok} 台，失败 {fail} 台");
            MessageBox.Show($"已发送 {ok} 个魔术包（广播地址 {bip}:9）。\n\n" +
                            "若没唤醒：确认目标机与控制在同一广播域、BIOS/网卡已开启网络唤醒、Windows 关闭了快速启动。",
                "WOL 唤醒", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>按本机 IP + 掩码算子网定向广播地址（比 255.255.255.255 更容易走对网卡）。</summary>
        private static string BuildSubnetBroadcast(string ip, string mask)
        {
            if (!Validators.TryParseIpv4(ip, out byte[] a)) return "255.255.255.255";
            if (!Validators.TryParseIpv4(mask, out byte[] m)) return "255.255.255.255";
            var b = new byte[4];
            for (int i = 0; i < 4; i++) b[i] = (byte)(a[i] | ~m[i]);
            return $"{b[0]}.{b[1]}.{b[2]}.{b[3]}";
        }


        /// <summary>
        /// 生成「MAC→后缀」导入模板：把当前终端列表的 MAC 预先填好，操作员只填后缀，
        /// 填完再用【导入 MAC→后缀 CSV】倒回来。模板里带一行说明，导入时会被自动忽略。
        /// </summary>
        private void ExportMacSuffixTemplate()
        {
            var items = _session.Items.ToList();
            if (items.Count == 0)
            {
                MessageBox.Show("终端列表为空。可以先让被控端上报，或手工新建模板后逐行填写 MAC。", "提示");
            }

            using var dlg = new SaveFileDialog
            {
                Filter = "CSV 文件 (*.csv)|*.csv",
                FileName = "MAC后缀-导入模板.csv"
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            var sb = new StringBuilder();
            sb.AppendLine("# MAC→后缀 导入模板：只填第 2 列“后缀”，保存为 CSV 后点【导入 MAC→后缀 CSV】。");
            sb.AppendLine("# 后缀规则：主机名 = 界面上“计算机名前缀” + 后缀原文；IP 末段 = 后缀 + 1（1 → .2）。");
            sb.AppendLine("MAC,后缀,当前主机名（仅参考，可删）");
            foreach (var it in items)
                sb.AppendLine($"{Q(it.Mac)},{Q(it.Suffix)},{Q(it.TargetHostName)}");
            if (items.Count == 0)
                sb.AppendLine("00-11-22-33-44-55,,（示例行，请替换成真实 MAC）");

            try
            {
                File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true)); // BOM：Excel 打开不乱码
                LogLine($"已生成 MAC→后缀 导入模板：{items.Count} 行 → {dlg.FileName}");
                MessageBox.Show($"模板已生成（{items.Count} 行）：\n{dlg.FileName}\n\n" +
                                "用 Excel 打开，只填“后缀”列，保存后回到这里点【导入 MAC→后缀 CSV】。",
                    "生成模板", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("生成失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ImportMacSuffixCsv()
        {
            using var dlg = new OpenFileDialog { Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*", Title = "导入 MAC→后缀 清单" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            string[] lines;
            try { lines = File.ReadAllLines(dlg.FileName, Encoding.UTF8); }
            catch (Exception ex) { MessageBox.Show("读取失败：" + ex.Message); return; }

            int applied = 0, skipped = 0;
            var problems = new List<string>();
            foreach (var raw in lines)
            {
                var line = (raw ?? "").Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var parts = line.Split(new[] { ',', '，', '\t', ';' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) { skipped++; continue; }
                string mac = NetworkHelper.NormalizeMac(parts[0]);
                string suffix = parts[1].Trim();
                if (!Validators.IsValidMac(mac)) { if (!line.StartsWith("MAC", StringComparison.OrdinalIgnoreCase)) skipped++; continue; }

                var it = _session.GetByMac(mac);
                if (it == null) { problems.Add($"未找到 MAC {mac}"); skipped++; continue; }
                if (string.IsNullOrEmpty(suffix)) { skipped++; continue; }   // 模板里没填的行直接跳过
                if (!int.TryParse(suffix, out int n) || n < 0 || n > 254) { problems.Add($"后缀不合法 {suffix}（{mac}）"); skipped++; continue; }

                string host = _txtHnPrefix.Text + suffix;
                string ip = _chkDhcp.Checked ? "" : _txtIpPrefix.Text + (n + 1);
                if (!_chkDhcp.Checked && !Validators.IsValidIp(ip)) { problems.Add($"IP 不合法 {ip}（{mac}）"); skipped++; continue; }
                if (!Validators.IsValidHostName(host)) { problems.Add($"主机名不合法 {host}（{mac}）"); skipped++; continue; }

                it.Suffix = suffix;
                it.TargetHostName = host;
                it.TargetIp = ip;
                it.IsDhcp = _chkDhcp.Checked;
                it.Status = SlaveStatus.Reported;
                it.Skip = false;
                it.ErrorMsg = "";
                applied++;
            }
            RefreshGrid();
            UpdateStatusBar();
            string msg = $"导入完成：应用 {applied} 条，跳过 {skipped} 条。";
            if (problems.Count > 0) msg += Environment.NewLine + Environment.NewLine + "部分原因：" + Environment.NewLine +
                                       string.Join(Environment.NewLine, problems.Take(8));
            MessageBox.Show(msg, "导入结果", MessageBoxButtons.OK, MessageBoxIcon.Information);
            LogLine($"导入 MAC→后缀：应用 {applied} 条，跳过 {skipped} 条（{dlg.FileName}）");
        }

        /// <summary>极简输入框（用于筛选条件），避免为了一个输入框引入新窗体类。</summary>
        private string Prompt(string title, string label)
        {
            using var dlg = new Form
            {
                Text = title,
                Size = new Size(UiScale.S(420), UiScale.S(170)),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                Font = Font
            };
            var lbl = new Label { Text = label, Left = UiScale.S(14), Top = UiScale.S(16), AutoSize = true };
            var txt = new TextBox { Left = UiScale.S(14), Top = UiScale.S(44), Width = UiScale.S(376) };
            var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Left = UiScale.S(228), Top = UiScale.S(86), Width = UiScale.S(76) };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Left = UiScale.S(312), Top = UiScale.S(86), Width = UiScale.S(76) };
            dlg.Controls.AddRange(new Control[] { lbl, txt, ok, cancel });
            dlg.AcceptButton = ok;
            dlg.CancelButton = cancel;
            return dlg.ShowDialog(this) == DialogResult.OK ? txt.Text.Trim() : null;
        }

        private void RefreshGrid()
        {
            var items = _session.Items.ToList();
            var confIps = ConflictValidator.GetConflictIps(items);
            var confHosts = ConflictValidator.GetConflictHosts(items);
            bool hasConflict = confIps.Count > 0 || confHosts.Count > 0;

            // 先安全结束当前编辑，避免在编辑状态下清空行触发 SetCurrentCellAddressCore 异常
            try { if (_grid.IsCurrentCellInEditMode) _grid.EndEdit(); } catch { }

            _grid.SuspendLayout();
            _grid.Rows.Clear();
            try
            {
                foreach (var it in items)
                {
                    bool conflict = confIps.Contains(it.TargetIp) || confHosts.Contains(it.TargetHostName ?? "");
                    string verifyText = it.VerifyStatus == VerifyStatus.Pass ? "✅ 通过"
                        : it.VerifyStatus == VerifyStatus.Fail ? "❌ 不符" : "—";
                    int idx = _grid.Rows.Add(
                        it.Mac, it.OnlineDisplay, it.SourceIp, it.Suffix, it.TargetHostName, it.TargetIp,
                        it.SubnetMask, it.Gateway, it.Dns1, it.Dns2,
                        it.IsDhcp ? "DHCP" : "静态",
                        it.StatusDisplay, it.ErrorMsg, conflict ? "冲突" : "",
                        verifyText, it.FailCategory ?? "");
                    var row = _grid.Rows[idx];
                    row.Tag = it;
                    if (conflict) row.DefaultCellStyle.BackColor = System.Drawing.Color.LightSalmon;
                    if (it.Status == SlaveStatus.Failed) row.DefaultCellStyle.ForeColor = System.Drawing.Color.Red;
                    if (it.Status == SlaveStatus.Skipped) row.DefaultCellStyle.BackColor = System.Drawing.Color.Gainsboro;
                    if (!it.Online) row.Cells["colOnline"].Style.ForeColor = System.Drawing.Color.Gray;
                    if (it.VerifyStatus == VerifyStatus.Pass) row.Cells["colVerify"].Style.ForeColor = System.Drawing.Color.Green;
                    if (it.VerifyStatus == VerifyStatus.Fail)
                    {
                        row.Cells["colVerify"].Style.ForeColor = System.Drawing.Color.Red;
                        row.Cells["colVerify"].ToolTipText = it.VerifyNote ?? "";
                    }
                    if (!string.IsNullOrEmpty(it.FailSuggestion))
                        row.Cells["colFailCat"].ToolTipText = it.FailSuggestion;
                    // 错误信息即使折行也可能看不全（被窗口高度限制），悬停给完整原文
                    if (!string.IsNullOrEmpty(it.ErrorMsg))
                        row.Cells["colErr"].ToolTipText = it.ErrorMsg;
                    // 资产摘要挂在 MAC 单元格的提示上，点开不占列宽
                    if (!string.IsNullOrEmpty(it.HardwareSummary))
                        row.Cells["colMac"].ToolTipText =
                            it.HardwareSummary + Environment.NewLine +
                            $"主板 {it.Hardware?.BaseBoard} / BIOS {it.Hardware?.BiosVersion} / SN {it.Hardware?.SerialNumber}";
                }
            }
            finally { _grid.ResumeLayout(); }

            // 冲突或正在下发时都禁止再次点击下发
            _btnDispatch.Enabled = !hasConflict && !_dispatching;
        }

        /// <summary>只刷新“在线”列（心跳定时器调用，避免整表刷新打断选择/编辑）。</summary>
        private void RefreshOnlineCells()
        {
            if (_grid == null || _grid.IsDisposed) return;
            int col = _grid.Columns["colOnline"]?.Index ?? -1;
            if (col < 0) return;
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.Tag is not SlaveSessionItem it) continue;
                string text = it.OnlineDisplay;
                if (!string.Equals(row.Cells[col].Value as string, text, StringComparison.Ordinal))
                {
                    row.Cells[col].Value = text;
                    row.Cells[col].Style.ForeColor = it.Online ? System.Drawing.Color.Green : System.Drawing.Color.Gray;
                }
            }
        }

        private void _grid_CellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (e.RowIndex >= _grid.Rows.Count || e.ColumnIndex >= _grid.Columns.Count) return;
            if (_grid.Rows[e.RowIndex].Tag is not SlaveSessionItem it) return;
            string col = _grid.Columns[e.ColumnIndex].Name;
            string val = (_grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value ?? "").ToString().Trim();

            if (col == "colHost")
            {
                if (!Validators.IsValidHostName(val))
                {
                    MessageBox.Show("目标主机名不合法：需 ≤15 字符，仅 ASCII 字母/数字/短横线，首尾不能是短横线，且不能全为数字。",
                        "校验", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    RevertGrid();
                    return;
                }
                it.TargetHostName = val;
            }
            else if (col == "colIp")
            {
                bool empty = string.IsNullOrWhiteSpace(val);
                if (!it.IsDhcp && empty)
                {
                    MessageBox.Show("静态模式下目标 IP 不能为空。", "校验", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    RevertGrid();
                    return;
                }
                if (!empty && !Validators.IsValidIp(val))
                {
                    MessageBox.Show("目标 IP 格式不合法（必须形如 192.168.1.10）。", "校验",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    RevertGrid();
                    return;
                }
                it.TargetIp = val;
            }
            else
            {
                return; // 其它列只读
            }
            RevertGrid();
        }

        /// <summary>延迟刷新：避免在 CellEndEdit 事件内部清空行再次触发 SetCurrentCellAddressCore 异常。</summary>
        private void RevertGrid()
        {
            BeginInvoke(new Action(() => { try { RefreshGrid(); } catch { } UpdateStatusBar(); }));
        }

        // ---------- 日志 / 导出 ----------

        private void LogLine(string msg)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            _txtLog.AppendText(line + Environment.NewLine);
            _logger.Info(msg);
        }

        /// <summary>导出“终端列表”当前内容（不是日志）。</summary>
        private void btnExportTerminalsCsv_Click(object sender, EventArgs e)
        {
            var items = _session.Items.ToList();
            if (items.Count == 0)
            {
                MessageBox.Show("终端列表还是空的，没有可导出的内容。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var confIps = ConflictValidator.GetConflictIps(items);
            var confHosts = ConflictValidator.GetConflictHosts(items);

            var sb = new StringBuilder();
            sb.AppendLine("MAC,来源IP,后缀,目标主机名,目标IP,子网掩码,网关,DNS1,DNS2,模式,状态,是否跳过,冲突,错误信息");
            foreach (var it in items)
            {
                bool conflict = confIps.Contains(it.TargetIp ?? "") || confHosts.Contains(it.TargetHostName ?? "");
                sb.AppendLine(string.Join(",", new[]
                {
                    Csv(it.Mac), Csv(it.SourceIp), Csv(it.Suffix), Csv(it.TargetHostName), Csv(it.TargetIp),
                    Csv(it.SubnetMask), Csv(it.Gateway), Csv(it.Dns1), Csv(it.Dns2),
                    it.IsDhcp ? "DHCP" : "静态", Csv(it.StatusDisplay), it.Skip ? "是" : "否",
                    conflict ? "冲突" : "", Csv(it.ErrorMsg)
                }));
            }
            SaveCsv($"LanAutoConfig_终端列表_{DateTime.Now:yyyyMMdd_HHmmss}.csv", sb.ToString(), "终端列表");
        }

        /// <summary>导出“日志报表”（SQLite 里的 OpLog），列按人看报表的顺序排。</summary>
        private void btnExportLogCsv_Click(object sender, EventArgs e)
        {
            var rows = _db.Query(100000);
            if (rows.Count == 0)
            {
                MessageBox.Show("日志库里还没有记录（执行并回传结果后才会有日志）。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var sb = new StringBuilder();
            sb.AppendLine("序号,时间,结果,目标主机名,目标IP,MAC,原始IP,错误信息,会话ID");
            int no = 0;
            foreach (var r in rows)
            {
                no++;
                sb.AppendLine(string.Join(",", new[]
                {
                    no.ToString(),
                    Csv(r.OpTime.ToString("yyyy-MM-dd HH:mm:ss")),
                    Csv(r.Result), Csv(r.TargetHostName), Csv(r.TargetIp), Csv(r.Mac), Csv(r.SourceIp),
                    Csv(r.ErrorMsg), Csv(ShortId(r.SessionId))
                }));
            }
            SaveCsv($"LanAutoConfig_日志_{DateTime.Now:yyyyMMdd_HHmmss}.csv", sb.ToString(), "日志报表");
        }

        /// <summary>通用 CSV 保存：目标目录不可写时自动改存到可写位置，并告知实际路径。</summary>
        private void SaveCsv(string fileName, string content, string what)
        {
            using var dlg = new SaveFileDialog
            {
                Filter = "CSV 文件|*.csv",
                FileName = fileName,
                InitialDirectory = PickWritableDir()   // 避免默认落到需要管理员权限的目录
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            // 用户选的位置可能不可写（比如 C:\ 根目录、Program Files），依次退到可写目录，尽量把报表存下来
            string saved = null, lastError = null;
            foreach (string target in ExportTargets(dlg.FileName, fileName))
            {
                try
                {
                    string dir = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(target, content, new UTF8Encoding(true));
                    saved = target;
                    break;
                }
                catch (Exception ex) { lastError = ex.Message; }
            }

            if (saved == null)
            {
                MessageBox.Show("导出失败：目标位置没有写入权限，且备用位置也写入失败。\n\n最后错误：" + lastError,
                    "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (!string.Equals(saved, dlg.FileName, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("你选择的位置没有写入权限（可能需要管理员身份），已自动改存到：\n\n" + saved,
                    "已导出（位置已调整）", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show($"已导出{what}：\n\n" + saved, "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            LogLine($"已导出{what}：" + saved);
        }

        /// <summary>选一个当前用户可写的目录（桌面 / 文档 / 程序数据目录）。</summary>
        private static string PickWritableDir()
        {
            var candidates = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                AppConfig.DataDir
            };
            foreach (var dir in candidates)
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                try
                {
                    string probe = Path.Combine(dir, ".lanac-write-test");
                    File.WriteAllText(probe, "x");
                    File.Delete(probe);
                    return dir;
                }
                catch { }
            }
            return AppConfig.DataDir;
        }

        /// <summary>导出时依次尝试的落盘位置：用户选的位置 → 桌面 → 文档 → 程序数据目录 → 临时目录。</summary>
        private static IEnumerable<string> ExportTargets(string chosenPath, string fileName)
        {
            yield return chosenPath;
            foreach (var dir in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                AppConfig.DataDir,
                Path.GetTempPath()
            })
            {
                if (string.IsNullOrEmpty(dir)) continue;
                string candidate = Path.Combine(dir, fileName);
                if (string.Equals(candidate, chosenPath, StringComparison.OrdinalIgnoreCase)) continue;
                yield return candidate;
            }
        }

        private static string Csv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        // ---------- 模板校验 / 状态 ----------

        private TemplateModel BuildTemplateFromUi()
        {
            string ipPrefix = _txtIpPrefix.Text.Trim();
            string mask = _txtMask.Text.Trim();
            string gw = _txtGw.Text.Trim();
            string dns1 = _txtDns1.Text.Trim();
            string dns2 = _txtDns2.Text.Trim();
            string hn = _txtHnPrefix.Text.Trim();
            bool dhcp = _chkDhcp.Checked;

            if (!Validators.IsValidHostName(hn + "1"))
            {
                MessageBox.Show("计算机名前缀不合法：拼接后完整主机名须 ≤15 字符，仅 ASCII 字母/数字/短横线。", "校验",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
            if (!dhcp)
            {
                if (!Validators.IsValidIpPrefix(ipPrefix))
                {
                    MessageBox.Show("IP 前缀格式错误（形如 192.168.1.，末尾必须带点）。", "校验", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return null;
                }
                if (!Validators.IsValidMask(mask))
                {
                    MessageBox.Show("子网掩码格式错误（必须是连续掩码，如 255.255.255.0）。", "校验", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return null;
                }
                if (!Validators.IsValidIp(gw))
                {
                    MessageBox.Show("网关格式错误（必须形如 192.168.1.1）。", "校验", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return null;
                }
                if (!Validators.IsValidIp(dns1))
                {
                    MessageBox.Show("DNS1 格式错误。", "校验", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return null;
                }
                if (!string.IsNullOrWhiteSpace(dns2) && !Validators.IsValidIp(dns2))
                {
                    MessageBox.Show("DNS2 格式错误（留空表示不设置第二个 DNS）。", "校验", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return null;
                }
            }

            return new TemplateModel
            {
                IpPrefix = ipPrefix, SubnetMask = mask, Gateway = gw,
                Dns1 = dns1, Dns2 = dns2, HostNamePrefix = hn, IsDhcp = dhcp
            };
        }

        private void SetBroadcastUi(bool on)
        {
            _btnStart.Enabled = !on;
            _btnStop.Enabled = on;
            foreach (Control c in new Control[]
                     { _txtIpPrefix, _txtMask, _txtGw, _txtDns1, _txtDns2, _txtHnPrefix, _chkDhcp, _chkDhcpServer, _cmbAdapter })
                c.Enabled = !on;
        }

        private void UpdateStatusBar()
        {
            var items = _session.Items.ToList();
            _lblReported.Text = $"已上报: {items.Count(x => x.Status == SlaveStatus.Reported)}";
            _lblPending.Text = $"待执行: {items.Count(x => x.Status == SlaveStatus.Reported && !x.Skip)}";
            _lblSuccess.Text = $"成功: {items.Count(x => x.Status == SlaveStatus.Success)}";
            _lblFailed.Text = $"失败: {items.Count(x => x.Status == SlaveStatus.Failed)}";
            _lblConflict.Text = $"冲突: {ConflictValidator.GetConflictIps(items).Count + ConflictValidator.GetConflictHosts(items).Count}";
            if (_lblOnline != null)
                _lblOnline.Text = $"在线: {items.Count(x => x.Online)} / {items.Count}";
        }

        // ---------- UI 线程封装 ----------

        private void SafeUi(Action a)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke(a); } catch { } }
            else a();
        }

        // ---------- UI 构建 ----------

        private void BuildUi()
        {
            _tabs = new TabControl { Dock = DockStyle.Fill };

            // ==================== Tab1 模板配置 ====================
            _tabTemplates = new TabPage("模板配置") { Padding = new Padding(10) };

            var root1 = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
            root1.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
            root1.RowStyles.Add(new RowStyle(SizeType.Absolute, 200));
            root1.RowStyles.Add(new RowStyle(SizeType.Absolute, 106));
            root1.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));   // 模板方案库
            root1.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
            root1.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            // --- 广播服务 ---
            var gbRun = new GroupBox { Text = "广播服务", Dock = DockStyle.Fill };
            var pnlAdapter = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(10, 10, 10, 0), WrapContents = false };
            pnlAdapter.Controls.Add(new Label { Text = "广播网卡(本机 IP)：", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
            _cmbAdapter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
            _cmbAdapter.SelectedIndexChanged += (s, e) => UpdateDhcpPoolPreview();
            pnlAdapter.Controls.Add(_cmbAdapter);

            var pnlButtons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(10, 6, 10, 0), WrapContents = false };
            _btnStart = new Button { Text = "开始发布模板", Width = 130, Height = 32 };
            _btnStop = new Button { Text = "停止服务", Width = 100, Height = 32, Enabled = false };
            _btnReset = new Button { Text = "重置会话", Width = 100, Height = 32 };
            _btnStart.Click += btnStart_Click;
            _btnStop.Click += btnStop_Click;
            _btnReset.Click += btnReset_Click;
            _lblSession = new Label { Text = "尚未开始广播", AutoSize = true, ForeColor = System.Drawing.Color.DimGray, Margin = new Padding(16, 9, 0, 0) };
            pnlButtons.Controls.Add(_btnStart);
            pnlButtons.Controls.Add(_btnStop);
            pnlButtons.Controls.Add(_btnReset);
            pnlButtons.Controls.Add(_lblSession);
            gbRun.Controls.Add(pnlButtons);   // Top 停靠顺序与添加顺序相反：后加的在上
            gbRun.Controls.Add(pnlAdapter);

            // --- 下发模板参数 ---
            var gbParams = new GroupBox { Text = "下发模板参数", Dock = DockStyle.Fill };
            var tbl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 5, Padding = new Padding(12, 6, 12, 4) };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            for (int i = 0; i < 4; i++) tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));

            _txtIpPrefix = NewTextBox(); _txtMask = NewTextBox(); _txtGw = NewTextBox();
            _txtDns1 = NewTextBox(); _txtDns2 = NewTextBox(); _txtHnPrefix = NewTextBox();
            _txtIpPrefix.Text = "192.168.1.";
            _txtMask.Text = "255.255.255.0";
            _txtGw.Text = "192.168.1.1";
            _txtDns1.Text = "223.5.5.5";
            _txtDns2.Text = "223.6.6.6";
            _txtHnPrefix.Text = "PC-ROOM01-";
            _txtIpPrefix.TextChanged += (s, e) => UpdateDhcpPoolPreview();
            _txtMask.TextChanged += (s, e) => UpdateDhcpPoolPreview();

            AddField(tbl, 0, 0, "IP 前缀", _txtIpPrefix);
            AddField(tbl, 0, 2, "子网掩码", _txtMask);
            AddField(tbl, 1, 0, "网关", _txtGw);
            AddField(tbl, 1, 2, "DNS1", _txtDns1);
            AddField(tbl, 2, 0, "DNS2（可留空）", _txtDns2);
            AddField(tbl, 2, 2, "计算机名前缀", _txtHnPrefix);

            _chkDhcp = new CheckBox
            {
                Text = "被控端使用 DHCP（不拼接静态 IP，直接恢复 DHCP）",
                AutoSize = true,
                Margin = new Padding(0, 4, 0, 0)
            };
            tbl.Controls.Add(_chkDhcp, 1, 3);

            var lblHint = new Label
            {
                Text = "拼接规则：主机名 = 前缀 + 后缀原文；IP 末段 = 后缀编号 + 1（编号 1 → .2，把 .1 留给网关）。模板每 2.5 秒广播一次。",
                Dock = DockStyle.Fill,
                AutoSize = false,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                ForeColor = System.Drawing.Color.DimGray
            };
            tbl.Controls.Add(lblHint, 0, 4);
            tbl.SetColumnSpan(lblHint, 4);

            gbParams.Controls.Add(tbl);

            // --- DHCP 服务 ---
            var gbDhcp = new GroupBox { Text = "DHCP 服务（机房没有 DHCP 时勾选，让被控端先拿到地址）", Dock = DockStyle.Fill };
            var pnlDhcp1 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(10, 8, 10, 0), WrapContents = false };
            _chkDhcpServer = new CheckBox
            {
                Text = "开启 DHCP 服务（按上面的 IP 前缀/掩码自动分配地址）",
                AutoSize = true,
                Margin = new Padding(0, 2, 0, 0)
            };
            pnlDhcp1.Controls.Add(_chkDhcpServer);
            var pnlDhcp2 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(12, 2, 10, 0), WrapContents = false };
            _lblDhcpPool = new Label { Text = "地址池：-", AutoSize = true, ForeColor = System.Drawing.Color.DimGray };
            pnlDhcp2.Controls.Add(_lblDhcpPool);
            gbDhcp.Controls.Add(pnlDhcp2);
            gbDhcp.Controls.Add(pnlDhcp1);

            // --- 模板方案库（多套命名模板，一键切换 / 保存） ---
            var gbScheme = new GroupBox { Text = "模板方案库（多套参数一键切换）", Dock = DockStyle.Fill };
            var pnlScheme = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 10, 12, 0), WrapContents = false };
            pnlScheme.Controls.Add(new Label { Text = "方案：", AutoSize = true, Margin = new Padding(0, 7, 4, 0) });
            _cmbTemplateScheme = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 260, Margin = new Padding(0, 3, 10, 0) };
            pnlScheme.Controls.Add(_cmbTemplateScheme);
            _btnSchemeApply = new Button { Text = "应用", Width = 80, Height = 30 };
            _btnSchemeApply.Click += (s, e) => SchemeApply();
            _btnSchemeSave = new Button { Text = "另存为…", Width = 90, Height = 30 };
            _btnSchemeSave.Click += (s, e) => SchemeSave();
            _btnSchemeDelete = new Button { Text = "删除", Width = 80, Height = 30 };
            _btnSchemeDelete.Click += (s, e) => SchemeDelete();
            pnlScheme.Controls.Add(_btnSchemeApply);
            pnlScheme.Controls.Add(_btnSchemeSave);
            pnlScheme.Controls.Add(_btnSchemeDelete);
            var tipScheme = new ToolTip();
            tipScheme.SetToolTip(_btnSchemeApply, "把下拉框选中的方案参数填到上面的模板里");
            tipScheme.SetToolTip(_btnSchemeSave, "把当前模板参数保存成一个命名方案（可输入新名字另存）");
            tipScheme.SetToolTip(_btnSchemeDelete, "删除下拉框选中的方案");
            gbScheme.Controls.Add(pnlScheme);

            // --- 黑名单 / 白名单 ---
            var gbBlack = new GroupBox { Text = "名单（黑名单拒绝 / 白名单放行）", Dock = DockStyle.Fill };
            var pnlBlack = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(10, 8, 10, 0), WrapContents = false };
            _lblBlacklist = new Label { Text = "黑名单：0 条", AutoSize = true, ForeColor = System.Drawing.Color.DimGray, Margin = new Padding(0, 7, 14, 0) };
            _btnManageBlacklist = new Button { Text = "管理黑名单…", Width = 120, Height = 30 };
            _btnManageBlacklist.Click += (s, e) => ShowBlacklist();
            _btnWhitelist = new Button { Text = "白名单…", Width = 96, Height = 30 };
            _btnWhitelist.Click += (s, e) => ShowWhitelist();
            pnlBlack.Controls.Add(_lblBlacklist);
            pnlBlack.Controls.Add(_btnManageBlacklist);
            pnlBlack.Controls.Add(_btnWhitelist);
            gbBlack.Controls.Add(pnlBlack);

            root1.Controls.Add(gbRun, 0, 0);
            root1.Controls.Add(gbParams, 0, 1);
            root1.Controls.Add(gbDhcp, 0, 2);
            root1.Controls.Add(gbScheme, 0, 3);
            root1.Controls.Add(gbBlack, 0, 4);
            root1.Controls.Add(new Panel { Dock = DockStyle.Fill }, 0, 5);   // 底部留白，避免名单被拉高
            _tabTemplates.Controls.Add(root1);

            // 模板方案库：启动时填充下拉框
            RefreshSchemeList();

            // ==================== Tab2 终端列表 ====================
            var tab2 = new TabPage("终端列表") { Padding = new Padding(10) };
            var bar2 = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,                      // 按钮放不下时自动折成两行，不再被固定高度裁掉
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(0, 2, 0, 6),
                WrapContents = true
            };
            _btnDispatch = new Button { Text = "下发执行命令", Width = 140, Height = 32 };
            _btnDispatch.Click += btnDispatch_Click;
            _btnExportTerminals = new Button { Text = "导出终端列表 CSV", Width = 150, Height = 32 };
            _btnExportTerminals.Click += btnExportTerminalsCsv_Click;
            var btnSelectAll = new Button { Text = "全选", Width = 70, Height = 32 };
            btnSelectAll.Click += (s, e) => { _grid.SelectAll(); };
            var btnRefresh = new Button { Text = "刷新列表", Width = 90, Height = 32 };
            btnRefresh.Click += (s, e) => RefreshTerminalList();

            // ---- 第二行：按上线顺序编号 + 电源类 + 自启 ----
            var btnAssignSeq = new Button { Text = "按上线顺序编号", Width = 130, Height = 32 };
            btnAssignSeq.Click += (s, e) => AssignSuffixByOnlineOrder();
            var btnReboot = new Button { Text = "批量重启", Width = 90, Height = 32 };
            btnReboot.Click += (s, e) => BatchRebootSelected();
            var btnShutdown = new Button { Text = "批量关机", Width = 90, Height = 32 };
            btnShutdown.Click += (s, e) => BatchShutdownSelected();
            var btnWol = new Button { Text = "WOL 唤醒", Width = 96, Height = 32 };
            btnWol.Click += (s, e) => WakeSelected();
            var btnAutostartOn = new Button { Text = "开启自启", Width = 90, Height = 32 };
            btnAutostartOn.Click += (s, e) => BatchSetAutostartSelected(true);
            var btnAutostartOff = new Button { Text = "取消自启", Width = 90, Height = 32 };
            btnAutostartOff.Click += (s, e) => BatchSetAutostartSelected(false);

            var tipBar = new ToolTip();
            tipBar.SetToolTip(btnAssignSeq, "对“在线未上报”的终端按上线顺序分配 01、02、03… 并让它们自动上报（可先选中部分终端只处理选中的）");
            tipBar.SetToolTip(btnReboot, "被控端执行 shutdown /r /t 10（10 秒后重启）");
            tipBar.SetToolTip(btnShutdown, "被控端执行 shutdown /s /t 10（10 秒后关机，之后该终端离线）");
            tipBar.SetToolTip(btnWol, "向关机中的终端发送 WOL 魔术包（需同一广播域且 BIOS/网卡开启网络唤醒）");
            tipBar.SetToolTip(btnAutostartOn, "被控端写入注册表 Run 项：开机自动运行本程序（沿用原静默参数）");
            tipBar.SetToolTip(btnAutostartOff, "被控端删除注册表 Run 项：开机不再自动运行（不影响当前运行）");

            bar2.Controls.Add(_btnDispatch);
            bar2.Controls.Add(_btnExportTerminals);
            bar2.Controls.Add(btnSelectAll);
            bar2.Controls.Add(btnRefresh);
            bar2.SetFlowBreak(btnRefresh, true);      // 显式分成两行，不靠容器宽度“碰运气”
            bar2.Controls.Add(btnAssignSeq);
            bar2.Controls.Add(btnReboot);
            bar2.Controls.Add(btnShutdown);
            bar2.Controls.Add(btnWol);
            bar2.Controls.Add(btnAutostartOn);
            bar2.Controls.Add(btnAutostartOff);

            // ---- 第三行：自定义命令 / 分批重启 / 验收 ----
            var btnCustomCmd = new Button { Text = "自定义命令", Width = 100, Height = 32 };
            btnCustomCmd.Click += (s, e) => ShowCustomCmdDialog();
            var btnStagger = new Button { Text = "分批错峰重启", Width = 120, Height = 32 };
            btnStagger.Click += (s, e) => ShowStaggerRebootDialog();
            var btnVerify = new Button { Text = "验收选中", Width = 100, Height = 32 };
            btnVerify.Click += (s, e) => VerifySelected();
            tipBar.SetToolTip(btnCustomCmd, "向选中终端（未选中则全部在线）下发 PowerShell / CMD 脚本或命令，并收回结果");
            tipBar.SetToolTip(btnStagger, "把选中终端分多批、每批间隔若干秒下发重启，避免同时重启压垮交换机/域控；上线后自动验收");
            tipBar.SetToolTip(btnVerify, "向选中终端（未选中则全部在线）下发验收指令，比对实际计算机名/IP 与期望值");
            bar2.Controls.Add(btnCustomCmd);
            bar2.Controls.Add(btnStagger);
            bar2.Controls.Add(btnVerify);

            _lblGridHint = new Label
            {
                Text = "提示：可多选（Ctrl/Shift），右键批量删除/标记；双击“目标主机名 / 目标 IP”可直接改",
                Dock = DockStyle.Bottom,
                Height = 24,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                ForeColor = System.Drawing.Color.DimGray
            };
            tab2.Controls.Add(bar2);
            tab2.Controls.Add(_lblGridHint);

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                ReadOnly = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true,
                // 紧凑排列：列宽按比例填满窗口（一屏看全，不出现横向滚动条）。
                // 想看某列完整内容时手动拖列宽，或把鼠标停在该单元格上看提示。
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowHeadersVisible = false,
                BackgroundColor = System.Drawing.SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle
            };
            _grid.Columns.Add("colMac", "MAC");
            _grid.Columns.Add("colOnline", "在线");
            _grid.Columns.Add("colSrc", "来源IP");
            _grid.Columns.Add("colSuffix", "后缀");
            int cHost = _grid.Columns.Add("colHost", "目标主机名");
            int cIp = _grid.Columns.Add("colIp", "目标IP");
            _grid.Columns.Add("colMask", "掩码");
            _grid.Columns.Add("colGw", "网关");
            _grid.Columns.Add("colDns", "DNS1");
            _grid.Columns.Add("colDns2", "DNS2");
            _grid.Columns.Add("colMode", "模式");
            _grid.Columns.Add("colStatus", "状态");
            _grid.Columns.Add("colErr", "错误信息");
            _grid.Columns.Add("colConf", "冲突");
            _grid.Columns.Add("colVerify", "验收");
            _grid.Columns.Add("colFailCat", "失败归类");
            // DataGridView.ReadOnly=true 会覆盖列设置，所以这里逐列控制
            foreach (DataGridViewColumn col in _grid.Columns) col.ReadOnly = true;
            _grid.Columns[cHost].ReadOnly = false;
            _grid.Columns[cIp].ReadOnly = false;
            _grid.CellEndEdit += _grid_CellEndEdit;
            GridContextMenu();
            tab2.Controls.Add(_grid);
            tab2.Controls.SetChildIndex(_grid, 0);

            // ==================== Tab3 日志报表 ====================
            var tab3 = new TabPage("日志报表") { Padding = new Padding(10) };
            var bar3 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(0, 2, 0, 6), WrapContents = false };
            _btnExportLog = new Button { Text = "导出日志 CSV", Width = 130, Height = 32 };
            _btnExportLog.Click += btnExportLogCsv_Click;
            _btnClearLog = new Button { Text = "清空显示", Width = 100, Height = 32 };
            _btnClearLog.Click += (s, e) => _txtLog.Clear();
            _btnBlacklist = new Button { Text = "管理黑名单…", Width = 120, Height = 32 };
            _btnBlacklist.Click += (s, e) => ShowBlacklist();
            bar3.Controls.Add(_btnExportLog);
            bar3.Controls.Add(_btnClearLog);
            bar3.Controls.Add(_btnBlacklist);
            tab3.Controls.Add(bar3);

            _txtLog = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                ReadOnly = true,
                WordWrap = false,
                BackColor = System.Drawing.Color.White,
                Font = new System.Drawing.Font("Consolas", 9F)
            };
            tab3.Controls.Add(_txtLog);
            tab3.Controls.SetChildIndex(_txtLog, 0);

            _tabs.TabPages.Add(_tabTemplates);
            _tabs.TabPages.Add(tab2);
            _tabs.TabPages.Add(tab3);
            Controls.Add(_tabs);

            // ==================== 状态栏 ====================
            _status = new StatusStrip { SizingGrip = false };
            _lblReported = new ToolStripStatusLabel("已上报: 0");
            _lblPending = new ToolStripStatusLabel("待执行: 0");
            _lblSuccess = new ToolStripStatusLabel("成功: 0");
            _lblFailed = new ToolStripStatusLabel("失败: 0");
            _lblConflict = new ToolStripStatusLabel("冲突: 0");
            _lblOnline = new ToolStripStatusLabel("在线: 0 / 0");
            _lblBroadcast = new ToolStripStatusLabel("广播: 未启动");
            _status.Items.Add(_lblReported);
            _status.Items.Add(new ToolStripSeparator());
            _status.Items.Add(_lblOnline);
            _status.Items.Add(new ToolStripSeparator());
            _status.Items.Add(_lblPending);
            _status.Items.Add(new ToolStripSeparator());
            _status.Items.Add(_lblSuccess);
            _status.Items.Add(new ToolStripSeparator());
            _status.Items.Add(_lblFailed);
            _status.Items.Add(new ToolStripSeparator());
            _status.Items.Add(_lblConflict);
            _status.Items.Add(new ToolStripSeparator());
            _status.Items.Add(_lblBroadcast);
            Controls.Add(_status);
            _tabs.Dock = DockStyle.Fill;
            _status.Dock = DockStyle.Bottom;

            UpdateDhcpPoolPreview();

            UiScale.Apply(this);   // 高 DPI：把上面写死的像素常量按系统缩放比例放大
        }

        /// <summary>按当前模板参数刷新 DHCP 地址池预览。</summary>
        private void UpdateDhcpPoolPreview()
        {
            if (_lblDhcpPool == null || _txtIpPrefix == null || _txtMask == null) return;
            string serverIp = SelectedBroadcastIp() ?? NetworkHelper.GetLocalIpv4();
            if (DhcpConfig.TryDerive(_txtIpPrefix.Text.Trim(), _txtMask.Text.Trim(), _txtGw.Text.Trim(), serverIp, out DhcpConfig cfg, out string err))
                _lblDhcpPool.Text = $"地址池：{cfg.PoolStart} ~ {cfg.PoolEnd}（网关 {cfg.Gateway} 与控制端 {serverIp} 不会被分配；需要管理员权限）";
            else
                _lblDhcpPool.Text = "地址池：" + (err ?? "无法按当前参数推导");
        }

        private static TextBox NewTextBox() => new TextBox { Dock = DockStyle.Fill };

        /// <summary>向 4 列表格按显式 (row, col) 添加“标签 + 输入框”一对。</summary>
        private static void AddField(TableLayoutPanel t, int row, int col, string label, Control input)
        {
            t.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleLeft, AutoSize = false }, col, row);
            t.Controls.Add(input, col + 1, row);
        }

        // ---------- 配置持久化 ----------

        private readonly string _settingsPath = AppConfig.MasterSettingsPath;

        /// <summary>合并写盘：改动后 3 秒写一次，避免每次心跳/上报都写文件。</summary>
        private void ScheduleSave()
        {
            try
            {
                if (_saveTimer == null) return;
                _saveTimer.Stop();
                _saveTimer.Start();
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                var s = new MasterSettings
                {
                    SessionId = _session.SessionId,
                    IpPrefix = _txtIpPrefix.Text,
                    SubnetMask = _txtMask.Text,
                    Gateway = _txtGw.Text,
                    Dns1 = _txtDns1.Text,
                    Dns2 = _txtDns2.Text,
                    HostNamePrefix = _txtHnPrefix.Text,
                    IsDhcp = _chkDhcp.Checked,
                    DhcpServer = _chkDhcpServer.Checked,
                    AdapterAlias = (_cmbAdapter.SelectedItem as NetworkAdapterInfo)?.Alias,
                    Items = _session.Items.ToList()
                };
                s.Save(_settingsPath);
            }
            catch { }
        }

        private void LoadSettings()
        {
            var s = MasterSettings.Load(_settingsPath);
            if (s == null) return;
            try
            {
                if (!string.IsNullOrEmpty(s.IpPrefix)) _txtIpPrefix.Text = s.IpPrefix;
                if (!string.IsNullOrEmpty(s.SubnetMask)) _txtMask.Text = s.SubnetMask;
                if (!string.IsNullOrEmpty(s.Gateway)) _txtGw.Text = s.Gateway;
                if (s.Dns1 != null) _txtDns1.Text = s.Dns1;
                if (s.Dns2 != null) _txtDns2.Text = s.Dns2;
                if (!string.IsNullOrEmpty(s.HostNamePrefix)) _txtHnPrefix.Text = s.HostNamePrefix;
                _chkDhcp.Checked = s.IsDhcp;
                _chkDhcpServer.Checked = s.DhcpServer;

                if (!string.IsNullOrEmpty(s.AdapterAlias))
                {
                    for (int i = 0; i < _cmbAdapter.Items.Count; i++)
                    {
                        if (_cmbAdapter.Items[i] is NetworkAdapterInfo a &&
                            string.Equals(a.Alias, s.AdapterAlias, StringComparison.OrdinalIgnoreCase))
                        { _cmbAdapter.SelectedIndex = i; break; }
                    }
                }

                if (!string.IsNullOrEmpty(s.SessionId))
                    _session.Restore(s.SessionId, s.Items);

                RefreshGrid();
                UpdateStatusBar();
                RefreshBlacklistSummary();
                LogLine($"已恢复上次配置（会话 {ShortId(_session.SessionId)}，终端 {s.Items.Count} 台，保存于 {s.SavedAt:MM-dd HH:mm}）");

                // 重启后接收服务默认没起（要点【开始发布模板】）。把这件事写在界面上，
                // 否则用户看到“终端列表有记录但全是离线”，会误以为被控端掉线了。
                if (s.Items.Count > 0 && !_broadcasting)
                {
                    _lblSession.Text = $"已恢复上次会话 {ShortId(_session.SessionId)}（{s.Items.Count} 台）；点【开始发布模板】继续";
                    _lblBroadcast.Text = "广播: 未启动";
                }

                // 白名单：本次启动一律默认关闭（防止上次开着、这次忘了 → 全部终端被静默拒绝）
                if (_whitelist.PersistedEnabled)
                    LogLine("白名单上次为“启用”，本次启动已默认关闭；需要时请在【白名单…】窗口重新勾选。");
            }
            catch (Exception ex)
            {
                LogLine("恢复上次配置失败：" + ex.Message);
            }
        }

        // ---------- 黑名单展示 ----------

        /// <summary>刷新模板页上的黑/白名单摘要。</summary>
        private void RefreshBlacklistSummary()
        {
            if (_lblBlacklist == null) return;
            string wl = _whitelist.Enabled
                ? $"白名单：已启用 {_whitelist.Macs.Count} 条"
                : $"白名单：未启用（{_whitelist.Macs.Count} 条）";
            _lblBlacklist.Text = $"黑名单：{_blacklist.Macs.Count} 条 MAC / {_blacklist.Ips.Count} 条 IP；{wl}";
        }

        /// <summary>弹窗显示黑名单明细。</summary>
        private void ShowBlacklist()
        {
            RefreshBlacklistSummary();
            using var dlg = new BlacklistForm(_blacklist);
            dlg.Changed += () => RefreshBlacklistSummary();
            dlg.ShowDialog(this);
            RefreshBlacklistSummary();
        }

        /// <summary>弹窗管理 MAC 白名单（启用后只接受名单里的终端上报）。</summary>
        private void ShowWhitelist()
        {
            using var dlg = new WhitelistForm(_whitelist, _session.Items.ToList());
            dlg.ShowDialog(this);
            RefreshBlacklistSummary();
            LogLine($"白名单已更新：{( _whitelist.Enabled ? "启用" : "未启用")}，共 {_whitelist.Macs.Count} 条");
        }

        // ---------- 广播网卡选择 ----------

        /// <summary>下拉项：网卡 + 其 IPv4（广播绑定用）。</summary>
        private class BroadcastAdapter
        {
            public NetworkAdapterInfo Adapter;
            public string Ip;
            public override string ToString() => $"{Adapter.Alias}    IP: {Ip}    [{Adapter.Mac}]" + (Adapter.Virtual ? "  (虚拟)" : "");
        }

        /// <summary>填充有 IPv4 的网卡下拉列表，默认选中带本机 IP 的网卡。</summary>
        private void LoadBroadcastAdapters()
        {
            _cmbAdapter.Items.Clear();
            string localIp = NetworkHelper.GetLocalIpv4();
            foreach (var a in NetworkHelper.GetAdapters())
            {
                string ip = NetworkHelper.GetIpv4ByAdapter(a.Alias);
                if (string.IsNullOrEmpty(ip)) continue; // 只列有 IPv4 的网卡，广播需要绑定 IPv4
                var item = new BroadcastAdapter { Adapter = a, Ip = ip };
                _cmbAdapter.Items.Add(item);
                if (string.Equals(ip, localIp, StringComparison.Ordinal)) _cmbAdapter.SelectedItem = item;
            }
            if (_cmbAdapter.SelectedIndex < 0 && _cmbAdapter.Items.Count > 0) _cmbAdapter.SelectedIndex = 0;
        }

        /// <summary>当前选中的广播网卡 IP；未选择返回 null。</summary>
        private string SelectedBroadcastIp()
            => (_cmbAdapter.SelectedItem as BroadcastAdapter)?.Ip;
    }
}
