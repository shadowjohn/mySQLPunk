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

    private static void MySqlImport_Regression_TargetLiteralsAndStrategies()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mysqlpunk_repair_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "fixture.sql");
            string payload = "src.orders\r\n-- keep this literal line\r\n# and this one\r\n/* src.orders */\r\nlast";
            File.WriteAllText(path, "# restore src\r\nUSE /* choose */ `src`; INSERT INTO src.t VALUES ('" + payload + "'); " +
                "/* src.t comment */ INSERT INTO `src`.`t` VALUES ('escaped \\' src.t', '{\"name\":\"src.orders\"}');", new UTF8Encoding(false));
            var db = new MockMySqlImportDatabase();
            var result = new MySqlImportRunner(db, "target").RunAsync(new MySqlImportRequest(Guid.NewGuid(), path, "target"), new MySqlImportOptions(), null, CancellationToken.None).Result;
            Assert(result.Status == MySqlImportStatus.Completed, "Inline/comment USE and inserts complete: " + result.Summary);
            Assert(result.ExecutedStatements == 3, "Inline statements split consistently");
            Assert(db.ExecutedSql.Exists(sql => sql.Contains("INSERT INTO `target`.t") && sql.Contains(payload)), "Database target changes while multiline payload remains exact");
            Assert(db.ExecutedSql.Exists(sql => sql.Contains("/* src.t comment */") && sql.Contains("'{\"name\":\"src.orders\"}'")), "Comment and JSON literal remain exact");
            string rewritten = MySqlImportRunner.RewriteDatabaseQualifier("INSERT INTO src.t VALUES ('src.t', 'xsrc.t') /* src.t */", "src", "target");
            AssertContains(rewritten, "`target`.t VALUES ('src.t', 'xsrc.t') /* src.t */", "Only identifier token rewritten");
            AssertEquals("SELECT xsrc.t", MySqlImportRunner.RewriteDatabaseQualifier("SELECT xsrc.t", "src", "target"), "Longer identifiers are not source qualifiers");

            File.WriteAllText(path, "INSERT INTO other.t VALUES (1);", new UTF8Encoding(false));
            db = new MockMySqlImportDatabase();
            result = new MySqlImportRunner(db, "target").RunAsync(new MySqlImportRequest(Guid.NewGuid(), path, "target"), new MySqlImportOptions(), null, CancellationToken.None).Result;
            Assert(result.Status == MySqlImportStatus.Completed && db.ExecutedSql.Exists(sql => sql.Contains("INSERT INTO `target`.t")), "Qualified INSERT without USE cannot write to other database");

            File.WriteAllText(path, "/* preserve */ DROP TABLE IF EXISTS old; CREATE TABLE old (id int); INSERT INTO old VALUES (1); CREATE TABLE fresh (id int); INSERT INTO fresh VALUES (2); INSERT INTO fresh VALUES (3);", new UTF8Encoding(false));
            db = new MockMySqlImportDatabase { GetTablesHandler = () => new List<string> { "old" }, TableExistsHandler = (database, table) => table == "old" };
            result = new MySqlImportRunner(db, "target").RunAsync(new MySqlImportRequest(Guid.NewGuid(), path, "target"), new MySqlImportOptions { ExistingObjectStrategy = MySqlExistingObjectStrategy.SkipExisting }, null, CancellationToken.None).Result;
            Assert(result.Status == MySqlImportStatus.Completed, "Mixed existing/new SkipExisting import completes");
            Assert(result.ExecutedStatements == 3 && result.SkippedStatements == 3, "Fresh CREATE and both INSERT batches execute; existing object protected");
            Assert(!db.ExecutedSql.Exists(sql => MySqlScriptReader.CodeOnly(sql).TrimStart().StartsWith("DROP", StringComparison.OrdinalIgnoreCase)), "Comment-prefixed DROP skipped");
            File.WriteAllText(path, "DROP DATABASE src;", new UTF8Encoding(false));
            db = new MockMySqlImportDatabase();
            result = new MySqlImportRunner(db, "target").RunAsync(new MySqlImportRequest(Guid.NewGuid(), path, "target"), new MySqlImportOptions(), null, CancellationToken.None).Result;
            Assert(result.Status == MySqlImportStatus.Failed && !db.ExecutedSql.Exists(sql => sql.Contains("DROP DATABASE")), "Database-wide DROP rejected");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void MySqlImport_Regression_CancelTransactionsAndPreflight()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mysqlpunk_repair_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "fixture.sql");
            File.WriteAllText(path, "INSERT INTO t VALUES (1);", new UTF8Encoding(false));
            using (var cancel = new CancellationTokenSource())
            {
                var db = new MockMySqlImportDatabase { ExecSqlHandler = sql => { if (sql.StartsWith("INSERT")) cancel.Cancel(); return new Dictionary<string, string> { { "status", "OK" } }; } };
                var result = new MySqlImportRunner(db, "target").RunAsync(new MySqlImportRequest(Guid.NewGuid(), path, "target"), new MySqlImportOptions(), null, cancel.Token).Result;
                Assert(result.Status == MySqlImportStatus.Cancelled, "Cancellation inside last command cannot report Completed");
                Assert(db.ExecutedSql.Contains("ROLLBACK;"), "Cancelled job attempts owned transaction cleanup");
            }
            File.WriteAllText(path, "BEGIN; INSERT INTO t VALUES (1);", new UTF8Encoding(false));
            var txDb = new MockMySqlImportDatabase();
            var txResult = new MySqlImportRunner(txDb, "target").RunAsync(new MySqlImportRequest(Guid.NewGuid(), path, "target"), new MySqlImportOptions(), null, CancellationToken.None).Result;
            Assert(txResult.Status == MySqlImportStatus.Failed, "Uncommitted script cannot report restored rows as complete");
            File.WriteAllText(path, "BEGIN; INSERT INTO t VALUES (1); COMMIT;", new UTF8Encoding(false));
            txDb = new MockMySqlImportDatabase { ExecSqlHandler = sql => new Dictionary<string, string> { { "status", sql.StartsWith("COMMIT") ? "NO" : "OK" }, { "reason", "injected commit failure" } } };
            txResult = new MySqlImportRunner(txDb, "target").RunAsync(new MySqlImportRequest(Guid.NewGuid(), path, "target"), new MySqlImportOptions(), null, CancellationToken.None).Result;
            Assert(txResult.Status == MySqlImportStatus.Failed, "Real status=NO commit cannot complete");
            File.WriteAllText(path, "INSERT INTO t VALUES (1);", new UTF8Encoding(false));
            txDb = new MockMySqlImportDatabase { ExecSqlHandler = sql => new Dictionary<string, string> { { "status", sql.StartsWith("ROLLBACK") ? "NO" : "OK" }, { "reason", "injected cleanup failure" } } };
            txResult = new MySqlImportRunner(txDb, "target").RunAsync(new MySqlImportRequest(Guid.NewGuid(), path, "target"), new MySqlImportOptions(), null, CancellationToken.None).Result;
            Assert(txResult.Status == MySqlImportStatus.Failed, "Cleanup status=NO cannot report completion");
            File.WriteAllText(path, "/*!40101 USE src */;", new UTF8Encoding(false));
            Assert(!MySqlImportPreflightScanner.Scan(path, CancellationToken.None).Passed, "Executable version comments explicitly unsupported");
            File.WriteAllText(path, "INSERT INTO t VALUES ('unterminated);", new UTF8Encoding(false));
            Assert(!MySqlImportPreflightScanner.Scan(path, CancellationToken.None).Passed, "Unterminated literal fails before execution");
            File.WriteAllText(path, "-- mySQLPunk Native Manifest\r\n-- Manifest-SHA256: " + new string('0', 64) + "\r\n", new UTF8Encoding(false));
            Assert(!MySqlImportPreflightScanner.Scan(path, CancellationToken.None).Passed, "Native digest is verified instead of displayed only");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void MySqlImport_Regression_ModifiedInsertProtectionAndTriggerPseudoRows()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mysqlpunk_review_repair_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "fixture.sql");
            string[] forms =
            {
                "INSERT LOW_PRIORITY INTO `TABLE`", "INSERT HIGH_PRIORITY `TABLE`",
                "INSERT DELAYED IGNORE INTO src.`TABLE`", "INSERT IGNORE `src`.`TABLE`",
                "INSERT `TABLE`", "REPLACE LOW_PRIORITY INTO `TABLE`",
                "REPLACE DELAYED src.TABLE", "REPLACE `TABLE`"
            };
            string sourceLookingValue = "'src.old NEW.id OLD.id'";
            var script = new StringBuilder("CREATE TABLE fresh (value text);\r\n");
            foreach (string form in forms)
            {
                script.Append(form.Replace("TABLE", "old")).Append(" VALUES (").Append(sourceLookingValue).Append(");\r\n");
                script.Append(form.Replace("TABLE", "fresh")).Append(" VALUES (").Append(sourceLookingValue).Append(");\r\n");
            }
            File.WriteAllText(path, script.ToString(), new UTF8Encoding(false));
            var db = new MockMySqlImportDatabase { GetTablesHandler = () => new List<string> { "old" }, TableExistsHandler = (database, table) => table == "old" };
            var options = new MySqlImportOptions { ExistingObjectStrategy = MySqlExistingObjectStrategy.SkipExisting };
            var result = new MySqlImportRunner(db, "target").RunAsync(new MySqlImportRequest(Guid.NewGuid(), path, "target"), options, null, CancellationToken.None).Result;
            Assert(result.Status == MySqlImportStatus.Completed, "Priority/optional-INTO forms restore safely: " + result.Summary);
            Assert(result.SkippedStatements == forms.Length && result.ExecutedStatements == forms.Length + 1, "All old-table INSERT/REPLACE variants skipped and fresh writes execute");
            foreach (string sql in db.ExecutedSql)
            {
                string code = MySqlScriptReader.CodeOnly(sql).TrimStart();
                if (!code.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) && !code.StartsWith("REPLACE", StringComparison.OrdinalIgnoreCase)) continue;
                Assert(code.IndexOf("old", StringComparison.OrdinalIgnoreCase) < 0, "Protected table never reaches provider for any accepted variant");
                AssertContains(sql, sourceLookingValue, "Source/pseudo-row-looking literal remains exact in fresh-table writes");
                Assert(!code.Contains("src." ) && !code.Contains("`src`."), "Qualified optional-INTO source targets redirected without a USE");
            }
            script.Clear();
            foreach (string form in forms) script.Append(form.Replace("TABLE", "old")).Append(" VALUES (1);\r\n");
            File.WriteAllText(path, script.ToString(), new UTF8Encoding(false));
            db = new MockMySqlImportDatabase { GetTablesHandler = () => new List<string> { "old" } };
            result = new MySqlImportRunner(db, "target").RunAsync(new MySqlImportRequest(Guid.NewGuid(), path, "target"), options, null, CancellationToken.None).Result;
            Assert(result.Status == MySqlImportStatus.Completed && result.ExecutedStatements == 0 && result.SkippedStatements == forms.Length, "Data-only modifier/optional-INTO forms all preserve initial existing table");

            File.WriteAllText(path, "DELIMITER //\r\n" +
                "CREATE TRIGGER `tr` BEFORE UPDATE ON `src`.`t` FOR EACH ROW\r\n" +
                "BEGIN\r\nSET NEW.id = OLD.id;\r\n" +
                "INSERT LOW_PRIORITY INTO src.audit VALUES (NEW.id, 'src.audit NEW.id OLD.id');\r\nEND//\r\nDELIMITER ;\r\n", new UTF8Encoding(false));
            db = new MockMySqlImportDatabase();
            result = new MySqlImportRunner(db, "target").RunAsync(new MySqlImportRequest(Guid.NewGuid(), path, "target"), new MySqlImportOptions(), null, CancellationToken.None).Result;
            Assert(result.Status == MySqlImportStatus.Completed && result.ExecutedStatements == 1, "Native DELIMITER trigger with NEW/OLD imports as one definition: " + result.Summary);
            string trigger = db.ExecutedSql.Find(sql => sql.Contains("CREATE TRIGGER"));
            AssertContains(trigger, "ON `target`.`t`", "Trigger table qualified to selected target without USE");
            AssertContains(trigger, "INSERT LOW_PRIORITY INTO `target`.audit", "Trigger body real object qualified to selected target");
            AssertContains(trigger, "SET NEW.id = OLD.id;", "Trigger pseudo-row identifiers remain unchanged");
            AssertContains(trigger, "'src.audit NEW.id OLD.id'", "Trigger source-looking literal remains unchanged");
            File.WriteAllText(path, "INSERT INTO t VALUES (NEW.id);", new UTF8Encoding(false));
            db = new MockMySqlImportDatabase();
            result = new MySqlImportRunner(db, "target").RunAsync(new MySqlImportRequest(Guid.NewGuid(), path, "target"), new MySqlImportOptions(), null, CancellationToken.None).Result;
            Assert(result.Status == MySqlImportStatus.Failed, "NEW/OLD exemption is confined to recognized trigger definitions");
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class MockMySqlImportDatabase : IDatabase
    {
        public ConnectionState State => ConnectionState.Open;
        public string ProviderName => "MySQL";
        public List<string> ExecutedSql = new List<string>();
        public Func<string, Dictionary<string, string>> ExecSqlHandler = null;
        public Func<string, string, bool> TableExistsHandler = null;
        public Func<List<string>> GetTablesHandler = null;
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
        public List<string> GetTables(string databaseName) => GetTablesHandler != null ? GetTablesHandler() : new List<string> { "test" };
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
