using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using mySQLPunk.lib;
using mySQLPunk.lib.MssqlImport;
namespace mySQLPunk
{
    internal sealed class MssqlImportWizardForm : Form
    {
        private readonly Func<MssqlImportService> serviceFactory;
        private readonly string database;
        private readonly Panel[] pages = new Panel[4];
        private readonly TextBox source = new TextBox(), target = new TextBox(), logPath = new TextBox(), errors = new TextBox();
        private readonly CheckBox skip = new CheckBox(), confirm = new CheckBox();
        private readonly Label steps = new Label(), status = new Label(), facts = new Label(), checkSummary = new Label(), confirmSummary = new Label();
        private readonly Label stage = new Label(), coverage = new Label(), position = new Label(), elapsed = new Label(), eta = new Label(), errorCount = new Label(), resultText = new Label();
        private readonly Button next = new Button(), back = new Button(), recheck = new Button(), cancel = new Button(), browse = new Button(), mute = new Button();
        private readonly Button openLog = new Button(), openFolder = new Button(), copyPath = new Button();
        private readonly ProgressBar bar = new ProgressBar(), scanBar = new ProgressBar();
        private readonly PunkyReportLoadingControl punky = new PunkyReportLoadingControl(), confirmPunky = new PunkyReportLoadingControl();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 200 };
        private readonly Stopwatch clock = new Stopwatch();
        private readonly object progressGate = new object();
        private MssqlImportProgress latest;
        private MssqlImportPreflight prepared;
        private MssqlImportRequest request;
        private MssqlImportService service;
        private CancellationTokenSource cancellation;
        private Task cancellationDispatch;
        private bool acceptingProgress, terminalRendered;
        private long confirmedBytes, lastAdvance, fileBytes;
        private int generation, step;
        private Guid activeJob;
        private bool busy, executing, closing, cancelRequested, disposing;
        internal MssqlImportResult Result { get; private set; }
        // Instance action boundaries prevent native tests from launching apps or changing the clipboard.
        internal Action<string> PathOpener;
        internal Action<string> ClipboardWriter;
        private static string T(string zh, string en) => ReportText.Get(zh, en);
        internal MssqlImportWizardForm(MssqlImportRequest initialRequest, Func<MssqlImportService> serviceFactory)
        {
            if (initialRequest == null) throw new ArgumentNullException(nameof(initialRequest));
            this.serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory)); database = initialRequest.DatabaseName;
            Name = "mssqlImportWizard"; Text = T("匯入 SQL 檔案 · SQL Server", "Import SQL file · SQL Server");
            AutoScaleMode = AutoScaleMode.Dpi; Font = UiKit.Body; ClientSize = new Size(1100, 750);
            MinimumSize = new Size(640, 480); StartPosition = FormStartPosition.CenterParent;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 4 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); Controls.Add(root);
            var header = Stack(); header.Controls.Add(L(T("SQL 檔案匯入精靈", "SQL file import wizard"), UiKit.Title));
            steps.AutoSize = true; steps.Margin = new Padding(0, 12, 0, 16); header.Controls.Add(steps); root.Controls.Add(header, 0, 0);
            var body = new Panel { Dock = DockStyle.Fill }; root.Controls.Add(body, 0, 1);
            for (int i = 0; i < 4; i++) { pages[i] = new Panel { Name = "importStep" + i, Dock = DockStyle.Fill, AutoScroll = true }; body.Controls.Add(pages[i]); }
            BuildSource(initialRequest); BuildCheck(); BuildConfirm(); BuildExecution();
            status.Name = "importStatus"; status.AutoSize = true; status.Margin = new Padding(0, 10, 0, 8); root.Controls.Add(status, 0, 2);
            var footer = new FlowLayoutPanel { Name = "importFooter", Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, FlowDirection = FlowDirection.RightToLeft };
            Configure(cancel, "importCancel", T("取消", "Cancel"), (s, e) => { if (busy) RequestCancel(); else Close(); });
            Configure(next, "importNext", T("下一步 →", "Next →"), async (s, e) => {
                if (busy || closing || disposing) return;
                if (step == 0) await CheckAsync();
                else if (step == 1 && prepared?.Passed == true) { step = 2; ShowPage(); }
                else if (step == 2 && prepared?.Passed == true && confirm.Checked) await RunAsync();
                else if (step == 3 && Result != null) await CheckAsync();
            });
            Configure(back, "importBack", T("← 上一步", "← Back"), (s, e) => { if (!busy && step > 0 && step < 3) { step--; ShowPage(); } });
            Configure(recheck, "importRecheck", T("重新檢查", "Recheck"), async (s, e) => { if (!busy && !closing) await CheckAsync(); });
            footer.Controls.AddRange(new Control[] { cancel, next, back, recheck }); root.Controls.Add(footer, 0, 3);
            ThemeManager.MarkAsPrimary(next); AcceptButton = next; CancelButton = cancel; cancel.DialogResult = DialogResult.None;
            timer.Tick += (s, e) => RenderHeartbeat();
            ClientSizeChanged += (s, e) => {
                root.Padding = new Padding(ClientSize.Width < 800 || ClientSize.Height < 600 ? 12 : 24);
                steps.MaximumSize = new Size(Math.Max(180, root.ClientSize.Width - root.Padding.Horizontal), 0); status.MaximumSize = steps.MaximumSize;
                foreach (var button in new[] { next, back, cancel, recheck }) { button.Margin = new Padding(3); button.MinimumSize = new Size(85, 32); }
                Reflow();
            };
            Shown += (s, e) => {
                var work = Screen.FromControl(Owner ?? this).WorkingArea;
                MinimumSize = new Size(Math.Min(MinimumSize.Width, work.Width), Math.Min(MinimumSize.Height, work.Height));
                Size = new Size(Math.Min(Width, work.Width), Math.Min(Height, work.Height));
                Location = new Point(Math.Max(work.Left, Math.Min(Left, work.Right - Width)), Math.Max(work.Top, Math.Min(Top, work.Bottom - Height))); Reflow();
            };
            FormClosing += (s, e) => { if (busy) { e.Cancel = true; closing = true; RequestCancel(); } else DiscardPrepared(); };
            ThemeManager.ApplyTo(this); ShowPage();
        }
        private static FlowLayoutPanel Stack() => new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        private static Label L(string text, Font font = null) => new Label { AutoSize = true, Text = text, Font = font ?? UiKit.Body, Margin = new Padding(0, 6, 0, 8) };
        private static void Configure(Button b, string name, string text, EventHandler handler)
        { b.Name = name; b.Text = b.AccessibleName = text; b.AutoSize = true; b.MinimumSize = new Size(90, 34); b.Margin = new Padding(3); b.Click += handler; }
        private void BuildSource(MssqlImportRequest initial)
        {
            var content = Stack(); pages[0].Controls.Add(content);
            content.Controls.Add(L(T("把 SQL 檔案，帶進目標資料庫", "Bring a SQL file into the selected database"), UiKit.Subtitle));
            content.Controls.Add(L(T("先選檔案，再串流檢查；預檢不執行檔案中的 SQL。", "Choose a file, then check it by streaming. Preflight does not execute its SQL.")));
            source.Name = "importSourcePath"; source.Text = initial.SourcePath; source.ReadOnly = true; source.Width = 800;
            source.AccessibleName = T("SQL 檔案完整路徑", "SQL file path"); source.TextChanged += (s, e) => { InvalidatePrepared(); ReadFileSize(); };
            content.Controls.Add(L(T("SQL 檔案", "SQL file"))); content.Controls.Add(source);
            Configure(browse, "importBrowse", T("選擇檔案…", "Choose file…"), (s, e) => {
                if (busy) return;
                using (var dialog = new OpenFileDialog { Filter = Localization.T("Common.SqlFilesFilter"), Multiselect = false, CheckFileExists = true })
                    if (dialog.ShowDialog(this) == DialogResult.OK) source.Text = dialog.FileName;
            }); content.Controls.Add(browse);
            facts.Name = "importFileFacts"; facts.AutoSize = true; content.Controls.Add(facts);
            target.Name = "importTarget"; target.ReadOnly = true; target.Width = 800;
            target.Text = T("已選資料庫：", "Selected database: ") + database + T("；實際伺服器識別將於預檢顯示。", "; resolved server identity appears after preflight.");
            target.AccessibleName = T("已選目標資料庫", "Selected target database"); content.Controls.Add(L(T("目標 · 使用目前資料庫樹的連線設定", "Target · existing selected connection"))); content.Controls.Add(target);
            content.Controls.Add(L(T("更換目標請關閉此精靈，於資料庫樹選擇另一個資料庫後重新開啟。", "To change target, close this wizard and select another database in the tree.")));
            content.Controls.Add(L(T("支援 mySQLPunk 匯出 SQL 與一般 T-SQL／GO；嚴格偵測 UTF-8，或有 BOM 的 UTF-16。單一 GO 批次上限 32 MiB（16,777,216 個 UTF-16 code unit），不是欄位大小限制。", "Supports mySQLPunk SQL and ordinary T-SQL/GO; strict UTF-8 or BOM-marked UTF-16. One GO batch is limited to 32 MiB decoded (16,777,216 UTF-16 code units), not a field-size limit.")));
            content.Controls.Add(L(T("不支援 SQLCMD 指令或直接 USE 切換資料庫。三段名稱、動態 SQL 或程序仍可能存取其他資料庫，請確認腳本來源。精靈不會自動建立、清空或覆寫資料庫。", "No SQLCMD directives or direct USE changes. Three-part names, dynamic SQL and procedures can still access other databases; review the script source. No automatic database creation or clearing."))); ReadFileSize();
        }
        private void BuildCheck()
        {
            var content = Stack(); pages[1].Controls.Add(content);
            content.Controls.Add(L(T("先確認，這份檔案能不能匯入", "Check the file and target before importing"), UiKit.Subtitle));
            content.Controls.Add(L(T("完整串流預掃描：編碼、GO 批次、格式摘要、目標與 log 可寫性。這是分析進度，尚未執行 SQL。", "Streaming preflight checks encoding, GO batches, format summary, target and log writability. No source SQL has executed.")));
            scanBar.Name = "importScanProgress"; scanBar.Width = 800; scanBar.Height = 10; content.Controls.Add(scanBar);
            checkSummary.Name = "importCheckSummary"; checkSummary.AutoSize = true; checkSummary.Text = T("尚未開始檢查。", "Not checked yet."); content.Controls.Add(checkSummary);
        }
        private void BuildConfirm()
        {
            var content = Stack(); pages[2].Controls.Add(content);
            content.Controls.Add(L(T("準備好，開始匯入這份資料", "Review before starting this import"), UiKit.Subtitle));
            confirmSummary.Name = "importConfirmSummary"; confirmSummary.AutoSize = true; content.Controls.Add(confirmSummary);
            skip.Name = "importSkipErrors"; skip.AutoSize = true; skip.Text = T("遇到 SQL 錯誤時，略過失敗 GO 批次並繼續（變更後須重新檢查）", "Skip failed GO batches on eligible SQL errors (recheck after changing)");
            skip.AccessibleName = skip.Text; skip.Checked = false; skip.CheckedChanged += (s, e) => InvalidatePrepared(); content.Controls.Add(skip);
            content.Controls.Add(L(T("略過不會撤回該批已生效的語句。timeout、斷線、交易／LOB 狀態不明及 log 寫入失敗仍停止。", "Skipping does not undo statements already applied. Timeout, disconnection, uncertain transaction/LOB state and log failures still stop.")));
            confirm.Name = "importConfirmTarget"; confirm.AutoSize = true; confirm.Text = T("已確認目標資料庫，並了解異常時可能保留部分資料。", "I confirm the target and understand that partial writes may remain.");
            confirm.AccessibleName = confirm.Text; confirm.CheckedChanged += (s, e) => UpdateButtons(); content.Controls.Add(confirm);
            confirmPunky.Name = "importConfirmPunky"; confirmPunky.Size = new Size(200, 200); content.Controls.Add(confirmPunky);
        }
        private void BuildExecution()
        {
            var content = Stack(); pages[3].Controls.Add(content);
            stage.Name = "importStage"; stage.Font = UiKit.Subtitle; coverage.Name = "importCoverage"; position.Name = "importPosition"; elapsed.Name = "importElapsed"; eta.Name = "importEta";
            resultText.Name = "importResult"; resultText.AutoSize = true; resultText.Visible = false;
            var statsRow = new TableLayoutPanel { Name = "importStatsLayout", AutoSize = true, ColumnCount = 2, RowCount = 1, Width = 1000 };
            statsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72)); statsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
            var stats = Stack(); statsRow.Controls.Add(stats, 0, 0); content.Controls.Add(statsRow);
            foreach (var label in new[] { stage, coverage, position, elapsed, eta }) { label.AutoSize = true; label.Margin = new Padding(0, 4, 0, 4); stats.Controls.Add(label); }
            bar.Name = "importProgress"; bar.Width = 650; bar.Height = 10; stats.Controls.Add(bar);
            var mascot = new FlowLayoutPanel { AutoSize = true, WrapContents = true, FlowDirection = FlowDirection.LeftToRight };
            punky.Name = "importPunky"; punky.Size = new Size(160, 160); mascot.Controls.Add(punky);
            Configure(mute, "importMute", T("靜音", "Mute"), (s, e) => { punky.Muted = !punky.Muted; mute.Text = mute.AccessibleName = punky.Muted ? T("開啟聲音", "Unmute") : T("靜音", "Mute"); });
            mute.Enabled = punky.AudioAvailable; mute.AccessibleDescription = T("每次實際執行只在首輪播放一次；取消靜音不會重播。", "Sound plays once per execution job; unmuting never replays it."); mascot.Controls.Add(mute);
            statsRow.Controls.Add(mascot, 1, 0); content.Controls.Add(resultText);
            content.Controls.Add(L(T("錯誤紀錄 · 最近預覽與本機 log", "Error log · recent preview and local log"), UiKit.Subtitle));
            logPath.Name = "importLogPath"; logPath.ReadOnly = true; logPath.Multiline = true; logPath.WordWrap = true; logPath.ScrollBars = ScrollBars.Vertical; logPath.Height = 42; logPath.Width = 800; logPath.AccessibleName = T("完整 log 路徑", "Full log path"); content.Controls.Add(logPath);
            errors.Name = "importErrorText"; errors.ReadOnly = true; errors.Multiline = true; errors.ScrollBars = ScrollBars.Both; errors.WordWrap = false; errors.Width = 800; errors.Height = 115; errors.MaxLength = 32768;
            errors.AccessibleName = T("最近錯誤紀錄（唯讀）", "Recent errors (read only)"); content.Controls.Add(errors);
            errorCount.Name = "importErrorCount"; errorCount.AutoSize = true; content.Controls.Add(errorCount);
            var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
            Configure(openLog, "importOpenLog", T("開啟 log", "Open log"), (s, e) => OpenLogPath(false));
            Configure(openFolder, "importOpenFolder", T("開啟資料夾", "Open folder"), (s, e) => OpenLogPath(true));
            Configure(copyPath, "importCopyPath", T("複製路徑", "Copy path"), (s, e) => {
                if (!HasLog()) return;
                try { if (ClipboardWriter != null) ClipboardWriter(logPath.Text); else Clipboard.SetText(logPath.Text); status.Text = T("已複製 log 路徑。", "Log path copied."); }
                catch (Exception) { status.Text = T("無法複製路徑，請選取上方完整路徑手動複製。", "Cannot copy; select the full path above and copy it manually."); }
            }); actions.Controls.AddRange(new Control[] { openLog, openFolder, copyPath }); content.Controls.Add(actions);
        }
        private void ReadFileSize()
        {
            fileBytes = 0; try { if (File.Exists(source.Text)) fileBytes = new FileInfo(source.Text).Length; } catch (Exception) { }
            facts.Text = fileBytes.ToString("N0") + T(" bytes；編碼與來源摘要待預檢。", " bytes; encoding and source summary await preflight."); UpdateButtons();
        }
        private void Reflow()
        {
            foreach (var page in pages)
            {
                int width = Math.Max(180, page.ClientSize.Width - 28);
                foreach (Control container in page.Controls)
                {
                    container.MaximumSize = new Size(width, 0);
                    foreach (Control c in container.Controls)
                    {
                        if (c is Label || c is CheckBox) c.MaximumSize = new Size(Math.Max(160, width - 8), 0);
                        if (c is TextBox || c is ProgressBar) c.Width = Math.Max(160, width - 8);
                        if (c is FlowLayoutPanel) c.MaximumSize = new Size(width, 0);
                        if (c.Name == "importStatsLayout") {
                            c.Width = width; c.MaximumSize = new Size(width, 0); int statWidth = Math.Max(120, (int)(width * .69));
                            foreach (var label in new[] { stage, coverage, position, elapsed, eta }) label.MaximumSize = new Size(statWidth, 0);
                            bar.Width = statWidth; int side = Math.Max(80, Math.Min(160, (int)(width * .23))); punky.Size = new Size(side, side);
                        }
                    }
                }
            }
        }
        private void ShowPage()
        {
            for (int i = 0; i < 4; i++) { pages[i].Visible = step == i; if (step == i) pages[i].BringToFront(); }
            steps.Text = T("① 檔案與目標  →  ② 匯入檢查  →  ③ 確認執行  →  ④ 執行與結果", "① File and target  →  ② Checks  →  ③ Confirm  →  ④ Run and result");
            if (step == 2) status.Text = T("請確認實際目標與錯誤政策後開始匯入；開始會修改目標資料庫。", "Confirm actual target and policy before starting; execution changes the database.");
            confirmPunky.SetRunning(step == 2 && !closing); Reflow(); UpdateButtons();
        }
        private void UpdateButtons()
        {
            if (disposing) return;
            next.Visible = step < 3 || !busy && Result != null;
            next.Enabled = !busy && !closing && (step == 0 ? !string.IsNullOrWhiteSpace(source.Text) && !string.IsNullOrWhiteSpace(database) : step == 1 ? prepared?.Passed == true : step == 2 ? prepared?.Passed == true && confirm.Checked : Result != null);
            next.Text = next.AccessibleName = step == 2 ? T("開始匯入", "Start import") : step == 3 ? T("重新檢查匯入", "Recheck import") : T("下一步 →", "Next →");
            back.Visible = step > 0 && step < 3; back.Enabled = !busy && !closing;
            recheck.Visible = step == 1 || step == 2; recheck.Enabled = !busy && !closing;
            cancel.Enabled = !cancelRequested; cancel.Text = cancel.AccessibleName = busy ? cancelRequested ? T("正在停止…", "Stopping…") : T("取消匯入", "Cancel import") : T("關閉", "Close");
            browse.Enabled = source.Enabled = skip.Enabled = !busy && !closing; confirm.Enabled = !busy && prepared?.Passed == true;
            bool hasLog = HasLog(); openLog.Enabled = openFolder.Enabled = copyPath.Enabled = hasLog;
        }
        private void InvalidatePrepared()
        {
            if (executing || disposing) return;
            lock (progressGate) { generation++; latest = null; activeJob = Guid.Empty; }
            if (busy) { cancelRequested = true; SignalCancellation(); }
            DiscardPrepared(); confirm.Checked = false; Result = null;
            checkSummary.Text = T("檔案或錯誤政策已變更，請重新檢查。", "File or policy changed; recheck required.");
            confirmSummary.Text = T("舊預檢已失效。重新檢查後，請再確認實際目標。", "Previous preflight is invalid. Recheck and confirm the actual target."); UpdateButtons();
        }
        private void DiscardPrepared()
        {
            var old = prepared; prepared = null; request = null; service = null; if (old == null) return;
            try { old.Dispose(); }
            catch (Exception) { if (!disposing) status.Text = T("預檢資源釋放時發生異常，請保留 log 並重新檢查。", "Preflight resource release failed; retain the log and recheck."); }
        }
        private void BeginOperation(MssqlImportRequest current, bool run)
        {
            busy = true; executing = run; terminalRendered = false; cancelRequested = false; cancellation = new CancellationTokenSource(); cancellationDispatch = null; clock.Restart();
            lock (progressGate) { acceptingProgress = true; latest = null; activeJob = current.JobId; confirmedBytes = 0; lastAdvance = Stopwatch.GetTimestamp(); }
            timer.Start(); UpdateButtons();
        }
        private void Publish(MssqlImportProgress progress, int version, Guid job)
        {
            if (progress == null || progress.JobId != job) return;
            lock (progressGate)
            {
                if (disposing || !acceptingProgress || version != generation || activeJob != job) return;
                if (progress.ProcessedBytes > confirmedBytes) { confirmedBytes = progress.ProcessedBytes; lastAdvance = Stopwatch.GetTimestamp(); }
                latest = progress;
            }
        }
        private bool Current(int version, Guid job) { lock (progressGate) return !disposing && !IsDisposed && version == generation && activeJob == job; }
        private async Task CheckAsync()
        {
            if (busy || closing || disposing) return;
            DiscardPrepared(); confirm.Checked = false; Result = null; errors.Text = ""; resultText.Text = ""; resultText.Visible = false; logPath.Text = ""; scanBar.Value = 0;
            lock (progressGate) generation++;
            var current = new MssqlImportRequest(Guid.NewGuid(), source.Text, database, skip.Checked); int version = generation;
            step = 1; checkSummary.ForeColor = ThemeManager.TextColor; checkSummary.Text = T("正在串流分析檔案…", "Analyzing file by streaming…"); status.Text = checkSummary.Text;
            ShowPage(); BeginOperation(current, false); var owner = cancellation; var token = owner.Token;
            try
            {
                var value = await Task.Run(() => {
                    var worker = serviceFactory(); return Tuple.Create(worker, worker.Preflight(current, p => Publish(p, version, current.JobId), token));
                });
                if (!Current(version, current.JobId) || token.IsCancellationRequested || cancelRequested || closing) {
                    value.Item2.Dispose();
                    if (Current(version, current.JobId) && !closing) status.Text = checkSummary.Text = T("檢查已停止，尚未執行 SQL。", "Check stopped; no source SQL executed.");
                    return;
                }
                service = value.Item1; prepared = value.Item2; request = current; RenderCheck(prepared);
            }
            catch (Exception) { if (Current(version, current.JobId)) status.Text = checkSummary.Text = token.IsCancellationRequested ? T("檢查已停止，尚未執行 SQL。", "Check stopped; no source SQL executed.") : T("無法完成預檢，請確認檔案與連線設定後重試。", "Preflight failed. Review file and connection settings and retry."); }
            finally { await EndOperation(owner); }
        }
        private void RenderCheck(MssqlImportPreflight check)
        {
            string details = "";
            if (check.Scan != null)
            {
                var scan = check.Scan; fileBytes = scan.FileBytes; scanBar.Style = ProgressBarStyle.Continuous; scanBar.Value = 100;
                details += source.Text + "\r\n" + scan.FileBytes.ToString("N0") + " bytes · " + scan.EncodingName + " · " + scan.BatchCount.ToString("N0") + T(" 批次", " batches") + "\r\n";
                details += T("最大批次：", "Largest batch: ") + scan.MaxBatchCodeUnits.ToString("N0") + " UTF-16 code units\r\n";
                details += ScriptSummary(scan) + "\r\n"; facts.Text = details;
                if (string.Equals(scan.Script.SourceReadMode, "READ COMMITTED", StringComparison.OrdinalIgnoreCase)) details += T("來源 READ COMMITTED：各表可能來自不同時間點，匯入可能遇到外鍵衝突；完整摘要不保證跨表一致。", "Source READ COMMITTED: tables may reflect different moments; foreign keys may fail. A complete footer does not prove consistency.") + "\r\n";
            }
            if (check.Target != null)
            {
                var t = check.Target; target.Text = t.ServerName + " / " + t.DatabaseName + " · ID " + t.DatabaseId + " · " + t.DatabaseCreateDate.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                details += T("實際目標：", "Actual target: ") + target.Text + "\r\nSQL Server " + t.Version + " · " + (t.Writable ? "READ_WRITE" : "READ_ONLY") + "\r\n";
                details += T("可見使用者物件：", "Visible user objects: ") + (t.MetadataVisible && t.UserObjectCount.HasValue ? t.UserObjectCount.Value.ToString("N0") : T("無法完整確認", "not fully known")) + "\r\n";
            }
            details += string.Join("\r\n", check.Issues.Select(i => (i.IsWarning ? T("提醒：", "Warning: ") : T("阻擋：", "Blocked: ")) + Explain(i.Code) + (i.BatchNumber > 0 ? " · " + T("批次 ", "batch ") + i.BatchNumber + T("／行 ", "/line ") + i.Line + " / byte " + i.ByteOffset : "")));
            checkSummary.Text = details; checkSummary.ForeColor = check.Passed ? ThemeManager.TextColor : ThemeManager.DangerColor;
            status.Text = check.Passed ? T("檢查通過，尚未執行 SQL。請到下一步確認。", "Preflight passed; no source SQL executed. Continue to review.") : T("有未通過的項目，暫時無法開始匯入。", "Preflight has blocking issues; import cannot start.");
            confirmSummary.Text = details + "\r\n\r\n" + T("完整 log：", "Full log: ") + check.LogPath + "\r\n\r\n" + PartialWarning();
            logPath.Text = check.LogPath ?? ""; UpdateButtons(); Reflow();
        }
        private static string ScriptSummary(MssqlImportScan scan)
        {
            var script = scan.Script;
            if (script.Kind == MssqlImportScriptKind.Generic) return T("一般 T-SQL／GO；無匯出筆數摘要。", "Generic T-SQL/GO; no export row summary.");
            return (script.Kind == MssqlImportScriptKind.NativeSchema ? T("mySQLPunk 結構匯出", "mySQLPunk schema export") : T("mySQLPunk 資料型匯出", "mySQLPunk data-only export")) + " · " + (script.ExpectedTables?.ToString("N0") ?? "?") + T(" 表／", " tables / ") + (script.ExpectedRows?.ToString("N0") ?? "?") + T(" 筆（檔尾預期摘要，非內容證明）", " rows (footer expectations, not content proof)") + " · " + script.SourceReadMode;
        }
        private async Task RunAsync()
        {
            if (busy || closing || disposing || prepared?.Passed != true || !confirm.Checked || request == null || service == null) return;
            var current = request; var check = prepared; var worker = service; int version = generation;
            Result = null; errors.Text = resultText.Text = ""; resultText.Visible = false; step = 3; ShowPage(); BeginOperation(current, true);
            var owner = cancellation; var token = owner.Token; stage.ForeColor = ThemeManager.TextColor; stage.Text = status.Text = T("正在執行匯入…", "Import executing…");
            coverage.Text = T("已處理檔案範圍：0 / ", "Processed file coverage: 0 / ") + check.Scan.FileBytes.ToString("N0") + " bytes";
            position.Text = T("目標：", "Target: ") + target.Text + "\r\n" + T("正在確認執行 session，尚未確認任何批次。", "Checking execution session; no batch acknowledged yet.");
            elapsed.Text = T("已經過：00:00:00", "Elapsed: 00:00:00"); eta.Text = T("預估剩餘：未知／計算中", "Remaining: unknown / estimating");
            errorCount.Text = T("目前沒有錯誤；完整 log 路徑見上方。", "No errors yet; full log path above."); bar.Style = ProgressBarStyle.Continuous; bar.Value = 0;
            punky.BeginExport(); // Only execution starts sound; scanning/navigation uses silent animation.
            try
            {
                try
                {
                    Result = await Task.Run(() => worker.Run(current, check, p => Publish(p, version, current.JobId), token));
                }
                catch (Exception)
                {
                    Result = new MssqlImportResult(current.JobId, MssqlImportStatus.CleanupUncertain, check.Scan.FileBytes,
                        logPath: check.LogPath, validationSummary: "UnexpectedWorkerFailure;CleanupUnconfirmed");
                }
                // Rendering cannot replace an authoritative worker result with a synthetic outcome.
                if (Current(version, current.JobId)) { RenderHeartbeat(); RenderResult(Result); }
            }
            finally
            {
                try { check.Dispose(); }
                finally { prepared = null; request = null; service = null; await EndOperation(owner); }
            }
        }
        private async Task EndOperation(CancellationTokenSource owner)
        {
            // Provider Cancel may wait synchronously. Keep the same CTS alive until its dispatch returns.
            if (cancellationDispatch != null) { try { await cancellationDispatch; } catch (Exception) { /* Worker result remains authoritative. */ } }
            lock (progressGate) { acceptingProgress = false; latest = null; activeJob = Guid.Empty; }
            owner.Dispose(); if (ReferenceEquals(cancellation, owner)) cancellation = null;
            busy = executing = false; cancelRequested = false; clock.Stop(); if (disposing || IsDisposed) return;
            timer.Stop(); punky.EndExport(); UpdateButtons(); if (closing) Close();
        }
        private void RequestCancel()
        {
            if (!busy || terminalRendered || cancelRequested || cancellation == null) return;
            cancelRequested = true; status.Text = stage.Text = T("正在停止匯入，等待指令回應與清理完成…", "Stopping import; waiting for command response and cleanup…");
            eta.Text = T("預估剩餘：未知（正在停止）", "Remaining: unknown (stopping)"); RenderHeartbeat(); SignalCancellation(); UpdateButtons();
        }
        private void SignalCancellation()
        {
            if (cancellation == null || cancellationDispatch != null) return;
            var owner = cancellation;
            cancellationDispatch = Task.Run(() => owner.Cancel());
        }
        private void RenderHeartbeat()
        {
            if (disposing || IsDisposed || !busy || terminalRendered) return;
            MssqlImportProgress p; long advance; lock (progressGate) { p = latest; advance = lastAdvance; }
            if (p != null)
            {
                if (executing)
                {
                    stage.Text = cancelRequested ? T("正在停止匯入，等待清理…", "Stopping import; awaiting cleanup…") : StageText(p);
                    coverage.Text = T("已處理檔案範圍：", "Processed file coverage: ") + p.ProcessedBytes.ToString("N0") + " / " + p.FileBytes.ToString("N0") + " bytes · " + T("成功批次 ", "successful batches ") + p.SuccessfulBatches.ToString("N0") + T("；失敗／略過 ", "; failed/skipped ") + p.FailedBatches.ToString("N0") + "\r\n" + T("成功範圍 ", "Successful coverage ") + p.SuccessfulBytes.ToString("N0") + T(" bytes；略過範圍 ", " bytes; skipped coverage ") + p.SkippedBytes.ToString("N0") + " bytes";
                    position.Text = T("目標：", "Target: ") + target.Text + "\r\n" + (p.BatchNumber > 0 ? T("目前批次 ", "Current batch ") + p.BatchNumber.ToString("N0") + T(" · 行 ", " · lines ") + p.StartLine.ToString("N0") + "–" + p.EndLine.ToString("N0") + " · byte " + p.StartByte.ToString("N0") + "–" + p.EndByte.ToString("N0") : p.Stage == MssqlImportStage.Executing ? T("正在確認執行 session，尚未確認任何批次。", "Checking execution session; no batch acknowledged yet.") : T("正在核對或收尾；請以最終結果為準。", "Verifying or finishing; await the terminal result."));
                    bar.Style = ProgressBarStyle.Continuous; bar.Value = Percent(p.ProcessedBytes, p.FileBytes);
                    if (errors.Text != p.RecentErrorText) errors.Text = p.RecentErrorText;
                    logPath.Text = p.LogPath ?? logPath.Text;
                    errorCount.Text = (p.LogTruncated ? T("已截段；僅保留最近最多 100 筆／64 KiB UTF-16 文字。", "Truncated; recent at most 100 events / 64 KiB UTF-16 text.") : T("最近錯誤紀錄。", "Recent error events.")) + T(" 共 ", " Total ") + p.TotalErrors.ToString("N0") + T(" 筆；完整 log 路徑見上方。", " events; full log path above.");
                }
                else {
                    checkSummary.Text = T("正在串流分析：", "Streaming analysis: ") + p.ScannedBytes.ToString("N0") + " / " + fileBytes.ToString("N0") + T(" bytes；尚未執行 SQL。", " bytes; no source SQL executed.");
                    scanBar.Style = ProgressBarStyle.Continuous; scanBar.Value = Percent(p.ScannedBytes, fileBytes);
                }
            }
            elapsed.Text = T("已經過：", "Elapsed: ") + Duration(clock.Elapsed);
            bool stale = (Stopwatch.GetTimestamp() - advance) / (double)Stopwatch.Frequency >= 5;
            bool known = executing && !cancelRequested && !stale && p != null && p.Stage == MssqlImportStage.Executing && p.Phase != MssqlImportPhase.Indexes && p.Phase != MssqlImportPhase.Constraints && p.Remaining.HasValue && p.EstimatedFinish.HasValue;
            eta.Text = known ? T("預估剩餘：約 ", "Remaining: about ") + Duration(p.Remaining.Value) + T(" · 預計完成 ", " · Estimated finish ") + p.EstimatedFinish.Value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) : T("預估剩餘：未知／計算中（目前指令或核對中）", "Remaining: unknown / estimating (current command or verification)"); UpdateButtons();
        }
        private void RenderResult(MssqlImportResult result)
        {
            bool truncated;
            lock (progressGate) truncated = latest != null && latest.JobId == result.JobId && latest.LogTruncated;
            terminalRendered = true; stage.Text = status.Text = StatusText(result.Status);
            stage.ForeColor = result.Status == MssqlImportStatus.Completed ? ThemeManager.SuccessColor : result.Status == MssqlImportStatus.CompletedWithErrors || result.Status == MssqlImportStatus.Cancelled ? ThemeManager.WarningColor : ThemeManager.DangerColor;
            elapsed.Text = T("已經過：", "Elapsed: ") + Duration(result.Elapsed); eta.Text = T("預估剩餘：未知（作業已結束）", "Remaining: unknown (job ended)"); logPath.Text = result.LogPath ?? logPath.Text;
            resultText.Visible = true; resultText.Text = StatusText(result.Status) + "\r\n" + T("目標：", "Target: ") + target.Text + "\r\n" + T("成功批次：", "Successful batches: ") + result.SuccessfulBatches.ToString("N0") + T("；失敗／略過：", "; failed/skipped: ") + result.FailedBatches.ToString("N0") + "\r\n" + T("已處理檔案範圍：", "Processed file coverage: ") + result.ProcessedBytes.ToString("N0") + " / " + result.FileBytes.ToString("N0") + " bytes" + "\r\n" + T("最後確認批次：", "Last acknowledged batch: ") + result.LastAcknowledgedBatch.ToString("N0") + T("；未確認批次：", "; uncertain batch: ") + (result.UncertainBatch?.ToString("N0") ?? T("無", "none")) + "\r\n" + string.Join("\r\n", (result.ValidationSummary ?? "ImportFailed").Split(';').Select(Explain)) + "\r\n" + CleanupText(result.Cleanup) + "\r\n" + PartialWarning();
            coverage.Text = T("已處理檔案範圍：", "Processed file coverage: ") + result.ProcessedBytes.ToString("N0") + " / " + result.FileBytes.ToString("N0") + " bytes"; bar.Style = ProgressBarStyle.Continuous; bar.Value = Percent(result.ProcessedBytes, result.FileBytes);
            errorCount.Text = (truncated ? T("已截段；最近最多 100 筆／64 KiB UTF-16。", "Truncated; recent at most 100 events / 64 KiB UTF-16.") : T("最近錯誤紀錄。", "Recent errors.")) + T(" 共 ", " Total ") + result.TotalErrors.ToString("N0") + T(" 筆；完整 log 路徑見上方。", " events; full log path above."); Reflow();
        }
        private static int Percent(long bytes, long total) => total <= 0 ? 0 : (int)Math.Max(0, Math.Min(100, 100.0 * bytes / total));
        private static string Duration(TimeSpan value) => ((long)Math.Max(0, value.TotalHours)).ToString("00") + ":" + value.Minutes.ToString("00") + ":" + value.Seconds.ToString("00");
        private static string PartialWarning() => T("已執行的結構與資料可能保留，目前批次也可能部分生效；沒有整份檔案的自動回復或續跑。請先檢查目標與 log，再決定如何重新匯入。", "Applied schema/data and partial current-batch effects may remain. No whole-file undo or resume. Review the target and log before importing again.");
        private static string StatusText(MssqlImportStatus status)
        {
            switch (status)
            {
                case MssqlImportStatus.Completed: return T("匯入執行完成", "Import execution completed");
                case MssqlImportStatus.CompletedWithErrors: return T("執行完成，但有錯誤", "Execution completed with errors");
                case MssqlImportStatus.Cancelled: return T("這次匯入已取消", "Import cancelled");
                case MssqlImportStatus.CleanupUncertain: return T("清理或執行結果尚未確認", "Cleanup or execution outcome is unconfirmed");
                default: return T("匯入已停止：發生錯誤", "Import stopped: error");
            }
        }
        private static string StageText(MssqlImportProgress p)
        {
            if (p.Stage == MssqlImportStage.Stopping) return T("正在停止匯入，等待清理…", "Stopping import; awaiting cleanup…");
            if (p.Stage == MssqlImportStage.Verifying) return T("正在核對 session、檔案與必要摘要…", "Verifying session, file and applicable summary…");
            if (p.Stage == MssqlImportStage.Finished) return T("正在整理執行結果…", "Preparing terminal result…");
            return p.Phase == MssqlImportPhase.Indexes ? T("正在建立索引…", "Creating indexes…") : p.Phase == MssqlImportPhase.Constraints ? T("正在建立約束…", "Creating constraints…") : p.Phase == MssqlImportPhase.Schema ? T("正在執行結構批次…", "Executing schema batches…") : T("正在依序執行 SQL 批次…", "Executing SQL batches in order…");
        }
        private static string CleanupText(MssqlImportCleanup cleanup)
        {
            if (cleanup == null) return T("清理狀態：尚未取得確認。", "Cleanup: no confirmed receipt.");
            return T("指令已結束：", "Command ended: ") + Yes(cleanup.CommandEnded) + T("；session 已釋放：", "; session released: ") + Yes(cleanup.SessionReleased) + T("；未提交交易清理已確認：", "; transaction cleanup confirmed: ") + Yes(cleanup.TransactionCleanupConfirmed) + (string.IsNullOrWhiteSpace(cleanup.IssueCode) ? "" : "\r\n" + string.Join("\r\n", cleanup.IssueCode.Split(';').Select(Explain)));
        }
        private static string Yes(bool value) => value ? T("是", "yes") : T("尚未確認", "unconfirmed");
        internal static string Explain(string code)
        {
            switch (code)
            {
                case "SourceRequired": return T("請先選擇 SQL 檔案。", "Choose a SQL file.");
                case "TargetRequired": return T("請先選擇目標資料庫。", "Select a target database.");
                case "TargetReadOnly": return T("目標資料庫為唯讀，無法匯入。", "Target is read only.");
                case "TargetIdentityUnavailable": return T("無法確認實際伺服器與資料庫識別。", "Actual server/database identity unavailable.");
                case "TargetVisibilityRequired": return T("無法完整確認目標物件；結構匯出須確認空庫。", "Complete visibility is required to prove an empty schema target.");
                case "TargetNotEmpty": return T("結構匯出需要空目標；請另選資料庫，精靈不會清空物件。", "Schema import requires an empty target; select another database.");
                case "CreateTableRequired": return T("無法確認建立資料表的必要權限。", "Required CREATE TABLE permission unconfirmed.");
                case "CreateTableNotProven": case "CreateViewNotProven": return T("建立物件權限未能完整確認，實際 SQL 仍可能失敗。", "Creation permissions not fully known; SQL may still fail.");
                case "TargetNotProvenEmpty": return T("目標未確認為空；一般或資料型腳本依自身 SQL 與權限執行。", "Target not proven empty; generic/data SQL uses existing permissions.");
                case "SessionIntrinsicsMayChange": return T("批次間狀態查詢可能改變 @@ROWCOUNT／@@ERROR；需保留時請在同一 batch 先自行保存。", "Interbatch probes may change @@ROWCOUNT/@@ERROR; preserve them in the same batch if needed.");
                case "PartialBatchWritesMayRemain": return PartialWarning();
                case "Cancelled": case "PreviouslyCommittedWritesMayRemain": return T("已停止排程；先前已提交的寫入可能保留。", "Scheduling stopped; previously committed writes may remain.");
                case "LogIncomplete": return T("完整錯誤紀錄未確認存妥。請保留畫面內容與現有 log，後續批次已停止。", "Full log persistence unconfirmed. Retain preview and existing log; later batches stopped.");
                case "SqlBatchFailed": return T("SQL 批次失敗；請查看下方錯誤與完整 log。", "SQL batch failed; inspect errors and full log.");
                case "AllBatchesExecuted": return T("所有批次已完成執行。", "All batches executed.");
                case "NoDataContentValidation": return T("一般／資料型腳本未提供可比較的完整摘要；未核對資料內容。", "Generic/data SQL has no comparable full manifest; data contents not verified.");
                case "AllBatchesProcessedWithErrors": case "FullRestoreNotValidated": return T("已遍歷檔案但含失敗／略過批次，未通過完整還原核對。", "File processed with failed/skipped batches; full restore not validated.");
                case "NativeSummaryCountsMatched": return T("native 摘要的資料表數與總筆數相符。", "Native footer table and total-row counts match.");
                case "NotColumnContentValidation": return T("此核對僅限表數與總筆數，不代表逐欄資料內容相同。", "Count-only validation; column contents were not compared.");
                case "TargetChanged": return T("目標識別已變更，作業停止。", "Target identity changed; job stopped.");
                case "DigestMismatch": return T("檔案 digest 與預掃描不符，已執行部分無法自動撤回。", "File digest differs from preflight; applied changes cannot be undone.");
                case "SummaryMismatch": return T("資料表數或總筆數與 native 摘要不符。", "Counts differ from native summary.");
                case "EofTransactionOpen": return T("檔尾仍有未結交易，不能視為成功。", "An open transaction remains at EOF.");
                case "SessionStateInvalid": return T("無法確認 session／交易狀態，作業停止。", "Session/transaction state invalid; stopped.");
                case "LobStateInvalid": case "LobInitializationMissing": return T("native LOB 傳輸狀態不完整或不一致，不能略過。", "Native LOB state missing/inconsistent; cannot skip.");
                case "PreflightInvalidOrConsumed": return T("預檢已失效或已使用，請重新檢查。", "Preflight invalid/consumed; recheck.");
                case "EmbeddedNul": return T("v1 不支援 SQL 文字中的 U+0000；也請確認是否為無 BOM 的 UTF-16。", "v1 does not support U+0000 in SQL text; check for BOM-less UTF-16.");
                case "BatchTooLarge": return T("單一 GO 批次超過 32 MiB 解碼上限，請調整來源匯出批次後重產檔案。", "GO batch exceeds 32 MiB decoded; regenerate smaller batches.");
                case "UnsupportedGo": case "UnsupportedGoSyntax": return T("GO 語法不支援：不可重複次數或在分隔行使用 block comment。", "Unsupported GO repeat count or block comment delimiter.");
                case "SqlCmdUnsupported": case "SqlcmdUnsupported": case "UnsupportedSqlCmd": case "UnsupportedSqlcmd": return T("不支援 SQLCMD 指令或變數替代。", "SQLCMD directives/substitution unsupported.");
                case "InvalidEncoding": case "UnsupportedEncoding": return T("檔案編碼無效或不支援；需要 UTF-8 或帶 BOM 的 UTF-16。", "Invalid encoding; use UTF-8 or BOM-marked UTF-16.");
                case "DatabaseChange": case "CrossDatabaseUse": case "UnsupportedUse": return T("不支援直接 USE 切換至另一資料庫。", "Direct USE to another database unsupported.");
                case "CleanupStillActive": case "SessionUnavailable": case "TransactionCleanupUnconfirmed": case "TransactionCleanupFailed": case "CleanupUnconfirmed": case "CleanupFailed": case "SessionReleaseFailed": case "CommandStillActive": case "CleanupReceiptMissing": case "ExecutionOutcomeUnknown": case "UnexpectedWorkerFailure": return T("執行或清理結果尚未確認，請依 log 核對目標。", "Execution/cleanup unconfirmed; inspect target using log.");
                case "NativeFormat": return T("mySQLPunk 匯出標記或 LOB job／完成摘要不完整或不一致。", "Native export markers, LOB job or completion summary are missing/inconsistent.");
                case "UnclosedContext": return T("檔案結尾有未閉合的字串、識別字或註解。", "Unclosed string, identifier or comment at EOF.");
                case "PreflightFailed": return T("無法完成預檢，請確認檔案可讀、連線設定與 log 目錄可寫。", "Preflight failed; check readable file, connection and writable log directory.");
                case "ImportFailed": return T("匯入因檔案、連線或執行異常停止，請核對 log 與目標。", "Import stopped on file/connection/execution error; inspect log and target.");
                case "InputReleaseFailed": return T("來源檔案 handle 釋放未確認。", "Source handle release unconfirmed.");
                default: return T("檢查或執行未完成，診斷代碼：", "Check/execution incomplete; diagnostic code: ") + code;
            }
        }
        private bool HasLog() => !string.IsNullOrWhiteSpace(logPath.Text) && File.Exists(logPath.Text);
        private void OpenLogPath(bool folder)
        {
            if (!HasLog()) return; string path = folder ? Path.GetDirectoryName(logPath.Text) : logPath.Text;
            try { if (PathOpener != null) PathOpener(path); else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception) { status.Text = T("無法開啟路徑，請使用上方完整路徑。", "Cannot open path; use full path above."); }
        }
        protected override void Dispose(bool disposingManaged)
        {
            if (disposingManaged && !disposing)
            {
                lock (progressGate) { disposing = true; acceptingProgress = false; generation++; latest = null; activeJob = Guid.Empty; }
                SignalCancellation();
                DiscardPrepared(); timer.Dispose();
            }
            base.Dispose(disposingManaged);
        }
    }
}
