using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
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
                tableTitle = "New Table";
            }

            this.Text = $"{prefix}Design Table - {tableTitle}";
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
            ToolStripButton btnSave = new ToolStripButton("儲存", GetIcon(iconPath + "save.png"), BtnSave_Click);
            
            ToolStripSeparator sep1 = new ToolStripSeparator();
            ToolStripButton btnAddCol = new ToolStripButton("加入欄位", GetIcon(iconPath + "add.png"), (s, e) => AddColumn(false));
            ToolStripButton btnInsertCol = new ToolStripButton("插入欄位", GetIcon(iconPath + "insert.png"), (s, e) => AddColumn(true));
            ToolStripButton btnDelCol = new ToolStripButton("刪除欄位", GetIcon(iconPath + "delete.png"), (s, e) => DeleteColumn());
            
            ToolStripSeparator sep2 = new ToolStripSeparator();
            ToolStripButton btnMoveUp = new ToolStripButton("上移", GetIcon(iconPath + "up.png"), (s, e) => MoveColumn(-1));
            ToolStripButton btnMoveDown = new ToolStripButton("下移", GetIcon(iconPath + "down.png"), (s, e) => MoveColumn(1));

            btnFloat = new ToolStripButton("浮動", null, (s, e) => _mainHost?.FloatDockableForm(this)) { Alignment = ToolStripItemAlignment.Right };
            btnDock = new ToolStripButton("嵌入", null, (s, e) => _mainHost?.DockDockableForm(this)) { Visible = false, Alignment = ToolStripItemAlignment.Right };
            
            tsTop.Items.AddRange(new ToolStripItem[] { 
                btnSave, sep1, btnAddCol, btnInsertCol, btnDelCol, sep2, btnMoveUp, btnMoveDown, btnFloat, btnDock 
            });

            tcMain = new TabControl() { Dock = DockStyle.Fill, Padding = new Point(12, 5) };
            tcMain.SelectedIndexChanged += (s, e) => {
                bool isCol = tcMain.SelectedTab == tpColumns;
                bool isIdx = tcMain.SelectedTab == tpIndexes;
                
                btnAddCol.Text = isCol ? "加入欄位" : (isIdx ? "加入索引" : "加入");
                btnInsertCol.Visible = isCol; // 索引通常沒有「插入」概念
                btnDelCol.Text = isCol ? "刪除欄位" : (isIdx ? "刪除索引" : "刪除");
                
                if (tcMain.SelectedTab == tpSqlPreview) GeneratePreviewSql();
            };
            
            // 1. 欄位分頁
            tpColumns = new TabPage("欄位");
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
            pnlColumnProperties.Controls.Add(new Label() { Text = "欄位屬性 (選取欄位以進行詳細設定)", ForeColor = Color.Gray, Location = new Point(10, 10), AutoSize = true });
            
            splitColumns.Panel1.Controls.Add(dgvColumns);
            splitColumns.Panel2.Controls.Add(pnlColumnProperties);
            tpColumns.Controls.Add(splitColumns);

            // 2. 索引分頁
            tpIndexes = new TabPage("索引");
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
            tpOptions = new TabPage("選項");
            TableLayoutPanel tlpOptions = new TableLayoutPanel() { Dock = DockStyle.Top, Height = 200, RowCount = 4, ColumnCount = 2, Padding = new Padding(20) };
            tlpOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            tlpOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            tlpOptions.Controls.Add(new Label() { Text = "資料表名稱:", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 0);
            txtTableName = new TextBox() { Width = 260, Text = _tableName, ReadOnly = !IsNewTable };
            txtTableName.TextChanged += (s, e) =>
            {
                if (IsNewTable) MarkAsModified();
                UpdateTitle();
            };
            tlpOptions.Controls.Add(txtTableName, 1, 0);

            tlpOptions.Controls.Add(new Label() { Text = "引擎:", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 1);
            cbEngine = new ComboBox() { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
            cbEngine.Items.AddRange(new object[] { "InnoDB", "MyISAM", "MEMORY" });
            cbEngine.SelectedItem = "InnoDB";
            cbEngine.SelectedIndexChanged += (s, e) => MarkAsModified();
            tlpOptions.Controls.Add(cbEngine, 1, 1);
            tpOptions.Controls.Add(tlpOptions);

            // 4. 註解分頁
            tpComment = new TabPage("註解");
            txtTableComment = new TextBox() { Dock = DockStyle.Fill, Multiline = true };
            txtTableComment.TextChanged += (s, e) => MarkAsModified();
            tpComment.Controls.Add(txtTableComment);

            // 5. SQL 預覽分頁
            tpSqlPreview = new TabPage("SQL 預覽");
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

            var result = MessageBox.Show($"你要儲存對 {_tableName} 的變更嗎？", "確認", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
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
                
                using (Form f = new Form() { Text = "選擇索引欄位", Size = new Size(300, 400), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog })
                {
                    CheckedListBox clb = new CheckedListBox() { Dock = DockStyle.Fill };
                    foreach (string c in allCols) clb.Items.Add(c, currentVal.Contains(c));
                    
                    Button btnOk = new Button() { Text = "確定", Dock = DockStyle.Bottom };
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
                    }
                    else if (_db is my_postgresql)
                    {
                        newRow["Name"] = row["column_name"];
                        newRow["Type"] = row["data_type"];
                        newRow["NotNull"] = (row["is_nullable"].ToString() == "NO");
                        newRow["Default"] = row["column_default"];
                    }
                    else if (_db is my_sqlite)
                    {
                        newRow["Name"] = row["name"];
                        newRow["Type"] = row["type"];
                        newRow["NotNull"] = (row["notnull"].ToString() == "1");
                        newRow["PK"] = (row["pk"].ToString() == "1");
                        newRow["Default"] = row["dflt_value"];
                    }
                    else if (_db is my_mssql)
                    {
                        newRow["Name"] = row["COLUMN_NAME"];
                        newRow["Type"] = row["DATA_TYPE"];
                        newRow["NotNull"] = (row["IS_NULLABLE"].ToString() == "NO");
                        newRow["Default"] = row["COLUMN_DEFAULT"];
                    }
                    else
                    {
                        newRow["Name"] = row[0];
                    }

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

        private void BindColumns(DataTable displayDt)
        {
            dgvColumns.DataSource = displayDt;

            dgvColumns.Columns["Name"].HeaderText = "名稱";
            dgvColumns.Columns["Type"].HeaderText = "類型";
            dgvColumns.Columns["Length"].HeaderText = "長度";
            dgvColumns.Columns["Decimals"].HeaderText = "小數位數";
            dgvColumns.Columns["NotNull"].HeaderText = "不是 Null";
            dgvColumns.Columns["PK"].HeaderText = "主鍵";
            dgvColumns.Columns["Default"].HeaderText = "預設";
            dgvColumns.Columns["Comment"].HeaderText = "註解";
            dgvColumns.Columns["_OldName"].Visible = false;
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
                rtbSqlPreview.Text = "-- 既有資料表 ALTER 目前只支援 MySQL；非 MySQL 請使用 New Table 建立新表。";
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
                        // 處理欄位格式 (相容舊版 .NET)
                        string[] rawCols = cols.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        List<string> formattedCols = new List<string>();
                        foreach (string c in rawCols)
                        {
                            formattedCols.Add("`" + c.Trim() + "`");
                        }
                        string colList = string.Join(", ", formattedCols.ToArray());
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

        private string BuildGenericCreateTableSql(DataTable currentDt)
        {
            string tableName = GetTableNameForSave();
            if (string.IsNullOrWhiteSpace(tableName))
            {
                return "-- 請先在「選項」分頁輸入資料表名稱。";
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
                return "-- 請至少新增一個欄位。";
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
                if (type == "FULLTEXT" || type == "SPATIAL") continue;
                if (string.IsNullOrWhiteSpace(indexName)) indexName = tableName + "_idx";

                string columnList = FormatGenericIndexColumns(columns);
                if (string.IsNullOrWhiteSpace(columnList)) continue;

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

            return columnType;
        }

        private string FormatGenericIndexColumns(string columns)
        {
            string[] rawCols = columns.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            List<string> formattedCols = new List<string>();
            foreach (string rawCol in rawCols)
            {
                string col = rawCol.Trim();
                if (!string.IsNullOrWhiteSpace(col))
                {
                    formattedCols.Add(QuoteDesignerIdentifier(col));
                }
            }

            return string.Join(", ", formattedCols.ToArray());
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
            return QuoteDesignerIdentifier(tableName);
        }

        private string QuoteDesignerIdentifier(string name)
        {
            if (_db is my_mssql)
            {
                return "[" + name.Replace("]", "]]") + "]";
            }
            if (_db is my_postgresql || _db is my_sqlite)
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
                return "-- 請先在「選項」分頁輸入資料表名稱。";
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
                return "-- 請至少新增一個欄位。";
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
            string[] rawCols = columns.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            List<string> formattedCols = new List<string>();
            foreach (string rawCol in rawCols)
            {
                string col = rawCol.Trim();
                if (!string.IsNullOrWhiteSpace(col))
                {
                    formattedCols.Add(QuoteMySqlIdentifier(col));
                }
            }

            return string.Join(", ", formattedCols);
        }

        private static string QuoteMySqlIdentifier(string name)
        {
            return "`" + name.Replace("`", "``") + "`";
        }

        private static string EscapeMySqlStringLiteral(string value)
        {
            return "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
        }

        private void BtnSave_Click(object sender, EventArgs e)
        {
            GeneratePreviewSql();
            string sql = rtbSqlPreview.Text;
            
            if (sql.StartsWith("--"))
            {
                MessageBox.Show(sql.TrimStart('-', ' '), "無法儲存", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (MessageBox.Show($"即將執行以下 SQL：\n\n{sql}\n\n確定嗎？", "Confirm Save", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                var res = _db.ExecSQL(sql);
                if (res["status"] == "OK")
                {
                    MessageBox.Show("儲存成功！");
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
                    MessageBox.Show("儲存失敗：" + res["reason"]);
                }
            }
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

                // MySQL 索引是按 Key_name 分多列，我們需要分組
                var indexGroups = new Dictionary<string, List<DataRow>>();
                foreach (DataRow row in rawIdx.Rows)
                {
                    string keyName = row["Key_name"].ToString();
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
                    List<string> cols = new List<string>();
                    foreach (var r in group.Value) cols.Add(r["Column_name"].ToString());
                    newRow["欄位"] = string.Join(", ", cols);

                    if (group.Key == "PRIMARY") newRow["索引類型"] = "PRIMARY";
                    else if (first["Non_unique"].ToString() == "0") newRow["索引類型"] = "UNIQUE";
                    else if (first["Index_type"].ToString() == "FULLTEXT") newRow["索引類型"] = "FULLTEXT";
                    else newRow["索引類型"] = "NORMAL";

                    newRow["索引方法"] = first["Index_type"];
                    newRow["註解"] = first["Index_comment"];
                    
                    displayIdx.Rows.Add(newRow);
                }

                _originalIdxDt = displayIdx.Copy();
                BindIndexes(displayIdx);
            }
            catch { /* 暫時不處理錯誤 */ }
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
                    HeaderText = "索引類型",
                    DataPropertyName = "索引類型",
                    FlatStyle = FlatStyle.Flat
                };
                cb.Items.AddRange(new object[] { "NORMAL", "UNIQUE", "PRIMARY", "FULLTEXT", "SPATIAL" });
                dgvIndexes.Columns.Insert(idx, cb);
            }

            if (dgvIndexes.Columns.Contains("索引方法") && !(dgvIndexes.Columns["索引方法"] is DataGridViewComboBoxColumn))
            {
                int idx = dgvIndexes.Columns["索引方法"].Index;
                dgvIndexes.Columns.RemoveAt(idx);
                var cb = new DataGridViewComboBoxColumn()
                {
                    Name = "索引方法",
                    HeaderText = "索引方法",
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
