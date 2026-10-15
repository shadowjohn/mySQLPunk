using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using mySQLPunk.lib;
using mySQLPunk.lib.MySqlImport;
using MySqlImportResult = mySQLPunk.lib.MySqlImport.MySqlImportResult;

namespace mySQLPunk
{
    internal sealed class MySqlImportWizardForm : Form
    {
        private readonly IDatabase db;
        private readonly string databaseName;
        private readonly MySqlImportRunner runner;
        private readonly Panel[] pages = new Panel[4];
        private readonly TextBox sourcePathBox = new TextBox(), targetDbBox = new TextBox(), logPathBox = new TextBox();
        private readonly ComboBox strategyCombo = new ComboBox();
        private readonly CheckBox continueOnError = new CheckBox(), confirmCheck = new CheckBox();
        private readonly Label stepsLabel = new Label(), statusLabel = new Label(), fileFactsLabel = new Label();
        private readonly Label checkSummary = new Label(), confirmSummary = new Label();
        private readonly Label currentStatementLabel = new Label(), coverageLabel = new Label(), speedLabel = new Label();
        private readonly Label elapsedLabel = new Label(), etaLabel = new Label(), errorCountLabel = new Label(), resultText = new Label(), mascotHint = new Label();
        private readonly Button nextButton = new Button(), backButton = new Button(), cancelButton = new Button(), browseButton = new Button(), soundButton = new Button();
        private readonly Button recheckButton = new Button(), openLogButton = new Button(), openFolderButton = new Button(), copyPathButton = new Button();
        private readonly ProgressBar execBar = new ProgressBar(), scanBar = new ProgressBar();
        private readonly PunkyReportLoadingControl punky = new PunkyReportLoadingControl();
        private readonly ListBox errorList = new ListBox();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 250 };
        private readonly Stopwatch clock = new Stopwatch();

        private MySqlImportPreflight preflight;
        private MySqlImportRequest request;
        private CancellationTokenSource cancellation;
        private bool busy, executing, closing;
        private int step;

        internal MySqlImportResult Result { get; private set; }

        private static string T(string zh, string en) => ReportText.Get(zh, en);

        internal MySqlImportWizardForm(IDatabase db, string databaseName, string initialSourcePath = "")
        {
            this.db = db ?? throw new ArgumentNullException(nameof(db));
            this.databaseName = !string.IsNullOrWhiteSpace(databaseName) ? databaseName : throw new ArgumentException("Database name is required", nameof(databaseName));
            this.runner = new MySqlImportRunner(db, databaseName);
            this.request = new MySqlImportRequest(Guid.NewGuid(), initialSourcePath, databaseName);

            Name = "mySqlImportWizard";
            Text = T("匯入 SQL 檔案 · MySQL / MariaDB", "Import SQL file · MySQL / MariaDB");
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
            header.Controls.Add(new Label { AutoSize = true, Text = T("MySQL SQL 檔案匯入精靈", "MySQL SQL file import wizard"), Font = UiKit.Title, Margin = new Padding(0, 4, 0, 8) });
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

            timer.Tick += (s, e) =>
            {
                if (executing)
                    elapsedLabel.Text = string.Format(T("耗時：{0:mm\\:ss}", "Elapsed: {0:mm\\:ss}"), clock.Elapsed);
            };

            FormClosing += (s, e) =>
            {
                if (busy)
                {
                    e.Cancel = true;
                    closing = true;
                    RequestCancel();
                }
            };

            ThemeManager.ApplyTo(this);
            ShowPage();
        }

        private static FlowLayoutPanel Stack() => new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        private static Label HeaderLabel(string text) => new Label { AutoSize = true, Text = text, Font = UiKit.Subtitle, Margin = new Padding(0, 4, 0, 8) };
        private static Label HintLabel(string text) => new Label { AutoSize = true, Text = text, Font = UiKit.Body, Margin = new Padding(0, 2, 0, 6) };

        private static void ConfigureButton(Button b, string name, string text, EventHandler handler)
        {
            b.Name = name;
            b.Text = text;
            b.AutoSize = true;
            b.MinimumSize = new Size(95, 34);
            b.Margin = new Padding(4);
            b.Click += handler;
        }

        private void BuildPage0()
        {
            var content = Stack();
            pages[0].Controls.Add(content);
            content.Controls.Add(HeaderLabel(T("步驟 1：選擇檔案與目標資料庫", "Step 1: Choose SQL file and target database")));
            content.Controls.Add(HintLabel(T("先選取欲匯入的 SQL 檔案，系統將於下一步進行串流掃描檢查，預檢絕不執行檔案中的 SQL。", "Choose a SQL file to import. The next step will scan it safely without executing any SQL.")));

            content.Controls.Add(new Label { Text = T("SQL 檔案完整路徑：", "SQL file path:"), AutoSize = true, Margin = new Padding(0, 8, 0, 4) });

            var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 0, 0, 6) };
            sourcePathBox.Width = 600;
            sourcePathBox.Text = request.SourcePath;
            sourcePathBox.TextChanged += (s, e) =>
            {
                request.SourcePath = sourcePathBox.Text.Trim();
                UpdateFileFacts();
            };
            ConfigureButton(browseButton, "importBrowse", T("瀏覽…", "Browse…"), (s, e) =>
            {
                using (var ofd = new OpenFileDialog
                {
                    Filter = Localization.T("Common.SqlFilesFilter"),
                    Multiselect = false,
                    CheckFileExists = true
                })
                {
                    if (ofd.ShowDialog(this) == DialogResult.OK)
                        sourcePathBox.Text = ofd.FileName;
                }
            });
            row.Controls.AddRange(new Control[] { sourcePathBox, browseButton });
            content.Controls.Add(row);

            fileFactsLabel.AutoSize = true;
            fileFactsLabel.Margin = new Padding(0, 0, 0, 12);
            content.Controls.Add(fileFactsLabel);

            content.Controls.Add(new Label { Text = T("目標資料庫：", "Target Database:"), AutoSize = true, Margin = new Padding(0, 4, 0, 4) });
            targetDbBox.Width = 600;
            targetDbBox.ReadOnly = true;
            targetDbBox.Text = databaseName;
            content.Controls.Add(targetDbBox);

            UpdateFileFacts();
        }

        private void BuildPage1()
        {
            var content = Stack();
            pages[1].Controls.Add(content);
            content.Controls.Add(HeaderLabel(T("步驟 2：串流預檢 (Preflight Check)", "Step 2: Safe Preflight Scan")));
            content.Controls.Add(HintLabel(T("系統將完整串流掃描來源檔案，驗證編碼、語法完整性、單批次 32 MiB 上限與 Native Manifest 識別。", "Scanning file for encoding, syntax validity, 32 MiB batch boundary, and Native Manifest.")));

            scanBar.Width = 600;
            scanBar.Height = 18;
            scanBar.Margin = new Padding(0, 10, 0, 12);
            content.Controls.Add(scanBar);

            checkSummary.AutoSize = true;
            checkSummary.Margin = new Padding(0, 4, 0, 12);
            content.Controls.Add(checkSummary);
        }

        private void BuildPage2()
        {
            var content = Stack();
            pages[2].Controls.Add(content);
            content.Controls.Add(HeaderLabel(T("步驟 3：確認執行與策略設定", "Step 3: Confirm execution and strategy")));
            content.Controls.Add(HintLabel(T("請確認目標資料庫、設定物件衝突時的處理原則與獨立日誌儲存位置。", "Review target database, conflict handling strategy, and independent log location.")));

            var optGroup = new GroupBox
            {
                Text = T("匯入策略與錯誤政策", "Import and Error Policies"),
                Size = new Size(740, 140),
                Padding = new Padding(12),
                Margin = new Padding(0, 6, 0, 12)
            };
            var groupFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            optGroup.Controls.Add(groupFlow);

            groupFlow.Controls.Add(new Label { Text = T("既有物件策略：", "Existing Object Strategy:"), AutoSize = true, Margin = new Padding(0, 4, 0, 4) });
            strategyCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            strategyCombo.Width = 400;
            strategyCombo.Items.Add(Localization.T("MySqlImport.StrategyExecuteScript"));
            strategyCombo.Items.Add(Localization.T("MySqlImport.StrategyDropRecreate"));
            strategyCombo.Items.Add(Localization.T("MySqlImport.StrategyCreateIfMissing"));
            strategyCombo.Items.Add(Localization.T("MySqlImport.StrategySkipExisting"));
            strategyCombo.SelectedIndex = 0;
            groupFlow.Controls.Add(strategyCombo);

            continueOnError.Text = Localization.T("MySqlImport.ContinueOnError") + " (" + T("遇到一般 SQL 錯誤時，略過失敗批次並繼續", "Skip failed statement and continue") + ")";
            continueOnError.Checked = false;
            continueOnError.AutoSize = true;
            continueOnError.Margin = new Padding(0, 10, 0, 8);
            groupFlow.Controls.Add(continueOnError);

            content.Controls.Add(optGroup);

            content.Controls.Add(new Label { Text = T("獨立執行日誌位置：", "Independent execution log path:"), AutoSize = true, Margin = new Padding(0, 4, 0, 4) });
            logPathBox.Width = 600;
            logPathBox.ReadOnly = true;
            logPathBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mySQLPunk", "ImportLogs");
            content.Controls.Add(logPathBox);

            confirmCheck.Text = T("我已確認目標資料庫正確，並了解異常或終止時可能保留部分已生效資料。", "I have confirmed the target database and acknowledge partial writes may remain upon error.");
            confirmCheck.Checked = false;
            confirmCheck.AutoSize = true;
            confirmCheck.Margin = new Padding(0, 12, 0, 10);
            confirmCheck.CheckedChanged += (s, e) =>
            {
                if (step == 2) nextButton.Enabled = confirmCheck.Checked;
            };
            content.Controls.Add(confirmCheck);
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
            ConfigureButton(openLogButton, "importOpenLog", T("開啟日誌", "Open Log"), (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(Result?.LogPath) && File.Exists(Result.LogPath))
                    Process.Start(new ProcessStartInfo("notepad.exe", Result.LogPath) { UseShellExecute = true });
            });
            ConfigureButton(openFolderButton, "importOpenFolder", T("開啟資料夾", "Open Folder"), (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(Result?.LogPath) && File.Exists(Result.LogPath))
                    Process.Start("explorer.exe", $"/select,\"{Result.LogPath}\"");
            });
            ConfigureButton(copyPathButton, "importCopyPath", T("複製日誌路徑", "Copy Log Path"), (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(Result?.LogPath))
                {
                    Clipboard.SetText(Result.LogPath);
                    MessageBox.Show(T("日誌路徑已複製到剪貼簿！", "Log path copied to clipboard!"), T("已複製", "Copied"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            });

            openLogButton.Visible = false;
            openFolderButton.Visible = false;
            copyPathButton.Visible = false;
            actRow.Controls.AddRange(new Control[] { openLogButton, openFolderButton, copyPathButton });
            statsFlow.Controls.Add(actRow);

            topPanel.Controls.Add(statsFlow, 0, 0);

            // 右側放置 Punky 整理資料動畫與音效控制
            var mascotPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(12, 0, 0, 0)
            };
            punky.Name = "mysqlImportPunky";
            punky.Size = new Size(200, 200);
            punky.Margin = new Padding(0, 0, 0, 4);
            mascotPanel.Controls.Add(punky);

            mascotHint.Text = T("正在幫你匯入資料！", "Importing your data!");
            mascotHint.Font = UiKit.Body;
            mascotHint.AutoSize = true;
            mascotHint.Margin = new Padding(0, 0, 0, 6);
            mascotPanel.Controls.Add(mascotHint);

            ConfigureButton(soundButton, "mysqlImportToggleSound", punky.Muted ? T("開啟聲音", "Unmute") : T("靜音", "Mute"), (s, e) =>
            {
                punky.Muted = !punky.Muted;
                soundButton.Text = punky.Muted ? T("開啟聲音", "Unmute") : T("靜音", "Mute");
            });
            soundButton.Enabled = punky.AudioAvailable;
            soundButton.AutoSize = true;
            mascotPanel.Controls.Add(soundButton);

            topPanel.Controls.Add(mascotPanel, 1, 0);

            content.Controls.Add(topPanel);

            errorList.Dock = DockStyle.Fill;
            content.Controls.Add(errorList);
            errorList.BringToFront();
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
            }
            else if (step == 3)
            {
                nextButton.Text = T("完成", "Finished");
                nextButton.Enabled = !busy;
            }
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
                step = 3;
                ShowPage();
                await StartImportAsync();
            }
            else if (step == 3)
            {
                Close();
            }
        }

        private async Task RunPreflightAsync()
        {
            busy = true;
            nextButton.Enabled = false;
            recheckButton.Enabled = false;
            scanBar.Style = ProgressBarStyle.Marquee;
            checkSummary.Text = T("正在進行串流預檢掃描中...", "Scanning file in streaming mode...");

            string path = sourcePathBox.Text.Trim();
            cancellation = new CancellationTokenSource();
            preflight = await Task.Run(() => MySqlImportPreflightScanner.Scan(path, cancellation.Token));

            scanBar.Style = ProgressBarStyle.Blocks;
            scanBar.Value = preflight.Passed ? 100 : 0;
            busy = false;
            recheckButton.Enabled = true;

            if (preflight.Passed)
            {
                nextButton.Enabled = true;
                string manifestInfo = preflight.HasManifest
                    ? string.Format(T("✔ 偵測到 Native Manifest 標註：共 {0} 張表、約 {1:N0} 列（SHA: {2}）", "✔ Native Manifest detected: {0} tables, approx. {1:N0} rows (SHA: {2})"),
                        preflight.ManifestTables, preflight.ManifestRows, preflight.ManifestSha256?.Substring(0, 16) + "...")
                    : T("✔ 檔案未包含 Manifest 註解，將作為標準 SQL 腳本匯入。", "✔ File does not contain Manifest comments; will import as standard script.");

                checkSummary.Text =
                    $"✔ {T("預檢掃描通過！未發現非法編碼或超長批次。", "Preflight scan passed! No encoding issues or oversized batches.")}\n" +
                    $"✔ {T("檔案大小：", "File size: ")} {preflight.FileBytes:N0} 位元組\n" +
                    $"✔ {T("預估語句數：", "Estimated statements: ")} {preflight.EstimatedStatements:N0}\n" +
                    $"{manifestInfo}\n" +
                    $"✔ {T("目標資料庫連線正常：", "Target connection normal: ")} {databaseName}";
            }
            else
            {
                nextButton.Enabled = false;
                checkSummary.Text = $"❌ {T("預檢發現問題，無法直接匯入：", "Preflight identified issues:")}\n" + string.Join("\n", preflight.Issues.Select(iss => "● " + iss));
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

            var strat = (MySqlExistingObjectStrategy)strategyCombo.SelectedIndex;
            var opt = new MySqlImportOptions
            {
                ContinueOnError = continueOnError.Checked,
                ExistingObjectStrategy = strat
            };

            var prog = new Progress<MySqlImportProgress>(p => RenderProgress(p));
            clock.Restart();
            timer.Start();
            punky.BeginExport();

            try
            {
                Result = await runner.RunAsync(request, opt, prog, cancellation.Token);
                timer.Stop();
                clock.Stop();

                if (Result.Status == MySqlImportStatus.Completed)
                {
                    currentStatementLabel.Text = T("🎉 匯入成功完成！", "🎉 Import completed successfully!");
                    resultText.Text = Result.Summary;
                }
                else if (Result.Status == MySqlImportStatus.CompletedWithErrors)
                {
                    currentStatementLabel.Text = T("⚠️ 匯入完成，但有部分語句失敗／略過。", "⚠️ Import finished with errors / skipped statements.");
                    resultText.Text = Result.Summary;
                }
                else if (Result.Status == MySqlImportStatus.Cancelled)
                {
                    currentStatementLabel.Text = T("⚠️ 匯入已被使用者取消。", "⚠️ Import was cancelled by user.");
                    resultText.Text = T("已停止執行，先前已提交之變更可能保留於資料庫中。", "Execution stopped; previously committed changes may remain.");
                }
                else
                {
                    currentStatementLabel.Text = T("❌ 匯入停止：發生重大錯誤。", "❌ Import stopped: fatal error occurred.");
                    resultText.Text = Result.Summary;
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

        private void RenderProgress(MySqlImportProgress p)
        {
            if (p.TotalBytes > 0)
            {
                int val = Math.Min(100, Math.Max(0, (int)(p.ProcessedBytes * 100.0 / p.TotalBytes)));
                execBar.Value = val;
                coverageLabel.Text = $"{val}% ({p.ProcessedBytes / 1024:N0} KB / {p.TotalBytes / 1024:N0} KB)";
            }
            currentStatementLabel.Text = p.Message;
            speedLabel.Text = string.Format(T("速率：{0:N0} KB/秒", "Speed: {0:N0} KB/s"), p.BytesPerSecond / 1024.0);
            errorCountLabel.Text = string.Format(T("錯誤：{0} 筆", "Errors: {0}"), p.FailedStatements);
            elapsedLabel.Text = string.Format(T("耗時：{0:mm\\:ss}", "Elapsed: {0:mm\\:ss}"), p.Elapsed);
            etaLabel.Text = p.EstimatedRemaining.HasValue
                ? string.Format(T("預估剩餘：約 {0:mm\\:ss}", "ETA: approx. {0:mm\\:ss}"), p.EstimatedRemaining.Value)
                : "";

            if (!string.IsNullOrWhiteSpace(p.CurrentStatementPreview))
            {
                if (errorList.Items.Count < 200)
                {
                    errorList.Items.Add($"[{DateTime.Now:HH:mm:ss}] {p.CurrentStatementPreview}");
                    errorList.TopIndex = errorList.Items.Count - 1;
                }
            }
        }

        private void RequestCancel()
        {
            if (cancellation != null && !cancellation.IsCancellationRequested)
            {
                cancellation.Cancel();
                cancelButton.Enabled = false;
                punky.EndExport();
                currentStatementLabel.Text = T("正在停止匯入中...", "Stopping import...");
            }
        }
    }
}
