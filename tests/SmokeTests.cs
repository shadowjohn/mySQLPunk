using System;
using System.Collections.Generic;
using System.Data;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;
using mySQLPunk;
using mySQLPunk.lib;

public static class SmokeTests
{
    [STAThread]
    public static int Main()
    {
        int passed = 0;
        Run("Geometry WKB 轉 WKT", TestGeometryWktConverter, ref passed);
        Run("View SQL 跨 provider 轉換", TestViewSqlConversion, ref passed);
        Run("View SQL 進階轉換案例", TestAdvancedViewSqlConversion, ref passed);
        Run("SQLite 專用物件 SQL builder", TestSqliteSpecialObjectSqlBuilder, ref passed);
        Run("Table Designer DDL builder", TestTableDesignerDdlBuilder, ref passed);
        Run("Table Designer ALTER provider matrix", TestTableDesignerAlterProviderMatrix, ref passed);
        Run("Pre-delete backup path builder", TestPreDeleteBackupPathBuilder, ref passed);
        Run("Database dump service", TestDatabaseDumpService, ref passed);
        Run("Query result export service", TestQueryResultExportService, ref passed);
        Run("Connection and metadata services", TestConnectionAndMetadataServices, ref passed);
        Run("Windows credential service", TestWindowsCredentialService, ref passed);
        Console.WriteLine("Smoke tests passed: " + passed);
        return 0;
    }

    private static void Run(string name, Action test, ref int passed)
    {
        try
        {
            test();
            passed++;
            Console.WriteLine("[PASS] " + name);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[FAIL] " + name);
            Console.Error.WriteLine(ex);
            Environment.Exit(1);
        }
    }

    private static void TestGeometryWktConverter()
    {
        byte[] wkb = BuildLittleEndianPointWkb(121.5, 25);
        string wkt;
        Assert(GeometryWktConverter.TryGeometryBytesToWkt(wkb, out wkt), "WKB point should convert to WKT.");
        AssertEquals("POINT (121.5 25)", wkt, "Unexpected WKT point output.");

        Assert(!GeometryWktConverter.TryGeometryBytesToWkt(new byte[] { 1, 2, 3, 4 }, out wkt), "Invalid geometry bytes should fail cleanly.");
    }

    private static byte[] BuildLittleEndianPointWkb(double x, double y)
    {
        byte[] bytes = new byte[21];
        bytes[0] = 1;
        Array.Copy(BitConverter.GetBytes((uint)1), 0, bytes, 1, 4);
        Array.Copy(BitConverter.GetBytes(x), 0, bytes, 5, 8);
        Array.Copy(BitConverter.GetBytes(y), 0, bytes, 13, 8);
        return bytes;
    }

    private static void TestViewSqlConversion()
    {
        object mysqlPreview = BuildViewSqlPreview(
            "CREATE VIEW v AS SELECT TOP (5) Id, GETDATE() AS CheckedAt FROM dbo.Users",
            "mssql",
            "mysql");
        Assert((bool)GetProperty(mysqlPreview, "CanConvert"), "SQL Server TOP query should convert to MySQL.");
        string mysqlSql = (string)GetProperty(mysqlPreview, "ConvertedSql");
        AssertContains(mysqlSql, "LIMIT 5", "Converted MySQL SQL should contain LIMIT.");
        AssertContains(mysqlSql, "CURRENT_TIMESTAMP", "Converted MySQL SQL should rewrite GETDATE().");

        object mssqlPreview = BuildViewSqlPreview(
            "SELECT id, DATE_FORMAT(created_at, '%Y-%m-%d') AS day_text FROM logs LIMIT 10",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(mssqlPreview, "CanConvert"), "MySQL LIMIT query should convert to SQL Server.");
        string mssqlSql = (string)GetProperty(mssqlPreview, "ConvertedSql");
        AssertContains(mssqlSql, "TOP (10)", "Converted SQL Server SQL should contain TOP.");
        AssertContains(mssqlSql, "FORMAT(", "Converted SQL Server SQL should rewrite DATE_FORMAT.");
    }

    private static void TestAdvancedViewSqlConversion()
    {
        object pgPreview = BuildViewSqlPreview(
            "SELECT id, name FROM accounts WHERE is_active = 1 AND ROWNUM <= 3",
            "oracle",
            "postgresql");
        Assert((bool)GetProperty(pgPreview, "CanConvert"), "Simple Oracle ROWNUM predicate should convert to PostgreSQL.");
        string pgSql = (string)GetProperty(pgPreview, "ConvertedSql");
        AssertContains(pgSql, "LIMIT 3", "Converted PostgreSQL SQL should contain LIMIT from ROWNUM.");
        AssertNotContains(pgSql, "ROWNUM", "Converted PostgreSQL SQL should remove simple ROWNUM predicate.");

        object aggregatePreview = BuildViewSqlPreview(
            "SELECT project_id, GROUP_CONCAT(user_name SEPARATOR '|') AS members FROM project_users GROUP BY project_id",
            "mysql",
            "mssql");
        Assert((bool)GetProperty(aggregatePreview, "CanConvert"), "MySQL GROUP_CONCAT should convert to SQL Server.");
        string aggregateSql = (string)GetProperty(aggregatePreview, "ConvertedSql");
        AssertContains(aggregateSql, "STRING_AGG(user_name, '|')", "Converted SQL Server SQL should use STRING_AGG.");

        object jsonPreview = BuildViewSqlPreview(
            "SELECT JSON_EXTRACT(payload, '$.status') AS status_text FROM event_log",
            "mysql",
            "oracle");
        Assert((bool)GetProperty(jsonPreview, "CanConvert"), "MySQL JSON_EXTRACT should convert to Oracle.");
        string jsonSql = (string)GetProperty(jsonPreview, "ConvertedSql");
        AssertContains(jsonSql, "JSON_VALUE(payload, '$.status')", "Converted Oracle SQL should use JSON_VALUE.");

        object cteWindowPreview = BuildViewSqlPreview(
            "WITH ranked AS (SELECT id, ROW_NUMBER() OVER (PARTITION BY group_id ORDER BY created_at DESC) AS rn FROM items) SELECT id FROM ranked WHERE rn = 1",
            "postgresql",
            "mssql");
        Assert((bool)GetProperty(cteWindowPreview, "CanConvert"), "Portable CTE/window SQL should be preserved for SQL Server.");
        string cteWindowSql = (string)GetProperty(cteWindowPreview, "ConvertedSql");
        AssertContains(cteWindowSql, "WITH ranked AS", "Converted SQL should preserve CTE.");
        AssertContains(cteWindowSql, "ROW_NUMBER() OVER", "Converted SQL should preserve window function.");

        object unsupportedPreview = BuildViewSqlPreview(
            "CREATE VIEW v AS SELECT id FROM employee START WITH manager_id IS NULL CONNECT BY PRIOR id = manager_id",
            "oracle",
            "mysql");
        Assert(!(bool)GetProperty(unsupportedPreview, "CanConvert"), "Oracle hierarchical query should be rejected instead of converted silently.");
        string reason = (string)GetProperty(unsupportedPreview, "Reason");
        Assert(!string.IsNullOrWhiteSpace(reason), "Unsupported conversion should return a readable reason.");
    }

    private static object BuildViewSqlPreview(string sql, string sourceProvider, string targetProvider)
    {
        Type converter = typeof(DatabaseCopyService).Assembly.GetType("mySQLPunk.lib.ViewSqlDialectConverter", true);
        MethodInfo method = converter.GetMethod("BuildPreview", BindingFlags.Public | BindingFlags.Static);
        return method.Invoke(null, new object[] { sql, sourceProvider, targetProvider });
    }

    private static object GetProperty(object target, string name)
    {
        return target.GetType().GetProperty(name).GetValue(target, null);
    }

    private static void TestSqliteSpecialObjectSqlBuilder()
    {
        string ftsSql = SqliteSpecialObjectSqlBuilder.BuildFtsVirtualTable(
            "doc_search",
            SqliteSpecialObjectSqlBuilder.SplitCommaSeparatedNames("title, body"),
            "unicode61",
            "documents");
        AssertContains(ftsSql, "CREATE VIRTUAL TABLE \"doc_search\" USING fts5", "FTS SQL should create an fts5 virtual table.");
        AssertContains(ftsSql, "\"title\"", "FTS SQL should quote title column.");
        AssertContains(ftsSql, "tokenize = 'unicode61'", "FTS SQL should include tokenizer.");
        AssertContains(ftsSql, "content = 'documents'", "FTS SQL should include content table.");

        string rtreeSql = SqliteSpecialObjectSqlBuilder.BuildRTreeVirtualTable(
            "idx_boxes",
            "id",
            SqliteSpecialObjectSqlBuilder.SplitCommaSeparatedNames("minX, maxX, minY, maxY"));
        AssertContains(rtreeSql, "CREATE VIRTUAL TABLE \"idx_boxes\" USING rtree", "RTree SQL should create an rtree virtual table.");
        AssertContains(rtreeSql, "\"minX\"", "RTree SQL should quote dimension columns.");

        string spatialSql = SqliteSpecialObjectSqlBuilder.BuildSpatiaLiteSpatialIndex("places", "geom");
        AssertEquals("SELECT CreateSpatialIndex('places', 'geom');", spatialSql, "SpatiaLite spatial index SQL should call CreateSpatialIndex.");

        bool rejected = false;
        try
        {
            SqliteSpecialObjectSqlBuilder.BuildRTreeVirtualTable("bad", "id", SqliteSpecialObjectSqlBuilder.SplitCommaSeparatedNames("minX, maxX, minY"));
        }
        catch (ArgumentException)
        {
            rejected = true;
        }
        Assert(rejected, "RTree builder should reject incomplete min/max dimension pairs.");
    }

    private static void TestTableDesignerDdlBuilder()
    {
        string mysqlSql = BuildCreateTableSql(new my_mysql(), "codex_smoke_mysql");
        AssertContains(mysqlSql, "CREATE TABLE", "MySQL create table SQL should be generated.");
        AssertContains(mysqlSql, "codex_smoke_mysql", "MySQL create table SQL should include table name.");
        AssertContains(mysqlSql, "PRIMARY KEY", "MySQL create table SQL should include primary key.");

        string sqliteSql = BuildCreateTableSql(new my_sqlite(), "codex_smoke_sqlite");
        AssertContains(sqliteSql, "CREATE TABLE", "SQLite create table SQL should be generated.");
        AssertContains(sqliteSql, "codex_smoke_sqlite", "SQLite create table SQL should include table name.");
        AssertContains(sqliteSql, "__mysqlpunk_column_comments", "SQLite create table SQL should include sidecar comments when comments exist.");
    }

    private static string BuildCreateTableSql(IDatabase db, string tableName)
    {
        using (TableDesignerForm form = new TableDesignerForm(db, "main", null))
        {
            SetTextBoxField(form, "txtTableName", tableName);

            MethodInfo createTableMethod = typeof(TableDesignerForm).GetMethod("CreateColumnsDisplayTable", BindingFlags.Instance | BindingFlags.NonPublic);
            DataTable columns = (DataTable)createTableMethod.Invoke(form, null);

            DataRow row = columns.NewRow();
            row["Name"] = "Id";
            row["Type"] = "int";
            row["NotNull"] = true;
            row["PK"] = true;
            row["Comment"] = "識別碼";
            columns.Rows.Add(row);

            MethodInfo buildMethod = typeof(TableDesignerForm).GetMethod("BuildCreateTableSql", BindingFlags.Instance | BindingFlags.NonPublic);
            return (string)buildMethod.Invoke(form, new object[] { columns });
        }
    }

    private static void TestTableDesignerAlterProviderMatrix()
    {
        string mysqlSql = BuildExistingAlterSql(
            new my_mysql(),
            "main",
            "demo_table",
            CreateOriginalColumnsForAlter(includeRemovedColumn: true),
            CreateChangedColumnsForAlter(includeRemovedColumn: false),
            "BuildMySqlAlterTableSql");
        AssertContains(mysqlSql, "ALTER TABLE `main`.`demo_table`", "MySQL ALTER should target the existing table.");
        AssertContains(mysqlSql, "DROP COLUMN `removed_col`", "MySQL ALTER should drop removed columns.");
        AssertContains(mysqlSql, "CHANGE COLUMN `legacy_name` `display_name`", "MySQL ALTER should rename changed columns.");
        AssertContains(mysqlSql, "ADD COLUMN `created_at`", "MySQL ALTER should add new columns.");

        string postgresqlSql = BuildExistingAlterSql(
            CreateProvider<my_postgresql>(),
            "main",
            "public.demo_table",
            CreateOriginalColumnsForAlter(includeRemovedColumn: false),
            CreateChangedColumnsForAlter(includeRemovedColumn: false),
            "BuildGenericAlterTableSql");
        AssertContains(postgresqlSql, "RENAME COLUMN \"legacy_name\" TO \"display_name\"", "PostgreSQL ALTER should rename columns.");
        AssertContains(postgresqlSql, "ALTER COLUMN \"display_name\" TYPE VARCHAR(120)", "PostgreSQL ALTER should change column type.");
        AssertContains(postgresqlSql, "ALTER COLUMN \"display_name\" SET NOT NULL", "PostgreSQL ALTER should change nullability.");
        AssertContains(postgresqlSql, "ALTER COLUMN \"display_name\" SET DEFAULT", "PostgreSQL ALTER should change defaults.");
        AssertContains(postgresqlSql, "COMMENT ON COLUMN \"public\".\"demo_table\".\"display_name\"", "PostgreSQL ALTER should update comments.");

        string sqlServerSql = BuildExistingAlterSql(
            CreateProvider<my_mssql>(),
            "main",
            "dbo.demo_table",
            CreateOriginalColumnsForAlter(includeRemovedColumn: false),
            CreateChangedColumnsForAlter(includeRemovedColumn: false),
            "BuildGenericAlterTableSql");
        AssertContains(sqlServerSql, "sys.sp_rename", "SQL Server ALTER should rename columns.");
        AssertContains(sqlServerSql, "ALTER COLUMN [display_name] NVARCHAR(120) NOT NULL", "SQL Server ALTER should change type and nullability.");
        AssertContains(sqlServerSql, "sys.default_constraints", "SQL Server ALTER should drop existing default constraints safely.");
        AssertContains(sqlServerSql, "ADD CONSTRAINT [DF_demo_table_display_name]", "SQL Server ALTER should add named default constraints.");
        AssertContains(sqlServerSql, "sp_addextendedproperty", "SQL Server ALTER should update column comments.");

        string oracleSql = BuildExistingAlterSql(
            CreateProvider<my_oracle>(),
            "MAIN",
            "DEMO_TABLE",
            CreateOriginalColumnsForAlter(includeRemovedColumn: false),
            CreateChangedColumnsForAlter(includeRemovedColumn: false),
            "BuildGenericAlterTableSql");
        AssertContains(oracleSql, "RENAME COLUMN \"legacy_name\" TO \"display_name\"", "Oracle ALTER should rename columns.");
        AssertContains(oracleSql, "MODIFY (\"display_name\" VARCHAR2(120)", "Oracle ALTER should modify column definitions.");
        AssertContains(oracleSql, "COMMENT ON COLUMN \"MAIN\".\"DEMO_TABLE\".\"display_name\"", "Oracle ALTER should update comments.");

        string sqliteSql = BuildExistingAlterSql(
            new my_sqlite(),
            "main",
            "demo_table",
            CreateOriginalColumnsForAlter(includeRemovedColumn: true),
            CreateChangedColumnsForAlter(includeRemovedColumn: false),
            "BuildGenericAlterTableSql");
        AssertContains(sqliteSql, "BEGIN TRANSACTION", "SQLite ALTER should rebuild the table inside a transaction.");
        AssertContains(sqliteSql, "CREATE TABLE \"__mysqlpunk_rebuild_demo_table\"", "SQLite ALTER should create a rebuild table.");
        AssertContains(sqliteSql, "DROP TABLE \"demo_table\"", "SQLite ALTER should drop the old table during rebuild.");
        AssertContains(sqliteSql, "RENAME TO \"demo_table\"", "SQLite ALTER should rename the rebuild table back.");
        AssertContains(sqliteSql, "__mysqlpunk_column_comments", "SQLite ALTER should preserve sidecar comments.");
    }

    private static void TestPreDeleteBackupPathBuilder()
    {
        MethodInfo buildMethod = typeof(Form1).GetMethod("BuildLogicalPreDeleteBackupPath", BindingFlags.Static | BindingFlags.NonPublic);
        string path = (string)buildMethod.Invoke(null, new object[] { "sales db:2026", "mysql", new DateTime(2026, 5, 19, 8, 7, 6) });

        AssertContains(path, "mySQLPunk", "Pre-delete backup path should live under the mySQLPunk backup directory.");
        AssertContains(path, "pre-delete-backups", "Pre-delete backup path should use the pre-delete backup directory.");
        AssertContains(path, "sales_db_2026_mysql_before_delete_20260519_080706.sql", "Pre-delete backup file name should be deterministic and sanitized.");
    }

    private static void TestDatabaseDumpService()
    {
        FakeDumpDatabase db = new FakeDumpDatabase();
        string dump = DatabaseDumpService.BuildDatabaseDump(db, "main");

        AssertContains(dump, "-- mySQLPunk Database Backup", "Database dump service should emit the backup header.");
        AssertContains(dump, "CREATE TABLE \"public\".\"users\"", "Database dump service should include table DDL.");
        AssertContains(dump, "INSERT INTO \"public.users\" (\"id\", \"name\", \"payload\") VALUES (1, 'O''Reilly', '\\x0102');", "Database dump service should preserve existing INSERT target behavior and escaped values.");
        AssertContains(dump, "CREATE VIEW \"public\".\"active_users\"", "Database dump service should include view DDL.");

        AssertEquals("\"public\".\"users\"", DatabaseDumpService.BuildQualifiedObjectName(db, "main", "public.users"), "PostgreSQL qualified table name should preserve schema.");
        AssertEquals("'\\x0A0B'", DatabaseDumpService.ToSqlLiteral(db, new byte[] { 0x0A, 0x0B }), "PostgreSQL byte literal should be hex escaped.");
    }

    private static void TestQueryResultExportService()
    {
        DataTable table = new DataTable();
        table.Columns.Add("Name", typeof(string));
        table.Columns.Add("Amount", typeof(decimal));
        table.Columns.Add("Payload", typeof(byte[]));
        table.Rows.Add("A, B", 12.5m, new byte[] { 0xAA, 0xBB, 0xCC });

        string csv = QueryResultExportService.BuildText(table, QueryResultExportFormat.Csv);
        AssertContains(csv, "\"A, B\",12.5,[BLOB 3 bytes] 0xAABBCC", "CSV export should escape commas and format BLOB values.");

        string json = QueryResultExportService.BuildText(table, QueryResultExportFormat.Json);
        AssertContains(json, "\"Name\": \"A, B\"", "JSON export should include string values.");
        AssertContains(json, "[BLOB 3 bytes] 0xAABBCC", "JSON export should use the shared BLOB preview.");

        string markdown = QueryResultExportService.BuildText(table, QueryResultExportFormat.Markdown);
        AssertContains(markdown, "| Name | Amount | Payload |", "Markdown export should include headers.");
        Assert(QueryResultExportService.ResolveFormat("result.json", 2) == QueryResultExportFormat.Json, "Extension should determine query export format.");
        Assert(QueryResultExportService.CountExportRows(table) == 1, "Query export service should count non-deleted rows.");
    }

    private static void TestConnectionAndMetadataServices()
    {
        FakeDumpDatabase openedDb = null;
        ConnectionOpenResult openResult = ConnectionOpenService.Open(() =>
        {
            openedDb = new FakeDumpDatabase();
            return openedDb;
        }, "Host=localhost;Database=main");

        Assert(ReferenceEquals(openedDb, openResult.Database), "ConnectionOpenService should return the opened database instance.");
        Assert(openedDb.WasOpened, "ConnectionOpenService should open the database.");
        AssertEquals("Host=localhost;Database=main", openedDb.ConnectionString, "ConnectionOpenService should set the connection string.");
        Assert(openResult.Databases.Count == 1 && openResult.Databases[0] == "main", "ConnectionOpenService should return database names.");
        Assert(ConnectionOpenService.ShouldOfferRetry(new TimeoutException("timeout")), "Timeout should be retryable.");
        Assert(!ConnectionOpenService.ShouldOfferRetry(new Exception("password authentication failed")), "Credential failures should not be retryable.");

        MetadataLoadService metadataService = new MetadataLoadService(
            (db, name) => CreateNamedRowsTable("Name", "fn_ping"),
            (db, name, connInfo) => CreateNamedRowsTable("Name", connInfo["user"].ToString()),
            (db, name) => CreateNamedRowsTable("Name", "ev_daily"));
        DatabaseMetadataSnapshot snapshot = metadataService.Load(openedDb, "main", new Dictionary<string, object> { { "user", "tester" } });

        Assert(snapshot.Tables.Count == 1 && snapshot.Tables[0] == "public.users", "MetadataLoadService should load table names.");
        Assert(snapshot.Views.Count == 1 && snapshot.Views[0] == "public.active_users", "MetadataLoadService should load view names.");
        AssertEquals("fn_ping", snapshot.Functions.Rows[0]["Name"].ToString(), "MetadataLoadService should use the function loader.");
        AssertEquals("tester", snapshot.Users.Rows[0]["Name"].ToString(), "MetadataLoadService should pass connection info to the user loader.");
        AssertEquals("ev_daily", snapshot.Events.Rows[0]["Name"].ToString(), "MetadataLoadService should use the event loader.");
    }

    private static void TestWindowsCredentialService()
    {
        Dictionary<string, object> conn = new Dictionary<string, object>
        {
            { "conn_name", "Smoke Test/Connection" },
            { "db_kind", "mysql" },
            { "username", "tester" },
            { "host", "localhost" },
            { "port", "3306" }
        };
        string target = WindowsCredentialService.BuildTargetName("default", conn) + "/" + Guid.NewGuid().ToString("N");
        AssertContains(target, "mySQLPunk/default/mysql/Smoke_Test_Connection/tester@localhost_3306", "Credential target should be deterministic and sanitized.");

        try
        {
            Assert(WindowsCredentialService.TryWritePassword(target, "tester", "secret-value"), "Credential service should write a password.");
            string password;
            Assert(WindowsCredentialService.TryReadPassword(target, out password), "Credential service should read a password.");
            AssertEquals("secret-value", password, "Credential service should round-trip the password.");
        }
        finally
        {
            Assert(WindowsCredentialService.TryDeletePassword(target), "Credential service should delete the test credential.");
        }
    }

    private static DataTable CreateNamedRowsTable(string columnName, string value)
    {
        DataTable table = new DataTable();
        table.Columns.Add(columnName);
        table.Rows.Add(value);
        return table;
    }

    private static string BuildExistingAlterSql(IDatabase db, string databaseName, string tableName, DataTable originalColumns, DataTable currentColumns, string methodName)
    {
        TableDesignerForm form = (TableDesignerForm)FormatterServices.GetUninitializedObject(typeof(TableDesignerForm));
        DataGridView indexesGrid = new DataGridView();
        DataTable indexes = CreateDesignerIndexesTable();
        indexesGrid.DataSource = indexes;

        SetPrivateField(form, "_db", db);
        SetPrivateField(form, "_databaseName", databaseName);
        SetPrivateField(form, "_tableName", tableName);
        SetPrivateField(form, "_originalDt", originalColumns);
        SetPrivateField(form, "_originalIdxDt", indexes.Copy());
        SetPrivateField(form, "dgvIndexes", indexesGrid);

        try
        {
            MethodInfo buildMethod = typeof(TableDesignerForm).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            return (string)buildMethod.Invoke(form, new object[] { currentColumns });
        }
        finally
        {
            indexesGrid.Dispose();
        }
    }

    private static T CreateProvider<T>() where T : class, IDatabase
    {
        return (T)FormatterServices.GetUninitializedObject(typeof(T));
    }

    private static DataTable CreateOriginalColumnsForAlter(bool includeRemovedColumn)
    {
        DataTable columns = CreateDesignerColumnsTable();
        AddDesignerColumn(columns, "legacy_name", "varchar", "50", "", false, false, "", "old comment", "legacy_name");
        if (includeRemovedColumn)
        {
            AddDesignerColumn(columns, "removed_col", "int", "", "", false, false, "", "", "removed_col");
        }
        return columns;
    }

    private static DataTable CreateChangedColumnsForAlter(bool includeRemovedColumn)
    {
        DataTable columns = CreateDesignerColumnsTable();
        AddDesignerColumn(columns, "display_name", "varchar", "120", "", true, false, "unknown", "display comment", "legacy_name");
        if (includeRemovedColumn)
        {
            AddDesignerColumn(columns, "removed_col", "int", "", "", false, false, "", "", "removed_col");
        }
        AddDesignerColumn(columns, "created_at", "datetime", "", "", false, false, "CURRENT_TIMESTAMP", "created time", "");
        return columns;
    }

    private static DataTable CreateDesignerColumnsTable()
    {
        DataTable columns = new DataTable();
        columns.Columns.Add("Name");
        columns.Columns.Add("Type");
        columns.Columns.Add("Length");
        columns.Columns.Add("Decimals");
        columns.Columns.Add("NotNull", typeof(bool));
        columns.Columns.Add("PK", typeof(bool));
        columns.Columns.Add("Default");
        columns.Columns.Add("Comment");
        columns.Columns.Add("_OldName");
        columns.Columns.Add("_AutoComment", typeof(bool));
        return columns;
    }

    private static void AddDesignerColumn(DataTable table, string name, string type, string length, string decimals, bool notNull, bool primaryKey, string defaultValue, string comment, string oldName)
    {
        DataRow row = table.NewRow();
        row["Name"] = name;
        row["Type"] = type;
        row["Length"] = length;
        row["Decimals"] = decimals;
        row["NotNull"] = notNull;
        row["PK"] = primaryKey;
        row["Default"] = defaultValue;
        row["Comment"] = comment;
        row["_OldName"] = oldName;
        row["_AutoComment"] = false;
        table.Rows.Add(row);
    }

    private static DataTable CreateDesignerIndexesTable()
    {
        DataTable indexes = new DataTable();
        indexes.Columns.Add("?迂");
        indexes.Columns.Add("甈?");
        indexes.Columns.Add("蝝Ｗ?憿?");
        indexes.Columns.Add("蝝Ｗ??寞?");
        indexes.Columns.Add("閮餉圾");
        indexes.Columns.Add("_OldName");
        return indexes;
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        field.SetValue(target, value);
    }

    private static void SetTextBoxField(object target, string fieldName, string value)
    {
        FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        TextBox textBox = (TextBox)field.GetValue(target);
        textBox.Text = value;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void AssertEquals(string expected, string actual, string message)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new Exception(message + " Expected: " + expected + " Actual: " + actual);
        }
    }

    private static void AssertContains(string value, string expected, string message)
    {
        if (value == null || value.IndexOf(expected, StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new Exception(message + " Expected fragment: " + expected + " Actual: " + value);
        }
    }

    private static void AssertNotContains(string value, string unexpected, string message)
    {
        if (value != null && value.IndexOf(unexpected, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            throw new Exception(message + " Unexpected fragment: " + unexpected + " Actual: " + value);
        }
    }

    private sealed class FakeDumpDatabase : IDatabase
    {
        public ConnectionState State => ConnectionState.Open;
        public string ProviderName => "postgresql";
        public string ConnectionString;
        public bool WasOpened;

        public void SetConn(string connectionString) { ConnectionString = connectionString; }
        public void Open() { WasOpened = true; }
        public void Close() { }
        public void Dispose() { }

        public DataTable SelectSQL(string sql, Dictionary<string, object> parameters = null) { throw new NotSupportedException(); }
        public Dictionary<string, string> ExecSQL(string sql, Dictionary<string, object> parameters = null) { throw new NotSupportedException(); }
        public System.Threading.Tasks.Task<DataTable> SelectSQLAsync(string sql, Dictionary<string, object> parameters = null) { throw new NotSupportedException(); }
        public System.Threading.Tasks.Task<Dictionary<string, string>> ExecSQLAsync(string sql, Dictionary<string, object> parameters = null) { throw new NotSupportedException(); }
        public List<string> GetDatabases() { return new List<string> { "main" }; }
        public List<string> GetTables(string databaseName) { return new List<string> { "public.users" }; }
        public List<string> GetViews(string databaseName) { return new List<string> { "public.active_users" }; }
        public DataTable GetColumns(string databaseName, string tableName) { return new DataTable(); }
        public DataTable GetIndexes(string databaseName, string tableName) { return new DataTable(); }
        public DataTable GetTableStatus(string databaseName) { return new DataTable(); }
        public Dictionary<string, string> GetDatabaseInfo(string databaseName) { return new Dictionary<string, string>(); }
        public string GetTableCreateStatement(string databaseName, string tableName) { return "CREATE TABLE \"public\".\"users\" (\"id\" integer, \"name\" text, \"payload\" bytea)"; }
        public bool TableExists(string databaseName, string tableName) { return true; }
        public bool ViewExists(string databaseName, string viewName) { return true; }
        public void RenameTable(string databaseName, string oldTableName, string newTableName) { throw new NotSupportedException(); }
        public void RenameView(string databaseName, string oldViewName, string newViewName) { throw new NotSupportedException(); }
        public long CountRows(string databaseName, string tableName) { return 1; }
        public DataTable GetCopyColumns(string databaseName, string tableName) { throw new NotSupportedException(); }
        public DataTable GetCopyIndexes(string databaseName, string tableName) { throw new NotSupportedException(); }
        public void CreateTableForCopy(string databaseName, string tableName, DataTable sourceColumns, string sourceProvider) { throw new NotSupportedException(); }
        public void CreateIndexesForCopy(string databaseName, string tableName, DataTable sourceIndexes, string sourceProvider) { throw new NotSupportedException(); }
        public DataTable SelectTablePage(string databaseName, string tableName, long offset, int limit)
        {
            DataTable table = new DataTable();
            table.Columns.Add("id", typeof(int));
            table.Columns.Add("name", typeof(string));
            table.Columns.Add("payload", typeof(byte[]));
            table.Rows.Add(1, "O'Reilly", new byte[] { 0x01, 0x02 });
            return table;
        }
        public void InsertTableBatch(string databaseName, string tableName, DataTable rows) { throw new NotSupportedException(); }
        public string GetViewCreateStatement(string databaseName, string viewName) { return "CREATE VIEW \"public\".\"active_users\" AS SELECT * FROM \"public\".\"users\""; }
        public void CreateViewFromStatement(string databaseName, string viewName, string sourceViewSql) { throw new NotSupportedException(); }
    }
}
