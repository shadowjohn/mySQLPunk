using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using mySQLPunk.lib;
using mySQLPunk.lib.OracleImport;
using OracleImportResult = mySQLPunk.lib.OracleImport.OracleImportResult;

namespace mySQLPunk
{
    internal sealed class OracleImportWizardForm : Form
    {
        private const int MaxLogLines = 2000;
        private readonly IDatabase db;
        private readonly string databaseName; // Owner / Schema
        private readonly Panel[] pages = new Panel[4];
        private readonly TextBox filePathBox = new TextBox();
        private readonly Button browseFileButton = new Button();
        private readonly Label preflightSummary = new Label(), preflightDetails = new Label();
        private readonly ListBox issuesList = new ListBox();
        private readonly RadioButton strategyKeep = new RadioButton(), strategyReplace = new RadioButton();
        private readonly RadioButton strategyDrop = new RadioButton(), strategyTruncate = new RadioButton();
        private readonly CheckBox autoRegisterSpatial = new CheckBox(), stopOnError = new CheckBox(), useTransaction = new CheckBox();
        private readonly Label stepsLabel = new Label(), statusLabel = new Label();
        private readonly Label currentStatementLabel = new Label(), coverageLabel = new Label(), speedLabel = new Label();
        private readonly Label elapsedLabel = new Label(), etaLabel = new Label(), resultText = new Label(), mascotHint = new Label();
        private readonly Button nextButton = new Button(), backButton = new Button(), cancelButton = new Button(), soundButton = new Button();
        private readonly Button openLogButton = new Button(), openFolderButton = new Button(), copyPathButton = new Button(), clearLogButton = new Button();
        private readonly CheckBox autoScrollCheck = new CheckBox();
        private readonly ProgressBar execBar = new ProgressBar();
        private readonly PunkyReportLoadingControl punky = new PunkyReportLoadingControl();
        private readonly ListBox logList = new ListBox();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 250 };
        private readonly Stopwatch clock = new Stopwatch();

        private OracleImportPreflight preflight;
        private CancellationTokenSource cancellation;
        private bool executing, closing;
        private int step;
        private long lastBytes;

        internal OracleImportResult Result { get; private set; }

        private static string T(string zh, string en) => ReportText.Get(zh, en);

        internal OracleImportWizardForm(IDatabase db, string databaseName, string initialSourcePath = "")
        {
            this.db = db ?? throw new ArgumentNullException(nameof(db));
            this.databaseName = !string.IsNullOrWhiteSpace(databaseName) ? databaseName : "USER";

            Name = "oracleImportWizard";
            Text = T("匯入 SQL 檔案 · Oracle (支援 SDO_GEOMETRY 空間幾何還原)", "Import SQL file · Oracle (with SDO_GEOMETRY spatial support)");
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = UiKit.Body;
            ClientSize = new Size(1120, 750);
            MinimumSize = new Size(680, 500);
            StartPosition = FormStartPosition.CenterParent;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(24),
                ColumnCount = 1,
                RowCount = 4
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            var header = Stack();
            header.Controls.Add(new Label { AutoSize = true, Text = T("Oracle 原生 SQL 匯入精靈", "Oracle native SQL import wizard") + "   ·   " + databaseName, Font = UiKit.Title, Margin = new Padding(0, 4, 0, 8) });
            stepsLabel.AutoSize = true;
            stepsLabel.Margin = new Padding(0, 4, 0, 12);
            header.Controls.Add(stepsLabel);
            root.Controls.Add(header, 0, 0);

            var body = new Panel { Dock = DockStyle.Fill };
            root.Controls.Add(body, 0, 1);
            for (int i = 0; i < 4; i++)
            {
                pages[i] = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
                body.Controls.Add(pages[i]);
            }

            BuildPage0(initialSourcePath);
            BuildPage1();
            BuildPage2();
            BuildPage3();

            statusLabel.AutoSize = true;
            statusLabel.Margin = new Padding(0, 8, 0, 6);
            root.Controls.Add(statusLabel, 0, 2);

            var nav = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
            nextButton.AutoSize = true;
            nextButton.Click += async (s, e) => await OnNextAsync();
            backButton.AutoSize = true;
            backButton.Text = T("◀ 上一步", "◀ Back");
            backButton.Click += (s, e) => MoveStep(-1);
            cancelButton.AutoSize = true;
            cancelButton.Text = T("取消", "Cancel");
            cancelButton.Click += (s, e) => Close();
            nav.Controls.Add(nextButton);
            nav.Controls.Add(backButton);
            nav.Controls.Add(cancelButton);
            root.Controls.Add(nav, 0, 3);

            timer.Tick += (s, e) => UpdateLiveTicker();
            FormClosing += (s, e) =>
            {
                if (executing)
                {
                    e.Cancel = true;
                    if (closing) return;
                    if (MessageBox.Show(T("正在匯入 Oracle SQL 腳本中，確定要中途停止嗎？", "Import is currently executing. Cancel it?"), T("確認", "Confirm"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    {
                        e.Cancel = true;
                        return;
                    }
                    closing = true;
                    cancellation?.Cancel();
                    return; // Wait for worker reader/transaction cleanup.
                }
                timer.Stop();
                clock.Stop();
            };

            SetStep(0);
        }

        private static FlowLayoutPanel Stack() => new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        private static FlowLayoutPanel Row() => new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Margin = new Padding(0, 0, 0, 8) };

        private void BuildPage0(string initialPath)
        {
            var p = pages[0];
            var host = Stack();

            var fileRow = Row();
            filePathBox.Width = 560;
            filePathBox.Text = initialPath ?? string.Empty;
            browseFileButton.Text = T("瀏覽...", "Browse...");
            browseFileButton.AutoSize = true;
            browseFileButton.Click += (s, e) =>
            {
                using (var ofd = new OpenFileDialog())
                {
                    ofd.Filter = "SQL Files (*.sql)|*.sql|All Files (*.*)|*.*";
                    if (ofd.ShowDialog(this) == DialogResult.OK)
                    {
                        filePathBox.Text = ofd.FileName;
                    }
                }
            };

            fileRow.Controls.Add(new Label { Text = T("選取 SQL 腳本檔案：", "Select SQL script file:"), AutoSize = true, Margin = new Padding(0, 5, 4, 0) });
            fileRow.Controls.Add(filePathBox);
            fileRow.Controls.Add(browseFileButton);
            host.Controls.Add(fileRow);

            var infoBox = new GroupBox { Text = T("匯入環境說明", "Import target info"), AutoSize = true, Padding = new Padding(12), Margin = new Padding(0, 8, 0, 0) };
            var infoFlow = Stack();
            infoFlow.Controls.Add(new Label { Text = "• " + T("目標資料庫綱要 (Owner)：", "Target Schema/Owner: ") + databaseName, AutoSize = true, Margin = new Padding(0, 2, 0, 4) });
            infoFlow.Controls.Add(new Label { Text = "• " + T("執行核心：串流狀態機 Lexer (支援 PL/SQL 區塊斜線 / 分隔符與 32 MiB 上限保護)", "Engine: Streaming State Machine Lexer with PL/SQL block delimiter / & 32 MiB safety check"), AutoSize = true, Margin = new Padding(0, 2, 0, 4) });
            infoFlow.Controls.Add(new Label { Text = "• " + T("空間支援：MDSYS.SDO_GEOMETRY、WKB 二進位與 USER_SDO_GEOM_METADATA 註冊", "Spatial Support: MDSYS.SDO_GEOMETRY, WKB & USER_SDO_GEOM_METADATA"), AutoSize = true, Margin = new Padding(0, 2, 0, 4) });
            infoBox.Controls.Add(infoFlow);
            host.Controls.Add(infoBox);

            p.Controls.Add(host);
        }

        private void BuildPage1()
        {
            var p = pages[1];
            var host = Stack();

            host.Controls.Add(new Label { Text = T("預檢掃描結果 (Preflight Inspection)", "Preflight Inspection Result"), Font = UiKit.Subtitle, AutoSize = true, Margin = new Padding(0, 0, 0, 8) });
            preflightSummary.AutoSize = true;
            preflightSummary.Font = UiKit.Body;
            host.Controls.Add(preflightSummary);

            preflightDetails.AutoSize = true;
            preflightDetails.Font = new Font("Consolas", 9.5f);
            preflightDetails.Margin = new Padding(0, 8, 0, 8);
            host.Controls.Add(preflightDetails);

            host.Controls.Add(new Label { Text = T("注意事項與潛在風險：", "Issues and Notices:"), AutoSize = true, Margin = new Padding(0, 4, 0, 4) });
            issuesList.Height = 160;
            issuesList.Width = 820;
            issuesList.Font = new Font("Consolas", 9f);
            host.Controls.Add(issuesList);

            p.Controls.Add(host);
        }

        private void BuildPage2()
        {
            var p = pages[2];
            var host = Stack();

            var stratBox = new GroupBox { Text = T("既有物件衝突處理策略", "Object conflict resolution strategy"), AutoSize = true, Padding = new Padding(12), Margin = new Padding(0, 0, 0, 8) };
            var stratFlow = Stack();
            strategyKeep.Text = T("保留既有物件 (Keep Existing) - 若物件已存在則忽略錯誤繼續", "Keep Existing - Ignore ORA-00955 if table/object already exists");
            strategyKeep.Checked = true;
            strategyKeep.AutoSize = true;
            strategyReplace.Text = T("全部取代 (Replace All) - 自動執行 DROP TABLE CASCADE CONSTRAINTS 後重建", "Replace All - Automatically execute DROP TABLE CASCADE CONSTRAINTS");
            strategyReplace.AutoSize = true;
            strategyDrop.Text = T("先刪除後重建 (Drop and Recreate) - 在建表前刪除同名資料表", "Drop and Recreate - Drop table before creating");
            strategyDrop.AutoSize = true;
            strategyTruncate.Text = T("清空既有內容 (Truncate and Insert) - 清空資料表後再匯入新資料", "Truncate and Insert - Truncate table before inserting rows");
            strategyTruncate.AutoSize = true;

            stratFlow.Controls.Add(strategyKeep);
            stratFlow.Controls.Add(strategyReplace);
            stratFlow.Controls.Add(strategyDrop);
            stratFlow.Controls.Add(strategyTruncate);
            stratBox.Controls.Add(stratFlow);
            host.Controls.Add(stratBox);

            var optBox = new GroupBox { Text = T("執行保護與 Oracle Spatial 選項", "Execution protection & Spatial settings"), AutoSize = true, Padding = new Padding(12), Margin = new Padding(0, 0, 0, 8) };
            var optFlow = Stack();

            autoRegisterSpatial.Text = T("執行腳本內的 USER_SDO_GEOM_METADATA 註冊 (不推測缺少的 metadata)", "Execute script spatial metadata registration (never infer missing metadata)");
            autoRegisterSpatial.Checked = true;
            autoRegisterSpatial.AutoSize = true;

            useTransaction.Text = T("使用交易包覆 (COMMIT / ROLLBACK)", "Wrap execution in transaction (COMMIT / ROLLBACK)");
            useTransaction.Checked = true;
            useTransaction.AutoSize = true;

            stopOnError.Text = T("發生錯誤時立即停止並還原 (Stop on Error)", "Stop immediately and rollback on error");
            stopOnError.Checked = true;
            stopOnError.AutoSize = true;

            optFlow.Controls.Add(autoRegisterSpatial);
            optFlow.Controls.Add(useTransaction);
            optFlow.Controls.Add(stopOnError);
            optBox.Controls.Add(optFlow);
            host.Controls.Add(optBox);

            p.Controls.Add(host);
        }

        private void BuildPage3()
        {
            var p = pages[3];
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var mascotPane = Stack();
            punky.Size = new Size(240, 240);
            mascotPane.Controls.Add(punky);

            soundButton.Text = punky.Muted ? T("開啟聲音", "Unmute") : T("靜音", "Mute");
            soundButton.Enabled = punky.AudioAvailable;
            soundButton.AutoSize = true;
            soundButton.Margin = new Padding(12, 8, 0, 0);
            soundButton.Click += (s, e) =>
            {
                punky.Muted = !punky.Muted;
                soundButton.Text = punky.Muted ? T("開啟聲音", "Unmute") : T("靜音", "Mute");
            };
            mascotPane.Controls.Add(soundButton);

            mascotHint.AutoSize = true;
            mascotHint.Margin = new Padding(12, 8, 0, 0);
            mascotHint.Text = T("Punky 看板娘正在監視 Oracle 匯入狀態機與終端機日誌...", "Punky is streaming Oracle SQL into the database...");
            mascotPane.Controls.Add(mascotHint);
            root.Controls.Add(mascotPane, 0, 0);
            root.SetRowSpan(mascotPane, 3);

            var statStack = Stack();
            currentStatementLabel.AutoSize = true;
            currentStatementLabel.Font = UiKit.Subtitle;
            statStack.Controls.Add(currentStatementLabel);

            execBar.Height = 24;
            execBar.Dock = DockStyle.Top;
            execBar.Margin = new Padding(0, 8, 0, 8);
            statStack.Controls.Add(execBar);

            var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 4, Margin = new Padding(0, 4, 0, 8) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

            coverageLabel.AutoSize = true; grid.Controls.Add(coverageLabel, 0, 0);
            speedLabel.AutoSize = true; grid.Controls.Add(speedLabel, 1, 0);
            elapsedLabel.AutoSize = true; grid.Controls.Add(elapsedLabel, 2, 0);
            etaLabel.AutoSize = true; grid.Controls.Add(etaLabel, 3, 0);
            statStack.Controls.Add(grid);

            root.Controls.Add(statStack, 1, 0);

            var buttonRow = Row();
            openLogButton.Text = T("開啟詳細 Log", "Open Log");
            openLogButton.AutoSize = true;
            openLogButton.Visible = false;
            openLogButton.Click += (s, e) =>
            {
                if (Result != null && File.Exists(Result.LogFilePath)) Process.Start(new ProcessStartInfo(Result.LogFilePath) { UseShellExecute = true });
            };
            openFolderButton.Text = T("開啟 Log 目錄", "Open Log Folder");
            openFolderButton.AutoSize = true;
            openFolderButton.Visible = false;
            openFolderButton.Click += (s, e) =>
            {
                if (Result != null && !string.IsNullOrWhiteSpace(Result.LogFilePath))
                {
                    string dir = Path.GetDirectoryName(Result.LogFilePath);
                    if (Directory.Exists(dir)) Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
                }
            };
            copyPathButton.Text = T("複製 Log 路徑", "Copy Log Path");
            copyPathButton.AutoSize = true;
            copyPathButton.Visible = false;
            copyPathButton.Click += (s, e) =>
            {
                if (Result != null && !string.IsNullOrWhiteSpace(Result.LogFilePath))
                {
                    Clipboard.SetText(Result.LogFilePath);
                    MessageBox.Show(T("已複製 Log 完整路徑至剪貼簿！", "Log path copied!"), T("提示", "Notice"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            };
            clearLogButton.Text = T("清除終端畫面", "Clear View");
            clearLogButton.AutoSize = true;
            clearLogButton.Click += (s, e) => logList.Items.Clear();

            autoScrollCheck.Text = T("自動捲動 (最多維持 2,000 行)", "Auto-scroll (Max 2,000 lines)");
            autoScrollCheck.Checked = true;
            autoScrollCheck.AutoSize = true;

            resultText.AutoSize = true;
            resultText.Margin = new Padding(0, 4, 12, 0);

            buttonRow.Controls.Add(resultText);
            buttonRow.Controls.Add(openLogButton);
            buttonRow.Controls.Add(openFolderButton);
            buttonRow.Controls.Add(copyPathButton);
            buttonRow.Controls.Add(clearLogButton);
            buttonRow.Controls.Add(autoScrollCheck);
            root.Controls.Add(buttonRow, 1, 1);

            logList.Dock = DockStyle.Fill;
            logList.Font = new Font("Consolas", 9f);
            root.Controls.Add(logList, 1, 2);

            p.Controls.Add(root);
        }

        private void SetStep(int s)
        {
            step = s;
            for (int i = 0; i < 4; i++) pages[i].Visible = (i == step);

            backButton.Enabled = (step > 0 && step < 3);
            cancelButton.Enabled = (step < 3);

            if (step == 0)
            {
                stepsLabel.Text = T("步驟 1/4：選取要匯入的 Oracle SQL 腳本檔案", "Step 1/4: Select Oracle SQL script file");
                nextButton.Text = T("下一步 (預檢掃描) ▶", "Next (Preflight) ▶");
            }
            else if (step == 1)
            {
                stepsLabel.Text = T("步驟 2/4：檢視預檢分析與 Oracle Spatial 特性", "Step 2/4: Review preflight inspection and spatial features");
                nextButton.Text = T("下一步 (選擇策略) ▶", "Next (Strategy) ▶");
            }
            else if (step == 2)
            {
                stepsLabel.Text = T("步驟 3/4：設定物件衝突策略與交易防護", "Step 3/4: Configure conflict strategy & transaction protection");
                nextButton.Text = T("開始匯入 🚀", "Start Import 🚀");
            }
            else if (step == 3)
            {
                stepsLabel.Text = T("步驟 4/4：正在串流執行 Oracle SQL 腳本...", "Step 4/4: Executing Oracle SQL script streaming...");
                nextButton.Text = T("完成", "Done");
                nextButton.Enabled = false;
                backButton.Enabled = false;
            }
        }

        private void MoveStep(int delta)
        {
            int n = step + delta;
            if (n >= 0 && n < 4) SetStep(n);
        }

        private async Task OnNextAsync()
        {
            if (executing || closing) return;
            if (step == 0)
            {
                string path = filePathBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    MessageBox.Show(T("請指定存在的 SQL 腳本檔案！", "Please specify a valid SQL script file!"), T("提示", "Notice"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                await RunPreflightAsync(path);
                if (closing || IsDisposed) return;
                if (preflight != null && preflight.CanProceed)
                {
                    MoveStep(1);
                }
            }
            else if (step == 1)
            {
                MoveStep(1);
            }
            else if (step == 2)
            {
                MoveStep(1);
                await StartImportAsync();
            }
            else if (step == 3)
            {
                Close();
            }
        }

        private async Task RunPreflightAsync(string path)
        {
            executing = true;
            cancellation = new CancellationTokenSource();
            try
            {
            statusLabel.Text = T("正在對 SQL 檔案進行狀態機預檢分析...", "Scanning SQL file with preflight lexer...");
            var scanner = new OracleImportPreflightScanner();
            preflight = await Task.Run(() => scanner.Scan(path, cancellation.Token)).ConfigureAwait(true);

            if (closing || IsDisposed) return;
            if (!preflight.CanProceed)
            {
                MessageBox.Show(string.Join("\n", preflight.Issues.ToArray()), T("預檢未通過", "Preflight Failed"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                statusLabel.Text = T("預檢未通過，請排除錯誤後再試。", "Preflight failed.");
                return;
            }

            preflightSummary.Text = string.Format(
                T("✅ 預檢分析通過！檔案大小: {0:N0} 位元組，編碼: {1}，預估執行語句: {2:N0} 條。",
                  "✅ Preflight passed! Size: {0:N0} bytes, Encoding: {1}, Estimated statements: {2:N0}."),
                preflight.FileSizeBytes, preflight.DetectedEncoding.WebName, preflight.StatementCount);

            var sb = new StringBuilder();
            sb.AppendLine("• " + T("PL/SQL 區塊偵測: ", "PL/SQL Blocks: ") + (preflight.HasPlSqlBlocks ? string.Format("包含 {0} 個預存程序／觸發程序區塊 (以獨立行 / 結尾)", preflight.PlSqlBlocksCount) : "無"));
            sb.AppendLine("• " + T("Oracle Spatial 特徵: ", "Spatial Features: ") + (preflight.HasSpatialFeatures ? "偵測到 SDO_GEOMETRY 空間幾何 (" + string.Join(", ", preflight.DetectedSpatialKeywords.ToArray()) + ")" : "無空間欄位"));
            sb.AppendLine("• " + T("Native Manifest 清單: ", "Native Manifest: ") + (preflight.ContainsManifest ? string.Format("包含 (共宣告 {0} 張資料表，附帶 SHA-256 核對)", preflight.ManifestDeclaredTables.Count) : "未包含"));
            preflightDetails.Text = sb.ToString();

            issuesList.Items.Clear();
            if (preflight.Issues.Count > 0)
            {
                foreach (var issue in preflight.Issues) issuesList.Items.Add(issue);
            }
            else
            {
                issuesList.Items.Add(T("文字預檢通過；尚未驗證 Oracle 語法／權限。一般預存 PL/SQL 不支援；DDL 會隱含提交，rollback 僅保護尚未提交的 DML。", "Text preflight passed; Oracle syntax/permissions remain unverified. Stored PL/SQL is unsupported. DDL implicitly commits; rollback protects pending DML only."));
            }

            statusLabel.Text = T("預檢完成，請確認設定。", "Preflight complete.");
            }
            finally
            {
                executing = false;
                cancellation?.Dispose();
                cancellation = null;
                if (closing && !IsDisposed) BeginInvoke(new Action(Close));
            }
        }

        private async Task StartImportAsync()
        {
            executing = true;
            clock.Restart();
            timer.Start();
            punky.BeginExport();

            var opt = new OracleImportOptions
            {
                ConflictStrategy = strategyKeep.Checked ? OracleConflictStrategy.KeepExisting :
                                   (strategyReplace.Checked ? OracleConflictStrategy.ReplaceAll :
                                   (strategyDrop.Checked ? OracleConflictStrategy.DropAndRecreate : OracleConflictStrategy.TruncateAndInsert)),
                AutoRegisterSpatialMetadata = autoRegisterSpatial.Checked,
                UseTransaction = useTransaction.Checked,
                StopOnError = stopOnError.Checked
            };

            var req = new OracleImportRequest(filePathBox.Text.Trim(), databaseName, opt);
            cancellation = new CancellationTokenSource();

            Action<string> appendTerminal = null;
            appendTerminal = line =>
            {
                if (InvokeRequired)
                {
                    try { Invoke(new Action<string>(appendTerminal), line); } catch { }
                    return;
                }

                if (logList.Items.Count >= MaxLogLines)
                {
                    logList.Items.RemoveAt(0); // FIFO 滑動視窗
                }
                logList.Items.Add(line);
                if (autoScrollCheck.Checked)
                {
                    logList.TopIndex = logList.Items.Count - 1;
                }
            };

            var runner = new OracleImportRunner(db, req, appendTerminal);

            var prog = new Progress<OracleImportProgress>(p =>
            {
                execBar.Value = (int)p.PercentComplete;
                coverageLabel.Text = string.Format("{0:0.0} %", p.PercentComplete);
                currentStatementLabel.Text = p.Message;
                lastBytes = p.BytesRead;
            });

            try
            {
                Result = await Task.Run(() => runner.RunAsync(prog, cancellation.Token)).ConfigureAwait(true);
                timer.Stop();
                clock.Stop();
                punky.EndExport();

                resultText.Text = Result.Success
                    ? string.Format(T("✅ 匯入成功！耗時 {0:hh\\:mm\\:ss}，執行 {1:N0} 條語句", "✅ Import succeeded! Elapsed {0:hh\\:mm\\:ss}, Executed {1:N0} statements"), Result.Elapsed, Result.StatementsExecuted)
                    : string.Format(T("❌ 匯入中斷: {0}", "❌ Import failed: {0}"), Result.ErrorMessage);

                resultText.ForeColor = Result.Success ? Color.DarkGreen : Color.Red;
                openLogButton.Visible = true;
                openFolderButton.Visible = true;
                copyPathButton.Visible = true;
                nextButton.Enabled = true;
                cancelButton.Enabled = true;
                cancelButton.Text = T("關閉", "Close");
            }
            catch (Exception ex)
            {
                timer.Stop();
                clock.Stop();
                punky.EndExport();

                resultText.Text = T("❌ 執行失敗: ", "❌ Execution failed: ") + ex.Message;
                resultText.ForeColor = Color.Red;
                nextButton.Enabled = true;
                cancelButton.Enabled = true;
            }
            finally
            {
                executing = false;
                cancellation?.Dispose();
                cancellation = null;
                if (closing && !IsDisposed) BeginInvoke(new Action(Close));
            }
        }

        private void UpdateLiveTicker()
        {
            elapsedLabel.Text = string.Format(T("已耗時: {0:hh\\:mm\\:ss}", "Elapsed: {0:hh\\:mm\\:ss}"), clock.Elapsed);
            if (clock.Elapsed.TotalSeconds > 0 && lastBytes > 0)
            {
                double kbSec = (lastBytes / 1024.0) / clock.Elapsed.TotalSeconds;
                speedLabel.Text = string.Format("{0:N0} KB/s", kbSec);
            }
        }
    }
}
