using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using mySQLPunk.lib;
using mySQLPunk.lib.PostgreSqlImport;

public static partial class SmokeTests
{
    private static void PostgreSqlImport_PreflightScanner_PostgisAndDollarQuotes()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "mysqlpunk_pg_preflight_test_" + Guid.NewGuid().ToString("N") + ".sql");
        try
        {
            string sql = @"
-- mySQLPunk PostgreSQL Logical Dump
CREATE EXTENSION IF NOT EXISTS postgis;

CREATE TABLE ""public"".""gis_points"" (
    ""id"" serial PRIMARY KEY,
    ""geom"" geometry(Point, 4326),
    ""updated_at"" timestamp
);

-- PL/pgSQL Dollar-quoted function with internal semicolons
CREATE OR REPLACE FUNCTION update_timestamp() RETURNS trigger AS $body$
BEGIN
    NEW.updated_at := CURRENT_TIMESTAMP;
    RETURN NEW;
END;
$body$ LANGUAGE plpgsql;

INSERT INTO ""public"".""gis_points"" (""geom"") VALUES
('\x0101000020E6100000000000000000F03F000000000000F03F'::geometry),
('\x0101000020E610000000000000000000400000000000000040'::geometry);

-- ====================================================================
-- mySQLPunk Native Manifest
-- Manifest-Table: gis_points | Rows: 2
-- Manifest-Checksum-SHA256: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
-- ====================================================================
";
            File.WriteAllText(tempFile, sql, System.Text.Encoding.UTF8);

            var scanner = new PostgreSqlImportPreflightScanner();
            var result = scanner.Scan(tempFile);

            Assert(result.CanProceed, "Valid PostgreSQL script should pass preflight");
            Assert(result.HasPostgisFeatures, "Should detect PostGIS keywords (geometry, postgis)");
            Assert(result.HasDollarQuotes, "Should detect Dollar-quoted PL/pgSQL function block ($body$)");
            Assert(result.DollarQuoteBlocksCount == 1, "Expected 1 dollar-quoted block, got " + result.DollarQuoteBlocksCount);

            // 驗證語句計數：CREATE EXTENSION (1) + CREATE TABLE (2) + CREATE FUNCTION (3) + INSERT (4) = 4 條語句 (函數本體內的 2 個分號不可被誤切！)
            Assert(result.StatementCount == 4, "StatementCount should be 4, semicolons inside $body$ must NOT be split. Got: " + result.StatementCount);

            Assert(result.ContainsManifest, "Should detect Native Manifest footer");
            Assert(result.ManifestDeclaredTables.ContainsKey("gis_points"), "Manifest should contain gis_points");
            Assert(result.ManifestDeclaredTables["gis_points"] == 2, "Declared rows should be 2");
            Assert(result.ManifestSha256 == "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "SHA256 should match");
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    private static void PostgreSqlImport_PreflightScanner_EmbeddedNul()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "mysqlpunk_pg_nul_test_" + Guid.NewGuid().ToString("N") + ".sql");
        try
        {
            byte[] corrupt = new byte[] { (byte)'S', (byte)'E', (byte)'L', (byte)'E', (byte)'C', (byte)'T', 0x00, (byte)';' };
            File.WriteAllBytes(tempFile, corrupt);

            var scanner = new PostgreSqlImportPreflightScanner();
            var result = scanner.Scan(tempFile);

            Assert(!result.CanProceed, "Embedded NUL byte must fail preflight");
            Assert(result.Issues.Count > 0, "Issues list should report corrupted NUL character");
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    private static void PostgreSqlImport_Runner_ExecutionAndManifestVerification()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "mysqlpunk_pg_run_test_" + Guid.NewGuid().ToString("N") + ".sql");
        try
        {
            string sql = @"
CREATE EXTENSION IF NOT EXISTS postgis;
CREATE TABLE ""parcels"" (""id"" int, ""geom"" geometry);
INSERT INTO ""parcels"" VALUES (1, '\x0101'::geometry), (2, '\x0102'::geometry);
-- mySQLPunk Native Manifest
-- Manifest-Table: parcels | Rows: 2
-- Manifest-Checksum-SHA256: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
";
            File.WriteAllText(tempFile, sql, System.Text.Encoding.UTF8);

            var mockDb = new MockPostgreSqlImportDatabase();
            var options = new PostgreSqlImportOptions
            {
                AutoInstallPostgis = true,
                UseTransaction = true,
                StopOnError = true
            };
            var request = new PostgreSqlImportRequest(tempFile, "gis_db", options);
            var runner = new PostgreSqlImportRunner(mockDb, request);

            var result = runner.Run();

            Assert(result.Success, "Runner execution should succeed");
            Assert(result.StatementsExecuted == 3, "Expected 3 statements executed, got " + result.StatementsExecuted);
            Assert(result.TotalRowsAffected == 2, "Expected 2 rows affected, got " + result.TotalRowsAffected);
            Assert(result.ManifestMatched, "Manifest verification should pass (declared 2 == actual 2)");
            Assert(File.Exists(result.LogFilePath), "Independent log file must exist: " + result.LogFilePath);

            // 驗證交易包覆有正確 COMMIT
            Assert(mockDb.ExecutedQueries.Contains("BEGIN;"), "Transaction BEGIN must be executed");
            Assert(mockDb.ExecutedQueries.Contains("COMMIT;"), "Transaction COMMIT must be executed");
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    private static void PostgreSqlImport_Runner_ExistingObjectStrategies()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "mysqlpunk_pg_strat_test_" + Guid.NewGuid().ToString("N") + ".sql");
        try
        {
            string sql = @"
CREATE TABLE ""roads"" (""id"" int);
INSERT INTO ""roads"" VALUES (1);
";
            File.WriteAllText(tempFile, sql, System.Text.Encoding.UTF8);

            // 1. KeepExisting 策略：CREATE TABLE 應轉換為 CREATE TABLE IF NOT EXISTS
            var mockDb1 = new MockPostgreSqlImportDatabase();
            var opt1 = new PostgreSqlImportOptions { ConflictStrategy = PostgreSqlConflictStrategy.KeepExisting };
            var runner1 = new PostgreSqlImportRunner(mockDb1, new PostgreSqlImportRequest(tempFile, "gis_db", opt1));
            runner1.Run();

            bool hasIfNotExists = mockDb1.ExecutedQueries.Exists(q => q.Contains("CREATE TABLE IF NOT EXISTS \"roads\""));
            Assert(hasIfNotExists, "KeepExisting should convert to CREATE TABLE IF NOT EXISTS");

            // 2. DropAndRecreate 策略：應在建表前預先執行 DROP TABLE IF EXISTS ... CASCADE
            var mockDb2 = new MockPostgreSqlImportDatabase();
            var opt2 = new PostgreSqlImportOptions { ConflictStrategy = PostgreSqlConflictStrategy.DropAndRecreate };
            var runner2 = new PostgreSqlImportRunner(mockDb2, new PostgreSqlImportRequest(tempFile, "gis_db", opt2));
            runner2.Run();

            bool hasDrop = mockDb2.ExecutedQueries.Exists(q => q.Contains("DROP TABLE IF EXISTS \"roads\" CASCADE;"));
            Assert(hasDrop, "DropAndRecreate should execute DROP TABLE IF EXISTS CASCADE");

            // 3. TruncateAndInsert 策略：應在第一次 INSERT 前執行 TRUNCATE TABLE ... CASCADE
            var mockDb3 = new MockPostgreSqlImportDatabase();
            var opt3 = new PostgreSqlImportOptions { ConflictStrategy = PostgreSqlConflictStrategy.TruncateAndInsert };
            var runner3 = new PostgreSqlImportRunner(mockDb3, new PostgreSqlImportRequest(tempFile, "gis_db", opt3));
            runner3.Run();

            bool hasTruncate = mockDb3.ExecutedQueries.Exists(q => q.Contains("TRUNCATE TABLE \"roads\" CASCADE;"));
            Assert(hasTruncate, "TruncateAndInsert should execute TRUNCATE TABLE CASCADE");
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    private static void PostgreSqlImport_Runner_TransactionRollbackOnFailure()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "mysqlpunk_pg_fail_test_" + Guid.NewGuid().ToString("N") + ".sql");
        try
        {
            string sql = @"
CREATE TABLE ""valid"" (""id"" int);
INVALID SQL SYNTAX HERE;
";
            File.WriteAllText(tempFile, sql, System.Text.Encoding.UTF8);

            var mockDb = new MockPostgreSqlImportDatabase();
            mockDb.FailOnSql = "INVALID SQL";

            var opt = new PostgreSqlImportOptions
            {
                UseTransaction = true,
                StopOnError = true
            };
            var request = new PostgreSqlImportRequest(tempFile, "gis_db", opt);
            var runner = new PostgreSqlImportRunner(mockDb, request);

            var result = runner.Run();

            Assert(!result.Success, "Runner should report failure");
            Assert(mockDb.ExecutedQueries.Contains("ROLLBACK;"), "Transaction ROLLBACK must be executed upon statement failure");
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    private sealed class MockPostgreSqlImportDatabase : IDatabase
    {
        public ConnectionState State => ConnectionState.Open;
        public string ProviderName => "postgresql";
        public List<string> ExecutedQueries { get; } = new List<string>();
        public string FailOnSql { get; set; } = null;

        public void Open() { }
        public void Close() { }
        public void Dispose() { }

        public DataTable SelectSQL(string sql, Dictionary<string, object> parameters = null)
        {
            ExecutedQueries.Add(sql);
            var dt = new DataTable();

            if (sql.Contains("SELECT COUNT(*) FROM \"parcels\""))
            {
                dt.Columns.Add("count", typeof(long));
                dt.Rows.Add(2L);
                return dt;
            }

            return dt;
        }

        public Dictionary<string, string> ExecSQL(string sql, Dictionary<string, object> parameters = null)
        {
            ExecutedQueries.Add(sql);

            if (!string.IsNullOrWhiteSpace(FailOnSql) && sql.Contains(FailOnSql))
            {
                throw new InvalidOperationException("Simulated syntax error: " + sql);
            }

            long rows = sql.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
            return new Dictionary<string, string>
            {
                { "status", "OK" },
                { "rowsAffected", rows.ToString() }
            };
        }

        public Task<DataTable> SelectSQLAsync(string sql, Dictionary<string, object> parameters = null) => Task.FromResult(SelectSQL(sql, parameters));
        public Task<Dictionary<string, string>> ExecSQLAsync(string sql, Dictionary<string, object> parameters = null) => Task.FromResult(ExecSQL(sql, parameters));
        public List<string> GetDatabases() => new List<string> { "gis_db" };
        public List<string> GetTables(string databaseName) => new List<string>();
        public List<string> GetViews(string databaseName) => new List<string>();
        public DataTable GetColumns(string databaseName, string tableName) => new DataTable();
        public DataTable GetIndexes(string databaseName, string tableName) => new DataTable();
        public DataTable GetTableStatus(string databaseName) => new DataTable();
        public Dictionary<string, string> GetDatabaseInfo(string databaseName) => new Dictionary<string, string>();
        public string GetTableCreateStatement(string databaseName, string tableName) => "";
        public bool TableExists(string databaseName, string tableName) => false;
        public bool ViewExists(string databaseName, string viewName) => false;
        public void RenameTable(string databaseName, string oldTableName, string newTableName) { }
        public void RenameView(string databaseName, string oldViewName, string newViewName) { }
        public long CountRows(string databaseName, string tableName) => 0;
        public DataTable GetCopyColumns(string databaseName, string tableName) => new DataTable();
        public DataTable GetCopyIndexes(string databaseName, string tableName) => new DataTable();
        public void SetConn(string connection) { }
        public void CreateTableForCopy(string databaseName, string tableName, DataTable sourceColumns, string sourceProvider) { }
        public void DropTableForCopy(string databaseName, string tableName) { }
        public void CreateIndexesForCopy(string databaseName, string tableName, DataTable sourceIndexes, string sourceProvider) { }
        public DataTable SelectTablePage(string databaseName, string tableName, long offset, int limit) => new DataTable();
        public void InsertTableBatch(string databaseName, string tableName, DataTable rows) { }
        public string GetViewCreateStatement(string databaseName, string viewName) => "";
        public void CreateViewFromStatement(string databaseName, string viewName, string sourceViewSql) { }
    }
}
