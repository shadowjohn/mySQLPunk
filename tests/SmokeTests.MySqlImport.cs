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
            Assert(result.TotalRowsAffected == 1, "TotalRowsAffected should be 1");
            Assert(File.Exists(result.LogPath), "Import log file must exist at " + result.LogPath);

            string logContent = File.ReadAllText(result.LogPath);
            AssertContains(logContent, "mySQLPunk MySQL Import Execution Log", "Log contains start header");
            AssertContains(logContent, "Status    : Completed", "Log contains completion status");
            AssertContains(logContent, "Rows      : 1 rows affected", "Log contains rows affected");

            string rawCommented = "-- table structure\r\n-- more comment\r\nCREATE TABLE `orders` (`id` int);\r\n";
            string headline1 = MySqlImportRunner.SanitizeSqlHeadline(rawCommented);
            AssertEquals("CREATE TABLE `orders` (`id` int);", headline1, "Commented DDL headline match");

            string hugeInsert = "INSERT INTO `orders` VALUES " + new string('A', 500);
            string headline2 = MySqlImportRunner.SanitizeSqlHeadline(hugeInsert);
            Assert(headline2.Length <= 135, "Headline length bounded to prevent memory growth");
            AssertContains(headline2, "INSERT INTO `orders` VALUES (...)", "Huge insert condensed");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    private static void MySqlImport_Runner_ErrorDetection_HaltAndContinue()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "mysqlpunk_err_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string sqlFile = Path.Combine(tempDir, "err_import.sql");

        try
        {
            string sqlContent =
                "CREATE TABLE `users` (`id` int);\r\n" +
                "CREATE TABLE `users` (`id` int);\r\n" +
                "INSERT INTO `users` VALUES (1);\r\n";
            File.WriteAllText(sqlFile, sqlContent, Encoding.UTF8);

            // 1. 測試 ContinueOnError = false (遇到錯誤立即中斷)
            {
                var mockDb = new MockMySqlImportDatabase();
                int execCount = 0;
                mockDb.ExecSqlHandler = sql =>
                {
                    if (sql.StartsWith("CREATE TABLE `users`", StringComparison.OrdinalIgnoreCase))
                    {
                        execCount++;
                        if (execCount == 2)
                        {
                            return new Dictionary<string, string>
                            {
                                { "status", "NO" },
                                { "reason", "Table 'users' already exists" }
                            };
                        }
                    }
                    return new Dictionary<string, string> { { "status", "OK" }, { "rowsAffected", "1" } };
                };

                var runner = new MySqlImportRunner(mockDb, "test_db");
                var request = new MySqlImportRequest(Guid.NewGuid(), sqlFile, "test_db");
                var options = new MySqlImportOptions { ContinueOnError = false };

                var task = runner.RunAsync(request, options, null, CancellationToken.None);
                task.Wait();
                var result = task.Result;

                Assert(result.Status == MySqlImportStatus.Failed, "Status should be Failed on error without continue");
                Assert(result.FailedStatements == 1, "Failed count should be 1");
                Assert(result.ExecutedStatements == 1, "Executed count should be 1 (stopped before third statement)");
                Assert(result.Errors.Count == 1, "Errors list should have 1 entry");
                AssertContains(result.Errors[0], "Table 'users' already exists", "Error reason must be recorded");
            }

            // 2. 測試 ContinueOnError = true (遇到錯誤略過失敗批次並繼續)
            {
                var mockDb = new MockMySqlImportDatabase();
                int execCount = 0;
                mockDb.ExecSqlHandler = sql =>
                {
                    if (sql.StartsWith("CREATE TABLE `users`", StringComparison.OrdinalIgnoreCase))
                    {
                        execCount++;
                        if (execCount == 2)
                        {
                            return new Dictionary<string, string>
                            {
                                { "status", "NO" },
                                { "reason", "Table 'users' already exists" }
                            };
                        }
                    }
                    return new Dictionary<string, string> { { "status", "OK" }, { "rowsAffected", "1" } };
                };

                var runner = new MySqlImportRunner(mockDb, "test_db");
                var request = new MySqlImportRequest(Guid.NewGuid(), sqlFile, "test_db");
                var options = new MySqlImportOptions { ContinueOnError = true };

                var task = runner.RunAsync(request, options, null, CancellationToken.None);
                task.Wait();
                var result = task.Result;

                Assert(result.Status == MySqlImportStatus.CompletedWithErrors, "Status should be CompletedWithErrors");
                Assert(result.FailedStatements == 1, "Failed count should be 1");
                Assert(result.ExecutedStatements == 2, "Executed count should be 2 (first and third statements executed)");
                Assert(result.Errors.Count == 1, "Errors list should have 1 entry");
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    private static void MySqlImport_Runner_DatabaseRedirection()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "mysqlpunk_redirect_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string sqlFile = Path.Combine(tempDir, "redirect_import.sql");

        try
        {
            string sqlContent =
                "USE `old_db`;\r\n" +
                "CREATE DATABASE `old_db`;\r\n" +
                "CREATE TABLE `old_db`.`customers` (`id` int);\r\n" +
                "INSERT INTO `old_db`.`customers` VALUES (100);\r\n";
            File.WriteAllText(sqlFile, sqlContent, Encoding.UTF8);

            var mockDb = new MockMySqlImportDatabase();
            var runner = new MySqlImportRunner(mockDb, "target_db");
            var request = new MySqlImportRequest(Guid.NewGuid(), sqlFile, "target_db");
            var options = new MySqlImportOptions { ExistingObjectStrategy = MySqlExistingObjectStrategy.ExecuteScript };

            var task = runner.RunAsync(request, options, null, CancellationToken.None);
            task.Wait();
            var result = task.Result;

            Assert(result.Status == MySqlImportStatus.Completed, "Status should be Completed");
            Assert(result.ExecutedStatements == 4, "Executed 4 redirected statements");

            // 驗證沒有任何 SQL 執行於 old_db，全數導向至 target_db
            foreach (var executed in mockDb.ExecutedSql)
            {
                Assert(!executed.Contains("`old_db`"), "Executed SQL should never target old_db, got: " + executed);
            }
            Assert(mockDb.ExecutedSql.Exists(s => s.StartsWith("USE `target_db`")), "Must execute USE `target_db`");
            Assert(mockDb.ExecutedSql.Exists(s => s.StartsWith("CREATE DATABASE IF NOT EXISTS `target_db`")), "Must ensure target_db exists");
            Assert(mockDb.ExecutedSql.Exists(s => s.Contains("`target_db`.`customers`")), "Must qualify customer table with target_db");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    private static void MySqlImport_Runner_ExistingObjectStrategies()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "mysqlpunk_strat_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string sqlFile = Path.Combine(tempDir, "strat_import.sql");

        try
        {
            string sqlContent =
                "DROP TABLE IF EXISTS `items`;\r\n" +
                "CREATE TABLE `items` (`id` int);\r\n" +
                "INSERT INTO `items` VALUES (1);\r\n";
            File.WriteAllText(sqlFile, sqlContent, Encoding.UTF8);

            // 1. DropAndRecreate 策略：若表已存在，建立前執行 DROP
            {
                var mockDb = new MockMySqlImportDatabase();
                mockDb.TableExistsHandler = (db, tbl) => tbl == "items";

                var runner = new MySqlImportRunner(mockDb, "test_db");
                var request = new MySqlImportRequest(Guid.NewGuid(), sqlFile, "test_db");
                var options = new MySqlImportOptions { ExistingObjectStrategy = MySqlExistingObjectStrategy.DropAndRecreate };

                var task = runner.RunAsync(request, options, null, CancellationToken.None);
                task.Wait();
                var result = task.Result;

                Assert(result.Status == MySqlImportStatus.Completed, "DropAndRecreate completed");
                Assert(mockDb.ExecutedSql.Exists(s => s.StartsWith("DROP TABLE IF EXISTS `test_db`.`items`")), "DROP before CREATE was executed");
            }

            // 2. CreateIfMissing 策略：略過 DROP，且略過既有表的 CREATE
            {
                var mockDb = new MockMySqlImportDatabase();
                mockDb.TableExistsHandler = (db, tbl) => tbl == "items";

                var runner = new MySqlImportRunner(mockDb, "test_db");
                var request = new MySqlImportRequest(Guid.NewGuid(), sqlFile, "test_db");
                var options = new MySqlImportOptions { ExistingObjectStrategy = MySqlExistingObjectStrategy.CreateIfMissing };

                var task = runner.RunAsync(request, options, null, CancellationToken.None);
                task.Wait();
                var result = task.Result;

                Assert(result.Status == MySqlImportStatus.Completed, "CreateIfMissing completed");
                Assert(result.SkippedStatements == 2, "DROP and CREATE TABLE should be skipped (2 skipped)");
                Assert(result.ExecutedStatements == 1, "INSERT was executed");
            }

            // 3. SkipExisting 策略：略過 DROP、CREATE 與 INSERT
            {
                var mockDb = new MockMySqlImportDatabase();
                mockDb.TableExistsHandler = (db, tbl) => tbl == "items";

                var runner = new MySqlImportRunner(mockDb, "test_db");
                var request = new MySqlImportRequest(Guid.NewGuid(), sqlFile, "test_db");
                var options = new MySqlImportOptions { ExistingObjectStrategy = MySqlExistingObjectStrategy.SkipExisting };

                var task = runner.RunAsync(request, options, null, CancellationToken.None);
                task.Wait();
                var result = task.Result;

                Assert(result.Status == MySqlImportStatus.Completed, "SkipExisting completed");
                Assert(result.SkippedStatements == 3, "DROP, CREATE TABLE, and INSERT should all be skipped (3 skipped)");
                Assert(result.ExecutedStatements == 0, "No statements executed");
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    private static void MySqlImport_Runner_ManifestMismatchDetection()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "mysqlpunk_manifest_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string sqlFile = Path.Combine(tempDir, "manifest_mismatch.sql");

        try
        {
            string sqlContent =
                "CREATE TABLE `products` (`id` int);\r\n" +
                "-- ========================================================\r\n" +
                "-- mySQLPunk Native Manifest\r\n" +
                "-- Tables: 1\r\n" +
                "-- TotalRows: 50\r\n" +
                "-- Manifest-Table: products | Rows: 50\r\n" +
                "-- ========================================================\r\n";
            File.WriteAllText(sqlFile, sqlContent, Encoding.UTF8);

            var mockDb = new MockMySqlImportDatabase();
            // 目標資料庫實際只有 0 筆，與 Manifest 記載的 50 筆不符
            mockDb.CountRowsHandler = (db, tbl) => 0;

            var runner = new MySqlImportRunner(mockDb, "test_db");
            var request = new MySqlImportRequest(Guid.NewGuid(), sqlFile, "test_db");
            var options = new MySqlImportOptions { ExistingObjectStrategy = MySqlExistingObjectStrategy.ExecuteScript };

            var task = runner.RunAsync(request, options, null, CancellationToken.None);
            task.Wait();
            var result = task.Result;

            Assert(result.Status == MySqlImportStatus.CompletedWithErrors, "Status should be CompletedWithErrors due to manifest mismatch");
            AssertContains(result.Summary, "Native Manifest 驗證發現列數不符", "Summary must report manifest discrepancy");
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
        public List<string> ExecutedSql = new List<string>();
        public Func<string, Dictionary<string, string>> ExecSqlHandler = null;
        public Func<string, string, bool> TableExistsHandler = null;
        public Func<string, string, long> CountRowsHandler = null;

        public void SetConn(string connectionString) { }
        public void Open() { }
        public void Close() { }
        public void Dispose() { }

        public DataTable SelectSQL(string sql, Dictionary<string, object> parameters = null) => new DataTable();
        public Dictionary<string, string> ExecSQL(string sql, Dictionary<string, object> parameters = null)
        {
            ExecutedSql.Add(sql);
            if (ExecSqlHandler != null) return ExecSqlHandler(sql);
            var res = new Dictionary<string, string>();
            res["status"] = "OK";
            string trimmed = (sql ?? "").TrimStart();
            bool isDataDml = trimmed.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) ||
                             trimmed.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) ||
                             trimmed.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase) ||
                             trimmed.StartsWith("REPLACE", StringComparison.OrdinalIgnoreCase);
            res["rowsAffected"] = isDataDml ? "1" : "0";
            return res;
        }
        public Task<DataTable> SelectSQLAsync(string sql, Dictionary<string, object> parameters = null) => Task.FromResult(new DataTable());
        public Task<Dictionary<string, string>> ExecSQLAsync(string sql, Dictionary<string, object> parameters = null) => Task.FromResult(ExecSQL(sql, parameters));

        public List<string> GetDatabases() => new List<string> { "test_db" };
        public List<string> GetTables(string databaseName) => new List<string> { "test" };
        public List<string> GetViews(string databaseName) => new List<string>();
        public DataTable GetColumns(string databaseName, string tableName) => new DataTable();
        public DataTable GetIndexes(string databaseName, string tableName) => new DataTable();
        public DataTable GetTableStatus(string databaseName) => new DataTable();
        public Dictionary<string, string> GetDatabaseInfo(string databaseName) => new Dictionary<string, string>();
        public string GetTableCreateStatement(string databaseName, string tableName) => "";
        public bool TableExists(string databaseName, string tableName) => TableExistsHandler != null ? TableExistsHandler(databaseName, tableName) : true;
        public bool ViewExists(string databaseName, string viewName) => false;
        public void RenameTable(string databaseName, string oldTableName, string newTableName) { }
        public void RenameView(string databaseName, string oldViewName, string newViewName) { }
        public long CountRows(string databaseName, string tableName) => CountRowsHandler != null ? CountRowsHandler(databaseName, tableName) : 1;
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
