using System;
using System.Collections.Generic;
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
                string title = "Query";
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
        }

        private async void CalculateTotalRowsAsync()
        {
            if (string.IsNullOrEmpty(_baseSql)) return;
            
            // 嘗試解析資料表路徑 (處理 FROM `db`.`table` 或 FROM table 格式)
            string pattern = @"FROM\s+([`\w\.\x22]+)";
            Match match = Regex.Match(_baseSql, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                string tablePath = match.Groups[1].Value.Trim();
                
                // 如果 tablePath 已經包含點 (例如 `db`.`table`)，直接使用
                // 否則如果 _databaseName 存在，則補上資料庫字首
                string fullTableName = tablePath;
                if (!tablePath.Contains(".") && !string.IsNullOrEmpty(_databaseName))
                {
                    fullTableName = $"`{_databaseName}`.{tablePath}";
                }

                string countSql = $"SELECT COUNT(*) FROM {fullTableName}";
                var dt = await Task.Run(() => _db.SelectSQL(countSql));
                if (dt != null && dt.Rows.Count > 0)
                {
                    _totalRows = Convert.ToInt64(dt.Rows[0][0]);
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

            if (active)
            {
                if (split != null) split.Panel1Collapsed = true;
                this.Text = $"{_databaseName}.{GetTableNameFromSql()} - Table Data";
                
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
            string pattern = @"FROM\s+([`\w\.\x22]+)";
            Match match = Regex.Match(_baseSql, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                string path = match.Groups[1].Value.Replace("`", "").Replace("\"", "");
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
            mainMenuStrip.Items.Add(new ToolStripMenuItem("File"));
            mainMenuStrip.Items.Add(new ToolStripMenuItem("Edit"));
            mainMenuStrip.Items.Add(new ToolStripMenuItem("View"));
            mainMenuStrip.Items.Add(new ToolStripMenuItem("Window"));
            mainMenuStrip.Items.Add(new ToolStripMenuItem("Help"));

            // ── 頂部專業工具列 (ToolStrip) ──
            mainToolStrip = new ToolStrip { 
                ImageScalingSize = new Size(24, 24), 
                Padding = new Padding(0, 5, 0, 5),
                GripStyle = ToolStripGripStyle.Hidden,
                BackColor = Color.White
            };
            
            tsBtnExecute = new ToolStripButton("Execute", null, (s, e) => ExecutePagedQuery()) { DisplayStyle = ToolStripItemDisplayStyle.ImageAndText };
            tsBtnCancel = new ToolStripButton("Stop", null, (s, e) => CancelQuery()) { DisplayStyle = ToolStripItemDisplayStyle.ImageAndText, Enabled = false };
            tsBtnBeautify = new ToolStripButton("Beautify", null, (s, e) => BeautifySql()) { DisplayStyle = ToolStripItemDisplayStyle.ImageAndText };
            
            tsBtnSave = new ToolStripButton("Save", null, (s, e) => SaveChanges()) { DisplayStyle = ToolStripItemDisplayStyle.ImageAndText, Visible = false };
            tsBtnAdd = new ToolStripButton("Add", null, (s, e) => AddNewRow()) { DisplayStyle = ToolStripItemDisplayStyle.ImageAndText, Visible = false };
            tsBtnDelete = new ToolStripButton("Delete", null, (s, e) => DeleteSelectedRows()) { DisplayStyle = ToolStripItemDisplayStyle.ImageAndText, Visible = false };
            tsBtnRefresh = new ToolStripButton("Refresh", null, (s, e) => ExecutePagedQuery()) { DisplayStyle = ToolStripItemDisplayStyle.ImageAndText, Visible = false };
            
            tsBtnExport = new ToolStripButton("Export", null, (s, e) => ExportCsv()) { DisplayStyle = ToolStripItemDisplayStyle.ImageAndText };
            tsBtnFloat = new ToolStripButton("Float", null, (s, e) => FloatToWindow()) { DisplayStyle = ToolStripItemDisplayStyle.Image };
            tsBtnDock = new ToolStripButton("Dock", null, (s, e) => DockToMainWindow()) { DisplayStyle = ToolStripItemDisplayStyle.Image, Visible = false };

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

            ToolStripLabel lblLimit = new ToolStripLabel(" Limit: ") { Margin = new Padding(10, 0, 0, 0) };
            txtPageSize = new ToolStripTextBox { Text = _pageSize.ToString(), Width = 50, TextBoxTextAlign = HorizontalAlignment.Center };
            txtPageSize.TextChanged += (s, e) => { if (int.TryParse(txtPageSize.Text, out int val)) _pageSize = val; };
            ToolStripLabel lblRecords = new ToolStripLabel(" records ") { Margin = new Padding(0, 0, 20, 0) };

            btnDataFirst = new ToolStripButton("|<", null, (s, e) => { _currentPage = 1; ExecutePagedQuery(); }) { Alignment = ToolStripItemAlignment.Right };
            btnDataPrev = new ToolStripButton("<", null, (s, e) => { if (_currentPage > 1) { _currentPage--; ExecutePagedQuery(); } }) { Alignment = ToolStripItemAlignment.Right };
            lblDataPagination = new ToolStripLabel(" Page 1 of 1 ") { Alignment = ToolStripItemAlignment.Right, Margin = new Padding(10, 0, 10, 0) };
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

            TabPage tabData = new TabPage("Results");
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
            tabData.Controls.Add(dgvResults);
            tabResults.TabPages.Add(tabData);

            split.Panel2.Controls.Add(tabResults);

            // ── 狀態列 ──
            statusStrip = new StatusStrip { BackColor = Color.White };
            lblStatus = new ToolStripStatusLabel("Ready");
            lblSqlPreview = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.Gray };
            statusStrip.Items.AddRange(new ToolStripItem[] { lblStatus, lblSqlPreview });

            this.Controls.Add(split);
            this.Controls.Add(mainToolStrip);
            this.Controls.Add(mainMenuStrip); // 加入選單
            this.Controls.Add(statusStrip);
            
            // 將 dataToolStrip 放到 split.Panel2 的底部 (跟著表格走)
            split.Panel2.Controls.Add(dataToolStrip);
        }

        public void SetMainHost(Form1 mainHost)
        {
            _mainHost = mainHost;
        }

        public string GetDisplayTitle()
        {
            return Text;
        }

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

            int offset = (_currentPage - 1) * _pageSize;
            txtSql.Text = $"{_baseSql} LIMIT {_pageSize} OFFSET {offset};";
            ExecuteQueryAsync();
            UpdatePaginationUI();
        }

         private void UpdatePaginationUI()
        {
            int totalPages = GetTotalPages();
            lblDataPagination.Text = $" Page {_currentPage} of {totalPages} (Total: {_totalRows}) ";
            btnDataFirst.Enabled = _currentPage > 1;
            btnDataPrev.Enabled = _currentPage > 1;
            btnDataNext.Enabled = _currentPage < totalPages;
            btnDataLast.Enabled = _currentPage < totalPages;
        }

        private int GetTotalPages()
        {
            if (_totalRows <= 0) return 1;
            return (int)Math.Ceiling((double)_totalRows / _pageSize);
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

                    sw.Stop();
                    dgvResults.DataSource = dt;
                    AutoResizeColumns(dgvResults);
                    tsBtnExport.Enabled = dt.Rows.Count > 0;
                    UpdateStatus(string.Format(
                        "OK  |  {0} rows  |  {1} ms",
                        dt.Rows.Count, sw.ElapsedMilliseconds));
                }
                else
                {
                    var result = await Task.Run(
                        () => _db.ExecSQL(sql),
                        _cts.Token);

                    sw.Stop();
                    dgvResults.DataSource = null;

                    if (result["status"] == "OK")
                    {
                        UpdateStatus(string.Format(
                            "OK  |  {0} ms", sw.ElapsedMilliseconds));
                    }
                    else
                    {
                        lblStatus.Text = "Error: " + result["reason"];
                        MessageBox.Show(result["reason"], "Execute Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                UpdateStatus("Cancelled.");
            }
            catch (Exception ex)
            {
                sw.Stop();
                UpdateStatus("Error: " + ex.Message);
                MessageBox.Show(ex.Message, "Query Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                tsBtnExecute.Enabled = true;
                tsBtnCancel.Enabled = false;
                tsBtnRefresh.Enabled = true;
                btnDataRefresh.Enabled = true;
                _cts?.Dispose();
                _cts = null;
            }
        }

        private void UpdateStatus(string msg)
        {
            lblStatus.Text = msg;
            if (_isDocked && _mainHost != null)
            {
                _mainHost.UpdateMainStatus(msg);
            }
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

        private void SaveChanges()
        {
            MessageBox.Show("儲存變更功能開發中...", "資訊", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void AddNewRow()
        {
            if (dgvResults.DataSource is DataTable dt)
            {
                dt.Rows.Add(dt.NewRow());
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
                    StringBuilder sb = new StringBuilder();

                    // 標頭
                    for (int c = 0; c < dt.Columns.Count; c++)
                    {
                        if (c > 0) sb.Append(',');
                        sb.Append(CsvEscape(dt.Columns[c].ColumnName));
                    }
                    sb.AppendLine();

                    // 資料
                    foreach (DataRow row in dt.Rows)
                    {
                        for (int c = 0; c < dt.Columns.Count; c++)
                        {
                            if (c > 0) sb.Append(',');
                            sb.Append(CsvEscape(row[c]?.ToString() ?? ""));
                        }
                        sb.AppendLine();
                    }

                    File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
                    lblStatus.Text = string.Format("Exported {0} rows to {1}", dt.Rows.Count, dlg.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Export Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private static string CsvEscape(string value)
        {
            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n"))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            if (_mainHost != null)
            {
                _mainHost.NotifyDockableFormClosed(this);
            }
            base.OnFormClosed(e);
        }

    }
}
