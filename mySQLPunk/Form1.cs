using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Drawing;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Security.Cryptography;
using mySQLPunk.entity;
using utility;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using MySqlConnector;
//using MySql.Data.MySqlClient;
using mySQLPunk.lib;
using mySQLPunk.template;

namespace mySQLPunk
{

    public partial class Form1 : Form
    {
        private const int MainToolbarHeight = 96;
        private const int MainToolbarItemWidth = 76;
        private const int MainToolbarItemHeight = 84;
        private const int MainToolbarIconSize = 40;
        private const int MainToolbarIconContentSize = 38;

        public Form dialog = new Form();
        public Label dialogLabel = new Label();
        public int dialogFlag = 0;
        public string test = "";
        
        // Windows API 用於實作視窗拖拽接管
        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HTCAPTION = 0x2;

        // Windows Shell API 用於取得系統資料夾圖示
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }
        [DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);
        private const uint SHGFI_ICON = 0x100;
        private const uint SHGFI_SMALLICON = 0x001;
        private const uint SHGFI_OPENICON = 0x002;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x010;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

        private static Bitmap GetShellFolderBitmap(bool open)
        {
            var shfi = new SHFILEINFO();
            uint flags = SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES | (open ? SHGFI_OPENICON : 0);
            SHGetFileInfo("folder", FILE_ATTRIBUTE_DIRECTORY, ref shfi,
                (uint)System.Runtime.InteropServices.Marshal.SizeOf(shfi), flags);
            if (shfi.hIcon == IntPtr.Zero) return new Bitmap(16, 16);
            Bitmap bmp;
            using (Icon ic = Icon.FromHandle(shfi.hIcon)) { bmp = ic.ToBitmap(); }
            DestroyIcon(shfi.hIcon);
            return bmp;
        }

        public static Dictionary<string, List<object>> displayTools = new Dictionary<string, List<object>>();
        mySQLPunk_main myN = new mySQLPunk_main();
        myinclude my = new myinclude();
        ToolStripButton query_btn = new ToolStripButton(); // 新增查詢按鈕
        ToolStripButton function_btn = new ToolStripButton();
        ToolStripButton other_btn = new ToolStripButton();
        ToolStripButton query_section_btn = new ToolStripButton();
        ToolStripButton backup_btn = new ToolStripButton();
        ToolStripButton auto_run_btn = new ToolStripButton();
        ToolStripButton model_btn = new ToolStripButton();
        ToolStripButton bi_btn = new ToolStripButton();
        private TabControl queryTabs;
        private int queryTabCounter = 1;
        private TabPage dragTab = null; // 用於拖拽頁籤
        private Point dragStartPos;

        // 側邊欄組件
        private Panel pnlSidebar;
        private Panel pnlObjectFilter;
        private TextBox txtObjectFilter;
        private ToolStrip tsSidebar;
        private RichTextBox rtbDDL;
        private DataGridView dgvDetails;
        private ToolStripButton btnInfo;
        private ToolStripButton btnDDL;
        private Label lblSidebarTitle;
        private ToolStripStatusLabel lblMainStatus; // 新增主程式狀態標籤
        private Panel _dockHintOverlay;
        private DatabaseCopyItem _treeClipboardItem;
        private bool _allowTreeLabelEdit = false;
	        private readonly List<string> _favoriteNodePaths = new List<string>();
	        private readonly HashSet<int> _openingConnectionIndices = new HashSet<int>();
	        private readonly HashSet<string> _loadingDatabaseMetadataKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Timer _backupIntegrityTimer = new Timer();
        private bool _backupIntegrityScanRunning;

        // 群組功能：快速查詢連線節點（key = 連線索引）
        private TreeNode[] _connectionTreeNodes = new TreeNode[0];

        private const string ConnectionGroupNodeTag = "__CONN_GROUP__";
        private const string ConnectionExportSourceIdFileName = "connection-export-source-id.txt";
        private const string TrustedConnectionImportSourcesFileName = "trusted-connection-import-sources.json";

        private class TreeDatabaseTarget
        {
            public IDatabase Database;
            public string DatabaseName;
            public string ProviderName;
            public TreeNode DatabaseNode;
            public Dictionary<string, object> ConnectionInfo;
        }

        private class ImportedConnectionPasswordTarget
        {
            public int Index;
            public string ConnectionName;
            public string DbKind;
            public string Username;
            public string Target;
        }

        private class ConnectionImportPreviewEntry
        {
            public bool Include;
            public string Status;
            public int ImportedIndex;
            public int ExistingIndex;
            public string Key;
            public string ConnectionName;
            public string DbKind;
            public string Target;
        }

        private class ConnectionImportPreviewReport
        {
            public List<Dictionary<string, object>> ImportedConnections { get; } = new List<Dictionary<string, object>>();
            public List<string> ImportedGroups { get; } = new List<string>();
            public List<ConnectionImportPreviewEntry> Entries { get; } = new List<ConnectionImportPreviewEntry>();
            public string SourceSignature { get; set; }
            public bool SourceSignaturePresent { get; set; }
            public bool SourceSignatureValid { get; set; }
            public string SourceExportedAtUtc { get; set; }
            public string SourceId { get; set; }
            public bool SourceTrusted { get; set; }
            public int Added { get; set; }
            public int Updated { get; set; }
            public int Unchanged { get; set; }
            public int ExistingOnly { get; set; }
        }

        private class ConnectionImportReviewLogTarget
        {
            public bool Selected { get; set; }
            public string Status { get; set; }
            public int ImportedIndex { get; set; }
            public string ConnectionName { get; set; }
            public string DbKind { get; set; }
            public string Target { get; set; }
        }

        private class ConnectionImportReviewLogEntry
        {
            public int FormatVersion { get; set; }
            public string ReviewedAtUtc { get; set; }
            public string Action { get; set; }
            public string SourceId { get; set; }
            public string SourceSignatureSha256 { get; set; }
            public string SourceSignatureState { get; set; }
            public bool SourceTrusted { get; set; }
            public string SourceExportedAtUtc { get; set; }
            public int Added { get; set; }
            public int Updated { get; set; }
            public int Unchanged { get; set; }
            public int ExistingOnly { get; set; }
            public int PasswordsRequired { get; set; }
            public int SelectedImportedCount { get; set; }
            public List<string> Groups { get; set; }
            public List<ConnectionImportReviewLogTarget> Targets { get; set; }
        }

        private enum ConnectionImportMode
        {
            Cancel,
            Replace,
            Merge
        }

        private class ConnectionImportDecision
        {
            public ConnectionImportMode Mode { get; set; }
            public HashSet<int> SelectedImportedIndexes { get; } = new HashSet<int>();
        }

        private static int GetConnectionIconIndex(string dbKind, bool isConnected)
        {
            switch ((dbKind ?? string.Empty).ToLowerInvariant())
            {
                case "mysql":
                    return isConnected ? 1 : 0;
                case "postgresql":
                    return isConnected ? 3 : 2;
                case "oracle":
                    return isConnected ? 5 : 4;
                case "sqlite":
                    return isConnected ? 7 : 6;
                case "mssql":
                case "sqlserver":
                    return isConnected ? 9 : 8;
                default:
                    return isConnected ? 11 : 10;
            }
        }

        private static void ApplyConnectionNodeIcon(TreeNode node, string dbKind, bool isConnected)
        {
            if (node == null) return;
            int iconIndex = GetConnectionIconIndex(dbKind, isConnected);
            node.ImageIndex = iconIndex;
            node.SelectedImageIndex = iconIndex;
        }

        private static string BuildSqlServerDataSource(string host, string port)
        {
            if (string.IsNullOrWhiteSpace(port)) return host;
            if ((host ?? string.Empty).Contains(",") || (host ?? string.Empty).Contains("\\")) return host;
            return host + "," + port;
        }

        private static string BuildSqlServerConnectionString(Dictionary<string, object> conn)
        {
            var builder = new System.Data.SqlClient.SqlConnectionStringBuilder();
            string host = GetConnectionValue(conn, "host");
            string port = GetConnectionValue(conn, "port");
            builder.DataSource = BuildSqlServerDataSource(host, port);
            builder.InitialCatalog = string.IsNullOrWhiteSpace(GetConnectionValue(conn, "initial_database"))
                ? "master"
                : GetConnectionValue(conn, "initial_database");
            bool trusted = GetConnectionValue(conn, "trusted_connection") == "T";
            builder.IntegratedSecurity = trusted;
            builder.TrustServerCertificate = true;
            builder.MultipleActiveResultSets = true;
            builder.ConnectTimeout = 8;

            if (!trusted)
            {
                builder.UserID = GetConnectionValue(conn, "username");
                builder.Password = GetConnectionValue(conn, "pwd");
            }

            return builder.ConnectionString;
        }

        private static string BuildConnectionFailureMessage(string providerName, Exception ex)
        {
            string provider = string.IsNullOrWhiteSpace(providerName) ? "Database" : providerName;
            string reason = ex == null ? "Unknown error" : ex.Message;
            return provider + " 連線失敗：" + reason;
        }

        private bool TryEnterConnectionOpenScope(int index)
        {
            lock (_openingConnectionIndices)
            {
                if (_openingConnectionIndices.Contains(index)) return false;
                _openingConnectionIndices.Add(index);
                return true;
            }
        }

	        private void ExitConnectionOpenScope(int index)
	        {
	            lock (_openingConnectionIndices)
	            {
	                _openingConnectionIndices.Remove(index);
	            }
	        }

	        private static string BuildDatabaseMetadataLoadKey(int connectionIndex, string databaseName)
	        {
	            return connectionIndex + "|" + (databaseName ?? string.Empty);
	        }

	        private bool TryEnterDatabaseMetadataLoadScope(int connectionIndex, string databaseName)
	        {
	            string key = BuildDatabaseMetadataLoadKey(connectionIndex, databaseName);
	            lock (_loadingDatabaseMetadataKeys)
	            {
	                if (_loadingDatabaseMetadataKeys.Contains(key)) return false;
	                _loadingDatabaseMetadataKeys.Add(key);
	                return true;
	            }
	        }

	        private void ExitDatabaseMetadataLoadScope(int connectionIndex, string databaseName)
	        {
	            string key = BuildDatabaseMetadataLoadKey(connectionIndex, databaseName);
	            lock (_loadingDatabaseMetadataKeys)
	            {
	                _loadingDatabaseMetadataKeys.Remove(key);
	            }
	        }

        private static bool ShouldOfferRetryForConnectionOpen(Exception ex)
        {
            return ConnectionOpenService.ShouldOfferRetry(ex);
        }

        private static string BuildMySqlConnectionString(Dictionary<string, object> conn)
        {
            string host = GetConnectionValue(conn, "host");
            string portText = GetConnectionValue(conn, "port");
            string user = GetConnectionValue(conn, "username");
            string password = GetConnectionValue(conn, "pwd");
            string initialDatabase = GetConnectionValue(conn, "initial_database");

            uint port = 3306;
            if (!string.IsNullOrWhiteSpace(portText)) uint.TryParse(portText, out port);

            var builder = new MySqlConnectionStringBuilder
            {
                Server = host,
                Port = port,
                UserID = user,
                Password = password,
                Database = string.IsNullOrWhiteSpace(initialDatabase) ? string.Empty : initialDatabase,
                SslMode = MySqlSslMode.None,
                CharacterSet = "utf8",
                AllowZeroDateTime = true,
                ConnectionTimeout = 8
            };

            return builder.ConnectionString;
        }

        private static string BuildPostgreSqlConnectionString(Dictionary<string, object> conn)
        {
            string host = GetConnectionValue(conn, "host");
            string portText = GetConnectionValue(conn, "port");
            string user = GetConnectionValue(conn, "username");
            string password = GetConnectionValue(conn, "pwd");

            int port = 5432;
            if (!string.IsNullOrWhiteSpace(portText)) int.TryParse(portText, out port);

            // 用 postgres 作為初始 DB 以便取得 database 清單
            return $"Server={host};Port={port};User Id={user};Password={password};Database=postgres;Timeout=8;Command Timeout=8;";
        }

        private void PopulateConnectionDatabaseNodes(TreeNode connectionNode, IEnumerable<string> databaseNames)
        {
            if (connectionNode == null) return;
            connectionNode.Nodes.Clear();
            if (databaseNames == null) return;

            foreach (string databaseName in databaseNames)
            {
                TreeNode newNode = new TreeNode(databaseName);
                newNode.ImageIndex = 10;
                newNode.SelectedImageIndex = 10;
                connectionNode.Nodes.Add(newNode);
            }
        }

        private async Task OpenMySqlConnectionAsync(int index, TreeView tree)
        {
            if (index < 0 || index >= myN.connections.Count) return;
            if (!TryEnterConnectionOpenScope(index)) return;

            Cursor previousCursor = Cursor;
            bool previousEnabled = tree?.Enabled ?? true;
            try
            {
                if (tree != null) tree.Enabled = false;
                Cursor = Cursors.WaitCursor;
                await Task.Yield(); // 讓狀態列與游標有機會先更新，避免 UI 看起來卡住

                var conn = myN.connections[index];
                string connString = BuildMySqlConnectionString(conn);

                Exception lastError = null;
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    try
                    {
                        ConnectionOpenResult openResult = await Task.Run(() => ConnectionOpenService.Open(() => new my_mysql(), connString));
                        IDatabase db = openResult.Database;
                        List<string> databases = openResult.Databases;

                        myN.connections[index]["connString"] = connString;
                        myN.connections[index]["pdo"] = db;
                        myN.connections[index]["isConnect"] = "T";
                        ApplyConnectionNodeIcon(FindConnectionNode(index), myN.connections[index]["db_kind"].ToString(), true);

                        TreeNode connNode = FindConnectionNode(index);
                        PopulateConnectionDatabaseNodes(connNode, databases);
                        if (connNode != null) connNode.Expand();
                        return;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;

                        bool canRetry = attempt == 1 && ShouldOfferRetryForConnectionOpen(ex);
                        if (canRetry)
                        {
                            DialogResult retry = MessageBox.Show(
                                "MySQL 連線失敗：" + ex.Message + "\n\n是否要重試一次？",
                                "MySQL",
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Warning);
                            if (retry == DialogResult.Yes) continue;
                        }

                        myN.connections[index]["pdo"] = null;
                        myN.connections[index]["isConnect"] = "F";
                        HandleConnectionOpenFailure(index, tree, "MySQL", lastError);
                        return;
                    }
                }

                myN.connections[index]["pdo"] = null;
                myN.connections[index]["isConnect"] = "F";
                HandleConnectionOpenFailure(index, tree, "MySQL", lastError);
            }
            finally
            {
                if (tree != null) tree.Enabled = previousEnabled;
                Cursor = previousCursor;
                ExitConnectionOpenScope(index);
            }
        }

        private async Task OpenPostgreSqlConnectionAsync(int index, TreeView tree)
        {
            if (index < 0 || index >= myN.connections.Count) return;
            if (!TryEnterConnectionOpenScope(index)) return;

            Cursor previousCursor = Cursor;
            bool previousEnabled = tree?.Enabled ?? true;
            try
            {
                if (tree != null) tree.Enabled = false;
                Cursor = Cursors.WaitCursor;
                await Task.Yield();

                var conn = myN.connections[index];
                string connString = BuildPostgreSqlConnectionString(conn);

                Exception lastError = null;
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    try
                    {
                        ConnectionOpenResult openResult = await Task.Run(() => ConnectionOpenService.Open(() => new my_postgresql(), connString));
                        IDatabase db = openResult.Database;
                        List<string> databases = openResult.Databases;

                        myN.connections[index]["connString"] = connString;
                        myN.connections[index]["pdo"] = db;
                        myN.connections[index]["isConnect"] = "T";
                        ApplyConnectionNodeIcon(FindConnectionNode(index), myN.connections[index]["db_kind"].ToString(), true);

                        TreeNode connNode = FindConnectionNode(index);
                        PopulateConnectionDatabaseNodes(connNode, databases);
                        if (connNode != null) connNode.Expand();
                        return;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;

                        bool canRetry = attempt == 1 && ShouldOfferRetryForConnectionOpen(ex);
                        if (canRetry)
                        {
                            DialogResult retry = MessageBox.Show(
                                "PostgreSQL 連線失敗：" + ex.Message + "\n\n是否要重試一次？",
                                "PostgreSQL",
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Warning);
                            if (retry == DialogResult.Yes) continue;
                        }

                        myN.connections[index]["pdo"] = null;
                        myN.connections[index]["isConnect"] = "F";
                        HandleConnectionOpenFailure(index, tree, "PostgreSQL", lastError);
                        return;
                    }
                }

                myN.connections[index]["pdo"] = null;
                myN.connections[index]["isConnect"] = "F";
                HandleConnectionOpenFailure(index, tree, "PostgreSQL", lastError);
            }
            finally
            {
                if (tree != null) tree.Enabled = previousEnabled;
                Cursor = previousCursor;
                ExitConnectionOpenScope(index);
            }
        }

        private async Task OpenSqliteConnectionAsync(int index, TreeView tree)
        {
            if (index < 0 || index >= myN.connections.Count) return;
            if (!TryEnterConnectionOpenScope(index)) return;

            Cursor previousCursor = Cursor;
            bool previousEnabled = tree?.Enabled ?? true;
            try
            {
                if (tree != null) tree.Enabled = false;
                Cursor = Cursors.WaitCursor;
                await Task.Yield();

                var conn = myN.connections[index];
                string connString = "Data Source=" + GetConnectionValue(conn, "path") + ";Version=3;";

                Exception lastError = null;
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    try
                    {
                        ConnectionOpenResult openResult = await Task.Run(() => ConnectionOpenService.Open(() => new my_sqlite(), connString));
                        my_sqlite db = openResult.Database as my_sqlite;
                        List<string> databases = openResult.Databases;

                        myN.connections[index]["connString"] = connString;
                        myN.connections[index]["pdo"] = db;
                        myN.connections[index]["isConnect"] = "T";
                        ApplyConnectionNodeIcon(FindConnectionNode(index), myN.connections[index]["db_kind"].ToString(), true);

                        if (db.SpatiaLiteEnabled) UpdateMainStatus("SQLite + SpatiaLite ready");
                        else if (!string.IsNullOrWhiteSpace(db.SpatiaLiteLoadError)) UpdateMainStatus(db.SpatiaLiteLoadError);

                        TreeNode connNode = FindConnectionNode(index);
                        PopulateConnectionDatabaseNodes(connNode, databases);
                        if (connNode != null) connNode.Expand();
                        return;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;

                        bool canRetry = attempt == 1 && ShouldOfferRetryForConnectionOpen(ex);
                        if (canRetry)
                        {
                            DialogResult retry = MessageBox.Show(
                                "SQLite 連線失敗：" + ex.Message + "\n\n是否要重試一次？",
                                "SQLite",
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Warning);
                            if (retry == DialogResult.Yes) continue;
                        }

                        myN.connections[index]["pdo"] = null;
                        myN.connections[index]["isConnect"] = "F";
                        HandleConnectionOpenFailure(index, tree, "SQLite", lastError);
                        return;
                    }
                }

                myN.connections[index]["pdo"] = null;
                myN.connections[index]["isConnect"] = "F";
                HandleConnectionOpenFailure(index, tree, "SQLite", lastError);
            }
            finally
            {
                if (tree != null) tree.Enabled = previousEnabled;
                Cursor = previousCursor;
                ExitConnectionOpenScope(index);
            }
        }

        private async Task OpenOracleConnectionAsync(int index, TreeView tree)
        {
            if (index < 0 || index >= myN.connections.Count) return;
            if (!TryEnterConnectionOpenScope(index)) return;

            Cursor previousCursor = Cursor;
            bool previousEnabled = tree?.Enabled ?? true;
            try
            {
                if (tree != null) tree.Enabled = false;
                Cursor = Cursors.WaitCursor;
                await Task.Yield();

                var conn = myN.connections[index];
                string connString = BuildOracleConnectionString(conn);

                Exception lastError = null;
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    try
                    {
                        ConnectionOpenResult openResult = await Task.Run(() => ConnectionOpenService.Open(() => new my_oracle(), connString));
                        IDatabase db = openResult.Database;
                        List<string> databases = openResult.Databases;

                        myN.connections[index]["connString"] = connString;
                        myN.connections[index]["pdo"] = db;
                        myN.connections[index]["isConnect"] = "T";
                        ApplyConnectionNodeIcon(FindConnectionNode(index), myN.connections[index]["db_kind"].ToString(), true);

                        TreeNode connNode = FindConnectionNode(index);
                        PopulateConnectionDatabaseNodes(connNode, databases);
                        if (connNode != null) connNode.Expand();
                        return;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;

                        bool canRetry = attempt == 1 && ShouldOfferRetryForConnectionOpen(ex);
                        if (canRetry)
                        {
                            DialogResult retry = MessageBox.Show(
                                "Oracle 連線失敗：" + ex.Message + "\n\n是否要重試一次？",
                                "Oracle",
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Warning);
                            if (retry == DialogResult.Yes) continue;
                        }

                        myN.connections[index]["pdo"] = null;
                        myN.connections[index]["isConnect"] = "F";
                        HandleConnectionOpenFailure(index, tree, "Oracle", lastError);
                        return;
                    }
                }

                myN.connections[index]["pdo"] = null;
                myN.connections[index]["isConnect"] = "F";
                HandleConnectionOpenFailure(index, tree, "Oracle", lastError);
            }
            finally
            {
                if (tree != null) tree.Enabled = previousEnabled;
                Cursor = previousCursor;
                ExitConnectionOpenScope(index);
            }
        }

        private async Task OpenSqlServerConnectionAsync(int index, TreeView tree)
        {
            if (index < 0 || index >= myN.connections.Count) return;
            if (!TryEnterConnectionOpenScope(index)) return;

            Cursor previousCursor = Cursor;
            bool previousEnabled = tree?.Enabled ?? true;
            try
            {
                if (tree != null) tree.Enabled = false;
                Cursor = Cursors.WaitCursor;
                await Task.Yield();

                var conn = myN.connections[index];
                string connString = BuildSqlServerConnectionString(conn);

                Exception lastError = null;
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    try
                    {
                        ConnectionOpenResult openResult = await Task.Run(() => ConnectionOpenService.Open(() => new my_mssql(), connString));
                        IDatabase db = openResult.Database;
                        List<string> databases = openResult.Databases;

                        myN.connections[index]["connString"] = connString;
                        myN.connections[index]["pdo"] = db;
                        myN.connections[index]["isConnect"] = "T";
                        ApplyConnectionNodeIcon(FindConnectionNode(index), myN.connections[index]["db_kind"].ToString(), true);

                        TreeNode connNode = FindConnectionNode(index);
                        PopulateConnectionDatabaseNodes(connNode, databases);
                        if (connNode != null) connNode.Expand();
                        return;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;

                        bool canRetry = attempt == 1 && ShouldOfferRetryForConnectionOpen(ex);
                        if (canRetry)
                        {
                            DialogResult retry = MessageBox.Show(
                                "SQL Server 連線失敗：" + ex.Message + "\n\n是否要重試一次？",
                                "SQL Server",
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Warning);
                            if (retry == DialogResult.Yes) continue;
                        }

                        myN.connections[index]["pdo"] = null;
                        myN.connections[index]["isConnect"] = "F";
                        HandleConnectionOpenFailure(index, tree, "SQL Server", lastError);
                        return;
                    }
                }

                myN.connections[index]["pdo"] = null;
                myN.connections[index]["isConnect"] = "F";
                HandleConnectionOpenFailure(index, tree, "SQL Server", lastError);
            }
            finally
            {
                if (tree != null) tree.Enabled = previousEnabled;
                Cursor = previousCursor;
                ExitConnectionOpenScope(index);
            }
        }

        private void HandleConnectionOpenFailure(int index, TreeView tree, string providerName, Exception ex)
        {
            string message = BuildConnectionFailureMessage(providerName, ex);
            Console.WriteLine(message);
            if (index >= 0 && index < myN.connections.Count)
            {
                myN.connections[index]["isConnect"] = "F";
                ApplyConnectionNodeIcon(FindConnectionNode(index), myN.connections[index]["db_kind"].ToString(), false);
            }
            if (tree != null && tree.SelectedNode != null)
            {
                tree.SelectedNode.Collapse();
            }
            UpdateMainStatus(message);
            MessageBox.Show(message, providerName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private class DatabaseObjectSelection
        {
            public IDatabase Database;
            public string DatabaseName;
            public string ObjectName;
            public string GroupName;
            public string Host;
            public TreeNode Node;
        }

        private class OpenQueryTabInfo
        {
            public int DisplayIndex;
            public int TabIndex;
            public TabPage Page;
            public QueryForm Query;
        }

        private class QueryHistoryEntry
        {
            public DateTime ExecutedAt;
            public string DatabaseName;
            public string Sql;
            public string Status;
            public int Rows;
            public long ElapsedMilliseconds;
            public bool IsQuery;
        }

        private class AutoCommentColumnUpdate
        {
            public string TableName;
            public string ColumnName;
            public string Comment;
            public string Sql;
        }

        private readonly List<QueryHistoryEntry> _queryHistory = new List<QueryHistoryEntry>();

        private static readonly string[] DatabaseReportNames =
        {
            "Database Summary",
            "Table Row Counts",
            "Object Inventory"
        };

        private static readonly string[] DatabaseModelNames =
        {
            "Schema Overview",
            "Column Catalog",
            "Index Catalog"
        };

        private static readonly string[] DatabaseBIReportNames =
        {
            "Object Distribution",
            "Table Size Summary",
            "Row Count Ranking"
        };

        private static readonly string[] DatabaseOtherToolNames =
        {
            "Connection Diagnostics",
            "Provider Capabilities",
            "Maintenance Checklist"
        };

        public Form1()
        {
            Localization.Load();
            ThemeManager.Load();
            InitializeComponent();
            _backupIntegrityTimer.Interval = 60 * 60 * 1000;
            _backupIntegrityTimer.Tick += (s, e) => RunBackupIntegrityScheduleIfDue();
        }
        
        public static void ApplyModernTheme(Control parent)
        {
            if (parent is Form f)
            {
                f.Opacity = 1.0f;
            }
            ThemeManager.ApplyTo(parent);
        }

        public class CyberpunkColorTable : ProfessionalColorTable
        {
            public override Color ToolStripGradientBegin => Color.FromArgb(26, 26, 26);
            public override Color ToolStripGradientEnd => Color.FromArgb(26, 26, 26);
            public override Color ToolStripBorder => Color.FromArgb(0, 255, 204);
            public override Color MenuItemSelected => Color.FromArgb(0, 100, 100);
            public override Color MenuItemSelectedGradientBegin => Color.FromArgb(0, 100, 100);
            public override Color MenuItemSelectedGradientEnd => Color.FromArgb(0, 100, 100);
            public override Color MenuItemBorder => Color.FromArgb(0, 255, 204);
        }
        public void showTools(string kind)
        {
            switch (kind)
            {
                case "點到連線":
                case "點到未展開的資料庫":
                    {
                        foreach (var k in displayTools.Keys)
                        {
                            if (k == "tables")
                            {
                                foreach (var item in displayTools[k])
                                {
                                    ((ToolStripButton)item).Visible = true;
                                    ((ToolStripButton)item).Enabled = false;
                                }
                            }
                            else
                            {
                                foreach (var item in displayTools[k])
                                {
                                    ((ToolStripButton)item).Visible = false;
                                }
                            }
                        }
                    }
                    break;
                case "點到展開的資料庫":
                case "點到Tables":
                    {
                        foreach (var k in displayTools.Keys)
                        {
                            if (k == "tables")
                            {
                                foreach (var item in displayTools[k])
                                {
                                    ((ToolStripButton)item).Visible = true;
                                    ((ToolStripButton)item).Enabled = false;
                                }
                                ((ToolStripButton)displayTools[k][2]).Enabled = true; //New Table
                                ((ToolStripButton)displayTools[k][4]).Enabled = true; //Import Wizard
                                ((ToolStripButton)displayTools[k][5]).Enabled = true; //Export Wizard
                            }
                            else
                            {
                                foreach (var item in displayTools[k])
                                {
                                    ((ToolStripButton)item).Visible = false;
                                }
                            }
                        }
                    }
                    break;
                case "點到Table本身":
                    {
                        foreach (var k in displayTools.Keys)
                        {
                            if (k == "tables")
                            {
                                foreach (var item in displayTools[k])
                                {
                                    ((ToolStripButton)item).Visible = true;
                                    ((ToolStripButton)item).Enabled = true;
                                }
                            }
                            else
                            {
                                foreach (var item in displayTools[k])
                                {
                                    ((ToolStripButton)item).Visible = false;
                                }
                            }
                        }
                    }
                    break;
                case "點到Views本身":
                    {
                        foreach (var k in displayTools.Keys)
                        {
                            if (k == "views")
                            {
                                foreach (var item in displayTools[k])
                                {
                                    ((ToolStripButton)item).Visible = true;
                                    ((ToolStripButton)item).Enabled = false;
                                }
                                ((ToolStripButton)displayTools[k][2]).Enabled = true; //New View
                                ((ToolStripButton)displayTools[k][4]).Enabled = true; //View Export Wizard
                            }
                            else
                            {
                                foreach (var item in displayTools[k])
                                {
                                    ((ToolStripButton)item).Visible = false;
                                }
                            }
                        }
                    }
                    break;
                case "點到View本身":
                    {
                        foreach (var k in displayTools.Keys)
                        {
                            if (k == "views")
                            {
                                foreach (var item in displayTools[k])
                                {
                                    ((ToolStripButton)item).Visible = true;
                                    ((ToolStripButton)item).Enabled = true;
                                }
                            }
                            else
                            {
                                foreach (var item in displayTools[k])
                                {
                                    ((ToolStripButton)item).Visible = false;
                                }
                            }
                        }
                    }
                    break;
                case "點到Functions本身":
                    {
                        foreach (var k in displayTools.Keys)
                        {
                            if (k == "functions")
                            {
                                foreach (var item in displayTools[k])
                                {
                                    ((ToolStripButton)item).Visible = true;
                                    ((ToolStripButton)item).Enabled = false;
                                }
                                ((ToolStripButton)displayTools[k][1]).Enabled = true; //New Function                                
                            }
                            else
                            {
                                foreach (var item in displayTools[k])
                                {
                                    ((ToolStripButton)item).Visible = false;
                                }
                            }
                        }
                    }
                    break;
                case "點到Function本身":
                    {
                        foreach (var k in displayTools.Keys)
                        {
                            if (k == "functions")
                            {
                                foreach (var item in displayTools[k])
                                {
                                    ((ToolStripButton)item).Visible = true;
                                    ((ToolStripButton)item).Enabled = true;
                                }
                            }
                            else
                            {
                                foreach (var item in displayTools[k])
                                {
                                    ((ToolStripButton)item).Visible = false;
                                }
                            }
                        }
                    }
                    break;
            }
        }
        public void UpdateMainStatus(string msg)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(UpdateMainStatus), msg);
                return;
            }
            if (lblMainStatus != null)
            {
                lblMainStatus.Text = msg;
            }
        }

        public void drawLists()
        {
            ImageList myImageList = new ImageList();
            string pwd = my.pwd();
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\mysql_close.png")); //0
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\mysql_open.png")); //1
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\postgresql_close.png")); //2
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\postgresql_open.png")); //3
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\oracle_close.png")); //4
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\oracle_open.png")); //5
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\sqlite_close.png")); //6
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\sqlite_open.png")); //7
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\sqlserver_close.png"));  //8
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\sqlserver_open.png"));  //9           
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\db_close.png"));  //10           
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\db_open.png"));  //11

            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\tables.png"));  //12
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\views.png"));  //13
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\functions.png"));  //14
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\events.png"));  //15
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\queries.png"));  //16
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\reports.png"));  //17
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\backups.png"));  //18
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\user.png"));  //19
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\model.png"));  //20
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\bi.png"));  //21
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\other.png"));  //22
            myImageList.Images.Add(GetShellFolderBitmap(false)); //23 folder_closed
            myImageList.Images.Add(GetShellFolderBitmap(true));  //24 folder_open

            // Assign the ImageList to the TreeView.

            db_tree.ShowRootLines = false;
            db_tree.ShowLines = true;
            db_tree.ShowNodeToolTips = ShouldShowObjectTooltips();

            db_tree.ShowPlusMinus = true;
            db_tree.Nodes.Clear();

            // 收集所有群組名稱：myN.groups（含空群組）＋連線衍生的群組，去重後排序
            bool hideConnectionGroups = ApplicationOptionSettings.GetBool("ViewHideConnectionGroups");
            var groupNames = hideConnectionGroups
                ? new List<string>()
                : myN.groups
                    .Concat(myN.connections.Select(c => GetConnectionValue(c, "conn_group")))
                    .Where(g => !string.IsNullOrWhiteSpace(g))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(g => g)
                    .ToList();

            // 建立群組節點
            var groupNodeMap = new Dictionary<string, TreeNode>(StringComparer.Ordinal);
            foreach (string grp in groupNames)
            {
                TreeNode gNode = new TreeNode(grp)
                {
                    Tag = ConnectionGroupNodeTag,
                    Name = ConnectionGroupNodeTag + ":" + grp,
                    ImageIndex = 23,          // folder_closed
                    SelectedImageIndex = 24,  // folder_open
                    NodeFont = new Font(db_tree.Font, FontStyle.Bold)
                };
                db_tree.Nodes.Add(gNode);
                groupNodeMap[grp] = gNode;
            }

            // 建立連線節點並記錄至 _connectionTreeNodes
            _connectionTreeNodes = new TreeNode[myN.connections.Count];
            for (int i = 0, max_i = myN.connections.Count; i < max_i; i++)
            {
                TreeNode newNode = new TreeNode(myN.connections[i]["conn_name"].ToString());
                newNode.Tag = i; // 以整數 Tag 儲存連線索引
                ApplyConnectionNodeIcon(
                    newNode,
                    myN.connections[i]["db_kind"].ToString(),
                    myN.connections[i]["isConnect"].ToString() != "F");
                TryPopulateOpenConnectionDatabases(newNode, i);

                string connGroup = GetConnectionValue(myN.connections[i], "conn_group");
                if (!hideConnectionGroups && !string.IsNullOrWhiteSpace(connGroup) && groupNodeMap.ContainsKey(connGroup))
                {
                    groupNodeMap[connGroup].Nodes.Add(newNode);
                }
                else
                {
                    db_tree.Nodes.Add(newNode);
                }
                _connectionTreeNodes[i] = newNode;
            }

            // 展開所有群組節點
            foreach (var gNode in groupNodeMap.Values) gNode.Expand();

            db_tree.ImageList = myImageList;
            db_tree.Indent = 20;
            db_tree.ItemHeight = 22;

            // 群組節點展開/收合時切換資料夾圖示
            db_tree.AfterExpand -= db_tree_AfterExpand;
            db_tree.AfterCollapse -= db_tree_AfterCollapse;
            db_tree.AfterExpand += db_tree_AfterExpand;
            db_tree.AfterCollapse += db_tree_AfterCollapse;

            SetWindowTheme(db_tree.Handle, "explorer", null);

        }

        private void db_tree_AfterExpand(object sender, TreeViewEventArgs e)
        {
            if (IsConnectionGroupNode(e.Node))
            {
                e.Node.ImageIndex = 24;          // folder_open
                e.Node.SelectedImageIndex = 24;
            }
        }

        private void db_tree_AfterCollapse(object sender, TreeViewEventArgs e)
        {
            if (IsConnectionGroupNode(e.Node))
            {
                e.Node.ImageIndex = 23;          // folder_closed
                e.Node.SelectedImageIndex = 24;  // 選取時仍顯示開啟
            }
        }

        private void thirty_two_change(string kind)
        {
            List<object> btns = new List<object>();
            btns.Add(table_btn);
            btns.Add(view_btn);
            btns.Add(function_btn);
            btns.Add(user_btn);
            btns.Add(other_btn);
            btns.Add(query_section_btn);
            btns.Add(backup_btn);
            btns.Add(auto_run_btn);
            btns.Add(model_btn);
            btns.Add(bi_btn);
            for (int i = 0, max_i = btns.Count; i < max_i; i++)
            {
                ((ToolStripButton)btns[i]).Checked = false;
                ((ToolStripButton)btns[i]).BackColor = ThemeManager.SurfaceColor;
            }
            switch (kind.ToLower())
            {
                case "user":
                    user_btn.BackColor = ThemeManager.SelectionColor;
                    user_btn.Checked = true;
                    break;
                case "table":
                    table_btn.BackColor = ThemeManager.SelectionColor;
                    table_btn.Checked = true;
                    break;
                case "view":
                    view_btn.BackColor = ThemeManager.SelectionColor;
                    view_btn.Checked = true;
                    break;
                case "function":
                    function_btn.BackColor = ThemeManager.SelectionColor;
                    function_btn.Checked = true;
                    break;
                case "other":
                    other_btn.BackColor = ThemeManager.SelectionColor;
                    other_btn.Checked = true;
                    break;
                case "query":
                    query_section_btn.BackColor = ThemeManager.SelectionColor;
                    query_section_btn.Checked = true;
                    break;
                case "backup":
                    backup_btn.BackColor = ThemeManager.SelectionColor;
                    backup_btn.Checked = true;
                    break;
                case "auto":
                    auto_run_btn.BackColor = ThemeManager.SelectionColor;
                    auto_run_btn.Checked = true;
                    break;
                case "model":
                    model_btn.BackColor = ThemeManager.SelectionColor;
                    model_btn.Checked = true;
                    break;
                case "bi":
                    bi_btn.BackColor = ThemeManager.SelectionColor;
                    bi_btn.Checked = true;
                    break;
            }
        }
        private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            MessageBox.Show(BuildAboutMessage(Application.ProductVersion), Localization.T("Menu.Help"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public static string BuildAboutMessage(string productVersion)
        {
            return
                "mySQLPunk\r\n\r\n" +
                "版本：" + productVersion + "\r\n" +
                "平台：.NET Framework WinForms\r\n" +
                "支援連線：MySQL、PostgreSQL、SQLite、SQL Server、Oracle\r\n\r\n" +
                "作者：\r\n" +
                "羽山秋人 ( https://3wa.tw )\r\n" +
                "NickYCLin\r\n" +
                "Codex 協作";
        }

        private async void CheckForUpdatesAsync(bool silent)
        {
            if (!silent) UpdateMainStatus(Localization.T("Update.Checking"));

            try
            {
                AppUpdateCheckResult result = await Task.Run(() =>
                    AppUpdateService.CheckGitHubLatestRelease(Application.ProductVersion));

                if (result.UpdateAvailable)
                {
                    string message = Localization.Format("Update.Available", result.LatestVersion, result.CurrentVersion);
                    DialogResult answer = MessageBox.Show(
                        message,
                        Localization.T("Menu.CheckUpdates"),
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Information);
                    if (answer == DialogResult.Yes)
                    {
                        if (!string.IsNullOrWhiteSpace(result.InstallerDownloadUrl))
                        {
                            await DownloadAndLaunchUpdateInstallerAsync(result);
                        }
                        else if (!string.IsNullOrWhiteSpace(result.PortableZipDownloadUrl))
                        {
                            await DownloadAndOpenPortableUpdateAsync(result);
                        }
                        else if (MessageBox.Show(
                            Localization.T("Update.OpenReleasePage"),
                            Localization.T("Menu.CheckUpdates"),
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Information) == DialogResult.Yes &&
                            !string.IsNullOrWhiteSpace(result.ReleasePageUrl))
                        {
                            Process.Start(new ProcessStartInfo(result.ReleasePageUrl) { UseShellExecute = true });
                        }
                    }
                    UpdateMainStatus(message.Replace("\n", " "));
                    return;
                }

                if (!silent)
                {
                    string message = Localization.T("Update.NotAvailable");
                    UpdateMainStatus(message);
                    MessageBox.Show(message, Localization.T("Menu.CheckUpdates"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                string message = Localization.Format("Update.CheckFailed", ex.Message);
                UpdateMainStatus(message);
                if (!silent)
                {
                    MessageBox.Show(message, Localization.T("Menu.CheckUpdates"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private async Task DownloadAndLaunchUpdateInstallerAsync(AppUpdateCheckResult result)
        {
            string downloadDirectory = Path.Combine(Path.GetTempPath(), "mySQLPunk", "updates");
            Directory.CreateDirectory(downloadDirectory);
            string targetPath = AppUpdateService.BuildInstallerDownloadPath(result, downloadDirectory);
            UpdateMainStatus(Localization.Format("Update.Downloading", AppUpdateService.GetInstallerFileName(result)));

            using (System.Net.WebClient client = new System.Net.WebClient())
            {
                client.Headers[System.Net.HttpRequestHeader.UserAgent] = "mySQLPunk-update-download";
                System.Net.IWebProxy proxy = ConnectionProxySettingsService.CreateWebProxyFromOptions();
                if (proxy != null) client.Proxy = proxy;
                await client.DownloadFileTaskAsync(new Uri(result.InstallerDownloadUrl), targetPath);
                await VerifyDownloadedUpdatePackageAsync(client, result, targetPath);
            }

            UpdateMainStatus(Localization.Format("Update.Downloaded", targetPath));
            Process.Start(new ProcessStartInfo(targetPath) { UseShellExecute = true });
        }

        private async Task DownloadAndOpenPortableUpdateAsync(AppUpdateCheckResult result)
        {
            string downloadDirectory = Path.Combine(Path.GetTempPath(), "mySQLPunk", "updates");
            Directory.CreateDirectory(downloadDirectory);
            string targetPath = AppUpdateService.BuildPortableZipDownloadPath(result, downloadDirectory);
            UpdateMainStatus(Localization.Format("Update.Downloading", AppUpdateService.GetPortableZipFileName(result)));

            using (System.Net.WebClient client = new System.Net.WebClient())
            {
                client.Headers[System.Net.HttpRequestHeader.UserAgent] = "mySQLPunk-update-download";
                System.Net.IWebProxy proxy = ConnectionProxySettingsService.CreateWebProxyFromOptions();
                if (proxy != null) client.Proxy = proxy;
                await client.DownloadFileTaskAsync(new Uri(result.PortableZipDownloadUrl), targetPath);
                await VerifyDownloadedUpdatePackageAsync(client, result, targetPath);
            }

            UpdateMainStatus(Localization.Format("Update.PortableDownloaded", targetPath));
            Process.Start(new ProcessStartInfo(targetPath) { UseShellExecute = true });
        }

        private async Task VerifyDownloadedUpdatePackageAsync(System.Net.WebClient client, AppUpdateCheckResult result, string targetPath)
        {
            if (client == null || result == null || string.IsNullOrWhiteSpace(result.ReleaseManifestDownloadUrl)) return;
            if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath)) return;

            string fileName = Path.GetFileName(targetPath);
            UpdateMainStatus(Localization.Format("Update.Verifying", fileName));
            string manifestJson = await client.DownloadStringTaskAsync(new Uri(result.ReleaseManifestDownloadUrl));
            string expectedSha256 = AppUpdateService.FindExpectedSha256InReleaseManifest(manifestJson, fileName);
            if (string.IsNullOrWhiteSpace(expectedSha256)) return;

            string actualSha256;
            if (!AppUpdateService.VerifyFileSha256(targetPath, expectedSha256, out actualSha256))
            {
                throw new InvalidDataException(Localization.Format(
                    "Update.HashMismatch",
                    fileName,
                    ShortHash(expectedSha256),
                    ShortHash(actualSha256)));
            }

            UpdateMainStatus(Localization.Format("Update.Verified", fileName));
        }

        private static string ShortHash(string hash)
        {
            string value = (hash ?? string.Empty).Trim();
            return value.Length <= 12 ? value : value.Substring(0, 12);
        }

        private void OpenConnectionToolStripMenuItem_Click(object sender, EventArgs e)
        {
            TreeNode root = GetSelectedConnectionRoot();
            if (root == null)
            {
                UpdateMainStatus(Localization.T("Status.SelectConnection"));
                return;
            }

            db_tree.SelectedNode = root;
            db_tree_DoubleClick(db_tree, EventArgs.Empty);
        }

        private void CloseConnectionToolStripMenuItem_Click(object sender, EventArgs e)
        {
            TreeNode root = GetSelectedConnectionRoot();
            if (root == null)
            {
                UpdateMainStatus(Localization.T("Status.SelectConnection"));
                return;
            }

            CloseConnection(GetConnectionIndex(root));
        }

        private void ExportToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = Localization.T("Connection.ExportTitle");
                dialog.Filter = Localization.T("Connection.JsonFilter");
                dialog.FileName = "mysqlpunk-connections.json";
                dialog.DefaultExt = "json";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    ExportConnectionsToFile(dialog.FileName);
                    UpdateMainStatus(Localization.T("Status.ConnectionsExported"));
                    MessageBox.Show(Localization.T("Status.ConnectionsExported"), Localization.T("Common.Success"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Localization.T("Status.ExportFailed") + ex.Message, Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void ImportConnectionsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = Localization.T("Connection.ImportTitle");
                dialog.Filter = Localization.T("Connection.JsonFilter");
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    ConnectionImportPreviewReport preview = BuildConnectionImportPreview(dialog.FileName, myN.connections);
                    ConnectionImportDecision decision = ShowConnectionImportPreviewDialog(preview);
                    if (decision == null || decision.Mode == ConnectionImportMode.Cancel) return;

                    if (decision.Mode == ConnectionImportMode.Replace)
                    {
                        ImportConnectionsFromFile(dialog.FileName);
                    }
                    else
                    {
                        MergeImportedConnections(preview, decision.SelectedImportedIndexes);
                    }

                    string reviewLogPath = TryWriteConnectionImportReviewLog(preview, decision);
                    int savedPasswords = PromptForImportedConnectionPasswords();
                    string message = savedPasswords > 0
                        ? Localization.Format("Status.ConnectionsImportedWithPasswords", savedPasswords)
                        : Localization.T("Status.ConnectionsImported");
                    if (!string.IsNullOrWhiteSpace(reviewLogPath))
                    {
                        message += Environment.NewLine + Localization.Format("Connection.ImportReviewLogWritten", reviewLogPath);
                    }
                    UpdateMainStatus(message);
                    MessageBox.Show(message, Localization.T("Common.Success"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Localization.T("Status.ImportFailed") + ex.Message, Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private bool ExportConnectionsToFile(string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return false;
            }

            string dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            myN.exportConnections(targetPath);
            SignConnectionExportFile(targetPath);
            return true;
        }

        private int ImportConnectionsFromFile(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                return -1;
            }

            CloseAllConnectionsBeforeImport();
            myN.importConnections(sourcePath);
            drawLists();
            return myN.connections.Count;
        }

        private void MergeImportedConnections(ConnectionImportPreviewReport preview, HashSet<int> selectedImportedIndexes)
        {
            if (preview == null || selectedImportedIndexes == null || selectedImportedIndexes.Count == 0) return;

            CloseAllConnectionsBeforeImport();
            Dictionary<string, int> existingIndexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < myN.connections.Count; i++)
            {
                string key = BuildConnectionImportKey(myN.connections[i]);
                if (!existingIndexByKey.ContainsKey(key)) existingIndexByKey[key] = i;
            }

            foreach (int importedIndex in selectedImportedIndexes.OrderBy(i => i))
            {
                if (importedIndex < 0 || importedIndex >= preview.ImportedConnections.Count) continue;
                Dictionary<string, object> imported = new Dictionary<string, object>(preview.ImportedConnections[importedIndex]);
                imported["isConnect"] = "F";
                imported["pdo"] = null;
                string key = BuildConnectionImportKey(imported);
                int existingIndex;
                if (existingIndexByKey.TryGetValue(key, out existingIndex))
                {
                    myN.connections[existingIndex] = imported;
                }
                else
                {
                    myN.connections.Add(imported);
                    existingIndexByKey[key] = myN.connections.Count - 1;
                }
            }

            foreach (string groupName in preview.ImportedGroups)
            {
                if (!string.IsNullOrWhiteSpace(groupName) && !myN.groups.Contains(groupName))
                {
                    myN.groups.Add(groupName);
                }
            }

            myN.setSettingINI();
            drawLists();
        }

        private static string TryWriteConnectionImportReviewLog(ConnectionImportPreviewReport preview, ConnectionImportDecision decision)
        {
            if (preview == null || decision == null || decision.Mode == ConnectionImportMode.Cancel) return string.Empty;

            try
            {
                IEnumerable<int> selectedIndexes = decision.Mode == ConnectionImportMode.Replace
                    ? Enumerable.Range(0, preview.ImportedConnections.Count)
                    : (IEnumerable<int>)decision.SelectedImportedIndexes;
                string action = decision.Mode == ConnectionImportMode.Replace ? "replaceAll" : "mergeSelected";
                return WriteConnectionImportReviewLog(preview, action, selectedIndexes, BuildConnectionImportReviewLogPath());
            }
            catch
            {
                return string.Empty;
            }
        }

        private ConnectionImportDecision ShowConnectionImportPreviewDialog(ConnectionImportPreviewReport preview)
        {
            using (Form dialog = new Form())
            using (Label summaryLabel = new Label())
            using (DataGridView grid = new DataGridView())
            using (Button replaceButton = new Button())
            using (Button mergeButton = new Button())
            using (Button trustSourceButton = new Button())
            using (Button cancelButton = new Button())
            {
                dialog.Text = Localization.T("Connection.ImportPreviewTitle");
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.MinimizeBox = false;
                dialog.ShowInTaskbar = false;
                dialog.Size = new Size(900, 560);
                dialog.MinimumSize = new Size(760, 440);

                summaryLabel.Dock = DockStyle.Top;
                summaryLabel.Height = 124;
                summaryLabel.Padding = new Padding(12, 10, 12, 8);
                summaryLabel.Text = BuildConnectionImportPreviewSummary(preview);

                DataTable table = BuildConnectionImportPreviewTable(preview);
                grid.Dock = DockStyle.Fill;
                grid.AllowUserToAddRows = false;
                grid.AllowUserToDeleteRows = false;
                grid.RowHeadersVisible = false;
                grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
                grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
                grid.DataSource = table;
                grid.DataBindingComplete += (s, e) =>
                {
                    if (grid.Columns.Contains("_importedIndex")) grid.Columns["_importedIndex"].Visible = false;
                    if (grid.Columns.Contains("_status")) grid.Columns["_status"].Visible = false;
                    if (grid.Columns.Contains(Localization.T("Connection.ImportPreviewInclude")))
                    {
                        grid.Columns[Localization.T("Connection.ImportPreviewInclude")].Width = 64;
                        grid.Columns[Localization.T("Connection.ImportPreviewInclude")].AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
                    }
                };

                FlowLayoutPanel footer = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    Height = 52,
                    FlowDirection = FlowDirection.RightToLeft,
                    Padding = new Padding(8)
                };

                replaceButton.Text = Localization.T("Connection.ImportPreviewReplaceAll");
                replaceButton.AutoSize = true;
                replaceButton.Click += (s, e) =>
                {
                    if (!ConfirmConnectionImportTrust(preview)) return;
                    if (MessageBox.Show(Localization.T("Connection.ImportReplaceConfirm"), Localization.T("Common.Confirm"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    {
                        dialog.Tag = CreateConnectionImportDecision(ConnectionImportMode.Replace, table);
                        dialog.DialogResult = DialogResult.OK;
                    }
                };

                mergeButton.Text = Localization.T("Connection.ImportPreviewMergeSelected");
                mergeButton.AutoSize = true;
                mergeButton.Click += (s, e) =>
                {
                    grid.EndEdit();
                    ConnectionImportDecision decision = CreateConnectionImportDecision(ConnectionImportMode.Merge, table);
                    if (decision.SelectedImportedIndexes.Count == 0)
                    {
                        MessageBox.Show(Localization.T("Connection.ImportPreviewSelectAtLeastOne"), dialog.Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    if (!ConfirmConnectionImportTrust(preview)) return;
                    dialog.Tag = decision;
                    dialog.DialogResult = DialogResult.OK;
                };

                trustSourceButton.Text = Localization.T("Connection.ImportTrustSource");
                trustSourceButton.AutoSize = true;
                trustSourceButton.Enabled = CanTrustConnectionImportSource(preview);
                trustSourceButton.Visible = trustSourceButton.Enabled;
                trustSourceButton.Click += (s, e) =>
                {
                    try
                    {
                        TrustConnectionImportSource(preview.SourceId, preview.SourceSignature, BuildTrustedConnectionImportSourcesPath());
                        preview.SourceTrusted = true;
                        summaryLabel.Text = BuildConnectionImportPreviewSummary(preview);
                        trustSourceButton.Enabled = false;
                        trustSourceButton.Visible = false;
                        MessageBox.Show(Localization.T("Connection.ImportSourceTrusted"), dialog.Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(Localization.Format("Connection.ImportTrustSourceFailed", ex.Message), dialog.Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                };

                cancelButton.Text = Localization.T("Common.Cancel");
                cancelButton.AutoSize = true;
                cancelButton.DialogResult = DialogResult.Cancel;
                footer.Controls.Add(cancelButton);
                footer.Controls.Add(mergeButton);
                footer.Controls.Add(replaceButton);
                footer.Controls.Add(trustSourceButton);

                dialog.Controls.Add(grid);
                dialog.Controls.Add(summaryLabel);
                dialog.Controls.Add(footer);
                dialog.CancelButton = cancelButton;
                ThemeManager.ApplyTo(dialog);

                return dialog.ShowDialog(this) == DialogResult.OK
                    ? dialog.Tag as ConnectionImportDecision
                    : new ConnectionImportDecision { Mode = ConnectionImportMode.Cancel };
            }
        }

        private static DataTable BuildConnectionImportPreviewTable(ConnectionImportPreviewReport preview)
        {
            DataTable table = new DataTable();
            table.Columns.Add("_importedIndex", typeof(int));
            table.Columns.Add("_status");
            table.Columns.Add(Localization.T("Connection.ImportPreviewInclude"), typeof(bool));
            table.Columns.Add(Localization.T("Connection.ImportPreviewStatus"));
            table.Columns.Add(Localization.T("Common.ConnectionName"));
            table.Columns.Add(Localization.T("Common.ConnectionType"));
            table.Columns.Add(Localization.T("Common.Host"));

            if (preview == null) return table;
            foreach (ConnectionImportPreviewEntry entry in preview.Entries)
            {
                DataRow row = table.NewRow();
                row["_importedIndex"] = entry.ImportedIndex;
                row["_status"] = entry.Status;
                row[Localization.T("Connection.ImportPreviewInclude")] = entry.Include;
                row[Localization.T("Connection.ImportPreviewStatus")] = FormatConnectionImportPreviewStatus(entry.Status);
                row[Localization.T("Common.ConnectionName")] = entry.ConnectionName;
                row[Localization.T("Common.ConnectionType")] = entry.DbKind;
                row[Localization.T("Common.Host")] = entry.Target;
                table.Rows.Add(row);
            }
            return table;
        }

        private static string BuildConnectionImportPreviewSummary(ConnectionImportPreviewReport preview)
        {
            string summary = Localization.Format(
                "Connection.ImportPreviewSummary",
                preview?.Added ?? 0,
                preview?.Updated ?? 0,
                preview?.Unchanged ?? 0,
                preview?.ExistingOnly ?? 0);

            if (preview == null) return summary;
            return summary + Environment.NewLine +
                   BuildConnectionImportSignatureSummary(preview) + Environment.NewLine +
                   BuildConnectionImportTrustSummary(preview) + Environment.NewLine +
                   BuildConnectionImportReviewSummary(preview);
        }

        private static string BuildConnectionImportReviewSummary(ConnectionImportPreviewReport preview)
        {
            if (preview == null) return string.Empty;

            int activeChanges = preview.Added + preview.Updated;
            int passwordRequired = preview.ImportedConnections.Count(ConnectionNeedsPasswordAfterImport);
            string sourceState = BuildConnectionImportReviewSourceState(preview);
            string groupSummary = preview.ImportedGroups.Count == 0
                ? Localization.T("Connection.ImportReviewNoGroups")
                : string.Join(", ", preview.ImportedGroups.Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g, StringComparer.OrdinalIgnoreCase).Take(5).ToArray());
            string targetSummary = BuildConnectionImportReviewTargets(preview);

            return Localization.Format(
                "Connection.ImportReviewSummary",
                sourceState,
                activeChanges,
                preview.ExistingOnly,
                passwordRequired,
                groupSummary,
                targetSummary);
        }

        private static string BuildConnectionImportReviewSourceState(ConnectionImportPreviewReport preview)
        {
            if (preview == null || !preview.SourceSignaturePresent) return Localization.T("Connection.ImportReviewSourceUnsigned");
            if (!preview.SourceSignatureValid) return Localization.T("Connection.ImportReviewSourceInvalid");
            if (preview.SourceTrusted) return Localization.T("Connection.ImportReviewSourceTrusted");
            return Localization.T("Connection.ImportReviewSourceUntrusted");
        }

        private static string BuildConnectionImportReviewTargets(ConnectionImportPreviewReport preview)
        {
            if (preview == null || preview.Entries.Count == 0) return Localization.T("Connection.ImportReviewNoTargets");

            List<string> targets = new List<string>();
            foreach (ConnectionImportPreviewEntry entry in preview.Entries)
            {
                if (!string.Equals(entry.Status, "added", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(entry.Status, "updated", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string item = entry.DbKind + " " + entry.ConnectionName + "@" + entry.Target;
                if (!targets.Contains(item, StringComparer.OrdinalIgnoreCase)) targets.Add(item);
                if (targets.Count >= 5) break;
            }

            return targets.Count == 0
                ? Localization.T("Connection.ImportReviewNoTargets")
                : string.Join("; ", targets.ToArray());
        }

        private static string BuildConnectionImportReviewLogPath()
        {
            return Path.Combine(GetConnectionTrustStoreDirectory(), "connection-import-review-log.jsonl");
        }

        private static string WriteConnectionImportReviewLog(
            ConnectionImportPreviewReport preview,
            string action,
            IEnumerable<int> selectedImportedIndexes,
            string reviewLogPath)
        {
            if (preview == null) throw new ArgumentNullException(nameof(preview));
            if (string.IsNullOrWhiteSpace(reviewLogPath)) throw new ArgumentException("Review log path is required.", nameof(reviewLogPath));

            ConnectionImportReviewLogEntry entry = BuildConnectionImportReviewLogEntry(preview, action, selectedImportedIndexes);
            string dir = Path.GetDirectoryName(reviewLogPath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(
                reviewLogPath,
                JsonConvert.SerializeObject(entry, Formatting.None) + Environment.NewLine,
                new UTF8Encoding(false));
            return reviewLogPath;
        }

        private static ConnectionImportReviewLogEntry BuildConnectionImportReviewLogEntry(
            ConnectionImportPreviewReport preview,
            string action,
            IEnumerable<int> selectedImportedIndexes)
        {
            if (preview == null) throw new ArgumentNullException(nameof(preview));

            HashSet<int> selected = selectedImportedIndexes == null
                ? new HashSet<int>()
                : new HashSet<int>(selectedImportedIndexes.Where(i => i >= 0));
            bool hasExplicitSelection = selected.Count > 0;

            List<ConnectionImportReviewLogTarget> targets = new List<ConnectionImportReviewLogTarget>();
            foreach (ConnectionImportPreviewEntry entry in preview.Entries)
            {
                if (entry.ImportedIndex < 0) continue;

                bool isChanged = string.Equals(entry.Status, "added", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(entry.Status, "updated", StringComparison.OrdinalIgnoreCase);
                bool isSelected = selected.Contains(entry.ImportedIndex);
                if (!isChanged && !isSelected) continue;

                targets.Add(new ConnectionImportReviewLogTarget
                {
                    Selected = hasExplicitSelection ? isSelected : entry.Include,
                    Status = entry.Status ?? string.Empty,
                    ImportedIndex = entry.ImportedIndex,
                    ConnectionName = entry.ConnectionName ?? string.Empty,
                    DbKind = entry.DbKind ?? string.Empty,
                    Target = entry.Target ?? string.Empty
                });
            }

            return new ConnectionImportReviewLogEntry
            {
                FormatVersion = 1,
                ReviewedAtUtc = DateTime.UtcNow.ToString("o"),
                Action = string.IsNullOrWhiteSpace(action) ? "unknown" : action.Trim(),
                SourceId = preview.SourceId ?? string.Empty,
                SourceSignatureSha256 = preview.SourceSignature ?? string.Empty,
                SourceSignatureState = BuildConnectionImportReviewSourceState(preview),
                SourceTrusted = preview.SourceTrusted,
                SourceExportedAtUtc = preview.SourceExportedAtUtc ?? string.Empty,
                Added = preview.Added,
                Updated = preview.Updated,
                Unchanged = preview.Unchanged,
                ExistingOnly = preview.ExistingOnly,
                PasswordsRequired = preview.ImportedConnections.Count(ConnectionNeedsPasswordAfterImport),
                SelectedImportedCount = hasExplicitSelection ? selected.Count : preview.Entries.Count(e => e.ImportedIndex >= 0 && e.Include),
                Groups = preview.ImportedGroups
                    .Where(g => !string.IsNullOrWhiteSpace(g))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Targets = targets
            };
        }

        private static string BuildConnectionImportSignatureSummary(ConnectionImportPreviewReport preview)
        {
            if (preview == null || !preview.SourceSignaturePresent)
            {
                return Localization.T("Connection.ImportSignatureMissing");
            }

            string shortSignature = preview.SourceSignature ?? "";
            if (shortSignature.Length > 12) shortSignature = shortSignature.Substring(0, 12);
            string status = preview.SourceSignatureValid
                ? Localization.T("Connection.ImportSignatureValid")
                : Localization.T("Connection.ImportSignatureInvalid");

            if (string.IsNullOrWhiteSpace(preview.SourceExportedAtUtc))
            {
                return Localization.Format("Connection.ImportSignatureSummary", status, shortSignature);
            }

            return Localization.Format("Connection.ImportSignatureSummaryWithTime", status, shortSignature, preview.SourceExportedAtUtc);
        }

        private static string BuildConnectionImportTrustSummary(ConnectionImportPreviewReport preview)
        {
            if (preview == null || !preview.SourceSignaturePresent)
            {
                return Localization.T("Connection.ImportTrustMissingSignature");
            }

            if (!preview.SourceSignatureValid)
            {
                return Localization.T("Connection.ImportTrustSkippedInvalidSignature");
            }

            if (string.IsNullOrWhiteSpace(preview.SourceId))
            {
                return Localization.T("Connection.ImportTrustMissingSourceId");
            }

            return preview.SourceTrusted
                ? Localization.T("Connection.ImportTrustKnown")
                : Localization.T("Connection.ImportTrustUnknown");
        }

        private static bool CanTrustConnectionImportSource(ConnectionImportPreviewReport preview)
        {
            return preview != null &&
                   preview.SourceSignaturePresent &&
                   preview.SourceSignatureValid &&
                   !preview.SourceTrusted &&
                   !string.IsNullOrWhiteSpace(preview.SourceId);
        }

        private bool ConfirmConnectionImportTrust(ConnectionImportPreviewReport preview)
        {
            if (preview == null) return true;
            if (preview.SourceSignaturePresent && preview.SourceSignatureValid && preview.SourceTrusted) return true;

            string message;
            if (!preview.SourceSignaturePresent)
            {
                message = Localization.T("Connection.ImportTrustConfirmMissingSignature");
            }
            else if (!preview.SourceSignatureValid)
            {
                message = Localization.T("Connection.ImportTrustConfirmInvalidSignature");
            }
            else if (string.IsNullOrWhiteSpace(preview.SourceId))
            {
                message = Localization.T("Connection.ImportTrustConfirmMissingSourceId");
            }
            else
            {
                message = Localization.T("Connection.ImportTrustConfirmUnknown");
            }

            return MessageBox.Show(message, Localization.T("Common.Warning"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
        }

        private static ConnectionImportDecision CreateConnectionImportDecision(ConnectionImportMode mode, DataTable table)
        {
            ConnectionImportDecision decision = new ConnectionImportDecision { Mode = mode };
            if (table == null) return decision;

            foreach (DataRow row in table.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;
                if (row["_importedIndex"] == DBNull.Value) continue;
                int importedIndex = Convert.ToInt32(row["_importedIndex"]);
                if (importedIndex < 0) continue;
                bool include = row[Localization.T("Connection.ImportPreviewInclude")] != DBNull.Value &&
                               Convert.ToBoolean(row[Localization.T("Connection.ImportPreviewInclude")]);
                if (include) decision.SelectedImportedIndexes.Add(importedIndex);
            }
            return decision;
        }

        private static string FormatConnectionImportPreviewStatus(string status)
        {
            if (string.Equals(status, "added", StringComparison.OrdinalIgnoreCase)) return Localization.T("Connection.ImportPreviewAdded");
            if (string.Equals(status, "updated", StringComparison.OrdinalIgnoreCase)) return Localization.T("Connection.ImportPreviewUpdated");
            if (string.Equals(status, "unchanged", StringComparison.OrdinalIgnoreCase)) return Localization.T("Connection.ImportPreviewUnchanged");
            return Localization.T("Connection.ImportPreviewExistingOnly");
        }

        private int PromptForImportedConnectionPasswords()
        {
            List<ImportedConnectionPasswordTarget> targets = BuildImportedConnectionPasswordTargets(myN.connections);
            if (targets.Count == 0) return 0;

            DialogResult answer = MessageBox.Show(
                Localization.Format("Connection.ImportPasswordPrompt", targets.Count),
                Localization.T("Connection.ImportPasswordTitle"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return 0;

            Dictionary<int, string> passwords = ShowImportedConnectionPasswordDialog(targets);
            if (passwords == null || passwords.Count == 0) return 0;

            int saved = 0;
            foreach (KeyValuePair<int, string> pair in passwords)
            {
                if (pair.Key < 0 || pair.Key >= myN.connections.Count) continue;
                if (string.IsNullOrEmpty(pair.Value)) continue;
                myN.connections[pair.Key]["pwd"] = pair.Value;
                saved++;
            }

            if (saved > 0)
            {
                myN.setSettingINI();
            }

            return saved;
        }

        private static List<ImportedConnectionPasswordTarget> BuildImportedConnectionPasswordTargets(IEnumerable<Dictionary<string, object>> connections)
        {
            List<ImportedConnectionPasswordTarget> targets = new List<ImportedConnectionPasswordTarget>();
            if (connections == null) return targets;

            int index = 0;
            foreach (Dictionary<string, object> conn in connections)
            {
                if (ConnectionNeedsPasswordAfterImport(conn))
                {
                    targets.Add(new ImportedConnectionPasswordTarget
                    {
                        Index = index,
                        ConnectionName = GetConnectionValue(conn, "conn_name"),
                        DbKind = GetConnectionValue(conn, "db_kind"),
                        Username = GetConnectionValue(conn, "username"),
                        Target = BuildImportedConnectionPasswordTargetText(conn)
                    });
                }
                index++;
            }

            return targets;
        }

        private static bool ConnectionNeedsPasswordAfterImport(Dictionary<string, object> conn)
        {
            if (conn == null) return false;
            if (!string.IsNullOrEmpty(GetConnectionValue(conn, "pwd"))) return false;

            string kind = GetConnectionValue(conn, "db_kind").ToLowerInvariant();
            if (kind == "sqlite") return false;
            if ((kind == "mssql" || kind == "sqlserver") && GetConnectionValue(conn, "trusted_connection") == "T") return false;

            switch (kind)
            {
                case "mysql":
                case "postgresql":
                case "oracle":
                case "mssql":
                case "sqlserver":
                    return !string.IsNullOrWhiteSpace(GetConnectionValue(conn, "username"));
                default:
                    return false;
            }
        }

        private static string BuildImportedConnectionPasswordTargetText(Dictionary<string, object> conn)
        {
            string kind = GetConnectionValue(conn, "db_kind").ToLowerInvariant();
            if (kind == "sqlite") return GetConnectionValue(conn, "path");
            if (kind == "oracle")
            {
                string tns = GetConnectionValue(conn, "tns_name");
                if (!string.IsNullOrWhiteSpace(tns)) return tns;
                string service = GetConnectionValue(conn, "service_name");
                string sid = GetConnectionValue(conn, "sid");
                string host = GetConnectionValue(conn, "host");
                string port = GetConnectionValue(conn, "port");
                string name = string.IsNullOrWhiteSpace(service) ? sid : service;
                return host + (string.IsNullOrWhiteSpace(port) ? "" : ":" + port) +
                       (string.IsNullOrWhiteSpace(name) ? "" : "/" + name);
            }

            string target = GetConnectionValue(conn, "host");
            string targetPort = GetConnectionValue(conn, "port");
            if (!string.IsNullOrWhiteSpace(targetPort)) target += ":" + targetPort;
            return target;
        }

        private Dictionary<int, string> ShowImportedConnectionPasswordDialog(List<ImportedConnectionPasswordTarget> targets)
        {
            using (Form dialog = new Form())
            using (Label label = new Label())
            using (DataGridView grid = new DataGridView())
            using (Button okButton = new Button())
            using (Button cancelButton = new Button())
            {
                dialog.Text = Localization.T("Connection.ImportPasswordTitle");
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.MinimizeBox = false;
                dialog.ShowInTaskbar = false;
                dialog.Size = new Size(820, 500);
                dialog.MinimumSize = new Size(700, 420);

                label.Dock = DockStyle.Top;
                label.Height = 56;
                label.Padding = new Padding(12, 10, 12, 8);
                label.Text = Localization.T("Connection.ImportPasswordDescription");

                DataTable table = BuildImportedConnectionPasswordTable(targets);
                grid.Dock = DockStyle.Fill;
                grid.AllowUserToAddRows = false;
                grid.AllowUserToDeleteRows = false;
                grid.RowHeadersVisible = false;
                grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
                grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
                grid.DataSource = table;
                grid.DataBindingComplete += (s, e) =>
                {
                    if (grid.Columns.Contains("_index")) grid.Columns["_index"].Visible = false;
                    if (grid.Columns.Contains("Password")) grid.Columns["Password"].HeaderText = Localization.T("Common.Password");
                };
                grid.CellFormatting += (s, e) =>
                {
                    if (grid.Columns[e.ColumnIndex].Name == "Password" && e.Value != null && !string.IsNullOrEmpty(e.Value.ToString()))
                    {
                        e.Value = new string('*', Math.Min(12, e.Value.ToString().Length));
                        e.FormattingApplied = true;
                    }
                };

                FlowLayoutPanel footer = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    Height = 48,
                    FlowDirection = FlowDirection.RightToLeft,
                    Padding = new Padding(8)
                };
                okButton.Text = Localization.T("Connection.ImportPasswordSave");
                okButton.DialogResult = DialogResult.OK;
                okButton.AutoSize = true;
                cancelButton.Text = Localization.T("Common.Cancel");
                cancelButton.DialogResult = DialogResult.Cancel;
                cancelButton.AutoSize = true;
                footer.Controls.Add(cancelButton);
                footer.Controls.Add(okButton);

                dialog.Controls.Add(grid);
                dialog.Controls.Add(label);
                dialog.Controls.Add(footer);
                dialog.AcceptButton = okButton;
                dialog.CancelButton = cancelButton;
                ThemeManager.ApplyTo(dialog);

                if (dialog.ShowDialog(this) != DialogResult.OK) return null;
                return CollectImportedConnectionPasswords(table);
            }
        }

        private static DataTable BuildImportedConnectionPasswordTable(List<ImportedConnectionPasswordTarget> targets)
        {
            DataTable table = new DataTable();
            table.Columns.Add("_index", typeof(int));
            table.Columns.Add(Localization.T("Common.ConnectionName"));
            table.Columns.Add(Localization.T("Common.ConnectionType"));
            table.Columns.Add(Localization.T("Common.Username"));
            table.Columns.Add(Localization.T("Common.Host"));
            table.Columns.Add("Password");

            foreach (ImportedConnectionPasswordTarget target in targets)
            {
                DataRow row = table.NewRow();
                row[0] = target.Index;
                row[1] = target.ConnectionName;
                row[2] = target.DbKind;
                row[3] = target.Username;
                row[4] = target.Target;
                row[5] = "";
                table.Rows.Add(row);
            }
            return table;
        }

        private static Dictionary<int, string> CollectImportedConnectionPasswords(DataTable table)
        {
            Dictionary<int, string> passwords = new Dictionary<int, string>();
            if (table == null) return passwords;

            foreach (DataRow row in table.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;
                string password = row["Password"] == DBNull.Value ? "" : row["Password"].ToString();
                if (string.IsNullOrEmpty(password)) continue;
                passwords[Convert.ToInt32(row["_index"])] = password;
            }
            return passwords;
        }

        private void CloseAllConnectionsBeforeImport()
        {
            foreach (Dictionary<string, object> conn in myN.connections)
            {
                if (conn == null) continue;

                try
                {
                    if (conn.ContainsKey("pdo") && conn["pdo"] is IDatabase db)
                    {
                        db.Close();
                        db.Dispose();
                    }
                }
                catch
                {
                }

                conn["isConnect"] = "F";
                if (conn.ContainsKey("pdo"))
                {
                    conn.Remove("pdo");
                }
            }
        }

        private void CloseToolStripMenuItem_Click(object sender, EventArgs e)
        {
            CloseSelectedTab();
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }

        private void splitContainer2_Panel1_Paint(object sender, PaintEventArgs e)
        {

        }
        private void UI_init()
        {
            splitContainer5.Orientation = Orientation.Vertical;
            splitContainer5.SplitterDistance = splitContainer5.Width - 300; 

            ConfigureMainMenu();

            // 將 table_top 移到 Panel1 並設為 Fill
            table_top.Parent = splitContainer5.Panel1;
            table_top.Dock = DockStyle.Fill;
            table_top.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None; // 必須先關閉 RowHeader 相關自動調整
            table_top.RowHeadersWidthSizeMode = DataGridViewRowHeadersWidthSizeMode.EnableResizing;
            table_top.RowHeadersVisible = false;
            
            // 確保狀態列在最底端且可見 (保留 Resize 功能)
            statusStrip1.Dock = DockStyle.Bottom;
            statusStrip1.Visible = true; 
            statusStrip1.SizingGrip = true; // 恢復 Resize 手柄
            statusStrip1.SendToBack(); 
            lblMainStatus = new ToolStripStatusLabel(Localization.T("Status.Ready")) { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            statusStrip1.Items.Clear();
            statusStrip1.Items.Add(lblMainStatus);

            // 確保上方只有兩排：選單列 + 圖示工具列。
            menuStrip1.Dock = DockStyle.Top;
            menuStrip1.Visible = true;
            menuStrip1.BringToFront();
            
            // 確保容器高度足夠容納工具列
            splitContainer1.SplitterDistance = MainToolbarHeight;
            tool_Connection.Dock = DockStyle.Fill;
            tool_Connection.AutoSize = false;
            tool_Connection.Height = MainToolbarHeight;
            tool_Connection.ImageScalingSize = new Size(MainToolbarIconSize, MainToolbarIconSize);
            tool_Connection.GripStyle = ToolStripGripStyle.Hidden;
            tool_Connection.Padding = new Padding(10, 8, 10, 4);

            // 初始化所有頂部工具列按鈕的樣式
            foreach (ToolStripItem item in tool_Connection.Items)
            {
                item.AutoSize = false;
                item.Size = new Size(MainToolbarItemWidth, MainToolbarItemHeight);
                item.Padding = new Padding(0, 3, 0, 2);
                if (item is ToolStripButton btn) btn.TextImageRelation = TextImageRelation.ImageAboveText;
                if (item is ToolStripDropDownButton dd) dd.TextImageRelation = TextImageRelation.ImageAboveText;
            }
            Resize -= Form1_Resize;
            Resize += Form1_Resize;
            ArrangeMainLayout();

            table_top.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            table_top.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            table_top.AllowUserToAddRows = false;
            table_top.BackgroundColor = Color.White;
            table_top.GridColor = Color.FromArgb(240, 240, 240);
            table_top.BorderStyle = BorderStyle.None;
            InitObjectFilterPanel();

            queryTabs = new TabControl();
            queryTabs.Dock = DockStyle.Fill;
            queryTabs.Visible = false;
            queryTabs.Parent = splitContainer5.Panel1; // 同樣在 Panel1

            // ── 啟用頁籤自定義繪製與拖拽功能 ──
            queryTabs.DrawMode = TabDrawMode.OwnerDrawFixed;
            queryTabs.Padding = new Point(24, 4); // 留更多空間給 X
            queryTabs.AllowDrop = true; // 支援拖放回巢
            queryTabs.DrawItem += QueryTabs_DrawItem;
            queryTabs.MouseDown += QueryTabs_MouseDown;
            queryTabs.MouseMove += QueryTabs_MouseMove;
            queryTabs.MouseUp += QueryTabs_MouseUp;
            queryTabs.MouseClick += QueryTabs_MouseClick;
            queryTabs.DragEnter += QueryTabs_DragEnter;
            queryTabs.DragDrop += QueryTabs_DragDrop;

            // ── 父容器與 table_top 只負責轉送拖放事件，真正可 dock 的範圍會限制在頁籤列 ──
            splitContainer5.Panel1.AllowDrop = true;
            splitContainer5.Panel1.DragEnter += QueryTabs_DragEnter;
            splitContainer5.Panel1.DragOver += QueryTabs_DragOver;
            splitContainer5.Panel1.DragLeave += QueryTabs_DragLeave;
            splitContainer5.Panel1.DragDrop += QueryTabs_DragDrop;

            // table_top 蓋在 Panel1 上，必須也開 AllowDrop；但內容區不會觸發 dock
            table_top.AllowDrop = true;
            table_top.DragEnter += QueryTabs_DragEnter;
            table_top.DragOver += QueryTabs_DragOver;
            table_top.DragLeave += QueryTabs_DragLeave;
            table_top.DragDrop += QueryTabs_DragDrop;

            // 初始化 dock 提示框 (懸浮視窗拖回時的視覺指引)
            _dockHintOverlay = new Panel() { Dock = DockStyle.Top, Height = 36, Visible = false, BackColor = Color.FromArgb(220, 235, 255) };
            _dockHintOverlay.Paint += (s, e) =>
            {
                using (var pen = new Pen(ThemeManager.AccentColor, 4))
                    e.Graphics.DrawRectangle(pen, 2, 2, _dockHintOverlay.Width - 5, _dockHintOverlay.Height - 5);
                string msg = Localization.T("Status.ReleaseToDock");
                using (var font = new Font("Microsoft JhengHei", 14, FontStyle.Bold))
                using (var brush = new SolidBrush(ThemeManager.SelectionTextColor))
                {
                    var sz = e.Graphics.MeasureString(msg, font);
                    e.Graphics.DrawString(msg, font, brush,
                        12f,
                        (_dockHintOverlay.Height - sz.Height) / 2f);
                }
            };
            splitContainer5.Panel1.Controls.Add(_dockHintOverlay);

            // 初始化側邊欄 (Sidebar) 在 Panel2
            InitSidebar();
            ApplyViewPaneSettings();
            List<object> d = new List<object>();
            d.Add(OpenTable);
            d.Add(DesignTable);
            d.Add(NewTable);
            d.Add(DeleteTable);
            d.Add(ImportWizard);
            d.Add(ExportWizard);
            displayTools.Add("tables", d);
            d = new List<object>();
            d.Add(OpenView);
            d.Add(DesignView);
            d.Add(NewView);
            d.Add(DeleteView);
            d.Add(View_ExportWizard);
            displayTools.Add("views", d);
            d = new List<object>();
            d.Add(DesignFunction);
            d.Add(NewFunction);
            d.Add(DeleteFunction);
            d.Add(ExecuteFunction);
            displayTools.Add("functions", d);
            showTools("點到連線");
            table_top.Width = splitContainer5.Width;
            table_top.Height = splitContainer5.Height;

            string imgPath = Path.Combine(Application.StartupPath, "image");
            ConfigureMainToolbar(imgPath);
            ApplyLanguage();

            // 連結第二層工具列事件
            OpenTable.Click += (s, e) => OpenSelectedTableInQuery();
            DesignTable.Click += DesignTable_Click;
            NewTable.Click += (s, e) => CreateNewTable();
            DeleteTable.Click += (s, e) => DeleteSelectedTable();
            ImportWizard.Click += (s, e) => ImportSqlWithDialog();
            ExportWizard.Click += (s, e) => DumpCurrentSelectionSqlWithDialog(false);
            OpenView.Click += (s, e) => OpenSelectedViewInQuery();
            DesignView.Click += (s, e) => ShowSelectedViewDefinition();
            NewView.Click += (s, e) => CreateNewView();
            DeleteView.Click += (s, e) => DeleteSelectedView();
            View_ExportWizard.Click += (s, e) => DumpCurrentSelectionSqlWithDialog(false);
            DesignFunction.Click += (s, e) => ShowSelectedFunctionDefinition();
            NewFunction.Click += (s, e) => CreateNewFunction();
            DeleteFunction.Click += (s, e) => DeleteSelectedFunction();
            ExecuteFunction.Click += (s, e) => ExecuteSelectedFunction();

            // 連結右鍵選單事件
            db_tree.NodeMouseClick += db_tree_NodeMouseClick;
            db_tree.MouseDown += db_tree_MouseDown;
            db_tree.AllowDrop = true;
            db_tree.DragEnter -= DbTree_DragEnter;
            db_tree.DragDrop -= DbTree_DragDrop;
            db_tree.DragEnter += DbTree_DragEnter;
            db_tree.DragDrop += DbTree_DragDrop;

            tool_Connection.BringToFront();

            queryTabs.MouseClick += queryTabs_MouseClick;
            table_top.CellMouseDown += table_top_CellMouseDown;
            table_top.CellDoubleClick += table_top_CellDoubleClick;
            table_top.CellFormatting += table_top_CellFormatting;
            table_top.DataBindingComplete += table_top_DataBindingComplete;
            table_top.DataError += table_top_DataError;
            db_tree.KeyDown += db_tree_KeyDown;
            db_tree.LabelEdit = false;
            db_tree.BeforeLabelEdit += db_tree_BeforeLabelEdit;
            db_tree.AfterLabelEdit += db_tree_AfterLabelEdit;
            newConnectionToolStripMenuItem.Click -= NewConnectionToolStripMenuItem_Click;
            newConnectionToolStripMenuItem.Click += NewConnectionToolStripMenuItem_Click;
            openConnectionToolStripMenuItem.Click -= OpenConnectionToolStripMenuItem_Click;
            openConnectionToolStripMenuItem.Click += OpenConnectionToolStripMenuItem_Click;
            closeConnectionToolStripMenuItem.Click -= CloseConnectionToolStripMenuItem_Click;
            closeConnectionToolStripMenuItem.Click += CloseConnectionToolStripMenuItem_Click;
            exportToolStripMenuItem.Click -= ExportToolStripMenuItem_Click;
            exportToolStripMenuItem.Click += ExportToolStripMenuItem_Click;
            importConnectionsToolStripMenuItem.Click -= ImportConnectionsToolStripMenuItem_Click;
            importConnectionsToolStripMenuItem.Click += ImportConnectionsToolStripMenuItem_Click;
            closeToolStripMenuItem.Click -= CloseToolStripMenuItem_Click;
            closeToolStripMenuItem.Click += CloseToolStripMenuItem_Click;

        }

        private static bool LooksLikeSqliteExtension(string path)
        {
            string ext = (Path.GetExtension(path) ?? string.Empty).Trim().ToLowerInvariant();
            return ext == ".db" || ext == ".sqlite" || ext == ".sqlite3" || ext == ".db3" || ext == ".s3db";
        }

        private static bool LooksLikeSqliteHeader(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || Directory.Exists(path)) return false;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] header = new byte[16];
                    int read = fs.Read(header, 0, header.Length);
                    if (read != header.Length) return false;
                    byte[] magic = Encoding.ASCII.GetBytes("SQLite format 3\0");
                    for (int i = 0; i < magic.Length; i++)
                        if (header[i] != magic[i]) return false;
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private int FindConnectionIndexBySqlitePath(string sqlitePath)
        {
            if (string.IsNullOrWhiteSpace(sqlitePath)) return -1;
            for (int i = 0; i < myN.connections.Count; i++)
            {
                var conn = myN.connections[i];
                if (conn == null) continue;
                if (!conn.ContainsKey("db_kind") || conn["db_kind"] == null) continue;
                if (!string.Equals(conn["db_kind"].ToString(), "sqlite", StringComparison.OrdinalIgnoreCase)) continue;
                if (!conn.ContainsKey("path") || conn["path"] == null) continue;
                if (string.Equals(conn["path"].ToString(), sqlitePath, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        private string MakeUniqueConnectionName(string baseName)
        {
            string name = (baseName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name)) name = "SQLite";

            var existing = new HashSet<string>(
                myN.connections
                    .Where(c => c != null && c.ContainsKey("conn_name") && c["conn_name"] != null)
                    .Select(c => c["conn_name"].ToString()),
                StringComparer.OrdinalIgnoreCase);

            if (!existing.Contains(name)) return name;
            for (int i = 2; i < 1000; i++)
            {
                string candidate = name + " (" + i + ")";
                if (!existing.Contains(candidate)) return candidate;
            }
            return name + " (" + DateTime.Now.ToString("yyyyMMddHHmmss") + ")";
        }

        private string GetDropTargetGroupName(DragEventArgs e)
        {
            try
            {
                if (ApplicationOptionSettings.GetBool("ViewHideConnectionGroups")) return string.Empty;
                if (db_tree == null) return string.Empty;

                Point pt = db_tree.PointToClient(new Point(e.X, e.Y));
                TreeNode node = db_tree.GetNodeAt(pt);
                while (node != null)
                {
                    if (IsConnectionGroupNode(node)) return node.Text;
                    node = node.Parent;
                }
            }
            catch { }
            return string.Empty;
        }

        private void DbTree_DragEnter(object sender, DragEventArgs e)
        {
            try
            {
                if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    e.Effect = DragDropEffects.None;
                    return;
                }

                string[] paths = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (paths == null || paths.Length == 0)
                {
                    e.Effect = DragDropEffects.None;
                    return;
                }

                bool hasSqlite = paths.Any(p => LooksLikeSqliteExtension(p) || LooksLikeSqliteHeader(p));
                e.Effect = hasSqlite ? DragDropEffects.Copy : DragDropEffects.None;
            }
            catch
            {
                e.Effect = DragDropEffects.None;
            }
        }

        private void DbTree_DragDrop(object sender, DragEventArgs e)
        {
            if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths == null || paths.Length == 0) return;

            string targetGroup = GetDropTargetGroupName(e);
            var sqliteFiles = paths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .Where(p => File.Exists(p) && !Directory.Exists(p))
                .Where(p => LooksLikeSqliteExtension(p) || LooksLikeSqliteHeader(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (sqliteFiles.Count == 0) return;

            int lastIndex = -1;
            foreach (string sqlitePath in sqliteFiles)
            {
                int existingIndex = FindConnectionIndexBySqlitePath(sqlitePath);
                if (existingIndex >= 0)
                {
                    lastIndex = existingIndex;
                    continue;
                }

                Dictionary<string, object> conn = new Dictionary<string, object>();
                conn["conn_name"] = MakeUniqueConnectionName(Path.GetFileNameWithoutExtension(sqlitePath));
                conn["host"] = "";
                conn["port"] = "";
                conn["initial_database"] = "main";
                conn["db_kind"] = "sqlite";
                conn["username"] = "";
                conn["pwd"] = "";
                conn["path"] = sqlitePath;
                conn["init_geospatial"] = "T";
                conn["isConnect"] = "F";
                if (!string.IsNullOrWhiteSpace(targetGroup)) conn["conn_group"] = targetGroup;

                add_connection(conn);
                lastIndex = myN.connections.Count - 1;
            }

            if (lastIndex >= 0)
            {
                TreeNode node = FindConnectionNode(lastIndex);
                if (node != null)
                {
                    db_tree.SelectedNode = node;
                    node.EnsureVisible();
                }
            }
        }

        private void ConfigureMainMenu()
        {
            檔案ToolStripMenuItem.Text = Localization.T("Menu.File");
            newConnectionToolStripMenuItem.Text = Localization.T("Menu.NewConnection");
            openConnectionToolStripMenuItem.Text = Localization.T("Menu.OpenConnection");
            closeConnectionToolStripMenuItem.Text = Localization.T("Menu.CloseConnection");
            exportToolStripMenuItem.Text = Localization.T("Menu.ExportConnections");
            importConnectionsToolStripMenuItem.Text = Localization.T("Menu.ImportConnections");
            closeToolStripMenuItem.Text = Localization.T("Menu.Close");
            exitToolStripMenuItem.Text = Localization.T("Menu.Exit");
            ttToolStripMenuItem.Text = Localization.T("Menu.Window");
            helpToolStripMenuItem.Text = Localization.T("Menu.Help");
            aboutToolStripMenuItem.Text = Localization.T("Menu.About");
            ToolStripMenuItem checkUpdatesMenu = new ToolStripMenuItem(Localization.T("Menu.CheckUpdates"));

            ToolStripMenuItem editMenu = new ToolStripMenuItem(Localization.T("Menu.Edit"));
            ToolStripMenuItem viewMenu = new ToolStripMenuItem(Localization.T("Menu.View"));
            ToolStripMenuItem favoriteMenu = new ToolStripMenuItem(Localization.T("Menu.Favorites"));
            ToolStripMenuItem toolsMenu = new ToolStripMenuItem(Localization.T("Menu.Tools"));
            ToolStripMenuItem editCopyMenu = new ToolStripMenuItem(Localization.T("Tool.CopyObject"));
            ToolStripMenuItem editPasteMenu = new ToolStripMenuItem(Localization.T("Tool.PasteObject"));
            ToolStripMenuItem editRenameMenu = new ToolStripMenuItem(Localization.T("Tool.RenameObject"));
            ToolStripMenuItem windowCloseMenu = new ToolStripMenuItem(Localization.T("Menu.Close"));
            ToolStripMenuItem languageMenu = new ToolStripMenuItem(Localization.T("Menu.Language"));
            ToolStripMenuItem zhMenu = new ToolStripMenuItem(Localization.T("Menu.LanguageZh"));
            ToolStripMenuItem enMenu = new ToolStripMenuItem(Localization.T("Menu.LanguageEn"));
            ToolStripMenuItem themeMenu = new ToolStripMenuItem(Localization.T("Menu.Theme"));
            ToolStripMenuItem lightMenu = new ToolStripMenuItem(Localization.T("Menu.ThemeLight"));
            ToolStripMenuItem darkMenu = new ToolStripMenuItem(Localization.T("Menu.ThemeDark"));
            ToolStripMenuItem optionsMenu = new ToolStripMenuItem(Localization.T("Menu.Options"));
            ToolStripMenuItem dataDictionaryMenu = new ToolStripMenuItem(Localization.T("Menu.ToolDataDictionary"));
            ToolStripMenuItem queryHistoryMenu = new ToolStripMenuItem(Localization.T("Menu.ToolQueryHistory"));
            ToolStripMenuItem backupsMenu = new ToolStripMenuItem(Localization.T("Menu.ToolBackups"));
            ToolStripMenuItem diagnosticsMenu = new ToolStripMenuItem(Localization.T("Menu.ToolConnectionDiagnostics"));
            ToolStripMenuItem capabilitiesMenu = new ToolStripMenuItem(Localization.T("Menu.ToolProviderCapabilities"));
            ToolStripMenuItem maintenanceMenu = new ToolStripMenuItem(Localization.T("Menu.ToolMaintenanceChecklist"));
            zhMenu.Checked = !Localization.IsEnglish;
            enMenu.Checked = Localization.IsEnglish;
            zhMenu.Click += (s, e) => ApplyPreferences(Localization.TraditionalChinese, ThemeManager.CurrentTheme, "Status.LanguageChanged");
            enMenu.Click += (s, e) => ApplyPreferences(Localization.English, ThemeManager.CurrentTheme, "Status.LanguageChanged");
            languageMenu.DropDownItems.AddRange(new ToolStripItem[] { zhMenu, enMenu });
            lightMenu.Checked = !ThemeManager.IsDark;
            darkMenu.Checked = ThemeManager.IsDark;
            lightMenu.Click += (s, e) => ApplyPreferences(Localization.CurrentLanguage, ThemeManager.Light, "Status.ThemeChanged");
            darkMenu.Click += (s, e) => ApplyPreferences(Localization.CurrentLanguage, ThemeManager.Dark, "Status.ThemeChanged");
            themeMenu.DropDownItems.AddRange(new ToolStripItem[] { lightMenu, darkMenu });
            editCopyMenu.Click += (s, e) => CopySelectedDatabaseObjectToInternalClipboard();
            editPasteMenu.Click += (s, e) => PasteInternalClipboardToSelectedDatabase();
            editRenameMenu.Click += (s, e) => BeginRenameSelectedDatabaseObject();
            editMenu.DropDownItems.AddRange(new ToolStripItem[] { editCopyMenu, editPasteMenu, editRenameMenu });
            BuildViewMenuItems(viewMenu);
            ConfigureFavoritesMenu(favoriteMenu);
            windowCloseMenu.Click += (s, e) => CloseSelectedTab();
            ttToolStripMenuItem.DropDownItems.Clear();
            ttToolStripMenuItem.DropDownItems.Add(windowCloseMenu);
            dataDictionaryMenu.Click += (s, e) => SelectDatabaseGroupNode("Models");
            queryHistoryMenu.Click += (s, e) => ShowQueryHistoryForSelectedDatabase();
            backupsMenu.Click += (s, e) => SelectDatabaseGroupNode("Backups");
            diagnosticsMenu.Click += (s, e) => ShowSelectedOtherTool("Connection Diagnostics");
            capabilitiesMenu.Click += (s, e) => ShowSelectedOtherTool("Provider Capabilities");
            maintenanceMenu.Click += (s, e) => ShowSelectedOtherTool("Maintenance Checklist");
            optionsMenu.Click += (s, e) => OpenOptionsDialog();
            checkUpdatesMenu.Click += (s, e) => CheckForUpdatesAsync(false);
            toolsMenu.DropDownItems.AddRange(new ToolStripItem[]
            {
                dataDictionaryMenu,
                queryHistoryMenu,
                backupsMenu,
                new ToolStripSeparator(),
                diagnosticsMenu,
                capabilitiesMenu,
                maintenanceMenu,
                new ToolStripSeparator(),
                optionsMenu,
                new ToolStripSeparator(),
                languageMenu,
                themeMenu
            });
            helpToolStripMenuItem.DropDownItems.Clear();
            helpToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[]
            {
                checkUpdatesMenu,
                new ToolStripSeparator(),
                aboutToolStripMenuItem
            });

            menuStrip1.Items.Clear();
            menuStrip1.Items.AddRange(new ToolStripItem[]
            {
                檔案ToolStripMenuItem,
                editMenu,
                viewMenu,
                favoriteMenu,
                toolsMenu,
                ttToolStripMenuItem,
                helpToolStripMenuItem
            });
        }

        private void ShowSelectedOtherTool(string toolName)
        {
            TreeDatabaseTarget target = BuildTargetFromNode(db_tree.SelectedNode);
            if (target == null)
            {
                TreeNode databaseNode = GetSelectedDatabaseNode();
                target = BuildTargetFromNode(databaseNode);
            }

            if (target == null)
            {
                UpdateMainStatus(Localization.T("Status.SelectExpandedDatabase"));
                return;
            }

            EnsureDatabaseGroupNodes(target.DatabaseNode);
            foreach (TreeNode child in target.DatabaseNode.Nodes)
            {
                if (string.Equals(GetTreeGroupKey(child), "Other", StringComparison.OrdinalIgnoreCase))
                {
                    db_tree.SelectedNode = child;
                    child.EnsureVisible();
                    break;
                }
            }

            thirty_two_change("other");
            ShowDatabaseOtherTool(target.Database, target.DatabaseName, toolName, target.ConnectionInfo);
        }

        private void ShowQueryHistoryForSelectedDatabase()
        {
            TreeDatabaseTarget target = BuildTargetFromNode(db_tree.SelectedNode);
            if (target == null)
            {
                TreeNode databaseNode = GetSelectedDatabaseNode();
                target = BuildTargetFromNode(databaseNode);
            }

            if (target == null)
            {
                UpdateMainStatus(Localization.T("Status.SelectExpandedDatabase"));
                return;
            }

            EnsureDatabaseGroupNodes(target.DatabaseNode);
            foreach (TreeNode child in target.DatabaseNode.Nodes)
            {
                if (string.Equals(GetTreeGroupKey(child), "Queries", StringComparison.OrdinalIgnoreCase))
                {
                    db_tree.SelectedNode = child;
                    child.EnsureVisible();
                    break;
                }
            }

            thirty_two_change("query");
            table_top.DataSource = BuildQueryHistoryTable(target.DatabaseName);
            UpdateMainStatus("Query history loaded: " + target.DatabaseName);
        }

        private void BuildViewMenuItems(ToolStripMenuItem viewMenu)
        {
            viewMenu.DropDownItems.Clear();

            ToolStripMenuItem navigationPaneMenu = new ToolStripMenuItem(Localization.T("View.NavigationPane"));
            ToolStripMenuItem showNavigationPaneItem = CreateCheckedViewMenuItem(
                Localization.T("View.ShowNavigationPane"),
                ApplicationOptionSettings.GetBool("ViewShowNavigationPane"),
                value => SetNavigationPaneVisible(value));
            ToolStripMenuItem hideConnectionGroupsItem = CreateCheckedViewMenuItem(
                Localization.T("View.HideConnectionGroups"),
                ApplicationOptionSettings.GetBool("ViewHideConnectionGroups"),
                value => SetHideConnectionGroups(value));
            ToolStripMenuItem activeObjectsOnlyItem = CreateCheckedViewMenuItem(
                Localization.T("View.ActiveObjectsOnly") + "\tF12",
                ApplicationOptionSettings.GetBool("ViewActiveObjectsOnly"),
                value => SetActiveObjectsOnly(value));
            ToolStripMenuItem showTopFilterItem = CreateCheckedViewMenuItem(
                Localization.T("View.ShowTopFilter"),
                ApplicationOptionSettings.GetBool("ViewShowTopFilter"),
                value => SetObjectFilterVisible(value));
            navigationPaneMenu.DropDownItems.AddRange(new ToolStripItem[]
            {
                showNavigationPaneItem,
                hideConnectionGroupsItem,
                activeObjectsOnlyItem,
                showTopFilterItem
            });

            ToolStripMenuItem infoPaneMenu = new ToolStripMenuItem(Localization.T("View.InfoPane"));
            ToolStripMenuItem showInfoPaneItem = CreateCheckedViewMenuItem(
                Localization.T("View.ShowInfoPane"),
                ApplicationOptionSettings.GetBool("ViewShowInfoPane"),
                value => SetInfoPaneVisible(value));
            ToolStripMenuItem generalInfoItem = CreateCheckedViewMenuItem(
                Localization.T("Common.General"),
                !ApplicationOptionSettings.GetBool("ViewInfoPaneAiMode"),
                value => SetInfoPaneMode(false));
            ToolStripMenuItem aiInfoItem = CreateCheckedViewMenuItem(
                Localization.T("Options.AI"),
                ApplicationOptionSettings.GetBool("ViewInfoPaneAiMode"),
                value => SetInfoPaneMode(true));
            infoPaneMenu.DropDownItems.AddRange(new ToolStripItem[] { showInfoPaneItem, new ToolStripSeparator(), generalInfoItem, aiInfoItem });

            ToolStripMenuItem listModeItem = CreateCheckedViewMenuItem(
                Localization.T("View.ListMode"),
                string.Equals(ApplicationOptionSettings.GetString("ViewObjectListMode"), "list", StringComparison.OrdinalIgnoreCase),
                value => SetObjectListMode("list"));
            ToolStripMenuItem detailModeItem = CreateCheckedViewMenuItem(
                Localization.T("View.DetailMode"),
                !string.Equals(ApplicationOptionSettings.GetString("ViewObjectListMode"), "list", StringComparison.OrdinalIgnoreCase),
                value => SetObjectListMode("details"));
            ToolStripMenuItem erDiagramItem = new ToolStripMenuItem(Localization.T("View.ERDiagram")) { Enabled = false };
            ToolStripMenuItem hideObjectGroupsItem = CreateCheckedViewMenuItem(
                Localization.T("View.HideObjectGroups"),
                ApplicationOptionSettings.GetBool("ViewHideObjectGroups"),
                value => SetHideObjectGroups(value));

            ToolStripMenuItem sortMenu = new ToolStripMenuItem(Localization.T("Menu.Sort"));
            AddSortMenuItem(sortMenu, "名稱");
            AddSortMenuItem(sortMenu, "群組");
            AddSortMenuItem(sortMenu, "自動遞增值");
            AddSortMenuItem(sortMenu, "列格式");
            AddSortMenuItem(sortMenu, "修改日期");
            AddSortMenuItem(sortMenu, "建立日期");
            AddSortMenuItem(sortMenu, "索引長度");
            AddSortMenuItem(sortMenu, "資料長度");
            AddSortMenuItem(sortMenu, "引擎");
            AddSortMenuItem(sortMenu, "列");
            AddSortMenuItem(sortMenu, "最大資料長度");
            AddSortMenuItem(sortMenu, "資料可用空間");
            AddSortMenuItem(sortMenu, "檢查時間");
            AddSortMenuItem(sortMenu, "定序");
            AddSortMenuItem(sortMenu, "建立選項");
            AddSortMenuItem(sortMenu, "註解");
            sortMenu.DropDownItems.Add(new ToolStripSeparator());
            sortMenu.DropDownItems.Add(new ToolStripMenuItem(Localization.T("View.AdvancedSort")) { Enabled = false });
            ToolStripMenuItem reverseSortItem = CreateCheckedViewMenuItem(
                Localization.T("View.ReverseSort"),
                ApplicationOptionSettings.GetBool("ViewSortDescending"),
                value => SetSortDescending(value));
            sortMenu.DropDownItems.Add(reverseSortItem);

            ToolStripMenuItem chooseColumnsItem = new ToolStripMenuItem(Localization.T("View.ChooseColumns"));
            chooseColumnsItem.Click += (s, e) => OpenViewColumnChooser();

            ToolStripMenuItem showHiddenItemsItem = CreateCheckedViewMenuItem(
                Localization.T("View.ShowHiddenItems"),
                ApplicationOptionSettings.GetBool("ViewShowHiddenItems"),
                value => SetShowHiddenItems(value));
            ToolStripMenuItem showTableSettingsItem = CreateCheckedViewMenuItem(
                Localization.T("View.ShowTableSettingsProfile"),
                ApplicationOptionSettings.GetBool("RememberTableSettings"),
                value =>
                {
                    ApplicationOptionSettings.SetBool("RememberTableSettings", value);
                    ApplicationOptionSettings.Save();
                    UpdateMainStatus(Localization.T("View.TableSettingsProfileChanged"));
                });

            viewMenu.DropDownItems.AddRange(new ToolStripItem[]
            {
                navigationPaneMenu,
                infoPaneMenu,
                new ToolStripSeparator(),
                listModeItem,
                detailModeItem,
                erDiagramItem,
                new ToolStripSeparator(),
                hideObjectGroupsItem,
                sortMenu,
                chooseColumnsItem,
                new ToolStripSeparator(),
                showHiddenItemsItem,
                showTableSettingsItem
            });
        }

        private ToolStripMenuItem CreateCheckedViewMenuItem(string text, bool isChecked, Action<bool> onChanged)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text)
            {
                Checked = isChecked,
                CheckOnClick = true
            };
            item.Click += (s, e) => onChanged?.Invoke(item.Checked);
            return item;
        }

        private void AddSortMenuItem(ToolStripMenuItem sortMenu, string columnName)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(columnName)
            {
                Checked = string.Equals(ApplicationOptionSettings.GetString("ViewSortColumn"), columnName, StringComparison.OrdinalIgnoreCase)
            };
            item.Click += (s, e) => ApplySortColumn(columnName);
            sortMenu.DropDownItems.Add(item);
        }

        private void AddViewGroupMenuItem(ToolStripMenuItem viewMenu, string text, string groupName)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Click += (s, e) => SelectDatabaseGroupNode(groupName);
            viewMenu.DropDownItems.Add(item);
        }

        private void ApplyViewPaneSettings()
        {
            SetNavigationPaneVisible(ApplicationOptionSettings.GetBool("ViewShowNavigationPane"), false);
            SetInfoPaneVisible(ApplicationOptionSettings.GetBool("ViewShowInfoPane"), false);
            SetObjectListMode(ApplicationOptionSettings.GetString("ViewObjectListMode"), false);
            SetObjectFilterVisible(ApplicationOptionSettings.GetBool("ViewShowTopFilter"), false);
            SetInfoPaneMode(ApplicationOptionSettings.GetBool("ViewInfoPaneAiMode"), false);
        }

        private void SetNavigationPaneVisible(bool visible, bool save = true)
        {
            if (splitContainer3 != null)
            {
                splitContainer3.Panel1Collapsed = !visible;
            }

            if (splitContainer4 != null)
            {
                splitContainer4.Panel1Collapsed = !visible;
            }

            if (save)
            {
                ApplicationOptionSettings.SetBool("ViewShowNavigationPane", visible);
                ApplicationOptionSettings.Save();
                ConfigureMainMenu();
                UpdateMainStatus(Localization.T(visible ? "View.NavigationPaneShown" : "View.NavigationPaneHidden"));
            }
        }

        private void SetInfoPaneVisible(bool visible, bool save = true)
        {
            if (splitContainer5 != null)
            {
                splitContainer5.Panel2Collapsed = !visible;
            }

            if (save)
            {
                ApplicationOptionSettings.SetBool("ViewShowInfoPane", visible);
                ApplicationOptionSettings.Save();
                ConfigureMainMenu();
                UpdateMainStatus(Localization.T(visible ? "View.InfoPaneShown" : "View.InfoPaneHidden"));
            }
        }

        private void SetInfoPaneMode(bool aiMode, bool save = true)
        {
            if (save)
            {
                ApplicationOptionSettings.SetBool("ViewInfoPaneAiMode", aiMode);
                ApplicationOptionSettings.Save();
                ConfigureMainMenu();
            }

            if (aiMode && dgvDetails != null && rtbDDL != null && btnInfo != null && btnDDL != null)
            {
                dgvDetails.Visible = false;
                rtbDDL.Visible = true;
                btnInfo.Checked = false;
                btnDDL.Checked = true;
                if (string.IsNullOrWhiteSpace(rtbDDL.Text))
                {
                    rtbDDL.Text = Localization.T("View.AiPaneHint");
                }
            }
        }

        private void SetHideConnectionGroups(bool value)
        {
            ApplicationOptionSettings.SetBool("ViewHideConnectionGroups", value);
            ApplicationOptionSettings.Save();
            RefreshConnectionList();
            ConfigureMainMenu();
            UpdateMainStatus(Localization.T("View.ConnectionGroupsChanged"));
        }

        private void SaveViewBoolOption(string key, bool value, string statusKey)
        {
            ApplicationOptionSettings.SetBool(key, value);
            ApplicationOptionSettings.Save();
            ConfigureMainMenu();
            UpdateMainStatus(Localization.T(statusKey));
        }

        private void SetHideObjectGroups(bool value)
        {
            ApplicationOptionSettings.SetBool("ViewHideObjectGroups", value);
            ApplicationOptionSettings.Save();
            RefreshCurrentObjectView();
            ConfigureMainMenu();
            UpdateMainStatus(Localization.T("View.ObjectGroupsChanged"));
        }

        private void SetObjectFilterVisible(bool visible, bool save = true)
        {
            if (pnlObjectFilter != null)
            {
                pnlObjectFilter.Visible = visible && table_top != null && table_top.Visible;
                pnlObjectFilter.BringToFront();
            }

            if (!visible && txtObjectFilter != null && txtObjectFilter.TextLength > 0)
            {
                txtObjectFilter.Clear();
            }
            else
            {
                ApplyObjectListFilter();
            }

            if (save)
            {
                ApplicationOptionSettings.SetBool("ViewShowTopFilter", visible);
                ApplicationOptionSettings.Save();
                ConfigureMainMenu();
                UpdateMainStatus(Localization.T("View.TopFilterChanged"));
            }
        }

        private void UpdateObjectFilterVisibility()
        {
            SetObjectFilterVisible(ApplicationOptionSettings.GetBool("ViewShowTopFilter"), false);
        }

        private void ApplyObjectListFilter()
        {
            if (table_top == null) return;
            DataTable table = table_top.DataSource as DataTable;
            if (table == null) return;

            string filter = DataViewFilterService.BuildContainsFilter(table, txtObjectFilter == null ? "" : txtObjectFilter.Text);
            try
            {
                table.DefaultView.RowFilter = filter;
            }
            catch
            {
                table.DefaultView.RowFilter = string.Empty;
            }
        }

        private void SetShowHiddenItems(bool value)
        {
            ApplicationOptionSettings.SetBool("ViewShowHiddenItems", value);
            ApplicationOptionSettings.Save();
            RefreshCurrentObjectView();
            ConfigureMainMenu();
            UpdateMainStatus(Localization.T("View.HiddenItemsChanged"));
        }

        private void SetActiveObjectsOnly(bool value)
        {
            ApplicationOptionSettings.SetBool("ViewActiveObjectsOnly", value);
            ApplicationOptionSettings.Save();
            RefreshCurrentObjectView();
            ConfigureMainMenu();
            UpdateMainStatus(Localization.T("View.ActiveObjectsOnlyChanged"));
        }

        private void RefreshCurrentObjectView()
        {
            TreeNode selected = db_tree == null ? null : db_tree.SelectedNode;
            TreeDatabaseTarget target = BuildTargetFromNode(selected);
            if (target != null && target.DatabaseNode != null)
            {
                RefreshDatabaseObjectNodes(target.DatabaseNode);
                return;
            }

            TreeNode databaseNode = GetSelectedDatabaseNode();
            if (databaseNode != null)
            {
                RefreshDatabaseObjectNodes(databaseNode);
            }
        }

        private void SetObjectListMode(string mode, bool save = true)
        {
            string normalized = string.Equals(mode, "list", StringComparison.OrdinalIgnoreCase) ? "list" : "details";
            if (table_top != null)
            {
                table_top.RowHeadersVisible = false;
                table_top.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
                table_top.AutoSizeColumnsMode = normalized == "list"
                    ? DataGridViewAutoSizeColumnsMode.DisplayedCells
                    : DataGridViewAutoSizeColumnsMode.Fill;
            }

            if (save)
            {
                ApplicationOptionSettings.SetString("ViewObjectListMode", normalized);
                ApplicationOptionSettings.Save();
                ConfigureMainMenu();
                UpdateMainStatus(Localization.T(normalized == "list" ? "View.ListModeApplied" : "View.DetailModeApplied"));
            }
        }

        private void ApplySortColumn(string columnName)
        {
            ApplicationOptionSettings.SetString("ViewSortColumn", columnName);
            ApplicationOptionSettings.Save();
            ApplyGridSortPreference();
            ConfigureMainMenu();
            UpdateMainStatus(Localization.Format("View.SortApplied", columnName));
        }

        private void SetSortDescending(bool descending)
        {
            ApplicationOptionSettings.SetBool("ViewSortDescending", descending);
            ApplicationOptionSettings.Save();
            ApplyGridSortPreference();
            ConfigureMainMenu();
            UpdateMainStatus(Localization.T(descending ? "View.DescendingSortApplied" : "View.AscendingSortApplied"));
        }

        private void ApplyGridSortPreference()
        {
            if (table_top == null) return;
            DataTable table = table_top.DataSource as DataTable;
            if (table == null) return;

            string columnName = ApplicationOptionSettings.GetString("ViewSortColumn");
            string sortExpression = DataViewSortService.BuildSortExpression(table, columnName, ApplicationOptionSettings.GetBool("ViewSortDescending"));
            table.DefaultView.Sort = sortExpression;
            ApplyObjectListFilter();
        }

        private void OpenViewColumnChooser()
        {
            string provider = GetCurrentViewProvider();
            string group = GetCurrentViewGroup();
            using (ViewColumnChooserForm form = new ViewColumnChooserForm(provider, group))
            {
                if (form.ShowDialog(this) == DialogResult.OK)
                {
                    ApplyCurrentColumnPreferences(form.SelectedGroup);
                    ConfigureMainMenu();
                    UpdateMainStatus(Localization.T("View.ColumnPreferencesApplied"));
                }
            }
        }

        private string GetCurrentViewProvider()
        {
            TreeDatabaseTarget target = GetTargetFromCurrentSelection();
            if (target != null && !string.IsNullOrWhiteSpace(target.ProviderName)) return target.ProviderName;

            TreeNode root = GetSelectedConnectionRoot();
            int index = GetConnectionIndex(root);
            if (index >= 0 && index < myN.connections.Count) return GetConnectionValue(myN.connections[index], "db_kind");

            return "mysql";
        }

        private string GetCurrentViewGroup()
        {
            TreeNode node = db_tree == null ? null : db_tree.SelectedNode;
            string[] parts = GetTreePathParts(node);
            if (parts.Length >= 3) return parts[2];
            return "Tables";
        }

        private void ApplyCurrentColumnPreferences(string groupName = null)
        {
            if (table_top == null || table_top.Columns.Count == 0) return;

            string provider = GetCurrentViewProvider();
            string group = string.IsNullOrWhiteSpace(groupName) ? GetCurrentViewGroup() : groupName;
            List<ViewColumnPreference> prefs = ViewColumnPreferenceService.Load(provider, group);
            Dictionary<string, ViewColumnPreference> prefMap = prefs.ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);

            int displayIndex = 0;
            foreach (DataGridViewColumn column in table_top.Columns)
            {
                if (!prefMap.ContainsKey(column.Name))
                {
                    column.Visible = true;
                    column.DisplayIndex = displayIndex++;
                }
            }

            foreach (ViewColumnPreference pref in prefs)
            {
                if (!table_top.Columns.Contains(pref.Name)) continue;
                DataGridViewColumn column = table_top.Columns[pref.Name];
                column.Visible = pref.Visible;
                column.DisplayIndex = displayIndex++;
            }
        }

        private void ConfigureFavoritesMenu(ToolStripMenuItem favoriteMenu)
        {
            favoriteMenu.DropDownItems.Clear();

            ToolStripMenuItem addCurrentItem = new ToolStripMenuItem(Localization.T("Menu.AddFavorite"));
            addCurrentItem.Click += (s, e) => AddSelectedNodeToFavorites();
            favoriteMenu.DropDownItems.Add(addCurrentItem);
            favoriteMenu.DropDownItems.Add(new ToolStripSeparator());

            if (_favoriteNodePaths.Count == 0)
            {
                ToolStripMenuItem emptyItem = new ToolStripMenuItem(Localization.T("Menu.NoFavorites"));
                emptyItem.Enabled = false;
                favoriteMenu.DropDownItems.Add(emptyItem);
            }
            else
            {
                foreach (string path in _favoriteNodePaths)
                {
                    string capturedPath = path;
                    ToolStripMenuItem item = new ToolStripMenuItem(GetFavoriteDisplayText(path));
                    ApplyObjectTooltip(item, path);
                    item.Click += (s, e) => OpenFavoriteNodePath(capturedPath);
                    favoriteMenu.DropDownItems.Add(item);
                }
            }

            favoriteMenu.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem clearItem = new ToolStripMenuItem(Localization.T("Menu.ClearFavorites"));
            clearItem.Enabled = _favoriteNodePaths.Count > 0;
            clearItem.Click += (s, e) => ClearFavorites();
            favoriteMenu.DropDownItems.Add(clearItem);
        }

        private void AddSelectedNodeToFavorites()
        {
            if (db_tree.SelectedNode == null)
            {
                UpdateMainStatus(Localization.T("Status.SelectConnection"));
                return;
            }

            string path = db_tree.SelectedNode.FullPath;
            if (!_favoriteNodePaths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
            {
                _favoriteNodePaths.Add(path);
                SaveFavoriteNodePaths();
                ConfigureMainMenu();
            }

            UpdateMainStatus("Favorite added: " + path);
        }

        private void ClearFavorites()
        {
            _favoriteNodePaths.Clear();
            SaveFavoriteNodePaths();
            ConfigureMainMenu();
            UpdateMainStatus("Favorites cleared.");
        }

        private void OpenFavoriteNodePath(string path)
        {
            TreeNode node = FindFavoriteNode(path);
            if (node == null)
            {
                UpdateMainStatus("Favorite not found: " + path);
                return;
            }

            db_tree.SelectedNode = node;
            node.EnsureVisible();
            db_tree_AfterSelect(db_tree, new TreeViewEventArgs(node));
            UpdateMainStatus("Favorite opened: " + path);
        }

        private TreeNode FindFavoriteNode(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            string[] parts = path.Split('\\');
            if (parts.Length == 0) return null;

            TreeNode current = db_tree.Nodes.Cast<TreeNode>()
                .FirstOrDefault(n => string.Equals(n.Text, parts[0], StringComparison.OrdinalIgnoreCase));
            if (current == null) return null;
            if (parts.Length == 1) return current;

            if (current.Parent == null && current.Nodes.Count == 0)
            {
                db_tree.SelectedNode = current;
                db_tree_DoubleClick(db_tree, EventArgs.Empty);
            }

            for (int i = 1; i < parts.Length; i++)
            {
                if (i == 2)
                {
                    EnsureDatabaseGroupNodes(current);
                }

                current = current.Nodes.Cast<TreeNode>()
                    .FirstOrDefault(n => string.Equals(n.Text, parts[i], StringComparison.OrdinalIgnoreCase));
                if (current == null) return null;
            }

            return current;
        }

        private void LoadFavoriteNodePaths()
        {
            _favoriteNodePaths.Clear();

            try
            {
                string path = GetFavoritesFilePath();
                if (!File.Exists(path)) return;

                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string value = line.Trim();
                    if (value.Length == 0) continue;
                    if (_favoriteNodePaths.Any(p => string.Equals(p, value, StringComparison.OrdinalIgnoreCase))) continue;
                    _favoriteNodePaths.Add(value);
                }
            }
            catch
            {
            }
        }

        private void SaveFavoriteNodePaths()
        {
            try
            {
                string path = GetFavoritesFilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, _favoriteNodePaths.ToArray(), Encoding.UTF8);
            }
            catch
            {
            }
        }

        private static string GetFavoritesFilePath()
        {
            return Path.Combine(Application.UserAppDataPath, "favorites.txt");
        }

        private static string GetFavoriteDisplayText(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            string[] parts = path.Split('\\');
            if (parts.Length <= 1) return parts[0];
            return parts[parts.Length - 1] + " (" + parts[0] + ")";
        }

        // ── 連線群組 helpers ────────────────────────────────────────────

        private static bool IsConnectionGroupNode(TreeNode node)
        {
            return node != null && (node.Tag as string) == ConnectionGroupNodeTag;
        }

        private int GetConnectionIndex(TreeNode node)
        {
            if (node == null) return -1;
            if (node.Tag is int idx) return idx;
            return -1;
        }

        private TreeNode FindConnectionNode(int index)
        {
            if (index >= 0 && index < _connectionTreeNodes.Length && _connectionTreeNodes[index] != null)
                return _connectionTreeNodes[index];
            return null;
        }

        private string GetConnectionGroupName(int index)
        {
            if (index < 0 || index >= myN.connections.Count) return string.Empty;
            return GetConnectionValue(myN.connections[index], "conn_group");
        }

        private List<string> GetAllGroupNames()
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (string g in myN.groups)
                if (!string.IsNullOrWhiteSpace(g)) result.Add(g);
            foreach (var c in myN.connections)
            {
                string g = GetConnectionValue(c, "conn_group");
                if (!string.IsNullOrWhiteSpace(g)) result.Add(g);
            }
            return result.OrderBy(g => g).ToList();
        }

        private void ShowCreateGroupDialog()
        {
            string name = PromptForText(
                Localization.T("Menu.NewGroup"),
                Localization.T("Menu.GroupNamePrompt"),
                "");
            if (string.IsNullOrWhiteSpace(name)) return;

            // 若已有連線屬於此群組，則只需要重繪
            if (GetAllGroupNames().Contains(name))
            {
                UpdateMainStatus(Localization.Format("Menu.GroupNameExists", name));
                MessageBox.Show(Localization.Format("Menu.GroupNameExists", name),
                    Localization.T("Menu.NewGroup"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 儲存群組名稱並重繪
            if (!myN.groups.Contains(name))
                myN.groups.Add(name);
            myN.setSettingINI();
            drawLists();
            UpdateMainStatus(Localization.Format("Menu.GroupCreated", name));
        }

        private void MoveConnectionToGroup(int index, string groupName)
        {
            if (index < 0 || index >= myN.connections.Count) return;
            myN.connections[index]["conn_group"] = groupName ?? "";
            if (!string.IsNullOrWhiteSpace(groupName) && !myN.groups.Contains(groupName))
                myN.groups.Add(groupName);
            myN.setSettingINI();
            drawLists();
            UpdateMainStatus(string.IsNullOrWhiteSpace(groupName)
                ? Localization.T("Menu.ConnectionRemovedFromGroup")
                : Localization.Format("Menu.ConnectionMovedToGroup", groupName));
        }

        private void ShowMoveToGroupDialog(int index)
        {
            if (index < 0 || index >= myN.connections.Count) return;

            List<string> groups = GetAllGroupNames();

            using (Form dlg = new Form())
            {
                dlg.Text = Localization.T("Menu.MoveToGroup");
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.Size = new Size(360, 230);
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;

                Label lbl = new Label { Text = Localization.T("Menu.GroupName"), Location = new Point(16, 18), AutoSize = true };
                ComboBox combo = new ComboBox
                {
                    Location = new Point(16, 40),
                    Width = 310,
                    DropDownStyle = ComboBoxStyle.DropDownList
                };
                foreach (string g in groups) combo.Items.Add(g);

                Label lblNew = new Label { Text = Localization.T("Menu.GroupNamePrompt"), Location = new Point(16, 86), AutoSize = true };
                TextBox txtNew = new TextBox { Location = new Point(16, 108), Width = 310 };

                Button btnOk = new Button
                {
                    Text = Localization.T("Common.OK"),
                    DialogResult = DialogResult.OK,
                    Location = new Point(160, 150),
                    Size = new Size(80, 30)
                };
                Button btnCancel = new Button
                {
                    Text = Localization.T("Common.Cancel"),
                    DialogResult = DialogResult.Cancel,
                    Location = new Point(248, 150),
                    Size = new Size(80, 30)
                };

                dlg.Controls.AddRange(new Control[] { lbl, combo, lblNew, txtNew, btnOk, btnCancel });
                dlg.AcceptButton = btnOk;
                dlg.CancelButton = btnCancel;
                ThemeManager.ApplyTo(dlg);

                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                string target = txtNew.Text.Trim();
                if (string.IsNullOrWhiteSpace(target) && combo.SelectedItem != null)
                    target = combo.SelectedItem.ToString();
                if (string.IsNullOrWhiteSpace(target)) return;

                MoveConnectionToGroup(index, target);
            }
        }

        private void DeleteConnectionGroup(string groupName)
        {
            if (string.IsNullOrWhiteSpace(groupName)) return;

            DialogResult confirm = MessageBox.Show(
                Localization.Format("Menu.ConfirmDeleteGroup", groupName),
                Localization.T("Menu.DeleteGroup"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            foreach (var conn in myN.connections)
            {
                if (GetConnectionValue(conn, "conn_group") == groupName)
                    conn["conn_group"] = "";
            }
            myN.groups.Remove(groupName);
            myN.setSettingINI();
            drawLists();
            UpdateMainStatus(Localization.Format("Menu.GroupDeleted", groupName));
        }

        private void RenameConnectionGroup(string oldName)
        {
            if (string.IsNullOrWhiteSpace(oldName)) return;

            string newName = PromptForText(
                Localization.T("Menu.RenameGroup"),
                Localization.T("Menu.GroupNamePrompt"),
                oldName);
            if (string.IsNullOrWhiteSpace(newName) || newName == oldName) return;

            foreach (var conn in myN.connections)
            {
                if (GetConnectionValue(conn, "conn_group") == oldName)
                    conn["conn_group"] = newName;
            }
            int idx = myN.groups.IndexOf(oldName);
            if (idx >= 0) myN.groups[idx] = newName;
            else if (!myN.groups.Contains(newName)) myN.groups.Add(newName);
            myN.setSettingINI();
            drawLists();
            UpdateMainStatus(Localization.Format("Menu.GroupRenamed", oldName, newName));
        }

        // ── end 連線群組 helpers ────────────────────────────────────────

        private sealed class TreeObjectNodeTag
        {
            public TreeObjectNodeTag(string groupKey)
            {
                GroupKey = groupKey ?? string.Empty;
            }

            public string GroupKey { get; }
        }

        private string[] GetTreePathParts(TreeNode node)
        {
            if (node == null) return new string[0];

            List<string> parts = new List<string>();
            TreeNode current = node;
            while (current != null)
            {
                // 跳過連線群組節點，不納入路徑
                if (IsConnectionGroupNode(current))
                {
                    current = current.Parent;
                    continue;
                }
                TreeObjectNodeTag objectTag = current.Tag as TreeObjectNodeTag;
                if (objectTag != null)
                {
                    parts.Insert(0, current.Text);
                    string parentGroupKey = current.Parent == null ? string.Empty : GetTreeGroupKey(current.Parent);
                    if (!string.Equals(parentGroupKey, objectTag.GroupKey, StringComparison.OrdinalIgnoreCase))
                    {
                        parts.Insert(0, objectTag.GroupKey);
                    }
                    current = current.Parent;
                    continue;
                }
                string groupKey = GetTreeGroupKey(current);
                parts.Insert(0, string.IsNullOrWhiteSpace(groupKey) ? current.Text : groupKey);
                current = current.Parent;
            }

            return parts.ToArray();
        }

        private string GetTreeGroupKey(TreeNode node)
        {
            if (node == null) return string.Empty;
            if (IsTreeGroupKey(node.Name)) return node.Name;
            string tag = node.Tag as string;
            return IsTreeGroupKey(tag) ? tag : string.Empty;
        }

        private string GetTreeObjectGroupKey(TreeNode node)
        {
            TreeObjectNodeTag objectTag = node == null ? null : node.Tag as TreeObjectNodeTag;
            return objectTag == null ? string.Empty : objectTag.GroupKey;
        }

        private bool TreeNodeBelongsToGroup(TreeNode node, string groupKey)
        {
            if (node == null || string.IsNullOrWhiteSpace(groupKey)) return false;
            return string.Equals(GetTreeGroupKey(node), groupKey, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(GetTreeObjectGroupKey(node), groupKey, StringComparison.OrdinalIgnoreCase);
        }

        private TreeNode FindDatabaseObjectNode(TreeNode databaseNode, string groupName, string objectName)
        {
            if (databaseNode == null || string.IsNullOrWhiteSpace(groupName) || string.IsNullOrWhiteSpace(objectName)) return null;

            foreach (TreeNode child in databaseNode.Nodes)
            {
                if (string.Equals(GetTreeGroupKey(child), groupName, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (TreeNode item in child.Nodes)
                    {
                        if (item.Text == objectName) return item;
                    }
                    continue;
                }

                if (string.Equals(GetTreeObjectGroupKey(child), groupName, StringComparison.OrdinalIgnoreCase) &&
                    child.Text == objectName)
                {
                    return child;
                }
            }

            return null;
        }

        private static bool IsTreeGroupKey(string key)
        {
            return DatabaseGroupVisibilityService.IsKnownGroup(key);
        }

        private static string GetTreeGroupText(string groupKey)
        {
            return IsTreeGroupKey(groupKey) ? Localization.T("Tree." + groupKey) : groupKey;
        }

        private static TreeNode CreateTreeGroupNode(string groupKey, int imageIndex)
        {
            return new TreeNode(GetTreeGroupText(groupKey))
            {
                Name = groupKey,
                Tag = groupKey,
                ImageIndex = imageIndex,
                SelectedImageIndex = imageIndex
            };
        }

        private static TreeNode CreateTreeObjectNode(string objectName, string groupKey, int imageIndex)
        {
            return new TreeNode(objectName)
            {
                Tag = new TreeObjectNodeTag(groupKey),
                ImageIndex = imageIndex,
                SelectedImageIndex = imageIndex
            };
        }

        private void AddTreeGroupNodeIfVisible(TreeNode databaseNode, TreeNode groupNode)
        {
            if (databaseNode == null || groupNode == null) return;
            bool activeObjectsOnly = ApplicationOptionSettings.GetBool("ViewActiveObjectsOnly");
            string groupKey = GetTreeGroupKey(groupNode);
            if (!DatabaseGroupVisibilityService.ShouldShowGroup(groupKey, groupNode.Nodes.Count, activeObjectsOnly)) return;

            if (DatabaseGroupVisibilityService.ShouldFlattenGroup(groupKey, ApplicationOptionSettings.GetBool("ViewHideObjectGroups")))
            {
                while (groupNode.Nodes.Count > 0)
                {
                    TreeNode child = groupNode.Nodes[0];
                    groupNode.Nodes.RemoveAt(0);
                    databaseNode.Nodes.Add(child);
                }
                return;
            }

            databaseNode.Nodes.Add(groupNode);
        }

        private void LocalizeTreeGroupNodes()
        {
            if (db_tree == null) return;
            foreach (TreeNode root in db_tree.Nodes)
            {
                LocalizeTreeGroupNodes(root);
            }
        }

        private void LocalizeTreeGroupNodes(TreeNode node)
        {
            if (node == null) return;
            string groupKey = GetTreeGroupKey(node);
            if (!string.IsNullOrWhiteSpace(groupKey))
            {
                node.Text = GetTreeGroupText(groupKey);
            }

            foreach (TreeNode child in node.Nodes)
            {
                LocalizeTreeGroupNodes(child);
            }
        }

        private void OpenOptionsDialog()
        {
            using (OptionsForm form = new OptionsForm())
            {
                if (form.ShowDialog(this) == DialogResult.OK)
                {
                    ApplyPreferences(form.SelectedLanguage, form.SelectedTheme, "Status.OptionsApplied");
                }
            }
        }

        private void ApplyPreferences(string language, string theme, string statusKey)
        {
            Localization.SetLanguage(language, true);
            ThemeManager.SetTheme(theme, true);
            ApplyLanguage();
            foreach (Form form in Application.OpenForms)
            {
                if (form == this) continue;
                QueryForm queryForm = form as QueryForm;
                if (queryForm != null) queryForm.ApplyLanguage();
                TableDesignerForm designerForm = form as TableDesignerForm;
                if (designerForm != null) designerForm.ApplyLanguage();
                Localization.ApplyTo(form);
                if (queryForm != null) queryForm.ApplyTheme();
                if (designerForm != null) designerForm.ApplyTheme();
                ThemeManager.ApplyTo(form);
            }
            UpdateMainStatus(Localization.T(statusKey));
        }

        private void ApplyLanguage()
        {
            Text = Localization.T("App.Title");
            ConfigureMainMenu();
            ConfigureMainToolbar(Path.Combine(Application.StartupPath, "image"));
            label1.Text = Localization.T("Sidebar.Connections");
            if (lblSidebarTitle != null) lblSidebarTitle.Text = Localization.T("Sidebar.ObjectDetails");
            if (pnlObjectFilter != null && pnlObjectFilter.Controls.OfType<Label>().Any())
            {
                pnlObjectFilter.Controls.OfType<Label>().First().Text = Localization.T("View.TopFilterLabel");
            }
            if (lblMainStatus != null && (lblMainStatus.Text == "Ready" || lblMainStatus.Text == "就緒" || string.IsNullOrWhiteSpace(lblMainStatus.Text)))
            {
                lblMainStatus.Text = Localization.T("Status.Ready");
            }
            LocalizeTreeGroupNodes();

            OpenTable.Text = Localization.T("Tool.OpenTable");
            DesignTable.Text = Localization.T("Tool.DesignTable");
            NewTable.Text = Localization.T("Tool.NewTable");
            DeleteTable.Text = Localization.T("Tool.DeleteTable");
            ImportWizard.Text = Localization.T("Tool.ImportWizard");
            ExportWizard.Text = Localization.T("Tool.ExportWizard");
            OpenView.Text = Localization.T("Tool.OpenView");
            DesignView.Text = Localization.T("Tool.DesignView");
            NewView.Text = Localization.T("Tool.NewView");
            DeleteView.Text = Localization.T("Tool.DeleteView");
            View_ExportWizard.Text = Localization.T("Tool.ExportWizard");
            DesignFunction.Text = Localization.T("Tool.DesignFunction");
            NewFunction.Text = Localization.T("Tool.NewFunction");
            DeleteFunction.Text = Localization.T("Tool.DeleteFunction");
            ExecuteFunction.Text = Localization.T("Tool.ExecuteFunction");
            ApplyTheme();
        }

        private void ApplyTheme()
        {
            ThemeManager.ApplyTo(this);
            if (_dockHintOverlay != null)
            {
                _dockHintOverlay.BackColor = ThemeManager.SelectionColor;
                _dockHintOverlay.Invalidate();
            }
            if (table_top != null)
            {
                table_top.BackgroundColor = ThemeManager.WindowBackColor;
                table_top.GridColor = ThemeManager.GridColor;
            }
            if (pnlSidebar != null) pnlSidebar.BackColor = ThemeManager.WindowBackColor;
            if (rtbDDL != null)
            {
                rtbDDL.BackColor = ThemeManager.TextBoxBackColor;
                rtbDDL.ForeColor = ThemeManager.TextColor;
            }
            if (lblSidebarTitle != null) lblSidebarTitle.ForeColor = ThemeManager.TextColor;
            if (lblMainStatus != null) lblMainStatus.ForeColor = ThemeManager.TextColor;
            ThemeManager.ApplyToolStrip(menuStrip1);
            ThemeManager.ApplyToolStrip(tool_Connection);
            ThemeManager.ApplyToolStrip(statusStrip1);
            if (tsSidebar != null) ThemeManager.ApplyToolStrip(tsSidebar);
        }

        private void CreateNewTable()
        {
            TreeDatabaseTarget target = GetTargetFromCurrentSelection();
            if (target == null)
            {
                MessageBox.Show(Localization.T("Designer.SelectDatabase"), Localization.T("Designer.NewTable"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            TableDesignerForm tdf = new TableDesignerForm(target.Database, target.DatabaseName, "");
            DockDockableForm(tdf);
            UpdateMainStatus(Localization.T("Designer.NewTableOpened"));
        }

        private void DeleteSelectedTable()
        {
            DropSelectedTable(true);
        }

        private bool DropSelectedTable(bool confirm)
        {
            if (db_tree.SelectedNode == null) return false;
            var pathParts = GetTreePathParts(db_tree.SelectedNode);
            if (pathParts.Length < 4 || pathParts[2] != "Tables")
            {
                MessageBox.Show(Localization.T("Object.SelectTable"), Localization.T("Tool.DeleteTable"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            string dbName = pathParts[1];
            string tableName = pathParts[3];

            if (confirm && MessageBox.Show(Localization.Format("Object.ConfirmDeleteTable", tableName), Localization.T("Common.Warning"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return false;
            }

            TreeNode selectedNode = db_tree.SelectedNode;
            TreeNode parent = selectedNode.Parent;
            TreeNode root = selectedNode;
            while (root.Parent != null && !IsConnectionGroupNode(root.Parent)) root = root.Parent;
            int rootConnIdx = GetConnectionIndex(root);
            if (rootConnIdx < 0 || rootConnIdx >= myN.connections.Count) return false;
            IDatabase db = (IDatabase)myN.connections[rootConnIdx]["pdo"];

            var res = db.ExecSQL("DROP TABLE " + BuildQualifiedObjectName(db, dbName, tableName));
            if (res.ContainsKey("status") && res["status"] == "OK")
            {
                parent.Nodes.Remove(selectedNode);
                db_tree.SelectedNode = parent;
                ShowDatabaseGroupList(db, dbName, "Tables");
                UpdateMainStatus(Localization.Format("Object.TableDeletedStatus", tableName));
                if (confirm) MessageBox.Show(Localization.T("Object.TableDeleted"), Localization.T("Common.Complete"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }

            MessageBox.Show(Localization.Format("Object.DeleteFailed", res.ContainsKey("reason") ? res["reason"] : Localization.T("Object.UnknownError")), Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        private void queryTabs_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                for (int i = 0; i < queryTabs.TabCount; ++i)
                {
                    if (queryTabs.GetTabRect(i).Contains(e.Location))
                    {
                        queryTabs.SelectedIndex = i;
                        var cms = new ContextMenuStrip();
                        var itemFloat = new ToolStripMenuItem(Localization.T("Query.Float") + " / Undock");
                        itemFloat.Click += (s, ev) => {
                            if (queryTabs.SelectedTab?.Tag is IDockableForm dockable)
                                FloatDockableForm(dockable);
                        };
                        cms.Items.Add(itemFloat);
                        
                        var itemClose = new ToolStripMenuItem(Localization.T("Menu.Close"));
                        itemClose.Click += (s, ev) => {
                            TabPage tp = queryTabs.SelectedTab;
                            if (tp != null)
                            {
                                if (tp.Tag is Form f) f.Close();
                                queryTabs.TabPages.Remove(tp);
                            }
                        };
                        cms.Items.Add(itemClose);

                        ThemeManager.ApplyToolStrip(cms);
                        cms.Show(queryTabs, e.Location);
                        break;
                    }
                }
            }
        }

        private void InitSidebar()
        {
            pnlSidebar = new Panel() { Dock = DockStyle.Fill, BackColor = Color.White };
            
            // 標題區
            Panel pnlTitle = new Panel() { Dock = DockStyle.Top, Height = 40, Padding = new Padding(10, 5, 10, 0) };
            lblSidebarTitle = new Label() { Text = Localization.T("Sidebar.ObjectDetails"), Font = new Font("Microsoft JhengHei", 10, FontStyle.Bold), AutoSize = true, Location = new Point(10, 10) };
            pnlTitle.Controls.Add(lblSidebarTitle);

            // 切換按鈕 (Info / DDL)
            string imgPath = Path.Combine(Application.StartupPath, "image");
            tsSidebar = new ToolStrip() { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.Professional };
            
            btnInfo = new ToolStripButton("Info") { DisplayStyle = ToolStripItemDisplayStyle.ImageAndText };
            if (File.Exists(Path.Combine(imgPath, "database.png"))) btnInfo.Image = Image.FromFile(Path.Combine(imgPath, "database.png"));
            else btnInfo.Image = global::mySQLPunk.Properties.Resources.database;

            btnDDL = new ToolStripButton("DDL") { DisplayStyle = ToolStripItemDisplayStyle.ImageAndText };
            if (File.Exists(Path.Combine(imgPath, "queries.png"))) btnDDL.Image = Image.FromFile(Path.Combine(imgPath, "queries.png"));
            else if (File.Exists(Path.Combine(imgPath, "query.png"))) btnDDL.Image = Image.FromFile(Path.Combine(imgPath, "query.png"));
            
            btnInfo.Click += (s, e) => { dgvDetails.Visible = true; rtbDDL.Visible = false; btnInfo.Checked = true; btnDDL.Checked = false; };
            btnDDL.Click += (s, e) => { dgvDetails.Visible = false; rtbDDL.Visible = true; btnInfo.Checked = false; btnDDL.Checked = true; };
            tsSidebar.Items.AddRange(new ToolStripItem[] { btnInfo, btnDDL });
            btnInfo.Checked = true;

            // 內容區 - 詳情表格
            dgvDetails = new DataGridView()
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.None,
                RowHeadersVisible = false,
                ColumnHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false
            };
            dgvDetails.Columns.Add("Key", "Property");
            dgvDetails.Columns.Add("Value", "Value");
            dgvDetails.Columns[0].FillWeight = 40;
            dgvDetails.SelectionMode = DataGridViewSelectionMode.CellSelect;
            dgvDetails.GridColor = Color.FromArgb(245, 245, 245);
            dgvDetails.DefaultCellStyle.Font = new Font("Microsoft JhengHei", 9);

            // 內容區 - DDL
            rtbDDL = new RichTextBox()
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                ReadOnly = true,
                BackColor = Color.White,
                Font = new Font("Consolas", 10),
                Visible = false
            };

            pnlSidebar.Controls.Add(dgvDetails);
            pnlSidebar.Controls.Add(rtbDDL);
            pnlSidebar.Controls.Add(tsSidebar);
            pnlSidebar.Controls.Add(pnlTitle);

            splitContainer5.Panel2.Controls.Add(pnlSidebar);
            pnlSidebar.BringToFront();
        }

        private void InitObjectFilterPanel()
        {
            if (pnlObjectFilter != null) return;

            pnlObjectFilter = new Panel
            {
                Dock = DockStyle.Top,
                Height = 34,
                Padding = new Padding(8, 5, 8, 4),
                Visible = false
            };

            Label label = new Label
            {
                Text = Localization.T("View.TopFilterLabel"),
                AutoSize = true,
                Dock = DockStyle.Left,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(0, 6, 8, 0)
            };

            txtObjectFilter = new TextBox
            {
                Dock = DockStyle.Fill
            };
            txtObjectFilter.TextChanged += (s, e) => ApplyObjectListFilter();

            pnlObjectFilter.Controls.Add(txtObjectFilter);
            pnlObjectFilter.Controls.Add(label);
            splitContainer5.Panel1.Controls.Add(pnlObjectFilter);
            pnlObjectFilter.BringToFront();
        }

        private void ConfigureMainToolbar(string imgPath)
        {
            ConfigureToolbarItem(connection_btn, Localization.T("Toolbar.Connection"));
            ConfigureToolbarItem(query_btn, Localization.T("Toolbar.NewQuery"));
            ConfigureToolbarItem(table_btn, Localization.T("Toolbar.Table"));
            ConfigureToolbarItem(view_btn, Localization.T("Toolbar.View"));
            ConfigureToolbarItem(function_btn, Localization.T("Toolbar.Function"));
            ConfigureToolbarItem(user_btn, Localization.T("Toolbar.User"));
            ConfigureToolbarItem(other_btn, Localization.T("Toolbar.Other"));
            ConfigureToolbarItem(query_section_btn, Localization.T("Toolbar.Query"));
            ConfigureToolbarItem(backup_btn, Localization.T("Toolbar.Backup"));
            ConfigureToolbarItem(auto_run_btn, Localization.T("Toolbar.AutoRun"));
            ConfigureToolbarItem(model_btn, Localization.T("Toolbar.Model"));
            ConfigureToolbarItem(bi_btn, Localization.T("Toolbar.BI"));

            query_btn.Click -= Query_btn_Click;
            query_btn.Click += Query_btn_Click;
            function_btn.Click -= function_btn_Click;
            function_btn.Click += function_btn_Click;
            other_btn.Click -= other_btn_Click;
            other_btn.Click += other_btn_Click;
            query_section_btn.Click -= query_section_btn_Click;
            query_section_btn.Click += query_section_btn_Click;
            backup_btn.Click -= backup_btn_Click;
            backup_btn.Click += backup_btn_Click;
            auto_run_btn.Click -= auto_run_btn_Click;
            auto_run_btn.Click += auto_run_btn_Click;
            model_btn.Click -= model_btn_Click;
            model_btn.Click += model_btn_Click;
            bi_btn.Click -= bi_btn_Click;
            bi_btn.Click += bi_btn_Click;
            connection_btn.Click -= connection_btn_Click;
            connection_btn.Click += connection_btn_Click;
            ConfigureConnectionToolbarButton();

            LoadIcon(connection_btn, Path.Combine(imgPath, "connection.png"), global::mySQLPunk.Properties.Resources.database);
            LoadIcon(query_btn, Path.Combine(imgPath, "new_query.png"), global::mySQLPunk.Properties.Resources.database);
            LoadIcon(table_btn, Path.Combine(imgPath, "table.png"), global::mySQLPunk.Properties.Resources.tables_32);
            LoadIcon(view_btn, Path.Combine(imgPath, "view.png"), global::mySQLPunk.Properties.Resources.views_32);
            LoadIcon(function_btn, Path.Combine(imgPath, "functions.png"), global::mySQLPunk.Properties.Resources.database);
            LoadIcon(user_btn, Path.Combine(imgPath, "user.png"), global::mySQLPunk.Properties.Resources.user);
            LoadIcon(other_btn, Path.Combine(imgPath, "other.png"), global::mySQLPunk.Properties.Resources.database);
            LoadIcon(query_section_btn, Path.Combine(imgPath, "query_section.png"), global::mySQLPunk.Properties.Resources.database);
            LoadIcon(backup_btn, Path.Combine(imgPath, "backups.png"), global::mySQLPunk.Properties.Resources.database);
            LoadIcon(auto_run_btn, Path.Combine(imgPath, "auto_run.png"), global::mySQLPunk.Properties.Resources.database);
            LoadIcon(model_btn, Path.Combine(imgPath, "model.png"), global::mySQLPunk.Properties.Resources.database);
            LoadIcon(bi_btn, Path.Combine(imgPath, "bi.png"), global::mySQLPunk.Properties.Resources.database);

            tool_Connection.Items.Clear();
            tool_Connection.Items.AddRange(new ToolStripItem[]
            {
                connection_btn,
                query_btn,
                table_btn,
                view_btn,
                function_btn,
                user_btn,
                other_btn,
                query_section_btn,
                backup_btn,
                auto_run_btn,
                model_btn,
                bi_btn
            });

            tool_Connection.ImageScalingSize = new Size(MainToolbarIconSize, MainToolbarIconSize);
            tool_Connection.Height = MainToolbarHeight;
            tool_Connection.Padding = new Padding(10, 8, 10, 4);

            foreach (ToolStripItem item in tool_Connection.Items)
            {
                item.TextImageRelation = TextImageRelation.ImageAboveText;
                item.TextAlign = ContentAlignment.BottomCenter;
                item.AutoSize = false;
                item.Size = new Size(MainToolbarItemWidth, MainToolbarItemHeight);
                item.Margin = new Padding(2, 2, 2, 0);
                item.Padding = new Padding(0, 3, 0, 2);
            }
        }

        private void ConfigureConnectionToolbarButton()
        {
            ApplyObjectTooltip(connection_btn, Localization.T("Menu.NewConnection"));
        }

        private static void ConfigureToolbarItem(ToolStripItem item, string text)
        {
            item.Text = text;
            ApplyObjectTooltip(item, text);
            item.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            item.ImageScaling = ToolStripItemImageScaling.None;
        }

        private static void ApplyObjectTooltip(ToolStripItem item, string text)
        {
            if (item == null) return;
            item.AutoToolTip = false;
            item.ToolTipText = ShouldShowObjectTooltips() ? (text ?? string.Empty) : string.Empty;
        }

        private static bool ShouldShowObjectTooltips()
        {
            return ApplicationOptionSettings.GetBool("ShowObjectTooltips");
        }

        private void LoadIcon(ToolStripItem btn, string path, Image defaultImg)
        {
            if (File.Exists(path))
            {
                try 
                { 
                    using (Image image = Image.FromFile(path))
                    {
                        btn.Image = CreateToolbarIcon(image);
                    }
                    btn.ImageScaling = ToolStripItemImageScaling.None;
                }
                catch { btn.Image = CreateToolbarIcon(defaultImg); }
            }
            else
            {
                btn.Image = CreateToolbarIcon(defaultImg);
            }
        }

        private static Image CreateToolbarIcon(Image source)
        {
            Bitmap bitmap = new Bitmap(MainToolbarIconSize, MainToolbarIconSize);
            try
            {
                bitmap.SetResolution(source.HorizontalResolution, source.VerticalResolution);
            }
            catch
            {
            }

            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

                float scale = Math.Min((float)MainToolbarIconContentSize / source.Width, (float)MainToolbarIconContentSize / source.Height);
                int width = Math.Max(1, (int)Math.Round(source.Width * scale));
                int height = Math.Max(1, (int)Math.Round(source.Height * scale));
                int x = (MainToolbarIconSize - width) / 2;
                int y = (MainToolbarIconSize - height) / 2;
                graphics.DrawImage(source, new Rectangle(x, y, width, height));
            }

            return bitmap;
        }

        private void DesignTable_Click(object sender, EventArgs e)
        {
            OpenSelectedTableDesigner(null);
        }

        private void OpenSelectedTableDesigner(AutoCommentMode? autoCommentMode)
        {
            if (db_tree.SelectedNode == null) return;

            TreeNode node = db_tree.SelectedNode;
            var pathParts = GetTreePathParts(node);

            // 只有點到 Table 本身時才允許設計 (假設路徑是 Conn\DB\Tables\TableName)
            if (pathParts.Length >= 4 && pathParts[2] == "Tables")
            {
                int connIndex = -1;
                TreeNode root = node;
                while (root.Parent != null && !IsConnectionGroupNode(root.Parent)) root = root.Parent;
                connIndex = GetConnectionIndex(root);
                if (connIndex < 0 || connIndex >= myN.connections.Count) return;

                string dbName = pathParts[1];
                string tableName = pathParts[3];

                var connInfo = myN.connections[connIndex];
                IDatabase db = (IDatabase)connInfo["pdo"];

                TableDesignerForm tdf = new TableDesignerForm(db, dbName, tableName);
                DockDockableForm(tdf);
                if (autoCommentMode.HasValue)
                {
                    tdf.FillAutoColumnComments(autoCommentMode.Value);
                }
            }
            else
            {
                MessageBox.Show(Localization.T("Object.SelectTable"), Localization.T("Tool.DesignTable"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void Query_btn_Click(object sender, EventArgs e)
        {
            if (db_tree.SelectedNode == null)
            {
                MessageBox.Show(Localization.T("Object.SelectDatabaseOrConnection"), Localization.T("Toolbar.NewQuery"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 解析目前選中的連線與資料庫
            TreeNode node = db_tree.SelectedNode;
            var pathParts = GetTreePathParts(node);

            int connIndex = -1;
            string dbName = "";

            if (pathParts.Length >= 1)
            {
                // 找根節點 (Connection)，支援群組節點
                TreeNode root = node;
                while (root.Parent != null && !IsConnectionGroupNode(root.Parent)) root = root.Parent;
                connIndex = GetConnectionIndex(root);
            }

            if (pathParts.Length >= 2)
            {
                // 找資料庫節點
                dbName = pathParts[1];
            }

            if (connIndex != -1)
            {
                var connInfo = myN.connections[connIndex];
                if (connInfo["isConnect"].ToString() == "T")
                {
                    IDatabase db = (IDatabase)connInfo["pdo"];
                    string host = connInfo.ContainsKey("host") && connInfo["host"] != null
                        ? connInfo["host"].ToString()
                        : string.Empty;
                    OpenQuery(db, dbName, host, string.Empty, true);
                }
                else
                {
                    MessageBox.Show(Localization.T("Object.OpenConnectionFirst"), Localization.T("Toolbar.NewQuery"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        private void OpenSelectedTableInQuery()
        {
            OpenSelectedTableQuery(false);
        }

        private void OpenSelectedTableAllColumnsInQuery()
        {
            OpenSelectedTableQuery(true);
        }

        private void OpenSelectedTableQuery(bool explicitColumns)
        {
            if (db_tree.SelectedNode == null)
            {
                return;
            }

            TreeNode node = db_tree.SelectedNode;
            var pathParts = GetTreePathParts(node);

            if (pathParts.Length < 4 || pathParts[2] != "Tables")
            {
                MessageBox.Show(Localization.T("Object.SelectTable"), Localization.T("Tool.OpenTable"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            TreeNode root = node;
            while (root.Parent != null && !IsConnectionGroupNode(root.Parent)) root = root.Parent;
            int rootIdx = GetConnectionIndex(root);
            if (rootIdx < 0 || rootIdx >= myN.connections.Count) return;

            var connInfo = myN.connections[rootIdx];
            if (connInfo["isConnect"].ToString() != "T")
            {
                MessageBox.Show(Localization.T("Object.OpenConnectionFirst"), Localization.T("Tool.OpenTable"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string dbName = pathParts[1];
            string tableName = pathParts[3];
            string host = connInfo.ContainsKey("host") && connInfo["host"] != null
                ? connInfo["host"].ToString()
                : string.Empty;
            IDatabase db = (IDatabase)connInfo["pdo"];
            string initialSql = BuildTableSelectSql(db, dbName, tableName, explicitColumns);

            OpenQuery(db, dbName, host, initialSql, true);
            UpdateMainStatus(Localization.Format(explicitColumns ? "Object.SelectColumnsOpenedStatus" : "Object.SelectStarOpenedStatus", tableName));
        }

        private void OpenSelectedViewInQuery()
        {
            DatabaseObjectSelection selection = GetSelectedDatabaseObject();
            if (selection == null || selection.GroupName != "Views")
            {
                MessageBox.Show(Localization.T("Object.SelectView"), Localization.T("Tool.OpenView"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string initialSql = "SELECT * FROM " + BuildQualifiedObjectName(selection.Database, selection.DatabaseName, selection.ObjectName) + ";";
            OpenQuery(selection.Database, selection.DatabaseName, selection.Host, initialSql, true);
        }

        private static string BuildTableSelectSql(IDatabase db, string databaseName, string tableName, bool explicitColumns)
        {
            if (!explicitColumns)
            {
                return "SELECT * FROM " + BuildQualifiedObjectName(db, databaseName, tableName) + ";";
            }

            string selectList = BuildTableSelectColumnList(db, databaseName, tableName);
            return "SELECT " + selectList + Environment.NewLine +
                   "FROM " + BuildQualifiedObjectName(db, databaseName, tableName) + ";";
        }

        private static string BuildTableSelectColumnList(IDatabase db, string databaseName, string tableName)
        {
            try
            {
                DataTable columns = db.GetColumns(databaseName, tableName);
                List<string> names = new List<string>();
                foreach (DataRow row in columns.Rows)
                {
                    string columnName = FirstColumnValue(row, "Field", "name", "Name", "COLUMN_NAME", "column_name");
                    if (!string.IsNullOrWhiteSpace(columnName))
                    {
                        names.Add(QuoteDumpIdentifier(db, columnName));
                    }
                }

                if (names.Count > 0)
                {
                    return string.Join(", ", names.ToArray());
                }
            }
            catch
            {
            }

            return "*";
        }

        private DatabaseObjectSelection GetSelectedDatabaseObject()
        {
            if (db_tree.SelectedNode == null) return null;

            TreeNode node = db_tree.SelectedNode;
            var pathParts = GetTreePathParts(node);
            if (pathParts.Length < 4) return null;
            if (pathParts[2] != "Tables" && pathParts[2] != "Views" && pathParts[2] != "Functions") return null;

            TreeNode root = node;
            while (root.Parent != null && !IsConnectionGroupNode(root.Parent)) root = root.Parent;
            int connIdxSel = GetConnectionIndex(root);
            if (connIdxSel < 0 || connIdxSel >= myN.connections.Count) return null;
            var connInfo = myN.connections[connIdxSel];
            if (connInfo["isConnect"].ToString() != "T" || !(connInfo["pdo"] is IDatabase db)) return null;

            return new DatabaseObjectSelection
            {
                Database = db,
                DatabaseName = pathParts[1],
                ObjectName = pathParts[3],
                GroupName = pathParts[2],
                Host = connInfo.ContainsKey("host") && connInfo["host"] != null ? connInfo["host"].ToString() : string.Empty,
                Node = node
            };
        }

        private void ShowSelectedViewDefinition()
        {
            DatabaseObjectSelection selection = GetSelectedDatabaseObject();
            if (selection == null || selection.GroupName != "Views")
            {
                MessageBox.Show(Localization.T("Object.SelectView"), Localization.T("Tool.DesignView"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            lblSidebarTitle.Text = "View: " + selection.ObjectName;
            ShowDatabaseGroupList(selection.Database, selection.DatabaseName, "Views");
            ShowViewDetails(selection.Database, selection.DatabaseName, selection.ObjectName);
            btnDDL.PerformClick();
        }

        private void CreateNewView()
        {
            TreeDatabaseTarget target = GetTargetFromCurrentSelection();
            if (target == null)
            {
                MessageBox.Show(Localization.T("Object.SelectViewTarget"), Localization.T("Tool.NewView"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string initialSql = BuildNewViewTemplate(target.Database, target.DatabaseName, GetSelectedSchemaSeedObjectName(target.Database));
            OpenQuery(target.Database, target.DatabaseName, string.Empty, initialSql, true);
            UpdateMainStatus(Localization.T("Object.NewViewTemplateOpened"));
        }

        private void CreateNewFunction()
        {
            TreeDatabaseTarget target = GetTargetFromCurrentSelection();
            if (target == null)
            {
                MessageBox.Show(Localization.T("Object.SelectFunctionTarget"), Localization.T("Tool.NewFunction"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            OpenQuery(target.Database, target.DatabaseName, GetTargetHost(target), BuildFunctionTemplate(target.Database, target.DatabaseName, GetSelectedSchemaSeedObjectName(target.Database)), true);
            UpdateMainStatus(Localization.T("Object.NewFunctionTemplateOpened"));
        }

        private void ShowSelectedFunctionDefinition()
        {
            DatabaseObjectSelection selection = GetSelectedDatabaseObject();
            if (selection == null || selection.GroupName != "Functions")
            {
                MessageBox.Show(Localization.T("Object.SelectFunction"), Localization.T("Tool.DesignFunction"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            lblSidebarTitle.Text = "Function: " + selection.ObjectName;
            ShowDatabaseGroupList(selection.Database, selection.DatabaseName, "Functions");
            ShowFunctionDetails(selection.Database, selection.DatabaseName, selection.ObjectName);
            btnDDL.PerformClick();
        }

        private void ExecuteSelectedFunction()
        {
            DatabaseObjectSelection selection = GetSelectedDatabaseObject();
            if (selection == null || selection.GroupName != "Functions")
            {
                MessageBox.Show(Localization.T("Object.SelectFunction"), Localization.T("Tool.ExecuteFunction"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string initialSql = BuildFunctionExecuteSql(selection.Database, selection.DatabaseName, selection.ObjectName, GetSelectedFunctionType(selection));
            OpenQuery(selection.Database, selection.DatabaseName, selection.Host, initialSql, true);
            UpdateMainStatus(Localization.Format("Object.FunctionExecutionOpened", selection.ObjectName));
        }

        private void DeleteSelectedFunction()
        {
            DropSelectedFunction(true);
        }

        private bool DropSelectedFunction(bool confirm)
        {
            DatabaseObjectSelection selection = GetSelectedDatabaseObject();
            if (selection == null || selection.GroupName != "Functions")
            {
                MessageBox.Show(Localization.T("Object.SelectFunction"), Localization.T("Tool.DeleteFunction"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            if (selection.Database is my_sqlite)
            {
                MessageBox.Show(Localization.T("Object.SqliteNoStoredFunction"), Localization.T("Tool.DeleteFunction"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            if (confirm && MessageBox.Show(Localization.Format("Object.ConfirmDeleteFunction", selection.ObjectName), Localization.T("Common.Warning"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return false;
            }

            string routineType = GetSelectedFunctionType(selection);
            string dropKind = IsProcedureRoutine(routineType) ? "PROCEDURE" : "FUNCTION";
            Dictionary<string, string> res = selection.Database.ExecSQL("DROP " + dropKind + " " + BuildQualifiedObjectName(selection.Database, selection.DatabaseName, selection.ObjectName));
            if (res.ContainsKey("status") && res["status"] == "OK")
            {
                TreeNode parent = db_tree.SelectedNode.Parent;
                parent.Nodes.Remove(db_tree.SelectedNode);
                db_tree.SelectedNode = parent;
                ShowDatabaseGroupList(selection.Database, selection.DatabaseName, "Functions");
                UpdateMainStatus(Localization.Format("Object.FunctionDeletedStatus", selection.ObjectName));
                if (confirm) MessageBox.Show(Localization.T("Object.FunctionDeleted"), Localization.T("Common.Complete"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }

            MessageBox.Show(Localization.Format("Object.DeleteFailed", res.ContainsKey("reason") ? res["reason"] : Localization.T("Object.UnknownError")), Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        private void DeleteSelectedView()
        {
            DropSelectedView(true);
        }

        private bool DropSelectedView(bool confirm)
        {
            DatabaseObjectSelection selection = GetSelectedDatabaseObject();
            if (selection == null || selection.GroupName != "Views")
            {
                MessageBox.Show(Localization.T("Object.SelectView"), Localization.T("Tool.DeleteView"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            if (confirm && MessageBox.Show(Localization.Format("Object.ConfirmDeleteView", selection.ObjectName), Localization.T("Common.Warning"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return false;
            }

            var res = selection.Database.ExecSQL("DROP VIEW " + BuildQualifiedObjectName(selection.Database, selection.DatabaseName, selection.ObjectName));
            if (res.ContainsKey("status") && res["status"] == "OK")
            {
                TreeNode parent = db_tree.SelectedNode.Parent;
                parent.Nodes.Remove(db_tree.SelectedNode);
                db_tree.SelectedNode = parent;
                ShowDatabaseGroupList(selection.Database, selection.DatabaseName, "Views");
                UpdateMainStatus(Localization.Format("Object.ViewDeletedStatus", selection.ObjectName));
                if (confirm) MessageBox.Show(Localization.T("Object.ViewDeleted"), Localization.T("Common.Complete"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }
            else
            {
                MessageBox.Show(Localization.Format("Object.DeleteFailed", res.ContainsKey("reason") ? res["reason"] : Localization.T("Object.UnknownError")), Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        private void OpenQuery(IDatabase db, string dbName, string host, string initialSql, bool docked)
        {
            QueryForm queryForm = new QueryForm(db, dbName, host, initialSql);
            docked = DockableTabOptionService.ResolveDockPreference(
                docked,
                ApplicationOptionSettings.GetString("IndexOpenTarget"),
                queryTabs != null && queryTabs.TabPages.Count > 0);
            if (docked)
            {
                DockDockableForm(queryForm);
            }
            else
            {
                queryForm.SetMainHost(this);
                queryForm.Show();
            }
        }

        public void DockDockableForm(IDockableForm dockable)
        {
            if (dockable == null) return;
            Form f = (Form)dockable;

            // 檢查是否已經在 Tab 中
            foreach (TabPage tp in queryTabs.TabPages)
            {
                if (tp.Tag == dockable)
                {
                    queryTabs.SelectedTab = tp;
                    return;
                }

                IDockableForm existingDockable = tp.Tag as IDockableForm;
                if (existingDockable != null &&
                    DockableTabOptionService.ShouldReuseTab(
                        ApplicationOptionSettings.GetBool("AllowDuplicateObjects"),
                        existingDockable.GetDisplayTitle(),
                        dockable.GetDisplayTitle(),
                        existingDockable.GetType(),
                        dockable.GetType()))
                {
                    queryTabs.SelectedTab = tp;
                    f.Dispose();
                    return;
                }
            }

            table_top.Visible = false;
            queryTabs.Visible = true;
            queryTabs.BringToFront();
            UpdateObjectFilterVisibility();

            dockable.SetMainHost(this);
            dockable.PrepareForDocking();

            TabPage page = new TabPage(dockable.GetDisplayTitle());
            page.Tag = dockable;
            f.Dock = DockStyle.Fill;
            page.Controls.Add(f);
            queryTabs.TabPages.Add(page);
            queryTabs.SelectedTab = page;
            f.Show();
            RefreshQueriesGroupIfSelected();
        }

        public void FloatDockableForm(IDockableForm dockable)
        {
            if (dockable == null) return;
            Form f = (Form)dockable;

            foreach (TabPage page in queryTabs.TabPages)
            {
                if (page.Tag == dockable)
                {
                    queryTabs.TabPages.Remove(page);
                    break;
                }
            }

            dockable.PrepareForFloating();
            
            // 將視窗中心設定在滑鼠位置
            f.Location = new Point(Cursor.Position.X - f.Width / 2, Cursor.Position.Y - 15);
            f.Show();

            // 強制接管拖拽：讓視窗立刻跟著滑鼠走
            ReleaseCapture();
            SendMessage(f.Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);

            if (queryTabs.TabPages.Count == 0)
            {
                queryTabs.Visible = false;
                table_top.Visible = true;
                UpdateObjectFilterVisibility();
            }
        }

        // 給懸浮視窗的 WndProc 查詢 drop 目標的螢幕範圍
        public Control GetTabDropArea() => queryTabs;

        public Rectangle GetTabDropScreenBounds()
        {
            if (queryTabs == null || splitContainer5 == null) return Rectangle.Empty;

            int headerHeight = 34;
            if (queryTabs.TabPages.Count > 0)
            {
                Rectangle firstTab = queryTabs.GetTabRect(0);
                if (!firstTab.IsEmpty)
                    headerHeight = Math.Max(headerHeight, firstTab.Bottom + 6);
            }

            Rectangle hostBounds = queryTabs.Visible
                ? queryTabs.RectangleToScreen(queryTabs.ClientRectangle)
                : splitContainer5.Panel1.RectangleToScreen(splitContainer5.Panel1.ClientRectangle);

            // 只允許頁籤列區域觸發 dock，避免整個內容畫面都變成吸附區。
            return new Rectangle(hostBounds.Left, hostBounds.Top, hostBounds.Width, headerHeight);
        }

        public bool IsPointInTabDropArea(Point screenPoint)
        {
            Rectangle bounds = GetTabDropScreenBounds();
            return !bounds.IsEmpty && bounds.Contains(screenPoint);
        }

        public void ShowDockHint()
        {
            if (_dockHintOverlay == null) return;
            Rectangle bounds = GetTabDropScreenBounds();
            if (!bounds.IsEmpty)
                _dockHintOverlay.Height = bounds.Height;
            _dockHintOverlay.Visible = true;
            _dockHintOverlay.BringToFront();
            _dockHintOverlay.Invalidate();
        }

        public void HideDockHint()
        {
            if (_dockHintOverlay != null)
                _dockHintOverlay.Visible = false;
        }

        public void NotifyDockableFormClosed(IDockableForm dockable)
        {
            if (dockable == null || queryTabs == null) return;

            foreach (TabPage page in queryTabs.TabPages)
            {
                if (page.Tag == dockable)
                {
                    queryTabs.TabPages.Remove(page);
                    page.Dispose();
                    break;
                }
            }

            if (queryTabs.TabPages.Count == 0)
            {
                queryTabs.Visible = false;
                table_top.Visible = true;
                table_top.BringToFront();
                UpdateObjectFilterVisibility();
            }
            RefreshQueriesGroupIfSelected();
        }

        private TabPage FindQueryTab(QueryForm queryForm)
        {
            if (queryTabs == null)
            {
                return null;
            }

            foreach (TabPage page in queryTabs.TabPages)
            {
                if (ReferenceEquals(page.Tag, queryForm))
                {
                    return page;
                }
            }

            return null;
        }

        private string BuildQueryTabText(QueryForm queryForm)
        {
            string text = queryForm.GetDisplayTitle();
            if (string.IsNullOrWhiteSpace(text))
            {
                text = "Query " + queryTabCounter;
                queryTabCounter++;
            }

            return text;
        }

        private void DesignSelectedTable()
        {
            DesignTable_Click(this, EventArgs.Empty);
        }

        private void FillSelectedTableComments()
        {
            FillSelectedTableComments(AutoCommentMode.FillBlanks);
        }

        private void FillSelectedTableComments(AutoCommentMode mode)
        {
            OpenSelectedTableDesigner(mode);
        }

        private ToolStripMenuItem CreateAutoCommentModeMenuItem(Action<AutoCommentMode> onModeSelected)
        {
            ToolStripMenuItem menuItem = new ToolStripMenuItem(Localization.T("Tool.FillAutoComments"));
            ToolStripMenuItem fillBlankItem = new ToolStripMenuItem(Localization.T("Tool.FillBlankAutoComments"));
            fillBlankItem.Click += (s, ev) => onModeSelected(AutoCommentMode.FillBlanks);
            ToolStripMenuItem overwriteItem = new ToolStripMenuItem(Localization.T("Tool.OverwriteAutoComments"));
            overwriteItem.Click += (s, ev) => onModeSelected(AutoCommentMode.Overwrite);
            menuItem.DropDownItems.Add(fillBlankItem);
            menuItem.DropDownItems.Add(overwriteItem);
            return menuItem;
        }

        private void DumpSelectedTableSql(bool dataOnly)
        {
            if (db_tree.SelectedNode == null)
            {
                return;
            }

            TreeNode node = db_tree.SelectedNode;
            var pathParts = GetTreePathParts(node);

            if (pathParts.Length < 4 || pathParts[2] != "Tables")
            {
                MessageBox.Show(Localization.T("Object.SelectTable"), Localization.T("Tool.DumpSql"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            TreeNode root = node;
            while (root.Parent != null && !IsConnectionGroupNode(root.Parent)) root = root.Parent;
            int connIdxDump = GetConnectionIndex(root);
            if (connIdxDump < 0 || connIdxDump >= myN.connections.Count) return;

            var connInfo = myN.connections[connIdxDump];
            if (connInfo["isConnect"].ToString() != "T")
            {
                MessageBox.Show(Localization.T("Object.OpenConnectionFirst"), Localization.T("Tool.DumpSql"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            IDatabase db = (IDatabase)connInfo["pdo"];
            string dbName = pathParts[1];
            string tableName = pathParts[3];

            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Filter = Localization.T("Common.SqlFilesFilter");
                dialog.DefaultExt = "sql";
                dialog.FileName = dbName + "_" + tableName + (dataOnly ? "_data" : "_structure_data") + ".sql";

                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                try
                {
                    string sql = DatabaseDumpService.BuildTableDump(db, dbName, tableName, dataOnly);
                    File.WriteAllText(dialog.FileName, sql, Encoding.UTF8);
                    MessageBox.Show(Localization.T("Object.SqlExported"), Localization.T("Common.Complete"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Localization.Format("Object.SqlExportFailed", ex.Message), Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void ExportSqliteColumnCommentsWithDialog(string tableName)
        {
            TreeDatabaseTarget target = GetTargetFromCurrentSelection();
            if (!IsSqliteTarget(target))
            {
                MessageBox.Show(Localization.T("Object.SqliteColumnCommentExchangeRequiresSqlite"), Localization.T("Tool.ExportSqliteColumnComments"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = Localization.T("Tool.ExportSqliteColumnComments");
                dialog.Filter = Localization.T("Object.SqliteColumnCommentJsonFilter");
                dialog.DefaultExt = "json";
                dialog.FileName = BuildSqliteColumnCommentExchangeFileName(target.DatabaseName, tableName);
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    SqliteColumnCommentExportResult result =
                        SqliteColumnCommentExchangeService.WriteExportFile(target.Database, target.DatabaseName, tableName, dialog.FileName);
                    UpdateMainStatus(Localization.Format("Object.SqliteColumnCommentsExportedStatus", result.CommentCount, dialog.FileName));
                    MessageBox.Show(
                        Localization.Format("Object.SqliteColumnCommentsExported", result.TableCount, result.CommentCount),
                        Localization.T("Tool.ExportSqliteColumnComments"),
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        Localization.Format("Object.SqliteColumnCommentsExportFailed", ex.Message),
                        Localization.T("Tool.ExportSqliteColumnComments"),
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
        }

        private void ImportSqliteColumnCommentsWithDialog(string tableName)
        {
            TreeDatabaseTarget target = GetTargetFromCurrentSelection();
            if (!IsSqliteTarget(target))
            {
                MessageBox.Show(Localization.T("Object.SqliteColumnCommentExchangeRequiresSqlite"), Localization.T("Tool.ImportSqliteColumnComments"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = Localization.T("Tool.ImportSqliteColumnComments");
                dialog.Filter = Localization.T("Object.SqliteColumnCommentJsonFilter");
                dialog.CheckFileExists = true;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    SqliteColumnCommentImportPlan plan =
                        SqliteColumnCommentExchangeService.BuildImportPlanFromFile(dialog.FileName, tableName);
                    SqliteColumnCommentImportReviewReport review =
                        SqliteColumnCommentExchangeService.BuildImportReviewReport(target.Database, target.DatabaseName, plan);
                    review.SourcePath = dialog.FileName;
                    if (MessageBox.Show(
                        Localization.Format(
                            "Object.SqliteColumnCommentsImportConfirmWithReview",
                            plan.TableCount,
                            plan.CommentCount,
                            review.Added,
                            review.Updated,
                            review.Removed,
                            review.Unchanged),
                        Localization.T("Tool.ImportSqliteColumnComments"),
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question) != DialogResult.Yes)
                    {
                        return;
                    }

                    foreach (string statement in plan.Statements)
                    {
                        target.Database.ExecSQL(statement);
                    }

                    string reviewReportPath = TryWriteSqliteColumnCommentImportReviewReport(review);
                    RefreshDatabaseObjectNodes(target.DatabaseNode);
                    string statusMessage = Localization.Format("Object.SqliteColumnCommentsImportedStatus", plan.CommentCount);
                    if (!string.IsNullOrWhiteSpace(reviewReportPath))
                    {
                        statusMessage += " " + Localization.Format("Object.SqliteColumnCommentsImportReviewReport", reviewReportPath);
                    }
                    UpdateMainStatus(statusMessage);
                    MessageBox.Show(
                        string.IsNullOrWhiteSpace(reviewReportPath)
                            ? Localization.Format("Object.SqliteColumnCommentsImported", plan.TableCount, plan.CommentCount)
                            : Localization.Format("Object.SqliteColumnCommentsImportedWithReview", plan.TableCount, plan.CommentCount, reviewReportPath),
                        Localization.T("Tool.ImportSqliteColumnComments"),
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        Localization.Format("Object.SqliteColumnCommentsImportFailed", ex.Message),
                        Localization.T("Tool.ImportSqliteColumnComments"),
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
        }

        private static string TryWriteSqliteColumnCommentImportReviewReport(SqliteColumnCommentImportReviewReport review)
        {
            if (review == null) return string.Empty;
            try
            {
                return SqliteColumnCommentExchangeService.WriteImportReviewReport(
                    review,
                    GetSqliteColumnCommentImportReviewReportDirectory());
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool IsSqliteTarget(TreeDatabaseTarget target)
        {
            return target != null &&
                   target.Database != null &&
                   string.Equals(target.Database.ProviderName, "sqlite", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildSqliteColumnCommentExchangeFileName(string databaseName, string tableName)
        {
            string baseName = string.IsNullOrWhiteSpace(tableName)
                ? databaseName
                : databaseName + "_" + tableName;
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "sqlite";
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                baseName = baseName.Replace(invalid, '_');
            }
            return baseName + "_column_comments.json";
        }

        private static string GetSqliteColumnCommentImportReviewReportDirectory()
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documents))
            {
                documents = Application.UserAppDataPath;
            }
            return Path.Combine(documents, "mySQLPunk", "sqlite-column-comment-import-reviews");
        }

        private void DumpSelectedViewSql()
        {
            DatabaseObjectSelection selection = GetSelectedDatabaseObject();
            if (selection == null || selection.GroupName != "Views")
            {
                MessageBox.Show(Localization.T("Object.SelectView"), Localization.T("Tool.DumpSql"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Filter = Localization.T("Common.SqlFilesFilter");
                dialog.DefaultExt = "sql";
                dialog.FileName = selection.DatabaseName + "_" + selection.ObjectName + "_view.sql";

                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                try
                {
                    string sql = DatabaseDumpService.BuildViewDump(selection.Database, selection.DatabaseName, selection.ObjectName);
                    File.WriteAllText(dialog.FileName, sql, Encoding.UTF8);
                    MessageBox.Show(Localization.T("Object.SqlExported"), Localization.T("Common.Complete"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Localization.Format("Object.SqlExportFailed", ex.Message), Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void DumpCurrentSelectionSqlWithDialog(bool dataOnlyForTable)
        {
            TreeDatabaseTarget target = GetTargetFromCurrentSelection();
            if (target == null)
            {
                MessageBox.Show(Localization.T("Backup.SelectDatabase"), Localization.T("Tool.ExportWizard"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Filter = Localization.T("Common.SqlFilesFilter");
                dialog.DefaultExt = "sql";
                dialog.FileName = BuildCurrentSelectionDumpFileName(dataOnlyForTable);

                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                try
                {
                    DumpCurrentSelectionSqlToFile(dialog.FileName, dataOnlyForTable);
                    MessageBox.Show(Localization.T("Object.SqlExported"), Localization.T("Common.Complete"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Localization.Format("Object.SqlExportFailed", ex.Message), Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private string BuildCurrentSelectionDumpFileName(bool dataOnlyForTable)
        {
            DatabaseObjectSelection selection = GetSelectedDatabaseObject();
            if (selection != null)
            {
                if (selection.GroupName == "Tables")
                {
                    return selection.DatabaseName + "_" + selection.ObjectName + (dataOnlyForTable ? "_data" : "_structure_data") + ".sql";
                }
                if (selection.GroupName == "Views")
                {
                    return selection.DatabaseName + "_" + selection.ObjectName + "_view.sql";
                }
            }

            TreeDatabaseTarget target = GetTargetFromCurrentSelection();
            string databaseName = target == null || string.IsNullOrWhiteSpace(target.DatabaseName)
                ? "database"
                : target.DatabaseName;
            return databaseName + "_dump_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".sql";
        }

        private bool DumpCurrentSelectionSqlToFile(string targetPath, bool dataOnlyForTable)
        {
            DatabaseObjectSelection selection = GetSelectedDatabaseObject();
            string sql = null;
            string statusTarget = null;

            if (selection != null && selection.GroupName == "Tables")
            {
                sql = DatabaseDumpService.BuildTableDump(selection.Database, selection.DatabaseName, selection.ObjectName, dataOnlyForTable);
                statusTarget = Localization.Format("Object.TableTarget", selection.ObjectName);
            }
            else if (selection != null && selection.GroupName == "Views")
            {
                sql = DatabaseDumpService.BuildViewDump(selection.Database, selection.DatabaseName, selection.ObjectName);
                statusTarget = Localization.Format("Object.ViewTarget", selection.ObjectName);
            }
            else
            {
                TreeDatabaseTarget target = GetTargetFromCurrentSelection();
                if (target == null)
                {
                    return false;
                }

                sql = DatabaseDumpService.BuildDatabaseDump(target.Database, target.DatabaseName);
                statusTarget = Localization.Format("Object.DatabaseTarget", target.DatabaseName);
            }

            string dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(targetPath, sql, Encoding.UTF8);
            UpdateMainStatus(Localization.Format("Object.SqlDumpCreatedFor", statusTarget, targetPath));
            return true;
        }

        private void DumpSelectedDatabaseSqlWithDialog()
        {
            TreeDatabaseTarget target = GetTargetFromCurrentSelection();
            if (target == null)
            {
                MessageBox.Show(Localization.T("Backup.SelectDatabase"), Localization.T("Tool.DumpSql"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Filter = Localization.T("Common.SqlFilesFilter");
                dialog.DefaultExt = "sql";
                dialog.FileName = target.DatabaseName + "_dump_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".sql";

                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                try
                {
                    DumpSelectedDatabaseSqlToFile(dialog.FileName);
                    MessageBox.Show(Localization.T("Object.SqlExported"), Localization.T("Common.Complete"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Localization.Format("Object.SqlExportFailed", ex.Message), Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private bool DumpSelectedDatabaseSqlToFile(string targetPath)
        {
            TreeDatabaseTarget target = GetTargetFromCurrentSelection();
            if (target == null)
            {
                return false;
            }

            string dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            DatabaseDumpService.WriteDatabaseDump(target.Database, target.DatabaseName, targetPath);
            UpdateMainStatus(Localization.Format("Object.SqlDumpCreated", targetPath));
            return true;
        }

        private void ImportSqlWithDialog()
        {
            TreeDatabaseTarget target = GetTargetFromCurrentSelection();
            if (target == null)
            {
                MessageBox.Show(Localization.T("ImportSql.SelectDatabase"), Localization.T("ImportSql.Title"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = Localization.T("ImportSql.Title");
                dialog.Filter = Localization.T("Common.SqlFilesFilter");
                dialog.Multiselect = false;

                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                try
                {
                    string script = File.ReadAllText(dialog.FileName, Encoding.UTF8);
                    int executed = ImportSqlScript(target, script);
                    string message = string.Format(Localization.T("ImportSql.Success"), executed);
                    MessageBox.Show(message, Localization.T("Common.Complete"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateMainStatus(message);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Localization.T("ImportSql.Failed") + ex.Message, Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateMainStatus(Localization.T("ImportSql.Failed") + ex.Message);
                }
            }
        }

        private void RestoreBackupWithDialog()
        {
            TreeDatabaseTarget target = GetTargetFromCurrentSelection();
            if (target == null)
            {
                MessageBox.Show(Localization.T("Backup.SelectDatabase"), Localization.T("Backup.RestoreTitle"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = Localization.T("Backup.RestoreTitle");
                dialog.Filter = Localization.T("Backup.RestoreFileFilter");
                dialog.Multiselect = false;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    BackupRestorePackage package = BackupRestoreService.LoadRestorePackage(dialog.FileName, CountSqlScriptStatements);
                    string confirmMessage = Localization.Format(
                        "Backup.RestoreConfirm",
                        package.EntryName,
                        package.StatementCount,
                        FormatBytes(package.SizeBytes),
                        target.DatabaseName);
                    if (MessageBox.Show(confirmMessage, Localization.T("Backup.RestoreTitle"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    {
                        return;
                    }

                    DatabaseRestoreSnapshot beforeSnapshot = CaptureDatabaseRestoreSnapshot(target);
                    string safetyBackupPath = CreatePreRestoreSafetyBackup(target);
                    int executed = ImportSqlScript(target, package.Script);
                    DatabaseRestoreSnapshot afterSnapshot = CaptureDatabaseRestoreSnapshot(target);
                    string diffSummary = BackupRestoreDiffService.BuildSummary(beforeSnapshot, afterSnapshot);
                    string contentScanReportMessage = BuildRestoreContentScanReportMessage(beforeSnapshot, afterSnapshot);
                    string message = string.IsNullOrWhiteSpace(diffSummary)
                        ? Localization.Format("Backup.RestoreSuccessWithSnapshot", executed, safetyBackupPath)
                        : Localization.Format("Backup.RestoreSuccessWithSnapshotAndDiff", executed, safetyBackupPath, diffSummary);
                    if (!string.IsNullOrWhiteSpace(contentScanReportMessage))
                    {
                        message += Environment.NewLine + contentScanReportMessage;
                    }
                    MessageBox.Show(message, Localization.T("Common.Complete"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateMainStatus(message);
                }
                catch (Exception ex)
                {
                    string message = Localization.Format("Backup.RestoreFailed", ex.Message);
                    MessageBox.Show(message, Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateMainStatus(message);
                }
            }
        }

        private void RestoreQuarantinedBackupWithDialog()
        {
            string quarantineDirectory = GetBackupIntegrityQuarantineDirectory();
            List<BackupQuarantineRestoreCandidate> candidates = BackupQuarantineRestoreService.FindCandidates(quarantineDirectory);
            if (candidates.Count == 0)
            {
                MessageBox.Show(Localization.T("Backup.QuarantineRestoreEmpty"), Localization.T("Backup.QuarantineRestoreTitle"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (OpenFileDialog openDialog = new OpenFileDialog())
            {
                openDialog.Title = Localization.T("Backup.QuarantineRestoreTitle");
                openDialog.Filter = Localization.T("Backup.QuarantineRestoreFileFilter");
                openDialog.Multiselect = false;
                if (Directory.Exists(quarantineDirectory))
                {
                    openDialog.InitialDirectory = quarantineDirectory;
                }
                if (openDialog.ShowDialog(this) != DialogResult.OK) return;

                BackupQuarantineRestoreCandidate candidate = BackupQuarantineRestoreService.FindCandidate(openDialog.FileName, quarantineDirectory);
                if (candidate == null)
                {
                    MessageBox.Show(Localization.T("Backup.QuarantineRestoreEmpty"), Localization.T("Backup.QuarantineRestoreTitle"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                BackupQuarantineRestorePreview preview = BackupQuarantineRestoreService.BuildPreview(candidate, CountSqlScriptStatements);
                BackupIntegrityResult integrity = preview.IntegrityResult;
                string originalPathText = candidate.HasOriginalPath ? candidate.OriginalPath : Localization.T("Common.None");
                string integrityStatus = preview.PassedIntegrityCheck
                    ? Localization.T("Backup.QuarantineRestoreIntegrityPassed")
                    : Localization.T("Backup.QuarantineRestoreIntegrityFailed");
                string integrityMessage = integrity == null ? string.Empty : integrity.Message;
                string previewMessage = Localization.Format(
                    "Backup.QuarantineRestorePreviewConfirm",
                    candidate.QuarantinedPath,
                    originalPathText,
                    FormatBytes(candidate.SizeBytes),
                    integrityStatus,
                    integrityMessage,
                    preview.DestinationDiffSummary);
                if (MessageBox.Show(previewMessage, Localization.T("Backup.QuarantineRestoreTitle"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                {
                    return;
                }

                string suggestedPath = candidate != null && candidate.HasOriginalPath
                    ? candidate.OriginalPath
                    : openDialog.FileName;

                using (SaveFileDialog saveDialog = new SaveFileDialog())
                {
                    saveDialog.Title = Localization.T("Backup.QuarantineRestoreTitle");
                    saveDialog.Filter = Localization.T("Backup.QuarantineRestoreFileFilter");
                    saveDialog.FileName = Path.GetFileName(suggestedPath);
                    string suggestedDirectory = Path.GetDirectoryName(suggestedPath);
                    if (!string.IsNullOrWhiteSpace(suggestedDirectory) && Directory.Exists(suggestedDirectory))
                    {
                        saveDialog.InitialDirectory = suggestedDirectory;
                    }
                    if (saveDialog.ShowDialog(this) != DialogResult.OK) return;

                    bool overwrite = false;
                    if (File.Exists(saveDialog.FileName))
                    {
                        string overwriteMessage = Localization.Format("Backup.QuarantineRestoreOverwrite", saveDialog.FileName);
                        if (MessageBox.Show(overwriteMessage, Localization.T("Backup.QuarantineRestoreTitle"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                        {
                            return;
                        }
                        overwrite = true;
                    }

                    try
                    {
                        BackupQuarantineRestoreResult result = BackupQuarantineRestoreService.RestoreQuarantinedFile(openDialog.FileName, saveDialog.FileName, overwrite);
                        string message = Localization.Format("Backup.QuarantineRestoreSuccess", result.RestoredPath);
                        MessageBox.Show(message, Localization.T("Common.Complete"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                        UpdateMainStatus(message);
                    }
                    catch (Exception ex)
                    {
                        string message = Localization.Format("Backup.QuarantineRestoreFailed", ex.Message);
                        MessageBox.Show(message, Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                        UpdateMainStatus(message);
                    }
                }
            }
        }

        private void BatchRestoreQuarantinedBackupsWithDialog()
        {
            string quarantineDirectory = GetBackupIntegrityQuarantineDirectory();
            List<BackupQuarantineRestoreCandidate> candidates = BackupQuarantineRestoreService.FindCandidates(quarantineDirectory);
            if (candidates.Count == 0)
            {
                MessageBox.Show(Localization.T("Backup.QuarantineRestoreEmpty"), Localization.T("Backup.QuarantineRestoreTitle"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int withOriginalPath = 0;
            foreach (BackupQuarantineRestoreCandidate candidate in candidates)
            {
                if (candidate != null && candidate.HasOriginalPath) withOriginalPath++;
            }

            string confirmMessage = Localization.Format("Backup.QuarantineBatchRestoreConfirm", candidates.Count, withOriginalPath);
            if (MessageBox.Show(confirmMessage, Localization.T("Backup.QuarantineRestoreTitle"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }

            BackupQuarantineBatchRestoreResult result =
                BackupQuarantineRestoreService.RestoreAllToOriginalPaths(candidates, false);
            string message = Localization.Format(
                "Backup.QuarantineBatchRestoreResult",
                result.RestoredFiles,
                result.SkippedNoOriginalPath,
                result.SkippedExistingDestination,
                result.FailedFiles);
            MessageBox.Show(message, Localization.T("Common.Complete"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            UpdateMainStatus(message);
        }

        private DatabaseRestoreSnapshot CaptureDatabaseRestoreSnapshot(TreeDatabaseTarget target)
        {
            if (target == null || target.Database == null)
            {
                return BackupRestoreDiffService.CreateSnapshot("", "", 0, 0, 0, 0);
            }

            DataTable functions = GetDatabaseFunctions(target.Database, target.DatabaseName);
            DataTable events = GetDatabaseEvents(target.Database, target.DatabaseName);
            DatabaseRestoreSnapshot snapshot = BackupRestoreDiffService.CreateSnapshot(
                target.DatabaseName,
                target.Database.ProviderName,
                GetTablesSafe(target.Database, target.DatabaseName),
                GetViewsSafe(target.Database, target.DatabaseName),
                ExtractNameColumn(functions),
                ExtractNameColumn(events));

            foreach (string tableName in snapshot.Tables)
            {
                BackupRestoreDiffService.AddTableColumns(
                    snapshot,
                    tableName,
                    BackupRestoreDiffService.CreateColumnSnapshots(tableName, GetColumnsSafe(target.Database, target.DatabaseName, tableName)));
                try
                {
                    long rowCount = target.Database.CountRows(target.DatabaseName, tableName);
                    BackupRestoreDiffService.SetTableRowCount(snapshot, tableName, rowCount);
                    if (rowCount > 0)
                    {
                        BackupRestoreDiffService.SetTableContentFingerprint(
                            snapshot,
                            tableName,
                            BackupRestoreDiffService.CreateTableContentFingerprint(
                                tableName,
                                rowCount,
                                (offset, limit) => target.Database.SelectTablePage(target.DatabaseName, tableName, offset, limit),
                                BackupRestoreDiffService.ContentSnapshotPageSize,
                                GetRestoreContentSnapshotMaxRows()));
                    }
                }
                catch
                {
                }
            }

            return snapshot;
        }

        private string BuildRestoreContentScanReportMessage(DatabaseRestoreSnapshot beforeSnapshot, DatabaseRestoreSnapshot afterSnapshot)
        {
            try
            {
                string reportPath = BackupRestoreDiffService.WriteContentScanReport(
                    beforeSnapshot,
                    afterSnapshot,
                    GetRestoreContentScanReportDirectory());
                return Localization.Format("Backup.RestoreContentScanReportCreated", reportPath);
            }
            catch (Exception ex)
            {
                return Localization.Format("Backup.RestoreContentScanReportFailed", ex.Message);
            }
        }

        private static int GetRestoreContentSnapshotMaxRows()
        {
            return BackupRestoreDiffService.ResolveMaxContentSnapshotRows(BackupMirrorSettings.RestoreContentSnapshotMaxRows);
        }

        private static List<string> ExtractNameColumn(DataTable table)
        {
            List<string> names = new List<string>();
            if (table == null || !table.Columns.Contains("Name")) return names;
            foreach (DataRow row in table.Rows)
            {
                string name = row["Name"] == null ? string.Empty : row["Name"].ToString();
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            }
            return names;
        }

        private string CreatePreRestoreSafetyBackup(TreeDatabaseTarget target)
        {
            string backupPath = BuildPreRestoreBackupPath(target.DatabaseName, target.Database.ProviderName, DateTime.Now);
            try
            {
                CreateDatabaseBackup(target, backupPath);
                string archivePath = PreDeleteBackupArchiveService.ArchiveAndPrune(
                    backupPath,
                    PreDeleteBackupArchiveService.DefaultRetainCount,
                    PreDeleteBackupArchiveService.PreRestoreBackupArchivePattern);
                UpdateMainStatus(Localization.Format("Backup.RestoreSafetyBackupCreated", archivePath));
                return archivePath;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(Localization.Format("Backup.RestoreSafetyBackupFailedCancelled", ex.Message), ex);
            }
        }

        private int ImportSqlScriptToSelectedDatabase(string script)
        {
            TreeDatabaseTarget target = GetTargetFromCurrentSelection();
            if (target == null)
            {
                return -1;
            }

            return ImportSqlScript(target, script);
        }

        private int ImportSqlScript(TreeDatabaseTarget target, string script)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));

            int executed = ExecuteSqlScript(target.Database, target.DatabaseName, script);
            RefreshDatabaseObjectNodes(target.DatabaseNode);
            db_tree.SelectedNode = target.DatabaseNode;
            UpdateMainStatus(string.Format(Localization.T("ImportSql.Success"), executed));
            return executed;
        }

        private static int ExecuteSqlScript(IDatabase db, string databaseName, string script)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (script == null) throw new ArgumentNullException(nameof(script));

            int executed = 0;
            foreach (string statement in SplitSqlScript(script))
            {
                string sql = statement.Trim();
                if (sql.Length == 0) continue;

                Dictionary<string, string> result = db.ExecSQL(sql);
                if (!result.ContainsKey("status") || result["status"] != "OK")
                {
                    string reason = result.ContainsKey("reason") ? result["reason"] : "unknown error";
                    throw new Exception(reason + Environment.NewLine + sql);
                }

                executed++;
            }

            return executed;
        }

        private static int CountSqlScriptStatements(string script)
        {
            return SplitSqlScript(script).Count(statement => !string.IsNullOrWhiteSpace(statement));
        }

        private static List<string> SplitSqlScript(string script)
        {
            List<string> statements = new List<string>();
            StringBuilder current = new StringBuilder();
            bool inSingle = false;
            bool inDouble = false;
            bool inBacktick = false;
            bool inLineComment = false;
            bool inBlockComment = false;

            for (int i = 0; i < script.Length; i++)
            {
                char c = script[i];
                char next = i + 1 < script.Length ? script[i + 1] : '\0';

                if (inLineComment)
                {
                    current.Append(c);
                    if (c == '\n') inLineComment = false;
                    continue;
                }

                if (inBlockComment)
                {
                    current.Append(c);
                    if (c == '*' && next == '/')
                    {
                        current.Append(next);
                        i++;
                        inBlockComment = false;
                    }
                    continue;
                }

                if (!inSingle && !inDouble && !inBacktick)
                {
                    if (c == '-' && next == '-')
                    {
                        current.Append(c);
                        current.Append(next);
                        i++;
                        inLineComment = true;
                        continue;
                    }
                    if (c == '/' && next == '*')
                    {
                        current.Append(c);
                        current.Append(next);
                        i++;
                        inBlockComment = true;
                        continue;
                    }
                    if (c == ';')
                    {
                        statements.Add(current.ToString());
                        current.Clear();
                        continue;
                    }
                }

                current.Append(c);

                if (c == '\'' && !inDouble && !inBacktick)
                {
                    if (inSingle && next == '\'')
                    {
                        current.Append(next);
                        i++;
                    }
                    else
                    {
                        inSingle = !inSingle;
                    }
                }
                else if (c == '"' && !inSingle && !inBacktick)
                {
                    if (inDouble && next == '"')
                    {
                        current.Append(next);
                        i++;
                    }
                    else
                    {
                        inDouble = !inDouble;
                    }
                }
                else if (c == '`' && !inSingle && !inDouble)
                {
                    inBacktick = !inBacktick;
                }
            }

            if (current.Length > 0)
            {
                statements.Add(current.ToString());
            }

            return statements;
        }

        private void BackupSelectedDatabaseWithDialog()
        {
            TreeDatabaseTarget target = GetTargetFromCurrentSelection();
            if (target == null)
            {
                MessageBox.Show(Localization.T("Backup.SelectDatabase"), Localization.T("Backup.Title"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                bool sqlite = target.Database is my_sqlite;
                dialog.Filter = sqlite ? "SQLite database (*.sqlite)|*.sqlite|All files (*.*)|*.*" : "SQL files (*.sql)|*.sql|All files (*.*)|*.*";
                dialog.DefaultExt = sqlite ? "sqlite" : "sql";
                dialog.FileName = target.DatabaseName + "_backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + (sqlite ? ".sqlite" : ".sql");

                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                try
                {
                    string remoteMirrorPath = CreateDatabaseBackup(target, dialog.FileName);
                    string message = string.IsNullOrWhiteSpace(remoteMirrorPath)
                        ? Localization.T("Backup.SuccessVerified")
                        : Localization.Format("Backup.SuccessWithRemoteMirror", remoteMirrorPath);
                    MessageBox.Show(message, Localization.T("Common.Complete"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateMainStatus(message);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Localization.T("Backup.Failed") + ex.Message, Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateMainStatus("Backup failed: " + ex.Message);
                }
            }
        }

        private bool BackupSelectedDatabaseToFile(string targetPath)
        {
            TreeDatabaseTarget target = GetTargetFromCurrentSelection();
            if (target == null)
            {
                return false;
            }

            string remoteMirrorPath = CreateDatabaseBackup(target, targetPath);
            UpdateMainStatus(string.IsNullOrWhiteSpace(remoteMirrorPath)
                ? "Backup created: " + targetPath
                : Localization.Format("Backup.SuccessWithRemoteMirror", remoteMirrorPath));
            return true;
        }

        private string CreateDatabaseBackup(TreeDatabaseTarget target, string targetPath)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentException("Target path is required.", nameof(targetPath));

            string dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }

            my_sqlite sqlite = target.Database as my_sqlite;
            if (sqlite != null)
            {
                using (System.Data.SQLite.SQLiteConnection destination = new System.Data.SQLite.SQLiteConnection("Data Source=" + targetPath + ";Version=3;"))
                {
                    destination.Open();
                    sqlite.MCT.BackupDatabase(destination, "main", "main", -1, null, 0);
                }
                VerifyDatabaseBackupOrThrow(targetPath);
                return MirrorDatabaseBackupToRemote(targetPath);
            }

            DatabaseDumpService.WriteDatabaseDump(target.Database, target.DatabaseName, targetPath);
            VerifyDatabaseBackupOrThrow(targetPath);
            return MirrorDatabaseBackupToRemote(targetPath);
        }

        private static string MirrorDatabaseBackupToRemote(string targetPath)
        {
            string remoteDirectory = BackupMirrorSettings.RemoteDirectory;
            if (string.IsNullOrWhiteSpace(remoteDirectory)) return string.Empty;
            string mirrorPath = BackupRemoteMirrorService.MirrorBackup(targetPath, remoteDirectory, BackupMirrorSettings.RetainCount);
            if (!string.IsNullOrWhiteSpace(mirrorPath))
            {
                VerifyDatabaseBackupOrThrow(mirrorPath);
            }
            return mirrorPath;
        }

        private static void VerifyDatabaseBackupOrThrow(string backupPath)
        {
            BackupIntegrityResult result = BackupIntegrityService.VerifyBackup(backupPath, CountSqlScriptStatements);
            if (!result.IsValid)
            {
                throw new InvalidOperationException(Localization.Format("Backup.IntegrityFailed", backupPath, result.Message));
            }
        }

        private static string BuildDatabaseDump(IDatabase db, string databaseName)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("-- mySQLPunk Database Backup");
            builder.AppendLine("-- Provider: " + db.ProviderName);
            builder.AppendLine("-- Database: " + databaseName);
            builder.AppendLine("-- Created: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            builder.AppendLine();

            foreach (string tableName in db.GetTables(databaseName))
            {
                builder.AppendLine(BuildTableDump(db, databaseName, tableName, false));
            }

            foreach (string viewName in db.GetViews(databaseName))
            {
                builder.AppendLine(BuildViewDump(db, databaseName, viewName));
            }

            return builder.ToString();
        }

        private static string BuildTableDump(IDatabase db, string databaseName, string tableName, bool dataOnly)
        {
            StringBuilder builder = new StringBuilder();
            string insertTarget = BuildDumpInsertTargetName(db, databaseName, tableName);

            builder.AppendLine("-- mySQLPunk SQL Dump");
            builder.AppendLine("-- Provider: " + db.ProviderName);
            builder.AppendLine("-- Database: " + databaseName);
            builder.AppendLine("-- Table: " + tableName);
            if (db is my_mysql)
            {
                builder.AppendLine("SET NAMES utf8mb4;");
                builder.AppendLine("USE " + QuoteDumpIdentifier(db, databaseName) + ";");
            }
            builder.AppendLine();

            if (!dataOnly)
            {
                string ddl = db.GetTableCreateStatement(databaseName, tableName);
                if (!string.IsNullOrWhiteSpace(ddl))
                {
                    builder.AppendLine(ddl.TrimEnd().TrimEnd(';') + ";");
                    builder.AppendLine();
                }

                string sqliteColumnComments = BuildSqliteColumnCommentsDump(db, databaseName, tableName);
                if (!string.IsNullOrWhiteSpace(sqliteColumnComments))
                {
                    builder.AppendLine(sqliteColumnComments.TrimEnd());
                    builder.AppendLine();
                }
            }

            long total = db.CountRows(databaseName, tableName);
            long copied = 0;
            const int batchSize = 1000;
            while (copied < total)
            {
                DataTable dataTable = db.SelectTablePage(databaseName, tableName, copied, batchSize);
                if (dataTable == null || dataTable.Rows.Count == 0) break;

                foreach (DataRow row in dataTable.Rows)
                {
                    builder.Append("INSERT INTO ");
                    builder.Append(insertTarget);
                    builder.Append(" (");

                    for (int i = 0; i < dataTable.Columns.Count; i++)
                    {
                        if (i > 0) builder.Append(", ");
                        builder.Append(QuoteDumpIdentifier(db, dataTable.Columns[i].ColumnName));
                    }

                    builder.Append(") VALUES (");

                    for (int i = 0; i < dataTable.Columns.Count; i++)
                    {
                        if (i > 0) builder.Append(", ");
                        builder.Append(ToSqlLiteral(db, row[i]));
                    }

                    builder.AppendLine(");");
                }

                copied += dataTable.Rows.Count;
            }

            return builder.ToString();
        }

        private static string BuildSqliteColumnCommentsDump(IDatabase db, string databaseName, string tableName)
        {
            if (!IsDumpProvider(db, "sqlite") || string.IsNullOrWhiteSpace(tableName)) return "";

            DataTable columns;
            try
            {
                columns = db.GetColumns(databaseName, tableName);
            }
            catch
            {
                return "";
            }

            if (columns == null || columns.Rows.Count == 0) return "";

            List<string> inserts = new List<string>();
            foreach (DataRow row in columns.Rows)
            {
                string columnName = FirstColumnValue(row, "name", "Name", "COLUMN_NAME", "column_name", "Field");
                string comment = FirstColumnValue(row, "Comment", "comment");
                if (string.IsNullOrWhiteSpace(columnName) || string.IsNullOrWhiteSpace(comment)) continue;

                inserts.Add("INSERT OR REPLACE INTO " + QuoteSqliteIdentifier(my_sqlite.ColumnCommentTableName) +
                            " (table_name, column_name, comment) VALUES (" +
                            "'" + EscapeSqlLiteral(tableName) + "', " +
                            "'" + EscapeSqlLiteral(columnName) + "', " +
                            "'" + EscapeSqlLiteral(comment) + "');");
            }

            if (inserts.Count == 0) return "";

            StringBuilder builder = new StringBuilder();
            builder.AppendLine("-- SQLite column comments sidecar metadata");
            builder.AppendLine("CREATE TABLE IF NOT EXISTS " + QuoteSqliteIdentifier(my_sqlite.ColumnCommentTableName) + " (" +
                               "table_name TEXT NOT NULL, " +
                               "column_name TEXT NOT NULL, " +
                               "comment TEXT NOT NULL, " +
                               "PRIMARY KEY (table_name, column_name));");
            foreach (string insert in inserts)
            {
                builder.AppendLine(insert);
            }

            return builder.ToString();
        }

        private static string BuildDumpInsertTargetName(IDatabase db, string databaseName, string tableName)
        {
            if (IsDumpProvider(db, "oracle"))
            {
                return BuildQualifiedObjectName(db, databaseName, tableName);
            }

            return QuoteDumpIdentifier(db, tableName);
        }

        private static string BuildViewDump(IDatabase db, string databaseName, string viewName)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("-- mySQLPunk SQL Dump");
            builder.AppendLine("-- Provider: " + db.ProviderName);
            builder.AppendLine("-- Database: " + databaseName);
            builder.AppendLine("-- View: " + viewName);
            if (db is my_mysql)
            {
                builder.AppendLine("SET NAMES utf8mb4;");
                builder.AppendLine("USE " + QuoteDumpIdentifier(db, databaseName) + ";");
            }
            builder.AppendLine();

            string ddl = db.GetViewCreateStatement(databaseName, viewName);
            if (!string.IsNullOrWhiteSpace(ddl))
            {
                builder.AppendLine(ddl.TrimEnd().TrimEnd(';') + ";");
            }

            return builder.ToString();
        }

        private static string QuoteDumpIdentifier(IDatabase db, string name)
        {
            if (IsDumpProvider(db, "mysql"))
            {
                return "`" + name.Replace("`", "``") + "`";
            }
            if (IsDumpProvider(db, "mssql") || IsDumpProvider(db, "sqlserver"))
            {
                return "[" + name.Replace("]", "]]") + "]";
            }
            if (IsDumpProvider(db, "postgresql") || IsDumpProvider(db, "sqlite") || IsDumpProvider(db, "oracle"))
            {
                return "\"" + name.Replace("\"", "\"\"") + "\"";
            }
            return name;
        }

        private static string BuildQualifiedObjectName(IDatabase db, string databaseName, string objectName)
        {
            if (IsDumpProvider(db, "mysql"))
            {
                return QuoteDumpIdentifier(db, databaseName) + "." + QuoteDumpIdentifier(db, objectName);
            }
            if (IsDumpProvider(db, "mssql") || IsDumpProvider(db, "sqlserver"))
            {
                SqlServerObjectName target = ParseSqlServerObjectName(objectName);
                return QuoteDumpIdentifier(db, databaseName) + "." + QuoteDumpIdentifier(db, target.Schema) + "." + QuoteDumpIdentifier(db, target.Name);
            }
            if (IsDumpProvider(db, "postgresql"))
            {
                PostgreSqlObjectName target = ParsePostgreSqlObjectName(objectName);
                return QuoteDumpIdentifier(db, target.Schema) + "." + QuoteDumpIdentifier(db, target.Name);
            }
            if (IsDumpProvider(db, "sqlite"))
            {
                return QuoteDumpIdentifier(db, objectName);
            }
            if (IsDumpProvider(db, "oracle"))
            {
                return QuoteDumpIdentifier(db, databaseName) + "." + QuoteDumpIdentifier(db, objectName);
            }
            return QuoteDumpIdentifier(db, objectName);
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

        private static bool IsDumpProvider(IDatabase db, string providerName)
        {
            return db != null && string.Equals(db.ProviderName, providerName, StringComparison.OrdinalIgnoreCase);
        }

        private string GetSelectedSchemaSeedObjectName(IDatabase db)
        {
            if (!(db is my_postgresql) && !(db is my_mssql)) return string.Empty;

            string[] pathParts = GetTreePathParts(db_tree.SelectedNode);
            if (pathParts.Length < 4) return string.Empty;

            string groupName = pathParts[2];
            if (groupName != "Tables" && groupName != "Views" && groupName != "Functions" && groupName != "Triggers")
            {
                return string.Empty;
            }

            return pathParts[3];
        }

        private static string BuildSiblingObjectName(IDatabase db, string schemaSeedObjectName, string objectName)
        {
            if (string.IsNullOrWhiteSpace(schemaSeedObjectName)) return objectName;

            if (db is my_postgresql)
            {
                PostgreSqlObjectName target = ParsePostgreSqlObjectName(schemaSeedObjectName);
                return target.Schema + "." + objectName;
            }

            if (db is my_mssql)
            {
                SqlServerObjectName target = ParseSqlServerObjectName(schemaSeedObjectName);
                return target.Schema + "." + objectName;
            }

            return objectName;
        }

        private static string BuildObjectDefinitionName(IDatabase db, string databaseName, string objectName)
        {
            if (IsDumpProvider(db, "mssql") || IsDumpProvider(db, "sqlserver"))
            {
                SqlServerObjectName target = ParseSqlServerObjectName(objectName);
                return QuoteDumpIdentifier(db, target.Schema) + "." + QuoteDumpIdentifier(db, target.Name);
            }

            return BuildQualifiedObjectName(db, databaseName, objectName);
        }

        private static string BuildNewViewTemplate(IDatabase db, string databaseName, string schemaSeedObjectName)
        {
            string newViewName = BuildSiblingObjectName(db, schemaSeedObjectName, "new_view");
            string sourceTableName = BuildSiblingObjectName(db, schemaSeedObjectName, "table_name");
            return "CREATE VIEW " + BuildObjectDefinitionName(db, databaseName, newViewName) + " AS" + Environment.NewLine +
                   "SELECT *" + Environment.NewLine +
                   "FROM " + BuildQualifiedObjectName(db, databaseName, sourceTableName) + ";";
        }

        private static string BuildFunctionTemplate(IDatabase db, string databaseName, string schemaSeedObjectName)
        {
            if (db is my_sqlite)
            {
                return "-- SQLite does not store functions in the database schema." + Environment.NewLine +
                       "-- Application-defined SQLite functions must be registered by the client connection." + Environment.NewLine +
                       "SELECT 1;";
            }

            if (db is my_mysql)
            {
                return "DELIMITER $$" + Environment.NewLine +
                       "CREATE FUNCTION " + QuoteDumpIdentifier(db, "new_function") + "()" + Environment.NewLine +
                       "RETURNS INT" + Environment.NewLine +
                       "DETERMINISTIC" + Environment.NewLine +
                       "BEGIN" + Environment.NewLine +
                       "    RETURN 1;" + Environment.NewLine +
                       "END$$" + Environment.NewLine +
                       "DELIMITER ;";
            }

            if (db is my_postgresql)
            {
                return "CREATE OR REPLACE FUNCTION " + BuildQualifiedObjectName(db, databaseName, BuildSiblingObjectName(db, schemaSeedObjectName, "new_function")) + "()" + Environment.NewLine +
                       "RETURNS integer" + Environment.NewLine +
                       "LANGUAGE sql" + Environment.NewLine +
                       "AS $$" + Environment.NewLine +
                       "    SELECT 1;" + Environment.NewLine +
                       "$$;";
            }

            if (db is my_mssql)
            {
                return "CREATE FUNCTION " + BuildObjectDefinitionName(db, databaseName, BuildSiblingObjectName(db, schemaSeedObjectName, "new_function")) + "()" + Environment.NewLine +
                       "RETURNS INT" + Environment.NewLine +
                       "AS" + Environment.NewLine +
                       "BEGIN" + Environment.NewLine +
                       "    RETURN 1;" + Environment.NewLine +
                       "END;";
            }

            if (db is my_oracle)
            {
                string owner = string.IsNullOrWhiteSpace(databaseName) ? "USER" : QuoteDumpIdentifier(db, databaseName.ToUpperInvariant());
                return "CREATE OR REPLACE FUNCTION " + owner + "." + QuoteDumpIdentifier(db, "NEW_FUNCTION") + Environment.NewLine +
                       "RETURN NUMBER" + Environment.NewLine +
                       "AS" + Environment.NewLine +
                       "BEGIN" + Environment.NewLine +
                       "    RETURN 1;" + Environment.NewLine +
                       "END;" + Environment.NewLine +
                       "/";
            }

            return "CREATE FUNCTION new_function() RETURNS INT BEGIN RETURN 1; END;";
        }

        private static string BuildFunctionExecuteSql(IDatabase db, string databaseName, string routineName, string routineType)
        {
            bool isProcedure = IsProcedureRoutine(routineType);
            string qualifiedName = BuildQualifiedObjectName(db, databaseName, routineName);

            if (db is my_mssql)
            {
                return isProcedure ? "EXEC " + qualifiedName + ";" : "SELECT " + qualifiedName + "();";
            }

            if (db is my_oracle)
            {
                return isProcedure ? "BEGIN" + Environment.NewLine + "    " + qualifiedName + "();" + Environment.NewLine + "END;" :
                    "SELECT " + qualifiedName + "() FROM dual;";
            }

            if (isProcedure)
            {
                return "CALL " + qualifiedName + "();";
            }

            return "SELECT " + qualifiedName + "();";
        }

        private static bool IsProcedureRoutine(string routineType)
        {
            return !string.IsNullOrWhiteSpace(routineType) &&
                   routineType.IndexOf("procedure", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private string GetSelectedFunctionType(DatabaseObjectSelection selection)
        {
            if (selection == null) return "Function";

            DataRow match = GetDatabaseFunctions(selection.Database, selection.DatabaseName).Rows
                .Cast<DataRow>()
                .FirstOrDefault(row => string.Equals(row["Name"].ToString(), selection.ObjectName, StringComparison.OrdinalIgnoreCase));
            return match == null ? "Function" : match["Type"].ToString();
        }

        private static string GetTargetHost(TreeDatabaseTarget target)
        {
            if (target == null || target.ConnectionInfo == null || !target.ConnectionInfo.ContainsKey("host") || target.ConnectionInfo["host"] == null)
            {
                return string.Empty;
            }

            return target.ConnectionInfo["host"].ToString();
        }

        private static string ToSqlLiteral(IDatabase db, object value)
        {
            if (value == null || value == DBNull.Value)
            {
                return "NULL";
            }

            if (value is byte[] bytes)
            {
                StringBuilder hex = new StringBuilder(bytes.Length * 2);
                for (int i = 0; i < bytes.Length; i++)
                {
                    hex.Append(bytes[i].ToString("X2"));
                }

                if (IsDumpProvider(db, "oracle"))
                {
                    return "HEXTORAW('" + hex + "')";
                }
                if (IsDumpProvider(db, "postgresql"))
                {
                    return "'\\x" + hex + "'";
                }
                if (IsDumpProvider(db, "sqlite"))
                {
                    return "X'" + hex + "'";
                }

                return "0x" + hex;
            }

            if (value is bool)
            {
                return ((bool)value) ? "1" : "0";
            }

            if (IsDumpProvider(db, "oracle") && value is DateTime oracleDateTime)
            {
                return "TO_TIMESTAMP('" +
                       oracleDateTime.ToString("yyyy-MM-dd HH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture) +
                       "', 'YYYY-MM-DD HH24:MI:SS.FF7')";
            }

            if (value is string || value is char || value is DateTime || value is Guid)
            {
                return "'" + value.ToString().Replace("'", "''") + "'";
            }

            if (value is IFormattable formattable)
            {
                return formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
            }

            return "'" + value.ToString().Replace("'", "''") + "'";
        }

        private static string ToSqlLiteral(object value)
        {
            return ToSqlLiteral(null, value);
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            ApplyModernTheme(this);
            LoadFavoriteNodePaths();
            UI_init();
            myN.getSettingINI();
            drawLists();
            ArrangeMainLayout();
            StartBackupIntegritySchedule();
            if (ApplicationOptionSettings.GetBool("AutoCheckUpdates"))
            {
                BeginInvoke(new Action(() => CheckForUpdatesAsync(true)));
            }
        }

        private void StartBackupIntegritySchedule()
        {
            if (!_backupIntegrityTimer.Enabled) _backupIntegrityTimer.Start();
            RunBackupIntegrityScheduleIfDue();
        }

        private async void RunBackupIntegrityScheduleIfDue()
        {
            if (_backupIntegrityScanRunning) return;
            if (!BackupIntegrityScheduleService.IsDue(
                BackupMirrorSettings.IntegrityScheduleEnabled,
                BackupMirrorSettings.LastIntegrityVerifiedUtc,
                BackupMirrorSettings.IntegrityIntervalHours,
                DateTime.UtcNow))
            {
                return;
            }

            _backupIntegrityScanRunning = true;
            try
            {
                string[] directories = GetBackupIntegrityDirectories().ToArray();
                BackupIntegrityScheduleReport report = await Task.Run(() =>
                    BackupIntegrityScheduleService.VerifyDirectories(directories, CountSqlScriptStatements));
                BackupIntegrityQuarantineResult quarantineResult = null;
                if (report.HasFailures && BackupMirrorSettings.IntegrityAutoQuarantineEnabled)
                {
                    quarantineResult = BackupIntegrityScheduleService.QuarantineFailedBackups(
                        report,
                        GetBackupIntegrityQuarantineDirectory(),
                        BackupMirrorSettings.IntegrityQuarantineRetainCount);
                }
                string reportPath = BackupIntegrityScheduleService.WriteReport(report, GetBackupIntegrityReportDirectory());

                BackupMirrorSettings.LastIntegrityVerifiedUtc = report.VerifiedAtUtc;
                BackupMirrorSettings.LastIntegrityReportPath = reportPath;
                BackupMirrorSettings.Save();

                string reportNote = Localization.Format("Status.BackupIntegrityReportPath", reportPath);
                string quarantineNote = quarantineResult != null && quarantineResult.MovedFiles > 0
                    ? " " + Localization.Format("Status.BackupIntegrityQuarantined", quarantineResult.MovedFiles, quarantineResult.QuarantineDirectory)
                    : string.Empty;

                if (report.TotalFiles <= 0)
                {
                    UpdateMainStatus(Localization.T("Status.BackupIntegritySkipped") + " " + reportNote);
                }
                else if (report.HasFailures)
                {
                    UpdateMainStatus(Localization.Format("Status.BackupIntegrityFailed", report.FailedFiles) + quarantineNote + " " + reportNote);
                }
                else
                {
                    UpdateMainStatus(Localization.Format("Status.BackupIntegrityPassed", report.VerifiedFiles) + " " + reportNote);
                }
            }
            catch (Exception ex)
            {
                UpdateMainStatus(Localization.Format("Status.BackupIntegrityFailed", 1) + " " + ex.Message);
            }
            finally
            {
                _backupIntegrityScanRunning = false;
            }
        }

        private static IEnumerable<string> GetBackupIntegrityDirectories()
        {
            HashSet<string> directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrWhiteSpace(documents))
            {
                directories.Add(Path.Combine(documents, "mySQLPunk", "pre-delete-backups"));
                directories.Add(Path.Combine(documents, "mySQLPunk", "pre-restore-backups"));
            }

            string remoteDirectory = BackupMirrorSettings.RemoteDirectory;
            if (!string.IsNullOrWhiteSpace(remoteDirectory))
            {
                directories.Add(remoteDirectory);
            }

            return directories;
        }

        private static string GetBackupIntegrityReportDirectory()
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documents))
            {
                documents = Application.UserAppDataPath;
            }
            return Path.Combine(documents, "mySQLPunk", "backup-integrity-reports");
        }

        private static string GetRestoreContentScanReportDirectory()
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documents))
            {
                documents = Application.UserAppDataPath;
            }
            return Path.Combine(documents, "mySQLPunk", "restore-content-scan-reports");
        }

        private static string GetBackupIntegrityQuarantineDirectory()
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documents))
            {
                documents = Application.UserAppDataPath;
            }
            return Path.Combine(documents, "mySQLPunk", "backup-quarantine");
        }

        private void Form1_Resize(object sender, EventArgs e)
        {
            ArrangeMainLayout();
        }

        private void ArrangeMainLayout()
        {
            if (menuStrip1 == null || splitContainer1 == null || statusStrip1 == null) return;

            int menuHeight = menuStrip1.Visible ? menuStrip1.Height : 0;
            int statusHeight = statusStrip1.Visible ? statusStrip1.Height : 0;

            menuStrip1.Dock = DockStyle.None;
            menuStrip1.AutoSize = false;
            menuStrip1.BackColor = ThemeManager.SurfaceColor;
            menuStrip1.Location = new Point(0, 0);
            menuStrip1.Size = new Size(ClientSize.Width, menuHeight);

            statusStrip1.Dock = DockStyle.None;
            statusStrip1.AutoSize = false;
            statusStrip1.BackColor = ThemeManager.SurfaceColor;
            statusStrip1.Location = new Point(0, Math.Max(menuHeight, ClientSize.Height - statusHeight));
            statusStrip1.Size = new Size(ClientSize.Width, statusHeight);
            statusStrip1.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;

            splitContainer1.Dock = DockStyle.None;
            splitContainer1.Location = new Point(0, menuHeight);
            splitContainer1.Size = new Size(
                ClientSize.Width,
                Math.Max(0, ClientSize.Height - menuHeight - statusHeight));
            splitContainer1.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            int toolbarHeight = Math.Min(MainToolbarHeight, Math.Max(0, splitContainer1.Height - splitContainer1.SplitterWidth));
            if (toolbarHeight > 0 && splitContainer1.SplitterDistance != toolbarHeight)
            {
                splitContainer1.SplitterDistance = toolbarHeight;
            }

            menuStrip1.BringToFront();
            statusStrip1.BringToFront();
        }

        [DllImport("uxtheme.dll", SetLastError = true, ExactSpelling = true, CharSet = CharSet.Unicode)]
        public static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

        private void menuStrip1_ItemClicked(object sender, ToolStripItemClickedEventArgs e)
        {

        }

        private void splitContainer3_Panel1_Resize(object sender, EventArgs e)
        {
            db_tree.Width = splitContainer3.Width;
            db_tree.Height = splitContainer3.Height;
        }

        private void db_tree_AfterSelect(object sender, TreeViewEventArgs e)
        {
            if (e.Node == null) return;

            // 若點選的是連線群組節點，不做任何展開動作
            if (IsConnectionGroupNode(e.Node)) return;

            string fullPath = e.Node.FullPath;
            var pathParts = GetTreePathParts(e.Node);

            // 更新導覽列
            Control[] navControls = this.Controls.Find("lblNav", true);
            if (navControls.Length > 0) ((Label)navControls[0]).Text = "  " + fullPath.Replace("\\", " > ");

            // 取得根連線資訊（支援群組節點，利用 Tag 取得正確的連線索引）
            TreeNode root = e.Node;
            while (root.Parent != null && !IsConnectionGroupNode(root.Parent))
                root = root.Parent;
            int connIndex = GetConnectionIndex(root);
            if (connIndex < 0 || connIndex >= myN.connections.Count) return;
            var connInfo = myN.connections[connIndex];

            if (connInfo["isConnect"].ToString() != "T") return;
            IDatabase db = (IDatabase)connInfo["pdo"];

            if (pathParts.Length >= 2)
            {
                // 選到資料庫及其子節點 (Tables, Table, etc.)
                table_top.Visible = true;
                queryTabs.Visible = false;
                table_top.BringToFront();
                UpdateObjectFilterVisibility();

                string dbName = pathParts[1];
                
                // 如果是 Database 節點或物件分類節點，依分類顯示清單
                if (pathParts.Length == 2)
                {
                    lblSidebarTitle.Text = $"Database: {dbName}";
                    ShowDatabaseObjectList(db, dbName);
                    ShowDatabaseInfo(db, dbName);
                    showTools("點到展開的資料庫");
                }
                else if (pathParts.Length == 3)
                {
                    string groupName = pathParts[2];
                    lblSidebarTitle.Text = $"{GetTreeGroupText(groupName)}: {dbName}";
                    ShowDatabaseGroupList(db, dbName, groupName, connInfo);
                    ShowDatabaseInfo(db, dbName);
                    if (groupName == "Tables") showTools("點到Tables");
                    else if (groupName == "Views") showTools("點到Views本身");
                    else if (groupName == "Functions") showTools("點到Functions本身");
                }
                
                // 如果是 Table 節點，則側邊欄顯示 Table 詳情
                if (pathParts.Length >= 4 && pathParts[2] == "Tables")
                {
                    string tableName = pathParts[3];
                    lblSidebarTitle.Text = $"Table: {tableName}";
                    // 只有當 table_top 還沒載入過或是不同 DB 時才重載列表
                    ShowDatabaseObjectList(db, dbName); 
                    ShowTableDetails(db, dbName, tableName);
                    showTools("點到Table本身");
                }
                if (pathParts.Length >= 4 && pathParts[2] == "Views")
                {
                    string viewName = pathParts[3];
                    lblSidebarTitle.Text = $"View: {viewName}";
                    ShowDatabaseGroupList(db, dbName, "Views", connInfo);
                    ShowViewDetails(db, dbName, viewName);
                    showTools("點到View本身");
                }
                if (pathParts.Length >= 4 && pathParts[2] == "Events")
                {
                    string eventName = pathParts[3];
                    lblSidebarTitle.Text = $"Event: {eventName}";
                    ShowDatabaseGroupList(db, dbName, "Events", connInfo);
                    ShowEventDetails(db, dbName, eventName);
                }
                if (pathParts.Length >= 4 && pathParts[2] == "Functions")
                {
                    string functionName = pathParts[3];
                    lblSidebarTitle.Text = $"Function: {functionName}";
                    ShowDatabaseGroupList(db, dbName, "Functions", connInfo);
                    ShowFunctionDetails(db, dbName, functionName);
                    showTools("點到Function本身");
                }
                if (pathParts.Length >= 4 && pathParts[2] == "Users")
                {
                    string userName = pathParts[3];
                    lblSidebarTitle.Text = $"User: {userName}";
                    ShowDatabaseGroupList(db, dbName, "Users", connInfo);
                    ShowUserDetails(db, dbName, userName, connInfo);
                }
                if (pathParts.Length >= 4 && pathParts[2] == "Models")
                {
                    string modelName = pathParts[3];
                    lblSidebarTitle.Text = $"Model: {modelName}";
                    ShowDatabaseModel(db, dbName, modelName);
                }
                if (pathParts.Length >= 4 && pathParts[2] == "BI")
                {
                    string biName = pathParts[3];
                    lblSidebarTitle.Text = $"BI: {biName}";
                    ShowDatabaseBIReport(db, dbName, biName, connInfo);
                }
                if (pathParts.Length >= 4 && pathParts[2] == "Other")
                {
                    string toolName = pathParts[3];
                    lblSidebarTitle.Text = $"Other: {toolName}";
                    ShowDatabaseOtherTool(db, dbName, toolName, connInfo);
                }
                if (pathParts.Length >= 4 && pathParts[2] == "Reports")
                {
                    string reportName = pathParts[3];
                    lblSidebarTitle.Text = $"Report: {reportName}";
                    ShowDatabaseReport(db, dbName, reportName, connInfo);
                }
            }
        }

        private void table_top_CellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0)
            {
                table_top.ClearSelection();
                table_top.Rows[e.RowIndex].Selected = true;
                
                // 選取對應的 TreeView 節點以保持同步
                string objectName = table_top.Rows[e.RowIndex].Cells["名稱"].Value.ToString();
                string groupName = GetCurrentGridGroupName(e.RowIndex);
                SyncTreeWithDatabaseObject(objectName, groupName);

                ContextMenuStrip cms = BuildGridContextMenu(groupName);
                if (cms == null) return;
                ThemeManager.ApplyToolStrip(cms);
                cms.Show(table_top, table_top.PointToClient(Cursor.Position));
            }
        }

        private void table_top_DataBindingComplete(object sender, DataGridViewBindingCompleteEventArgs e)
        {
            ConfigureBinaryGridColumns(table_top);
        }

        private void table_top_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.Value is byte[] bytes)
            {
                e.Value = FormatBinaryGridCellValue(bytes);
                e.FormattingApplied = true;
            }
        }

        private void table_top_DataError(object sender, DataGridViewDataErrorEventArgs e)
        {
            e.ThrowException = false;
            e.Cancel = false;
            UpdateMainStatus(Localization.T("Grid.BinaryFormatFallback"));
        }

        private static void ConfigureBinaryGridColumns(DataGridView grid)
        {
            if (grid == null || grid.Columns.Count == 0) return;

            DataTable source = grid.DataSource as DataTable;
            if (source == null) return;

            for (int i = 0; i < grid.Columns.Count; i++)
            {
                DataGridViewColumn gridColumn = grid.Columns[i];
                if (!IsBinaryDataGridColumn(source, gridColumn)) continue;

                if (!(gridColumn is DataGridViewTextBoxColumn))
                {
                    DataGridViewTextBoxColumn textColumn = new DataGridViewTextBoxColumn
                    {
                        Name = gridColumn.Name,
                        HeaderText = gridColumn.HeaderText,
                        DataPropertyName = gridColumn.DataPropertyName,
                        ReadOnly = gridColumn.ReadOnly,
                        SortMode = DataGridViewColumnSortMode.Automatic,
                        Width = gridColumn.Width,
                        DisplayIndex = gridColumn.DisplayIndex
                    };
                    grid.Columns.RemoveAt(i);
                    grid.Columns.Insert(i, textColumn);
                    gridColumn = textColumn;
                }

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

        private static string FormatBinaryGridCellValue(byte[] bytes)
        {
            if (bytes == null) return "";
            if (GeometryWktConverter.TryGeometryBytesToWkt(bytes, out string wkt))
            {
                return "[Geometry] " + wkt;
            }

            int previewLength = Math.Min(bytes.Length, 12);
            StringBuilder preview = new StringBuilder(previewLength * 2);
            for (int i = 0; i < previewLength; i++)
            {
                preview.Append(bytes[i].ToString("X2"));
            }
            if (bytes.Length > previewLength) preview.Append("...");

            return "[BLOB " + bytes.Length + " bytes] 0x" + preview;
        }

        private ContextMenuStrip BuildGridContextMenu(string groupName)
        {
            var cms = new ContextMenuStrip();
            if (groupName == "Views")
            {
                var itemOpen = new ToolStripMenuItem(Localization.T("Tool.OpenView"));
                itemOpen.Click += (s, ev) => OpenSelectedViewInQuery();
                cms.Items.Add(itemOpen);

                var itemDesign = new ToolStripMenuItem(Localization.T("Tool.DesignView"));
                itemDesign.Click += (s, ev) => ShowSelectedViewDefinition();
                cms.Items.Add(itemDesign);

                var itemDump = new ToolStripMenuItem(Localization.T("Tool.DumpSql"));
                itemDump.Click += (s, ev) => DumpSelectedViewSql();
                cms.Items.Add(itemDump);

                var itemDrop = new ToolStripMenuItem(Localization.T("Tool.DeleteView"));
                itemDrop.Click += (s, ev) => DeleteSelectedView();
                cms.Items.Add(itemDrop);

                AddCopyRenameObjectMenuItems(cms);
            }
            else if (groupName == "Tables")
            {
                var itemOpen = new ToolStripMenuItem(Localization.T("Tool.SelectStar"));
                itemOpen.Click += (s, ev) => OpenSelectedTableInQuery();
                cms.Items.Add(itemOpen);

                var itemSelectColumns = new ToolStripMenuItem(Localization.T("Tool.SelectAllColumns"));
                itemSelectColumns.Click += (s, ev) => OpenSelectedTableAllColumnsInQuery();
                cms.Items.Add(itemSelectColumns);

                var itemDesign = new ToolStripMenuItem(Localization.T("Tool.DesignTable"));
                itemDesign.Click += (s, ev) => DesignSelectedTable();
                cms.Items.Add(itemDesign);

                cms.Items.Add(CreateAutoCommentModeMenuItem(mode => FillSelectedTableComments(mode)));

                var itemDrop = new ToolStripMenuItem(Localization.T("Tool.DeleteTable"));
                itemDrop.Click += (s, ev) => DeleteSelectedTable();
                cms.Items.Add(itemDrop);

                AddCopyRenameObjectMenuItems(cms);
            }
            else if (groupName == "Backups")
            {
                var itemBackup = new ToolStripMenuItem(Localization.T("Tool.CreateBackup"));
                itemBackup.Click += (s, ev) => BackupSelectedDatabaseWithDialog();
                cms.Items.Add(itemBackup);

                var itemRestore = new ToolStripMenuItem(Localization.T("Tool.RestoreBackup"));
                itemRestore.Click += (s, ev) => RestoreBackupWithDialog();
                cms.Items.Add(itemRestore);

                var itemRestoreQuarantine = new ToolStripMenuItem(Localization.T("Tool.RestoreQuarantinedBackup"));
                itemRestoreQuarantine.Click += (s, ev) => RestoreQuarantinedBackupWithDialog();
                cms.Items.Add(itemRestoreQuarantine);

                var itemBatchRestoreQuarantine = new ToolStripMenuItem(Localization.T("Tool.BatchRestoreQuarantinedBackups"));
                itemBatchRestoreQuarantine.Click += (s, ev) => BatchRestoreQuarantinedBackupsWithDialog();
                cms.Items.Add(itemBatchRestoreQuarantine);
            }
            else if (groupName == "Functions")
            {
                var itemExecute = new ToolStripMenuItem(Localization.T("Tool.ExecuteFunction"));
                itemExecute.Click += (s, ev) => ExecuteSelectedFunction();
                cms.Items.Add(itemExecute);

                var itemDesign = new ToolStripMenuItem(Localization.T("Tool.DesignFunction"));
                itemDesign.Click += (s, ev) => ShowSelectedFunctionDefinition();
                cms.Items.Add(itemDesign);

                var itemDrop = new ToolStripMenuItem(Localization.T("Tool.DeleteFunction"));
                itemDrop.Click += (s, ev) => DeleteSelectedFunction();
                cms.Items.Add(itemDrop);
            }
            else if (groupName == "Queries")
            {
                var itemOpen = new ToolStripMenuItem(Localization.T("Tool.OpenQuery"));
                itemOpen.Click += (s, ev) => OpenSelectedQueryTabFromGrid();
                cms.Items.Add(itemOpen);

                var itemClose = new ToolStripMenuItem(Localization.T("Tool.CloseQuery"));
                itemClose.Click += (s, ev) => CloseSelectedQueryTabFromGrid();
                cms.Items.Add(itemClose);
            }
            else if (IsDetailsOnlyGroup(groupName))
            {
                var itemOpenDetails = new ToolStripMenuItem(Localization.T("Tool.OpenDetails"));
                itemOpenDetails.Click += (s, ev) => ShowSelectedTreeNodeDetails();
                cms.Items.Add(itemOpenDetails);
            }

            if (cms.Items.Count == 0)
            {
                cms.Dispose();
                return null;
            }

            return cms;
        }

        private ToolStripMenuItem CreateTableDumpMenuItem()
        {
            ToolStripMenuItem dumpSqlItem = new ToolStripMenuItem(Localization.T("Tool.DumpSql"));

            ToolStripMenuItem dumpStructureAndDataItem = new ToolStripMenuItem(Localization.T("Tool.StructureAndData"));
            dumpStructureAndDataItem.Click += (s, ev) => DumpSelectedTableSql(false);
            dumpSqlItem.DropDownItems.Add(dumpStructureAndDataItem);

            ToolStripMenuItem dumpDataOnlyItem = new ToolStripMenuItem(Localization.T("Tool.DataOnly"));
            dumpDataOnlyItem.Click += (s, ev) => DumpSelectedTableSql(true);
            dumpSqlItem.DropDownItems.Add(dumpDataOnlyItem);

            return dumpSqlItem;
        }

        private void table_top_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            if (TryRunOtherToolGridAction(e.RowIndex)) return;

            string groupName = GetCurrentGridGroupName(e.RowIndex);
            if (groupName == "Queries")
            {
                ActivateQueryTabFromGridRow(e.RowIndex);
                return;
            }

            ActivateDatabaseObjectFromGridRow(e.RowIndex, groupName);
        }

        private bool TryRunOtherToolGridAction(int rowIndex)
        {
            if (table_top == null || rowIndex < 0 || rowIndex >= table_top.Rows.Count) return false;
            if (!table_top.Columns.Contains("項目") || !table_top.Columns.Contains("說明")) return false;

            string item = Convert.ToString(table_top.Rows[rowIndex].Cells["項目"].Value);
            if (!string.Equals(item, "SpatiaLite Repair Command", StringComparison.OrdinalIgnoreCase)) return false;

            string repositoryRoot = SpatiaLiteRuntimeDiagnosticService.FindRepositoryRoot(AppDomain.CurrentDomain.BaseDirectory);
            if (string.IsNullOrWhiteSpace(repositoryRoot))
            {
                MessageBox.Show(
                    "找不到 tools\\spatialite\\Build-SpatiaLiteRuntime.ps1，請確認目前執行目錄仍在完整 source tree 內。",
                    Localization.T("Common.Error"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return true;
            }

            try
            {
                Process process = Process.Start(SpatiaLiteRuntimeDiagnosticService.BuildRepairProcessStartInfo(repositoryRoot, false));
                if (process != null)
                {
                    process.EnableRaisingEvents = true;
                    process.Exited += (s, args) =>
                    {
                        int exitCode = -1;
                        try { exitCode = process.ExitCode; } catch { }
                        try { process.Dispose(); } catch { }
                        HandleSpatiaLiteRepairCompleted(exitCode);
                    };
                }
                UpdateMainStatus("SpatiaLite runtime 修復腳本已啟動。");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            return true;
        }

        private void HandleSpatiaLiteRepairCompleted(int exitCode)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<int>(HandleSpatiaLiteRepairCompleted), exitCode);
                return;
            }

            TreeDatabaseTarget target = GetTargetFromCurrentSelection();
            my_sqlite sqlite = target != null ? target.Database as my_sqlite : null;
            if (exitCode != 0)
            {
                UpdateMainStatus(Localization.Format("Connection.SpatiaLiteRepairFailed", exitCode));
                if (sqlite != null)
                {
                    ShowDatabaseOtherTool(target.Database, target.DatabaseName, "Connection Diagnostics", target.ConnectionInfo);
                }
                return;
            }

            if (sqlite == null)
            {
                UpdateMainStatus(Localization.T("Connection.SpatiaLiteRepairCompletedReconnect"));
                return;
            }

            sqlite.RetryLoadSpatiaLite();
            ShowDatabaseOtherTool(target.Database, target.DatabaseName, "Connection Diagnostics", target.ConnectionInfo);
            UpdateMainStatus(sqlite.SpatiaLiteEnabled
                ? Localization.T("Connection.SpatiaLiteRepairReloaded")
                : Localization.Format("Connection.SpatiaLiteRepairReloadFailed", sqlite.SpatiaLiteLoadError));
        }

        private bool ActivateDatabaseObjectFromGridRow(int rowIndex, string groupName)
        {
            if (rowIndex < 0 || rowIndex >= table_top.Rows.Count || !table_top.Columns.Contains("名稱")) return false;

            object value = table_top.Rows[rowIndex].Cells["名稱"].Value;
            if (value == null || value == DBNull.Value) return false;

            string objectName = value.ToString();
            if (string.IsNullOrWhiteSpace(objectName)) return false;

            SyncTreeWithDatabaseObject(objectName, groupName);
            if (db_tree.SelectedNode == null) return false;

            if (groupName == "Tables")
            {
                OpenSelectedTableInQuery();
                return true;
            }

            if (groupName == "Views")
            {
                OpenSelectedViewInQuery();
                return true;
            }

            if (groupName == "Functions")
            {
                ExecuteSelectedFunction();
                return true;
            }

            if (groupName == "Events" || groupName == "Users" || groupName == "Models" ||
                groupName == "BI" || groupName == "Other" || groupName == "Reports")
            {
                db_tree_AfterSelect(db_tree, new TreeViewEventArgs(db_tree.SelectedNode));
                return true;
            }

            return false;
        }

        private bool OpenSelectedQueryTabFromGrid()
        {
            if (table_top.SelectedRows.Count == 0) return false;
            return ActivateQueryTabFromGridRow(table_top.SelectedRows[0].Index);
        }

        private bool CloseSelectedQueryTabFromGrid()
        {
            if (table_top.SelectedRows.Count == 0) return false;
            int tabIndex = GetQueryTabIndexFromGridRow(table_top.SelectedRows[0].Index);
            if (tabIndex < 0 || tabIndex >= queryTabs.TabPages.Count) return false;

            TabPage page = queryTabs.TabPages[tabIndex];
            if (page.Tag is QueryForm queryForm)
            {
                queryForm.Close();
            }
            else
            {
                queryTabs.TabPages.Remove(page);
                page.Dispose();
            }

            RefreshQueriesGroupIfSelected();
            return true;
        }

        private bool ActivateQueryTabFromGridRow(int rowIndex)
        {
            int tabIndex = GetQueryTabIndexFromGridRow(rowIndex);
            if (tabIndex < 0 || tabIndex >= queryTabs.TabPages.Count) return false;

            table_top.Visible = false;
            queryTabs.Visible = true;
            queryTabs.SelectedIndex = tabIndex;
            queryTabs.BringToFront();
            UpdateObjectFilterVisibility();
            UpdateMainStatus("Query tab opened: " + queryTabs.TabPages[tabIndex].Text);
            return true;
        }

        private int GetQueryTabIndexFromGridRow(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= table_top.Rows.Count || !table_top.Columns.Contains("分頁索引")) return -1;
            object value = table_top.Rows[rowIndex].Cells["分頁索引"].Value;
            if (value == null || value == DBNull.Value) return -1;
            int tabIndex;
            return int.TryParse(value.ToString(), out tabIndex) ? tabIndex : -1;
        }

        private string GetCurrentGridGroupName(int rowIndex)
        {
            if (table_top.Columns.Contains("類型") && rowIndex >= 0 && rowIndex < table_top.Rows.Count)
            {
                object typeValue = table_top.Rows[rowIndex].Cells["類型"].Value;
                if (typeValue != null && string.Equals(typeValue.ToString(), "View", StringComparison.OrdinalIgnoreCase))
                {
                    return "Views";
                }
                if (typeValue != null && string.Equals(typeValue.ToString(), "Query", StringComparison.OrdinalIgnoreCase))
                {
                    return "Queries";
                }
                if (typeValue != null && (string.Equals(typeValue.ToString(), "User", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(typeValue.ToString(), "Role", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(typeValue.ToString(), "Connection", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(typeValue.ToString(), "Current User", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(typeValue.ToString(), "Superuser", StringComparison.OrdinalIgnoreCase)))
                {
                    return "Users";
                }
                if (typeValue != null && string.Equals(typeValue.ToString(), "Model", StringComparison.OrdinalIgnoreCase))
                {
                    return "Models";
                }
                if (typeValue != null && string.Equals(typeValue.ToString(), "BI", StringComparison.OrdinalIgnoreCase))
                {
                    return "BI";
                }
                if (typeValue != null && string.Equals(typeValue.ToString(), "Other", StringComparison.OrdinalIgnoreCase))
                {
                    return "Other";
                }
                if (typeValue != null && string.Equals(typeValue.ToString(), "Report", StringComparison.OrdinalIgnoreCase))
                {
                    return "Reports";
                }
            }

            if (db_tree.SelectedNode != null)
            {
                var pathParts = GetTreePathParts(db_tree.SelectedNode);
                if (pathParts.Length >= 3 && (pathParts[2] == "Views" || pathParts[2] == "Tables" || pathParts[2] == "Backups" || pathParts[2] == "Functions" || pathParts[2] == "Users" || pathParts[2] == "Models" || pathParts[2] == "BI" || pathParts[2] == "Other" || pathParts[2] == "Queries" || pathParts[2] == "Reports"))
                {
                    return pathParts[2];
                }
            }

            return "Tables";
        }

        private void SyncTreeWithDatabaseObject(string objectName, string groupName)
        {
            if (db_tree.SelectedNode == null) return;

            TreeNode databaseNode = GetSelectedDatabaseNode();
            if (databaseNode == null) return;

            TreeNode node = FindDatabaseObjectNode(databaseNode, groupName, objectName);
            if (node != null) db_tree.SelectedNode = node;
        }

        private void db_tree_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.C)
            {
                CopySelectedDatabaseObjectToInternalClipboard();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            if (e.Control && e.KeyCode == Keys.V)
            {
                PasteInternalClipboardToSelectedDatabase();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            if (e.KeyCode == Keys.F2)
            {
                BeginRenameSelectedDatabaseObject();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            if (e.KeyCode == Keys.Escape)
            {
                if (db_tree.SelectedNode == null) return;

                var pathParts = GetTreePathParts(db_tree.SelectedNode);
                
                // 使用者要求：table 按 esc 沒事，如果在 database 可以關閉 database (連線)
                if (pathParts.Length <= 2)
                {
                    TreeNode root = db_tree.SelectedNode;
                    while (root.Parent != null && !IsConnectionGroupNode(root.Parent)) root = root.Parent;
                    int closeIdx = GetConnectionIndex(root);
                    if (closeIdx >= 0) CloseConnection(closeIdx);
                }
                
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private void BeginRenameSelectedDatabaseObject()
        {
            if (!IsRenameableObjectNode(db_tree.SelectedNode))
            {
                MessageBox.Show(Localization.T("Object.SelectTableOrView"), Localization.T("Tool.RenameObject"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _allowTreeLabelEdit = true;
            db_tree.LabelEdit = true;
            db_tree.SelectedNode.BeginEdit();
        }

        private void db_tree_BeforeLabelEdit(object sender, NodeLabelEditEventArgs e)
        {
            if (!_allowTreeLabelEdit || !IsRenameableObjectNode(e.Node))
            {
                e.CancelEdit = true;
                _allowTreeLabelEdit = false;
                db_tree.LabelEdit = false;
            }
        }

        private void db_tree_AfterLabelEdit(object sender, NodeLabelEditEventArgs e)
        {
            _allowTreeLabelEdit = false;
            db_tree.LabelEdit = false;
            e.CancelEdit = true;

            if (e.Node == null || e.Label == null) return;
            string newName = e.Label.Trim();
            string oldName = e.Node.Text;
            if (newName.Length == 0 || newName == oldName) return;

            RenameDatabaseObjectNode(e.Node, oldName, newName);
        }

        private bool IsRenameableObjectNode(TreeNode node)
        {
            if (node == null) return false;
            var pathParts = GetTreePathParts(node);
            return pathParts.Length >= 4 && (pathParts[2] == "Tables" || pathParts[2] == "Views");
        }

        private void RenameDatabaseObjectNode(TreeNode node, string oldName, string newName)
        {
            DatabaseCopyItem item = BuildCopyItemFromNode(node);
            if (item == null)
            {
                MessageBox.Show(Localization.T("Object.SelectionConnectionUnavailable"), Localization.T("Tool.RenameObject"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                bool isView = item.ObjectKind == "view";
                if (isView ? item.Database.ViewExists(item.DatabaseName, newName) : item.Database.TableExists(item.DatabaseName, newName))
                {
                    MessageBox.Show(Localization.Format("Object.TargetNameExists", newName), Localization.T("Tool.RenameObject"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                Cursor = Cursors.WaitCursor;
                if (isView)
                    item.Database.RenameView(item.DatabaseName, oldName, newName);
                else
                    item.Database.RenameTable(item.DatabaseName, oldName, newName);

                node.Text = newName;
                db_tree.SelectedNode = node;
                UpdateMainStatus(Localization.Format("Object.RenamedStatus", item.ObjectKind, oldName, newName));
                db_tree_AfterSelect(db_tree, new TreeViewEventArgs(node));
            }
            catch (Exception ex)
            {
                UpdateMainStatus(Localization.Format("Object.RenameFailed", ex.Message));
                MessageBox.Show(Localization.Format("Object.RenameFailed", ex.Message), Localization.T("Tool.RenameObject"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void CopySelectedDatabaseObjectToInternalClipboard()
        {
            DatabaseCopyItem item = BuildCopyItemFromNode(db_tree.SelectedNode);
            if (item == null)
            {
                MessageBox.Show(Localization.T("Object.SelectTableOrView"), Localization.T("Tool.CopyObject"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _treeClipboardItem = item;
            UpdateMainStatus(Localization.Format("Object.CopiedStatus", item.ObjectKind, item.DatabaseName, item.ObjectName));
        }

        private async void PasteInternalClipboardToSelectedDatabase()
        {
            if (_treeClipboardItem == null)
            {
                MessageBox.Show(Localization.T("Object.NoCopiedObject"), Localization.T("Tool.PasteObject"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            TreeDatabaseTarget target = BuildTargetFromNode(db_tree.SelectedNode);
            if (target == null)
            {
                MessageBox.Show(Localization.T("Object.SelectCopyTarget"), Localization.T("Tool.PasteObject"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DatabaseCopyItem targetItem = new DatabaseCopyItem
            {
                Database = target.Database,
                DatabaseName = target.DatabaseName,
                ProviderName = target.ProviderName,
                ObjectKind = _treeClipboardItem.ObjectKind
            };

            // 跨 provider 複製 View 時，先詢問使用者複製方式
            ViewCopyFallback viewFallback = ViewCopyFallback.AutoSnapshot;
            bool isCrossProviderView = string.Equals(_treeClipboardItem.ObjectKind, "view", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(_treeClipboardItem.ProviderName, target.ProviderName, StringComparison.OrdinalIgnoreCase);
            if (isCrossProviderView)
            {
                ViewCopyFallback? choice = ShowViewCopyOptionsDialog(
                    _treeClipboardItem, targetItem);
                if (choice == null) return;
                viewFallback = choice.Value;
            }

            try
            {
                Cursor = Cursors.WaitCursor;
                var service = new DatabaseCopyService(1000);
                DatabaseCopyResult result = await Task.Run(() => service.Copy(_treeClipboardItem, targetItem, p =>
                {
                    if (!string.IsNullOrEmpty(p.Message)) UpdateMainStatus(p.Message);
                }, viewFallback));

                RefreshDatabaseObjectNodes(target.DatabaseNode);
                SelectObjectNode(target.DatabaseNode, result.ObjectKind, result.TargetName);
                UpdateMainStatus(result.ObjectKind == "table"
                    ? Localization.Format("Object.CopyCompletedRowsStatus", result.TargetName, result.CopiedRows)
                    : Localization.Format("Object.CopyCompletedStatus", result.TargetName));
            }
            catch (Exception ex)
            {
                UpdateMainStatus(Localization.Format("Object.CopyFailed", ex.Message));
                MessageBox.Show(Localization.Format("Object.CopyFailed", ex.Message), Localization.T("Tool.PasteObject"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private ViewCopyFallback? ShowViewCopyOptionsDialog(DatabaseCopyItem sourceItem, DatabaseCopyItem targetItem)
        {
            ViewCopyFallback selected = ViewCopyFallback.AutoSnapshot;
            ViewSqlConversionPreview preview = BuildViewCopyPreview(sourceItem, targetItem);
            using (Form dlg = new Form())
            {
                dlg.Text = Localization.T("Object.ViewCopyTitle");
                dlg.Size = new Size(760, 560);
                dlg.MinimumSize = new Size(680, 460);
                dlg.FormBorderStyle = FormBorderStyle.Sizable;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                ApplyModernTheme(dlg);

                var lbl = new Label
                {
                    Text = Localization.Format("Object.ViewCopyPrompt", sourceItem.ProviderName, sourceItem.ObjectName, targetItem.ProviderName),
                    Location = new Point(16, 14),
                    Size = new Size(700, 54),
                    Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
                    AutoSize = false
                };
                lbl.ForeColor = ThemeManager.TextColor;

                var rb1 = new RadioButton
                {
                    Text = Localization.T("Object.ViewCopyAutoConvert"),
                    Location = new Point(16, 72),
                    Size = new Size(700, 22),
                    Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
                    Checked = true,
                    ForeColor = ThemeManager.TextColor
                };
                var rb2 = new RadioButton
                {
                    Text = Localization.T("Object.ViewCopyForceSnapshot"),
                    Location = new Point(16, 98),
                    Size = new Size(700, 22),
                    Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
                    ForeColor = ThemeManager.TextColor
                };

                var tabs = new TabControl
                {
                    Location = new Point(16, 132),
                    Size = new Size(718, 330),
                    Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom
                };
                var sourcePage = new TabPage(Localization.T("Object.ViewCopySourceSql"));
                var convertedPage = new TabPage(Localization.T("Object.ViewCopyConvertedSql"));
                var sourceBox = CreateSqlPreviewBox(preview.SourceSql);
                var convertedText = preview.CanConvert
                    ? preview.ConvertedSql
                    : Localization.Format("Object.ViewCopyPreviewUnavailable", preview.Reason);
                var convertedBox = CreateSqlPreviewBox(convertedText);
                sourcePage.Controls.Add(sourceBox);
                convertedPage.Controls.Add(convertedBox);
                tabs.TabPages.Add(sourcePage);
                tabs.TabPages.Add(convertedPage);

                var btnOk = new Button
                {
                    Text = Localization.T("Common.OK"),
                    DialogResult = DialogResult.None,
                    Location = new Point(562, 480),
                    Size = new Size(80, 30),
                    Anchor = AnchorStyles.Right | AnchorStyles.Bottom
                };
                var btnCancel = new Button
                {
                    Text = Localization.T("Common.Cancel"),
                    DialogResult = DialogResult.Cancel,
                    Location = new Point(654, 480),
                    Size = new Size(80, 30),
                    Anchor = AnchorStyles.Right | AnchorStyles.Bottom
                };

                btnOk.Click += (s, e) =>
                {
                    selected = rb2.Checked ? ViewCopyFallback.ForceTableSnapshot : ViewCopyFallback.AutoSnapshot;
                    dlg.DialogResult = DialogResult.OK;
                };

                dlg.Controls.AddRange(new Control[] { lbl, rb1, rb2, tabs, btnOk, btnCancel });
                dlg.AcceptButton = btnOk;
                dlg.CancelButton = btnCancel;

                if (dlg.ShowDialog(this) != DialogResult.OK) return null;
            }
            return selected;
        }

        private ViewSqlConversionPreview BuildViewCopyPreview(DatabaseCopyItem sourceItem, DatabaseCopyItem targetItem)
        {
            try
            {
                string sourceSql = sourceItem.Database.GetViewCreateStatement(sourceItem.DatabaseName, sourceItem.ObjectName);
                return ViewSqlDialectConverter.BuildPreview(sourceSql, sourceItem.ProviderName, targetItem.ProviderName);
            }
            catch (Exception ex)
            {
                return new ViewSqlConversionPreview
                {
                    SourceSql = "",
                    ConvertedSql = "",
                    CanConvert = false,
                    Reason = ex.Message
                };
            }
        }

        private RichTextBox CreateSqlPreviewBox(string text)
        {
            RichTextBox box = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                WordWrap = false,
                ScrollBars = RichTextBoxScrollBars.Both,
                Font = new Font("Consolas", 10),
                Text = text ?? ""
            };
            box.BackColor = ThemeManager.TextBoxBackColor;
            box.ForeColor = ThemeManager.TextColor;
            return box;
        }

        private DatabaseCopyItem BuildCopyItemFromNode(TreeNode node)
        {
            if (node == null) return null;
            var pathParts = GetTreePathParts(node);
            if (pathParts.Length < 4) return null;
            if (pathParts[2] != "Tables" && pathParts[2] != "Views") return null;

            TreeNode root = node;
            while (root.Parent != null && !IsConnectionGroupNode(root.Parent)) root = root.Parent;
            int connIdxCopyItem = GetConnectionIndex(root);
            if (connIdxCopyItem < 0 || connIdxCopyItem >= myN.connections.Count) return null;
            var connInfo = myN.connections[connIdxCopyItem];
            if (connInfo["isConnect"].ToString() != "T" || !(connInfo["pdo"] is IDatabase db)) return null;

            return new DatabaseCopyItem
            {
                Database = db,
                DatabaseName = pathParts[1],
                ObjectName = pathParts[3],
                ObjectKind = pathParts[2] == "Views" ? "view" : "table",
                ProviderName = db.ProviderName
            };
        }

        private TreeDatabaseTarget BuildTargetFromNode(TreeNode node)
        {
            if (node == null) return null;
            var pathParts = GetTreePathParts(node);
            if (pathParts.Length < 2) return null;

            TreeNode root = node;
            while (root.Parent != null && !IsConnectionGroupNode(root.Parent)) root = root.Parent;
            int connIdxTarget = GetConnectionIndex(root);
            if (connIdxTarget < 0 || connIdxTarget >= myN.connections.Count) return null;
            var connInfo = myN.connections[connIdxTarget];
            if (connInfo["isConnect"].ToString() != "T" || !(connInfo["pdo"] is IDatabase db)) return null;

            TreeNode dbNode = node;
            while (dbNode.Parent != null && dbNode.Parent.Parent != null) dbNode = dbNode.Parent;
            if (dbNode.Parent == null) return null;

            return new TreeDatabaseTarget
            {
                Database = db,
                DatabaseName = dbNode.Text,
                ProviderName = db.ProviderName,
                DatabaseNode = dbNode,
                ConnectionInfo = connInfo
            };
        }

        private TreeDatabaseTarget GetTargetFromCurrentSelection()
        {
            TreeDatabaseTarget target = BuildTargetFromNode(db_tree.SelectedNode);
            if (target != null) return target;

            TreeNode databaseNode = GetSelectedDatabaseNode();
            return BuildTargetFromNode(databaseNode);
        }

	        private void RefreshDatabaseObjectNodes(TreeNode databaseNode)
	        {
	            if (databaseNode == null) return;
	            TreeNode root = databaseNode;
	            while (root.Parent != null && !IsConnectionGroupNode(root.Parent)) root = root.Parent;
	            int connIdxRefresh = GetConnectionIndex(root);
	            if (connIdxRefresh < 0 || connIdxRefresh >= myN.connections.Count) return;
	            IDatabase db = (IDatabase)myN.connections[connIdxRefresh]["pdo"];
	            databaseNode.Nodes.Clear();
	            _ = LoadDatabaseMetadataAsync(connIdxRefresh, databaseNode, db, databaseNode.Text);
	        }

        private void SelectObjectNode(TreeNode databaseNode, string objectKind, string objectName)
        {
            if (databaseNode == null) return;
            string groupName = objectKind == "view" ? "Views" : "Tables";
            TreeNode item = FindDatabaseObjectNode(databaseNode, groupName, objectName);
            if (item == null) return;
            db_tree.SelectedNode = item;
            item.EnsureVisible();
        }

        private void CloseConnection(int index)
        {
            var conn = myN.connections[index];
            if (conn["isConnect"].ToString() == "T")
            {
                if (conn.ContainsKey("pdo") && conn["pdo"] is IDisposable disp)
                {
                    try { disp.Dispose(); } catch { }
                }
                
                conn["isConnect"] = "F";
                conn["pdo"] = null;
                
                TreeNode root = FindConnectionNode(index);
                if (root != null)
                {
                    root.Nodes.Clear();
                    // 加回一個虛擬節點以便下次雙擊展開
                    root.Nodes.Add("loading...");
                    root.Collapse();
                }
                
                // 重設右側與中間面板
                table_top.DataSource = null;
                table_top.Visible = true;
                queryTabs.Visible = false;
                UpdateObjectFilterVisibility();
                lblSidebarTitle.Text = Localization.T("Sidebar.ObjectDetails");
                dgvDetails.DataSource = null;
                rtbDDL.Clear();

                Control[] navControls = this.Controls.Find("lblNav", true);
                if (navControls.Length > 0) ((Label)navControls[0]).Text = " " + Localization.T("Status.Ready");
                
                UpdateMainStatus(Localization.T("Status.ConnectionClosed"));
                if (SystemInformation.UserInteractive)
                {
                    MessageBox.Show(Localization.T("Status.ConnectionClosed"));
                }
            }
        }

        private TreeNode GetSelectedConnectionRoot()
        {
            TreeNode node = db_tree.SelectedNode;
            if (node == null) return null;

            // 向上走直到找到連線節點（跳過群組節點）
            while (node != null && IsConnectionGroupNode(node))
                node = node.Parent;
            if (node == null) return null;

            while (node.Parent != null && !IsConnectionGroupNode(node.Parent))
                node = node.Parent;

            return GetConnectionIndex(node) >= 0 ? node : null;
        }

        private void CloseSelectedTab()
        {
            if (queryTabs == null || queryTabs.SelectedTab == null)
            {
                UpdateMainStatus(Localization.T("Status.SelectTab"));
                return;
            }

            TabPage page = queryTabs.SelectedTab;
            TableDesignerForm designer = page.Controls.OfType<TableDesignerForm>().FirstOrDefault();
            if (designer != null && !designer.ConfirmClose())
            {
                return;
            }

            Form form = page.Tag as Form;
            if (form != null)
            {
                form.Close();
            }

            queryTabs.TabPages.Remove(page);
            if (queryTabs.TabPages.Count == 0) queryTabs.Visible = false;
            UpdateMainStatus(Localization.T("Status.TabClosed"));
        }

        private void ShowDatabaseObjectList(IDatabase db, string dbName)
        {
            try
            {
                DataTable dt = db.GetTableStatus(dbName);
                if (dt == null) return;

                // 轉換為顯示用的格式
                DataTable displayDt = new DataTable();
                displayDt.Columns.Add("名稱");
                displayDt.Columns.Add("自動遞增值");
                displayDt.Columns.Add("修改日期");
                displayDt.Columns.Add("資料長度");
                displayDt.Columns.Add("引擎");
                displayDt.Columns.Add("列");
                displayDt.Columns.Add("註解");

                bool includeHiddenObjects = ApplicationOptionSettings.GetBool("ViewShowHiddenItems");
                foreach (DataRow row in dt.Rows)
                {
                    string objectName = FirstColumnValue(row, "Name", "NAME", "TABLE_NAME");
                    if (!ObjectVisibilityService.IsVisibleName(db.ProviderName, "table", objectName, includeHiddenObjects)) continue;

                    DataRow newRow = displayDt.NewRow();
                    newRow["名稱"] = objectName;
                    newRow["自動遞增值"] = FirstColumnValue(row, "Auto_increment", "AUTO_INCREMENT");
                    newRow["修改日期"] = FirstColumnValue(row, "Update_time", "UPDATE_TIME");
                    newRow["資料長度"] = FormatBytesValue(FirstColumnValue(row, "Data_length", "DATA_LENGTH"));
                    newRow["引擎"] = FirstColumnValue(row, "Engine", "ENGINE");
                    string rowCount = FirstColumnValue(row, "Rows", "ROWS", "NUM_ROWS");
                    newRow["列"] = rowCount == "-1" ? "-" : rowCount;
                    newRow["註解"] = FirstColumnValue(row, "Comment", "COMMENTS");
                    displayDt.Rows.Add(newRow);
                }

                table_top.DataSource = displayDt;
                ApplyCurrentColumnPreferences("Tables");
                ApplyGridSortPreference();
            }
            catch { }
        }

        private void ShowDatabaseGroupList(IDatabase db, string dbName, string groupName, Dictionary<string, object> connInfo = null)
        {
            if (string.Equals(groupName, "Tables", StringComparison.OrdinalIgnoreCase))
            {
                ShowDatabaseObjectList(db, dbName);
                return;
            }

            DataTable displayDt = BuildDatabaseGroupList(db, dbName, groupName, connInfo);
            table_top.DataSource = displayDt;
            ApplyCurrentColumnPreferences(groupName);
            ApplyGridSortPreference();
            if (table_top.Columns.Contains("分頁索引"))
            {
                table_top.Columns["分頁索引"].Visible = false;
            }
        }

        private DataTable BuildDatabaseGroupList(IDatabase db, string dbName, string groupName, Dictionary<string, object> connInfo = null)
        {
            DataTable displayDt = new DataTable();
            displayDt.Columns.Add("名稱");
            displayDt.Columns.Add("類型");
            displayDt.Columns.Add("狀態");
            string ready = Localization.T("Diagnostic.StatusReady");

            if (string.Equals(groupName, "Views", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string viewName in GetViewsSafe(db, dbName))
                {
                    DataRow row = displayDt.NewRow();
                    row["名稱"] = viewName;
                    row["類型"] = Localization.T("DatabaseModel.ObjectTypeView");
                    row["狀態"] = ready;
                    displayDt.Rows.Add(row);
                }
            }
            else if (string.Equals(groupName, "Backups", StringComparison.OrdinalIgnoreCase))
            {
                displayDt.Columns.Add("路徑");
                DataRow row = displayDt.NewRow();
                row["名稱"] = dbName;
                row["類型"] = db is my_sqlite ? Localization.T("Common.SQLiteFile") : Localization.T("DatabaseGroup.TypeSqlDump");
                string sqlitePath = GetSQLiteDatabasePath(connInfo, db);
                row["狀態"] = db is my_sqlite && !File.Exists(sqlitePath) ? Localization.T("DatabaseGroup.StatusMissingSource") : ready;
                row["路徑"] = string.IsNullOrWhiteSpace(sqlitePath) ? Localization.T("DatabaseGroup.LogicalBackupPath") : sqlitePath;
                displayDt.Rows.Add(row);
            }
            else if (string.Equals(groupName, "Events", StringComparison.OrdinalIgnoreCase))
            {
                foreach (DataRow eventRow in GetDatabaseEvents(db, dbName).Rows)
                {
                    DataRow row = displayDt.NewRow();
                    row["名稱"] = eventRow["Name"];
                    row["類型"] = eventRow["Type"];
                    row["狀態"] = eventRow["Status"];
                    displayDt.Rows.Add(row);
                }
            }
            else if (string.Equals(groupName, "Functions", StringComparison.OrdinalIgnoreCase))
            {
                displayDt.Columns.Add("回傳型別");
                foreach (DataRow functionRow in GetDatabaseFunctions(db, dbName).Rows)
                {
                    DataRow row = displayDt.NewRow();
                    row["名稱"] = functionRow["Name"];
                    row["類型"] = functionRow["Type"];
                    row["狀態"] = functionRow["Status"];
                    row["回傳型別"] = functionRow["ReturnType"];
                    displayDt.Rows.Add(row);
                }
            }
            else if (string.Equals(groupName, "Users", StringComparison.OrdinalIgnoreCase))
            {
                displayDt.Columns.Add("主機");
                displayDt.Columns.Add("來源");
                foreach (DataRow userRow in GetDatabaseUsers(db, dbName, connInfo).Rows)
                {
                    DataRow row = displayDt.NewRow();
                    row["名稱"] = userRow["Name"];
                    row["類型"] = userRow["Type"];
                    row["狀態"] = userRow["Status"];
                    row["主機"] = userRow["Host"];
                    row["來源"] = userRow["Source"];
                    displayDt.Rows.Add(row);
                }
            }
            else if (string.Equals(groupName, "Models", StringComparison.OrdinalIgnoreCase))
            {
                displayDt.Columns.Add("描述");
                foreach (string modelName in DatabaseModelNames)
                {
                    DataRow row = displayDt.NewRow();
                    row["名稱"] = modelName;
                    row["類型"] = Localization.T("DatabaseGroup.TypeModel");
                    row["狀態"] = ready;
                    row["描述"] = GetDatabaseModelDescription(modelName);
                    displayDt.Rows.Add(row);
                }
            }
            else if (string.Equals(groupName, "BI", StringComparison.OrdinalIgnoreCase))
            {
                displayDt.Columns.Add("描述");
                foreach (string biName in DatabaseBIReportNames)
                {
                    DataRow row = displayDt.NewRow();
                    row["名稱"] = biName;
                    row["類型"] = Localization.T("DatabaseGroup.TypeBI");
                    row["狀態"] = ready;
                    row["描述"] = GetDatabaseBIDescription(biName);
                    displayDt.Rows.Add(row);
                }
            }
            else if (string.Equals(groupName, "Other", StringComparison.OrdinalIgnoreCase))
            {
                displayDt.Columns.Add("描述");
                foreach (string toolName in DatabaseOtherToolNames)
                {
                    DataRow row = displayDt.NewRow();
                    row["名稱"] = toolName;
                    row["類型"] = Localization.T("DatabaseGroup.TypeOther");
                    row["狀態"] = ready;
                    row["描述"] = GetDatabaseOtherToolDescription(toolName);
                    displayDt.Rows.Add(row);
                }
            }
            else if (string.Equals(groupName, "Queries", StringComparison.OrdinalIgnoreCase))
            {
                displayDt.Columns.Add("資料庫");
                displayDt.Columns.Add("編號");
                displayDt.Columns.Add("分頁索引");
                displayDt.Columns.Add("SQL");
                foreach (OpenQueryTabInfo queryInfo in GetOpenQueryTabs(dbName))
                {
                    DataRow row = displayDt.NewRow();
                    row["名稱"] = queryInfo.Page.Text;
                    row["類型"] = Localization.T("DatabaseGroup.TypeQuery");
                    row["狀態"] = string.IsNullOrWhiteSpace(queryInfo.Query.CurrentStatus) ? ready : queryInfo.Query.CurrentStatus;
                    row["資料庫"] = queryInfo.Query.DatabaseName;
                    row["編號"] = queryInfo.DisplayIndex;
                    row["分頁索引"] = queryInfo.TabIndex;
                    row["SQL"] = BuildQueryPreview(queryInfo.Query.CurrentSql);
                    displayDt.Rows.Add(row);
                }
            }
            else if (string.Equals(groupName, "Reports", StringComparison.OrdinalIgnoreCase))
            {
                displayDt.Columns.Add("描述");
                foreach (string reportName in DatabaseReportNames)
                {
                    DataRow row = displayDt.NewRow();
                    row["名稱"] = reportName;
                    row["類型"] = Localization.T("DatabaseGroup.TypeReport");
                    row["狀態"] = ready;
                    row["描述"] = GetDatabaseReportDescription(reportName);
                    displayDt.Rows.Add(row);
                }
            }
            else
            {
                DataRow row = displayDt.NewRow();
                row["名稱"] = groupName;
                row["類型"] = groupName;
                row["狀態"] = Localization.T("DatabaseGroup.StatusEmpty");
                displayDt.Rows.Add(row);
            }

            return displayDt;
        }

        private List<OpenQueryTabInfo> GetOpenQueryTabs(string databaseName)
        {
            List<OpenQueryTabInfo> tabs = new List<OpenQueryTabInfo>();
            if (queryTabs == null) return tabs;

            for (int i = 0; i < queryTabs.TabPages.Count; i++)
            {
                TabPage page = queryTabs.TabPages[i];
                QueryForm query = page.Tag as QueryForm;
                if (query == null) continue;
                if (!string.Equals(query.DatabaseName, databaseName ?? string.Empty, StringComparison.OrdinalIgnoreCase)) continue;

                tabs.Add(new OpenQueryTabInfo
                {
                    DisplayIndex = tabs.Count + 1,
                    TabIndex = i,
                    Page = page,
                    Query = query
                });
            }

            return tabs;
        }

        private static string BuildQueryPreview(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql)) return string.Empty;
            string preview = sql.Replace("\r", " ").Replace("\n", " ").Trim();
            while (preview.Contains("  ")) preview = preview.Replace("  ", " ");
            return preview.Length <= 120 ? preview : preview.Substring(0, 117) + "...";
        }

        public void RecordQueryHistory(string databaseName, string sql, string status, long elapsedMilliseconds, int rows, bool isQuery)
        {
            if (string.IsNullOrWhiteSpace(sql)) return;

            DiagnosticLogService.AppendQueryHistory(databaseName, sql, status, elapsedMilliseconds, rows, isQuery);

            _queryHistory.Insert(0, new QueryHistoryEntry
            {
                ExecutedAt = DateTime.Now,
                DatabaseName = databaseName ?? string.Empty,
                Sql = sql,
                Status = string.IsNullOrWhiteSpace(status) ? "OK" : status,
                Rows = rows,
                ElapsedMilliseconds = elapsedMilliseconds,
                IsQuery = isQuery
            });

            while (_queryHistory.Count > 200)
            {
                _queryHistory.RemoveAt(_queryHistory.Count - 1);
            }

            DataTable currentTable = table_top == null ? null : table_top.DataSource as DataTable;
            if (currentTable != null && currentTable.Columns.Contains("耗時(ms)"))
            {
                TreeDatabaseTarget target = BuildTargetFromNode(db_tree.SelectedNode);
                string activeDatabaseName = target == null ? databaseName : target.DatabaseName;
                table_top.DataSource = BuildQueryHistoryTable(activeDatabaseName);
            }
        }

        private DataTable BuildQueryHistoryTable(string databaseName)
        {
            DataTable dt = new DataTable();
            dt.Columns.Add("時間");
            dt.Columns.Add("資料庫");
            dt.Columns.Add("類型");
            dt.Columns.Add("狀態");
            dt.Columns.Add("列數");
            dt.Columns.Add("耗時(ms)");
            dt.Columns.Add("SQL");

            foreach (QueryHistoryEntry entry in _queryHistory)
            {
                if (!string.IsNullOrWhiteSpace(databaseName) &&
                    !string.Equals(entry.DatabaseName, databaseName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                DataRow row = dt.NewRow();
                row["時間"] = entry.ExecutedAt.ToString("yyyy-MM-dd HH:mm:ss");
                row["資料庫"] = entry.DatabaseName;
                row["類型"] = Localization.T(entry.IsQuery ? "Query.Query" : "Query.Command");
                row["狀態"] = entry.Status;
                row["列數"] = entry.Rows >= 0 ? entry.Rows.ToString() : "";
                row["耗時(ms)"] = entry.ElapsedMilliseconds.ToString();
                row["SQL"] = BuildQueryPreview(entry.Sql);
                dt.Rows.Add(row);
            }

            return dt;
        }

        private void RefreshQueriesGroupIfSelected()
        {
            if (db_tree.SelectedNode == null) return;
            var pathParts = GetTreePathParts(db_tree.SelectedNode);
            if (pathParts.Length < 3 || pathParts[2] != "Queries") return;

            TreeNode root = db_tree.SelectedNode;
            while (root.Parent != null && !IsConnectionGroupNode(root.Parent)) root = root.Parent;
            int connIdxQ = GetConnectionIndex(root);
            if (connIdxQ < 0 || connIdxQ >= myN.connections.Count) return;
            var connInfo = myN.connections[connIdxQ];
            if (connInfo["isConnect"].ToString() != "T" || !(connInfo["pdo"] is IDatabase db)) return;

            ShowDatabaseGroupList(db, pathParts[1], "Queries", connInfo);
        }

        private void ShowDatabaseReport(IDatabase db, string dbName, string reportName, Dictionary<string, object> connInfo = null)
        {
            table_top.DataSource = BuildDatabaseReport(db, dbName, reportName, connInfo);
            ShowReportDetails(db, dbName, reportName);
        }

        private DataTable BuildDatabaseReport(IDatabase db, string dbName, string reportName, Dictionary<string, object> connInfo = null)
        {
            if (string.Equals(reportName, "Table Row Counts", StringComparison.OrdinalIgnoreCase))
            {
                return BuildTableRowCountReport(db, dbName);
            }

            if (string.Equals(reportName, "Object Inventory", StringComparison.OrdinalIgnoreCase))
            {
                return BuildObjectInventoryReport(db, dbName, connInfo);
            }

            return BuildDatabaseSummaryReport(db, dbName, connInfo);
        }

        private DataTable BuildDatabaseSummaryReport(IDatabase db, string dbName, Dictionary<string, object> connInfo = null)
        {
            DataTable dt = new DataTable();
            dt.Columns.Add("項目");
            dt.Columns.Add("數值");

            List<string> tables = GetTablesSafe(db, dbName);
            List<string> views = GetViewsSafe(db, dbName);
            DataTable functions = GetDatabaseFunctions(db, dbName);
            DataTable events = GetDatabaseEvents(db, dbName);

            AddReportMetric(dt, "資料庫", dbName);
            AddReportMetric(dt, "Provider", db.ProviderName);
            AddReportMetric(dt, "資料表數", tables.Count.ToString());
            AddReportMetric(dt, "檢視數", views.Count.ToString());
            AddReportMetric(dt, "函式/程序數", functions.Rows.Count.ToString());
            AddReportMetric(dt, "事件/Trigger 數", events.Rows.Count.ToString());
            AddReportMetric(dt, "開啟中查詢分頁", GetOpenQueryTabs(dbName).Count.ToString());
            AddReportMetric(dt, "備份來源", GetBackupSourceDescription(db, connInfo));
            return dt;
        }

        private DataTable BuildTableRowCountReport(IDatabase db, string dbName)
        {
            DataTable dt = new DataTable();
            dt.Columns.Add("資料表");
            dt.Columns.Add("列數");
            dt.Columns.Add("狀態");
            string ready = Localization.T("Diagnostic.StatusReady");

            foreach (string tableName in GetTablesSafe(db, dbName))
            {
                DataRow row = dt.NewRow();
                row["資料表"] = tableName;
                try
                {
                    row["列數"] = db.CountRows(dbName, tableName).ToString();
                    row["狀態"] = ready;
                }
                catch (Exception ex)
                {
                    row["列數"] = "";
                    row["狀態"] = ex.Message;
                }
                dt.Rows.Add(row);
            }

            return dt;
        }

        private DataTable BuildObjectInventoryReport(IDatabase db, string dbName, Dictionary<string, object> connInfo = null)
        {
            DataTable dt = new DataTable();
            dt.Columns.Add("名稱");
            dt.Columns.Add("類型");
            dt.Columns.Add("狀態");
            string ready = Localization.T("Diagnostic.StatusReady");
            string tableType = Localization.T("DatabaseModel.ObjectTypeTable");
            string viewType = Localization.T("DatabaseModel.ObjectTypeView");

            foreach (string tableName in GetTablesSafe(db, dbName))
            {
                AddInventoryRow(dt, tableName, tableType, ready);
            }

            foreach (string viewName in GetViewsSafe(db, dbName))
            {
                AddInventoryRow(dt, viewName, viewType, ready);
            }

            foreach (DataRow row in GetDatabaseFunctions(db, dbName).Rows)
            {
                AddInventoryRow(dt, row["Name"].ToString(), row["Type"].ToString(), row["Status"].ToString());
            }

            foreach (DataRow row in GetDatabaseEvents(db, dbName).Rows)
            {
                AddInventoryRow(dt, row["Name"].ToString(), row["Type"].ToString(), row["Status"].ToString());
            }

            AddInventoryRow(dt, dbName, db is my_sqlite ? Localization.T("DatabaseReport.SqliteBackupSource") : Localization.T("DatabaseReport.SqlBackupTarget"), GetBackupSourceDescription(db, connInfo));
            return dt;
        }

        private static void AddReportMetric(DataTable dt, string name, string value)
        {
            DataRow row = dt.NewRow();
            row["項目"] = name;
            row["數值"] = value ?? string.Empty;
            dt.Rows.Add(row);
        }

        private static void AddInventoryRow(DataTable dt, string name, string type, string status)
        {
            DataRow row = dt.NewRow();
            row["名稱"] = name ?? string.Empty;
            row["類型"] = type ?? string.Empty;
            row["狀態"] = status ?? string.Empty;
            dt.Rows.Add(row);
        }

        private static string GetDatabaseReportDescription(string reportName)
        {
            if (string.Equals(reportName, "Table Row Counts", StringComparison.OrdinalIgnoreCase))
            {
                return "列出所有資料表目前列數";
            }

            if (string.Equals(reportName, "Object Inventory", StringComparison.OrdinalIgnoreCase))
            {
                return "彙整資料表、檢視、函式、事件與備份目標";
            }

            return "彙整資料庫物件數量與目前開啟狀態";
        }

        private static List<string> GetTablesSafe(IDatabase db, string dbName)
        {
            try
            {
                bool includeHiddenObjects = ApplicationOptionSettings.GetBool("ViewShowHiddenItems");
                return ObjectVisibilityService.FilterNames(db.GetTables(dbName), db.ProviderName, "table", includeHiddenObjects);
            }
            catch { return new List<string>(); }
        }

        private static List<string> GetViewsSafe(IDatabase db, string dbName)
        {
            try
            {
                bool includeHiddenObjects = ApplicationOptionSettings.GetBool("ViewShowHiddenItems");
                return ObjectVisibilityService.FilterNames(db.GetViews(dbName), db.ProviderName, "view", includeHiddenObjects);
            }
            catch { return new List<string>(); }
        }

        private static string GetBackupSourceDescription(IDatabase db, Dictionary<string, object> connInfo)
        {
            if (!(db is my_sqlite)) return "Logical SQL dump";
            string sqlitePath = GetSQLiteDatabasePath(connInfo, db);
            if (string.IsNullOrWhiteSpace(sqlitePath)) return "(unknown SQLite file)";
            return File.Exists(sqlitePath) ? sqlitePath : "Missing source: " + sqlitePath;
        }

        private void ShowDatabaseModel(IDatabase db, string dbName, string modelName)
        {
            table_top.DataSource = BuildDatabaseModel(db, dbName, modelName);
            ShowModelDetails(db, dbName, modelName);
        }

        private DataTable BuildDatabaseModel(IDatabase db, string dbName, string modelName)
        {
            if (string.Equals(modelName, "Column Catalog", StringComparison.OrdinalIgnoreCase))
            {
                return BuildColumnCatalogModel(db, dbName);
            }

            if (string.Equals(modelName, "Index Catalog", StringComparison.OrdinalIgnoreCase))
            {
                return BuildIndexCatalogModel(db, dbName);
            }

            return BuildSchemaOverviewModel(db, dbName);
        }

        private DataTable BuildSchemaOverviewModel(IDatabase db, string dbName)
        {
            DataTable dt = new DataTable();
            dt.Columns.Add("名稱");
            dt.Columns.Add("類型");
            dt.Columns.Add("欄位數");
            dt.Columns.Add("索引數");
            dt.Columns.Add("列數");
            dt.Columns.Add("狀態");
            string ready = Localization.T("Diagnostic.StatusReady");
            string tableType = Localization.T("DatabaseModel.ObjectTypeTable");
            string viewType = Localization.T("DatabaseModel.ObjectTypeView");

            foreach (string tableName in GetTablesSafe(db, dbName))
            {
                DataRow row = dt.NewRow();
                row["名稱"] = tableName;
                row["類型"] = tableType;
                row["欄位數"] = GetColumnsSafe(db, dbName, tableName).Rows.Count.ToString();
                row["索引數"] = GetDistinctIndexCount(GetIndexesSafe(db, dbName, tableName)).ToString();
                row["列數"] = GetObjectRowCountText(db, dbName, tableName);
                row["狀態"] = ready;
                dt.Rows.Add(row);
            }

            foreach (string viewName in GetViewsSafe(db, dbName))
            {
                DataRow row = dt.NewRow();
                row["名稱"] = viewName;
                row["類型"] = viewType;
                row["欄位數"] = GetColumnsSafe(db, dbName, viewName).Rows.Count.ToString();
                row["索引數"] = "0";
                row["列數"] = GetObjectRowCountText(db, dbName, viewName);
                row["狀態"] = ready;
                dt.Rows.Add(row);
            }

            return dt;
        }

        private DataTable BuildColumnCatalogModel(IDatabase db, string dbName)
        {
            DataTable dt = new DataTable();
            dt.Columns.Add("物件");
            dt.Columns.Add("類型");
            dt.Columns.Add("欄位");
            dt.Columns.Add("資料型別");
            dt.Columns.Add("允許空值");
            dt.Columns.Add("鍵");
            dt.Columns.Add("預設值");

            AppendColumnCatalogRows(dt, db, dbName, GetTablesSafe(db, dbName), Localization.T("DatabaseModel.ObjectTypeTable"));
            AppendColumnCatalogRows(dt, db, dbName, GetViewsSafe(db, dbName), Localization.T("DatabaseModel.ObjectTypeView"));
            return dt;
        }

        private void AppendColumnCatalogRows(DataTable target, IDatabase db, string dbName, IEnumerable<string> objectNames, string objectType)
        {
            foreach (string objectName in objectNames)
            {
                DataTable columns = GetColumnsSafe(db, dbName, objectName);
                foreach (DataRow column in columns.Rows)
                {
                    DataRow row = target.NewRow();
                    row["物件"] = objectName;
                    row["類型"] = objectType;
                    row["欄位"] = GetModelColumnName(column);
                    row["資料型別"] = GetModelColumnType(column);
                    row["允許空值"] = GetModelNullable(column);
                    row["鍵"] = GetModelKey(column);
                    row["預設值"] = GetModelDefault(column);
                    target.Rows.Add(row);
                }
            }
        }

        private DataTable BuildIndexCatalogModel(IDatabase db, string dbName)
        {
            DataTable dt = new DataTable();
            dt.Columns.Add("資料表");
            dt.Columns.Add("索引");
            dt.Columns.Add("欄位");
            dt.Columns.Add("唯一");
            dt.Columns.Add("順序");
            dt.Columns.Add("索引類型");

            foreach (string tableName in GetTablesSafe(db, dbName))
            {
                DataTable indexes = GetIndexesSafe(db, dbName, tableName);
                if (indexes.Rows.Count == 0)
                {
                    DataRow row = dt.NewRow();
                    row["資料表"] = tableName;
                    row["索引"] = Localization.T("DatabaseModel.NoExplicitIndexes");
                    row["欄位"] = "";
                    row["唯一"] = "";
                    row["順序"] = "";
                    row["索引類型"] = "";
                    dt.Rows.Add(row);
                    continue;
                }

                foreach (DataRow index in indexes.Rows)
                {
                    DataRow row = dt.NewRow();
                    row["資料表"] = tableName;
                    row["索引"] = GetModelIndexName(index);
                    row["欄位"] = GetModelIndexColumn(index);
                    row["唯一"] = GetModelIndexUnique(index);
                    row["順序"] = GetModelIndexSequence(index);
                    row["索引類型"] = GetModelIndexType(index);
                    dt.Rows.Add(row);
                }
            }

            return dt;
        }

        private static string GetDatabaseModelDescription(string modelName)
        {
            if (string.Equals(modelName, "Column Catalog", StringComparison.OrdinalIgnoreCase))
            {
                return "列出資料表與檢視欄位、型別、空值與鍵資訊";
            }

            if (string.Equals(modelName, "Index Catalog", StringComparison.OrdinalIgnoreCase))
            {
                return "列出資料表索引、欄位順序與唯一性";
            }

            return "彙整資料表與檢視的欄位數、索引數與列數";
        }

        private static DataTable GetColumnsSafe(IDatabase db, string dbName, string objectName)
        {
            try { return db.GetColumns(dbName, objectName) ?? new DataTable(); }
            catch { return new DataTable(); }
        }

        private static DataTable GetIndexesSafe(IDatabase db, string dbName, string tableName)
        {
            try { return db.GetIndexes(dbName, tableName) ?? new DataTable(); }
            catch { return new DataTable(); }
        }

        private static int GetDistinctIndexCount(DataTable indexes)
        {
            if (indexes == null || indexes.Rows.Count == 0) return 0;
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DataRow row in indexes.Rows)
            {
                string name = GetModelIndexName(row);
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            }
            return names.Count;
        }

        private static string GetObjectRowCountText(IDatabase db, string dbName, string objectName)
        {
            try { return db.CountRows(dbName, objectName).ToString(); }
            catch (Exception ex) { return ex.Message; }
        }

        private static string GetModelColumnName(DataRow row)
        {
            return FirstColumnValue(row, "Field", "name", "COLUMN_NAME", "column_name");
        }

        private static string GetModelColumnType(DataRow row)
        {
            return FirstColumnValue(row, "Type", "type", "DATA_TYPE", "data_type");
        }

        private static string GetModelNullable(DataRow row)
        {
            string explicitValue = FirstColumnValue(row, "Null", "IS_NULLABLE", "is_nullable", "NULLABLE");
            if (!string.IsNullOrWhiteSpace(explicitValue)) return explicitValue;

            string notNull = FirstColumnValue(row, "notnull");
            if (notNull == "1") return "NO";
            if (notNull == "0") return "YES";
            return "";
        }

        private static string GetModelKey(DataRow row)
        {
            string key = FirstColumnValue(row, "Key");
            if (!string.IsNullOrWhiteSpace(key)) return key;

            string pk = FirstColumnValue(row, "pk");
            if (pk == "1") return "PK";
            return "";
        }

        private static string GetModelDefault(DataRow row)
        {
            return FirstColumnValue(row, "Default", "dflt_value", "COLUMN_DEFAULT", "column_default", "DATA_DEFAULT");
        }

        private static string GetModelIndexName(DataRow row)
        {
            return FirstColumnValue(row, "Key_name", "KEY_NAME", "IndexName", "INDEX_NAME");
        }

        private static string GetModelIndexColumn(DataRow row)
        {
            return FirstColumnValue(row, "Column_name", "COLUMN_NAME", "ColumnName");
        }

        private static string GetModelIndexUnique(DataRow row)
        {
            string nonUnique = FirstColumnValue(row, "Non_unique", "NON_UNIQUE", "NonUnique");
            if (nonUnique == "0" || nonUnique.Equals("False", StringComparison.OrdinalIgnoreCase)) return "Yes";
            if (nonUnique == "1" || nonUnique.Equals("True", StringComparison.OrdinalIgnoreCase)) return "No";
            return "";
        }

        private static string GetModelIndexSequence(DataRow row)
        {
            return FirstColumnValue(row, "Seq_in_index", "SEQ_IN_INDEX", "SeqInIndex");
        }

        private static string GetModelIndexType(DataRow row)
        {
            return FirstColumnValue(row, "Index_type", "INDEX_TYPE", "IndexType");
        }

        private static string FirstColumnValue(DataRow row, params string[] names)
        {
            foreach (string name in names)
            {
                string value = GetColumnValue(row, name);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return string.Empty;
        }

        private void ShowDatabaseBIReport(IDatabase db, string dbName, string biName, Dictionary<string, object> connInfo = null)
        {
            table_top.DataSource = BuildDatabaseBIReport(db, dbName, biName, connInfo);
            ShowBIDetails(db, dbName, biName);
        }

        private DataTable BuildDatabaseBIReport(IDatabase db, string dbName, string biName, Dictionary<string, object> connInfo = null)
        {
            if (string.Equals(biName, "Table Size Summary", StringComparison.OrdinalIgnoreCase))
            {
                return BuildTableSizeSummaryBI(db, dbName);
            }

            if (string.Equals(biName, "Row Count Ranking", StringComparison.OrdinalIgnoreCase))
            {
                return BuildRowCountRankingBI(db, dbName);
            }

            return BuildObjectDistributionBI(db, dbName);
        }

        private DataTable BuildObjectDistributionBI(IDatabase db, string dbName)
        {
            DataTable dt = new DataTable();
            dt.Columns.Add("類別");
            dt.Columns.Add("數量", typeof(int));
            dt.Columns.Add("佔比");

            int tableCount = GetTablesSafe(db, dbName).Count;
            int viewCount = GetViewsSafe(db, dbName).Count;
            int functionCount = GetDatabaseFunctions(db, dbName).Rows.Count;
            int eventCount = GetDatabaseEvents(db, dbName).Rows.Count;
            int total = tableCount + viewCount + functionCount + eventCount;

            AddBIDistributionRow(dt, Localization.T("DatabaseBI.CategoryTables"), tableCount, total);
            AddBIDistributionRow(dt, Localization.T("DatabaseBI.CategoryViews"), viewCount, total);
            AddBIDistributionRow(dt, Localization.T("DatabaseBI.CategoryFunctions"), functionCount, total);
            AddBIDistributionRow(dt, Localization.T("DatabaseBI.CategoryEvents"), eventCount, total);
            return dt;
        }

        private DataTable BuildTableSizeSummaryBI(IDatabase db, string dbName)
        {
            DataTable dt = new DataTable();
            dt.Columns.Add("資料表");
            dt.Columns.Add("列數");
            dt.Columns.Add("資料長度");
            dt.Columns.Add("索引長度");
            dt.Columns.Add("引擎");
            dt.Columns.Add("註解");

            DataTable status = GetTableStatusSafe(db, dbName);
            foreach (DataRow source in status.Rows)
            {
                DataRow row = dt.NewRow();
                row["資料表"] = FirstColumnValue(source, "Name", "NAME", "TABLE_NAME");
                row["列數"] = FirstColumnValue(source, "Rows", "ROWS", "NUM_ROWS");
                row["資料長度"] = FormatBytesValue(FirstColumnValue(source, "Data_length", "DATA_LENGTH"));
                row["索引長度"] = FormatBytesValue(FirstColumnValue(source, "Index_length", "INDEX_LENGTH"));
                row["引擎"] = FirstColumnValue(source, "Engine", "ENGINE");
                row["註解"] = FirstColumnValue(source, "Comment", "COMMENTS");
                if (!string.IsNullOrWhiteSpace(row["資料表"].ToString())) dt.Rows.Add(row);
            }

            return dt;
        }

        private DataTable BuildRowCountRankingBI(IDatabase db, string dbName)
        {
            DataTable dt = new DataTable();
            dt.Columns.Add("排名", typeof(int));
            dt.Columns.Add("名稱");
            dt.Columns.Add("類型");
            dt.Columns.Add("列數", typeof(long));
            dt.Columns.Add("狀態");

            List<Tuple<string, string, long, string>> rows = new List<Tuple<string, string, long, string>>();
            string tableType = Localization.T("DatabaseModel.ObjectTypeTable");
            string viewType = Localization.T("DatabaseModel.ObjectTypeView");
            foreach (string tableName in GetTablesSafe(db, dbName))
            {
                rows.Add(GetBIObjectRowCount(db, dbName, tableName, tableType));
            }
            foreach (string viewName in GetViewsSafe(db, dbName))
            {
                rows.Add(GetBIObjectRowCount(db, dbName, viewName, viewType));
            }

            int rank = 1;
            foreach (Tuple<string, string, long, string> item in rows.OrderByDescending(r => r.Item3).ThenBy(r => r.Item1))
            {
                DataRow row = dt.NewRow();
                row["排名"] = rank++;
                row["名稱"] = item.Item1;
                row["類型"] = item.Item2;
                row["列數"] = item.Item3;
                row["狀態"] = item.Item4;
                dt.Rows.Add(row);
            }

            return dt;
        }

        private static void AddBIDistributionRow(DataTable dt, string name, int count, int total)
        {
            DataRow row = dt.NewRow();
            row["類別"] = name;
            row["數量"] = count;
            row["佔比"] = total <= 0 ? "0%" : Math.Round(count * 100m / total, 2).ToString() + "%";
            dt.Rows.Add(row);
        }

        private static DataTable GetTableStatusSafe(IDatabase db, string dbName)
        {
            try { return db.GetTableStatus(dbName) ?? new DataTable(); }
            catch { return new DataTable(); }
        }

        private Tuple<string, string, long, string> GetBIObjectRowCount(IDatabase db, string dbName, string objectName, string objectType)
        {
            try
            {
                return Tuple.Create(objectName, objectType, db.CountRows(dbName, objectName), Localization.T("Diagnostic.StatusReady"));
            }
            catch (Exception ex)
            {
                return Tuple.Create(objectName, objectType, 0L, ex.Message);
            }
        }

        private string FormatBytesValue(string value)
        {
            long bytes;
            if (!long.TryParse(value, out bytes)) return value ?? string.Empty;
            return FormatBytes(bytes);
        }

        private static string GetDatabaseBIDescription(string biName)
        {
            if (string.Equals(biName, "Table Size Summary", StringComparison.OrdinalIgnoreCase))
            {
                return "彙整資料表列數、資料長度、索引長度與引擎";
            }

            if (string.Equals(biName, "Row Count Ranking", StringComparison.OrdinalIgnoreCase))
            {
                return "依列數排序資料表與檢視";
            }

            return "統計資料庫物件類別分布";
        }

        private void ShowDatabaseOtherTool(IDatabase db, string dbName, string toolName, Dictionary<string, object> connInfo = null)
        {
            table_top.DataSource = BuildDatabaseOtherTool(db, dbName, toolName, connInfo);
            ShowOtherToolDetails(db, dbName, toolName);
        }

        private DataTable BuildDatabaseOtherTool(IDatabase db, string dbName, string toolName, Dictionary<string, object> connInfo = null)
        {
            if (string.Equals(toolName, "Provider Capabilities", StringComparison.OrdinalIgnoreCase))
            {
                return BuildProviderCapabilitiesTool(db, dbName);
            }

            if (string.Equals(toolName, "Maintenance Checklist", StringComparison.OrdinalIgnoreCase))
            {
                return BuildMaintenanceChecklistTool(db, dbName, connInfo);
            }

            return BuildConnectionDiagnosticsTool(db, dbName, connInfo);
        }

        private DataTable BuildConnectionDiagnosticsTool(IDatabase db, string dbName, Dictionary<string, object> connInfo = null)
        {
            DataTable dt = CreateOtherToolTable();
            string ready = Localization.T("Diagnostic.StatusReady");
            string warning = Localization.T("Diagnostic.StatusWarning");
            AddOtherToolRow(dt, Localization.T("ConnectionDiagnostics.Provider"), ready, db.ProviderName);
            AddOtherToolRow(dt, Localization.T("ConnectionDiagnostics.ConnectionState"), db.State == ConnectionState.Open ? ready : warning, db.State.ToString());
            AddOtherToolRow(dt, Localization.T("ConnectionDiagnostics.Database"), string.IsNullOrWhiteSpace(dbName) ? warning : ready, dbName);
            AddOtherToolRow(dt, Localization.T("ConnectionDiagnostics.Tables"), ready, GetTablesSafe(db, dbName).Count.ToString());
            AddOtherToolRow(dt, Localization.T("ConnectionDiagnostics.Views"), ready, GetViewsSafe(db, dbName).Count.ToString());
            AddOtherToolRow(dt, Localization.T("ConnectionDiagnostics.Functions"), ready, GetDatabaseFunctions(db, dbName).Rows.Count.ToString());
            AddOtherToolRow(dt, Localization.T("ConnectionDiagnostics.Events"), ready, GetDatabaseEvents(db, dbName).Rows.Count.ToString());
            AddOtherToolRow(dt, Localization.T("ConnectionDiagnostics.BackupSource"), ready, GetBackupSourceDescription(db, connInfo));
            if (db is my_sqlite sqlite)
            {
                AddSpatiaLiteDiagnosticsRows(dt, sqlite);
            }
            return dt;
        }

        private void AddSpatiaLiteDiagnosticsRows(DataTable dt, my_sqlite sqlite)
        {
            string runtimeDir = my_sqlite.GetSpatiaLiteRuntimeDir();
            string dllPath = Path.Combine(runtimeDir, "mod_spatialite.dll");
            string ready = Localization.T("Diagnostic.StatusReady");
            string warning = Localization.T("Diagnostic.StatusWarning");
            AddOtherToolRow(dt, Localization.T("ConnectionDiagnostics.SpatiaLiteRuntime"), Directory.Exists(runtimeDir) ? ready : warning, runtimeDir);
            AddOtherToolRow(dt, Localization.T("ConnectionDiagnostics.SpatiaLiteDll"), File.Exists(dllPath) ? ready : warning, dllPath);
            AddOtherToolRow(dt, Localization.T("ConnectionDiagnostics.SpatiaLiteLoaded"), sqlite.SpatiaLiteEnabled ? ready : warning, sqlite.SpatiaLiteEnabled ? Localization.T("ConnectionDiagnostics.ExtensionLoaded") : sqlite.SpatiaLiteLoadError);

            if (!sqlite.SpatiaLiteEnabled)
            {
                string repositoryRoot = SpatiaLiteRuntimeDiagnosticService.FindRepositoryRoot(AppDomain.CurrentDomain.BaseDirectory);
                foreach (SpatiaLiteDiagnosticRow row in SpatiaLiteRuntimeDiagnosticService.BuildRows(runtimeDir, sqlite.SpatiaLiteLoadError, repositoryRoot))
                {
                    AddOtherToolRow(dt, row.Item, row.Status, row.Detail);
                }
                return;
            }

            try
            {
                DataTable version = sqlite.SelectSQL("SELECT spatialite_version() AS Version");
                string versionText = version.Rows.Count > 0 ? version.Rows[0]["Version"].ToString() : string.Empty;
                AddOtherToolRow(dt, Localization.T("ConnectionDiagnostics.SpatiaLiteVersion"), string.IsNullOrWhiteSpace(versionText) ? warning : ready, versionText);
            }
            catch (Exception ex)
            {
                AddOtherToolRow(dt, Localization.T("ConnectionDiagnostics.SpatiaLiteVersion"), warning, ex.Message);
            }
        }

        private DataTable BuildProviderCapabilitiesTool(IDatabase db, string dbName)
        {
            DataTable dt = CreateOtherToolTable();
            string supported = Localization.T("ProviderCapability.Supported");
            AddOtherToolRow(dt, Localization.T("ProviderCapability.Tables"), supported, Localization.Format("ProviderCapability.LoadedCount", GetTablesSafe(db, dbName).Count));
            AddOtherToolRow(dt, Localization.T("ProviderCapability.Views"), supported, Localization.Format("ProviderCapability.LoadedCount", GetViewsSafe(db, dbName).Count));
            AddOtherToolRow(dt, Localization.T("ProviderCapability.EditableTableData"), supported, Localization.T("ProviderCapability.QueryFormTableMode"));
            AddOtherToolRow(dt, Localization.T("ProviderCapability.SqlImport"), supported, Localization.T("ProviderCapability.ExecSqlPipeline"));
            AddOtherToolRow(dt, Localization.T("ProviderCapability.SqlExport"), supported, Localization.T("ProviderCapability.DumpSqlTool"));
            AddOtherToolRow(dt, Localization.T("ProviderCapability.Backup"), supported, db is my_sqlite ? Localization.T("ProviderCapability.SqliteFileCopy") : Localization.T("ProviderCapability.LogicalSqlDump"));
            AddOtherToolRow(dt,
                Localization.T("ProviderCapability.StoredFunctions"),
                db is my_sqlite ? Localization.T("ProviderCapability.Unavailable") : supported,
                db is my_sqlite ? Localization.T("ProviderCapability.SqliteNoRoutines") : Localization.T("ProviderCapability.RoutineMetadataPermission"));
            AddOtherToolRow(dt, Localization.T("ProviderCapability.TriggersEvents"), supported, Localization.T("ProviderCapability.MetadataPermission"));
            return dt;
        }

        private DataTable BuildMaintenanceChecklistTool(IDatabase db, string dbName, Dictionary<string, object> connInfo = null)
        {
            DataTable dt = CreateOtherToolTable();
            List<string> tables = GetTablesSafe(db, dbName);
            List<string> views = GetViewsSafe(db, dbName);
            string ok = Localization.T("Diagnostic.StatusOk");
            string warning = Localization.T("Diagnostic.StatusWarning");
            string info = Localization.T("Diagnostic.StatusInfo");
            string backupSource = GetBackupSourceDescription(db, connInfo);
            int openQueryTabCount = GetOpenQueryTabs(dbName).Count;

            AddOtherToolRow(dt, Localization.T("MaintenanceChecklist.ConnectionOpen"), db.State == ConnectionState.Open ? ok : warning, db.State.ToString());
            AddOtherToolRow(dt, Localization.T("MaintenanceChecklist.TablesLoaded"), tables.Count > 0 ? ok : warning, Localization.Format("MaintenanceChecklist.TableCount", tables.Count));
            AddOtherToolRow(dt, Localization.T("MaintenanceChecklist.ViewsLoaded"), views.Count > 0 ? ok : info, Localization.Format("MaintenanceChecklist.ViewCount", views.Count));
            AddOtherToolRow(dt, Localization.T("MaintenanceChecklist.BackupTarget"), string.IsNullOrWhiteSpace(backupSource) ? warning : ok, backupSource);
            AddOtherToolRow(dt, Localization.T("MaintenanceChecklist.OpenQueryTabs"), openQueryTabCount > 0 ? info : ok, Localization.Format("MaintenanceChecklist.TabCount", openQueryTabCount));
            AddOtherToolRow(dt, Localization.T("MaintenanceChecklist.LargestTable"), info, GetLargestTableDescription(db, dbName, tables));
            return dt;
        }

        private static DataTable CreateOtherToolTable()
        {
            DataTable dt = new DataTable();
            dt.Columns.Add("項目");
            dt.Columns.Add("狀態");
            dt.Columns.Add("說明");
            return dt;
        }

        private static void AddOtherToolRow(DataTable dt, string item, string status, string detail)
        {
            DataRow row = dt.NewRow();
            row["項目"] = item;
            row["狀態"] = status;
            row["說明"] = detail ?? string.Empty;
            dt.Rows.Add(row);
        }

        private string GetLargestTableDescription(IDatabase db, string dbName, List<string> tables)
        {
            if (tables == null || tables.Count == 0) return Localization.T("MaintenanceChecklist.NoTables");

            string largestName = "";
            long largestRows = -1;
            foreach (string tableName in tables)
            {
                try
                {
                    long rows = db.CountRows(dbName, tableName);
                    if (rows > largestRows)
                    {
                        largestName = tableName;
                        largestRows = rows;
                    }
                }
                catch { }
            }

            return largestRows < 0
                ? Localization.T("MaintenanceChecklist.Unavailable")
                : Localization.Format("MaintenanceChecklist.LargestTableRows", largestName, largestRows);
        }

        private static string GetDatabaseOtherToolDescription(string toolName)
        {
            if (string.Equals(toolName, "Provider Capabilities", StringComparison.OrdinalIgnoreCase))
            {
                return "列出目前資料庫 Provider 可用功能";
            }

            if (string.Equals(toolName, "Maintenance Checklist", StringComparison.OrdinalIgnoreCase))
            {
                return "彙整連線、備份、查詢分頁與資料表狀態";
            }

            return "檢查連線狀態、物件數量與備份來源";
        }

        private DataTable GetDatabaseEvents(IDatabase db, string dbName)
        {
            DataTable events = CreateDatabaseEventTable();

            if (db is my_sqlite)
            {
                AppendDatabaseEventsFromQuery(events, db,
                    "SELECT name AS Name, 'Trigger' AS Type, tbl_name AS Status, COALESCE(sql, '') AS DDL " +
                    "FROM sqlite_master WHERE type='trigger' ORDER BY name;");
                return events;
            }

            if (db is my_mysql)
            {
                string safeDb = EscapeSqlLiteral(dbName);
                AppendDatabaseEventsFromQuery(events, db, BuildMySqlEventMetadataSql(safeDb));
                if (CountDatabaseEvents(events, "Event") == 0)
                {
                    AppendDatabaseEventsFromQuery(events, db,
                        "SELECT EVENT_NAME AS Name, 'Event' AS Type, STATUS AS Status, EVENT_DEFINITION AS DDL " +
                        "FROM information_schema.EVENTS WHERE EVENT_SCHEMA='" + safeDb + "' ORDER BY EVENT_NAME;");
                }

                AppendDatabaseEventsFromQuery(events, db, BuildMySqlTriggerMetadataSql(safeDb));
                if (CountDatabaseEvents(events, "Trigger") == 0)
                {
                    AppendDatabaseEventsFromQuery(events, db,
                        "SELECT TRIGGER_NAME AS Name, 'Trigger' AS Type, EVENT_MANIPULATION AS Status, ACTION_STATEMENT AS DDL " +
                        "FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA='" + safeDb + "' ORDER BY TRIGGER_NAME;");
                }
                return events;
            }

            if (db is my_postgresql)
            {
                AppendDatabaseEventsFromQuery(events, db, BuildPostgreSqlTriggerMetadataSql());
                if (events.Rows.Count == 0)
                {
                    AppendDatabaseEventsFromQuery(events, db, BuildPostgreSqlTriggerInformationSchemaSql());
                }
                return events;
            }

            if (db is my_mssql)
            {
                AppendDatabaseEventsFromQuery(events, db,
                    "SELECT t.name AS [Name], 'Trigger' AS [Type], CASE WHEN t.is_disabled = 1 THEN 'DISABLED' ELSE 'ENABLED' END AS [Status], " +
                    "COALESCE(OBJECT_DEFINITION(t.object_id), '') AS [DDL] FROM [" + EscapeSqlServerName(dbName) + "].sys.triggers t ORDER BY t.name;");
                return events;
            }

            if (db is my_oracle)
            {
                AppendDatabaseEventsFromQuery(events, db, BuildOracleTriggerMetadataSql(dbName));
                if (events.Rows.Count == 0)
                {
                    string owner = EscapeSqlLiteral((dbName ?? string.Empty).ToUpperInvariant());
                    AppendDatabaseEventsFromQuery(events, db,
                        "SELECT TRIGGER_NAME AS Name, 'Trigger' AS Type, STATUS AS Status, TRIGGER_BODY AS DDL " +
                        "FROM ALL_TRIGGERS WHERE OWNER='" + owner + "' ORDER BY TRIGGER_NAME");
                }
            }

            return events;
        }

        private static string BuildMySqlEventMetadataSql(string safeDb)
        {
            return "SELECT EVENT_NAME AS Name, 'Event' AS Type, STATUS AS Status, " +
                   "CONCAT('CREATE DEFINER=', DEFINER, ' EVENT `', REPLACE(EVENT_SCHEMA, '`', '``'), '`.`', REPLACE(EVENT_NAME, '`', '``'), '` ON SCHEDULE ', " +
                   "CASE WHEN EVENT_TYPE = 'ONE TIME' THEN CONCAT('AT ', IFNULL(DATE_FORMAT(EXECUTE_AT, '%Y-%m-%d %H:%i:%s'), 'CURRENT_TIMESTAMP')) " +
                   "ELSE CONCAT('EVERY ', IFNULL(INTERVAL_VALUE, '1'), ' ', IFNULL(INTERVAL_FIELD, 'DAY'), " +
                   "IF(STARTS IS NULL, '', CONCAT(' STARTS ''', DATE_FORMAT(STARTS, '%Y-%m-%d %H:%i:%s'), '''')), " +
                   "IF(ENDS IS NULL, '', CONCAT(' ENDS ''', DATE_FORMAT(ENDS, '%Y-%m-%d %H:%i:%s'), ''''))) END, " +
                   "CASE WHEN ON_COMPLETION = 'PRESERVE' THEN ' ON COMPLETION PRESERVE' ELSE ' ON COMPLETION NOT PRESERVE' END, ' ', " +
                   "CASE STATUS WHEN 'ENABLED' THEN 'ENABLE' WHEN 'DISABLED' THEN 'DISABLE' WHEN 'SLAVESIDE_DISABLED' THEN 'DISABLE ON SLAVE' ELSE STATUS END, " +
                   "IF(EVENT_COMMENT IS NULL OR EVENT_COMMENT = '', '', CONCAT(' COMMENT ', QUOTE(EVENT_COMMENT))), ' DO ', EVENT_DEFINITION) AS DDL " +
                   "FROM information_schema.EVENTS WHERE EVENT_SCHEMA='" + safeDb + "' ORDER BY EVENT_NAME;";
        }

        private static string BuildMySqlTriggerMetadataSql(string safeDb)
        {
            return "SELECT TRIGGER_NAME AS Name, 'Trigger' AS Type, EVENT_MANIPULATION AS Status, " +
                   "CONCAT('CREATE DEFINER=', DEFINER, ' TRIGGER `', REPLACE(TRIGGER_SCHEMA, '`', '``'), '`.`', REPLACE(TRIGGER_NAME, '`', '``'), '` ', " +
                   "ACTION_TIMING, ' ', EVENT_MANIPULATION, ' ON `', REPLACE(EVENT_OBJECT_SCHEMA, '`', '``'), '`.`', REPLACE(EVENT_OBJECT_TABLE, '`', '``'), " +
                   "'` FOR EACH ', ACTION_ORIENTATION, ' ', ACTION_STATEMENT) AS DDL " +
                   "FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA='" + safeDb + "' ORDER BY TRIGGER_NAME;";
        }

        private static int CountDatabaseEvents(DataTable events, string type)
        {
            if (events == null || !events.Columns.Contains("Type")) return 0;
            return events.Rows.Cast<DataRow>().Count(row => string.Equals(row["Type"].ToString(), type, StringComparison.OrdinalIgnoreCase));
        }

        private static string BuildPostgreSqlTriggerMetadataSql()
        {
            return "SELECT CASE WHEN n.nspname = 'public' THEN t.tgname ELSE n.nspname || '.' || t.tgname END AS \"Name\", 'Trigger' AS \"Type\", " +
                   "CASE WHEN t.tgenabled = 'D' THEN 'DISABLED' ELSE 'ENABLED' END AS \"Status\", " +
                   "pg_catalog.pg_get_triggerdef(t.oid, true) AS \"DDL\" " +
                   "FROM pg_catalog.pg_trigger t " +
                   "INNER JOIN pg_catalog.pg_class c ON c.oid = t.tgrelid " +
                   "INNER JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace " +
                   "WHERE NOT t.tgisinternal AND n.nspname NOT IN ('pg_catalog', 'information_schema') ORDER BY n.nspname, t.tgname;";
        }

        private static string BuildPostgreSqlTriggerInformationSchemaSql()
        {
            return "SELECT CASE WHEN trigger_schema = 'public' THEN trigger_name ELSE trigger_schema || '.' || trigger_name END AS \"Name\", " +
                   "'Trigger' AS \"Type\", event_manipulation AS \"Status\", action_statement AS \"DDL\" " +
                   "FROM information_schema.triggers WHERE trigger_schema NOT IN ('pg_catalog', 'information_schema') ORDER BY trigger_schema, trigger_name;";
        }

        private static string BuildOracleTriggerMetadataSql(string ownerName)
        {
            string owner = EscapeSqlLiteral((ownerName ?? string.Empty).ToUpperInvariant());
            return "SELECT TRIGGER_NAME AS Name, 'Trigger' AS Type, STATUS AS Status, " +
                   "DBMS_METADATA.GET_DDL('TRIGGER', TRIGGER_NAME, OWNER) AS DDL " +
                   "FROM ALL_TRIGGERS WHERE OWNER='" + owner + "' ORDER BY TRIGGER_NAME";
        }

        private DataTable GetDatabaseFunctions(IDatabase db, string dbName)
        {
            DataTable functions = CreateDatabaseFunctionTable();

            if (db is my_sqlite)
            {
                return functions;
            }

            if (db is my_mysql)
            {
                string safeDb = EscapeSqlLiteral(dbName);
                AppendDatabaseFunctionsFromQuery(functions, db,
                    "SELECT ROUTINE_NAME AS Name, ROUTINE_TYPE AS Type, COALESCE(DATA_TYPE, '') AS ReturnType, IS_DETERMINISTIC AS Status, COALESCE(ROUTINE_DEFINITION, '') AS DDL " +
                    "FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA='" + safeDb + "' ORDER BY ROUTINE_TYPE, ROUTINE_NAME;");
                PopulateMySqlRoutineCreateStatements(functions, db, dbName);
                return functions;
            }

            if (db is my_postgresql)
            {
                AppendDatabaseFunctionsFromQuery(functions, db, BuildPostgreSqlRoutineMetadataSql());
                return functions;
            }

            if (db is my_mssql)
            {
                AppendDatabaseFunctionsFromQuery(functions, db,
                    "SELECT o.name AS [Name], CASE WHEN o.type = 'P' THEN 'Procedure' ELSE 'Function' END AS [Type], '' AS [ReturnType], o.type_desc AS [Status], " +
                    "COALESCE(OBJECT_DEFINITION(o.object_id), '') AS [DDL] FROM [" + EscapeSqlServerName(dbName) + "].sys.objects o " +
                    "WHERE o.type IN ('FN','IF','TF','FS','FT','P') ORDER BY o.type_desc, o.name;");
                return functions;
            }

            if (db is my_oracle)
            {
                AppendDatabaseFunctionsFromQuery(functions, db, BuildOracleRoutineMetadataSql(dbName));
                if (functions.Rows.Count == 0)
                {
                    string owner = EscapeSqlLiteral((dbName ?? string.Empty).ToUpperInvariant());
                    AppendDatabaseFunctionsFromQuery(functions, db,
                        "SELECT OBJECT_NAME AS Name, OBJECT_TYPE AS Type, '' AS ReturnType, STATUS AS Status, '' AS DDL " +
                        "FROM ALL_OBJECTS WHERE OWNER='" + owner + "' AND OBJECT_TYPE IN ('FUNCTION','PROCEDURE') ORDER BY OBJECT_TYPE, OBJECT_NAME");
                }
            }

            return functions;
        }

        private static void PopulateMySqlRoutineCreateStatements(DataTable functions, IDatabase db, string dbName)
        {
            if (functions == null || db == null) return;

            foreach (DataRow row in functions.Rows)
            {
                string routineName = row["Name"].ToString();
                string routineType = row["Type"].ToString();
                if (string.IsNullOrWhiteSpace(routineName)) continue;

                try
                {
                    DataTable createTable = db.SelectSQL(BuildMySqlShowCreateRoutineSql(dbName, routineName, routineType));
                    if (createTable == null || createTable.Rows.Count == 0) continue;

                    string createSql = FirstColumnValue(createTable.Rows[0], "Create Function", "Create Procedure");
                    if (!string.IsNullOrWhiteSpace(createSql))
                    {
                        row["DDL"] = createSql;
                    }
                }
                catch
                {
                    // Keep ROUTINE_DEFINITION as a fallback when SHOW CREATE is hidden by permissions.
                }
            }
        }

        private static string BuildMySqlShowCreateRoutineSql(string databaseName, string routineName, string routineType)
        {
            string kind = IsProcedureRoutine(routineType) ? "PROCEDURE" : "FUNCTION";
            return "SHOW CREATE " + kind + " " + QuoteMySqlIdentifier(databaseName) + "." + QuoteMySqlIdentifier(routineName) + ";";
        }

        private static string BuildPostgreSqlRoutineMetadataSql()
        {
            return "SELECT CASE WHEN n.nspname = 'public' THEN p.proname ELSE n.nspname || '.' || p.proname END AS \"Name\", " +
                   "CASE WHEN p.prokind = 'p' THEN 'Procedure' ELSE 'Function' END AS \"Type\", " +
                   "COALESCE(pg_catalog.pg_get_function_result(p.oid), '') AS \"ReturnType\", " +
                   "COALESCE(l.lanname, '') AS \"Status\", pg_catalog.pg_get_functiondef(p.oid) AS \"DDL\" " +
                   "FROM pg_catalog.pg_proc p INNER JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace " +
                   "LEFT JOIN pg_catalog.pg_language l ON l.oid = p.prolang " +
                   "WHERE n.nspname NOT IN ('pg_catalog', 'information_schema') ORDER BY n.nspname, p.proname;";
        }

        private static string BuildOracleRoutineMetadataSql(string ownerName)
        {
            string owner = EscapeSqlLiteral((ownerName ?? string.Empty).ToUpperInvariant());
            return "SELECT o.OBJECT_NAME AS Name, " +
                   "CASE WHEN o.OBJECT_TYPE = 'PROCEDURE' THEN 'Procedure' ELSE 'Function' END AS Type, " +
                   "'' AS ReturnType, o.STATUS AS Status, " +
                   "(SELECT RTRIM(XMLAGG(XMLELEMENT(e, s.TEXT) ORDER BY s.LINE).EXTRACT('//text()').GETCLOBVAL()) " +
                   "FROM ALL_SOURCE s WHERE s.OWNER = o.OWNER AND s.NAME = o.OBJECT_NAME AND s.TYPE = o.OBJECT_TYPE) AS DDL " +
                   "FROM ALL_OBJECTS o WHERE o.OWNER='" + owner + "' AND o.OBJECT_TYPE IN ('FUNCTION','PROCEDURE') ORDER BY o.OBJECT_TYPE, o.OBJECT_NAME";
        }

        private DataTable GetDatabaseUsers(IDatabase db, string dbName, Dictionary<string, object> connInfo = null)
        {
            DataTable users = CreateDatabaseUserTable();

            if (db is my_sqlite)
            {
                DataRow row = users.NewRow();
                row["Name"] = "SQLite connection";
                row["Type"] = "Connection";
                row["Host"] = GetSQLiteDatabasePath(connInfo, db);
                row["Status"] = "SQLite has no database users";
                row["Source"] = "SQLite file";
                users.Rows.Add(row);
                return users;
            }

            if (db is my_mysql)
            {
                AppendDatabaseUsersFromQuery(users, db,
                    "SELECT User AS Name, 'User' AS Type, Host AS Host, " +
                    "CASE WHEN account_locked = 'Y' THEN 'Locked' ELSE 'Open' END AS Status, 'mysql.user' AS Source " +
                    "FROM mysql.user ORDER BY User, Host;");
                if (users.Rows.Count == 0)
                {
                    AppendDatabaseUsersFromQuery(users, db,
                        "SELECT CURRENT_USER() AS Name, 'Current User' AS Type, '' AS Host, 'Active' AS Status, 'CURRENT_USER()' AS Source;");
                }
                return users;
            }

            if (db is my_postgresql)
            {
                AppendDatabaseUsersFromQuery(users, db,
                    "SELECT rolname AS \"Name\", CASE WHEN rolsuper THEN 'Superuser' ELSE 'Role' END AS \"Type\", '' AS \"Host\", " +
                    "CASE WHEN rolcanlogin THEN 'Login' ELSE 'No login' END AS \"Status\", 'pg_roles' AS \"Source\" " +
                    "FROM pg_roles ORDER BY rolname;");
                return users;
            }

            if (db is my_mssql)
            {
                AppendDatabaseUsersFromQuery(users, db,
                    "SELECT name AS [Name], type_desc AS [Type], '' AS [Host], authentication_type_desc AS [Status], " +
                    "'sys.database_principals' AS [Source] FROM [" + EscapeSqlServerName(dbName) + "].sys.database_principals " +
                    "WHERE type NOT IN ('A','G','R','X') AND name NOT LIKE '##%' ORDER BY name;");
                return users;
            }

            if (db is my_oracle)
            {
                AppendDatabaseUsersFromQuery(users, db,
                    "SELECT USERNAME AS Name, 'User' AS Type, '' AS Host, ACCOUNT_STATUS AS Status, 'ALL_USERS' AS Source " +
                    "FROM ALL_USERS ORDER BY USERNAME");
            }

            return users;
        }

        private static DataTable CreateDatabaseEventTable()
        {
            DataTable dt = new DataTable();
            dt.Columns.Add("Name");
            dt.Columns.Add("Type");
            dt.Columns.Add("Status");
            dt.Columns.Add("DDL");
            return dt;
        }

        private static DataTable CreateDatabaseFunctionTable()
        {
            DataTable dt = new DataTable();
            dt.Columns.Add("Name");
            dt.Columns.Add("Type");
            dt.Columns.Add("ReturnType");
            dt.Columns.Add("Status");
            dt.Columns.Add("DDL");
            return dt;
        }

        private static DataTable CreateDatabaseUserTable()
        {
            DataTable dt = new DataTable();
            dt.Columns.Add("Name");
            dt.Columns.Add("Type");
            dt.Columns.Add("Host");
            dt.Columns.Add("Status");
            dt.Columns.Add("Source");
            return dt;
        }

        private static void AppendDatabaseEventsFromQuery(DataTable target, IDatabase db, string sql)
        {
            try
            {
                AppendDatabaseEvents(target, db.SelectSQL(sql));
            }
            catch
            {
                // Metadata visibility differs by provider and permissions. Keep the group usable when one source is unavailable.
            }
        }

        private static void AppendDatabaseEvents(DataTable target, DataTable source)
        {
            if (source == null) return;
            foreach (DataRow sourceRow in source.Rows)
            {
                DataRow row = target.NewRow();
                row["Name"] = GetColumnValue(sourceRow, "Name");
                row["Type"] = GetColumnValue(sourceRow, "Type");
                row["Status"] = GetColumnValue(sourceRow, "Status");
                row["DDL"] = GetColumnValue(sourceRow, "DDL");
                if (row["Name"].ToString().Length > 0) target.Rows.Add(row);
            }
        }

        private static void AppendDatabaseFunctionsFromQuery(DataTable target, IDatabase db, string sql)
        {
            try
            {
                AppendDatabaseFunctions(target, db.SelectSQL(sql));
            }
            catch
            {
                // Routine metadata can be hidden by provider permissions. Keep the Functions group usable.
            }
        }

        private static void AppendDatabaseUsersFromQuery(DataTable target, IDatabase db, string sql)
        {
            try
            {
                AppendDatabaseUsers(target, db.SelectSQL(sql));
            }
            catch
            {
                // User catalogs vary by provider permissions. Keep the Users group usable.
            }
        }

        private static void AppendDatabaseFunctions(DataTable target, DataTable source)
        {
            if (source == null) return;
            foreach (DataRow sourceRow in source.Rows)
            {
                DataRow row = target.NewRow();
                row["Name"] = GetColumnValue(sourceRow, "Name");
                row["Type"] = GetColumnValue(sourceRow, "Type");
                row["ReturnType"] = GetColumnValue(sourceRow, "ReturnType");
                row["Status"] = GetColumnValue(sourceRow, "Status");
                row["DDL"] = GetColumnValue(sourceRow, "DDL");
                if (row["Name"].ToString().Length > 0) target.Rows.Add(row);
            }
        }

        private static void AppendDatabaseUsers(DataTable target, DataTable source)
        {
            if (source == null) return;
            foreach (DataRow sourceRow in source.Rows)
            {
                DataRow row = target.NewRow();
                row["Name"] = GetColumnValue(sourceRow, "Name");
                row["Type"] = GetColumnValue(sourceRow, "Type");
                row["Host"] = GetColumnValue(sourceRow, "Host");
                row["Status"] = GetColumnValue(sourceRow, "Status");
                row["Source"] = GetColumnValue(sourceRow, "Source");
                if (row["Name"].ToString().Length > 0) target.Rows.Add(row);
            }
        }

        private static string GetColumnValue(DataRow row, string name)
        {
            if (row.Table.Columns.Contains(name) && row[name] != DBNull.Value) return row[name].ToString();
            foreach (DataColumn column in row.Table.Columns)
            {
                if (string.Equals(column.ColumnName, name, StringComparison.OrdinalIgnoreCase) && row[column] != DBNull.Value)
                {
                    return row[column].ToString();
                }
            }
            return string.Empty;
        }

        private static string EscapeSqlLiteral(string value)
        {
            return (value ?? string.Empty).Replace("'", "''");
        }

        private static string QuoteMySqlIdentifier(string name)
        {
            return "`" + (name ?? string.Empty).Replace("`", "``") + "`";
        }

        private static string QuoteSqliteIdentifier(string name)
        {
            return "\"" + (name ?? string.Empty).Replace("\"", "\"\"") + "\"";
        }

        private static string EscapeSqlServerName(string name)
        {
            return (name ?? string.Empty).Replace("]", "]]");
        }

        private static string GetSQLiteDatabasePath(Dictionary<string, object> connInfo, IDatabase db)
        {
            if (!(db is my_sqlite)) return string.Empty;

            if (connInfo != null && connInfo.ContainsKey("path") && connInfo["path"] != null)
            {
                string path = connInfo["path"].ToString();
                if (!string.IsNullOrWhiteSpace(path)) return path;
            }

            my_sqlite sqlite = db as my_sqlite;
            if (sqlite != null && sqlite.MCT != null)
            {
                try
                {
                    System.Data.SQLite.SQLiteConnectionStringBuilder builder =
                        new System.Data.SQLite.SQLiteConnectionStringBuilder(sqlite.MCT.ConnectionString);
                    return builder.DataSource;
                }
                catch { }
            }

            return string.Empty;
        }

        private void ShowDatabaseInfo(IDatabase db, string dbName)
        {
            dgvDetails.Rows.Clear();
            rtbDDL.Text = "";
            btnInfo.PerformClick();

            var info = db.GetDatabaseInfo(dbName);
            if (info.ContainsKey("character_set"))
            {
                dgvDetails.Rows.Add("字符集", info["character_set"]);
                dgvDetails.Rows.Add("定序", info["collation"]);
            }
        }

        private void ShowTableDetails(IDatabase db, string dbName, string tableName)
        {
            dgvDetails.Rows.Clear();
            btnInfo.PerformClick();

            try
            {
                // 取得詳情
                DataTable dt = db.GetTableStatus(dbName);
                DataRow r = FindTableStatusRow(dt, tableName);
                if (r != null)
                {
                    dgvDetails.Rows.Add("列", FirstColumnValue(r, "Rows", "ROWS", "NUM_ROWS"));
                    dgvDetails.Rows.Add("引擎", FirstColumnValue(r, "Engine", "ENGINE"));
                    dgvDetails.Rows.Add("自動遞增", FirstColumnValue(r, "Auto_increment", "AUTO_INCREMENT"));
                    dgvDetails.Rows.Add("列格式", FirstColumnValue(r, "Row_format", "ROW_FORMAT"));
                    dgvDetails.Rows.Add("修改日期", FirstColumnValue(r, "Update_time", "UPDATE_TIME"));
                    dgvDetails.Rows.Add("建立日期", FirstColumnValue(r, "Create_time", "CREATE_TIME"));
                    dgvDetails.Rows.Add("檢查時間", FirstColumnValue(r, "Check_time", "CHECK_TIME"));
                    dgvDetails.Rows.Add("索引長度", FormatBytesValue(FirstColumnValue(r, "Index_length", "INDEX_LENGTH")));
                    dgvDetails.Rows.Add("資料長度", FormatBytesValue(FirstColumnValue(r, "Data_length", "DATA_LENGTH")));
                    dgvDetails.Rows.Add("最大資料長度", FormatBytesValue(FirstColumnValue(r, "Max_data_length", "MAX_DATA_LENGTH")));
                    dgvDetails.Rows.Add("資料可用空間", FormatBytesValue(FirstColumnValue(r, "Data_free", "DATA_FREE")));
                    dgvDetails.Rows.Add("定序", FirstColumnValue(r, "Collation", "COLLATION"));
                    dgvDetails.Rows.Add("建立選項", FirstColumnValue(r, "Create_options", "CREATE_OPTIONS"));
                    dgvDetails.Rows.Add("註解", FirstColumnValue(r, "Comment", "COMMENTS"));
                }

                // 取得 DDL
                rtbDDL.Text = db.GetTableCreateStatement(dbName, tableName);
            }
            catch (Exception ex)
            {
                rtbDDL.Text = "Error loading details: " + ex.Message;
            }
        }

        private static DataRow FindTableStatusRow(DataTable tableStatus, string tableName)
        {
            if (tableStatus == null) return null;
            foreach (DataRow row in tableStatus.Rows)
            {
                string name = FirstColumnValue(row, "Name", "NAME", "TABLE_NAME");
                if (string.Equals(name, tableName, StringComparison.OrdinalIgnoreCase)) return row;
            }

            return null;
        }

        private void ShowViewDetails(IDatabase db, string dbName, string viewName)
        {
            dgvDetails.Rows.Clear();
            btnInfo.PerformClick();

            try
            {
                dgvDetails.Rows.Add("類型", "View");
                dgvDetails.Rows.Add("名稱", viewName);
                rtbDDL.Text = db.GetViewCreateStatement(dbName, viewName);
            }
            catch (Exception ex)
            {
                rtbDDL.Text = "Error loading view details: " + ex.Message;
            }
        }

        private void ShowEventDetails(IDatabase db, string dbName, string eventName)
        {
            dgvDetails.Rows.Clear();
            btnInfo.PerformClick();

            try
            {
                DataRow match = GetDatabaseEvents(db, dbName).Rows
                    .Cast<DataRow>()
                    .FirstOrDefault(row => string.Equals(row["Name"].ToString(), eventName, StringComparison.OrdinalIgnoreCase));

                if (match == null)
                {
                    rtbDDL.Text = "Event not found: " + eventName;
                    return;
                }

                dgvDetails.Rows.Add("類型", match["Type"]);
                dgvDetails.Rows.Add("名稱", match["Name"]);
                dgvDetails.Rows.Add("狀態", match["Status"]);
                rtbDDL.Text = match["DDL"].ToString();
            }
            catch (Exception ex)
            {
                rtbDDL.Text = "Error loading event details: " + ex.Message;
            }
        }

        private void ShowFunctionDetails(IDatabase db, string dbName, string functionName)
        {
            dgvDetails.Rows.Clear();
            btnInfo.PerformClick();

            try
            {
                DataRow match = GetDatabaseFunctions(db, dbName).Rows
                    .Cast<DataRow>()
                    .FirstOrDefault(row => string.Equals(row["Name"].ToString(), functionName, StringComparison.OrdinalIgnoreCase));

                if (match == null)
                {
                    rtbDDL.Text = "Function not found: " + functionName;
                    return;
                }

                dgvDetails.Rows.Add("類型", match["Type"]);
                dgvDetails.Rows.Add("名稱", match["Name"]);
                dgvDetails.Rows.Add("回傳型別", match["ReturnType"]);
                dgvDetails.Rows.Add("狀態", match["Status"]);
                rtbDDL.Text = match["DDL"].ToString();
            }
            catch (Exception ex)
            {
                rtbDDL.Text = "Error loading function details: " + ex.Message;
            }
        }

        private void ShowUserDetails(IDatabase db, string dbName, string userName, Dictionary<string, object> connInfo = null)
        {
            dgvDetails.Rows.Clear();
            btnInfo.PerformClick();

            try
            {
                DataRow match = GetDatabaseUsers(db, dbName, connInfo).Rows
                    .Cast<DataRow>()
                    .FirstOrDefault(row => string.Equals(row["Name"].ToString(), userName, StringComparison.OrdinalIgnoreCase));

                if (match == null)
                {
                    rtbDDL.Text = "User not found: " + userName;
                    return;
                }

                dgvDetails.Rows.Add("類型", match["Type"]);
                dgvDetails.Rows.Add("名稱", match["Name"]);
                dgvDetails.Rows.Add("主機", match["Host"]);
                dgvDetails.Rows.Add("狀態", match["Status"]);
                dgvDetails.Rows.Add("來源", match["Source"]);
                rtbDDL.Text = "-- User: " + match["Name"] + Environment.NewLine +
                              "-- Source: " + match["Source"] + Environment.NewLine +
                              "-- Status: " + match["Status"];
            }
            catch (Exception ex)
            {
                rtbDDL.Text = "Error loading user details: " + ex.Message;
            }
        }

        private void ShowModelDetails(IDatabase db, string dbName, string modelName)
        {
            dgvDetails.Rows.Clear();
            btnInfo.PerformClick();

            dgvDetails.Rows.Add("類型", "Model");
            dgvDetails.Rows.Add("名稱", modelName);
            dgvDetails.Rows.Add("資料庫", dbName);
            dgvDetails.Rows.Add("Provider", db.ProviderName);
            rtbDDL.Text = "-- Model: " + modelName + Environment.NewLine +
                          "-- " + GetDatabaseModelDescription(modelName);
        }

        private void ShowBIDetails(IDatabase db, string dbName, string biName)
        {
            dgvDetails.Rows.Clear();
            btnInfo.PerformClick();

            dgvDetails.Rows.Add("類型", "BI");
            dgvDetails.Rows.Add("名稱", biName);
            dgvDetails.Rows.Add("資料庫", dbName);
            dgvDetails.Rows.Add("Provider", db.ProviderName);
            rtbDDL.Text = "-- BI: " + biName + Environment.NewLine +
                          "-- " + GetDatabaseBIDescription(biName);
        }

        private void ShowOtherToolDetails(IDatabase db, string dbName, string toolName)
        {
            dgvDetails.Rows.Clear();
            btnInfo.PerformClick();

            dgvDetails.Rows.Add("類型", "Other");
            dgvDetails.Rows.Add("名稱", toolName);
            dgvDetails.Rows.Add("資料庫", dbName);
            dgvDetails.Rows.Add("Provider", db.ProviderName);
            rtbDDL.Text = "-- Other: " + toolName + Environment.NewLine +
                          "-- " + GetDatabaseOtherToolDescription(toolName);
        }

        private void ShowReportDetails(IDatabase db, string dbName, string reportName)
        {
            dgvDetails.Rows.Clear();
            btnInfo.PerformClick();

            dgvDetails.Rows.Add("類型", "Report");
            dgvDetails.Rows.Add("名稱", reportName);
            dgvDetails.Rows.Add("資料庫", dbName);
            dgvDetails.Rows.Add("Provider", db.ProviderName);
            rtbDDL.Text = "-- Report: " + reportName + Environment.NewLine +
                          "-- " + GetDatabaseReportDescription(reportName);
        }

        private string FormatBytes(long bytes)
        {
            string[] suffix = { "B", "KB", "MB", "GB", "TB" };
            if (bytes == 0) return "0 B";
            int i = (int)Math.Floor(Math.Log(Math.Abs(bytes), 1024));
            return Math.Round(bytes / Math.Pow(1024, i), 2) + " " + suffix[i];
        }

        private void db_tree_NodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                db_tree.SelectedNode = e.Node;

                ContextMenuStrip menu = BuildTreeContextMenu(e.Node);
                if (menu == null) return;
                menu.Show(db_tree, e.Location);
            }
        }

        private void db_tree_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            if (db_tree.GetNodeAt(e.Location) != null) return;

            db_tree.SelectedNode = null;
            ContextMenuStrip menu = BuildTreeBlankContextMenu();
            menu.Show(db_tree, e.Location);
        }

        private ContextMenuStrip BuildTreeBlankContextMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();

            ToolStripMenuItem newConnectionItem = new ToolStripMenuItem(Localization.T("Menu.NewConnection"));
            newConnectionItem.Click += (s, ev) => ShowConnectionTypeSelection();
            menu.Items.Add(newConnectionItem);

            ToolStripMenuItem newGroupItem = new ToolStripMenuItem(Localization.T("Menu.NewGroup"));
            newGroupItem.Click += (s, ev) => ShowCreateGroupDialog();
            menu.Items.Add(newGroupItem);

            ToolStripMenuItem refreshItem = new ToolStripMenuItem(Localization.T("Query.Refresh"));
            refreshItem.Click += (s, ev) => RefreshConnectionList();
            menu.Items.Add(refreshItem);

            ThemeManager.ApplyToolStrip(menu);
            return menu;
        }

        private void RefreshConnectionList()
        {
            drawLists();
            ConfigureMainMenu();
            UpdateMainStatus(Localization.T("Status.ConnectionListRefreshed"));
        }

        private ContextMenuStrip BuildTreeContextMenu(TreeNode node)
        {
            if (node == null) return null;

            ContextMenuStrip menu = new ContextMenuStrip();

            // 連線群組節點：顯示重新命名 / 刪除群組
            if (IsConnectionGroupNode(node))
            {
                string groupName = node.Text;
                ToolStripMenuItem renameGroupItem = new ToolStripMenuItem(Localization.T("Menu.RenameGroup"));
                renameGroupItem.Click += (s, ev) => RenameConnectionGroup(groupName);
                menu.Items.Add(renameGroupItem);

                ToolStripMenuItem deleteGroupItem = new ToolStripMenuItem(Localization.T("Menu.DeleteGroup"));
                deleteGroupItem.Click += (s, ev) => DeleteConnectionGroup(groupName);
                menu.Items.Add(deleteGroupItem);

                ThemeManager.ApplyToolStrip(menu);
                return menu;
            }

            if (node.Parent == null || IsConnectionGroupNode(node.Parent))
            {
                AddConnectionRootMenuItems(menu, node);
            }
            else
            {
                var pathParts = GetTreePathParts(node);
                if (pathParts.Length == 3 && pathParts[2] == "Views")
                {
                    AddViewGroupMenuItems(menu, node);
                }
                if (pathParts.Length == 2)
                {
                    AddDatabaseNodeMenuItems(menu, node);
                }
                if (pathParts.Length == 3 && pathParts[2] == "Tables")
                {
                    AddTableGroupMenuItems(menu, node);
                }
                if (pathParts.Length == 3 && pathParts[2] == "Queries")
                {
                    AddQueryGroupMenuItems(menu, node);
                }
                if (pathParts.Length == 3 && pathParts[2] == "Backups")
                {
                    ToolStripMenuItem backupDatabaseItem = new ToolStripMenuItem(Localization.T("Tool.CreateBackup"));
                    backupDatabaseItem.Click += (s, ev) => BackupSelectedDatabaseWithDialog();
                    menu.Items.Add(backupDatabaseItem);

                    ToolStripMenuItem restoreBackupItem = new ToolStripMenuItem(Localization.T("Tool.RestoreBackup"));
                    restoreBackupItem.Click += (s, ev) => RestoreBackupWithDialog();
                    menu.Items.Add(restoreBackupItem);

                    ToolStripMenuItem restoreQuarantinedBackupItem = new ToolStripMenuItem(Localization.T("Tool.RestoreQuarantinedBackup"));
                    restoreQuarantinedBackupItem.Click += (s, ev) => RestoreQuarantinedBackupWithDialog();
                    menu.Items.Add(restoreQuarantinedBackupItem);

                    ToolStripMenuItem batchRestoreQuarantinedBackupItem = new ToolStripMenuItem(Localization.T("Tool.BatchRestoreQuarantinedBackups"));
                    batchRestoreQuarantinedBackupItem.Click += (s, ev) => BatchRestoreQuarantinedBackupsWithDialog();
                    menu.Items.Add(batchRestoreQuarantinedBackupItem);
                }
                if (pathParts.Length >= 4 && pathParts[2] == "Tables")
                {
                    ToolStripMenuItem openTableItem = new ToolStripMenuItem(Localization.T("Tool.SelectStar"));
                    openTableItem.Click += (s, ev) => OpenSelectedTableInQuery();
                    menu.Items.Add(openTableItem);

                    ToolStripMenuItem selectColumnsItem = new ToolStripMenuItem(Localization.T("Tool.SelectAllColumns"));
                    selectColumnsItem.Click += (s, ev) => OpenSelectedTableAllColumnsInQuery();
                    menu.Items.Add(selectColumnsItem);

                    ToolStripMenuItem designTableItem = new ToolStripMenuItem(Localization.T("Tool.DesignTable"));
                    designTableItem.Click += (s, ev) => DesignSelectedTable();
                    menu.Items.Add(designTableItem);

                    menu.Items.Add(CreateAutoCommentModeMenuItem(mode => FillSelectedTableComments(mode)));

                    TreeDatabaseTarget tableTarget = BuildTargetFromNode(node);
                    if (IsSqliteTarget(tableTarget))
                    {
                        string selectedTableName = pathParts[3];
                        ToolStripMenuItem exportSqliteCommentsItem = new ToolStripMenuItem(Localization.T("Tool.ExportSqliteColumnComments"));
                        exportSqliteCommentsItem.Click += (s, ev) => ExportSqliteColumnCommentsWithDialog(selectedTableName);
                        menu.Items.Add(exportSqliteCommentsItem);

                        ToolStripMenuItem importSqliteCommentsItem = new ToolStripMenuItem(Localization.T("Tool.ImportSqliteColumnComments"));
                        importSqliteCommentsItem.Click += (s, ev) => ImportSqliteColumnCommentsWithDialog(selectedTableName);
                        menu.Items.Add(importSqliteCommentsItem);
                    }

                    ToolStripMenuItem deleteTableItem = new ToolStripMenuItem(Localization.T("Tool.DeleteTable"));
                    deleteTableItem.Click += (s, ev) => DeleteSelectedTable();
                    menu.Items.Add(deleteTableItem);

                    AddCopyRenameObjectMenuItems(menu);
                }
                else if (pathParts.Length >= 4 && pathParts[2] == "Views")
                {
                    ToolStripMenuItem openViewItem = new ToolStripMenuItem(Localization.T("Tool.OpenView"));
                    openViewItem.Click += (s, ev) => OpenSelectedViewInQuery();
                    menu.Items.Add(openViewItem);

                    ToolStripMenuItem designViewItem = new ToolStripMenuItem(Localization.T("Tool.DesignView"));
                    designViewItem.Click += (s, ev) => ShowSelectedViewDefinition();
                    menu.Items.Add(designViewItem);

                    ToolStripMenuItem dumpSqlItem = new ToolStripMenuItem(Localization.T("Tool.DumpSql"));
                    dumpSqlItem.Click += (s, ev) => DumpSelectedViewSql();
                    menu.Items.Add(dumpSqlItem);

                    ToolStripMenuItem deleteViewItem = new ToolStripMenuItem(Localization.T("Tool.DeleteView"));
                    deleteViewItem.Click += (s, ev) => DeleteSelectedView();
                    menu.Items.Add(deleteViewItem);

                    AddCopyRenameObjectMenuItems(menu);
                }
                else if (pathParts.Length >= 4 && pathParts[2] == "Functions")
                {
                    ToolStripMenuItem executeFunctionItem = new ToolStripMenuItem(Localization.T("Tool.ExecuteFunction"));
                    executeFunctionItem.Click += (s, ev) => ExecuteSelectedFunction();
                    menu.Items.Add(executeFunctionItem);

                    ToolStripMenuItem designFunctionItem = new ToolStripMenuItem(Localization.T("Tool.DesignFunction"));
                    designFunctionItem.Click += (s, ev) => ShowSelectedFunctionDefinition();
                    menu.Items.Add(designFunctionItem);

                    ToolStripMenuItem deleteFunctionItem = new ToolStripMenuItem(Localization.T("Tool.DeleteFunction"));
                    deleteFunctionItem.Click += (s, ev) => DeleteSelectedFunction();
                    menu.Items.Add(deleteFunctionItem);
                }
                else if (pathParts.Length >= 4 && IsDetailsOnlyGroup(pathParts[2]))
                {
                    ToolStripMenuItem openDetailsItem = new ToolStripMenuItem(Localization.T("Tool.OpenDetails"));
                    openDetailsItem.Click += (s, ev) => ShowSelectedTreeNodeDetails();
                    menu.Items.Add(openDetailsItem);
                }
            }

            if (menu.Items.Count == 0)
            {
                menu.Dispose();
                return null;
            }

            ThemeManager.ApplyToolStrip(menu);
            return menu;
        }

        private void AddQueryGroupMenuItems(ContextMenuStrip menu, TreeNode node)
        {
            ToolStripMenuItem newQueryItem = new ToolStripMenuItem(Localization.T("Toolbar.NewQuery"));
            newQueryItem.Click += (s, ev) => Query_btn_Click(s, ev);
            menu.Items.Add(newQueryItem);

            menu.Items.Add(new ToolStripSeparator());

            AddPasteObjectMenuItem(menu);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem openFolderItem = new ToolStripMenuItem(Localization.T("Tool.OpenContainingFolder"));
            openFolderItem.Click += (s, ev) => OpenQueryFolder();
            menu.Items.Add(openFolderItem);

            ToolStripMenuItem openExternalQueryItem = new ToolStripMenuItem(Localization.T("Tool.OpenExternalQuery"));
            openExternalQueryItem.Click += (s, ev) => OpenExternalQueryWithDialog();
            menu.Items.Add(openExternalQueryItem);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem refreshItem = new ToolStripMenuItem(Localization.T("Query.Refresh"));
            refreshItem.Click += (s, ev) => RefreshQueryGroupNode(node);
            menu.Items.Add(refreshItem);
        }

        private void OpenQueryFolder()
        {
            string path = GetQueryFolderPath();
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            UpdateMainStatus(Localization.Format("Query.FolderOpened", path));
        }

        private static string GetQueryFolderPath()
        {
            return Path.Combine(Application.UserAppDataPath, "queries");
        }

        private void OpenExternalQueryWithDialog()
        {
            TreeDatabaseTarget target = GetTargetFromCurrentSelection();
            if (target == null)
            {
                MessageBox.Show(Localization.T("Object.SelectDatabaseOrConnection"), Localization.T("Tool.OpenExternalQuery"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = Localization.T("Tool.OpenExternalQuery");
                dialog.Filter = Localization.T("Common.SqlFilesFilter");
                dialog.CheckFileExists = true;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                string sql = File.ReadAllText(dialog.FileName, Encoding.UTF8);
                OpenQuery(target.Database, target.DatabaseName, GetTargetHost(target), sql, true);
                UpdateMainStatus(Localization.Format("Query.ExternalOpened", dialog.FileName));
            }
        }

        private void RefreshQueryGroupNode(TreeNode queriesNode)
        {
            if (queriesNode == null) return;
            db_tree.SelectedNode = queriesNode;
            ShowQueryHistoryForSelectedDatabase();
        }

        private void AddViewGroupMenuItems(ContextMenuStrip menu, TreeNode node)
        {
            ToolStripMenuItem newViewItem = new ToolStripMenuItem(Localization.T("Tool.NewView"));
            newViewItem.Click += (s, ev) => CreateNewView();
            menu.Items.Add(newViewItem);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exportWizardItem = new ToolStripMenuItem(Localization.T("Tool.ExportWizard"));
            exportWizardItem.Click += (s, ev) => DumpCurrentSelectionSqlWithDialog(false);
            menu.Items.Add(exportWizardItem);

            ToolStripMenuItem dictionaryItem = new ToolStripMenuItem(Localization.T("Tool.DataDictionary"));
            dictionaryItem.Click += (s, ev) => OpenSelectedDatabaseDictionary();
            menu.Items.Add(dictionaryItem);

            menu.Items.Add(new ToolStripSeparator());

            AddPasteObjectMenuItem(menu);

            ToolStripMenuItem refreshItem = new ToolStripMenuItem(Localization.T("Query.Refresh"));
            refreshItem.Click += (s, ev) => RefreshDatabaseGroupNode(node, "Views");
            menu.Items.Add(refreshItem);
        }

        private void AddTableGroupMenuItems(ContextMenuStrip menu, TreeNode node)
        {
            ToolStripMenuItem newTableItem = new ToolStripMenuItem(Localization.T("Tool.NewTable"));
            newTableItem.Click += (s, ev) => CreateNewTable();
            menu.Items.Add(newTableItem);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem importSqlItem = new ToolStripMenuItem(Localization.T("Tool.ImportWizard"));
            importSqlItem.Click += (s, ev) => ImportSqlWithDialog();
            menu.Items.Add(importSqlItem);

            ToolStripMenuItem exportWizardItem = new ToolStripMenuItem(Localization.T("Tool.ExportWizard"));
            exportWizardItem.Click += (s, ev) => DumpCurrentSelectionSqlWithDialog(false);
            menu.Items.Add(exportWizardItem);

            ToolStripMenuItem dictionaryItem = new ToolStripMenuItem(Localization.T("Tool.DataDictionary"));
            dictionaryItem.Click += (s, ev) => OpenSelectedDatabaseDictionary();
            menu.Items.Add(dictionaryItem);

            ToolStripMenuItem dataGeneratorItem = new ToolStripMenuItem(Localization.T("Tool.GenerateData"));
            dataGeneratorItem.Click += (s, ev) => ShowDataGenerationDialog();
            menu.Items.Add(dataGeneratorItem);

            TreeDatabaseTarget sqliteTarget = BuildTargetFromNode(node);
            if (IsSqliteTarget(sqliteTarget))
            {
                ToolStripMenuItem exportSqliteCommentsItem = new ToolStripMenuItem(Localization.T("Tool.ExportSqliteColumnComments"));
                exportSqliteCommentsItem.Click += (s, ev) => ExportSqliteColumnCommentsWithDialog(null);
                menu.Items.Add(exportSqliteCommentsItem);

                ToolStripMenuItem importSqliteCommentsItem = new ToolStripMenuItem(Localization.T("Tool.ImportSqliteColumnComments"));
                importSqliteCommentsItem.Click += (s, ev) => ImportSqliteColumnCommentsWithDialog(null);
                menu.Items.Add(importSqliteCommentsItem);
            }

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem executeSqlFileItem = new ToolStripMenuItem(Localization.T("Tool.ExecuteSqlFile"));
            executeSqlFileItem.Click += (s, ev) => ImportSqlWithDialog();
            menu.Items.Add(executeSqlFileItem);

            ToolStripMenuItem findItem = new ToolStripMenuItem(Localization.T("Tool.FindInDatabase"));
            findItem.Click += (s, ev) => FindInSelectedDatabase();
            menu.Items.Add(findItem);

            menu.Items.Add(new ToolStripSeparator());

            AddPasteObjectMenuItem(menu);

            ToolStripMenuItem refreshItem = new ToolStripMenuItem(Localization.T("Query.Refresh"));
            refreshItem.Click += (s, ev) => RefreshDatabaseGroupNode(node, "Tables");
            menu.Items.Add(refreshItem);
        }

        private void ShowDataGenerationDialog()
        {
            TreeDatabaseTarget target = GetTargetFromCurrentSelection();
            if (target == null)
            {
                MessageBox.Show(Localization.T("Status.SelectExpandedDatabase"), Localization.T("Tool.GenerateData"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            List<string> tables;
            try
            {
                tables = target.Database.GetTables(target.DatabaseName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Localization.Format("Database.DataGenerationLoadFailed", ex.Message), Localization.T("Tool.GenerateData"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (tables == null || tables.Count == 0)
            {
                MessageBox.Show(Localization.T("Database.DataGenerationNoTables"), Localization.T("Tool.GenerateData"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (Form dialog = new Form())
            using (TableLayoutPanel layout = new TableLayoutPanel())
            using (ComboBox tableCombo = new ComboBox())
            using (NumericUpDown rowCountInput = new NumericUpDown())
            using (RichTextBox preview = new RichTextBox())
            using (FlowLayoutPanel footer = new FlowLayoutPanel())
            using (Button generateButton = new Button())
            using (Button openQueryButton = new Button())
            using (Button executeButton = new Button())
            using (Button closeButton = new Button())
            {
                dialog.Text = Localization.T("Tool.GenerateData");
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.Size = new Size(860, 620);
                dialog.MinimizeBox = false;
                dialog.ShowInTaskbar = false;

                layout.Dock = DockStyle.Fill;
                layout.Padding = new Padding(12);
                layout.ColumnCount = 2;
                layout.RowCount = 4;
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

                tableCombo.DropDownStyle = ComboBoxStyle.DropDownList;
                tableCombo.Dock = DockStyle.Fill;
                foreach (string table in tables.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
                {
                    tableCombo.Items.Add(table);
                }
                tableCombo.SelectedIndex = 0;

                rowCountInput.Dock = DockStyle.Left;
                rowCountInput.Minimum = 1;
                rowCountInput.Maximum = 500;
                rowCountInput.Value = 10;
                rowCountInput.Width = 120;

                preview.Dock = DockStyle.Fill;
                preview.Font = new Font("Consolas", 10);
                preview.WordWrap = false;
                preview.ScrollBars = RichTextBoxScrollBars.Both;

                footer.Dock = DockStyle.Fill;
                footer.FlowDirection = FlowDirection.RightToLeft;
                footer.Padding = new Padding(0, 8, 0, 0);
                generateButton.Text = Localization.T("Database.GenerateDataPreview");
                generateButton.AutoSize = true;
                openQueryButton.Text = Localization.T("Database.GenerateDataOpenQuery");
                openQueryButton.AutoSize = true;
                executeButton.Text = Localization.T("Database.GenerateDataExecute");
                executeButton.AutoSize = true;
                closeButton.Text = Localization.T("Common.Close");
                closeButton.AutoSize = true;

                Action generatePreview = () =>
                {
                    string tableName = tableCombo.SelectedItem == null ? "" : tableCombo.SelectedItem.ToString();
                    preview.Text = BuildDataGenerationSql(target.Database, target.DatabaseName, tableName, (int)rowCountInput.Value);
                };

                generateButton.Click += (s, e) => generatePreview();
                openQueryButton.Click += (s, e) =>
                {
                    if (string.IsNullOrWhiteSpace(preview.Text)) generatePreview();
                    OpenQuery(target.Database, target.DatabaseName, GetTargetHost(target), preview.Text, true);
                    UpdateMainStatus(Localization.Format("Database.DataGenerationOpened", tableCombo.SelectedItem));
                    dialog.Close();
                };
                executeButton.Click += (s, e) =>
                {
                    if (string.IsNullOrWhiteSpace(preview.Text)) generatePreview();
                    int statementCount = CountGeneratedDataStatements(preview.Text);
                    if (statementCount == 0)
                    {
                        MessageBox.Show(Localization.T("Database.DataGenerationNothingToExecute"), Localization.T("Tool.GenerateData"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    string tableName = tableCombo.SelectedItem == null ? "" : tableCombo.SelectedItem.ToString();
                    DialogResult confirm = MessageBox.Show(
                        Localization.Format("Database.DataGenerationExecuteConfirm", tableName, statementCount),
                        Localization.T("Tool.GenerateData"),
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning);
                    if (confirm != DialogResult.Yes) return;

                    try
                    {
                        ExecuteGeneratedDataSql(target.Database, preview.Text);
                        string message = Localization.Format("Database.DataGenerationExecuted", tableName, statementCount);
                        UpdateMainStatus(message);
                        MessageBox.Show(message, Localization.T("Common.Complete"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                        dialog.Close();
                    }
                    catch (Exception ex)
                    {
                        string message = Localization.Format("Database.DataGenerationExecuteFailed", ex.Message);
                        UpdateMainStatus(message);
                        MessageBox.Show(message, Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                };
                closeButton.Click += (s, e) => dialog.Close();

                layout.Controls.Add(new Label { Text = Localization.T("Database.GenerateDataTable"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
                layout.Controls.Add(tableCombo, 1, 0);
                layout.Controls.Add(new Label { Text = Localization.T("Database.GenerateDataRows"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
                layout.Controls.Add(rowCountInput, 1, 1);
                layout.Controls.Add(preview, 0, 2);
                layout.SetColumnSpan(preview, 2);
                footer.Controls.Add(closeButton);
                footer.Controls.Add(executeButton);
                footer.Controls.Add(openQueryButton);
                footer.Controls.Add(generateButton);
                layout.Controls.Add(footer, 0, 3);
                layout.SetColumnSpan(footer, 2);

                dialog.Controls.Add(layout);
                ThemeManager.ApplyTo(dialog);
                generatePreview();
                dialog.ShowDialog(this);
            }
        }

        private int CountGeneratedDataStatements(string sql)
        {
            return SplitGeneratedDataStatements(sql).Count;
        }

        private List<string> SplitGeneratedDataStatements(string sql)
        {
            List<string> statements = new List<string>();
            if (string.IsNullOrWhiteSpace(sql)) return statements;

            string[] lines = sql.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            foreach (string line in lines)
            {
                string statement = (line ?? string.Empty).Trim();
                if (statement.Length == 0 || statement.StartsWith("--")) continue;
                if (!statement.StartsWith("INSERT ", StringComparison.OrdinalIgnoreCase)) continue;
                statements.Add(statement.TrimEnd(';'));
            }

            return statements;
        }

        private void ExecuteGeneratedDataSql(IDatabase db, string sql)
        {
            if (db == null) throw new InvalidOperationException(Localization.T("Database.DataGenerationNoTarget"));
            List<string> statements = SplitGeneratedDataStatements(sql);
            if (statements.Count == 0) throw new InvalidOperationException(Localization.T("Database.DataGenerationNothingToExecute"));

            Form progressOwner = Form.ActiveForm ?? this;
            using (RunnerProgressOverlay progressOverlay = RunnerProgressOverlay.Show(progressOwner, Localization.T("Tool.GenerateData"), Localization.Format("Database.DataGenerationExecuting", 0, statements.Count)))
            {
                for (int i = 0; i < statements.Count; i++)
                {
                    progressOverlay.SetProgress(i, statements.Count, Localization.Format("Database.DataGenerationExecuting", i, statements.Count));
                    Dictionary<string, string> result = db.ExecSQL(statements[i]);
                    if (result == null || !result.ContainsKey("status") || !string.Equals(result["status"], "OK", StringComparison.OrdinalIgnoreCase))
                    {
                        string reason = result != null && result.ContainsKey("reason") ? result["reason"] : Localization.T("Common.Error");
                        throw new InvalidOperationException(reason);
                    }
                    progressOverlay.SetProgress(i + 1, statements.Count, Localization.Format("Database.DataGenerationExecuting", i + 1, statements.Count));
                }
            }
        }

        private string BuildDataGenerationSql(IDatabase db, string databaseName, string tableName, int rowCount)
        {
            if (db == null || string.IsNullOrWhiteSpace(tableName)) return string.Empty;
            if (rowCount < 1) rowCount = 1;

            DataTable columns = db.GetColumns(databaseName, tableName);
            List<DataGenerationColumn> writableColumns = new List<DataGenerationColumn>();
            foreach (DataRow row in columns.Rows)
            {
                DataGenerationColumn column = BuildDataGenerationColumn(row);
                if (string.IsNullOrWhiteSpace(column.Name) || column.IsAutoGenerated) continue;
                writableColumns.Add(column);
            }

            if (writableColumns.Count == 0)
            {
                return "-- " + Localization.T("Database.DataGenerationNoColumns");
            }

            StringBuilder builder = new StringBuilder();
            builder.AppendLine("-- " + Localization.Format("Database.DataGenerationHeader", tableName, rowCount));
            string qualifiedTable = BuildQualifiedObjectName(db, databaseName, tableName);
            string columnList = string.Join(", ", writableColumns.Select(c => QuoteDumpIdentifier(db, c.Name)).ToArray());

            for (int rowIndex = 1; rowIndex <= rowCount; rowIndex++)
            {
                List<string> values = new List<string>();
                foreach (DataGenerationColumn column in writableColumns)
                {
                    values.Add(ToSqlLiteral(db, BuildGeneratedSampleValue(column, rowIndex)));
                }

                builder.AppendLine("INSERT INTO " + qualifiedTable + " (" + columnList + ") VALUES (" + string.Join(", ", values.ToArray()) + ");");
            }

            return builder.ToString();
        }

        private static DataGenerationColumn BuildDataGenerationColumn(DataRow row)
        {
            string name = FirstColumnValue(row, "Field", "name", "Name", "COLUMN_NAME", "column_name");
            string type = FirstColumnValue(row, "Type", "type", "COLUMN_TYPE", "DATA_TYPE", "data_type");
            string extra = FirstColumnValue(row, "Extra", "EXTRA", "extra");
            string defaultValue = FirstColumnValue(row, "Default", "dflt_value", "COLUMN_DEFAULT", "column_default", "DATA_DEFAULT");

            bool autoGenerated =
                extra.IndexOf("auto_increment", StringComparison.OrdinalIgnoreCase) >= 0 ||
                extra.IndexOf("identity", StringComparison.OrdinalIgnoreCase) >= 0 ||
                defaultValue.IndexOf("nextval(", StringComparison.OrdinalIgnoreCase) >= 0 ||
                type.IndexOf("rowversion", StringComparison.OrdinalIgnoreCase) >= 0 ||
                type.IndexOf("timestamp without time zone", StringComparison.OrdinalIgnoreCase) >= 0 && defaultValue.IndexOf("now()", StringComparison.OrdinalIgnoreCase) >= 0;

            return new DataGenerationColumn
            {
                Name = name,
                Type = type,
                IsAutoGenerated = autoGenerated
            };
        }

        private static object BuildGeneratedSampleValue(DataGenerationColumn column, int rowIndex)
        {
            string name = (column.Name ?? string.Empty).ToLowerInvariant();
            string type = (column.Type ?? string.Empty).ToLowerInvariant();

            if (type.Contains("uuid") || type.Contains("uniqueidentifier") || name == "uuid" || name.EndsWith("_uuid"))
            {
                return Guid.NewGuid();
            }

            if (type.Contains("bool") || type == "bit" || type.Contains("tinyint(1)"))
            {
                return rowIndex % 2 == 1;
            }

            if (type.Contains("date") || type.Contains("time"))
            {
                return DateTime.Today.AddDays(rowIndex - 1).AddMinutes(rowIndex);
            }

            if (type.Contains("int") || type.Contains("number") && !type.Contains(",") || type.Contains("numeric") && !type.Contains(","))
            {
                return rowIndex;
            }

            if (type.Contains("decimal") || type.Contains("numeric") || type.Contains("float") || type.Contains("double") || type.Contains("real") || type.Contains("money"))
            {
                return rowIndex + 0.25m;
            }

            if (type.Contains("json"))
            {
                return "{\"sample\":" + rowIndex + "}";
            }

            if (type.Contains("blob") || type.Contains("binary") || type.Contains("bytea") || type.Contains("raw") || type.Contains("image"))
            {
                return new byte[] { (byte)(rowIndex & 0xff), 0x50, 0x4b };
            }

            if (type.Contains("geometry") || type.Contains("geography") || type.Contains("point") || type.Contains("polygon"))
            {
                return DBNull.Value;
            }

            string baseName = string.IsNullOrWhiteSpace(column.Name) ? "value" : column.Name;
            return "sample_" + baseName + "_" + rowIndex;
        }

        private sealed class DataGenerationColumn
        {
            public string Name { get; set; }
            public string Type { get; set; }
            public bool IsAutoGenerated { get; set; }
        }

        private void FindInSelectedDatabase()
        {
            TreeDatabaseTarget target = GetTargetFromCurrentSelection();
            if (target == null) return;

            string keyword = PromptForText(Localization.T("Database.SearchTitle"), Localization.T("Database.SearchPrompt"), "");
            if (keyword == null) return;
            keyword = keyword.Trim();
            if (keyword.Length == 0)
            {
                MessageBox.Show(Localization.T("Database.SearchKeywordRequired"), Localization.T("Database.SearchTitle"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DataTable results = BuildDatabaseSearchResults(target.Database, target.DatabaseName, keyword);
            table_top.Visible = true;
            queryTabs.Visible = false;
            table_top.BringToFront();
            UpdateObjectFilterVisibility();
            table_top.DataSource = results;
            lblSidebarTitle.Text = Localization.T("Database.SearchTitle") + ": " + target.DatabaseName;
            dgvDetails.DataSource = null;
            rtbDDL.Text = "-- " + Localization.T("Database.SearchTitle") + Environment.NewLine +
                          "-- " + keyword;
            UpdateMainStatus(Localization.Format("Database.SearchCompleted", results.Rows.Count));
        }

        private DataTable BuildDatabaseSearchResults(IDatabase db, string databaseName, string keyword)
        {
            DataTable results = new DataTable();
            results.Columns.Add("類型");
            results.Columns.Add("名稱");
            results.Columns.Add("欄位");
            results.Columns.Add("位置");

            foreach (string tableName in GetTablesSafe(db, databaseName))
            {
                AddSearchResultIfMatches(results, Localization.T("DatabaseSearch.TypeTable"), tableName, "", Localization.T("DatabaseSearch.LocationTables"), keyword);
                AddColumnSearchResults(results, db, databaseName, tableName, false, keyword);
            }

            foreach (string viewName in GetViewsSafe(db, databaseName))
            {
                AddSearchResultIfMatches(results, Localization.T("DatabaseSearch.TypeView"), viewName, "", Localization.T("DatabaseSearch.LocationViews"), keyword);
                AddColumnSearchResults(results, db, databaseName, viewName, true, keyword);
            }

            return results;
        }

        private void AddColumnSearchResults(DataTable results, IDatabase db, string databaseName, string objectName, bool isView, string keyword)
        {
            DataTable columns = GetColumnsSafe(db, databaseName, objectName);
            string type = Localization.T(isView ? "DatabaseSearch.TypeViewColumn" : "DatabaseSearch.TypeTableColumn");
            string location = Localization.T(isView ? "DatabaseSearch.LocationViews" : "DatabaseSearch.LocationTables");
            foreach (DataRow column in columns.Rows)
            {
                string columnName = GetModelColumnName(column);
                AddSearchResultIfMatches(results, type, objectName, columnName, location, keyword);
            }
        }

        private static void AddSearchResultIfMatches(DataTable results, string type, string name, string column, string location, string keyword)
        {
            if (!ContainsIgnoreCase(name, keyword) && !ContainsIgnoreCase(column, keyword)) return;

            DataRow row = results.NewRow();
            row["類型"] = type;
            row["名稱"] = name;
            row["欄位"] = column;
            row["位置"] = location;
            results.Rows.Add(row);
        }

        private static bool ContainsIgnoreCase(string value, string keyword)
        {
            return (value ?? "").IndexOf(keyword ?? "", StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private void RefreshDatabaseGroupNode(TreeNode groupNode, string groupName)
        {
            if (groupNode == null || groupNode.Parent == null) return;
            TreeNode databaseNode = groupNode.Parent;
            RefreshDatabaseObjectNodes(databaseNode);
            foreach (TreeNode child in databaseNode.Nodes)
            {
                if (!TreeNodeBelongsToGroup(child, groupName)) continue;
                db_tree.SelectedNode = child;
                child.EnsureVisible();
                return;
            }
        }

        private void AddDatabaseNodeMenuItems(ContextMenuStrip menu, TreeNode node)
        {
            ToolStripMenuItem closeItem = new ToolStripMenuItem(Localization.T("Tool.CloseDatabase"));
            closeItem.Click += (s, ev) => CloseDatabaseNode(node);
            menu.Items.Add(closeItem);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem editItem = new ToolStripMenuItem(Localization.T("Tool.EditDatabase"));
            editItem.Click += (s, ev) => EditSelectedDatabase();
            menu.Items.Add(editItem);

            ToolStripMenuItem newDatabaseItem = new ToolStripMenuItem(Localization.T("Tool.NewDatabase"));
            newDatabaseItem.Click += (s, ev) => CreateDatabaseFromDatabaseNode(node);
            menu.Items.Add(newDatabaseItem);

            ToolStripMenuItem deleteItem = new ToolStripMenuItem(Localization.T("Tool.DeleteDatabase"));
            deleteItem.Click += (s, ev) => DeleteSelectedDatabase();
            menu.Items.Add(deleteItem);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem newQueryItem = new ToolStripMenuItem(Localization.T("Toolbar.NewQuery"));
            newQueryItem.Click += (s, ev) => Query_btn_Click(s, ev);
            menu.Items.Add(newQueryItem);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem commandLineItem = new ToolStripMenuItem(Localization.T("Tool.CommandLine"));
            commandLineItem.Click += (s, ev) => OpenSelectedDatabaseCommandLine(node);
            menu.Items.Add(commandLineItem);

            ToolStripMenuItem executeSqlFileItem = new ToolStripMenuItem(Localization.T("Tool.ExecuteSqlFile"));
            executeSqlFileItem.Click += (s, ev) => ImportSqlWithDialog();
            menu.Items.Add(executeSqlFileItem);

            ToolStripMenuItem dumpSqlItem = new ToolStripMenuItem(Localization.T("Tool.DumpSql"));
            ToolStripMenuItem dumpStructureAndDataItem = new ToolStripMenuItem(Localization.T("Tool.StructureAndData"));
            dumpStructureAndDataItem.Click += (s, ev) => DumpSelectedDatabaseSqlWithDialog();
            dumpSqlItem.DropDownItems.Add(dumpStructureAndDataItem);
            menu.Items.Add(dumpSqlItem);

            ToolStripMenuItem restoreBackupItem = new ToolStripMenuItem(Localization.T("Tool.RestoreBackup"));
            restoreBackupItem.Click += (s, ev) => RestoreBackupWithDialog();
            menu.Items.Add(restoreBackupItem);

            ToolStripMenuItem restoreQuarantinedBackupItem = new ToolStripMenuItem(Localization.T("Tool.RestoreQuarantinedBackup"));
            restoreQuarantinedBackupItem.Click += (s, ev) => RestoreQuarantinedBackupWithDialog();
            menu.Items.Add(restoreQuarantinedBackupItem);

            ToolStripMenuItem batchRestoreQuarantinedBackupItem = new ToolStripMenuItem(Localization.T("Tool.BatchRestoreQuarantinedBackups"));
            batchRestoreQuarantinedBackupItem.Click += (s, ev) => BatchRestoreQuarantinedBackupsWithDialog();
            menu.Items.Add(batchRestoreQuarantinedBackupItem);

            ToolStripMenuItem dictionaryItem = new ToolStripMenuItem(Localization.T("Tool.DataDictionary"));
            dictionaryItem.Click += (s, ev) => OpenSelectedDatabaseDictionary();
            menu.Items.Add(dictionaryItem);

            TreeDatabaseTarget sqliteTarget = BuildTargetFromNode(node);
            if (IsSqliteTarget(sqliteTarget))
            {
                ToolStripMenuItem sqliteSpecialItem = new ToolStripMenuItem(Localization.T("Tool.SqliteSpecialObjectWizard"));
                sqliteSpecialItem.Click += (s, ev) => OpenSqliteSpecialObjectWizard(node);
                menu.Items.Add(sqliteSpecialItem);

                ToolStripMenuItem exportSqliteCommentsItem = new ToolStripMenuItem(Localization.T("Tool.ExportSqliteColumnComments"));
                exportSqliteCommentsItem.Click += (s, ev) => ExportSqliteColumnCommentsWithDialog(null);
                menu.Items.Add(exportSqliteCommentsItem);

                ToolStripMenuItem importSqliteCommentsItem = new ToolStripMenuItem(Localization.T("Tool.ImportSqliteColumnComments"));
                importSqliteCommentsItem.Click += (s, ev) => ImportSqliteColumnCommentsWithDialog(null);
                menu.Items.Add(importSqliteCommentsItem);
            }

            menu.Items.Add(CreateAutoCommentModeMenuItem(mode => FillSelectedDatabaseComments(node, mode)));

            ToolStripMenuItem reverseModelItem = new ToolStripMenuItem(Localization.T("Tool.ReverseEngineerModel"));
            reverseModelItem.Click += (s, ev) => ReverseEngineerSelectedDatabaseModel();
            menu.Items.Add(reverseModelItem);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem shareItem = new ToolStripMenuItem(Localization.T("Menu.Share"));
            shareItem.Click += (s, ev) => ShareSelectedDatabaseConnection(node);
            menu.Items.Add(shareItem);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem refreshItem = new ToolStripMenuItem(Localization.T("Query.Refresh"));
            refreshItem.Click += (s, ev) => RefreshDatabaseObjectNodes(node);
            menu.Items.Add(refreshItem);
        }

        private async void FillSelectedDatabaseComments(TreeNode databaseNode, AutoCommentMode mode)
        {
            TreeDatabaseTarget target = BuildTargetFromNode(databaseNode);
            if (target == null)
            {
                MessageBox.Show(Localization.T("Object.SelectDatabaseOrConnection"), Localization.T("Tool.FillAutoComments"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string confirmKey = mode == AutoCommentMode.Overwrite ? "Database.AutoCommentsConfirmOverwrite" : "Database.AutoCommentsConfirm";
            if (MessageBox.Show(Localization.Format(confirmKey, target.DatabaseName), Localization.T("Tool.FillAutoComments"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            using (RunnerProgressOverlay progressOverlay = RunnerProgressOverlay.Show(this, Localization.T("Tool.FillAutoComments"), Localization.T("Designer.AutoCommentsLoading")))
            {
                try
                {
                    progressOverlay.SetProgress(0, 1, Localization.T("Designer.AutoCommentsLoading"));
                    Dictionary<string, string> comments;
                    try
                    {
                        comments = await TableDesignerForm.GetAutoColumnCommentTask();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(Localization.Format("Designer.AutoCommentsLoadFailed", ex.Message), Localization.T("Tool.FillAutoComments"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    if (comments == null || comments.Count == 0)
                    {
                        MessageBox.Show(Localization.T("Designer.AutoCommentsUnavailable"), Localization.T("Tool.FillAutoComments"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    progressOverlay.SetProgress(0, 1, TableDesignerForm.GetAutoColumnCommentSourceMessage());
                    progressOverlay.SetProgress(0, 1, Localization.T("Database.AutoCommentsScanning"));
                    List<AutoCommentColumnUpdate> updates = await Task.Run(() => BuildDatabaseAutoCommentUpdates(target.Database, target.DatabaseName, comments, mode));
                    if (updates.Count == 0)
                    {
                        string noUpdatesMessage = GetDatabaseAutoCommentNoUpdatesMessage(mode);
                        progressOverlay.SetProgress(1, 1, noUpdatesMessage);
                        MessageBox.Show(noUpdatesMessage, Localization.T("Tool.FillAutoComments"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    int applied = 0;
                    for (int i = 0; i < updates.Count; i++)
                    {
                        AutoCommentColumnUpdate update = updates[i];
                        progressOverlay.SetProgress(i + 1, updates.Count, Localization.Format("Database.AutoCommentsProgress", i + 1, updates.Count, update.TableName, update.ColumnName));
                        await Task.Run(() => ExecuteAutoCommentUpdate(target.Database, update));
                        applied++;
                        await Task.Delay(20);
                    }

                    progressOverlay.SetProgress(updates.Count, updates.Count, Localization.Format("Database.AutoCommentsDone", applied));
                    UpdateMainStatus(Localization.Format("Database.AutoCommentsDone", applied));
                    MessageBox.Show(GetDatabaseAutoCommentAppliedMessage(applied, mode), Localization.T("Tool.FillAutoComments"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    progressOverlay.SetProgress(1, 1, Localization.Format("Database.AutoCommentsFailed", ex.Message));
                    MessageBox.Show(Localization.Format("Database.AutoCommentsFailed", ex.Message), Localization.T("Tool.FillAutoComments"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    await Task.Delay(250);
                }
            }
        }

        private static string GetDatabaseAutoCommentNoUpdatesMessage(AutoCommentMode mode)
        {
            return mode == AutoCommentMode.Overwrite
                ? Localization.T("Database.AutoCommentsNoUpdates")
                : Localization.T("Designer.AutoCommentsNoMatches");
        }

        private static string GetDatabaseAutoCommentAppliedMessage(int applied, AutoCommentMode mode)
        {
            return mode == AutoCommentMode.Overwrite
                ? Localization.Format("Database.AutoCommentsUpdated", applied)
                : Localization.Format("Database.AutoCommentsApplied", applied);
        }

        private static List<AutoCommentColumnUpdate> BuildDatabaseAutoCommentUpdates(IDatabase db, string databaseName, Dictionary<string, string> comments, AutoCommentMode mode)
        {
            List<AutoCommentColumnUpdate> updates = new List<AutoCommentColumnUpdate>();
            HashSet<string> viewNames = GetDatabaseViewNameSet(db, databaseName);
            foreach (string tableName in GetTablesSafe(db, databaseName))
            {
                if (viewNames.Contains(tableName)) continue;

                DataTable columns = db.GetColumns(databaseName, tableName);
                foreach (DataRow row in columns.Rows)
                {
                    string columnName = FirstColumnValue(row, "Field", "Name", "NAME", "COLUMN_NAME", "column_name", "name");
                    if (string.IsNullOrWhiteSpace(columnName)) continue;

                    string comment;
                    if (!comments.TryGetValue(columnName, out comment) || string.IsNullOrWhiteSpace(comment)) continue;
                    comment = comment.Trim();

                    string currentComment = FirstColumnValue(row, "Comment", "COMMENT", "COMMENTS", "comment");
                    if (mode == AutoCommentMode.FillBlanks && !string.IsNullOrWhiteSpace(currentComment)) continue;
                    if (string.Equals(currentComment, comment, StringComparison.Ordinal)) continue;

                    string sql = BuildAutoCommentSql(db, databaseName, tableName, row, columnName, comment);
                    if (string.IsNullOrWhiteSpace(sql)) continue;

                    updates.Add(new AutoCommentColumnUpdate
                    {
                        TableName = tableName,
                        ColumnName = columnName,
                        Comment = comment,
                        Sql = sql
                    });
                }
            }
            return updates;
        }

        private static HashSet<string> GetDatabaseViewNameSet(IDatabase db, string databaseName)
        {
            HashSet<string> viewNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                List<string> views = GetViewsSafe(db, databaseName);
                if (views == null) return viewNames;
                foreach (string viewName in views)
                {
                    if (!string.IsNullOrWhiteSpace(viewName)) viewNames.Add(viewName);
                }
            }
            catch
            {
            }
            return viewNames;
        }

        private static string BuildAutoCommentSql(IDatabase db, string databaseName, string tableName, DataRow row, string columnName, string comment)
        {
            if (IsDumpProvider(db, "mysql"))
            {
                return BuildMySqlAutoCommentSql(databaseName, tableName, row, columnName, comment);
            }
            if (IsDumpProvider(db, "postgresql") || IsDumpProvider(db, "oracle"))
            {
                return "COMMENT ON COLUMN " + BuildQualifiedObjectName(db, databaseName, tableName) + "." + QuoteDumpIdentifier(db, columnName) +
                       " IS '" + EscapeSqlLiteral(comment) + "';";
            }
            if (IsDumpProvider(db, "mssql") || IsDumpProvider(db, "sqlserver"))
            {
                return BuildSqlServerAutoCommentSql(databaseName, tableName, columnName, comment);
            }
            if (IsDumpProvider(db, "sqlite"))
            {
                return BuildSqliteAutoCommentSql(tableName, columnName, comment);
            }
            return null;
        }

        private static string BuildSqliteAutoCommentSql(string tableName, string columnName, string comment)
        {
            return "CREATE TABLE IF NOT EXISTS " + QuoteSqliteIdentifier(my_sqlite.ColumnCommentTableName) + " (" +
                   "table_name TEXT NOT NULL, " +
                   "column_name TEXT NOT NULL, " +
                   "comment TEXT NOT NULL, " +
                   "PRIMARY KEY (table_name, column_name));" +
                   "\r\nINSERT OR REPLACE INTO " + QuoteSqliteIdentifier(my_sqlite.ColumnCommentTableName) +
                   " (table_name, column_name, comment) VALUES (" +
                   "'" + EscapeSqlLiteral(tableName) + "', " +
                   "'" + EscapeSqlLiteral(columnName) + "', " +
                   "'" + EscapeSqlLiteral(comment ?? "") + "');";
        }

        private static string BuildMySqlAutoCommentSql(string databaseName, string tableName, DataRow row, string columnName, string comment)
        {
            string columnType = FirstColumnValue(row, "Type", "COLUMN_TYPE");
            if (string.IsNullOrWhiteSpace(columnType)) return null;

            string extra = FirstColumnValue(row, "Extra", "EXTRA");
            if (IsMySqlGeneratedColumn(extra)) return null;

            List<string> parts = new List<string>
            {
                QuoteMySqlIdentifier(columnName),
                columnType
            };

            string nullValue = FirstColumnValue(row, "Null", "IS_NULLABLE", "is_nullable");
            parts.Add(IsMySqlNullable(nullValue) ? "NULL" : "NOT NULL");

            string defaultClause = BuildMySqlDefaultClause(row, columnType);
            if (!string.IsNullOrWhiteSpace(defaultClause)) parts.Add(defaultClause);

            string extraClause = BuildMySqlExtraClause(extra);
            if (!string.IsNullOrWhiteSpace(extraClause)) parts.Add(extraClause);

            parts.Add("COMMENT " + EscapeMySqlStringLiteral(comment));

            return "ALTER TABLE " + QuoteMySqlIdentifier(databaseName) + "." + QuoteMySqlIdentifier(tableName) +
                   " MODIFY COLUMN " + string.Join(" ", parts.ToArray()) + ";";
        }

        private static bool IsMySqlNullable(string value)
        {
            return string.Equals(value, "YES", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "Y", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsMySqlGeneratedColumn(string extra)
        {
            string value = extra ?? string.Empty;
            return value.IndexOf("VIRTUAL GENERATED", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("STORED GENERATED", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string BuildMySqlDefaultClause(DataRow row, string columnType)
        {
            object rawValue;
            if (!TryGetRawColumnValue(row, out rawValue, "Default", "COLUMN_DEFAULT", "column_default")) return string.Empty;

            string value = rawValue == null ? "" : rawValue.ToString();
            if (string.Equals(value, "NULL", StringComparison.OrdinalIgnoreCase)) return "DEFAULT NULL";
            return "DEFAULT " + FormatMySqlDefaultValue(columnType, value);
        }

        private static string FormatMySqlDefaultValue(string columnType, string value)
        {
            string trimmed = value ?? string.Empty;
            string upper = trimmed.ToUpperInvariant();
            if (upper == "CURRENT_TIMESTAMP" || upper == "CURRENT_TIMESTAMP()" || upper.StartsWith("CURRENT_TIMESTAMP("))
            {
                return trimmed;
            }
            if ((trimmed.StartsWith("'") && trimmed.EndsWith("'")) ||
                (trimmed.StartsWith("(") && trimmed.EndsWith(")")) ||
                trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("b'", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }

            decimal numericValue;
            string type = (columnType ?? string.Empty).ToLowerInvariant();
            bool numericType = type.Contains("int") || type.Contains("decimal") || type.Contains("numeric") ||
                               type.Contains("float") || type.Contains("double") || type.Contains("real");
            if (numericType && decimal.TryParse(trimmed, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out numericValue))
            {
                return trimmed;
            }

            return EscapeMySqlStringLiteral(trimmed);
        }

        private static string BuildMySqlExtraClause(string extra)
        {
            if (string.IsNullOrWhiteSpace(extra)) return string.Empty;

            List<string> clauses = new List<string>();
            if (extra.IndexOf("auto_increment", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                clauses.Add("AUTO_INCREMENT");
            }

            int onUpdateIndex = extra.IndexOf("on update", StringComparison.OrdinalIgnoreCase);
            if (onUpdateIndex >= 0)
            {
                clauses.Add(extra.Substring(onUpdateIndex).Trim());
            }

            return string.Join(" ", clauses.ToArray());
        }

        private static string BuildSqlServerAutoCommentSql(string databaseName, string tableName, string columnName, string comment)
        {
            string database = "[" + EscapeSqlServerName(databaseName) + "]";
            SqlServerObjectName target = ParseSqlServerObjectName(tableName);
            string schemaLiteral = EscapeSqlLiteral(target.Schema);
            string tableLiteral = EscapeSqlLiteral(target.Name);
            string columnLiteral = EscapeSqlLiteral(columnName);
            string valueLiteral = EscapeSqlLiteral(comment ?? "");

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

            return "IF " + existsSql +
                   " EXEC " + database + ".sys.sp_updateextendedproperty @value=N'" + valueLiteral + "', " + commonArgs +
                   " ELSE EXEC " + database + ".sys.sp_addextendedproperty @value=N'" + valueLiteral + "', " + commonArgs + ";";
        }

        private static bool TryGetRawColumnValue(DataRow row, out object value, params string[] names)
        {
            foreach (string name in names)
            {
                if (TryGetRawColumnValue(row, name, out value)) return true;
            }
            value = null;
            return false;
        }

        private static bool TryGetRawColumnValue(DataRow row, string name, out object value)
        {
            if (row.Table.Columns.Contains(name))
            {
                value = row[name];
                return value != DBNull.Value;
            }
            foreach (DataColumn column in row.Table.Columns)
            {
                if (string.Equals(column.ColumnName, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = row[column];
                    return value != DBNull.Value;
                }
            }
            value = null;
            return false;
        }

        private static string EscapeMySqlStringLiteral(string value)
        {
            return "'" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("'", "\\'") + "'";
        }

        private static void ExecuteAutoCommentUpdate(IDatabase db, AutoCommentColumnUpdate update)
        {
            Dictionary<string, string> result = db.ExecSQL(update.Sql);
            if (result != null && result.ContainsKey("status") && string.Equals(result["status"], "OK", StringComparison.OrdinalIgnoreCase)) return;

            string reason = result != null && result.ContainsKey("reason") ? result["reason"] : "unknown error";
            throw new Exception(update.TableName + "." + update.ColumnName + ": " + reason);
        }

        private void CloseDatabaseNode(TreeNode databaseNode)
        {
            if (databaseNode == null) return;
            string databaseName = databaseNode.Text;
            databaseNode.Nodes.Clear();
            databaseNode.Collapse();
            db_tree.SelectedNode = databaseNode;
            table_top.DataSource = null;
            dgvDetails.DataSource = null;
            rtbDDL.Clear();
            UpdateMainStatus(Localization.Format("Database.Closed", databaseName));
        }

        private void EditSelectedDatabase()
        {
            TreeDatabaseTarget target = GetTargetFromCurrentSelection();
            if (target == null) return;

            db_tree_AfterSelect(db_tree, new TreeViewEventArgs(target.DatabaseNode));
            ShowDatabaseInfo(target.Database, target.DatabaseName);
            UpdateMainStatus(Localization.Format("Database.EditOpened", target.DatabaseName));
        }

        private void CreateDatabaseFromDatabaseNode(TreeNode databaseNode)
        {
            if (databaseNode == null) return;
            TreeNode root = databaseNode;
            while (root.Parent != null && !IsConnectionGroupNode(root.Parent)) root = root.Parent;
            CreateDatabaseFromConnection(root);
        }

        private void DeleteSelectedDatabase()
        {
            TreeDatabaseTarget target = GetTargetFromCurrentSelection();
            if (target == null) return;

            DialogResult result = MessageBox.Show(
                Localization.Format("Database.ConfirmDelete", target.DatabaseName),
                Localization.T("Tool.DeleteDatabase"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (result != DialogResult.Yes) return;

            try
            {
                if (target.Database is my_oracle && !ConfirmOracleSchemaDrop(target.DatabaseName))
                {
                    return;
                }

                if (target.Database is my_sqlite)
                {
                    DeleteSqliteDatabaseFile(target);
                    return;
                }

                string createdBackupPath;
                if (!ConfirmAndCreatePreDeleteBackup(target, out createdBackupPath))
                {
                    return;
                }

                string sql = BuildDropDatabaseSql(target.Database, target.DatabaseName);
                Dictionary<string, string> execResult = target.Database.ExecSQL(sql);
                if (!execResult.ContainsKey("status") || execResult["status"] != "OK")
                {
                    string reason = execResult.ContainsKey("reason") ? execResult["reason"] : Localization.T("Object.UnknownError");
                    throw new Exception(reason);
                }

                TreeNode root = target.DatabaseNode;
                while (root.Parent != null) root = root.Parent;
                target.DatabaseNode.Remove();
                db_tree.SelectedNode = root;
                UpdateMainStatus(string.IsNullOrWhiteSpace(createdBackupPath)
                    ? Localization.Format("Database.Deleted", target.DatabaseName)
                    : Localization.Format("Database.DeletedWithBackup", target.DatabaseName, createdBackupPath));
            }
            catch (Exception ex)
            {
                string message = Localization.Format("Database.DeleteFailed", ex.Message);
                UpdateMainStatus(message);
                MessageBox.Show(message, Localization.T("Tool.DeleteDatabase"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void DeleteSqliteDatabaseFile(TreeDatabaseTarget target)
        {
            string fullPath;
            string reason;
            if (!TryGetSafeSqliteDeletePath(target.ConnectionInfo, target.Database, out fullPath, out reason))
                throw new InvalidOperationException(reason);

            string backupPath = BuildSqlitePreDeleteBackupPath(fullPath, DateTime.Now);
            DialogResult fileResult = MessageBox.Show(
                Localization.Format("Database.SqliteConfirmDeleteFile", fullPath, backupPath),
                Localization.T("Tool.DeleteDatabase"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (fileResult != DialogResult.Yes) return;

            string fileName = Path.GetFileName(fullPath);
            string confirmation = PromptForText(
                Localization.T("Tool.DeleteDatabase"),
                Localization.Format("Database.SqliteConfirmDeletePrompt", fileName),
                "");
            if (confirmation == null) return;

            if (!string.Equals(confirmation.Trim(), fileName, StringComparison.Ordinal))
            {
                string message = Localization.T("Database.SqliteConfirmDeleteMismatch");
                UpdateMainStatus(message);
                MessageBox.Show(message, Localization.T("Tool.DeleteDatabase"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string createdBackupPath = CreateSqlitePreDeleteBackup(target.Database as my_sqlite, fullPath, backupPath);
            createdBackupPath = PreDeleteBackupArchiveService.ArchiveAndPrune(createdBackupPath);
            target.Database.Close();
            System.Data.SQLite.SQLiteConnection.ClearAllPools();
            DeleteSqliteDatabaseFiles(fullPath);

            if (target.ConnectionInfo != null)
            {
                target.ConnectionInfo["isConnect"] = "F";
            }

            TreeNode root = target.DatabaseNode;
            while (root.Parent != null && !IsConnectionGroupNode(root.Parent)) root = root.Parent;
            ApplyConnectionNodeIcon(root, GetConnectionValue(target.ConnectionInfo, "db_kind"), false);
            target.DatabaseNode.Remove();
            db_tree.SelectedNode = root;
            table_top.DataSource = null;
            dgvDetails.DataSource = null;
            rtbDDL.Clear();
            UpdateMainStatus(Localization.Format("Database.DeletedWithBackup", target.DatabaseName, createdBackupPath));
        }

        private bool ConfirmAndCreatePreDeleteBackup(TreeDatabaseTarget target, out string createdBackupPath)
        {
            createdBackupPath = string.Empty;
            if (target == null || target.Database == null) return false;

            string backupPath = BuildLogicalPreDeleteBackupPath(target.DatabaseName, target.Database.ProviderName, DateTime.Now);
            DialogResult backupResult = MessageBox.Show(
                Localization.Format("Database.PreDeleteBackupPrompt", target.DatabaseName, backupPath),
                Localization.T("Tool.DeleteDatabase"),
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button1);

            if (backupResult == DialogResult.Cancel) return false;
            if (backupResult == DialogResult.No) return true;

            try
            {
                CreateDatabaseBackup(target, backupPath);
                createdBackupPath = PreDeleteBackupArchiveService.ArchiveAndPrune(backupPath);
                UpdateMainStatus(Localization.Format("Database.PreDeleteBackupCreated", createdBackupPath));
                return true;
            }
            catch (Exception ex)
            {
                string message = Localization.Format("Database.PreDeleteBackupFailedCancelled", ex.Message);
                UpdateMainStatus(message);
                MessageBox.Show(message, Localization.T("Tool.DeleteDatabase"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        private static bool TryGetSafeSqliteDeletePath(Dictionary<string, object> connInfo, IDatabase db, out string fullPath, out string reason)
        {
            fullPath = string.Empty;
            reason = string.Empty;

            string rawPath = GetSQLiteDatabasePath(connInfo, db);
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                reason = Localization.T("Database.SqlitePathMissing");
                return false;
            }

            try
            {
                fullPath = Path.GetFullPath(rawPath);
            }
            catch
            {
                reason = Localization.Format("Database.SqliteUnsafePath", rawPath);
                return false;
            }

            if (!Path.IsPathRooted(fullPath) || Directory.Exists(fullPath))
            {
                reason = Localization.Format("Database.SqliteUnsafePath", fullPath);
                return false;
            }

            if (!File.Exists(fullPath))
            {
                reason = Localization.Format("Database.SqliteFileMissing", fullPath);
                return false;
            }

            return true;
        }

        private static ConnectionImportPreviewReport BuildConnectionImportPreview(string sourcePath, IEnumerable<Dictionary<string, object>> existingConnections)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                throw new FileNotFoundException(sourcePath);
            }

            ConnectionImportPreviewReport report = new ConnectionImportPreviewReport();
            ReadConnectionImportSignature(sourcePath, report);
            LoadConnectionImportFile(sourcePath, report.ImportedConnections, report.ImportedGroups);

            List<Dictionary<string, object>> existing = existingConnections == null
                ? new List<Dictionary<string, object>>()
                : existingConnections.ToList();
            Dictionary<string, int> existingIndexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < existing.Count; i++)
            {
                string key = BuildConnectionImportKey(existing[i]);
                if (!existingIndexByKey.ContainsKey(key)) existingIndexByKey[key] = i;
            }

            HashSet<int> matchedExisting = new HashSet<int>();
            for (int i = 0; i < report.ImportedConnections.Count; i++)
            {
                Dictionary<string, object> imported = report.ImportedConnections[i];
                string key = BuildConnectionImportKey(imported);
                int existingIndex;
                bool hasExisting = existingIndexByKey.TryGetValue(key, out existingIndex);
                string status;
                bool include = true;

                if (!hasExisting)
                {
                    status = "added";
                    report.Added++;
                }
                else
                {
                    matchedExisting.Add(existingIndex);
                    if (ConnectionImportEquivalent(existing[existingIndex], imported))
                    {
                        status = "unchanged";
                        include = false;
                        report.Unchanged++;
                    }
                    else
                    {
                        status = "updated";
                        report.Updated++;
                    }
                }

                report.Entries.Add(new ConnectionImportPreviewEntry
                {
                    Include = include,
                    Status = status,
                    ImportedIndex = i,
                    ExistingIndex = hasExisting ? existingIndex : -1,
                    Key = key,
                    ConnectionName = GetConnectionValue(imported, "conn_name"),
                    DbKind = GetConnectionValue(imported, "db_kind"),
                    Target = BuildImportedConnectionPasswordTargetText(imported)
                });
            }

            for (int i = 0; i < existing.Count; i++)
            {
                if (matchedExisting.Contains(i)) continue;
                report.ExistingOnly++;
                Dictionary<string, object> conn = existing[i];
                report.Entries.Add(new ConnectionImportPreviewEntry
                {
                    Include = false,
                    Status = "existingOnly",
                    ImportedIndex = -1,
                    ExistingIndex = i,
                    Key = BuildConnectionImportKey(conn),
                    ConnectionName = GetConnectionValue(conn, "conn_name"),
                    DbKind = GetConnectionValue(conn, "db_kind"),
                    Target = BuildImportedConnectionPasswordTargetText(conn)
                });
            }

            return report;
        }

        private static void LoadConnectionImportFile(string sourcePath, List<Dictionary<string, object>> connections, List<string> groups)
        {
            JToken root = JToken.Parse(File.ReadAllText(sourcePath, Encoding.UTF8));
            if (root.Type == JTokenType.Array)
            {
                foreach (JToken token in (JArray)root) AddImportedConnectionToken(token, connections);
                return;
            }

            JObject obj = root as JObject;
            if (obj == null) return;
            JArray connArray = obj["connections"] as JArray;
            if (connArray != null)
            {
                foreach (JToken token in connArray) AddImportedConnectionToken(token, connections);
            }

            JArray groupArray = obj["groups"] as JArray;
            if (groupArray != null)
            {
                foreach (JToken token in groupArray)
                {
                    string groupName = token.ToString();
                    if (!string.IsNullOrWhiteSpace(groupName) && !groups.Contains(groupName)) groups.Add(groupName);
                }
            }
        }

        private static void SignConnectionExportFile(string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath)) return;

            JObject root = JToken.Parse(File.ReadAllText(targetPath, Encoding.UTF8)) as JObject;
            if (root == null) return;

            JObject metadata = root["exportMetadata"] as JObject ?? new JObject();
            metadata["formatVersion"] = 1;
            metadata["app"] = "mySQLPunk";
            metadata["exportedAtUtc"] = DateTime.UtcNow.ToString("o");
            metadata["sourceId"] = GetConnectionExportSourceId();
            metadata.Remove("signatureSha256");
            root["exportMetadata"] = metadata;

            metadata["signatureSha256"] = ComputeConnectionImportSignature(root);
            File.WriteAllText(targetPath, root.ToString(Formatting.Indented), new UTF8Encoding(false));
        }

        private static void ReadConnectionImportSignature(string sourcePath, ConnectionImportPreviewReport report)
        {
            if (report == null || string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return;

            JObject root = JToken.Parse(File.ReadAllText(sourcePath, Encoding.UTF8)) as JObject;
            JObject metadata = root?["exportMetadata"] as JObject;
            if (metadata == null) return;

            string signature = metadata.Value<string>("signatureSha256");
            report.SourceExportedAtUtc = metadata.Value<string>("exportedAtUtc") ?? "";
            report.SourceId = metadata.Value<string>("sourceId") ?? "";
            report.SourceSignature = signature ?? "";
            report.SourceSignaturePresent = !string.IsNullOrWhiteSpace(signature);
            report.SourceSignatureValid = report.SourceSignaturePresent &&
                                          string.Equals(signature, ComputeConnectionImportSignature(root), StringComparison.OrdinalIgnoreCase);
            report.SourceTrusted = report.SourceSignatureValid &&
                                   IsConnectionImportSourceTrusted(report.SourceId, BuildTrustedConnectionImportSourcesPath());
        }

        private static string BuildConnectionExportSourceIdPath()
        {
            return Path.Combine(GetConnectionTrustStoreDirectory(), ConnectionExportSourceIdFileName);
        }

        private static string BuildTrustedConnectionImportSourcesPath()
        {
            return Path.Combine(GetConnectionTrustStoreDirectory(), TrustedConnectionImportSourcesFileName);
        }

        private static string GetConnectionTrustStoreDirectory()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(Application.UserAppDataPath))
                {
                    return Application.UserAppDataPath;
                }
            }
            catch
            {
            }

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(appData)) appData = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(appData, "mySQLPunk");
        }

        private static string GetConnectionExportSourceId()
        {
            string path = BuildConnectionExportSourceIdPath();
            try
            {
                if (File.Exists(path))
                {
                    string existing = File.ReadAllText(path, Encoding.UTF8).Trim();
                    if (IsValidConnectionImportSourceId(existing)) return existing;
                }

                string sourceId = Guid.NewGuid().ToString("N");
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, sourceId, new UTF8Encoding(false));
                return sourceId;
            }
            catch
            {
                return Guid.NewGuid().ToString("N");
            }
        }

        private static bool IsValidConnectionImportSourceId(string sourceId)
        {
            if (string.IsNullOrWhiteSpace(sourceId)) return false;
            string text = sourceId.Trim();
            Guid parsed;
            return Guid.TryParse(text, out parsed) || text.Length >= 16;
        }

        private static bool IsConnectionImportSourceTrusted(string sourceId, string trustedSourcesPath)
        {
            if (!IsValidConnectionImportSourceId(sourceId)) return false;
            return LoadTrustedConnectionImportSourceIds(trustedSourcesPath).Contains(sourceId.Trim());
        }

        private static HashSet<string> LoadTrustedConnectionImportSourceIds(string trustedSourcesPath)
        {
            HashSet<string> trusted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(trustedSourcesPath) || !File.Exists(trustedSourcesPath)) return trusted;

            try
            {
                JToken root = JToken.Parse(File.ReadAllText(trustedSourcesPath, Encoding.UTF8));
                JArray sources = root as JArray ?? root["sources"] as JArray;
                if (sources == null) return trusted;

                foreach (JToken token in sources)
                {
                    string sourceId = token.Type == JTokenType.String
                        ? token.ToString()
                        : token.Value<string>("sourceId");
                    if (IsValidConnectionImportSourceId(sourceId)) trusted.Add(sourceId.Trim());
                }
            }
            catch
            {
            }

            return trusted;
        }

        private static void TrustConnectionImportSource(string sourceId, string signature, string trustedSourcesPath)
        {
            if (!IsValidConnectionImportSourceId(sourceId))
            {
                throw new ArgumentException(Localization.T("Connection.ImportTrustInvalidSourceId"));
            }

            if (string.IsNullOrWhiteSpace(trustedSourcesPath))
            {
                throw new ArgumentException(Localization.T("Connection.ImportTrustStoreUnavailable"));
            }

            JObject root = new JObject
            {
                ["formatVersion"] = 1,
                ["sources"] = new JArray()
            };

            if (File.Exists(trustedSourcesPath))
            {
                try
                {
                    JObject existing = JToken.Parse(File.ReadAllText(trustedSourcesPath, Encoding.UTF8)) as JObject;
                    if (existing != null) root = existing;
                }
                catch
                {
                }
            }

            JArray sources = root["sources"] as JArray;
            if (sources == null)
            {
                sources = new JArray();
                root["sources"] = sources;
            }

            JObject entry = null;
            foreach (JToken token in sources)
            {
                JObject obj = token as JObject;
                if (obj == null) continue;
                if (string.Equals(obj.Value<string>("sourceId"), sourceId.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    entry = obj;
                    break;
                }
            }

            if (entry == null)
            {
                entry = new JObject { ["sourceId"] = sourceId.Trim() };
                sources.Add(entry);
            }

            entry["trustedAtUtc"] = DateTime.UtcNow.ToString("o");
            if (!string.IsNullOrWhiteSpace(signature)) entry["lastSignatureSha256"] = signature.Trim();

            string dir = Path.GetDirectoryName(trustedSourcesPath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(trustedSourcesPath, root.ToString(Formatting.Indented), new UTF8Encoding(false));
        }

        private static string ComputeConnectionImportSignature(JToken root)
        {
            if (root == null) return "";

            JToken clone = root.DeepClone();
            JObject obj = clone as JObject;
            JObject metadata = obj?["exportMetadata"] as JObject;
            metadata?.Remove("signatureSha256");

            string canonical = CanonicalizeJsonToken(clone).ToString(Formatting.None);
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                StringBuilder sb = new StringBuilder(bytes.Length * 2);
                foreach (byte b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static JToken CanonicalizeJsonToken(JToken token)
        {
            JObject obj = token as JObject;
            if (obj != null)
            {
                JObject sorted = new JObject();
                foreach (JProperty property in obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    sorted[property.Name] = CanonicalizeJsonToken(property.Value);
                }
                return sorted;
            }

            JArray array = token as JArray;
            if (array != null)
            {
                JArray normalized = new JArray();
                foreach (JToken item in array)
                {
                    normalized.Add(CanonicalizeJsonToken(item));
                }
                return normalized;
            }

            return token == null ? JValue.CreateNull() : token.DeepClone();
        }

        private static void AddImportedConnectionToken(JToken token, List<Dictionary<string, object>> connections)
        {
            if (token == null) return;
            if (token.Type == JTokenType.Array)
            {
                foreach (JToken child in (JArray)token) AddImportedConnectionToken(child, connections);
                return;
            }

            Dictionary<string, object> conn = token.ToObject<Dictionary<string, object>>();
            if (conn == null) return;
            NormalizeImportedConnectionForPreview(conn);
            connections.Add(conn);
        }

        private static void NormalizeImportedConnectionForPreview(Dictionary<string, object> conn)
        {
            CopyImportedConnectionValueIfMissing(conn, "name", "conn_name");
            CopyImportedConnectionValueIfMissing(conn, "ip", "host");
            CopyImportedConnectionValueIfMissing(conn, "kind", "db_kind");
            CopyImportedConnectionValueIfMissing(conn, "login_id", "username");

            if (!conn.ContainsKey("initial_database")) conn["initial_database"] = "";
            if (!conn.ContainsKey("trusted_connection")) conn["trusted_connection"] = "F";
            if (!conn.ContainsKey("conn_group")) conn["conn_group"] = "";
            if (!conn.ContainsKey("credential_target")) conn["credential_target"] = "";
            conn["username"] = TryDecryptConnectionImportValue(GetConnectionValue(conn, "username"));
            conn["pwd"] = TryDecryptConnectionImportValue(GetConnectionValue(conn, "pwd"));
            conn["isConnect"] = "F";
            conn["pdo"] = null;
        }

        private static void CopyImportedConnectionValueIfMissing(Dictionary<string, object> conn, string oldKey, string newKey)
        {
            if (!conn.ContainsKey(newKey) && conn.ContainsKey(oldKey)) conn[newKey] = conn[oldKey];
        }

        private static string TryDecryptConnectionImportValue(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            try { return Crypto.Decrypt(value); }
            catch { return value; }
        }

        private static string BuildConnectionImportKey(Dictionary<string, object> conn)
        {
            return string.Join("|", new[]
            {
                GetConnectionValue(conn, "conn_name"),
                GetConnectionValue(conn, "db_kind"),
                GetConnectionValue(conn, "host"),
                GetConnectionValue(conn, "port"),
                GetConnectionValue(conn, "path"),
                GetConnectionValue(conn, "username"),
                GetConnectionValue(conn, "tns_name"),
                GetConnectionValue(conn, "service_name"),
                GetConnectionValue(conn, "sid")
            }).ToLowerInvariant();
        }

        private static bool ConnectionImportEquivalent(Dictionary<string, object> left, Dictionary<string, object> right)
        {
            string[] keys =
            {
                "conn_name", "db_kind", "host", "port", "initial_database", "path",
                "username", "trusted_connection", "init_geospatial", "connection_type",
                "tns_name", "service_name", "sid", "oracle_identifier_type", "conn_group"
            };
            foreach (string key in keys)
            {
                if (!string.Equals(GetConnectionValue(left, key), GetConnectionValue(right, key), StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        private static string BuildLogicalPreDeleteBackupPath(string databaseName, string providerName, DateTime timestamp)
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documents)) documents = AppDomain.CurrentDomain.BaseDirectory;
            string directory = Path.Combine(documents, "mySQLPunk", "pre-delete-backups");
            string safeDatabaseName = SanitizeBackupFileNamePart(databaseName);
            string safeProviderName = SanitizeBackupFileNamePart(providerName);
            string stamp = timestamp.ToString("yyyyMMdd_HHmmss");
            string fileName = safeDatabaseName + "_" + safeProviderName + "_before_delete_" + stamp + ".sql";
            return BuildUniqueFilePath(directory, fileName);
        }

        private static string BuildPreRestoreBackupPath(string databaseName, string providerName, DateTime timestamp)
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documents)) documents = AppDomain.CurrentDomain.BaseDirectory;
            string directory = Path.Combine(documents, "mySQLPunk", "pre-restore-backups");
            string safeDatabaseName = SanitizeBackupFileNamePart(databaseName);
            string safeProviderName = SanitizeBackupFileNamePart(providerName);
            string stamp = timestamp.ToString("yyyyMMdd_HHmmss");
            string extension = string.Equals(providerName, "sqlite", StringComparison.OrdinalIgnoreCase) ? ".sqlite" : ".sql";
            string fileName = safeDatabaseName + "_" + safeProviderName + "_before_restore_" + stamp + extension;
            return BuildUniqueFilePath(directory, fileName);
        }

        private static string SanitizeBackupFileNamePart(string value)
        {
            string text = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text)) text = "database";

            char[] invalidChars = Path.GetInvalidFileNameChars();
            StringBuilder builder = new StringBuilder();
            foreach (char ch in text)
            {
                bool invalid = false;
                foreach (char invalidChar in invalidChars)
                {
                    if (ch == invalidChar)
                    {
                        invalid = true;
                        break;
                    }
                }

                builder.Append(invalid || char.IsWhiteSpace(ch) ? '_' : ch);
            }

            string result = builder.ToString().Trim('_');
            return string.IsNullOrWhiteSpace(result) ? "database" : result;
        }

        private static string BuildUniqueFilePath(string directory, string fileName)
        {
            string basePath = Path.Combine(directory, fileName);
            if (!File.Exists(basePath)) return basePath;

            string name = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);
            int suffix = 2;
            string candidate;
            do
            {
                candidate = Path.Combine(directory, name + "_" + suffix + extension);
                suffix++;
            }
            while (File.Exists(candidate));

            return candidate;
        }

        private static string BuildSqlitePreDeleteBackupPath(string fullPath, DateTime timestamp)
        {
            string directory = Path.GetDirectoryName(fullPath);
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fullPath);
            string extension = Path.GetExtension(fullPath);
            if (string.IsNullOrWhiteSpace(fileNameWithoutExtension)) fileNameWithoutExtension = "sqlite_database";
            if (string.IsNullOrWhiteSpace(extension)) extension = ".sqlite";

            string stamp = timestamp.ToString("yyyyMMdd_HHmmss");
            string basePath = Path.Combine(directory, fileNameWithoutExtension + "_before_delete_" + stamp + extension);
            string candidate = basePath;
            int suffix = 2;
            while (File.Exists(candidate))
            {
                candidate = Path.Combine(directory, fileNameWithoutExtension + "_before_delete_" + stamp + "_" + suffix + extension);
                suffix++;
            }

            return candidate;
        }

        private static string CreateSqlitePreDeleteBackup(my_sqlite sqlite, string fullPath, string backupPath)
        {
            if (sqlite == null) throw new InvalidOperationException(Localization.T("Database.SqliteBackupBeforeDeleteConnectionMissing"));
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath)) throw new FileNotFoundException(Localization.Format("Database.SqliteFileMissing", fullPath), fullPath);
            if (string.IsNullOrWhiteSpace(backupPath)) throw new ArgumentException("backupPath");

            string backupDir = Path.GetDirectoryName(backupPath);
            if (!string.IsNullOrWhiteSpace(backupDir) && !Directory.Exists(backupDir))
            {
                Directory.CreateDirectory(backupDir);
            }
            if (File.Exists(backupPath)) File.Delete(backupPath);

            if (sqlite.MCT != null && sqlite.MCT.State == ConnectionState.Open)
            {
                using (System.Data.SQLite.SQLiteConnection destination = new System.Data.SQLite.SQLiteConnection("Data Source=" + backupPath + ";Version=3;"))
                {
                    destination.Open();
                    sqlite.MCT.BackupDatabase(destination, "main", "main", -1, null, 0);
                }
            }
            else
            {
                File.Copy(fullPath, backupPath, false);
            }

            return backupPath;
        }

        private static void DeleteSqliteDatabaseFiles(string fullPath)
        {
            foreach (string companionPath in new[] { fullPath + "-wal", fullPath + "-shm", fullPath + "-journal" })
            {
                MoveFileToRecycleBinOrDelete(companionPath);
            }

            MoveFileToRecycleBinOrDelete(fullPath);
        }

        private static void MoveFileToRecycleBinOrDelete(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            catch
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private bool ConfirmOracleSchemaDrop(string schemaName)
        {
            if (IsProtectedOracleSchema(schemaName))
            {
                string message = Localization.Format("Database.OracleProtectedSchema", schemaName);
                UpdateMainStatus(message);
                MessageBox.Show(message, Localization.T("Tool.DeleteDatabase"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            MessageBox.Show(Localization.T("Database.OracleUnsupportedDelete"), Localization.T("Tool.DeleteDatabase"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            string confirmation = PromptForText(
                Localization.T("Tool.DeleteDatabase"),
                Localization.Format("Database.OracleConfirmDropPrompt", schemaName),
                "");
            if (confirmation == null) return false;

            if (!string.Equals(confirmation.Trim(), schemaName, StringComparison.Ordinal))
            {
                string message = Localization.T("Database.OracleConfirmDropMismatch");
                UpdateMainStatus(message);
                MessageBox.Show(message, Localization.T("Tool.DeleteDatabase"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            return true;
        }

        private static string BuildDropDatabaseSql(IDatabase db, string databaseName)
        {
            string provider = db == null ? "" : db.ProviderName;
            if (string.Equals(provider, "mysql", StringComparison.OrdinalIgnoreCase))
                return "DROP DATABASE " + QuoteDatabaseIdentifier(databaseName, "`", "`") + ";";
            if (string.Equals(provider, "postgresql", StringComparison.OrdinalIgnoreCase))
                return "DROP DATABASE " + QuoteDatabaseIdentifier(databaseName, "\"", "\"") + ";";
            if (string.Equals(provider, "mssql", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(provider, "sqlserver", StringComparison.OrdinalIgnoreCase))
                return "DROP DATABASE " + QuoteDatabaseIdentifier(databaseName, "[", "]") + ";";
            if (string.Equals(provider, "oracle", StringComparison.OrdinalIgnoreCase))
            {
                if (IsProtectedOracleSchema(databaseName))
                    throw new NotSupportedException(Localization.Format("Database.OracleProtectedSchema", databaseName));
                return "DROP USER " + QuoteOracleIdentifier(databaseName) + " CASCADE";
            }
            if (string.Equals(provider, "sqlite", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException(Localization.T("Database.SqliteUnsupportedDelete"));

            throw new NotSupportedException(Localization.Format("Database.UnsupportedDelete", provider));
        }

        private static bool IsProtectedOracleSchema(string schemaName)
        {
            string normalized = (schemaName ?? string.Empty).Trim().ToUpperInvariant();
            return normalized == "SYS" ||
                   normalized == "SYSTEM" ||
                   normalized == "XDB" ||
                   normalized == "CTXSYS" ||
                   normalized == "MDSYS" ||
                   normalized == "ORDSYS" ||
                   normalized == "OUTLN" ||
                   normalized == "DBSNMP";
        }

        private void OpenSelectedDatabaseCommandLine(TreeNode databaseNode)
        {
            if (databaseNode == null) return;
            TreeNode root = databaseNode;
            while (root.Parent != null && !IsConnectionGroupNode(root.Parent)) root = root.Parent;
            int cliIdx = GetConnectionIndex(root);
            if (cliIdx >= 0) OpenDatabaseCommandLine(cliIdx, databaseNode.Text);
        }

        private void OpenSelectedDatabaseDictionary()
        {
            SelectDatabaseGroupNode("Models");
        }

        private void OpenSqliteSpecialObjectWizard(TreeNode databaseNode)
        {
            TreeDatabaseTarget target = BuildTargetFromNode(databaseNode);
            if (target == null || !(target.Database is my_sqlite))
            {
                MessageBox.Show(Localization.T("Object.SelectDatabaseOrConnection"), Localization.T("Tool.SqliteSpecialObjectWizard"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (SqliteSpecialObjectWizardForm form = new SqliteSpecialObjectWizardForm(target.Database, target.DatabaseName))
            {
                form.ShowDialog(this);
            }
            RefreshDatabaseObjectNodes(databaseNode);
        }

        private void ReverseEngineerSelectedDatabaseModel()
        {
            TreeDatabaseTarget target = GetTargetFromCurrentSelection();
            if (target == null) return;

            EnsureDatabaseGroupNodes(target.DatabaseNode);
            SelectDatabaseGroupNode("Models");
            ShowDatabaseModel(target.Database, target.DatabaseName, "Schema Overview");
            UpdateMainStatus(Localization.Format("Database.ModelOpened", target.DatabaseName));
        }

        private void ShareSelectedDatabaseConnection(TreeNode databaseNode)
        {
            if (databaseNode == null) return;
            TreeNode root = databaseNode;
            while (root.Parent != null && !IsConnectionGroupNode(root.Parent)) root = root.Parent;
            int shareIdx = GetConnectionIndex(root);
            if (shareIdx >= 0) ShareConnection(shareIdx);
        }

        private void AddConnectionRootMenuItems(ContextMenuStrip menu, TreeNode node)
        {
            int connIdx = GetConnectionIndex(node);
            if (connIdx < 0 || connIdx >= myN.connections.Count) return;

            bool isConnected = IsConnectionOpen(connIdx);

            ToolStripMenuItem openItem = new ToolStripMenuItem(Localization.T("Menu.OpenConnection"));
            openItem.Click += (s, ev) => OpenConnectionNode(node);
            menu.Items.Add(openItem);

            ToolStripMenuItem profileItem = new ToolStripMenuItem(Localization.T("Menu.ConnectionProfile"));
            AddConnectionProfileMenuItems(profileItem);
            menu.Items.Add(profileItem);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem editItem = new ToolStripMenuItem(Localization.T("Tool.EditConnection"));
            editItem.Click += (s, ev) => db_tree_edit_connection(connIdx);
            menu.Items.Add(editItem);

            ToolStripMenuItem newItem = new ToolStripMenuItem(Localization.T("Menu.NewConnection"));
            newItem.Click += (s, ev) => ShowConnectionTypeSelection();
            menu.Items.Add(newItem);

            ToolStripMenuItem deleteItem = new ToolStripMenuItem(Localization.T("Tool.DeleteConnection"));
            deleteItem.Click += (s, ev) => db_tree_delete_connection(connIdx);
            menu.Items.Add(deleteItem);

            ToolStripMenuItem duplicateItem = new ToolStripMenuItem(Localization.T("Tool.CopyConnection"));
            duplicateItem.Click += (s, ev) => DuplicateConnection(connIdx);
            menu.Items.Add(duplicateItem);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem newDatabaseItem = new ToolStripMenuItem(Localization.T("Tool.NewDatabase"));
            newDatabaseItem.Enabled = isConnected;
            newDatabaseItem.Click += (s, ev) => CreateDatabaseFromConnection(node);
            menu.Items.Add(newDatabaseItem);

            ToolStripMenuItem newQueryItem = new ToolStripMenuItem(Localization.T("Toolbar.NewQuery"));
            newQueryItem.Enabled = isConnected;
            newQueryItem.Click += (s, ev) => Query_btn_Click(s, ev);
            menu.Items.Add(newQueryItem);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem commandLineItem = new ToolStripMenuItem(Localization.T("Tool.CommandLine"));
            commandLineItem.Enabled = isConnected;
            commandLineItem.Click += (s, ev) => OpenDatabaseCommandLine(connIdx);
            menu.Items.Add(commandLineItem);

            ToolStripMenuItem executeSqlFileItem = new ToolStripMenuItem(Localization.T("Tool.ExecuteSqlFile"));
            executeSqlFileItem.Enabled = isConnected;
            executeSqlFileItem.Click += (s, ev) => ImportSqlWithDialog();
            menu.Items.Add(executeSqlFileItem);

            ToolStripMenuItem sortItem = new ToolStripMenuItem(Localization.T("Menu.Sort"));
            ToolStripMenuItem sortAscItem = new ToolStripMenuItem(Localization.T("Menu.SortAscending"));
            sortAscItem.Click += (s, ev) => SortConnectionDatabaseNodes(node, true);
            ToolStripMenuItem sortDescItem = new ToolStripMenuItem(Localization.T("Menu.SortDescending"));
            sortDescItem.Click += (s, ev) => SortConnectionDatabaseNodes(node, false);
            sortItem.DropDownItems.AddRange(new ToolStripItem[] { sortAscItem, sortDescItem });
            menu.Items.Add(sortItem);

            menu.Items.Add(new ToolStripSeparator());

            bool isFavorite = _favoriteNodePaths.Any(p => string.Equals(p, node.FullPath, StringComparison.OrdinalIgnoreCase));
            ToolStripMenuItem starItem = new ToolStripMenuItem(isFavorite ? Localization.T("Menu.UnstarConnection") : Localization.T("Menu.StarConnection"));
            starItem.Click += (s, ev) => ToggleConnectionFavorite(node);
            menu.Items.Add(starItem);

            AddConnectionColorMenu(menu, node);

            // 群組管理：移至群組 / 移出群組
            ToolStripMenuItem moveToGroupItem = new ToolStripMenuItem(Localization.T("Menu.MoveToGroup"));
            moveToGroupItem.Click += (s, ev) => ShowMoveToGroupDialog(connIdx);
            menu.Items.Add(moveToGroupItem);

            string currentGroup = GetConnectionGroupName(connIdx);
            if (!string.IsNullOrWhiteSpace(currentGroup))
            {
                ToolStripMenuItem removeFromGroupItem = new ToolStripMenuItem(Localization.T("Menu.RemoveFromGroup"));
                removeFromGroupItem.Click += (s, ev) => MoveConnectionToGroup(connIdx, "");
                menu.Items.Add(removeFromGroupItem);
            }

            ToolStripMenuItem shareItem = new ToolStripMenuItem(Localization.T("Menu.Share"));
            shareItem.Click += (s, ev) => ShareConnection(connIdx);
            menu.Items.Add(shareItem);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem refreshItem = new ToolStripMenuItem(Localization.T("Query.Refresh"));
            refreshItem.Enabled = isConnected;
            refreshItem.Click += (s, ev) => RefreshConnectionDatabaseNodes(node);
            menu.Items.Add(refreshItem);
        }

        private void AddConnectionProfileMenuItems(ToolStripMenuItem profileItem)
        {
            string activeProfile = myN.ActiveProfileName;
            ToolStripMenuItem currentProfileItem = new ToolStripMenuItem(
                Localization.Format("Connection.CurrentProfileName", GetProfileDisplayName(activeProfile)));
            currentProfileItem.Enabled = false;
            profileItem.DropDownItems.Add(currentProfileItem);
            profileItem.DropDownItems.Add(new ToolStripSeparator());

            foreach (string profileName in myN.GetProfileNames())
            {
                ToolStripMenuItem item = new ToolStripMenuItem(GetProfileDisplayName(profileName));
                item.Checked = string.Equals(profileName, activeProfile, StringComparison.OrdinalIgnoreCase);
                string targetProfile = profileName;
                item.Click += (s, ev) => SwitchConnectionProfile(targetProfile);
                profileItem.DropDownItems.Add(item);
            }

            profileItem.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem newProfileItem = new ToolStripMenuItem(Localization.T("Connection.NewProfile"));
            newProfileItem.Click += (s, ev) => CreateConnectionProfile();
            profileItem.DropDownItems.Add(newProfileItem);

            ToolStripMenuItem copyProfileItem = new ToolStripMenuItem(Localization.T("Connection.CopyProfile"));
            copyProfileItem.Click += (s, ev) => CopyConnectionProfile(activeProfile);
            profileItem.DropDownItems.Add(copyProfileItem);

            bool isDefaultProfile = string.Equals(activeProfile, mySQLPunk_main.DefaultProfileName, StringComparison.OrdinalIgnoreCase);
            ToolStripMenuItem renameProfileItem = new ToolStripMenuItem(Localization.T("Connection.RenameProfile"));
            renameProfileItem.Enabled = !isDefaultProfile;
            renameProfileItem.Click += (s, ev) => RenameConnectionProfile(activeProfile);
            profileItem.DropDownItems.Add(renameProfileItem);

            ToolStripMenuItem deleteProfileItem = new ToolStripMenuItem(Localization.T("Connection.DeleteProfile"));
            deleteProfileItem.Enabled = !isDefaultProfile;
            deleteProfileItem.Click += (s, ev) => DeleteConnectionProfile(activeProfile);
            profileItem.DropDownItems.Add(deleteProfileItem);
        }

        private string GetProfileDisplayName(string profileName)
        {
            return string.Equals(profileName, mySQLPunk_main.DefaultProfileName, StringComparison.OrdinalIgnoreCase)
                ? Localization.T("Connection.DefaultProfile")
                : profileName;
        }

        private void SwitchConnectionProfile(string profileName)
        {
            if (string.IsNullOrWhiteSpace(profileName)) return;
            if (string.Equals(profileName, myN.ActiveProfileName, StringComparison.OrdinalIgnoreCase)) return;

            myN.setSettingINI();
            CloseAllConnectionsBeforeImport();
            myN.SwitchProfile(profileName);
            drawLists();
            ConfigureMainMenu();
            UpdateMainStatus(Localization.Format("Connection.ProfileSwitched", GetProfileDisplayName(profileName)));
        }

        private void CreateConnectionProfile()
        {
            string profileName = PromptForText(
                Localization.T("Connection.NewProfile"),
                Localization.T("Connection.ProfileNamePrompt"),
                "");
            if (string.IsNullOrWhiteSpace(profileName)) return;

            profileName = profileName.Trim();
            if (myN.GetProfileNames().Contains(profileName, StringComparer.OrdinalIgnoreCase))
            {
                string message = Localization.Format("Connection.ProfileExists", profileName);
                UpdateMainStatus(message);
                MessageBox.Show(message, Localization.T("Connection.NewProfile"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            myN.setSettingINI();
            CloseAllConnectionsBeforeImport();
            myN.CreateProfile(profileName);
            drawLists();
            ConfigureMainMenu();
            UpdateMainStatus(Localization.Format("Connection.ProfileCreated", profileName));
        }

        private void CopyConnectionProfile(string sourceProfile)
        {
            string defaultName = GetProfileDisplayName(sourceProfile) + " Copy";
            string profileName = PromptForText(
                Localization.T("Connection.CopyProfile"),
                Localization.T("Connection.ProfileNamePrompt"),
                defaultName);
            if (string.IsNullOrWhiteSpace(profileName)) return;

            profileName = profileName.Trim();
            if (myN.GetProfileNames().Contains(profileName, StringComparer.OrdinalIgnoreCase))
            {
                string message = Localization.Format("Connection.ProfileExists", profileName);
                UpdateMainStatus(message);
                MessageBox.Show(message, Localization.T("Connection.CopyProfile"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            myN.setSettingINI();
            myN.CopyProfile(sourceProfile, profileName);
            ConfigureMainMenu();
            UpdateMainStatus(Localization.Format("Connection.ProfileCopied", profileName));
        }

        private void RenameConnectionProfile(string oldProfile)
        {
            string profileName = PromptForText(
                Localization.T("Connection.RenameProfile"),
                Localization.T("Connection.ProfileNamePrompt"),
                oldProfile);
            if (string.IsNullOrWhiteSpace(profileName)) return;

            profileName = profileName.Trim();
            if (string.Equals(profileName, oldProfile, StringComparison.OrdinalIgnoreCase)) return;
            if (myN.GetProfileNames().Contains(profileName, StringComparer.OrdinalIgnoreCase))
            {
                string message = Localization.Format("Connection.ProfileExists", profileName);
                UpdateMainStatus(message);
                MessageBox.Show(message, Localization.T("Connection.RenameProfile"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            myN.setSettingINI();
            myN.RenameProfile(oldProfile, profileName);
            ConfigureMainMenu();
            UpdateMainStatus(Localization.Format("Connection.ProfileRenamed", oldProfile, profileName));
        }

        private void DeleteConnectionProfile(string profileName)
        {
            DialogResult confirm = MessageBox.Show(
                Localization.Format("Connection.ConfirmDeleteProfile", GetProfileDisplayName(profileName)),
                Localization.T("Connection.DeleteProfile"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            CloseAllConnectionsBeforeImport();
            myN.DeleteProfile(profileName);
            drawLists();
            ConfigureMainMenu();
            UpdateMainStatus(Localization.Format("Connection.ProfileDeleted", GetProfileDisplayName(profileName)));
        }

        private bool IsConnectionOpen(int index)
        {
            if (index < 0 || index >= myN.connections.Count) return false;
            Dictionary<string, object> conn = myN.connections[index];
            return conn.ContainsKey("isConnect") && conn["isConnect"].ToString() == "T" &&
                   conn.ContainsKey("pdo") && conn["pdo"] is IDatabase;
        }

        private void OpenConnectionNode(TreeNode node)
        {
            if (node == null) return;
            db_tree.SelectedNode = node;
            int connIdx = GetConnectionIndex(node);
            if (IsConnectionOpen(connIdx))
            {
                if (node.Nodes.Count == 0)
                {
                    RefreshConnectionDatabaseNodes(node);
                }

                node.Expand();
                UpdateMainStatus(Localization.Format("Status.ConnectionAlreadyOpen", node.Text));
                return;
            }

            db_tree_DoubleClick(db_tree, EventArgs.Empty);
        }

        private void DuplicateConnection(int index)
        {
            if (index < 0 || index >= myN.connections.Count) return;

            Dictionary<string, object> source = myN.connections[index];
            Dictionary<string, object> copy = new Dictionary<string, object>(source);
            copy["conn_name"] = GetUniqueConnectionName(GetConnectionValue(source, "conn_name") + " Copy");
            copy["isConnect"] = "F";
            copy["pdo"] = null;
            copy.Remove("connString");

            myN.connections.Add(copy);
            RememberRecentConnectionType(copy);
            myN.setSettingINI();
            drawLists();
            UpdateMainStatus(Localization.Format("Connection.Duplicated", copy["conn_name"]));
        }

        private string GetUniqueConnectionName(string baseName)
        {
            string cleanBase = string.IsNullOrWhiteSpace(baseName) ? "Connection Copy" : baseName.Trim();
            string candidate = cleanBase;
            int seq = 1;
            while (myN.connections.Any(c => string.Equals(GetConnectionValue(c, "conn_name"), candidate, StringComparison.OrdinalIgnoreCase)))
            {
                candidate = cleanBase + " " + seq;
                seq++;
            }

            return candidate;
        }

        private void CreateDatabaseFromConnection(TreeNode node)
        {
            int nodeIdx = GetConnectionIndex(node);
            if (node == null || nodeIdx < 0 || nodeIdx >= myN.connections.Count) return;
            if (!IsConnectionOpen(nodeIdx))
            {
                MessageBox.Show(Localization.T("Object.OpenConnectionFirst"), Localization.T("Tool.NewDatabase"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Dictionary<string, object> conn = myN.connections[nodeIdx];
            IDatabase db = (IDatabase)conn["pdo"];
            if (db is my_oracle)
            {
                CreateOracleSchemaFromConnection(node, db);
                return;
            }

            string databaseName = PromptForText(Localization.T("Database.NewTitle"), Localization.T("Database.NewPrompt"), "");
            if (databaseName == null) return;
            databaseName = databaseName.Trim();
            if (databaseName.Length == 0)
            {
                MessageBox.Show(Localization.T("Database.NameRequired"), Localization.T("Database.NewTitle"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                string sql = BuildCreateDatabaseSql(db, databaseName);
                Dictionary<string, string> result = db.ExecSQL(sql);
                if (!result.ContainsKey("status") || result["status"] != "OK")
                {
                    string reason = result.ContainsKey("reason") ? result["reason"] : Localization.T("Object.UnknownError");
                    throw new Exception(reason);
                }

                RefreshConnectionDatabaseNodes(node);
                SelectConnectionDatabaseNode(node, databaseName);
                UpdateMainStatus(Localization.Format("Database.Created", databaseName));
            }
            catch (Exception ex)
            {
                string message = Localization.Format("Database.CreateFailed", ex.Message);
                UpdateMainStatus(message);
                MessageBox.Show(message, Localization.T("Database.NewTitle"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void CreateOracleSchemaFromConnection(TreeNode node, IDatabase db)
        {
            OracleSchemaCreateOptions options = ShowOracleCreateSchemaDialog();
            if (options == null) return;

            try
            {
                foreach (string statement in BuildOracleCreateSchemaStatements(options))
                {
                    Dictionary<string, string> result = db.ExecSQL(statement);
                    if (!result.ContainsKey("status") || result["status"] != "OK")
                    {
                        string reason = result.ContainsKey("reason") ? result["reason"] : Localization.T("Object.UnknownError");
                        throw new Exception(reason);
                    }
                }

                RefreshConnectionDatabaseNodes(node);
                SelectConnectionDatabaseNode(node, options.SchemaName);
                UpdateMainStatus(Localization.Format("Database.Created", options.SchemaName));
            }
            catch (Exception ex)
            {
                string message = Localization.Format("Database.CreateFailed", ex.Message);
                UpdateMainStatus(message);
                MessageBox.Show(message, Localization.T("Database.OracleCreateTitle"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private OracleSchemaCreateOptions ShowOracleCreateSchemaDialog()
        {
            using (Form dialog = new Form())
            using (Label userLabel = new Label())
            using (TextBox userBox = new TextBox())
            using (Label passwordLabel = new Label())
            using (TextBox passwordBox = new TextBox())
            using (Label defaultTablespaceLabel = new Label())
            using (TextBox defaultTablespaceBox = new TextBox())
            using (Label temporaryTablespaceLabel = new Label())
            using (TextBox temporaryTablespaceBox = new TextBox())
            using (CheckBox quotaBox = new CheckBox())
            using (Button okButton = new Button())
            using (Button cancelButton = new Button())
            {
                dialog.Text = Localization.T("Database.OracleCreateTitle");
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.MinimizeBox = false;
                dialog.MaximizeBox = false;
                dialog.ClientSize = new Size(460, 252);
                dialog.ShowIcon = false;
                ApplyModernTheme(dialog);

                userLabel.Text = Localization.T("Database.OracleUserName");
                userLabel.AutoSize = true;
                userLabel.Location = new Point(16, 18);
                userBox.Location = new Point(170, 15);
                userBox.Width = 260;

                passwordLabel.Text = Localization.T("Database.OraclePassword");
                passwordLabel.AutoSize = true;
                passwordLabel.Location = new Point(16, 58);
                passwordBox.Location = new Point(170, 55);
                passwordBox.Width = 260;
                passwordBox.PasswordChar = '*';

                defaultTablespaceLabel.Text = Localization.T("Database.OracleDefaultTablespace");
                defaultTablespaceLabel.AutoSize = true;
                defaultTablespaceLabel.Location = new Point(16, 98);
                defaultTablespaceBox.Location = new Point(170, 95);
                defaultTablespaceBox.Width = 260;
                defaultTablespaceBox.Text = "USERS";

                temporaryTablespaceLabel.Text = Localization.T("Database.OracleTemporaryTablespace");
                temporaryTablespaceLabel.AutoSize = true;
                temporaryTablespaceLabel.Location = new Point(16, 138);
                temporaryTablespaceBox.Location = new Point(170, 135);
                temporaryTablespaceBox.Width = 260;
                temporaryTablespaceBox.Text = "TEMP";

                quotaBox.Text = Localization.T("Database.OracleUnlimitedQuota");
                quotaBox.AutoSize = true;
                quotaBox.Checked = true;
                quotaBox.Location = new Point(170, 172);

                okButton.Text = Localization.T("Common.OK");
                okButton.DialogResult = DialogResult.OK;
                okButton.Location = new Point(260, 210);
                okButton.Width = 80;
                cancelButton.Text = Localization.T("Common.Cancel");
                cancelButton.DialogResult = DialogResult.Cancel;
                cancelButton.Location = new Point(350, 210);
                cancelButton.Width = 80;

                dialog.Controls.AddRange(new Control[]
                {
                    userLabel, userBox,
                    passwordLabel, passwordBox,
                    defaultTablespaceLabel, defaultTablespaceBox,
                    temporaryTablespaceLabel, temporaryTablespaceBox,
                    quotaBox, okButton, cancelButton
                });
                dialog.AcceptButton = okButton;
                dialog.CancelButton = cancelButton;

                while (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    string schemaName = userBox.Text.Trim();
                    string password = passwordBox.Text;
                    if (string.IsNullOrWhiteSpace(schemaName))
                    {
                        MessageBox.Show(Localization.T("Database.NameRequired"), dialog.Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        userBox.Focus();
                        continue;
                    }
                    if (string.IsNullOrEmpty(password))
                    {
                        MessageBox.Show(Localization.T("Database.OraclePasswordRequired"), dialog.Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        passwordBox.Focus();
                        continue;
                    }

                    return new OracleSchemaCreateOptions
                    {
                        SchemaName = schemaName,
                        Password = password,
                        DefaultTablespace = defaultTablespaceBox.Text.Trim(),
                        TemporaryTablespace = temporaryTablespaceBox.Text.Trim(),
                        UnlimitedQuota = quotaBox.Checked
                    };
                }
            }

            return null;
        }

        private static string BuildCreateDatabaseSql(IDatabase db, string databaseName)
        {
            string provider = db == null ? "" : db.ProviderName;
            if (string.Equals(provider, "mysql", StringComparison.OrdinalIgnoreCase))
                return "CREATE DATABASE " + QuoteDatabaseIdentifier(databaseName, "`", "`") + ";";
            if (string.Equals(provider, "postgresql", StringComparison.OrdinalIgnoreCase))
                return "CREATE DATABASE " + QuoteDatabaseIdentifier(databaseName, "\"", "\"") + ";";
            if (string.Equals(provider, "mssql", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(provider, "sqlserver", StringComparison.OrdinalIgnoreCase))
                return "CREATE DATABASE " + QuoteDatabaseIdentifier(databaseName, "[", "]") + ";";
            if (string.Equals(provider, "oracle", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException(Localization.T("Database.OracleUnsupportedCreate"));
            if (string.Equals(provider, "sqlite", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException(Localization.T("Database.SqliteUnsupportedCreate"));

            throw new NotSupportedException(Localization.Format("Database.UnsupportedCreate", provider));
        }

        private static List<string> BuildOracleCreateSchemaStatements(OracleSchemaCreateOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(options.SchemaName)) throw new ArgumentException(Localization.T("Database.NameRequired"));
            if (string.IsNullOrEmpty(options.Password)) throw new ArgumentException(Localization.T("Database.OraclePasswordRequired"));

            string schema = QuoteOracleIdentifier(options.SchemaName);
            List<string> statements = new List<string>();
            StringBuilder create = new StringBuilder();
            create.Append("CREATE USER ");
            create.Append(schema);
            create.Append(" IDENTIFIED BY ");
            create.Append(QuoteOracleIdentifier(options.Password));
            if (!string.IsNullOrWhiteSpace(options.DefaultTablespace))
            {
                create.Append(" DEFAULT TABLESPACE ");
                create.Append(QuoteOracleIdentifier(options.DefaultTablespace));
            }
            if (!string.IsNullOrWhiteSpace(options.TemporaryTablespace))
            {
                create.Append(" TEMPORARY TABLESPACE ");
                create.Append(QuoteOracleIdentifier(options.TemporaryTablespace));
            }
            statements.Add(create.ToString());

            if (options.UnlimitedQuota && !string.IsNullOrWhiteSpace(options.DefaultTablespace))
            {
                statements.Add("ALTER USER " + schema + " QUOTA UNLIMITED ON " + QuoteOracleIdentifier(options.DefaultTablespace));
            }

            statements.Add("GRANT CREATE SESSION, CREATE TABLE, CREATE VIEW, CREATE SEQUENCE, CREATE PROCEDURE TO " + schema);
            return statements;
        }

        private static string QuoteOracleIdentifier(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
        }

        private sealed class OracleSchemaCreateOptions
        {
            public string SchemaName { get; set; }
            public string Password { get; set; }
            public string DefaultTablespace { get; set; }
            public string TemporaryTablespace { get; set; }
            public bool UnlimitedQuota { get; set; }
        }

        private static string QuoteDatabaseIdentifier(string name, string openQuote, string closeQuote)
        {
            if (openQuote == "[") return "[" + name.Replace("]", "]]") + "]";
            return openQuote + name.Replace(openQuote, openQuote + openQuote) + closeQuote;
        }

        private void RefreshConnectionDatabaseNodes(TreeNode root)
        {
            if (root == null) return;
            int connIdxRefreshConn = GetConnectionIndex(root);
            if (connIdxRefreshConn < 0 || connIdxRefreshConn >= myN.connections.Count) return;
            if (!IsConnectionOpen(connIdxRefreshConn)) return;

            PopulateOpenConnectionDatabases(root, connIdxRefreshConn);
            root.Expand();
            UpdateMainStatus(Localization.Format("Connection.Refreshed", root.Text));
        }

        private void TryPopulateOpenConnectionDatabases(TreeNode root, int index)
        {
            try
            {
                PopulateOpenConnectionDatabases(root, index);
            }
            catch
            {
                root.Nodes.Clear();
            }
        }

        private void PopulateOpenConnectionDatabases(TreeNode root, int index)
        {
            if (root == null || index < 0 || index >= myN.connections.Count) return;
            if (!IsConnectionOpen(index)) return;

            IDatabase db = (IDatabase)myN.connections[index]["pdo"];
            root.Nodes.Clear();
            foreach (string databaseName in db.GetDatabases())
            {
                TreeNode newNode = new TreeNode(databaseName);
                newNode.ImageIndex = 10;
                newNode.SelectedImageIndex = 10;
                root.Nodes.Add(newNode);
            }
        }

        private void SelectConnectionDatabaseNode(TreeNode root, string databaseName)
        {
            if (root == null) return;
            foreach (TreeNode child in root.Nodes)
            {
                if (!string.Equals(child.Text, databaseName, StringComparison.OrdinalIgnoreCase)) continue;
                db_tree.SelectedNode = child;
                child.EnsureVisible();
                return;
            }
        }

        private void SortConnectionDatabaseNodes(TreeNode root, bool ascending)
        {
            if (root == null) return;
            List<TreeNode> nodes = root.Nodes.Cast<TreeNode>()
                .OrderBy(n => n.Text, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (!ascending) nodes.Reverse();

            root.Nodes.Clear();
            root.Nodes.AddRange(nodes.ToArray());
            root.Expand();
        }

        private void ToggleConnectionFavorite(TreeNode node)
        {
            if (node == null) return;
            string path = node.FullPath;
            string existing = _favoriteNodePaths.FirstOrDefault(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                _favoriteNodePaths.Add(path);
            }
            else
            {
                _favoriteNodePaths.Remove(existing);
            }

            SaveFavoriteNodePaths();
            ConfigureMainMenu();
            UpdateMainStatus(existing == null ? "Favorite added: " + path : "Favorite removed: " + path);
        }

        private void AddConnectionColorMenu(ContextMenuStrip menu, TreeNode node)
        {
            ToolStripMenuItem colorItem = new ToolStripMenuItem(Localization.T("Menu.Color"));
            AddConnectionColorMenuItem(colorItem, node, Localization.T("Menu.ColorDefault"), Color.Empty);
            AddConnectionColorMenuItem(colorItem, node, Localization.T("Menu.ColorRed"), Color.FromArgb(190, 44, 44));
            AddConnectionColorMenuItem(colorItem, node, Localization.T("Menu.ColorOrange"), Color.FromArgb(204, 120, 50));
            AddConnectionColorMenuItem(colorItem, node, Localization.T("Menu.ColorYellow"), Color.FromArgb(170, 140, 20));
            AddConnectionColorMenuItem(colorItem, node, Localization.T("Menu.ColorGreen"), Color.FromArgb(50, 135, 90));
            AddConnectionColorMenuItem(colorItem, node, Localization.T("Menu.ColorBlue"), Color.FromArgb(51, 103, 145));
            AddConnectionColorMenuItem(colorItem, node, Localization.T("Menu.ColorPurple"), Color.FromArgb(125, 80, 160));
            menu.Items.Add(colorItem);
        }

        private void AddConnectionColorMenuItem(ToolStripMenuItem parent, TreeNode node, string text, Color color)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Click += (s, ev) =>
            {
                node.ForeColor = color;
                UpdateMainStatus(Localization.Format("Connection.MarkedColor", text));
            };
            parent.DropDownItems.Add(item);
        }

        private void ShareConnection(int index)
        {
            if (index < 0 || index >= myN.connections.Count) return;
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = Localization.T("Connection.ShareTitle");
                dialog.Filter = Localization.T("Connection.ShareFilter");
                dialog.DefaultExt = "json";
                dialog.FileName = GetConnectionValue(myN.connections[index], "conn_name") + ".json";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    Dictionary<string, object> shared = BuildShareableConnection(myN.connections[index]);
                    File.WriteAllText(dialog.FileName, JsonConvert.SerializeObject(shared, Formatting.Indented), Encoding.UTF8);
                    MessageBox.Show(Localization.T("Connection.Shared"), Localization.T("Common.Complete"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateMainStatus(Localization.T("Connection.Shared"));
                }
                catch (Exception ex)
                {
                    string message = Localization.Format("Connection.ShareFailed", ex.Message);
                    UpdateMainStatus(message);
                    MessageBox.Show(message, Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private static Dictionary<string, object> BuildShareableConnection(Dictionary<string, object> source)
        {
            Dictionary<string, object> shared = new Dictionary<string, object>();
            string[] keys =
            {
                "conn_name",
                "db_kind",
                "host",
                "port",
                "initial_database",
                "path",
                "username",
                "trusted_connection",
                "init_geospatial",
                "connection_type",
                "tns_name",
                "service_name",
                "sid",
                "oracle_identifier_type"
            };

            foreach (string key in keys)
            {
                if (source.ContainsKey(key)) shared[key] = source[key];
            }

            shared["pwd"] = "";
            return shared;
        }

        private void OpenDatabaseCommandLine(int index)
        {
            OpenDatabaseCommandLine(index, null);
        }

        private void OpenDatabaseCommandLine(int index, string databaseName)
        {
            if (index < 0 || index >= myN.connections.Count) return;
            Dictionary<string, object> conn = new Dictionary<string, object>(myN.connections[index]);
            if (!string.IsNullOrWhiteSpace(databaseName))
            {
                conn["initial_database"] = databaseName;
            }

            DatabaseCliLaunch cliLaunch = BuildDatabaseCommandLineLaunch(conn);
            if (cliLaunch == null || string.IsNullOrWhiteSpace(cliLaunch.Command))
            {
                string kind = GetConnectionValue(conn, "db_kind");
                string message = Localization.Format("Connection.CommandLineUnavailable", kind);
                UpdateMainStatus(message);
                MessageBox.Show(message, Localization.T("Tool.CommandLine"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string cliTarget = GetCliAvailabilityTarget(GetConnectionValue(conn, "db_kind").ToLowerInvariant());
            if (!string.IsNullOrEmpty(cliTarget) && !IsCliAvailable(cliTarget))
            {
                string installHint = GetCliInstallHint(GetConnectionValue(conn, "db_kind").ToLowerInvariant());
                string notFoundMsg = Localization.Format("Connection.CliNotFound", cliTarget, installHint);
                UpdateMainStatus(notFoundMsg);
                MessageBox.Show(notFoundMsg, Localization.T("Tool.CommandLine"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!ApplyTemporaryCliPasswordIfNeeded(conn))
            {
                string message = Localization.T("Connection.CliTemporaryPasswordCancelled");
                UpdateMainStatus(message);
                return;
            }
            cliLaunch = BuildDatabaseCommandLineLaunch(conn);

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe");
                psi.Arguments = "/k " + cliLaunch.Command;
                psi.UseShellExecute = false;
                foreach (KeyValuePair<string, string> pair in cliLaunch.EnvironmentVariables)
                {
                    psi.EnvironmentVariables[pair.Key] = pair.Value ?? string.Empty;
                }
                Process.Start(psi);
                UpdateMainStatus(Localization.T("Connection.CommandLineOpened"));
            }
            catch (Exception ex)
            {
                string message = Localization.Format("Connection.CommandLineOpenFailed", ex.Message);
                UpdateMainStatus(message);
                MessageBox.Show(message, Localization.T("Tool.CommandLine"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private bool ApplyTemporaryCliPasswordIfNeeded(Dictionary<string, object> conn)
        {
            if (!ShouldPromptForTemporaryCliPassword(conn)) return true;

            string password = PromptForText(
                Localization.T("Tool.CommandLine"),
                Localization.T("Connection.CliTemporaryPasswordPrompt"),
                "",
                true);
            if (password == null) return false;
            if (!string.IsNullOrEmpty(password))
            {
                conn["pwd"] = password;
            }
            return true;
        }

        private static string GetCliAvailabilityTarget(string dbKind)
        {
            string customPath = CliPathSettings.GetPath(GetCliProviderKey(dbKind));
            if (!string.IsNullOrWhiteSpace(customPath)) return customPath;

            switch (dbKind)
            {
                case "mysql":      return "mysql.exe";
                case "postgresql": return "psql.exe";
                case "mssql":
                case "sqlserver":  return "sqlcmd.exe";
                case "oracle":     return "sqlplus.exe";
                case "sqlite":     return "sqlite3.exe";
                default:           return null;
            }
        }

        private static string GetCliProviderKey(string dbKind)
        {
            switch ((dbKind ?? string.Empty).ToLowerInvariant())
            {
                case "mssql":
                case "sqlserver":
                    return "sqlserver";
                default:
                    return (dbKind ?? string.Empty).ToLowerInvariant();
            }
        }

        private static string GetCliInstallHint(string dbKind)
        {
            switch (dbKind)
            {
                case "mysql":      return "https://dev.mysql.com/downloads/";
                case "postgresql": return "https://www.postgresql.org/download/";
                case "mssql":
                case "sqlserver":  return "https://learn.microsoft.com/zh-tw/sql/tools/sqlcmd/sqlcmd-utility";
                case "oracle":     return "https://www.oracle.com/tools/downloads/sqlplus-downloads/";
                case "sqlite":     return "https://www.sqlite.org/download.html";
                default:           return "";
            }
        }

        private static bool IsCliAvailable(string executableOrPath)
        {
            if (string.IsNullOrEmpty(executableOrPath)) return true;
            if (Path.IsPathRooted(executableOrPath) || executableOrPath.Contains("\\") || executableOrPath.Contains("/"))
            {
                return File.Exists(executableOrPath);
            }

            // 先用 where.exe 查詢（Windows）
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("where")
                {
                    Arguments = executableOrPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    if (p.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                        return true;
                }
            }
            catch { /* where.exe 不存在時 fallback 到 PATH 掃描 */ }

            // Fallback：手動掃描 PATH
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in pathEnv.Split(';'))
            {
                string trimmed = dir.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                try
                {
                    if (File.Exists(Path.Combine(trimmed, executableOrPath)))
                        return true;
                }
                catch { }
            }
            return false;
        }

        private string BuildDatabaseCommandLine(Dictionary<string, object> conn)
        {
            DatabaseCliLaunch launch = BuildDatabaseCommandLineLaunch(conn);
            return launch == null ? "" : launch.Command;
        }

        private static bool ShouldPromptForTemporaryCliPassword(Dictionary<string, object> conn)
        {
            if (conn == null) return false;
            if (!string.IsNullOrEmpty(GetConnectionValue(conn, "pwd"))) return false;
            if (string.IsNullOrWhiteSpace(GetConnectionValue(conn, "username"))) return false;

            string kind = GetConnectionValue(conn, "db_kind").ToLowerInvariant();
            if (kind == "mysql" || kind == "postgresql") return true;
            if (kind == "mssql" || kind == "sqlserver")
                return GetConnectionValue(conn, "trusted_connection") != "T";
            return false;
        }

        private DatabaseCliLaunch BuildDatabaseCommandLineLaunch(Dictionary<string, object> conn)
        {
            string kind = GetConnectionValue(conn, "db_kind").ToLowerInvariant();
            string host = GetConnectionValue(conn, "host");
            string port = GetConnectionValue(conn, "port");
            string user = GetConnectionValue(conn, "username");
            string password = GetConnectionValue(conn, "pwd");
            string initialDatabase = GetConnectionValue(conn, "initial_database");
            DatabaseCliLaunch launch = new DatabaseCliLaunch();

            switch (kind)
            {
                case "mysql":
                    if (!string.IsNullOrEmpty(password)) launch.EnvironmentVariables["MYSQL_PWD"] = password;
                    launch.Command = GetCliCommand(kind, "mysql") + " -h " + QuoteCommandArgument(host) +
                           (string.IsNullOrWhiteSpace(port) ? "" : " -P " + QuoteCommandArgument(port)) +
                           " -u " + QuoteCommandArgument(user) +
                           (string.IsNullOrEmpty(password) ? " -p" : "") +
                           (string.IsNullOrWhiteSpace(initialDatabase) ? "" : " " + QuoteCommandArgument(initialDatabase));
                    return launch;
                case "postgresql":
                    if (string.IsNullOrWhiteSpace(initialDatabase)) initialDatabase = "postgres";
                    if (!string.IsNullOrEmpty(password)) launch.EnvironmentVariables["PGPASSWORD"] = password;
                    launch.Command = GetCliCommand(kind, "psql") + " -h " + QuoteCommandArgument(host) +
                           (string.IsNullOrWhiteSpace(port) ? "" : " -p " + QuoteCommandArgument(port)) +
                           " -U " + QuoteCommandArgument(user) + " " + QuoteCommandArgument(initialDatabase);
                    return launch;
                case "mssql":
                case "sqlserver":
                    string dataSource = BuildSqlServerDataSource(host, port);
                    string sqlcmd = GetCliCommand(kind, "sqlcmd");
                    if (GetConnectionValue(conn, "trusted_connection") == "T")
                    {
                        launch.Command = sqlcmd + " -S " + QuoteCommandArgument(dataSource) + " -E" +
                               (string.IsNullOrWhiteSpace(initialDatabase) ? "" : " -d " + QuoteCommandArgument(initialDatabase));
                        return launch;
                    }
                    if (!string.IsNullOrEmpty(password)) launch.EnvironmentVariables["SQLCMDPASSWORD"] = password;
                    launch.Command = sqlcmd + " -S " + QuoteCommandArgument(dataSource) + " -U " + QuoteCommandArgument(user) +
                           (string.IsNullOrWhiteSpace(initialDatabase) ? "" : " -d " + QuoteCommandArgument(initialDatabase));
                    return launch;
                case "sqlite":
                    string sqlitePath = GetConnectionValue(conn, "path");
                    string exe = GetCliCommand(kind, "sqlite3");
                    launch.Command = exe + " " + QuoteCommandArgument(sqlitePath);
                    return launch;
                case "oracle":
                    string connectName = BuildOracleCommandLineConnectName(conn);
                    string login = string.IsNullOrWhiteSpace(connectName) ? user : user + "@" + connectName;
                    string sqlplus = GetCliCommand(kind, "sqlplus");
                    launch.Command = string.IsNullOrWhiteSpace(login) ? sqlplus : sqlplus + " " + QuoteCommandArgument(login);
                    return launch;
                default:
                    return launch;
            }
        }

        private sealed class DatabaseCliLaunch
        {
            public string Command { get; set; } = "";
            public Dictionary<string, string> EnvironmentVariables { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private static string GetCliCommand(string dbKind, string fallbackCommand)
        {
            string configured = CliPathSettings.GetPath(GetCliProviderKey(dbKind));
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return QuoteCommandArgument(configured);
            }

            if (Path.IsPathRooted(fallbackCommand) || fallbackCommand.Contains("\\") || fallbackCommand.Contains("/"))
            {
                return QuoteCommandArgument(fallbackCommand);
            }

            return fallbackCommand;
        }

        private static string BuildOracleCommandLineConnectName(Dictionary<string, object> conn)
        {
            string tnsName = GetConnectionValue(conn, "tns_name");
            if (!string.IsNullOrWhiteSpace(tnsName)) return tnsName;

            string host = GetConnectionValue(conn, "host");
            string port = GetConnectionValue(conn, "port");
            string serviceName = GetConnectionValue(conn, "service_name");
            string sid = GetConnectionValue(conn, "sid");
            string identifier = string.Equals(GetConnectionValue(conn, "oracle_identifier_type"), "sid", StringComparison.OrdinalIgnoreCase)
                ? sid
                : serviceName;

            if (string.IsNullOrWhiteSpace(identifier)) identifier = string.IsNullOrWhiteSpace(serviceName) ? sid : serviceName;
            if (string.IsNullOrWhiteSpace(identifier)) return string.Empty;
            if (string.IsNullOrWhiteSpace(host)) return identifier;

            string hostPort = string.IsNullOrWhiteSpace(port) ? host : host + ":" + port;
            return "//" + hostPort + "/" + identifier;
        }

        private static string QuoteCommandArgument(string value)
        {
            value = value ?? "";
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private string PromptForText(string title, string label, string defaultValue)
        {
            return PromptForText(title, label, defaultValue, false);
        }

        private string PromptForText(string title, string label, string defaultValue, bool isPassword)
        {
            using (Form prompt = new Form())
            using (Label promptLabel = new Label())
            using (TextBox input = new TextBox())
            using (Button okButton = new Button())
            using (Button cancelButton = new Button())
            {
                prompt.Text = title;
                prompt.StartPosition = FormStartPosition.CenterParent;
                prompt.FormBorderStyle = FormBorderStyle.FixedDialog;
                prompt.MinimizeBox = false;
                prompt.MaximizeBox = false;
                prompt.ShowIcon = false;

                promptLabel.Text = label;
                promptLabel.AutoSize = true;
                promptLabel.Location = new Point(14, 18);
                promptLabel.MaximumSize = new Size(386, 0);

                input.Text = defaultValue ?? string.Empty;
                input.Location = new Point(17, Math.Max(45, promptLabel.Bottom + 10));
                input.Width = 386;
                input.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
                input.UseSystemPasswordChar = isPassword;

                okButton.Text = Localization.T("Common.OK");
                okButton.DialogResult = DialogResult.OK;
                okButton.Location = new Point(247, input.Bottom + 18);
                okButton.Width = 75;

                cancelButton.Text = Localization.T("Common.Cancel");
                cancelButton.DialogResult = DialogResult.Cancel;
                cancelButton.Location = new Point(328, input.Bottom + 18);
                cancelButton.Width = 75;
                prompt.ClientSize = new Size(420, cancelButton.Bottom + 14);

                prompt.Controls.Add(promptLabel);
                prompt.Controls.Add(input);
                prompt.Controls.Add(okButton);
                prompt.Controls.Add(cancelButton);
                prompt.AcceptButton = okButton;
                prompt.CancelButton = cancelButton;
                ThemeManager.ApplyTo(prompt);

                return prompt.ShowDialog(this) == DialogResult.OK ? input.Text : null;
            }
        }

        private void AddCopyRenameObjectMenuItems(ContextMenuStrip menu)
        {
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem copyItem = new ToolStripMenuItem(Localization.T("Tool.CopyObject"));
            copyItem.Click += (s, ev) => CopySelectedDatabaseObjectToInternalClipboard();
            menu.Items.Add(copyItem);

            ToolStripMenuItem renameItem = new ToolStripMenuItem(Localization.T("Tool.RenameObject"));
            renameItem.Click += (s, ev) => BeginRenameSelectedDatabaseObject();
            menu.Items.Add(renameItem);
        }

        private void AddPasteObjectMenuItem(ContextMenuStrip menu)
        {
            ToolStripMenuItem pasteItem = new ToolStripMenuItem(Localization.T("Tool.PasteObject"));
            pasteItem.Enabled = _treeClipboardItem != null;
            pasteItem.Click += (s, ev) => PasteInternalClipboardToSelectedDatabase();
            menu.Items.Add(pasteItem);
        }

        private static bool IsDetailsOnlyGroup(string groupName)
        {
            return string.Equals(groupName, "Events", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(groupName, "Users", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(groupName, "Models", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(groupName, "BI", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(groupName, "Other", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(groupName, "Reports", StringComparison.OrdinalIgnoreCase);
        }

        private bool ShowSelectedTreeNodeDetails()
        {
            if (db_tree.SelectedNode == null) return false;
            db_tree_AfterSelect(db_tree, new TreeViewEventArgs(db_tree.SelectedNode));
            return true;
        }

        private void db_tree_edit_connection(int index)
        {
            var conn = myN.connections[index];
            string kind = conn["db_kind"].ToString().ToLower();
            switch (kind)
            {
                case "mysql":
                    {
                        mysql_add_edit form = new mysql_add_edit();
                        form.F1 = this;
                        form.editIndex = index;
                        form.ShowDialog();
                    }
                    break;
                case "postgresql":
                    {
                        postgresql_add_edit form = new postgresql_add_edit();
                        form.F1 = this;
                        form.editIndex = index;
                        form.ShowDialog();
                    }
                    break;
                case "oracle":
                    {
                        oracle_add_edit form = new oracle_add_edit();
                        form.F1 = this;
                        form.editIndex = index;
                        form.oracle_connection_type.Text = "Basic";
                        form.oracle_connection_type_selected_trigger_change();
                        form.ShowDialog();
                    }
                    break;
                case "sqlite":
                    {
                        sqlite_add_edit form = new sqlite_add_edit();
                        form.F1 = this;
                        form.editIndex = index;
                        form.ShowDialog();
                    }
                    break;
                case "mssql":
                case "sqlserver":
                    {
                        sqlserver_add_edit form = new sqlserver_add_edit();
                        form.F1 = this;
                        form.editIndex = index;
                        form.ShowDialog();
                    }
                    break;
                default:
                    MessageBox.Show(BuildUnsupportedConnectionEditMessage(kind), Localization.T("Tool.EditConnection"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                    break;
            }
        }

        private static string BuildUnsupportedConnectionEditMessage(string kind)
        {
            return Localization.Format("Connection.UnsupportedEdit", kind);
        }

        private static string BuildDeleteConnectionMessage(string name)
        {
            return Localization.Format("Connection.ConfirmDelete", name);
        }

        private void db_tree_delete_connection(int index)
        {
            var conn = myN.connections[index];
            string name = conn["conn_name"].ToString();
            var result = MessageBox.Show(
                BuildDeleteConnectionMessage(name),
                Localization.T("Connection.DeleteTitle"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
            {
                DeleteConnectionCredential(conn);
                myN.connections.RemoveAt(index);
                myN.setSettingINI();
                drawLists();
            }
        }

        private static void DeleteConnectionCredential(Dictionary<string, object> conn)
        {
            string target = GetConnectionValue(conn, "credential_target");
            if (!string.IsNullOrWhiteSpace(target))
            {
                WindowsCredentialService.TryDeletePassword(target);
            }
        }

        private async Task db_tree_second_click(int father_index, int index, string databaseName)
        {
            TreeNode connNode = FindConnectionNode(father_index);
            if (connNode == null || index < 0 || index >= connNode.Nodes.Count) return;
            TreeNode databaseNode = connNode.Nodes[index];
            if (!(myN.connections[father_index]["pdo"] is IDatabase db)) return;

            await LoadDatabaseMetadataAsync(father_index, databaseNode, db, databaseName);
        }

        private MetadataLoadService CreateMetadataLoadService()
        {
            return new MetadataLoadService(GetDatabaseFunctions, GetDatabaseUsers, GetDatabaseEvents);
        }

        private async Task LoadDatabaseMetadataAsync(int connectionIndex, TreeNode databaseNode, IDatabase db, string databaseName)
        {
            if (databaseNode == null || db == null) return;
            if (databaseNode.Nodes.Count > 0)
            {
                databaseNode.Expand();
                return;
            }

            if (!TryEnterDatabaseMetadataLoadScope(connectionIndex, databaseName))
            {
                databaseNode.Expand();
                return;
            }

            Cursor previousCursor = Cursor;
            try
            {
                Cursor = Cursors.WaitCursor;
                await Task.Yield();

                db_tree?.BeginUpdate();
                try
                {
                    databaseNode.Nodes.Clear();
                    TreeNode loading = new TreeNode(Localization.T("Status.LoadingData"));
                    loading.ImageIndex = 11;
                    loading.SelectedImageIndex = 11;
                    databaseNode.Nodes.Add(loading);
                    databaseNode.Expand();
                    databaseNode.ImageIndex = 11;
                    databaseNode.SelectedImageIndex = 11;
                }
                finally
                {
                    db_tree?.EndUpdate();
                }

                Dictionary<string, object> connInfo = connectionIndex >= 0 && connectionIndex < myN.connections.Count ? myN.connections[connectionIndex] : null;
                MetadataLoadService metadataService = CreateMetadataLoadService();
                bool includeHiddenObjects = ApplicationOptionSettings.GetBool("ViewShowHiddenItems");
                DatabaseMetadataSnapshot snapshot = await Task.Run(() => metadataService.Load(db, databaseName, connInfo, includeHiddenObjects));
                if (IsDisposed) return;

                db_tree?.BeginUpdate();
                try
                {
                    databaseNode.Nodes.Clear();
                    PopulateDatabaseChildren(databaseNode, snapshot, databaseName, connInfo);
                    databaseNode.Expand();
                    databaseNode.ImageIndex = 11;
                    databaseNode.SelectedImageIndex = 11;
                }
                finally
                {
                    db_tree?.EndUpdate();
                }
            }
            catch (Exception ex)
            {
                db_tree?.BeginUpdate();
                try
                {
                    databaseNode.Nodes.Clear();
                }
                finally
                {
                    db_tree?.EndUpdate();
                }

                string providerName = db?.ProviderName ?? "database";
                string message = $"{providerName} metadata 載入失敗（{databaseName}）：{ex.Message}";
                Console.WriteLine(message);
                UpdateMainStatus(message);
                MessageBox.Show(message, "Metadata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = previousCursor;
                ExitDatabaseMetadataLoadScope(connectionIndex, databaseName);
            }
        }

        private void PopulateDatabaseChildren(TreeNode databaseNode, DatabaseMetadataSnapshot snapshot, string databaseName, Dictionary<string, object> connInfo)
        {
            if (databaseNode == null || snapshot == null) return;

            TreeNode tablesNode = CreateTreeGroupNode("Tables", 12);

            foreach (string tableName in snapshot.Tables ?? new List<string>())
            {
                tablesNode.Nodes.Add(CreateTreeObjectNode(tableName, "Tables", 12));
            }
            AddTreeGroupNodeIfVisible(databaseNode, tablesNode);

            TreeNode viewsNode = CreateTreeGroupNode("Views", 13);

            foreach (string viewName in snapshot.Views ?? new List<string>())
            {
                viewsNode.Nodes.Add(CreateTreeObjectNode(viewName, "Views", 13));
            }
            AddTreeGroupNodeIfVisible(databaseNode, viewsNode);

            TreeNode newNode = CreateTreeGroupNode("Functions", 14);

            if (snapshot.Functions != null)
            {
                foreach (DataRow functionRow in snapshot.Functions.Rows)
                {
                    newNode.Nodes.Add(CreateTreeObjectNode(functionRow["Name"].ToString(), "Functions", 14));
                }
            }
            AddTreeGroupNodeIfVisible(databaseNode, newNode);

            newNode = CreateTreeGroupNode("Users", 19);

            if (snapshot.Users != null)
            {
                foreach (DataRow userRow in snapshot.Users.Rows)
                {
                    newNode.Nodes.Add(CreateTreeObjectNode(userRow["Name"].ToString(), "Users", 19));
                }
            }
            AddTreeGroupNodeIfVisible(databaseNode, newNode);

            newNode = CreateTreeGroupNode("Models", 20);

            foreach (string modelName in DatabaseModelNames)
            {
                TreeNode modelNode = new TreeNode(modelName);
                modelNode.ImageIndex = 20;
                modelNode.SelectedImageIndex = 20;
                newNode.Nodes.Add(modelNode);
            }
            AddTreeGroupNodeIfVisible(databaseNode, newNode);

            newNode = CreateTreeGroupNode("BI", 21);

            foreach (string biName in DatabaseBIReportNames)
            {
                TreeNode biNode = new TreeNode(biName);
                biNode.ImageIndex = 21;
                biNode.SelectedImageIndex = 21;
                newNode.Nodes.Add(biNode);
            }
            AddTreeGroupNodeIfVisible(databaseNode, newNode);

            newNode = CreateTreeGroupNode("Other", 22);

            foreach (string toolName in DatabaseOtherToolNames)
            {
                TreeNode toolNode = new TreeNode(toolName);
                toolNode.ImageIndex = 22;
                toolNode.SelectedImageIndex = 22;
                newNode.Nodes.Add(toolNode);
            }
            AddTreeGroupNodeIfVisible(databaseNode, newNode);

            newNode = CreateTreeGroupNode("Events", 15);

            if (snapshot.Events != null)
            {
                foreach (DataRow eventRow in snapshot.Events.Rows)
                {
                    newNode.Nodes.Add(CreateTreeObjectNode(eventRow["Name"].ToString(), "Events", 15));
                }
            }
            AddTreeGroupNodeIfVisible(databaseNode, newNode);

            newNode = CreateTreeGroupNode("Queries", 16);
            AddTreeGroupNodeIfVisible(databaseNode, newNode);

            newNode = CreateTreeGroupNode("Reports", 17);

            foreach (string reportName in DatabaseReportNames)
            {
                TreeNode reportNode = new TreeNode(reportName);
                reportNode.ImageIndex = 17;
                reportNode.SelectedImageIndex = 17;
                newNode.Nodes.Add(reportNode);
            }
            AddTreeGroupNodeIfVisible(databaseNode, newNode);

            newNode = CreateTreeGroupNode("Backups", 18);
            AddTreeGroupNodeIfVisible(databaseNode, newNode);
        }

        private void PopulateDatabaseChildren(TreeNode databaseNode, IDatabase db, string databaseName)
        {
            TreeNode tablesNode = CreateTreeGroupNode("Tables", 12);

            foreach (string tableName in GetTablesSafe(db, databaseName))
            {
                tablesNode.Nodes.Add(CreateTreeObjectNode(tableName, "Tables", 12));
            }
            AddTreeGroupNodeIfVisible(databaseNode, tablesNode);

            TreeNode viewsNode = CreateTreeGroupNode("Views", 13);

            foreach (string viewName in GetViewsSafe(db, databaseName))
            {
                viewsNode.Nodes.Add(CreateTreeObjectNode(viewName, "Views", 13));
            }
            AddTreeGroupNodeIfVisible(databaseNode, viewsNode);

            TreeNode newNode = CreateTreeGroupNode("Functions", 14);

            foreach (DataRow functionRow in GetDatabaseFunctions(db, databaseName).Rows)
            {
                newNode.Nodes.Add(CreateTreeObjectNode(functionRow["Name"].ToString(), "Functions", 14));
            }
            AddTreeGroupNodeIfVisible(databaseNode, newNode);

            newNode = CreateTreeGroupNode("Users", 19);

            TreeNode root = databaseNode;
            while (root.Parent != null && !IsConnectionGroupNode(root.Parent)) root = root.Parent;
            int connIdxUsers = GetConnectionIndex(root);
            Dictionary<string, object> connInfo = connIdxUsers >= 0 && connIdxUsers < myN.connections.Count ? myN.connections[connIdxUsers] : null;
            foreach (DataRow userRow in GetDatabaseUsers(db, databaseName, connInfo).Rows)
            {
                newNode.Nodes.Add(CreateTreeObjectNode(userRow["Name"].ToString(), "Users", 19));
            }
            AddTreeGroupNodeIfVisible(databaseNode, newNode);

            newNode = CreateTreeGroupNode("Models", 20);

            foreach (string modelName in DatabaseModelNames)
            {
                TreeNode modelNode = new TreeNode(modelName);
                modelNode.ImageIndex = 20;
                modelNode.SelectedImageIndex = 20;
                newNode.Nodes.Add(modelNode);
            }
            AddTreeGroupNodeIfVisible(databaseNode, newNode);

            newNode = CreateTreeGroupNode("BI", 21);

            foreach (string biName in DatabaseBIReportNames)
            {
                TreeNode biNode = new TreeNode(biName);
                biNode.ImageIndex = 21;
                biNode.SelectedImageIndex = 21;
                newNode.Nodes.Add(biNode);
            }
            AddTreeGroupNodeIfVisible(databaseNode, newNode);

            newNode = CreateTreeGroupNode("Other", 22);

            foreach (string toolName in DatabaseOtherToolNames)
            {
                TreeNode toolNode = new TreeNode(toolName);
                toolNode.ImageIndex = 22;
                toolNode.SelectedImageIndex = 22;
                newNode.Nodes.Add(toolNode);
            }
            AddTreeGroupNodeIfVisible(databaseNode, newNode);

            newNode = CreateTreeGroupNode("Events", 15);

            foreach (DataRow eventRow in GetDatabaseEvents(db, databaseName).Rows)
            {
                newNode.Nodes.Add(CreateTreeObjectNode(eventRow["Name"].ToString(), "Events", 15));
            }
            AddTreeGroupNodeIfVisible(databaseNode, newNode);

            newNode = CreateTreeGroupNode("Queries", 16);
            AddTreeGroupNodeIfVisible(databaseNode, newNode);

            newNode = CreateTreeGroupNode("Reports", 17);

            foreach (string reportName in DatabaseReportNames)
            {
                TreeNode reportNode = new TreeNode(reportName);
                reportNode.ImageIndex = 17;
                reportNode.SelectedImageIndex = 17;
                newNode.Nodes.Add(reportNode);
            }
            AddTreeGroupNodeIfVisible(databaseNode, newNode);

            newNode = CreateTreeGroupNode("Backups", 18);
            AddTreeGroupNodeIfVisible(databaseNode, newNode);
        }
        private void db_tree_third_click(int father_index, int index, string databaseName, string name)
        {
            //MessageBox.Show(father_index + "," + index + "," + name);
        }
        private async void db_tree_DoubleClick(object sender, EventArgs e)
        {
            var tree = (TreeView)sender;
            if (tree.SelectedNode == null) return;
            if (IsConnectionGroupNode(tree.SelectedNode))
            {
                if (tree.SelectedNode.Nodes.Count > 0) tree.SelectedNode.Toggle();
                return;
            }

            UpdateMainStatus(Localization.T("Status.LoadingData"));
            try
            {
                var m = GetTreePathParts(tree.SelectedNode);

                // 取得根連線節點（跳過連線群組節點）
                TreeNode rootNode = tree.SelectedNode;
                while (rootNode.Parent != null && !IsConnectionGroupNode(rootNode.Parent))
                    rootNode = rootNode.Parent;
                int index = GetConnectionIndex(rootNode);
                if (index < 0 || index >= myN.connections.Count)
                {
                    UpdateMainStatus(Localization.T("Status.SelectConnection"));
                    return;
                }

                if (m.Length == 2)
                {
                    // 代表是資料庫層級
                    await db_tree_second_click(index, tree.SelectedNode.Index, tree.SelectedNode.Text);
                    return;
                }
                if (m.Length == 3)
                {
                    // 代表是 Tables/Views 群組層級
                    if (m[2] == "Backups")
                    {
                        BackupSelectedDatabaseWithDialog();
                        return;
                    }
                    db_tree_third_click(index, tree.SelectedNode.Parent.Index, tree.SelectedNode.Parent.Text, tree.SelectedNode.Text);
                    return;
                }
                if (m.Length == 4)
                {
                    // 代表是資料表或檢視層級 -> 直接開啟
                    if (m[2] == "Views") OpenSelectedViewInQuery();
                    else if (m[2] == "Events") db_tree_AfterSelect(db_tree, new TreeViewEventArgs(tree.SelectedNode));
                    else if (m[2] == "Functions") ExecuteSelectedFunction();
                    else if (m[2] == "Users") db_tree_AfterSelect(db_tree, new TreeViewEventArgs(tree.SelectedNode));
                    else if (m[2] == "Models") db_tree_AfterSelect(db_tree, new TreeViewEventArgs(tree.SelectedNode));
                    else if (m[2] == "BI") db_tree_AfterSelect(db_tree, new TreeViewEventArgs(tree.SelectedNode));
                    else if (m[2] == "Other") db_tree_AfterSelect(db_tree, new TreeViewEventArgs(tree.SelectedNode));
                    else if (m[2] == "Reports") db_tree_AfterSelect(db_tree, new TreeViewEventArgs(tree.SelectedNode));
                    else OpenSelectedTableInQuery();
                    return;
                }

                // 如果是根連線節點 (m.Length == 1)
                var db = myN.connections[index];
                //連線測試
                if (db["isConnect"].ToString() == "T")
                {
                    if (tree.SelectedNode.Nodes.Count == 0)
                    {
                        RefreshConnectionDatabaseNodes(tree.SelectedNode);
                    }

                    tree.SelectedNode.Expand();
                    UpdateMainStatus(Localization.Format("Status.ConnectionAlreadyOpen", tree.SelectedNode.Text));
                    //((TreeView)sender).SelectedNode.Toggle();
                    /*switch (((TreeView)sender).SelectedNode.IsExpanded)
                    {
                        case false:
                            ((TreeView)sender).SelectedNode.ExpandAll();
                            break;
                        default:

                            break;
                    }
                    */
                }
                else
                {
                    TreeNode connRoot = FindConnectionNode(index);
                    if (connRoot != null) connRoot.Nodes.Clear();

                    //連線，展開
	                    switch (db["db_kind"].ToString().ToLower())
	                    {
	                    case "postgresql":
	                        {
	                            await OpenPostgreSqlConnectionAsync(index, (TreeView)sender);
	                        }
	                        break;
		                    case "sqlite":
	                        {
	                            await OpenSqliteConnectionAsync(index, (TreeView)sender);
	                        }
	                        break;
                    case "mysql":
                        {
                            await OpenMySqlConnectionAsync(index, (TreeView)sender);
                        }
                        break;
	                    case "oracle":
	                        {
	                            await OpenOracleConnectionAsync(index, (TreeView)sender);
	                        }
	                        break;
	                    case "mssql":
	                    case "sqlserver":
	                        {
	                            await OpenSqlServerConnectionAsync(index, (TreeView)sender);
	                        }
	                        break;
                }

            }
            }
            finally
            {
                if (lblMainStatus != null && lblMainStatus.Text == Localization.T("Status.LoadingData"))
                {
                    UpdateMainStatus(Localization.T("Status.Ready"));
                }
            }
        }

        private void splitContainer5_Panel1_Resize(object sender, EventArgs e)
        {
            table_top.Width = splitContainer5.Width;
            table_top.Height = splitContainer5.Height;
        }

        private void table_btn_Click(object sender, EventArgs e)
        {
            thirty_two_change("table");
            SelectDatabaseGroupNode("Tables");
            showTools("點到Tables");
        }

        private void view_btn_Click(object sender, EventArgs e)
        {
            thirty_two_change("view");
            SelectDatabaseGroupNode("Views");
            showTools("點到Views本身");
        }

        private void user_btn_Click(object sender, EventArgs e)
        {
            thirty_two_change("user");
            SelectDatabaseGroupNode("Users");
        }

        private void function_btn_Click(object sender, EventArgs e)
        {
            thirty_two_change("function");
            SelectDatabaseGroupNode("Functions");
            showTools("點到Functions本身");
        }

        private void other_btn_Click(object sender, EventArgs e)
        {
            thirty_two_change("other");
            SelectDatabaseGroupNode("Other");
        }

        private void query_section_btn_Click(object sender, EventArgs e)
        {
            thirty_two_change("query");
            SelectDatabaseGroupNode("Queries");
        }

        private void backup_btn_Click(object sender, EventArgs e)
        {
            thirty_two_change("backup");
            SelectDatabaseGroupNode("Backups");
        }

        private void auto_run_btn_Click(object sender, EventArgs e)
        {
            thirty_two_change("auto");
            SelectDatabaseGroupNode("Events");
        }

        private void model_btn_Click(object sender, EventArgs e)
        {
            thirty_two_change("model");
            SelectDatabaseGroupNode("Models");
        }

        private void bi_btn_Click(object sender, EventArgs e)
        {
            thirty_two_change("bi");
            SelectDatabaseGroupNode("BI");
        }

        private void SelectDatabaseGroupNode(string groupName)
        {
            TreeNode databaseNode = GetSelectedDatabaseNode();
            if (databaseNode == null)
            {
                UpdateMainStatus(Localization.T("Status.SelectExpandedDatabase"));
                return;
            }

            EnsureDatabaseGroupNodes(databaseNode);

            foreach (TreeNode child in databaseNode.Nodes)
            {
                if (string.Equals(GetTreeGroupKey(child), groupName, StringComparison.OrdinalIgnoreCase))
                {
                    bool alreadySelected = ReferenceEquals(db_tree.SelectedNode, child);
                    db_tree.SelectedNode = child;
                    child.EnsureVisible();
                    if (alreadySelected)
                    {
                        db_tree_AfterSelect(db_tree, new TreeViewEventArgs(child));
                    }
                    return;
                }
            }

            UpdateMainStatus("目前資料庫沒有 " + groupName + " 節點。");
        }

        private TreeNode GetSelectedDatabaseNode()
        {
            TreeNode node = db_tree.SelectedNode;
            if (node == null) return null;

            TreeNode connectionRoot = GetSelectedConnectionRoot();
            if (connectionRoot == null) return null;

            if (ReferenceEquals(node, connectionRoot))
            {
                return GetPreferredDatabaseNode(connectionRoot);
            }

            while (node != null && node.Parent != null && !ReferenceEquals(node.Parent, connectionRoot))
            {
                node = node.Parent;
            }

            return node != null && ReferenceEquals(node.Parent, connectionRoot) ? node : null;
        }

        private static TreeNode GetPreferredDatabaseNode(TreeNode connectionNode)
        {
            if (connectionNode == null) return null;

            TreeNode loadedDatabase = connectionNode.Nodes
                .Cast<TreeNode>()
                .FirstOrDefault(n => n.IsExpanded || n.Nodes.Count > 0);
            if (loadedDatabase != null) return loadedDatabase;

            return connectionNode.Nodes.Count == 1 ? connectionNode.Nodes[0] : null;
        }

	        private void EnsureDatabaseGroupNodes(TreeNode databaseNode)
	        {
	            if (databaseNode == null || databaseNode.Nodes.Count > 0) return;

            TreeNode root = databaseNode;
            while (root.Parent != null && !IsConnectionGroupNode(root.Parent)) root = root.Parent;
            int index = GetConnectionIndex(root);
            if (index < 0 || index >= myN.connections.Count) return;

	            Dictionary<string, object> conn = myN.connections[index];
	            if (!conn.ContainsKey("isConnect") || conn["isConnect"].ToString() != "T") return;
	            if (!(conn.ContainsKey("pdo") && conn["pdo"] is IDatabase db)) return;

	            _ = LoadDatabaseMetadataAsync(index, databaseNode, db, databaseNode.Text);
	        }

        private void tool_Connection_ItemClicked(object sender, ToolStripItemClickedEventArgs e)
        {

        }

        private void NewConnectionToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ShowConnectionTypeSelection();
        }

        private void connection_btn_Click(object sender, EventArgs e)
        {
            ShowConnectionTypeSelection();
        }

        private void ShowConnectionTypeSelection()
        {
            using (ConnectionTypeSelectionForm form = new ConnectionTypeSelectionForm(GetRecentConnectionTypesForWizard()))
            {
                form.CreateConnectionForm = CreateNewConnectionForm;
                form.ShowDialog(this);
            }
        }

        private void OpenNewConnectionForm(string connectionType)
        {
            using (Form form = CreateNewConnectionForm(connectionType))
            {
                if (form != null) form.ShowDialog(this);
            }
        }

        private Form CreateNewConnectionForm(string connectionType)
        {
            switch (connectionType)
            {
                case ConnectionTypeSelectionForm.MySql:
                    return new mysql_add_edit { F1 = this };
                case ConnectionTypeSelectionForm.PostgreSql:
                    return new postgresql_add_edit { F1 = this };
                case ConnectionTypeSelectionForm.SqlServer:
                    return new sqlserver_add_edit { F1 = this };
                case ConnectionTypeSelectionForm.Oracle:
                    oracle_add_edit oracleForm = new oracle_add_edit { F1 = this };
                    oracleForm.oracle_connection_type.Text = "Basic";
                    oracleForm.oracle_connection_type_selected_trigger_change();
                    return oracleForm;
                case ConnectionTypeSelectionForm.Sqlite:
                    return new sqlite_add_edit { F1 = this };
                default:
                    return null;
            }
        }

        private void OpenMySqlConnectionForm()
        {
            OpenNewConnectionForm(ConnectionTypeSelectionForm.MySql);
        }

        private void OpenPostgreSqlConnectionForm()
        {
            OpenNewConnectionForm(ConnectionTypeSelectionForm.PostgreSql);
        }

        private void OpenOracleConnectionForm()
        {
            OpenNewConnectionForm(ConnectionTypeSelectionForm.Oracle);
        }

        private void OpenSqliteConnectionForm()
        {
            OpenNewConnectionForm(ConnectionTypeSelectionForm.Sqlite);
        }

        private void OpenSqlServerConnectionForm()
        {
            OpenNewConnectionForm(ConnectionTypeSelectionForm.SqlServer);
        }

        private static string BuildOracleConnectionString(Dictionary<string, object> conn)
        {
            if (conn.ContainsKey("connString") && !string.IsNullOrWhiteSpace(conn["connString"]?.ToString()))
            {
                return conn["connString"].ToString();
            }

            Oracle.ManagedDataAccess.Client.OracleConnectionStringBuilder builder =
                new Oracle.ManagedDataAccess.Client.OracleConnectionStringBuilder();
            builder.UserID = GetConnectionValue(conn, "username");
            builder.Password = GetConnectionValue(conn, "pwd");
            builder.DataSource = string.Equals(GetConnectionValue(conn, "connection_type"), "TNS", StringComparison.OrdinalIgnoreCase)
                ? GetConnectionValue(conn, "tns_name")
                : BuildOracleBasicDataSource(conn);
            return builder.ConnectionString;
        }

        private static string BuildOracleBasicDataSource(Dictionary<string, object> conn)
        {
            string host = string.IsNullOrWhiteSpace(GetConnectionValue(conn, "host")) ? "localhost" : GetConnectionValue(conn, "host");
            string port = string.IsNullOrWhiteSpace(GetConnectionValue(conn, "port")) ? "1521" : GetConnectionValue(conn, "port");
            string value = GetConnectionValue(conn, "service_name");
            if (string.IsNullOrWhiteSpace(value)) value = GetConnectionValue(conn, "sid");
            string key = GetConnectionValue(conn, "oracle_identifier_type") == "sid" ? "SID" : "SERVICE_NAME";
            return "(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=" + host + ")(PORT=" + port + "))" +
                   "(CONNECT_DATA=(" + key + "=" + value + ")))";
        }

        private static string GetConnectionValue(Dictionary<string, object> conn, string key)
        {
            if (conn != null && conn.ContainsKey(key) && conn[key] != null) return conn[key].ToString();
            return string.Empty;
        }

        private void mysqlStripMenuItem_Click(object sender, EventArgs e)
        {
            OpenMySqlConnectionForm();
        }

        private void postgreSQLToolStripMenuItem_Click(object sender, EventArgs e)
        {
            OpenPostgreSqlConnectionForm();
        }

        private void oracleToolStripMenuItem_Click(object sender, EventArgs e)
        {
            OpenOracleConnectionForm();
        }

        private void sQLiteToolStripMenuItem_Click(object sender, EventArgs e)
        {
            OpenSqliteConnectionForm();
        }

        private void sQLServerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            OpenSqlServerConnectionForm();
        }

        public void add_connection(Dictionary<string, object> conn)
        {
            myN.connections.Add(conn);
            RememberRecentConnectionType(conn);
            myN.setSettingINI();
            drawLists();
        }

        public Dictionary<string, object> get_connection(int index)
        {
            return myN.connections[index];
        }

        public void update_connection(int index, Dictionary<string, object> conn)
        {
            // 保留舊連線的 conn_group，避免編輯時群組資訊消失
            if (index >= 0 && index < myN.connections.Count)
            {
                var existing = myN.connections[index];
                if (!conn.ContainsKey("conn_group") || conn["conn_group"] == null)
                    conn["conn_group"] = existing.ContainsKey("conn_group") && existing["conn_group"] != null ? existing["conn_group"] : "";
            }
            myN.connections[index] = conn;
            RememberRecentConnectionType(conn);
            myN.setSettingINI();
            drawLists();
        }

        private IEnumerable<string> GetRecentConnectionTypesForWizard()
        {
            return myN.connections
                .Where(conn => conn != null && conn.ContainsKey("db_kind") && conn["db_kind"] != null)
                .Select(conn => conn["db_kind"].ToString())
                .Reverse()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(5);
        }

        private static void RememberRecentConnectionType(Dictionary<string, object> conn)
        {
            if (conn == null || !conn.ContainsKey("db_kind") || conn["db_kind"] == null) return;
            ConnectionTypeSelectionForm.RememberRecentConnectionType(conn["db_kind"].ToString());
        }
        // ── 頁籤進階功能實作 ──

        private void QueryTabs_DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= queryTabs.TabPages.Count) return;
            
            var tabRect = queryTabs.GetTabRect(e.Index);
            var title = queryTabs.TabPages[e.Index].Text;
            bool isSelected = (queryTabs.SelectedIndex == e.Index);
            
            // 背景
            Color bgColor = isSelected ? ThemeManager.ElevatedColor : ThemeManager.SurfaceColor;
            using (var brush = new SolidBrush(bgColor))
            {
                e.Graphics.FillRectangle(brush, tabRect);
            }

            // 文字 (置中偏左)
            Rectangle textRect = new Rectangle(tabRect.X + 4, tabRect.Y, tabRect.Width - 25, tabRect.Height);
            TextRenderer.DrawText(e.Graphics, title, queryTabs.Font, textRect, ThemeManager.TextColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

            // 繪製關閉按鈕 (X)
            var xRect = GetCloseButtonRect(tabRect);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (Pen p = new Pen(ThemeManager.MutedTextColor, 1.5f))
            {
                // 畫兩條線組成 X
                e.Graphics.DrawLine(p, xRect.X + 3, xRect.Y + 3, xRect.Right - 3, xRect.Bottom - 3);
                e.Graphics.DrawLine(p, xRect.Right - 3, xRect.Y + 3, xRect.X + 3, xRect.Bottom - 3);
            }
        }

        private Rectangle GetCloseButtonRect(Rectangle tabRect)
        {
            return new Rectangle(tabRect.Right - 20, tabRect.Top + (tabRect.Height - 14) / 2, 14, 14);
        }

        private void QueryTabs_MouseClick(object sender, MouseEventArgs e)
        {
            for (int i = 0; i < queryTabs.TabPages.Count; i++)
            {
                var tabRect = queryTabs.GetTabRect(i);
                
                if ((e.Button == MouseButtons.Middle && tabRect.Contains(e.Location)) ||
                    (e.Button == MouseButtons.Left && GetCloseButtonRect(tabRect).Contains(e.Location)))
                {
                    // ── 增加關閉前確認邏輯 ──
                    TabPage tp = queryTabs.TabPages[i];
                    var designer = tp.Controls.OfType<TableDesignerForm>().FirstOrDefault();
                    if (designer != null)
                    {
                        if (!designer.ConfirmClose()) return; // 取消關閉
                    }
                    
                    queryTabs.TabPages.RemoveAt(i);
                    if (queryTabs.TabPages.Count == 0) queryTabs.Visible = false;
                    break;
                }
            }
        }

        private void QueryTabs_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                dragTab = GetTabAt(e.Location);
                dragStartPos = e.Location;
            }
        }

        private void QueryTabs_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || dragTab == null) return;
            
            // 只有移動超過一定距離才算拖拽
            if (Math.Abs(e.X - dragStartPos.X) < 5 && Math.Abs(e.Y - dragStartPos.Y) < 5) return;

            // 檢查是否移出範圍 (拖拽拉出功能)
            if (!queryTabs.ClientRectangle.Contains(e.Location))
            {
                var dockable = dragTab.Controls.OfType<Form>().OfType<IDockableForm>().FirstOrDefault();
                if (dockable != null)
                {
                    dragTab = null; // 停止拖拽
                    FloatDockableForm(dockable);
                    return;
                }
            }

            TabPage hoverTab = GetTabAt(e.Location);
            if (hoverTab != null && hoverTab != dragTab)
            {
                int dragIdx = queryTabs.TabPages.IndexOf(dragTab);
                int dropIdx = queryTabs.TabPages.IndexOf(hoverTab);
                
                queryTabs.TabPages.RemoveAt(dragIdx);
                queryTabs.TabPages.Insert(dropIdx, dragTab);
                queryTabs.SelectedTab = dragTab;
            }
        }

        // 從 DragDrop 資料中取出任何實作 IDockableForm 的物件
        private IDockableForm GetDockableFromDrag(IDataObject data)
        {
            foreach (string fmt in data.GetFormats(false))
            {
                try
                {
                    var obj = data.GetData(fmt, false);
                    if (obj is IDockableForm dock) return dock;
                }
                catch { }
            }
            return null;
        }

        private void QueryTabs_DragEnter(object sender, DragEventArgs e)
        {
            bool canDock = GetDockableFromDrag(e.Data) != null && IsPointInTabDropArea(new Point(e.X, e.Y));
            if (canDock)
            {
                e.Effect = DragDropEffects.Move;
                queryTabs.Visible = true;
                queryTabs.BackColor = ThemeManager.SelectionColor;
                ShowDockHint();
                if (queryTabs.TabPages.Count == 0)
                {
                    statusStrip1.Visible = true; // 拖入時顯示提示
                    lblMainStatus.Text = Localization.T("Status.ReleaseToDock") + "...";
                }
            }
            else
                e.Effect = DragDropEffects.None;
        }

        private void QueryTabs_DragOver(object sender, DragEventArgs e)
        {
            bool canDock = GetDockableFromDrag(e.Data) != null && IsPointInTabDropArea(new Point(e.X, e.Y));
            if (canDock)
            {
                e.Effect = DragDropEffects.Move;
                ShowDockHint();
            }
            else
            {
                e.Effect = DragDropEffects.None;
                HideDockHint();
            }
        }

        private void QueryTabs_DragLeave(object sender, EventArgs e)
        {
            queryTabs.BackColor = ThemeManager.WindowBackColor;
            HideDockHint();
            if (queryTabs.TabPages.Count == 0) queryTabs.Visible = false;
            statusStrip1.Visible = false; // 離開時隱藏
            lblMainStatus.Text = Localization.T("Status.Ready");
        }

        private void QueryTabs_DragDrop(object sender, DragEventArgs e)
        {
            queryTabs.BackColor = ThemeManager.WindowBackColor;
            HideDockHint();
            statusStrip1.Visible = false; // 放下時隱藏
            lblMainStatus.Text = Localization.T("Status.Ready");
            var dockable = GetDockableFromDrag(e.Data);
            if (dockable != null && IsPointInTabDropArea(new Point(e.X, e.Y)))
                DockDockableForm(dockable);
        }

        private void QueryTabs_MouseUp(object sender, MouseEventArgs e)
        {
            dragTab = null;
        }

        private TabPage GetTabAt(Point position)
        {
            for (int i = 0; i < queryTabs.TabPages.Count; i++)
            {
                if (queryTabs.GetTabRect(i).Contains(position)) return queryTabs.TabPages[i];
            }
            return null;
        }
        public void UpdateTabTitle(Form childForm)
        {
            foreach (TabPage tp in queryTabs.TabPages)
            {
                if (tp.Controls.Contains(childForm))
                {
                    tp.Text = childForm.Text;
                    break;
                }
            }
        }
    }
}
