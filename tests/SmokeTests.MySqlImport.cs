using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using mySQLPunk.lib;
using mySQLPunk.lib.MySqlImport;

public static partial class SmokeTests
{
    private static void MySqlImport_PreflightScanner_DelimitersAndManifest()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "mysqlpunk_import_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string sqlFile = Path.Combine(tempDir, "sample_import.sql");

        try
        {
            string sqlContent = 
                "-- Header comments\r\n" +
                "USE `test_db`;\r\n" +
                "CREATE TABLE `users` (`id` int, `name` varchar(50));\r\n" +
                "DELIMITER //\r\n" +
                "CREATE PROCEDURE `test_proc`()\r\n" +
                "BEGIN\r\n" +
                "    SELECT 1;\r\n" +
                "END //\r\n" +
                "DELIMITER ;\r\n" +
                "INSERT INTO `users` VALUES (1, 'Alice');\r\n" +
                "-- ========================================================\r\n" +
                "-- mySQLPunk Native Manifest\r\n" +
                "-- Tables: 1\r\n" +
                "-- TotalRows: 1\r\n" +
                "-- Manifest-Table: users | Rows: 1\r\n" +
                "-- ========================================================\r\n";

            File.WriteAllText(sqlFile, sqlContent, Encoding.UTF8);

            var preflight = MySqlImportPreflightScanner.Scan(sqlFile, CancellationToken.None);
            Assert(preflight.Passed, "Preflight scan passed");
            Assert(preflight.FileBytes > 0, "FileBytes > 0");
            Assert(preflight.EstimatedStatements == 4, "Statement count should be 4 (USE, CREATE TABLE, CREATE PROCEDURE, INSERT), got " + preflight.EstimatedStatements);
            Assert(preflight.HasManifest, "Manifest should be recognized");
            Assert(preflight.ManifestTables == 1, "ManifestTables count match");
            Assert(preflight.ManifestRows == 1, "ManifestRows count match");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    private static void MySqlImport_Runner_ExecutionAndLog()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "mysqlpunk_runner_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string sqlFile = Path.Combine(tempDir, "run_import.sql");

        try
        {
            string sqlContent =
                "CREATE TABLE `test` (`id` int);\r\n" +
                "INSERT INTO `test` VALUES (10);\r\n" +
                "-- Manifest-Table: test | Rows: 1\r\n" +
                "-- Tables: 1\r\n" +
                "-- TotalRows: 1\r\n";

            File.WriteAllText(sqlFile, sqlContent, Encoding.UTF8);

            var mockDb = new MockMySqlImportDatabase();
            var runner = new MySqlImportRunner(mockDb, "test_db");

            var request = new MySqlImportRequest(Guid.NewGuid(), sqlFile, "test_db");
            var options = new MySqlImportOptions
            {
                ExistingObjectStrategy = MySqlExistingObjectStrategy.ExecuteScript,
                ContinueOnError = true
            };

            MySqlImportProgress lastProgress = null;
            var progress = new Progress<MySqlImportProgress>(p => lastProgress = p);

            var task = runner.RunAsync(request, options, progress, CancellationToken.None);
            task.Wait();
            var result = task.Result;

            Assert(result.Status == MySqlImportStatus.Completed, "Import status should be Completed");
            Assert(result.ExecutedStatements == 2, "Executed 2 statements");
            Assert(result.FailedStatements == 0, "0 failed statements");
            Assert(File.Exists(result.LogPath), "Import log file must exist at " + result.LogPath);

            string logContent = File.ReadAllText(result.LogPath);
            AssertContains(logContent, "mySQLPunk MySQL Import Execution Log", "Log contains start header");
            AssertContains(logContent, "Status    : Completed", "Log contains completion status");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    private sealed class MockMySqlImportDatabase : IDatabase
    {
        public ConnectionState State => ConnectionState.Open;
        public string ProviderName => "MySQL";

        public void SetConn(string connectionString) { }
        public void Open() { }
        public void Close() { }
        public void Dispose() { }

        public DataTable SelectSQL(string sql, Dictionary<string, object> parameters = null) => new DataTable();
        public Dictionary<string, string> ExecSQL(string sql, Dictionary<string, object> parameters = null) => new Dictionary<string, string>();
        public Task<DataTable> SelectSQLAsync(string sql, Dictionary<string, object> parameters = null) => Task.FromResult(new DataTable());
        public Task<Dictionary<string, string>> ExecSQLAsync(string sql, Dictionary<string, object> parameters = null) => Task.FromResult(new Dictionary<string, string>());

        public List<string> GetDatabases() => new List<string> { "test_db" };
        public List<string> GetTables(string databaseName) => new List<string> { "test" };
        public List<string> GetViews(string databaseName) => new List<string>();
        public DataTable GetColumns(string databaseName, string tableName) => new DataTable();
        public DataTable GetIndexes(string databaseName, string tableName) => new DataTable();
        public DataTable GetTableStatus(string databaseName) => new DataTable();
        public Dictionary<string, string> GetDatabaseInfo(string databaseName) => new Dictionary<string, string>();
        public string GetTableCreateStatement(string databaseName, string tableName) => "";
        public bool TableExists(string databaseName, string tableName) => true;
        public bool ViewExists(string databaseName, string viewName) => false;
        public void RenameTable(string databaseName, string oldTableName, string newTableName) { }
        public void RenameView(string databaseName, string oldViewName, string newViewName) { }
        public long CountRows(string databaseName, string tableName) => 1;
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
