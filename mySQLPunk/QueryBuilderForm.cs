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
    /// 視覺查詢建構器：把資料表加入畫布、勾選輸出欄位、拖曳欄位建立連接，並以表格設定別名、彙總、排序、分組與條件；
    /// SQL 窗格會即時更新，也可修改 SQL 後「由 SQL 更新」轉回圖形。只產生 SQL，不直接執行。
    /// </summary>
    public sealed class QueryBuilderForm : Form
    {
        private const string StarColumn = "*";
        private static readonly string[] Connectors = { "AND", "OR" };

        private readonly IDatabase database;
        private readonly string databaseName;
        private readonly string provider;
        private readonly ListBox tableList;
        private readonly CanvasPanel canvas;
        private readonly DataGridView columnGrid;
        private readonly DataGridView joinGrid;
        private readonly DataGridView conditionGrid;
        private readonly CheckBox distinctBox;
        private readonly NumericUpDown limitBox;
        private readonly TextBox sqlBox;
        private readonly ToolStripStatusLabel statusLabel;
        private readonly Dictionary<string, TableBox> boxes = new Dictionary<string, TableBox>(StringComparer.OrdinalIgnoreCase);
        private SchemaModelSnapshot snapshot;
        private QueryBuilderModel model = new QueryBuilderModel();
        private bool refreshing;

        public QueryBuilderForm(IDatabase database, string databaseName, Action<string> openInQuery)
        {
            if (database == null) throw new ArgumentNullException("database");
            this.database = database;
            this.databaseName = databaseName;
            provider = SchemaSyncScriptService.NormalizeProvider(database.ProviderName);

            Text = Localization.Format("QueryBuilder.WindowTitle", databaseName);
            Width = 1280;
            Height = 800;
            MinimumSize = new Size(900, 560);
            StartPosition = FormStartPosition.CenterParent;

            tableList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            Button addTable = new Button { Text = Localization.T("QueryBuilder.AddTable"), Dock = DockStyle.Bottom, Height = 30 };
            Panel left = new Panel { Dock = DockStyle.Left, Width = 220, Padding = new Padding(8) };
            left.Controls.Add(tableList);
            left.Controls.Add(new Label { Text = Localization.T("QueryBuilder.Tables"), Dock = DockStyle.Top, Height = 20 });
            left.Controls.Add(addTable);

            canvas = new CanvasPanel(this) { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(248, 250, 252) };
            Label canvasHint = new Label { Text = Localization.T("QueryBuilder.Canvas.Hint"), Dock = DockStyle.Top, Height = 20, ForeColor = Color.FromArgb(102, 112, 133) };

            columnGrid = CreateGrid();
            columnGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Output", HeaderText = Localization.T("QueryBuilder.Column.Output"), FillWeight = 8 });
            columnGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Field", HeaderText = Localization.T("QueryBuilder.Column.Field"), ReadOnly = true, FillWeight = 26 });
            columnGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Alias", HeaderText = Localization.T("QueryBuilder.Column.Alias"), FillWeight = 18 });
            columnGrid.Columns.Add(ComboColumn("Aggregate", "QueryBuilder.Column.Aggregate", Enum.GetValues(typeof(QueryAggregate)).Cast<QueryAggregate>().Select(AggregateText), 18));
            columnGrid.Columns.Add(ComboColumn("Sort", "QueryBuilder.Column.Sort", Enum.GetValues(typeof(QuerySort)).Cast<QuerySort>().Select(SortText), 18));
            columnGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "GroupBy", HeaderText = Localization.T("QueryBuilder.Column.GroupBy"), FillWeight = 8 });

            joinGrid = CreateGrid();
            joinGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Left", HeaderText = Localization.T("QueryBuilder.Column.Left"), ReadOnly = true, FillWeight = 35 });
            joinGrid.Columns.Add(ComboColumn("Type", "QueryBuilder.Column.JoinType", Enum.GetValues(typeof(QueryJoinType)).Cast<QueryJoinType>().Select(JoinText), 20));
            joinGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Right", HeaderText = Localization.T("QueryBuilder.Column.Right"), ReadOnly = true, FillWeight = 35 });

            conditionGrid = CreateGrid();
            conditionGrid.Columns.Add(ComboColumn("Connector", "QueryBuilder.Column.Connector", Connectors, 10));
            conditionGrid.Columns.Add(new DataGridViewComboBoxColumn { Name = "Field", HeaderText = Localization.T("QueryBuilder.Column.Field"), FillWeight = 26, FlatStyle = FlatStyle.Flat });
            conditionGrid.Columns.Add(ComboColumn("Aggregate", "QueryBuilder.Column.Aggregate", Enum.GetValues(typeof(QueryAggregate)).Cast<QueryAggregate>().Select(AggregateText), 14));
            conditionGrid.Columns.Add(ComboColumn("Operator", "QueryBuilder.Column.Operator", QueryBuilderService.Operators, 14));
            conditionGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = Localization.T("QueryBuilder.Column.Value"), FillWeight = 20 });
            conditionGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value2", HeaderText = Localization.T("QueryBuilder.Column.Value2"), FillWeight = 16 });

            Button addCondition = new Button { Text = Localization.T("QueryBuilder.AddCondition"), AutoSize = true };
            Button removeSelected = new Button { Text = Localization.T("QueryBuilder.RemoveSelected"), AutoSize = true };
            distinctBox = new CheckBox { Text = Localization.T("QueryBuilder.Distinct"), AutoSize = true, Padding = new Padding(12, 4, 0, 0) };
            limitBox = new NumericUpDown { Minimum = 0, Maximum = 1000000, Width = 90 };
            FlowLayoutPanel gridActions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(2) };
            gridActions.Controls.AddRange(new Control[]
            {
                addCondition, removeSelected, distinctBox,
                new Label { Text = Localization.T("QueryBuilder.Limit"), AutoSize = true, Padding = new Padding(12, 6, 0, 0) }, limitBox,
                new Label { Text = Localization.T("QueryBuilder.NoLimit"), AutoSize = true, Padding = new Padding(4, 6, 0, 0), ForeColor = Color.FromArgb(102, 112, 133) }
            });
            TabControl tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(TabWith("QueryBuilder.Tab.Columns", columnGrid));
            tabs.TabPages.Add(TabWith("QueryBuilder.Tab.Joins", joinGrid));
            tabs.TabPages.Add(TabWith("QueryBuilder.Tab.Conditions", conditionGrid));
            Panel bottom = new Panel { Dock = DockStyle.Fill };
            bottom.Controls.Add(tabs);
            bottom.Controls.Add(gridActions);

            SplitContainer center = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 380 };
            center.Panel1.Controls.Add(canvas);
            center.Panel1.Controls.Add(canvasHint);
            center.Panel2.Controls.Add(bottom);

            sqlBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                AcceptsReturn = true,
                AcceptsTab = true,
                Font = new Font(FontFamily.GenericMonospace, 10f)
            };
            Button applySql = new Button { Text = Localization.T("QueryBuilder.ApplySql"), AutoSize = true };
            Button copySql = new Button { Text = Localization.T("QueryBuilder.Copy"), AutoSize = true };
            Button openQuery = new Button { Text = Localization.T("QueryBuilder.OpenInQuery"), AutoSize = true, Enabled = openInQuery != null };
            Button closeButton = new Button { Text = Localization.T("Common.Close"), AutoSize = true };
            FlowLayoutPanel sqlActions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 70, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(2) };
            sqlActions.Controls.AddRange(new Control[] { applySql, copySql, openQuery, closeButton });
            Panel right = new Panel { Dock = DockStyle.Right, Width = 420, Padding = new Padding(8) };
            right.Controls.Add(sqlBox);
            right.Controls.Add(new Label { Text = Localization.T("QueryBuilder.Sql"), Dock = DockStyle.Top, Height = 20 });
            right.Controls.Add(sqlActions);

            StatusStrip statusStrip = new StatusStrip { SizingGrip = true };
            statusLabel = new ToolStripStatusLabel(string.Empty) { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            statusStrip.Items.Add(statusLabel);

            Controls.Add(center);
            Controls.Add(right);
            Controls.Add(left);
            Controls.Add(statusStrip);

            addTable.Click += (sender, args) => AddSelectedTable();
            tableList.DoubleClick += (sender, args) => AddSelectedTable();
            addCondition.Click += (sender, args) => AddConditionRow();
            removeSelected.Click += (sender, args) => RemoveSelectedRows(tabs.SelectedIndex);
            distinctBox.CheckedChanged += (sender, args) => { if (!refreshing) { model.Distinct = distinctBox.Checked; UpdateSql(); } };
            limitBox.ValueChanged += (sender, args) => { if (!refreshing) { model.Limit = limitBox.Value > 0 ? (int?)(int)limitBox.Value : null; UpdateSql(); } };
            foreach (DataGridView grid in new[] { columnGrid, joinGrid, conditionGrid })
            {
                DataGridView current = grid;
                grid.CurrentCellDirtyStateChanged += (sender, args) =>
                {
                    if (current.IsCurrentCellDirty && (current.CurrentCell is DataGridViewCheckBoxCell || current.CurrentCell is DataGridViewComboBoxCell))
                    {
                        current.CommitEdit(DataGridViewDataErrorContexts.Commit);
                    }
                };
                grid.DataError += (sender, args) => args.ThrowException = false;
            }
            columnGrid.CellValueChanged += (sender, args) => { if (!refreshing && args.RowIndex >= 0) ReadColumnRow(columnGrid.Rows[args.RowIndex]); };
            joinGrid.CellValueChanged += (sender, args) => { if (!refreshing && args.RowIndex >= 0) ReadJoinRow(joinGrid.Rows[args.RowIndex]); };
            conditionGrid.CellValueChanged += (sender, args) => { if (!refreshing && args.RowIndex >= 0) ReadConditionRow(conditionGrid.Rows[args.RowIndex]); };
            applySql.Click += (sender, args) => ApplySql(sqlBox.Text);
            copySql.Click += (sender, args) =>
            {
                if (sqlBox.Text.Length > 0) Clipboard.SetText(sqlBox.Text);
                statusLabel.Text = Localization.T("QueryBuilder.Copied");
            };
            openQuery.Click += (sender, args) =>
            {
                if (openInQuery != null && sqlBox.Text.Trim().Length > 0)
                {
                    openInQuery(sqlBox.Text);
                    Close();
                }
            };
            closeButton.Click += (sender, args) => Close();
            Shown += (sender, args) => Guard(LoadTables);
            ThemeManager.ApplyTo(this);
        }

        public QueryBuilderModel Model { get { return model; } }

        public string Sql { get { return sqlBox.Text; } }

        /// <summary>讀取資料表與欄位；也供測試直接呼叫。</summary>
        public void LoadTables()
        {
            snapshot = SchemaModelService.Load(database, databaseName);
            tableList.Items.Clear();
            foreach (SchemaTableModel table in snapshot.Tables) tableList.Items.Add(table.Name);
        }

        /// <summary>加入資料表並依外鍵自動連接；回傳別名。也供測試直接呼叫。</summary>
        public string AddTable(string tableName)
        {
            SchemaTableModel table = FindTableModel(tableName);
            if (table == null) throw new ArgumentException(Localization.Format("DataGen.Error.TableMissing", tableName));
            string alias = model.NextAlias(table.Name);
            int offset = model.Tables.Count;
            model.Tables.Add(new QueryBuilderTable { Name = table.Name, Alias = alias, X = 16 + (offset % 3) * 270, Y = 12 + (offset / 3) * 260 });
            int joins = 0;
            foreach (QueryBuilderTable existing in model.Tables.Where(item => item.Alias != alias).ToList())
            {
                foreach (SchemaRelationshipModel relation in snapshot.Relationships)
                {
                    if (string.Equals(relation.FromTable, table.Name, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(relation.ToTable, existing.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        model.Joins.Add(new QueryBuilderJoin { LeftAlias = existing.Alias, LeftColumn = relation.ToColumn, RightAlias = alias, RightColumn = relation.FromColumn });
                        joins++;
                    }
                    else if (string.Equals(relation.ToTable, table.Name, StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(relation.FromTable, existing.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        model.Joins.Add(new QueryBuilderJoin { LeftAlias = existing.Alias, LeftColumn = relation.FromColumn, RightAlias = alias, RightColumn = relation.ToColumn });
                        joins++;
                    }
                }
                if (joins > 0) break;
            }
            RebuildAll();
            statusLabel.Text = Localization.Format("QueryBuilder.TableAdded", table.Name, joins);
            return alias;
        }

        /// <summary>切換欄位輸出；也供測試直接呼叫。</summary>
        public void SetOutput(string alias, string column, bool output)
        {
            QueryBuilderColumn existing = model.Columns.FirstOrDefault(item => Same(item, alias, column) && item.Aggregate == QueryAggregate.None) ??
                                          model.Columns.FirstOrDefault(item => Same(item, alias, column));
            if (output)
            {
                if (existing == null) model.Columns.Add(new QueryBuilderColumn { TableAlias = alias, Column = column });
                else existing.Output = true;
            }
            else if (existing != null)
            {
                if (existing.Sort == QuerySort.None && !existing.GroupBy) model.Columns.Remove(existing);
                else existing.Output = false;
            }
            RebuildAll();
        }

        /// <summary>建立連接；也供測試與拖曳連接呼叫。</summary>
        public void AddJoin(string leftAlias, string leftColumn, string rightAlias, string rightColumn, QueryJoinType type)
        {
            if (string.Equals(leftAlias, rightAlias, StringComparison.OrdinalIgnoreCase) || leftColumn == StarColumn || rightColumn == StarColumn) return;
            // 連接方向固定為「先加入的表 → 後加入的表」，產生的 SQL 才與畫面一致。
            if (model.Tables.FindIndex(item => item.Alias == leftAlias) > model.Tables.FindIndex(item => item.Alias == rightAlias))
            {
                string alias = leftAlias, column = leftColumn;
                leftAlias = rightAlias;
                leftColumn = rightColumn;
                rightAlias = alias;
                rightColumn = column;
            }
            model.Joins.Add(new QueryBuilderJoin { LeftAlias = leftAlias, LeftColumn = leftColumn, RightAlias = rightAlias, RightColumn = rightColumn, Type = type });
            RebuildAll();
            statusLabel.Text = Localization.Format("QueryBuilder.JoinAdded", leftAlias + "." + leftColumn, rightAlias + "." + rightColumn);
        }

        /// <summary>新增條件；也供測試直接呼叫。</summary>
        public void AddCondition(QueryBuilderCondition condition)
        {
            model.Conditions.Add(condition);
            RebuildAll();
        }

        /// <summary>把 SQL 轉回圖形；失敗時保留原圖並回報原因。也供測試直接呼叫。</summary>
        public bool ApplySql(string sql)
        {
            QueryBuilderModel parsed;
            string error;
            List<string> known = snapshot == null ? new List<string>() : snapshot.Tables.Select(table => table.Name).ToList();
            if (!QueryBuilderService.TryParse(provider, sql, known, out parsed, out error))
            {
                statusLabel.Text = Localization.Format("QueryBuilder.ParseFailed", error);
                return false;
            }

            for (int index = 0; index < parsed.Tables.Count; index++)
            {
                QueryBuilderTable previous = model.FindTable(parsed.Tables[index].Alias);
                parsed.Tables[index].X = previous != null ? previous.X : 16 + (index % 3) * 270;
                parsed.Tables[index].Y = previous != null ? previous.Y : 12 + (index / 3) * 260;
            }
            model = parsed;
            RebuildAll();
            statusLabel.Text = Localization.T("QueryBuilder.Applied");
            return true;
        }

        private void AddSelectedTable()
        {
            if (tableList.SelectedItem == null) return;
            Guard(() => AddTable(Convert.ToString(tableList.SelectedItem)));
        }

        private void AddConditionRow()
        {
            QueryBuilderTable first = model.Tables.FirstOrDefault();
            if (first == null) return;
            SchemaTableModel table = FindTableModel(first.Name);
            string column = table == null || table.Columns.Count == 0 ? string.Empty : table.Columns.OrderBy(item => item.Ordinal).First().Name;
            AddCondition(new QueryBuilderCondition { TableAlias = first.Alias, Column = column });
        }

        private void RemoveSelectedRows(int tab)
        {
            DataGridView grid = tab == 0 ? columnGrid : tab == 1 ? joinGrid : conditionGrid;
            List<object> tags = grid.SelectedCells.Cast<DataGridViewCell>().Select(cell => grid.Rows[cell.RowIndex].Tag).Distinct().ToList();
            foreach (object tag in tags)
            {
                if (tag is QueryBuilderColumn) model.Columns.Remove((QueryBuilderColumn)tag);
                if (tag is QueryBuilderJoin) model.Joins.Remove((QueryBuilderJoin)tag);
                if (tag is QueryBuilderCondition) model.Conditions.Remove((QueryBuilderCondition)tag);
            }
            RebuildAll();
        }

        private void RemoveTable(string alias)
        {
            QueryBuilderTable table = model.FindTable(alias);
            if (table == null) return;
            model.Tables.Remove(table);
            model.Columns.RemoveAll(item => string.Equals(item.TableAlias, alias, StringComparison.OrdinalIgnoreCase));
            model.Joins.RemoveAll(item => string.Equals(item.LeftAlias, alias, StringComparison.OrdinalIgnoreCase) || string.Equals(item.RightAlias, alias, StringComparison.OrdinalIgnoreCase));
            model.Conditions.RemoveAll(item => string.Equals(item.TableAlias, alias, StringComparison.OrdinalIgnoreCase));
            RebuildAll();
        }

        private void RebuildAll()
        {
            refreshing = true;
            try
            {
                foreach (TableBox box in boxes.Values.ToList())
                {
                    if (model.FindTable(box.Alias) == null)
                    {
                        canvas.Controls.Remove(box);
                        boxes.Remove(box.Alias);
                        box.Dispose();
                    }
                }

                foreach (QueryBuilderTable table in model.Tables)
                {
                    TableBox box;
                    if (!boxes.TryGetValue(table.Alias, out box))
                    {
                        SchemaTableModel schema = FindTableModel(table.Name);
                        List<string> columns = new List<string> { StarColumn };
                        if (schema != null) columns.AddRange(schema.Columns.OrderBy(item => item.Ordinal).Select(item => item.Name));
                        box = new TableBox(this, table, columns);
                        boxes[table.Alias] = box;
                        canvas.Controls.Add(box);
                    }
                    box.Location = new Point(table.X + canvas.AutoScrollPosition.X, table.Y + canvas.AutoScrollPosition.Y);
                    box.SyncChecks(model);
                }

                // 先結束編輯並清掉目前儲存格，重建列時才不會把舊的編輯值寫回模型。
                foreach (DataGridView grid in new[] { columnGrid, joinGrid, conditionGrid })
                {
                    grid.CancelEdit();
                    grid.CurrentCell = null;
                }

                columnGrid.Rows.Clear();
                foreach (QueryBuilderColumn column in model.Columns)
                {
                    int index = columnGrid.Rows.Add(column.Output, column.TableAlias + "." + column.Column, column.Alias ?? string.Empty,
                        AggregateText(column.Aggregate), SortText(column.Sort), column.GroupBy);
                    columnGrid.Rows[index].Tag = column;
                }

                joinGrid.Rows.Clear();
                foreach (QueryBuilderJoin join in model.Joins)
                {
                    int index = joinGrid.Rows.Add(join.LeftAlias + "." + join.LeftColumn, JoinText(join.Type), join.RightAlias + "." + join.RightColumn);
                    joinGrid.Rows[index].Tag = join;
                }

                DataGridViewComboBoxColumn fieldColumn = (DataGridViewComboBoxColumn)conditionGrid.Columns["Field"];
                fieldColumn.Items.Clear();
                foreach (string field in AllFields()) fieldColumn.Items.Add(field);
                conditionGrid.Rows.Clear();
                foreach (QueryBuilderCondition condition in model.Conditions)
                {
                    string field = condition.TableAlias + "." + condition.Column;
                    if (!fieldColumn.Items.Contains(field)) fieldColumn.Items.Add(field);
                    int index = conditionGrid.Rows.Add(condition.Connector, field, AggregateText(condition.Aggregate), condition.Operator, condition.Value, condition.Value2);
                    conditionGrid.Rows[index].Tag = condition;
                }

                distinctBox.Checked = model.Distinct;
                limitBox.Value = model.Limit.HasValue ? Math.Min(limitBox.Maximum, model.Limit.Value) : 0;
            }
            finally
            {
                refreshing = false;
            }
            canvas.Invalidate();
            UpdateSql();
        }

        private void UpdateSql()
        {
            try
            {
                sqlBox.Text = QueryBuilderService.BuildSql(provider, model).Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
            }
            catch (FormatException exception)
            {
                statusLabel.Text = exception.Message;
            }
        }

        private void ReadColumnRow(DataGridViewRow row)
        {
            QueryBuilderColumn column = row.Tag as QueryBuilderColumn;
            if (column == null) return;
            column.Output = row.Cells["Output"].Value is bool && (bool)row.Cells["Output"].Value;
            column.Alias = Convert.ToString(row.Cells["Alias"].Value);
            column.Aggregate = ParseAggregate(Convert.ToString(row.Cells["Aggregate"].Value));
            column.Sort = Enum.GetValues(typeof(QuerySort)).Cast<QuerySort>().FirstOrDefault(item => SortText(item) == Convert.ToString(row.Cells["Sort"].Value));
            column.GroupBy = row.Cells["GroupBy"].Value is bool && (bool)row.Cells["GroupBy"].Value;
            TableBox box;
            if (boxes.TryGetValue(column.TableAlias ?? string.Empty, out box))
            {
                refreshing = true;
                box.SyncChecks(model);
                refreshing = false;
            }
            UpdateSql();
        }

        private void ReadJoinRow(DataGridViewRow row)
        {
            QueryBuilderJoin join = row.Tag as QueryBuilderJoin;
            if (join == null) return;
            join.Type = Enum.GetValues(typeof(QueryJoinType)).Cast<QueryJoinType>().FirstOrDefault(item => JoinText(item) == Convert.ToString(row.Cells["Type"].Value));
            canvas.Invalidate();
            UpdateSql();
        }

        private void ReadConditionRow(DataGridViewRow row)
        {
            QueryBuilderCondition condition = row.Tag as QueryBuilderCondition;
            if (condition == null) return;
            condition.Connector = Convert.ToString(row.Cells["Connector"].Value) == "OR" ? "OR" : "AND";
            string field = Convert.ToString(row.Cells["Field"].Value);
            int dot = field.IndexOf('.');
            if (dot > 0)
            {
                condition.TableAlias = field.Substring(0, dot);
                condition.Column = field.Substring(dot + 1);
            }
            condition.Aggregate = ParseAggregate(Convert.ToString(row.Cells["Aggregate"].Value));
            condition.Operator = Convert.ToString(row.Cells["Operator"].Value);
            condition.Value = Convert.ToString(row.Cells["Value"].Value) ?? string.Empty;
            condition.Value2 = Convert.ToString(row.Cells["Value2"].Value) ?? string.Empty;
            UpdateSql();
        }

        private IEnumerable<string> AllFields()
        {
            foreach (QueryBuilderTable table in model.Tables)
            {
                SchemaTableModel schema = FindTableModel(table.Name);
                if (schema == null) continue;
                foreach (SchemaColumnModel column in schema.Columns.OrderBy(item => item.Ordinal)) yield return table.Alias + "." + column.Name;
            }
        }

        private SchemaTableModel FindTableModel(string name)
        {
            return snapshot == null ? null : snapshot.Tables.FirstOrDefault(table => string.Equals(table.Name, name, StringComparison.OrdinalIgnoreCase));
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

        private static bool Same(QueryBuilderColumn item, string alias, string column)
        {
            return string.Equals(item.TableAlias, alias, StringComparison.OrdinalIgnoreCase) && string.Equals(item.Column, column, StringComparison.OrdinalIgnoreCase);
        }

        private static DataGridView CreateGrid()
        {
            return new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = SystemColors.Window
            };
        }

        private static DataGridViewComboBoxColumn ComboColumn(string name, string header, IEnumerable<string> items, float weight)
        {
            DataGridViewComboBoxColumn column = new DataGridViewComboBoxColumn { Name = name, HeaderText = Localization.T(header), FillWeight = weight, FlatStyle = FlatStyle.Flat };
            column.Items.AddRange(items.Cast<object>().ToArray());
            return column;
        }

        private static TabPage TabWith(string title, Control content)
        {
            TabPage page = new TabPage(Localization.T(title));
            page.Controls.Add(content);
            return page;
        }

        private static string AggregateText(QueryAggregate aggregate)
        {
            return aggregate == QueryAggregate.None ? "-" : QueryBuilderService.AggregateLabel(aggregate);
        }

        private static QueryAggregate ParseAggregate(string text)
        {
            return Enum.GetValues(typeof(QueryAggregate)).Cast<QueryAggregate>().FirstOrDefault(item => AggregateText(item) == text);
        }

        private static string SortText(QuerySort sort)
        {
            return Localization.T("QueryBuilder.Sort." + sort);
        }

        private static string JoinText(QueryJoinType type)
        {
            switch (type)
            {
                case QueryJoinType.Left: return "LEFT JOIN";
                case QueryJoinType.Right: return "RIGHT JOIN";
                case QueryJoinType.Full: return "FULL OUTER JOIN";
                default: return "INNER JOIN";
            }
        }

        /// <summary>雙緩衝畫布，負責畫出連接線。</summary>
        private sealed class CanvasPanel : Panel
        {
            private readonly QueryBuilderForm owner;

            public CanvasPanel(QueryBuilderForm owner)
            {
                this.owner = owner;
                DoubleBuffered = true;
                ResizeRedraw = true;
            }

            protected override void OnScroll(ScrollEventArgs se)
            {
                base.OnScroll(se);
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (Pen pen = new Pen(Color.FromArgb(29, 78, 216), 2f))
                using (Brush brush = new SolidBrush(Color.FromArgb(29, 78, 216)))
                using (Font font = new Font(Font.FontFamily, 7.5f))
                {
                    foreach (QueryBuilderJoin join in owner.model.Joins)
                    {
                        TableBox left, right;
                        if (!owner.boxes.TryGetValue(join.LeftAlias, out left) || !owner.boxes.TryGetValue(join.RightAlias, out right)) continue;
                        Point from = left.AnchorFor(join.LeftColumn, right.Left > left.Left);
                        Point to = right.AnchorFor(join.RightColumn, left.Left > right.Left);
                        e.Graphics.DrawLine(pen, from, to);
                        e.Graphics.FillEllipse(brush, to.X - 3, to.Y - 3, 6, 6);
                        string label = JoinText(join.Type).Replace(" JOIN", string.Empty).Replace(" OUTER", string.Empty);
                        e.Graphics.DrawString(label, font, brush, (from.X + to.X) / 2f + 2, (from.Y + to.Y) / 2f - 12);
                    }
                }
            }
        }

        /// <summary>畫布上的一張表：標題列可拖曳移動、欄位可勾選輸出，拖曳欄位到另一張表建立連接。</summary>
        private sealed class TableBox : Panel
        {
            private readonly CheckedListBox columns;
            private Point dragStart;
            private bool moving;
            private int pressedIndex = -1;

            public TableBox(QueryBuilderForm owner, QueryBuilderTable table, List<string> columnNames)
            {
                Alias = table.Alias;
                Width = 190;
                Height = Math.Min(240, 30 + columnNames.Count * 17 + 8);
                BorderStyle = BorderStyle.FixedSingle;
                BackColor = Color.White;

                Label title = new Label
                {
                    Dock = DockStyle.Top,
                    Height = 24,
                    Padding = new Padding(4, 4, 24, 0),
                    AutoEllipsis = true,
                    BackColor = Color.FromArgb(27, 43, 75),
                    ForeColor = Color.White,
                    Cursor = Cursors.SizeAll,
                    Text = string.Equals(table.Name.Split('.').Last(), table.Alias, StringComparison.Ordinal) ? table.Name : table.Name + " AS " + table.Alias
                };
                new ToolTip().SetToolTip(title, Localization.T("QueryBuilder.RemoveTable"));
                // 右上角的 X 直接畫在標題列上（按鈕子控制項在部分執行環境不會顯示）。
                title.Paint += (sender, args) =>
                {
                    using (Pen pen = new Pen(Color.White, 1.6f))
                    {
                        int x = title.Width - 16, y = 7;
                        args.Graphics.DrawLine(pen, x, y, x + 9, y + 9);
                        args.Graphics.DrawLine(pen, x + 9, y, x, y + 9);
                    }
                };
                columns = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false, BorderStyle = BorderStyle.None, AllowDrop = true };
                columns.Items.AddRange(columnNames.Cast<object>().ToArray());
                Controls.Add(columns);
                Controls.Add(title);

                title.MouseDown += (sender, args) =>
                {
                    if (args.X >= title.Width - 22)
                    {
                        owner.RemoveTable(Alias);
                        return;
                    }
                    moving = true;
                    dragStart = args.Location;
                };
                title.MouseMove += (sender, args) =>
                {
                    if (!moving) return;
                    Left += args.X - dragStart.X;
                    Top += args.Y - dragStart.Y;
                    owner.canvas.Invalidate();
                };
                title.MouseUp += (sender, args) =>
                {
                    moving = false;
                    table.X = Left - owner.canvas.AutoScrollPosition.X;
                    table.Y = Top - owner.canvas.AutoScrollPosition.Y;
                };
                columns.ItemCheck += (sender, args) =>
                {
                    if (owner.refreshing) return;
                    string column = Convert.ToString(columns.Items[args.Index]);
                    bool output = args.NewValue == CheckState.Checked;
                    owner.BeginInvoke((Action)(() => owner.SetOutput(Alias, column, output)));
                };
                columns.MouseDown += (sender, args) => { pressedIndex = columns.IndexFromPoint(args.Location); dragStart = args.Location; };
                columns.MouseMove += (sender, args) =>
                {
                    if (args.Button != MouseButtons.Left || pressedIndex < 0) return;
                    if (Math.Abs(args.X - dragStart.X) + Math.Abs(args.Y - dragStart.Y) < 8) return;
                    string payload = Alias + "\u0001" + Convert.ToString(columns.Items[pressedIndex]);
                    pressedIndex = -1;
                    columns.DoDragDrop(payload, DragDropEffects.Link);
                };
                columns.MouseUp += (sender, args) => pressedIndex = -1;
                columns.DragOver += (sender, args) =>
                {
                    string payload = args.Data.GetData(typeof(string)) as string;
                    args.Effect = payload != null && !payload.StartsWith(Alias + "\u0001", StringComparison.Ordinal) ? DragDropEffects.Link : DragDropEffects.None;
                };
                columns.DragDrop += (sender, args) =>
                {
                    string payload = args.Data.GetData(typeof(string)) as string;
                    int index = columns.IndexFromPoint(columns.PointToClient(new Point(args.X, args.Y)));
                    if (payload == null || index < 0) return;
                    string[] parts = payload.Split('\u0001');
                    owner.AddJoin(parts[0], parts[1], Alias, Convert.ToString(columns.Items[index]), QueryJoinType.Inner);
                };
                columns.TopIndexChangedHack(owner.canvas);
            }

            public string Alias { get; private set; }

            public void SyncChecks(QueryBuilderModel model)
            {
                for (int index = 0; index < columns.Items.Count; index++)
                {
                    string column = Convert.ToString(columns.Items[index]);
                    bool output = model.Columns.Any(item => item.Output && string.Equals(item.TableAlias, Alias, StringComparison.OrdinalIgnoreCase) &&
                                                            string.Equals(item.Column, column, StringComparison.OrdinalIgnoreCase));
                    if (columns.GetItemChecked(index) != output) columns.SetItemChecked(index, output);
                }
            }

            /// <summary>連接線端點：欄位所在列的左或右邊緣（捲出可見範圍時貼齊標題列）。</summary>
            public Point AnchorFor(string column, bool rightSide)
            {
                int index = columns.Items.IndexOf(column);
                int y;
                if (index < 0)
                {
                    y = Top + 12;
                }
                else
                {
                    Rectangle item = columns.GetItemRectangle(index);
                    int middle = columns.Top + item.Top + item.Height / 2;
                    y = Top + Math.Max(24, Math.Min(Height - 4, middle));
                }
                return new Point(rightSide ? Right : Left, y);
            }
        }
    }

    internal static class CheckedListBoxExtensions
    {
        /// <summary>CheckedListBox 捲動時沒有事件；以滑鼠滾輪與按鍵重畫畫布，讓連接線跟上。</summary>
        public static void TopIndexChangedHack(this CheckedListBox list, Control canvas)
        {
            list.MouseWheel += (sender, args) => canvas.Invalidate();
            list.KeyUp += (sender, args) => canvas.Invalidate();
            list.SelectedIndexChanged += (sender, args) => canvas.Invalidate();
        }
    }
}
