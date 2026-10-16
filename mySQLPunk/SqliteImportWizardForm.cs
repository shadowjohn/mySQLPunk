using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using mySQLPunk.lib;
using mySQLPunk.lib.SqliteImport;
using SqliteImportResult = mySQLPunk.lib.SqliteImport.SqliteImportResult;

namespace mySQLPunk
{
    internal sealed class SqliteImportWizardForm : Form
    {
        private readonly IDatabase db;
        private readonly string databaseName;
        private readonly SqliteImportRunner runner;
        private readonly Panel[] pages = new Panel[4];
        private readonly TextBox sourcePathBox = new TextBox(), targetDbBox = new TextBox(), logPathBox = new TextBox();
        private readonly ComboBox strategyCombo = new ComboBox();
        private readonly CheckBox continueOnError = new CheckBox(), wrapInTransaction = new CheckBox(), autoLoadSpatiaLite = new CheckBox(), confirmCheck = new CheckBox(), autoScrollCheck = new CheckBox();
        private readonly Label stepsLabel = new Label(), statusLabel = new Label(), fileFactsLabel = new Label();
        private readonly Label checkSummary = new Label(), confirmSummary = new Label(), logTitleLabel = new Label(), logCountLabel = new Label();
        private readonly Label currentStatementLabel = new Label(), coverageLabel = new Label(), speedLabel = new Label();
        private readonly Label elapsedLabel = new Label(), etaLabel = new Label(), errorCountLabel = new Label(), resultText = new Label(), mascotHint = new Label();
        private readonly Button nextButton = new Button(), backButton = new Button(), cancelButton = new Button(), browseButton = new Button(), soundButton = new Button(), clearLogButton = new Button();
        private readonly Button recheckButton = new Button(), openLogButton = new Button(), openFolderButton = new Button(), copyPathButton = new Button();
        private readonly ProgressBar execBar = new ProgressBar(), scanBar = new ProgressBar();
        private readonly PunkyReportLoadingControl punky = new PunkyReportLoadingControl();
        private readonly ListBox errorList = new ListBox();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 250 };
        private readonly Stopwatch clock = new Stopwatch();
        private const int MaxLogLines = 2000;

        private SqliteImportPreflight preflight;
        private SqliteImportRequest request;
        private CancellationTokenSource cancellation;
        private bool busy, executing, closing;
        private int step;

        internal SqliteImportResult Result { get; private set; }

        private static string T(string zh, string en) => ReportText.Get(zh, en);

        internal SqliteImportWizardForm(IDatabase db, string databaseName, string initialSourcePath = "")
        {
            this.db = db ?? throw new ArgumentNullException(nameof(db));
            this.databaseName = !string.IsNullOrWhiteSpace(databaseName) ? databaseName : throw new ArgumentException("Database name is required", nameof(databaseName));
            this.runner = new SqliteImportRunner(db, databaseName);
            this.request = new SqliteImportRequest(Guid.NewGuid(), initialSourcePath, databaseName);

            Name = "sqliteImportWizard";
            Text = T("匯入 SQL 檔案 · SQLite (支援 SpatiaLite / GeoPackage GIS)", "Import SQL file · SQLite (with SpatiaLite / GeoPackage GIS)");
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = UiKit.Body;
            ClientSize = new Size(1100, 750);
            MinimumSize = new Size(640, 480);
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
            header.Controls.Add(new Label { AutoSize = true, Text = T("SQLite SQL 檔案匯入精靈", "SQLite SQL file import wizard"), Font = UiKit.Title, Margin = new Padding(0, 4, 0, 8) });
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

            BuildPage0();
            BuildPage1();
            BuildPage2();
            BuildPage3();

            statusLabel.AutoSize = true;
            statusLabel.Margin = new Padding(0, 8, 0, 6);
            root.Controls.Add(statusLabel, 0, 2);

            var footer = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                WrapContents = true,
                FlowDirection = FlowDirection.RightToLeft
            };
            ConfigureButton(cancelButton, "importCancel", T("取消", "Cancel"), (s, e) => { if (busy) RequestCancel(); else Close(); });
            ConfigureButton(nextButton, "importNext", T("下一步 →", "Next →"), async (s, e) => await OnNextAsync());
            ConfigureButton(backButton, "importBack", T("← 上一步", "← Back"), (s, e) => { if (!busy && step > 0 && step < 3) { step--; ShowPage(); } });
            ConfigureButton(recheckButton, "importRecheck", T("重新預檢", "Recheck"), async (s, e) => { if (!busy) await RunPreflightAsync(); });
            recheckButton.Visible = false;

            footer.Controls.AddRange(new Control[] { cancelButton, nextButton, backButton, recheckButton });
            root.Controls.Add(footer, 0, 3);

            ThemeManager.MarkAsPrimary(nextButton);
            AcceptButton = nextButton;
            CancelButton = cancelButton;
            cancelButton.DialogResult = DialogResult.None;

            timer.Tick += (s, e) => UpdateLiveTicker();

            Shown += (s, e) =>
            {
                UpdateFileFacts();
                ShowPage();
            };

            FormClosing += (s, e) =>
            {
                if (busy)
                {
                    e.Cancel = true;
                    if (!closing)
                    {
                        closing = true;
                        RequestCancel();
                    }
                }
            };
        }

        private static FlowLayoutPanel Stack() => new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true
        };

        private static Label HeaderLabel(string text) => new Label
        {
            Text = text,
            Font = UiKit.Subtitle,
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 8)
        };

        private static Label HintLabel(string text) => new Label
        {
            Text = text,
            Font = UiKit.Body,
            ForeColor = Color.Gray,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8)
        };

        private static void ConfigureButton(Button b, string name, string text, EventHandler onClick)
        {
            b.Name = name;
            b.Text = text;
            b.Font = UiKit.Body;
            b.AutoSize = true;
            b.MinimumSize = new Size(110, 36);
            b.Margin = new Padding(8, 0, 0, 0);
            b.Click += onClick;
        }

        private void BuildPage0()
        {
            var content = Stack();
            pages[0].Controls.Add(content);

            content.Controls.Add(HeaderLabel(T("步驟 1：選取來源 SQL 檔案與目標 SQLite 資料庫", "Step 1: Select source SQL file and target SQLite database")));
            content.Controls.Add(HintLabel(T("請選擇欲執行的本機 SQL 傾印備份檔，並確認還原之 SQLite 目標資料庫。", "Choose a local SQL dump file to restore and verify the target SQLite database.")));

            var fileGroup = new GroupBox
            {
                Text = T("來源 SQL 檔案", "Source SQL file"),
                Font = UiKit.Body,
                Width = 1040,
                Height = 100,
                Margin = new Padding(0, 8, 0, 16)
            };
            sourcePathBox.Location = new Point(16, 26);
            sourcePathBox.Size = new Size(900, 28);
            sourcePathBox.Text = request.SourcePath;
            sourcePathBox.TextChanged += (s, e) =>
            {
                request = new SqliteImportRequest(request.JobId, sourcePathBox.Text.Trim(), databaseName);
                UpdateFileFacts();
            };

            ConfigureButton(browseButton, "importBrowse", T("瀏覽...", "Browse..."), (s, e) =>
            {
                using (var ofd = new OpenFileDialog())
                {
                    ofd.Title = T("選擇 SQL 檔案", "Select SQL file");
                    ofd.Filter = "SQL Files (*.sql)|*.sql|All Files (*.*)|*.*";
                    if (ofd.ShowDialog(this) == DialogResult.OK)
                    {
                        sourcePathBox.Text = ofd.FileName;
                    }
                }
            });
            browseButton.Location = new Point(925, 23);
            browseButton.Size = new Size(100, 32);

            fileFactsLabel.Location = new Point(16, 62);
            fileFactsLabel.AutoSize = true;
            fileFactsLabel.ForeColor = Color.Gray;

            fileGroup.Controls.AddRange(new Control[] { sourcePathBox, browseButton, fileFactsLabel });
            content.Controls.Add(fileGroup);

            var dbGroup = new GroupBox
            {
                Text = T("目標 SQLite 資料庫", "Target SQLite database"),
                Font = UiKit.Body,
                Width = 1040,
                Height = 80,
                Margin = new Padding(0, 0, 0, 16)
            };
            targetDbBox.Location = new Point(16, 28);
            targetDbBox.Size = new Size(1000, 28);
            targetDbBox.Text = databaseName;
            targetDbBox.ReadOnly = true;
            dbGroup.Controls.Add(targetDbBox);
            content.Controls.Add(dbGroup);
        }

        private void BuildPage1()
        {
            var content = Stack();
            pages[1].Controls.Add(content);

            content.Controls.Add(HeaderLabel(T("步驟 2：串流預檢 (Preflight Scan)", "Step 2: Preflight Scan")));
            content.Controls.Add(HintLabel(T("正在對 SQL 檔案進行非侵入性串流掃描，檢查編碼、NUL 字元、SpatiaLite 空間特徵與 Manifest 完整性...",
                                            "Scanning SQL file stream for encoding, NUL bytes, SpatiaLite GIS features, and Native Manifest...")));

            scanBar.Width = 1040;
            scanBar.Height = 16;
            scanBar.Style = ProgressBarStyle.Marquee;
            scanBar.Margin = new Padding(0, 8, 0, 16);
            content.Controls.Add(scanBar);

            checkSummary.Font = UiKit.Body;
            checkSummary.AutoSize = true;
            checkSummary.Margin = new Padding(0, 8, 0, 16);
            content.Controls.Add(checkSummary);
        }

        private void BuildPage2()
        {
            var content = Stack();
            pages[2].Controls.Add(content);

            content.Controls.Add(HeaderLabel(T("步驟 3：確認匯入與衝突策略", "Step 3: Confirm import & conflict policy")));
            content.Controls.Add(HintLabel(T("請選擇當目標資料庫已存在同名資料表時的處理策略，並確認開始執行。", "Select policy for existing objects and confirm execution.")));

            var optGroup = new GroupBox
            {
                Text = T("既有物件衝突策略與安全選項", "Conflict policy and safety options"),
                Font = UiKit.Body,
                Width = 1040,
                Height = 220,
                Margin = new Padding(0, 8, 0, 16)
            };

            var stratLabel = new Label { Text = T("衝突處理策略：", "Conflict Strategy:"), Location = new Point(16, 30), AutoSize = true };
            strategyCombo.Location = new Point(160, 26);
            strategyCombo.Width = 480;
            strategyCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            strategyCombo.Items.AddRange(new object[] {
                T("略過既有物件 (SkipExisting，保留現有結構與資料)", "SkipExisting (Skip existing objects)"),
                T("刪除並重建既有物件 (DropAndRecreate，DROP TABLE IF EXISTS)", "DropAndRecreate (DROP and recreate)"),
                T("保留結構，寫入或覆蓋資料 (OverwriteKeepExisting)", "OverwriteKeepExisting (Keep schema, insert data)"),
                T("若已存在則中斷匯入 (AbortIfExists，安全防護)", "AbortIfExists (Halt on existing object)")
            });
            strategyCombo.SelectedIndex = 0;

            continueOnError.Text = T("發生錯誤時略過並繼續執行 (預設關閉，遇錯立即停止以保全資料)", "Continue on error (Skip failed statement and proceed)");
            continueOnError.Location = new Point(16, 70);
            continueOnError.AutoSize = true;

            wrapInTransaction.Text = T("使用交易包覆 (提升寫入效能 10~50 倍，遇到嚴重錯誤時自動回滾)", "Wrap in transaction (boosts writes 10-50x, auto rollback on error)");
            wrapInTransaction.Location = new Point(16, 100);
            wrapInTransaction.Checked = true;
            wrapInTransaction.AutoSize = true;

            autoLoadSpatiaLite.Text = T("自動動態加載 SpatiaLite 空間擴充模組 (mod_spatialite.dll)", "Auto dynamic load SpatiaLite extension (mod_spatialite.dll)");
            autoLoadSpatiaLite.Location = new Point(16, 130);
            autoLoadSpatiaLite.Checked = true;
            autoLoadSpatiaLite.AutoSize = true;

            confirmCheck.Text = T("我已確認目標 SQLite 資料庫正確，並同意執行匯入", "I have verified the target SQLite database and confirm execution");
            confirmCheck.Location = new Point(16, 170);
            confirmCheck.AutoSize = true;
            confirmCheck.Font = new Font(UiKit.Body.FontFamily, UiKit.Body.Size, FontStyle.Bold);
            confirmCheck.CheckedChanged += (s, e) => ShowPage();

            optGroup.Controls.AddRange(new Control[] { stratLabel, strategyCombo, continueOnError, wrapInTransaction, autoLoadSpatiaLite, confirmCheck });
            content.Controls.Add(optGroup);

            confirmSummary.Font = UiKit.Body;
            confirmSummary.AutoSize = true;
            confirmSummary.Margin = new Padding(0, 0, 0, 16);
            content.Controls.Add(confirmSummary);
        }

        private void BuildPage3()
        {
            var content = new Panel { Dock = DockStyle.Fill };
            pages[3].Controls.Add(content);

            var topPanel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 1 };
            topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
            topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));

            var statsFlow = Stack();
            currentStatementLabel.Font = UiKit.Subtitle;
            currentStatementLabel.AutoSize = true;
            statsFlow.Controls.Add(currentStatementLabel);

            execBar.Width = 520;
            execBar.Height = 20;
            execBar.Margin = new Padding(0, 8, 0, 6);
            statsFlow.Controls.Add(execBar);

            var numRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            coverageLabel.AutoSize = true;
            coverageLabel.Margin = new Padding(0, 0, 20, 0);
            speedLabel.AutoSize = true;
            speedLabel.Margin = new Padding(0, 0, 20, 0);
            errorCountLabel.AutoSize = true;
            numRow.Controls.AddRange(new Control[] { coverageLabel, speedLabel, errorCountLabel });
            statsFlow.Controls.Add(numRow);

            var timeRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 4, 0, 6) };
            elapsedLabel.AutoSize = true;
            elapsedLabel.Margin = new Padding(0, 0, 20, 0);
            etaLabel.AutoSize = true;
            timeRow.Controls.AddRange(new Control[] { elapsedLabel, etaLabel });
            statsFlow.Controls.Add(timeRow);

            resultText.AutoSize = true;
            resultText.Margin = new Padding(0, 6, 0, 8);
            statsFlow.Controls.Add(resultText);

            var actRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            ConfigureButton(openLogButton, "importOpenLog", T("開啟 Log 檔案", "Open Log File"), (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(Result?.LogPath) && File.Exists(Result.LogPath))
                    Process.Start(new ProcessStartInfo("notepad.exe", Result.LogPath) { UseShellExecute = true });
            });
            ConfigureButton(openFolderButton, "importOpenFolder", T("開啟 Log 資料夾", "Open Log Folder"), (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(Result?.LogPath) && File.Exists(Result.LogPath))
                    Process.Start("explorer.exe", $"/select,\"{Result.LogPath}\"");
            });
            ConfigureButton(copyPathButton, "importCopyPath", T("複製 Log 路徑", "Copy Log Path"), (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(Result?.LogPath))
                {
                    Clipboard.SetText(Result.LogPath);
                    MessageBox.Show(T("Log 檔案路徑已複製至剪貼簿！", "Log path copied to clipboard!"), T("已複製", "Copied"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            });

            openLogButton.Visible = false;
            openFolderButton.Visible = false;
            copyPathButton.Visible = false;
            actRow.Controls.AddRange(new Control[] { openLogButton, openFolderButton, copyPathButton });
            statsFlow.Controls.Add(actRow);

            topPanel.Controls.Add(statsFlow, 0, 0);

            // 右側放置 Punky 看板娘動畫與音效控制
            var mascotPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(12, 0, 0, 0)
            };
            punky.Name = "sqliteImportPunky";
            punky.Size = new Size(200, 200);
            punky.Margin = new Padding(0, 0, 0, 4);
            mascotPanel.Controls.Add(punky);

            mascotHint.Text = T("崩琦正在為你匯入 SQLite GIS 資料！", "Punky is importing SQLite GIS data for you!");
            mascotHint.Font = UiKit.Body;
            mascotHint.AutoSize = true;
            mascotHint.Margin = new Padding(0, 0, 0, 6);
            mascotPanel.Controls.Add(mascotHint);

            ConfigureButton(soundButton, "sqliteToggleSound", punky.Muted ? T("開啟聲音", "Unmute") : T("靜音", "Mute"), (s, e) =>
            {
                punky.Muted = !punky.Muted;
                soundButton.Text = punky.Muted ? T("開啟聲音", "Unmute") : T("靜音", "Mute");
            });
            soundButton.Enabled = punky.AudioAvailable;
            soundButton.AutoSize = true;
            mascotPanel.Controls.Add(soundButton);

            topPanel.Controls.Add(mascotPanel, 1, 0);

            content.Controls.Add(topPanel);

            // 下方放置 2,000 行滑動視窗終端機風格日誌
            var logContainer = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 10, 0, 0) };

            var logHeader = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 32,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };

            logTitleLabel.Text = T("即時執行日誌", "Live Execution Log");
            logTitleLabel.Font = UiKit.Subtitle;
            logTitleLabel.AutoSize = true;
            logTitleLabel.Margin = new Padding(0, 4, 12, 0);

            logCountLabel.Text = string.Format(T("（保留最新 {0:N0} 行）", "(Rolling latest {0:N0} lines)"), MaxLogLines);
            logCountLabel.Font = UiKit.Body;
            logCountLabel.ForeColor = Color.Gray;
            logCountLabel.AutoSize = true;
            logCountLabel.Margin = new Padding(0, 6, 20, 0);

            autoScrollCheck.Text = T("自動滾動", "Auto-scroll");
            autoScrollCheck.Checked = true;
            autoScrollCheck.AutoSize = true;
            autoScrollCheck.Margin = new Padding(0, 5, 12, 0);

            ConfigureButton(clearLogButton, "importClearLog", T("清空日誌", "Clear"), (s, e) =>
            {
                errorList.Items.Clear();
            });
            clearLogButton.MinimumSize = new Size(70, 26);
            clearLogButton.Height = 26;
            clearLogButton.Margin = new Padding(0, 2, 0, 0);

            logHeader.Controls.AddRange(new Control[] { logTitleLabel, logCountLabel, autoScrollCheck, clearLogButton });
            logContainer.Controls.Add(logHeader);

            errorList.Name = "importLogList";
            errorList.Dock = DockStyle.Fill;
            errorList.IntegralHeight = false;
            errorList.BorderStyle = BorderStyle.FixedSingle;
            errorList.Font = new Font("Consolas", 9f, FontStyle.Regular);
            logContainer.Controls.Add(errorList);
            errorList.BringToFront();

            content.Controls.Add(logContainer);
        }

        private void UpdateFileFacts()
        {
            string path = sourcePathBox.Text.Trim();
            if (File.Exists(path))
            {
                long bytes = new FileInfo(path).Length;
                fileFactsLabel.Text = string.Format(T("檔案大小：{0:N0} 位元組 ({1:N2} MB)", "File size: {0:N0} bytes ({1:N2} MB)"), bytes, bytes / (1024.0 * 1024.0));
                nextButton.Enabled = (step == 0);
            }
            else
            {
                fileFactsLabel.Text = T("未選擇檔案或檔案不存在。", "No file selected or file does not exist.");
                if (step == 0) nextButton.Enabled = false;
            }
        }

        private void ShowPage()
        {
            for (int i = 0; i < 4; i++) pages[i].Visible = (i == step);

            string[] stepTitles = {
                T("1. 檔案與目標", "1. File & Target"),
                T("2. 串流預檢", "2. Preflight Scan"),
                T("3. 確認與策略", "3. Confirm & Policy"),
                T("4. 執行匯入", "4. Execute Import")
            };
            stepsLabel.Text = string.Join("   →   ", stepTitles.Select((t, i) => i == step ? $"【{t}】" : t));

            backButton.Enabled = (step > 0 && step < 3 && !busy);
            recheckButton.Visible = (step == 1);

            if (step == 0)
            {
                nextButton.Text = T("下一步 →", "Next →");
                nextButton.Enabled = File.Exists(sourcePathBox.Text.Trim());
            }
            else if (step == 1)
            {
                nextButton.Text = T("下一步 →", "Next →");
                nextButton.Enabled = preflight != null && preflight.Passed;
            }
            else if (step == 2)
            {
                nextButton.Text = T("🚀 開始匯入", "🚀 Start Import");
                nextButton.Enabled = confirmCheck.Checked;
                UpdateConfirmSummary();
            }
            else if (step == 3)
            {
                nextButton.Text = T("完成", "Finished");
                nextButton.Enabled = !busy;
            }
        }

        private async Task RunPreflightAsync()
        {
            busy = true;
            recheckButton.Enabled = false;
            nextButton.Enabled = false;
            backButton.Enabled = false;
            scanBar.Visible = true;
            checkSummary.Text = T("正在非侵入性串流掃描 SQL 檔案架構與空間中繼資料...", "Streaming scan of SQL file architecture & spatial metadata...");

            cancellation = new CancellationTokenSource();
            try
            {
                preflight = await Task.Run(() => SqliteImportPreflightScanner.Scan(request.SourcePath, cancellation.Token));

                if (preflight.Passed)
                {
                    string spatialNote = preflight.DetectedSpatiaLite
                        ? T("✅ 偵測到 SpatiaLite / GeoPackage 空間幾何語法或中繼表，匯入時將自動配置空間擴充模組！\n",
                            "✅ SpatiaLite / GeoPackage spatial features detected; extension will be configured automatically!\n")
                        : "";

                    string manifestNote = preflight.HasManifest
                        ? string.Format(T("✅ 包含 Native Manifest 核對摘要：預計還原 {0} 張資料表，約 {1:N0} 列資料。\n",
                                          "✅ Native Manifest detected: Expected {0} tables, approx. {1:N0} rows.\n"),
                                        preflight.ManifestTables, preflight.ManifestRows)
                        : T("ℹ️ 無 Native Manifest 摘要 (非本軟體匯出或傳統傾印檔，不影響匯入)。\n", "ℹ️ No Native Manifest found.\n");

                    checkSummary.Text =
                        T("🎉 預檢掃描通過！\n\n", "🎉 Preflight scan passed!\n\n") +
                        spatialNote +
                        manifestNote +
                        string.Format(T("● 檔案大小：{0:N0} 位元組 ({1:N2} MB)\n", "● File size: {0:N0} bytes ({1:N2} MB)\n"), preflight.FileBytes, preflight.FileBytes / (1024.0 * 1024.0)) +
                        string.Format(T("● 預估總 SQL 語句數：約 {0:N0} 句\n", "● Estimated SQL statements: approx. {0:N0}\n"), preflight.StatementCount) +
                        T("● 編碼與結構檢查：無 Embedded NUL，單批次大小合規", "● Encoding & structure check: No Embedded NUL, batch size valid");
                }
                else
                {
                    checkSummary.Text =
                        T("❌ 預檢掃描發現問題，無法直接執行：\n\n", "❌ Preflight scan detected issues:\n\n") +
                        string.Join("\n", preflight.Issues);
                }
            }
            catch (Exception ex)
            {
                checkSummary.Text = T("❌ 預檢掃描發生例外：", "❌ Preflight exception: ") + ex.Message;
            }
            finally
            {
                scanBar.Visible = false;
                busy = false;
                recheckButton.Enabled = true;
                backButton.Enabled = true;
                nextButton.Enabled = (preflight != null && preflight.Passed);
                if (closing) Close();
            }
        }

        private void UpdateConfirmSummary()
        {
            var strat = SqliteExistingObjectSelection.FromIndex(strategyCombo.SelectedIndex);
            confirmSummary.Text =
                $"● {T("目標 SQLite 資料庫：", "Target Database: ")} {databaseName}\n" +
                $"● {T("來源 SQL 檔案：", "Source SQL: ")} {request.SourcePath} ({new FileInfo(request.SourcePath).Length / 1024.0 / 1024.0:N2} MB)\n" +
                $"● {T("衝突處理策略：", "Conflict Strategy: ")} {strat}\n" +
                $"● {T("交易保護加速：", "Transaction Wrapping: ")} {(wrapInTransaction.Checked ? T("啟用 (寫入速度提升數十倍，失敗回滾)", "Enabled") : T("停用", "Disabled"))}\n" +
                $"● {T("SpatiaLite 空間擴充：", "SpatiaLite Extension: ")} {(autoLoadSpatiaLite.Checked ? T("自動動態載入", "Auto-loaded") : T("關閉", "Disabled"))}\n" +
                $"● {T("遇錯處理：", "On Error: ")} {(continueOnError.Checked ? T("略過錯誤並繼續", "Continue on error") : T("立即中止並保全現狀", "Halt on error"))}\n\n" +
                $"{T("提示：匯入過程為全串流逐句執行，日誌將維持 2,000 行滑動視窗，並於本機儲存完整日誌檔案。", "Notice: Streaming execution. Live log retains rolling 2,000 lines, full log saved locally.")}";
        }

        private async Task OnNextAsync()
        {
            if (busy) return;

            if (step == 0)
            {
                step = 1;
                ShowPage();
                await RunPreflightAsync();
            }
            else if (step == 1)
            {
                step = 2;
                ShowPage();
            }
            else if (step == 2)
            {
                if (!confirmCheck.Checked)
                {
                    MessageBox.Show(T("請先勾選確認方塊以同意執行匯入作業。", "Please check the confirmation box to proceed."), T("尚未確認", "Unconfirmed"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                step = 3;
                ShowPage();
                await StartImportAsync();
            }
            else if (step == 3)
            {
                Close();
            }
        }

        private async Task StartImportAsync()
        {
            busy = true;
            executing = true;
            cancellation = new CancellationTokenSource();
            nextButton.Enabled = false;
            backButton.Enabled = false;
            cancelButton.Text = T("停止匯入", "Stop Import");
            errorList.Items.Clear();

            var opt = new SqliteImportOptions
            {
                ContinueOnError = continueOnError.Checked,
                ExistingObjectStrategy = SqliteExistingObjectSelection.FromIndex(strategyCombo.SelectedIndex),
                WrapInTransaction = wrapInTransaction.Checked,
                AutoLoadSpatiaLite = autoLoadSpatiaLite.Checked
            };

            var progress = new Progress<SqliteImportProgress>(RenderProgress);

            punky.BeginExport();
            clock.Restart();
            timer.Start();

            try
            {
                Result = await runner.RunAsync(request, opt, progress, cancellation.Token);
                timer.Stop();
                clock.Stop();

                if (Result.Status == SqliteImportStatus.Completed)
                {
                    currentStatementLabel.Text = T("🎉 匯入作業已成功完成！", "🎉 Import completed successfully!");
                    string manifestPart = Result.ManifestVerified
                        ? T("✅ Native Manifest 核對完全相符！\n", "✅ Native Manifest verified exactly!\n")
                        : (!string.IsNullOrWhiteSpace(Result.ManifestReport) ? $"Manifest Report: {Result.ManifestReport}\n" : "");

                    resultText.Text = string.Format(
                        T("執行語句：{0:N0} 成功，{1} 失敗，{2} 略過。\n實際寫入/影響列數：{3:N0} 列。\n{4}日誌檔案已儲存至：{5}",
                          "Statements: {0:N0} succeeded, {1} failed, {2} skipped.\nTotal rows affected: {3:N0} rows.\n{4}Log file: {5}"),
                        Result.ExecutedStatements, Result.FailedStatements, Result.SkippedStatements, Result.TotalRowsAffected, manifestPart, Result.LogPath);
                }
                else if (Result.Status == SqliteImportStatus.Cancelled)
                {
                    currentStatementLabel.Text = T("⚠️ 匯入作業已由使用者取消。", "⚠️ Import cancelled by user.");
                    resultText.Text = T("匯入已取消；若有開啟交易則已自動回滾。", "Import was cancelled; transaction rolled back if active.");
                }
                else
                {
                    currentStatementLabel.Text = T("❌ 匯入未完成或發生錯誤。", "❌ Import failed or incomplete.");
                    resultText.Text = string.Format(
                        T("執行結束：{0} 失敗，{1} 略過。\n日誌檔案：{2}",
                          "Finished with {0} failed, {1} skipped.\nLog: {2}"),
                        Result.FailedStatements, Result.SkippedStatements, Result.LogPath);
                }

                openLogButton.Visible = true;
                openFolderButton.Visible = true;
                copyPathButton.Visible = true;
            }
            catch (Exception ex)
            {
                timer.Stop();
                clock.Stop();
                currentStatementLabel.Text = T("❌ 發生非預期例外：", "❌ Unexpected exception: ") + ex.Message;
                AppendLogLine("❌ " + ex.Message);
            }
            finally
            {
                punky.EndExport();
                busy = false;
                executing = false;
                cancelButton.Text = T("關閉", "Close");
                nextButton.Enabled = true;
                if (closing) Close();
            }
        }

        private void RenderProgress(SqliteImportProgress p)
        {
            if (p.TotalBytes > 0)
            {
                int val = Math.Min(100, Math.Max(0, (int)(p.ProcessedBytes * 100.0 / p.TotalBytes)));
                execBar.Value = val;
                coverageLabel.Text = $"{val}% ({p.ProcessedBytes / 1024:N0} KB / {p.TotalBytes / 1024:N0} KB)";
            }
            currentStatementLabel.Text = p.Message;
            speedLabel.Text = string.Format(T("速率：{0:N0} KB/秒", "Speed: {0:N0} KB/s"), p.BytesPerSecond / 1024.0);
            errorCountLabel.Text = string.Format(T("已寫入：{0:N0} 列 · 錯誤：{1} 筆", "Written: {0:N0} rows · Errors: {1}"), p.TotalRowsAffected, p.FailedStatements);
            elapsedLabel.Text = string.Format(T("耗時：{0:mm\\:ss}", "Elapsed: {0:mm\\:ss}"), p.Elapsed);
            etaLabel.Text = p.EstimatedRemaining.HasValue
                ? string.Format(T("預估剩餘：約 {0:mm\\:ss}", "ETA: approx. {0:mm\\:ss}"), p.EstimatedRemaining.Value)
                : "";

            if (!string.IsNullOrWhiteSpace(p.CurrentStatementPreview))
            {
                AppendLogLine(p.CurrentStatementPreview);
            }
        }

        private void AppendLogLine(string headline)
        {
            if (string.IsNullOrWhiteSpace(headline)) return;

            string entry = $"[{DateTime.Now:HH:mm:ss}] {headline}";

            if (errorList.Items.Count >= MaxLogLines)
            {
                errorList.BeginUpdate();
                try
                {
                    int excess = errorList.Items.Count - MaxLogLines + 1;
                    for (int i = 0; i < excess && errorList.Items.Count > 0; i++)
                    {
                        errorList.Items.RemoveAt(0);
                    }
                    errorList.Items.Add(entry);
                }
                finally
                {
                    errorList.EndUpdate();
                }
            }
            else
            {
                errorList.Items.Add(entry);
            }

            if (autoScrollCheck.Checked && errorList.Items.Count > 0)
            {
                errorList.TopIndex = errorList.Items.Count - 1;
            }
        }

        private void UpdateLiveTicker()
        {
            if (executing)
            {
                elapsedLabel.Text = string.Format(T("耗時：{0:mm\\:ss}", "Elapsed: {0:mm\\:ss}"), clock.Elapsed);
            }
        }

        private void RequestCancel()
        {
            if (cancellation != null && !cancellation.IsCancellationRequested)
            {
                cancelButton.Enabled = false;
                cancelButton.Text = T("正在取消...", "Cancelling...");
                cancellation.Cancel();
            }
        }
    }
}
