using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Linq;
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

            sqlContent = sqlContent.Replace("-- Manifest-SHA256: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\r\n", "");
            File.WriteAllText(sqlFile, sqlContent, Encoding.UTF8);
            using (var hash=System.Security.Cryptography.SHA256.Create())
            {
                string checksum=BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(sqlFile))).Replace("-", "").ToLowerInvariant();
                File.AppendAllText(sqlFile,"-- Manifest-Payload-SHA256: "+checksum+"\r\n",new UTF8Encoding(false));
            }

            var preflight = SqliteImportPreflightScanner.Scan(sqlFile, CancellationToken.None);
            Assert(preflight.Passed, "Preflight scan should pass");
            Assert(preflight.FileBytes > 0, "FileBytes > 0");
            Assert(preflight.DetectedSpatiaLite, "Preflight must detect SpatiaLite functions like InitSpatialMetaData / AddGeometryColumn");
            Assert(preflight.HasManifest, "Preflight must detect Native Manifest");
            Assert(preflight.ManifestTables == 1, "ManifestTables count match 1");
            Assert(preflight.ManifestRows == 1, "ManifestRows count match 1");
            Assert(preflight.ManifestSha256?.Length == 64, "Manifest payload SHA-256 validated");

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

    private static SqliteImportOptions SqliteSafeImportOptions(SqliteExistingObjectStrategy strategy = SqliteExistingObjectStrategy.AbortIfExists)
    {
        return new SqliteImportOptions { ExistingObjectStrategy = strategy, WrapInTransaction = true, PragmaForeignKeysOff = false, PragmaSynchronousOff = false, AutoLoadSpatiaLite = false };
    }
    private static SqliteImportResult SqliteRunFixture(SqliteFixtureDatabase database, string directory, string sql, SqliteImportOptions options = null, CancellationToken token = default(CancellationToken))
    {
        string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".sql");
        File.WriteAllText(path, sql, new UTF8Encoding(false, true));
        return new SqliteImportRunner(database, "main").RunAsync(new SqliteImportRequest(Guid.NewGuid(), path, "main"), options ?? SqliteSafeImportOptions(), null, token).GetAwaiter().GetResult();
    }
    private static void SqliteImport_NativeConflictPolicies_ProtectDropAndDependencies()
    {
        var expected = new[] { SqliteExistingObjectStrategy.SkipExisting, SqliteExistingObjectStrategy.DropAndRecreate, SqliteExistingObjectStrategy.OverwriteKeepExisting, SqliteExistingObjectStrategy.AbortIfExists };
        string directory = Path.Combine(Path.GetTempPath(), "mysqlpunk_sqlite_policy_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            for (int index = 0; index < expected.Length; index++)
            {
                Assert(SqliteExistingObjectSelection.FromIndex(index) == expected[index], "Visible conflict label maps to its policy");
                using (var database = new SqliteFixtureDatabase())
                {
                    database.Must("CREATE TABLE t(x PRIMARY KEY); INSERT INTO t VALUES(99); CREATE TABLE audit(x); CREATE INDEX t_old ON t(x); CREATE TRIGGER t_ai AFTER INSERT ON t BEGIN INSERT INTO audit VALUES(new.x); END;");
                    string sql = "BEGIN; DROP TRIGGER IF EXISTS main.t_ai; DROP TABLE IF EXISTS main.t; CREATE TABLE main.t(x PRIMARY KEY); INSERT INTO main.t VALUES(1); CREATE INDEX t_new ON t(x); CREATE TRIGGER t_new_ai AFTER INSERT ON t BEGIN INSERT INTO audit VALUES(new.x+100); END; COMMIT;";
                    var result = SqliteRunFixture(database, directory, sql, SqliteSafeImportOptions(expected[index]));
                    Assert(database.MCT.AutoCommit, "Conflict policy leaves no transaction active");
                    Assert(database.TableExists("main", "t"), "Every conflict policy retains a table");
                    if (expected[index] == SqliteExistingObjectStrategy.AbortIfExists)
                    {
                        Assert(result.Status == SqliteImportStatus.Failed, "Abort policy fails before DROP");
                        Assert(Convert.ToInt64(database.Scalar("SELECT x FROM t;")) == 99, "Abort preserves original rows");
                        Assert(database.Exists("trigger", "t_ai"), "Abort preserves dependent trigger");
                    }
                    else if (expected[index] == SqliteExistingObjectStrategy.DropAndRecreate)
                    {
                        Assert(result.Status == SqliteImportStatus.Completed, "Recreate completes");
                        Assert(Convert.ToInt64(database.Scalar("SELECT x FROM t;")) == 1, "Recreate restores dump row");
                        Assert(!database.Exists("index", "t_old") && database.Exists("index", "t_new"), "Recreate replaces dependent indexes");
                    }
                    else
                    {
                        Assert(result.Status == SqliteImportStatus.Completed, "Preserving policy completes");
                        Assert(Convert.ToInt64(database.Scalar("SELECT count(*) FROM t WHERE x=99;")) == 1, "Preserving policy keeps original rows");
                        Assert(database.Exists("index", "t_old") && !database.Exists("index", "t_new"), "Preserving policy protects dependent structure");
                        Assert(database.Exists("trigger", "t_ai") && !database.Exists("trigger", "t_new_ai"), "Preserving policy protects dependent triggers");
                        long count = Convert.ToInt64(database.Scalar("SELECT count(*) FROM t;"));
                        Assert(count == (expected[index] == SqliteExistingObjectStrategy.SkipExisting ? 1 : 2), "Skip protects data and Keep inserts data");
                    }
                }
            }
            using(var database=new SqliteFixtureDatabase())
            {
                database.Must("CREATE TABLE 中文(x); INSERT INTO 中文 VALUES(99);");
                var result=SqliteRunFixture(database,directory,"DROP TABLE 中文; CREATE TABLE 中文(x); INSERT INTO 中文 VALUES(1);",SqliteSafeImportOptions(SqliteExistingObjectStrategy.SkipExisting));
                Assert(result.Status==SqliteImportStatus.Completed && Convert.ToInt64(database.Scalar("SELECT x FROM 中文;"))==99,"Unicode unquoted names cannot bypass SkipExisting");
            }
            foreach(var policy in expected)
            using(var database=new SqliteFixtureDatabase())
            {
                database.Must("CREATE TABLE __mysqlpunk_column_comments(table_name TEXT NOT NULL,column_name TEXT NOT NULL,comment TEXT NOT NULL,PRIMARY KEY(table_name,column_name)); INSERT INTO __mysqlpunk_column_comments VALUES('other','x','preserve');");
                var result=SqliteRunFixture(database,directory,"CREATE TABLE t(x); CREATE TABLE IF NOT EXISTS __mysqlpunk_column_comments(table_name TEXT NOT NULL,column_name TEXT NOT NULL,comment TEXT NOT NULL,PRIMARY KEY(table_name,column_name)); INSERT OR REPLACE INTO __mysqlpunk_column_comments(table_name,column_name,comment) VALUES('t','x','new');",SqliteSafeImportOptions(policy));
                Assert(result.Status==SqliteImportStatus.Completed && Convert.ToInt64(database.Scalar("SELECT count(*) FROM __mysqlpunk_column_comments;"))==2,"Idempotent sidecar preserves other tables' comments under "+policy);
            }
        }
        finally { Directory.Delete(directory, true); }
    }
    private static void SqliteImport_NativeLexer_TriggerAndExactText()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mysqlpunk_sqlite_lexer_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            using (var database = new SqliteFixtureDatabase())
            {
                string text = "a\nb\rc\r\nd'中; END";
                string sql = "-- leading\nBEGIN;\nCREATE TABLE `t; name`(x TEXT);\nCREATE TABLE audit(x TEXT);\n" +
                    "CREATE TRIGGER `t; trigger` AFTER INSERT ON `t; name` BEGIN INSERT INTO audit VALUES(CASE WHEN new.x IS NULL THEN 'END; --' ELSE new.x END); /* END; */ UPDATE audit SET x=x||';' WHERE rowid=last_insert_rowid(); END;\n" +
                    "INSERT INTO `t; name` VALUES('" + text.Replace("'", "''") + "'); -- inline keeps next token separate\nCOMMIT;";
                var result = SqliteRunFixture(database, directory, sql);
                Assert(result.Status == SqliteImportStatus.Completed, "Trigger/CASE/quotes/comments import succeeds: " + string.Join(";",result.Errors));
                AssertEquals(text, Convert.ToString(database.Scalar("SELECT x FROM `t; name`;")), "Literal CR/LF bytes remain exact");
                AssertEquals(text + ";", Convert.ToString(database.Scalar("SELECT x FROM audit;")), "Multi-statement trigger executes");
                Assert(database.MCT.AutoCommit, "Trigger import committed");
            }
            foreach (string sql in new[] { "SELECT 'unterminated", "SELECT 1; /* unterminated", "CREATE TRIGGER tr AFTER INSERT ON t BEGIN SELECT 1;" })
            {
                string path=Path.Combine(directory, Guid.NewGuid().ToString("N")+".sql"); File.WriteAllText(path, sql,new UTF8Encoding(false));
                Assert(!SqliteImportPreflightScanner.Scan(path,CancellationToken.None).Passed, "Unterminated lexical input rejected");
            }
            string malformed=Path.Combine(directory,"malformed.sql"); File.WriteAllBytes(malformed,new byte[]{0x53,0x45,0x4C,0x45,0x43,0x54,0x20,0xC3,0x28});
            Assert(!SqliteImportPreflightScanner.Scan(malformed,CancellationToken.None).Passed,"Malformed UTF-8 rejected without replacement");
        }
        finally { Directory.Delete(directory,true); }
    }
    private static void SqliteImport_NativeTransactions_FailureCancelAndPragmas()
    {
        string directory=Path.Combine(Path.GetTempPath(),"mysqlpunk_sqlite_cleanup_"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            foreach(string sql in new[] { "BEGIN; CREATE TABLE t(x); INSERT INTO t VALUES(1); FAILING_SQL; COMMIT;", "CREATE TABLE t(x); INSERT INTO t VALUES(1); FAILING_SQL" })
            using(var database=new SqliteFixtureDatabase())
            {
                database.Must("PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL;");
                var options=SqliteSafeImportOptions(); options.PragmaForeignKeysOff=true; options.PragmaSynchronousOff=true;
                var result=SqliteRunFixture(database,directory,sql,options);
                Assert(result.Status==SqliteImportStatus.Failed,"SQL failure including unterminated tail fails");
                Assert(database.MCT.AutoCommit && !database.TableExists("main","t"),"Script and managed transactions roll back schema/rows");
                Assert(Convert.ToInt64(database.Scalar("PRAGMA foreign_keys;"))==1 && Convert.ToInt64(database.Scalar("PRAGMA synchronous;"))==1,"Original PRAGMAs restored on failure");
            }
            using(var database=new SqliteFixtureDatabase())
            {
                database.Must("PRAGMA foreign_keys=ON;");
                var result=SqliteRunFixture(database,directory,"CREATE TABLE parent(id INTEGER PRIMARY KEY); CREATE TABLE child(id REFERENCES parent(id) DEFERRABLE INITIALLY DEFERRED); INSERT INTO child VALUES(7);");
                Assert(result.Status==SqliteImportStatus.Failed,"Deferred foreign-key COMMIT failure cannot report Completed");
                Assert(database.MCT.AutoCommit && !database.TableExists("main","child"),"Rejected COMMIT rolls back pending schema/rows");
            }
            foreach(string command in new[]{"BEGIN","COMMIT","ROLLBACK"})
            using(var database=new SqliteFixtureDatabase())
            {
                database.FailExecPrefix=command;
                string sql=command=="ROLLBACK" ? "CREATE TABLE t(x); FAILING_SQL;" : "CREATE TABLE t(x);";
                var result=SqliteRunFixture(database,directory,sql);
                Assert(result.Status==SqliteImportStatus.Failed,"Provider NO for "+command+" is failure");
                if(command=="ROLLBACK") { Assert(result.Errors.Any(e=>e.Contains("ROLLBACK cleanup failed")),"Rollback NO is visible"); database.FailExecPrefix=null; database.Must("ROLLBACK;"); }
                else Assert(database.MCT.AutoCommit && !database.TableExists("main","t"),command+" failure cannot leak pending work");
            }
            string cancellationDatabase = Path.Combine(directory,"cancel-fixture.sqlite");
            using(var database=new SqliteFixtureDatabase(cancellationDatabase))
            using(var cancellation=new CancellationTokenSource())
            {
                database.Must("PRAGMA foreign_keys=OFF; PRAGMA synchronous=FULL;");
                cancellation.CancelAfter(100);
                var latency=System.Diagnostics.Stopwatch.StartNew();
                var result=SqliteRunFixture(database,directory,"BEGIN; CREATE TABLE t(x); WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<100000000) INSERT INTO t SELECT x FROM n; COMMIT;",SqliteSafeImportOptions(),cancellation.Token);
                Assert(latency.Elapsed<TimeSpan.FromSeconds(5),"Native cancellation interrupts a running statement promptly");
                Assert(result.Status==SqliteImportStatus.Cancelled,"Long native command cancellation is reported");
                Assert(database.MCT.AutoCommit && !database.TableExists("main","t"),"Cancellation rolls back script transaction");
                using(var other=new SQLiteConnection(new SQLiteConnectionStringBuilder { DataSource=cancellationDatabase, Version=3, Pooling=false }.ToString()))
                { other.Open(); using(var command=new SQLiteCommand("CREATE TABLE independent_write(x);",other)) command.ExecuteNonQuery(); }
                Assert(Convert.ToInt64(database.Scalar("PRAGMA foreign_keys;"))==0 && Convert.ToInt64(database.Scalar("PRAGMA synchronous;"))==2,"Original PRAGMAs restored on cancellation");
            }
        }
        finally { Directory.Delete(directory,true); }
    }
    private static void SqliteImport_StatementLimit_MultibyteAndTerminated()
    {
        string directory=Path.Combine(Path.GetTempPath(),"mysqlpunk_sqlite_limit_"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            string path=Path.Combine(directory,"limit.sql");
            using(var writer=new StreamWriter(path,false,new UTF8Encoding(false)))
            {
                writer.Write("SELECT '"); string chunk=new string('中',4096);
                for(int i=0;i<2731;i++) writer.Write(chunk); writer.Write("';");
            }
            var result=SqliteImportPreflightScanner.Scan(path,CancellationToken.None);
            Assert(!result.Passed && result.Issues.Any(e=>e.Contains("32 MiB")),"Completed single-line multibyte statement is bounded in bytes before semicolon");
        }
        finally { Directory.Delete(directory,true); }
    }

    private static void SqliteImport_ReviewRegression_ManagedDropAndImplicitRollback()
    {
        string directory=Path.Combine(Path.GetTempPath(),"mysqlpunk_sqlite_review_tx_"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            foreach(string sql in new[]{"CREATE TABLE t(x); INSERT INTO t VALUES(1); FAILING_SQL;", "CREATE TABLE t(x,);"})
            using(var database=new SqliteFixtureDatabase())
            {
                database.Must("CREATE TABLE t(x); INSERT INTO t VALUES(99);");
                var result=SqliteRunFixture(database,directory,sql,SqliteSafeImportOptions(SqliteExistingObjectStrategy.DropAndRecreate));
                Assert(result.Status==SqliteImportStatus.Failed && database.MCT.AutoCommit,"Replacement failure rolls back managed transaction");
                Assert(database.TableExists("main","t") && Convert.ToInt64(database.Scalar("SELECT x FROM t;"))==99,"Policy-generated DROP is rolled back with replacement failure");
            }
            foreach(string sql in new[]{
                "CREATE TABLE t(x UNIQUE); INSERT INTO t VALUES(1); INSERT OR ROLLBACK INTO t VALUES(1); CREATE TABLE leaked(x); INSERT INTO leaked VALUES(2);",
                "BEGIN; CREATE TABLE t(x UNIQUE); INSERT INTO t VALUES(1); INSERT OR ROLLBACK INTO t VALUES(1); CREATE TABLE leaked(x); INSERT INTO leaked VALUES(2); COMMIT;",
                "CREATE TABLE t(x); CREATE TRIGGER rollback_trigger BEFORE INSERT ON t WHEN new.x=2 BEGIN SELECT RAISE(ROLLBACK,'stop'); END; INSERT INTO t VALUES(1); INSERT INTO t VALUES(2); CREATE TABLE leaked(x);"})
            using(var database=new SqliteFixtureDatabase())
            {
                database.Must("CREATE TABLE original(x); INSERT INTO original VALUES(99);");
                var options=SqliteSafeImportOptions(); options.ContinueOnError=true;
                var result=SqliteRunFixture(database,directory,sql,options);
                Assert(result.Status==SqliteImportStatus.Failed && database.MCT.AutoCommit,"Statement-level full rollback stops continued execution");
                Assert(!database.TableExists("main","t") && !database.TableExists("main","leaked"),"No later statement leaks into autocommit after OR/RAISE ROLLBACK");
                Assert(Convert.ToInt64(database.Scalar("SELECT x FROM original;"))==99,"Initial rows survive implicit rollback");
            }
        }
        finally { Directory.Delete(directory,true); }
    }
    private static void SqliteImport_ReviewRegression_MetadataOwnerPreservation()
    {
        string directory=Path.Combine(Path.GetTempPath(),"mysqlpunk_sqlite_review_metadata_"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            foreach(var policy in new[]{SqliteExistingObjectStrategy.SkipExisting,SqliteExistingObjectStrategy.OverwriteKeepExisting})
            using(var database=new SqliteFixtureDatabase())
            {
                database.Must("CREATE TABLE ai(id INTEGER PRIMARY KEY AUTOINCREMENT); INSERT INTO ai VALUES(1); INSERT INTO ai VALUES(100); DELETE FROM ai WHERE id=100; CREATE TABLE __mysqlpunk_column_comments(table_name TEXT NOT NULL,column_name TEXT NOT NULL,comment TEXT NOT NULL,PRIMARY KEY(table_name,column_name)); INSERT INTO __mysqlpunk_column_comments VALUES('ai','id','preserve'); INSERT INTO __mysqlpunk_column_comments VALUES('other','x','unrelated');");
                string sql="DELETE FROM sqlite_sequence; INSERT INTO sqlite_sequence(name,seq) VALUES('ai',1); DELETE FROM __mysqlpunk_column_comments; INSERT OR REPLACE INTO __mysqlpunk_column_comments VALUES('ai','id','overwrite'); CREATE TABLE new_ai(id INTEGER PRIMARY KEY AUTOINCREMENT); INSERT INTO new_ai VALUES(1); DELETE FROM sqlite_sequence WHERE name='new_ai'; INSERT INTO sqlite_sequence(name,seq) VALUES('new_ai',77); DELETE FROM sqlite_sequence WHERE name='new_ai' OR 1=1; CREATE TABLE new_t(x); INSERT OR REPLACE INTO __mysqlpunk_column_comments(table_name,column_name,comment) VALUES('new_t','x',CAST(X'6E6577' AS TEXT));";
                var result=SqliteRunFixture(database,directory,sql,SqliteSafeImportOptions(policy));
                Assert(result.Status==SqliteImportStatus.Completed,"Preserving policy handles mixed protected/new owner metadata");
                database.Must("INSERT INTO ai DEFAULT VALUES; INSERT INTO new_ai DEFAULT VALUES;");
                Assert(Convert.ToInt64(database.Scalar("SELECT max(id) FROM ai;"))==101,"Unscoped sequence DELETE cannot erase protected high-water mark under "+policy);
                Assert(Convert.ToInt64(database.Scalar("SELECT max(id) FROM new_ai;"))==78,"Exact single-owner sequence operations remain supported");
                AssertEquals("preserve",Convert.ToString(database.Scalar("SELECT comment FROM __mysqlpunk_column_comments WHERE table_name='ai';")),"Protected table comment remains");
                AssertEquals("unrelated",Convert.ToString(database.Scalar("SELECT comment FROM __mysqlpunk_column_comments WHERE table_name='other';")),"Unscoped sidecar DELETE cannot remove unrelated owners");
                AssertEquals("new",Convert.ToString(database.Scalar("SELECT comment FROM __mysqlpunk_column_comments WHERE table_name='new_t';")),"Exact single-owner native comment operation remains supported");
            }
        }
        finally { Directory.Delete(directory,true); }
    }
    private static System.Windows.Forms.Form SqliteCreateWizard(string typeName,params object[] arguments)
    {
        Type type=typeof(IDatabase).Assembly.GetType("mySQLPunk."+typeName,true);
        return (System.Windows.Forms.Form)Activator.CreateInstance(type,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic,null,arguments,null);
    }
    private static System.Reflection.FieldInfo SqliteWizardField(System.Windows.Forms.Form form,string name)
    {
        return form.GetType().GetField(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
    }
    private static void SqliteAssertRepeatedCloseBlocked(System.Windows.Forms.Form form,SqliteFixtureDatabase database)
    {
        using(var cancellation=new CancellationTokenSource())
        {
            SqliteWizardField(form,"busy").SetValue(form,true); SqliteWizardField(form,"cancellation").SetValue(form,cancellation);
            var method=typeof(System.Windows.Forms.Form).GetMethod("OnFormClosing",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
            for(int attempt=0;attempt<2;attempt++)
            {
                var closing=new System.Windows.Forms.FormClosingEventArgs(System.Windows.Forms.CloseReason.UserClosing,false);
                method.Invoke(form,new object[]{closing});
                Assert(closing.Cancel && !form.IsDisposed && database.State==ConnectionState.Open,"Every close attempt remains cancelled until worker cleanup completes");
            }
            Assert(cancellation.IsCancellationRequested,"First close requests cancellation");
            SqliteWizardField(form,"busy").SetValue(form,false);
            var completed=new System.Windows.Forms.FormClosingEventArgs(System.Windows.Forms.CloseReason.UserClosing,false); method.Invoke(form,new object[]{completed});
            Assert(!completed.Cancel,"Close is allowed once worker is no longer busy");
        }
    }
    private static void SqliteWaitWizardTask(Task task)
    {
        var timeout=System.Diagnostics.Stopwatch.StartNew();
        while(!task.IsCompleted)
        {
            Assert(timeout.Elapsed<TimeSpan.FromSeconds(10),"Wizard task must finish while UI messages are pumped");
            System.Windows.Forms.Application.DoEvents(); Thread.Sleep(1);
        }
        task.GetAwaiter().GetResult();
    }
    private static void SqliteImport_ReviewRegression_RepeatedCloseAndPreflight()
    {
        string directory=Path.Combine(Path.GetTempPath(),"mysqlpunk_sqlite_review_close_"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            string file=Path.Combine(directory,"preflight.sql"); File.WriteAllText(file,"SELECT 1;",new UTF8Encoding(false));
            using(var database=new SqliteFixtureDatabase())
            {
                using(var form=SqliteCreateWizard("SqliteImportWizardForm",database,"main",file)) SqliteAssertRepeatedCloseBlocked(form,database);
                using(var form=SqliteCreateWizard("SqliteImportWizardForm",database,"main",file))
                {
                    SqliteWizardField(form,"closing").SetValue(form,true);
                    var task=(Task)form.GetType().GetMethod("RunPreflightAsync",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(form,null);
                    SqliteWaitWizardTask(task);
                    Assert(form.IsDisposed,"Pending close is honored after preflight finally releases busy state");
                }
            }
        }
        finally { Directory.Delete(directory,true); }
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
            var result = new DataTable();
            if (sql.StartsWith("PRAGMA ", StringComparison.OrdinalIgnoreCase)) { result.Columns.Add("value",typeof(int)); result.Rows.Add(0); }
            else if (sql.Contains("SELECT type, name, tbl_name")) { result.Columns.Add("type"); result.Columns.Add("name"); result.Columns.Add("tbl_name"); foreach(string name in ExistingTables) result.Rows.Add("table",name,name); }
            return result;
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
            if (sql.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase))
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
#pragma warning disable 0108
    private sealed class SqliteFixtureDatabase : my_sqlite, IDatabase
    {
        private readonly SQLiteConnection connection;
        public string FailExecPrefix;
        public string FailQueryContains;
        public SqliteFixtureDatabase(string path = null) { connection = new SQLiteConnection(new SQLiteConnectionStringBuilder { DataSource=path ?? ":memory:", Version=3, Pooling=false }.ToString()); connection.Open(); MCT = connection; }
        public ConnectionState State { get { return connection.State; } }
        public string ProviderName { get { return "sqlite"; } }
        public void Dispose() { connection.Dispose(); }
        public void SetConn(string value) { throw new NotSupportedException(); }
        public void Open() { throw new NotSupportedException(); }
        public void Close() { throw new NotSupportedException(); }
        public object Scalar(string sql) { using(var cmd = new SQLiteCommand(sql, connection)) return cmd.ExecuteScalar(); }
        public void Must(string sql) { var r = ExecSQL(sql); if (r["status"] != "OK") throw new Exception(r["reason"]); }
        public DataTable SelectSQL(string sql, Dictionary<string,object> parameters=null)
        {
            var t = new DataTable();
            if (FailQueryContains != null && sql.Contains(FailQueryContains)) { t.ExtendedProperties["QueryError"] = "injected source read failure"; return t; }
            try { using(var cmd = new SQLiteCommand(sql, connection)) { Bind(cmd, parameters); using(var reader=cmd.ExecuteReader()) t.Load(reader); } }
            catch(Exception ex) { t.ExtendedProperties["QueryError"] = ex.Message; }
            return t;
        }
        public Dictionary<string,string> ExecSQL(string sql, Dictionary<string,object> parameters=null)
        {
            if (FailExecPrefix != null && sql.TrimStart().StartsWith(FailExecPrefix, StringComparison.OrdinalIgnoreCase))
                return new Dictionary<string,string>{{"status","NO"},{"reason","injected execution failure"}};
            try { using(var cmd = new SQLiteCommand(sql, connection)) { Bind(cmd, parameters); int n=cmd.ExecuteNonQuery(); return new Dictionary<string,string>{{"status","OK"},{"rowsAffected",n.ToString()}}; } }
            catch(Exception ex) { return new Dictionary<string,string>{{"status","NO"},{"reason",ex.Message}}; }
        }
        private static void Bind(SQLiteCommand cmd, Dictionary<string,object> p) { if(p!=null) foreach(var pair in p) cmd.Parameters.AddWithValue("@"+pair.Key,pair.Value); }
        private static string Q(string s) { return "\""+s.Replace("\"","\"\"")+"\""; }
        public bool Exists(string type,string name) { return Convert.ToInt64(Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='"+type+"' AND name='"+name.Replace("'","''")+"';"))>0; }
        public List<string> GetDatabases() { return new List<string>{"main"}; }
        private List<string> Names(string type) { return SelectSQL("SELECT name FROM sqlite_master WHERE type='"+type+"' AND name NOT LIKE 'sqlite_%';").Rows.Cast<DataRow>().Select(r=>r[0].ToString()).ToList(); }
        public List<string> GetTables(string name) { return Names("table").Where(x=>x!="__mysqlpunk_column_comments" && x!="__mysqlpunk_table_comments").ToList(); }
        public List<string> GetViews(string name) { return Names("view"); }
        public DataTable GetColumns(string db,string table)
        {
            var t=SelectSQL("PRAGMA table_info("+Q(table)+");"); t.Columns.Add("Comment",typeof(string));
            if(Exists("table","__mysqlpunk_column_comments")) foreach(DataRow r in t.Rows)
                r["Comment"]=Scalar("SELECT comment FROM __mysqlpunk_column_comments WHERE table_name='"+table.Replace("'","''")+"' AND column_name='"+r["name"].ToString().Replace("'","''")+"';")??DBNull.Value;
            return t;
        }
        public long CountRows(string db,string table) { var t=SelectSQL("SELECT COUNT(*) FROM "+Q(table)+";"); return t.ExtendedProperties.ContainsKey("QueryError")?-1:Convert.ToInt64(t.Rows[0][0]); }
        public DataTable SelectTablePage(string db,string table,long offset,int limit) { return SelectSQL("SELECT * FROM "+Q(table)+" LIMIT "+limit+" OFFSET "+offset+";"); }
        public bool TableExists(string db,string table) { return Exists("table",table); }
        public bool ViewExists(string db,string view) { return Exists("view",view); }
        public Task<DataTable> SelectSQLAsync(string s,Dictionary<string,object> p=null) { return Task.FromResult(SelectSQL(s,p)); }
        public Task<Dictionary<string,string>> ExecSQLAsync(string s,Dictionary<string,object> p=null) { return Task.FromResult(ExecSQL(s,p)); }
        public DataTable GetIndexes(string d,string t) { return new DataTable(); }
        public DataTable GetTableStatus(string d) { return new DataTable(); }
        public Dictionary<string,string> GetDatabaseInfo(string d) { return new Dictionary<string,string>(); }
        public string GetTableCreateStatement(string d,string t) { return ""; }
        public void RenameTable(string d,string a,string b) { throw new NotSupportedException(); }
        public void RenameView(string d,string a,string b) { throw new NotSupportedException(); }
        public DataTable GetCopyColumns(string d,string t) { return new DataTable(); }
        public DataTable GetCopyIndexes(string d,string t) { return new DataTable(); }
        public void CreateTableForCopy(string d,string t,DataTable c,string p) { throw new NotSupportedException(); }
        public void DropTableForCopy(string d,string t) { throw new NotSupportedException(); }
        public void CreateIndexesForCopy(string d,string t,DataTable i,string p) { throw new NotSupportedException(); }
        public void InsertTableBatch(string d,string t,DataTable r) { throw new NotSupportedException(); }
        public string GetViewCreateStatement(string d,string v) { return ""; }
        public void CreateViewFromStatement(string d,string v,string s) { throw new NotSupportedException(); }
    }

#pragma warning restore 0108

}
