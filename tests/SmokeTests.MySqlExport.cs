using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using mySQLPunk.lib;
using mySQLPunk.lib.MySqlExport;

public static partial class SmokeTests
{
    private static void MySqlExport_OptionsAndModels_Defaults()
    {
        var options = new MySqlExportWizardOptions(MySqlExportMode.SchemaAndData, @"C:\dump.sql");
        Assert(options.Mode == MySqlExportMode.SchemaAndData, "Default mode should be SchemaAndData");
        Assert(options.SingleTransaction, "SingleTransaction should be true by default");
        Assert(options.DisableForeignKeyChecks, "DisableForeignKeyChecks should be true by default");
        Assert(options.DisableUniqueChecks, "DisableUniqueChecks should be true by default");
        Assert(options.SetNoAutoValueOnZero, "SetNoAutoValueOnZero should be true by default");
        Assert(options.IncludeDropStatements, "IncludeDropStatements should be true by default");
        Assert(options.RemoveDefiner, "RemoveDefiner should be true by default");
        Assert(options.IncludeViews, "IncludeViews should be true by default");
        Assert(options.IncludeRoutines, "IncludeRoutines should be true by default");
        Assert(options.IncludeTriggers, "IncludeTriggers should be true by default");
        Assert(options.InsertBatchSize == 1000, "Default batch size should be 1000");

        var item = new MySqlTableExportItem("users", 1500, true);
        Assert(item.Name == "users", "TableName match");
        Assert(item.EstimatedRows == 1500, "EstimatedRows match");
        Assert(item.IsSelected, "IsSelected match");
    }

    private static void MySqlExport_ServiceAndManifest_AtomicWrite()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "mysqlpunk_export_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string targetFile = Path.Combine(tempDir, "export_test.sql");

        try
        {
            var mockDb = new MockMySqlExportDatabase();
            var service = new MySqlSqlExportService(mockDb, "test_db");

            var options = new MySqlExportWizardOptions(MySqlExportMode.SchemaAndData, targetFile)
            {
                SingleTransaction = false
            };
            options.SelectedTables.Add("users");
            options.SelectedViews.Add("active_users");
            options.SelectedRoutines.Add("sp_calc");
            options.SelectedTriggers.Add("trg_users_audit");

            MySqlExportProgress lastProgress = null;
            var progress = new Progress<MySqlExportProgress>(p => lastProgress = p);

            var task = service.ExportAsync(options, progress, CancellationToken.None);
            task.Wait();
            var result = task.Result;

            Assert(result.Success, "Export should succeed");
            Assert(File.Exists(targetFile), "Target file must exist after atomic move");
            Assert(result.TableCount == 1, "Exported table count should be 1");
            Assert(result.ViewCount == 1, "Exported view count should be 1");
            Assert(result.RoutineCount == 1, "Exported routine count should be 1");
            Assert(result.TriggerCount == 1, "Exported trigger count should be 1");
            Assert(result.TotalRows == 2, "Exported row count should be 2");

            string content = File.ReadAllText(targetFile);
            AssertContains(content, "-- mySQLPunk MySQL Logical Dump", "Header comment present");
            AssertContains(content, "SET FOREIGN_KEY_CHECKS = 0;", "Disable FK checks statement present");
            AssertContains(content, "SET UNIQUE_CHECKS = 0;", "Disable unique checks statement present");
            AssertContains(content, "SET SQL_MODE = 'NO_AUTO_VALUE_ON_ZERO';", "Set NO_AUTO_VALUE_ON_ZERO statement present");
            AssertContains(content, "SET UNIQUE_CHECKS = 1;", "Restore unique checks statement present");
            AssertContains(content, "USE `test_db`;", "USE database statement present");
            AssertContains(content, "CREATE TABLE `users`", "Table DDL present");
            AssertContains(content, "INSERT INTO `users`", "Table data INSERT present");
            AssertContains(content, "-- Manifest-Table: users | Rows: 2", "Native manifest table entry present");
            AssertContains(content, "-- Tables: 1", "Manifest total tables count present");
            AssertContains(content, "-- TotalRows: 2", "Manifest total rows count present");
            AssertContains(content, "-- Manifest-SHA256:", "Manifest SHA-256 marker present");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    private static void MySqlExport_Regression_GeneratedBoundsAndFailures()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mysqlpunk_export_repair_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "dump.sql");
            var options = new MySqlExportWizardOptions(MySqlExportMode.SchemaAndData, path) { SelectedTables = new List<string> { "users" } };
            var db = new MockMySqlExportDatabase { Generated = true };
            var result = new MySqlSqlExportService(db, "test_db").ExportAsync(options, null, CancellationToken.None).Result;
            Assert(result.Success, "Generated column export succeeds by omitting writable generated value: " + result.ErrorMessage);
            string dump = File.ReadAllText(path);
            AssertContains(dump, "GENERATED ALWAYS", "Generated DDL retained");
            AssertContains(dump, "INSERT INTO `users` (`id`, `name`)", "Generated column absent from explicit INSERT column list");
            Assert(!dump.Contains("`id`, `name`, `computed`"), "Computed values are not inserted");
            Assert(db.ExecutedSql.Contains("ROLLBACK;") && !db.ExecutedSql.Contains("COMMIT;"), "Read snapshot ends with validated rollback");
            Assert(result.FileBytes == new FileInfo(path).Length, "Reported final bytes include checksum footer");
            Assert(mySQLPunk.lib.MySqlImport.MySqlImportPreflightScanner.Scan(path, CancellationToken.None).Passed, "Native generated dump passes its own parser/digest preflight");
            File.AppendAllText(path, "-- tampered");
            Assert(!mySQLPunk.lib.MySqlImport.MySqlImportPreflightScanner.Scan(path, CancellationToken.None).Passed, "Tampered native dump rejected");

            foreach (string failure in new[] { "snapshot", "rollback", "engine", "large" })
            {
                File.WriteAllText(path, "existing complete backup");
                db = new MockMySqlExportDatabase { Failure = failure, MaxRowBytes = failure == "large" ? 1048577 : 32 };
                result = new MySqlSqlExportService(db, "test_db").ExportAsync(options, null, CancellationToken.None).Result;
                Assert(!result.Success && result.Stage == MySqlExportStage.Failed, failure + " fails explicitly");
                AssertEquals("existing complete backup", File.ReadAllText(path), failure + " preserves existing backup");
                if (failure == "large") Assert(db.PageReads == 0, "Oversized row rejected before materializing source page");
            }
            db = new MockMySqlExportDatabase();
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                result = new MySqlSqlExportService(db, "test_db").ExportAsync(options, null, CancellationToken.None).Result;
                Assert(result.Stage == MySqlExportStage.Failed, "Replacement lock failure is reported");
            }
            AssertEquals("existing complete backup", File.ReadAllText(path), "Replacement failure preserves previous complete backup");
            Assert(Directory.GetFiles(directory, "dump.sql.staging.*.tmp").Length == 1, "Completed staging survives publication failure");
            foreach (string retained in Directory.GetFiles(directory, "dump.sql.staging.*.tmp"))
                Assert(mySQLPunk.lib.MySqlImport.MySqlImportPreflightScanner.Scan(retained, CancellationToken.None).Passed, "Retained staging is a complete verified dump");
            db = new MockMySqlExportDatabase { Failure = "page-race" };
            result = new MySqlSqlExportService(db, "test_db").ExportAsync(options, null, CancellationToken.None).Result;
            Assert(!result.Success, "Concurrent enlargement marker fails explicitly");
            AssertEquals("existing complete backup", File.ReadAllText(path), "Concurrent enlargement leaves prior backup intact");
            using (var cancel = new CancellationTokenSource())
            {
                db = new MockMySqlExportDatabase { OnPageRead = () => cancel.Cancel() };
                result = new MySqlSqlExportService(db, "test_db").ExportAsync(options, null, cancel.Token).Result;
                Assert(result.Stage == MySqlExportStage.Cancelled, "Cancellation at last source read cannot publish");
                AssertEquals("existing complete backup", File.ReadAllText(path), "Cancelled export preserves prior backup");
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class MockMySqlExportDatabase : IDatabase
    {
        public ConnectionState State => ConnectionState.Open;
        public string ProviderName => "MySQL";
        public bool Generated;
        public string Failure;
        public long MaxRowBytes = 32;
        public int PageReads;
        public Action OnPageRead;
        public readonly List<string> ExecutedSql = new List<string>();

        public void SetConn(string connectionString) { }
        public void Open() { }
        public void Close() { }
        public void Dispose() { }

        public DataTable SelectSQL(string sql, Dictionary<string, object> parameters = null)
        {
            var dt = new DataTable();
            if (sql.Contains("AS MaxRowBytes")) { dt.Columns.Add("MaxRowBytes", typeof(long)); dt.Rows.Add(MaxRowBytes); return dt; }
            if (sql.Contains("AS __mysqlpunk_row_bytes"))
            {
                bool first = sql.Contains("LIMIT 0,");
                dt = SelectTablePage("test_db", "users", first ? 0 : 16, 16);
                dt.Columns.Add("__mysqlpunk_row_bytes", typeof(long));
                foreach (DataRow row in dt.Rows) row["__mysqlpunk_row_bytes"] = Failure == "page-race" ? 1048577L : MaxRowBytes;
                PageReads++;
                OnPageRead?.Invoke();
                return dt;
            }
            if (sql.Contains("ROUTINES"))
            {
                dt.Columns.Add("ROUTINE_NAME");
                dt.Columns.Add("ROUTINE_TYPE");
                dt.Rows.Add("sp_calc", "PROCEDURE");
                return dt;
            }
            if (sql.Contains("TRIGGERS") && sql.Contains("information_schema"))
            {
                dt.Columns.Add("TRIGGER_NAME");
                dt.Rows.Add("trg_users_audit");
                return dt;
            }
            if (sql.Contains("SHOW CREATE VIEW"))
            {
                dt.Columns.Add("View");
                dt.Columns.Add("Create View");
                dt.Rows.Add("active_users", "CREATE VIEW `active_users` AS SELECT * FROM `users` WHERE active = 1");
                return dt;
            }
            if (sql.Contains("SHOW CREATE PROCEDURE"))
            {
                dt.Columns.Add("Procedure");
                dt.Columns.Add("Create Procedure");
                dt.Rows.Add("sp_calc", "CREATE PROCEDURE `sp_calc`() BEGIN SELECT 1; END");
                return dt;
            }
            if (sql.Contains("SHOW CREATE TRIGGER"))
            {
                dt.Columns.Add("Trigger");
                dt.Columns.Add("SQL Original Statement");
                dt.Rows.Add("trg_users_audit", "CREATE TRIGGER `trg_users_audit` AFTER INSERT ON `users` FOR EACH ROW BEGIN END");
                return dt;
            }
            return dt;
        }

        public Dictionary<string, string> ExecSQL(string sql, Dictionary<string, object> parameters = null)
        {
            ExecutedSql.Add(sql);
            bool fail = Failure == "snapshot" && sql.StartsWith("START TRANSACTION") || Failure == "rollback" && sql.StartsWith("ROLLBACK");
            return fail ? new Dictionary<string, string> { { "status", "NO" }, { "reason", "injected " + Failure } } : new Dictionary<string, string> { { "status", "OK" } };
        }

        public Task<DataTable> SelectSQLAsync(string sql, Dictionary<string, object> parameters = null)
        {
            return Task.FromResult(SelectSQL(sql, parameters));
        }

        public Task<Dictionary<string, string>> ExecSQLAsync(string sql, Dictionary<string, object> parameters = null)
        {
            return Task.FromResult(ExecSQL(sql, parameters));
        }

        public List<string> GetDatabases() => new List<string> { "test_db" };
        public List<string> GetTables(string databaseName) => new List<string> { "users" };
        public List<string> GetViews(string databaseName) => new List<string> { "active_users" };

        public DataTable GetColumns(string databaseName, string tableName)
        {
            var dt = new DataTable();
            dt.Columns.Add("COLUMN_NAME");
            dt.Columns.Add("DATA_TYPE");
            dt.Columns.Add("Extra");
            dt.Rows.Add("id", "int", "");
            dt.Rows.Add("name", "varchar", "");
            if (Generated) dt.Rows.Add("computed", "int", "VIRTUAL GENERATED");
            return dt;
        }

        public DataTable GetIndexes(string databaseName, string tableName) => new DataTable();
        public DataTable GetTableStatus(string databaseName)
        {
            var dt = new DataTable(); dt.Columns.Add("Name"); dt.Columns.Add("Engine");
            dt.Rows.Add("users", Failure == "engine" ? "MyISAM" : "InnoDB"); return dt;
        }
        public Dictionary<string, string> GetDatabaseInfo(string databaseName) => new Dictionary<string, string>();

        public string GetTableCreateStatement(string databaseName, string tableName)
        {
            if (Generated) return "CREATE TABLE `users` (`id` int, `name` varchar(50), `computed` int GENERATED ALWAYS AS (id+1) VIRTUAL) ENGINE=InnoDB;";
            return "CREATE TABLE `users` (`id` int NOT NULL, `name` varchar(50) DEFAULT NULL, PRIMARY KEY (`id`)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;";
        }

        public bool TableExists(string databaseName, string tableName) => true;
        public bool ViewExists(string databaseName, string viewName) => true;
        public void RenameTable(string databaseName, string oldTableName, string newTableName) { }
        public void RenameView(string databaseName, string oldViewName, string newViewName) { }
        public long CountRows(string databaseName, string tableName) => 2;
        public DataTable GetCopyColumns(string databaseName, string tableName) => new DataTable();
        public DataTable GetCopyIndexes(string databaseName, string tableName) => new DataTable();
        public void CreateTableForCopy(string databaseName, string tableName, DataTable sourceColumns, string sourceProvider) { }
        public void DropTableForCopy(string databaseName, string tableName) { }
        public void CreateIndexesForCopy(string databaseName, string tableName, DataTable sourceIndexes, string sourceProvider) { }

        public DataTable SelectTablePage(string databaseName, string tableName, long offset, int limit)
        {
            var dt = new DataTable();
            dt.Columns.Add("id", typeof(int));
            dt.Columns.Add("name", typeof(string));
            if (Generated) dt.Columns.Add("computed", typeof(int));
            if (offset == 0)
            {
                dt.Rows.Add(1, "Alice");
                dt.Rows.Add(2, "Bob");
            }
            if (Generated) foreach (DataRow row in dt.Rows) row["computed"] = Convert.ToInt32(row["id"]) + 1;
            return dt;
        }

        public void InsertTableBatch(string databaseName, string tableName, DataTable rows) { }
        public string GetViewCreateStatement(string databaseName, string viewName) => "CREATE VIEW `active_users` AS SELECT * FROM `users` WHERE active = 1;";
        public void CreateViewFromStatement(string databaseName, string viewName, string sourceViewSql) { }
    }
}
