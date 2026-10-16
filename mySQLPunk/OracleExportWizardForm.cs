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
using mySQLPunk.lib.OracleExport;
using OracleExportResult = mySQLPunk.lib.OracleExport.OracleExportResult;

namespace mySQLPunk
{
    internal sealed class OracleExportWizardForm : Form
    {
        private readonly IDatabase db;
        private readonly string databaseName; // Owner / Schema
        private readonly OracleSqlExportService service;
        private readonly List<string> initialSelection = new List<string>();
        private readonly Panel[] pages = new Panel[4];
        private readonly TextBox search = new TextBox(), targetPathBox = new TextBox();
        private readonly DataGridView tableGrid = new DataGridView();
        private readonly RadioButton schemaData = new RadioButton(), schemaOnly = new RadioButton(), dataOnly = new RadioButton();
        private readonly CheckBox readOnlySnapshot = new CheckBox(), excludeShadowTables = new CheckBox(), includeSpatialMetadata = new CheckBox(), includeSpatialIndexes = new CheckBox();
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

        private List<OracleTableExportItem> catalog = new List<OracleTableExportItem>();
        private CancellationTokenSource cancellation;
        private OracleExportProgress lastProgress;
        private bool busy, closing, exporting;
        private int step;

        internal OracleExportResult Result { get; private set; }

        private static string T(string zh, string en) => ReportText.Get(zh, en);

        internal OracleExportWizardForm(IDatabase db, string databaseName, OracleExportWizardOptions initialOptions = null)
        {
            this.db = db ?? throw new ArgumentNullException(nameof(db));
            this.databaseName = !string.IsNullOrWhiteSpace(databaseName) ? databaseName : "USER";
            this.service = new OracleSqlExportService(db, databaseName);
            if (initialOptions != null) initialSelection.AddRange(initialOptions.SelectedTables);

            Name = "oracleExportWizard";
            Text = T("匯出資料庫 · Oracle (支援有界 SDO_GEOMETRY 備份)", "Export database · Oracle (with SDO_GEOMETRY spatial support)");
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
            header.Controls.Add(new Label { AutoSize = true, Text = T("Oracle 資料庫邏輯傾印精靈", "Oracle database logical dump wizard") + "   ·   " + databaseName, Font = UiKit.Title, Margin = new Padding(0, 4, 0, 8) });
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
                if (exporting || busy)
                {
                    e.Cancel = true;
                    if (closing) return;
                    if (MessageBox.Show(T("正在匯出 Oracle 資料庫中，確定要中止嗎？", "Export is currently running. Cancel it?"), T("確認", "Confirm"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
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

            Shown += async (s, e) => await LoadCatalogAsync();
            SetStep(0);
        }

        private static FlowLayoutPanel Stack() => new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        private static FlowLayoutPanel Row() => new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Margin = new Padding(0, 0, 0, 8) };

        private void BuildPage0()
        {
            var p = pages[0];
            p.Padding = new Padding(0, 4, 0, 0);

            var filterRow = Row();
            search.Width = 260;
            search.TextChanged += (s, e) => ApplyFilter();
            var allBtn = new Button { Text = T("全選", "Select all"), AutoSize = true };
            allBtn.Click += (s, e) => SetAllSelection(true);
            var clearBtn = new Button { Text = T("全不選", "Clear all"), AutoSize = true };
            clearBtn.Click += (s, e) => SetAllSelection(false);

            filterRow.Controls.Add(new Label { Text = T("搜尋：", "Search:"), AutoSize = true, Margin = new Padding(0, 5, 4, 0) });
            filterRow.Controls.Add(search);
            filterRow.Controls.Add(allBtn);
            filterRow.Controls.Add(clearBtn);

            tableGrid.Dock = DockStyle.Fill;
            tableGrid.AllowUserToAddRows = false;
            tableGrid.AllowUserToDeleteRows = false;
            tableGrid.RowHeadersVisible = false;
            tableGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            tableGrid.MultiSelect = false;
            tableGrid.AutoGenerateColumns = false;
            tableGrid.BackgroundColor = SystemColors.Window;

            tableGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "sel", HeaderText = T("勾選", "Export"), Width = 60, DataPropertyName = "Selected" });
            tableGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "owner", HeaderText = T("綱要 (Owner)", "Owner"), Width = 110, ReadOnly = true });
            tableGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = T("物件名稱", "Object name"), Width = 230, ReadOnly = true });
            tableGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "type", HeaderText = T("類型", "Type"), Width = 70, ReadOnly = true });
            tableGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "spatial", HeaderText = T("空間特性 (Oracle Spatial)", "Spatial Features"), Width = 260, ReadOnly = true });
            tableGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "rows", HeaderText = T("列數預估", "Rows (est.)"), Width = 110, ReadOnly = true });
            tableGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "bytes", HeaderText = T("大小 (Bytes)", "Bytes"), Width = 110, ReadOnly = true });

            tableGrid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (tableGrid.IsCurrentCellDirty) tableGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            tableGrid.CellValueChanged += (s, e) =>
            {
                if (e.RowIndex < 0 || e.ColumnIndex != 0) return;
                var row = tableGrid.Rows[e.RowIndex];
                var item = row.Tag as OracleTableExportItem;
                if (item != null) item.Selected = Convert.ToBoolean(row.Cells[0].Value ?? false);
                UpdateSelectionSummary();
            };
            tableGrid.CellContentClick += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.ColumnIndex == 0)
                {
                    tableGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
                    UpdateSelectionSummary();
                }
            };

            var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            main.Controls.Add(filterRow, 0, 0);
            main.Controls.Add(tableGrid, 0, 1);
            selectionSummary.AutoSize = true;
            selectionSummary.Margin = new Padding(0, 6, 0, 0);
            main.Controls.Add(selectionSummary, 0, 2);

            p.Controls.Add(main);
        }

        private void BuildPage1(OracleExportWizardOptions opt)
        {
            var p = pages[1];
            var host = Stack();

            var pathRow = Row();
            targetPathBox.Width = 560;
            browseButton.Text = T("瀏覽...", "Browse...");
            browseButton.AutoSize = true;
            browseButton.Click += (s, e) =>
            {
                using (var sfd = new SaveFileDialog())
                {
                    sfd.Filter = "SQL Files (*.sql)|*.sql|All Files (*.*)|*.*";
                    sfd.FileName = string.Format("oracle_{0}_dump_{1:yyyyMMdd_HHmmss}.sql", databaseName, DateTime.Now);
                    if (sfd.ShowDialog(this) == DialogResult.OK)
                    {
                        targetPathBox.Text = sfd.FileName;
                    }
                }
            };
            pathRow.Controls.Add(new Label { Text = T("匯出檔案路徑：", "Target file path:"), AutoSize = true, Margin = new Padding(0, 5, 4, 0) });
            pathRow.Controls.Add(targetPathBox);
            pathRow.Controls.Add(browseButton);
            host.Controls.Add(pathRow);

            var modeBox = new GroupBox { Text = T("匯出模式", "Export mode"), AutoSize = true, Padding = new Padding(12), Margin = new Padding(0, 4, 0, 8) };
            var modeFlow = Stack();
            schemaData.Text = T("結構與資料 (Schema and Data)", "Schema and Data");
            schemaData.Checked = true;
            schemaData.AutoSize = true;
            dataOnly.Text = T("僅資料 (Data only)", "Data only");
            dataOnly.AutoSize = true;
            schemaOnly.Text = T("僅結構 (Schema only)", "Schema only");
            schemaOnly.AutoSize = true;
            modeFlow.Controls.Add(schemaData);
            modeFlow.Controls.Add(dataOnly);
            modeFlow.Controls.Add(schemaOnly);
            modeBox.Controls.Add(modeFlow);
            host.Controls.Add(modeBox);

            var spatialBox = new GroupBox { Text = T("Oracle Spatial 崩琦 GIS 特徵與交易防護", "Oracle Spatial GIS & Transaction Settings"), AutoSize = true, Padding = new Padding(12), Margin = new Padding(0, 4, 0, 8) };
            var spatialFlow = Stack();
            readOnlySnapshot.Text = T("使用交易唯讀快照隔離 (SET TRANSACTION READ ONLY) 防範資料與外鍵不一致", "Use transaction READ ONLY snapshot isolation to guarantee foreign key consistency");
            readOnlySnapshot.Checked = true;
            readOnlySnapshot.AutoSize = true;

            excludeShadowTables.Text = T("排除 Oracle Spatial 內部 R-Tree 空間索引影子表 (MDRT_*, MDXT_*, MDRS_*)", "Exclude Oracle Spatial internal R-Tree index shadow tables (MDRT_*, MDXT_*)");
            excludeShadowTables.Checked = true;
            excludeShadowTables.AutoSize = true;

            includeSpatialMetadata.Text = T("產生空間中繼字典註冊語句 (INSERT INTO USER_SDO_GEOM_METADATA)", "Generate spatial metadata registration (INSERT INTO USER_SDO_GEOM_METADATA)");
            includeSpatialMetadata.Checked = true;
            includeSpatialMetadata.AutoSize = true;

            includeSpatialIndexes.Text = T("導出 Oracle Spatial 空間網格幾何索引 (INDEXTYPE IS MDSYS.SPATIAL_INDEX)", "Export Oracle Spatial domain indexes (INDEXTYPE IS MDSYS.SPATIAL_INDEX)");
            includeSpatialIndexes.Checked = true;
            includeSpatialIndexes.AutoSize = true;

            spatialFlow.Controls.Add(readOnlySnapshot);
            spatialFlow.Controls.Add(excludeShadowTables);
            spatialFlow.Controls.Add(includeSpatialMetadata);
            spatialFlow.Controls.Add(includeSpatialIndexes);
            spatialBox.Controls.Add(spatialFlow);
            host.Controls.Add(spatialBox);

            var ddlBox = new GroupBox { Text = T("進階選項與腳本 Manifest", "Advanced options & Script Manifest"), AutoSize = true, Padding = new Padding(12), Margin = new Padding(0, 4, 0, 8) };
            var ddlFlow = Stack();
            includeDrop.Text = T("在建表前加入 DROP TABLE CASCADE CONSTRAINTS", "Add DROP TABLE CASCADE CONSTRAINTS before CREATE");
            includeDrop.AutoSize = true;
            truncateBefore.Text = T("在插入前加入 TRUNCATE TABLE", "Add TRUNCATE TABLE before INSERT");
            truncateBefore.AutoSize = true;
            includeViews.Text = T("包含檢視 (Views)", "Include views");
            includeViews.Checked = true;
            includeViews.AutoSize = true;
            includeIndexes.Text = T("包含常規索引 (Indexes)", "Include regular indexes");
            includeIndexes.Checked = true;
            includeIndexes.AutoSize = true;
            includeComments.Text = T("包含資料表與欄位註解 (Comments)", "Include comments");
            includeComments.Checked = true;
            includeComments.AutoSize = true;
            includeManifest.Text = T("在檔尾附帶 Native Manifest 清單與 SHA-256 核對碼", "Append Native Manifest summary & SHA-256 footer");
            includeManifest.Checked = true;
            includeManifest.AutoSize = true;

            ddlFlow.Controls.Add(includeDrop);
            ddlFlow.Controls.Add(truncateBefore);
            ddlFlow.Controls.Add(includeViews);
            ddlFlow.Controls.Add(includeIndexes);
            ddlFlow.Controls.Add(includeComments);
            ddlFlow.Controls.Add(includeManifest);
            ddlBox.Controls.Add(ddlFlow);
            host.Controls.Add(ddlBox);

            if (opt != null)
            {
                if (opt.Mode == OracleExportMode.DataOnly) dataOnly.Checked = true;
                else if (opt.Mode == OracleExportMode.SchemaOnly) schemaOnly.Checked = true;
                if (!string.IsNullOrWhiteSpace(opt.TargetPath)) targetPathBox.Text = opt.TargetPath;
                readOnlySnapshot.Checked = opt.UseTransactionSnapshot;
                excludeShadowTables.Checked = opt.ExcludeSpatialIndexInternalTables;
                includeSpatialMetadata.Checked = opt.IncludeSpatialMetadata;
                includeSpatialIndexes.Checked = opt.IncludeSpatialIndexes;
                includeDrop.Checked = opt.DropTableIfExists;
                truncateBefore.Checked = opt.TruncateBeforeInsert;
                includeViews.Checked = opt.IncludeViews;
                includeIndexes.Checked = opt.IncludeIndexes;
                includeComments.Checked = opt.IncludeComments;
                includeManifest.Checked = opt.IncludeManifest;
            }
            if (string.IsNullOrWhiteSpace(targetPathBox.Text))
            {
                targetPathBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    string.Format("oracle_{0}_dump_{1:yyyyMMdd_HHmmss}.sql", databaseName, DateTime.Now));
            }

            host.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(900, 0), Text = T("支援 AL32UTF8；每列值最多 8 MiB／SQL 區塊 28 MiB。BLOB／CLOB 分段；identity、NCLOB、LRS／曲線／複合幾何等明確拒絕。唯讀快照需非 SYS 帳號；空間 metadata 還原帳號須等於 owner。FK 父表必須一併選取，循環 FK／外部 sequence、function 等相依尚不支援。", "AL32UTF8 only; row values <=8 MiB / SQL block <=28 MiB. BLOB/CLOB chunks supported; identity, NCLOB, LRS/curves/compound geometry rejected. Non-SYS snapshot; metadata restore session user must equal owner. Include all FK parents; cyclic FKs/external sequences/functions are unsupported.") });
            p.Controls.Add(host);
        }

        private void BuildPage2()
        {
            var p = pages[2];
            var host = Stack();
            host.Controls.Add(new Label { Text = T("請確認即將執行的 Oracle 邏輯傾印清單與設定：", "Confirm export settings and objects:"), AutoSize = true, Font = UiKit.Subtitle, Margin = new Padding(0, 0, 0, 12) });
            confirmSummary.AutoSize = true;
            confirmSummary.Font = UiKit.Body;
            confirmSummary.Margin = new Padding(12, 0, 0, 0);
            host.Controls.Add(confirmSummary);
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
            mascotHint.Text = T("Punky 看板娘正在為您嚴格把關空間座標與傾印串流...", "Punky is streaming Oracle data with spatial integrity...");
            mascotPane.Controls.Add(mascotHint);
            root.Controls.Add(mascotPane, 0, 0);
            root.SetRowSpan(mascotPane, 3);

            var statStack = Stack();
            currentObjectLabel.AutoSize = true;
            currentObjectLabel.Font = UiKit.Subtitle;
            statStack.Controls.Add(currentObjectLabel);

            overallBar.Height = 24;
            overallBar.Dock = DockStyle.Top;
            overallBar.Margin = new Padding(0, 8, 0, 8);
            statStack.Controls.Add(overallBar);

            var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 4, Margin = new Padding(0, 4, 0, 8) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

            percentLabel.AutoSize = true; grid.Controls.Add(percentLabel, 0, 0);
            writtenLabel.AutoSize = true; grid.Controls.Add(writtenLabel, 1, 0);
            speedLabel.AutoSize = true; grid.Controls.Add(speedLabel, 2, 0);
            elapsedLabel.AutoSize = true; grid.Controls.Add(elapsedLabel, 3, 0);
            statStack.Controls.Add(grid);

            root.Controls.Add(statStack, 1, 0);

            var buttonRow = Row();
            openFileButton.Text = T("開啟匯出檔", "Open file");
            openFileButton.AutoSize = true;
            openFileButton.Visible = false;
            openFileButton.Click += (s, e) =>
            {
                if (File.Exists(targetPathBox.Text)) Process.Start(new ProcessStartInfo(targetPathBox.Text) { UseShellExecute = true });
            };
            openFolderButton.Text = T("開啟目錄", "Open folder");
            openFolderButton.AutoSize = true;
            openFolderButton.Visible = false;
            openFolderButton.Click += (s, e) =>
            {
                string dir = Path.GetDirectoryName(targetPathBox.Text);
                if (Directory.Exists(dir)) Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            };
            resultText.AutoSize = true;
            resultText.Margin = new Padding(0, 4, 12, 0);
            buttonRow.Controls.Add(resultText);
            buttonRow.Controls.Add(openFileButton);
            buttonRow.Controls.Add(openFolderButton);
            root.Controls.Add(buttonRow, 1, 1);

            logList.Dock = DockStyle.Fill;
            logList.Font = new Font("Consolas", 9f);
            root.Controls.Add(logList, 1, 2);

            p.Controls.Add(root);
        }

        private async Task LoadCatalogAsync()
        {
            if (busy) return;
            busy = true;
            cancellation = new CancellationTokenSource();
            statusLabel.Text = T("正在探測 Oracle 資料庫物件與空間幾何欄位...", "Discovering Oracle tables and spatial features...");
            try
            {
                catalog = await service.GetTableCatalogAsync(cancellation.Token).ConfigureAwait(true);
                if (initialSelection.Count > 0)
                {
                    var selected = new HashSet<string>(initialSelection.Select(name => mySQLPunk.lib.OracleImport.OracleImportRunner.ResolveManifestTarget(name, databaseName)), StringComparer.Ordinal);
                    foreach (var item in catalog) item.Selected = selected.Contains(item.FullQualifiedName);
                }
                ApplyFilter();
                statusLabel.Text = string.Format(T("探測完成：共找到 {0} 個資料表與檢視。", "Catalog loaded: {0} tables/views found."), catalog.Count);
            }
            catch (Exception ex)
            {
                statusLabel.Text = T("探測失敗: ", "Discovery failed: ") + ex.Message;
            }
            finally
            {
                busy = false;
                cancellation?.Dispose();
                cancellation = null;
                if (closing && !IsDisposed) BeginInvoke(new Action(Close));
            }
        }

        private void ApplyFilter()
        {
            string q = search.Text.Trim();
            tableGrid.Rows.Clear();

            foreach (var item in catalog)
            {
                if (!string.IsNullOrWhiteSpace(q))
                {
                    if (item.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                        item.Owner.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                        item.SpatialFeatureDescription.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                }

                int rowIdx = tableGrid.Rows.Add(
                    item.Selected,
                    item.Owner,
                    item.Name,
                    item.IsView ? "VIEW" : "TABLE",
                    item.IsSpatial ? "⚡ " + item.SpatialFeatureDescription : (item.IsSpatialMetadata ? "⚙️ Spatial Shadow" : "-"),
                    item.IsView ? "-" : item.RowCount.ToString("N0"),
                    item.IsView ? "-" : item.EstimatedBytes.ToString("N0")
                );

                tableGrid.Rows[rowIdx].Tag = item;
                if (item.IsSpatial)
                {
                    tableGrid.Rows[rowIdx].DefaultCellStyle.BackColor = Color.FromArgb(240, 248, 255);
                }
            }

            UpdateSelectionSummary();
        }

        private void SetAllSelection(bool select)
        {
            foreach (DataGridViewRow row in tableGrid.Rows)
            {
                if (row.Tag is OracleTableExportItem item)
                {
                    if (item.IsSpatialMetadata && select && excludeShadowTables.Checked)
                    {
                        // 影子表維持排除
                        continue;
                    }
                    item.Selected = select;
                    row.Cells[0].Value = select;
                }
            }
            UpdateSelectionSummary();
        }

        private void UpdateSelectionSummary()
        {
            int tables = 0, views = 0, spatial = 0;
            long rows = 0;

            foreach (var item in catalog)
            {
                if (item.Selected)
                {
                    if (item.IsView) views++;
                    else { tables++; rows += item.RowCount; }
                    if (item.IsSpatial) spatial++;
                }
            }

            selectionSummary.Text = string.Format(
                T("已選取：{0} 張資料表 ({1:N0} 列)，{2} 個檢視。空間幾何物件：{3} 個。",
                  "Selected: {0} tables ({1:N0} rows), {2} views. Spatial objects: {3}."),
                tables, rows, views, spatial);
        }

        private void SetStep(int s)
        {
            step = s;
            for (int i = 0; i < 4; i++) pages[i].Visible = (i == step);

            backButton.Enabled = (step > 0 && step < 3);
            cancelButton.Enabled = (step < 3);

            if (step == 0)
            {
                stepsLabel.Text = T("步驟 1/4：選取要匯出的 Oracle 資料表與檢視", "Step 1/4: Select Oracle tables and views to export");
                nextButton.Text = T("下一步 (設定選項) ▶", "Next (Options) ▶");
            }
            else if (step == 1)
            {
                stepsLabel.Text = T("步驟 2/4：指定匯出檔案與 Oracle Spatial / 快照選項", "Step 2/4: Set export options and Oracle Spatial settings");
                nextButton.Text = T("下一步 (確認清單) ▶", "Next (Review) ▶");
            }
            else if (step == 2)
            {
                stepsLabel.Text = T("步驟 3/4：確認匯出設定與即將傾印物件清單", "Step 3/4: Review export settings and objects");
                nextButton.Text = T("開始匯出 🚀", "Start Export 🚀");
                UpdateConfirmSummary();
            }
            else if (step == 3)
            {
                stepsLabel.Text = T("步驟 4/4：正在串流匯出 Oracle 資料庫...", "Step 4/4: Exporting Oracle database streaming...");
                nextButton.Text = T("完成", "Done");
                nextButton.Enabled = false;
                backButton.Enabled = false;
            }
        }

        private void UpdateConfirmSummary()
        {
            var sb = new StringBuilder();
            sb.AppendLine("• " + T("目標輸出路徑: ", "Target path: ") + targetPathBox.Text);
            sb.AppendLine("• " + T("匯出模式: ", "Export mode: ") + (schemaData.Checked ? "結構與資料 (Schema and Data)" : (dataOnly.Checked ? "僅資料 (Data only)" : "僅結構 (Schema only)")));
            sb.AppendLine("• " + T("交易快照隔離: ", "Snapshot: ") + (readOnlySnapshot.Checked ? "SET TRANSACTION READ ONLY (一致性快照)" : "無 (不開啟快照)"));
            sb.AppendLine("• " + T("空間中繼註冊: ", "Spatial metadata: ") + (includeSpatialMetadata.Checked ? "是 (USER_SDO_GEOM_METADATA)" : "否"));
            sb.AppendLine("• " + T("空間網格索引: ", "Spatial index: ") + (includeSpatialIndexes.Checked ? "是 (MDSYS.SPATIAL_INDEX)" : "否"));
            sb.AppendLine("• " + T("Native Manifest: ", "Native Manifest: ") + (includeManifest.Checked ? "包含 SHA-256 與逐表列數" : "不包含"));
            sb.AppendLine();
            sb.AppendLine(selectionSummary.Text);

            confirmSummary.Text = sb.ToString();
        }

        private void MoveStep(int delta)
        {
            int n = step + delta;
            if (n >= 0 && n < 4) SetStep(n);
        }

        private async Task OnNextAsync()
        {
            if (step == 0)
            {
                int count = catalog.Count(i => i.Selected);
                if (count == 0)
                {
                    MessageBox.Show(T("請至少選取一個資料表或檢視！", "Please select at least one table or view!"), T("提示", "Notice"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                MoveStep(1);
            }
            else if (step == 1)
            {
                if (string.IsNullOrWhiteSpace(targetPathBox.Text))
                {
                    MessageBox.Show(T("請指定目標檔案路徑！", "Please specify target file path!"), T("提示", "Notice"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                MoveStep(1);
            }
            else if (step == 2)
            {
                MoveStep(1);
                await StartExportAsync();
            }
            else if (step == 3)
            {
                Close();
            }
        }

        private async Task StartExportAsync()
        {
            exporting = true;
            clock.Restart();
            timer.Start();
            punky.BeginExport();

            var opt = new OracleExportWizardOptions
            {
                Mode = schemaData.Checked ? OracleExportMode.SchemaAndData : (dataOnly.Checked ? OracleExportMode.DataOnly : OracleExportMode.SchemaOnly),
                TargetPath = targetPathBox.Text.Trim(),
                UseTransactionSnapshot = readOnlySnapshot.Checked,
                ExcludeSpatialIndexInternalTables = excludeShadowTables.Checked,
                IncludeSpatialMetadata = includeSpatialMetadata.Checked,
                IncludeSpatialIndexes = includeSpatialIndexes.Checked,
                DropTableIfExists = includeDrop.Checked,
                TruncateBeforeInsert = truncateBefore.Checked,
                IncludeViews = includeViews.Checked,
                IncludeIndexes = includeIndexes.Checked,
                IncludeComments = includeComments.Checked,
                IncludeManifest = includeManifest.Checked
            };

            foreach (var it in catalog)
            {
                if (it.Selected) opt.SelectedTables.Add(it.FullQualifiedName);
            }

            cancellation = new CancellationTokenSource();
            var prog = new Progress<OracleExportProgress>(p =>
            {
                lastProgress = p;
                overallBar.Value = (int)p.PercentComplete;
                percentLabel.Text = string.Format("{0:0.0} %", p.PercentComplete);
                currentObjectLabel.Text = p.Message;
                writtenLabel.Text = string.Format(T("已寫入: {0:N0} KB", "Written: {0:N0} KB"), p.BytesExported / 1024);

                if (!string.IsNullOrWhiteSpace(p.Message))
                {
                    logList.Items.Add(string.Format("[{0:HH:mm:ss}] {1}", DateTime.Now, p.Message));
                    logList.TopIndex = logList.Items.Count - 1;
                }
            });

            try
            {
                Result = await Task.Run(() => service.ExportAsync(opt, prog, cancellation.Token)).ConfigureAwait(true);
                timer.Stop();
                clock.Stop();
                punky.EndExport();

                resultText.Text = string.Format(T("✅ 匯出成功！耗時 {0:hh\\:mm\\:ss}，檔案大小: {1:N0} KB", "✅ Export succeeded! Elapsed {0:hh\\:mm\\:ss}, Size: {1:N0} KB"), Result.Elapsed, Result.FileSizeBytes / 1024);
                resultText.ForeColor = Color.DarkGreen;
                openFileButton.Visible = true;
                openFolderButton.Visible = true;
                nextButton.Enabled = true;
                cancelButton.Enabled = true;
                cancelButton.Text = T("關閉", "Close");
            }
            catch (Exception ex)
            {
                timer.Stop();
                clock.Stop();
                punky.EndExport();

                resultText.Text = T("❌ 匯出失敗: ", "❌ Export failed: ") + ex.Message;
                resultText.ForeColor = Color.Red;
                nextButton.Enabled = true;
                cancelButton.Enabled = true;
            }
            finally
            {
                exporting = false;
                cancellation?.Dispose();
                cancellation = null;
                if (closing && !IsDisposed) BeginInvoke(new Action(Close));
            }
        }

        private void UpdateLiveTicker()
        {
            elapsedLabel.Text = string.Format(T("已耗時: {0:hh\\:mm\\:ss}", "Elapsed: {0:hh\\:mm\\:ss}"), clock.Elapsed);
            if (lastProgress != null && clock.Elapsed.TotalSeconds > 0)
            {
                double kbSec = (lastProgress.BytesExported / 1024.0) / clock.Elapsed.TotalSeconds;
                speedLabel.Text = string.Format("{0:N0} KB/s", kbSec);
            }
        }
    }
}
