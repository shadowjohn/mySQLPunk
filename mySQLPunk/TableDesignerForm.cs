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
        private DataTable _originalDt; // 儲存原始狀態
        private Form1 _mainHost;
        private bool _isDocked;
        private ToolStripButton btnFloat;
        private ToolStripButton btnDock;

        private TabControl tcMain;
        private TabPage tpColumns, tpIndexes, tpOptions, tpComment, tpSqlPreview;
        private RichTextBox rtbSqlPreview;
        private TextBox txtTableComment;
        private ComboBox cbEngine;
        private Panel pnlColumnProperties; // 屬性面板
        private myinclude my = new myinclude();        
        public TableDesignerForm(IDatabase db, string databaseName, string tableName)
        {
            _db = db;
            _databaseName = databaseName;
            _tableName = tableName;
            InitializeComponent();
            this.Text = $"Design Table - {tableName}";
            LoadColumns();
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

            // 主 TabControl
            tcMain = new TabControl() { Dock = DockStyle.Fill, Padding = new Point(12, 5) };
            
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
            DataGridView dgvIndexes = new DataGridView() { Dock = DockStyle.Fill, BackgroundColor = Color.White };
            tpIndexes.Controls.Add(dgvIndexes);

            // 3. 選項分頁
            tpOptions = new TabPage("選項");
            TableLayoutPanel tlpOptions = new TableLayoutPanel() { Dock = DockStyle.Top, Height = 200, RowCount = 4, ColumnCount = 2, Padding = new Padding(20) };
            tlpOptions.Controls.Add(new Label() { Text = "引擎:", Anchor = AnchorStyles.Left }, 0, 0);
            cbEngine = new ComboBox() { Width = 200 };
            tlpOptions.Controls.Add(cbEngine, 1, 0);
            tpOptions.Controls.Add(tlpOptions);

            // 4. 註解分頁
            tpComment = new TabPage("註解");
            txtTableComment = new TextBox() { Dock = DockStyle.Fill, Multiline = true };
            tpComment.Controls.Add(txtTableComment);

            // 5. SQL 預覽分頁
            tpSqlPreview = new TabPage("SQL 預覽");
            rtbSqlPreview = new RichTextBox() { Dock = DockStyle.Fill, ReadOnly = true, Font = new Font("Consolas", 11), BackColor = Color.White };
            tpSqlPreview.Controls.Add(rtbSqlPreview);

            tcMain.TabPages.AddRange(new TabPage[] { tpColumns, tpIndexes, tpOptions, tpComment, tpSqlPreview });
            tcMain.SelectedIndexChanged += (s, e) => { if (tcMain.SelectedTab == tpSqlPreview) GeneratePreviewSql(); };

            this.Controls.Add(tcMain);
            this.Controls.Add(tsTop);
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
                Control area = _mainHost.GetTabDropArea();
                bool isOver = area.RectangleToScreen(area.ClientRectangle).Contains(Cursor.Position);
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
                Control area = _mainHost.GetTabDropArea();
                if (this.Bounds.IntersectsWith(area.RectangleToScreen(area.ClientRectangle)))
                    _mainHost.DockDockableForm(this);
            }
        }

        private void LoadColumns()
        {
            try
            {
                DataTable rawDt = _db.GetColumns(_databaseName, _tableName);
                
                // 建立統一格式的表格
                DataTable displayDt = new DataTable();
                displayDt.Columns.Add("Name");
                displayDt.Columns.Add("Type");
                displayDt.Columns.Add("Length");
                displayDt.Columns.Add("Decimals");
                displayDt.Columns.Add("NotNull", typeof(bool));
                displayDt.Columns.Add("PK", typeof(bool));
                displayDt.Columns.Add("Default");
                displayDt.Columns.Add("Comment");
                displayDt.Columns.Add("_OldName"); // 隱藏欄位，用來追蹤重新命名

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
                dgvColumns.DataSource = displayDt;

                // 只有顯示時用中文標題
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
            catch (Exception ex)
            {
                MessageBox.Show($"無法載入欄位資訊: {ex.Message}");
            }
        }

        private void GeneratePreviewSql()
        {
            if (!(_db is my_mysql))
            {
                rtbSqlPreview.Text = "-- SQL Preview is currently only supported for MySQL.";
                return;
            }

            DataTable currentDt = (DataTable)dgvColumns.DataSource;
            if (currentDt == null) return;

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

        private void BtnSave_Click(object sender, EventArgs e)
        {
            GeneratePreviewSql();
            string sql = rtbSqlPreview.Text;
            
            if (sql.StartsWith("--"))
            {
                MessageBox.Show("未偵測到任何變動。");
                return;
            }

            if (MessageBox.Show($"即將執行以下 SQL：\n\n{sql}\n\n確定嗎？", "Confirm Save", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                var res = _db.ExecSQL(sql);
                if (res["status"] == "OK")
                {
                    MessageBox.Show("儲存成功！");
                    LoadColumns(); // 重新載入
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
