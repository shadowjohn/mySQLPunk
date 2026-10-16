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
using mySQLPunk.lib.SqliteExport;
using SqliteExportResult = mySQLPunk.lib.SqliteExport.SqliteExportResult;

namespace mySQLPunk
{
    internal sealed class SqliteExportWizardForm : Form
    {
        private readonly IDatabase db;
        private readonly string databaseName;
        private readonly SqliteSqlExportService service;
        private readonly HashSet<string> initialSelection;
        private readonly Panel[] pages = new Panel[4];
        private readonly TextBox search = new TextBox(), targetPathBox = new TextBox();
        private readonly DataGridView tableGrid = new DataGridView();
        private readonly RadioButton schemaData = new RadioButton(), schemaOnly = new RadioButton(), dataOnly = new RadioButton();
        private readonly CheckBox singleTransaction = new CheckBox(), pragmaFk = new CheckBox(), pragmaSync = new CheckBox(), includeDrop = new CheckBox();
        private readonly CheckBox includeViews = new CheckBox(), includeIndexes = new CheckBox(), includeTriggers = new CheckBox();
        private readonly CheckBox includeComments = new CheckBox(), excludeSpatialMetadata = new CheckBox();
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

        private List<SqliteTableExportItem> catalog = new List<SqliteTableExportItem>();
        private List<string> views = new List<string>();
        private List<string> triggers = new List<string>();
        private CancellationTokenSource cancellation;
        private SqliteExportProgress lastProgress;
        private bool busy, closing, exporting;
        private int step;

        internal SqliteExportResult Result { get; private set; }

        private static string T(string zh, string en) => ReportText.Get(zh, en);

        internal SqliteExportWizardForm(IDatabase db, string databaseName, SqliteExportWizardOptions initialOptions = null)
        {
            this.db = db ?? throw new ArgumentNullException(nameof(db));
            this.databaseName = !string.IsNullOrWhiteSpace(databaseName) ? databaseName : throw new ArgumentException("Database name is required", nameof(databaseName));
            this.service = new SqliteSqlExportService(db, databaseName);
            initialSelection = initialOptions?.SelectedTables?.Count > 0 ? new HashSet<string>(initialOptions.SelectedTables, StringComparer.OrdinalIgnoreCase) : null;

            Name = "sqliteExportWizard";
            Text = T("匯出資料庫 · SQLite (支援 SpatiaLite / GeoPackage GIS)", "Export database · SQLite (with SpatiaLite / GeoPackage GIS)");
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
            header.Controls.Add(new Label { AutoSize = true, Text = T("SQLite 資料庫傾印匯出精靈", "SQLite database export wizard") + "   ·   " + databaseName, Font = UiKit.Title, Margin = new Padding(0, 4, 0, 8) });
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
            includeViews.Checked = initialOptions?.IncludeViews ?? true;
            includeTriggers.Checked = initialOptions?.IncludeTriggers ?? true;
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

            timer.Tick += (s, e) => UpdateLiveTicker();

            Shown += async (s, e) => await LoadCatalogAsync();
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
            var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 0, 0) };
            pages[0].Controls.Add(content);

            var topPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(0, 0, 0, 8)
            };

            topPanel.Controls.Add(HeaderLabel(T("步驟 1：選取要匯出的 SQLite 資料表與空間物件", "Step 1: Select SQLite tables and spatial layers to export")));
            topPanel.Controls.Add(HintLabel(T("可勾選或篩選資料表。SpatiaLite 與 GeoPackage 幾何欄位將全串流無損匯出。", "Check tables to include. SpatiaLite and GeoPackage GIS layers are preserved in binary fidelity.")));

            var filterPanel = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Margin = new Padding(0, 4, 0, 4)
            };

            search.Width = 180;
            search.Margin = new Padding(0, 0, 8, 0);
            search.TextChanged += (s, e) => ApplySearchFilter();

            var selectAllBtn = new Button { Text = T("全選", "Select All"), AutoSize = true, Margin = new Padding(0, 0, 6, 0) };
            selectAllBtn.Click += (s, e) => SetAllSelected(true);
            var clearAllBtn = new Button { Text = T("全取消", "Clear All"), AutoSize = true, Margin = new Padding(0, 0, 16, 0) };
            clearAllBtn.Click += (s, e) => SetAllSelected(false);

            includeViews.Text = T("包含檢視 (Views)", "Include Views");
            includeViews.Checked = true;
            includeViews.AutoSize = true;
            includeViews.Margin = new Padding(0, 4, 12, 0);

            includeIndexes.Text = T("包含索引 (Indexes)", "Include Indexes");
            includeIndexes.Checked = true;
            includeIndexes.AutoSize = true;
            includeIndexes.Margin = new Padding(0, 4, 12, 0);

            includeTriggers.Text = T("包含觸發器 (Triggers)", "Include Triggers");
            includeTriggers.Checked = true;
            includeTriggers.AutoSize = true;
            includeTriggers.Margin = new Padding(0, 4, 12, 0);

            includeComments.Text = T("包含側車註解 (Comments)", "Include Comments");
            includeComments.Checked = true;
            includeComments.AutoSize = true;
            includeComments.Margin = new Padding(0, 4, 12, 0);

            excludeSpatialMetadata.Text = T("排除龐大空間中繼資料 (spatial_ref_sys 5,000+ 筆 EPSG)", "Exclude bulky spatial metadata (spatial_ref_sys 5000+ EPSG rows)");
            excludeSpatialMetadata.Checked = false;
            excludeSpatialMetadata.AutoSize = true;
            excludeSpatialMetadata.Margin = new Padding(0, 4, 0, 0);
            excludeSpatialMetadata.CheckedChanged += (s, e) =>
            {
                if (excludeSpatialMetadata.Checked)
                {
                    foreach (var item in catalog.Where(t => t.IsSpatialMetadata)) item.IsSelected = false;
                }
                ApplySearchFilter();
            };

            filterPanel.Controls.AddRange(new Control[] {
                new Label { Text = T("搜尋：", "Search:"), AutoSize = true, Margin = new Padding(0, 5, 4, 0) },
                search, selectAllBtn, clearAllBtn,
                includeViews, includeIndexes, includeTriggers, includeComments, excludeSpatialMetadata
            });
            topPanel.Controls.Add(filterPanel);

            content.Controls.Add(topPanel);

            tableGrid.Dock = DockStyle.Fill;
            tableGrid.AllowUserToAddRows = false;
            tableGrid.AllowUserToDeleteRows = false;
            tableGrid.RowHeadersVisible = false;
            tableGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            tableGrid.MultiSelect = false;
            tableGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;

            tableGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "ColSelect", HeaderText = T("選取", "Select"), FillWeight = 15 });
            tableGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ColName", HeaderText = T("資料表名稱", "Table Name"), FillWeight = 40, ReadOnly = true });
            tableGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ColRows", HeaderText = T("預估列數", "Estimated Rows"), FillWeight = 20, ReadOnly = true });
            tableGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ColSpatial", HeaderText = T("空間與型態特徵", "Spatial & Object Characteristics"), FillWeight = 25, ReadOnly = true });

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

        private void BuildPage1(SqliteExportWizardOptions initialOptions)
        {
            var content = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12) };
            pages[1].Controls.Add(content);

            var header = HeaderLabel(T("步驟 2：設定輸出檔案與效能選項", "Step 2: Output file and performance settings"));
            header.Location = new Point(12, 10);
            content.Controls.Add(header);

            var pathGroup = new GroupBox
            {
                Text = T("輸出 SQL 檔案路徑", "Output SQL file path"),
                Font = UiKit.Body,
                Location = new Point(12, 50),
                Size = new Size(1040, 75),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            targetPathBox.Location = new Point(16, 30);
            targetPathBox.Size = new Size(900, 28);
            targetPathBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            targetPathBox.Text = initialOptions?.TargetPath ?? "";
            targetPathBox.TextChanged += (s, e) => ShowPage();

            ConfigureButton(browseButton, "exportBrowse", T("瀏覽...", "Browse..."), (s, e) =>
            {
                using (var sfd = new SaveFileDialog())
                {
                    sfd.Title = T("選擇輸出 SQL 檔案", "Select output SQL file");
                    sfd.Filter = "SQL Files (*.sql)|*.sql|All Files (*.*)|*.*";
                    sfd.FileName = Path.GetFileName(targetPathBox.Text);
                    if (sfd.ShowDialog(this) == DialogResult.OK)
                    {
                        targetPathBox.Text = sfd.FileName;
                    }
                }
            });
            browseButton.Location = new Point(925, 27);
            browseButton.Size = new Size(100, 32);
            browseButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            pathGroup.Controls.AddRange(new Control[] { targetPathBox, browseButton });
            content.Controls.Add(pathGroup);

            var modeGroup = new GroupBox
            {
                Text = T("匯出內容範圍", "Export content scope"),
                Font = UiKit.Body,
                Location = new Point(12, 135),
                Size = new Size(1040, 115),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            var modeFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(16, 12, 16, 12) };

            schemaData.Text = T("結構與資料 (完整傾印，包含 DDL 與二進位幾何 INSERT)", "Structure & Data (Full logical dump including DDL and binary geometry INSERTs)");
            schemaData.Checked = (initialOptions?.Mode ?? SqliteExportMode.SchemaAndData) == SqliteExportMode.SchemaAndData;
            schemaData.AutoSize = true;
            schemaData.Margin = new Padding(0, 0, 0, 8);

            schemaOnly.Text = T("僅結構 (Schema Only，僅匯出資料表、檢視、索引與觸發器定義)", "Structure only (Schema only, excludes all table rows)");
            schemaOnly.Checked = initialOptions?.Mode == SqliteExportMode.SchemaOnly;
            schemaOnly.AutoSize = true;
            schemaOnly.Margin = new Padding(0, 0, 0, 8);

            dataOnly.Text = T("僅資料 (Data Only，僅匯出資料列 INSERT 語句，不含 CREATE/DROP)", "Data only (Data only, emits INSERT statements without CREATE/DROP)");
            dataOnly.Checked = initialOptions?.Mode == SqliteExportMode.DataOnly;
            dataOnly.AutoSize = true;

            modeFlow.Controls.AddRange(new Control[] { schemaData, schemaOnly, dataOnly });
            modeGroup.Controls.Add(modeFlow);
            content.Controls.Add(modeGroup);

            var optGroup = new GroupBox
            {
                Text = T("SQLite 引擎與交易保護選項", "SQLite engine & transaction options"),
                Font = UiKit.Body,
                Location = new Point(12, 260),
                Size = new Size(1040, 195),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            var optFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(16, 12, 16, 12) };

            singleTransaction.Text = T("使用單一交易包覆 (BEGIN TRANSACTION; ... COMMIT;，寫入速度提升數十倍並確保一致性)", "Wrap in single transaction (BEGIN TRANSACTION; ... COMMIT;, boosts write speeds 10-50x)");
            singleTransaction.Checked = true;
            singleTransaction.AutoSize = true;
            singleTransaction.Margin = new Padding(0, 0, 0, 8);

            pragmaSync.Text = T("匯入時關閉同步寫盤 (PRAGMA synchronous = OFF;，避免每次寫入強迫磁碟 flush)", "Disable synchronous disk flushing during restore (PRAGMA synchronous = OFF;)");
            pragmaSync.Checked = true;
            pragmaSync.AutoSize = true;
            pragmaSync.Margin = new Padding(0, 0, 0, 8);

            pragmaFk.Text = T("匯入時關閉外鍵檢查 (PRAGMA foreign_keys = OFF;，防範資料表相依順序報錯)", "Disable foreign key checks on import (PRAGMA foreign_keys = OFF;)");
            pragmaFk.Checked = true;
            pragmaFk.AutoSize = true;
            pragmaFk.Margin = new Padding(0, 0, 0, 8);

            includeDrop.Text = T("匯入前先刪除既有物件 (DROP TABLE / VIEW / INDEX / TRIGGER IF EXISTS)", "Drop existing objects before recreate (DROP ... IF EXISTS)");
            includeDrop.Checked = true;
            includeDrop.AutoSize = true;

            optFlow.Controls.AddRange(new Control[] { singleTransaction, pragmaSync, pragmaFk, includeDrop });
            optGroup.Controls.Add(optFlow);
            content.Controls.Add(optGroup);
        }

        private void BuildPage2()
        {
            var content = Stack();
            pages[2].Controls.Add(content);
            content.Controls.Add(HeaderLabel(T("步驟 3：確認物件與預估", "Step 3: Confirm selection and estimates")));
            content.Controls.Add(HintLabel(T("請確認本次 SQLite 匯出參數，點擊「開始匯出」即會串流寫入本機檔案。", "Review export parameters. Click 'Start Export' to begin streaming to local file.")));

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

            // 右側放置 Punky 看板娘動畫與音效控制
            var mascotPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(12, 0, 0, 0)
            };
            punky.Name = "sqlitePunky";
            punky.Size = new Size(200, 200);
            punky.Margin = new Padding(0, 0, 0, 4);
            mascotPanel.Controls.Add(punky);

            mascotHint.Text = T("崩琦正在幫你整理 SQLite GIS 資料！", "Punky is organizing your SQLite GIS data!");
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

            logList.Name = "exportLogList";
            logList.Dock = DockStyle.Fill;
            logList.IntegralHeight = false;
            logList.BorderStyle = BorderStyle.FixedSingle;
            logList.Font = new Font("Consolas", 9f, FontStyle.Regular);
            content.Controls.Add(logList);
            logList.BringToFront();
        }

        private async Task LoadCatalogAsync()
        {
            busy = true;
            try
            {
                statusLabel.Text = T("正在載入 SQLite 資料庫目錄與空間中繼資訊...", "Loading SQLite database catalog and spatial metadata...");
                catalog = await Task.Run(() => service.LoadTableCatalog());
                views = await Task.Run(() => service.LoadViews());
                triggers = await Task.Run(() => service.LoadTriggers());

                tableGrid.Rows.Clear();
                foreach (var item in catalog)
                {
                    if (initialSelection != null) item.IsSelected = initialSelection.Contains(item.Name);
                    tableGrid.Rows.Add(item.IsSelected, item.Name, item.EstimatedRows.ToString("N0"), item.SpatialInfo);
                }

                statusLabel.Text = "";
                UpdateSelectionSummary();
            }
            finally
            {
                busy = false;
                if (closing) Close();
            }
        }

        private void ApplySearchFilter()
        {
            string term = search.Text.Trim();
            tableGrid.Rows.Clear();
            foreach (var item in catalog)
            {
                if (string.IsNullOrWhiteSpace(term) || item.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 || item.SpatialInfo.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    tableGrid.Rows.Add(item.IsSelected, item.Name, item.EstimatedRows.ToString("N0"), item.SpatialInfo);
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
            int spatialCount = catalog.Count(t => t.IsSelected && t.IsSpatial);
            long totalRows = catalog.Where(t => t.IsSelected).Sum(t => t.EstimatedRows);
            selectionSummary.Text = string.Format(
                T("已選取 {0} / {1} 張資料表 (含 {2} 個 GIS 空間圖層，預估約 {3:N0} 筆資料)", "Selected {0} / {1} tables ({2} GIS layers, approx. {3:N0} rows)"),
                selectedCount, catalog.Count, spatialCount, totalRows);
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
            int spatialCount = selectedTables.Count(t => t.IsSpatial);
            confirmSummary.Text =
                $"● {T("目標資料庫：", "Target Database: ")} {databaseName}\n" +
                $"● {T("匯出模式：", "Export Mode: ")} {(schemaData.Checked ? T("結構與資料 (含 GIS 二進位幾何)", "Structure & Data (with GIS binary geometries)") : schemaOnly.Checked ? T("僅結構", "Structure only") : T("僅資料", "Data only"))}\n" +
                $"● {T("選取資料表：", "Selected Tables: ")} {selectedTables.Count} 張（含 {spatialCount} 個 GIS 圖層，預估 {selectedTables.Sum(t => t.EstimatedRows):N0} 列）\n" +
                $"● {T("選取檢視：", "Selected Views: ")} {(includeViews.Checked ? views.Count : 0)} 個\n" +
                $"● {T("選取索引/觸發器：", "Selected Indexes/Triggers: ")} {(includeIndexes.Checked ? T("包含", "Included") : T("略過", "Skipped"))} / {(includeTriggers.Checked ? triggers.Count : 0)} 個\n" +
                $"● {T("單一交易包覆：", "Single Transaction: ")} {(singleTransaction.Checked ? T("啟用 (BEGIN TRANSACTION; ... COMMIT;)", "Enabled") : T("停用", "Disabled"))}\n" +
                $"● {T("SQLite 寫入優化：", "SQLite Engine Optimizations: ")} {(pragmaSync.Checked ? "PRAGMA synchronous = OFF; " : "")} {(pragmaFk.Checked ? "PRAGMA foreign_keys = OFF;" : "")}\n" +
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

        private SqliteExportWizardOptions BuildExportOptions()
        {
            var mode = schemaData.Checked ? SqliteExportMode.SchemaAndData : schemaOnly.Checked ? SqliteExportMode.SchemaOnly : SqliteExportMode.DataOnly;
            return new SqliteExportWizardOptions(mode, targetPathBox.Text.Trim())
            {
                SingleTransaction = singleTransaction.Checked,
                PragmaForeignKeysOff = pragmaFk.Checked,
                PragmaSynchronousOff = pragmaSync.Checked,
                IncludeDropStatements = includeDrop.Checked,
                IncludeViews = includeViews.Checked,
                IncludeIndexes = includeIndexes.Checked,
                IncludeTriggers = includeTriggers.Checked,
                IncludeComments = includeComments.Checked,
                ExcludeSpatialMetadata = excludeSpatialMetadata.Checked,
                SelectedTables = catalog.Where(t => t.IsSelected).Select(t => t.Name).ToList(),
                SelectedViews = catalog.All(t => t.IsSelected) ? views : new List<string>(),
                SelectedTriggers = triggers
            };
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

            var opt = BuildExportOptions();

            var progress = new Progress<SqliteExportProgress>(p =>
            {
                lastProgress = p;
                RenderProgress(p);
            });

            punky.BeginExport();
            clock.Restart();
            timer.Start();

            try
            {
                Result = await service.ExportAsync(opt, progress, cancellation.Token);
                timer.Stop();
                clock.Stop();

                if (Result.Success)
                {
                    currentObjectLabel.Text = T("✅ 匯出作業順利完成！", "✅ Export completed successfully!");
                    resultText.Text = string.Format(
                        T("共計匯出 {0} 張資料表、{1} 個檢視、{2} 個索引，總計 {3:N0} 列。\n檔案大小：{4:N0} 位元組 · SHA-256：{5}",
                          "Exported {0} tables, {1} views, {2} indexes, {3:N0} rows.\nFile size: {4:N0} bytes · SHA-256: {5}"),
                        Result.TableCount, Result.ViewCount, Result.IndexCount, Result.TotalRows, Result.FileBytes, Result.Sha256Hash);
                    openFileButton.Visible = true;
                    openFolderButton.Visible = true;
                }
                else if (Result.Stage == SqliteExportStage.Cancelled)
                {
                    currentObjectLabel.Text = T("⚠️ 匯出作業已由使用者取消。", "⚠️ Export cancelled by user.");
                    resultText.Text = Result.ErrorMessage;
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
                resultText.Text = ex.Message;
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

        private void RenderProgress(SqliteExportProgress p)
        {
            if (p.EstimatedTotalRows > 0)
            {
                int val = Math.Min(100, Math.Max(0, (int)(p.ProcessedRows * 100.0 / p.EstimatedTotalRows)));
                overallBar.Value = val;
                percentLabel.Text = $"{val}% ({p.ProcessedRows:N0} / {p.EstimatedTotalRows:N0} 列)";
            }
            else
            {
                percentLabel.Text = $"{p.ProcessedRows:N0} 列";
            }

            currentObjectLabel.Text = p.Message;
            writtenLabel.Text = string.Format(T("已寫入：{0:N2} MB", "Written: {0:N2} MB"), p.WrittenBytes / (1024.0 * 1024.0));
            speedLabel.Text = string.Format(T("速率：{0:N0} 列/秒", "Speed: {0:N0} rows/s"), p.RowsPerSecond);
            elapsedLabel.Text = string.Format(T("耗時：{0:mm\\:ss}", "Elapsed: {0:mm\\:ss}"), p.Elapsed);
            etaLabel.Text = p.EstimatedRemaining.HasValue
                ? string.Format(T("預估剩餘：約 {0:mm\\:ss}", "ETA: approx. {0:mm\\:ss}"), p.EstimatedRemaining.Value)
                : "";

            if (!string.IsNullOrWhiteSpace(p.Message))
            {
                logList.Items.Add($"[{DateTime.Now:HH:mm:ss}] {p.Message}");
                if (logList.Items.Count > 1000) logList.Items.RemoveAt(0);
                logList.TopIndex = logList.Items.Count - 1;
            }
        }

        private void UpdateLiveTicker()
        {
            if (exporting && lastProgress != null)
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
