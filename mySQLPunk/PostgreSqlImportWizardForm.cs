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
using mySQLPunk.lib.PostgreSqlImport;
using PostgreSqlImportResult = mySQLPunk.lib.PostgreSqlImport.PostgreSqlImportResult;

namespace mySQLPunk
{
    internal sealed class PostgreSqlImportWizardForm : Form
    {
        private const int MaxLogLines = 2000;
        private readonly IDatabase db;
        private readonly string databaseName;
        private readonly Panel[] pages = new Panel[4];
        private readonly TextBox filePathBox = new TextBox();
        private readonly Button browseFileButton = new Button();
        private readonly Label preflightSummary = new Label(), preflightDetails = new Label();
        private readonly ListBox issuesList = new ListBox();
        private readonly RadioButton strategyKeep = new RadioButton(), strategyReplace = new RadioButton();
        private readonly RadioButton strategyDrop = new RadioButton(), strategyTruncate = new RadioButton();
        private readonly CheckBox autoInstallPostgis = new CheckBox(), stopOnError = new CheckBox(), useTransaction = new CheckBox();
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

        private PostgreSqlImportPreflightResult preflight;
        private CancellationTokenSource cancellation;
        private bool busy, executing, closing;
        private int step;
        private long lastBytes;

        internal PostgreSqlImportResult Result { get; private set; }

        private static string T(string zh, string en) => ReportText.Get(zh, en);

        internal PostgreSqlImportWizardForm(IDatabase db, string databaseName, string initialSourcePath = "")
        {
            this.db = db ?? throw new ArgumentNullException(nameof(db));
            this.databaseName = !string.IsNullOrWhiteSpace(databaseName) ? databaseName : throw new ArgumentException("Database name is required", nameof(databaseName));

            Name = "postgreSqlImportWizard";
            Text = T("匯入 SQL 檔案 · PostgreSQL (支援 PostGIS 空間幾何還原)", "Import SQL file · PostgreSQL (with PostGIS spatial GIS support)");
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
            header.Controls.Add(new Label { AutoSize = true, Text = T("PostgreSQL 原生 SQL 匯入精靈", "PostgreSQL native SQL import wizard") + "   ·   " + databaseName, Font = UiKit.Title, Margin = new Padding(0, 4, 0, 8) });
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

            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            nextButton.AutoSize = true;
            backButton.AutoSize = true;
            cancelButton.AutoSize = true;
            nextButton.Text = T("下一步 >", "Next >");
            backButton.Text = T("< 上一步", "< Back");
            cancelButton.Text = T("取消", "Cancel");
            nextButton.Click += (s, e) => Navigate(step + 1);
            backButton.Click += (s, e) => Navigate(step - 1);
            cancelButton.Click += (s, e) => HandleCancel();
            footer.Controls.Add(nextButton);
            footer.Controls.Add(backButton);
            footer.Controls.Add(cancelButton);
            root.Controls.Add(footer, 0, 3);

            timer.Tick += (s, e) => UpdateLiveTicker();

            Shown += (s, e) => UpdateStep();
            FormClosing += (s, e) =>
            {
                if (busy && !closing)
                {
                    closing = true;
                    cancellation?.Cancel();
                }
            };
        }

        private static FlowLayoutPanel Stack() => new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };

        private void BuildPage0(string initialPath)
        {
            var p = pages[0];
            p.Padding = new Padding(12);
            var layout = Stack();
            p.Controls.Add(layout);

            layout.Controls.Add(new Label { Text = T("選取欲匯入的 PostgreSQL SQL 檔案：", "Select PostgreSQL SQL file to import:"), Font = UiKit.Subtitle, AutoSize = true, Margin = new Padding(0, 4, 0, 6) });

            var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, 16) };
            filePathBox.Width = 620;
            filePathBox.Text = initialPath ?? string.Empty;
            browseFileButton.Text = T("瀏覽...", "Browse...");
            browseFileButton.AutoSize = true;
            browseFileButton.Click += (s, e) => ChooseSqlFile();
            row.Controls.Add(filePathBox);
            row.Controls.Add(browseFileButton);
            layout.Controls.Add(row);

            var info = new Label
            {
                Text = string.Format(T("目標資料庫：{0}\r\n💡 精靈將以狀態機串流掃描腳本編碼、語句數、Dollar-Quotes 區塊與 PostGIS 空間特徵。",
                                       "Target database: {0}\r\n💡 Wizard will stream-scan encoding, statement count, dollar quotes, and PostGIS spatial features."), databaseName),
                AutoSize = true,
                Font = UiKit.Body,
                ForeColor = Color.DarkSlateBlue
            };
            layout.Controls.Add(info);
        }

        private void BuildPage1()
        {
            var p = pages[1];
            p.Padding = new Padding(12);
            var layout = Stack();
            p.Controls.Add(layout);

            layout.Controls.Add(new Label { Text = T("SQL 腳本預檢報告 (Preflight Report)：", "SQL script preflight report:"), Font = UiKit.Title, AutoSize = true, Margin = new Padding(0, 4, 0, 8) });

            preflightSummary.Font = UiKit.Subtitle;
            preflightSummary.AutoSize = true;
            preflightSummary.Margin = new Padding(0, 0, 0, 8);
            layout.Controls.Add(preflightSummary);

            preflightDetails.Font = UiKit.Body;
            preflightDetails.AutoSize = true;
            preflightDetails.Margin = new Padding(0, 0, 0, 12);
            layout.Controls.Add(preflightDetails);

            layout.Controls.Add(new Label { Text = T("預檢警示或問題清單：", "Preflight warnings and issues:"), Font = UiKit.Subtitle, AutoSize = true, Margin = new Padding(0, 4, 0, 4) });
            issuesList.Height = 140;
            issuesList.Width = 800;
            layout.Controls.Add(issuesList);
        }

        private void BuildPage2()
        {
            var p = pages[2];
            p.Padding = new Padding(12);
            var layout = Stack();
            p.Controls.Add(layout);

            layout.Controls.Add(new Label { Text = T("既有物件衝突處理策略：", "Existing objects conflict strategy:"), Font = UiKit.Subtitle, AutoSize = true, Margin = new Padding(0, 4, 0, 6) });
            strategyKeep.Text = T("保留現有物件 (CREATE TABLE IF NOT EXISTS，遇衝突略過)", "Keep existing objects (CREATE TABLE IF NOT EXISTS, ignore collisions)");
            strategyKeep.Checked = true;
            strategyKeep.AutoSize = true;

            strategyReplace.Text = T("全部替換 (覆寫/替換既有物件)", "Replace all (overwrite existing objects)");
            strategyReplace.AutoSize = true;

            strategyDrop.Text = T("刪表重建 (DROP TABLE IF EXISTS ... CASCADE 後重新建表)", "Drop and recreate (DROP TABLE IF EXISTS ... CASCADE then recreate)");
            strategyDrop.AutoSize = true;

            strategyTruncate.Text = T("清空重匯 (TRUNCATE TABLE ... CASCADE 後寫入資料)", "Truncate and insert (TRUNCATE TABLE ... CASCADE then insert)");
            strategyTruncate.AutoSize = true;

            layout.Controls.Add(strategyKeep);
            layout.Controls.Add(strategyReplace);
            layout.Controls.Add(strategyDrop);
            layout.Controls.Add(strategyTruncate);

            layout.Controls.Add(new Label { Text = T("執行保護與 PostGIS 選項：", "Execution protection and PostGIS options:"), Font = UiKit.Subtitle, AutoSize = true, Margin = new Padding(0, 16, 0, 6) });

            autoInstallPostgis.Text = T("自動載入 PostGIS 擴充套件 (CREATE EXTENSION IF NOT EXISTS postgis;)", "Auto install PostGIS extension (CREATE EXTENSION IF NOT EXISTS postgis;)");
            autoInstallPostgis.Checked = true;
            autoInstallPostgis.AutoSize = true;

            stopOnError.Text = T("發生錯誤時立即停止 (Stop on error)", "Stop immediately on error");
            stopOnError.Checked = true;
            stopOnError.AutoSize = true;

            useTransaction.Text = T("以單一交易包覆全腳本 (BEGIN ... COMMIT，遇錯自動 ROLLBACK)", "Wrap entire execution in single transaction (Rollback on failure)");
            useTransaction.Checked = true;
            useTransaction.AutoSize = true;

            layout.Controls.Add(autoInstallPostgis);
            layout.Controls.Add(stopOnError);
            layout.Controls.Add(useTransaction);
        }

        private void BuildPage3()
        {
            var p = pages[3];
            p.Padding = new Padding(8);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            p.Controls.Add(layout);

            var leftTop = Stack();
            currentStatementLabel.Text = T("準備開始匯入...", "Preparing import...");
            currentStatementLabel.Font = UiKit.Subtitle;
            currentStatementLabel.AutoSize = true;
            leftTop.Controls.Add(currentStatementLabel);

            execBar.Dock = DockStyle.Top;
            execBar.Height = 24;
            leftTop.Controls.Add(execBar);

            var statRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 4, 0, 4) };
            coverageLabel.Text = "0%";
            elapsedLabel.Text = "00:00:00";
            speedLabel.Text = "0 KB/s";
            statRow.Controls.Add(coverageLabel);
            statRow.Controls.Add(new Label { Text = " · ", AutoSize = true });
            statRow.Controls.Add(elapsedLabel);
            statRow.Controls.Add(new Label { Text = " · ", AutoSize = true });
            statRow.Controls.Add(speedLabel);
            leftTop.Controls.Add(statRow);
            layout.Controls.Add(leftTop, 0, 0);

            var rightBox = Stack();
            punky.Size = new Size(160, 160);
            rightBox.Controls.Add(punky);

            mascotHint.Text = T("崩琦正在為你匯入 PostgreSQL GIS 資料！", "Punky is importing PostgreSQL GIS data for you!");
            mascotHint.AutoSize = true;
            mascotHint.Font = UiKit.Body;
            rightBox.Controls.Add(mascotHint);

            soundButton.Text = punky.Muted ? T("開啟聲音", "Unmute") : T("靜音", "Mute");
            soundButton.Enabled = punky.AudioAvailable;
            soundButton.AutoSize = true;
            soundButton.Click += (s, e) =>
            {
                punky.Muted = !punky.Muted;
                soundButton.Text = punky.Muted ? T("開啟聲音", "Unmute") : T("靜音", "Mute");
            };
            rightBox.Controls.Add(soundButton);
            layout.Controls.Add(rightBox, 1, 0);

            // 終端機風格 2,000 行滑動視窗即時日誌
            var logPanel = new Panel { Dock = DockStyle.Fill };
            var logHeader = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30, AutoSize = false };
            logHeader.Controls.Add(new Label { Text = T("即時執行日誌 (保留最新 2,000 行)：", "Live Execution Log (rolling 2,000 lines):"), Font = UiKit.Subtitle, AutoSize = true, Margin = new Padding(0, 4, 12, 0) });
            autoScrollCheck.Text = T("自動滾動", "Auto-scroll");
            autoScrollCheck.Checked = true;
            autoScrollCheck.AutoSize = true;
            logHeader.Controls.Add(autoScrollCheck);
            clearLogButton.Text = T("清空日誌", "Clear");
            clearLogButton.AutoSize = true;
            clearLogButton.Click += (s, e) => logList.Items.Clear();
            logHeader.Controls.Add(clearLogButton);
            logPanel.Controls.Add(logHeader);

            logList.Dock = DockStyle.Fill;
            logList.Font = new Font("Consolas", 9f);
            logList.BackColor = Color.FromArgb(20, 20, 20);
            logList.ForeColor = Color.FromArgb(200, 240, 200);
            logPanel.Controls.Add(logList);
            logList.BringToFront();

            layout.Controls.Add(logPanel, 0, 2);
            layout.SetColumnSpan(logPanel, 2);

            resultText.AutoSize = true;
            resultText.Font = UiKit.Subtitle;
            resultText.ForeColor = Color.DarkGreen;
            resultText.Margin = new Padding(0, 8, 0, 8);
            layout.Controls.Add(resultText, 0, 3);
            layout.SetColumnSpan(resultText, 2);

            var actRow = new FlowLayoutPanel { AutoSize = true };
            openLogButton.Text = T("開啟詳細 Log 檔案", "Open Log File");
            openLogButton.AutoSize = true;
            openLogButton.Visible = false;
            openLogButton.Click += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(Result?.LogFilePath) && File.Exists(Result.LogFilePath))
                {
                    Process.Start(new ProcessStartInfo("notepad.exe", Result.LogFilePath) { UseShellExecute = true });
                }
            };

            openFolderButton.Text = T("開啟 Log 資料夾", "Open Log Folder");
            openFolderButton.AutoSize = true;
            openFolderButton.Visible = false;
            openFolderButton.Click += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(Result?.LogFilePath) && File.Exists(Result.LogFilePath))
                {
                    Process.Start("explorer.exe", string.Format("/select,\"{0}\"", Result.LogFilePath));
                }
            };

            actRow.Controls.Add(openLogButton);
            actRow.Controls.Add(openFolderButton);
            layout.Controls.Add(actRow, 0, 4);
            layout.SetColumnSpan(actRow, 2);
        }

        private void ChooseSqlFile()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = T("SQL 腳本檔案 (*.sql)|*.sql|所有檔案 (*.*)|*.*", "SQL Script Files (*.sql)|*.sql|All Files (*.*)|*.*");
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    filePathBox.Text = dialog.FileName;
                }
            }
        }

        private async void Navigate(int nextStep)
        {
            if (nextStep < 0 || nextStep > 3) return;

            if (step == 0 && nextStep == 1)
            {
                string path = filePathBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    MessageBox.Show(this, T("請指定有效的 SQL 腳本檔案！", "Please specify a valid SQL script file!"), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                await RunPreflightAsync(path);
                if (preflight != null && !preflight.CanProceed)
                {
                    MessageBox.Show(this, T("SQL 腳本未通過安全預檢，無法繼續匯入！", "SQL script failed preflight checks!"), Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }
            else if (step == 2 && nextStep == 3)
            {
                StartImport();
            }

            step = nextStep;
            UpdateStep();
        }

        private async Task RunPreflightAsync(string path)
        {
            busy = true;
            statusLabel.Text = T("正在對 SQL 檔案執行狀態機串流預檢...", "Scanning SQL script with state-machine preflight...");
            try
            {
                var scanner = new PostgreSqlImportPreflightScanner();
                preflight = await scanner.ScanAsync(path).ConfigureAwait(true);

                preflightSummary.Text = preflight.CanProceed
                    ? T("✅ 預檢通過！腳本結構正常。", "✅ Preflight passed! Script syntax is normal.")
                    : T("❌ 預檢發現重大問題！", "❌ Preflight found critical issues!");
                preflightSummary.ForeColor = preflight.CanProceed ? Color.DarkGreen : Color.Crimson;

                var sb = new StringBuilder();
                sb.AppendLine(string.Format("• {0}: {1}", T("檔案編碼", "Encoding"), preflight.DetectedEncoding.WebName.ToUpperInvariant()));
                sb.AppendLine(string.Format("• {0}: {1:N0} KB", T("檔案體積", "File Size"), preflight.FileSize / 1024));
                sb.AppendLine(string.Format("• {0}: {1:N0} {2}", T("估計語句總數", "Estimated Statements"), preflight.StatementCount, T("條", "statements")));
                sb.AppendLine(string.Format("• {0}: {1}", T("PostGIS 空間特徵", "PostGIS Spatial"), preflight.HasPostgisFeatures ? "🌐 " + T("偵測到 PostGIS 空間特徵 (含 geometry / geography / raster 關鍵字)", "PostGIS spatial features detected") : T("無", "None")));
                sb.AppendLine(string.Format("• {0}: {1}", T("Dollar Quotes 區塊", "Dollar Quotes Blocks"), preflight.HasDollarQuotes ? string.Format(T("偵測到 {0} 個 $$...$$ 函數區塊 (將精確解析)", "{0} blocks detected"), preflight.DollarQuoteBlocksCount) : T("無", "None")));
                sb.AppendLine(string.Format("• {0}: {1}", T("Native Manifest 驗證標記", "Native Manifest Signature"), preflight.ContainsManifest ? T("已偵測到結尾清單與 SHA-256 核對碼", "Detected footer summary & SHA-256") : T("未附帶 Manifest 清單", "No Manifest signature")));

                preflightDetails.Text = sb.ToString();

                issuesList.Items.Clear();
                if (preflight.Issues.Count == 0)
                {
                    issuesList.Items.Add(T("（無任何警告或錯誤）", "(No warnings or errors)"));
                }
                else
                {
                    foreach (var issue in preflight.Issues) issuesList.Items.Add(issue);
                }

                statusLabel.Text = T("預檢完成。", "Preflight complete.");
            }
            catch (Exception ex)
            {
                preflightSummary.Text = T("❌ 預檢過程發生例外：", "❌ Preflight exception: ") + ex.Message;
                preflightSummary.ForeColor = Color.Crimson;
            }
            finally
            {
                busy = false;
            }
        }

        private void UpdateStep()
        {
            for (int i = 0; i < 4; i++) pages[i].Visible = (i == step);

            string[] stepTitles = new[]
            {
                T("步驟 1/4：選取欲匯入的 PostgreSQL SQL 檔案與目標庫", "Step 1/4: Select PostgreSQL SQL file and target database"),
                T("步驟 2/4：SQL 腳本安全預檢與 PostGIS 空間特徵報告", "Step 2/4: SQL script preflight and PostGIS spatial report"),
                T("步驟 3/4：物件衝突處理策略與交易保護設定", "Step 3/4: Conflict strategy and transaction settings"),
                T("步驟 4/4：執行串流匯入與即時終端機日誌", "Step 4/4: Streaming execution and live terminal log")
            };

            stepsLabel.Text = stepTitles[step];
            backButton.Enabled = (step > 0 && step < 3 && !busy);
            nextButton.Enabled = (!busy || step == 3);
            nextButton.Text = (step == 2) ? T("開始匯入 🚀", "Start Import 🚀") : (step == 3 ? T("完成", "Finished") : T("下一步 >", "Next >"));
            cancelButton.Enabled = !busy || executing;

            if (step == 3 && !executing)
            {
                nextButton.Enabled = true;
                cancelButton.Enabled = false;
            }
        }

        private async void StartImport()
        {
            executing = true;
            busy = true;
            backButton.Enabled = false;
            nextButton.Enabled = false;
            cancellation = new CancellationTokenSource();
            clock.Restart();
            timer.Start();

            punky.BeginExport();

            var strategy = strategyKeep.Checked ? PostgreSqlConflictStrategy.KeepExisting :
                           strategyReplace.Checked ? PostgreSqlConflictStrategy.ReplaceAll :
                           strategyDrop.Checked ? PostgreSqlConflictStrategy.DropAndRecreate :
                                                  PostgreSqlConflictStrategy.TruncateAndInsert;

            var opt = new PostgreSqlImportOptions
            {
                ConflictStrategy = strategy,
                AutoInstallPostgis = autoInstallPostgis.Checked,
                StopOnError = stopOnError.Checked,
                UseTransaction = useTransaction.Checked
            };

            var req = new PostgreSqlImportRequest(filePathBox.Text.Trim(), databaseName, opt);
            var runner = new PostgreSqlImportRunner(db, req);

            var progressReporter = new Progress<PostgreSqlImportProgress>(p =>
            {
                coverageLabel.Text = string.Format("{0:0.0}% ({1:N0} / {2:N0})", p.PercentComplete, p.CurrentStatement, p.TotalStatements);
                execBar.Value = Math.Min(100, (int)p.PercentComplete);
                currentStatementLabel.Text = p.Message;
                lastBytes = p.BytesRead;
            });

            Action<string> terminalSink = line =>
            {
                if (InvokeRequired)
                {
                    BeginInvoke(new Action(() => AppendTerminalLine(line)));
                }
                else
                {
                    AppendTerminalLine(line);
                }
            };

            try
            {
                Result = await runner.RunAsync(progressReporter, terminalSink, cancellation.Token).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Result = new PostgreSqlImportResult { Success = false, ErrorMessage = ex.Message };
            }
            finally
            {
                executing = false;
                busy = false;
                clock.Stop();
                timer.Stop();
                punky.EndExport();

                if (Result != null && Result.Success)
                {
                    resultText.Text = string.Format(T("✅ 匯入成功！共執行 {0:N0} 條語句，寫入/影響 {1:N0} 列資料 (耗時 {2:hh\\:mm\\:ss\\.fff})",
                                                      "✅ Import succeeded! Executed {0:N0} statements, affected {1:N0} rows (took {2:hh\\:mm\\:ss\\.fff})"),
                                                    Result.StatementsExecuted, Result.TotalRowsAffected, Result.Elapsed);
                    resultText.ForeColor = Color.DarkGreen;
                    openLogButton.Visible = true;
                    openFolderButton.Visible = true;
                }
                else
                {
                    resultText.Text = T("❌ 匯入未完成：", "❌ Import failed: ") + (Result?.ErrorMessage ?? "Unknown error");
                    resultText.ForeColor = Color.Crimson;
                }

                UpdateStep();
            }
        }

        private void AppendTerminalLine(string line)
        {
            if (logList.Items.Count >= MaxLogLines)
            {
                logList.BeginUpdate();
                try
                {
                    int excess = logList.Items.Count - MaxLogLines + 1;
                    for (int i = 0; i < excess && logList.Items.Count > 0; i++)
                    {
                        logList.Items.RemoveAt(0);
                    }
                    logList.Items.Add(line);
                }
                finally
                {
                    logList.EndUpdate();
                }
            }
            else
            {
                logList.Items.Add(line);
            }

            if (autoScrollCheck.Checked && logList.Items.Count > 0)
            {
                logList.TopIndex = logList.Items.Count - 1;
            }
        }

        private void UpdateLiveTicker()
        {
            if (!clock.IsRunning) return;
            var el = clock.Elapsed;
            elapsedLabel.Text = string.Format("{0:00}:{1:00}:{2:00}", (int)el.TotalHours, el.Minutes, el.Seconds);

            if (lastBytes > 0 && el.TotalSeconds > 0)
            {
                double bytesPerSec = lastBytes / el.TotalSeconds;
                speedLabel.Text = FormatBytes((long)bytesPerSec) + "/s";
            }
        }

        private void HandleCancel()
        {
            if (busy && executing)
            {
                if (MessageBox.Show(this, T("匯入正在執行中，確定要中止嗎？", "Import is running, are you sure to cancel?"), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    cancellation?.Cancel();
                }
            }
            else
            {
                Close();
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            if (bytes < 1024 * 1024 * 1024) return (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MB";
            return (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("0.0") + " GB";
        }
    }
}
