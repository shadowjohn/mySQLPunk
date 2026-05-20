using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using mySQLPunk.lib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using utility;
namespace mySQLPunk
{
	    public class TableDesignerForm : Form, IDockableForm
	    {
	        private const string AutoColumnCommentUrl = "https://3wa.tw/fast/all_my_columns.php?raw=1";
	        private const int AutoColumnCommentTimeoutMs = 8000;
	        private const int AutoColumnCommentRetryCount = 2;
	        private const string AutoColumnCommentCacheFileName = "auto_column_comments_cache.json";
	        private const string AutoColumnCommentDictionaryDirectoryName = "auto_comment_dictionaries";
	        private static readonly object AutoColumnCommentSync = new object();
	        private static Task<Dictionary<string, string>> AutoColumnCommentTask;
	        private static string AutoColumnCommentLastError;
	        private static DateTime? AutoColumnCommentLastErrorUtc;
	        private static string AutoColumnCommentLastSource;
	        private static string AutoColumnCommentLastSourceName;

        private IDatabase _db;
        private string _databaseName;
        private string _tableName;
        private readonly SynchronizationContext _uiContext;
        
        private DataGridView dgvColumns;
        private DataGridView dgvIndexes;
        private DataTable _originalDt; // 儲存欄位原始狀態
        private DataTable _originalIdxDt; // 儲存索引原始狀態
        private Form1 _mainHost;
        private bool _isDocked;
        private ToolStripButton btnFloat;
        private ToolStripButton btnDock;
        private ToolStripButton btnExecuteSql;
        private ToolStripSplitButton btnFillAutoComments;
        private RunnerProgressOverlay autoCommentProgressOverlay;
        private bool _isFillingAutoComments;

        private TabControl tcMain;
        private TabPage tpColumns, tpIndexes, tpOptions, tpComment, tpSqlPreview;
        private RichTextBox rtbSqlPreview;
        private TextBox txtTableName;
        private TextBox txtTableComment;
        private ComboBox cbEngine;
        private Panel pnlColumnProperties; // 屬性面板
        private bool _isModified = false;
        private bool IsNewTable => string.IsNullOrWhiteSpace(_tableName);

        public class AutoColumnCommentDictionaryDiffEntry
        {
            public string Key { get; set; }
            public string Status { get; set; }
            public string ExistingValue { get; set; }
            public string ImportedValue { get; set; }
        }

        public class AutoColumnCommentDictionaryDiffReport
        {
            public List<AutoColumnCommentDictionaryDiffEntry> Entries { get; } = new List<AutoColumnCommentDictionaryDiffEntry>();
            public int Added { get; set; }
            public int Updated { get; set; }
            public int Removed { get; set; }
            public int Unchanged { get; set; }
            public int ImportedCount { get; set; }
            public AutoColumnCommentDictionarySignatureInfo SignatureInfo { get; set; }
        }

        public class AutoColumnCommentDictionaryImportPreview
        {
            public Dictionary<string, string> Comments { get; set; }
            public AutoColumnCommentDictionarySignatureInfo SignatureInfo { get; set; }
        }

        public class AutoColumnCommentDictionarySignatureInfo
        {
            public string Signature { get; set; }
            public bool SignaturePresent { get; set; }
            public bool SignatureValid { get; set; }
            public string ExportedAtUtc { get; set; }
            public string Source { get; set; }
        }

        public class AutoColumnCommentDictionaryVersionInfo
        {
            public string DictionaryName { get; set; }
            public string VersionId { get; set; }
            public string FilePath { get; set; }
            public DateTime CreatedAtUtc { get; set; }
            public int EntryCount { get; set; }
        }

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
            _uiContext = SynchronizationContext.Current;
            _db = db;
            _databaseName = databaseName;
            _tableName = tableName;
            InitializeComponent();
            ApplyLanguage();
            ApplyTheme();
            UpdateTitle();
            LoadColumns();
            LoadIndexes();

            dgvColumns.CellValueChanged += DgvColumns_CellValueChanged;
            dgvIndexes.CellValueChanged += (s, e) => MarkAsModified();
            // 監聽刪除行等操作
            dgvColumns.RowsRemoved += (s, e) => MarkAsModified();
            dgvIndexes.RowsRemoved += (s, e) => MarkAsModified();
            BeginLoadAutoColumnComments();
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
            btnExecuteSql = new ToolStripButton(Localization.T("Designer.ExecuteSql"), GetIcon(iconPath + "new_query.png"), BtnExecuteSql_Click);
            btnExecuteSql.ToolTipText = Localization.T("Designer.ExecuteSqlTooltip");
            
            ToolStripSeparator sep1 = new ToolStripSeparator();
            ToolStripButton btnAddCol = new ToolStripButton(Localization.T("Designer.AddColumn"), GetIcon(iconPath + "add.png"));
            ToolStripButton btnInsertCol = new ToolStripButton(Localization.T("Designer.InsertColumn"), GetIcon(iconPath + "insert.png"));
            ToolStripButton btnDelCol = new ToolStripButton(Localization.T("Designer.DeleteColumn"), GetIcon(iconPath + "delete.png"));
            
            ToolStripSeparator sep2 = new ToolStripSeparator();
            btnFillAutoComments = new ToolStripSplitButton(Localization.T("Designer.FillAutoComments"), GetIcon(iconPath + "table.png"));
            ToolStripMenuItem fillBlankCommentsItem = new ToolStripMenuItem(Localization.T("Designer.FillBlankAutoComments"));
            fillBlankCommentsItem.Click += async (s, e) => await FillAutoColumnCommentsAsync(AutoCommentMode.FillBlanks, true);
            ToolStripMenuItem overwriteCommentsItem = new ToolStripMenuItem(Localization.T("Designer.OverwriteAutoComments"));
            overwriteCommentsItem.Click += async (s, e) => await FillAutoColumnCommentsAsync(AutoCommentMode.Overwrite, true);
            ToolStripMenuItem importCommentsDictionaryItem = new ToolStripMenuItem(Localization.T("Designer.ImportAutoCommentsDictionary"));
            importCommentsDictionaryItem.Click += (s, e) => ImportAutoColumnCommentsDictionaryWithDialog();
            ToolStripMenuItem exportCommentsDictionaryItem = new ToolStripMenuItem(Localization.T("Designer.ExportAutoCommentsDictionary"));
            exportCommentsDictionaryItem.Click += async (s, e) => await ExportAutoColumnCommentsDictionaryWithDialogAsync();
            ToolStripMenuItem saveCommentsDictionaryItem = new ToolStripMenuItem(Localization.T("Designer.SaveAutoCommentsDictionaryAs"));
            saveCommentsDictionaryItem.Click += async (s, e) => await SaveAutoColumnCommentsDictionaryWithDialogAsync();
            ToolStripMenuItem switchCommentsDictionaryItem = new ToolStripMenuItem(Localization.T("Designer.SwitchAutoCommentsDictionary"));
            switchCommentsDictionaryItem.Click += (s, e) => SwitchAutoColumnCommentsDictionaryWithDialog();
            ToolStripMenuItem renameCommentsDictionaryItem = new ToolStripMenuItem(Localization.T("Designer.RenameAutoCommentsDictionary"));
            renameCommentsDictionaryItem.Click += (s, e) => RenameAutoColumnCommentsDictionaryWithDialog();
            ToolStripMenuItem deleteCommentsDictionaryItem = new ToolStripMenuItem(Localization.T("Designer.DeleteAutoCommentsDictionary"));
            deleteCommentsDictionaryItem.Click += (s, e) => DeleteAutoColumnCommentsDictionaryWithDialog();
            ToolStripMenuItem compareCommentsDictionaryVersionItem = new ToolStripMenuItem(Localization.T("Designer.CompareAutoCommentsDictionaryVersion"));
            compareCommentsDictionaryVersionItem.Click += (s, e) => CompareAutoColumnCommentsDictionaryVersionWithDialog();
            ToolStripMenuItem restoreCommentsDictionaryVersionItem = new ToolStripMenuItem(Localization.T("Designer.RestoreAutoCommentsDictionaryVersion"));
            restoreCommentsDictionaryVersionItem.Click += (s, e) => RestoreAutoColumnCommentsDictionaryVersionWithDialog();
            btnFillAutoComments.DropDownItems.Add(fillBlankCommentsItem);
            btnFillAutoComments.DropDownItems.Add(overwriteCommentsItem);
            btnFillAutoComments.DropDownItems.Add(new ToolStripSeparator());
            btnFillAutoComments.DropDownItems.Add(importCommentsDictionaryItem);
            btnFillAutoComments.DropDownItems.Add(exportCommentsDictionaryItem);
            btnFillAutoComments.DropDownItems.Add(new ToolStripSeparator());
            btnFillAutoComments.DropDownItems.Add(saveCommentsDictionaryItem);
            btnFillAutoComments.DropDownItems.Add(switchCommentsDictionaryItem);
            btnFillAutoComments.DropDownItems.Add(renameCommentsDictionaryItem);
            btnFillAutoComments.DropDownItems.Add(deleteCommentsDictionaryItem);
            btnFillAutoComments.DropDownItems.Add(compareCommentsDictionaryVersionItem);
            btnFillAutoComments.DropDownItems.Add(restoreCommentsDictionaryVersionItem);
            btnFillAutoComments.ButtonClick += async (s, e) => await FillAutoColumnCommentsAsync(AutoCommentMode.FillBlanks, true);
            ToolStripSeparator sepAutoComment = new ToolStripSeparator();
            ToolStripButton btnMoveUp = new ToolStripButton(Localization.T("Designer.MoveUp"), GetIcon(iconPath + "up.png"));
            ToolStripButton btnMoveDown = new ToolStripButton(Localization.T("Designer.MoveDown"), GetIcon(iconPath + "down.png"));

            btnFloat = new ToolStripButton(Localization.T("Query.Float"), null, (s, e) => _mainHost?.FloatDockableForm(this)) { Alignment = ToolStripItemAlignment.Right };
            btnDock = new ToolStripButton(Localization.T("Query.Dock"), null, (s, e) => _mainHost?.DockDockableForm(this)) { Visible = false, Alignment = ToolStripItemAlignment.Right };
            
            tsTop.Items.AddRange(new ToolStripItem[] { 
                btnSave, btnExecuteSql, sep1, btnAddCol, btnInsertCol, btnDelCol, sep2, btnFillAutoComments, sepAutoComment, btnMoveUp, btnMoveDown, btnFloat, btnDock
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
            rtbSqlPreview = new RichTextBox() { Dock = DockStyle.Fill, ReadOnly = false, Font = new Font("Consolas", 11), BackColor = Color.White };
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

        private void DgvColumns_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex >= 0 && e.ColumnIndex >= 0 && dgvColumns.Columns[e.ColumnIndex].Name == "Name")
            {
                ApplyAutoColumnCommentForGridRow(e.RowIndex);
            }

            MarkAsModified();
        }

        private async void BtnFillAutoComments_Click(object sender, EventArgs e)
        {
            await FillAutoColumnCommentsAsync(AutoCommentMode.FillBlanks, true);
        }

        public void FillMissingAutoColumnComments()
        {
            FillAutoColumnComments(AutoCommentMode.FillBlanks);
        }

        public void FillAutoColumnComments(AutoCommentMode mode)
        {
            if (IsDisposed) return;
            Action apply = async () => await FillAutoColumnCommentsAsync(mode, true);
            if (IsHandleCreated) BeginInvoke(apply);
            else apply();
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
            displayDt.Columns.Add("_AutoComment");
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
                newRow["Comment"] = GetMetadataString(row, "Comment", "comment");
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

        private void BeginLoadAutoColumnComments()
        {
            if (!IsNewTable) return;

            Task<Dictionary<string, string>> task = GetAutoColumnCommentTask();
            task.ContinueWith(t =>
            {
                if (t.IsCanceled || t.IsFaulted)
                {
                    Exception ex = t.Exception?.GetBaseException();
                    if (ex != null) Trace.WriteLine($"[AutoColumnComment] preload failed: {ex.Message}");
                    return;
                }

                Action apply = () =>
                {
                    if (!IsDisposed) ApplyAutoColumnCommentsToEmptyRows();
                };

                if (_uiContext != null)
                {
                    _uiContext.Post(_ => apply(), null);
                }
                else if (IsHandleCreated)
                {
                    BeginInvoke(apply);
                }
            });
        }

        public static Task<Dictionary<string, string>> GetAutoColumnCommentTask()
        {
            lock (AutoColumnCommentSync)
            {
                if (AutoColumnCommentTask == null || AutoColumnCommentTask.IsCanceled || AutoColumnCommentTask.IsFaulted)
                {
                    AutoColumnCommentTask = Task.Factory.StartNew(
                        LoadAutoColumnComments,
                        CancellationToken.None,
                        TaskCreationOptions.None,
                        TaskScheduler.Default);
                }

                return AutoColumnCommentTask;
            }
        }

        public static string GetAutoColumnCommentLastError()
        {
            lock (AutoColumnCommentSync)
            {
                return AutoColumnCommentLastError;
            }
        }

        public static string GetAutoColumnCommentLastSource()
        {
            lock (AutoColumnCommentSync)
            {
                return AutoColumnCommentLastSource;
            }
        }

        public static string GetAutoColumnCommentLastSourceName()
        {
            lock (AutoColumnCommentSync)
            {
                return AutoColumnCommentLastSourceName;
            }
        }

        public static string GetAutoColumnCommentSourceMessage()
        {
            string source = GetAutoColumnCommentLastSource();
            if (string.Equals(source, "cache", StringComparison.OrdinalIgnoreCase))
            {
                return Localization.T("Designer.AutoCommentsDictionaryCache");
            }
            if (string.Equals(source, "remote", StringComparison.OrdinalIgnoreCase))
            {
                return Localization.T("Designer.AutoCommentsDictionaryRemote");
            }
            if (string.Equals(source, "import", StringComparison.OrdinalIgnoreCase))
            {
                return Localization.T("Designer.AutoCommentsDictionaryImport");
            }
            if (string.Equals(source, "named", StringComparison.OrdinalIgnoreCase))
            {
                string sourceName = GetAutoColumnCommentLastSourceName();
                if (!string.IsNullOrWhiteSpace(sourceName))
                {
                    return Localization.Format("Designer.AutoCommentsDictionaryNamed", sourceName);
                }
                return Localization.T("Designer.AutoCommentsDictionaryImport");
            }
            return Localization.T("Designer.AutoCommentsLoading");
        }

        private sealed class AutoColumnCommentWebClient : WebClient
        {
            private readonly int _timeoutMs;

            public AutoColumnCommentWebClient(int timeoutMs)
            {
                _timeoutMs = timeoutMs;
            }

            protected override WebRequest GetWebRequest(Uri address)
            {
                WebRequest request = base.GetWebRequest(address);
                if (request == null) return null;
                request.Timeout = _timeoutMs;
                ConnectionProxySettingsService.ApplyTo(request);
                if (request is HttpWebRequest httpRequest)
                {
                    httpRequest.ReadWriteTimeout = _timeoutMs;
                }
                return request;
            }
        }

        private static Dictionary<string, string> LoadAutoColumnComments()
        {
            return LoadAutoColumnCommentsCore(
                DownloadAutoColumnCommentJson,
                SaveAutoColumnCommentCache,
                LoadAutoColumnCommentCache);
        }

        private static Dictionary<string, string> LoadAutoColumnCommentsCore(
            Func<string> downloadJson,
            Action<Dictionary<string, string>> saveCache,
            Func<Dictionary<string, string>> loadCache)
        {
            Exception lastException = null;
            for (int attempt = 0; attempt <= AutoColumnCommentRetryCount; attempt++)
            {
                try
                {
                    Dictionary<string, string> comments = ParseAutoColumnCommentJson(downloadJson());
                    if (comments.Count == 0) throw new InvalidOperationException("字典沒有可用資料");
                    saveCache?.Invoke(comments);

                    lock (AutoColumnCommentSync)
                    {
                        AutoColumnCommentLastError = null;
                        AutoColumnCommentLastErrorUtc = null;
                        AutoColumnCommentLastSource = "remote";
                        AutoColumnCommentLastSourceName = null;
                    }

                    return comments;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    lock (AutoColumnCommentSync)
                    {
                        AutoColumnCommentLastError = ex.Message;
                        AutoColumnCommentLastErrorUtc = DateTime.UtcNow;
                        AutoColumnCommentLastSource = null;
                        AutoColumnCommentLastSourceName = null;
                    }

                    if (attempt < AutoColumnCommentRetryCount)
                    {
                        Thread.Sleep(200 * (attempt + 1));
                    }
                }
            }

            try
            {
                Dictionary<string, string> cachedComments = loadCache?.Invoke();
                if (cachedComments != null && cachedComments.Count > 0)
                {
                    lock (AutoColumnCommentSync)
                    {
                        AutoColumnCommentLastError = "遠端自動註解字典載入失敗，已改用本機快取：" + (lastException?.Message ?? "未知錯誤");
                        AutoColumnCommentLastErrorUtc = DateTime.UtcNow;
                        AutoColumnCommentLastSource = "cache";
                        AutoColumnCommentLastSourceName = null;
                    }

                    return cachedComments;
                }
            }
            catch (Exception cacheEx)
            {
                Trace.WriteLine("[AutoColumnComment] cache load failed: " + cacheEx.Message);
            }

            throw new Exception("自動註解字典載入失敗：" + (lastException?.Message ?? "未知錯誤"), lastException);
        }

        private static string DownloadAutoColumnCommentJson()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (WebClient client = new AutoColumnCommentWebClient(AutoColumnCommentTimeoutMs))
            {
                client.Encoding = Encoding.UTF8;
                return client.DownloadString(AutoColumnCommentUrl);
            }
        }

        private static Dictionary<string, string> ParseAutoColumnCommentJson(string json)
        {
            Dictionary<string, string> parsed = ParseAutoColumnCommentDictionaryPayload(json);
            if (parsed == null) throw new InvalidOperationException("字典回傳格式不正確（解析結果為 null）");

            Dictionary<string, string> comments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in parsed)
            {
                if (string.IsNullOrWhiteSpace(item.Key) || string.IsNullOrWhiteSpace(item.Value)) continue;
                comments[item.Key.Trim()] = item.Value.Trim();
            }

            return comments;
        }

        private static string GetAutoColumnCommentCachePath()
        {
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(appDataPath)) appDataPath = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(appDataPath, "mySQLPunk", AutoColumnCommentCacheFileName);
        }

        public static string GetAutoColumnCommentDictionaryStorePath()
        {
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(appDataPath)) appDataPath = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(appDataPath, "mySQLPunk", AutoColumnCommentDictionaryDirectoryName);
        }

        public static string NormalizeAutoColumnCommentDictionaryName(string dictionaryName)
        {
            string value = (dictionaryName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(Localization.T("Designer.AutoCommentsDictionaryNameRequired"));

            char[] invalidChars = Path.GetInvalidFileNameChars();
            StringBuilder normalized = new StringBuilder();
            bool previousWasSpace = false;
            foreach (char c in value)
            {
                bool invalid = char.IsControl(c);
                if (!invalid)
                {
                    for (int i = 0; i < invalidChars.Length; i++)
                    {
                        if (c == invalidChars[i])
                        {
                            invalid = true;
                            break;
                        }
                    }
                }

                if (invalid) continue;
                if (char.IsWhiteSpace(c))
                {
                    if (!previousWasSpace)
                    {
                        normalized.Append(' ');
                        previousWasSpace = true;
                    }
                    continue;
                }

                normalized.Append(c);
                previousWasSpace = false;
            }

            value = normalized.ToString().Trim().TrimEnd('.');
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(Localization.T("Designer.AutoCommentsDictionaryNameRequired"));
            return value;
        }

        private static string GetNamedAutoColumnCommentDictionaryPath(string dictionaryName)
        {
            string name = NormalizeAutoColumnCommentDictionaryName(dictionaryName);
            return Path.Combine(GetAutoColumnCommentDictionaryStorePath(), name + ".json");
        }

        private static string GetNamedAutoColumnCommentDictionaryVersionDirectory(string dictionaryName)
        {
            string name = NormalizeAutoColumnCommentDictionaryName(dictionaryName);
            return Path.Combine(GetAutoColumnCommentDictionaryStorePath(), ".versions", name);
        }

        private static string GetNamedAutoColumnCommentDictionaryVersionPath(string dictionaryName, string versionId)
        {
            string normalizedVersionId = NormalizeAutoColumnCommentDictionaryVersionId(versionId);
            return Path.Combine(GetNamedAutoColumnCommentDictionaryVersionDirectory(dictionaryName), normalizedVersionId + ".json");
        }

        private static string NormalizeAutoColumnCommentDictionaryVersionId(string versionId)
        {
            string value = (versionId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(Localization.T("Designer.AutoCommentsDictionaryVersionRequired"));

            foreach (char c in value)
            {
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-'))
                {
                    throw new ArgumentException(Localization.T("Designer.AutoCommentsDictionaryVersionRequired"));
                }
            }

            return value;
        }

        public static List<string> ListNamedAutoColumnCommentDictionaries()
        {
            List<string> names = new List<string>();
            string storePath = GetAutoColumnCommentDictionaryStorePath();
            if (!Directory.Exists(storePath)) return names;

            foreach (string filePath in Directory.GetFiles(storePath, "*.json"))
            {
                string name = Path.GetFileNameWithoutExtension(filePath);
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        public static string SaveNamedAutoColumnCommentDictionaryFile(string dictionaryName, Dictionary<string, string> comments)
        {
            string name = NormalizeAutoColumnCommentDictionaryName(dictionaryName);
            string path = GetNamedAutoColumnCommentDictionaryPath(dictionaryName);
            string dir = Path.GetDirectoryName(path);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            CreateNamedAutoColumnCommentDictionaryVersionFile(name, path);
            ExportAutoColumnCommentDictionaryFile(path, comments);
            return path;
        }

        private static string CreateNamedAutoColumnCommentDictionaryVersionFile(string dictionaryName, string currentPath)
        {
            string name = NormalizeAutoColumnCommentDictionaryName(dictionaryName);
            if (string.IsNullOrWhiteSpace(currentPath) || !File.Exists(currentPath)) return null;

            string versionDirectory = GetNamedAutoColumnCommentDictionaryVersionDirectory(name);
            if (!Directory.Exists(versionDirectory)) Directory.CreateDirectory(versionDirectory);

            string versionId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
            string versionPath = Path.Combine(versionDirectory, versionId + ".json");
            int suffix = 2;
            while (File.Exists(versionPath))
            {
                versionPath = Path.Combine(versionDirectory, versionId + "_" + suffix + ".json");
                suffix++;
            }

            File.Copy(currentPath, versionPath);
            return versionPath;
        }

        public static List<AutoColumnCommentDictionaryVersionInfo> ListNamedAutoColumnCommentDictionaryVersions(string dictionaryName)
        {
            string name = NormalizeAutoColumnCommentDictionaryName(dictionaryName);
            string versionDirectory = GetNamedAutoColumnCommentDictionaryVersionDirectory(name);
            List<AutoColumnCommentDictionaryVersionInfo> versions = new List<AutoColumnCommentDictionaryVersionInfo>();
            if (!Directory.Exists(versionDirectory)) return versions;

            foreach (string filePath in Directory.GetFiles(versionDirectory, "*.json"))
            {
                string versionId = Path.GetFileNameWithoutExtension(filePath);
                DateTime createdAtUtc = File.GetLastWriteTimeUtc(filePath);
                DateTime parsedCreatedAtUtc;
                if (DateTime.TryParseExact(
                    versionId.Length >= 19 ? versionId.Substring(0, 19) : versionId,
                    "yyyyMMdd_HHmmss_fff",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out parsedCreatedAtUtc))
                {
                    createdAtUtc = parsedCreatedAtUtc;
                }

                int entryCount = 0;
                try
                {
                    Dictionary<string, string> comments = PreviewAutoColumnCommentDictionaryFile(filePath);
                    entryCount = comments.Count;
                }
                catch
                {
                }

                versions.Add(new AutoColumnCommentDictionaryVersionInfo
                {
                    DictionaryName = name,
                    VersionId = versionId,
                    FilePath = filePath,
                    CreatedAtUtc = createdAtUtc,
                    EntryCount = entryCount
                });
            }

            versions.Sort((left, right) =>
            {
                int dateCompare = right.CreatedAtUtc.CompareTo(left.CreatedAtUtc);
                if (dateCompare != 0) return dateCompare;
                return string.Compare(right.VersionId, left.VersionId, StringComparison.OrdinalIgnoreCase);
            });
            return versions;
        }

        public static AutoColumnCommentDictionaryDiffReport BuildNamedAutoColumnCommentDictionaryVersionDiffReport(string dictionaryName, string versionId)
        {
            Dictionary<string, string> current = LoadNamedAutoColumnCommentDictionaryContent(dictionaryName);
            Dictionary<string, string> version = LoadNamedAutoColumnCommentDictionaryVersionContent(dictionaryName, versionId);
            return BuildAutoColumnCommentDictionaryDiffReport(current, version);
        }

        public static Dictionary<string, string> RestoreNamedAutoColumnCommentDictionaryVersion(string dictionaryName, string versionId)
        {
            string name = NormalizeAutoColumnCommentDictionaryName(dictionaryName);
            string currentPath = GetNamedAutoColumnCommentDictionaryPath(name);
            string versionPath = GetNamedAutoColumnCommentDictionaryVersionPath(name, versionId);
            if (!File.Exists(versionPath)) throw new FileNotFoundException(Localization.T("Designer.AutoCommentsDictionaryVersionNotFound"), versionPath);

            Dictionary<string, string> comments = PreviewAutoColumnCommentDictionaryFile(versionPath);
            if (File.Exists(currentPath)) CreateNamedAutoColumnCommentDictionaryVersionFile(name, currentPath);

            string dir = Path.GetDirectoryName(currentPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            ExportAutoColumnCommentDictionaryFile(currentPath, comments);
            SaveAutoColumnCommentCache(comments);
            SetAutoColumnCommentDictionary(comments, "named", name);
            return comments;
        }

        private static Dictionary<string, string> LoadNamedAutoColumnCommentDictionaryContent(string dictionaryName)
        {
            string name = NormalizeAutoColumnCommentDictionaryName(dictionaryName);
            string path = GetNamedAutoColumnCommentDictionaryPath(name);
            if (!File.Exists(path)) throw new FileNotFoundException(Localization.T("Designer.AutoCommentsDictionaryNotFound"), path);
            return PreviewAutoColumnCommentDictionaryFile(path);
        }

        private static Dictionary<string, string> LoadNamedAutoColumnCommentDictionaryVersionContent(string dictionaryName, string versionId)
        {
            string path = GetNamedAutoColumnCommentDictionaryVersionPath(dictionaryName, versionId);
            if (!File.Exists(path)) throw new FileNotFoundException(Localization.T("Designer.AutoCommentsDictionaryVersionNotFound"), path);
            return PreviewAutoColumnCommentDictionaryFile(path);
        }

        public static Dictionary<string, string> LoadNamedAutoColumnCommentDictionaryFile(string dictionaryName)
        {
            string name = NormalizeAutoColumnCommentDictionaryName(dictionaryName);
            string path = GetNamedAutoColumnCommentDictionaryPath(name);
            if (!File.Exists(path)) throw new FileNotFoundException(Localization.T("Designer.AutoCommentsDictionaryNotFound"), path);

            Dictionary<string, string> comments = PreviewAutoColumnCommentDictionaryFile(path);
            SaveAutoColumnCommentCache(comments);
            SetAutoColumnCommentDictionary(comments, "named", name);
            return comments;
        }

        public static string RenameNamedAutoColumnCommentDictionaryFile(string oldDictionaryName, string newDictionaryName)
        {
            string oldName = NormalizeAutoColumnCommentDictionaryName(oldDictionaryName);
            string newName = NormalizeAutoColumnCommentDictionaryName(newDictionaryName);
            string oldPath = GetNamedAutoColumnCommentDictionaryPath(oldName);
            string newPath = GetNamedAutoColumnCommentDictionaryPath(newName);

            if (!File.Exists(oldPath)) throw new FileNotFoundException(Localization.T("Designer.AutoCommentsDictionaryNotFound"), oldPath);
            if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) return newPath;
            if (File.Exists(newPath)) throw new IOException(Localization.Format("Designer.AutoCommentsDictionaryAlreadyExists", newName));

            File.Move(oldPath, newPath);
            lock (AutoColumnCommentSync)
            {
                if (string.Equals(AutoColumnCommentLastSource, "named", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(AutoColumnCommentLastSourceName, oldName, StringComparison.OrdinalIgnoreCase))
                {
                    AutoColumnCommentLastSourceName = newName;
                }
            }

            return newPath;
        }

        public static void DeleteNamedAutoColumnCommentDictionaryFile(string dictionaryName)
        {
            string name = NormalizeAutoColumnCommentDictionaryName(dictionaryName);
            string path = GetNamedAutoColumnCommentDictionaryPath(name);
            if (!File.Exists(path)) throw new FileNotFoundException(Localization.T("Designer.AutoCommentsDictionaryNotFound"), path);

            File.Delete(path);
            lock (AutoColumnCommentSync)
            {
                if (string.Equals(AutoColumnCommentLastSource, "named", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(AutoColumnCommentLastSourceName, name, StringComparison.OrdinalIgnoreCase))
                {
                    AutoColumnCommentLastSourceName = null;
                }
            }
        }

        private static Dictionary<string, string> ParseAutoColumnCommentDictionaryPayload(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            JToken token = JToken.Parse(json);
            JObject obj = token as JObject;
            if (obj == null) throw new InvalidOperationException(Localization.T("Designer.AutoCommentsInvalidDictionaryFormat"));

            JToken dictionaryToken = GetAutoColumnCommentDictionaryToken(obj);
            return dictionaryToken.ToObject<Dictionary<string, string>>();
        }

        private static JToken GetAutoColumnCommentDictionaryToken(JObject obj)
        {
            foreach (string propertyName in new[] { "columns", "comments", "entries" })
            {
                JToken candidate;
                if (TryGetJObjectProperty(obj, propertyName, out candidate) && candidate is JObject)
                {
                    return candidate;
                }
            }

            return obj;
        }

        private static bool TryGetJObjectProperty(JObject obj, string propertyName, out JToken value)
        {
            foreach (JProperty property in obj.Properties())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            value = null;
            return false;
        }

        private static void SaveAutoColumnCommentCache(Dictionary<string, string> comments)
        {
            if (comments == null || comments.Count == 0) return;

            try
            {
                string cachePath = GetAutoColumnCommentCachePath();
                string cacheDir = Path.GetDirectoryName(cachePath);
                if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);

                string tempPath = cachePath + ".tmp";
                File.WriteAllText(tempPath, JsonConvert.SerializeObject(comments, Formatting.Indented), new UTF8Encoding(false));
                if (File.Exists(cachePath)) File.Delete(cachePath);
                File.Move(tempPath, cachePath);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[AutoColumnComment] cache save failed: " + ex.Message);
            }
        }

        private static Dictionary<string, string> LoadAutoColumnCommentCache()
        {
            string cachePath = GetAutoColumnCommentCachePath();
            if (!File.Exists(cachePath)) return null;
            return ParseAutoColumnCommentJson(File.ReadAllText(cachePath, Encoding.UTF8));
        }

        public static Dictionary<string, string> ImportAutoColumnCommentDictionaryFile(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("sourcePath");
            AutoColumnCommentDictionaryImportPreview preview = PreviewAutoColumnCommentDictionaryImportFile(sourcePath);
            Dictionary<string, string> comments = preview.Comments;
            if (comments.Count == 0) throw new InvalidOperationException(Localization.T("Designer.AutoCommentsImportEmpty"));

            SaveAutoColumnCommentCache(comments);
            SetAutoColumnCommentDictionary(comments, "import");
            return comments;
        }

        public static int ExportAutoColumnCommentDictionaryFile(string targetPath, Dictionary<string, string> comments)
        {
            if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentException("targetPath");
            if (comments == null || comments.Count == 0) throw new InvalidOperationException(Localization.T("Designer.AutoCommentsUnavailable"));

            string dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(targetPath, BuildAutoColumnCommentDictionaryExportJson(comments), new UTF8Encoding(false));
            return comments.Count;
        }

        private static string BuildAutoColumnCommentDictionaryExportJson(Dictionary<string, string> comments)
        {
            Dictionary<string, string> normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in comments)
            {
                if (string.IsNullOrWhiteSpace(item.Key) || string.IsNullOrWhiteSpace(item.Value)) continue;
                normalized[item.Key.Trim()] = item.Value.Trim();
            }

            JObject root = new JObject
            {
                ["version"] = 1,
                ["exportedAtUtc"] = DateTime.UtcNow.ToString("o"),
                ["source"] = "mySQLPunk",
                ["entryCount"] = normalized.Count,
                ["columns"] = JObject.FromObject(normalized)
            };
            root["signatureSha256"] = ComputeAutoColumnCommentDictionarySignature(root);
            return root.ToString(Formatting.Indented);
        }

        public static AutoColumnCommentDictionarySignatureInfo ReadAutoColumnCommentDictionarySignature(string json)
        {
            AutoColumnCommentDictionarySignatureInfo info = new AutoColumnCommentDictionarySignatureInfo();
            if (string.IsNullOrWhiteSpace(json)) return info;

            JObject root = ParseAutoColumnCommentDictionaryJObject(json);
            if (root == null) return info;

            string signature = root.Value<string>("signatureSha256") ?? "";
            info.Signature = signature;
            info.SignaturePresent = !string.IsNullOrWhiteSpace(signature);
            info.ExportedAtUtc = root.Value<string>("exportedAtUtc") ?? "";
            info.Source = root.Value<string>("source") ?? "";
            info.SignatureValid = info.SignaturePresent &&
                                  string.Equals(signature, ComputeAutoColumnCommentDictionarySignature(root), StringComparison.OrdinalIgnoreCase);
            return info;
        }

        private static JObject ParseAutoColumnCommentDictionaryJObject(string json)
        {
            using (StringReader stringReader = new StringReader(json))
            using (JsonTextReader jsonReader = new JsonTextReader(stringReader))
            {
                jsonReader.DateParseHandling = DateParseHandling.None;
                return JToken.ReadFrom(jsonReader) as JObject;
            }
        }

        public static string ComputeAutoColumnCommentDictionarySignature(JToken root)
        {
            if (root == null) return "";

            JToken clone = root.DeepClone();
            JObject obj = clone as JObject;
            if (obj != null) obj.Remove("signatureSha256");

            string canonical = CanonicalizeAutoColumnCommentDictionaryJsonToken(clone).ToString(Formatting.None);
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                StringBuilder sb = new StringBuilder(bytes.Length * 2);
                foreach (byte b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static JToken CanonicalizeAutoColumnCommentDictionaryJsonToken(JToken token)
        {
            JObject obj = token as JObject;
            if (obj != null)
            {
                JObject sorted = new JObject();
                foreach (JProperty property in obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    sorted[property.Name] = CanonicalizeAutoColumnCommentDictionaryJsonToken(property.Value);
                }
                return sorted;
            }

            JArray array = token as JArray;
            if (array != null)
            {
                JArray normalized = new JArray();
                foreach (JToken item in array)
                {
                    normalized.Add(CanonicalizeAutoColumnCommentDictionaryJsonToken(item));
                }
                return normalized;
            }

            return token == null ? JValue.CreateNull() : token.DeepClone();
        }

        private static void SetAutoColumnCommentDictionary(Dictionary<string, string> comments, string source, string sourceName = null)
        {
            Dictionary<string, string> normalized = new Dictionary<string, string>(comments, StringComparer.OrdinalIgnoreCase);
            lock (AutoColumnCommentSync)
            {
                AutoColumnCommentTask = Task.FromResult(normalized);
                AutoColumnCommentLastError = null;
                AutoColumnCommentLastErrorUtc = null;
                AutoColumnCommentLastSource = source;
                AutoColumnCommentLastSourceName = sourceName;
            }
        }

        private void ImportAutoColumnCommentsDictionaryWithDialog()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = Localization.T("Designer.ImportAutoCommentsDictionary");
                dialog.Filter = Localization.T("Designer.AutoCommentsDictionaryFileFilter");
                dialog.DefaultExt = "json";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    AutoColumnCommentDictionaryImportPreview preview = PreviewAutoColumnCommentDictionaryImportFile(dialog.FileName);
                    Dictionary<string, string> comments = preview.Comments;
                    Dictionary<string, string> existing = LoadAutoColumnCommentCache() ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    AutoColumnCommentDictionaryDiffReport diffReport = BuildAutoColumnCommentDictionaryDiffReport(existing, comments);
                    diffReport.SignatureInfo = preview.SignatureInfo;
                    if (ShowAutoColumnCommentDictionaryDiffDialog(this, diffReport) != DialogResult.OK)
                    {
                        return;
                    }

                    if (preview.SignatureInfo != null &&
                        preview.SignatureInfo.SignaturePresent &&
                        !preview.SignatureInfo.SignatureValid &&
                        MessageBox.Show(
                            Localization.T("Designer.AutoCommentsDictionaryInvalidSignatureConfirm"),
                            Localization.T("Common.Warning"),
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Warning) != DialogResult.Yes)
                    {
                        return;
                    }

                    comments = ImportAutoColumnCommentDictionaryFile(dialog.FileName);
                    ApplyAutoColumnCommentsToEmptyRows();
                    MessageBox.Show(Localization.Format("Designer.AutoCommentsImportSuccess", comments.Count), Localization.T("Designer.ImportAutoCommentsDictionary"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Localization.Format("Designer.AutoCommentsImportFailed", ex.Message), Localization.T("Designer.ImportAutoCommentsDictionary"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        public static Dictionary<string, string> PreviewAutoColumnCommentDictionaryFile(string sourcePath)
        {
            return PreviewAutoColumnCommentDictionaryImportFile(sourcePath).Comments;
        }

        public static AutoColumnCommentDictionaryImportPreview PreviewAutoColumnCommentDictionaryImportFile(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("sourcePath");
            string json = File.ReadAllText(sourcePath, Encoding.UTF8);
            Dictionary<string, string> comments = ParseAutoColumnCommentJson(json);
            if (comments.Count == 0) throw new InvalidOperationException(Localization.T("Designer.AutoCommentsImportEmpty"));
            return new AutoColumnCommentDictionaryImportPreview
            {
                Comments = comments,
                SignatureInfo = ReadAutoColumnCommentDictionarySignature(json)
            };
        }

        private static string BuildAutoColumnCommentDictionaryDiffSummary(Dictionary<string, string> existing, Dictionary<string, string> imported)
        {
            return BuildAutoColumnCommentDictionaryDiffSummary(BuildAutoColumnCommentDictionaryDiffReport(existing, imported));
        }

        private static string BuildAutoColumnCommentDictionaryDiffSummary(AutoColumnCommentDictionaryDiffReport report)
        {
            if (report == null) report = new AutoColumnCommentDictionaryDiffReport();

            string summary = Localization.Format(
                "Designer.AutoCommentsImportDiffSummary",
                report.ImportedCount,
                report.Added,
                report.Updated,
                report.Removed,
                report.Unchanged);

            if (report.SignatureInfo == null) return summary;
            return summary + Environment.NewLine + Environment.NewLine +
                   BuildAutoColumnCommentDictionarySignatureSummary(report.SignatureInfo);
        }

        private static string BuildAutoColumnCommentDictionarySignatureSummary(AutoColumnCommentDictionarySignatureInfo info)
        {
            if (info == null || !info.SignaturePresent)
            {
                return Localization.T("Designer.AutoCommentsDictionarySignatureMissing");
            }

            string shortSignature = info.Signature ?? "";
            if (shortSignature.Length > 12) shortSignature = shortSignature.Substring(0, 12);
            string status = info.SignatureValid
                ? Localization.T("Designer.AutoCommentsDictionarySignatureValid")
                : Localization.T("Designer.AutoCommentsDictionarySignatureInvalid");

            if (string.IsNullOrWhiteSpace(info.ExportedAtUtc))
            {
                return Localization.Format("Designer.AutoCommentsDictionarySignatureSummary", status, shortSignature);
            }

            return Localization.Format("Designer.AutoCommentsDictionarySignatureSummaryWithTime", status, shortSignature, info.ExportedAtUtc);
        }

        public static AutoColumnCommentDictionaryDiffReport BuildAutoColumnCommentDictionaryDiffReport(Dictionary<string, string> existing, Dictionary<string, string> imported)
        {
            existing = existing ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            imported = imported ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            AutoColumnCommentDictionaryDiffReport report = new AutoColumnCommentDictionaryDiffReport
            {
                ImportedCount = imported.Count
            };

            foreach (var item in imported)
            {
                string existingValue;
                if (!existing.TryGetValue(item.Key, out existingValue))
                {
                    report.Added++;
                    report.Entries.Add(CreateAutoColumnCommentDictionaryDiffEntry(item.Key, "added", "", item.Value));
                }
                else if (string.Equals(existingValue, item.Value, StringComparison.Ordinal))
                {
                    report.Unchanged++;
                    report.Entries.Add(CreateAutoColumnCommentDictionaryDiffEntry(item.Key, "unchanged", existingValue, item.Value));
                }
                else
                {
                    report.Updated++;
                    report.Entries.Add(CreateAutoColumnCommentDictionaryDiffEntry(item.Key, "updated", existingValue, item.Value));
                }
            }

            foreach (var item in existing)
            {
                if (imported.ContainsKey(item.Key)) continue;
                report.Removed++;
                report.Entries.Add(CreateAutoColumnCommentDictionaryDiffEntry(item.Key, "removed", item.Value, ""));
            }

            report.Entries.Sort((left, right) =>
            {
                int statusCompare = GetAutoColumnCommentDictionaryDiffStatusOrder(left.Status)
                    .CompareTo(GetAutoColumnCommentDictionaryDiffStatusOrder(right.Status));
                if (statusCompare != 0) return statusCompare;
                return string.Compare(left.Key, right.Key, StringComparison.OrdinalIgnoreCase);
            });
            return report;
        }

        private static AutoColumnCommentDictionaryDiffEntry CreateAutoColumnCommentDictionaryDiffEntry(string key, string status, string existingValue, string importedValue)
        {
            return new AutoColumnCommentDictionaryDiffEntry
            {
                Key = key ?? "",
                Status = status ?? "",
                ExistingValue = existingValue ?? "",
                ImportedValue = importedValue ?? ""
            };
        }

        private static int GetAutoColumnCommentDictionaryDiffStatusOrder(string status)
        {
            if (string.Equals(status, "added", StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.Equals(status, "updated", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(status, "removed", StringComparison.OrdinalIgnoreCase)) return 2;
            return 3;
        }

        private static string FormatAutoColumnCommentDictionaryDiffStatus(string status)
        {
            if (string.Equals(status, "added", StringComparison.OrdinalIgnoreCase)) return Localization.T("Designer.AutoCommentsDiffAdded");
            if (string.Equals(status, "updated", StringComparison.OrdinalIgnoreCase)) return Localization.T("Designer.AutoCommentsDiffUpdated");
            if (string.Equals(status, "removed", StringComparison.OrdinalIgnoreCase)) return Localization.T("Designer.AutoCommentsDiffRemoved");
            return Localization.T("Designer.AutoCommentsDiffUnchanged");
        }

        private static DataTable BuildAutoColumnCommentDictionaryDiffTable(AutoColumnCommentDictionaryDiffReport report)
        {
            DataTable table = new DataTable();
            table.Columns.Add(Localization.T("Designer.AutoCommentsDiffStatus"));
            table.Columns.Add(Localization.T("Designer.AutoCommentsDiffKey"));
            table.Columns.Add(Localization.T("Designer.AutoCommentsDiffExisting"));
            table.Columns.Add(Localization.T("Designer.AutoCommentsDiffImported"));

            if (report == null) return table;
            foreach (AutoColumnCommentDictionaryDiffEntry entry in report.Entries)
            {
                DataRow row = table.NewRow();
                row[0] = FormatAutoColumnCommentDictionaryDiffStatus(entry.Status);
                row[1] = entry.Key;
                row[2] = entry.ExistingValue;
                row[3] = entry.ImportedValue;
                table.Rows.Add(row);
            }
            return table;
        }

        private static DialogResult ShowAutoColumnCommentDictionaryDiffDialog(Form owner, AutoColumnCommentDictionaryDiffReport report)
        {
            return ShowAutoColumnCommentDictionaryDiffDialog(
                owner,
                report,
                Localization.T("Designer.AutoCommentsImportDiffTitle"),
                Localization.T("Designer.AutoCommentsImportApply"),
                true);
        }

        private static DialogResult ShowAutoColumnCommentDictionaryDiffDialog(Form owner, AutoColumnCommentDictionaryDiffReport report, string title, string okText, bool showCancelButton)
        {
            using (Form dialog = new Form())
            using (Label summaryLabel = new Label())
            using (DataGridView grid = new DataGridView())
            using (Button okButton = new Button())
            using (Button cancelButton = new Button())
            {
                dialog.Text = title;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.MinimizeBox = false;
                dialog.ShowInTaskbar = false;
                dialog.Size = new Size(820, 560);
                dialog.MinimumSize = new Size(680, 420);

                summaryLabel.Dock = DockStyle.Top;
                summaryLabel.Height = 96;
                summaryLabel.Padding = new Padding(12, 10, 12, 8);
                summaryLabel.Text = BuildAutoColumnCommentDictionaryDiffSummary(report);

                grid.Dock = DockStyle.Fill;
                grid.ReadOnly = true;
                grid.AllowUserToAddRows = false;
                grid.AllowUserToDeleteRows = false;
                grid.RowHeadersVisible = false;
                grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
                grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
                grid.DataSource = BuildAutoColumnCommentDictionaryDiffTable(report);

                FlowLayoutPanel footer = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    Height = 48,
                    FlowDirection = FlowDirection.RightToLeft,
                    Padding = new Padding(8)
                };

                okButton.Text = okText;
                okButton.DialogResult = DialogResult.OK;
                okButton.AutoSize = true;
                cancelButton.Text = Localization.T("Common.Cancel");
                cancelButton.DialogResult = DialogResult.Cancel;
                cancelButton.AutoSize = true;

                if (showCancelButton) footer.Controls.Add(cancelButton);
                footer.Controls.Add(okButton);
                dialog.Controls.Add(grid);
                dialog.Controls.Add(summaryLabel);
                dialog.Controls.Add(footer);
                dialog.AcceptButton = okButton;
                if (showCancelButton) dialog.CancelButton = cancelButton;
                ThemeManager.ApplyTo(dialog);
                return dialog.ShowDialog(owner);
            }
        }

        private async Task ExportAutoColumnCommentsDictionaryWithDialogAsync()
        {
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = Localization.T("Designer.ExportAutoCommentsDictionary");
                dialog.Filter = Localization.T("Designer.AutoCommentsDictionaryFileFilter");
                dialog.DefaultExt = "json";
                dialog.FileName = AutoColumnCommentCacheFileName;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    Dictionary<string, string> comments = await GetAutoColumnCommentTask();
                    int count = ExportAutoColumnCommentDictionaryFile(dialog.FileName, comments);
                    MessageBox.Show(Localization.Format("Designer.AutoCommentsExportSuccess", count), Localization.T("Designer.ExportAutoCommentsDictionary"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Localization.Format("Designer.AutoCommentsExportFailed", ex.Message), Localization.T("Designer.ExportAutoCommentsDictionary"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private async Task SaveAutoColumnCommentsDictionaryWithDialogAsync()
        {
            try
            {
                Dictionary<string, string> comments = await GetAutoColumnCommentTask();
                if (comments == null || comments.Count == 0) throw new InvalidOperationException(Localization.T("Designer.AutoCommentsUnavailable"));

                string defaultName = "auto_comments_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string dictionaryName = PromptForAutoColumnCommentDictionaryName(this, defaultName);
                if (dictionaryName == null) return;

                dictionaryName = NormalizeAutoColumnCommentDictionaryName(dictionaryName);
                SaveNamedAutoColumnCommentDictionaryFile(dictionaryName, comments);
                MessageBox.Show(
                    Localization.Format("Designer.AutoCommentsDictionarySaved", dictionaryName, comments.Count),
                    Localization.T("Designer.SaveAutoCommentsDictionaryAs"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    Localization.Format("Designer.AutoCommentsDictionarySaveFailed", ex.Message),
                    Localization.T("Designer.SaveAutoCommentsDictionaryAs"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void SwitchAutoColumnCommentsDictionaryWithDialog()
        {
            try
            {
                List<string> dictionaries = ListNamedAutoColumnCommentDictionaries();
                if (dictionaries.Count == 0)
                {
                    MessageBox.Show(
                        Localization.T("Designer.AutoCommentsNoSavedDictionaries"),
                        Localization.T("Designer.SwitchAutoCommentsDictionary"),
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                string dictionaryName = SelectAutoColumnCommentDictionary(this, dictionaries, Localization.T("Designer.SwitchAutoCommentsDictionary"));
                if (dictionaryName == null) return;

                Dictionary<string, string> comments = LoadNamedAutoColumnCommentDictionaryFile(dictionaryName);
                ApplyAutoColumnCommentsToEmptyRows();
                MessageBox.Show(
                    Localization.Format("Designer.AutoCommentsDictionarySwitched", dictionaryName, comments.Count),
                    Localization.T("Designer.SwitchAutoCommentsDictionary"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    Localization.Format("Designer.AutoCommentsDictionarySwitchFailed", ex.Message),
                    Localization.T("Designer.SwitchAutoCommentsDictionary"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void RenameAutoColumnCommentsDictionaryWithDialog()
        {
            try
            {
                List<string> dictionaries = ListNamedAutoColumnCommentDictionaries();
                if (dictionaries.Count == 0)
                {
                    MessageBox.Show(
                        Localization.T("Designer.AutoCommentsNoSavedDictionaries"),
                        Localization.T("Designer.RenameAutoCommentsDictionary"),
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                string oldName = SelectAutoColumnCommentDictionary(this, dictionaries, Localization.T("Designer.RenameAutoCommentsDictionary"));
                if (oldName == null) return;

                string newName = PromptForAutoColumnCommentDictionaryName(this, oldName);
                if (newName == null) return;

                newName = NormalizeAutoColumnCommentDictionaryName(newName);
                RenameNamedAutoColumnCommentDictionaryFile(oldName, newName);
                MessageBox.Show(
                    Localization.Format("Designer.AutoCommentsDictionaryRenamed", oldName, newName),
                    Localization.T("Designer.RenameAutoCommentsDictionary"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    Localization.Format("Designer.AutoCommentsDictionaryRenameFailed", ex.Message),
                    Localization.T("Designer.RenameAutoCommentsDictionary"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void DeleteAutoColumnCommentsDictionaryWithDialog()
        {
            try
            {
                List<string> dictionaries = ListNamedAutoColumnCommentDictionaries();
                if (dictionaries.Count == 0)
                {
                    MessageBox.Show(
                        Localization.T("Designer.AutoCommentsNoSavedDictionaries"),
                        Localization.T("Designer.DeleteAutoCommentsDictionary"),
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                string dictionaryName = SelectAutoColumnCommentDictionary(this, dictionaries, Localization.T("Designer.DeleteAutoCommentsDictionary"));
                if (dictionaryName == null) return;

                if (MessageBox.Show(
                    Localization.Format("Designer.AutoCommentsDictionaryDeleteConfirm", dictionaryName),
                    Localization.T("Designer.DeleteAutoCommentsDictionary"),
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    return;
                }

                DeleteNamedAutoColumnCommentDictionaryFile(dictionaryName);
                MessageBox.Show(
                    Localization.Format("Designer.AutoCommentsDictionaryDeleted", dictionaryName),
                    Localization.T("Designer.DeleteAutoCommentsDictionary"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    Localization.Format("Designer.AutoCommentsDictionaryDeleteFailed", ex.Message),
                    Localization.T("Designer.DeleteAutoCommentsDictionary"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void CompareAutoColumnCommentsDictionaryVersionWithDialog()
        {
            try
            {
                string dictionaryName = SelectNamedAutoColumnCommentDictionaryForVersionTool(Localization.T("Designer.CompareAutoCommentsDictionaryVersion"));
                if (dictionaryName == null) return;

                AutoColumnCommentDictionaryVersionInfo version = SelectAutoColumnCommentDictionaryVersion(
                    this,
                    ListNamedAutoColumnCommentDictionaryVersions(dictionaryName),
                    Localization.T("Designer.CompareAutoCommentsDictionaryVersion"));
                if (version == null) return;

                AutoColumnCommentDictionaryDiffReport report =
                    BuildNamedAutoColumnCommentDictionaryVersionDiffReport(dictionaryName, version.VersionId);
                ShowAutoColumnCommentDictionaryDiffDialog(
                    this,
                    report,
                    Localization.Format("Designer.AutoCommentsDictionaryVersionDiffTitle", dictionaryName),
                    Localization.T("Common.OK"),
                    false);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    Localization.Format("Designer.AutoCommentsDictionaryVersionCompareFailed", ex.Message),
                    Localization.T("Designer.CompareAutoCommentsDictionaryVersion"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void RestoreAutoColumnCommentsDictionaryVersionWithDialog()
        {
            try
            {
                string dictionaryName = SelectNamedAutoColumnCommentDictionaryForVersionTool(Localization.T("Designer.RestoreAutoCommentsDictionaryVersion"));
                if (dictionaryName == null) return;

                AutoColumnCommentDictionaryVersionInfo version = SelectAutoColumnCommentDictionaryVersion(
                    this,
                    ListNamedAutoColumnCommentDictionaryVersions(dictionaryName),
                    Localization.T("Designer.RestoreAutoCommentsDictionaryVersion"));
                if (version == null) return;

                AutoColumnCommentDictionaryDiffReport report =
                    BuildNamedAutoColumnCommentDictionaryVersionDiffReport(dictionaryName, version.VersionId);
                if (ShowAutoColumnCommentDictionaryDiffDialog(
                    this,
                    report,
                    Localization.Format("Designer.AutoCommentsDictionaryVersionRestoreTitle", dictionaryName),
                    Localization.T("Designer.AutoCommentsDictionaryVersionRestoreApply"),
                    true) != DialogResult.OK)
                {
                    return;
                }

                Dictionary<string, string> comments = RestoreNamedAutoColumnCommentDictionaryVersion(dictionaryName, version.VersionId);
                ApplyAutoColumnCommentsToEmptyRows();
                MessageBox.Show(
                    Localization.Format("Designer.AutoCommentsDictionaryVersionRestored", dictionaryName, comments.Count),
                    Localization.T("Designer.RestoreAutoCommentsDictionaryVersion"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    Localization.Format("Designer.AutoCommentsDictionaryVersionRestoreFailed", ex.Message),
                    Localization.T("Designer.RestoreAutoCommentsDictionaryVersion"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private string SelectNamedAutoColumnCommentDictionaryForVersionTool(string title)
        {
            List<string> dictionaries = ListNamedAutoColumnCommentDictionaries();
            if (dictionaries.Count == 0)
            {
                MessageBox.Show(
                    Localization.T("Designer.AutoCommentsNoSavedDictionaries"),
                    title,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return null;
            }

            string dictionaryName = SelectAutoColumnCommentDictionary(this, dictionaries, title);
            if (dictionaryName == null) return null;

            if (ListNamedAutoColumnCommentDictionaryVersions(dictionaryName).Count == 0)
            {
                MessageBox.Show(
                    Localization.Format("Designer.AutoCommentsDictionaryNoVersions", dictionaryName),
                    title,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return null;
            }

            return dictionaryName;
        }

        private static string PromptForAutoColumnCommentDictionaryName(Form owner, string defaultName)
        {
            using (Form dialog = new Form())
            using (Label label = new Label())
            using (TextBox textBox = new TextBox())
            using (Button okButton = new Button())
            using (Button cancelButton = new Button())
            {
                dialog.Text = Localization.T("Designer.AutoCommentsDictionaryNameTitle");
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.MinimizeBox = false;
                dialog.MaximizeBox = false;
                dialog.ClientSize = new Size(420, 130);

                label.Text = Localization.T("Designer.AutoCommentsDictionaryNamePrompt");
                label.AutoSize = false;
                label.Location = new Point(12, 12);
                label.Size = new Size(396, 24);

                textBox.Location = new Point(12, 42);
                textBox.Size = new Size(396, 24);
                textBox.Text = defaultName;
                textBox.SelectAll();

                okButton.Text = Localization.T("Common.OK");
                okButton.DialogResult = DialogResult.OK;
                okButton.Location = new Point(252, 88);
                okButton.Size = new Size(75, 28);

                cancelButton.Text = Localization.T("Common.Cancel");
                cancelButton.DialogResult = DialogResult.Cancel;
                cancelButton.Location = new Point(333, 88);
                cancelButton.Size = new Size(75, 28);

                dialog.Controls.Add(label);
                dialog.Controls.Add(textBox);
                dialog.Controls.Add(okButton);
                dialog.Controls.Add(cancelButton);
                dialog.AcceptButton = okButton;
                dialog.CancelButton = cancelButton;

                return dialog.ShowDialog(owner) == DialogResult.OK ? textBox.Text : null;
            }
        }

        private static string SelectAutoColumnCommentDictionary(Form owner, List<string> dictionaries, string title)
        {
            using (Form dialog = new Form())
            using (ListBox listBox = new ListBox())
            using (Button okButton = new Button())
            using (Button cancelButton = new Button())
            {
                dialog.Text = title;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.MinimizeBox = false;
                dialog.MaximizeBox = false;
                dialog.ClientSize = new Size(420, 300);

                listBox.Location = new Point(12, 12);
                listBox.Size = new Size(396, 238);
                foreach (string dictionaryName in dictionaries) listBox.Items.Add(dictionaryName);
                if (listBox.Items.Count > 0) listBox.SelectedIndex = 0;
                listBox.DoubleClick += (s, e) =>
                {
                    if (listBox.SelectedItem != null) dialog.DialogResult = DialogResult.OK;
                };

                okButton.Text = Localization.T("Common.OK");
                okButton.DialogResult = DialogResult.OK;
                okButton.Location = new Point(252, 260);
                okButton.Size = new Size(75, 28);

                cancelButton.Text = Localization.T("Common.Cancel");
                cancelButton.DialogResult = DialogResult.Cancel;
                cancelButton.Location = new Point(333, 260);
                cancelButton.Size = new Size(75, 28);

                dialog.Controls.Add(listBox);
                dialog.Controls.Add(okButton);
                dialog.Controls.Add(cancelButton);
                dialog.AcceptButton = okButton;
                dialog.CancelButton = cancelButton;

                return dialog.ShowDialog(owner) == DialogResult.OK && listBox.SelectedItem != null
                    ? listBox.SelectedItem.ToString()
                    : null;
            }
        }

        private static AutoColumnCommentDictionaryVersionInfo SelectAutoColumnCommentDictionaryVersion(Form owner, List<AutoColumnCommentDictionaryVersionInfo> versions, string title)
        {
            if (versions == null || versions.Count == 0) return null;

            using (Form dialog = new Form())
            using (ListBox listBox = new ListBox())
            using (Button okButton = new Button())
            using (Button cancelButton = new Button())
            {
                dialog.Text = title;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.MinimizeBox = false;
                dialog.MaximizeBox = false;
                dialog.ClientSize = new Size(520, 300);

                listBox.Location = new Point(12, 12);
                listBox.Size = new Size(496, 238);
                foreach (AutoColumnCommentDictionaryVersionInfo version in versions)
                {
                    listBox.Items.Add(FormatAutoColumnCommentDictionaryVersion(version));
                }
                if (listBox.Items.Count > 0) listBox.SelectedIndex = 0;
                listBox.DoubleClick += (s, e) =>
                {
                    if (listBox.SelectedItem != null) dialog.DialogResult = DialogResult.OK;
                };

                okButton.Text = Localization.T("Common.OK");
                okButton.DialogResult = DialogResult.OK;
                okButton.Location = new Point(352, 260);
                okButton.Size = new Size(75, 28);

                cancelButton.Text = Localization.T("Common.Cancel");
                cancelButton.DialogResult = DialogResult.Cancel;
                cancelButton.Location = new Point(433, 260);
                cancelButton.Size = new Size(75, 28);

                dialog.Controls.Add(listBox);
                dialog.Controls.Add(okButton);
                dialog.Controls.Add(cancelButton);
                dialog.AcceptButton = okButton;
                dialog.CancelButton = cancelButton;

                return dialog.ShowDialog(owner) == DialogResult.OK && listBox.SelectedIndex >= 0
                    ? versions[listBox.SelectedIndex]
                    : null;
            }
        }

        private static string FormatAutoColumnCommentDictionaryVersion(AutoColumnCommentDictionaryVersionInfo version)
        {
            if (version == null) return "";
            DateTime localTime = version.CreatedAtUtc.Kind == DateTimeKind.Utc
                ? version.CreatedAtUtc.ToLocalTime()
                : DateTime.SpecifyKind(version.CreatedAtUtc, DateTimeKind.Utc).ToLocalTime();
            return Localization.Format(
                "Designer.AutoCommentsDictionaryVersionListItem",
                localTime.ToString("yyyy-MM-dd HH:mm:ss"),
                version.EntryCount,
                version.VersionId);
        }

        private void ApplyAutoColumnCommentForGridRow(int rowIndex)
        {
            if (!IsNewTable || dgvColumns == null || rowIndex < 0 || rowIndex >= dgvColumns.Rows.Count) return;
            DataGridViewRow gridRow = dgvColumns.Rows[rowIndex];
            if (gridRow == null || gridRow.IsNewRow) return;

            DataRowView rowView = gridRow.DataBoundItem as DataRowView;
            if (rowView == null) return;

            ApplyAutoColumnComment(rowView.Row);
        }

        private void ApplyAutoColumnCommentsToEmptyRows()
        {
            if (!IsNewTable || dgvColumns == null) return;
            DataTable currentDt = dgvColumns.DataSource as DataTable;
            if (currentDt == null) return;

            bool changed = false;
            foreach (DataRow row in currentDt.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;
                changed |= ApplyAutoColumnComment(row);
            }

            if (changed)
            {
                MarkAsModified();
                if (tcMain != null && tcMain.SelectedTab == tpSqlPreview) GeneratePreviewSql();
            }
        }

        private bool ApplyAutoColumnComment(DataRow row)
        {
            if (!IsNewTable || row == null || !row.Table.Columns.Contains("Comment")) return false;
            if (!row.Table.Columns.Contains("_AutoComment")) return false;

            string columnName = GetRowString(row, "Name").Trim();
            if (string.IsNullOrWhiteSpace(columnName)) return false;

            string currentComment = GetRowString(row, "Comment").Trim();
            string currentAutoComment = GetRowString(row, "_AutoComment").Trim();
            if (!string.IsNullOrWhiteSpace(currentComment) &&
                !string.Equals(currentComment, currentAutoComment, StringComparison.Ordinal))
            {
                return false;
            }

            string comment;
            if (TryGetLoadedAutoColumnComment(columnName, out comment))
            {
                if (currentComment == comment && currentAutoComment == comment) return false;
                row["Comment"] = comment;
                row["_AutoComment"] = comment;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(currentAutoComment) && currentComment == currentAutoComment)
            {
                row["Comment"] = "";
                row["_AutoComment"] = "";
                return true;
            }

            return false;
        }

        private async Task<int> FillAutoColumnCommentsAsync(AutoCommentMode mode, bool showMessage)
        {
            if (_isFillingAutoComments) return 0;

            DataTable currentDt = dgvColumns?.DataSource as DataTable;
            if (currentDt == null) return 0;

            Cursor previousCursor = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;
            _isFillingAutoComments = true;
            if (btnFillAutoComments != null) btnFillAutoComments.Enabled = false;
            using (autoCommentProgressOverlay = RunnerProgressOverlay.Show(GetAutoCommentProgressOwner(), Localization.T("Designer.FillAutoComments"), Localization.T("Designer.AutoCommentsLoading")))
            {
                ShowAutoCommentProgress(Localization.T("Designer.AutoCommentsLoading"), 0, 1);
                try
                {
                    Dictionary<string, string> comments;
                    try
                    {
                        comments = await GetAutoColumnCommentTask();
                    }
                    catch (Exception ex)
                    {
                        if (showMessage)
                        {
                            MessageBox.Show(Localization.Format("Designer.AutoCommentsLoadFailed", ex.Message), Localization.T("Designer.FillAutoComments"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                        return 0;
                    }

                    if (comments == null || comments.Count == 0)
                    {
                        if (showMessage)
                        {
                            MessageBox.Show(Localization.T("Designer.AutoCommentsUnavailable"), Localization.T("Designer.FillAutoComments"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                        return 0;
                    }

                    int applied = 0;
                    int processed = 0;
                    int total = CountFillableColumnRows(currentDt);
                    ShowAutoCommentProgress(GetAutoColumnCommentSourceMessage(), 0, Math.Max(total, 1));
                    foreach (DataRow row in currentDt.Rows)
                    {
                        if (row.RowState == DataRowState.Deleted) continue;
                        string columnName = GetRowString(row, "Name").Trim();
                        if (string.IsNullOrWhiteSpace(columnName)) continue;
                        processed++;
                        ShowAutoCommentProgress(Localization.Format("Designer.AutoCommentsProgress", processed, total, columnName), processed, total);
                        await Task.Delay(20);
                        if (ApplyAutoColumnComment(row, comments, mode)) applied++;
                    }

                    if (applied > 0)
                    {
                        MarkAsModified();
                        if (tcMain != null)
                        {
                            tcMain.SelectedTab = tpSqlPreview;
                            GeneratePreviewSql();
                        }
                    }

                    ShowAutoCommentProgress(Localization.Format("Designer.AutoCommentsDone", applied, total), total, total);

                    if (showMessage)
                    {
                        string message = GetDesignerAutoCommentResultMessage(applied, mode);
                        MessageBox.Show(message, Localization.T("Designer.FillAutoComments"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }

                    return applied;
                }
                finally
                {
                    await Task.Delay(250);
                    autoCommentProgressOverlay = null;
                    if (btnFillAutoComments != null) btnFillAutoComments.Enabled = true;
                    _isFillingAutoComments = false;
                    Cursor.Current = previousCursor;
                }
            }
        }

        private int CountFillableColumnRows(DataTable currentDt)
        {
            if (currentDt == null) return 0;
            int total = 0;
            foreach (DataRow row in currentDt.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;
                if (!string.IsNullOrWhiteSpace(GetRowString(row, "Name"))) total++;
            }
            return Math.Max(total, 1);
        }

        private void ShowAutoCommentProgress(string message, int current, int total)
        {
            if (autoCommentProgressOverlay != null) autoCommentProgressOverlay.SetProgress(current, total, message);
        }

        private static string GetDesignerAutoCommentResultMessage(int applied, AutoCommentMode mode)
        {
            if (applied > 0)
            {
                return mode == AutoCommentMode.Overwrite
                    ? Localization.Format("Designer.AutoCommentsUpdated", applied)
                    : Localization.Format("Designer.AutoCommentsApplied", applied);
            }

            return mode == AutoCommentMode.Overwrite
                ? Localization.T("Designer.AutoCommentsNoUpdates")
                : Localization.T("Designer.AutoCommentsNoMatches");
        }

        private Form GetAutoCommentProgressOwner()
        {
            if (_isDocked && _mainHost != null && !_mainHost.IsDisposed) return _mainHost;
            return this;
        }

        private bool ApplyAutoColumnComment(DataRow row, Dictionary<string, string> comments, AutoCommentMode mode)
        {
            if (row == null || comments == null || !row.Table.Columns.Contains("Comment")) return false;

            string columnName = GetRowString(row, "Name").Trim();
            if (string.IsNullOrWhiteSpace(columnName)) return false;

            string comment;
            if (!comments.TryGetValue(columnName, out comment)) return false;
            if (string.IsNullOrWhiteSpace(comment)) return false;

            string currentComment = GetRowString(row, "Comment");
            string nextComment = comment.Trim();
            if (mode == AutoCommentMode.FillBlanks && !string.IsNullOrWhiteSpace(currentComment)) return false;
            if (string.Equals(currentComment, nextComment, StringComparison.Ordinal)) return false;

            row["Comment"] = nextComment;
            if (row.Table.Columns.Contains("_AutoComment"))
            {
                row["_AutoComment"] = nextComment;
            }
            return true;
        }

        private static bool TryGetLoadedAutoColumnComment(string columnName, out string comment)
        {
            comment = "";
            Task<Dictionary<string, string>> task;
            lock (AutoColumnCommentSync)
            {
                task = AutoColumnCommentTask;
            }

            if (task == null || !task.IsCompleted || task.IsFaulted || task.IsCanceled) return false;

            Dictionary<string, string> comments = task.Result;
            return comments != null && comments.TryGetValue(columnName, out comment);
        }

        private void BindColumns(DataTable displayDt)
        {
            dgvColumns.DataSource = displayDt;

            ApplyColumnHeaders();
            dgvColumns.Columns["_OldName"].Visible = false;
            dgvColumns.Columns["_AutoComment"].Visible = false;
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
            if (btnExecuteSql != null)
            {
                btnExecuteSql.Text = Localization.T("Designer.ExecuteSql");
                btnExecuteSql.ToolTipText = Localization.T("Designer.ExecuteSqlTooltip");
            }
            if (btnFillAutoComments != null) btnFillAutoComments.Text = Localization.T("Designer.FillAutoComments");
            if (btnFillAutoComments != null && btnFillAutoComments.DropDownItems.Count >= 2)
            {
                btnFillAutoComments.DropDownItems[0].Text = Localization.T("Designer.FillBlankAutoComments");
                btnFillAutoComments.DropDownItems[1].Text = Localization.T("Designer.OverwriteAutoComments");
                if (btnFillAutoComments.DropDownItems.Count >= 5)
                {
                    btnFillAutoComments.DropDownItems[3].Text = Localization.T("Designer.ImportAutoCommentsDictionary");
                    btnFillAutoComments.DropDownItems[4].Text = Localization.T("Designer.ExportAutoCommentsDictionary");
                }
                if (btnFillAutoComments.DropDownItems.Count >= 8)
                {
                    btnFillAutoComments.DropDownItems[6].Text = Localization.T("Designer.SaveAutoCommentsDictionaryAs");
                    btnFillAutoComments.DropDownItems[7].Text = Localization.T("Designer.SwitchAutoCommentsDictionary");
                }
                if (btnFillAutoComments.DropDownItems.Count >= 10)
                {
                    btnFillAutoComments.DropDownItems[8].Text = Localization.T("Designer.RenameAutoCommentsDictionary");
                    btnFillAutoComments.DropDownItems[9].Text = Localization.T("Designer.DeleteAutoCommentsDictionary");
                }
            }
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
                rtbSqlPreview.Text = BuildSqlPreviewText(BuildCreateTableSql(currentDt));
                return;
            }

            if (!(_db is my_mysql))
            {
                rtbSqlPreview.Text = BuildSqlPreviewText(BuildGenericAlterTableSql(currentDt));
                return;
            }

            rtbSqlPreview.Text = BuildMySqlAlterTableSql(currentDt);
        }

        private string BuildMySqlAlterTableSql(DataTable currentDt)
        {
            List<string> changes = new List<string>();
            string prevCol = null;

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
                    changes.Add("DROP COLUMN " + QuoteMySqlIdentifier(oldName));
                }
            }

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
                string commentStr = string.IsNullOrEmpty(colComment) ? "" : "COMMENT " + EscapeMySqlStringLiteral(colComment);
                
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
                        string addIndexChange = BuildMySqlAddIndexChange(curr);
                        if (!string.IsNullOrWhiteSpace(addIndexChange))
                        {
                            changes.Add(addIndexChange);
                        }
                    }
                }
            }

            if (changes.Count > 0)
            {
                return $"ALTER TABLE `{_databaseName}`.`{_tableName}`\n  " +
                       string.Join(",\n  ", changes) + ";";
            }

            return "-- No changes detected.";
        }

        private string BuildMySqlAddIndexChange(DataRow row)
        {
            string indexName = GetRowString(row, "名稱").Trim();
            string type = GetRowString(row, "索引類型").Trim().ToUpperInvariant();
            string method = GetRowString(row, "索引方法").Trim();
            string columns = GetRowString(row, "欄位").Trim();
            string columnList = FormatMySqlIndexColumns(columns);
            if (string.IsNullOrWhiteSpace(columnList)) return "";

            string methodClause = string.IsNullOrWhiteSpace(method) ? "" : " USING " + method;
            string comment = GetRowString(row, "註解").Trim();
            string commentClause = string.IsNullOrWhiteSpace(comment) ? "" : " COMMENT " + EscapeMySqlStringLiteral(comment);

            if (type == "PRIMARY")
            {
                return "ADD PRIMARY KEY (" + columnList + ")" + methodClause;
            }

            if (string.IsNullOrWhiteSpace(indexName)) return "";

            if (type == "UNIQUE")
            {
                return "ADD UNIQUE INDEX " + QuoteMySqlIdentifier(indexName) + " (" + columnList + ")" + methodClause + commentClause;
            }

            if (type == "FULLTEXT")
            {
                return "ADD FULLTEXT INDEX " + QuoteMySqlIdentifier(indexName) + " (" + columnList + ")" + commentClause;
            }

            if (type == "SPATIAL")
            {
                return "ADD SPATIAL INDEX " + QuoteMySqlIdentifier(indexName) + " (" + columnList + ")" + commentClause;
            }

            return "ADD INDEX " + QuoteMySqlIdentifier(indexName) + " (" + columnList + ")" + methodClause + commentClause;
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
                    if (_db is my_sqlite)
                    {
                        statements.Add(BuildSqliteDeleteColumnCommentStatement(_tableName, oldName));
                    }
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
                    if (_db is my_sqlite)
                    {
                        statements.AddRange(BuildSqliteColumnCommentStatements(_tableName, current));
                    }
                    continue;
                }

                if (!oldName.Equals(columnName, StringComparison.Ordinal))
                {
                    statements.Add(BuildRenameColumnStatement(oldName, columnName));
                    if (_db is my_sqlite)
                    {
                        statements.Add(BuildSqliteDeleteColumnCommentStatement(_tableName, oldName));
                    }
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

            statements.AddRange(BuildSqliteReplaceAllColumnCommentStatements(_tableName, currentDt));

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
                SqlServerDesignerObjectName target = ParseSqlServerDesignerObjectName(_tableName);
                return "EXEC " + QuoteDesignerIdentifier(_databaseName) + ".sys.sp_rename N'" +
                       EscapeSqlServerLiteral(target.Schema) + "." + EscapeSqlServerLiteral(target.Name) + "." + EscapeSqlServerLiteral(oldName) +
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
                    statements.AddRange(BuildSqliteColumnCommentStatements(_tableName, current));
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
            SqlServerDesignerObjectName target = ParseSqlServerDesignerObjectName(_tableName);
            return "DECLARE @constraintName nvarchar(128);\r\n" +
                   "SELECT @constraintName = dc.name\r\n" +
                   "FROM " + database + ".sys.default_constraints dc\r\n" +
                   "INNER JOIN " + database + ".sys.columns c ON c.default_object_id = dc.object_id\r\n" +
                   "INNER JOIN " + database + ".sys.tables t ON t.object_id = c.object_id\r\n" +
                   "INNER JOIN " + database + ".sys.schemas s ON s.schema_id = t.schema_id\r\n" +
                   "WHERE s.name = N'" + EscapeSqlServerLiteral(target.Schema) + "' AND t.name = N'" + EscapeSqlServerLiteral(target.Name) + "' AND c.name = N'" + EscapeSqlServerLiteral(columnName) + "';\r\n" +
                   "IF @constraintName IS NOT NULL EXEC(N'ALTER TABLE " + GetQualifiedDesignerTableName(_tableName).Replace("'", "''") + " DROP CONSTRAINT [' + REPLACE(@constraintName, ']', ']]') + N']');";
        }

        private string BuildSqlServerAddDefaultConstraintStatement(string columnName, string defaultValue)
        {
            return "ALTER TABLE " + GetQualifiedDesignerTableName(_tableName) +
                   " ADD CONSTRAINT " + QuoteDesignerIdentifier(BuildSqlServerDefaultConstraintName(GetSqlServerDesignerObjectName(_tableName), columnName)) +
                   " DEFAULT " + FormatGenericDefault(defaultValue) +
                   " FOR " + QuoteDesignerIdentifier(columnName) + ";";
        }

        private static string BuildSqlServerDefaultConstraintName(string tableName, string columnName)
        {
            string baseName = "DF_" + SanitizeSqlServerConstraintNamePart(tableName) + "_" + SanitizeSqlServerConstraintNamePart(columnName);
            if (baseName.Length <= 120) return baseName;

            string hash = StableHexHash(tableName + "." + columnName);
            int keepLength = Math.Max(1, 120 - hash.Length - 1);
            return baseName.Substring(0, keepLength) + "_" + hash;
        }

        private static string SanitizeSqlServerConstraintNamePart(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "column";

            StringBuilder sb = new StringBuilder();
            foreach (char ch in value.Trim())
            {
                if (char.IsLetterOrDigit(ch) || ch == '_')
                {
                    sb.Append(ch);
                }
                else
                {
                    sb.Append('_');
                }
            }

            string result = sb.ToString().Trim('_');
            return string.IsNullOrWhiteSpace(result) ? "column" : result;
        }

        private static string StableHexHash(string value)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char ch in value ?? string.Empty)
                {
                    hash ^= ch;
                    hash *= 16777619;
                }
                return hash.ToString("x8");
            }
        }

        private string BuildSqlServerColumnCommentStatement(string columnName, string comment)
        {
            return BuildSqlServerColumnCommentStatement(_tableName, columnName, comment);
        }

        private string BuildSqlServerColumnCommentStatement(string tableName, string columnName, string comment)
        {
            string database = QuoteDesignerIdentifier(_databaseName);
            SqlServerDesignerObjectName target = ParseSqlServerDesignerObjectName(tableName);
            string schemaLiteral = EscapeSqlServerLiteral(target.Schema);
            string tableLiteral = EscapeSqlServerLiteral(target.Name);
            string columnLiteral = EscapeSqlServerLiteral(columnName);
            string valueLiteral = EscapeSqlServerLiteral(comment ?? "");

            string existsSql =
                "EXISTS (SELECT 1 FROM " + database + ".sys.extended_properties ep " +
                "INNER JOIN " + database + ".sys.columns c ON c.object_id = ep.major_id AND c.column_id = ep.minor_id " +
                "INNER JOIN " + database + ".sys.tables t ON t.object_id = c.object_id " +
                "INNER JOIN " + database + ".sys.schemas s ON s.schema_id = t.schema_id " +
                "WHERE ep.name = N'MS_Description' AND s.name = N'" + schemaLiteral + "' AND t.name = N'" + tableLiteral + "' AND c.name = N'" + columnLiteral + "')";

            string commonArgs =
                "@name=N'MS_Description', @level0type=N'SCHEMA', @level0name=N'" + schemaLiteral + "', " +
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
            SqlServerDesignerObjectName target = ParseSqlServerDesignerObjectName(_tableName);
            return "DECLARE @primaryKeyName nvarchar(128);\r\n" +
                   "SELECT @primaryKeyName = kc.name\r\n" +
                   "FROM " + database + ".sys.key_constraints kc\r\n" +
                   "INNER JOIN " + database + ".sys.tables t ON t.object_id = kc.parent_object_id\r\n" +
                   "INNER JOIN " + database + ".sys.schemas s ON s.schema_id = t.schema_id\r\n" +
                   "WHERE kc.[type] = 'PK' AND s.name = N'" + EscapeSqlServerLiteral(target.Schema) + "' AND t.name = N'" + EscapeSqlServerLiteral(target.Name) + "';\r\n" +
                   "IF @primaryKeyName IS NOT NULL EXEC(N'ALTER TABLE " + GetQualifiedDesignerTableName(_tableName).Replace("'", "''") + " DROP CONSTRAINT [' + REPLACE(@primaryKeyName, ']', ']]') + N']');";
        }

        private string BuildSqlServerAddPrimaryKeyStatement(DataRow row)
        {
            string indexName = GetRowString(row, "名稱").Trim();
            if (string.IsNullOrWhiteSpace(indexName) || IsPrimaryIndexName(indexName))
            {
                indexName = "PK_" + GetSqlServerDesignerObjectName(_tableName);
            }

            return "ALTER TABLE " + GetQualifiedDesignerTableName(_tableName) +
                   " ADD CONSTRAINT " + QuoteDesignerIdentifier(indexName) +
                   " PRIMARY KEY (" + FormatGenericIndexColumns(GetRowString(row, "欄位").Trim()) + ");";
        }

        private string BuildPostgreSqlDropPrimaryKeyStatement()
        {
            PostgreSqlDesignerObjectName target = ParsePostgreSqlDesignerObjectName(_tableName);
            string schemaLiteral = EscapeSqlStringValue(target.Schema);
            string tableLiteral = EscapeSqlStringValue(target.Name);
            string qualifiedTable = GetQualifiedDesignerTableName(_tableName).Replace("'", "''");

            return "DO $mysqlpunk$\r\n" +
                   "DECLARE primary_key_name text;\r\n" +
                   "BEGIN\r\n" +
                   "  SELECT tc.constraint_name INTO primary_key_name\r\n" +
                   "  FROM information_schema.table_constraints tc\r\n" +
                   "  WHERE tc.constraint_type = 'PRIMARY KEY' AND tc.table_schema = '" + schemaLiteral + "' AND tc.table_name = '" + tableLiteral + "';\r\n" +
                   "  IF primary_key_name IS NOT NULL THEN\r\n" +
                   "    EXECUTE 'ALTER TABLE " + qualifiedTable + " DROP CONSTRAINT ' || quote_ident(primary_key_name);\r\n" +
                   "  END IF;\r\n" +
                   "END\r\n" +
                   "$mysqlpunk$;";
        }

        private string BuildPostgreSqlAddPrimaryKeyStatement(DataRow row)
        {
            string indexName = GetPostgreSqlPrimaryKeyConstraintName(row);
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

        private string GetPostgreSqlPrimaryKeyConstraintName(DataRow row)
        {
            string indexName = GetRowString(row, "名稱").Trim();
            if (string.IsNullOrWhiteSpace(indexName) || IsPrimaryIndexName(indexName))
            {
                indexName = "pk_" + ParsePostgreSqlDesignerObjectName(_tableName).Name;
            }
            return indexName;
        }

        private string BuildDropIndexStatement(string indexName)
        {
            if (_db is my_mssql)
            {
                return "DROP INDEX " + QuoteDesignerIdentifier(indexName) + " ON " + GetQualifiedDesignerTableName(_tableName) + ";";
            }
            if (_db is my_postgresql)
            {
                return "DROP INDEX " + GetPostgreSqlQualifiedObjectName(indexName) + ";";
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

            List<string> comments = BuildGenericCreateColumnCommentStatements(tableName, currentDt);
            if (comments.Count > 0)
            {
                sql += "\n" + string.Join("\n", comments.ToArray());
            }

            return sql;
        }

        private List<string> BuildGenericCreateColumnCommentStatements(string tableName, DataTable currentDt)
        {
            List<string> statements = new List<string>();
            if (currentDt == null) return statements;

            if (_db is my_sqlite)
            {
                return BuildSqliteReplaceAllColumnCommentStatements(tableName, currentDt);
            }

            foreach (DataRow row in currentDt.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;

                string columnName = GetRowString(row, "Name").Trim();
                string comment = GetRowString(row, "Comment").Trim();
                if (string.IsNullOrWhiteSpace(columnName) || string.IsNullOrWhiteSpace(comment)) continue;

                if (_db is my_mssql)
                {
                    statements.Add(BuildSqlServerColumnCommentStatement(tableName, columnName, comment));
                    continue;
                }

                if (_db is my_postgresql || _db is my_oracle)
                {
                    statements.Add("COMMENT ON COLUMN " + GetQualifiedDesignerTableName(tableName) + "." + QuoteDesignerIdentifier(columnName) +
                                   " IS " + EscapeSqlStringLiteral(comment) + ";");
                }
            }

            return statements;
        }

        private List<string> BuildSqliteReplaceAllColumnCommentStatements(string tableName, DataTable currentDt)
        {
            List<string> statements = new List<string>();
            if (currentDt == null || string.IsNullOrWhiteSpace(tableName)) return statements;

            statements.Add(BuildSqliteEnsureColumnCommentTableStatement());
            statements.Add("DELETE FROM " + QuoteDesignerIdentifier(my_sqlite.ColumnCommentTableName) +
                           " WHERE table_name = " + EscapeSqlStringLiteral(tableName) + ";");

            foreach (DataRow row in currentDt.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;

                string columnName = GetRowString(row, "Name").Trim();
                string comment = GetRowString(row, "Comment").Trim();
                if (string.IsNullOrWhiteSpace(columnName) || string.IsNullOrWhiteSpace(comment)) continue;

                statements.Add(BuildSqliteUpsertColumnCommentStatement(tableName, columnName, comment));
            }

            return statements;
        }

        private List<string> BuildSqliteColumnCommentStatements(string tableName, DataRow row)
        {
            List<string> statements = new List<string>();
            if (row == null || string.IsNullOrWhiteSpace(tableName)) return statements;

            string columnName = GetRowString(row, "Name").Trim();
            if (string.IsNullOrWhiteSpace(columnName)) return statements;

            statements.Add(BuildSqliteEnsureColumnCommentTableStatement());
            string comment = GetRowString(row, "Comment").Trim();
            statements.Add(string.IsNullOrWhiteSpace(comment)
                ? BuildSqliteDeleteColumnCommentStatement(tableName, columnName)
                : BuildSqliteUpsertColumnCommentStatement(tableName, columnName, comment));
            return statements;
        }

        private string BuildSqliteEnsureColumnCommentTableStatement()
        {
            return "CREATE TABLE IF NOT EXISTS " + QuoteDesignerIdentifier(my_sqlite.ColumnCommentTableName) + " (" +
                   "table_name TEXT NOT NULL, " +
                   "column_name TEXT NOT NULL, " +
                   "comment TEXT NOT NULL, " +
                   "PRIMARY KEY (table_name, column_name)" +
                   ");";
        }

        private string BuildSqliteUpsertColumnCommentStatement(string tableName, string columnName, string comment)
        {
            return "INSERT OR REPLACE INTO " + QuoteDesignerIdentifier(my_sqlite.ColumnCommentTableName) +
                   " (table_name, column_name, comment) VALUES (" +
                   EscapeSqlStringLiteral(tableName) + ", " +
                   EscapeSqlStringLiteral(columnName) + ", " +
                   EscapeSqlStringLiteral(comment) + ");";
        }

        private string BuildSqliteDeleteColumnCommentStatement(string tableName, string columnName)
        {
            return "DELETE FROM " + QuoteDesignerIdentifier(my_sqlite.ColumnCommentTableName) +
                   " WHERE table_name = " + EscapeSqlStringLiteral(tableName) +
                   " AND column_name = " + EscapeSqlStringLiteral(columnName) + ";";
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
            SqlServerDesignerObjectName target = ParseSqlServerDesignerObjectName(tableName);
            string schemaIdentifier = QuoteDesignerIdentifier(target.Schema).Replace("'", "''");
            string tableIdentifier = QuoteDesignerIdentifier(target.Name).Replace("'", "''");
            string catalogIdentifier = QuoteDesignerIdentifier(catalogName).Replace("'", "''");
            string escapedFullTextColumns = fullTextColumns.Replace("'", "''");
            string schemaLiteral = EscapeSqlServerLiteral(target.Schema);
            string tableLiteral = EscapeSqlServerLiteral(target.Name);
            string databaseIdentifier = QuoteDesignerIdentifier(_databaseName);

            return "DECLARE @fullTextKeyIndex nvarchar(128);\r\n" +
                   "DECLARE @catalogName sysname = N'" + EscapeSqlServerLiteral(catalogName) + "';\r\n" +
                   "DECLARE @createCatalogSql nvarchar(max);\r\n" +
                   "DECLARE @createIndexSql nvarchar(max);\r\n" +
                   "SELECT TOP (1) @fullTextKeyIndex = i.name\r\n" +
                   "FROM " + QuoteDesignerIdentifier(_databaseName) + ".sys.indexes i\r\n" +
                   "INNER JOIN " + QuoteDesignerIdentifier(_databaseName) + ".sys.objects o ON o.object_id = i.object_id\r\n" +
                   "INNER JOIN " + QuoteDesignerIdentifier(_databaseName) + ".sys.schemas s ON s.schema_id = o.schema_id\r\n" +
                   "WHERE s.name = N'" + schemaLiteral + "' AND o.name = N'" + tableLiteral + "' AND (i.is_primary_key = 1 OR i.is_unique = 1) AND i.is_disabled = 0 AND i.has_filter = 0\r\n" +
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
                   "SET @createIndexSql = N'CREATE FULLTEXT INDEX ON " + schemaIdentifier + "." + tableIdentifier +
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
                SqlServerDesignerObjectName target = ParseSqlServerDesignerObjectName(tableName);
                return QuoteDesignerIdentifier(_databaseName) + "." + QuoteDesignerIdentifier(target.Schema) + "." + QuoteDesignerIdentifier(target.Name);
            }
            if (_db is my_postgresql)
            {
                PostgreSqlDesignerObjectName target = ParsePostgreSqlDesignerObjectName(tableName);
                return QuoteDesignerIdentifier(target.Schema) + "." + QuoteDesignerIdentifier(target.Name);
            }
            if (_db is my_oracle)
            {
                return QuoteDesignerIdentifier(_databaseName) + "." + QuoteDesignerIdentifier(tableName);
            }
            return QuoteDesignerIdentifier(tableName);
        }

        private string GetPostgreSqlQualifiedObjectName(string objectName)
        {
            PostgreSqlDesignerObjectName tableTarget = ParsePostgreSqlDesignerObjectName(_tableName);
            PostgreSqlDesignerObjectName objectTarget = ParsePostgreSqlDesignerObjectName(objectName);
            string schema = objectTarget.HasExplicitSchema ? objectTarget.Schema : tableTarget.Schema;
            return QuoteDesignerIdentifier(schema) + "." + QuoteDesignerIdentifier(objectTarget.Name);
        }

        private struct SqlServerDesignerObjectName
        {
            public string Schema;
            public string Name;
        }

        private struct PostgreSqlDesignerObjectName
        {
            public string Schema;
            public string Name;
            public bool HasExplicitSchema;
        }

        private static SqlServerDesignerObjectName ParseSqlServerDesignerObjectName(string objectName)
        {
            string value = (objectName ?? string.Empty).Trim();
            int dotIndex = value.IndexOf('.');
            if (dotIndex > 0 && dotIndex < value.Length - 1)
            {
                return new SqlServerDesignerObjectName
                {
                    Schema = value.Substring(0, dotIndex).Trim(),
                    Name = value.Substring(dotIndex + 1).Trim()
                };
            }

            return new SqlServerDesignerObjectName { Schema = "dbo", Name = value };
        }

        private static PostgreSqlDesignerObjectName ParsePostgreSqlDesignerObjectName(string objectName)
        {
            string value = (objectName ?? string.Empty).Trim();
            int dotIndex = value.IndexOf('.');
            if (dotIndex > 0 && dotIndex < value.Length - 1)
            {
                return new PostgreSqlDesignerObjectName
                {
                    Schema = value.Substring(0, dotIndex).Trim(),
                    Name = value.Substring(dotIndex + 1).Trim(),
                    HasExplicitSchema = true
                };
            }

            return new PostgreSqlDesignerObjectName
            {
                Schema = "public",
                Name = value,
                HasExplicitSchema = false
            };
        }

        private static string GetSqlServerDesignerObjectName(string objectName)
        {
            return ParseSqlServerDesignerObjectName(objectName).Name;
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
            
            if (!ContainsExecutableSql(sql))
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

        private void BtnExecuteSql_Click(object sender, EventArgs e)
        {
            if (rtbSqlPreview == null) return;

            if (tcMain != null && tpSqlPreview != null && tcMain.SelectedTab != tpSqlPreview)
            {
                GeneratePreviewSql();
                tcMain.SelectedTab = tpSqlPreview;
            }

            string sql = rtbSqlPreview.Text == null ? "" : rtbSqlPreview.Text.Trim();
            if (!ContainsExecutableSql(sql))
            {
                MessageBox.Show(Localization.T("Designer.NoSqlToExecute"), Localization.T("Designer.ExecuteSql"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (MessageBox.Show(Localization.Format("Designer.ConfirmExecuteSql", sql), Localization.T("Designer.ConfirmExecuteSqlTitle"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            Dictionary<string, string> res = ExecuteDesignerSql(sql);
            if (res.ContainsKey("status") && res["status"] == "OK")
            {
                MessageBox.Show(Localization.T("Designer.ExecuteSqlSucceeded"), Localization.T("Common.Success"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                if (IsNewTable)
                {
                    _tableName = GetTableNameForSave();
                    if (txtTableName != null) txtTableName.ReadOnly = true;
                }
                IsModified = false;
                LoadColumns();
                LoadIndexes();
                GeneratePreviewSql();
                UpdateTitle();
                return;
            }

            MessageBox.Show(FormatDesignerSaveError(_db?.ProviderName, _databaseName, GetTableNameForSave(), res), Localization.T("Designer.ExecuteSqlFailedTitle"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private static bool ContainsExecutableSql(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql)) return false;

            string[] lines = sql.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                if (trimmed.StartsWith("--", StringComparison.Ordinal)) continue;
                return true;
            }

            return false;
        }

        private string BuildSqlPreviewText(string sql)
        {
            if (_db is my_oracle)
            {
                return AddOraclePreviewNotice(sql, _databaseName, GetTableNameForSave());
            }

            return sql;
        }

        private static string AddOraclePreviewNotice(string sql, string databaseName, string tableName)
        {
            if (!ContainsExecutableSql(sql)) return sql;

            List<string> lines = new List<string>();
            lines.Add("-- " + Localization.T("Designer.OraclePreviewNoticeTitle"));
            foreach (string hint in GetOracleDesignerPreviewHints(databaseName, tableName))
            {
                lines.Add("-- - " + hint);
            }
            lines.Add("-- - " + Localization.T("Designer.OraclePreviewPrivilegeQueryHint"));
            foreach (string queryLine in BuildOraclePrivilegeDiagnosticSql(databaseName, tableName))
            {
                lines.Add("--   " + queryLine);
            }
            lines.Add("");
            lines.Add((sql ?? "").TrimStart());
            return string.Join("\r\n", lines.ToArray());
        }

        private static IEnumerable<string> GetOracleDesignerPreviewHints(string databaseName, string tableName)
        {
            string owner = string.IsNullOrWhiteSpace(databaseName) ? Localization.T("Designer.CurrentSchema") : databaseName;
            string objectName = string.IsNullOrWhiteSpace(tableName) ? Localization.T("Designer.CurrentTable") : tableName;
            yield return Localization.Format("Designer.OraclePreviewObjectHint", owner, objectName);
            yield return Localization.T("Designer.OraclePreviewPrivilegeHint");
            yield return Localization.T("Designer.OraclePreviewStepHint");
        }

        private static IEnumerable<string> BuildOraclePrivilegeDiagnosticSql(string databaseName, string tableName)
        {
            string owner = string.IsNullOrWhiteSpace(databaseName) ? "USER" : "UPPER(" + EscapeSqlStringLiteral(databaseName) + ")";
            string objectName = string.IsNullOrWhiteSpace(tableName) ? "UPPER(USER)" : "UPPER(" + EscapeSqlStringLiteral(tableName) + ")";
            yield return "SELECT owner, table_name, privilege, grantor";
            yield return "FROM all_tab_privs";
            yield return "WHERE UPPER(owner) = " + owner;
            yield return "  AND UPPER(table_name) = " + objectName;
            yield return "  AND privilege IN ('ALTER', 'INDEX', 'SELECT', 'INSERT', 'UPDATE', 'DELETE')";
            yield return "ORDER BY owner, table_name, privilege;";
            yield return "SELECT privilege";
            yield return "FROM session_privs";
            yield return "WHERE privilege IN ('ALTER ANY TABLE', 'CREATE ANY INDEX', 'CREATE ANY TABLE', 'COMMENT ANY TABLE', 'DROP ANY TABLE')";
            yield return "ORDER BY privilege;";
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
                if (!string.IsNullOrWhiteSpace(statement) && ContainsExecutableSql(statement)) statements.Add(statement);
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
            ApplyAutoColumnComment(newRow);
            
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
