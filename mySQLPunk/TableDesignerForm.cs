using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using mySQLPunk.lib;
using System.Diagnostics;
using utility;
namespace mySQLPunk
{
    public class TableDesignerForm : Form, IDockableForm
    {
        private IDatabase _db;
        private string _databaseName;
        private string _tableName;
        
        private DataGridView dgvColumns;
        private DataGridView dgvIndexes;
        private DataTable _originalDt; // 儲存欄位原始狀態
        private DataTable _originalIdxDt; // 儲存索引原始狀態
        private Form1 _mainHost;
        private bool _isDocked;
        private ToolStripButton btnFloat;
        private ToolStripButton btnDock;

        private TabControl tcMain;
        private TabPage tpColumns, tpIndexes, tpOptions, tpComment, tpSqlPreview;
        private RichTextBox rtbSqlPreview;
        private TextBox txtTableName;
        private TextBox txtTableComment;
        private ComboBox cbEngine;
        private Panel pnlColumnProperties; // 屬性面板
        private bool _isModified = false;
        private bool IsNewTable => string.IsNullOrWhiteSpace(_tableName);

        public bool IsModified 
        { 
            get => _isModified; 
            set {
                _isModified = value;
                UpdateTitle();
            }
        }
        private myinclude my = new myinclude();        
        public TableDesignerForm(IDatabase db, string databaseName, string tableName)
        {
            _db = db;
            _databaseName = databaseName;
            _tableName = tableName;
            InitializeComponent();
            ApplyLanguage();
            ApplyTheme();
            UpdateTitle();
            LoadColumns();
            LoadIndexes();

            dgvColumns.CellValueChanged += (s, e) => MarkAsModified();
            dgvIndexes.CellValueChanged += (s, e) => MarkAsModified();
            // 監聽刪除行等操作
            dgvColumns.RowsRemoved += (s, e) => MarkAsModified();
            dgvIndexes.RowsRemoved += (s, e) => MarkAsModified();
        }

        private void UpdateTitle()
        {
            string prefix = _isModified ? "* " : "";
            string tableTitle = _tableName;
            if (string.IsNullOrWhiteSpace(tableTitle) && txtTableName != null)
            {
                tableTitle = txtTableName.Text.Trim();
            }
            if (string.IsNullOrWhiteSpace(tableTitle))
            {
                tableTitle = Localization.T("Designer.NewTable");
            }

            this.Text = $"{prefix}{Localization.T("Designer.DesignTable")} - {tableTitle}";
            if (_mainHost != null) _mainHost.UpdateTabTitle(this);
        }

        private void MarkAsModified()
        {
            if (!_isModified) IsModified = true;
        }

        private void InitializeComponent()
        {
            this.Size = new Size(1000, 700);
            this.StartPosition = FormStartPosition.CenterParent;
            this.BackColor = Color.FromArgb(240, 240, 240);

            // 頂部工具列
            ToolStrip tsTop = new ToolStrip() 
            { 
                Height = 40, 
                AutoSize = false, 
                GripStyle = ToolStripGripStyle.Hidden, 
                Padding = new Padding(5),
                ImageScalingSize = new Size(20, 20) 
            };
            
            string iconPath = my.pwd() + "\\image\\";
            ToolStripButton btnSave = new ToolStripButton(Localization.T("Designer.Save"), GetIcon(iconPath + "save.png"), BtnSave_Click);
            
            ToolStripSeparator sep1 = new ToolStripSeparator();
            ToolStripButton btnAddCol = new ToolStripButton(Localization.T("Designer.AddColumn"), GetIcon(iconPath + "add.png"));
            ToolStripButton btnInsertCol = new ToolStripButton(Localization.T("Designer.InsertColumn"), GetIcon(iconPath + "insert.png"));
            ToolStripButton btnDelCol = new ToolStripButton(Localization.T("Designer.DeleteColumn"), GetIcon(iconPath + "delete.png"));
            
            ToolStripSeparator sep2 = new ToolStripSeparator();
            ToolStripButton btnMoveUp = new ToolStripButton(Localization.T("Designer.MoveUp"), GetIcon(iconPath + "up.png"));
            ToolStripButton btnMoveDown = new ToolStripButton(Localization.T("Designer.MoveDown"), GetIcon(iconPath + "down.png"));

            btnFloat = new ToolStripButton(Localization.T("Query.Float"), null, (s, e) => _mainHost?.FloatDockableForm(this)) { Alignment = ToolStripItemAlignment.Right };
            btnDock = new ToolStripButton(Localization.T("Query.Dock"), null, (s, e) => _mainHost?.DockDockableForm(this)) { Visible = false, Alignment = ToolStripItemAlignment.Right };
            
            tsTop.Items.AddRange(new ToolStripItem[] { 
                btnSave, sep1, btnAddCol, btnInsertCol, btnDelCol, sep2, btnMoveUp, btnMoveDown, btnFloat, btnDock 
            });

            tcMain = new TabControl() { Dock = DockStyle.Fill, Padding = new Point(12, 5) };
            tcMain.SelectedIndexChanged += (s, e) => {
                bool isCol = tcMain.SelectedTab == tpColumns;
                bool isIdx = tcMain.SelectedTab == tpIndexes;
                
                btnAddCol.Text = isCol ? Localization.T("Designer.AddColumn") : (isIdx ? Localization.T("Designer.AddIndex") : Localization.T("Query.Add"));
                btnInsertCol.Visible = isCol; // 索引通常沒有「插入」概念
                btnDelCol.Text = isCol ? Localization.T("Designer.DeleteColumn") : (isIdx ? Localization.T("Designer.DeleteIndex") : Localization.T("Query.Delete"));
                
                if (tcMain.SelectedTab == tpSqlPreview) GeneratePreviewSql();
            };
            
            // 1. 欄位分頁
            tpColumns = new TabPage(Localization.T("Designer.Columns"));
            SplitContainer splitColumns = new SplitContainer() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 350 };
            dgvColumns = new DataGridView() 
            { 
                Dock = DockStyle.Fill, 
                BackgroundColor = Color.White, 
                BorderStyle = BorderStyle.None, 
                RowHeadersWidth = 25,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            pnlColumnProperties = new Panel() { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(10) };
            pnlColumnProperties.Controls.Add(new Label() { Text = Localization.T("Designer.ColumnProperties"), ForeColor = Color.Gray, Location = new Point(10, 10), AutoSize = true });
            
            splitColumns.Panel1.Controls.Add(dgvColumns);
            splitColumns.Panel2.Controls.Add(pnlColumnProperties);
            tpColumns.Controls.Add(splitColumns);

            // 2. 索引分頁
            tpIndexes = new TabPage(Localization.T("Designer.Indexes"));
            dgvIndexes = new DataGridView() 
            { 
                Dock = DockStyle.Fill, 
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.None,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                AllowUserToAddRows = true
            };
            tpIndexes.Controls.Add(dgvIndexes);

            // 3. 選項分頁
            tpOptions = new TabPage(Localization.T("Designer.Options"));
            TableLayoutPanel tlpOptions = new TableLayoutPanel() { Dock = DockStyle.Top, Height = 200, RowCount = 4, ColumnCount = 2, Padding = new Padding(20) };
            tlpOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            tlpOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            tlpOptions.Controls.Add(new Label() { Text = Localization.T("Designer.TableName"), Anchor = AnchorStyles.Left, AutoSize = true }, 0, 0);
            txtTableName = new TextBox() { Width = 260, Text = _tableName, ReadOnly = !IsNewTable };
            txtTableName.TextChanged += (s, e) =>
            {
                if (IsNewTable) MarkAsModified();
                UpdateTitle();
            };
            tlpOptions.Controls.Add(txtTableName, 1, 0);

            tlpOptions.Controls.Add(new Label() { Text = Localization.T("Designer.Engine"), Anchor = AnchorStyles.Left, AutoSize = true }, 0, 1);
            cbEngine = new ComboBox() { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
            cbEngine.Items.AddRange(new object[] { "InnoDB", "MyISAM", "MEMORY" });
            cbEngine.SelectedItem = "InnoDB";
            cbEngine.SelectedIndexChanged += (s, e) => MarkAsModified();
            tlpOptions.Controls.Add(cbEngine, 1, 1);
            tpOptions.Controls.Add(tlpOptions);

            // 4. 註解分頁
            tpComment = new TabPage(Localization.T("Designer.Comment"));
            txtTableComment = new TextBox() { Dock = DockStyle.Fill, Multiline = true };
            txtTableComment.TextChanged += (s, e) => MarkAsModified();
            tpComment.Controls.Add(txtTableComment);

            // 5. SQL 預覽分頁
            tpSqlPreview = new TabPage(Localization.T("Designer.SqlPreview"));
            rtbSqlPreview = new RichTextBox() { Dock = DockStyle.Fill, ReadOnly = true, Font = new Font("Consolas", 11), BackColor = Color.White };
            tpSqlPreview.Controls.Add(rtbSqlPreview);

            tcMain.TabPages.AddRange(new TabPage[] { tpColumns, tpIndexes, tpOptions, tpComment, tpSqlPreview });
            
            // 配置工具列通用事件
            btnAddCol.Click += (s, e) => {
                if (tcMain.SelectedTab == tpColumns) AddColumn(false);
                else if (tcMain.SelectedTab == tpIndexes) AddIndex();
            };
            btnInsertCol.Click += (s, e) => {
                if (tcMain.SelectedTab == tpColumns) AddColumn(true);
            };
            btnDelCol.Click += (s, e) => {
                if (tcMain.SelectedTab == tpColumns) DeleteColumn();
                else if (tcMain.SelectedTab == tpIndexes) DeleteIndex();
            };
            btnMoveUp.Click += (s, e) => {
                if (tcMain.SelectedTab == tpColumns) MoveColumn(-1);
                else if (tcMain.SelectedTab == tpIndexes) MoveIndex(-1);
            };
            btnMoveDown.Click += (s, e) => {
                if (tcMain.SelectedTab == tpColumns) MoveColumn(1);
                else if (tcMain.SelectedTab == tpIndexes) MoveIndex(1);
            };

            dgvIndexes.CellClick += DgvIndexes_CellClick;

            this.Controls.Add(tcMain);
            this.Controls.Add(tsTop);
        }

        // 供外部調用，回傳 true 代表可以關閉，false 代表取消關閉
        public bool ConfirmClose()
        {
            if (!_isModified) return true;

            var result = MessageBox.Show(
                Localization.Format("Designer.ConfirmCloseChanges", _tableName),
                Localization.T("Common.Confirm"),
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question);
            if (result == DialogResult.Yes)
            {
                BtnSave_Click(null, null);
                return !_isModified; // 如果儲存成功且重置了 _isModified，則允許關閉
            }
            else if (result == DialogResult.No)
            {
                return true; // 使用者選擇不儲存，允許關閉
            }
            return false; // 取消
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_isDocked) 
            {
                base.OnFormClosing(e);
                return;
            }

            if (!ConfirmClose())
            {
                e.Cancel = true;
            }
            else
            {
                base.OnFormClosing(e);
            }
        }

        private void DgvIndexes_CellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (dgvIndexes.Columns[e.ColumnIndex].Name == "欄位")
            {
                // 獲取目前所有可選欄位
                List<string> allCols = new List<string>();
                DataTable colDt = (DataTable)dgvColumns.DataSource;
                foreach (DataRow r in colDt.Rows) allCols.Add(r["Name"].ToString());

                string currentVal = dgvIndexes.CurrentCell.Value?.ToString() ?? "";
                
                using (Form f = new Form() { Text = Localization.T("Designer.SelectIndexColumns"), Size = new Size(300, 400), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog })
                {
                    CheckedListBox clb = new CheckedListBox() { Dock = DockStyle.Fill };
                    foreach (string c in allCols) clb.Items.Add(c, currentVal.Contains(c));
                    
                    Button btnOk = new Button() { Text = Localization.T("Common.OK"), Dock = DockStyle.Bottom };
                    btnOk.Click += (s, ev) => {
                        List<string> selected = new List<string>();
                        foreach (var item in clb.CheckedItems) selected.Add(item.ToString());
                        dgvIndexes.CurrentCell.Value = string.Join(", ", selected);
                        f.Close();
                    };
                    f.Controls.Add(clb);
                    f.Controls.Add(btnOk);
                    f.ShowDialog();
                }
            }
        }

        public void SetMainHost(Form1 mainHost) => _mainHost = mainHost;
        public string GetDisplayTitle() => this.Text;

        public void PrepareForDocking()
        {
            if (Visible) Hide();
            if (Parent != null) Parent.Controls.Remove(this);
            FormBorderStyle = FormBorderStyle.None;
            TopLevel = false;
            ShowInTaskbar = false;
            _isDocked = true;
            btnFloat.Visible = true;
            btnDock.Visible = false;
        }

        public void PrepareForFloating()
        {
            if (Visible) Hide();
            if (Parent != null) Parent.Controls.Remove(this);
            Dock = DockStyle.None;
            TopLevel = true;
            FormBorderStyle = FormBorderStyle.Sizable;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterParent;
            _isDocked = false;
            btnFloat.Visible = false;
            btnDock.Visible = true;
        }

        private const int WM_MOVING = 0x0216;
        private const int WM_EXITSIZEMOVE = 0x0232;
        private bool _wasOverDropZone = false;

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (_isDocked || _mainHost == null) return;

            if (m.Msg == WM_MOVING)
            {
                bool isOver = _mainHost.IsPointInTabDropArea(Cursor.Position);
                if (isOver != _wasOverDropZone)
                {
                    _wasOverDropZone = isOver;
                    if (isOver) _mainHost.ShowDockHint();
                    else _mainHost.HideDockHint();
                }
            }
            else if (m.Msg == WM_EXITSIZEMOVE)
            {
                _mainHost.HideDockHint();
                _wasOverDropZone = false;
                if (_mainHost.IsPointInTabDropArea(Cursor.Position))
                    _mainHost.DockDockableForm(this);
            }
        }

        private void LoadColumns()
        {
            try
            {
                DataTable displayDt = CreateColumnsDisplayTable();

                if (IsNewTable)
                {
                    _originalDt = displayDt.Copy();
                    BindColumns(displayDt);
                    return;
                }

                DataTable rawDt = _db.GetColumns(_databaseName, _tableName);

                foreach (DataRow row in rawDt.Rows)
                {
                    DataRow newRow = displayDt.NewRow();
                    PopulateDesignerColumnRow(newRow, row);
                    displayDt.Rows.Add(newRow);
                }

                _originalDt = displayDt.Copy();
                BindColumns(displayDt);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"無法載入欄位資訊: {ex.Message}");
            }
        }

        private DataTable CreateColumnsDisplayTable()
        {
            DataTable displayDt = new DataTable();
            displayDt.Columns.Add("Name");
            displayDt.Columns.Add("Type");
            displayDt.Columns.Add("Length");
            displayDt.Columns.Add("Decimals");
            displayDt.Columns.Add("NotNull", typeof(bool));
            displayDt.Columns.Add("PK", typeof(bool));
            displayDt.Columns.Add("Default");
            displayDt.Columns.Add("Comment");
            displayDt.Columns.Add("_OldName");
            return displayDt;
        }

        private void PopulateDesignerColumnRow(DataRow newRow, DataRow row)
        {
            if (_db is my_mysql)
            {
                newRow["Name"] = row["Field"];
                newRow["_OldName"] = row["Field"];
                string typeStr = row["Type"].ToString();
                if (typeStr.Contains("("))
                {
                    string rawType = typeStr.Split('(')[0];
                    string fullLen = typeStr.Split('(')[1].Replace(")", "");
                    newRow["Type"] = rawType;
                    if (fullLen.Contains(","))
                    {
                        newRow["Length"] = fullLen.Split(',')[0];
                        newRow["Decimals"] = fullLen.Split(',')[1];
                    }
                    else
                    {
                        newRow["Length"] = fullLen;
                    }
                }
                else
                {
                    newRow["Type"] = typeStr;
                }
                newRow["NotNull"] = (row["Null"].ToString() == "NO");
                newRow["PK"] = (row["Key"].ToString() == "PRI");
                newRow["Default"] = row["Default"];
                newRow["Comment"] = row["Comment"];
                return;
            }

            if (_db is my_postgresql)
            {
                newRow["Name"] = row["column_name"];
                newRow["_OldName"] = row["column_name"];
                newRow["Type"] = row["data_type"];
                newRow["Length"] = GetLengthFromMetadata(row, "character_maximum_length", "numeric_precision");
                newRow["Decimals"] = GetMetadataString(row, "numeric_scale");
                newRow["NotNull"] = (row["is_nullable"].ToString() == "NO");
                newRow["Default"] = row["column_default"];
                newRow["Comment"] = GetMetadataString(row, "Comment", "comment");
                return;
            }

            if (_db is my_sqlite)
            {
                newRow["Name"] = row["name"];
                newRow["_OldName"] = row["name"];
                newRow["Type"] = row["type"];
                newRow["NotNull"] = (row["notnull"].ToString() == "1");
                newRow["PK"] = (row["pk"].ToString() == "1");
                newRow["Default"] = row["dflt_value"];
                return;
            }

            if (_db is my_mssql)
            {
                newRow["Name"] = row["COLUMN_NAME"];
                newRow["_OldName"] = row["COLUMN_NAME"];
                newRow["Type"] = row["DATA_TYPE"];
                newRow["Length"] = GetLengthFromMetadata(row, "CHARACTER_MAXIMUM_LENGTH", "NUMERIC_PRECISION");
                newRow["Decimals"] = GetMetadataString(row, "NUMERIC_SCALE");
                newRow["NotNull"] = (row["IS_NULLABLE"].ToString() == "NO");
                newRow["Default"] = row["COLUMN_DEFAULT"];
                newRow["Comment"] = GetMetadataString(row, "Comment", "COMMENT");
                return;
            }

            if (_db is my_oracle)
            {
                newRow["Name"] = row["COLUMN_NAME"];
                newRow["_OldName"] = row["COLUMN_NAME"];
                newRow["Type"] = row["DATA_TYPE"];
                newRow["Length"] = GetMetadataString(row, "DATA_LENGTH");
                newRow["Decimals"] = GetMetadataString(row, "DATA_SCALE");
                newRow["NotNull"] = (row["IS_NULLABLE"].ToString() == "N");
                newRow["Default"] = row["COLUMN_DEFAULT"];
                newRow["Comment"] = GetMetadataString(row, "Comment", "COMMENT");
                return;
            }

            newRow["Name"] = row[0];
        }

        private static string GetLengthFromMetadata(DataRow row, string textLengthColumn, string numericPrecisionColumn)
        {
            if (row.Table.Columns.Contains(textLengthColumn) && row[textLengthColumn] != DBNull.Value)
            {
                string value = row[textLengthColumn].ToString();
                return value == "-1" ? "MAX" : value;
            }

            if (row.Table.Columns.Contains(numericPrecisionColumn) && row[numericPrecisionColumn] != DBNull.Value)
            {
                return row[numericPrecisionColumn].ToString();
            }

            return "";
        }

        private static string GetMetadataString(DataRow row, params string[] columnNames)
        {
            foreach (string columnName in columnNames)
            {
                if (row.Table.Columns.Contains(columnName) && row[columnName] != DBNull.Value)
                {
                    return row[columnName].ToString();
                }
            }

            return "";
        }

        private static int GetMetadataInt(DataRow row, params string[] columnNames)
        {
            string value = GetMetadataString(row, columnNames).Trim();
            int parsed;
            return int.TryParse(value, out parsed) ? parsed : 0;
        }

        private void BindColumns(DataTable displayDt)
        {
            dgvColumns.DataSource = displayDt;

            ApplyColumnHeaders();
            dgvColumns.Columns["_OldName"].Visible = false;
        }

        public void ApplyLanguage()
        {
            Localization.ApplyTo(this);
            if (tpColumns != null) tpColumns.Text = Localization.T("Designer.Columns");
            if (tpIndexes != null) tpIndexes.Text = Localization.T("Designer.Indexes");
            if (tpOptions != null) tpOptions.Text = Localization.T("Designer.Options");
            if (tpComment != null) tpComment.Text = Localization.T("Designer.Comment");
            if (tpSqlPreview != null) tpSqlPreview.Text = Localization.T("Designer.SqlPreview");
            if (btnFloat != null) btnFloat.Text = Localization.T("Query.Float");
            if (btnDock != null) btnDock.Text = Localization.T("Query.Dock");
            ApplyColumnHeaders();
            UpdateTitle();
            ApplyTheme();
        }

        public void ApplyTheme()
        {
            ThemeManager.ApplyTo(this);
            if (dgvColumns != null)
            {
                dgvColumns.BackgroundColor = ThemeManager.WindowBackColor;
                dgvColumns.GridColor = ThemeManager.GridColor;
            }
            if (dgvIndexes != null)
            {
                dgvIndexes.BackgroundColor = ThemeManager.WindowBackColor;
                dgvIndexes.GridColor = ThemeManager.GridColor;
            }
            if (pnlColumnProperties != null) pnlColumnProperties.BackColor = ThemeManager.WindowBackColor;
            if (rtbSqlPreview != null)
            {
                rtbSqlPreview.BackColor = ThemeManager.TextBoxBackColor;
                rtbSqlPreview.ForeColor = ThemeManager.TextColor;
            }
        }

        private void ApplyColumnHeaders()
        {
            if (dgvColumns == null || dgvColumns.Columns.Count == 0) return;
            if (dgvColumns.Columns.Contains("Name")) dgvColumns.Columns["Name"].HeaderText = Localization.T("Designer.Name");
            if (dgvColumns.Columns.Contains("Type")) dgvColumns.Columns["Type"].HeaderText = Localization.T("Designer.Type");
            if (dgvColumns.Columns.Contains("Length")) dgvColumns.Columns["Length"].HeaderText = Localization.T("Designer.Length");
            if (dgvColumns.Columns.Contains("Decimals")) dgvColumns.Columns["Decimals"].HeaderText = Localization.T("Designer.Decimals");
            if (dgvColumns.Columns.Contains("NotNull")) dgvColumns.Columns["NotNull"].HeaderText = Localization.T("Designer.NotNull");
            if (dgvColumns.Columns.Contains("PK")) dgvColumns.Columns["PK"].HeaderText = Localization.T("Designer.PrimaryKey");
            if (dgvColumns.Columns.Contains("Default")) dgvColumns.Columns["Default"].HeaderText = Localization.T("Designer.Default");
            if (dgvColumns.Columns.Contains("Comment")) dgvColumns.Columns["Comment"].HeaderText = Localization.T("Designer.Comment");
        }

        private void GeneratePreviewSql()
        {
            DataTable currentDt = (DataTable)dgvColumns.DataSource;
            if (currentDt == null) return;

            if (IsNewTable)
            {
                rtbSqlPreview.Text = BuildCreateTableSql(currentDt);
                return;
            }

            if (!(_db is my_mysql))
            {
                rtbSqlPreview.Text = BuildGenericAlterTableSql(currentDt);
                return;
            }

            List<string> changes = new List<string>();
            string prevCol = null;

            for (int i = 0; i < currentDt.Rows.Count; i++)
            {
                DataRow row = currentDt.Rows[i];
                if (row.RowState == DataRowState.Deleted) continue;
                if (string.IsNullOrEmpty(row["Name"].ToString())) continue;

                string colName = row["Name"].ToString();
                string oldName = row["_OldName"]?.ToString();
                string colType = row["Type"].ToString();
                string colLen = row["Length"].ToString();
                string colDec = row["Decimals"]?.ToString();
                bool notNull = row["NotNull"] != DBNull.Value && (bool)row["NotNull"];
                string colDefault = row["Default"].ToString();
                string colComment = row["Comment"].ToString();

                // 組合類型字串
                string typeFull = colType;
                if (!string.IsNullOrEmpty(colLen))
                {
                    typeFull += "(" + colLen;
                    if (!string.IsNullOrEmpty(colDec)) typeFull += "," + colDec;
                    typeFull += ")";
                }

                string nullStr = notNull ? "NOT NULL" : "NULL";
                string defStr = string.IsNullOrEmpty(colDefault) ? "" : $"DEFAULT '{colDefault}'";
                if (colDefault.ToUpper() == "NULL") defStr = "DEFAULT NULL";
                string commentStr = string.IsNullOrEmpty(colComment) ? "" : $"COMMENT '{colComment}'";
                
                // 位置語法
                string posStr = (prevCol == null) ? "FIRST" : $"AFTER `{prevCol}`";

                DataRow[] origRows = string.IsNullOrEmpty(oldName) ? new DataRow[0] : _originalDt.Select($"Name = '{oldName}'");
                
                if (origRows.Length == 0)
                {
                    // 新增欄位
                    changes.Add($"ADD COLUMN `{colName}` {typeFull} {nullStr} {defStr} {commentStr} {posStr}");
                }
                else
                {
                    // 比對變動 (含重新命名與位置變動)
                    DataRow orig = origRows[0];
                    int origIdx = _originalDt.Rows.IndexOf(orig);
                    
                    bool changed = (colName != oldName) || 
                                   (orig["Type"].ToString() != colType) || 
                                   (orig["Length"].ToString() != colLen) || 
                                   (orig["NotNull"] != DBNull.Value && (bool)orig["NotNull"] != notNull) || 
                                   (orig["Default"].ToString() != colDefault) ||
                                   (orig["Comment"].ToString() != colComment) ||
                                   (origIdx != i); // 位置變動

                    if (changed)
                    {
                        if (colName != oldName)
                            changes.Add($"CHANGE COLUMN `{oldName}` `{colName}` {typeFull} {nullStr} {defStr} {commentStr} {posStr}");
                        else
                            changes.Add($"MODIFY COLUMN `{colName}` {typeFull} {nullStr} {defStr} {commentStr} {posStr}");
                    }
                }
                prevCol = colName;
            }

            // --- 索引變更偵測 ---
            DataTable currentIdxDt = (DataTable)dgvIndexes.DataSource;
            if (currentIdxDt != null && _originalIdxDt != null)
            {
                // 1. 偵測刪除與修改 (先 DROP)
                foreach (DataRow orig in _originalIdxDt.Rows)
                {
                    string oldIdxName = orig["名稱"].ToString();
                    bool stillExists = false;
                    bool changed = true;

                    foreach (DataRow curr in currentIdxDt.Rows)
                    {
                        if (curr.RowState == DataRowState.Deleted) continue;
                        if (curr["名稱"].ToString() == oldIdxName)
                        {
                            stillExists = true;
                            if (curr["欄位"].ToString() == orig["欄位"].ToString() &&
                                curr["索引類型"].ToString() == orig["索引類型"].ToString() &&
                                curr["索引方法"].ToString() == orig["索引方法"].ToString() &&
                                curr["註解"].ToString() == orig["註解"].ToString())
                            {
                                changed = false;
                            }
                            break;
                        }
                    }

                    if (!stillExists || changed)
                    {
                        if (oldIdxName == "PRIMARY") changes.Add("DROP PRIMARY KEY");
                        else changes.Add($"DROP INDEX `{oldIdxName}`");
                    }
                }

                // 2. 偵測新增與修改 (後 ADD)
                foreach (DataRow curr in currentIdxDt.Rows)
                {
                    if (curr.RowState == DataRowState.Deleted) continue;
                    string idxName = curr["名稱"].ToString();
                    if (string.IsNullOrEmpty(idxName)) continue;

                    bool isNew = true;
                    foreach (DataRow orig in _originalIdxDt.Rows)
                    {
                        if (orig["名稱"].ToString() == idxName)
                        {
                            // 檢查是否變動
                            if (curr["欄位"].ToString() == orig["欄位"].ToString() &&
                                curr["索引類型"].ToString() == orig["索引類型"].ToString() &&
                                curr["索引方法"].ToString() == orig["索引方法"].ToString() &&
                                curr["註解"].ToString() == orig["註解"].ToString())
                            {
                                isNew = false;
                            }
                            break;
                        }
                    }

                    if (isNew)
                    {
                        string type = curr["索引類型"].ToString();
                        string method = curr["索引方法"].ToString();
                        string cols = curr["欄位"].ToString();
                        string colList = FormatMySqlIndexColumns(cols);
                        string comment = curr["註解"].ToString();
                        string commentStr = string.IsNullOrEmpty(comment) ? "" : $"COMMENT '{comment}'";

                        if (type == "PRIMARY") changes.Add($"ADD PRIMARY KEY ({colList}) USING {method}");
                        else if (type == "UNIQUE") changes.Add($"ADD UNIQUE INDEX `{idxName}` ({colList}) USING {method} {commentStr}");
                        else if (type == "FULLTEXT") changes.Add($"ADD FULLTEXT INDEX `{idxName}` ({colList}) {commentStr}");
                        else changes.Add($"ADD INDEX `{idxName}` ({colList}) USING {method} {commentStr}");
                    }
                }
            }

            if (changes.Count > 0)
            {
                rtbSqlPreview.Text = $"ALTER TABLE `{_databaseName}`.`{_tableName}`\n  " + 
                                     string.Join(",\n  ", changes) + ";";
            }
            else
            {
                rtbSqlPreview.Text = "-- No changes detected.";
            }
        }

        private string BuildCreateTableSql(DataTable currentDt)
        {
            if (_db is my_mysql) return BuildMySqlCreateTableSql(currentDt);
            return BuildGenericCreateTableSql(currentDt);
        }

        private string BuildGenericAlterTableSql(DataTable currentDt)
        {
            if (_db is my_sqlite && NeedsSqliteTableRebuild(currentDt))
            {
                return BuildSqliteRebuildTableSql(currentDt);
            }

            List<string> statements = new List<string>();
            List<string> unsupported = new List<string>();

            foreach (DataRow original in _originalDt.Rows)
            {
                string oldName = GetRowString(original, "Name").Trim();
                if (string.IsNullOrWhiteSpace(oldName)) continue;

                bool stillExists = false;
                foreach (DataRow current in currentDt.Rows)
                {
                    if (current.RowState == DataRowState.Deleted) continue;
                    if (GetRowString(current, "_OldName").Trim() == oldName)
                    {
                        stillExists = true;
                        break;
                    }
                }

                if (!stillExists)
                {
                    statements.Add(BuildDropColumnStatement(oldName));
                }
            }

            foreach (DataRow current in currentDt.Rows)
            {
                if (current.RowState == DataRowState.Deleted) continue;

                string columnName = GetRowString(current, "Name").Trim();
                if (string.IsNullOrWhiteSpace(columnName)) continue;

                string oldName = GetRowString(current, "_OldName").Trim();
                DataRow original = FindOriginalColumn(oldName);
                if (original == null)
                {
                    statements.Add(BuildAddColumnStatement(current));
                    continue;
                }

                if (!oldName.Equals(columnName, StringComparison.Ordinal))
                {
                    statements.Add(BuildRenameColumnStatement(oldName, columnName));
                }

                statements.AddRange(BuildAlterColumnStatements(original, current, columnName, unsupported));
            }

            statements.AddRange(BuildGenericAlterIndexStatements(unsupported));

            if (unsupported.Count > 0)
            {
                return FormatUnsupportedChanges(unsupported);
            }

            if (statements.Count == 0)
            {
                return "-- " + Localization.T("Designer.NoChangesDetected");
            }

            return string.Join("\r\n", statements.ToArray());
        }

        private bool NeedsSqliteTableRebuild(DataTable currentDt)
        {
            if (!(_db is my_sqlite) || currentDt == null || _originalDt == null) return false;

            foreach (DataRow original in _originalDt.Rows)
            {
                string oldName = GetRowString(original, "Name").Trim();
                if (string.IsNullOrWhiteSpace(oldName)) continue;
                if (FindCurrentColumnByOldName(currentDt, oldName) == null) return true;
            }

            int visibleIndex = 0;
            foreach (DataRow current in currentDt.Rows)
            {
                if (current.RowState == DataRowState.Deleted) continue;
                if (string.IsNullOrWhiteSpace(GetRowString(current, "Name"))) continue;

                string oldName = GetRowString(current, "_OldName").Trim();
                DataRow original = FindOriginalColumn(oldName);
                if (original == null)
                {
                    visibleIndex++;
                    continue;
                }

                if (!oldName.Equals(GetRowString(current, "Name").Trim(), StringComparison.Ordinal)) return true;
                if (HasTextChanged(original, current, "Type")) return true;
                if (HasTextChanged(original, current, "Length")) return true;
                if (HasTextChanged(original, current, "Decimals")) return true;
                if (HasBoolChanged(original, current, "NotNull")) return true;
                if (HasTextChanged(original, current, "Default")) return true;
                if (HasBoolChanged(original, current, "PK")) return true;
                if (_originalDt.Rows.IndexOf(original) != visibleIndex) return true;

                visibleIndex++;
            }

            return false;
        }

        private static string FormatUnsupportedChanges(List<string> unsupported)
        {
            return "-- " + Localization.T("Designer.UnsupportedExistingChanges") + "\r\n-- " + string.Join("\r\n-- ", unsupported.ToArray());
        }

        private DataRow FindCurrentColumnByOldName(DataTable currentDt, string oldName)
        {
            foreach (DataRow row in currentDt.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;
                if (GetRowString(row, "_OldName").Trim() == oldName) return row;
            }
            return null;
        }

        private string BuildSqliteRebuildTableSql(DataTable currentDt)
        {
            List<string> unsupported = new List<string>();
            foreach (DataRow current in currentDt.Rows)
            {
                if (current.RowState == DataRowState.Deleted) continue;
                string oldName = GetRowString(current, "_OldName").Trim();
                DataRow original = FindOriginalColumn(oldName);
                if (original != null && HasTextChanged(original, current, "Comment"))
                {
                    unsupported.Add(Localization.Format("Designer.SqliteColumnCommentUnsupported", GetRowString(current, "Name").Trim()));
                }
            }

            if (unsupported.Count > 0)
            {
                return FormatUnsupportedChanges(unsupported);
            }

            List<DataRow> currentColumns = new List<DataRow>();
            foreach (DataRow row in currentDt.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;
                if (!string.IsNullOrWhiteSpace(GetRowString(row, "Name"))) currentColumns.Add(row);
            }

            if (currentColumns.Count == 0)
            {
                return "-- " + Localization.T("Designer.KeepAtLeastOneColumn");
            }

            string tempTableName = BuildUniqueSqliteRebuildTableName();
            List<string> definitions = new List<string>();
            List<string> primaryColumns = new List<string>();
            foreach (DataRow row in currentColumns)
            {
                definitions.Add("  " + BuildGenericColumnDefinition(row));
                if (GetBool(row, "PK"))
                {
                    primaryColumns.Add(QuoteDesignerIdentifier(GetRowString(row, "Name").Trim()));
                }
            }
            if (primaryColumns.Count > 0)
            {
                definitions.Add("  PRIMARY KEY (" + string.Join(", ", primaryColumns.ToArray()) + ")");
            }

            List<string> insertColumns = new List<string>();
            List<string> selectExpressions = new List<string>();
            foreach (DataRow row in currentColumns)
            {
                string columnName = GetRowString(row, "Name").Trim();
                insertColumns.Add(QuoteDesignerIdentifier(columnName));

                DataRow original = FindOriginalColumn(GetRowString(row, "_OldName").Trim());
                if (original != null)
                {
                    selectExpressions.Add(QuoteDesignerIdentifier(GetRowString(original, "Name").Trim()));
                    continue;
                }

                string defaultValue = GetRowString(row, "Default").Trim();
                selectExpressions.Add(string.IsNullOrWhiteSpace(defaultValue) ? "NULL" : FormatGenericDefault(defaultValue));
            }

            List<string> statements = new List<string>();
            statements.Add("PRAGMA foreign_keys=OFF;");
            statements.Add("BEGIN TRANSACTION;");
            statements.Add("CREATE TABLE " + QuoteDesignerIdentifier(tempTableName) + " (\r\n" +
                           string.Join(",\r\n", definitions.ToArray()) + "\r\n" +
                           ");");
            statements.Add("INSERT INTO " + QuoteDesignerIdentifier(tempTableName) +
                           " (" + string.Join(", ", insertColumns.ToArray()) + ") SELECT " +
                           string.Join(", ", selectExpressions.ToArray()) +
                           " FROM " + GetQualifiedDesignerTableName(_tableName) + ";");
            statements.Add("DROP TABLE " + GetQualifiedDesignerTableName(_tableName) + ";");
            statements.Add("ALTER TABLE " + QuoteDesignerIdentifier(tempTableName) +
                           " RENAME TO " + QuoteDesignerIdentifier(_tableName) + ";");

            foreach (string indexStatement in BuildSqliteRebuildIndexStatements(currentDt, _tableName))
            {
                statements.Add(indexStatement);
            }

            statements.Add("COMMIT;");
            statements.Add("PRAGMA foreign_keys=ON;");
            return string.Join("\r\n", statements.ToArray());
        }

        private string BuildUniqueSqliteRebuildTableName()
        {
            string baseName = "__mysqlpunk_rebuild_" + _tableName;
            HashSet<string> existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (string tableName in _db.GetTables(_databaseName))
                {
                    if (!string.IsNullOrWhiteSpace(tableName))
                    {
                        existingNames.Add(tableName);
                    }
                }
            }
            catch
            {
            }

            if (!existingNames.Contains(baseName))
            {
                return baseName;
            }

            for (int i = 1; i < 1000; i++)
            {
                string candidate = baseName + "_" + i.ToString();
                if (!existingNames.Contains(candidate))
                {
                    return candidate;
                }
            }

            return baseName + "_" + Guid.NewGuid().ToString("N");
        }

        private List<string> BuildSqliteRebuildIndexStatements(DataTable currentDt, string tableName)
        {
            List<string> statements = new List<string>();
            DataTable currentIdxDt = dgvIndexes.DataSource as DataTable;
            if (currentIdxDt == null) return statements;

            Dictionary<string, string> columnNameMap = BuildCurrentColumnNameMap(currentDt);

            foreach (DataRow row in currentIdxDt.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;

                string indexName = GetRowString(row, "名稱").Trim();
                string type = GetRowString(row, "索引類型").Trim().ToUpperInvariant();
                string columns = RemapIndexColumns(GetRowString(row, "欄位").Trim(), columnNameMap);
                if (string.IsNullOrWhiteSpace(columns)) continue;
                if (type == "PRIMARY") continue;
                if (type == "FULLTEXT" || type == "SPATIAL") continue;
                if (string.IsNullOrWhiteSpace(indexName)) indexName = tableName + "_idx";

                string columnList = FormatGenericIndexColumns(columns);
                if (string.IsNullOrWhiteSpace(columnList)) continue;

                string unique = type == "UNIQUE" ? "UNIQUE " : "";
                statements.Add("CREATE " + unique + "INDEX " + QuoteDesignerIdentifier(indexName) +
                               " ON " + QuoteDesignerIdentifier(tableName) +
                               " (" + columnList + ");");
            }

            return statements;
        }

        private static Dictionary<string, string> BuildCurrentColumnNameMap(DataTable currentDt)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (currentDt == null) return map;

            foreach (DataRow row in currentDt.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;

                string currentName = GetRowString(row, "Name").Trim();
                if (string.IsNullOrWhiteSpace(currentName)) continue;

                string oldName = GetRowString(row, "_OldName").Trim();
                if (!string.IsNullOrWhiteSpace(oldName)) map[oldName] = currentName;
                map[currentName] = currentName;
            }

            return map;
        }

        private static string RemapIndexColumns(string columns, Dictionary<string, string> columnNameMap)
        {
            if (string.IsNullOrWhiteSpace(columns)) return "";

            string[] rawCols = columns.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            List<string> mappedCols = new List<string>();
            foreach (string rawCol in rawCols)
            {
                string expression = rawCol.Trim();
                string columnName = GetIndexColumnName(expression);
                if (string.IsNullOrWhiteSpace(columnName)) continue;

                string mappedName;
                if (!columnNameMap.TryGetValue(columnName, out mappedName))
                {
                    continue;
                }

                mappedCols.Add(mappedName + GetIndexDirectionSuffix(expression));
            }

            return string.Join(", ", mappedCols.ToArray());
        }

        private DataRow FindOriginalColumn(string oldName)
        {
            if (string.IsNullOrWhiteSpace(oldName) || _originalDt == null) return null;
            foreach (DataRow row in _originalDt.Rows)
            {
                if (GetRowString(row, "Name").Trim() == oldName) return row;
            }
            return null;
        }

        private string BuildAddColumnStatement(DataRow row)
        {
            if (_db is my_oracle)
            {
                return "ALTER TABLE " + GetQualifiedDesignerTableName(_tableName) +
                       " ADD (" + BuildGenericColumnDefinition(row) + ");";
            }

            if (_db is my_mssql)
            {
                return "ALTER TABLE " + GetQualifiedDesignerTableName(_tableName) +
                       " ADD " + BuildGenericColumnDefinition(row) + ";";
            }

            return "ALTER TABLE " + GetQualifiedDesignerTableName(_tableName) +
                   " ADD COLUMN " + BuildGenericColumnDefinition(row) + ";";
        }

        private string BuildDropColumnStatement(string oldName)
        {
            return "ALTER TABLE " + GetQualifiedDesignerTableName(_tableName) +
                   " DROP COLUMN " + QuoteDesignerIdentifier(oldName) + ";";
        }

        private string BuildRenameColumnStatement(string oldName, string newName)
        {
            if (_db is my_mssql)
            {
                return "EXEC " + QuoteDesignerIdentifier(_databaseName) + ".sys.sp_rename N'dbo." +
                       EscapeSqlServerLiteral(_tableName) + "." + EscapeSqlServerLiteral(oldName) +
                       "', N'" + EscapeSqlServerLiteral(newName) + "', N'COLUMN';";
            }

            return "ALTER TABLE " + GetQualifiedDesignerTableName(_tableName) +
                   " RENAME COLUMN " + QuoteDesignerIdentifier(oldName) +
                   " TO " + QuoteDesignerIdentifier(newName) + ";";
        }

        private List<string> BuildAlterColumnStatements(DataRow original, DataRow current, string columnName, List<string> unsupported)
        {
            List<string> statements = new List<string>();
            bool typeChanged = HasTextChanged(original, current, "Type") ||
                               HasTextChanged(original, current, "Length") ||
                               HasTextChanged(original, current, "Decimals");
            bool nullChanged = HasBoolChanged(original, current, "NotNull");
            bool defaultChanged = HasTextChanged(original, current, "Default");
            bool commentChanged = HasTextChanged(original, current, "Comment");

            if (_db is my_sqlite)
            {
                if (typeChanged || nullChanged || defaultChanged)
                    unsupported.Add("SQLite 修改既有欄位型別、NULL 或 DEFAULT 需要重建資料表：" + columnName);
                if (commentChanged)
                    unsupported.Add("SQLite 不支援欄位註解：" + columnName);
                return statements;
            }

            if (_db is my_postgresql)
            {
                if (typeChanged)
                {
                    statements.Add("ALTER TABLE " + GetQualifiedDesignerTableName(_tableName) +
                                   " ALTER COLUMN " + QuoteDesignerIdentifier(columnName) +
                                   " TYPE " + MapDesignerType(GetRowString(current, "Type"), GetRowString(current, "Length"), GetRowString(current, "Decimals")) + ";");
                }
                if (nullChanged)
                {
                    statements.Add("ALTER TABLE " + GetQualifiedDesignerTableName(_tableName) +
                                   " ALTER COLUMN " + QuoteDesignerIdentifier(columnName) +
                                   (GetBool(current, "NotNull") ? " SET NOT NULL;" : " DROP NOT NULL;"));
                }
                if (defaultChanged)
                {
                    string defaultValue = GetRowString(current, "Default").Trim();
                    statements.Add("ALTER TABLE " + GetQualifiedDesignerTableName(_tableName) +
                                   " ALTER COLUMN " + QuoteDesignerIdentifier(columnName) +
                                   (string.IsNullOrWhiteSpace(defaultValue) ? " DROP DEFAULT;" : " SET DEFAULT " + FormatGenericDefault(defaultValue) + ";"));
                }
                if (commentChanged)
                {
                    statements.Add("COMMENT ON COLUMN " + GetQualifiedDesignerTableName(_tableName) + "." + QuoteDesignerIdentifier(columnName) +
                                   " IS " + EscapeSqlStringLiteral(GetRowString(current, "Comment")) + ";");
                }
                return statements;
            }

            if (_db is my_mssql)
            {
                if (typeChanged || nullChanged)
                {
                    statements.Add("ALTER TABLE " + GetQualifiedDesignerTableName(_tableName) +
                                   " ALTER COLUMN " + QuoteDesignerIdentifier(columnName) + " " +
                                   MapDesignerType(GetRowString(current, "Type"), GetRowString(current, "Length"), GetRowString(current, "Decimals")) +
                                   (GetBool(current, "NotNull") ? " NOT NULL;" : " NULL;"));
                }
                if (defaultChanged)
                {
                    statements.Add(BuildSqlServerDropDefaultConstraintStatement(columnName));
                    string defaultValue = GetRowString(current, "Default").Trim();
                    if (!string.IsNullOrWhiteSpace(defaultValue))
                    {
                        statements.Add(BuildSqlServerAddDefaultConstraintStatement(columnName, defaultValue));
                    }
                }
                if (commentChanged)
                {
                    statements.Add(BuildSqlServerColumnCommentStatement(columnName, GetRowString(current, "Comment")));
                }
                return statements;
            }

            if (_db is my_oracle)
            {
                if (typeChanged || nullChanged || defaultChanged)
                {
                    string defaultValue = GetRowString(current, "Default").Trim();
                    List<string> parts = new List<string>
                    {
                        QuoteDesignerIdentifier(columnName),
                        MapDesignerType(GetRowString(current, "Type"), GetRowString(current, "Length"), GetRowString(current, "Decimals"))
                    };
                    if (!string.IsNullOrWhiteSpace(defaultValue))
                    {
                        parts.Add("DEFAULT " + FormatGenericDefault(defaultValue));
                    }
                    parts.Add(GetBool(current, "NotNull") ? "NOT NULL" : "NULL");
                    statements.Add("ALTER TABLE " + GetQualifiedDesignerTableName(_tableName) +
                                   " MODIFY (" + string.Join(" ", parts.ToArray()) + ");");
                }
                if (commentChanged)
                {
                    statements.Add("COMMENT ON COLUMN " + GetQualifiedDesignerTableName(_tableName) + "." + QuoteDesignerIdentifier(columnName) +
                                   " IS " + EscapeSqlStringLiteral(GetRowString(current, "Comment")) + ";");
                }
            }

            return statements;
        }

        private string BuildSqlServerDropDefaultConstraintStatement(string columnName)
        {
            string database = QuoteDesignerIdentifier(_databaseName);
            return "DECLARE @constraintName nvarchar(128);\r\n" +
                   "SELECT @constraintName = dc.name\r\n" +
                   "FROM " + database + ".sys.default_constraints dc\r\n" +
                   "INNER JOIN " + database + ".sys.columns c ON c.default_object_id = dc.object_id\r\n" +
                   "INNER JOIN " + database + ".sys.tables t ON t.object_id = c.object_id\r\n" +
                   "INNER JOIN " + database + ".sys.schemas s ON s.schema_id = t.schema_id\r\n" +
                   "WHERE s.name = N'dbo' AND t.name = N'" + EscapeSqlServerLiteral(_tableName) + "' AND c.name = N'" + EscapeSqlServerLiteral(columnName) + "';\r\n" +
                   "IF @constraintName IS NOT NULL EXEC(N'ALTER TABLE " + GetQualifiedDesignerTableName(_tableName).Replace("'", "''") + " DROP CONSTRAINT [' + REPLACE(@constraintName, ']', ']]') + N']');";
        }

        private string BuildSqlServerAddDefaultConstraintStatement(string columnName, string defaultValue)
        {
            return "ALTER TABLE " + GetQualifiedDesignerTableName(_tableName) +
                   " ADD DEFAULT " + FormatGenericDefault(defaultValue) +
                   " FOR " + QuoteDesignerIdentifier(columnName) + ";";
        }

        private string BuildSqlServerColumnCommentStatement(string columnName, string comment)
        {
            string database = QuoteDesignerIdentifier(_databaseName);
            string tableLiteral = EscapeSqlServerLiteral(_tableName);
            string columnLiteral = EscapeSqlServerLiteral(columnName);
            string valueLiteral = EscapeSqlServerLiteral(comment ?? "");

            string existsSql =
                "EXISTS (SELECT 1 FROM " + database + ".sys.extended_properties ep " +
                "INNER JOIN " + database + ".sys.columns c ON c.object_id = ep.major_id AND c.column_id = ep.minor_id " +
                "INNER JOIN " + database + ".sys.tables t ON t.object_id = c.object_id " +
                "INNER JOIN " + database + ".sys.schemas s ON s.schema_id = t.schema_id " +
                "WHERE ep.name = N'MS_Description' AND s.name = N'dbo' AND t.name = N'" + tableLiteral + "' AND c.name = N'" + columnLiteral + "')";

            string commonArgs =
                "@name=N'MS_Description', @level0type=N'SCHEMA', @level0name=N'dbo', " +
                "@level1type=N'TABLE', @level1name=N'" + tableLiteral + "', " +
                "@level2type=N'COLUMN', @level2name=N'" + columnLiteral + "'";

            if (string.IsNullOrWhiteSpace(comment))
            {
                return "IF " + existsSql + " EXEC " + database + ".sys.sp_dropextendedproperty " + commonArgs + ";";
            }

            return "IF " + existsSql +
                   " EXEC " + database + ".sys.sp_updateextendedproperty @value=N'" + valueLiteral + "', " + commonArgs +
                   " ELSE EXEC " + database + ".sys.sp_addextendedproperty @value=N'" + valueLiteral + "', " + commonArgs + ";";
        }

        private List<string> BuildGenericAlterIndexStatements(List<string> unsupported)
        {
            List<string> statements = new List<string>();
            DataTable currentIdxDt = dgvIndexes.DataSource as DataTable;
            if (currentIdxDt == null || _originalIdxDt == null) return statements;
            bool primaryKeyDropAdded = false;

            foreach (DataRow original in _originalIdxDt.Rows)
            {
                string oldName = GetRowString(original, "名稱").Trim();
                if (string.IsNullOrWhiteSpace(oldName)) continue;
                if (IsPrimaryIndexName(oldName))
                {
                    if (!HasMatchingIndex(currentIdxDt, original))
                    {
                        if (SupportsDynamicPrimaryKeyChange())
                        {
                            statements.Add(BuildDropPrimaryKeyStatement());
                            primaryKeyDropAdded = true;
                        }
                        else
                        {
                            unsupported.Add(Localization.Format("Designer.PrimaryKeyNeedsConstraintName", _tableName));
                        }
                    }
                    continue;
                }

                DataRow current = FindCurrentIndex(currentIdxDt, oldName);
                if (current == null || IsIndexChanged(original, current))
                {
                    statements.Add(BuildDropIndexStatement(oldName));
                }
            }

            foreach (DataRow current in currentIdxDt.Rows)
            {
                if (current.RowState == DataRowState.Deleted) continue;
                string indexName = GetRowString(current, "名稱").Trim();
                string type = GetRowString(current, "索引類型").Trim().ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(indexName)) continue;
                if (type == "PRIMARY")
                {
                    if (!HasOriginalMatchingIndex(current))
                    {
                        if (SupportsDynamicPrimaryKeyChange())
                        {
                            string columns = GetRowString(current, "欄位").Trim();
                            if (string.IsNullOrWhiteSpace(columns))
                            {
                                unsupported.Add(Localization.Format("Designer.PrimaryKeyMissingColumns", _tableName));
                            }
                            else
                            {
                                if (!primaryKeyDropAdded && HasOriginalPrimaryIndex())
                                {
                                    statements.Add(BuildDropPrimaryKeyStatement());
                                    primaryKeyDropAdded = true;
                                }
                                statements.Add(BuildAddPrimaryKeyStatement(current));
                            }
                        }
                        else
                        {
                            unsupported.Add(Localization.Format("Designer.PrimaryKeyNeedsConstraintName", _tableName));
                        }
                    }
                    continue;
                }
                if (type == "FULLTEXT" && !SupportsFullTextIndex())
                {
                    unsupported.Add(Localization.Format("Designer.FullTextUnsupported", indexName));
                    continue;
                }
                if (type == "SPATIAL" && !SupportsSpatialIndex())
                {
                    unsupported.Add(Localization.Format("Designer.SpatialUnsupported", indexName));
                    continue;
                }

                DataRow original = FindOriginalIndex(indexName);
                if (original == null || IsIndexChanged(original, current))
                {
                    statements.Add(BuildCreateGenericIndexStatement(current));
                }
            }

            return statements;
        }

        private bool SupportsDynamicPrimaryKeyChange()
        {
            return _db is my_mssql || _db is my_postgresql || _db is my_oracle;
        }

        private bool SupportsSpatialIndex()
        {
            return _db is my_postgresql || _db is my_oracle || _db is my_mssql;
        }

        private bool SupportsFullTextIndex()
        {
            return _db is my_postgresql || _db is my_oracle || _db is my_mssql;
        }

        private string BuildDropPrimaryKeyStatement()
        {
            if (_db is my_mssql) return BuildSqlServerDropPrimaryKeyStatement();
            if (_db is my_postgresql) return BuildPostgreSqlDropPrimaryKeyStatement();
            if (_db is my_oracle) return BuildOracleDropPrimaryKeyStatement();
            return "";
        }

        private string BuildAddPrimaryKeyStatement(DataRow row)
        {
            if (_db is my_mssql) return BuildSqlServerAddPrimaryKeyStatement(row);
            if (_db is my_postgresql) return BuildPostgreSqlAddPrimaryKeyStatement(row);
            if (_db is my_oracle) return BuildOracleAddPrimaryKeyStatement(row);
            return "";
        }

        private string BuildSqlServerDropPrimaryKeyStatement()
        {
            string database = QuoteDesignerIdentifier(_databaseName);
            return "DECLARE @primaryKeyName nvarchar(128);\r\n" +
                   "SELECT @primaryKeyName = kc.name\r\n" +
                   "FROM " + database + ".sys.key_constraints kc\r\n" +
                   "INNER JOIN " + database + ".sys.tables t ON t.object_id = kc.parent_object_id\r\n" +
                   "INNER JOIN " + database + ".sys.schemas s ON s.schema_id = t.schema_id\r\n" +
                   "WHERE kc.[type] = 'PK' AND s.name = N'dbo' AND t.name = N'" + EscapeSqlServerLiteral(_tableName) + "';\r\n" +
                   "IF @primaryKeyName IS NOT NULL EXEC(N'ALTER TABLE " + GetQualifiedDesignerTableName(_tableName).Replace("'", "''") + " DROP CONSTRAINT [' + REPLACE(@primaryKeyName, ']', ']]') + N']');";
        }

        private string BuildSqlServerAddPrimaryKeyStatement(DataRow row)
        {
            string indexName = GetRowString(row, "名稱").Trim();
            if (string.IsNullOrWhiteSpace(indexName) || IsPrimaryIndexName(indexName))
            {
                indexName = "PK_" + _tableName;
            }

            return "ALTER TABLE " + GetQualifiedDesignerTableName(_tableName) +
                   " ADD CONSTRAINT " + QuoteDesignerIdentifier(indexName) +
                   " PRIMARY KEY (" + FormatGenericIndexColumns(GetRowString(row, "欄位").Trim()) + ");";
        }

        private string BuildPostgreSqlDropPrimaryKeyStatement()
        {
            string tableLiteral = EscapeSqlStringValue(_tableName);
            string qualifiedTable = GetQualifiedDesignerTableName(_tableName).Replace("'", "''");

            return "DO $mysqlpunk$\r\n" +
                   "DECLARE primary_key_name text;\r\n" +
                   "BEGIN\r\n" +
                   "  SELECT tc.constraint_name INTO primary_key_name\r\n" +
                   "  FROM information_schema.table_constraints tc\r\n" +
                   "  WHERE tc.constraint_type = 'PRIMARY KEY' AND tc.table_schema = 'public' AND tc.table_name = '" + tableLiteral + "';\r\n" +
                   "  IF primary_key_name IS NOT NULL THEN\r\n" +
                   "    EXECUTE 'ALTER TABLE " + qualifiedTable + " DROP CONSTRAINT ' || quote_ident(primary_key_name);\r\n" +
                   "  END IF;\r\n" +
                   "END\r\n" +
                   "$mysqlpunk$;";
        }

        private string BuildPostgreSqlAddPrimaryKeyStatement(DataRow row)
        {
            string indexName = GetPrimaryKeyConstraintName(row, "pk_");
            return "ALTER TABLE " + GetQualifiedDesignerTableName(_tableName) +
                   " ADD CONSTRAINT " + QuoteDesignerIdentifier(indexName) +
                   " PRIMARY KEY (" + FormatGenericIndexColumns(GetRowString(row, "欄位").Trim()) + ");";
        }

        private string BuildOracleDropPrimaryKeyStatement()
        {
            string ownerLiteral = EscapeSqlStringValue(_databaseName);
            string tableLiteral = EscapeSqlStringValue(_tableName);
            string qualifiedTable = GetQualifiedDesignerTableName(_tableName).Replace("'", "''");

            return "DECLARE\r\n" +
                   "  primary_key_name VARCHAR2(128);\r\n" +
                   "BEGIN\r\n" +
                   "  SELECT constraint_name INTO primary_key_name\r\n" +
                   "  FROM ALL_CONSTRAINTS\r\n" +
                   "  WHERE CONSTRAINT_TYPE = 'P' AND UPPER(OWNER) = UPPER('" + ownerLiteral + "') AND UPPER(TABLE_NAME) = UPPER('" + tableLiteral + "') AND ROWNUM = 1;\r\n" +
                   "  EXECUTE IMMEDIATE 'ALTER TABLE " + qualifiedTable + " DROP CONSTRAINT \"' || REPLACE(primary_key_name, '\"', '\"\"') || '\"';\r\n" +
                   "EXCEPTION\r\n" +
                   "  WHEN NO_DATA_FOUND THEN NULL;\r\n" +
                   "END;";
        }

        private string BuildOracleAddPrimaryKeyStatement(DataRow row)
        {
            string indexName = GetPrimaryKeyConstraintName(row, "PK_");
            return "ALTER TABLE " + GetQualifiedDesignerTableName(_tableName) +
                   " ADD CONSTRAINT " + QuoteDesignerIdentifier(indexName) +
                   " PRIMARY KEY (" + FormatGenericIndexColumns(GetRowString(row, "欄位").Trim()) + ");";
        }

        private string GetPrimaryKeyConstraintName(DataRow row, string prefix)
        {
            string indexName = GetRowString(row, "名稱").Trim();
            if (string.IsNullOrWhiteSpace(indexName) || IsPrimaryIndexName(indexName))
            {
                indexName = prefix + _tableName;
            }
            return indexName;
        }

        private string BuildDropIndexStatement(string indexName)
        {
            if (_db is my_mssql)
            {
                return "DROP INDEX " + QuoteDesignerIdentifier(indexName) + " ON " + GetQualifiedDesignerTableName(_tableName) + ";";
            }
            return "DROP INDEX " + QuoteDesignerIdentifier(indexName) + ";";
        }

        private string BuildCreateGenericIndexStatement(DataRow row)
        {
            string indexName = GetRowString(row, "名稱").Trim();
            string type = GetRowString(row, "索引類型").Trim().ToUpperInvariant();
            string columns = GetRowString(row, "欄位").Trim();
            if (type == "FULLTEXT" && _db is my_postgresql)
            {
                return "CREATE INDEX " + QuoteDesignerIdentifier(indexName) +
                       " ON " + GetQualifiedDesignerTableName(_tableName) +
                       " USING GIN (" + FormatPostgreSqlFullTextIndexExpression(columns) + ");";
            }
            if (type == "FULLTEXT" && _db is my_oracle)
            {
                return BuildOracleFullTextIndexStatement(indexName, _tableName, columns);
            }
            if (type == "FULLTEXT" && _db is my_mssql)
            {
                return BuildSqlServerFullTextIndexStatement(_tableName, columns);
            }
            if (type == "SPATIAL" && _db is my_postgresql)
            {
                return "CREATE INDEX " + QuoteDesignerIdentifier(indexName) +
                       " ON " + GetQualifiedDesignerTableName(_tableName) +
                       " USING GIST (" + FormatGenericIndexColumns(columns) + ");";
            }
            if (type == "SPATIAL" && _db is my_oracle)
            {
                return BuildOracleSpatialIndexStatement(indexName, _tableName, columns);
            }
            if (type == "SPATIAL" && _db is my_mssql)
            {
                return BuildSqlServerSpatialIndexStatement(indexName, _tableName, columns);
            }
            string unique = type == "UNIQUE" ? "UNIQUE " : "";
            return "CREATE " + unique + "INDEX " + QuoteDesignerIdentifier(indexName) +
                   " ON " + GetQualifiedDesignerTableName(_tableName) +
                   " (" + FormatGenericIndexColumns(columns) + ");";
        }

        private DataRow FindOriginalIndex(string indexName)
        {
            foreach (DataRow row in _originalIdxDt.Rows)
            {
                if (GetRowString(row, "名稱").Trim() == indexName) return row;
            }
            return null;
        }

        private DataRow FindCurrentIndex(DataTable currentIdxDt, string oldName)
        {
            foreach (DataRow row in currentIdxDt.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;
                string currentOldName = GetRowString(row, "_OldName").Trim();
                string currentName = GetRowString(row, "名稱").Trim();
                if (currentOldName == oldName || currentName == oldName) return row;
            }
            return null;
        }

        private bool HasMatchingIndex(DataTable currentIdxDt, DataRow original)
        {
            foreach (DataRow current in currentIdxDt.Rows)
            {
                if (current.RowState == DataRowState.Deleted) continue;
                if (!IsIndexChanged(original, current)) return true;
            }
            return false;
        }

        private bool HasOriginalMatchingIndex(DataRow current)
        {
            foreach (DataRow original in _originalIdxDt.Rows)
            {
                if (!IsIndexChanged(original, current)) return true;
            }
            return false;
        }

        private bool HasOriginalPrimaryIndex()
        {
            if (_originalIdxDt == null) return false;
            foreach (DataRow row in _originalIdxDt.Rows)
            {
                if (IsPrimaryIndexName(GetRowString(row, "名稱").Trim())) return true;
            }
            return false;
        }

        private static bool IsPrimaryIndexName(string indexName)
        {
            return indexName.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsIndexChanged(DataRow original, DataRow current)
        {
            return GetRowString(original, "名稱") != GetRowString(current, "名稱") ||
                   GetRowString(original, "欄位") != GetRowString(current, "欄位") ||
                   GetRowString(original, "索引類型") != GetRowString(current, "索引類型") ||
                   GetRowString(original, "索引方法") != GetRowString(current, "索引方法") ||
                   GetRowString(original, "註解") != GetRowString(current, "註解");
        }

        private static bool HasTextChanged(DataRow original, DataRow current, string columnName)
        {
            return GetRowString(original, columnName).Trim() != GetRowString(current, columnName).Trim();
        }

        private static bool HasBoolChanged(DataRow original, DataRow current, string columnName)
        {
            return GetBool(original, columnName) != GetBool(current, columnName);
        }

        private static bool GetBool(DataRow row, string columnName)
        {
            return row.Table.Columns.Contains(columnName) && row[columnName] != DBNull.Value && (bool)row[columnName];
        }

        private string BuildGenericCreateTableSql(DataTable currentDt)
        {
            string tableName = GetTableNameForSave();
            if (string.IsNullOrWhiteSpace(tableName))
            {
                return "-- " + Localization.T("Designer.EnterTableNameInOptions");
            }

            List<string> definitions = new List<string>();
            List<string> primaryColumns = new List<string>();

            foreach (DataRow row in currentDt.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;

                string columnName = GetRowString(row, "Name").Trim();
                if (string.IsNullOrWhiteSpace(columnName)) continue;

                definitions.Add("  " + BuildGenericColumnDefinition(row));

                if (row["PK"] != DBNull.Value && (bool)row["PK"])
                {
                    primaryColumns.Add(QuoteDesignerIdentifier(columnName));
                }
            }

            if (definitions.Count == 0)
            {
                return "-- " + Localization.T("Designer.AddAtLeastOneColumn");
            }

            if (primaryColumns.Count > 0)
            {
                definitions.Add("  PRIMARY KEY (" + string.Join(", ", primaryColumns.ToArray()) + ")");
            }

            string sql = "CREATE TABLE " + GetQualifiedDesignerTableName(tableName) + " (\n" +
                         string.Join(",\n", definitions.ToArray()) + "\n" +
                         ");";

            List<string> indexes = BuildGenericCreateIndexStatements(tableName);
            if (indexes.Count > 0)
            {
                sql += "\n" + string.Join("\n", indexes.ToArray());
            }

            return sql;
        }

        private string BuildGenericColumnDefinition(DataRow row)
        {
            string columnName = GetRowString(row, "Name").Trim();
            string columnType = GetRowString(row, "Type").Trim();
            string length = GetRowString(row, "Length").Trim();
            string decimals = GetRowString(row, "Decimals").Trim();
            bool notNull = row["NotNull"] != DBNull.Value && (bool)row["NotNull"];
            string defaultValue = GetRowString(row, "Default").Trim();

            List<string> parts = new List<string>
            {
                QuoteDesignerIdentifier(columnName),
                MapDesignerType(columnType, length, decimals),
                notNull ? "NOT NULL" : "NULL"
            };

            if (!string.IsNullOrWhiteSpace(defaultValue))
            {
                parts.Add("DEFAULT " + FormatGenericDefault(defaultValue));
            }

            return string.Join(" ", parts.ToArray());
        }

        private List<string> BuildGenericCreateIndexStatements(string tableName)
        {
            List<string> statements = new List<string>();
            DataTable currentIdxDt = dgvIndexes.DataSource as DataTable;
            if (currentIdxDt == null) return statements;

            foreach (DataRow row in currentIdxDt.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;

                string indexName = GetRowString(row, "名稱").Trim();
                string type = GetRowString(row, "索引類型").Trim().ToUpperInvariant();
                string columns = GetRowString(row, "欄位").Trim();
                if (string.IsNullOrWhiteSpace(columns)) continue;
                if (type == "PRIMARY") continue;
                if (type == "FULLTEXT" && !SupportsFullTextIndex()) continue;
                if (type == "SPATIAL" && !SupportsSpatialIndex()) continue;
                if (string.IsNullOrWhiteSpace(indexName)) indexName = tableName + "_idx";

                string columnList = FormatGenericIndexColumns(columns);
                if (string.IsNullOrWhiteSpace(columnList)) continue;

                if (type == "FULLTEXT" && _db is my_postgresql)
                {
                    statements.Add("CREATE INDEX " + QuoteDesignerIdentifier(indexName) +
                                   " ON " + GetQualifiedDesignerTableName(tableName) +
                                   " USING GIN (" + FormatPostgreSqlFullTextIndexExpression(columns) + ");");
                    continue;
                }

                if (type == "FULLTEXT" && _db is my_oracle)
                {
                    statements.Add(BuildOracleFullTextIndexStatement(indexName, tableName, columns));
                    continue;
                }

                if (type == "FULLTEXT" && _db is my_mssql)
                {
                    statements.Add(BuildSqlServerFullTextIndexStatement(tableName, columns));
                    continue;
                }

                if (type == "SPATIAL" && _db is my_postgresql)
                {
                    statements.Add("CREATE INDEX " + QuoteDesignerIdentifier(indexName) +
                                   " ON " + GetQualifiedDesignerTableName(tableName) +
                                   " USING GIST (" + columnList + ");");
                    continue;
                }

                if (type == "SPATIAL" && _db is my_oracle)
                {
                    statements.Add(BuildOracleSpatialIndexStatement(indexName, tableName, columns));
                    continue;
                }

                if (type == "SPATIAL" && _db is my_mssql)
                {
                    statements.Add(BuildSqlServerSpatialIndexStatement(indexName, tableName, columns));
                    continue;
                }

                string unique = type == "UNIQUE" ? "UNIQUE " : "";
                statements.Add("CREATE " + unique + "INDEX " + QuoteDesignerIdentifier(indexName) +
                               " ON " + GetQualifiedDesignerTableName(tableName) +
                               " (" + columnList + ");");
            }

            return statements;
        }

        private string MapDesignerType(string columnType, string length, string decimals)
        {
            string type = string.IsNullOrWhiteSpace(columnType) ? "varchar" : columnType.ToLowerInvariant();
            string len = string.IsNullOrWhiteSpace(length) ? "" : length;
            string scale = string.IsNullOrWhiteSpace(decimals) ? "" : decimals;

            if (_db is my_sqlite)
            {
                if (type.Contains("int")) return "INTEGER";
                if (type.Contains("decimal") || type.Contains("numeric") || type.Contains("double") || type.Contains("float") || type.Contains("real")) return "REAL";
                if (type.Contains("blob") || type.Contains("binary")) return "BLOB";
                return "TEXT";
            }

            if (_db is my_mssql)
            {
                if (type.Contains("geography")) return "GEOGRAPHY";
                if (type.Contains("geometry")) return "GEOMETRY";
                if (type.Contains("bigint")) return "BIGINT";
                if (type.Contains("smallint")) return "SMALLINT";
                if (type.Contains("tinyint")) return "TINYINT";
                if (type.Contains("int")) return "INT";
                if (type.Contains("decimal") || type.Contains("numeric")) return "DECIMAL(" + (len.Length > 0 ? len : "18") + "," + (scale.Length > 0 ? scale : "0") + ")";
                if (type.Contains("double") || type.Contains("float")) return "FLOAT";
                if (type.Contains("real")) return "REAL";
                if (type.Contains("bool") || type == "bit") return "BIT";
                if (type.Contains("date") || type.Contains("time")) return "DATETIME2";
                if (type.Contains("blob") || type.Contains("binary") || type.Contains("image")) return "VARBINARY(MAX)";
                return len.Length > 0 ? "NVARCHAR(" + len + ")" : "NVARCHAR(MAX)";
            }

            if (_db is my_postgresql)
            {
                if (type.Contains("bigint")) return "BIGINT";
                if (type.Contains("smallint")) return "SMALLINT";
                if (type.Contains("int")) return "INTEGER";
                if (type.Contains("decimal") || type.Contains("numeric")) return "NUMERIC(" + (len.Length > 0 ? len : "18") + "," + (scale.Length > 0 ? scale : "0") + ")";
                if (type.Contains("double") || type.Contains("float") || type.Contains("real")) return "DOUBLE PRECISION";
                if (type.Contains("bool") || type == "bit") return "BOOLEAN";
                if (type.Contains("date") || type.Contains("time")) return "TIMESTAMP";
                if (type.Contains("blob") || type.Contains("binary") || type.Contains("bytea")) return "BYTEA";
                return len.Length > 0 ? "VARCHAR(" + len + ")" : "TEXT";
            }

            if (_db is my_oracle)
            {
                if (type.Contains("sdo_geometry") || type == "geometry") return "SDO_GEOMETRY";
                if (type.Contains("bigint") || type.Contains("smallint") || type.Contains("int")) return "NUMBER(10)";
                if (type.Contains("decimal") || type.Contains("numeric") || type.Contains("number")) return "NUMBER(" + (len.Length > 0 ? len : "18") + "," + (scale.Length > 0 ? scale : "0") + ")";
                if (type.Contains("double") || type.Contains("float") || type.Contains("real")) return "BINARY_DOUBLE";
                if (type.Contains("bool") || type == "bit") return "NUMBER(1)";
                if (type.Contains("date") || type.Contains("time")) return "TIMESTAMP";
                if (type.Contains("blob") || type.Contains("binary") || type.Contains("bytea")) return "BLOB";
                if (type.Contains("char") || type.Contains("varchar")) return len.Length > 0 ? "VARCHAR2(" + len + ")" : "VARCHAR2(255)";
                if (type.Contains("clob")) return "CLOB";
                return len.Length > 0 ? "VARCHAR2(" + len + ")" : "CLOB";
            }

            return columnType;
        }

        private string BuildOracleSpatialIndexStatement(string indexName, string tableName, string columns)
        {
            string spatialColumn = FormatOracleSpatialIndexColumn(columns);
            return "CREATE INDEX " + QuoteDesignerIdentifier(indexName) +
                   " ON " + GetQualifiedDesignerTableName(tableName) +
                   " (" + spatialColumn + ") INDEXTYPE IS MDSYS.SPATIAL_INDEX;";
        }

        private string BuildSqlServerSpatialIndexStatement(string indexName, string tableName, string columns)
        {
            string spatialColumn = FormatSqlServerSingleIndexColumn(columns);
            string method = GetSqlServerSpatialIndexMethod(spatialColumn);
            return "CREATE SPATIAL INDEX " + QuoteDesignerIdentifier(indexName) +
                   " ON " + GetQualifiedDesignerTableName(tableName) +
                   " (" + spatialColumn + ") USING " + method + ";";
        }

        private string BuildSqlServerFullTextIndexStatement(string tableName, string columns)
        {
            string fullTextColumns = FormatSqlServerFullTextColumns(columns);
            string catalogName = "mysqlpunk_ft";
            string tableIdentifier = QuoteDesignerIdentifier(tableName).Replace("'", "''");
            string catalogIdentifier = QuoteDesignerIdentifier(catalogName).Replace("'", "''");
            string escapedFullTextColumns = fullTextColumns.Replace("'", "''");
            string tableLiteral = EscapeSqlServerLiteral(tableName);
            string databaseIdentifier = QuoteDesignerIdentifier(_databaseName);

            return "DECLARE @fullTextKeyIndex nvarchar(128);\r\n" +
                   "DECLARE @catalogName sysname = N'" + EscapeSqlServerLiteral(catalogName) + "';\r\n" +
                   "DECLARE @createCatalogSql nvarchar(max);\r\n" +
                   "DECLARE @createIndexSql nvarchar(max);\r\n" +
                   "SELECT TOP (1) @fullTextKeyIndex = i.name\r\n" +
                   "FROM " + QuoteDesignerIdentifier(_databaseName) + ".sys.indexes i\r\n" +
                   "INNER JOIN " + QuoteDesignerIdentifier(_databaseName) + ".sys.objects o ON o.object_id = i.object_id\r\n" +
                   "INNER JOIN " + QuoteDesignerIdentifier(_databaseName) + ".sys.schemas s ON s.schema_id = o.schema_id\r\n" +
                   "WHERE s.name = N'dbo' AND o.name = N'" + tableLiteral + "' AND (i.is_primary_key = 1 OR i.is_unique = 1) AND i.is_disabled = 0 AND i.has_filter = 0\r\n" +
                   "ORDER BY CASE WHEN i.is_primary_key = 1 THEN 0 ELSE 1 END, i.index_id;\r\n" +
                   "IF @fullTextKeyIndex IS NULL\r\n" +
                   "BEGIN\r\n" +
                   "    THROW 50000, 'SQL Server FULLTEXT index requires a primary key or unique index on the table.', 1;\r\n" +
                   "END\r\n" +
                   "IF NOT EXISTS (SELECT 1 FROM " + QuoteDesignerIdentifier(_databaseName) + ".sys.fulltext_catalogs WHERE name = @catalogName)\r\n" +
                   "BEGIN\r\n" +
                   "    SET @createCatalogSql = N'CREATE FULLTEXT CATALOG " + catalogIdentifier + " AS DEFAULT;';\r\n" +
                   "    EXEC " + databaseIdentifier + ".sys.sp_executesql @createCatalogSql;\r\n" +
                   "END\r\n" +
                   "SET @createIndexSql = N'CREATE FULLTEXT INDEX ON [dbo]." + tableIdentifier +
                   " (" + escapedFullTextColumns + ") KEY INDEX ' + QUOTENAME(@fullTextKeyIndex) + N' ON " + catalogIdentifier + " WITH CHANGE_TRACKING AUTO;';\r\n" +
                   "EXEC " + databaseIdentifier + ".sys.sp_executesql @createIndexSql;";
        }

        private string BuildOracleFullTextIndexStatement(string indexName, string tableName, string columns)
        {
            string textColumn = FormatOracleSingleIndexColumn(columns);
            return "CREATE INDEX " + QuoteDesignerIdentifier(indexName) +
                   " ON " + GetQualifiedDesignerTableName(tableName) +
                   " (" + textColumn + ") INDEXTYPE IS CTXSYS.CONTEXT;";
        }

        private string FormatOracleSpatialIndexColumn(string columns)
        {
            return FormatOracleSingleIndexColumn(columns);
        }

        private string FormatOracleSingleIndexColumn(string columns)
        {
            string[] rawCols = columns.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (rawCols.Length == 0) return "";
            string columnName = GetIndexColumnName(rawCols[0]);
            return QuoteDesignerIdentifier(columnName);
        }

        private string FormatSqlServerSingleIndexColumn(string columns)
        {
            string[] rawCols = columns.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (rawCols.Length == 0) return "";
            string columnName = GetIndexColumnName(rawCols[0]);
            return QuoteDesignerIdentifier(columnName);
        }

        private string FormatSqlServerFullTextColumns(string columns)
        {
            string[] rawCols = columns.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            List<string> formattedCols = new List<string>();
            foreach (string rawCol in rawCols)
            {
                string columnName = GetIndexColumnName(rawCol);
                if (string.IsNullOrWhiteSpace(columnName)) continue;
                formattedCols.Add(QuoteDesignerIdentifier(columnName) + " LANGUAGE 0x0");
            }

            return string.Join(", ", formattedCols.ToArray());
        }

        private string GetSqlServerSpatialIndexMethod(string quotedColumnName)
        {
            if (_originalDt != null)
            {
                string columnName = quotedColumnName.Trim('[', ']');
                foreach (DataRow row in _originalDt.Rows)
                {
                    if (GetRowString(row, "Name").Trim().Equals(columnName, StringComparison.OrdinalIgnoreCase) ||
                        GetRowString(row, "_OldName").Trim().Equals(columnName, StringComparison.OrdinalIgnoreCase))
                    {
                        string type = GetRowString(row, "Type").Trim().ToLowerInvariant();
                        if (type.Contains("geography")) return "GEOGRAPHY_AUTO_GRID";
                        break;
                    }
                }
            }

            return "GEOMETRY_AUTO_GRID";
        }

        private string FormatGenericIndexColumns(string columns)
        {
            return FormatIndexColumns(columns, QuoteDesignerIdentifier);
        }

        private string FormatPostgreSqlFullTextIndexExpression(string columns)
        {
            string[] rawCols = columns.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            List<string> vectors = new List<string>();
            foreach (string rawCol in rawCols)
            {
                string columnName = GetIndexColumnName(rawCol);
                if (string.IsNullOrWhiteSpace(columnName)) continue;
                vectors.Add("coalesce(" + QuoteDesignerIdentifier(columnName) + "::text, '')");
            }

            if (vectors.Count == 0)
            {
                return "to_tsvector('simple', '')";
            }

            return "to_tsvector('simple', " + string.Join(" || ' ' || ", vectors.ToArray()) + ")";
        }

        private string GetQualifiedDesignerTableName(string tableName)
        {
            if (_db is my_mssql)
            {
                return QuoteDesignerIdentifier(_databaseName) + ".[dbo]." + QuoteDesignerIdentifier(tableName);
            }
            if (_db is my_postgresql)
            {
                return "public." + QuoteDesignerIdentifier(tableName);
            }
            if (_db is my_oracle)
            {
                return QuoteDesignerIdentifier(_databaseName) + "." + QuoteDesignerIdentifier(tableName);
            }
            return QuoteDesignerIdentifier(tableName);
        }

        private string QuoteDesignerIdentifier(string name)
        {
            if (_db is my_mssql)
            {
                return "[" + name.Replace("]", "]]") + "]";
            }
            if (_db is my_postgresql || _db is my_sqlite || _db is my_oracle)
            {
                return "\"" + name.Replace("\"", "\"\"") + "\"";
            }
            return QuoteMySqlIdentifier(name);
        }

        private static string FormatGenericDefault(string value)
        {
            string upper = value.ToUpperInvariant();
            if (upper == "NULL" || upper == "CURRENT_TIMESTAMP" || upper == "CURRENT_TIMESTAMP()")
            {
                return value;
            }
            if ((value.StartsWith("'") && value.EndsWith("'")) ||
                (value.StartsWith("\"") && value.EndsWith("\"")))
            {
                return value;
            }

            decimal numericValue;
            if (decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out numericValue))
            {
                return value;
            }

            return "'" + value.Replace("'", "''") + "'";
        }

        private string BuildMySqlCreateTableSql(DataTable currentDt)
        {
            string tableName = GetTableNameForSave();
            if (string.IsNullOrWhiteSpace(tableName))
            {
                return "-- " + Localization.T("Designer.EnterTableNameInOptions");
            }

            List<string> definitions = new List<string>();
            List<string> primaryColumns = new List<string>();

            foreach (DataRow row in currentDt.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;

                string columnName = GetRowString(row, "Name").Trim();
                if (string.IsNullOrWhiteSpace(columnName)) continue;

                definitions.Add("  " + BuildMySqlColumnDefinition(row));

                if (row["PK"] != DBNull.Value && (bool)row["PK"])
                {
                    primaryColumns.Add(QuoteMySqlIdentifier(columnName));
                }
            }

            if (definitions.Count == 0)
            {
                return "-- " + Localization.T("Designer.AddAtLeastOneColumn");
            }

            foreach (string indexDefinition in BuildMySqlCreateIndexDefinitions(primaryColumns))
            {
                definitions.Add("  " + indexDefinition);
            }

            string engine = cbEngine?.SelectedItem?.ToString();
            if (string.IsNullOrWhiteSpace(engine)) engine = "InnoDB";

            string comment = txtTableComment?.Text.Trim() ?? "";
            string commentSql = string.IsNullOrWhiteSpace(comment)
                ? ""
                : " COMMENT=" + EscapeMySqlStringLiteral(comment);

            return "CREATE TABLE " + QuoteMySqlIdentifier(_databaseName) + "." + QuoteMySqlIdentifier(tableName) + " (\n" +
                   string.Join(",\n", definitions) + "\n" +
                   ") ENGINE=" + engine + " DEFAULT CHARSET=utf8mb4" + commentSql + ";";
        }

        private string BuildMySqlColumnDefinition(DataRow row)
        {
            string columnName = GetRowString(row, "Name").Trim();
            string columnType = GetRowString(row, "Type").Trim();
            if (string.IsNullOrWhiteSpace(columnType)) columnType = "varchar";

            string length = GetRowString(row, "Length").Trim();
            string decimals = GetRowString(row, "Decimals").Trim();
            string typeFull = columnType;
            if (!string.IsNullOrWhiteSpace(length))
            {
                typeFull += "(" + length;
                if (!string.IsNullOrWhiteSpace(decimals)) typeFull += "," + decimals;
                typeFull += ")";
            }

            bool notNull = row["NotNull"] != DBNull.Value && (bool)row["NotNull"];
            string defaultValue = GetRowString(row, "Default").Trim();
            string comment = GetRowString(row, "Comment");

            List<string> parts = new List<string>
            {
                QuoteMySqlIdentifier(columnName),
                typeFull,
                notNull ? "NOT NULL" : "NULL"
            };

            if (!string.IsNullOrWhiteSpace(defaultValue))
            {
                parts.Add("DEFAULT " + FormatMySqlDefault(defaultValue));
            }

            if (!string.IsNullOrWhiteSpace(comment))
            {
                parts.Add("COMMENT " + EscapeMySqlStringLiteral(comment));
            }

            return string.Join(" ", parts);
        }

        private List<string> BuildMySqlCreateIndexDefinitions(List<string> primaryColumns)
        {
            List<string> definitions = new List<string>();
            DataTable currentIdxDt = dgvIndexes.DataSource as DataTable;

            if (currentIdxDt != null)
            {
                foreach (DataRow row in currentIdxDt.Rows)
                {
                    if (row.RowState == DataRowState.Deleted) continue;

                    string indexName = GetRowString(row, "名稱").Trim();
                    string type = GetRowString(row, "索引類型").Trim().ToUpperInvariant();
                    string columns = GetRowString(row, "欄位").Trim();
                    if (string.IsNullOrWhiteSpace(type)) type = "NORMAL";
                    if (string.IsNullOrWhiteSpace(columns)) continue;

                    string columnList = FormatMySqlIndexColumns(columns);
                    if (string.IsNullOrWhiteSpace(columnList)) continue;

                    if (type == "PRIMARY")
                    {
                        definitions.Add("PRIMARY KEY (" + columnList + ")");
                        primaryColumns.Clear();
                    }
                    else if (type == "UNIQUE")
                    {
                        if (string.IsNullOrWhiteSpace(indexName)) continue;
                        definitions.Add("UNIQUE KEY " + QuoteMySqlIdentifier(indexName) + " (" + columnList + ")");
                    }
                    else if (type == "FULLTEXT")
                    {
                        if (string.IsNullOrWhiteSpace(indexName)) continue;
                        definitions.Add("FULLTEXT KEY " + QuoteMySqlIdentifier(indexName) + " (" + columnList + ")");
                    }
                    else if (type == "SPATIAL")
                    {
                        if (string.IsNullOrWhiteSpace(indexName)) continue;
                        definitions.Add("SPATIAL KEY " + QuoteMySqlIdentifier(indexName) + " (" + columnList + ")");
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(indexName)) continue;
                        definitions.Add("KEY " + QuoteMySqlIdentifier(indexName) + " (" + columnList + ")");
                    }
                }
            }

            if (primaryColumns.Count > 0)
            {
                definitions.Insert(0, "PRIMARY KEY (" + string.Join(", ", primaryColumns) + ")");
            }

            return definitions;
        }

        private string GetTableNameForSave()
        {
            return IsNewTable ? (txtTableName?.Text.Trim() ?? "") : _tableName;
        }

        private static string GetRowString(DataRow row, string columnName)
        {
            if (!row.Table.Columns.Contains(columnName) || row[columnName] == DBNull.Value)
                return "";
            return row[columnName].ToString();
        }

        private static string FormatMySqlDefault(string value)
        {
            string upper = value.ToUpperInvariant();
            if (upper == "NULL" || upper == "CURRENT_TIMESTAMP" || upper == "CURRENT_TIMESTAMP()")
            {
                return value;
            }

            decimal numericValue;
            if (decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out numericValue))
            {
                return value;
            }

            return EscapeMySqlStringLiteral(value);
        }

        private static string FormatMySqlIndexColumns(string columns)
        {
            return FormatIndexColumns(columns, QuoteMySqlIdentifier);
        }

        private static string FormatIndexColumns(string columns, Func<string, string> quoteIdentifier)
        {
            string[] rawCols = columns.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            List<string> formattedCols = new List<string>();
            foreach (string rawCol in rawCols)
            {
                string col = rawCol.Trim();
                if (!string.IsNullOrWhiteSpace(col))
                {
                    formattedCols.Add(FormatIndexColumn(col, quoteIdentifier));
                }
            }

            return string.Join(", ", formattedCols);
        }

        private static string FormatIndexColumn(string columnExpression, Func<string, string> quoteIdentifier)
        {
            return quoteIdentifier(GetIndexColumnName(columnExpression)) + GetIndexDirectionSuffix(columnExpression);
        }

        private static string GetIndexColumnName(string columnExpression)
        {
            string columnName = (columnExpression ?? "").Trim();
            if (EndsWithIndexDirection(columnName, "ASC"))
            {
                return columnName.Substring(0, columnName.Length - 3).TrimEnd();
            }
            if (EndsWithIndexDirection(columnName, "DESC"))
            {
                return columnName.Substring(0, columnName.Length - 4).TrimEnd();
            }
            return columnName;
        }

        private static string GetIndexDirectionSuffix(string columnExpression)
        {
            string value = (columnExpression ?? "").Trim();
            if (EndsWithIndexDirection(value, "ASC")) return " ASC";
            if (EndsWithIndexDirection(value, "DESC")) return " DESC";
            return "";
        }

        private static bool EndsWithIndexDirection(string value, string direction)
        {
            if (!value.EndsWith(direction, StringComparison.OrdinalIgnoreCase)) return false;

            int separatorIndex = value.Length - direction.Length - 1;
            return separatorIndex >= 0 && char.IsWhiteSpace(value[separatorIndex]);
        }

        private static string QuoteMySqlIdentifier(string name)
        {
            return "`" + name.Replace("`", "``") + "`";
        }

        private static string EscapeMySqlStringLiteral(string value)
        {
            return "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
        }

        private static string EscapeSqlStringLiteral(string value)
        {
            return "'" + (value ?? "").Replace("'", "''") + "'";
        }

        private static string EscapeSqlStringValue(string value)
        {
            return (value ?? "").Replace("'", "''");
        }

        private static string EscapeSqlServerLiteral(string value)
        {
            return (value ?? "").Replace("'", "''");
        }

        private void BtnSave_Click(object sender, EventArgs e)
        {
            GeneratePreviewSql();
            string sql = rtbSqlPreview.Text;
            
            if (sql.StartsWith("--"))
            {
                MessageBox.Show(sql.TrimStart('-', ' '), Localization.T("Designer.CannotSaveTitle"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (MessageBox.Show(Localization.Format("Designer.ConfirmExecuteSql", sql), Localization.T("Designer.ConfirmSaveTitle"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                var res = ExecuteDesignerSql(sql);
                if (res["status"] == "OK")
                {
                    MessageBox.Show(Localization.T("Designer.SaveSucceeded"), Localization.T("Common.Success"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                    if (IsNewTable)
                    {
                        _tableName = GetTableNameForSave();
                        if (txtTableName != null) txtTableName.ReadOnly = true;
                    }
                    IsModified = false;
                    LoadColumns(); // 重新載入
                    LoadIndexes();
                    UpdateTitle();
                }
                else
                {
                    MessageBox.Show(FormatDesignerSaveError(_db?.ProviderName, _databaseName, GetTableNameForSave(), res), Localization.T("Designer.SaveFailedTitle"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private Dictionary<string, string> ExecuteDesignerSql(string sql)
        {
            Dictionary<string, string> lastResult = new Dictionary<string, string> { { "status", "OK" } };
            bool transactionStarted = false;
            bool sqliteForeignKeysDisabled = false;
            string currentStatement = "";

            try
            {
                List<string> statements = SplitDesignerSqlStatements(sql, _db);
                foreach (string statement in statements)
                {
                    if (string.IsNullOrWhiteSpace(statement)) continue;

                    string normalizedStatement = statement.Trim();
                    currentStatement = normalizedStatement;
                    lastResult = _db.ExecSQL(normalizedStatement);

                    if (!lastResult.ContainsKey("status") || lastResult["status"] != "OK")
                    {
                        if (!lastResult.ContainsKey("statement"))
                        {
                            lastResult["statement"] = normalizedStatement;
                        }
                        RollbackDesignerTransactionIfNeeded(transactionStarted, sqliteForeignKeysDisabled);
                        return lastResult;
                    }

                    if (string.Equals(normalizedStatement, "BEGIN TRANSACTION", StringComparison.OrdinalIgnoreCase))
                    {
                        transactionStarted = true;
                    }
                    else if (string.Equals(normalizedStatement, "COMMIT", StringComparison.OrdinalIgnoreCase))
                    {
                        transactionStarted = false;
                    }
                    else if (_db is my_sqlite && string.Equals(normalizedStatement, "PRAGMA foreign_keys=OFF", StringComparison.OrdinalIgnoreCase))
                    {
                        sqliteForeignKeysDisabled = true;
                    }
                    else if (_db is my_sqlite && string.Equals(normalizedStatement, "PRAGMA foreign_keys=ON", StringComparison.OrdinalIgnoreCase))
                    {
                        sqliteForeignKeysDisabled = false;
                    }
                }

                return lastResult;
            }
            catch (Exception ex)
            {
                RollbackDesignerTransactionIfNeeded(transactionStarted, sqliteForeignKeysDisabled);
                return new Dictionary<string, string>
                {
                    { "status", "ERROR" },
                    { "reason", ex.Message },
                    { "statement", currentStatement }
                };
            }
        }

        private static string FormatDesignerSaveError(string providerName, string databaseName, string tableName, Dictionary<string, string> result)
        {
            string reason = GetResultValue(result, "reason");
            if (string.IsNullOrWhiteSpace(reason)) reason = "unknown error";

            List<string> lines = new List<string>();
            lines.Add(Localization.Format("Designer.SaveFailedReason", reason));

            if (string.Equals(providerName, "oracle", StringComparison.OrdinalIgnoreCase))
            {
                lines.Add("");
                lines.Add(Localization.T("Designer.OracleDiagnosticTitle"));
                foreach (string hint in GetOracleDesignerErrorHints(reason, databaseName, tableName))
                {
                    lines.Add("- " + hint);
                }
            }

            string statement = GetResultValue(result, "statement");
            if (!string.IsNullOrWhiteSpace(statement))
            {
                lines.Add("");
                lines.Add(Localization.T("Designer.FailedSql"));
                lines.Add(CompactSqlForMessage(statement, 700));
            }

            return string.Join(Environment.NewLine, lines.ToArray());
        }

        private static IEnumerable<string> GetOracleDesignerErrorHints(string reason, string databaseName, string tableName)
        {
            string owner = string.IsNullOrWhiteSpace(databaseName) ? Localization.T("Designer.CurrentSchema") : databaseName;
            string objectName = string.IsNullOrWhiteSpace(tableName) ? Localization.T("Designer.CurrentTable") : tableName;

            if (ContainsOracleError(reason, "ORA-01031"))
            {
                yield return Localization.T("Designer.OracleHintInsufficientPrivileges");
                yield return Localization.Format("Designer.OracleHintCrossSchemaPrivileges", owner, objectName);
                yield break;
            }

            if (ContainsOracleError(reason, "ORA-00942") || ContainsOracleError(reason, "ORA-04043"))
            {
                yield return Localization.Format("Designer.OracleHintObjectMissing", owner, objectName);
                yield return Localization.T("Designer.OracleHintRefreshAfterObjectChange");
                yield break;
            }

            if (ContainsOracleError(reason, "ORA-00955") || ContainsOracleError(reason, "ORA-01430"))
            {
                yield return Localization.T("Designer.OracleHintNameConflict");
                yield break;
            }

            if (ContainsOracleError(reason, "ORA-01442") || ContainsOracleError(reason, "ORA-01451"))
            {
                yield return Localization.T("Designer.OracleHintNullStateChanged");
                yield break;
            }

            if (ContainsOracleError(reason, "ORA-02296") || ContainsOracleError(reason, "ORA-01400"))
            {
                yield return Localization.T("Designer.OracleHintNotNullConflict");
                yield break;
            }

            if (ContainsOracleError(reason, "ORA-02429"))
            {
                yield return Localization.T("Designer.OracleHintConstraintIndexConflict");
                yield break;
            }

            if (ContainsOracleError(reason, "ORA-01735") || ContainsOracleError(reason, "ORA-00922"))
            {
                yield return Localization.T("Designer.OracleHintAlterSyntax");
                yield break;
            }

            yield return Localization.Format("Designer.OracleHintGeneric", owner, objectName);
        }

        private static bool ContainsOracleError(string reason, string code)
        {
            return !string.IsNullOrWhiteSpace(reason) &&
                   reason.IndexOf(code, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string GetResultValue(Dictionary<string, string> result, string key)
        {
            if (result == null || !result.ContainsKey(key) || result[key] == null) return "";
            return result[key];
        }

        private static string CompactSqlForMessage(string sql, int maxLength)
        {
            string value = (sql ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            if (maxLength > 0 && value.Length > maxLength)
            {
                return value.Substring(0, maxLength - 3) + "...";
            }
            return value;
        }

        private void RollbackDesignerTransactionIfNeeded(bool transactionStarted, bool sqliteForeignKeysDisabled)
        {
            if (transactionStarted)
            {
                try { _db.ExecSQL("ROLLBACK"); } catch { }
            }

            if (sqliteForeignKeysDisabled && _db is my_sqlite)
            {
                try { _db.ExecSQL("PRAGMA foreign_keys=ON"); } catch { }
            }
        }

        private static List<string> SplitSqlStatements(string sql)
        {
            List<string> statements = new List<string>();
            bool inSingleQuote = false;
            bool inDoubleQuote = false;
            int start = 0;

            for (int i = 0; i < sql.Length; i++)
            {
                char ch = sql[i];
                char next = i + 1 < sql.Length ? sql[i + 1] : '\0';

                if (inSingleQuote && ch == '\\')
                {
                    i++;
                    continue;
                }

                if (ch == '\'' && !inDoubleQuote)
                {
                    if (inSingleQuote && next == '\'')
                    {
                        i++;
                        continue;
                    }
                    inSingleQuote = !inSingleQuote;
                }
                else if (ch == '"' && !inSingleQuote)
                {
                    if (inDoubleQuote && next == '"')
                    {
                        i++;
                        continue;
                    }
                    inDoubleQuote = !inDoubleQuote;
                }
                else if (ch == ';' && !inSingleQuote && !inDoubleQuote)
                {
                    string statement = sql.Substring(start, i - start).Trim();
                    if (statement.Length > 0) statements.Add(statement);
                    start = i + 1;
                }
            }

            string last = sql.Substring(start).Trim();
            if (last.Length > 0) statements.Add(last);
            return statements;
        }

        private static List<string> SplitDesignerSqlStatements(string sql, IDatabase db)
        {
            if (db is my_mssql) return SplitSqlServerStatements(sql);
            if (db is my_postgresql) return SplitPostgreSqlStatements(sql);
            if (db is my_oracle) return SplitOracleStatements(sql);
            return SplitSqlStatements(sql);
        }

        private static List<string> SplitSqlServerStatements(string sql)
        {
            List<string> statements = new List<string>();
            StringBuilder pending = new StringBuilder();
            string normalizedSql = (sql ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalizedSql.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.TrimStart();
                if (StartsSqlServerDesignerBatch(trimmed))
                {
                    FlushSplitStatements(pending, statements);

                    StringBuilder batch = new StringBuilder();
                    batch.AppendLine(line);
                    while (i + 1 < lines.Length)
                    {
                        i++;
                        batch.AppendLine(lines[i]);
                        string batchLine = lines[i].TrimStart();
                        if (IsSqlServerDesignerBatchEnd(batchLine))
                        {
                            break;
                        }
                    }

                    string batchSql = batch.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(batchSql)) statements.Add(batchSql);
                    continue;
                }

                pending.AppendLine(line);
            }

            FlushSplitStatements(pending, statements);
            return statements;
        }

        private static bool StartsSqlServerDesignerBatch(string statement)
        {
            return statement.StartsWith("DECLARE @constraintName", StringComparison.OrdinalIgnoreCase) ||
                   statement.StartsWith("DECLARE @primaryKeyName", StringComparison.OrdinalIgnoreCase) ||
                   statement.StartsWith("DECLARE @fullTextKeyIndex", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSqlServerDesignerBatchEnd(string statement)
        {
            return statement.StartsWith("IF @constraintName", StringComparison.OrdinalIgnoreCase) ||
                   statement.StartsWith("IF @primaryKeyName", StringComparison.OrdinalIgnoreCase) ||
                   (statement.StartsWith("EXEC ", StringComparison.OrdinalIgnoreCase) &&
                    statement.IndexOf("@createIndexSql", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static void FlushSplitStatements(StringBuilder pending, List<string> statements)
        {
            if (pending.Length == 0) return;
            foreach (string statement in SplitSqlStatements(pending.ToString()))
            {
                if (!string.IsNullOrWhiteSpace(statement)) statements.Add(statement);
            }
            pending.Clear();
        }

        private static List<string> SplitPostgreSqlStatements(string sql)
        {
            List<string> statements = new List<string>();
            StringBuilder pending = new StringBuilder();
            string normalizedSql = (sql ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalizedSql.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("DO $mysqlpunk$", StringComparison.OrdinalIgnoreCase))
                {
                    FlushSplitStatements(pending, statements);

                    StringBuilder block = new StringBuilder();
                    block.AppendLine(line);
                    while (i + 1 < lines.Length)
                    {
                        i++;
                        block.AppendLine(lines[i]);
                        if (lines[i].Trim().Equals("$mysqlpunk$;", StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }
                    }

                    string blockSql = block.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(blockSql)) statements.Add(blockSql);
                    continue;
                }

                pending.AppendLine(line);
            }

            FlushSplitStatements(pending, statements);
            return statements;
        }

        private static List<string> SplitOracleStatements(string sql)
        {
            List<string> statements = new List<string>();
            StringBuilder pending = new StringBuilder();
            string normalizedSql = (sql ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalizedSql.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.TrimStart();
                if (trimmed.Equals("DECLARE", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Equals("BEGIN", StringComparison.OrdinalIgnoreCase))
                {
                    FlushSplitStatements(pending, statements);

                    StringBuilder block = new StringBuilder();
                    block.AppendLine(line);
                    while (i + 1 < lines.Length)
                    {
                        i++;
                        block.AppendLine(lines[i]);
                        if (lines[i].Trim().Equals("END;", StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }
                    }

                    string blockSql = block.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(blockSql)) statements.Add(blockSql);
                    continue;
                }

                pending.AppendLine(line);
            }

            FlushSplitStatements(pending, statements);
            return statements;
        }

        private void AddColumn(bool insert)
        {
            DataTable dt = (DataTable)dgvColumns.DataSource;
            if (dt == null)
            {
                dt = CreateColumnsDisplayTable();
                BindColumns(dt);
                _originalDt = dt.Copy();
            }

            DataRow newRow = dt.NewRow();
            newRow["Name"] = "new_column";
            newRow["Type"] = "varchar";
            newRow["Length"] = "255";
            newRow["NotNull"] = false;
            newRow["PK"] = false;
            
            if (insert && dgvColumns.CurrentRow != null)
                dt.Rows.InsertAt(newRow, dgvColumns.CurrentRow.Index);
            else
                dt.Rows.Add(newRow);

            MarkAsModified();
        }

        private void DeleteColumn()
        {
            if (dgvColumns.CurrentRow == null || dgvColumns.CurrentRow.IsNewRow) return;
            dgvColumns.Rows.RemoveAt(dgvColumns.CurrentRow.Index);
        }

        private void MoveColumn(int direction)
        {
            if (dgvColumns.CurrentRow == null || dgvColumns.CurrentRow.IsNewRow) return;
            int oldIdx = dgvColumns.CurrentRow.Index;
            int newIdx = oldIdx + direction;
            
            DataTable dt = (DataTable)dgvColumns.DataSource;
            if (newIdx >= 0 && newIdx < dt.Rows.Count)
            {
                DataRow row = dt.Rows[oldIdx];
                DataRow newRow = dt.NewRow();
                newRow.ItemArray = row.ItemArray;
                dt.Rows.RemoveAt(oldIdx);
                dt.Rows.InsertAt(newRow, newIdx);
                dgvColumns.CurrentCell = dgvColumns.Rows[newIdx].Cells[0];
            }
        }

        private void AddIndex()
        {
            DataTable dt = (DataTable)dgvIndexes.DataSource;
            if (dt == null) return;
            DataRow newRow = dt.NewRow();
            newRow["名稱"] = "idx_new";
            newRow["索引類型"] = "NORMAL";
            newRow["索引方法"] = "BTREE";
            dt.Rows.Add(newRow);
            MarkAsModified();
        }

        private void DeleteIndex()
        {
            if (dgvIndexes.CurrentRow == null || dgvIndexes.CurrentRow.IsNewRow) return;
            dgvIndexes.Rows.RemoveAt(dgvIndexes.CurrentRow.Index);
        }

        private void MoveIndex(int direction)
        {
            if (dgvIndexes.CurrentRow == null || dgvIndexes.CurrentRow.IsNewRow) return;
            int oldIdx = dgvIndexes.CurrentRow.Index;
            int newIdx = oldIdx + direction;
            DataTable dt = (DataTable)dgvIndexes.DataSource;
            if (newIdx >= 0 && newIdx < dt.Rows.Count)
            {
                DataRow row = dt.Rows[oldIdx];
                DataRow newRow = dt.NewRow();
                newRow.ItemArray = row.ItemArray;
                dt.Rows.RemoveAt(oldIdx);
                dt.Rows.InsertAt(newRow, newIdx);
                dgvIndexes.CurrentCell = dgvIndexes.Rows[newIdx].Cells[0];
            }
        }

        private void LoadIndexes()
        {
            try
            {
                DataTable displayIdx = CreateIndexesDisplayTable();

                if (IsNewTable)
                {
                    _originalIdxDt = displayIdx.Copy();
                    BindIndexes(displayIdx);
                    return;
                }

                DataTable rawIdx = _db.GetIndexes(_databaseName, _tableName);

                // Provider 索引 metadata 會按索引分多列，欄位名稱在不同 driver 大小寫不同。
                var indexGroups = new Dictionary<string, List<DataRow>>();
                foreach (DataRow row in rawIdx.Rows)
                {
                    string keyName = GetMetadataString(row, "Key_name", "KEY_NAME", "KeyName", "IndexName", "INDEXNAME", "index_name", "name");
                    if (string.IsNullOrWhiteSpace(keyName)) continue;
                    if (!indexGroups.ContainsKey(keyName)) indexGroups[keyName] = new List<DataRow>();
                    indexGroups[keyName].Add(row);
                }

                foreach (var group in indexGroups)
                {
                    DataRow first = group.Value[0];
                    DataRow newRow = displayIdx.NewRow();
                    newRow["名稱"] = group.Key;
                    newRow["_OldName"] = group.Key;
                    
                    // 組合多欄位索引
                    group.Value.Sort((left, right) => GetMetadataInt(left, "Seq_in_index", "SEQ_IN_INDEX", "SeqInIndex", "SEQININDEX", "seqno")
                        .CompareTo(GetMetadataInt(right, "Seq_in_index", "SEQ_IN_INDEX", "SeqInIndex", "SEQININDEX", "seqno")));
                    List<string> cols = new List<string>();
                    foreach (var r in group.Value)
                    {
                        string columnName = GetMetadataString(r, "Column_name", "COLUMN_NAME", "ColumnName", "COLUMNNAME", "column_name", "name");
                        if (!string.IsNullOrWhiteSpace(columnName)) cols.Add(columnName);
                    }
                    newRow["欄位"] = string.Join(", ", cols);

                    string nonUnique = GetMetadataString(first, "Non_unique", "NON_UNIQUE", "NonUnique", "NONUNIQUE");
                    string indexType = GetMetadataString(first, "Index_type", "INDEX_TYPE", "IndexType", "INDEXTYPE", "type_desc");
                    newRow["索引類型"] = GetDesignerIndexType(group.Key, nonUnique, indexType);

                    newRow["索引方法"] = indexType;
                    newRow["註解"] = GetMetadataString(first, "Index_comment", "INDEX_COMMENT", "IndexComment", "INDEXCOMMENT", "comment");
                    
                    displayIdx.Rows.Add(newRow);
                }

                _originalIdxDt = displayIdx.Copy();
                BindIndexes(displayIdx);
            }
            catch { /* 暫時不處理錯誤 */ }
        }

        private static string GetDesignerIndexType(string keyName, string nonUnique, string indexType)
        {
            string normalizedType = (indexType ?? "").Trim().ToUpperInvariant();
            if (string.Equals(keyName, "PRIMARY", StringComparison.OrdinalIgnoreCase)) return "PRIMARY";
            if (normalizedType == "FULLTEXT" || normalizedType.Contains("FULLTEXT")) return "FULLTEXT";
            if (normalizedType == "SPATIAL" || normalizedType.Contains("SPATIAL")) return "SPATIAL";
            if (nonUnique == "0" || string.Equals(nonUnique, "False", StringComparison.OrdinalIgnoreCase)) return "UNIQUE";
            return "NORMAL";
        }

        private DataTable CreateIndexesDisplayTable()
        {
            DataTable displayIdx = new DataTable();
            displayIdx.Columns.Add("名稱");
            displayIdx.Columns.Add("欄位");
            displayIdx.Columns.Add("索引類型");
            displayIdx.Columns.Add("索引方法");
            displayIdx.Columns.Add("註解");
            displayIdx.Columns.Add("_OldName");
            return displayIdx;
        }

        private object[] GetSupportedIndexTypes()
        {
            if (_db is my_mysql)
            {
                return new object[] { "NORMAL", "UNIQUE", "PRIMARY", "FULLTEXT", "SPATIAL" };
            }

            if (_db is my_postgresql)
            {
                return new object[] { "NORMAL", "UNIQUE", "PRIMARY", "FULLTEXT", "SPATIAL" };
            }

            if (_db is my_oracle)
            {
                return new object[] { "NORMAL", "UNIQUE", "PRIMARY", "FULLTEXT", "SPATIAL" };
            }

            if (_db is my_mssql)
            {
                return new object[] { "NORMAL", "UNIQUE", "PRIMARY", "FULLTEXT", "SPATIAL" };
            }

            return new object[] { "NORMAL", "UNIQUE", "PRIMARY" };
        }

        private void BindIndexes(DataTable displayIdx)
        {
            dgvIndexes.DataSource = displayIdx;

            // 移除舊的文字欄位，改用 ComboBox 欄位
            if (dgvIndexes.Columns.Contains("索引類型") && !(dgvIndexes.Columns["索引類型"] is DataGridViewComboBoxColumn))
            {
                int idx = dgvIndexes.Columns["索引類型"].Index;
                dgvIndexes.Columns.RemoveAt(idx);
                var cb = new DataGridViewComboBoxColumn()
                {
                    Name = "索引類型",
                    HeaderText = Localization.T("Designer.IndexType"),
                    DataPropertyName = "索引類型",
                    FlatStyle = FlatStyle.Flat
                };
                cb.Items.AddRange(GetSupportedIndexTypes());
                dgvIndexes.Columns.Insert(idx, cb);
            }

            if (dgvIndexes.Columns.Contains("索引方法") && !(dgvIndexes.Columns["索引方法"] is DataGridViewComboBoxColumn))
            {
                int idx = dgvIndexes.Columns["索引方法"].Index;
                dgvIndexes.Columns.RemoveAt(idx);
                var cb = new DataGridViewComboBoxColumn()
                {
                    Name = "索引方法",
                    HeaderText = Localization.T("Designer.IndexMethod"),
                    DataPropertyName = "索引方法",
                    FlatStyle = FlatStyle.Flat
                };
                cb.Items.AddRange(new object[] { "BTREE", "HASH" });
                dgvIndexes.Columns.Insert(idx, cb);
            }

            if (dgvIndexes.Columns.Contains("_OldName")) dgvIndexes.Columns["_OldName"].Visible = false;
        }

        private Image GetIcon(string path)
        {
            try { return Image.FromFile(path); }
            catch { return null; } // 找不到圖就顯示文字就好，不要報錯
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_mainHost != null)
            {
                _mainHost.NotifyDockableFormClosed(this);
            }
            base.OnFormClosed(e);
        }
    }
}
