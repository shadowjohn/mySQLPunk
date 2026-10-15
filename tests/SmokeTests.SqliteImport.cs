using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using mySQLPunk.lib;
using mySQLPunk.lib.SqliteImport;

public static partial class SmokeTests
{
    private static void SqliteImport_PreflightScanner_SpatiaLiteAndManifest()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "mysqlpunk_sqlite_preflight_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string sqlFile = Path.Combine(tempDir, "spatial_sample.sql");

        try
        {
            string sqlContent =
                "-- SQLite GIS Dump Header\r\n" +
                "PRAGMA foreign_keys = OFF;\r\n" +
                "BEGIN TRANSACTION;\r\n" +
                "CREATE TABLE \"spatial_points\" (\"id\" INTEGER PRIMARY KEY, \"name\" TEXT, \"geom\" BLOB);\r\n" +
                "SELECT InitSpatialMetaData(1);\r\n" +
                "SELECT AddGeometryColumn('spatial_points', 'geom', 4326, 'POINT', 'XY');\r\n" +
                "INSERT INTO \"spatial_points\" VALUES (1, 'Point with ; inside string', X'00010000007C0101000000');\r\n" +
                "COMMIT;\r\n" +
                "-- ========================================================\r\n" +
                "-- mySQLPunk Native Manifest\r\n" +
                "-- Tables: 1\r\n" +
                "-- TotalRows: 1\r\n" +
                "-- Manifest-Table: spatial_points | Rows: 1\r\n" +
                "-- Manifest-SHA256: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\r\n" +
                "-- ========================================================\r\n";

            File.WriteAllText(sqlFile, sqlContent, Encoding.UTF8);

            var preflight = SqliteImportPreflightScanner.Scan(sqlFile, CancellationToken.None);
            Assert(preflight.Passed, "Preflight scan should pass");
            Assert(preflight.FileBytes > 0, "FileBytes > 0");
            Assert(preflight.DetectedSpatiaLite, "Preflight must detect SpatiaLite functions like InitSpatialMetaData / AddGeometryColumn");
            Assert(preflight.HasManifest, "Preflight must detect Native Manifest");
            Assert(preflight.ManifestTables == 1, "ManifestTables count match 1");
            Assert(preflight.ManifestRows == 1, "ManifestRows count match 1");
            AssertEquals("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", preflight.ManifestSha256, "Manifest SHA-256 match");

            // 測試 Embedded NUL 偵測
            string badFile = Path.Combine(tempDir, "corrupted_nul.sql");
            File.WriteAllText(badFile, "CREATE TABLE \"bad\" (\0id int);", Encoding.UTF8);
            var badPreflight = SqliteImportPreflightScanner.Scan(badFile, CancellationToken.None);
            Assert(!badPreflight.Passed, "Preflight must fail on embedded NUL");
            AssertContains(badPreflight.Issues[0], "Embedded NUL", "NUL issue reported");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    private static void SqliteImport_Runner_ExecutionAndManifestVerification()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "mysqlpunk_sqlite_runner_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string sqlFile = Path.Combine(tempDir, "run_sqlite_import.sql");

        try
        {
            string sqlContent =
                "PRAGMA foreign_keys = OFF;\r\n" +
                "CREATE TABLE \"places\" (\"id\" INTEGER PRIMARY KEY, \"geom\" BLOB);\r\n" +
                "INSERT INTO \"places\" VALUES (1, X'000100');\r\n" +
                "INSERT INTO \"places\" VALUES (2, X'000200');\r\n" +
                "-- ========================================================\r\n" +
                "-- mySQLPunk Native Manifest\r\n" +
                "-- Tables: 1\r\n" +
                "-- TotalRows: 2\r\n" +
                "-- Manifest-Table: places | Rows: 2\r\n" +
                "-- ========================================================\r\n";

            File.WriteAllText(sqlFile, sqlContent, Encoding.UTF8);

            var mockDb = new MockSqliteImportDatabase();
            var runner = new SqliteImportRunner(mockDb, "test_gis.sqlite");

            var request = new SqliteImportRequest(Guid.NewGuid(), sqlFile, "test_gis.sqlite");
            var options = new SqliteImportOptions
            {
                ExistingObjectStrategy = SqliteExistingObjectStrategy.SkipExisting,
                ContinueOnError = true,
                WrapInTransaction = true
            };

            SqliteImportProgress lastProgress = null;
            var progress = new Progress<SqliteImportProgress>(p => lastProgress = p);

            var task = runner.RunAsync(request, options, progress, CancellationToken.None);
            task.Wait();
            var result = task.Result;

            Assert(result.Status == SqliteImportStatus.Completed, "Import status should be Completed");
            Assert(result.ExecutedStatements == 4, "Executed statements count should be 4 (PRAGMA, CREATE, 2x INSERT), got " + result.ExecutedStatements);
            Assert(result.FailedStatements == 0, "Failed statements should be 0");
            Assert(result.TotalRowsAffected == 2, "TotalRowsAffected should be 2, got " + result.TotalRowsAffected);
            Assert(result.ManifestVerified, "Native Manifest should verify table row counts match exactly (2 == 2)");
            Assert(File.Exists(result.LogPath), "Log file must exist at " + result.LogPath);

            string logContent = File.ReadAllText(result.LogPath);
            AssertContains(logContent, "mySQLPunk SQLite Import Execution Log", "Log contains SQLite import header");
            AssertContains(logContent, "Manifest Check: Table \"places\" -> Expected: 2, Actual: 2", "Log contains manifest verification pass");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    private static void SqliteImport_Runner_ExistingObjectStrategies()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "mysqlpunk_sqlite_strat_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string sqlFile = Path.Combine(tempDir, "conflict.sql");

        try
        {
            string sqlContent =
                "CREATE TABLE \"existing_tbl\" (\"id\" INTEGER);\r\n" +
                "INSERT INTO \"existing_tbl\" VALUES (100);\r\n";
            File.WriteAllText(sqlFile, sqlContent, Encoding.UTF8);

            var request = new SqliteImportRequest(Guid.NewGuid(), sqlFile, "test.sqlite");

            // 1. 測試 AbortIfExists：既有物件存在時立即終止
            var mockDb1 = new MockSqliteImportDatabase();
            mockDb1.ExistingTables.Add("existing_tbl");
            var runner1 = new SqliteImportRunner(mockDb1, "test.sqlite");
            var res1 = runner1.RunAsync(request, new SqliteImportOptions { ExistingObjectStrategy = SqliteExistingObjectStrategy.AbortIfExists, ContinueOnError = false }, null, CancellationToken.None).Result;
            Assert(res1.Status == SqliteImportStatus.Failed, "AbortIfExists strategy must fail when table exists");
            Assert(res1.FailedStatements >= 1, "Failed statement recorded");

            // 2. 測試 SkipExisting：既有物件存在時略過結構與資料
            var mockDb2 = new MockSqliteImportDatabase();
            mockDb2.ExistingTables.Add("existing_tbl");
            var runner2 = new SqliteImportRunner(mockDb2, "test.sqlite");
            var res2 = runner2.RunAsync(request, new SqliteImportOptions { ExistingObjectStrategy = SqliteExistingObjectStrategy.SkipExisting }, null, CancellationToken.None).Result;
            Assert(res2.Status == SqliteImportStatus.Completed, "SkipExisting strategy should complete");
            Assert(res2.SkippedStatements == 2, "Both CREATE TABLE and INSERT should be skipped, got " + res2.SkippedStatements);

            // 3. 測試 DropAndRecreate：先 DROP 後 CREATE
            var mockDb3 = new MockSqliteImportDatabase();
            mockDb3.ExistingTables.Add("existing_tbl");
            var runner3 = new SqliteImportRunner(mockDb3, "test.sqlite");
            var res3 = runner3.RunAsync(request, new SqliteImportOptions { ExistingObjectStrategy = SqliteExistingObjectStrategy.DropAndRecreate }, null, CancellationToken.None).Result;
            Assert(res3.Status == SqliteImportStatus.Completed, "DropAndRecreate strategy should complete");
            Assert(res3.ExecutedStatements == 2, "Executed statements should be 2");
            Assert(mockDb3.ExecutedStatements.Exists(s => s.StartsWith("DROP TABLE IF EXISTS", StringComparison.OrdinalIgnoreCase)), "DROP TABLE must be executed before recreate");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    private static void SqliteImport_Runner_TransactionRollbackOnFailure()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "mysqlpunk_sqlite_tx_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string sqlFile = Path.Combine(tempDir, "fail_tx.sql");

        try
        {
            string sqlContent =
                "INSERT INTO \"tbl\" VALUES (1);\r\n" +
                "FAILING_SQL_SYNTAX_ERROR;\r\n";
            File.WriteAllText(sqlFile, sqlContent, Encoding.UTF8);

            var mockDb = new MockSqliteImportDatabase();
            mockDb.FailOnFailingSql = true;

            var runner = new SqliteImportRunner(mockDb, "test.sqlite");
            var request = new SqliteImportRequest(Guid.NewGuid(), sqlFile, "test.sqlite");
            var options = new SqliteImportOptions
            {
                WrapInTransaction = true,
                ContinueOnError = false
            };

            var res = runner.RunAsync(request, options, null, CancellationToken.None).Result;
            Assert(res.Status == SqliteImportStatus.Failed, "Import should fail on syntax error with ContinueOnError=false");
            Assert(mockDb.ExecutedStatements.Contains("ROLLBACK;"), "Transaction must be rolled back on fatal failure");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    private sealed class MockSqliteImportDatabase : IDatabase
    {
        public ConnectionState State => ConnectionState.Open;
        public string ProviderName => "sqlite";
        public List<string> ExecutedStatements { get; } = new List<string>();
        public HashSet<string> ExistingTables { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool FailOnFailingSql { get; set; } = false;

        public void SetConn(string connectionString) { }
        public void Open() { }
        public void Close() { }
        public void Dispose() { }

        public DataTable SelectSQL(string sql, Dictionary<string, object> parameters = null)
        {
            return new DataTable();
        }

        public Dictionary<string, string> ExecSQL(string sql, Dictionary<string, object> parameters = null)
        {
            ExecutedStatements.Add(sql);
            if (FailOnFailingSql && sql.Contains("FAILING_SQL_SYNTAX_ERROR"))
            {
                return new Dictionary<string, string>
                {
                    { "status", "NO" },
                    { "reason", "near \"FAILING_SQL_SYNTAX_ERROR\": syntax error" }
                };
            }

            var res = new Dictionary<string, string> { { "status", "OK" } };
            if (sql.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase))
            {
                res["rowsAffected"] = "1";
            }
            return res;
        }

        public Task<DataTable> SelectSQLAsync(string sql, Dictionary<string, object> parameters = null) => Task.FromResult(SelectSQL(sql, parameters));
        public Task<Dictionary<string, string>> ExecSQLAsync(string sql, Dictionary<string, object> parameters = null) => Task.FromResult(ExecSQL(sql, parameters));

        public List<string> GetDatabases() => new List<string> { "main" };
        public List<string> GetTables(string databaseName) => new List<string>(ExistingTables);
        public List<string> GetViews(string databaseName) => new List<string>();
        public DataTable GetColumns(string databaseName, string tableName) => new DataTable();
        public DataTable GetIndexes(string databaseName, string tableName) => new DataTable();
        public DataTable GetTableStatus(string databaseName) => new DataTable();
        public Dictionary<string, string> GetDatabaseInfo(string databaseName) => new Dictionary<string, string>();
        public string GetTableCreateStatement(string databaseName, string tableName) => "";
        public bool TableExists(string databaseName, string tableName) => ExistingTables.Contains(tableName);
        public bool ViewExists(string databaseName, string viewName) => false;
        public void RenameTable(string databaseName, string oldTableName, string newTableName) { }
        public void RenameView(string databaseName, string oldViewName, string newViewName) { }
        public long CountRows(string databaseName, string tableName) => 2;
        public DataTable GetCopyColumns(string databaseName, string tableName) => new DataTable();
        public DataTable GetCopyIndexes(string databaseName, string tableName) => new DataTable();
        public void CreateTableForCopy(string databaseName, string tableName, DataTable sourceColumns, string sourceProvider) { }
        public void DropTableForCopy(string databaseName, string tableName) { }
        public void CreateIndexesForCopy(string databaseName, string tableName, DataTable sourceIndexes, string sourceProvider) { }
        public DataTable SelectTablePage(string databaseName, string tableName, long offset, int limit) => new DataTable();
        public void InsertTableBatch(string databaseName, string tableName, DataTable rows) { }
        public string GetViewCreateStatement(string databaseName, string viewName) => "";
        public void CreateViewFromStatement(string databaseName, string viewName, string sourceViewSql) { }
    }
}
