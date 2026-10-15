using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using mySQLPunk.lib;
using mySQLPunk.lib.MySqlExport;
using MySqlExportResult = mySQLPunk.lib.MySqlExport.MySqlExportResult;

namespace mySQLPunk
{
    internal sealed class MySqlExportWizardForm : Form
    {
        private readonly IDatabase db;
        private readonly string databaseName;
        private readonly MySqlSqlExportService service;
        private readonly Panel[] pages = new Panel[4];
        private readonly TextBox search = new TextBox(), targetPathBox = new TextBox();
        private readonly DataGridView tableGrid = new DataGridView();
        private readonly RadioButton schemaData = new RadioButton(), schemaOnly = new RadioButton(), dataOnly = new RadioButton();
        private readonly CheckBox singleTransaction = new CheckBox(), disableFk = new CheckBox(), includeDrop = new CheckBox();
        private readonly CheckBox includeCreateDb = new CheckBox(), removeDefiner = new CheckBox();
        private readonly CheckBox disableUnique = new CheckBox(), noAutoValueOnZero = new CheckBox();
        private readonly CheckBox includeViews = new CheckBox(), includeRoutines = new CheckBox(), includeTriggers = new CheckBox();
        private readonly Label stepsLabel = new Label(), statusLabel = new Label(), selectionSummary = new Label(), confirmSummary = new Label();
        private readonly Label currentObjectLabel = new Label(), percentLabel = new Label(), writtenLabel = new Label();
        private readonly Label elapsedLabel = new Label(), speedLabel = new Label(), etaLabel = new Label(), resultText = new Label(), mascotHint = new Label();
        private readonly Button nextButton = new Button(), backButton = new Button(), cancelButton = new Button(), browseButton = new Button(), soundButton = new Button();
        private readonly Button openFileButton = new Button(), openFolderButton = new Button();
        private readonly ProgressBar overallBar = new ProgressBar();
        private readonly PunkyReportLoadingControl punky = new PunkyReportLoadingControl();
        private readonly ListBox logList = new ListBox();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 250 };
        private readonly Stopwatch clock = new Stopwatch();

        private List<MySqlTableExportItem> catalog = new List<MySqlTableExportItem>();
        private List<string> views = new List<string>();
        private List<MySqlRoutineInfo> routines = new List<MySqlRoutineInfo>();
        private List<MySqlTriggerInfo> triggers = new List<MySqlTriggerInfo>();
        private CancellationTokenSource cancellation;
        private MySqlExportProgress lastProgress;
        private bool busy, closing, exporting;
        private int step;

        internal MySqlExportResult Result { get; private set; }

        private static string T(string zh, string en) => ReportText.Get(zh, en);

        internal MySqlExportWizardForm(IDatabase db, string databaseName, MySqlExportWizardOptions initialOptions = null)
        {
            this.db = db ?? throw new ArgumentNullException(nameof(db));
            this.databaseName = !string.IsNullOrWhiteSpace(databaseName) ? databaseName : throw new ArgumentException("Database name is required", nameof(databaseName));
            this.service = new MySqlSqlExportService(db, databaseName);

            Name = "mySqlExportWizard";
            Text = T("匯出資料庫 · MySQL / MariaDB", "Export database · MySQL / MariaDB");
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = UiKit.Body;
            ClientSize = new Size(1100, 730);
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
            header.Controls.Add(new Label { AutoSize = true, Text = T("MySQL 資料庫匯出精靈", "MySQL database export wizard") + "   ·   " + databaseName, Font = UiKit.Title, Margin = new Padding(0, 4, 0, 8) });
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
            BuildPage1(initialOptions);
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
            ConfigureButton(cancelButton, "exportCancel", T("取消", "Cancel"), (s, e) => { if (busy) RequestCancel(); else Close(); });
            ConfigureButton(nextButton, "exportNext", T("下一步 →", "Next →"), async (s, e) => await OnNextAsync());
            ConfigureButton(backButton, "exportBack", T("← 上一步", "← Back"), (s, e) => { if (!busy && step > 0 && step < 3) { step--; ShowPage(); } });

            footer.Controls.AddRange(new Control[] { cancelButton, nextButton, backButton });
            root.Controls.Add(footer, 0, 3);

            ThemeManager.MarkAsPrimary(nextButton);
            AcceptButton = nextButton;
            CancelButton = cancelButton;
            cancelButton.DialogResult = DialogResult.None;

            timer.Tick += (s, e) => UpdateHeartbeat();

            Shown += async (s, e) =>
            {
                await LoadCatalogAsync();
                ShowPage();
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
            var content = new Panel { Dock = DockStyle.Fill };
            pages[0].Controls.Add(content);

            var topPanel = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            topPanel.Controls.Add(HeaderLabel(T("步驟 1：選取要匯出的物件", "Step 1: Select objects to export")));
            topPanel.Controls.Add(HintLabel(T("勾選要包含的資料表與相關物件；可隨時透過搜尋框快速篩選。", "Select tables and objects to include; use search to filter.")));

            var modePanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 6, 0, 10) };
            schemaData.Text = T("結構與資料", "Structure & Data");
            schemaData.Checked = true;
            schemaData.AutoSize = true;
            schemaData.Margin = new Padding(0, 0, 16, 0);

            schemaOnly.Text = T("僅結構", "Structure only");
            schemaOnly.AutoSize = true;
            schemaOnly.Margin = new Padding(0, 0, 16, 0);

            dataOnly.Text = T("僅資料", "Data only");
            dataOnly.AutoSize = true;

            modePanel.Controls.AddRange(new Control[] { schemaData, schemaOnly, dataOnly });
            topPanel.Controls.Add(modePanel);

            var filterPanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 0, 0, 8) };
            search.Width = 260;
            search.Margin = new Padding(0, 0, 10, 0);
            search.TextChanged += (s, e) => ApplySearchFilter();

            var selectAllBtn = new Button { Text = T("全選", "Select All"), AutoSize = true, Margin = new Padding(0, 0, 6, 0) };
            selectAllBtn.Click += (s, e) => SetAllSelected(true);
            var clearAllBtn = new Button { Text = T("全取消", "Clear All"), AutoSize = true, Margin = new Padding(0, 0, 16, 0) };
            clearAllBtn.Click += (s, e) => SetAllSelected(false);

            includeViews.Text = T("包含檢視 (Views)", "Include Views");
            includeViews.Checked = true;
            includeViews.AutoSize = true;
            includeViews.Margin = new Padding(0, 4, 12, 0);

            includeRoutines.Text = T("包含預存程序/函數", "Include Routines");
            includeRoutines.Checked = true;
            includeRoutines.AutoSize = true;
            includeRoutines.Margin = new Padding(0, 4, 12, 0);

            includeTriggers.Text = T("包含觸發器 (Triggers)", "Include Triggers");
            includeTriggers.Checked = true;
            includeTriggers.AutoSize = true;
            includeTriggers.Margin = new Padding(0, 4, 0, 0);

            filterPanel.Controls.AddRange(new Control[] { new Label { Text = T("搜尋：", "Search:"), AutoSize = true, Margin = new Padding(0, 5, 4, 0) }, search, selectAllBtn, clearAllBtn, includeViews, includeRoutines, includeTriggers });
            topPanel.Controls.Add(filterPanel);

            content.Controls.Add(topPanel);

            tableGrid.Dock = DockStyle.Fill;
            tableGrid.AllowUserToAddRows = false;
            tableGrid.AllowUserToDeleteRows = false;
            tableGrid.RowHeadersVisible = false;
            tableGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            tableGrid.MultiSelect = false;
            tableGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;

            tableGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "ColSelect", HeaderText = T("選取", "Select"), FillWeight = 20 });
            tableGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ColName", HeaderText = T("資料表名稱", "Table Name"), FillWeight = 55, ReadOnly = true });
            tableGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ColRows", HeaderText = T("預估列數", "Estimated Rows"), FillWeight = 25, ReadOnly = true });

            tableGrid.CellValueChanged += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.ColumnIndex == 0)
                {
                    string name = tableGrid.Rows[e.RowIndex].Cells["ColName"].Value?.ToString();
                    bool isChecked = Convert.ToBoolean(tableGrid.Rows[e.RowIndex].Cells["ColSelect"].Value);
                    var item = catalog.FirstOrDefault(t => t.Name == name);
                    if (item != null) item.IsSelected = isChecked;
                    UpdateSelectionSummary();
                }
            };
            tableGrid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (tableGrid.IsCurrentCellDirty) tableGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };

            content.Controls.Add(tableGrid);
            tableGrid.BringToFront();

            var bottomPanel = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 6, 0, 0) };
            selectionSummary.AutoSize = true;
            bottomPanel.Controls.Add(selectionSummary);
            content.Controls.Add(bottomPanel);
        }

        private void BuildPage1(MySqlExportWizardOptions initialOptions)
        {
            var content = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12) };
            pages[1].Controls.Add(content);

            var header = HeaderLabel(T("步驟 2：設定輸出檔案與一致性選項", "Step 2: Output file and consistency settings"));
            header.Location = new Point(12, 10);
            content.Controls.Add(header);

            var hint = HintLabel(T("設定本機匯出 SQL 路徑與讀取事務一致性保障。", "Configure local output path and read transaction consistency."));
            hint.Location = new Point(12, 38);
            content.Controls.Add(hint);

            var destLabel = new Label { Text = T("匯出目的地檔案：", "Destination SQL file:"), AutoSize = true, Location = new Point(12, 68) };
            content.Controls.Add(destLabel);

            var fileRow = new FlowLayoutPanel { Location = new Point(12, 90), Size = new Size(740, 38), AutoSize = false, FlowDirection = FlowDirection.LeftToRight };
            targetPathBox.Width = 620;
            string defaultName = databaseName + "_dump_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".sql";
            targetPathBox.Text = initialOptions?.TargetPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), defaultName);
            ConfigureButton(browseButton, "exportBrowse", T("瀏覽…", "Browse…"), (s, e) =>
            {
                using (var sfd = new SaveFileDialog
                {
                    Filter = Localization.T("Common.SqlFilesFilter"),
                    DefaultExt = "sql",
                    FileName = Path.GetFileName(targetPathBox.Text)
                })
                {
                    if (sfd.ShowDialog(this) == DialogResult.OK)
                        targetPathBox.Text = sfd.FileName;
                }
            });
            fileRow.Controls.AddRange(new Control[] { targetPathBox, browseButton });
            content.Controls.Add(fileRow);

            // 第一組：交易與結構相容性保障
            var transGroup = new GroupBox
            {
                Text = T("交易與結構相容性保障 (Transaction & Portability)", "Transaction & Portability"),
                Location = new Point(12, 134),
                Size = new Size(740, 150),
                Padding = new Padding(12)
            };
            var transFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };

            singleTransaction.Text = T("啟用一致性快照 (--single-transaction，以 REPEATABLE READ 交易無鎖讀取，保障關聯一致)", "Consistent snapshot (--single-transaction, non-locking repeatable read)");
            singleTransaction.Checked = true;
            singleTransaction.AutoSize = true;
            singleTransaction.Margin = new Padding(0, 4, 0, 6);

            removeDefiner.Text = T("移除 DEFINER 宣告（提高還原至其他主機/帳號時的相容性）", "Remove DEFINER clauses for portability across hosts/accounts");
            removeDefiner.Checked = true;
            removeDefiner.AutoSize = true;
            removeDefiner.Margin = new Padding(0, 0, 0, 6);

            includeDrop.Text = T("包含 DROP TABLE / VIEW / ROUTINE / TRIGGER 語句（覆蓋舊物件）", "Include DROP statements (TABLE / VIEW / ROUTINE / TRIGGER)");
            includeDrop.Checked = true;
            includeDrop.AutoSize = true;
            includeDrop.Margin = new Padding(0, 0, 0, 6);

            includeCreateDb.Text = T("包含 CREATE DATABASE 語句", "Include CREATE DATABASE statement");
            includeCreateDb.Checked = false;
            includeCreateDb.AutoSize = true;

            transFlow.Controls.AddRange(new Control[] { singleTransaction, removeDefiner, includeDrop, includeCreateDb });
            transGroup.Controls.Add(transFlow);
            content.Controls.Add(transGroup);

            // 第二組：MySQL 專屬高速還原與約束優化
            var optGroup = new GroupBox
            {
                Text = T("MySQL 專屬高速還原與約束優化 (Engine Optimizations & Safety)", "Engine Optimizations & Safety"),
                Location = new Point(12, 294),
                Size = new Size(740, 130),
                Padding = new Padding(12)
            };
            var optFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };

            disableFk.Text = T("匯入時關閉外鍵檢查 (SET FOREIGN_KEY_CHECKS = 0，避免資料表相依順序報錯)", "Disable foreign key checks on import (SET FOREIGN_KEY_CHECKS = 0)");
            disableFk.Checked = true;
            disableFk.AutoSize = true;
            disableFk.Margin = new Padding(0, 4, 0, 6);

            disableUnique.Text = T("匯入時關閉唯一性檢查 (SET UNIQUE_CHECKS = 0，啟用 Insert Buffer 提升匯入速度 2~5 倍)", "Disable unique checks on import (SET UNIQUE_CHECKS = 0, boosts bulk INSERTs 2~5x)");
            disableUnique.Checked = true;
            disableUnique.AutoSize = true;
            disableUnique.Margin = new Padding(0, 0, 0, 6);

            noAutoValueOnZero.Text = T("防止主鍵 0 被 AUTO_INCREMENT 覆蓋 (SET SQL_MODE = 'NO_AUTO_VALUE_ON_ZERO')", "Prevent primary key 0 from auto-increment overwrite (NO_AUTO_VALUE_ON_ZERO)");
            noAutoValueOnZero.Checked = true;
            noAutoValueOnZero.AutoSize = true;

            optFlow.Controls.AddRange(new Control[] { disableFk, disableUnique, noAutoValueOnZero });
            optGroup.Controls.Add(optFlow);
            content.Controls.Add(optGroup);
        }

        private void BuildPage2()
        {
            var content = Stack();
            pages[2].Controls.Add(content);
            content.Controls.Add(HeaderLabel(T("步驟 3：確認物件與預估", "Step 3: Confirm selection and estimates")));
            content.Controls.Add(HintLabel(T("請確認本次匯出參數，點擊「開始匯出」即會串流寫入本機檔案。", "Review export parameters. Click 'Start Export' to begin streaming to local file.")));

            confirmSummary.AutoSize = true;
            confirmSummary.Margin = new Padding(0, 12, 0, 16);
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
            currentObjectLabel.Font = UiKit.Subtitle;
            currentObjectLabel.AutoSize = true;
            statsFlow.Controls.Add(currentObjectLabel);

            overallBar.Width = 520;
            overallBar.Height = 20;
            overallBar.Margin = new Padding(0, 8, 0, 6);
            statsFlow.Controls.Add(overallBar);

            var numRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            percentLabel.AutoSize = true;
            percentLabel.Margin = new Padding(0, 0, 20, 0);
            writtenLabel.AutoSize = true;
            writtenLabel.Margin = new Padding(0, 0, 20, 0);
            speedLabel.AutoSize = true;
            numRow.Controls.AddRange(new Control[] { percentLabel, writtenLabel, speedLabel });
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
            ConfigureButton(openFileButton, "exportOpenFile", T("開啟檔案", "Open File"), (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(Result?.TargetPath) && File.Exists(Result.TargetPath))
                    Process.Start(new ProcessStartInfo("notepad.exe", Result.TargetPath) { UseShellExecute = true });
            });
            ConfigureButton(openFolderButton, "exportOpenFolder", T("開啟資料夾", "Open Folder"), (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(Result?.TargetPath) && File.Exists(Result.TargetPath))
                    Process.Start("explorer.exe", $"/select,\"{Result.TargetPath}\"");
            });
            openFileButton.Visible = false;
            openFolderButton.Visible = false;
            actRow.Controls.AddRange(new Control[] { openFileButton, openFolderButton });
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
            punky.Name = "mysqlPunky";
            punky.Size = new Size(200, 200);
            punky.Margin = new Padding(0, 0, 0, 4);
            mascotPanel.Controls.Add(punky);

            mascotHint.Text = T("正在幫你整理資料！", "Organizing your data!");
            mascotHint.Font = UiKit.Body;
            mascotHint.AutoSize = true;
            mascotHint.Margin = new Padding(0, 0, 0, 6);
            mascotPanel.Controls.Add(mascotHint);

            ConfigureButton(soundButton, "mysqlToggleSound", punky.Muted ? T("開啟聲音", "Unmute") : T("靜音", "Mute"), (s, e) =>
            {
                punky.Muted = !punky.Muted;
                soundButton.Text = punky.Muted ? T("開啟聲音", "Unmute") : T("靜音", "Mute");
            });
            soundButton.Enabled = punky.AudioAvailable;
            soundButton.AutoSize = true;
            mascotPanel.Controls.Add(soundButton);

            topPanel.Controls.Add(mascotPanel, 1, 0);

            content.Controls.Add(topPanel);

            logList.Dock = DockStyle.Fill;
            content.Controls.Add(logList);
            logList.BringToFront();
        }

        private async Task LoadCatalogAsync()
        {
            statusLabel.Text = T("正在載入資料庫結構中...", "Loading database catalog...");
            catalog = await Task.Run(() => service.LoadTableCatalog());
            views = await Task.Run(() => service.LoadViews());
            routines = await Task.Run(() => service.LoadRoutines());
            triggers = await Task.Run(() => service.LoadTriggers());

            tableGrid.Rows.Clear();
            foreach (var item in catalog)
            {
                tableGrid.Rows.Add(item.IsSelected, item.Name, item.EstimatedRows.ToString("N0"));
            }

            statusLabel.Text = "";
            UpdateSelectionSummary();
        }

        private void ApplySearchFilter()
        {
            string term = search.Text.Trim();
            tableGrid.Rows.Clear();
            foreach (var item in catalog)
            {
                if (string.IsNullOrWhiteSpace(term) || item.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    tableGrid.Rows.Add(item.IsSelected, item.Name, item.EstimatedRows.ToString("N0"));
                }
            }
            UpdateSelectionSummary();
        }

        private void SetAllSelected(bool isSelected)
        {
            foreach (var item in catalog) item.IsSelected = isSelected;
            foreach (DataGridViewRow row in tableGrid.Rows) row.Cells["ColSelect"].Value = isSelected;
            UpdateSelectionSummary();
        }

        private void UpdateSelectionSummary()
        {
            int selectedCount = catalog.Count(t => t.IsSelected);
            long totalRows = catalog.Where(t => t.IsSelected).Sum(t => t.EstimatedRows);
            selectionSummary.Text = string.Format(
                T("已選取 {0} / {1} 張資料表（預估約 {2:N0} 筆資料）", "Selected {0} / {1} tables (approx. {2:N0} rows)"),
                selectedCount, catalog.Count, totalRows);
        }

        private void ShowPage()
        {
            for (int i = 0; i < 4; i++) pages[i].Visible = (i == step);

            string[] stepTitles = {
                T("1. 選取物件", "1. Select Objects"),
                T("2. 輸出設定", "2. Output Settings"),
                T("3. 確認", "3. Confirm"),
                T("4. 執行匯出", "4. Export")
            };
            stepsLabel.Text = string.Join("   →   ", stepTitles.Select((t, i) => i == step ? $"【{t}】" : t));

            backButton.Enabled = (step > 0 && step < 3 && !busy);
            if (step == 0)
            {
                nextButton.Text = T("下一步 →", "Next →");
                nextButton.Enabled = catalog.Any(t => t.IsSelected) || views.Count > 0;
            }
            else if (step == 1)
            {
                nextButton.Text = T("下一步 →", "Next →");
                nextButton.Enabled = !string.IsNullOrWhiteSpace(targetPathBox.Text);
            }
            else if (step == 2)
            {
                nextButton.Text = T("🚀 開始匯出", "🚀 Start Export");
                nextButton.Enabled = true;
                UpdateConfirmSummary();
            }
            else if (step == 3)
            {
                nextButton.Text = T("完成", "Finished");
                nextButton.Enabled = !busy;
            }
        }

        private void UpdateConfirmSummary()
        {
            var selectedTables = catalog.Where(t => t.IsSelected).ToList();
            confirmSummary.Text =
                $"● {T("目標資料庫：", "Target Database: ")} {databaseName}\n" +
                $"● {T("匯出模式：", "Export Mode: ")} {(schemaData.Checked ? T("結構與資料", "Structure & Data") : schemaOnly.Checked ? T("僅結構", "Structure only") : T("僅資料", "Data only"))}\n" +
                $"● {T("選取資料表：", "Selected Tables: ")} {selectedTables.Count} 張（預估 {selectedTables.Sum(t => t.EstimatedRows):N0} 列）\n" +
                $"● {T("選取檢視：", "Selected Views: ")} {(includeViews.Checked ? views.Count : 0)} 個\n" +
                $"● {T("選取常式/觸發器：", "Selected Routines/Triggers: ")} {(includeRoutines.Checked ? routines.Count : 0)} / {(includeTriggers.Checked ? triggers.Count : 0)} 個\n" +
                $"● {T("一致性快照：", "Consistency Snapshot: ")} {(singleTransaction.Checked ? T("啟用 (--single-transaction，不鎖表)", "Enabled (--single-transaction)") : T("停用", "Disabled"))}\n" +
                $"● {T("MySQL 引擎優化：", "MySQL Engine Optimizations: ")} {(disableUnique.Checked ? T("停用唯一檢查 (加速 2~5x)", "Unique Checks Off") : "")} {(disableFk.Checked ? T("・停用外鍵", "・FK Checks Off") : "")} {(noAutoValueOnZero.Checked ? "・NO_AUTO_VALUE_ON_ZERO" : "")}\n" +
                $"● {T("輸出檔案路徑：", "Target Path: ")} {targetPathBox.Text}\n\n" +
                $"{T("提示：匯出過程將全串流寫入暫存檔案，完成驗證後原子發布，不污染既有檔案。", "Notice: Streaming to staging file first, published atomically upon completion.")}";
        }

        private async Task OnNextAsync()
        {
            if (busy) return;
            if (step < 2)
            {
                step++;
                ShowPage();
            }
            else if (step == 2)
            {
                step = 3;
                ShowPage();
                await StartExportAsync();
            }
            else if (step == 3)
            {
                Close();
            }
        }

        private async Task StartExportAsync()
        {
            busy = true;
            exporting = true;
            cancellation = new CancellationTokenSource();
            nextButton.Enabled = false;
            backButton.Enabled = false;
            cancelButton.Text = T("停止匯出", "Stop Export");
            logList.Items.Clear();

            var mode = schemaData.Checked ? MySqlExportMode.SchemaAndData : schemaOnly.Checked ? MySqlExportMode.SchemaOnly : MySqlExportMode.DataOnly;
            var opt = new MySqlExportWizardOptions(mode, targetPathBox.Text.Trim())
            {
                SingleTransaction = singleTransaction.Checked,
                DisableForeignKeyChecks = disableFk.Checked,
                DisableUniqueChecks = disableUnique.Checked,
                SetNoAutoValueOnZero = noAutoValueOnZero.Checked,
                IncludeDropStatements = includeDrop.Checked,
                IncludeCreateDatabase = includeCreateDb.Checked,
                RemoveDefiner = removeDefiner.Checked,
                IncludeViews = includeViews.Checked,
                IncludeRoutines = includeRoutines.Checked,
                IncludeTriggers = includeTriggers.Checked,
                SelectedTables = catalog.Where(t => t.IsSelected).Select(t => t.Name).ToList(),
                SelectedViews = views,
                SelectedRoutines = routines.Select(r => r.Type + ":" + r.Name).ToList(),
                SelectedTriggers = triggers.Select(tr => tr.Name).ToList()
            };

            var progress = new Progress<MySqlExportProgress>(p =>
            {
                lastProgress = p;
                RenderProgress(p);
            });

            clock.Restart();
            timer.Start();
            punky.BeginExport();

            try
            {
                Result = await service.ExportAsync(opt, progress, cancellation.Token);
                timer.Stop();
                clock.Stop();

                if (Result.Success)
                {
                    currentObjectLabel.Text = T("🎉 匯出成功完成！", "🎉 Export completed successfully!");
                    resultText.Text = string.Format(
                        T("共計匯出 {0} 張資料表、{1:N0} 列資料、檔案大小 {2:N0} 位元組。\nSHA-256：{3}",
                          "Exported {0} tables, {1:N0} rows, size {2:N0} bytes.\nSHA-256: {3}"),
                        Result.TableCount, Result.TotalRows, Result.FileBytes, Result.Sha256Hash);
                    openFileButton.Visible = true;
                    openFolderButton.Visible = true;
                }
                else if (Result.Stage == MySqlExportStage.Cancelled)
                {
                    currentObjectLabel.Text = T("⚠️ 匯出已被使用者取消。", "⚠️ Export cancelled by user.");
                    resultText.Text = T("暫存檔案已自動清理，未更動目標檔案。", "Staging file was deleted, destination untouched.");
                }
                else
                {
                    currentObjectLabel.Text = T("❌ 匯出失敗：", "❌ Export failed: ") + Result.ErrorMessage;
                    resultText.Text = Result.ErrorMessage;
                }
            }
            catch (Exception ex)
            {
                timer.Stop();
                clock.Stop();
                currentObjectLabel.Text = T("❌ 發生非預期錯誤：", "❌ Unexpected error: ") + ex.Message;
            }
            finally
            {
                punky.EndExport();
                busy = false;
                exporting = false;
                cancelButton.Text = T("關閉", "Close");
                nextButton.Enabled = true;
                if (closing) Close();
            }
        }

        private void RenderProgress(MySqlExportProgress p)
        {
            if (p.TotalTables > 0)
            {
                int val = Math.Min(100, Math.Max(0, (int)(p.CompletedTables * 100.0 / p.TotalTables)));
                overallBar.Value = val;
                percentLabel.Text = $"{val}%";
            }
            currentObjectLabel.Text = p.Message;
            writtenLabel.Text = string.Format(T("已寫入：{0:N0} 列 ({1:N0} KB)", "Written: {0:N0} rows ({1:N0} KB)"), p.ProcessedRows, p.WrittenBytes / 1024);
            speedLabel.Text = string.Format(T("速率：{0:N0} 列/秒", "Speed: {0:N0} rows/s"), p.RowsPerSecond);
            elapsedLabel.Text = string.Format(T("耗時：{0:mm\\:ss}", "Elapsed: {0:mm\\:ss}"), p.Elapsed);
            etaLabel.Text = p.EstimatedRemaining.HasValue
                ? string.Format(T("預估剩餘：約 {0:mm\\:ss}", "ETA: approx. {0:mm\\:ss}"), p.EstimatedRemaining.Value)
                : "";

            if (!string.IsNullOrWhiteSpace(p.CurrentObject))
            {
                string logItem = $"[{DateTime.Now:HH:mm:ss}] {p.Message}";
                if (logList.Items.Count == 0 || !logList.Items[logList.Items.Count - 1].Equals(logItem))
                {
                    logList.Items.Add(logItem);
                    logList.TopIndex = logList.Items.Count - 1;
                }
            }
        }

        private void UpdateHeartbeat()
        {
            if (lastProgress != null && exporting)
            {
                elapsedLabel.Text = string.Format(T("耗時：{0:mm\\:ss}", "Elapsed: {0:mm\\:ss}"), clock.Elapsed);
            }
        }

        private void RequestCancel()
        {
            if (cancellation != null && !cancellation.IsCancellationRequested)
            {
                cancellation.Cancel();
                cancelButton.Enabled = false;
                punky.EndExport();
                currentObjectLabel.Text = T("正在取消匯出並清理暫存中...", "Cancelling export and cleaning temporary files...");
            }
        }
    }
}
