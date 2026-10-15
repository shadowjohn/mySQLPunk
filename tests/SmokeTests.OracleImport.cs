using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using mySQLPunk.lib;
using mySQLPunk.lib.OracleImport;

public static partial class SmokeTests
{
    private static void OracleImport_PreflightScanner_SdoGeometryAndPlSql()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "mysqlpunk_ora_preflight_test_" + Guid.NewGuid().ToString("N") + ".sql");
        try
        {
            string sql = @"
-- mySQLPunk Oracle Logical Dump
CREATE TABLE ""SCOTT"".""PARCELS"" (
    ""ID"" NUMBER PRIMARY KEY,
    ""NAME"" VARCHAR2(100),
    ""SHAPE"" MDSYS.SDO_GEOMETRY
);

-- PL/SQL Trigger with internal semicolons, terminated with a standalone slash /
CREATE OR REPLACE TRIGGER ""SCOTT"".""TRG_PARCELS_BI""
BEFORE INSERT ON ""SCOTT"".""PARCELS""
FOR EACH ROW
BEGIN
    IF :NEW.""ID"" IS NULL THEN
        SELECT SEQ_PARCELS.NEXTVAL INTO :NEW.""ID"" FROM DUAL;
    END IF;
END;
/

INSERT INTO ""SCOTT"".""PARCELS"" (""ID"", ""NAME"", ""SHAPE"") VALUES (1, 'Park', SDO_UTIL.FROM_WKBGEOMETRY(HEXTORAW('0101000000000000000000F03F000000000000F03F')));

-- ====================================================================
-- mySQLPunk Native Manifest
-- Manifest-Table: SCOTT.PARCELS | Rows: 1
-- Manifest-Checksum-SHA256: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
-- ====================================================================
";
            File.WriteAllText(tempFile, sql, System.Text.Encoding.UTF8);

            var scanner = new OracleImportPreflightScanner();
            var result = scanner.Scan(tempFile);

            Assert(result.CanProceed, "Valid Oracle script should pass preflight");
            Assert(result.HasSpatialFeatures, "Should detect Oracle Spatial keywords (sdo_geometry, sdo_util)");
            Assert(result.HasPlSqlBlocks, "Should detect PL/SQL trigger block");
            Assert(result.PlSqlBlocksCount == 1, "Expected 1 PL/SQL block, got " + result.PlSqlBlocksCount);

            // 語句計數：CREATE TABLE (1) + TRIGGER (2) + INSERT (3) = 3 條語句 (TRIGGER 內部的分號不可被誤切！)
            Assert(result.StatementCount == 3, "StatementCount should be 3, semicolons inside PL/SQL block must NOT be split. Got: " + result.StatementCount);

            Assert(result.ContainsManifest, "Should detect Native Manifest footer");
            Assert(result.ManifestDeclaredTables.ContainsKey("SCOTT.PARCELS"), "Manifest should contain SCOTT.PARCELS");
            Assert(result.ManifestDeclaredTables["SCOTT.PARCELS"] == 1, "Declared rows should be 1");
            Assert(result.ManifestSha256 == "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "SHA256 should match");
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    private static void OracleImport_PreflightScanner_EmbeddedNul()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "mysqlpunk_ora_nul_test_" + Guid.NewGuid().ToString("N") + ".sql");
        try
        {
            byte[] corrupt = new byte[] { (byte)'S', (byte)'E', (byte)'L', (byte)'E', (byte)'C', (byte)'T', 0x00, (byte)';' };
            File.WriteAllBytes(tempFile, corrupt);

            var scanner = new OracleImportPreflightScanner();
            var result = scanner.Scan(tempFile);

            Assert(!result.CanProceed, "Embedded NUL byte must fail preflight");
            Assert(result.Issues.Count > 0, "Issues list should report corrupted NUL character");
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    private static void OracleImport_Runner_ExecutionAndManifestVerification()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "mysqlpunk_ora_run_test_" + Guid.NewGuid().ToString("N") + ".sql");
        try
        {
            string sql = @"
CREATE TABLE ""SCOTT"".""PARCELS"" (""ID"" NUMBER, ""SHAPE"" MDSYS.SDO_GEOMETRY);
INSERT INTO ""SCOTT"".""PARCELS"" VALUES (1, SDO_UTIL.FROM_WKBGEOMETRY(HEXTORAW('0101')));
-- mySQLPunk Native Manifest
-- Manifest-Table: SCOTT.PARCELS | Rows: 1
-- Manifest-Checksum-SHA256: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
";
            File.WriteAllText(tempFile, sql, System.Text.Encoding.UTF8);

            var mockDb = new MockOracleImportDatabase();
            var options = new OracleImportOptions
            {
                UseTransaction = true,
                StopOnError = true
            };
            var request = new OracleImportRequest(tempFile, "SCOTT", options);
            var runner = new OracleImportRunner(mockDb, request);

            var result = runner.Run();

            Assert(result.Success, "Runner execution should succeed");
            Assert(result.StatementsExecuted == 2, "Expected 2 statements executed, got " + result.StatementsExecuted);
            Assert(result.TotalRowsAffected == 1, "Expected 1 row affected, got " + result.TotalRowsAffected);
            Assert(result.ManifestMatched, "Manifest verification should pass (declared 1 == actual 1)");
            Assert(File.Exists(result.LogFilePath), "Independent log file must exist: " + result.LogFilePath);

            // 驗證交易包覆有正確 COMMIT
            Assert(mockDb.ExecutedQueries.Contains("COMMIT;"), "Transaction COMMIT must be executed");
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    private static void OracleImport_Runner_ExistingObjectStrategies()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "mysqlpunk_ora_strat_test_" + Guid.NewGuid().ToString("N") + ".sql");
        try
        {
            string sql = @"
CREATE TABLE ""SCOTT"".""ROADS"" (""ID"" NUMBER);
INSERT INTO ""SCOTT"".""ROADS"" VALUES (1);
";
            File.WriteAllText(tempFile, sql, System.Text.Encoding.UTF8);

            // 1. KeepExisting 策略：遇 ORA-00955 (物件已存在) 時跳過且不中斷
            var mockDb1 = new MockOracleImportDatabase();
            mockDb1.SimulateExistingObjectError = true;
            var opt1 = new OracleImportOptions { ConflictStrategy = OracleConflictStrategy.KeepExisting, StopOnError = true };
            var runner1 = new OracleImportRunner(mockDb1, new OracleImportRequest(tempFile, "SCOTT", opt1));
            var res1 = runner1.Run();
            Assert(res1.Success, "KeepExisting should treat ORA-00955 as skipped and succeed");

            // 2. DropAndRecreate 策略：應在建表前預先執行 DROP TABLE ... CASCADE CONSTRAINTS
            var mockDb2 = new MockOracleImportDatabase();
            var opt2 = new OracleImportOptions { ConflictStrategy = OracleConflictStrategy.DropAndRecreate };
            var runner2 = new OracleImportRunner(mockDb2, new OracleImportRequest(tempFile, "SCOTT", opt2));
            runner2.Run();

            bool hasDrop = mockDb2.ExecutedQueries.Exists(q => q.Contains("DROP TABLE \"SCOTT\".\"ROADS\" CASCADE CONSTRAINTS"));
            Assert(hasDrop, "DropAndRecreate should execute DROP TABLE CASCADE CONSTRAINTS");

            // 3. TruncateAndInsert 策略：應在第一次 INSERT 前執行 TRUNCATE TABLE
            var mockDb3 = new MockOracleImportDatabase();
            var opt3 = new OracleImportOptions { ConflictStrategy = OracleConflictStrategy.TruncateAndInsert };
            var runner3 = new OracleImportRunner(mockDb3, new OracleImportRequest(tempFile, "SCOTT", opt3));
            runner3.Run();

            bool hasTruncate = mockDb3.ExecutedQueries.Exists(q => q.Contains("TRUNCATE TABLE \"SCOTT\".\"ROADS\";"));
            Assert(hasTruncate, "TruncateAndInsert should execute TRUNCATE TABLE");
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    private static void OracleImport_Runner_TransactionRollbackOnFailure()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "mysqlpunk_ora_fail_test_" + Guid.NewGuid().ToString("N") + ".sql");
        try
        {
            string sql = @"
CREATE TABLE ""SCOTT"".""VALID"" (""ID"" NUMBER);
INVALID SQL SYNTAX HERE;
";
            File.WriteAllText(tempFile, sql, System.Text.Encoding.UTF8);

            var mockDb = new MockOracleImportDatabase();
            mockDb.FailOnSql = "INVALID SQL";

            var opt = new OracleImportOptions
            {
                UseTransaction = true,
                StopOnError = true
            };
            var request = new OracleImportRequest(tempFile, "SCOTT", opt);
            var runner = new OracleImportRunner(mockDb, request);

            var result = runner.Run();

            Assert(!result.Success, "Runner should report failure");
            Assert(mockDb.ExecutedQueries.Contains("ROLLBACK;"), "Transaction ROLLBACK must be executed upon statement failure");
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    private sealed class MockOracleImportDatabase : IDatabase
    {
        public ConnectionState State => ConnectionState.Open;
        public string ProviderName => "oracle";
        public List<string> ExecutedQueries { get; } = new List<string>();
        public string FailOnSql { get; set; } = null;
        public bool SimulateExistingObjectError { get; set; } = false;

        public void Open() { }
        public void Close() { }
        public void Dispose() { }

        public DataTable SelectSQL(string sql, Dictionary<string, object> parameters = null)
        {
            ExecutedQueries.Add(sql);
            var dt = new DataTable();

            if (sql.Contains("SELECT COUNT(*) FROM \"SCOTT\".\"PARCELS\""))
            {
                dt.Columns.Add("COUNT", typeof(long));
                dt.Rows.Add(1L);
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

            if (SimulateExistingObjectError && sql.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("ORA-00955: name is already used by an existing object");
            }

            long rows = sql.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            return new Dictionary<string, string>
            {
                { "status", "OK" },
                { "rowsAffected", rows.ToString() }
            };
        }

        public Task<DataTable> SelectSQLAsync(string sql, Dictionary<string, object> parameters = null) => Task.FromResult(SelectSQL(sql, parameters));
        public Task<Dictionary<string, string>> ExecSQLAsync(string sql, Dictionary<string, object> parameters = null) => Task.FromResult(ExecSQL(sql, parameters));
        public List<string> GetDatabases() => new List<string> { "SCOTT" };
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
