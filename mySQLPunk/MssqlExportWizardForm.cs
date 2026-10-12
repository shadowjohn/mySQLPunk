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
using mySQLPunk.lib.MssqlExport;

namespace mySQLPunk
{
    internal sealed class MssqlExportWizardForm : Form
    {
        private readonly string database;
        private readonly MssqlExportWizardOptions options;
        private readonly MssqlSqlExportService service;
        private readonly HashSet<int> selected = new HashSet<int>();
        private readonly HashSet<int> processed = new HashSet<int>();
        private readonly Panel[] pages = new Panel[4];
        private readonly TextBox search = new TextBox(), output = new TextBox();
        private readonly DataGridView tables = new DataGridView(), queue = new DataGridView();
        private readonly CheckBox views = new CheckBox();
        private readonly RadioButton schemaData = new RadioButton(), schemaOnly = new RadioButton(), dataOnly = new RadioButton();
        private readonly Label steps = new Label(), status = new Label(), selection = new Label(), confirmation = new Label();
        private readonly Label stage = new Label(), current = new Label(), percent = new Label(), written = new Label(), tableRows = new Label(), elapsed = new Label(), speed = new Label(), eta = new Label(), resultText = new Label();
        private readonly Button next = new Button(), back = new Button(), cancel = new Button(), openFile = new Button(), openFolder = new Button(), sound = new Button();
        private readonly ProgressBar overall = new ProgressBar(), tableProgress = new ProgressBar();
        private readonly PunkyReportLoadingControl punky = new PunkyReportLoadingControl();
        private readonly ListBox log = new ListBox();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer
        {
            Interval = 500
        };
        private readonly Stopwatch clock = new Stopwatch();
        private IReadOnlyList<MssqlTableDefinition> catalog = new List<MssqlTableDefinition>();
        private CancellationTokenSource cancellation;
        private MssqlExportRequest prepared;
        private MssqlExportProgress last;
        private bool busy, closing, rebuilding, loaded, exporting, cancelRequested;
        private int step, generation;
        internal MssqlExportResult Result { get; private set; }

        private static string T(string zh, string en)
        {
            return ReportText.Get(zh, en);
        }

        internal MssqlExportWizardForm(Dictionary<string, object> connection, string databaseName, MssqlExportWizardOptions options) : this(connection, databaseName, options, null)
        {
        }

        internal MssqlExportWizardForm(Dictionary<string, object> connection, string databaseName, MssqlExportWizardOptions options, MssqlSqlExportService service)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (string.IsNullOrWhiteSpace(databaseName))
                throw new ArgumentException("Database is required", nameof(databaseName));
            this.database = databaseName;
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            var captured = new Dictionary<string, object>();
            foreach (var pair in connection)
                if (!new[]
                {
                    "pdo",
                    "isConnect",
                    "connString"
                }.Contains(pair.Key, StringComparer.OrdinalIgnoreCase) && (pair.Value == null || pair.Value is string || pair.Value.GetType().IsPrimitive || pair.Value is decimal))
                    captured[pair.Key] = pair.Value;
            captured["initial_database"] = databaseName;
            this.service = service ?? new MssqlSqlExportService(captured);
            Name = "mssqlExportWizard";
            Text = T("匯出資料庫 · SQL Server", "Export database · SQL Server");
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
            header.Controls.Add(Label(T("資料庫匯出精靈", "Database export wizard") + "    ·    " + database + "  /  MSSQL", UiKit.Title));
            steps.AutoSize = true;
            steps.Margin = new Padding(0, 16, 0, 20);
            header.Controls.Add(steps);
            root.Controls.Add(header, 0, 0);
            var body = new Panel
            {
                Dock = DockStyle.Fill
            };
            root.Controls.Add(body, 0, 1);
            for (int i = 0; i < 4; i++)
            {
                pages[i] = new Panel
                {
                    Name = "mssqlStep" + i,
                    Dock = DockStyle.Fill,
                    AutoScroll = true
                };
                body.Controls.Add(pages[i]);
            }

            BuildSelection();
            BuildOutput();
            BuildConfirmation();
            BuildProgress();
            status.Name = "mssqlStatus";
            status.AutoSize = true;
            status.MaximumSize = new Size(950, 0);
            status.Margin = new Padding(0, 12, 0, 10);
            root.Controls.Add(status, 0, 2);
            var footer = new FlowLayoutPanel
            {
                Name = "mssqlFooter",
                AutoSize = true,
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = true
            };
            Configure(cancel, "mssqlCancel", T("取消", "Cancel"), (s, e) =>
            {
                if (busy)
                    RequestCancel();
                else
                    Close();
            });
            Configure(next, "mssqlStart", T("下一步 →", "Next →"), async (s, e) =>
            {
                if (step == 0)
                {
                    step = 1;
                    ShowPage();
                }
                else if (step == 1)
                {
                    step = 2;
                    ShowPage();
                    await PreflightAsync();
                }
                else if (step == 2)
                    await ExportAsync();
                else
                {
                    InvalidateRequest();
                    step = 0;
                    ShowPage();
                }
            });
            Configure(back, "mssqlBack", T("← 上一步", "← Back"), (s, e) =>
            {
                InvalidateRequest();
                step--;
                ShowPage();
            });
            Configure(openFile, "mssqlOpenFile", T("開啟檔案", "Open file"), (s, e) => Open(Result?.PublishedPath));
            Configure(openFolder, "mssqlOpenFolder", T("開啟資料夾", "Open folder"), (s, e) => Open(Result?.PublishedPath == null ? null : Path.GetDirectoryName(Result.PublishedPath)));
            footer.Controls.AddRange(new Control[] { cancel, next, back, openFolder, openFile });
            root.Controls.Add(footer, 0, 3);
            ThemeManager.MarkAsPrimary(next);
            AcceptButton = next;
            CancelButton = cancel;
            // CancelButton assigns DialogResult.Cancel automatically. Keep cancellation
            // inside the wizard so a modal caller can inspect the terminal result.
            cancel.DialogResult = DialogResult.None;
            Shown += async (s, e) =>
            {
                var work = Screen.FromControl(Owner ?? this).WorkingArea;
                MinimumSize = new Size(Math.Min(MinimumSize.Width, work.Width), Math.Min(MinimumSize.Height, work.Height));
                Size = new Size(Math.Min(Width, work.Width), Math.Min(Height, work.Height));
                Location = new Point(Math.Max(work.Left, Math.Min(Left, work.Right - Width)), Math.Max(work.Top, Math.Min(Top, work.Bottom - Height)));
                await LoadAsync();
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
            timer.Tick += (s, e) => UpdateClock();
            ThemeManager.ApplyTo(this);
            percent.ForeColor = ThemeManager.AccentColor;
            ShowPage();
        }

        private static FlowLayoutPanel Stack()
        {
            return new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false
            };
        }

        private static Label Label(string text, Font font = null)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Font = font ?? UiKit.Body,
                Margin = new Padding(0, 6, 0, 8)
            };
        }

        private static void Configure(Button button, string name, string text, EventHandler handler)
        {
            button.Name = name;
            button.Text = text;
            button.AccessibleName = text;
            button.AutoSize = true;
            button.MinimumSize = new Size(90, 36);
            button.Click += handler;
        }

        private static void Grid(DataGridView grid)
        {
            grid.Dock = DockStyle.Fill;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.RowHeadersVisible = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.BackgroundColor = ThemeManager.SurfaceColor;
            grid.BorderStyle = BorderStyle.None;
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        }

        private void BuildSelection()
        {
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3
            };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            pages[0].Controls.Add(layout);
            var tools = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true
            };
            search.Name = "mssqlTableSearch";
            search.Width = 260;
            search.AccessibleName = T("搜尋 schema、資料表與註解", "Search schema, table and comment");
            tools.Controls.Add(Label(T("搜尋資料表", "Search tables")));
            tools.Controls.Add(search);
            var all = new Button();
            var none = new Button();
            Configure(all, "mssqlSelectAll", T("全選", "Select all"), (s, e) =>
            {
                foreach (var t in catalog)
                    selected.Add(t.Key.ObjectId);
                InvalidateRequest();
                RefreshTables();
            });
            Configure(none, "mssqlSelectNone", T("全部取消", "Clear all"), (s, e) =>
            {
                selected.Clear();
                InvalidateRequest();
                RefreshTables();
            });
            tools.Controls.Add(all);
            tools.Controls.Add(none);
            layout.Controls.Add(tools, 0, 0);
            Grid(tables);
            tables.Name = "mssqlTableList";
            tables.Columns.Add(new DataGridViewCheckBoxColumn { Name = "selected", HeaderText = T("選取", "Select"), FillWeight = 35 });
            foreach (var text in new[]
            {
                "Schema",
                T("資料表", "Table"),
                T("註解", "Comment"),
                T("估計筆數", "Est. rows"),
                T("估計大小", "Est. bytes")
            }

            )
            {
                int index = tables.Columns.Add(text, text);
                tables.Columns[index].ReadOnly = true;
            }

            tables.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (tables.IsCurrentCellDirty)
                    tables.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            tables.CellValueChanged += (s, e) =>
            {
                if (rebuilding || e.RowIndex < 0 || e.ColumnIndex != 0)
                    return;
                var key = (MssqlObjectKey)tables.Rows[e.RowIndex].Tag;
                if (Convert.ToBoolean(tables.Rows[e.RowIndex].Cells[0].Value))
                    selected.Add(key.ObjectId);
                else
                    selected.Remove(key.ObjectId);
                InvalidateRequest();
                UpdateButtons();
            };
            search.TextChanged += (s, e) => RefreshTables();
            layout.Controls.Add(tables, 0, 1);
            selection.AutoSize = true;
            layout.Controls.Add(selection, 0, 2);
        }

        private void RefreshTables()
        {
            rebuilding = true;
            tables.Rows.Clear();
            foreach (var t in catalog)
                if ((t.Key.Schema + " " + t.Key.Name + " " + t.Comment).IndexOf(search.Text, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    int i = tables.Rows.Add(selected.Contains(t.Key.ObjectId), t.Key.Schema, t.Key.Name, t.Comment, t.EstimatedRows?.ToString("N0") ?? "—", t.EstimatedBytes?.ToString("N0") ?? "—");
                    tables.Rows[i].Tag = t.Key;
                }

            rebuilding = false;
            UpdateButtons();
        }

        private void BuildOutput()
        {
            var content = Stack();
            pages[1].Controls.Add(content);
            content.Controls.Add(Label(T("輸出設定", "Output settings"), UiKit.Subtitle));
            var radios = new[]
            {
                schemaData,
                schemaOnly,
                dataOnly
            };
            var names = new[]
            {
                "mssqlSchemaAndData",
                "mssqlSchemaOnly",
                "mssqlDataOnly"
            };
            var titles = new[]
            {
                T("結構與資料", "Schema and data"),
                T("僅結構", "Schema only"),
                T("僅資料", "Data only")
            };
            for (int i = 0; i < 3; i++)
            {
                var radio = radios[i];
                radio.Name = names[i];
                radio.Text = titles[i];
                radio.AutoSize = true;
                content.Controls.Add(radio);
                radio.CheckedChanged += (s, e) =>
                {
                    if (!radio.Checked)
                        return;
                    views.Enabled = !dataOnly.Checked;
                    if (dataOnly.Checked)
                        views.Checked = false;
                    InvalidateRequest();
                    UpdateButtons();
                };
            }

            views.Name = "mssqlIncludeViews";
            views.Text = T("包含檢視表定義（結構模式）", "Include view definitions (schema modes)");
            views.AutoSize = true;
            views.Checked = options.IncludeViews;
            views.CheckedChanged += (s, e) =>
            {
                InvalidateRequest();
                UpdateButtons();
            };
            content.Controls.Add(views);
            radios[(int)options.Mode].Checked = true;
            content.Controls.Add(Label(T("SQL 輸出路徑", "SQL output path")));
            var path = new FlowLayoutPanel
            {
                AutoSize = true,
                Width = 850
            };
            output.Name = "mssqlOutputPath";
            output.ReadOnly = true;
            output.Width = 580;
            output.AccessibleName = T("SQL 輸出路徑", "SQL output path");
            output.Text = !string.IsNullOrWhiteSpace(options.DefaultFileName) && Path.IsPathRooted(options.DefaultFileName) ? options.DefaultFileName : "";
            output.TextChanged += (s, e) =>
            {
                InvalidateRequest();
                UpdateButtons();
            };
            path.Controls.Add(output);
            var browse = new Button();
            Configure(browse, "mssqlBrowse", T("瀏覽…", "Browse…"), (s, e) =>
            {
                using (var dialog = new SaveFileDialog
                {
                    Filter = "SQL (*.sql)|*.sql",
                    DefaultExt = "sql",
                    AddExtension = true,
                    OverwritePrompt = false,
                    FileName = string.IsNullOrWhiteSpace(output.Text) ? options.DefaultFileName ?? database + ".sql" : output.Text
                }

                )
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                        output.Text = dialog.FileName;
            });
            path.Controls.Add(browse);
            content.Controls.Add(path);
            content.Controls.Add(Label(options.Mirror == null ? T("鏡像：未啟用", "Mirror: disabled") : T("鏡像：", "Mirror: ") + options.Mirror.Directory + " · " + options.Mirror.RetainCount));
            content.Controls.Add(Label(T("SQL 邏輯匯出；不包含登入帳號、作業或原生 .bak 備份。", "Logical SQL export; excludes logins, jobs and native .bak backups.")));
        }

        private void BuildConfirmation()
        {
            var content = Stack();
            pages[2].Controls.Add(content);
            content.Controls.Add(Label(T("確認匯出與預檢", "Confirm export and preflight"), UiKit.Subtitle));
            confirmation.Name = "mssqlConfirmation";
            confirmation.AutoSize = true;
            confirmation.MaximumSize = new Size(900, 0);
            content.Controls.Add(confirmation);
        }

        private void BuildProgress()
        {
            var content = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 4
            };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72));
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
            pages[3].Controls.Add(content);
            var stats = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 1
            };
            content.Controls.Add(stats, 0, 0);
            foreach (var label in new[]
            {
                stage,
                current,
                percent,
                written,
                tableRows,
                elapsed,
                speed,
                eta
            }

            )
            {
                label.AutoSize = true;
                label.Margin = new Padding(0, 7, 0, 7);
                label.MaximumSize = new Size(720, 0);
            }

            stage.Name = "mssqlStage";
            stage.Font = UiKit.Subtitle;
            current.Name = "mssqlCurrentTable";
            percent.Font = UiKit.Display;
            written.Name = "mssqlWritten";
            elapsed.Name = "mssqlElapsed";
            speed.Name = "mssqlSpeed";
            eta.Name = "mssqlEta";
            overall.Name = "mssqlOverallProgress";
            overall.Dock = DockStyle.Top;
            overall.Height = 10;
            tableProgress.Name = "mssqlTableProgress";
            tableProgress.Dock = DockStyle.Top;
            tableProgress.Height = 6;
            foreach (Control c in new Control[]
            {
                stage,
                current,
                percent,
                written,
                overall,
                tableRows,
                tableProgress,
                elapsed,
                speed,
                eta
            }

            )
                stats.Controls.Add(c);
            var mascot = Stack();
            mascot.Dock = DockStyle.Top;
            punky.Name = "mssqlPunky";
            punky.Size = new Size(240, 240);
            punky.Muted = false;
            mascot.Controls.Add(punky);
            Configure(sound, "mssqlToggleSound", T("靜音", "Mute"), (s, e) =>
            {
                punky.Muted = !punky.Muted;
                sound.Text = punky.Muted ? T("開啟聲音", "Unmute") : T("靜音", "Mute");
                sound.AccessibleName = sound.Text;
            });
            sound.Enabled = punky.AudioAvailable;
            sound.AccessibleDescription = T("原音只播放一次；取消靜音不會重播。", "Original audio plays once; unmuting does not replay it.");
            mascot.Controls.Add(sound);
            mascot.Controls.Add(Label(T("正在幫你整理資料！", "Organizing your data!")));
            content.Controls.Add(mascot, 1, 0);
            var flow = Label(T("準備  ›  結構  ›  資料  ›  驗證  ›  發布  ›  鏡像", "Prepare  ›  Schema  ›  Data  ›  Verify  ›  Publish  ›  Mirror"));
            content.Controls.Add(flow, 0, 1);
            content.SetColumnSpan(flow, 2);
            Grid(queue);
            queue.Name = "mssqlTableQueue";
            queue.Height = 150;
            queue.Dock = DockStyle.Top;
            queue.ReadOnly = true;
            foreach (var text in new[]
            {
                T("資料表", "Table"),
                T("註解", "Comment"),
                T("狀態", "Status")
            }

            )
                queue.Columns.Add(text, text);
            content.Controls.Add(queue, 0, 2);
            content.SetColumnSpan(queue, 2);
            var lower = Stack();
            lower.Dock = DockStyle.Top;
            resultText.AutoSize = true;
            resultText.MaximumSize = new Size(900, 0);
            resultText.Name = "mssqlResult";
            lower.Controls.Add(resultText);
            var detail = new Button();
            Configure(detail, "mssqlDetails", T("詳細記錄", "Details"), (s, e) => log.Visible = !log.Visible);
            lower.Controls.Add(detail);
            log.Name = "mssqlLog";
            log.Width = 850;
            log.Height = 120;
            log.Visible = false;
            lower.Controls.Add(log);
            content.Controls.Add(lower, 0, 3);
            content.SetColumnSpan(lower, 2);
            pages[3].Resize += (s, e) =>
            {
                punky.Size = new Size(Math.Max(100, Math.Min(240, pages[3].ClientSize.Width / 4)), Math.Max(100, Math.Min(240, pages[3].ClientSize.Width / 4)));
            };
        }

        private MssqlExportMode Mode => dataOnly.Checked ? MssqlExportMode.DataOnly : schemaOnly.Checked ? MssqlExportMode.SchemaOnly : MssqlExportMode.SchemaAndData;

        private void InvalidateRequest()
        {
            generation++;
            prepared = null;
            Result = null;
            last = null;
            resultText.Text = "";
            openFile.Visible = openFolder.Visible = false;
        }

        private void ShowPage()
        {
            for (int i = 0; i < pages.Length; i++)
            {
                pages[i].Visible = i == step;
                if (i == step)
                    pages[i].BringToFront();
            }

            steps.Text = T("① 選擇資料表    →    ② 輸出設定    →    ③ 確認匯出    →    ④ 執行與結果", "① Tables    →    ② Output    →    ③ Confirm    →    ④ Progress and result");
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            selection.Text = T("已選取 ", "Selected ") + selected.Count + " / " + catalog.Count;
            back.Visible = step > 0 && step < 3;
            back.Enabled = !busy;
            next.Visible = step < 3 || (!busy && Result != null);
            next.Enabled = !busy && loaded && (step == 0 ? selected.Count > 0 : step == 1 ? !string.IsNullOrWhiteSpace(output.Text) : step == 2 ? prepared != null : true);
            next.Text = step == 2 ? T("開始匯出", "Start export") : step == 3 ? T("重新設定", "Reset") : T("下一步 →", "Next →");
            cancel.Enabled = !cancelRequested;
            cancel.Text = busy && exporting ? T("取消匯出", "Cancel export") : T("關閉", "Close");
            next.AccessibleName = next.Text;
            cancel.AccessibleName = cancel.Text;
            openFile.Visible = openFolder.Visible = !busy && Result?.PublishedPath != null;
            for (int i = 0; i < 3; i++)
                pages[i].Enabled = !busy;
        }

        private void BeginOperation()
        {
            busy = true;
            cancelRequested = false;
            cancellation = new CancellationTokenSource();
            UpdateButtons();
        }

        private void EndOperation()
        {
            busy = false;
            exporting = false;
            cancelRequested = false;
            cancellation.Dispose();
            cancellation = null;
            UpdateButtons();
            if (closing)
                Close();
        }

        private async Task LoadAsync()
        {
            BeginOperation();
            status.Text = T("讀取原生目錄…", "Loading native catalog…");
            try
            {
                var value = await Task.Run(() => service.LoadCatalog(database, cancellation.Token));
                if (closing)
                    return;
                catalog = value.Tables;
                selected.Clear();
                if (options.InitialObjectName != null)
                {
                    var matches = catalog.Where(t => (t.Key.Schema == "dbo" ? t.Key.Name : t.Key.Schema + "." + t.Key.Name) == options.InitialObjectName).ToList();
                    if (matches.Count == 1)
                        selected.Add(matches[0].Key.ObjectId);
                }
                else
                    foreach (var table in catalog)
                        if (options.InitialTables == null || options.InitialTables.Any(k => k.Schema == table.Key.Schema && k.Name == table.Key.Name))
                            selected.Add(table.Key.ObjectId);
                loaded = true;
                status.Text = selected.Count == 0 ? T("請選取資料表；初始物件可能已移除或名稱不唯一。", "Select tables; the initial object may be missing or ambiguous.") : T("估計筆數與大小來自目錄，可能與實際值不同。", "Catalog row and size estimates may differ from actual values.");
                RefreshTables();
            }
            catch (OperationCanceledException)
            {
                status.Text = T("已取消", "Canceled");
            }
            catch (Exception)
            {
                status.Text = T("無法讀取目錄，請關閉後重試。", "Cannot load catalog. Close and retry.");
            }
            finally
            {
                EndOperation();
            }
        }

        private async Task PreflightAsync()
        {
            InvalidateRequest();
            int version = generation;
            var keys = catalog.Where(t => selected.Contains(t.Key.ObjectId)).Select(t => t.Key).ToList().AsReadOnly();
            var mode = Mode;
            var include = views.Checked;
            var path = output.Text;
            BeginOperation();
            confirmation.Text = T("正在捕獲目的檔案狀態並預檢…", "Capturing destination and checking preflight…");
            try
            {
                var stamp = await Task.Run(() => MssqlExportArtifact.CaptureDestination(path, cancellation.Token));
                if (closing || version != generation)
                    return;
                if (stamp.Exists && MessageBox.Show(this, T("目的檔案已存在。確認覆寫這個檔案？", "Destination exists. Replace this file?"), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                {
                    confirmation.Text = T("未同意覆寫。", "Replacement not approved.");
                    return;
                }

                var request = new MssqlExportRequest(Guid.NewGuid(), database, keys, mode, include, path, stamp, options.Mirror);
                var check = await Task.Run(() => service.Preflight(request, cancellation.Token));
                if (closing || version != generation)
                    return;
                confirmation.Text = T("SQL 邏輯匯出；不包含登入帳號、作業、原生備份。", "Logical SQL export; excludes logins, jobs and native backups.") + "\r\n\r\n" + keys.Count + T(" 張資料表", " tables") + " · " + ModeText(mode) + "\r\n" + request.TargetPath + "\r\n" + (options.Mirror == null ? T("鏡像：未啟用", "Mirror: disabled") : T("鏡像：", "Mirror: ") + options.Mirror.Directory) + "\r\n" + (mode == MssqlExportMode.SchemaOnly ? T("僅結構：不開啟資料快照交易。", "Schema only: no data snapshot transaction.") : "SNAPSHOT: " + (check.SnapshotState == 1 ? "ON" : "OFF") + T("；全部資料使用同一交易。", "; all data uses one transaction.")) + "\r\n\r\n" + string.Join("\r\n", check.Issues.Select(x => x.Message));
                if (check.CanStart && (mode == MssqlExportMode.SchemaOnly || check.SnapshotState == 1))
                    prepared = request;
                status.Text = prepared != null ? T("預檢通過；開始時會再次檢查來源。", "Preflight passed; source is checked again at start.") : T("預檢未通過，無法開始。", "Preflight failed; start is blocked.");
            }
            catch (OperationCanceledException)
            {
                status.Text = T("已取消", "Canceled");
            }
            catch (Exception)
            {
                status.Text = T("目的路徑或預檢失敗，請返回檢查設定。", "Destination or preflight failed. Go back and check settings.");
            }
            finally
            {
                EndOperation();
            }
        }

        private async Task ExportAsync()
        {
            if (prepared == null)
                return;
            var request = prepared;
            int version = generation;
            Result = null;
            last = null;
            log.Items.Clear();
            queue.Rows.Clear();
            processed.Clear();
            foreach (var t in catalog.Where(t => selected.Contains(t.Key.ObjectId)))
            {
                int row = queue.Rows.Add(t.Key.Schema + "." + t.Key.Name, t.Comment, T("等待中", "Waiting"));
                queue.Rows[row].Tag = t.Key.ObjectId;
            }
            stage.Text = StageText(MssqlExportStage.Preflight);
            status.Text = stage.Text;
            current.Text = "";
            percent.Text = T("估算中", "Estimating");
            written.Text = T("已寫入 0 bytes", "Written 0 bytes");
            overall.Value = tableProgress.Value = 0;
            overall.Style = tableProgress.Style = ProgressBarStyle.Marquee;
            tableRows.Text = "";
            resultText.Text = "";
            step = 3;
            ShowPage();
            BeginOperation();
            exporting = true;
            clock.Restart();
            timer.Start();
            punky.BeginExport();
            UpdateButtons();
            var progress = new Progress<MssqlExportProgress>(p =>
            {
                if (!IsDisposed && !closing && busy && exporting && version == generation && p.JobId == request.JobId)
                    RenderProgress(p);
            });
            try
            {
                var result = await Task.Run(() => service.Export(request, progress, cancellation.Token));
                if (result.JobId == request.JobId)
                {
                    Result = result;
                    stage.Text = StageText(result.Stage);
                    status.Text = StageText(result.Stage);
                    foreach (DataGridViewRow row in queue.Rows)
                    {
                        bool done = processed.Contains((int)row.Tag) || result.RowCounts.Any(r => r.Key.ObjectId == (int)row.Tag) || result.Stage == MssqlExportStage.Completed;
                        row.Cells[2].Value = done ? T("已處理", "Processed") : result.Stage == MssqlExportStage.Canceled ? T("已取消", "Canceled") : T("未完成", "Incomplete");
                    }
                    resultText.Text = (result.PublishedPath == null ? T("沒有已發布的本機檔案。", "No local file was published.") : T("已發布：", "Published: ") + result.PublishedPath) + "\r\n" + (result.MirrorPath ?? "") + "\r\n" + string.Join("\r\n", result.Warnings.Concat(result.ResidualPaths)) + "\r\n" + (result.ErrorMessage ?? "") + "\r\n" + T("檔案驗證不等同還原驗收。請使用支援此檔案大小的客戶端，以 UTF-8、停用變數替代、遇錯停止，於同一工作階段還原。詳見 docs/MSSQL_SQL_EXPORT.md。", "File verification is not restore acceptance. Use a client supporting this file size: UTF-8, variable substitution disabled, stop on error, and one restore session. See docs/MSSQL_SQL_EXPORT.md.");
                    if (result.Stage == MssqlExportStage.Completed)
                    {
                        overall.Style = ProgressBarStyle.Continuous;
                        overall.Value = 100;
                        percent.Text = "100%";
                    }
                }
            }
            catch (Exception)
            {
                status.Text = T("匯出作業失敗。", "Export failed.");
                stage.Text = StageText(MssqlExportStage.Failed);
            }
            finally
            {
                timer.Stop();
                clock.Stop();
                punky.EndExport();
                EndOperation();
            }
        }

        private void RenderProgress(MssqlExportProgress p)
        {
            if (last?.CurrentTable != null && p.CompletedTables > last.CompletedTables)
                processed.Add(last.CurrentTable.ObjectId);
            last = p;
            stage.Text = cancelRequested ? StageText(MssqlExportStage.CancelRequested) : StageText(p.Stage);
            if (!cancelRequested)
                status.Text = stage.Text;
            current.Text = p.CurrentTable == null ? "" : p.CurrentTable.Schema + "." + p.CurrentTable.Name;
            percent.Text = p.Percent.HasValue ? Math.Min(99, p.Percent.Value).ToString("0") + "%" : T("估算中", "Estimating");
            overall.Style = p.Percent.HasValue ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
            if (p.Percent.HasValue)
                overall.Value = (int)Math.Max(0, Math.Min(99, p.Percent.Value));
            written.Text = T("已寫入 ", "Written ") + p.WrittenBytes.ToString("N0") + " bytes";
            var table = catalog.FirstOrDefault(t => t.Key.ObjectId == p.CurrentTable?.ObjectId);
            var estimate = table?.EstimatedRows;
            tableRows.Text = T("目前資料表：", "Current table: ") + p.CurrentTableRows.ToString("N0") + " / " + (estimate?.ToString("N0") ?? "—");
            tableProgress.Style = estimate > 0 ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
            if (estimate > 0)
                tableProgress.Value = (int)Math.Min(100, 100.0 * p.CurrentTableRows / estimate.Value);
            if (p.CurrentTable == null && p.PhaseTotalBytes > 0)
            {
                tableRows.Text = T("本階段檔案處理：", "Current file phase: ") + p.PhaseProcessedBytes.ToString("N0") + " / " + p.PhaseTotalBytes.Value.ToString("N0") + " bytes";
                tableProgress.Style = ProgressBarStyle.Continuous;
                tableProgress.Value = (int)Math.Min(100, 100.0 * p.PhaseProcessedBytes / p.PhaseTotalBytes.Value);
            }
            for (int i = 0; i < queue.Rows.Count; i++)
                queue.Rows[i].Cells[2].Value = processed.Contains((int)queue.Rows[i].Tag) ? T("已處理", "Processed") : p.CurrentTable != null && (int)queue.Rows[i].Tag == p.CurrentTable.ObjectId ? T("匯出中", "Exporting") : T("等待中", "Waiting");
            if (!string.IsNullOrWhiteSpace(p.Message))
            {
                log.Items.Add(p.Message);
                while (log.Items.Count > MssqlExportLimits.ProgressLogLimit)
                    log.Items.RemoveAt(0);
                log.TopIndex = log.Items.Count - 1;
            }

            UpdateClock();
        }

        private void UpdateClock()
        {
            elapsed.Text = T("已經過：", "Elapsed: ") + clock.Elapsed.ToString(@"hh\:mm\:ss");
            bool stale = last == null || clock.Elapsed - last.LastProgressElapsed >= TimeSpan.FromSeconds(5) || cancelRequested;
            speed.Text = T("本階段速度：", "Phase rate: ") + (!stale && last.BytesPerSecond.HasValue ? last.BytesPerSecond.Value.ToString("N0") + " bytes/s" : "—");
            eta.Text = T("本階段預估：", "Phase ETA: ") + (!stale && last.Eta.HasValue ? last.Eta.Value.ToString(@"hh\:mm\:ss") : T("等待／估算中", "Waiting / estimating"));
        }

        private void RequestCancel()
        {
            if (cancellation == null || cancelRequested)
                return;
            cancelRequested = true;
            cancellation.Cancel();
            status.Text = T("正在取消，等待查詢停止及清理完成…", "Canceling; waiting for query and cleanup…");
            stage.Text = StageText(MssqlExportStage.CancelRequested);
            UpdateButtons();
            UpdateClock();
        }

        private static string ModeText(MssqlExportMode mode)
        {
            return mode == MssqlExportMode.SchemaOnly ? T("僅結構", "Schema only") : mode == MssqlExportMode.DataOnly ? T("僅資料", "Data only") : T("結構與資料", "Schema and data");
        }

        private static string StageText(MssqlExportStage value)
        {
            switch (value)
            {
                case MssqlExportStage.Preflight:
                    return T("準備與預檢", "Preparing and checking");
                case MssqlExportStage.Schema:
                    return T("正在匯出結構", "Exporting schema");
                case MssqlExportStage.Data:
                    return T("正在匯出資料", "Exporting data");
                case MssqlExportStage.Verifying:
                    return T("正在驗證檔案", "Verifying file");
                case MssqlExportStage.Publishing:
                    return T("正在發布檔案", "Publishing file");
                case MssqlExportStage.Mirroring:
                    return T("正在複製並驗證鏡像", "Copying and verifying mirror");
                case MssqlExportStage.CancelRequested:
                    return T("正在取消，等待清理", "Canceling; waiting for cleanup");
                case MssqlExportStage.Completed:
                    return T("匯出完成", "Export completed");
                case MssqlExportStage.Canceled:
                    return T("匯出已取消", "Export canceled");
                case MssqlExportStage.LocalCompletedMirrorIncomplete:
                    return T("本機完成；鏡像未完成", "Local completed; mirror incomplete");
                default:
                    return T("匯出失敗", "Export failed");
            }
        }

        private void Open(string path)
        {
            if (path == null)
                return;
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception)
            {
                status.Text = T("無法開啟路徑。", "Cannot open path.");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
