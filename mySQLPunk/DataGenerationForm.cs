using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    /// <summary>
    /// 多表測試資料產生器：逐欄設定規則（自動依型別、名稱、唯一性與外鍵決定），預覽只在記憶體產生，
    /// 寫入前需確認並以單一交易執行。
    /// </summary>
    public sealed class DataGenerationForm : Form
    {
        private static readonly DataGeneratorRuleKind[] RuleKinds =
        {
            DataGeneratorRuleKind.Auto,
            DataGeneratorRuleKind.DatabaseDefault,
            DataGeneratorRuleKind.Null,
            DataGeneratorRuleKind.Fixed,
            DataGeneratorRuleKind.Sequence,
            DataGeneratorRuleKind.Range,
            DataGeneratorRuleKind.List,
            DataGeneratorRuleKind.Pattern
        };

        private readonly IDatabase database;
        private readonly string databaseName;
        private readonly DataGridView tableGrid;
        private readonly DataGridView columnGrid;
        private readonly Label columnHeader;
        private readonly TextBox seedBox;
        private readonly Button previewButton;
        private readonly Button writeButton;
        private readonly ToolStripStatusLabel statusLabel;
        private readonly Dictionary<string, Dictionary<string, DataGeneratorRule>> rules =
            new Dictionary<string, Dictionary<string, DataGeneratorRule>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DataGeneratorTable> described = new Dictionary<string, DataGeneratorTable>(StringComparer.OrdinalIgnoreCase);
        private SchemaModelSnapshot snapshot;
        private string shownTable;
        private bool loadingColumns;
        private bool busy;

        public DataGenerationForm(IDatabase database, string databaseName, string connectionName)
        {
            if (database == null) throw new ArgumentNullException("database");
            this.database = database;
            this.databaseName = databaseName;

            Text = Localization.Format("DataGen.WindowTitle", databaseName);
            Width = 1180;
            Height = 720;
            MinimumSize = new Size(820, 480);
            StartPosition = FormStartPosition.CenterParent;

            Label header = new Label
            {
                Dock = DockStyle.Top,
                Height = 28,
                Padding = new Padding(10, 6, 10, 0),
                AutoEllipsis = true,
                ForeColor = Color.FromArgb(181, 71, 8),
                Text = Localization.Format("DataGen.Header", connectionName, databaseName)
            };
            Label notice = new Label { Dock = DockStyle.Top, Height = 40, Padding = new Padding(10, 2, 10, 0), Text = Localization.T("DataGen.Notice") };

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
            tableGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Generate", HeaderText = string.Empty, FillWeight = 12 });
            tableGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Table", HeaderText = Localization.T("DataGen.Column.Table"), ReadOnly = true, FillWeight = 60 });
            tableGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Rows", HeaderText = Localization.T("DataGen.Column.Rows"), FillWeight = 28 });
            Panel left = new Panel { Dock = DockStyle.Left, Width = 320, Padding = new Padding(8) };
            left.Controls.Add(tableGrid);

            columnHeader = new Label { Dock = DockStyle.Top, Height = 24, Font = new Font(Font, FontStyle.Bold) };
            columnGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
                BackgroundColor = SystemColors.Window,
                EditMode = DataGridViewEditMode.EditOnEnter
            };
            columnGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Column", HeaderText = Localization.T("DataGen.Column.Column"), ReadOnly = true, FillWeight = 16 });
            columnGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Type", HeaderText = Localization.T("DataGen.Column.Type"), ReadOnly = true, FillWeight = 16 });
            DataGridViewComboBoxColumn ruleColumn = new DataGridViewComboBoxColumn
            {
                Name = "Rule",
                HeaderText = Localization.T("DataGen.Column.Rule"),
                FillWeight = 14,
                FlatStyle = FlatStyle.Flat,
                DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton
            };
            ruleColumn.Items.AddRange(RuleKinds.Select(RuleLabel).Cast<object>().ToArray());
            columnGrid.Columns.Add(ruleColumn);
            columnGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Parameter", HeaderText = Localization.T("DataGen.Column.Parameter"), FillWeight = 20 });
            columnGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "NullPercent", HeaderText = Localization.T("DataGen.Column.NullPercent"), FillWeight = 7 });
            columnGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Auto",
                HeaderText = Localization.T("DataGen.Column.Auto"),
                ReadOnly = true,
                FillWeight = 27,
                DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True, ForeColor = Color.FromArgb(71, 84, 103) }
            });
            Label ruleHelp = new Label { Dock = DockStyle.Bottom, Height = 36, Padding = new Padding(0, 4, 0, 0), Text = Localization.T("DataGen.RuleHelp") };
            Panel right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4, 8, 8, 8) };
            right.Controls.Add(columnGrid);
            right.Controls.Add(columnHeader);
            right.Controls.Add(ruleHelp);

            seedBox = new TextBox { Width = 110 };
            previewButton = new Button { Text = Localization.T("DataGen.Preview"), AutoSize = true };
            writeButton = new Button { Text = Localization.T("DataGen.Write"), AutoSize = true };
            Button closeButton = new Button { Text = Localization.T("Common.Close"), AutoSize = true };
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
            actions.Controls.AddRange(new Control[] { closeButton, writeButton, previewButton, seedBox, new Label { Text = Localization.T("DataGen.Seed"), AutoSize = true, Padding = new Padding(0, 6, 0, 0) } });
            Panel bottom = new Panel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(8, 6, 8, 6) };
            bottom.Controls.Add(actions);

            StatusStrip statusStrip = new StatusStrip { SizingGrip = true };
            statusLabel = new ToolStripStatusLabel(string.Empty) { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            statusStrip.Items.Add(statusLabel);

            Controls.Add(right);
            Controls.Add(left);
            Controls.Add(bottom);
            Controls.Add(notice);
            Controls.Add(header);
            Controls.Add(statusStrip);

            tableGrid.SelectionChanged += (sender, args) =>
            {
                if (tableGrid.CurrentRow != null && !busy) RunGuarded(() => ShowColumns(Convert.ToString(tableGrid.CurrentRow.Cells["Table"].Value)));
            };
            tableGrid.CurrentCellDirtyStateChanged += (sender, args) =>
            {
                if (tableGrid.IsCurrentCellDirty && tableGrid.CurrentCell is DataGridViewCheckBoxCell) tableGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            columnGrid.CurrentCellDirtyStateChanged += (sender, args) =>
            {
                if (columnGrid.IsCurrentCellDirty && columnGrid.CurrentCell is DataGridViewComboBoxCell) columnGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            columnGrid.CellValueChanged += (sender, args) => { if (!loadingColumns && args.RowIndex >= 0) SaveRule(columnGrid.Rows[args.RowIndex]); };
            previewButton.Click += (sender, args) => ShowPreview();
            writeButton.Click += (sender, args) => ConfirmAndWrite();
            closeButton.Click += (sender, args) => Close();
            FormClosing += (sender, args) => { if (busy) args.Cancel = true; };
            Shown += (sender, args) => RunGuarded(LoadTables);
            ThemeManager.ApplyTo(this);
        }

        public int TableCount { get { return tableGrid.Rows.Count; } }

        /// <summary>讀取資料表清單；也供測試直接呼叫。</summary>
        public void LoadTables()
        {
            string reason;
            if (!DataGeneratorService.IsSupported(database.ProviderName, out reason))
            {
                statusLabel.Text = reason;
                previewButton.Enabled = writeButton.Enabled = false;
                return;
            }

            snapshot = SchemaModelService.Load(database, databaseName);
            tableGrid.Rows.Clear();
            foreach (SchemaTableModel table in snapshot.Tables)
            {
                tableGrid.Rows.Add(false, table.Name, "100");
            }
            statusLabel.Text = Localization.Format("DataGen.TablesFound", tableGrid.Rows.Count);
        }

        /// <summary>勾選資料表並設定筆數；也供測試直接呼叫。</summary>
        public void SetTable(string tableName, bool generate, int rows)
        {
            DataGridViewRow row = FindTableRow(tableName);
            if (row == null) throw new ArgumentException(Localization.Format("DataGen.Error.TableMissing", tableName));
            row.Cells["Generate"].Value = generate;
            row.Cells["Rows"].Value = rows.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>設定欄位規則（Auto 即清除）；也供測試直接呼叫。</summary>
        public void SetRule(string tableName, string columnName, DataGeneratorRule rule)
        {
            Dictionary<string, DataGeneratorRule> tableRules = RulesFor(tableName);
            if (rule == null || rule.IsDefault) tableRules.Remove(columnName);
            else tableRules[columnName] = rule;
            if (string.Equals(shownTable, tableName, StringComparison.OrdinalIgnoreCase)) ShowColumns(tableName);
        }

        /// <summary>顯示資料表的欄位規則；也供測試直接呼叫，回傳欄位數。</summary>
        public int ShowColumns(string tableName)
        {
            DataGeneratorTable table;
            if (!described.TryGetValue(tableName, out table))
            {
                table = DataGeneratorService.LoadTable(database, databaseName, snapshot, tableName, false);
                if (table == null) return 0;
                described[tableName] = table;
            }

            shownTable = tableName;
            Dictionary<string, DataGeneratorRule> tableRules = RulesFor(tableName);
            HashSet<string> uniqueSingles = new HashSet<string>(table.UniqueSets.Where(set => set.Length == 1).Select(set => set[0]), StringComparer.OrdinalIgnoreCase);
            loadingColumns = true;
            try
            {
                columnGrid.Rows.Clear();
                foreach (DataGeneratorColumn column in table.Columns)
                {
                    DataGeneratorRule rule;
                    if (!tableRules.TryGetValue(column.Name, out rule)) rule = DataGeneratorRule.Auto;
                    DataGeneratorForeignKey foreignKey = table.ForeignKeys.FirstOrDefault(item => item.Columns.Contains(column.Name, StringComparer.OrdinalIgnoreCase));
                    List<string> flags = new List<string> { column.TypeText };
                    if (column.IsPrimaryKey) flags.Add("PK");
                    if (uniqueSingles.Contains(column.Name) && !column.IsPrimaryKey) flags.Add("UNIQUE");
                    if (foreignKey != null) flags.Add("FK");
                    flags.Add(column.IsNullable ? "NULL" : "NOT NULL");
                    int index = columnGrid.Rows.Add(
                        column.Name,
                        string.Join(" · ", flags),
                        RuleLabel(rule.Kind),
                        rule.Text,
                        rule.NullPercent.ToString(CultureInfo.InvariantCulture),
                        DataGeneratorCore.DescribeAuto(column, uniqueSingles.Contains(column.Name), foreignKey));
                    DataGridViewRow row = columnGrid.Rows[index];
                    row.Tag = column.Name;
                    if (column.IsComputed) row.ReadOnly = true;
                    row.Cells["NullPercent"].ReadOnly = !column.IsNullable || column.IsPrimaryKey || uniqueSingles.Contains(column.Name);
                }
            }
            finally
            {
                loadingColumns = false;
            }

            columnHeader.Text = tableName;
            statusLabel.Text = Localization.Format("DataGen.ColumnsLoaded", tableName, table.Columns.Count);
            return table.Columns.Count;
        }

        /// <summary>依目前勾選與規則產生（不寫入）；也供測試直接呼叫。</summary>
        public DataGenerationResult Generate(int? seed)
        {
            List<DataGeneratorPlan> plans = new List<DataGeneratorPlan>();
            foreach (DataGridViewRow row in tableGrid.Rows)
            {
                if (!(row.Cells["Generate"].Value is bool) || !(bool)row.Cells["Generate"].Value) continue;
                string tableName = Convert.ToString(row.Cells["Table"].Value);
                int count;
                if (!int.TryParse(Convert.ToString(row.Cells["Rows"].Value), NumberStyles.Integer, CultureInfo.InvariantCulture, out count)) count = 0;
                plans.Add(new DataGeneratorPlan(tableName, count, RulesFor(tableName)));
            }
            return DataGeneratorService.Generate(database, databaseName, snapshot, plans, seed);
        }

        /// <summary>寫入先前產生的結果（確認由 ConfirmAndWrite 負責）；也供測試直接呼叫。</summary>
        public DataSyncResult Write(DataGenerationResult result)
        {
            return DataGeneratorService.Apply(database, databaseName, result);
        }

        private void SaveRule(DataGridViewRow row)
        {
            if (shownTable == null || row.Tag == null) return;
            string label = Convert.ToString(row.Cells["Rule"].Value);
            DataGeneratorRuleKind kind = RuleKinds.FirstOrDefault(item => RuleLabel(item) == label);
            int nullPercent;
            if (!int.TryParse(Convert.ToString(row.Cells["NullPercent"].Value), NumberStyles.Integer, CultureInfo.InvariantCulture, out nullPercent)) nullPercent = 0;
            DataGeneratorRule rule = new DataGeneratorRule(kind, Convert.ToString(row.Cells["Parameter"].Value) ?? string.Empty, nullPercent);
            Dictionary<string, DataGeneratorRule> tableRules = RulesFor(shownTable);
            if (rule.IsDefault) tableRules.Remove((string)row.Tag);
            else tableRules[(string)row.Tag] = rule;
        }

        private bool TryReadSeed(out int? seed)
        {
            seed = null;
            string text = seedBox.Text.Trim();
            if (text.Length == 0) return true;
            int value;
            if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
            {
                statusLabel.Text = Localization.T("DataGen.BadSeed");
                return false;
            }
            seed = value;
            return true;
        }

        private DataGenerationResult GenerateForUi()
        {
            int? seed;
            if (!TryReadSeed(out seed)) return null;
            statusLabel.Text = Localization.T("DataGen.Generating");
            Application.DoEvents();
            DataGenerationResult result = Generate(seed);
            if (!result.Succeeded)
            {
                statusLabel.Text = Localization.Format("DataGen.Failed", result.Error);
                return null;
            }
            statusLabel.Text = Localization.Format("DataGen.Generated", result.TotalRows, Summarize(result));
            return result;
        }

        private void ShowPreview()
        {
            DataGenerationResult result = null;
            RunGuarded(() => result = GenerateForUi());
            if (result == null) return;
            string text = Localization.T("DataGen.PreviewHeader") + Environment.NewLine +
                          string.Concat(result.Warnings.Select(warning => "-- " + SchemaSyncScriptService.SingleLine(warning) + Environment.NewLine)) + Environment.NewLine +
                          DataGeneratorService.BuildPreview(database, result, 100);
            using (Form preview = new Form { Text = Localization.T("DataGen.Preview").Replace("&&", "&"), Width = 960, Height = 620, StartPosition = FormStartPosition.CenterParent })
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

        private void ConfirmAndWrite()
        {
            DataGenerationResult result = null;
            RunGuarded(() => result = GenerateForUi());
            if (result == null) return;
            string message = Localization.Format("DataGen.ConfirmWrite", databaseName, result.TotalRows, Summarize(result));
            if (result.Warnings.Count > 0)
            {
                message += Environment.NewLine + Environment.NewLine + Localization.T("DataGen.ConfirmWarnings") + Environment.NewLine + string.Join(Environment.NewLine, result.Warnings);
            }
            if (MessageBox.Show(this, message, Localization.T("DataGen.Write").Replace("&&", "&"), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            {
                statusLabel.Text = Localization.T("DataGen.Cancelled");
                return;
            }

            RunGuarded(() =>
            {
                DataSyncResult applied = Write(result);
                statusLabel.Text = applied.Succeeded
                    ? Localization.Format("DataGen.Written", result.TotalRows, Summarize(result))
                    : Localization.Format("DataGen.WriteFailed", applied.Summary);
            });
        }

        private static string Summarize(DataGenerationResult result)
        {
            return string.Join(", ", result.Tables.Select(table => table.TableName + " " + table.Inserts.ToString("N0", CultureInfo.InvariantCulture)));
        }

        private Dictionary<string, DataGeneratorRule> RulesFor(string tableName)
        {
            Dictionary<string, DataGeneratorRule> tableRules;
            if (!rules.TryGetValue(tableName, out tableRules))
            {
                tableRules = new Dictionary<string, DataGeneratorRule>(StringComparer.OrdinalIgnoreCase);
                rules[tableName] = tableRules;
            }
            return tableRules;
        }

        private DataGridViewRow FindTableRow(string tableName)
        {
            return tableGrid.Rows.Cast<DataGridViewRow>()
                .FirstOrDefault(row => string.Equals(Convert.ToString(row.Cells["Table"].Value), tableName, StringComparison.OrdinalIgnoreCase));
        }

        private static string RuleLabel(DataGeneratorRuleKind kind)
        {
            return Localization.T("DataGen.Rule." + kind);
        }

        private void RunGuarded(Action action)
        {
            if (busy) return;
            busy = true;
            Cursor previous = Cursor;
            Cursor = Cursors.WaitCursor;
            previewButton.Enabled = writeButton.Enabled = false;
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
                previewButton.Enabled = writeButton.Enabled = tableGrid.Rows.Count > 0;
            }
        }
    }
}
