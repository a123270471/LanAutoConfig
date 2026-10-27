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
        private readonly UdpBroadcastService _broadcast = new UdpBroadcastService();
        private readonly TcpServerService _server = new TcpServerService();
        private readonly System.Windows.Forms.Timer _sweepTimer = new System.Windows.Forms.Timer();

        private TabControl _tabs;
        private StatusStrip _status;
        private ToolStripStatusLabel _lblReported, _lblPending, _lblSuccess, _lblFailed, _lblConflict, _lblBroadcast;

        // Tab1 模板配置
        private TabPage _tabTemplates;
        private TextBox _txtIpPrefix, _txtMask, _txtGw, _txtDns1, _txtDns2, _txtHnPrefix;
        private Label _lblBlacklist, _lblDhcpPool;
        private CheckBox _chkDhcp, _chkDhcpServer;
        private Button _btnStart, _btnStop, _btnReset, _btnManageBlacklist;
        private Label _lblSession;
        private ComboBox _cmbAdapter;

        // Tab2 终端列表
        private DataGridView _grid;
        private Button _btnDispatch, _btnExportTerminals;
        private Label _lblGridHint;

        // Tab3 日志
        private TextBox _txtLog;
        private Button _btnExportLog, _btnBlacklist, _btnClearLog;

        // DHCP 服务（可选）
        private readonly DhcpServer _dhcp = new DhcpServer();

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
            _logger = new TextLogger(Path.Combine(AppConfig.LogDir, "master.log"));
            _session.NewSession(); // 初始化一个会话 ID

            BuildUi();
            LoadBroadcastAdapters();
            _session.Changed += () => SafeUi(() => { RefreshGrid(); UpdateStatusBar(); });
            _broadcast.Log += m => SafeUi(() => LogLine(m));
            _server.Log += m => SafeUi(() => LogLine(m));
            _server.OnSlaveReport += r => SafeUi(() => OnSlaveReport(r));
            _server.OnResultReport += r => SafeUi(() => OnResultReport(r));

            // 定时回收“执行中但一直没结果”的终端，避免永远卡在执行中
            _sweepTimer.Interval = SweepIntervalMs;
            _sweepTimer.Tick += (s, e) => SweepStaleExecuting();
            _sweepTimer.Start();

            FormClosing += (s, e) =>
            {
                try { _sweepTimer.Stop(); } catch { }
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
                LogLine($"[黑名单拒绝] MAC={m.LocalMac} IP={m.LocalSourceIp}");
                return;
            }
            _session.AddOrUpdate(m);
            LogLine($"新增/更新终端：{m.TargetHostName} @ {(m.IsDhcp ? "DHCP" : m.TargetIp)}（MAC={m.LocalMac} 来源={m.LocalSourceIp}）");
        }

        private void OnResultReport(SlaveResultReportMsg m)
        {
            if (m.SessionId != _session.SessionId)
            {
                LogLine($"[忽略] 会话不一致的执行结果：{m.LocalMac}");
                return;
            }
            var rec = _session.MarkResult(m);
            if (rec != null)
            {
                _db.Insert(rec);
                LogLine($"[结果] {rec.TargetHostName} @ {rec.TargetIp} -> {rec.Result} {rec.ErrorMsg}");
            }
            else
            {
                LogLine($"[结果] 收到未知终端 {m.LocalMac} 的执行结果，未匹配到列表项");
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
                    int idx = _grid.Rows.Add(
                        it.Mac, it.SourceIp, it.Suffix, it.TargetHostName, it.TargetIp,
                        it.SubnetMask, it.Gateway, it.Dns1, it.Dns2,
                        it.IsDhcp ? "DHCP" : "静态",
                        it.StatusDisplay, it.ErrorMsg, conflict ? "冲突" : "");
                    var row = _grid.Rows[idx];
                    row.Tag = it;
                    if (conflict) row.DefaultCellStyle.BackColor = System.Drawing.Color.LightSalmon;
                    if (it.Status == SlaveStatus.Failed) row.DefaultCellStyle.ForeColor = System.Drawing.Color.Red;
                    if (it.Status == SlaveStatus.Skipped) row.DefaultCellStyle.BackColor = System.Drawing.Color.Gainsboro;
                }
            }
            finally { _grid.ResumeLayout(); }

            // 冲突或正在下发时都禁止再次点击下发
            _btnDispatch.Enabled = !hasConflict && !_dispatching;
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

            var root1 = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
            root1.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
            root1.RowStyles.Add(new RowStyle(SizeType.Absolute, 206));
            root1.RowStyles.Add(new RowStyle(SizeType.Absolute, 106));
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

            // --- 黑名单 ---
            var gbBlack = new GroupBox { Text = "黑名单", Dock = DockStyle.Fill };
            var pnlBlack = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(10, 8, 10, 0), WrapContents = false };
            _lblBlacklist = new Label { Text = "黑名单：0 条", AutoSize = true, ForeColor = System.Drawing.Color.DimGray, Margin = new Padding(0, 7, 14, 0) };
            _btnManageBlacklist = new Button { Text = "管理黑名单…", Width = 120, Height = 30 };
            _btnManageBlacklist.Click += (s, e) => ShowBlacklist();
            pnlBlack.Controls.Add(_lblBlacklist);
            pnlBlack.Controls.Add(_btnManageBlacklist);
            gbBlack.Controls.Add(pnlBlack);

            root1.Controls.Add(gbRun, 0, 0);
            root1.Controls.Add(gbParams, 0, 1);
            root1.Controls.Add(gbDhcp, 0, 2);
            root1.Controls.Add(gbBlack, 0, 3);
            root1.Controls.Add(new Panel { Dock = DockStyle.Fill }, 0, 4);   // 底部留白，避免黑名单被拉高
            _tabTemplates.Controls.Add(root1);

            // ==================== Tab2 终端列表 ====================
            var tab2 = new TabPage("终端列表") { Padding = new Padding(10) };
            var bar2 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(0, 2, 0, 6), WrapContents = false };
            _btnDispatch = new Button { Text = "下发执行命令", Width = 140, Height = 32 };
            _btnDispatch.Click += btnDispatch_Click;
            _btnExportTerminals = new Button { Text = "导出终端列表 CSV", Width = 150, Height = 32 };
            _btnExportTerminals.Click += btnExportTerminalsCsv_Click;
            _lblGridHint = new Label
            {
                Text = "提示：双击“目标主机名 / 目标 IP”单元格可直接改，改完自动校验",
                AutoSize = true,
                ForeColor = System.Drawing.Color.DimGray,
                Margin = new Padding(14, 9, 0, 0)
            };
            bar2.Controls.Add(_btnDispatch);
            bar2.Controls.Add(_btnExportTerminals);
            bar2.Controls.Add(_lblGridHint);
            tab2.Controls.Add(bar2);

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                ReadOnly = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowHeadersVisible = false,
                BackgroundColor = System.Drawing.SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle
            };
            _grid.Columns.Add("colMac", "MAC");
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
            _lblBroadcast = new ToolStripStatusLabel("广播: 未启动");
            _status.Items.Add(_lblReported);
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

        // ---------- 黑名单展示 ----------

        /// <summary>刷新模板页上的黑名单摘要（条数）。</summary>
        private void RefreshBlacklistSummary()
        {
            if (_lblBlacklist == null) return;
            _lblBlacklist.Text = $"黑名单：{_blacklist.Macs.Count} 条 MAC / {_blacklist.Ips.Count} 条 IP（在“终端列表”右键添加）";
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
