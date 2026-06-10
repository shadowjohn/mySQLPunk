using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using mySQLPunk.lib;
using Newtonsoft.Json;

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
        private Panel loadingOverlay;
        private Label lblLoadingTitle;
        private Label lblLoadingMessage;
        private ListBox lstCompletion;
        private TabControl tabResults;
        private SplitContainer split;
        private Form1 _mainHost;
        private bool _isDocked;
        private bool _isTableDataMode; 
        private bool _isNoPrimaryKeyReadOnlyMode;
        private bool _recordLimitEnabled = true;
        private string _lastResultSql = "";
        private bool _lastResultCanStreamExport;
        private const int GridFullAutoResizeRowLimit = 200;
        private const int GridRowHeightApplyLimit = 500;
        private const int WM_SETREDRAW = 0x000B;

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

        private sealed class TableSaveTransactionStatements
        {
            public string BeginSql { get; set; }
            public string CommitSql { get; set; }
            public string RollbackSql { get; set; }
        }

        private class TablePageLoadResult
        {
            public DataTable Rows { get; set; }
            public long TotalRows { get; set; }
            public int CurrentPage { get; set; }
            public string ErrorMessage { get; set; }
        }

        private enum SqlTokenKind
        {
            Word,
            StringLiteral,
            QuotedIdentifier,
            Symbol,
            Operator
        }

        private sealed class SqlToken
        {
            public string Text { get; set; }
            public SqlTokenKind Kind { get; set; }

            public bool IsWord(string value)
            {
                return Kind == SqlTokenKind.Word && string.Equals(Text, value, StringComparison.OrdinalIgnoreCase);
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, bool wParam, IntPtr lParam);

        private sealed class GridRedrawScope : IDisposable
        {
            private readonly DataGridView _grid;

            public GridRedrawScope(DataGridView grid)
            {
                _grid = grid;
                SetGridRedraw(_grid, false);
            }

            public void Dispose()
            {
                SetGridRedraw(_grid, true);
                if (_grid == null || _grid.IsDisposed || _grid.Disposing) return;
                _grid.Invalidate();
                _grid.Update();
            }
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
        private ToolStripSeparator resultsBinarySeparator;
        private ToolStripMenuItem resultsViewBlobHexItem;
        private ToolStripMenuItem resultsCopyBlobHexItem;
        private ToolStripMenuItem resultsSaveBlobFileItem;
        private ToolStripMenuItem resultsImportBlobFileItem;
        private ToolStripSeparator resultsGeometrySeparator;
        private ToolStripMenuItem resultsCopyGeometryWktItem;
        private ToolStripMenuItem resultsCopyWktGeometrySqlItem;
        private ToolStripSeparator resultsEditSeparator;
        private ToolStripMenuItem resultsAddRowItem;
        private ToolStripMenuItem resultsDeleteRowItem;
        private ToolStripMenuItem resultsSaveRowsItem;
        private bool _isClosing;
        private System.Windows.Forms.Timer _autoRecoveryTimer;
        private string _lastAutoRecoverySqlHash = string.Empty;

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
            string tableDataBaseSql;
            if (TryBuildTableDataBaseSql(initialSql, out tableDataBaseSql))
            {
                _baseSql = tableDataBaseSql;
                SetTableDataMode(true);
                ExecutePagedQuery(); // 立即載入第一頁資料
            }
            ApplyLanguage();
        }

        private void SetTableDataMode(bool active)
        {
            _isTableDataMode = active;
            
            // 動態切換按鈕可見度
            tsBtnSave.Visible = active;
            tsBtnAdd.Visible = active;
            tsBtnDelete.Visible = active;
            tsBtnRefresh.Visible = active;
            
            tsBtnExecute.Visible = true;
            tsBtnBeautify.Visible = true;
            RefreshResultsContextMenu();

            if (active)
            {
                if (split != null)
                {
                    split.Panel1Collapsed = false;
                    split.SplitterDistance = Math.Min(180, Math.Max(120, split.Height / 4));
                }
                this.Text = $"{_databaseName}.{GetTableNameFromSql()} - {Localization.T("Query.TableData")}";
                
                _isNoPrimaryKeyReadOnlyMode = false;
                ApplyTableDataEditability();

            }
            else
            {
                _isNoPrimaryKeyReadOnlyMode = false;
                if (split != null) split.Panel1Collapsed = false;
            }
        }

        private string GetTableNameFromSql()
        {
            string tableName = ExtractTableNameFromSql(txtSql == null ? "" : txtSql.Text);
            if (!string.IsNullOrWhiteSpace(tableName)) return tableName;
            tableName = ExtractTableNameFromSql(_baseSql);
            if (!string.IsNullOrWhiteSpace(tableName)) return tableName;
            return "Table";
        }

        private static string ExtractTableNameFromSql(string sql)
        {
            if (string.IsNullOrEmpty(sql)) return "";
            string pattern = @"FROM\s+([`\[\]\w\.\x22]+)";
            Match match = Regex.Match(sql, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                string path = match.Groups[1].Value.Replace("`", "").Replace("\"", "").Replace("[", "").Replace("]", "");
                return path.Contains(".") ? path.Split('.').Last() : path;
            }
            return "";
        }

        private static bool TryBuildTableDataBaseSql(string sql, out string baseSql)
        {
            baseSql = "";
            if (string.IsNullOrWhiteSpace(sql)) return false;

            string candidate = sql.Trim();
            int semicolonIndex = candidate.IndexOf(';');
            if (semicolonIndex >= 0)
            {
                candidate = candidate.Substring(0, semicolonIndex);
            }

            candidate = Regex.Replace(candidate, @"\s+", " ").Trim();
            candidate = Regex.Replace(candidate, @"\s+LIMIT\s+\d+(?:\s*,\s*\d+)?\s*$", "", RegexOptions.IgnoreCase).Trim();
            if (!Regex.IsMatch(candidate, @"^SELECT\s+.+?\s+FROM\s+[`""\[\]\w\.]+$", RegexOptions.IgnoreCase))
            {
                return false;
            }

            if (Regex.IsMatch(candidate, @"\s+(WHERE|JOIN|GROUP\s+BY|ORDER\s+BY|HAVING|UNION|LIMIT|OFFSET|FETCH)\s+", RegexOptions.IgnoreCase))
            {
                return false;
            }

            baseSql = candidate;
            return true;
        }

        private void InitializeQueryForm()
        {
            LoadInitialQueryOptions();

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
            
            tsBtnExecute = new ToolStripButton(Localization.T("Query.Execute"), null, (s, e) => ExecuteQueryAsync()) { DisplayStyle = ToolStripItemDisplayStyle.ImageAndText };
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
            ApplyDataToolbarButtonTooltips();
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
                Font = BuildConfiguredFont("EditorFontName", "Consolas", "EditorFontSize", 11),
                AcceptsTab = true,
                WordWrap = ApplicationOptionSettings.GetBool("EditorWordWrap"),
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
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText
            };
            EnableGridDoubleBuffer(dgvResults);
            dgvResults.CellMouseDown += DgvResults_CellMouseDown;
            dgvResults.CellFormatting += DgvResults_CellFormatting;
            dgvResults.DataBindingComplete += DgvResults_DataBindingComplete;
            dgvResults.DataError += DgvResults_DataError;
            Font configuredGridFont = BuildConfiguredFont("RecordGridFontName", "Microsoft JhengHei UI", "RecordGridFontSize", 9);
            ApplyConfiguredResultGridAppearance(dgvResults, configuredGridFont);
            RefreshResultsContextMenu();
            tabData.Controls.Add(dgvResults);
            CreateLoadingOverlay(tabData);
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
            InitializeAutoRecoveryTimer();
            ApplyTheme();
        }

        private void InitializeAutoRecoveryTimer()
        {
            if (!AutoRecoveryDraftService.IsQueryAutoRecoveryEnabled()) return;

            _autoRecoveryTimer = new System.Windows.Forms.Timer();
            _autoRecoveryTimer.Interval = AutoRecoveryDraftService.GetQueryAutoRecoveryIntervalMilliseconds();
            _autoRecoveryTimer.Tick += (s, e) => SaveAutoRecoveryDraftIfNeeded();
            _autoRecoveryTimer.Start();
        }

        private void SaveAutoRecoveryDraftIfNeeded()
        {
            if (!AutoRecoveryDraftService.IsQueryAutoRecoveryEnabled() || txtSql == null) return;

            string sql = txtSql.Text;
            if (string.IsNullOrWhiteSpace(sql)) return;

            string hash = ComputeSqlHash(sql);
            if (string.Equals(hash, _lastAutoRecoverySqlHash, StringComparison.Ordinal)) return;

            string path = AutoRecoveryDraftService.WriteQueryDraft(_databaseName, connectionHost, Text, sql);
            if (!string.IsNullOrWhiteSpace(path))
            {
                _lastAutoRecoverySqlHash = hash;
            }
        }

        private static string ComputeSqlHash(string sql)
        {
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            {
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(sql ?? string.Empty)));
            }
        }

        private void LoadInitialQueryOptions()
        {
            _recordLimitEnabled = ApplicationOptionSettings.GetBool("RecordLimitEnabled");
            int configuredPageSize = ApplicationOptionSettings.GetInt("RecordLimit");
            if (configuredPageSize <= 0) configuredPageSize = 1000;
            _pageSize = _recordLimitEnabled ? configuredPageSize : 1000000;
        }

        private static Font BuildConfiguredFont(string fontNameKey, string fallbackName, string fontSizeKey, float fallbackSize)
        {
            string fontName = ApplicationOptionSettings.GetString(fontNameKey);
            if (string.IsNullOrWhiteSpace(fontName)) fontName = fallbackName;

            int configuredSize = ApplicationOptionSettings.GetInt(fontSizeKey);
            float fontSize = configuredSize > 0 ? configuredSize : fallbackSize;

            try
            {
                return new Font(fontName, fontSize);
            }
            catch
            {
                return new Font(fallbackName, fallbackSize);
            }
        }

        private static string GetConfiguredDirectory(string optionKey)
        {
            string path = ApplicationOptionSettings.GetString(optionKey);
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;

            try
            {
                Directory.CreateDirectory(path);
                return Directory.Exists(path) ? path : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void RememberConfiguredDirectoryForPath(string optionKey, string filePath)
        {
            if (string.IsNullOrWhiteSpace(optionKey) || string.IsNullOrWhiteSpace(filePath)) return;

            try
            {
                string directory = Path.GetDirectoryName(filePath);
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;

                ApplicationOptionSettings.SetString(optionKey, directory);
                ApplicationOptionSettings.Save();
            }
            catch
            {
            }
        }

        private void CreateLoadingOverlay(Control parent)
        {
            loadingOverlay = new Panel
            {
                Dock = DockStyle.Fill,
                Visible = false
            };

            Panel content = new Panel
            {
                Width = 360,
                Height = 120,
                Anchor = AnchorStyles.None,
                Padding = new Padding(28, 20, 28, 20)
            };
            content.Location = new Point(
                Math.Max(0, (parent.ClientSize.Width - content.Width) / 2),
                Math.Max(0, (parent.ClientSize.Height - content.Height) / 2));

            lblLoadingTitle = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 28,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Microsoft JhengHei UI", 11F, FontStyle.Bold)
            };

            lblLoadingMessage = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleCenter
            };

            ProgressBar loadingProgress = new ProgressBar
            {
                Dock = DockStyle.Bottom,
                Height = 10,
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 35
            };

            content.Controls.Add(loadingProgress);
            content.Controls.Add(lblLoadingMessage);
            content.Controls.Add(lblLoadingTitle);
            loadingOverlay.Controls.Add(content);
            loadingOverlay.Resize += (s, e) =>
            {
                content.Location = new Point(
                    Math.Max(0, (loadingOverlay.ClientSize.Width - content.Width) / 2),
                    Math.Max(0, (loadingOverlay.ClientSize.Height - content.Height) / 2));
            };

            parent.Controls.Add(loadingOverlay);
            loadingOverlay.BringToFront();
            UpdateLoadingOverlayText();
        }

        private void BuildMainMenu()
        {
            menuFile = new ToolStripMenuItem();
            menuEdit = new ToolStripMenuItem();
            menuView = new ToolStripMenuItem();
            menuWindow = new ToolStripMenuItem();
            menuHelp = new ToolStripMenuItem();

            menuFileExecute = new ToolStripMenuItem(null, null, (s, e) => ExecuteQueryAsync()) { ShortcutKeys = Keys.F5 };
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
                Filter = Localization.T("Common.SqlFilesFilter"),
                DefaultExt = "sql",
                FileName = string.IsNullOrWhiteSpace(_databaseName) ? "query.sql" : _databaseName + ".sql",
                InitialDirectory = GetConfiguredDirectory("FileQueryDirectory")
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                File.WriteAllText(dialog.FileName, txtSql.Text, Encoding.UTF8);
                UpdateStatus(Localization.Format("Query.SqlSaved", dialog.FileName));
            }
        }

        private void OpenSqlWithDialog()
        {
            using (OpenFileDialog dialog = new OpenFileDialog
            {
                Filter = Localization.T("Common.SqlFilesFilter"),
                CheckFileExists = true,
                InitialDirectory = GetConfiguredDirectory("FileQueryDirectory")
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                txtSql.Text = File.ReadAllText(dialog.FileName, Encoding.UTF8);
                txtSql.SelectionStart = txtSql.TextLength;
                txtSql.SelectionLength = 0;
                UpdateStatus(Localization.Format("Query.SqlOpened", dialog.FileName));
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
                if (ApplicationOptionSettings.GetBool("AutoCompleteEnabled") &&
                    ApplicationOptionSettings.GetBool("AutoCompleteAutoRefresh") &&
                    !string.IsNullOrEmpty(_databaseName) && _db != null)
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
            if (!ApplicationOptionSettings.GetBool("EditorSyntaxHighlight"))
            {
                ApplyPlainEditorFormatting();
                return;
            }
            if (!ShouldUseEditorHelpers(txtSql == null ? "" : txtSql.Text)) return;

            txtSql.TextChanged -= TxtSql_TextChanged;

            int selStart = txtSql.SelectionStart;
            int selLen = txtSql.SelectionLength;

            txtSql.SuspendLayout();

            Color defaultTextColor = ThemeManager.TextColor;
            Color keywordColor = ThemeManager.AccentColor;
            Color stringColor = ThemeManager.IsDark ? Color.FromArgb(206, 145, 120) : Color.DarkOrange;
            Color commentColor = ThemeManager.IsDark ? Color.FromArgb(106, 153, 85) : Color.DarkGreen;

            // 先全部重置為預設文字色
            txtSql.SelectAll();
            txtSql.SelectionColor = defaultTextColor;
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
                        txtSql.SelectionColor = keywordColor;
                        txtSql.SelectionFont = new Font(txtSql.Font, FontStyle.Bold);
                    }
                    idx += kw.Length;
                }
            }

            // 字串常值 (橙色)
            HighlightPattern(text, @"'[^']*'", stringColor, FontStyle.Regular);

            // 單行注釋 (綠色)
            HighlightPattern(text, @"--[^\r\n]*", commentColor, FontStyle.Italic);

            // 還原游標
            txtSql.Select(selStart, selLen);
            txtSql.SelectionColor = defaultTextColor;
            txtSql.SelectionFont = new Font(txtSql.Font, FontStyle.Regular);

            txtSql.ResumeLayout();
            txtSql.TextChanged += TxtSql_TextChanged;
        }

        private void ApplyPlainEditorFormatting()
        {
            if (txtSql == null) return;

            txtSql.TextChanged -= TxtSql_TextChanged;
            int selStart = txtSql.SelectionStart;
            int selLen = txtSql.SelectionLength;
            txtSql.SelectAll();
            txtSql.SelectionColor = ThemeManager.TextColor;
            txtSql.SelectionFont = new Font(txtSql.Font, FontStyle.Regular);
            txtSql.Select(selStart, selLen);
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

            txtSql.Text = FormatSqlForEditor(sql);
        }

        private static string FormatSqlForEditor(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql)) return sql;

            List<SqlToken> tokens = TokenizeSql(sql);
            if (tokens.Count == 0) return string.Empty;

            NormalizeSqlKeywords(tokens);

            int selectIndex = FindTopLevelToken(tokens, 0, tokens.Count, "SELECT");
            int fromIndex = selectIndex >= 0 ? FindTopLevelToken(tokens, selectIndex + 1, tokens.Count, "FROM") : -1;
            if (selectIndex == 0 && fromIndex > selectIndex)
            {
                return FormatSelectSql(tokens, selectIndex, fromIndex).Trim();
            }

            return FormatSqlClauses(tokens, 0, tokens.Count).Trim();
        }

        private static string FormatSelectSql(List<SqlToken> tokens, int selectIndex, int fromIndex)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("SELECT");

            List<Tuple<int, int>> columns = SplitTopLevelByComma(tokens, selectIndex + 1, fromIndex);
            for (int i = 0; i < columns.Count; i++)
            {
                string columnSql = BuildInlineSql(tokens, columns[i].Item1, columns[i].Item2);
                if (string.IsNullOrWhiteSpace(columnSql)) continue;

                sb.Append("  ");
                sb.Append(columnSql);
                if (i < columns.Count - 1) sb.Append(",");
                sb.AppendLine();
            }

            sb.Append(FormatSqlClauses(tokens, fromIndex, tokens.Count));
            return sb.ToString();
        }

        private static string FormatSqlClauses(List<SqlToken> tokens, int start, int end)
        {
            StringBuilder sb = new StringBuilder();
            int index = start;

            while (index < end)
            {
                int clauseEnd = FindNextClauseStart(tokens, index + 1, end);
                string line = BuildClauseLine(tokens, index, clauseEnd);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.Append(line);
                }
                index = clauseEnd;
            }

            return sb.ToString();
        }

        private static string BuildClauseLine(List<SqlToken> tokens, int start, int end)
        {
            if (start >= end) return string.Empty;

            StringBuilder sb = new StringBuilder();
            int index = start;
            if (tokens[index].IsWord("GROUP") && index + 1 < end && tokens[index + 1].IsWord("BY"))
            {
                sb.Append("GROUP BY");
                index += 2;
            }
            else if (tokens[index].IsWord("ORDER") && index + 1 < end && tokens[index + 1].IsWord("BY"))
            {
                sb.Append("ORDER BY");
                index += 2;
            }
            else if (IsJoinClauseStart(tokens, index, end))
            {
                int joinEnd = tokens[index].IsWord("JOIN") ? index + 1 : index + 2;
                sb.Append(BuildInlineSql(tokens, index, joinEnd));
                index = joinEnd;
            }
            else
            {
                sb.Append(tokens[index].Text);
                index++;
            }

            string tail = FormatClauseTail(tokens, index, end);
            if (!string.IsNullOrWhiteSpace(tail))
            {
                sb.Append(" ");
                sb.Append(tail);
            }

            return sb.ToString().TrimEnd();
        }

        private static string FormatClauseTail(List<SqlToken> tokens, int start, int end)
        {
            StringBuilder sb = new StringBuilder();
            int segmentStart = start;
            int depth = 0;

            for (int i = start; i < end; i++)
            {
                if (tokens[i].Text == "(") depth++;
                else if (tokens[i].Text == ")" && depth > 0) depth--;

                if (depth == 0 && i > segmentStart && (tokens[i].IsWord("AND") || tokens[i].IsWord("OR")))
                {
                    AppendInlineSegment(sb, tokens, segmentStart, i);
                    sb.AppendLine();
                    sb.Append("  ");
                    sb.Append(tokens[i].Text);
                    sb.Append(" ");
                    segmentStart = i + 1;
                }
            }

            AppendInlineSegment(sb, tokens, segmentStart, end);
            return sb.ToString().TrimEnd();
        }

        private static void AppendInlineSegment(StringBuilder sb, List<SqlToken> tokens, int start, int end)
        {
            string text = BuildInlineSql(tokens, start, end);
            if (!string.IsNullOrWhiteSpace(text)) sb.Append(text);
        }

        private static List<SqlToken> TokenizeSql(string sql)
        {
            List<SqlToken> tokens = new List<SqlToken>();
            int i = 0;
            while (i < sql.Length)
            {
                char c = sql[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                if (c == '\'' || c == '"' || c == '`' || c == '[')
                {
                    char close = c == '[' ? ']' : c;
                    int start = i++;
                    while (i < sql.Length)
                    {
                        if (sql[i] == close)
                        {
                            i++;
                            if (i < sql.Length && sql[i] == close && close != ']')
                            {
                                i++;
                                continue;
                            }
                            break;
                        }
                        i++;
                    }
                    tokens.Add(new SqlToken
                    {
                        Text = sql.Substring(start, i - start),
                        Kind = c == '\'' ? SqlTokenKind.StringLiteral : SqlTokenKind.QuotedIdentifier
                    });
                    continue;
                }

                if (char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == ':' || c == '?')
                {
                    int start = i++;
                    while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_' || sql[i] == '$'))
                    {
                        i++;
                    }
                    tokens.Add(new SqlToken { Text = sql.Substring(start, i - start), Kind = SqlTokenKind.Word });
                    continue;
                }

                if ("=<>!+-*/%".IndexOf(c) >= 0)
                {
                    int start = i++;
                    if (i < sql.Length && "=<>".IndexOf(sql[i]) >= 0) i++;
                    tokens.Add(new SqlToken { Text = sql.Substring(start, i - start), Kind = SqlTokenKind.Operator });
                    continue;
                }

                tokens.Add(new SqlToken { Text = c.ToString(), Kind = SqlTokenKind.Symbol });
                i++;
            }

            return tokens;
        }

        private static void NormalizeSqlKeywords(List<SqlToken> tokens)
        {
            HashSet<string> keywords = new HashSet<string>(Keywords, StringComparer.OrdinalIgnoreCase);
            foreach (SqlToken token in tokens)
            {
                if (token.Kind == SqlTokenKind.Word && keywords.Contains(token.Text))
                {
                    token.Text = token.Text.ToUpperInvariant();
                }
            }
        }

        private static int FindTopLevelToken(List<SqlToken> tokens, int start, int end, string word)
        {
            int depth = 0;
            for (int i = start; i < end; i++)
            {
                if (tokens[i].Text == "(") depth++;
                else if (tokens[i].Text == ")" && depth > 0) depth--;
                else if (depth == 0 && tokens[i].IsWord(word)) return i;
            }
            return -1;
        }

        private static int FindNextClauseStart(List<SqlToken> tokens, int start, int end)
        {
            int depth = 0;
            for (int i = start; i < end; i++)
            {
                if (tokens[i].Text == "(") depth++;
                else if (tokens[i].Text == ")" && depth > 0) depth--;

                if (depth == 0 && IsClauseStart(tokens, i, end))
                {
                    return i;
                }
            }
            return end;
        }

        private static bool IsClauseStart(List<SqlToken> tokens, int index, int end)
        {
            if (index >= end || tokens[index].Kind != SqlTokenKind.Word) return false;
            if (tokens[index].IsWord("FROM") ||
                tokens[index].IsWord("WHERE") ||
                tokens[index].IsWord("HAVING") ||
                tokens[index].IsWord("LIMIT") ||
                tokens[index].IsWord("OFFSET") ||
                tokens[index].IsWord("UNION") ||
                tokens[index].IsWord("VALUES") ||
                tokens[index].IsWord("SET"))
            {
                return true;
            }

            if ((tokens[index].IsWord("GROUP") || tokens[index].IsWord("ORDER")) &&
                index + 1 < end && tokens[index + 1].IsWord("BY"))
            {
                return true;
            }

            return IsJoinClauseStart(tokens, index, end);
        }

        private static bool IsJoinClauseStart(List<SqlToken> tokens, int index, int end)
        {
            if (index >= end) return false;
            if (tokens[index].IsWord("JOIN")) return true;
            if (index + 1 >= end || !tokens[index + 1].IsWord("JOIN")) return false;
            return tokens[index].IsWord("INNER") ||
                   tokens[index].IsWord("LEFT") ||
                   tokens[index].IsWord("RIGHT") ||
                   tokens[index].IsWord("FULL") ||
                   tokens[index].IsWord("CROSS") ||
                   tokens[index].IsWord("OUTER");
        }

        private static List<Tuple<int, int>> SplitTopLevelByComma(List<SqlToken> tokens, int start, int end)
        {
            List<Tuple<int, int>> ranges = new List<Tuple<int, int>>();
            int depth = 0;
            int segmentStart = start;

            for (int i = start; i < end; i++)
            {
                if (tokens[i].Text == "(") depth++;
                else if (tokens[i].Text == ")" && depth > 0) depth--;
                else if (depth == 0 && tokens[i].Text == ",")
                {
                    ranges.Add(Tuple.Create(segmentStart, i));
                    segmentStart = i + 1;
                }
            }

            ranges.Add(Tuple.Create(segmentStart, end));
            return ranges;
        }

        private static string BuildInlineSql(List<SqlToken> tokens, int start, int end)
        {
            StringBuilder sb = new StringBuilder();
            SqlToken previous = null;

            for (int i = start; i < end; i++)
            {
                SqlToken current = tokens[i];
                if (string.IsNullOrWhiteSpace(current.Text)) continue;

                if (NeedsSpaceBetween(previous, current))
                {
                    sb.Append(' ');
                }
                sb.Append(current.Text);
                previous = current;
            }

            return sb.ToString().Trim();
        }

        private static bool NeedsSpaceBetween(SqlToken previous, SqlToken current)
        {
            if (previous == null) return false;
            if (current.Text == "," || current.Text == ";" || current.Text == ")" || current.Text == ".") return false;
            if (previous.Text == "(" || previous.Text == ".") return false;
            if (current.Text == "(" && previous.Kind == SqlTokenKind.Word) return false;
            if (previous.Kind == SqlTokenKind.Operator || current.Kind == SqlTokenKind.Operator) return true;
            return true;
        }

        // ── 自動補完 ──
        private void ShowCompletion()
        {
            if (!ApplicationOptionSettings.GetBool("AutoCompleteEnabled") || !ShouldUseEditorHelpers(txtSql == null ? "" : txtSql.Text))
            {
                lstCompletion.Visible = false;
                return;
            }

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
                lstCompletion.SelectedIndex = ApplicationOptionSettings.GetBool("AutoCompleteSelectFirst") ? 0 : -1;
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
            bool helperEnabledForSize = ShouldUseEditorHelpers(txtSql == null ? "" : txtSql.Text);
            if (!helperEnabledForSize)
            {
                if (lstCompletion != null) lstCompletion.Visible = false;
                return;
            }

            if (ApplicationOptionSettings.GetBool("EditorSyntaxHighlight") && helperEnabledForSize) ApplySyntaxHighlight();
            else ApplyPlainEditorFormatting();

            if (ApplicationOptionSettings.GetBool("AutoCompleteEnabled") && helperEnabledForSize) ShowCompletion();
            else if (lstCompletion != null) lstCompletion.Visible = false;
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
            if (e.KeyCode == Keys.Tab && ApplicationOptionSettings.GetBool("EditorInsertSpaces"))
            {
                int tabWidth = ApplicationOptionSettings.GetInt("EditorTabWidth");
                if (tabWidth <= 0) tabWidth = 2;
                txtSql.SelectedText = new string(' ', Math.Min(tabWidth, 16));
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

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
            SetPaginationControlsEnabled(false);
            UpdateStatus(Localization.T("Query.LoadingTablePage"));
            ShowLoadingOverlay();

            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                TablePageLoadResult result = await Task.Run(
                    () => LoadTablePage(tableName, offset, _pageSize, _cts.Token),
                    _cts.Token);
                if (!CanUpdateUi()) return;
                if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                {
                    throw new InvalidOperationException(result.ErrorMessage);
                }
                DataTable dt = result.Rows;
                _totalRows = result.TotalRows;
                _currentPage = result.CurrentPage;
                _lastResultSql = "";
                _lastResultCanStreamExport = false;
                dt.AcceptChanges();
                PrepareTableDataForEditing(dt);

                sw.Stop();
                using (new GridRedrawScope(dgvResults))
                {
                    BindResultsTable(dt);
                    tsBtnExport.Enabled = dt.Rows.Count > 0;
                    ApplyTableDataEditability();
                }
                UpdateStatus(BuildQueryStatus(dt.Rows.Count, sw.ElapsedMilliseconds));
                UpdatePaginationUI();
            }
            catch (OperationCanceledException)
            {
                if (!CanUpdateUi()) return;
                UpdateStatus(Localization.T("Query.Cancelled"));
            }
            catch (Exception ex)
            {
                if (!CanUpdateUi()) return;
                string reason = BuildQueryExceptionMessage(ex);
                UpdateStatus(Localization.Format("Query.LoadFailed", reason));
                ShowResultFeedback(Localization.T("Query.QueryError"), Localization.Format("Query.LoadTableFailed", reason));
            }
            finally
            {
                if (CanUpdateUi())
                {
                    tsBtnExecute.Enabled = true;
                    tsBtnCancel.Enabled = false;
                    tsBtnRefresh.Enabled = true;
                    btnDataRefresh.Enabled = true;
                    if (txtPageSize != null) txtPageSize.Enabled = true;
                    HideLoadingOverlay();
                    UpdatePaginationUI();
                }

                CancellationTokenSource cts = _cts;
                _cts = null;
                cts?.Dispose();
            }
        }

        private TablePageLoadResult LoadTablePage(string tableName, int requestedOffset, int pageSize, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            long totalRows = _db.CountRows(_databaseName, tableName);
            token.ThrowIfCancellationRequested();

            int totalPages = totalRows <= 0 ? 1 : (int)Math.Ceiling((double)totalRows / pageSize);
            int page = requestedOffset / pageSize + 1;
            if (page < 1) page = 1;
            if (page > totalPages) page = totalPages;

            int offset = (page - 1) * pageSize;
            DataTable rows = _db.SelectTablePage(_databaseName, tableName, offset, pageSize);
            string errorMessage = GetQueryError(rows);
            return new TablePageLoadResult
            {
                Rows = rows,
                TotalRows = totalRows,
                CurrentPage = page,
                ErrorMessage = errorMessage
            };
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

        private void SetPaginationControlsEnabled(bool enabled)
        {
            if (btnDataFirst != null) btnDataFirst.Enabled = enabled;
            if (btnDataPrev != null) btnDataPrev.Enabled = enabled;
            if (btnDataNext != null) btnDataNext.Enabled = enabled;
            if (btnDataLast != null) btnDataLast.Enabled = enabled;
            if (txtPageSize != null) txtPageSize.Enabled = enabled;
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
                UpdateStatus(Localization.T("Query.PageSizeInvalid"));
                return;
            }

            _pageSize = parsed;
            _recordLimitEnabled = true;
            _currentPage = 1;
            if (txtPageSize.Text != _pageSize.ToString()) txtPageSize.Text = _pageSize.ToString();
            ApplicationOptionSettings.SetBool("RecordLimitEnabled", true);
            ApplicationOptionSettings.SetInt("RecordLimit", _pageSize);
            ApplicationOptionSettings.Save();
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
            _lastResultSql = "";
            _lastResultCanStreamExport = false;
            lblSqlPreview.Text = sql; // 更新底部的 SQL 預覽

            _cts = new CancellationTokenSource();
            tsBtnExecute.Enabled = false;
            tsBtnCancel.Enabled = true;
            tsBtnExport.Enabled = false;
            tsBtnRefresh.Enabled = false;
            btnDataRefresh.Enabled = false;
            UpdateStatus(Localization.T("Query.Executing"));

            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                // 判斷是否為 SELECT/SHOW/EXPLAIN/DESC (顯示結果集) 或 DML (顯示影響行數)
                string firstWord = GetFirstWord(rawSql);
                bool isQuery = IsSelectStatement(firstWord);

                if (isQuery)
                {
                    if (_isTableDataMode)
                    {
                        string tableDataBaseSql;
                        if (TryBuildTableDataBaseSql(rawSql, out tableDataBaseSql))
                        {
                            _baseSql = tableDataBaseSql;
                            this.Text = $"{_databaseName}.{GetTableNameFromSql()} - {Localization.T("Query.TableData")}";
                        }
                    }

                    DataTable dt = await Task.Run(
                        () => _db.SelectSQL(sql),
                        _cts.Token);
                    if (!CanUpdateUi()) return;
                    if (dt == null) dt = new DataTable();
                    string queryError = GetQueryError(dt);
                    if (!string.IsNullOrWhiteSpace(queryError))
                    {
                        throw new InvalidOperationException(queryError);
                    }
                    if (_isTableDataMode)
                    {
                        dt.AcceptChanges();
                        PrepareTableDataForEditing(dt);
                        _lastResultSql = "";
                        _lastResultCanStreamExport = false;
                    }
                    else
                    {
                        _lastResultSql = sql;
                        _lastResultCanStreamExport = dt.Rows.Count > 0;
                    }

                    sw.Stop();
                    string status = BuildQueryStatus(dt.Rows.Count, sw.ElapsedMilliseconds);
                    if (dt.Rows.Count == 0 && !_isTableDataMode)
                    {
                        UpdateStatus(status);
                        ShowResultFeedback(Localization.T("Query.NoRowsStatus"), Localization.T("Query.NoRowsMessage"));
                    }
                    else
                    {
                        using (new GridRedrawScope(dgvResults))
                        {
                            BindResultsTable(dt);
                            tsBtnExport.Enabled = dt.Rows.Count > 0;
                            if (_isTableDataMode)
                            {
                                ApplyTableDataEditability();
                            }
                            else
                            {
                                dgvResults.ReadOnly = true;
                                dgvResults.AllowUserToAddRows = false;
                                dgvResults.AllowUserToDeleteRows = false;
                            }
                        }
                        UpdateStatus(status);
                    }
                    _mainHost?.RecordQueryHistory(_databaseName, sql, status, sw.ElapsedMilliseconds, dt.Rows.Count, true);
                }
                else
                {
                    _lastResultSql = "";
                    _lastResultCanStreamExport = false;
                    var result = await Task.Run(
                        () => _db.ExecSQL(sql),
                        _cts.Token);
                    if (!CanUpdateUi()) return;

                    sw.Stop();
                    dgvResults.DataSource = null;

                    string resultStatus = GetResultValue(result, "status");
                    if (string.Equals(resultStatus, "OK", StringComparison.OrdinalIgnoreCase))
                    {
                        string status = string.Format(
                            "OK  |  {0} ms", sw.ElapsedMilliseconds);
                        UpdateStatus(status);
                        ShowResultFeedback(Localization.T("Common.Success"), status);
                        _mainHost?.RecordQueryHistory(_databaseName, sql, status, sw.ElapsedMilliseconds, -1, false);
                    }
                    else
                    {
                        string reason = BuildQueryFailureReason(GetResultValue(result, "reason"));
                        string status = Localization.Format("Query.ErrorStatus", reason);
                        UpdateStatus(status);
                        ShowResultFeedback(Localization.T("Query.ExecuteError"), reason);
                        _mainHost?.RecordQueryHistory(_databaseName, sql, status, sw.ElapsedMilliseconds, -1, false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (!CanUpdateUi()) return;
                sw.Stop();
                UpdateStatus(Localization.T("Query.Cancelled"));
            }
            catch (Exception ex)
            {
                if (!CanUpdateUi()) return;
                sw.Stop();
                string reason = BuildQueryExceptionMessage(ex);
                string status = Localization.Format("Query.ErrorStatus", reason);
                UpdateStatus(status);
                ShowResultFeedback(Localization.T("Query.QueryError"), reason);
                _mainHost?.RecordQueryHistory(_databaseName, sql, status, sw.ElapsedMilliseconds, -1, false);
            }
            finally
            {
                if (CanUpdateUi())
                {
                    tsBtnExecute.Enabled = true;
                    tsBtnCancel.Enabled = false;
                    tsBtnRefresh.Enabled = true;
                    btnDataRefresh.Enabled = true;
                    if (_isTableDataMode) ApplyTableDataEditability();
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

        private static string GetQueryError(DataTable table)
        {
            if (table == null || !table.ExtendedProperties.ContainsKey(my_sqlite.QueryErrorExtendedProperty))
            {
                return string.Empty;
            }

            return table.ExtendedProperties[my_sqlite.QueryErrorExtendedProperty]?.ToString() ?? string.Empty;
        }

        private void UpdateStatus(string msg)
        {
            lblStatus.Text = msg;
            if (_isDocked && _mainHost != null)
            {
                _mainHost.UpdateMainStatus(msg);
            }
        }

        private void ShowLoadingOverlay()
        {
            if (loadingOverlay == null) return;

            UpdateLoadingOverlayText();
            loadingOverlay.Visible = true;
            loadingOverlay.BringToFront();
        }

        private void HideLoadingOverlay()
        {
            if (loadingOverlay == null) return;

            loadingOverlay.Visible = false;
            loadingOverlay.SendToBack();
        }

        private void UpdateLoadingOverlayText()
        {
            if (lblLoadingTitle != null) lblLoadingTitle.Text = Localization.T("Query.LoadingPleaseWait");
            if (lblLoadingMessage != null) lblLoadingMessage.Text = Localization.T("Query.LoadingTablePage");
        }

        private string BuildQueryStatus(int rowCount, long elapsedMilliseconds)
        {
            if (rowCount == 0)
            {
                return Localization.T("Query.NoRowsStatus") + "  |  " + elapsedMilliseconds + " ms";
            }

            return string.Format("OK  |  {0} rows  |  {1} ms", rowCount, elapsedMilliseconds);
        }

        private void ShowResultFeedback(string title, string message)
        {
            if (dgvResults == null) return;

            DataTable feedback = new DataTable();
            feedback.Columns.Add(Localization.T("Query.FeedbackTypeColumn"));
            feedback.Columns.Add(Localization.T("Query.FeedbackMessageColumn"));
            DataRow row = feedback.NewRow();
            row[0] = title ?? string.Empty;
            row[1] = message ?? string.Empty;
            feedback.Rows.Add(row);
            feedback.AcceptChanges();

            using (new GridRedrawScope(dgvResults))
            {
                dgvResults.ReadOnly = true;
                BindResultsTable(feedback);
            }
            if (tsBtnExport != null) tsBtnExport.Enabled = false;
        }

        private static string GetResultValue(Dictionary<string, string> result, string key)
        {
            string value;
            return result != null && result.TryGetValue(key, out value) ? value : string.Empty;
        }

        private static string BuildQueryExceptionMessage(Exception ex)
        {
            string message = ex == null ? null : ex.Message;
            return BuildQueryFailureReason(message);
        }

        private static string BuildQueryFailureReason(string reason)
        {
            return string.IsNullOrWhiteSpace(reason) ? Localization.T("Query.UnknownError") : reason;
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
            ApplyDataToolbarButtonTooltips();
            UpdateLoadingOverlayText();
            if (lblStatus != null && (lblStatus.Text == "Ready" || lblStatus.Text == "就緒")) lblStatus.Text = Localization.T("Status.Ready");
            RefreshResultsContextMenu();
            UpdatePaginationUI();
            Localization.ApplyTo(this);
            ApplyTheme();
        }

        private void ApplyDataToolbarButtonTooltips()
        {
            ApplyToolStripTooltip(btnDataAdd, Localization.T("Query.DataAddTooltip"));
            ApplyToolStripTooltip(btnDataDelete, Localization.T("Query.DataDeleteTooltip"));
            ApplyToolStripTooltip(btnDataApply, Localization.T("Query.DataApplyTooltip"));
            ApplyToolStripTooltip(btnDataCancel, Localization.T("Query.DataCancelTooltip"));
            ApplyToolStripTooltip(btnDataRefresh, Localization.T("Query.DataRefreshTooltip"));
            ApplyToolStripTooltip(btnDataFirst, Localization.T("Query.DataFirstPageTooltip"));
            ApplyToolStripTooltip(btnDataPrev, Localization.T("Query.DataPreviousPageTooltip"));
            ApplyToolStripTooltip(btnDataNext, Localization.T("Query.DataNextPageTooltip"));
            ApplyToolStripTooltip(btnDataLast, Localization.T("Query.DataLastPageTooltip"));
        }

        private static void ApplyToolStripTooltip(ToolStripItem item, string tooltip)
        {
            if (item == null) return;
            bool showTooltip = ApplicationOptionSettings.GetBool("ShowObjectTooltips") && !string.IsNullOrWhiteSpace(tooltip);
            item.AutoToolTip = showTooltip;
            item.ToolTipText = showTooltip ? tooltip : string.Empty;
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
                txtSql.WordWrap = ApplicationOptionSettings.GetBool("EditorWordWrap");
                bool helperEnabledForSize = ShouldUseEditorHelpers(txtSql.Text);
                if (ApplicationOptionSettings.GetBool("EditorSyntaxHighlight") && helperEnabledForSize) ApplySyntaxHighlight();
                else if (helperEnabledForSize) ApplyPlainEditorFormatting();
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
                Font configuredGridFont = BuildConfiguredFont("RecordGridFontName", "Microsoft JhengHei UI", "RecordGridFontSize", 9);
                ApplyConfiguredResultGridAppearance(dgvResults, configuredGridFont);
                if (dgvResults.ContextMenuStrip != null) ThemeManager.ApplyToolStrip(dgvResults.ContextMenuStrip);
            }
            if (loadingOverlay != null)
            {
                loadingOverlay.BackColor = ThemeManager.WindowBackColor;
                foreach (Control child in loadingOverlay.Controls)
                {
                    child.BackColor = ThemeManager.ElevatedColor;
                    child.ForeColor = ThemeManager.TextColor;
                }
            }
            if (lblLoadingTitle != null)
            {
                lblLoadingTitle.BackColor = ThemeManager.ElevatedColor;
                lblLoadingTitle.ForeColor = ThemeManager.TextColor;
            }
            if (lblLoadingMessage != null)
            {
                lblLoadingMessage.BackColor = ThemeManager.ElevatedColor;
                lblLoadingMessage.ForeColor = ThemeManager.MutedTextColor;
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
                resultsBinarySeparator = new ToolStripSeparator();
                resultsContextMenu.Items.Add(resultsBinarySeparator);
                resultsViewBlobHexItem = AddResultsMenuItem(resultsContextMenu, "viewBlobHex", (s, e) => ViewSelectedBlobHex());
                resultsCopyBlobHexItem = AddResultsMenuItem(resultsContextMenu, "copyBlobHex", (s, e) => CopySelectedBlobHex());
                resultsSaveBlobFileItem = AddResultsMenuItem(resultsContextMenu, "saveBlobFile", (s, e) => SaveSelectedBlobToFile());
                resultsImportBlobFileItem = AddResultsMenuItem(resultsContextMenu, "importBlobFile", (s, e) => ImportBlobFromFile());
                resultsGeometrySeparator = new ToolStripSeparator();
                resultsContextMenu.Items.Add(resultsGeometrySeparator);
                resultsCopyGeometryWktItem = AddResultsMenuItem(resultsContextMenu, "copyGeometryWkt", (s, e) => CopySelectedGeometryAsWkt());
                resultsCopyWktGeometrySqlItem = AddResultsMenuItem(resultsContextMenu, "copyWktGeometrySql", (s, e) => CopySelectedWktAsGeometrySql());
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
            menu.Items.Add(new ToolStripSeparator());
            AddResultsMenuItem(menu, "viewBlobHex", (s, e) => ViewSelectedBlobHex()).Text = Localization.T("Query.ViewBlobHex");
            AddResultsMenuItem(menu, "copyBlobHex", (s, e) => CopySelectedBlobHex()).Text = Localization.T("Query.CopyBlobHex");
            AddResultsMenuItem(menu, "saveBlobFile", (s, e) => SaveSelectedBlobToFile()).Text = Localization.T("Query.SaveBlobFile");
            AddResultsMenuItem(menu, "importBlobFile", (s, e) => ImportBlobFromFile()).Text = Localization.T("Query.ImportBlobFile");
            menu.Items.Add(new ToolStripSeparator());
            AddResultsMenuItem(menu, "copyGeometryWkt", (s, e) => CopySelectedGeometryAsWkt()).Text = Localization.T("Query.CopyGeometryAsWkt");
            AddResultsMenuItem(menu, "copyWktGeometrySql", (s, e) => CopySelectedWktAsGeometrySql()).Text = Localization.T("Query.CopyWktAsGeometrySql");
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
            if (resultsViewBlobHexItem != null) resultsViewBlobHexItem.Text = Localization.T("Query.ViewBlobHex");
            if (resultsCopyBlobHexItem != null) resultsCopyBlobHexItem.Text = Localization.T("Query.CopyBlobHex");
            if (resultsSaveBlobFileItem != null) resultsSaveBlobFileItem.Text = Localization.T("Query.SaveBlobFile");
            if (resultsImportBlobFileItem != null) resultsImportBlobFileItem.Text = Localization.T("Query.ImportBlobFile");
            if (resultsCopyGeometryWktItem != null) resultsCopyGeometryWktItem.Text = Localization.T("Query.CopyGeometryAsWkt");
            if (resultsCopyWktGeometrySqlItem != null) resultsCopyWktGeometrySqlItem.Text = Localization.T("Query.CopyWktAsGeometrySql");
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
            object currentValue = GetCurrentResultCellValue();
            bool isBinaryCell = IsCurrentResultCellBinaryColumn();
            bool hasBlobValue = currentValue is byte[];
            bool canImportBlob = _isTableDataMode && isBinaryCell;
            bool canCopyGeometryWkt = currentValue is byte[] bytes && GeometryWktConverter.TryGeometryBytesToWkt(bytes, out _);
            bool canCopyWktGeometrySql = currentValue != null && LooksLikeWkt(currentValue.ToString());
            SetResultsMenuItemEnabled(menu, "copyCells", hasSelection);
            SetResultsMenuItemEnabled(menu, "copyHeaders", hasSelection);
            SetResultsMenuItemEnabled(menu, "copyRows", GetSelectedResultRows().Count > 0);
            SetResultsMenuItemEnabled(menu, "selectAll", hasData);
            SetResultsMenuItemEnabled(menu, "export", hasData);
            SetResultsMenuItemEnabled(menu, "viewBlobHex", hasBlobValue);
            SetResultsMenuItemEnabled(menu, "copyBlobHex", hasBlobValue);
            SetResultsMenuItemEnabled(menu, "saveBlobFile", hasBlobValue);
            SetResultsMenuItemEnabled(menu, "importBlobFile", canImportBlob && CanEditTableData());
            SetResultsMenuItemEnabled(menu, "copyGeometryWkt", canCopyGeometryWkt);
            SetResultsMenuItemEnabled(menu, "copyWktGeometrySql", canCopyWktGeometrySql);
            SetResultsMenuItemEnabled(menu, "addRow", CanEditTableData() && dgvResults != null && dgvResults.DataSource is DataTable);
            SetResultsMenuItemEnabled(menu, "deleteRow", CanEditTableData() && GetSelectedResultRows().Count > 0);
            SetResultsMenuItemEnabled(menu, "saveRows", CanEditTableData() && dgvResults != null && dgvResults.DataSource is DataTable);

            bool showBlobTools = isBinaryCell || hasBlobValue;
            if (resultsBinarySeparator != null) resultsBinarySeparator.Visible = showBlobTools;
            if (resultsViewBlobHexItem != null) resultsViewBlobHexItem.Visible = hasBlobValue;
            if (resultsCopyBlobHexItem != null) resultsCopyBlobHexItem.Visible = hasBlobValue;
            if (resultsSaveBlobFileItem != null) resultsSaveBlobFileItem.Visible = hasBlobValue;
            if (resultsImportBlobFileItem != null) resultsImportBlobFileItem.Visible = canImportBlob;

            bool showGeometryTools = canCopyGeometryWkt || canCopyWktGeometrySql;
            if (resultsGeometrySeparator != null) resultsGeometrySeparator.Visible = showGeometryTools;
            if (resultsCopyGeometryWktItem != null) resultsCopyGeometryWktItem.Visible = showGeometryTools;
            if (resultsCopyWktGeometrySqlItem != null) resultsCopyWktGeometrySqlItem.Visible = showGeometryTools;
        }

        private static void SetResultsMenuItemEnabled(ContextMenuStrip menu, string name, bool enabled)
        {
            ToolStripItem[] items = menu.Items.Find(name, false);
            if (items.Length > 0)
            {
                items[0].Enabled = enabled;
            }
        }

        private object GetCurrentResultCellValue()
        {
            if (dgvResults == null || dgvResults.CurrentCell == null) return null;
            if (dgvResults.CurrentCell.RowIndex < 0 || dgvResults.CurrentCell.ColumnIndex < 0) return null;
            return dgvResults.CurrentCell.Value;
        }

        private byte[] GetCurrentResultBlob()
        {
            return GetCurrentResultCellValue() as byte[];
        }

        private bool IsCurrentResultCellBinaryColumn()
        {
            if (dgvResults == null || dgvResults.CurrentCell == null) return false;
            if (dgvResults.CurrentCell.RowIndex < 0 || dgvResults.CurrentCell.ColumnIndex < 0) return false;

            DataTable source = dgvResults.DataSource as DataTable;
            if (source == null) return false;
            DataGridViewColumn column = dgvResults.Columns[dgvResults.CurrentCell.ColumnIndex];
            return IsBinaryDataGridColumn(source, column);
        }

        private string GetCurrentResultColumnName()
        {
            if (dgvResults == null || dgvResults.CurrentCell == null || dgvResults.CurrentCell.ColumnIndex < 0) return "";
            DataGridViewColumn column = dgvResults.Columns[dgvResults.CurrentCell.ColumnIndex];
            if (column == null) return "";
            string columnName = column.DataPropertyName;
            if (string.IsNullOrWhiteSpace(columnName)) columnName = column.Name;
            return columnName ?? "";
        }

        private void ViewSelectedBlobHex()
        {
            byte[] bytes = GetCurrentResultBlob();
            if (bytes == null)
            {
                MessageBox.Show(Localization.T("Query.BlobRequired"), Localization.T("Common.Info"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            const int pageSize = 4096;
            int currentPage = 0;
            int totalPages = Math.Max(1, (int)Math.Ceiling(bytes.Length / (double)pageSize));

            using (Form dialog = new Form())
            using (RichTextBox text = new RichTextBox())
            using (Label pageLabel = new Label())
            using (Button firstButton = new Button())
            using (Button previousButton = new Button())
            using (Button nextButton = new Button())
            using (Button lastButton = new Button())
            using (Button copyPageButton = new Button())
            using (Button closeButton = new Button())
            {
                dialog.Text = Localization.T("Query.ViewBlobHex");
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.Size = new Size(820, 560);
                dialog.MinimizeBox = false;
                dialog.ShowInTaskbar = false;

                text.Dock = DockStyle.Fill;
                text.ReadOnly = true;
                text.WordWrap = false;
                text.Font = new Font("Consolas", 10);

                FlowLayoutPanel footer = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    Height = 44,
                    FlowDirection = FlowDirection.RightToLeft,
                    Padding = new Padding(8)
                };
                pageLabel.AutoSize = true;
                pageLabel.TextAlign = ContentAlignment.MiddleLeft;
                pageLabel.Padding = new Padding(0, 7, 12, 0);
                pageLabel.ForeColor = ThemeManager.TextColor;
                firstButton.Text = Localization.T("Query.BlobFirstPage");
                firstButton.AutoSize = true;
                previousButton.Text = Localization.T("Query.BlobPreviousPage");
                previousButton.AutoSize = true;
                nextButton.Text = Localization.T("Query.BlobNextPage");
                nextButton.AutoSize = true;
                lastButton.Text = Localization.T("Query.BlobLastPage");
                lastButton.AutoSize = true;
                copyPageButton.Text = Localization.T("Query.BlobCopyPageHex");
                copyPageButton.AutoSize = true;
                closeButton.Text = Localization.T("Common.Close");
                closeButton.AutoSize = true;

                Action renderPage = () =>
                {
                    int offset = currentPage * pageSize;
                    int count = Math.Min(pageSize, Math.Max(0, bytes.Length - offset));
                    text.Text = BuildHexDump(bytes, offset, count);
                    int startByte = bytes.Length == 0 ? 0 : offset + 1;
                    int endByte = bytes.Length == 0 ? 0 : offset + count;
                    pageLabel.Text = Localization.Format("Query.BlobPageFormat", currentPage + 1, totalPages, startByte, endByte, bytes.Length);
                    firstButton.Enabled = currentPage > 0;
                    previousButton.Enabled = currentPage > 0;
                    nextButton.Enabled = currentPage < totalPages - 1;
                    lastButton.Enabled = currentPage < totalPages - 1;
                    copyPageButton.Enabled = count > 0;
                };

                firstButton.Click += (s, e) => { currentPage = 0; renderPage(); };
                previousButton.Click += (s, e) => { if (currentPage > 0) currentPage--; renderPage(); };
                nextButton.Click += (s, e) => { if (currentPage < totalPages - 1) currentPage++; renderPage(); };
                lastButton.Click += (s, e) => { currentPage = totalPages - 1; renderPage(); };
                copyPageButton.Click += (s, e) =>
                {
                    int offset = currentPage * pageSize;
                    int count = Math.Min(pageSize, Math.Max(0, bytes.Length - offset));
                    Clipboard.SetText(BytesToHex(bytes, offset, count));
                    UpdateStatus(Localization.T("Query.CopiedToClipboard"));
                };
                closeButton.Click += (s, e) => dialog.Close();
                footer.Controls.Add(closeButton);
                footer.Controls.Add(copyPageButton);
                footer.Controls.Add(lastButton);
                footer.Controls.Add(nextButton);
                footer.Controls.Add(previousButton);
                footer.Controls.Add(firstButton);
                footer.Controls.Add(pageLabel);

                dialog.Controls.Add(text);
                dialog.Controls.Add(footer);
                ThemeManager.ApplyTo(dialog);
                renderPage();
                dialog.ShowDialog(this);
            }
        }

        private void CopySelectedBlobHex()
        {
            byte[] bytes = GetCurrentResultBlob();
            if (bytes == null)
            {
                MessageBox.Show(Localization.T("Query.BlobRequired"), Localization.T("Common.Info"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Clipboard.SetText(BytesToHex(bytes));
            UpdateStatus(Localization.T("Query.CopiedToClipboard"));
        }

        private void SaveSelectedBlobToFile()
        {
            byte[] bytes = GetCurrentResultBlob();
            if (bytes == null)
            {
                MessageBox.Show(Localization.T("Query.BlobRequired"), Localization.T("Common.Info"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = Localization.T("Query.SaveBlobFile");
                dialog.Filter = Localization.T("Query.BlobFileFilter");
                dialog.FileName = BuildBlobFileName();
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                long streamedBytes;
                string streamError;
                if (!TrySaveSelectedTableBlobByStreaming(dialog.FileName, out streamedBytes, out streamError))
                {
                    BinaryCellStreamingService.WriteBytesToFile(bytes, dialog.FileName, (written, total) =>
                    {
                        UpdateBlobExportProgress(written, total);
                    });
                }

                UpdateStatus(Localization.Format("Query.BlobSaved", dialog.FileName));
            }
        }

        private bool TrySaveSelectedTableBlobByStreaming(string targetPath, out long writtenBytes, out string errorMessage)
        {
            writtenBytes = 0;
            errorMessage = "";

            try
            {
                if (!_isTableDataMode || dgvResults == null || dgvResults.CurrentCell == null) return false;
                if (dgvResults.CurrentCell.RowIndex < 0 || dgvResults.CurrentCell.ColumnIndex < 0) return false;

                string tableName = GetTableNameFromSql();
                string columnName = GetCurrentResultColumnName();
                if (string.IsNullOrWhiteSpace(tableName) || string.IsNullOrWhiteSpace(columnName)) return false;

                DataGridViewRow gridRow = dgvResults.Rows[dgvResults.CurrentCell.RowIndex];
                DataRowView rowView = gridRow.DataBoundItem as DataRowView;
                if (rowView == null || rowView.Row == null || rowView.Row.RowState != DataRowState.Unchanged) return false;

                List<TableColumnInfo> columns = GetTableColumns(tableName);
                List<string> primaryKeys = columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
                if (primaryKeys.Count == 0) return false;

                foreach (string primaryKey in primaryKeys)
                {
                    if (!rowView.Row.Table.Columns.Contains(primaryKey)) return false;
                }

                Dictionary<string, object> parameters = new Dictionary<string, object>();
                int index = 0;
                string whereSql = BuildWhereClause(rowView.Row, columns, primaryKeys, parameters, ref index);
                string sql = "SELECT " + QuoteIdentifier(columnName) +
                             " FROM " + GetQualifiedTableName(tableName) +
                             " WHERE " + whereSql + ";";

                writtenBytes = BinaryCellStreamingService.WriteFirstColumnToFile(_db, sql, parameters, targetPath, (written, total) =>
                {
                    UpdateBlobExportProgress(written, total);
                });
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private void UpdateBlobExportProgress(long written, long total)
        {
            if (lblStatus == null) return;

            string totalText = total >= 0 ? FormatByteCount(total) : "?";
            lblStatus.Text = BuildBlobStreamingProgressText(written, total);
            Application.DoEvents();
        }

        private static string BuildBlobStreamingProgressText(long written, long total)
        {
            string totalText = total >= 0 ? FormatByteCount(total) : "?";
            return Localization.Format("Query.BlobStreamingProgress", FormatByteCount(written), totalText);
        }

        private static string FormatByteCount(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double value = bytes / 1024d;
            if (value < 1024) return value.ToString("0.##") + " KB";
            value /= 1024d;
            if (value < 1024) return value.ToString("0.##") + " MB";
            value /= 1024d;
            return value.ToString("0.##") + " GB";
        }

        private void ImportBlobFromFile()
        {
            if (!IsCurrentResultCellBinaryColumn())
            {
                MessageBox.Show(Localization.T("Query.BlobRequired"), Localization.T("Common.Info"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = Localization.T("Query.ImportBlobFile");
                dialog.Filter = Localization.T("Query.BlobImportFileFilter");
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                SetCurrentResultBlob(File.ReadAllBytes(dialog.FileName));
                UpdateStatus(Localization.Format("Query.BlobImported", dialog.FileName));
            }
        }

        private void SetCurrentResultBlob(byte[] bytes)
        {
            if (bytes == null) bytes = new byte[0];
            if (!IsCurrentResultCellBinaryColumn())
            {
                throw new InvalidOperationException(Localization.T("Query.BlobRequired"));
            }

            DataGridViewCell cell = dgvResults.CurrentCell;
            string columnName = GetCurrentResultColumnName();
            if (string.IsNullOrWhiteSpace(columnName)) throw new InvalidOperationException(Localization.T("Query.BlobRequired"));

            DataRowView rowView = dgvResults.Rows[cell.RowIndex].DataBoundItem as DataRowView;
            if (rowView == null || !rowView.Row.Table.Columns.Contains(columnName))
            {
                throw new InvalidOperationException(Localization.T("Query.BlobRequired"));
            }

            rowView.Row[columnName] = bytes;
            dgvResults.InvalidateCell(cell);
        }

        private void CopySelectedGeometryAsWkt()
        {
            object value = GetCurrentResultCellValue();
            if (!(value is byte[] bytes) || !GeometryWktConverter.TryGeometryBytesToWkt(bytes, out string wkt))
            {
                MessageBox.Show(Localization.T("Query.GeometryToWktFailed"), Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Clipboard.SetText(wkt);
            UpdateStatus(Localization.T("Query.CopiedToClipboard"));
        }

        private void CopySelectedWktAsGeometrySql()
        {
            object value = GetCurrentResultCellValue();
            string wkt = value == null ? "" : value.ToString().Trim();
            if (!LooksLikeWkt(wkt))
            {
                MessageBox.Show(Localization.T("Query.WktRequired"), Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Clipboard.SetText(BuildGeometrySqlExpression(wkt));
            UpdateStatus(Localization.T("Query.CopiedToClipboard"));
        }

        private string BuildGeometrySqlExpression(string wkt)
        {
            string literal = "'" + wkt.Replace("'", "''") + "'";
            if (IsSqlServerProvider()) return "geometry::STGeomFromText(" + literal + ", 0)";
            if (IsOracleProvider()) return "SDO_GEOMETRY(" + literal + ", 0)";
            if (IsSqliteProvider()) return "ST_GeomFromText(" + literal + ", 0)";
            return "ST_GeomFromText(" + literal + ")";
        }

        private static bool LooksLikeWkt(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            string text = value.TrimStart();
            string[] prefixes =
            {
                "POINT", "LINESTRING", "POLYGON", "MULTIPOINT", "MULTILINESTRING",
                "MULTIPOLYGON", "GEOMETRYCOLLECTION"
            };

            foreach (string prefix in prefixes)
            {
                if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (text.Length == prefix.Length) return false;
                char next = text[prefix.Length];
                return char.IsWhiteSpace(next) || next == '(';
            }

            return false;
        }

        private string BuildBlobFileName()
        {
            string columnName = "blob";
            if (dgvResults != null && dgvResults.CurrentCell != null && dgvResults.CurrentCell.ColumnIndex >= 0)
            {
                DataGridViewColumn column = dgvResults.Columns[dgvResults.CurrentCell.ColumnIndex];
                if (column != null && !string.IsNullOrWhiteSpace(column.HeaderText))
                {
                    columnName = column.HeaderText;
                }
            }

            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                columnName = columnName.Replace(invalid, '_');
            }
            return columnName + ".bin";
        }

        private static string BytesToHex(byte[] bytes)
        {
            if (bytes == null) return string.Empty;
            return BytesToHex(bytes, 0, bytes.Length);
        }

        private static string BytesToHex(byte[] bytes, int offset, int count)
        {
            if (bytes == null || bytes.Length == 0) return string.Empty;
            int safeOffset = Math.Max(0, Math.Min(offset, bytes.Length));
            int length = Math.Min(Math.Max(0, count), bytes.Length - safeOffset);
            StringBuilder sb = new StringBuilder(length * 2);
            for (int i = 0; i < length; i++)
            {
                sb.Append(bytes[safeOffset + i].ToString("X2"));
            }
            return sb.ToString();
        }

        private static string BuildHexDump(byte[] bytes, int maxBytes)
        {
            if (bytes == null) return string.Empty;

            int length = Math.Min(bytes.Length, Math.Max(0, maxBytes));
            StringBuilder sb = new StringBuilder();
            for (int offset = 0; offset < length; offset += 16)
            {
                int lineLength = Math.Min(16, length - offset);
                sb.Append(offset.ToString("X8"));
                sb.Append("  ");
                for (int i = 0; i < 16; i++)
                {
                    if (i < lineLength) sb.Append(bytes[offset + i].ToString("X2"));
                    else sb.Append("  ");
                    sb.Append(i == 7 ? "  " : " ");
                }
                sb.Append(" ");
                for (int i = 0; i < lineLength; i++)
                {
                    byte value = bytes[offset + i];
                    sb.Append(value >= 32 && value <= 126 ? (char)value : '.');
                }
                sb.AppendLine();
            }

            if (bytes.Length > length)
            {
                sb.AppendLine();
                sb.AppendLine(Localization.Format("Query.BlobPreviewTruncated", length, bytes.Length));
            }

            return sb.ToString();
        }

        private static string BuildHexDump(byte[] bytes, int offset, int count)
        {
            if (bytes == null) return string.Empty;

            int safeOffset = Math.Max(0, Math.Min(offset, bytes.Length));
            int length = Math.Min(Math.Max(0, count), bytes.Length - safeOffset);
            StringBuilder sb = new StringBuilder();
            for (int relativeOffset = 0; relativeOffset < length; relativeOffset += 16)
            {
                int lineLength = Math.Min(16, length - relativeOffset);
                int absoluteOffset = safeOffset + relativeOffset;
                sb.Append(absoluteOffset.ToString("X8"));
                sb.Append("  ");
                for (int i = 0; i < 16; i++)
                {
                    if (i < lineLength) sb.Append(bytes[absoluteOffset + i].ToString("X2"));
                    else sb.Append("  ");
                    sb.Append(i == 7 ? "  " : " ");
                }
                sb.Append(" ");
                for (int i = 0; i < lineLength; i++)
                {
                    byte value = bytes[absoluteOffset + i];
                    sb.Append(value >= 32 && value <= 126 ? (char)value : '.');
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private void DgvResults_CellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (dgvResults.Rows[e.RowIndex].Cells[e.ColumnIndex].Selected) return;

            dgvResults.ClearSelection();
            dgvResults.CurrentCell = dgvResults.Rows[e.RowIndex].Cells[e.ColumnIndex];
            dgvResults.Rows[e.RowIndex].Cells[e.ColumnIndex].Selected = true;
        }

        private void DgvResults_DataBindingComplete(object sender, DataGridViewBindingCompleteEventArgs e)
        {
            ConfigureBinaryResultColumns();
            ApplyConfiguredResultGridAppearance(dgvResults, dgvResults.DefaultCellStyle.Font);
        }

        private void DgvResults_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            object formattedValue;
            if (TryFormatConfiguredResultCellValue(e.Value, out formattedValue))
            {
                e.Value = formattedValue;
                e.FormattingApplied = true;
            }
        }

        private void DgvResults_DataError(object sender, DataGridViewDataErrorEventArgs e)
        {
            e.ThrowException = false;
            e.Cancel = false;
        }

        private void BindResultsTable(DataTable table)
        {
            if (dgvResults == null) return;
            dgvResults.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            dgvResults.DataSource = table;
            AutoResizeColumns(dgvResults);
        }

        private static void SetGridRedraw(DataGridView grid, bool enabled)
        {
            if (grid == null || grid.IsDisposed || grid.Disposing || !grid.IsHandleCreated) return;
            SendMessage(grid.Handle, WM_SETREDRAW, enabled, IntPtr.Zero);
        }

        private static void EnableGridDoubleBuffer(DataGridView grid)
        {
            if (grid == null) return;

            try
            {
                PropertyInfo property = typeof(DataGridView).GetProperty(
                    "DoubleBuffered",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                property?.SetValue(grid, true, null);
            }
            catch
            {
                // 非關鍵最佳化，避免反射失敗影響查詢結果顯示。
            }
        }

        private void ConfigureBinaryResultColumns()
        {
            if (dgvResults == null || dgvResults.Columns.Count == 0) return;

            DataTable source = dgvResults.DataSource as DataTable;
            if (source == null) return;

            for (int i = 0; i < dgvResults.Columns.Count; i++)
            {
                DataGridViewColumn gridColumn = dgvResults.Columns[i];
                if (!IsBinaryDataGridColumn(source, gridColumn)) continue;

                if (!(gridColumn is DataGridViewTextBoxColumn))
                {
                    DataGridViewTextBoxColumn textColumn = new DataGridViewTextBoxColumn
                    {
                        Name = gridColumn.Name,
                        HeaderText = gridColumn.HeaderText,
                        DataPropertyName = gridColumn.DataPropertyName,
                        ReadOnly = true,
                        SortMode = DataGridViewColumnSortMode.Automatic,
                        Width = gridColumn.Width,
                        DisplayIndex = gridColumn.DisplayIndex
                    };
                    dgvResults.Columns.RemoveAt(i);
                    dgvResults.Columns.Insert(i, textColumn);
                    gridColumn = textColumn;
                }

                gridColumn.ReadOnly = true;
                gridColumn.DefaultCellStyle.NullValue = "";
            }
        }

        private static bool IsBinaryDataGridColumn(DataTable source, DataGridViewColumn gridColumn)
        {
            string columnName = gridColumn.DataPropertyName;
            if (string.IsNullOrWhiteSpace(columnName)) columnName = gridColumn.Name;
            if (string.IsNullOrWhiteSpace(columnName) || !source.Columns.Contains(columnName)) return false;
            return source.Columns[columnName].DataType == typeof(byte[]);
        }

        private static string FormatBinaryCellValue(byte[] bytes)
        {
            if (bytes == null) return "";
            if (GeometryWktConverter.TryGeometryBytesToWkt(bytes, out string wkt))
            {
                return Localization.Format("Grid.GeometryPreview", wkt);
            }

            int previewLength = Math.Min(bytes.Length, 12);
            StringBuilder preview = new StringBuilder(previewLength * 2);
            for (int i = 0; i < previewLength; i++)
            {
                preview.Append(bytes[i].ToString("X2"));
            }
            if (bytes.Length > previewLength) preview.Append("...");

            return Localization.Format("Grid.BinaryPreview", bytes.Length, preview.ToString());
        }

        private static bool TryFormatConfiguredResultCellValue(object value, out object formattedValue)
        {
            formattedValue = value;
            if (value == null || value == DBNull.Value) return false;

            byte[] bytes = value as byte[];
            if (bytes != null)
            {
                formattedValue = FormatBinaryCellValue(bytes);
                return true;
            }

            DateTime dateTime;
            if (TryGetDateTimeValue(value, out dateTime))
            {
                string format = GetConfiguredDateTimeFormat(dateTime);
                if (string.IsNullOrWhiteSpace(format)) return false;

                string text;
                if (TryFormatDateTime(dateTime, format, out text))
                {
                    formattedValue = text;
                    return true;
                }
                return false;
            }

            TimeSpan timeSpan;
            if (TryGetTimeSpanValue(value, out timeSpan))
            {
                string format = ApplicationOptionSettings.GetString("RecordTimeFormat");
                if (string.IsNullOrWhiteSpace(format)) return false;

                string text;
                if (TryFormatTimeSpan(timeSpan, format, out text))
                {
                    formattedValue = text;
                    return true;
                }
                return false;
            }

            if (IsNumericValue(value))
            {
                formattedValue = FormatConfiguredNumber(value);
                return true;
            }

            return false;
        }

        private static bool TryGetDateTimeValue(object value, out DateTime dateTime)
        {
            if (value is DateTime)
            {
                dateTime = (DateTime)value;
                return true;
            }

            dateTime = DateTime.MinValue;
            return false;
        }

        private static bool TryGetTimeSpanValue(object value, out TimeSpan timeSpan)
        {
            if (value is TimeSpan)
            {
                timeSpan = (TimeSpan)value;
                return true;
            }

            timeSpan = TimeSpan.Zero;
            return false;
        }

        private static string GetConfiguredDateTimeFormat(DateTime value)
        {
            string dateTimeFormat = ApplicationOptionSettings.GetString("RecordDateTimeFormat");
            string dateFormat = ApplicationOptionSettings.GetString("RecordDateFormat");

            if (value.TimeOfDay == TimeSpan.Zero && !string.IsNullOrWhiteSpace(dateFormat)) return dateFormat;
            if (!string.IsNullOrWhiteSpace(dateTimeFormat)) return dateTimeFormat;
            return value.TimeOfDay == TimeSpan.Zero ? dateFormat : string.Empty;
        }

        private static bool TryFormatDateTime(DateTime value, string format, out string text)
        {
            try
            {
                text = value.ToString(format, CultureInfo.CurrentCulture);
                return true;
            }
            catch
            {
                text = string.Empty;
                return false;
            }
        }

        private static bool TryFormatTimeSpan(TimeSpan value, string format, out string text)
        {
            try
            {
                DateTime display = DateTime.Today.Add(value);
                text = display.ToString(format, CultureInfo.CurrentCulture);
                return true;
            }
            catch
            {
                text = string.Empty;
                return false;
            }
        }

        private static bool IsNumericValue(object value)
        {
            if (value == null) return false;
            Type type = value.GetType();
            return type == typeof(byte) ||
                   type == typeof(sbyte) ||
                   type == typeof(short) ||
                   type == typeof(ushort) ||
                   type == typeof(int) ||
                   type == typeof(uint) ||
                   type == typeof(long) ||
                   type == typeof(ulong) ||
                   type == typeof(float) ||
                   type == typeof(double) ||
                   type == typeof(decimal);
        }

        private static string FormatConfiguredNumber(object value)
        {
            bool showThousands = ApplicationOptionSettings.GetBool("RecordShowThousandsSeparator");
            CultureInfo culture = ApplicationOptionSettings.GetBool("RecordUseSystemNumberFormat")
                ? CultureInfo.CurrentCulture
                : CultureInfo.InvariantCulture;

            string format = IsIntegerValue(value)
                ? (showThousands ? "#,0" : "0")
                : (showThousands ? "#,0.#############################" : "0.#############################");

            IFormattable formattable = value as IFormattable;
            return formattable == null ? Convert.ToString(value, culture) : formattable.ToString(format, culture);
        }

        private static void ApplyConfiguredResultGridAppearance(DataGridView grid, Font configuredGridFont)
        {
            if (grid == null) return;
            Font font = configuredGridFont ?? grid.DefaultCellStyle.Font ?? new Font("Microsoft JhengHei UI", 9);
            grid.DefaultCellStyle.Font = font;
            grid.ColumnHeadersDefaultCellStyle.Font = font;
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;

            int rowHeight = GetConfiguredResultGridRowHeight(font);
            grid.RowTemplate.Height = rowHeight;
            if (grid.Rows.Count > GridRowHeightApplyLimit) return;

            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row != null && !row.IsNewRow) row.Height = rowHeight;
            }
        }

        private static int GetConfiguredResultGridRowHeight(Font font)
        {
            int fontHeight = font == null ? 17 : font.Height;
            string mode = ApplicationOptionSettings.GetString("RecordRowHeightMode");
            if (string.Equals(mode, "compact", StringComparison.OrdinalIgnoreCase)) return Math.Max(20, fontHeight + 4);
            if (string.Equals(mode, "comfortable", StringComparison.OrdinalIgnoreCase)) return Math.Max(32, fontHeight + 14);
            return Math.Max(24, fontHeight + 8);
        }

        private static bool IsIntegerValue(object value)
        {
            if (value == null) return false;
            Type type = value.GetType();
            return type == typeof(byte) ||
                   type == typeof(sbyte) ||
                   type == typeof(short) ||
                   type == typeof(ushort) ||
                   type == typeof(int) ||
                   type == typeof(uint) ||
                   type == typeof(long) ||
                   type == typeof(ulong);
        }

        private static bool ShouldUseEditorHelpers(string sql)
        {
            if (string.IsNullOrEmpty(sql)) return true;

            int limitMb = ApplicationOptionSettings.GetInt("EditorLargeFileLimitMb");
            if (limitMb <= 0) limitMb = 10;

            long limitBytes = (long)Math.Max(1, limitMb) * 1024L * 1024L;
            return Encoding.UTF8.GetByteCount(sql) <= limitBytes;
        }

        private bool ResultsHaveDataRows()
        {
            if (dgvResults == null) return false;
            for (int i = 0; i < dgvResults.Rows.Count; i++)
            {
                DataGridViewRow row = dgvResults.Rows[i];
                if (row != null && !row.IsNewRow) return true;
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
            if (dgv == null || dgv.IsDisposed || dgv.Disposing || !dgv.IsHandleCreated || dgv.Columns.Count == 0) return;
            DataGridViewAutoSizeColumnsMode mode = dgv.Rows.Count <= GridFullAutoResizeRowLimit
                ? DataGridViewAutoSizeColumnsMode.DisplayedCells
                : DataGridViewAutoSizeColumnsMode.ColumnHeader;

            try
            {
                dgv.AutoResizeColumns(mode);
            }
            catch (InvalidOperationException)
            {
                if (dgv.IsDisposed || dgv.Disposing) return;
                throw;
            }
            foreach (DataGridViewColumn col in dgv.Columns)
            {
                if (col.Width < 80) col.Width = 80;
                if (col.Width > 300) col.Width = 300;
            }
        }

        private async void SaveChanges()
        {
            if (!_isTableDataMode)
            {
                MessageBox.Show(Localization.T("Query.SaveTableDataOnly"), Localization.T("Common.Info"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!CanEditTableData())
            {
                ShowNoPrimaryKeyReadOnlyMessage();
                return;
            }

            DataTable dt = dgvResults.DataSource as DataTable;
            if (dt == null)
            {
                MessageBox.Show(Localization.T("Query.NoDataToSave"), Localization.T("Common.Info"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            dgvResults.EndEdit();
            BindingContext[dgvResults.DataSource]?.EndCurrentEdit();

            DataTable changes = dt.GetChanges();
            if (changes == null || changes.Rows.Count == 0)
            {
                MessageBox.Show(Localization.T("Query.NoChangesDetected"), Localization.T("Common.Info"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (HasModifiedOrDeletedRows(changes) && IsSavingWithoutPrimaryKey())
            {
                DialogResult riskyAnswer = MessageBox.Show(
                    Localization.T("Query.NoPrimaryKeySaveWarning"),
                    Localization.T("Query.NoPrimaryKeySaveWarningTitle"),
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (riskyAnswer != DialogResult.Yes) return;
            }

            DialogResult answer = MessageBox.Show(
                Localization.T("Query.ConfirmSaveChanges"),
                Localization.T("Query.ConfirmSaveTitle"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;

            tsBtnSave.Enabled = false;
            btnDataApply.Enabled = false;
            tsBtnRefresh.Enabled = false;
            btnDataRefresh.Enabled = false;
            SetPaginationControlsEnabled(false);
            UpdateStatus(Localization.T("Query.SavingChanges"));

            bool reloadAfterSave = false;
            try
            {
                DataSaveResult result = await Task.Run(() => SaveTableChanges(dt));
                if (!CanUpdateUi()) return;
                dt.AcceptChanges();
                UpdateStatus(Localization.Format("Query.SavedChangesStatus", result.Inserted, result.Updated, result.Deleted));
                reloadAfterSave = true;
                MessageBox.Show(Localization.T("Query.DataSaved"), Localization.T("Common.Complete"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                if (!CanUpdateUi()) return;
                string reason = BuildQueryExceptionMessage(ex);
                UpdateStatus(Localization.Format("Query.SaveFailed", reason));
                MessageBox.Show(Localization.Format("Query.SaveFailed", reason), Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (CanUpdateUi())
                {
                    tsBtnSave.Enabled = true;
                    btnDataApply.Enabled = true;
                    tsBtnRefresh.Enabled = true;
                    btnDataRefresh.Enabled = true;
                    ApplyTableDataEditability();
                    if (txtPageSize != null) txtPageSize.Enabled = true;
                    UpdatePaginationUI();
                }
            }

            if (reloadAfterSave && CanUpdateUi())
            {
                ExecutePagedQuery();
            }
        }

        private bool IsSavingWithoutPrimaryKey()
        {
            try
            {
                string tableName = GetTableNameFromSql();
                if (string.IsNullOrWhiteSpace(tableName) || tableName == "Table") return false;

                List<TableColumnInfo> columns = GetTableColumns(tableName);
                return columns.Count > 0 && !columns.Any(c => c.IsPrimaryKey);
            }
            catch
            {
                return false;
            }
        }

        private bool CanEditTableData()
        {
            return _isTableDataMode && !_isNoPrimaryKeyReadOnlyMode;
        }

        private bool ShouldOpenNoPrimaryKeyAsReadOnly(string tableName)
        {
            if (!TableEditSettings.NoPrimaryKeyReadOnly) return false;
            if (string.IsNullOrWhiteSpace(tableName) || tableName == "Table") return false;

            try
            {
                List<TableColumnInfo> columns = GetTableColumns(tableName);
                return columns.Count > 0 && !columns.Any(c => c.IsPrimaryKey);
            }
            catch
            {
                return false;
            }
        }

        private void ApplyTableDataEditability()
        {
            bool editable = CanEditTableData();
            if (dgvResults != null)
            {
                dgvResults.ReadOnly = !editable;
                dgvResults.AllowUserToAddRows = editable;
                dgvResults.AllowUserToDeleteRows = editable;
            }

            if (tsBtnSave != null) tsBtnSave.Enabled = editable;
            if (tsBtnAdd != null) tsBtnAdd.Enabled = editable;
            if (tsBtnDelete != null) tsBtnDelete.Enabled = editable;
            if (btnDataAdd != null) btnDataAdd.Enabled = editable;
            if (btnDataDelete != null) btnDataDelete.Enabled = editable;
            if (btnDataApply != null) btnDataApply.Enabled = editable;
            RefreshResultsContextMenu();

            if (_isNoPrimaryKeyReadOnlyMode)
            {
                UpdateStatus(Localization.T("Query.NoPrimaryKeyReadOnlyStatus"));
            }
        }

        private void ShowNoPrimaryKeyReadOnlyMessage()
        {
            UpdateStatus(Localization.T("Query.NoPrimaryKeyReadOnlyStatus"));
            MessageBox.Show(
                Localization.T("Query.NoPrimaryKeyReadOnlyMessage"),
                Localization.T("Query.NoPrimaryKeySaveWarningTitle"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private static bool HasModifiedOrDeletedRows(DataTable changes)
        {
            if (changes == null) return false;
            foreach (DataRow row in changes.Rows)
            {
                if (row.RowState == DataRowState.Modified || row.RowState == DataRowState.Deleted)
                {
                    return true;
                }
            }
            return false;
        }

        private DataSaveResult SaveTableChanges(DataTable dt)
        {
            string tableName = GetTableNameFromSql();
            if (string.IsNullOrWhiteSpace(_databaseName) || string.IsNullOrWhiteSpace(tableName))
            {
                throw new Exception(Localization.T("Query.CannotDetermineSaveTable"));
            }

            List<TableColumnInfo> columns = GetTableColumns(tableName);
            List<string> primaryKeys = columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
            DataSaveResult result = new DataSaveResult();
            TableSaveTransactionStatements transaction = BuildTableSaveTransactionStatements(_db);
            bool transactionActive = false;

            try
            {
                if (transaction != null)
                {
                    ExecuteTransactionSql(transaction.BeginSql);
                    transactionActive = true;
                }

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

                if (transactionActive)
                {
                    ExecuteTransactionSql(transaction.CommitSql);
                    transactionActive = false;
                }

                return result;
            }
            catch
            {
                if (transactionActive)
                {
                    try
                    {
                        ExecuteTransactionSql(transaction.RollbackSql);
                    }
                    catch
                    {
                    }
                }
                throw;
            }
        }

        private void ExecuteTransactionSql(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql)) return;
            ExecOrThrow(sql, new Dictionary<string, object>(), false);
        }

        private static TableSaveTransactionStatements BuildTableSaveTransactionStatements(IDatabase db)
        {
            if (db == null || !ApplicationOptionSettings.GetBool("RecordAutoBeginTransaction")) return null;

            string provider = NormalizeTransactionProvider(db.ProviderName);
            if (provider == "mysql")
            {
                return new TableSaveTransactionStatements
                {
                    BeginSql = "START TRANSACTION;",
                    CommitSql = "COMMIT;",
                    RollbackSql = "ROLLBACK;"
                };
            }
            if (provider == "postgresql")
            {
                return new TableSaveTransactionStatements
                {
                    BeginSql = "BEGIN;",
                    CommitSql = "COMMIT;",
                    RollbackSql = "ROLLBACK;"
                };
            }
            if (provider == "sqlite")
            {
                return new TableSaveTransactionStatements
                {
                    BeginSql = "BEGIN TRANSACTION;",
                    CommitSql = "COMMIT;",
                    RollbackSql = "ROLLBACK;"
                };
            }
            if (provider == "mssql")
            {
                return new TableSaveTransactionStatements
                {
                    BeginSql = "BEGIN TRANSACTION;",
                    CommitSql = "COMMIT TRANSACTION;",
                    RollbackSql = "ROLLBACK TRANSACTION;"
                };
            }
            if (provider == "oracle")
            {
                return new TableSaveTransactionStatements
                {
                    BeginSql = "",
                    CommitSql = "COMMIT",
                    RollbackSql = "ROLLBACK"
                };
            }

            return null;
        }

        private static string NormalizeTransactionProvider(string providerName)
        {
            string provider = (providerName ?? "").Trim().ToLowerInvariant();
            if (provider == "postgres" || provider == "pgsql") return "postgresql";
            if (provider == "sqlserver" || provider == "sql server") return "mssql";
            return provider;
        }

        private List<TableColumnInfo> GetTableColumns(string tableName)
        {
            List<TableColumnInfo> columns = new List<TableColumnInfo>();

            if (IsMySqlProvider())
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
                string name = GetMetadataValue(row, "Name", "NAME", "name", "COLUMN_NAME", "column_name");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    columns.Add(new TableColumnInfo { Name = name });
                }
            }

            if (IsSqliteProvider())
            {
                DataTable sqliteColumns = _db.GetColumns(_databaseName, tableName);
                foreach (DataRow row in sqliteColumns.Rows)
                {
                    if (!row.Table.Columns.Contains("pk") || row["pk"].ToString() == "0") continue;
                    TableColumnInfo col = FindTableColumnInfo(columns, row["name"].ToString());
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
                    string keyName = GetMetadataValue(row, "Key_name", "KEY_NAME", "key_name", "KeyName", "index_name", "INDEX_NAME");
                    if (!keyName.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase)) continue;
                    string columnName = GetMetadataValue(row, "Column_name", "COLUMN_NAME", "column_name", "ColumnName");
                    TableColumnInfo col = FindTableColumnInfo(columns, columnName);
                    if (col != null) col.IsPrimaryKey = true;
                }
            }
            catch { }

            return columns;
        }

        private static TableColumnInfo FindTableColumnInfo(List<TableColumnInfo> columns, string columnName)
        {
            return columns.FirstOrDefault(c => c.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase));
        }

        private static string GetMetadataValue(DataRow row, params string[] names)
        {
            foreach (string name in names)
            {
                if (row.Table.Columns.Contains(name) && row[name] != DBNull.Value)
                {
                    return row[name].ToString();
                }
            }
            return "";
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
                throw new Exception(Localization.T("Query.NoWritableInsertColumns"));
            }

            string sql = "INSERT INTO " + GetQualifiedTableName(tableName) +
                         " (" + string.Join(", ", fieldSql) + ") VALUES (" + string.Join(", ", valueSql) + ");";
            ExecOrThrow(sql, parameters, false);
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
            ExecOrThrow(sql, parameters, true, BuildNoPrimaryKeyConflictDetailProvider(tableName, columns, primaryKeys, row));
        }

        private void ExecuteDelete(string tableName, List<TableColumnInfo> columns, List<string> primaryKeys, DataRow row)
        {
            Dictionary<string, object> parameters = new Dictionary<string, object>();
            int index = 0;
            string whereSql = BuildWhereClause(row, columns, primaryKeys, parameters, ref index);
            string sql = "DELETE FROM " + GetQualifiedTableName(tableName) +
                         " WHERE " + whereSql + ";";
            ExecOrThrow(sql, parameters, true, BuildNoPrimaryKeyConflictDetailProvider(tableName, columns, primaryKeys, row));
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
                : columns
                    .Where(c => row.Table.Columns.Contains(c.Name) && IsOptimisticWhereComparableValue(row[c.Name, DataRowVersion.Original]))
                    .Select(c => c.Name)
                    .ToList();
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
                throw new Exception(Localization.T("Query.UnsafeWhereClause"));
            }

            return string.Join(" AND ", clauses);
        }

        private static bool IsOptimisticWhereComparableValue(object value)
        {
            if (IsDbNull(value)) return true;
            return !(value is byte[]);
        }

        private void ExecOrThrow(string sql, Dictionary<string, object> parameters, bool requireAffectedRow)
        {
            ExecOrThrow(sql, parameters, requireAffectedRow, null);
        }

        private void ExecOrThrow(string sql, Dictionary<string, object> parameters, bool requireAffectedRow, Func<string> conflictDetailProvider)
        {
            Dictionary<string, string> result = _db.ExecSQL(sql, parameters);
            if (!result.ContainsKey("status") || result["status"] != "OK")
            {
                string reason = BuildQueryFailureReason(GetResultValue(result, "reason"));
                throw new Exception(reason);
            }

            if (requireAffectedRow && result.ContainsKey("rowsAffected"))
            {
                int rowsAffected;
                if (int.TryParse(result["rowsAffected"], out rowsAffected) && rowsAffected == 0)
                {
                    string extraDetail = "";
                    if (conflictDetailProvider != null)
                    {
                        extraDetail = conflictDetailProvider();
                    }
                    throw new Exception(BuildRowsAffectedConflictMessage(sql, parameters, extraDetail));
                }
            }
        }

        private Func<string> BuildNoPrimaryKeyConflictDetailProvider(string tableName, List<TableColumnInfo> columns, List<string> primaryKeys, DataRow row)
        {
            if (primaryKeys != null && primaryKeys.Count > 0) return null;
            return delegate
            {
                return BuildNoPrimaryKeyConflictDatabaseDiff(tableName, columns, row);
            };
        }

        private string BuildNoPrimaryKeyConflictDatabaseDiff(string tableName, List<TableColumnInfo> columns, DataRow row)
        {
            try
            {
                List<string> stableColumns = columns
                    .Where(c => row.Table.Columns.Contains(c.Name))
                    .Where(c => IsOptimisticWhereComparableValue(row[c.Name, DataRowVersion.Original]))
                    .Where(c => row.RowState == DataRowState.Deleted || !HasColumnChanged(row, c.Name))
                    .Select(c => c.Name)
                    .ToList();

                if (stableColumns.Count == 0)
                {
                    return Localization.Format("Query.ConflictDatabaseDiff", Localization.T("Query.ConflictDatabaseDiffNoStableColumns"));
                }

                Dictionary<string, object> parameters = new Dictionary<string, object>();
                int index = 0;
                string whereSql = BuildWhereClause(row, columns, stableColumns, parameters, ref index);
                string selectSql = BuildNoPrimaryKeyConflictSelectSql(tableName, columns, row, whereSql);
                DataTable candidates = _db.SelectSQL(selectSql, parameters);
                int count = candidates == null ? 0 : candidates.Rows.Count;

                if (count == 0)
                {
                    return Localization.Format("Query.ConflictDatabaseDiff", Localization.T("Query.ConflictDatabaseDiffNoRows"));
                }

                if (count > 1)
                {
                    return Localization.Format("Query.ConflictDatabaseDiff", Localization.T("Query.ConflictDatabaseDiffMultipleRows"));
                }

                return Localization.Format("Query.ConflictDatabaseDiff", BuildNoPrimaryKeyConflictRowDiff(row, candidates.Rows[0], columns));
            }
            catch (Exception ex)
            {
                return Localization.Format("Query.ConflictDatabaseDiff", Localization.Format("Query.ConflictDatabaseDiffReadFailed", ex.Message));
            }
        }

        private string BuildNoPrimaryKeyConflictSelectSql(string tableName, List<TableColumnInfo> columns, DataRow row, string whereSql)
        {
            List<string> selectColumns = columns
                .Where(c => row.Table.Columns.Contains(c.Name))
                .Select(c => QuoteIdentifier(c.Name))
                .ToList();
            if (selectColumns.Count == 0)
            {
                throw new Exception(Localization.T("Query.NoWritableInsertColumns"));
            }

            string selectList = string.Join(", ", selectColumns.ToArray());
            string qualifiedTable = GetQualifiedTableName(tableName);
            if (IsSqlServerProvider())
            {
                return "SELECT TOP 2 " + selectList + " FROM " + qualifiedTable + " WHERE " + whereSql + ";";
            }
            if (IsOracleProvider())
            {
                return "SELECT " + selectList + " FROM " + qualifiedTable + " WHERE (" + whereSql + ") AND ROWNUM <= 2";
            }
            return "SELECT " + selectList + " FROM " + qualifiedTable + " WHERE " + whereSql + " LIMIT 2;";
        }

        private static string BuildNoPrimaryKeyConflictRowDiff(DataRow originalRow, DataRow databaseRow, List<TableColumnInfo> columns)
        {
            List<string> diffs = new List<string>();
            foreach (TableColumnInfo column in columns)
            {
                if (!originalRow.Table.Columns.Contains(column.Name) || !databaseRow.Table.Columns.Contains(column.Name)) continue;
                object originalValue = originalRow[column.Name, DataRowVersion.Original];
                object databaseValue = databaseRow[column.Name];
                if (AreConflictValuesEqual(originalValue, databaseValue)) continue;

                diffs.Add(column.Name + "：" +
                    Localization.Format("Query.ConflictOriginalValue", FormatConflictParameterValue(originalValue)) + " / " +
                    Localization.Format("Query.ConflictDatabaseValue", FormatConflictParameterValue(databaseValue)));
            }

            if (diffs.Count == 0)
            {
                return Localization.T("Query.ConflictDatabaseDiffNoChanges");
            }

            return TruncateConflictDetail(string.Join("；", diffs.ToArray()), 720);
        }

        private static bool AreConflictValuesEqual(object left, object right)
        {
            if (IsDbNull(left) && IsDbNull(right)) return true;
            if (IsDbNull(left) || IsDbNull(right)) return false;
            byte[] leftBytes = left as byte[];
            byte[] rightBytes = right as byte[];
            if (leftBytes != null || rightBytes != null)
            {
                if (leftBytes == null || rightBytes == null || leftBytes.Length != rightBytes.Length) return false;
                for (int i = 0; i < leftBytes.Length; i++)
                {
                    if (leftBytes[i] != rightBytes[i]) return false;
                }
                return true;
            }
            return object.Equals(left, right);
        }

        private static string BuildRowsAffectedConflictMessage(string sql, Dictionary<string, object> parameters, string extraDetail)
        {
            string detail = BuildRowsAffectedConflictDetail(sql, parameters);
            if (!string.IsNullOrWhiteSpace(extraDetail))
            {
                detail = string.IsNullOrWhiteSpace(detail) ? extraDetail : detail + Environment.NewLine + extraDetail;
            }
            if (string.IsNullOrWhiteSpace(detail))
            {
                return Localization.T("Query.NoRowsAffectedConflict");
            }

            return Localization.T("Query.NoRowsAffectedConflict") + Environment.NewLine + detail;
        }

        private static string BuildRowsAffectedConflictDetail(string sql, Dictionary<string, object> parameters)
        {
            List<string> parts = new List<string>();
            string operation = ExtractSqlOperation(sql);
            string where = ExtractWhereSummary(sql);
            string values = BuildSqlParameterSummary(sql, parameters);

            if (!string.IsNullOrWhiteSpace(operation))
            {
                parts.Add(Localization.Format("Query.ConflictOperation", operation));
            }

            if (!string.IsNullOrWhiteSpace(where))
            {
                parts.Add(Localization.Format("Query.ConflictWhere", where));
            }

            if (!string.IsNullOrWhiteSpace(values))
            {
                parts.Add(Localization.Format("Query.ConflictValues", values));
            }

            return string.Join(Environment.NewLine, parts.ToArray());
        }

        private static string BuildSqlParameterSummary(string sql, Dictionary<string, object> parameters)
        {
            if (parameters == null || parameters.Count == 0 || string.IsNullOrWhiteSpace(sql)) return string.Empty;

            List<string> names = new List<string>();
            foreach (Match match in Regex.Matches(sql, @"(?:[:@?])(?<name>p\d+)\b", RegexOptions.IgnoreCase))
            {
                string name = match.Groups["name"].Value;
                if (!names.Any(v => string.Equals(v, name, StringComparison.OrdinalIgnoreCase)))
                {
                    names.Add(name);
                }
            }

            List<string> parts = new List<string>();
            foreach (string name in names)
            {
                KeyValuePair<string, object> pair = parameters.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrEmpty(pair.Key)) continue;
                parts.Add(pair.Key + "=" + FormatConflictParameterValue(pair.Value));
            }

            return parts.Count == 0 ? string.Empty : TruncateConflictDetail(string.Join("；", parts.ToArray()), 360);
        }

        private static string FormatConflictParameterValue(object value)
        {
            if (IsDbNull(value)) return "NULL";
            byte[] bytes = value as byte[];
            if (bytes != null) return Localization.Format("Grid.BinarySizePreview", bytes.Length.ToString(CultureInfo.InvariantCulture));
            if (value is DateTime)
            {
                return ((DateTime)value).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }

            string text = Convert.ToString(value, CultureInfo.InvariantCulture);
            text = Regex.Replace(text ?? "", @"\s+", " ").Trim();
            return TruncateConflictDetail(text, 120);
        }

        private static string ExtractSqlOperation(string sql)
        {
            Match match = Regex.Match(sql ?? "", @"^\s*(UPDATE|DELETE)\b", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return "";
            }

            return match.Groups[1].Value.ToUpperInvariant();
        }

        private static string ExtractWhereSummary(string sql)
        {
            Match match = Regex.Match(sql ?? "", @"\bWHERE\b\s+(?<where>.+?)\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!match.Success)
            {
                return "";
            }

            string where = Regex.Replace(match.Groups["where"].Value, @"\s+", " ").Trim();
            return TruncateConflictDetail("WHERE " + where, 240);
        }

        private static string TruncateConflictDetail(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value ?? "";
            }

            return value.Substring(0, Math.Max(0, maxLength - 3)) + "...";
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
            if (IsMySqlProvider())
            {
                return QuoteIdentifier(_databaseName) + "." + QuoteIdentifier(tableName);
            }
            if (IsSqlServerProvider())
            {
                SqlServerObjectName target = ParseSqlServerObjectName(tableName);
                return QuoteIdentifier(_databaseName) + "." + QuoteIdentifier(target.Schema) + "." + QuoteIdentifier(target.Name);
            }
            if (IsPostgreSqlProvider())
            {
                PostgreSqlObjectName target = ParsePostgreSqlObjectName(tableName);
                return QuoteIdentifier(target.Schema) + "." + QuoteIdentifier(target.Name);
            }
            if (IsOracleProvider())
            {
                return QuoteIdentifier(_databaseName) + "." + QuoteIdentifier(tableName);
            }
            return QuoteIdentifier(tableName);
        }

        private struct SqlServerObjectName
        {
            public string Schema;
            public string Name;
        }

        private struct PostgreSqlObjectName
        {
            public string Schema;
            public string Name;
        }

        private static SqlServerObjectName ParseSqlServerObjectName(string objectName)
        {
            string value = (objectName ?? string.Empty).Trim();
            int dotIndex = value.IndexOf('.');
            if (dotIndex > 0 && dotIndex < value.Length - 1)
            {
                return new SqlServerObjectName
                {
                    Schema = value.Substring(0, dotIndex).Trim(),
                    Name = value.Substring(dotIndex + 1).Trim()
                };
            }

            return new SqlServerObjectName { Schema = "dbo", Name = value };
        }

        private static PostgreSqlObjectName ParsePostgreSqlObjectName(string objectName)
        {
            string value = (objectName ?? string.Empty).Trim();
            int dotIndex = value.IndexOf('.');
            if (dotIndex > 0 && dotIndex < value.Length - 1)
            {
                return new PostgreSqlObjectName
                {
                    Schema = value.Substring(0, dotIndex).Trim(),
                    Name = value.Substring(dotIndex + 1).Trim()
                };
            }

            return new PostgreSqlObjectName { Schema = "public", Name = value };
        }

        private string QuoteIdentifier(string name)
        {
            if (IsMySqlProvider())
            {
                return "`" + name.Replace("`", "``") + "`";
            }
            if (IsSqlServerProvider())
            {
                return "[" + name.Replace("]", "]]") + "]";
            }
            if (IsPostgreSqlProvider() || IsSqliteProvider() || IsOracleProvider())
            {
                return "\"" + name.Replace("\"", "\"\"") + "\"";
            }
            return name;
        }

        private string ParameterToken(string parameterName)
        {
            if (IsPostgreSqlProvider() || IsOracleProvider()) return ":" + parameterName;
            if (IsSqlServerProvider()) return "@" + parameterName;
            if (IsSqliteProvider()) return "@" + parameterName;
            return "?" + parameterName;
        }

        private bool IsProvider(string providerName)
        {
            return _db != null && string.Equals(_db.ProviderName, providerName, StringComparison.OrdinalIgnoreCase);
        }

        private bool IsMySqlProvider()
        {
            return _db is my_mysql || IsProvider("mysql");
        }

        private bool IsPostgreSqlProvider()
        {
            return _db is my_postgresql || IsProvider("postgresql");
        }

        private bool IsSqlServerProvider()
        {
            return _db is my_mssql || IsProvider("mssql") || IsProvider("sqlserver");
        }

        private bool IsSqliteProvider()
        {
            return _db is my_sqlite || IsProvider("sqlite");
        }

        private bool IsOracleProvider()
        {
            return _db is my_oracle || IsProvider("oracle");
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
            if (!CanEditTableData())
            {
                ShowNoPrimaryKeyReadOnlyMessage();
                return;
            }

            if (dgvResults.DataSource is DataTable dt)
            {
                PrepareTableDataForEditing(dt);
                dt.Rows.Add(dt.NewRow());
            }
        }

        private void PrepareTableDataForEditing(DataTable dt)
        {
            if (!_isTableDataMode || dt == null) return;

            _isNoPrimaryKeyReadOnlyMode = false;
            string tableName = GetTableNameFromSql();
            if (string.IsNullOrWhiteSpace(tableName) || tableName == "Table") return;

            foreach (DataColumn dataColumn in dt.Columns)
            {
                dataColumn.AllowDBNull = true;
            }

            _isNoPrimaryKeyReadOnlyMode = ShouldOpenNoPrimaryKeyAsReadOnly(tableName);
        }

        private void DeleteSelectedRows()
        {
            if (!CanEditTableData())
            {
                ShowNoPrimaryKeyReadOnlyMessage();
                return;
            }

            if (dgvResults.SelectedRows.Count > 0)
            {
                foreach (DataGridViewRow row in dgvResults.SelectedRows)
                {
                    if (!row.IsNewRow) dgvResults.Rows.Remove(row);
                }
            }
        }

        private enum QueryExportFormat
        {
            Xlsx,
            Csv,
            Tsv,
            Json,
            Xml,
            Html,
            Markdown
        }

        // ── 匯出結果 ──
        private async void ExportCsv()
        {
            DataTable dt = dgvResults.DataSource as DataTable;
            if (dt == null || dt.Rows.Count == 0) return;

            using (SaveFileDialog dlg = new SaveFileDialog
            {
                Filter = Localization.T("Query.ExportFileFilter"),
                FileName = string.IsNullOrEmpty(_databaseName) ? "query" : _databaseName,
                DefaultExt = "csv",
                FilterIndex = 1,
                InitialDirectory = GetConfiguredDirectory("FileExportDirectory")
            })
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;

                try
                {
                    QueryResultExportFormat format = QueryResultExportService.ResolveFormat(dlg.FileName, dlg.FilterIndex);
                    string streamingSql;
                    if (TryGetStreamingExportSql(format, out streamingSql))
                    {
                        await ExportStreamingQueryResultAsync(streamingSql, dlg.FileName, format);
                    }
                    else
                    {
                        int exportedRows = QueryResultExportService.CountExportRows(dt);
                        QueryResultExportService.Write(dt, dlg.FileName, format);
                        QueryResultExportSummary summary = QueryResultExportService.BuildSummary(dlg.FileName, format, exportedRows);
                        RememberConfiguredDirectoryForPath("FileExportDirectory", summary.Path);
                        lblStatus.Text = Localization.Format("Query.ExportCompleted", summary.Rows, summary.Path);
                        ShowExportCompletedSummary(summary);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(BuildQueryExceptionMessage(ex), Localization.T("Query.ExportError"),
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void ShowExportCompletedSummary(QueryResultExportSummary summary)
        {
            if (!CanUpdateUi() || summary == null) return;

            using (ExportCompletedDialog dialog = new ExportCompletedDialog(summary))
            {
                dialog.ShowDialog(this);
            }
        }

        private bool TryGetStreamingExportSql(QueryResultExportFormat format, out string sql)
        {
            sql = "";
            if (!QueryResultExportService.CanStreamFormat(format)) return false;
            if (_isTableDataMode) return false;
            if (!_lastResultCanStreamExport) return false;
            if (string.IsNullOrWhiteSpace(_lastResultSql)) return false;

            sql = _lastResultSql;
            return true;
        }

        private async Task ExportStreamingQueryResultAsync(string sql, string targetPath, QueryResultExportFormat format)
        {
            bool oldExportEnabled = tsBtnExport != null && tsBtnExport.Enabled;
            if (tsBtnExport != null) tsBtnExport.Enabled = false;
            UpdateStatus(Localization.T("Query.StreamingExporting"));

            try
            {
                QueryResultStreamingExportResult result = await Task.Run(() =>
                    QueryResultExportService.WriteStreaming(_db, sql, null, targetPath, format));
                if (!CanUpdateUi()) return;
                QueryResultExportSummary summary = QueryResultExportService.BuildSummary(targetPath, format, result.Rows, result.BytesWritten);
                RememberConfiguredDirectoryForPath("FileExportDirectory", summary.Path);
                lblStatus.Text = Localization.Format("Query.StreamingExportCompleted", summary.Rows, QueryResultExportSummary.FormatByteCount(summary.BytesWritten), summary.Path);
                ShowExportCompletedSummary(summary);
            }
            catch (NotSupportedException)
            {
                DataTable dt = dgvResults.DataSource as DataTable;
                if (dt == null) throw;
                int exportedRows = QueryResultExportService.CountExportRows(dt);
                QueryResultExportService.Write(dt, targetPath, format);
                QueryResultExportSummary summary = QueryResultExportService.BuildSummary(targetPath, format, exportedRows);
                RememberConfiguredDirectoryForPath("FileExportDirectory", summary.Path);
                lblStatus.Text = Localization.Format("Query.ExportCompleted", summary.Rows, summary.Path);
                ShowExportCompletedSummary(summary);
            }
            finally
            {
                if (CanUpdateUi() && tsBtnExport != null) tsBtnExport.Enabled = oldExportEnabled;
            }
        }

        private static QueryExportFormat ResolveExportFormat(string path, int filterIndex)
        {
            string ext = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            switch (ext)
            {
                case ".xlsx": return QueryExportFormat.Xlsx;
                case ".csv": return QueryExportFormat.Csv;
                case ".tsv":
                case ".tab": return QueryExportFormat.Tsv;
                case ".json": return QueryExportFormat.Json;
                case ".xml": return QueryExportFormat.Xml;
                case ".html":
                case ".htm": return QueryExportFormat.Html;
                case ".md":
                case ".markdown": return QueryExportFormat.Markdown;
            }

            switch (filterIndex)
            {
                case 1: return QueryExportFormat.Csv;
                case 2: return QueryExportFormat.Xlsx;
                case 3: return QueryExportFormat.Tsv;
                case 4: return QueryExportFormat.Json;
                case 5: return QueryExportFormat.Xml;
                case 6: return QueryExportFormat.Html;
                case 7: return QueryExportFormat.Markdown;
                default: return QueryExportFormat.Csv;
            }
        }

        private static string BuildExportText(DataTable dt, QueryExportFormat format)
        {
            switch (format)
            {
                case QueryExportFormat.Tsv:
                    return BuildDelimited(dt, '\t');
                case QueryExportFormat.Json:
                    return BuildJson(dt);
                case QueryExportFormat.Xml:
                    return BuildXml(dt);
                case QueryExportFormat.Html:
                    return BuildHtml(dt);
                case QueryExportFormat.Markdown:
                    return BuildMarkdown(dt);
                default:
                    return BuildCsv(dt, out _);
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
                    sb.Append(CsvEscape(FormatExportValue(row[c])));
                }
                sb.AppendLine();
                exportedRows++;
            }

            return sb.ToString();
        }

        private static string BuildDelimited(DataTable dt, char delimiter)
        {
            if (dt == null) throw new ArgumentNullException(nameof(dt));

            StringBuilder sb = new StringBuilder();
            for (int c = 0; c < dt.Columns.Count; c++)
            {
                if (c > 0) sb.Append(delimiter);
                sb.Append(DelimitedEscape(dt.Columns[c].ColumnName, delimiter));
            }
            sb.AppendLine();

            foreach (DataRow row in dt.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;
                for (int c = 0; c < dt.Columns.Count; c++)
                {
                    if (c > 0) sb.Append(delimiter);
                    sb.Append(DelimitedEscape(FormatExportValue(row[c]), delimiter));
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static string BuildJson(DataTable dt)
        {
            if (dt == null) throw new ArgumentNullException(nameof(dt));

            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            foreach (DataRow row in dt.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;

                Dictionary<string, object> item = new Dictionary<string, object>();
                foreach (DataColumn column in dt.Columns)
                {
                    object value = row[column];
                    item[column.ColumnName] = value == DBNull.Value ? null : ConvertExportValue(value);
                }
                rows.Add(item);
            }

            return JsonConvert.SerializeObject(rows, Formatting.Indented);
        }

        private static string BuildXml(DataTable dt)
        {
            if (dt == null) throw new ArgumentNullException(nameof(dt));

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine("<results>");

            foreach (DataRow row in dt.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;
                sb.AppendLine("  <row>");
                foreach (DataColumn column in dt.Columns)
                {
                    object value = row[column];
                    string columnName = XmlEscape(column.ColumnName);
                    if (value == DBNull.Value || value == null)
                    {
                        sb.Append("    <field name=\"").Append(columnName).AppendLine("\" isNull=\"true\" />");
                    }
                    else
                    {
                        sb.Append("    <field name=\"").Append(columnName).Append("\">")
                          .Append(XmlEscape(FormatExportValue(value)))
                          .AppendLine("</field>");
                    }
                }
                sb.AppendLine("  </row>");
            }

            sb.AppendLine("</results>");
            return sb.ToString();
        }

        private static string BuildHtml(DataTable dt)
        {
            if (dt == null) throw new ArgumentNullException(nameof(dt));

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<!doctype html>");
            sb.AppendLine("<html><head><meta charset=\"utf-8\"><title>mySQLPunk export</title>");
            sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif}table{border-collapse:collapse}th,td{border:1px solid #ccc;padding:4px 8px;white-space:pre-wrap}th{background:#f2f2f2}</style>");
            sb.AppendLine("</head><body><table>");
            sb.AppendLine("<thead><tr>");
            foreach (DataColumn column in dt.Columns)
            {
                sb.Append("<th>").Append(HtmlEscape(column.ColumnName)).AppendLine("</th>");
            }
            sb.AppendLine("</tr></thead><tbody>");

            foreach (DataRow row in dt.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;
                sb.AppendLine("<tr>");
                foreach (DataColumn column in dt.Columns)
                {
                    sb.Append("<td>").Append(HtmlEscape(FormatExportValue(row[column]))).AppendLine("</td>");
                }
                sb.AppendLine("</tr>");
            }

            sb.AppendLine("</tbody></table></body></html>");
            return sb.ToString();
        }

        private static string BuildMarkdown(DataTable dt)
        {
            if (dt == null) throw new ArgumentNullException(nameof(dt));

            StringBuilder sb = new StringBuilder();
            sb.Append("| ");
            for (int c = 0; c < dt.Columns.Count; c++)
            {
                if (c > 0) sb.Append(" | ");
                sb.Append(MarkdownEscape(dt.Columns[c].ColumnName));
            }
            sb.AppendLine(" |");

            sb.Append("| ");
            for (int c = 0; c < dt.Columns.Count; c++)
            {
                if (c > 0) sb.Append(" | ");
                sb.Append("---");
            }
            sb.AppendLine(" |");

            foreach (DataRow row in dt.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;
                sb.Append("| ");
                for (int c = 0; c < dt.Columns.Count; c++)
                {
                    if (c > 0) sb.Append(" | ");
                    sb.Append(MarkdownEscape(FormatExportValue(row[c])));
                }
                sb.AppendLine(" |");
            }

            return sb.ToString();
        }

        private static void WriteXlsx(DataTable dt, string path)
        {
            if (dt == null) throw new ArgumentNullException(nameof(dt));
            if (File.Exists(path)) File.Delete(path);

            using (ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                AddZipEntry(archive, "[Content_Types].xml", BuildXlsxContentTypes());
                AddZipEntry(archive, "_rels/.rels", BuildXlsxRootRelationships());
                AddZipEntry(archive, "xl/workbook.xml", BuildXlsxWorkbook());
                AddZipEntry(archive, "xl/_rels/workbook.xml.rels", BuildXlsxWorkbookRelationships());
                AddZipEntry(archive, "xl/worksheets/sheet1.xml", BuildXlsxSheet(dt));
                AddZipEntry(archive, "xl/styles.xml", BuildXlsxStyles());
            }
        }

        private static void AddZipEntry(ZipArchive archive, string path, string content)
        {
            ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.Optimal);
            using (Stream stream = entry.Open())
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(content);
            }
        }

        private static string BuildXlsxSheet(DataTable dt)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.AppendLine("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");

            int rowIndex = 1;
            sb.Append("<row r=\"").Append(rowIndex).Append("\">");
            for (int c = 0; c < dt.Columns.Count; c++)
            {
                AppendInlineStringCell(sb, rowIndex, c + 1, dt.Columns[c].ColumnName);
            }
            sb.AppendLine("</row>");

            foreach (DataRow row in dt.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;
                rowIndex++;
                sb.Append("<row r=\"").Append(rowIndex).Append("\">");
                for (int c = 0; c < dt.Columns.Count; c++)
                {
                    AppendInlineStringCell(sb, rowIndex, c + 1, FormatExportValue(row[c]));
                }
                sb.AppendLine("</row>");
            }

            sb.AppendLine("</sheetData></worksheet>");
            return sb.ToString();
        }

        private static void AppendInlineStringCell(StringBuilder sb, int rowIndex, int columnIndex, string value)
        {
            sb.Append("<c r=\"").Append(GetExcelColumnName(columnIndex)).Append(rowIndex).Append("\" t=\"inlineStr\"><is><t");
            if (!string.IsNullOrEmpty(value) && (value.StartsWith(" ") || value.EndsWith(" ") || value.Contains("\n") || value.Contains("\r") || value.Contains("\t")))
            {
                sb.Append(" xml:space=\"preserve\"");
            }
            sb.Append(">").Append(XmlEscape(value)).Append("</t></is></c>");
        }

        private static string BuildXlsxContentTypes()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                   "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                   "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                   "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                   "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                   "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                   "</Types>";
        }

        private static string BuildXlsxRootRelationships()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                   "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                   "</Relationships>";
        }

        private static string BuildXlsxWorkbook()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                   "<sheets><sheet name=\"Results\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>";
        }

        private static string BuildXlsxWorkbookRelationships()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                   "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                   "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
                   "</Relationships>";
        }

        private static string BuildXlsxStyles()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                   "<fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
                   "<fills count=\"1\"><fill><patternFill patternType=\"none\"/></fill></fills>" +
                   "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
                   "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                   "<cellXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/></cellXfs>" +
                   "</styleSheet>";
        }

        private static int CountExportRows(DataTable dt)
        {
            if (dt == null) return 0;
            int count = 0;
            foreach (DataRow row in dt.Rows)
            {
                if (row.RowState != DataRowState.Deleted) count++;
            }
            return count;
        }

        private static object ConvertExportValue(object value)
        {
            if (value == null || value == DBNull.Value) return null;
            if (value is byte[]) return FormatExportValue(value);
            if (value is DateTime dt) return dt.ToString("o");
            return value;
        }

        private static string FormatExportValue(object value)
        {
            if (value == null || value == DBNull.Value) return string.Empty;
            if (value is byte[] bytes) return FormatBinaryCellValue(bytes);
            if (value is DateTime dt) return dt.ToString("yyyy-MM-dd HH:mm:ss");
            return value.ToString();
        }

        private static string CsvEscape(string value)
        {
            value = value ?? string.Empty;
            if (value.Contains(",") || value.Contains("\"") || value.Contains("\r") || value.Contains("\n"))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        private static string DelimitedEscape(string value, char delimiter)
        {
            value = value ?? string.Empty;
            if (value.Contains(delimiter.ToString()) || value.Contains("\"") || value.Contains("\r") || value.Contains("\n"))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        private static string HtmlEscape(string value)
        {
            return System.Web.HttpUtility.HtmlEncode(value ?? string.Empty);
        }

        private static string XmlEscape(string value)
        {
            return System.Security.SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;
        }

        private static string MarkdownEscape(string value)
        {
            value = (value ?? string.Empty).Replace("\r\n", "<br>").Replace("\n", "<br>").Replace("\r", "<br>");
            return value.Replace("\\", "\\\\").Replace("|", "\\|");
        }

        private static string GetExcelColumnName(int columnNumber)
        {
            string columnName = string.Empty;
            while (columnNumber > 0)
            {
                int modulo = (columnNumber - 1) % 26;
                columnName = Convert.ToChar('A' + modulo) + columnName;
                columnNumber = (columnNumber - modulo) / 26;
            }
            return columnName;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (dgvResults != null)
            {
                dgvResults.ContextMenuStrip = null;
            }
            resultsContextMenu = null;

            if (_autoRecoveryTimer != null)
            {
                SaveAutoRecoveryDraftIfNeeded();
                _autoRecoveryTimer.Stop();
                _autoRecoveryTimer.Dispose();
                _autoRecoveryTimer = null;
            }

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
