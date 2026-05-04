using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    public class TableDesignerForm : Form, IDockableForm
    {
        private IDatabase _db;
        private string _databaseName;
        private string _tableName;
        
        private DataGridView dgvColumns;
        private Button btnSave;
        private Panel pnlBottom;
        private DataTable _originalDt; // 儲存原始狀態
        private Form1 _mainHost;
        private bool _isDocked;
        private Button btnFloat;
        private Button btnDock;

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
            this.Size = new Size(900, 500);
            this.StartPosition = FormStartPosition.CenterParent;

            dgvColumns = new DataGridView()
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.White,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                AllowUserToAddRows = true // 允許新增欄位
            };

            pnlBottom = new Panel() { Dock = DockStyle.Bottom, Height = 50 };
            btnSave = new Button()
            {
                Text = "Save Change",
                Location = new Point(10, 10),
                Size = new Size(120, 30),
                Enabled = true
            };
            btnSave.Click += BtnSave_Click;
            
            btnFloat = new Button() { Text = "Float", Location = new Point(140, 10), Size = new Size(70, 30) };
            btnFloat.Click += (s, e) => _mainHost?.FloatDockableForm(this);
            
            btnDock = new Button() { Text = "Dock", Location = new Point(140, 10), Size = new Size(70, 30), Visible = false };
            btnDock.Click += (s, e) => _mainHost?.DockDockableForm(this);

            pnlBottom.Controls.Add(btnSave);
            pnlBottom.Controls.Add(btnFloat);
            pnlBottom.Controls.Add(btnDock);
            this.Controls.Add(dgvColumns);
            this.Controls.Add(pnlBottom);
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
                displayDt.Columns.Add("NotNull", typeof(bool));
                displayDt.Columns.Add("PK", typeof(bool));
                displayDt.Columns.Add("Default");
                displayDt.Columns.Add("Comment");

                foreach (DataRow row in rawDt.Rows)
                {
                    DataRow newRow = displayDt.NewRow();
                    
                    // 根據目前驅動類型進行不同的 Mapping
                    if (_db is my_mysql)
                    {
                        newRow["Name"] = row["Field"];
                        string typeStr = row["Type"].ToString();
                        if (typeStr.Contains("("))
                        {
                            newRow["Type"] = typeStr.Split('(')[0];
                            newRow["Length"] = typeStr.Split('(')[1].Replace(")", "");
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
                        // PG 的 PK 與 Comment 需要額外查詢，目前先留空
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
                        // 其他資料庫先顯示第一欄作為名稱
                        newRow["Name"] = row[0];
                    }

                    displayDt.Rows.Add(newRow);
                }

                _originalDt = displayDt.Copy(); // 備份原始狀態
                dgvColumns.DataSource = displayDt;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"無法載入欄位資訊: {ex.Message}");
            }
        }

        private void BtnSave_Click(object sender, EventArgs e)
        {
            if (!(_db is my_mysql))
            {
                MessageBox.Show("目前儲存功能僅支援 MySQL，其餘資料庫開發中！");
                return;
            }

            DataTable currentDt = (DataTable)dgvColumns.DataSource;
            List<string> changes = new List<string>();

            foreach (DataRow row in currentDt.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;
                if (string.IsNullOrEmpty(row["Name"].ToString())) continue;

                string colName = row["Name"].ToString();
                string colType = row["Type"].ToString();
                string colLen = row["Length"].ToString();
                bool notNull = (bool)row["NotNull"];
                string colDefault = row["Default"].ToString();
                string colComment = row["Comment"].ToString();

                string typeFull = colType + (string.IsNullOrEmpty(colLen) ? "" : $"({colLen})");
                string nullStr = notNull ? "NOT NULL" : "NULL";
                string defStr = string.IsNullOrEmpty(colDefault) ? "" : $"DEFAULT '{colDefault}'";
                string commentStr = string.IsNullOrEmpty(colComment) ? "" : $"COMMENT '{colComment}'";

                // 找找看原始資料裡有沒有這個欄位
                DataRow[] origRows = _originalDt.Select($"Name = '{colName}'");
                if (origRows.Length == 0)
                {
                    // 新增欄位
                    changes.Add($"ADD COLUMN `{colName}` {typeFull} {nullStr} {defStr} {commentStr}");
                }
                else
                {
                    // 比對是否有變動 (簡單比對)
                    DataRow orig = origRows[0];
                    if (orig["Type"].ToString() != colType || orig["Length"].ToString() != colLen || (bool)orig["NotNull"] != notNull || orig["Default"].ToString() != colDefault)
                    {
                        changes.Add($"MODIFY COLUMN `{colName}` {typeFull} {nullStr} {defStr} {commentStr}");
                    }
                }
            }

            if (changes.Count > 0)
            {
                string sql = $"ALTER TABLE `{_databaseName}`.`{_tableName}` \n  " + string.Join(",\n  ", changes) + ";";
                
                if (MessageBox.Show($"即將執行以下 SQL：\n\n{sql}\n\n確定嗎？", "SQL Preview", MessageBoxButtons.YesNo) == DialogResult.Yes)
                {
                    var res = _db.ExecSQL(sql);
                    if (res["status"] == "OK") MessageBox.Show("儲存成功！");
                    else MessageBox.Show("儲存失敗：" + res["reason"]);
                }
            }
            else
            {
                MessageBox.Show("未偵測到任何變動。");
            }
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
