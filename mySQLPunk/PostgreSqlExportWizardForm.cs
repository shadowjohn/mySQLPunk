using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using mySQLPunk.lib;
using mySQLPunk.lib.PostgreSqlExport;
using PostgreSqlExportResult = mySQLPunk.lib.PostgreSqlExport.PostgreSqlExportResult;

namespace mySQLPunk
{
    internal sealed class PostgreSqlExportWizardForm : Form
    {
        private readonly IDatabase db;
        private readonly string databaseName;
        private readonly PostgreSqlSqlExportService service;
        private readonly Panel[] pages = new Panel[4];
        private readonly TextBox search = new TextBox(), targetPathBox = new TextBox();
        private readonly DataGridView tableGrid = new DataGridView();
        private readonly RadioButton schemaData = new RadioButton(), schemaOnly = new RadioButton(), dataOnly = new RadioButton();
        private readonly CheckBox repeatableRead = new CheckBox(), createPostgisExt = new CheckBox(), excludeSpatialMetadata = new CheckBox();
        private readonly CheckBox includeDrop = new CheckBox(), truncateBefore = new CheckBox();
        private readonly CheckBox includeViews = new CheckBox(), includeIndexes = new CheckBox(), includeComments = new CheckBox(), includeManifest = new CheckBox();
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

        private List<PostgreSqlTableExportItem> catalog = new List<PostgreSqlTableExportItem>();
        private CancellationTokenSource cancellation;
        private PostgreSqlExportProgress lastProgress;
        private bool busy, closing, exporting;
        private int step;

        internal PostgreSqlExportResult Result { get; private set; }

        private static string T(string zh, string en) => ReportText.Get(zh, en);

        internal PostgreSqlExportWizardForm(IDatabase db, string databaseName, PostgreSqlExportWizardOptions initialOptions = null)
        {
            this.db = db ?? throw new ArgumentNullException(nameof(db));
            this.databaseName = !string.IsNullOrWhiteSpace(databaseName) ? databaseName : throw new ArgumentException("Database name is required", nameof(databaseName));
            this.service = new PostgreSqlSqlExportService(db, databaseName);

            Name = "postgreSqlExportWizard";
            Text = T("匯出資料庫 · PostgreSQL (支援 PostGIS 空間幾何無損備份)", "Export database · PostgreSQL (with PostGIS spatial GIS support)");
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = UiKit.Body;
            ClientSize = new Size(1120, 740);
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
            header.Controls.Add(new Label { AutoSize = true, Text = T("PostgreSQL 資料庫邏輯傾印精靈", "PostgreSQL database logical dump wizard") + "   ·   " + databaseName, Font = UiKit.Title, Margin = new Padding(0, 4, 0, 8) });
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

            Shown += async (s, e) =>
            {
                await LoadCatalogAsync();
                ApplyInitialSelection(initialOptions);
                UpdateStep();
            };
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

        private void BuildPage0()
        {
            var p = pages[0];
            p.Padding = new Padding(8);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            p.Controls.Add(layout);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
            search.Width = 260;
            search.Margin = new Padding(0, 2, 8, 0);
            search.TextChanged += (s, e) => FilterGrid();
            var all = new Button { AutoSize = true, Text = T("全選", "Select all"), Margin = new Padding(0, 0, 6, 0) };
            var none = new Button { AutoSize = true, Text = T("全不選", "Clear all"), Margin = new Padding(0, 0, 6, 0) };
            var spatialOnly = new Button { AutoSize = true, Text = T("僅選 PostGIS 空間表 🌐", "Select PostGIS spatial tables 🌐"), Margin = new Padding(0, 0, 6, 0) };
            all.Click += (s, e) => SetAllSelection(true);
            none.Click += (s, e) => SetAllSelection(false);
            spatialOnly.Click += (s, e) => SetSpatialSelectionOnly();

            bar.Controls.Add(new Label { Text = T("搜尋物件:", "Filter objects:"), AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
            bar.Controls.Add(search);
            bar.Controls.Add(all);
            bar.Controls.Add(none);
            bar.Controls.Add(spatialOnly);
            layout.Controls.Add(bar, 0, 0);

            var tip = new Label
            {
                Text = T("💡 支援 PostGIS 幾何/地理欄位 (Point, MultiPolygon, 3D/4D)，將以無損十六進位 EWKB 匯出。預設排除龐大空間中繼表 (spatial_ref_sys) 防範還原主鍵衝突。",
                         "💡 Supports PostGIS geometry/geography (Point, MultiPolygon, 3D/4D) in lossless hex EWKB. Bulky spatial metadata (spatial_ref_sys) is excluded by default."),
                AutoSize = true,
                ForeColor = Color.DarkSlateBlue,
                Margin = new Padding(0, 0, 0, 8)
            };
            layout.Controls.Add(tip, 0, 1);

            tableGrid.Dock = DockStyle.Fill;
            tableGrid.AllowUserToAddRows = false;
            tableGrid.AllowUserToDeleteRows = false;
            tableGrid.RowHeadersVisible = false;
            tableGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            tableGrid.AutoGenerateColumns = false;
            tableGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Export", HeaderText = T("匯出", "Export"), Width = 60 });
            tableGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Schema", HeaderText = T("結構結構 (Schema)", "Schema"), Width = 110, ReadOnly = true });
            tableGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = T("物件名稱", "Name"), Width = 220, ReadOnly = true });
            tableGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Kind", HeaderText = T("類型", "Type"), Width = 80, ReadOnly = true });
            tableGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Spatial", HeaderText = T("🌐 空間特色 (PostGIS)", "🌐 Spatial (PostGIS)"), Width = 280, ReadOnly = true });
            tableGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Rows", HeaderText = T("估計列數", "Rows"), Width = 110, ReadOnly = true });
            tableGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Bytes", HeaderText = T("大小", "Size"), Width = 110, ReadOnly = true });
            tableGrid.CellValueChanged += (s, e) => UpdateSelectionCounts();
            tableGrid.CurrentCellDirtyStateChanged += (s, e) => { if (tableGrid.IsCurrentCellDirty) tableGrid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            layout.Controls.Add(tableGrid, 0, 2);

            selectionSummary.AutoSize = true;
            selectionSummary.Margin = new Padding(0, 8, 0, 0);
            layout.Controls.Add(selectionSummary, 0, 3);
        }

        private void BuildPage1(PostgreSqlExportWizardOptions opt)
        {
            var p = pages[1];
            p.Padding = new Padding(8);
            var layout = Stack();
            p.Controls.Add(layout);

            layout.Controls.Add(new Label { Text = T("匯出範圍與模式：", "Export scope and mode:"), Font = UiKit.Subtitle, AutoSize = true, Margin = new Padding(0, 4, 0, 4) });
            schemaData.Text = T("結構與資料 (完整邏輯 SQL 傾印備份)", "Schema and Data (Full logical SQL dump backup)");
            schemaData.Checked = true;
            schemaData.AutoSize = true;
            schemaOnly.Text = T("僅結構 (僅產出 DDL、索引、檢視與註解)", "Schema Only (DDL, indexes, views, and comments)");
            schemaOnly.AutoSize = true;
            dataOnly.Text = T("僅資料 (僅產出 INSERT 語句)", "Data Only (INSERT statements)");
            dataOnly.AutoSize = true;
            layout.Controls.Add(schemaData);
            layout.Controls.Add(schemaOnly);
            layout.Controls.Add(dataOnly);

            layout.Controls.Add(new Label { Text = T("輸出 SQL 檔案位置：", "Output SQL file destination:"), Font = UiKit.Subtitle, AutoSize = true, Margin = new Padding(0, 16, 0, 4) });
            var pathRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, 12) };
            targetPathBox.Width = 620;
            browseButton.Text = T("瀏覽...", "Browse...");
            browseButton.AutoSize = true;
            browseButton.Click += (s, e) => ChooseTargetFile();
            pathRow.Controls.Add(targetPathBox);
            pathRow.Controls.Add(browseButton);
            layout.Controls.Add(pathRow);

            layout.Controls.Add(new Label { Text = T("PostgreSQL / PostGIS 進階選項：", "PostgreSQL / PostGIS advanced options:"), Font = UiKit.Subtitle, AutoSize = true, Margin = new Padding(0, 8, 0, 4) });
            repeatableRead.Text = T("開啟 REPEATABLE READ 一致性交易快照 (保證匯出期間點對點資料一致)", "Enable REPEATABLE READ transaction snapshot (guarantees point-in-time consistency)");
            repeatableRead.Checked = true;
            repeatableRead.AutoSize = true;

            createPostgisExt.Text = T("於腳本開頭加入 CREATE EXTENSION IF NOT EXISTS postgis; (空間環境自動初始化)", "Include CREATE EXTENSION IF NOT EXISTS postgis; at script header");
            createPostgisExt.Checked = true;
            createPostgisExt.AutoSize = true;

            excludeSpatialMetadata.Text = T("排除龐大空間中繼表 (spatial_ref_sys，防範還原時主鍵衝突)", "Exclude bulky spatial metadata tables (spatial_ref_sys, avoids PK collision on restore)");
            excludeSpatialMetadata.Checked = true;
            excludeSpatialMetadata.AutoSize = true;
            excludeSpatialMetadata.CheckedChanged += (s, e) =>
            {
                foreach (var item in catalog)
                {
                    if (item.IsSpatialMetadata) item.Selected = !excludeSpatialMetadata.Checked;
                }
                FilterGrid();
            };

            includeDrop.Text = T("建立前先加入 DROP TABLE IF EXISTS ... CASCADE;", "Include DROP TABLE IF EXISTS ... CASCADE; before creation");
            includeDrop.AutoSize = true;

            truncateBefore.Text = T("插入前先加入 TRUNCATE TABLE ... CASCADE; (清空目標表)", "Include TRUNCATE TABLE ... CASCADE; before inserting data");
            truncateBefore.AutoSize = true;

            includeViews.Text = T("包含檢視表 (CREATE OR REPLACE VIEW)", "Include views (CREATE OR REPLACE VIEW)");
            includeViews.Checked = true;
            includeViews.AutoSize = true;

            includeIndexes.Text = T("包含索引 (包含 PostGIS GIST / SP-GIST 空間幾何索引)", "Include indexes (including PostGIS GIST / SP-GIST spatial indexes)");
            includeIndexes.Checked = true;
            includeIndexes.AutoSize = true;

            includeComments.Text = T("包含結構註解 (COMMENT ON TABLE / COLUMN)", "Include sidecar comments (COMMENT ON TABLE / COLUMN)");
            includeComments.Checked = true;
            includeComments.AutoSize = true;

            includeManifest.Text = T("附加 Native Manifest 清單與 SHA-256 核對碼 (作為還原檢驗依據)", "Append Native Manifest summary & SHA-256 checksum");
            includeManifest.Checked = true;
            includeManifest.AutoSize = true;

            layout.Controls.Add(repeatableRead);
            layout.Controls.Add(createPostgisExt);
            layout.Controls.Add(excludeSpatialMetadata);
            layout.Controls.Add(includeDrop);
            layout.Controls.Add(truncateBefore);
            layout.Controls.Add(includeViews);
            layout.Controls.Add(includeIndexes);
            layout.Controls.Add(includeComments);
            layout.Controls.Add(includeManifest);

            if (opt != null)
            {
                if (opt.Mode == PostgreSqlExportMode.SchemaOnly) schemaOnly.Checked = true;
                else if (opt.Mode == PostgreSqlExportMode.DataOnly) dataOnly.Checked = true;
                if (!string.IsNullOrWhiteSpace(opt.TargetPath)) targetPathBox.Text = opt.TargetPath;
            }

            if (string.IsNullOrWhiteSpace(targetPathBox.Text))
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                targetPathBox.Text = Path.Combine(desktop, databaseName + "_pg_dump_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".sql");
            }
        }

        private void BuildPage2()
        {
            var p = pages[2];
            p.Padding = new Padding(12);
            var layout = Stack();
            p.Controls.Add(layout);

            layout.Controls.Add(new Label { Text = T("請確認以下匯出設定：", "Please confirm export settings:"), Font = UiKit.Title, AutoSize = true, Margin = new Padding(0, 4, 0, 16) });
            confirmSummary.AutoSize = true;
            confirmSummary.Font = UiKit.Body;
            layout.Controls.Add(confirmSummary);
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
            currentObjectLabel.Text = T("準備開始匯出...", "Preparing export...");
            currentObjectLabel.Font = UiKit.Subtitle;
            currentObjectLabel.AutoSize = true;
            leftTop.Controls.Add(currentObjectLabel);

            overallBar.Dock = DockStyle.Top;
            overallBar.Height = 24;
            leftTop.Controls.Add(overallBar);

            var statRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 4, 0, 4) };
            percentLabel.Text = "0%";
            writtenLabel.Text = "0 KB";
            elapsedLabel.Text = "00:00:00";
            speedLabel.Text = "0 KB/s";
            statRow.Controls.Add(percentLabel);
            statRow.Controls.Add(new Label { Text = " · ", AutoSize = true });
            statRow.Controls.Add(writtenLabel);
            statRow.Controls.Add(new Label { Text = " · ", AutoSize = true });
            statRow.Controls.Add(elapsedLabel);
            statRow.Controls.Add(new Label { Text = " · ", AutoSize = true });
            statRow.Controls.Add(speedLabel);
            leftTop.Controls.Add(statRow);
            layout.Controls.Add(leftTop, 0, 0);

            var rightBox = Stack();
            punky.Size = new Size(160, 160);
            rightBox.Controls.Add(punky);

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

            logList.Dock = DockStyle.Fill;
            logList.Font = new Font("Consolas", 9f);
            layout.Controls.Add(logList, 0, 2);
            layout.SetColumnSpan(logList, 2);

            resultText.AutoSize = true;
            resultText.Font = UiKit.Subtitle;
            resultText.ForeColor = Color.DarkGreen;
            resultText.Margin = new Padding(0, 8, 0, 8);
            layout.Controls.Add(resultText, 0, 3);
            layout.SetColumnSpan(resultText, 2);

            var finishActions = new FlowLayoutPanel { AutoSize = true };
            openFileButton.Text = T("開啟輸出檔案", "Open export file");
            openFileButton.AutoSize = true;
            openFileButton.Visible = false;
            openFileButton.Click += (s, e) =>
            {
                if (File.Exists(targetPathBox.Text)) Process.Start("explorer.exe", "/select,\"" + targetPathBox.Text + "\"");
            };

            openFolderButton.Text = T("開啟所在資料夾", "Open containing folder");
            openFolderButton.AutoSize = true;
            openFolderButton.Visible = false;
            openFolderButton.Click += (s, e) =>
            {
                string dir = Path.GetDirectoryName(targetPathBox.Text);
                if (Directory.Exists(dir)) Process.Start("explorer.exe", dir);
            };

            finishActions.Controls.Add(openFileButton);
            finishActions.Controls.Add(openFolderButton);
            layout.Controls.Add(finishActions, 0, 4);
            layout.SetColumnSpan(finishActions, 2);
        }

        private async Task LoadCatalogAsync()
        {
            try
            {
                statusLabel.Text = T("正在探測 PostgreSQL 物件結構與 PostGIS 空間中繼資料...", "Scanning PostgreSQL objects and PostGIS metadata...");
                catalog = await service.GetTableCatalogAsync().ConfigureAwait(true);
                FilterGrid();
                UpdateSelectionCounts();
                statusLabel.Text = T("物件載入完成。", "Objects loaded.");
            }
            catch (Exception ex)
            {
                statusLabel.Text = T("載入失敗：", "Failed to load: ") + ex.Message;
            }
        }

        private void FilterGrid()
        {
            string q = search.Text.Trim();
            tableGrid.Rows.Clear();

            foreach (var item in catalog)
            {
                if (!string.IsNullOrWhiteSpace(q))
                {
                    bool match = item.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 item.Schema.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 item.SpatialFeatureDescription.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!match) continue;
                }

                int idx = tableGrid.Rows.Add(
                    item.Selected,
                    item.Schema,
                    item.Name,
                    item.IsView ? "View" : "Table",
                    item.IsSpatial ? "🌐 " + item.SpatialFeatureDescription : "-",
                    string.Format("{0:N0}", item.RowCount),
                    FormatBytes(item.EstimatedBytes)
                );
                tableGrid.Rows[idx].Tag = item;
            }
            UpdateSelectionCounts();
        }

        private void SetAllSelection(bool selected)
        {
            foreach (var item in catalog)
            {
                item.Selected = selected;
            }
            FilterGrid();
        }

        private void SetSpatialSelectionOnly()
        {
            foreach (var item in catalog)
            {
                item.Selected = item.IsSpatial && !item.IsSpatialMetadata;
            }
            FilterGrid();
        }

        private void ApplyInitialSelection(PostgreSqlExportWizardOptions initialOptions)
        {
            if (initialOptions == null || initialOptions.SelectedTables.Count == 0) return;
            var set = new HashSet<string>(initialOptions.SelectedTables, StringComparer.OrdinalIgnoreCase);
            foreach (var item in catalog)
            {
                item.Selected = set.Contains(item.FullQualifiedName) || set.Contains(item.DisplayName) || set.Contains(item.Name);
            }
            FilterGrid();
        }

        private void UpdateSelectionCounts()
        {
            foreach (DataGridViewRow row in tableGrid.Rows)
            {
                var item = row.Tag as PostgreSqlTableExportItem;
                if (item != null && row.Cells["Export"].Value is bool b)
                {
                    item.Selected = b;
                }
            }

            int selectedCount = catalog.Count(i => i.Selected);
            int spatialCount = catalog.Count(i => i.Selected && i.IsSpatial);
            long totalRows = catalog.Where(i => i.Selected).Sum(i => i.RowCount);
            selectionSummary.Text = string.Format(T("已選取 {0} / {1} 個物件 (含 {2} 張 PostGIS 空間幾何表，預估共 {3:N0} 列資料)",
                                                    "Selected {0} / {1} objects ({2} PostGIS spatial tables, ~{3:N0} total rows)"),
                                                 selectedCount, catalog.Count, spatialCount, totalRows);
        }

        private void Navigate(int nextStep)
        {
            if (nextStep < 0 || nextStep > 3) return;

            if (step == 0 && nextStep == 1)
            {
                UpdateSelectionCounts();
                if (!catalog.Any(i => i.Selected))
                {
                    MessageBox.Show(this, T("請至少選取一個欲匯出的物件！", "Please select at least one object to export!"), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            else if (step == 1 && nextStep == 2)
            {
                if (string.IsNullOrWhiteSpace(targetPathBox.Text))
                {
                    MessageBox.Show(this, T("請指定輸出檔案路徑！", "Please specify output target file path!"), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                UpdateConfirmation();
            }
            else if (step == 2 && nextStep == 3)
            {
                StartExport();
            }

            step = nextStep;
            UpdateStep();
        }

        private void UpdateConfirmation()
        {
            int selectedCount = catalog.Count(i => i.Selected);
            int spatialCount = catalog.Count(i => i.Selected && i.IsSpatial);
            long totalRows = catalog.Where(i => i.Selected).Sum(i => i.RowCount);
            long totalBytes = catalog.Where(i => i.Selected).Sum(i => i.EstimatedBytes);

            string modeStr = schemaData.Checked ? T("結構與資料 (完整備份)", "Schema and Data (Full backup)") :
                             schemaOnly.Checked ? T("僅結構 (DDL)", "Schema Only (DDL)") :
                                                  T("僅資料 (INSERT)", "Data Only (INSERT)");

            var sb = new StringBuilder();
            sb.AppendLine(string.Format("• {0}: {1}", T("目標資料庫", "Target Database"), databaseName));
            sb.AppendLine(string.Format("• {0}: {1}", T("匯出模式", "Export Mode"), modeStr));
            sb.AppendLine(string.Format("• {0}: {1}", T("輸出路徑", "Destination Path"), targetPathBox.Text));
            sb.AppendLine(string.Format("• {0}: {1} {2}", T("選取物件", "Selected Objects"), selectedCount, T("個", "items")));
            sb.AppendLine(string.Format("• {0}: {1} {2}", T("PostGIS 空間幾何表", "PostGIS Spatial Tables"), spatialCount, T("個 (零失真 EWKB Hex 傾印)", "items (lossless EWKB Hex)")));
            sb.AppendLine(string.Format("• {0}: ~{1:N0} {2}", T("預估資料列數", "Estimated Rows"), totalRows, T("列", "rows")));
            sb.AppendLine(string.Format("• {0}: ~{1}", T("預估物件體積", "Estimated Size"), FormatBytes(totalBytes)));
            sb.AppendLine(string.Format("• {0}: {1}", T("一致性快照", "Consistency Snapshot"), repeatableRead.Checked ? "REPEATABLE READ" : T("無", "None")));
            sb.AppendLine(string.Format("• {0}: {1}", T("PostGIS 擴充模組開關", "CREATE EXTENSION postgis"), createPostgisExt.Checked ? T("是 (自動初始化)", "Yes (auto initialize)") : T("否", "No")));
            sb.AppendLine(string.Format("• {0}: {1}", T("排除空間中繼表", "Exclude Spatial Metadata"), excludeSpatialMetadata.Checked ? T("是 (spatial_ref_sys 已排除)", "Yes (spatial_ref_sys excluded)") : T("否", "No")));

            confirmSummary.Text = sb.ToString();
        }

        private void UpdateStep()
        {
            for (int i = 0; i < 4; i++) pages[i].Visible = (i == step);

            string[] stepTitles = new[]
            {
                T("步驟 1/4：選取欲匯出的 PostgreSQL 物件與空間幾何表", "Step 1/4: Select PostgreSQL objects and spatial tables"),
                T("步驟 2/4：輸出路徑與 PostGIS 空間備份進階設定", "Step 2/4: Output path and PostGIS spatial backup settings"),
                T("步驟 3/4：確認匯出設定與物件清單摘要", "Step 3/4: Confirm export settings and object summary"),
                T("步驟 4/4：執行串流傾印匯出進度", "Step 4/4: Streaming export execution progress")
            };

            stepsLabel.Text = stepTitles[step];
            backButton.Enabled = (step > 0 && step < 3 && !busy);
            nextButton.Enabled = (!busy || step == 3);
            nextButton.Text = (step == 2) ? T("開始匯出 🚀", "Start Export 🚀") : (step == 3 ? T("完成", "Finished") : T("下一步 >", "Next >"));
            cancelButton.Enabled = !busy || exporting;

            if (step == 3 && !exporting)
            {
                nextButton.Enabled = true;
                cancelButton.Enabled = false;
            }
        }

        private async void StartExport()
        {
            exporting = true;
            busy = true;
            backButton.Enabled = false;
            nextButton.Enabled = false;
            cancellation = new CancellationTokenSource();
            clock.Restart();
            timer.Start();

            punky.BeginExport();

            var opt = new PostgreSqlExportWizardOptions
            {
                Mode = schemaData.Checked ? PostgreSqlExportMode.SchemaAndData : (schemaOnly.Checked ? PostgreSqlExportMode.SchemaOnly : PostgreSqlExportMode.DataOnly),
                TargetPath = targetPathBox.Text.Trim(),
                UseTransactionSnapshot = repeatableRead.Checked,
                IncludeCreateExtensionPostgis = createPostgisExt.Checked,
                ExcludeSpatialMetadataTables = excludeSpatialMetadata.Checked,
                DropTableIfExists = includeDrop.Checked,
                TruncateBeforeInsert = truncateBefore.Checked,
                IncludeViews = includeViews.Checked,
                IncludeIndexes = includeIndexes.Checked,
                IncludeComments = includeComments.Checked,
                IncludeManifest = includeManifest.Checked
            };

            foreach (var item in catalog)
            {
                if (item.Selected) opt.SelectedTables.Add(item.FullQualifiedName);
            }

            var progressReporter = new Progress<PostgreSqlExportProgress>(p =>
            {
                lastProgress = p;
                currentObjectLabel.Text = p.Message;
                percentLabel.Text = string.Format("{0:0.0}%", p.PercentComplete);
                writtenLabel.Text = FormatBytes(p.BytesExported);
                overallBar.Value = Math.Min(100, (int)p.PercentComplete);
                if (!string.IsNullOrWhiteSpace(p.Message))
                {
                    logList.Items.Add(string.Format("[{0:HH:mm:ss}] {1}", DateTime.Now, p.Message));
                    logList.TopIndex = logList.Items.Count - 1;
                }
            });

            try
            {
                Result = await service.ExportAsync(opt, progressReporter, cancellation.Token).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Result = new PostgreSqlExportResult { Success = false, ErrorMessage = ex.Message };
            }
            finally
            {
                exporting = false;
                busy = false;
                clock.Stop();
                timer.Stop();
                punky.EndExport();

                if (Result != null && Result.Success)
                {
                    resultText.Text = string.Format(T("✅ 傾印成功！共匯出 {0} 個物件，{1:N0} 列資料，輸出至 {2} (耗時 {3:hh\\:mm\\:ss\\.fff})",
                                                      "✅ Dump succeeded! Exported {0} objects, {1:N0} rows to {2} (took {3:hh\\:mm\\:ss\\.fff})"),
                                                    Result.TableCount + Result.ViewCount, Result.TotalRows, Result.TargetPath, Result.Elapsed);
                    resultText.ForeColor = Color.DarkGreen;
                    openFileButton.Visible = true;
                    openFolderButton.Visible = true;
                }
                else
                {
                    resultText.Text = T("❌ 匯出未完成：", "❌ Export failed: ") + (Result?.ErrorMessage ?? "Unknown error");
                    resultText.ForeColor = Color.Crimson;
                }

                UpdateStep();
            }
        }

        private void UpdateLiveTicker()
        {
            if (!clock.IsRunning) return;
            var el = clock.Elapsed;
            elapsedLabel.Text = string.Format("{0:00}:{1:00}:{2:00}", (int)el.TotalHours, el.Minutes, el.Seconds);

            if (lastProgress != null && el.TotalSeconds > 0)
            {
                double bytesPerSec = lastProgress.BytesExported / el.TotalSeconds;
                speedLabel.Text = FormatBytes((long)bytesPerSec) + "/s";
            }
        }

        private void ChooseTargetFile()
        {
            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = T("SQL 腳本檔案 (*.sql)|*.sql|所有檔案 (*.*)|*.*", "SQL Script Files (*.sql)|*.sql|All Files (*.*)|*.*");
                dialog.FileName = Path.GetFileName(targetPathBox.Text);
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    targetPathBox.Text = dialog.FileName;
                }
            }
        }

        private void HandleCancel()
        {
            if (busy && exporting)
            {
                if (MessageBox.Show(this, T("匯出正在執行中，確定要中途取消嗎？", "Export is running, are you sure to cancel?"), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
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
