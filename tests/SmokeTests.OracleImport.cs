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
            Assert(mockDb.ExecutedQueries.Contains("COMMIT"), "Transaction COMMIT must be executed");
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

            bool hasTruncate = mockDb3.ExecutedQueries.Exists(q => q.Contains("TRUNCATE TABLE \"SCOTT\".\"ROADS\""));
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
INSERT INTO ""SCOTT"".""VALID"" VALUES (1);
";
            File.WriteAllText(tempFile, sql, System.Text.Encoding.UTF8);

            var mockDb = new MockOracleImportDatabase();
            mockDb.FailOnSql = "INSERT INTO";

            var opt = new OracleImportOptions
            {
                UseTransaction = true,
                StopOnError = true
            };
            var request = new OracleImportRequest(tempFile, "SCOTT", opt);
            var runner = new OracleImportRunner(mockDb, request);

            var result = runner.Run();

            Assert(!result.Success, "Runner should report failure");
            Assert(mockDb.ExecutedQueries.Contains("ROLLBACK"), "Transaction ROLLBACK must be executed upon statement failure");
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    private static void OracleImport_ReturnedNoAndTransactionControls()
    {
        string path = Path.Combine(Path.GetTempPath(), "ora_no_" + Guid.NewGuid().ToString("N") + ".sql");
        try
        {
            foreach (string failure in new[] { "INSERT", "COMMIT", "TRUNCATE", "DROP TABLE" })
            {
                File.WriteAllText(path, failure == "DROP TABLE" ? "CREATE TABLE T (ID NUMBER);" : "INSERT INTO T VALUES (1);");
                var db = new MockOracleImportDatabase { FailOnSql = failure };
                var options = new OracleImportOptions { ConflictStrategy = failure == "TRUNCATE" ? OracleConflictStrategy.TruncateAndInsert : failure == "DROP TABLE" ? OracleConflictStrategy.DropAndRecreate : OracleConflictStrategy.KeepExisting };
                var result = new OracleImportRunner(db, new OracleImportRequest(path, "SCOTT", options)).Run();
                Assert(!result.Success && result.StatementsFailed > 0, "Returned NO must fail " + failure);
                Assert(result.ErrorMessage.Contains("Simulated"), "Provider failure reason retained: " + failure);
                Assert(db.ExecutedQueries.Contains("SET TRANSACTION READ WRITE"), "Transaction option requests a transaction");
                Assert(db.ExecutedQueries.Contains("ROLLBACK"), "Failed pending transaction rolls back");
            }
            File.WriteAllText(path, "INSERT INTO T VALUES (1);");
            var noTransaction = new MockOracleImportDatabase();
            Assert(new OracleImportRunner(noTransaction, new OracleImportRequest(path, "SCOTT", new OracleImportOptions { UseTransaction = false })).Run().Success, "Nontransaction import succeeds");
            Assert(!noTransaction.ExecutedQueries.Contains("SET TRANSACTION READ WRITE") && !noTransaction.ExecutedQueries.Contains("COMMIT"), "Unchecked transaction option does not force a transaction");
            File.WriteAllText(path, "SET TRANSACTION READ WRITE; INSERT INTO T VALUES (1);");
            using (var scriptCancel = new CancellationTokenSource())
            {
                var scriptOwned = new MockOracleImportDatabase();
                scriptOwned.AfterExecute = sql => { if (sql.StartsWith("INSERT")) scriptCancel.Cancel(); };
                var scriptResult = new OracleImportRunner(scriptOwned, new OracleImportRequest(path, "SCOTT", new OracleImportOptions { UseTransaction = false })).Run(null, scriptCancel.Token);
                Assert(!scriptResult.Success && scriptOwned.ExecutedQueries.Contains("ROLLBACK"), "Script-owned transaction is cleaned after cancellation with option unchecked");
            }
            File.WriteAllText(path, "INSERT INTO T VALUES (1);");
            var rollbackNo = new MockOracleImportDatabase { FailOnSql = "ROLLBACK" };
            using (var cancel = new CancellationTokenSource())
            {
                rollbackNo.AfterExecute = sql => { if (sql.StartsWith("INSERT")) cancel.Cancel(); };
                var result = new OracleImportRunner(rollbackNo, new OracleImportRequest(path, "SCOTT")).Run(null, cancel.Token);
                Assert(!result.Success && result.ErrorMessage.Contains("rollback failed"), "Cancellation rollback NO is retained");
            }
        }
        finally { File.Delete(path); }
    }

    private static void OracleImport_TargetOwnerPreflightAndCancellation()
    {
        string path = Path.Combine(Path.GetTempPath(), "ora_owner_" + Guid.NewGuid().ToString("N") + ".sql");
        try
        {
            foreach (string sql in new[] { "INSERT INTO A.T VALUES (1);", "CREATE INDEX B.I ON A.T(ID);", "CREATE TABLE B.T(ID NUMBER REFERENCES A.T(ID));", "COMMENT ON COLUMN A.T.ID IS 'x';", "ALTER SESSION SET CURRENT_SCHEMA=A;", "BEGIN A.P(); END;\n/" })
            {
                File.WriteAllText(path, sql);
                var db = new MockOracleImportDatabase();
                var result = new OracleImportRunner(db, new OracleImportRequest(path, "B")).Run();
                Assert(!result.Success && db.ExecutedQueries.Count == 0, "Cross-owner/ambiguous SQL rejected before execution: " + sql);
            }
            File.WriteAllText(path, "DELETE FROM USER_SDO_GEOM_METADATA WHERE TABLE_NAME='T'; INSERT INTO T VALUES (1);");
            var noMetadata = new MockOracleImportDatabase();
            var noMetadataResult = new OracleImportRunner(noMetadata, new OracleImportRequest(path, "B", new OracleImportOptions { AutoRegisterSpatialMetadata = false })).Run();
            Assert(noMetadataResult.Success && !noMetadata.ExecutedQueries.Exists(command => command.StartsWith("DELETE FROM USER_SDO_GEOM_METADATA")), "Unchecked spatial registration skips script metadata mutations");
            File.WriteAllText(path, "-- A.T stays comment\nINSERT INTO T VALUES ('A.T; literal');\nINSERT INTO T VALUES (2);");
            using (var cancel = new CancellationTokenSource())
            {
                var db = new MockOracleImportDatabase();
                db.AfterExecute = sql => { if (sql.StartsWith("INSERT")) cancel.Cancel(); };
                var result = new OracleImportRunner(db, new OracleImportRequest(path, "B")).Run(null, cancel.Token);
                Assert(!result.Success, "Cancelled import fails");
                Assert(db.ExecutedQueries[0] == "ALTER SESSION SET CURRENT_SCHEMA = \"B\"", "Chosen owner pinned before writes");
                Assert(db.ExecutedQueries.Exists(sql => sql.Contains("'A.T; literal'")), "Owner-like literal remains unchanged");
                Assert(db.ExecutedQueries.Contains("ROLLBACK") && !db.ExecutedQueries.Contains("COMMIT"), "Cancellation cleans pending transaction without commit");
            }
        }
        finally { File.Delete(path); }
    }

    private static void OracleImport_ParserBytesCommentsAndAlternativeQuotes()
    {
        string sql = "/*x*/ SELECT '中文' FROM dual;\r\nINSERT INTO T VALUES (q'[I'm;here]');\nBEGIN\nNULL;\nEND;\n/\n";
        var parse = typeof(OracleImportRunner).GetMethod("EnumerateStatements", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        using (var reader = new StreamReader(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(sql)), System.Text.Encoding.UTF8))
        {
            var statements = (System.Collections.IEnumerable)parse.Invoke(null, new object[] { reader, CancellationToken.None, null });
            int count = 0; long bytes = 0;
            foreach (object item in statements)
            {
                string statement = (string)item.GetType().GetField("Sql").GetValue(item);
                int length = (int)item.GetType().GetField("ByteLength").GetValue(item);
                int line = (int)item.GetType().GetField("LineNumber").GetValue(item);
                Assert(length > 0, "Terminated statement has nonzero byte count"); bytes += length;
                if (count == 0) Assert(statement == "SELECT '中文' FROM dual" && line == 1, "Comment discarded and SQL terminator removed");
                if (count == 1) Assert(statement.Contains("q'[I'm;here]'") && line == 2, "Alternative quoted semicolon stays in one statement");
                if (count == 2) Assert(statement == "BEGIN\nNULL;\nEND;" && line == 3, "PL/SQL keeps terminal semicolon and original start line");
                count++;
            }
            Assert(count == 3 && bytes == System.Text.Encoding.UTF8.GetByteCount(sql), "Actual UTF-8/CRLF source bytes accounted");
        }
    }

    private static void OracleImport_ScopedTargetVariantsMetadataAndLobTruncate()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ora_scoped_targets_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "import.sql");
        try
        {
            foreach (string sql in new[] { "DELETE A.T WHERE ID=1;", "CREATE UNIQUE INDEX A.I ON B.T(ID);", "CREATE BITMAP INDEX A.I ON B.T(ID);", "INSERT INTO B.T@REMOTE VALUES (1);", "DELETE B.T@REMOTE WHERE ID=1;", "DELETE FROM ONLY(A.V) WHERE ID=1;", "UPDATE ONLY(A.V) SET ID=1;" })
            {
                File.WriteAllText(path, sql); var database = new MockOracleImportDatabase();
                var result = new OracleImportRunner(database, new OracleImportRequest(path, "B")).Run();
                Assert(!result.Success && database.ExecutedQueries.Count == 0, "Direct cross-owner/database-link variant fails before session/mutations: " + sql);
            }
            File.WriteAllText(path, "/*A.T @REMOTE*/ DELETE B.T WHERE NAME='A.T @REMOTE'; CREATE UNIQUE INDEX B.I ON B.T(ID); CREATE BITMAP INDEX B.J ON B.T(ID);");
            var sameOwner = new MockOracleImportDatabase();
            Assert(new OracleImportRunner(sameOwner, new OracleImportRequest(path, "B")).Run().Success, "Same-owner optional FROM and modified indexes accepted");
            Assert(sameOwner.ExecutedQueries.Exists(sql => sql.Contains("'A.T @REMOTE'")), "Literal/comment owner and at-sign text does not trigger remapping/rejection");
            foreach (string sql in new[] { "INSERT INTO \"USER_SDO_GEOM_METADATA\" (TABLE_NAME) VALUES ('T');", "DELETE \"USER_SDO_GEOM_METADATA\" WHERE TABLE_NAME='T';", "DELETE USER_SDO_GEOM_METADATA WHERE TABLE_NAME='T';", "UPDATE \"USER_SDO_GEOM_METADATA\" SET SRID=NULL;" })
            {
                File.WriteAllText(path, sql);
                var wrongUser = new MockOracleImportDatabase { SessionUser = "SCOTT" };
                Assert(!new OracleImportRunner(wrongUser, new OracleImportRequest(path, "B")).Run().Success, "Every metadata spelling enforces SESSION_USER");
                Assert(wrongUser.ExecutedQueries.Count == 1 && wrongUser.ExecutedQueries[0].Contains("SESSION_USER"), "Metadata mismatch fails before ALTER SESSION/mutation");
                var disabled = new MockOracleImportDatabase { SessionUser = "SCOTT" };
                var options = new OracleImportOptions { AutoRegisterSpatialMetadata = false };
                Assert(new OracleImportRunner(disabled, new OracleImportRequest(path, "B", options)).Run().Success, "Every metadata spelling is skipped when registration disabled");
                Assert(!disabled.ExecutedQueries.Exists(command => command.Contains("USER_SDO_GEOM_METADATA") || command.Contains("SESSION_USER")), "Disabled metadata spelling executes no registration or USER gate query");
                var ownerUser = new MockOracleImportDatabase { SessionUser = "B" };
                Assert(new OracleImportRunner(ownerUser, new OracleImportRequest(path, "B")).Run().Success, "Matching user can execute quoted/optional FROM metadata target");
            }
            foreach (bool allLarge in new[] { false, true })
            {
                var source = new MockOracleExportDatabase { TextValue = new string('中', 5000), GeometryBytes = 3000, AllLargeLobRows = allLarge };
                var export = new mySQLPunk.lib.OracleExport.OracleExportWizardOptions(mySQLPunk.lib.OracleExport.OracleExportMode.DataOnly, path) { IncludeManifest = false };
                export.SelectedTables.Add("PARCELS");
                new mySQLPunk.lib.OracleExport.OracleSqlExportService(source, "SCOTT").ExportAsync(export).GetAwaiter().GetResult();
                if (!allLarge)
                {
                    string dump = File.ReadAllText(path);
                    int small = dump.LastIndexOf("INSERT INTO \"SCOTT\".\"PARCELS\"", StringComparison.Ordinal);
                    dump = dump.Substring(0, small) + dump.Substring(small).Replace("INSERT INTO \"SCOTT\".\"PARCELS\"", "INSERT INTO PARCELS");
                    File.WriteAllText(path, dump);
                }
                var destination = new MockOracleImportDatabase { TrackRows = true, StoredRows = 9 };
                var restore = new OracleImportRunner(destination, new OracleImportRequest(path, "SCOTT", new OracleImportOptions { ConflictStrategy = OracleConflictStrategy.TruncateAndInsert })).Run();
                Assert(restore.Success && destination.StoredRows == 2, "No-manifest all-large/mixed dump keeps both restored rows");
                int firstTruncate = destination.ExecutedQueries.FindIndex(command => command.StartsWith("TRUNCATE TABLE"));
                int firstLob = destination.ExecutedQueries.FindIndex(command => command.StartsWith("DECLARE"));
                Assert(firstTruncate >= 0 && firstTruncate < firstLob && destination.ExecutedQueries.FindAll(command => command.StartsWith("TRUNCATE TABLE")).Count == 1, "Normalized truncate runs exactly once before first LOB row");
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class MockOracleImportDatabase : IDatabase
    {
        public ConnectionState State => ConnectionState.Open;
        public string ProviderName => "oracle";
        public List<string> ExecutedQueries { get; } = new List<string>();
        public string FailOnSql { get; set; } = null;
        public bool SimulateExistingObjectError { get; set; } = false;
        public Action<string> AfterExecute { get; set; }
        public long ActualRows = 1;
        public string SessionUser = "SCOTT";
        public bool TrackRows;
        public long StoredRows;

        public void Open() { }
        public void Close() { }
        public void Dispose() { }

        public DataTable SelectSQL(string sql, Dictionary<string, object> parameters = null)
        {
            ExecutedQueries.Add(sql);
            var dt = new DataTable();
            if (sql.Contains("SESSION_USER")) { dt.Columns.Add("OWNER"); dt.Rows.Add(SessionUser); return dt; }

            if (sql.Contains("SELECT COUNT(*) FROM \"SCOTT\".\"PARCELS\""))
            {
                dt.Columns.Add("COUNT", typeof(long));
                dt.Rows.Add(ActualRows);
                return dt;
            }

            return dt;
        }

        public Dictionary<string, string> ExecSQL(string sql, Dictionary<string, object> parameters = null)
        {
            ExecutedQueries.Add(sql);

            if (!string.IsNullOrWhiteSpace(FailOnSql) && sql.Contains(FailOnSql))
            {
                return new Dictionary<string, string> { { "status", "NO" }, { "reason", "Simulated syntax error: " + sql } };
            }

            if (SimulateExistingObjectError && sql.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase))
            {
                return new Dictionary<string, string> { { "status", "NO" }, { "reason", "ORA-00955: name is already used by an existing object" } };
            }

            if (TrackRows)
            {
                if (sql.StartsWith("TRUNCATE TABLE")) StoredRows = 0;
                if (sql.StartsWith("INSERT") || sql.StartsWith("DECLARE") && sql.Contains("INSERT INTO")) StoredRows++;
            }
            AfterExecute?.Invoke(sql);
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
