using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    /// <summary>
    /// 整庫資料傳輸精靈：勾選來源資料表、設定目標名稱與模式（建立新表／附加／取代資料）、欄位對應，
    /// 執行時每批記錄檢查點，可取消後續傳；完成後以列數驗證並可匯出 HTML 報告。取代資料需輸入目標資料庫名稱。
    /// </summary>
    public sealed class DataTransferForm : Form
    {
        private static readonly TransferMode[] Modes = { TransferMode.CreateNew, TransferMode.Append, TransferMode.ReplaceData };
        private const string SkipColumn = "—";

        private readonly SchemaComparisonEndpoint source;
        private readonly SchemaComparisonEndpoint target;
        private readonly string checkpointDirectory;
        private readonly DataGridView tableGrid;
        private readonly DataGridView columnGrid;
        private readonly Label columnHeader;
        private readonly NumericUpDown batchSize;
        private readonly CheckBox continueOnError;
        private readonly Button startButton;
        private readonly Button resumeButton;
        private readonly Button cancelButton;
        private readonly Button reportButton;
        private readonly ProgressBar progressBar;
        private readonly ToolStripStatusLabel statusLabel;
        private SchemaModelSnapshot sourceSnapshot;
        private TransferPlan plan;
        private TransferPlan unfinished;
        private CancellationTokenSource cancellation;
        private bool loading;
        private bool running;

        public DataTransferForm(SchemaComparisonEndpoint source, SchemaComparisonEndpoint target, string checkpointDirectory = null)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (target == null) throw new ArgumentNullException("target");
            this.source = source;
            this.target = target;
            this.checkpointDirectory = checkpointDirectory ?? DataTransferService.DefaultCheckpointDirectory;

            Text = Localization.T("Transfer.WindowTitle");
            Width = 1180;
            Height = 740;
            MinimumSize = new Size(860, 500);
            StartPosition = FormStartPosition.CenterParent;

            Label header = new Label
            {
                Dock = DockStyle.Top,
                Height = 44,
                Padding = new Padding(10, 6, 10, 0),
                AutoEllipsis = true,
                Text = Localization.Format("Transfer.Header", source.DisplayName, target.DisplayName)
            };
            Label notice = new Label { Dock = DockStyle.Top, Height = 36, Padding = new Padding(10, 0, 10, 0), ForeColor = Color.FromArgb(181, 71, 8), Text = Localization.T("Transfer.Notice") };

            tableGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = SystemColors.Window,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false
            };
            tableGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Include", HeaderText = string.Empty, FillWeight = 6 });
            tableGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Source", HeaderText = Localization.T("Transfer.Column.Source"), ReadOnly = true, FillWeight = 20 });
            tableGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Target", HeaderText = Localization.T("Transfer.Column.Target"), FillWeight = 20 });
            DataGridViewComboBoxColumn modeColumn = new DataGridViewComboBoxColumn { Name = "Mode", HeaderText = Localization.T("Transfer.Column.Mode"), FillWeight = 16, FlatStyle = FlatStyle.Flat };
            modeColumn.Items.AddRange(Modes.Select(mode => (object)DataTransferService.ModeText(mode)).ToArray());
            tableGrid.Columns.Add(modeColumn);
            tableGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Progress", HeaderText = Localization.T("Transfer.Column.Copied"), ReadOnly = true, FillWeight = 14 });
            tableGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Result", HeaderText = Localization.T("Transfer.Column.Result"), ReadOnly = true, FillWeight = 24 });

            columnHeader = new Label { Dock = DockStyle.Top, Height = 22, Text = Localization.T("Transfer.Columns.Hint") };
            columnGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = SystemColors.Window
            };
            columnGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "SourceColumn", HeaderText = Localization.T("Transfer.Column.SourceColumn"), ReadOnly = true });
            columnGrid.Columns.Add(new DataGridViewComboBoxColumn { Name = "TargetColumn", HeaderText = Localization.T("Transfer.Column.TargetColumn"), FlatStyle = FlatStyle.Flat });
            Panel right = new Panel { Dock = DockStyle.Right, Width = 380, Padding = new Padding(8) };
            right.Controls.Add(columnGrid);
            right.Controls.Add(columnHeader);

            batchSize = new NumericUpDown { Minimum = 1, Maximum = 50000, Value = 1000, Increment = 500, Width = 80 };
            continueOnError = new CheckBox { Text = Localization.T("Transfer.ContinueOnError"), AutoSize = true, Padding = new Padding(8, 4, 0, 0) };
            startButton = new Button { Text = Localization.T("Transfer.Start"), AutoSize = true };
            resumeButton = new Button { Text = Localization.T("Transfer.Resume"), AutoSize = true, Visible = false };
            cancelButton = new Button { Text = Localization.T("Transfer.Stop"), AutoSize = true, Enabled = false };
            reportButton = new Button { Text = Localization.T("Transfer.Report"), AutoSize = true, Enabled = false };
            Button closeButton = new Button { Text = Localization.T("Common.Close"), AutoSize = true };
            progressBar = new ProgressBar { Width = 180, Height = 20, Margin = new Padding(8, 6, 0, 0) };
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(8, 6, 8, 6) };
            actions.Controls.AddRange(new Control[]
            {
                new Label { Text = Localization.T("Transfer.BatchSize"), AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, batchSize, continueOnError,
                startButton, resumeButton, cancelButton, reportButton, progressBar, closeButton
            });

            StatusStrip statusStrip = new StatusStrip { SizingGrip = true };
            statusLabel = new ToolStripStatusLabel(string.Empty) { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            statusStrip.Items.Add(statusLabel);

            Panel center = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
            center.Controls.Add(tableGrid);
            Controls.Add(center);
            Controls.Add(right);
            Controls.Add(actions);
            Controls.Add(notice);
            Controls.Add(header);
            Controls.Add(statusStrip);

            tableGrid.CurrentCellDirtyStateChanged += (sender, args) =>
            {
                if (tableGrid.IsCurrentCellDirty && (tableGrid.CurrentCell is DataGridViewCheckBoxCell || tableGrid.CurrentCell is DataGridViewComboBoxCell))
                {
                    tableGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
                }
            };
            columnGrid.CurrentCellDirtyStateChanged += (sender, args) =>
            {
                if (columnGrid.IsCurrentCellDirty) columnGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            tableGrid.DataError += (sender, args) => args.ThrowException = false;
            columnGrid.DataError += (sender, args) => args.ThrowException = false;
            tableGrid.CellValueChanged += (sender, args) => { if (!loading && args.RowIndex >= 0) ReadTableRow(tableGrid.Rows[args.RowIndex]); };
            tableGrid.SelectionChanged += (sender, args) => { if (!loading) Guard(ShowColumns); };
            columnGrid.CellValueChanged += (sender, args) => { if (!loading && args.RowIndex >= 0) ReadColumnRow(columnGrid.Rows[args.RowIndex]); };
            startButton.Click += (sender, args) => StartAsync(false);
            resumeButton.Click += (sender, args) => StartAsync(true);
            cancelButton.Click += (sender, args) => { if (cancellation != null) cancellation.Cancel(); };
            reportButton.Click += (sender, args) => SaveReport();
            closeButton.Click += (sender, args) => Close();
            FormClosing += (sender, args) =>
            {
                if (!running) return;
                args.Cancel = true;
                statusLabel.Text = Localization.T("Transfer.StopFirst");
            };
            Shown += (sender, args) => Guard(LoadTables);
            ThemeManager.ApplyTo(this);
        }

        public TransferPlan Plan { get { return plan; } }

        public bool ResumeAvailable { get { return unfinished != null; } }

        /// <summary>讀取來源資料表並建立預設計畫；同路線有未完成的檢查點時啟用續傳。也供測試直接呼叫。</summary>
        public void LoadTables()
        {
            sourceSnapshot = SchemaModelService.Load(source.Database, source.DatabaseName);
            plan = DataTransferService.BuildPlan(source.Database, source.DatabaseName, source.ConnectionName, target.Database, target.DatabaseName, target.ConnectionName,
                sourceSnapshot.Tables.Select(table => table.Name));
            unfinished = DataTransferService.LoadUnfinished(checkpointDirectory, plan);
            resumeButton.Visible = unfinished != null;
            RenderPlan();
            statusLabel.Text = unfinished != null
                ? Localization.Format("Transfer.ResumeFound", unfinished.Items.Count(item => item.Include && item.Status != TransferItemStatus.Done), unfinished.UpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
                : Localization.Format("Transfer.TablesFound", plan.Items.Count);
        }

        /// <summary>設定一張表的傳輸方式；也供測試直接呼叫。</summary>
        public void SetItem(string sourceTable, bool include, TransferMode mode, string targetTable)
        {
            TransferItem item = plan.Items.Single(entry => string.Equals(entry.SourceTable, sourceTable, StringComparison.OrdinalIgnoreCase));
            item.Include = include;
            item.Mode = mode;
            item.TargetTable = targetTable ?? item.TargetTable;
            item.Columns.Clear();
            RenderPlan();
        }

        /// <summary>改用未完成的檢查點；也供測試直接呼叫。</summary>
        public void UseUnfinished()
        {
            if (unfinished == null) return;
            plan = unfinished;
            unfinished = null;
            resumeButton.Visible = false;
            RenderPlan();
        }

        /// <summary>同步執行目前計畫（UI 會在背景執行緒呼叫）；也供測試直接呼叫。</summary>
        public void Execute(CancellationToken token, Action<TransferProgress> progress = null)
        {
            plan.BatchSize = (int)batchSize.Value;
            plan.ContinueOnError = continueOnError.Checked;
            HashSet<string> keyed = new HashSet<string>(
                sourceSnapshot.Tables.Where(table => table.Columns.Any(column => column.IsPrimaryKey)).Select(table => table.Name),
                StringComparer.OrdinalIgnoreCase);
            DataTransferService.Run(plan, source.Database, target.Database, keyed, progress,
                current => DataTransferService.SaveCheckpoint(current, checkpointDirectory), token);
        }

        public string BuildReport()
        {
            return DataTransferService.BuildHtmlReport(plan, Application.ProductVersion);
        }

        private async void StartAsync(bool resume)
        {
            if (running || plan == null) return;
            if (resume) UseUnfinished();
            List<TransferItem> included = plan.Items.Where(item => item.Include && item.Status != TransferItemStatus.Done).ToList();
            if (included.Count == 0)
            {
                statusLabel.Text = Localization.T("Transfer.NothingSelected");
                return;
            }

            string message = Localization.Format("Transfer.Confirm", included.Count, target.DisplayName) + Environment.NewLine +
                             string.Join(Environment.NewLine, included.Take(20).Select(item => "• " + item.SourceTable + " → " + item.TargetTable + "（" + DataTransferService.ModeText(item.Mode) + "）"));
            bool destructive = included.Any(item => item.Mode == TransferMode.ReplaceData);
            bool confirmed = destructive
                ? TypedConfirm(message + Environment.NewLine + Environment.NewLine + Localization.T("Transfer.ConfirmReplace"), target.DatabaseName)
                : MessageBox.Show(this, message, Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;
            if (!confirmed) return;

            running = true;
            cancellation = new CancellationTokenSource();
            SetRunning(true);
            try
            {
                await Task.Run(() => Execute(cancellation.Token, progress => BeginInvoke((Action)(() =>
                {
                    statusLabel.Text = Localization.Format("Transfer.Running", progress.TableIndex, progress.TableCount, progress.Table, progress.Copied, progress.Total);
                    progressBar.Value = progress.Total <= 0 ? 0 : (int)Math.Min(100, progress.Copied * 100 / progress.Total);
                    RenderProgress();
                }))));
                statusLabel.Text = plan.IsFinished
                    ? Localization.Format("Transfer.Finished", plan.Items.Count(item => item.Include && item.Verified == true), plan.Items.Sum(item => item.CopiedRows))
                    : Localization.T("Transfer.FinishedWithErrors");
                if (plan.IsFinished) DataTransferService.DeleteCheckpoint(plan, checkpointDirectory);
            }
            catch (OperationCanceledException)
            {
                statusLabel.Text = Localization.T("Transfer.Cancelled");
                unfinished = null;
                resumeButton.Visible = true;
            }
            catch (Exception ex)
            {
                statusLabel.Text = ExceptionMessageService.GetReason(ex);
            }
            finally
            {
                running = false;
                SetRunning(false);
                RenderProgress();
                reportButton.Enabled = true;
            }
        }

        private void SetRunning(bool value)
        {
            startButton.Enabled = !value;
            resumeButton.Enabled = !value;
            cancelButton.Enabled = value;
            tableGrid.ReadOnly = value;
            columnGrid.ReadOnly = value;
            batchSize.Enabled = !value;
            continueOnError.Enabled = !value;
        }

        private void RenderPlan()
        {
            loading = true;
            try
            {
                tableGrid.Rows.Clear();
                foreach (TransferItem item in plan.Items)
                {
                    int index = tableGrid.Rows.Add(item.Include, item.SourceTable, item.TargetTable, DataTransferService.ModeText(item.Mode), string.Empty, string.Empty);
                    tableGrid.Rows[index].Tag = item;
                }
                batchSize.Value = Math.Max(batchSize.Minimum, Math.Min(batchSize.Maximum, plan.BatchSize));
                continueOnError.Checked = plan.ContinueOnError;
            }
            finally
            {
                loading = false;
            }
            RenderProgress();
            ShowColumns();
        }

        private void RenderProgress()
        {
            foreach (DataGridViewRow row in tableGrid.Rows)
            {
                TransferItem item = row.Tag as TransferItem;
                if (item == null) continue;
                row.Cells["Progress"].Value = item.Status == TransferItemStatus.Pending ? string.Empty
                    : item.CopiedRows.ToString("N0", CultureInfo.InvariantCulture) + " / " + item.SourceRows.ToString("N0", CultureInfo.InvariantCulture);
                string notes = string.Join(" ", new[] { item.Error, item.Warning }.Where(text => !string.IsNullOrEmpty(text)));
                row.Cells["Result"].Value = DataTransferService.ResultText(item) + (notes.Length > 0 ? "：" + notes : string.Empty);
                row.Cells["Result"].Style.ForeColor = item.Status == TransferItemStatus.Failed || item.Verified == false
                    ? Color.FromArgb(180, 35, 24)
                    : item.Status == TransferItemStatus.Done ? Color.FromArgb(6, 118, 71) : SystemColors.ControlText;
            }
        }

        private void ReadTableRow(DataGridViewRow row)
        {
            TransferItem item = row.Tag as TransferItem;
            if (item == null) return;
            item.Include = row.Cells["Include"].Value is bool && (bool)row.Cells["Include"].Value;
            string targetName = Convert.ToString(row.Cells["Target"].Value);
            TransferMode mode = Modes.FirstOrDefault(value => DataTransferService.ModeText(value) == Convert.ToString(row.Cells["Mode"].Value));
            if (!string.Equals(targetName, item.TargetTable, StringComparison.Ordinal) || mode != item.Mode) item.Columns.Clear();
            item.TargetTable = targetName;
            item.Mode = mode;
            Guard(ShowColumns);
        }

        private void ShowColumns()
        {
            TransferItem item = tableGrid.CurrentRow == null ? null : tableGrid.CurrentRow.Tag as TransferItem;
            loading = true;
            try
            {
                columnGrid.Rows.Clear();
                if (item == null || item.Mode == TransferMode.CreateNew)
                {
                    columnHeader.Text = item == null ? Localization.T("Transfer.Columns.Hint") : Localization.T("Transfer.Columns.CreateNew");
                    return;
                }

                List<string> targetColumns = DataTransferService.ColumnNames(target.Database, target.DatabaseName, item.TargetTable);
                if (item.Columns.Count == 0)
                {
                    item.Columns.AddRange(DataTransferService.AutoMap(DataTransferService.ColumnNames(source.Database, source.DatabaseName, item.SourceTable), targetColumns));
                }
                DataGridViewComboBoxColumn choices = (DataGridViewComboBoxColumn)columnGrid.Columns["TargetColumn"];
                choices.Items.Clear();
                choices.Items.Add(SkipColumn);
                foreach (string name in targetColumns) choices.Items.Add(name);
                foreach (TransferColumnMapping mapping in item.Columns)
                {
                    string value = string.IsNullOrEmpty(mapping.TargetColumn) ? SkipColumn : mapping.TargetColumn;
                    if (!choices.Items.Contains(value)) choices.Items.Add(value);
                    int index = columnGrid.Rows.Add(mapping.SourceColumn, value);
                    columnGrid.Rows[index].Tag = mapping;
                }
                columnHeader.Text = Localization.Format("Transfer.Columns.Mapping", item.SourceTable, item.TargetTable);
            }
            finally
            {
                loading = false;
            }
        }

        private void ReadColumnRow(DataGridViewRow row)
        {
            TransferColumnMapping mapping = row.Tag as TransferColumnMapping;
            if (mapping == null) return;
            string value = Convert.ToString(row.Cells["TargetColumn"].Value);
            mapping.TargetColumn = value == SkipColumn ? null : value;
        }

        private void SaveReport()
        {
            using (SaveFileDialog dialog = new SaveFileDialog { Filter = "HTML (*.html)|*.html", FileName = "transfer-report-" + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".html" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                File.WriteAllText(dialog.FileName, BuildReport(), new UTF8Encoding(false));
                statusLabel.Text = Localization.Format("Transfer.ReportSaved", dialog.FileName);
            }
        }

        private bool TypedConfirm(string message, string expected)
        {
            using (Form dialog = new Form
            {
                Text = Text,
                Width = 620,
                Height = 360,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                StartPosition = FormStartPosition.CenterParent
            })
            {
                Label label = new Label { Dock = DockStyle.Fill, Padding = new Padding(12, 12, 12, 0), Text = message };
                TextBox input = new TextBox { Dock = DockStyle.Top };
                Button ok = new Button { Text = Localization.T("SchemaSync.ConfirmButton"), DialogResult = DialogResult.OK, Enabled = false, AutoSize = true };
                Button cancel = new Button { Text = Localization.T("Common.Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
                FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(8) };
                buttons.Controls.Add(ok);
                buttons.Controls.Add(cancel);
                Panel inputPanel = new Panel { Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(12, 4, 12, 4) };
                inputPanel.Controls.Add(input);
                input.TextChanged += (sender, args) => ok.Enabled = string.Equals(input.Text, expected, StringComparison.Ordinal);
                dialog.Controls.Add(label);
                dialog.Controls.Add(inputPanel);
                dialog.Controls.Add(buttons);
                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;
                ThemeManager.ApplyTo(dialog);
                return dialog.ShowDialog(this) == DialogResult.OK && string.Equals(input.Text, expected, StringComparison.Ordinal);
            }
        }

        private void Guard(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                statusLabel.Text = ExceptionMessageService.GetReason(ex);
            }
        }
    }
}
