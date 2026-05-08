using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Diagnostics;
using System.Linq;
using mySQLPunk.lib;

namespace mySQLPunk
{
    public partial class QueryForm : Form, IDockableForm
    {
        private IDatabase _db;
        private string _databaseName;
        private List<string> _tableNames = new List<string>();
        private CancellationTokenSource _cts;

        // UI 元件 (已現代化)
        private RichTextBox txtSql;
        private DataGridView dgvResults;
        private ListBox lstCompletion;
        private TabControl tabResults;
        private SplitContainer split;
        private Form1 _mainHost;
        private bool _isDocked;
        private bool _isTableDataMode; 

        // 分頁相關
        private int _pageSize = 1000;
        private int _currentPage = 1;
        private long _totalRows = 0;
        private string _baseSql = ""; // 不含 LIMIT 的原始 SQL

        private class TableColumnInfo
        {
            public string Name { get; set; }
            public bool IsPrimaryKey { get; set; }
            public bool IsAutoIncrement { get; set; }
        }

        private class DataSaveResult
        {
            public int Inserted { get; set; }
            public int Updated { get; set; }
            public int Deleted { get; set; }
        }

        // 工具列與狀態列
        private ToolStrip mainToolStrip;
        private ToolStrip dataToolStrip;
        private MenuStrip mainMenuStrip; // 懸浮時顯示的選單
        private StatusStrip statusStrip;
        private ToolStripStatusLabel lblStatus;
        private ToolStripStatusLabel lblSqlPreview;
        
        // 頂部按鈕
        private ToolStripButton tsBtnExecute;
        private ToolStripButton tsBtnCancel;
        private ToolStripButton tsBtnBeautify;
        private ToolStripButton tsBtnSave;
        private ToolStripButton tsBtnAdd;
        private ToolStripButton tsBtnDelete;
        private ToolStripButton tsBtnRefresh;
        private ToolStripButton tsBtnExport;
        private ToolStripButton tsBtnFloat;
        private ToolStripButton tsBtnDock;

        // 底部資料列操作按鈕
        private ToolStripButton btnDataAdd;
        private ToolStripButton btnDataDelete;
        private ToolStripButton btnDataApply;
        private ToolStripButton btnDataCancel;
        private ToolStripButton btnDataRefresh;
        
        private ToolStripLabel lblDataPagination;
        private ToolStripTextBox txtPageSize;
        private ToolStripButton btnDataFirst;
        private ToolStripButton btnDataPrev;
        private ToolStripButton btnDataNext;
        private ToolStripButton btnDataLast;
        private ToolStripMenuItem menuFile;
        private ToolStripMenuItem menuEdit;
        private ToolStripMenuItem menuView;
        private ToolStripMenuItem menuWindow;
        private ToolStripMenuItem menuHelp;
        private ToolStripMenuItem menuFileExecute;
        private ToolStripMenuItem menuFileOpenSql;
        private ToolStripMenuItem menuFileSaveSql;
        private ToolStripMenuItem menuFileExport;
        private ToolStripMenuItem menuFileClose;
        private ToolStripMenuItem menuEditCut;
        private ToolStripMenuItem menuEditCopy;
        private ToolStripMenuItem menuEditPaste;
        private ToolStripMenuItem menuEditSelectAll;
        private ToolStripMenuItem menuEditBeautify;
        private ToolStripMenuItem menuViewSqlEditor;
        private ToolStripMenuItem menuViewResults;
        private ToolStripMenuItem menuWindowFloat;
        private ToolStripMenuItem menuWindowDock;
        private ToolStripMenuItem menuWindowClose;
        private ToolStripMenuItem menuHelpAbout;
        private ContextMenuStrip resultsContextMenu;
        private ToolStripMenuItem resultsCopyCellsItem;
        private ToolStripMenuItem resultsCopyHeadersItem;
        private ToolStripMenuItem resultsCopyRowsItem;
        private ToolStripMenuItem resultsSelectAllItem;
        private ToolStripMenuItem resultsExportItem;
        private ToolStripSeparator resultsEditSeparator;
        private ToolStripMenuItem resultsAddRowItem;
        private ToolStripMenuItem resultsDeleteRowItem;
        private ToolStripMenuItem resultsSaveRowsItem;
        private bool _isClosing;

        private static readonly string[] Keywords =
        {
            "SELECT", "FROM", "WHERE", "INSERT", "INTO", "UPDATE", "DELETE",
            "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "CROSS", "ON",
            "GROUP", "BY", "ORDER", "HAVING", "LIMIT", "OFFSET",
            "AND", "OR", "NOT", "IN", "IS", "NULL", "AS", "DISTINCT",
            "COUNT", "SUM", "AVG", "MAX", "MIN", "CASE", "WHEN", "THEN",
            "ELSE", "END", "CREATE", "DROP", "ALTER", "TABLE", "INDEX",
            "DESC", "ASC", "SET", "VALUES", "UNION", "ALL", "EXISTS"
        };

        private static readonly string[] BreakKeywords =
        {
            "SELECT", "FROM", "WHERE", "GROUP BY", "ORDER BY",
            "HAVING", "LIMIT", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN",
            "JOIN", "SET", "VALUES", "UNION"
        };

        private readonly string currentDatabase;
        private readonly string connectionHost;

        public QueryForm(IDatabase db, string dbName)
            : this(db, dbName, string.Empty, string.Empty)
        {
        }

        public QueryForm(IDatabase db, string dbName, string host)
            : this(db, dbName, host, string.Empty)
        {
        }

        public QueryForm(IDatabase db, string dbName, string host, string initialSql)
        {
            InitializeQueryForm();
            this._db = db;
            this._databaseName = dbName ?? string.Empty;
            this.currentDatabase = dbName ?? string.Empty;
            this.connectionHost = host ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(this.currentDatabase) || !string.IsNullOrWhiteSpace(this.connectionHost))
            {
                string title = Localization.T("Query.Query");
                if (!string.IsNullOrWhiteSpace(this.connectionHost))
                {
                    title += " - " + this.connectionHost;
                }
                if (!string.IsNullOrWhiteSpace(this.currentDatabase))
                {
                    title += " / " + this.currentDatabase;
                }
                this.Text = title;
            }

            if (!string.IsNullOrWhiteSpace(initialSql))
            {
                txtSql.Text = initialSql;
                txtSql.SelectionStart = txtSql.TextLength;
                txtSql.SelectionLength = 0;
            }

            LoadTableNames();
            if (initialSql.ToUpper().Trim().StartsWith("SELECT * FROM"))
            {
                _baseSql = initialSql.Split(';')[0].Split(new string[] { "LIMIT", "limit" }, StringSplitOptions.None)[0].Trim();
                SetTableDataMode(true);
                ExecutePagedQuery(); // 立即載入第一頁資料
            }
            ApplyLanguage();
        }

        private async void CalculateTotalRowsAsync()
        {
            if (string.IsNullOrEmpty(_baseSql)) return;

            string tableName = GetTableNameFromSql();
            if (!string.IsNullOrWhiteSpace(tableName) && tableName != "Table")
            {
                try
                {
                    _totalRows = await Task.Run(() => _db.CountRows(_databaseName, tableName));
                    if (!CanUpdateUi()) return;
                    UpdatePaginationUI();
                }
                catch (Exception ex)
                {
                    if (!CanUpdateUi()) return;
                    UpdateStatus("Count rows failed: " + ex.Message);
                    UpdatePaginationUI();
                }
            }
        }

        private void SetTableDataMode(bool active)
        {
            _isTableDataMode = active;
            
            // 動態切換按鈕可見度
            tsBtnSave.Visible = active;
            tsBtnAdd.Visible = active;
            tsBtnDelete.Visible = active;
            tsBtnRefresh.Visible = active;
            
            tsBtnExecute.Visible = !active;
            tsBtnBeautify.Visible = !active;
            RefreshResultsContextMenu();

            if (active)
            {
                if (split != null) split.Panel1Collapsed = true;
                this.Text = $"{_databaseName}.{GetTableNameFromSql()} - {Localization.T("Query.TableData")}";
                
                dgvResults.ReadOnly = false;
                dgvResults.AllowUserToAddRows = true;
                dgvResults.AllowUserToDeleteRows = true;

                // 立即計算總筆數以初始化分頁 UI
                CalculateTotalRowsAsync();
            }
            else
            {
                if (split != null) split.Panel1Collapsed = false;
            }
        }

        private string GetTableNameFromSql()
        {
            if (string.IsNullOrEmpty(_baseSql)) return "Table";
            string pattern = @"FROM\s+([`\[\]\w\.\x22]+)";
            Match match = Regex.Match(_baseSql, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                string path = match.Groups[1].Value.Replace("`", "").Replace("\"", "").Replace("[", "").Replace("]", "");
                return path.Contains(".") ? path.Split('.').Last() : path;
            }
            return "Table";
        }

        private void InitializeQueryForm()
        {
            Size = new Size(1100, 750);
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(600, 400);

            // ── 頂部選單 (平時隱藏) ──
            mainMenuStrip = new MenuStrip { Visible = false };
            BuildMainMenu();

            // ── 頂部專業工具列 (ToolStrip) ──
            mainToolStrip = new ToolStrip { 
                ImageScalingSize = new Size(24, 24), 
                Padding = new Padding(0, 5, 0, 5),
                GripStyle = ToolStripGripStyle.Hidden,
                BackColor = Color.White
            };
            
            tsBtnExecute = new ToolStripButton(Localization.T("Query.Execute"), null, (s, e) => ExecutePagedQuery()) { DisplayStyle = ToolStripItemDisplayStyle.ImageAndText };
            tsBtnCancel = new ToolStripButton(Localization.T("Query.Stop"), null, (s, e) => CancelQuery()) { DisplayStyle = ToolStripItemDisplayStyle.ImageAndText, Enabled = false };
            tsBtnBeautify = new ToolStripButton(Localization.T("Query.Beautify"), null, (s, e) => BeautifySql()) { DisplayStyle = ToolStripItemDisplayStyle.ImageAndText };
            
            tsBtnSave = new ToolStripButton(Localization.T("Query.Save"), null, (s, e) => SaveChanges()) { DisplayStyle = ToolStripItemDisplayStyle.ImageAndText, Visible = false };
            tsBtnAdd = new ToolStripButton(Localization.T("Query.Add"), null, (s, e) => AddNewRow()) { DisplayStyle = ToolStripItemDisplayStyle.ImageAndText, Visible = false };
            tsBtnDelete = new ToolStripButton(Localization.T("Query.Delete"), null, (s, e) => DeleteSelectedRows()) { DisplayStyle = ToolStripItemDisplayStyle.ImageAndText, Visible = false };
            tsBtnRefresh = new ToolStripButton(Localization.T("Query.Refresh"), null, (s, e) => ExecutePagedQuery()) { DisplayStyle = ToolStripItemDisplayStyle.ImageAndText, Visible = false };
            
            tsBtnExport = new ToolStripButton(Localization.T("Query.Export"), null, (s, e) => ExportCsv()) { DisplayStyle = ToolStripItemDisplayStyle.ImageAndText };
            tsBtnFloat = new ToolStripButton(Localization.T("Query.Float"), null, (s, e) => FloatToWindow()) { DisplayStyle = ToolStripItemDisplayStyle.Image };
            tsBtnDock = new ToolStripButton(Localization.T("Query.Dock"), null, (s, e) => DockToMainWindow()) { DisplayStyle = ToolStripItemDisplayStyle.Image, Visible = false };

            mainToolStrip.Items.AddRange(new ToolStripItem[] { 
                tsBtnExecute, tsBtnCancel, tsBtnBeautify, 
                new ToolStripSeparator(), 
                tsBtnSave, tsBtnAdd, tsBtnDelete, tsBtnRefresh, 
                new ToolStripSeparator(), 
                tsBtnExport, 
                new ToolStripSeparator(), 
                tsBtnFloat, tsBtnDock 
            });

            // 支援拖拽回巢邏輯
            mainToolStrip.MouseDown += (s, e) => {
                if (!_isDocked && e.Button == MouseButtons.Left)
                {
                    // 記錄起始位置，避免輕微晃動誤觸
                    mainToolStrip.Tag = e.Location;
                }
            };
            mainToolStrip.MouseMove += (s, e) => {
                if (!_isDocked && e.Button == MouseButtons.Left && mainToolStrip.Tag != null)
                {
                    Point startPos = (Point)mainToolStrip.Tag;
                    if (Math.Abs(e.X - startPos.X) > 5 || Math.Abs(e.Y - startPos.Y) > 5)
                    {
                        mainToolStrip.Tag = null; // 清除標記
                        mainToolStrip.DoDragDrop(this, DragDropEffects.Move);
                    }
                }
            };

            // ── 資料底端工具列 (dataToolStrip) ──
            dataToolStrip = new ToolStrip { 
                Dock = DockStyle.Bottom, 
                GripStyle = ToolStripGripStyle.Hidden,
                BackColor = Color.FromArgb(242, 242, 242),
                Height = 35 // 增加高度
            };

            btnDataAdd = new ToolStripButton("+", null, (s, e) => AddNewRow()) { Font = new Font("Segoe UI", 12, FontStyle.Bold) };
            btnDataDelete = new ToolStripButton("-", null, (s, e) => DeleteSelectedRows()) { Font = new Font("Segoe UI", 12, FontStyle.Bold) };
            btnDataApply = new ToolStripButton("✓", null, (s, e) => SaveChanges()) { ForeColor = Color.Green, Font = new Font("Segoe UI", 12, FontStyle.Bold) };
            btnDataCancel = new ToolStripButton("X", null, (s, e) => ExecutePagedQuery()) { ForeColor = Color.Red, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
            btnDataRefresh = new ToolStripButton("↻", null, (s, e) => ExecutePagedQuery()) { Font = new Font("Segoe UI", 12, FontStyle.Bold) };

            btnDataFirst = new ToolStripButton("|<", null, (s, e) => { _currentPage = 1; ExecutePagedQuery(); });
            btnDataPrev = new ToolStripButton("<", null, (s, e) => { if (_currentPage > 1) { _currentPage--; ExecutePagedQuery(); } });
            btnDataNext = new ToolStripButton(">", null, (s, e) => { if (_currentPage < GetTotalPages()) { _currentPage++; ExecutePagedQuery(); } });
            btnDataLast = new ToolStripButton(">|", null, (s, e) => { _currentPage = GetTotalPages(); ExecutePagedQuery(); });

            ToolStripLabel lblLimit = new ToolStripLabel(Localization.T("Query.Limit")) { Margin = new Padding(10, 0, 0, 0) };
            txtPageSize = new ToolStripTextBox { Text = _pageSize.ToString(), Width = 50, TextBoxTextAlign = HorizontalAlignment.Center };
            txtPageSize.Leave += (s, e) => ApplyPageSizeFromInput();
            txtPageSize.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    ApplyPageSizeFromInput();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };
            ToolStripLabel lblRecords = new ToolStripLabel(Localization.T("Query.Records")) { Margin = new Padding(0, 0, 20, 0) };

            btnDataFirst = new ToolStripButton("|<", null, (s, e) => { _currentPage = 1; ExecutePagedQuery(); }) { Alignment = ToolStripItemAlignment.Right };
            btnDataPrev = new ToolStripButton("<", null, (s, e) => { if (_currentPage > 1) { _currentPage--; ExecutePagedQuery(); } }) { Alignment = ToolStripItemAlignment.Right };
            lblDataPagination = new ToolStripLabel(Localization.Format("Query.PageFormat", 1, 1, 0)) { Alignment = ToolStripItemAlignment.Right, Margin = new Padding(10, 0, 10, 0) };
            btnDataNext = new ToolStripButton(">", null, (s, e) => { if (_currentPage < GetTotalPages()) { _currentPage++; ExecutePagedQuery(); } }) { Alignment = ToolStripItemAlignment.Right };
            btnDataLast = new ToolStripButton(">|", null, (s, e) => { _currentPage = GetTotalPages(); ExecutePagedQuery(); }) { Alignment = ToolStripItemAlignment.Right };

            dataToolStrip.Items.AddRange(new ToolStripItem[] {
                btnDataAdd, btnDataDelete, btnDataApply, btnDataCancel, btnDataRefresh,
                lblLimit, txtPageSize, lblRecords,
                btnDataLast, btnDataNext, lblDataPagination, btnDataPrev, btnDataFirst
            });
            btnDataNext.Alignment = ToolStripItemAlignment.Right;
            lblDataPagination.Alignment = ToolStripItemAlignment.Right;
            btnDataPrev.Alignment = ToolStripItemAlignment.Right;
            btnDataFirst.Alignment = ToolStripItemAlignment.Right;

            // ── 分割容器 ──
            split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 220
            };

            // ── SQL 編輯區 ──
            txtSql = new RichTextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 11),
                AcceptsTab = true,
                WordWrap = false,
                ScrollBars = RichTextBoxScrollBars.Both,
                Text = "SELECT * FROM "
            };
            txtSql.KeyDown += TxtSql_KeyDown;
            txtSql.TextChanged += TxtSql_TextChanged;

            // 自動補完清單
            lstCompletion = new ListBox
            {
                Visible = false,
                Width = 180,
                Height = 120,
                IntegralHeight = false
            };
            lstCompletion.DoubleClick += (s, e) => ApplyCompletion();
            lstCompletion.KeyDown += LstCompletion_KeyDown;

            split.Panel1.Controls.Add(lstCompletion);
            split.Panel1.Controls.Add(txtSql);

            // ── 結果區 (TabControl) ──
            tabResults = new TabControl { Dock = DockStyle.Fill };

            TabPage tabData = new TabPage(Localization.T("Query.Results"));
            dgvResults = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackgroundColor = Color.White,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
                ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText
            };
            dgvResults.CellMouseDown += DgvResults_CellMouseDown;
            RefreshResultsContextMenu();
            tabData.Controls.Add(dgvResults);
            tabResults.TabPages.Add(tabData);

            split.Panel2.Controls.Add(tabResults);

            // ── 狀態列 ──
            statusStrip = new StatusStrip { BackColor = Color.White };
            lblStatus = new ToolStripStatusLabel(Localization.T("Status.Ready"));
            lblSqlPreview = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.Gray };
            statusStrip.Items.AddRange(new ToolStripItem[] { lblStatus, lblSqlPreview });

            this.Controls.Add(split);
            this.Controls.Add(mainToolStrip);
            this.Controls.Add(mainMenuStrip); // 加入選單
            this.Controls.Add(statusStrip);
            
            // 將 dataToolStrip 放到 split.Panel2 的底部 (跟著表格走)
            split.Panel2.Controls.Add(dataToolStrip);
            ApplyTheme();
        }

        private void BuildMainMenu()
        {
            menuFile = new ToolStripMenuItem();
            menuEdit = new ToolStripMenuItem();
            menuView = new ToolStripMenuItem();
            menuWindow = new ToolStripMenuItem();
            menuHelp = new ToolStripMenuItem();

            menuFileExecute = new ToolStripMenuItem(null, null, (s, e) => ExecutePagedQuery()) { ShortcutKeys = Keys.F5 };
            menuFileOpenSql = new ToolStripMenuItem(null, null, (s, e) => OpenSqlWithDialog()) { ShortcutKeys = Keys.Control | Keys.O };
            menuFileSaveSql = new ToolStripMenuItem(null, null, (s, e) => SaveSqlWithDialog()) { ShortcutKeys = Keys.Control | Keys.S };
            menuFileExport = new ToolStripMenuItem(null, null, (s, e) => ExportCsv());
            menuFileClose = new ToolStripMenuItem(null, null, (s, e) => Close());

            menuEditCut = new ToolStripMenuItem(null, null, (s, e) => txtSql.Cut()) { ShortcutKeys = Keys.Control | Keys.X };
            menuEditCopy = new ToolStripMenuItem(null, null, (s, e) => CopyEditorSelection()) { ShortcutKeys = Keys.Control | Keys.C };
            menuEditPaste = new ToolStripMenuItem(null, null, (s, e) => txtSql.Paste()) { ShortcutKeys = Keys.Control | Keys.V };
            menuEditSelectAll = new ToolStripMenuItem(null, null, (s, e) => txtSql.SelectAll()) { ShortcutKeys = Keys.Control | Keys.A };
            menuEditBeautify = new ToolStripMenuItem(null, null, (s, e) => BeautifySql());

            menuViewSqlEditor = new ToolStripMenuItem(null, null, (s, e) => FocusSqlEditor());
            menuViewResults = new ToolStripMenuItem(null, null, (s, e) => FocusResultsGrid());

            menuWindowFloat = new ToolStripMenuItem(null, null, (s, e) => FloatToWindow());
            menuWindowDock = new ToolStripMenuItem(null, null, (s, e) => DockToMainWindow());
            menuWindowClose = new ToolStripMenuItem(null, null, (s, e) => Close());

            menuHelpAbout = new ToolStripMenuItem(null, null, (s, e) => ShowQueryAbout());

            menuFile.DropDownItems.AddRange(new ToolStripItem[]
            {
                menuFileExecute,
                new ToolStripSeparator(),
                menuFileOpenSql,
                menuFileSaveSql,
                new ToolStripSeparator(),
                menuFileExport,
                new ToolStripSeparator(),
                menuFileClose
            });
            menuEdit.DropDownItems.AddRange(new ToolStripItem[]
            {
                menuEditCut,
                menuEditCopy,
                menuEditPaste,
                new ToolStripSeparator(),
                menuEditSelectAll,
                new ToolStripSeparator(),
                menuEditBeautify
            });
            menuView.DropDownItems.AddRange(new ToolStripItem[] { menuViewSqlEditor, menuViewResults });
            menuWindow.DropDownItems.AddRange(new ToolStripItem[] { menuWindowFloat, menuWindowDock, new ToolStripSeparator(), menuWindowClose });
            menuHelp.DropDownItems.Add(menuHelpAbout);
            mainMenuStrip.Items.AddRange(new ToolStripItem[] { menuFile, menuEdit, menuView, menuWindow, menuHelp });
            ApplyMenuLanguage();
        }

        public void SetMainHost(Form1 mainHost)
        {
            _mainHost = mainHost;
        }

        public string GetDisplayTitle()
        {
            return Text;
        }

        public string DatabaseName => _databaseName ?? string.Empty;

        public string ConnectionHost => connectionHost ?? string.Empty;

        public string CurrentSql => txtSql == null ? string.Empty : txtSql.Text;

        public string CurrentStatus => lblStatus == null ? string.Empty : lblStatus.Text;

        public void PrepareForDocking()
        {
            if (Visible)
            {
                Hide();
            }

            if (Parent != null)
            {
                Parent.Controls.Remove(this);
            }

            FormBorderStyle = FormBorderStyle.None;
            TopLevel = false;
            TopMost = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            _isDocked = true;
            tsBtnFloat.Visible = true;
            tsBtnDock.Visible = false;
            mainMenuStrip.Visible = false;
            statusStrip.Visible = false; // 嵌入時隱藏自己的狀態列
        }

        public void PrepareForFloating()
        {
            if (Visible)
            {
                Hide();
            }

            if (Parent != null)
            {
                Parent.Controls.Remove(this);
            }

            Dock = DockStyle.None;
            TopLevel = true;
            FormBorderStyle = FormBorderStyle.Sizable;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterParent;
            _isDocked = false;
            tsBtnFloat.Visible = false;
            tsBtnDock.Visible = _mainHost != null;
            mainMenuStrip.Visible = true;
            statusStrip.Visible = true; // 懸浮時顯示自己的狀態列供 Resize
        }

        private void FloatToWindow()
        {
            if (_mainHost == null) return;
            _mainHost.FloatDockableForm(this);
        }

        private void DockToMainWindow()
        {
            if (_mainHost == null) return;
            _mainHost.DockDockableForm(this);
        }

        private void CopyEditorSelection()
        {
            if (txtSql.SelectionLength > 0)
            {
                txtSql.Copy();
            }
        }

        private void FocusSqlEditor()
        {
            if (split != null && _isTableDataMode)
            {
                split.Panel1Collapsed = false;
            }
            txtSql.Select();
            txtSql.Focus();
        }

        private void FocusResultsGrid()
        {
            if (tabResults.TabPages.Count > 0)
            {
                tabResults.SelectedTab = tabResults.TabPages[0];
            }
            tabResults.Focus();
            dgvResults.Select();
            dgvResults.Focus();
        }

        private void SaveSqlWithDialog()
        {
            using (SaveFileDialog dialog = new SaveFileDialog
            {
                Filter = "SQL files (*.sql)|*.sql|All files (*.*)|*.*",
                DefaultExt = "sql",
                FileName = string.IsNullOrWhiteSpace(_databaseName) ? "query.sql" : _databaseName + ".sql"
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                File.WriteAllText(dialog.FileName, txtSql.Text, Encoding.UTF8);
                UpdateStatus("SQL saved: " + dialog.FileName);
            }
        }

        private void OpenSqlWithDialog()
        {
            using (OpenFileDialog dialog = new OpenFileDialog
            {
                Filter = "SQL files (*.sql)|*.sql|All files (*.*)|*.*",
                CheckFileExists = true
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                txtSql.Text = File.ReadAllText(dialog.FileName, Encoding.UTF8);
                txtSql.SelectionStart = txtSql.TextLength;
                txtSql.SelectionLength = 0;
                UpdateStatus("SQL opened: " + dialog.FileName);
            }
        }

        private void ShowQueryAbout()
        {
            MessageBox.Show(
                "mySQLPunk Query\r\n\r\n" +
                "Database: " + (_databaseName ?? string.Empty) + "\r\n" +
                "Provider: " + (_db == null ? string.Empty : _db.ProviderName),
                Localization.T("Query.About"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        // 攔截 OS 層級視窗移動，提供 dock 提示框並在放開時自動塞回 tab
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

        // ── 載入資料表名稱供自動補完 ──
        private void LoadTableNames()
        {
            try
            {
                if (!string.IsNullOrEmpty(_databaseName) && _db != null)
                {
                    _tableNames = _db.GetTables(_databaseName);
                }
            }
            catch
            {
                // 取得失敗時靜默忽略，不影響主要功能
            }
        }

        // ── SQL 語法高亮 ──
        private void ApplySyntaxHighlight()
        {
            txtSql.TextChanged -= TxtSql_TextChanged;

            int selStart = txtSql.SelectionStart;
            int selLen = txtSql.SelectionLength;

            txtSql.SuspendLayout();

            // 先全部重置為黑色
            txtSql.SelectAll();
            txtSql.SelectionColor = Color.Black;
            txtSql.SelectionFont = new Font(txtSql.Font, FontStyle.Regular);

            string text = txtSql.Text;
            string upper = text.ToUpperInvariant();

            // 上色關鍵字 (藍色粗體)
            foreach (string kw in Keywords)
            {
                int idx = 0;
                while ((idx = upper.IndexOf(kw, idx)) != -1)
                {
                    // 確認是完整單字邊界
                    bool leftOk = idx == 0 || !char.IsLetterOrDigit(text[idx - 1]) && text[idx - 1] != '_';
                    bool rightOk = idx + kw.Length >= text.Length
                        || (!char.IsLetterOrDigit(text[idx + kw.Length]) && text[idx + kw.Length] != '_');

                    if (leftOk && rightOk)
                    {
                        txtSql.Select(idx, kw.Length);
                        txtSql.SelectionColor = Color.FromArgb(0, 0, 205);
                        txtSql.SelectionFont = new Font(txtSql.Font, FontStyle.Bold);
                    }
                    idx += kw.Length;
                }
            }

            // 字串常值 (橙色)
            HighlightPattern(text, @"'[^']*'", Color.DarkOrange, FontStyle.Regular);

            // 單行注釋 (綠色)
            HighlightPattern(text, @"--[^\r\n]*", Color.DarkGreen, FontStyle.Italic);

            // 還原游標
            txtSql.Select(selStart, selLen);
            txtSql.SelectionColor = Color.Black;
            txtSql.SelectionFont = new Font(txtSql.Font, FontStyle.Regular);

            txtSql.ResumeLayout();
            txtSql.TextChanged += TxtSql_TextChanged;
        }

        private void HighlightPattern(string text, string pattern, Color color, FontStyle style)
        {
            foreach (Match m in Regex.Matches(text, pattern))
            {
                txtSql.Select(m.Index, m.Length);
                txtSql.SelectionColor = color;
                txtSql.SelectionFont = new Font(txtSql.Font, style);
            }
        }

        // ── Beautify SQL ──
        private void BeautifySql()
        {
            string sql = txtSql.Text;
            if (string.IsNullOrEmpty(sql)) return;

            // 關鍵字大寫
            foreach (string kw in Keywords)
            {
                sql = Regex.Replace(sql, @"\b" + kw + @"\b", kw, RegexOptions.IgnoreCase);
            }

            // 主要子句換行
            foreach (string bw in BreakKeywords)
            {
                sql = Regex.Replace(sql, @"\b" + Regex.Escape(bw) + @"\b", "\n" + bw, RegexOptions.IgnoreCase);
            }

            // AND / OR 縮排換行
            sql = Regex.Replace(sql, @"\bAND\b", "\n  AND", RegexOptions.IgnoreCase);
            sql = Regex.Replace(sql, @"\bOR\b", "\n  OR", RegexOptions.IgnoreCase);

            // 清理多餘空行
            sql = Regex.Replace(sql, @"\n\s*\n", "\n");

            txtSql.Text = sql.Trim();
        }

        // ── 自動補完 ──
        private void ShowCompletion()
        {
            int pos = txtSql.SelectionStart;
            if (pos <= 0) { lstCompletion.Visible = false; return; }

            int start = txtSql.Text.LastIndexOfAny(new[] { ' ', '\n', '\r', '\t' }, pos - 1) + 1;
            string word = txtSql.Text.Substring(start, pos - start).ToUpperInvariant();

            if (word.Length < 2) { lstCompletion.Visible = false; return; }

            var allWords = new List<string>(Keywords);
            allWords.AddRange(_tableNames);
            allWords.Sort(StringComparer.OrdinalIgnoreCase);

            var matches = allWords.FindAll(k => k.ToUpperInvariant().StartsWith(word));

            if (matches.Count > 0)
            {
                lstCompletion.Items.Clear();
                foreach (var m in matches) lstCompletion.Items.Add(m);

                Point p = txtSql.GetPositionFromCharIndex(start);
                lstCompletion.Location = new Point(p.X, p.Y + txtSql.Font.Height + 4);
                lstCompletion.Visible = true;
                lstCompletion.BringToFront();
                lstCompletion.SelectedIndex = 0;
            }
            else
            {
                lstCompletion.Visible = false;
            }
        }

        private void ApplyCompletion()
        {
            if (lstCompletion.SelectedItem == null) return;

            string selected = lstCompletion.SelectedItem.ToString();
            int pos = txtSql.SelectionStart;
            int start = txtSql.Text.LastIndexOfAny(new[] { ' ', '\n', '\r', '\t' }, pos - 1) + 1;

            txtSql.Select(start, pos - start);
            txtSql.SelectedText = selected + " ";
            lstCompletion.Visible = false;
            txtSql.Focus();
        }

        // ── 取得要執行的 SQL 語句 ──
        private string GetSqlToExecute(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
            {
                return sql;
            }

            if (string.IsNullOrWhiteSpace(currentDatabase))
            {
                return sql;
            }

            if (_db is my_mysql)
            {
                return "USE `" + currentDatabase.Replace("`", "``") + "`;\r\n" + sql;
            }

            return sql;
        }

        // ── 事件處理 ──
        private void TxtSql_TextChanged(object sender, EventArgs e)
        {
            ApplySyntaxHighlight();
            ShowCompletion();
        }

        private void TxtSql_KeyDown(object sender, KeyEventArgs e)
        {
            if (lstCompletion.Visible)
            {
                if (e.KeyCode == Keys.Down)
                {
                    lstCompletion.Focus();
                    e.Handled = true;
                    return;
                }
                if (e.KeyCode == Keys.Tab || e.KeyCode == Keys.Enter)
                {
                    ApplyCompletion();
                    e.Handled = true;
                    return;
                }
                if (e.KeyCode == Keys.Escape)
                {
                    lstCompletion.Visible = false;
                    e.Handled = true;
                    return;
                }
            }

            // F5 或 Ctrl+Enter 執行
            if (e.KeyCode == Keys.F5 || (e.Control && e.KeyCode == Keys.Return))
            {
                ExecuteQueryAsync();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private void LstCompletion_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Tab)
            {
                ApplyCompletion();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                lstCompletion.Visible = false;
                txtSql.Focus();
            }
        }

        // ── 取消查詢 ──
        private void CancelQuery()
        {
            _cts?.Cancel();
        }

        // ── 執行查詢 ──
        private void ExecutePagedQuery()
        {
            if (!_isTableDataMode)
            {
                ExecuteQueryAsync();
                return;
            }

            EnsureValidPageSize();
            int totalPages = GetTotalPages();
            if (_currentPage < 1) _currentPage = 1;
            if (_currentPage > totalPages) _currentPage = totalPages;

            int offset = (_currentPage - 1) * _pageSize;
            ExecuteTablePageAsync(offset);
        }

        private async void ExecuteTablePageAsync(int offset)
        {
            string tableName = GetTableNameFromSql();
            if (string.IsNullOrWhiteSpace(tableName) || tableName == "Table")
            {
                ExecuteQueryAsync();
                return;
            }

            _cts = new CancellationTokenSource();
            tsBtnExecute.Enabled = false;
            tsBtnCancel.Enabled = true;
            tsBtnExport.Enabled = false;
            tsBtnRefresh.Enabled = false;
            btnDataRefresh.Enabled = false;
            UpdateStatus("Loading table page...");

            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                DataTable dt = await Task.Run(
                    () => _db.SelectTablePage(_databaseName, tableName, offset, _pageSize),
                    _cts.Token);
                if (!CanUpdateUi()) return;
                dt.AcceptChanges();
                PrepareTableDataForEditing(dt);

                sw.Stop();
                dgvResults.DataSource = dt;
                AutoResizeColumns(dgvResults);
                tsBtnExport.Enabled = dt.Rows.Count > 0;
                UpdateStatus(string.Format(
                    "OK  |  {0} rows  |  {1} ms",
                    dt.Rows.Count, sw.ElapsedMilliseconds));
                UpdatePaginationUI();
            }
            catch (OperationCanceledException)
            {
                if (!CanUpdateUi()) return;
                UpdateStatus("Cancelled.");
            }
            catch (Exception ex)
            {
                if (!CanUpdateUi()) return;
                UpdateStatus("Load failed: " + ex.Message);
                MessageBox.Show("載入資料表失敗：" + ex.Message, "錯誤", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (CanUpdateUi())
                {
                    tsBtnExecute.Enabled = true;
                    tsBtnCancel.Enabled = false;
                    tsBtnRefresh.Enabled = true;
                    btnDataRefresh.Enabled = true;
                }

                CancellationTokenSource cts = _cts;
                _cts = null;
                cts?.Dispose();
            }
        }

         private void UpdatePaginationUI()
        {
            EnsureValidPageSize();
            int totalPages = GetTotalPages();
            lblDataPagination.Text = Localization.Format("Query.PageFormat", _currentPage, totalPages, _totalRows);
            btnDataFirst.Enabled = _currentPage > 1;
            btnDataPrev.Enabled = _currentPage > 1;
            btnDataNext.Enabled = _currentPage < totalPages;
            btnDataLast.Enabled = _currentPage < totalPages;
        }

        private int GetTotalPages()
        {
            EnsureValidPageSize();
            if (_totalRows <= 0) return 1;
            return (int)Math.Ceiling((double)_totalRows / _pageSize);
        }

        private void ApplyPageSizeFromInput()
        {
            int parsed;
            if (txtPageSize == null || !int.TryParse(txtPageSize.Text, out parsed) || parsed <= 0)
            {
                EnsureValidPageSize();
                if (txtPageSize != null) txtPageSize.Text = _pageSize.ToString();
                UpdateStatus("Page size must be greater than 0.");
                return;
            }

            _pageSize = parsed;
            _currentPage = 1;
            if (txtPageSize.Text != _pageSize.ToString()) txtPageSize.Text = _pageSize.ToString();
            if (_isTableDataMode)
            {
                ExecutePagedQuery();
            }
            else
            {
                UpdatePaginationUI();
            }
        }

        private void EnsureValidPageSize()
        {
            if (_pageSize <= 0) _pageSize = 1000;
        }

        private async void ExecuteQueryAsync()
        {
            // 優先執行選取文字，否則全文
            string rawSql = txtSql.SelectionLength > 0
                ? txtSql.SelectedText.Trim()
                : txtSql.Text.Trim();

            if (string.IsNullOrEmpty(rawSql)) return;

            string sql = GetSqlToExecute(rawSql);
            lblSqlPreview.Text = sql; // 更新底部的 SQL 預覽

            _cts = new CancellationTokenSource();
            tsBtnExecute.Enabled = false;
            tsBtnCancel.Enabled = true;
            tsBtnExport.Enabled = false;
            tsBtnRefresh.Enabled = false;
            btnDataRefresh.Enabled = false;
            UpdateStatus("Executing...");

            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                // 判斷是否為 SELECT/SHOW/EXPLAIN/DESC (顯示結果集) 或 DML (顯示影響行數)
                string firstWord = GetFirstWord(rawSql);
                bool isQuery = IsSelectStatement(firstWord);

                if (isQuery)
                {
                    DataTable dt = await Task.Run(
                        () => _db.SelectSQL(sql),
                        _cts.Token);
                    if (!CanUpdateUi()) return;
                    if (_isTableDataMode)
                    {
                        dt.AcceptChanges();
                        PrepareTableDataForEditing(dt);
                    }

                    sw.Stop();
                    dgvResults.DataSource = dt;
                    AutoResizeColumns(dgvResults);
                    tsBtnExport.Enabled = dt.Rows.Count > 0;
                    string status = string.Format(
                        "OK  |  {0} rows  |  {1} ms",
                        dt.Rows.Count, sw.ElapsedMilliseconds);
                    UpdateStatus(status);
                    _mainHost?.RecordQueryHistory(_databaseName, sql, status, sw.ElapsedMilliseconds, dt.Rows.Count, true);
                }
                else
                {
                    var result = await Task.Run(
                        () => _db.ExecSQL(sql),
                        _cts.Token);
                    if (!CanUpdateUi()) return;

                    sw.Stop();
                    dgvResults.DataSource = null;

                    if (result["status"] == "OK")
                    {
                        string status = string.Format(
                            "OK  |  {0} ms", sw.ElapsedMilliseconds);
                        UpdateStatus(status);
                        _mainHost?.RecordQueryHistory(_databaseName, sql, status, sw.ElapsedMilliseconds, -1, false);
                    }
                    else
                    {
                        string status = "Error: " + result["reason"];
                        lblStatus.Text = status;
                        _mainHost?.RecordQueryHistory(_databaseName, sql, status, sw.ElapsedMilliseconds, -1, false);
                        MessageBox.Show(result["reason"], "Execute Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (!CanUpdateUi()) return;
                sw.Stop();
                UpdateStatus("Cancelled.");
            }
            catch (Exception ex)
            {
                if (!CanUpdateUi()) return;
                sw.Stop();
                string status = "Error: " + ex.Message;
                UpdateStatus(status);
                _mainHost?.RecordQueryHistory(_databaseName, sql, status, sw.ElapsedMilliseconds, -1, false);
                MessageBox.Show(ex.Message, "Query Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (CanUpdateUi())
                {
                    tsBtnExecute.Enabled = true;
                    tsBtnCancel.Enabled = false;
                    tsBtnRefresh.Enabled = true;
                    btnDataRefresh.Enabled = true;
                }

                CancellationTokenSource cts = _cts;
                _cts = null;
                cts?.Dispose();
            }
        }

        private bool CanUpdateUi()
        {
            return !_isClosing && !IsDisposed && !Disposing;
        }

        private void UpdateStatus(string msg)
        {
            lblStatus.Text = msg;
            if (_isDocked && _mainHost != null)
            {
                _mainHost.UpdateMainStatus(msg);
            }
        }

        public void ApplyLanguage()
        {
            ApplyMenuLanguage();

            if (tsBtnExecute != null) tsBtnExecute.Text = Localization.T("Query.Execute");
            if (tsBtnCancel != null) tsBtnCancel.Text = Localization.T("Query.Stop");
            if (tsBtnBeautify != null) tsBtnBeautify.Text = Localization.T("Query.Beautify");
            if (tsBtnSave != null) tsBtnSave.Text = Localization.T("Query.Save");
            if (tsBtnAdd != null) tsBtnAdd.Text = Localization.T("Query.Add");
            if (tsBtnDelete != null) tsBtnDelete.Text = Localization.T("Query.Delete");
            if (tsBtnRefresh != null) tsBtnRefresh.Text = Localization.T("Query.Refresh");
            if (tsBtnExport != null) tsBtnExport.Text = Localization.T("Query.Export");
            if (tsBtnFloat != null) tsBtnFloat.Text = Localization.T("Query.Float");
            if (tsBtnDock != null) tsBtnDock.Text = Localization.T("Query.Dock");
            if (tabResults != null && tabResults.TabPages.Count > 0) tabResults.TabPages[0].Text = Localization.T("Query.Results");
            if (lblStatus != null && (lblStatus.Text == "Ready" || lblStatus.Text == "就緒")) lblStatus.Text = Localization.T("Status.Ready");
            RefreshResultsContextMenu();
            UpdatePaginationUI();
            Localization.ApplyTo(this);
            ApplyTheme();
        }

        private void ApplyMenuLanguage()
        {
            if (menuFile != null) menuFile.Text = Localization.T("Menu.File");
            if (menuEdit != null) menuEdit.Text = Localization.T("Menu.Edit");
            if (menuView != null) menuView.Text = Localization.T("Menu.View");
            if (menuWindow != null) menuWindow.Text = Localization.T("Menu.Window");
            if (menuHelp != null) menuHelp.Text = Localization.T("Menu.Help");
            if (menuFileExecute != null) menuFileExecute.Text = Localization.T("Query.Execute");
            if (menuFileOpenSql != null) menuFileOpenSql.Text = Localization.T("Query.OpenSql");
            if (menuFileSaveSql != null) menuFileSaveSql.Text = Localization.T("Query.SaveSql");
            if (menuFileExport != null) menuFileExport.Text = Localization.T("Query.Export");
            if (menuFileClose != null) menuFileClose.Text = Localization.T("Menu.Close");
            if (menuEditCut != null) menuEditCut.Text = Localization.T("Query.Cut");
            if (menuEditCopy != null) menuEditCopy.Text = Localization.T("Query.Copy");
            if (menuEditPaste != null) menuEditPaste.Text = Localization.T("Query.Paste");
            if (menuEditSelectAll != null) menuEditSelectAll.Text = Localization.T("Query.SelectAll");
            if (menuEditBeautify != null) menuEditBeautify.Text = Localization.T("Query.Beautify");
            if (menuViewSqlEditor != null) menuViewSqlEditor.Text = Localization.T("Query.SqlEditor");
            if (menuViewResults != null) menuViewResults.Text = Localization.T("Query.Results");
            if (menuWindowFloat != null) menuWindowFloat.Text = Localization.T("Query.Float");
            if (menuWindowDock != null) menuWindowDock.Text = Localization.T("Query.Dock");
            if (menuWindowClose != null) menuWindowClose.Text = Localization.T("Menu.Close");
            if (menuHelpAbout != null) menuHelpAbout.Text = Localization.T("Query.About");
        }

        public void ApplyTheme()
        {
            ThemeManager.ApplyTo(this);
            if (mainToolStrip != null) ThemeManager.ApplyToolStrip(mainToolStrip);
            if (dataToolStrip != null) ThemeManager.ApplyToolStrip(dataToolStrip);
            if (mainMenuStrip != null) ThemeManager.ApplyToolStrip(mainMenuStrip);
            if (statusStrip != null) ThemeManager.ApplyToolStrip(statusStrip);
            if (txtSql != null)
            {
                txtSql.BackColor = ThemeManager.TextBoxBackColor;
                txtSql.ForeColor = ThemeManager.TextColor;
            }
            if (lstCompletion != null)
            {
                lstCompletion.BackColor = ThemeManager.ElevatedColor;
                lstCompletion.ForeColor = ThemeManager.TextColor;
            }
            if (dgvResults != null)
            {
                dgvResults.BackgroundColor = ThemeManager.WindowBackColor;
                dgvResults.GridColor = ThemeManager.GridColor;
                if (dgvResults.ContextMenuStrip != null) ThemeManager.ApplyToolStrip(dgvResults.ContextMenuStrip);
            }
            if (lblSqlPreview != null) lblSqlPreview.ForeColor = ThemeManager.MutedTextColor;
            if (lblStatus != null) lblStatus.ForeColor = ThemeManager.TextColor;
        }

        private void RefreshResultsContextMenu()
        {
            if (dgvResults == null) return;

            if (resultsContextMenu == null)
            {
                resultsContextMenu = new ContextMenuStrip();
                resultsContextMenu.Opening += ResultsContextMenu_Opening;
                resultsCopyCellsItem = AddResultsMenuItem(resultsContextMenu, "copyCells", (s, e) => CopyResultsSelectionToClipboard(false));
                resultsCopyHeadersItem = AddResultsMenuItem(resultsContextMenu, "copyHeaders", (s, e) => CopyResultsSelectionToClipboard(true));
                resultsCopyRowsItem = AddResultsMenuItem(resultsContextMenu, "copyRows", (s, e) => CopySelectedResultRowsToClipboard());
                resultsContextMenu.Items.Add(new ToolStripSeparator());
                resultsSelectAllItem = AddResultsMenuItem(resultsContextMenu, "selectAll", (s, e) => dgvResults.SelectAll());
                resultsExportItem = AddResultsMenuItem(resultsContextMenu, "export", (s, e) => ExportCsv());
                resultsEditSeparator = new ToolStripSeparator();
                resultsContextMenu.Items.Add(resultsEditSeparator);
                resultsAddRowItem = AddResultsMenuItem(resultsContextMenu, "addRow", (s, e) => AddNewRow());
                resultsDeleteRowItem = AddResultsMenuItem(resultsContextMenu, "deleteRow", (s, e) => DeleteSelectedRows());
                resultsSaveRowsItem = AddResultsMenuItem(resultsContextMenu, "saveRows", (s, e) => SaveChanges());
                dgvResults.ContextMenuStrip = resultsContextMenu;
            }

            UpdateResultsContextMenuItems();
            ThemeManager.ApplyToolStrip(resultsContextMenu);
        }

        private ContextMenuStrip BuildResultsContextMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Opening += ResultsContextMenu_Opening;
            AddResultsMenuItem(menu, "copyCells", (s, e) => CopyResultsSelectionToClipboard(false)).Text = Localization.T("Query.CopySelectedCells");
            AddResultsMenuItem(menu, "copyHeaders", (s, e) => CopyResultsSelectionToClipboard(true)).Text = Localization.T("Query.CopyWithHeaders");
            AddResultsMenuItem(menu, "copyRows", (s, e) => CopySelectedResultRowsToClipboard()).Text = Localization.T("Query.CopySelectedRows");
            menu.Items.Add(new ToolStripSeparator());
            AddResultsMenuItem(menu, "selectAll", (s, e) => dgvResults.SelectAll()).Text = Localization.T("Query.SelectAll");
            AddResultsMenuItem(menu, "export", (s, e) => ExportCsv()).Text = Localization.T("Query.Export");
            if (_isTableDataMode)
            {
                menu.Items.Add(new ToolStripSeparator());
                AddResultsMenuItem(menu, "addRow", (s, e) => AddNewRow()).Text = Localization.T("Query.Add");
                AddResultsMenuItem(menu, "deleteRow", (s, e) => DeleteSelectedRows()).Text = Localization.T("Query.Delete");
                AddResultsMenuItem(menu, "saveRows", (s, e) => SaveChanges()).Text = Localization.T("Query.Save");
            }
            return menu;
        }

        private void UpdateResultsContextMenuItems()
        {
            if (resultsCopyCellsItem != null) resultsCopyCellsItem.Text = Localization.T("Query.CopySelectedCells");
            if (resultsCopyHeadersItem != null) resultsCopyHeadersItem.Text = Localization.T("Query.CopyWithHeaders");
            if (resultsCopyRowsItem != null) resultsCopyRowsItem.Text = Localization.T("Query.CopySelectedRows");
            if (resultsSelectAllItem != null) resultsSelectAllItem.Text = Localization.T("Query.SelectAll");
            if (resultsExportItem != null) resultsExportItem.Text = Localization.T("Query.Export");
            if (resultsAddRowItem != null) resultsAddRowItem.Text = Localization.T("Query.Add");
            if (resultsDeleteRowItem != null) resultsDeleteRowItem.Text = Localization.T("Query.Delete");
            if (resultsSaveRowsItem != null) resultsSaveRowsItem.Text = Localization.T("Query.Save");

            if (resultsEditSeparator != null) resultsEditSeparator.Visible = _isTableDataMode;
            if (resultsAddRowItem != null) resultsAddRowItem.Visible = _isTableDataMode;
            if (resultsDeleteRowItem != null) resultsDeleteRowItem.Visible = _isTableDataMode;
            if (resultsSaveRowsItem != null) resultsSaveRowsItem.Visible = _isTableDataMode;
        }

        private static ToolStripMenuItem AddResultsMenuItem(ContextMenuStrip menu, string name, EventHandler onClick)
        {
            ToolStripMenuItem item = new ToolStripMenuItem();
            item.Name = name;
            item.Click += onClick;
            menu.Items.Add(item);
            return item;
        }

        private void ResultsContextMenu_Opening(object sender, CancelEventArgs e)
        {
            ContextMenuStrip menu = sender as ContextMenuStrip;
            if (menu == null) return;

            bool hasData = ResultsHaveDataRows();
            bool hasSelection = HasResultsSelection();
            SetResultsMenuItemEnabled(menu, "copyCells", hasSelection);
            SetResultsMenuItemEnabled(menu, "copyHeaders", hasSelection);
            SetResultsMenuItemEnabled(menu, "copyRows", GetSelectedResultRows().Count > 0);
            SetResultsMenuItemEnabled(menu, "selectAll", hasData);
            SetResultsMenuItemEnabled(menu, "export", hasData);
            SetResultsMenuItemEnabled(menu, "addRow", dgvResults != null && dgvResults.DataSource is DataTable);
            SetResultsMenuItemEnabled(menu, "deleteRow", GetSelectedResultRows().Count > 0);
            SetResultsMenuItemEnabled(menu, "saveRows", dgvResults != null && dgvResults.DataSource is DataTable);
        }

        private static void SetResultsMenuItemEnabled(ContextMenuStrip menu, string name, bool enabled)
        {
            ToolStripItem[] items = menu.Items.Find(name, false);
            if (items.Length > 0)
            {
                items[0].Enabled = enabled;
            }
        }

        private void DgvResults_CellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (dgvResults.Rows[e.RowIndex].Cells[e.ColumnIndex].Selected) return;

            dgvResults.ClearSelection();
            dgvResults.CurrentCell = dgvResults.Rows[e.RowIndex].Cells[e.ColumnIndex];
            dgvResults.Rows[e.RowIndex].Cells[e.ColumnIndex].Selected = true;
        }

        private bool ResultsHaveDataRows()
        {
            if (dgvResults == null) return false;
            foreach (DataGridViewRow row in dgvResults.Rows)
            {
                if (!row.IsNewRow) return true;
            }
            return false;
        }

        private bool HasResultsSelection()
        {
            if (dgvResults == null) return false;
            if (dgvResults.GetCellCount(DataGridViewElementStates.Selected) > 0) return true;
            return GetSelectedResultRows().Count > 0;
        }

        private void CopyResultsSelectionToClipboard(bool includeHeaders)
        {
            if (!HasResultsSelection()) return;

            DataGridViewClipboardCopyMode previousMode = dgvResults.ClipboardCopyMode;
            try
            {
                dgvResults.ClipboardCopyMode = includeHeaders
                    ? DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText
                    : DataGridViewClipboardCopyMode.EnableWithoutHeaderText;

                DataObject data = dgvResults.GetClipboardContent();
                if (data != null)
                {
                    Clipboard.SetDataObject(data, true);
                    UpdateStatus(Localization.T("Query.CopiedToClipboard"));
                }
            }
            finally
            {
                dgvResults.ClipboardCopyMode = previousMode;
            }
        }

        private void CopySelectedResultRowsToClipboard()
        {
            string text = BuildSelectedRowsClipboardText(false);
            if (string.IsNullOrEmpty(text)) return;

            Clipboard.SetText(text);
            UpdateStatus(Localization.T("Query.CopiedToClipboard"));
        }

        private string BuildSelectedRowsClipboardText(bool includeHeaders)
        {
            List<DataGridViewRow> rows = GetSelectedResultRows();
            if (rows.Count == 0) return string.Empty;

            List<DataGridViewColumn> columns = GetVisibleResultColumns();
            if (columns.Count == 0) return string.Empty;

            StringBuilder sb = new StringBuilder();
            if (includeHeaders)
            {
                AppendClipboardLine(sb, columns.Select(c => c.HeaderText));
            }

            foreach (DataGridViewRow row in rows)
            {
                AppendClipboardLine(sb, columns.Select(c => FormatClipboardValue(row.Cells[c.Index].Value)));
            }

            return sb.ToString().TrimEnd('\r', '\n');
        }

        private List<DataGridViewRow> GetSelectedResultRows()
        {
            List<DataGridViewRow> rows = new List<DataGridViewRow>();
            if (dgvResults == null) return rows;

            if (dgvResults.SelectedRows.Count > 0)
            {
                rows.AddRange(dgvResults.SelectedRows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow));
            }
            else if (dgvResults.SelectedCells.Count > 0)
            {
                rows.AddRange(dgvResults.SelectedCells
                    .Cast<DataGridViewCell>()
                    .Where(c => c.RowIndex >= 0)
                    .Select(c => dgvResults.Rows[c.RowIndex])
                    .Where(r => !r.IsNewRow));
            }
            else if (dgvResults.CurrentCell != null && dgvResults.CurrentCell.RowIndex >= 0)
            {
                DataGridViewRow currentRow = dgvResults.Rows[dgvResults.CurrentCell.RowIndex];
                if (!currentRow.IsNewRow) rows.Add(currentRow);
            }

            return rows
                .GroupBy(r => r.Index)
                .OrderBy(g => g.Key)
                .Select(g => g.First())
                .ToList();
        }

        private List<DataGridViewColumn> GetVisibleResultColumns()
        {
            if (dgvResults == null) return new List<DataGridViewColumn>();
            return dgvResults.Columns
                .Cast<DataGridViewColumn>()
                .Where(c => c.Visible)
                .OrderBy(c => c.DisplayIndex)
                .ToList();
        }

        private static void AppendClipboardLine(StringBuilder sb, IEnumerable<string> values)
        {
            sb.AppendLine(string.Join("\t", values.Select(FormatClipboardValue)));
        }

        private static string FormatClipboardValue(object value)
        {
            if (value == null || value == DBNull.Value) return string.Empty;
            return Convert.ToString(value)
                .Replace("\r\n", " ")
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Replace('\t', ' ');
        }

        private static string GetFirstWord(string sql)
        {
            string trimmed = sql.TrimStart();
            int i = 0;
            while (i < trimmed.Length && char.IsLetter(trimmed[i])) i++;
            return trimmed.Substring(0, i).ToUpperInvariant();
        }

        private static bool IsSelectStatement(string firstWord)
        {
            switch (firstWord)
            {
                case "SELECT":
                case "SHOW":
                case "EXPLAIN":
                case "DESC":
                case "DESCRIBE":
                case "WITH":
                    return true;
                default:
                    return false;
            }
        }

        private static void AutoResizeColumns(DataGridView dgv)
        {
            if (dgv.Columns.Count == 0) return;
            // ── 資料編輯功能 (Stubs) ──
            // 限制最大欄寬避免超寬欄位讓畫面難以閱讀
            dgv.AutoResizeColumns(DataGridViewAutoSizeColumnsMode.DisplayedCells);
            foreach (DataGridViewColumn col in dgv.Columns)
            {
                if (col.Width > 300) col.Width = 300;
            }
        }

        private async void SaveChanges()
        {
            if (!_isTableDataMode)
            {
                MessageBox.Show("只有開啟資料表資料時才能儲存變更。", "資訊", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DataTable dt = dgvResults.DataSource as DataTable;
            if (dt == null)
            {
                MessageBox.Show("目前沒有可儲存的資料。", "資訊", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            dgvResults.EndEdit();
            BindingContext[dgvResults.DataSource]?.EndCurrentEdit();

            DataTable changes = dt.GetChanges();
            if (changes == null || changes.Rows.Count == 0)
            {
                MessageBox.Show("沒有偵測到資料變更。", "資訊", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DialogResult answer = MessageBox.Show(
                "即將儲存目前資料表的新增、修改與刪除資料列，確定要繼續？",
                "確認儲存",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;

            tsBtnSave.Enabled = false;
            btnDataApply.Enabled = false;
            tsBtnRefresh.Enabled = false;
            btnDataRefresh.Enabled = false;
            UpdateStatus("Saving changes...");

            try
            {
                DataSaveResult result = await Task.Run(() => SaveTableChanges(dt));
                if (!CanUpdateUi()) return;
                dt.AcceptChanges();
                UpdateStatus($"Saved. Inserted: {result.Inserted}, Updated: {result.Updated}, Deleted: {result.Deleted}");
                CalculateTotalRowsAsync();
                MessageBox.Show("資料已儲存。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                if (!CanUpdateUi()) return;
                UpdateStatus("Save failed: " + ex.Message);
                MessageBox.Show("儲存失敗：" + ex.Message, "錯誤", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (CanUpdateUi())
                {
                    tsBtnSave.Enabled = true;
                    btnDataApply.Enabled = true;
                    tsBtnRefresh.Enabled = true;
                    btnDataRefresh.Enabled = true;
                }
            }
        }

        private DataSaveResult SaveTableChanges(DataTable dt)
        {
            string tableName = GetTableNameFromSql();
            if (string.IsNullOrWhiteSpace(_databaseName) || string.IsNullOrWhiteSpace(tableName))
            {
                throw new Exception("無法判斷要儲存的資料表。");
            }

            List<TableColumnInfo> columns = GetTableColumns(tableName);
            List<string> primaryKeys = columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
            DataSaveResult result = new DataSaveResult();

            foreach (DataRow row in dt.Rows)
            {
                if (row.RowState == DataRowState.Unchanged || row.RowState == DataRowState.Detached)
                {
                    continue;
                }

                if (row.RowState == DataRowState.Added)
                {
                    if (IsAddedRowEmpty(row, columns))
                    {
                        continue;
                    }

                    ExecuteInsert(tableName, columns, row);
                    result.Inserted++;
                }
                else if (row.RowState == DataRowState.Modified)
                {
                    ExecuteUpdate(tableName, columns, primaryKeys, row);
                    result.Updated++;
                }
                else if (row.RowState == DataRowState.Deleted)
                {
                    ExecuteDelete(tableName, columns, primaryKeys, row);
                    result.Deleted++;
                }
            }

            return result;
        }

        private List<TableColumnInfo> GetTableColumns(string tableName)
        {
            List<TableColumnInfo> columns = new List<TableColumnInfo>();

            if (_db is my_mysql)
            {
                DataTable columnTable = _db.GetColumns(_databaseName, tableName);
                foreach (DataRow row in columnTable.Rows)
                {
                    string name = row["Field"].ToString();
                    columns.Add(new TableColumnInfo
                    {
                        Name = name,
                        IsPrimaryKey = row.Table.Columns.Contains("Key") && row["Key"].ToString() == "PRI",
                        IsAutoIncrement = row.Table.Columns.Contains("Extra") &&
                            row["Extra"].ToString().IndexOf("auto_increment", StringComparison.OrdinalIgnoreCase) >= 0
                    });
                }
                return columns;
            }

            DataTable copyColumns = _db.GetCopyColumns(_databaseName, tableName);
            foreach (DataRow row in copyColumns.Rows)
            {
                columns.Add(new TableColumnInfo { Name = row["Name"].ToString() });
            }

            if (_db is my_sqlite)
            {
                DataTable sqliteColumns = _db.GetColumns(_databaseName, tableName);
                foreach (DataRow row in sqliteColumns.Rows)
                {
                    if (!row.Table.Columns.Contains("pk") || row["pk"].ToString() == "0") continue;
                    TableColumnInfo col = columns.FirstOrDefault(c => c.Name == row["name"].ToString());
                    if (col != null)
                    {
                        col.IsPrimaryKey = true;
                        string type = row.Table.Columns.Contains("type") ? row["type"].ToString() : "";
                        col.IsAutoIncrement = type.IndexOf("INT", StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                }
            }

            try
            {
                DataTable indexes = _db.GetIndexes(_databaseName, tableName);
                foreach (DataRow row in indexes.Rows)
                {
                    string keyName = row.Table.Columns.Contains("Key_name") ? row["Key_name"].ToString() : "";
                    if (!keyName.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase)) continue;
                    string columnName = row.Table.Columns.Contains("Column_name") ? row["Column_name"].ToString() : "";
                    TableColumnInfo col = columns.FirstOrDefault(c => c.Name == columnName);
                    if (col != null) col.IsPrimaryKey = true;
                }
            }
            catch { }

            return columns;
        }

        private void ExecuteInsert(string tableName, List<TableColumnInfo> columns, DataRow row)
        {
            Dictionary<string, object> parameters = new Dictionary<string, object>();
            List<string> fieldSql = new List<string>();
            List<string> valueSql = new List<string>();
            int index = 0;

            foreach (TableColumnInfo column in columns)
            {
                if (!row.Table.Columns.Contains(column.Name)) continue;

                object value = row[column.Name, DataRowVersion.Current];
                if (column.IsAutoIncrement && IsDbNull(value)) continue;

                string parameterName = "p" + index;
                fieldSql.Add(QuoteIdentifier(column.Name));
                valueSql.Add(ParameterToken(parameterName));
                parameters[parameterName] = NormalizeDbValue(value);
                index++;
            }

            if (fieldSql.Count == 0)
            {
                throw new Exception("新增資料列沒有可寫入的欄位。");
            }

            string sql = "INSERT INTO " + GetQualifiedTableName(tableName) +
                         " (" + string.Join(", ", fieldSql) + ") VALUES (" + string.Join(", ", valueSql) + ");";
            ExecOrThrow(sql, parameters);
        }

        private void ExecuteUpdate(string tableName, List<TableColumnInfo> columns, List<string> primaryKeys, DataRow row)
        {
            Dictionary<string, object> parameters = new Dictionary<string, object>();
            List<string> setSql = new List<string>();
            int index = 0;

            foreach (TableColumnInfo column in columns)
            {
                if (!row.Table.Columns.Contains(column.Name)) continue;
                if (primaryKeys.Contains(column.Name)) continue;
                if (!HasColumnChanged(row, column.Name)) continue;

                string parameterName = "p" + index;
                setSql.Add(QuoteIdentifier(column.Name) + " = " + ParameterToken(parameterName));
                parameters[parameterName] = NormalizeDbValue(row[column.Name, DataRowVersion.Current]);
                index++;
            }

            if (setSql.Count == 0) return;

            string whereSql = BuildWhereClause(row, columns, primaryKeys, parameters, ref index);
            string sql = "UPDATE " + GetQualifiedTableName(tableName) +
                         " SET " + string.Join(", ", setSql) +
                         " WHERE " + whereSql + ";";
            ExecOrThrow(sql, parameters);
        }

        private void ExecuteDelete(string tableName, List<TableColumnInfo> columns, List<string> primaryKeys, DataRow row)
        {
            Dictionary<string, object> parameters = new Dictionary<string, object>();
            int index = 0;
            string whereSql = BuildWhereClause(row, columns, primaryKeys, parameters, ref index);
            string sql = "DELETE FROM " + GetQualifiedTableName(tableName) +
                         " WHERE " + whereSql + ";";
            ExecOrThrow(sql, parameters);
        }

        private string BuildWhereClause(
            DataRow row,
            List<TableColumnInfo> columns,
            List<string> primaryKeys,
            Dictionary<string, object> parameters,
            ref int index)
        {
            List<string> targetColumns = primaryKeys.Count > 0
                ? primaryKeys
                : columns.Where(c => row.Table.Columns.Contains(c.Name)).Select(c => c.Name).ToList();
            List<string> clauses = new List<string>();

            foreach (string columnName in targetColumns)
            {
                object value = row[columnName, DataRowVersion.Original];
                if (IsDbNull(value))
                {
                    clauses.Add(QuoteIdentifier(columnName) + " IS NULL");
                }
                else
                {
                    string parameterName = "p" + index;
                    clauses.Add(QuoteIdentifier(columnName) + " = " + ParameterToken(parameterName));
                    parameters[parameterName] = NormalizeDbValue(value);
                    index++;
                }
            }

            if (clauses.Count == 0)
            {
                throw new Exception("無法建立安全的 WHERE 條件。");
            }

            return string.Join(" AND ", clauses);
        }

        private void ExecOrThrow(string sql, Dictionary<string, object> parameters)
        {
            Dictionary<string, string> result = _db.ExecSQL(sql, parameters);
            if (!result.ContainsKey("status") || result["status"] != "OK")
            {
                string reason = result.ContainsKey("reason") ? result["reason"] : "未知錯誤";
                throw new Exception(reason);
            }
        }

        private static bool IsAddedRowEmpty(DataRow row, List<TableColumnInfo> columns)
        {
            foreach (TableColumnInfo column in columns)
            {
                if (!row.Table.Columns.Contains(column.Name)) continue;
                if (!IsDbNull(row[column.Name, DataRowVersion.Current]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool HasColumnChanged(DataRow row, string columnName)
        {
            object original = row[columnName, DataRowVersion.Original];
            object current = row[columnName, DataRowVersion.Current];
            if (IsDbNull(original) && IsDbNull(current)) return false;
            return !object.Equals(original, current);
        }

        private string GetQualifiedTableName(string tableName)
        {
            if (_db is my_mysql)
            {
                return QuoteIdentifier(_databaseName) + "." + QuoteIdentifier(tableName);
            }
            if (_db is my_mssql)
            {
                return QuoteIdentifier(_databaseName) + ".[dbo]." + QuoteIdentifier(tableName);
            }
            if (_db is my_postgresql)
            {
                return "public." + QuoteIdentifier(tableName);
            }
            if (_db is my_oracle)
            {
                return QuoteIdentifier(_databaseName) + "." + QuoteIdentifier(tableName);
            }
            return QuoteIdentifier(tableName);
        }

        private string QuoteIdentifier(string name)
        {
            if (_db is my_mysql)
            {
                return "`" + name.Replace("`", "``") + "`";
            }
            if (_db is my_mssql)
            {
                return "[" + name.Replace("]", "]]") + "]";
            }
            if (_db is my_postgresql || _db is my_sqlite || _db is my_oracle)
            {
                return "\"" + name.Replace("\"", "\"\"") + "\"";
            }
            return name;
        }

        private string ParameterToken(string parameterName)
        {
            if (_db is my_postgresql || _db is my_oracle) return ":" + parameterName;
            if (_db is my_mssql) return "@" + parameterName;
            if (_db is my_sqlite) return "@" + parameterName;
            return "?" + parameterName;
        }

        private static bool IsDbNull(object value)
        {
            return value == null || value == DBNull.Value;
        }

        private static object NormalizeDbValue(object value)
        {
            return IsDbNull(value) ? DBNull.Value : value;
        }

        private void AddNewRow()
        {
            if (dgvResults.DataSource is DataTable dt)
            {
                PrepareTableDataForEditing(dt);
                dt.Rows.Add(dt.NewRow());
            }
        }

        private void PrepareTableDataForEditing(DataTable dt)
        {
            if (!_isTableDataMode || dt == null) return;

            string tableName = GetTableNameFromSql();
            if (string.IsNullOrWhiteSpace(tableName) || tableName == "Table") return;

            foreach (DataColumn dataColumn in dt.Columns)
            {
                dataColumn.AllowDBNull = true;
            }
        }

        private void DeleteSelectedRows()
        {
            if (dgvResults.SelectedRows.Count > 0)
            {
                foreach (DataGridViewRow row in dgvResults.SelectedRows)
                {
                    if (!row.IsNewRow) dgvResults.Rows.Remove(row);
                }
            }
        }

        // ── 匯出 CSV ──
        private void ExportCsv()
        {
            DataTable dt = dgvResults.DataSource as DataTable;
            if (dt == null || dt.Rows.Count == 0) return;

            using (SaveFileDialog dlg = new SaveFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv",
                FileName = string.IsNullOrEmpty(_databaseName) ? "query" : _databaseName,
                DefaultExt = "csv"
            })
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;

                try
                {
                    int exportedRows;
                    string csv = BuildCsv(dt, out exportedRows);
                    File.WriteAllText(dlg.FileName, csv, Encoding.UTF8);
                    lblStatus.Text = string.Format("Exported {0} rows to {1}", exportedRows, dlg.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Export Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private static string BuildCsv(DataTable dt, out int exportedRows)
        {
            if (dt == null) throw new ArgumentNullException(nameof(dt));

            exportedRows = 0;
            StringBuilder sb = new StringBuilder();

            for (int c = 0; c < dt.Columns.Count; c++)
            {
                if (c > 0) sb.Append(',');
                sb.Append(CsvEscape(dt.Columns[c].ColumnName));
            }
            sb.AppendLine();

            foreach (DataRow row in dt.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;

                for (int c = 0; c < dt.Columns.Count; c++)
                {
                    if (c > 0) sb.Append(',');
                    sb.Append(CsvEscape(row[c] == DBNull.Value ? string.Empty : row[c].ToString()));
                }
                sb.AppendLine();
                exportedRows++;
            }

            return sb.ToString();
        }

        private static string CsvEscape(string value)
        {
            value = value ?? string.Empty;
            if (value.Contains(",") || value.Contains("\"") || value.Contains("\r") || value.Contains("\n"))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (dgvResults != null)
            {
                dgvResults.ContextMenuStrip = null;
            }
            resultsContextMenu = null;

            CancellationTokenSource cts = _cts;
            _cts = null;
            cts?.Cancel();
            cts?.Dispose();
            if (_mainHost != null)
            {
                _mainHost.NotifyDockableFormClosed(this);
            }
            base.OnFormClosed(e);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _isClosing = true;
            _cts?.Cancel();
            base.OnFormClosing(e);
        }

    }
}
