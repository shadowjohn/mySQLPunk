using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    /// <summary>
    /// 兩個同類型資料庫之間的逐列資料比較與受控同步。比較只讀取；同步只修改目標、以單一交易執行，
    /// 包含刪除時必須輸入目標資料庫名稱。
    /// </summary>
    public sealed class DataComparisonForm : Form
    {
        private readonly SchemaComparisonEndpoint source;
        private readonly SchemaComparisonEndpoint target;
        private readonly CheckedListBox tableList;
        private readonly DataGridView resultGrid;
        private readonly CheckBox includeDeletes;
        private readonly Button compareButton;
        private readonly Button previewButton;
        private readonly Button syncButton;
        private readonly ToolStripStatusLabel statusLabel;
        private SchemaModelSnapshot targetSnapshot;
        private List<DataTableComparison> comparisons = new List<DataTableComparison>();
        private bool busy;

        public DataComparisonForm(SchemaComparisonEndpoint source, SchemaComparisonEndpoint target)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (target == null) throw new ArgumentNullException("target");
            this.source = source;
            this.target = target;

            Text = Localization.T("DataSync.WindowTitle");
            Width = 1040;
            Height = 700;
            MinimumSize = new Size(720, 460);
            StartPosition = FormStartPosition.CenterParent;

            Label header = new Label
            {
                Dock = DockStyle.Top,
                Height = 44,
                Padding = new Padding(10, 6, 10, 0),
                AutoEllipsis = true,
                Text = Localization.Format("DataSync.Header", source.DisplayName, target.DisplayName)
            };
            Label notice = new Label
            {
                Dock = DockStyle.Top,
                Height = 36,
                Padding = new Padding(10, 2, 10, 0),
                ForeColor = Color.FromArgb(181, 71, 8),
                Text = Localization.Format("DataSync.Notice", DataSyncCore.DefaultMaximumRows)
            };

            tableList = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
            Button selectAll = new Button { Text = Localization.T("DataSync.SelectAll"), AutoSize = true };
            Button selectNone = new Button { Text = Localization.T("DataSync.SelectNone"), AutoSize = true };
            compareButton = new Button { Text = Localization.T("DataSync.Compare"), AutoSize = true };
            FlowLayoutPanel listButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, Padding = new Padding(2) };
            listButtons.Controls.AddRange(new Control[] { selectAll, selectNone, compareButton });
            Panel left = new Panel { Dock = DockStyle.Left, Width = 280, Padding = new Padding(8) };
            left.Controls.Add(tableList);
            left.Controls.Add(listButtons);

            resultGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = SystemColors.Window,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };
            resultGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Table", HeaderText = Localization.T("DataSync.Column.Table"), FillWeight = 20 });
            resultGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = Localization.T("DataSync.Column.Status"), FillWeight = 40 });
            resultGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Warnings",
                HeaderText = Localization.T("DataSync.Column.Warnings"),
                FillWeight = 40,
                DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True }
            });

            includeDeletes = new CheckBox { Text = Localization.T("DataSync.IncludeDeletes"), AutoSize = true, Dock = DockStyle.Left };
            previewButton = new Button { Text = Localization.T("DataSync.Preview"), AutoSize = true, Enabled = false };
            syncButton = new Button { Text = Localization.T("DataSync.Sync"), AutoSize = true, Enabled = false };
            Button closeButton = new Button { Text = Localization.T("Common.Close"), AutoSize = true };
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
            actions.Controls.AddRange(new Control[] { closeButton, syncButton, previewButton });
            Panel bottom = new Panel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(8, 6, 8, 6) };
            bottom.Controls.Add(includeDeletes);
            bottom.Controls.Add(actions);

            StatusStrip statusStrip = new StatusStrip { SizingGrip = true };
            statusLabel = new ToolStripStatusLabel(string.Empty) { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            statusStrip.Items.Add(statusLabel);

            Controls.Add(resultGrid);
            Controls.Add(left);
            Controls.Add(bottom);
            Controls.Add(notice);
            Controls.Add(header);
            Controls.Add(statusStrip);

            selectAll.Click += (sender, args) => { for (int i = 0; i < tableList.Items.Count; i++) tableList.SetItemChecked(i, true); };
            selectNone.Click += (sender, args) => { for (int i = 0; i < tableList.Items.Count; i++) tableList.SetItemChecked(i, false); };
            compareButton.Click += (sender, args) => RunGuarded(() => CompareSelected());
            previewButton.Click += (sender, args) => ShowPreview();
            syncButton.Click += (sender, args) => ConfirmAndSync();
            closeButton.Click += (sender, args) => Close();
            FormClosing += (sender, args) => { if (busy) args.Cancel = true; };
            Shown += (sender, args) => RunGuarded(LoadTables);
            ThemeManager.ApplyTo(this);
        }

        public IList<DataTableComparison> Comparisons { get { return comparisons; } }

        public int TableCount { get { return tableList.Items.Count; } }

        /// <summary>讀取兩邊同名資料表；也供測試直接呼叫。</summary>
        public void LoadTables()
        {
            string reason;
            if (!DataSyncService.IsSupported(source.Database.ProviderName, target.Database.ProviderName, out reason))
            {
                statusLabel.Text = reason;
                compareButton.Enabled = false;
                return;
            }

            SchemaModelSnapshot sourceSnapshot = SchemaModelService.Load(source.Database, source.DatabaseName);
            targetSnapshot = SchemaModelService.Load(target.Database, target.DatabaseName);
            tableList.Items.Clear();
            foreach (string table in DataSyncService.CommonTables(sourceSnapshot, targetSnapshot)) tableList.Items.Add(table, true);
            statusLabel.Text = Localization.Format("DataSync.TablesFound", tableList.Items.Count);
        }

        /// <summary>比較勾選的資料表；也供測試直接呼叫。</summary>
        public List<DataTableComparison> CompareSelected()
        {
            List<string> selected = tableList.CheckedItems.Cast<string>().ToList();
            List<string> ordered = DataSyncService.OrderByDependencies(selected, targetSnapshot);
            comparisons = new List<DataTableComparison>();
            resultGrid.Rows.Clear();
            int done = 0;
            foreach (string table in ordered)
            {
                statusLabel.Text = Localization.Format("DataSync.Comparing", ++done, ordered.Count, table);
                Application.DoEvents();
                DataTableComparison comparison = DataSyncService.CompareTable(
                    source.Database, source.DatabaseName, target.Database, target.DatabaseName, targetSnapshot, table, DataSyncCore.DefaultMaximumRows);
                comparisons.Add(comparison);
                int index = resultGrid.Rows.Add(table, comparison.StatusText, string.Join(Environment.NewLine, comparison.Warnings.Take(5)));
                resultGrid.Rows[index].Cells["Status"].Style.ForeColor = comparison.IsSkipped
                    ? Color.FromArgb(181, 71, 8)
                    : comparison.Changes.Count == 0 ? Color.FromArgb(6, 118, 71) : Color.FromArgb(29, 78, 216);
            }

            bool hasChanges = comparisons.Any(item => !item.IsSkipped && item.Changes.Count > 0);
            previewButton.Enabled = hasChanges;
            syncButton.Enabled = hasChanges;
            statusLabel.Text = Localization.Format("DataSync.Compared",
                comparisons.Sum(item => item.Inserts), comparisons.Sum(item => item.Updates), comparisons.Sum(item => item.Deletes));
            return comparisons;
        }

        /// <summary>不經確認直接同步（確認由 ConfirmAndSync 負責）；也供測試直接呼叫。</summary>
        public DataSyncResult ApplySync(bool deletes)
        {
            DataSyncResult result = DataSyncService.Apply(target.Database, target.DatabaseName, comparisons, deletes);
            statusLabel.Text = result.Summary;
            return result;
        }

        private void ShowPreview()
        {
            bool deletes = includeDeletes.Checked;
            string text = Localization.T("DataSync.PreviewHeader") + Environment.NewLine + Environment.NewLine +
                          string.Join(Environment.NewLine, comparisons.Where(item => !item.IsSkipped && item.Changes.Count > 0)
                              .Select(item => DataSyncService.BuildPreview(target.Database, item, deletes)));
            using (Form preview = new Form { Text = Localization.T("DataSync.Preview"), Width = 960, Height = 620, StartPosition = FormStartPosition.CenterParent })
            {
                TextBox box = new TextBox
                {
                    Dock = DockStyle.Fill,
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Both,
                    WordWrap = false,
                    Font = new Font(FontFamily.GenericMonospace, 9.5f),
                    Text = text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine)
                };
                preview.Controls.Add(box);
                ThemeManager.ApplyTo(preview);
                preview.ShowDialog(this);
            }
        }

        private void ConfirmAndSync()
        {
            bool deletes = includeDeletes.Checked;
            List<DataTableComparison> active = comparisons.Where(item => !item.IsSkipped).ToList();
            int inserts = active.Sum(item => item.Inserts);
            int updates = active.Sum(item => item.Updates);
            int deleteCount = deletes ? active.Sum(item => item.Deletes) : 0;
            if (inserts + updates + deleteCount == 0)
            {
                statusLabel.Text = Localization.T("DataSync.NothingToSync");
                return;
            }

            string summary = Localization.Format("DataSync.ConfirmSync", target.DatabaseName, inserts, updates, deleteCount);
            bool confirmed = deleteCount > 0
                ? TypedConfirm(summary + Environment.NewLine + Localization.T("DataSync.ConfirmDestructive"), target.DatabaseName)
                : MessageBox.Show(this, summary, Localization.T("DataSync.Sync"), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;
            if (!confirmed) return;

            RunGuarded(() =>
            {
                DataSyncResult result = ApplySync(deletes);
                if (result.Succeeded)
                {
                    string done = result.Summary;
                    CompareSelected();
                    statusLabel.Text = done + " " + Localization.Format("DataSync.Recompared", statusLabel.Text);
                }
            });
        }

        private bool TypedConfirm(string message, string expected)
        {
            if (string.IsNullOrEmpty(expected)) return false;
            using (Form dialog = new Form
            {
                Text = Localization.T("DataSync.Sync"),
                Width = 560,
                Height = 240,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                StartPosition = FormStartPosition.CenterParent
            })
            {
                Label label = new Label { Dock = DockStyle.Top, Height = 96, Padding = new Padding(12, 12, 12, 0), Text = message };
                TextBox input = new TextBox { Dock = DockStyle.Top };
                Button ok = new Button { Text = Localization.T("SchemaSync.ConfirmButton"), DialogResult = DialogResult.OK, Enabled = false, AutoSize = true };
                Button cancel = new Button { Text = Localization.T("Common.Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
                FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(8) };
                buttons.Controls.Add(ok);
                buttons.Controls.Add(cancel);
                Panel inputPanel = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(12, 4, 12, 4) };
                inputPanel.Controls.Add(input);
                input.TextChanged += (sender, args) => ok.Enabled = string.Equals(input.Text, expected, StringComparison.Ordinal);
                dialog.Controls.Add(inputPanel);
                dialog.Controls.Add(label);
                dialog.Controls.Add(buttons);
                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;
                ThemeManager.ApplyTo(dialog);
                return dialog.ShowDialog(this) == DialogResult.OK && string.Equals(input.Text, expected, StringComparison.Ordinal);
            }
        }

        private void RunGuarded(Action action)
        {
            if (busy) return;
            busy = true;
            Cursor previous = Cursor;
            Cursor = Cursors.WaitCursor;
            compareButton.Enabled = previewButton.Enabled = syncButton.Enabled = false;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                statusLabel.Text = ExceptionMessageService.GetReason(ex);
            }
            finally
            {
                Cursor = previous;
                busy = false;
                compareButton.Enabled = tableList.Items.Count > 0;
                bool hasChanges = comparisons.Any(item => !item.IsSkipped && item.Changes.Count > 0);
                previewButton.Enabled = hasChanges;
                syncButton.Enabled = hasChanges;
            }
        }
    }
}
