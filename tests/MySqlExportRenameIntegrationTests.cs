using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using mySQLPunk.lib;

internal static class MySqlExportRenameIntegrationTests
{
    private const string SourceDatabase = "mysqlpunk_export_it";
    private const string RenamedDatabase = "mysqlpunk_rename_it";

    private static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: MySqlExportRenameIntegrationTests <port> <label> <password>");
            return 2;
        }

        bool preserveExport = args.Length >= 4 && !string.IsNullOrWhiteSpace(args[3]);
        string exportPath = preserveExport
            ? Path.GetFullPath(args[3])
            : Path.Combine(Path.GetTempPath(), "mysqlpunk-export-it-" + Guid.NewGuid().ToString("N") + ".sql");
        try
        {
            int port = int.Parse(args[0]);
            using (my_mysql db = ConnectWithRetry(port, args[2]))
            {
                Cleanup(db);
                try
                {
                    CreateFixture(db);
                    VerifyStreamingExportAndRoundTrip(db, exportPath);
                    VerifyExistingObjectStrategies(db, exportPath);
                    VerifyCopyBasedRename(db);
                }
                finally
                {
                    Cleanup(db);
                }
            }

            Console.WriteLine("[PASS] " + args[1] + " live export/import and rename integration");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[FAIL] " + ex);
            return 1;
        }
        finally
        {
            if (!preserveExport && File.Exists(exportPath)) File.Delete(exportPath);
        }
    }

    private static void VerifyStreamingExportAndRoundTrip(IDatabase db, string exportPath)
    {
        List<string> progress = new List<string>();
        MySqlExportResult export = MySqlExportService.WriteExportToFile(
            db,
            SourceDatabase,
            new MySqlExportOptions
            {
                IncludeCreateDatabase = true,
                IncludeUseDatabase = true,
                IncludeDropStatements = true,
                DisableForeignKeyChecks = true,
                RemoveDefiner = true,
                InsertBatchSize = 1
            },
            exportPath,
            message => progress.Add(message));

        Assert(export.Sql == null, "Streaming export retained the whole SQL script in memory.");
        Assert(export.TableCount == 2 && export.ViewCount == 1 && export.RoutineCount == 2 && export.TriggerCount == 1, "Export object counts were incomplete.");
        Assert(export.RowCount == 4, "Export row count was incorrect.");
        Assert(progress.Count >= 8, "Streaming export did not report object and row progress.");

        byte[] bytes = File.ReadAllBytes(exportPath);
        Assert(bytes.Length > 3 && !(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF), "Export file must be UTF-8 without BOM.");
        string sql = File.ReadAllText(exportPath, Encoding.UTF8);
        AssertContains(sql, "DELIMITER ;;", "Routine/trigger delimiter was missing.");
        AssertNotContains(sql, "DEFINER=", "Export should remove DEFINER by default.");
        AssertContains(sql, "0x00AABBFF", "BLOB data was not exported as a hex literal.");
        AssertContains(sql, "O\\'Reilly", "Quoted text was not escaped for MySQL import.");

        EnsureExec(db, "DROP DATABASE " + QuoteIdentifier(SourceDatabase) + ";");
        MySqlImportResult imported = MySqlImportService.Execute(db, exportPath, new MySqlImportOptions(), null);
        Assert(imported.FailedStatements == 0, "Round-trip import reported a failed statement.");
        VerifyFixture(db, SourceDatabase);

        MySqlExportOptions selected = new MySqlExportOptions
        {
            IncludeCreateDatabase = false,
            IncludeUseDatabase = false,
            IncludeViews = false,
            IncludeRoutines = false,
            IncludeTriggers = false,
            SelectedTables = new[] { "parent_items" }
        };
        string selectedSql = MySqlExportService.BuildExportSql(db, SourceDatabase, selected);
        AssertContains(selectedSql, "CREATE TABLE `parent_items`", "Selected table was not exported.");
        AssertNotContains(selectedSql, "CREATE TABLE `child_items`", "Unchecked table was exported.");
    }

    private static void VerifyExistingObjectStrategies(IDatabase db, string exportPath)
    {
        MySqlImportResult skipped = MySqlImportService.Execute(
            db,
            exportPath,
            new MySqlImportOptions { ExistingObjectStrategy = MySqlExistingObjectStrategy.SkipExisting },
            null);
        Assert(skipped.FailedStatements == 0 && skipped.SkippedStatements >= 7, "Skip-existing strategy did not skip existing objects and data.");
        Assert(ScalarLong(db, "SELECT COUNT(*) FROM " + QuoteIdentifier(SourceDatabase) + ".`parent_items`;") == 2, "Skip-existing strategy duplicated table data.");

        MySqlImportResult recreated = MySqlImportService.Execute(
            db,
            exportPath,
            new MySqlImportOptions { ExistingObjectStrategy = MySqlExistingObjectStrategy.DropAndRecreate },
            null);
        Assert(recreated.FailedStatements == 0, "Drop-and-recreate strategy failed.");
        VerifyFixture(db, SourceDatabase);
    }

    private static void VerifyCopyBasedRename(IDatabase db)
    {
        DatabaseRenameResult renamed = DatabaseRenameService.Rename(
            db,
            SourceDatabase,
            RenamedDatabase,
            new DatabaseRenameOptions { BatchSize = 1 },
            null);

        Assert(renamed.OldDatabaseRetained, "MySQL copy-based rename must retain the source database.");
        Assert(renamed.TablesCopied == 2 && renamed.ViewsCopied == 1 && renamed.RoutinesCopied == 2 && renamed.TriggersCopied == 1, "Copy-based rename did not copy every object type.");
        Assert(db.GetDatabases().Any(name => string.Equals(name, SourceDatabase, StringComparison.OrdinalIgnoreCase)), "Copy-based rename removed the source database.");
        Assert(db.GetDatabases().Any(name => string.Equals(name, RenamedDatabase, StringComparison.OrdinalIgnoreCase)), "Copy-based rename did not create the target database.");
        VerifyFixture(db, RenamedDatabase);
    }

    private static void CreateFixture(IDatabase db)
    {
        EnsureExec(db, "CREATE DATABASE " + QuoteIdentifier(SourceDatabase) + " DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;");
        EnsureExec(db, "CREATE TABLE " + QuoteIdentifier(SourceDatabase) + ".`parent_items` (" +
            "`id` INT NOT NULL AUTO_INCREMENT COMMENT '編號'," +
            "`code` VARCHAR(40) NOT NULL COMMENT '代碼'," +
            "`note` TEXT NULL COMMENT '內容'," +
            "`payload` BLOB NULL," +
            "`amount` DECIMAL(12,2) NOT NULL DEFAULT 0," +
            "`created_at` DATETIME NOT NULL," +
            "PRIMARY KEY (`id`), UNIQUE KEY `uq_parent_code` (`code`), KEY `idx_parent_created` (`created_at`)" +
            ") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='主資料表';");
        EnsureExec(db, "CREATE TABLE " + QuoteIdentifier(SourceDatabase) + ".`child_items` (" +
            "`id` INT NOT NULL AUTO_INCREMENT," +
            "`parent_id` INT NOT NULL," +
            "`note` VARCHAR(100) NULL," +
            "PRIMARY KEY (`id`), KEY `idx_child_parent` (`parent_id`)," +
            "CONSTRAINT `fk_child_parent` FOREIGN KEY (`parent_id`) REFERENCES `parent_items` (`id`)" +
            ") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;");
        EnsureExec(db, "INSERT INTO " + QuoteIdentifier(SourceDatabase) + ".`parent_items` (`code`,`note`,`payload`,`amount`,`created_at`) VALUES " +
            "('A-001','O\\'Reilly\\\\path\\n中文 😀',0x00AABBFF,1234.50,'2026-08-20 10:20:30')," +
            "('A-002',NULL,NULL,0.01,'2026-08-20 11:22:33');");
        EnsureExec(db, "INSERT INTO " + QuoteIdentifier(SourceDatabase) + ".`child_items` (`parent_id`,`note`) VALUES (1,' first '),(2,NULL);");
        EnsureExec(db, "CREATE VIEW " + QuoteIdentifier(SourceDatabase) + ".`v_parent_items` AS SELECT `id`,`code`,`note` FROM " + QuoteIdentifier(SourceDatabase) + ".`parent_items`;");
        EnsureExec(db, "CREATE FUNCTION " + QuoteIdentifier(SourceDatabase) + ".`fn_item_label`(`p_id` INT) RETURNS VARCHAR(40) DETERMINISTIC RETURN CONCAT('item-', `p_id`);");
        EnsureExec(db, "CREATE PROCEDURE " + QuoteIdentifier(SourceDatabase) + ".`sp_parent_count`() SELECT COUNT(*) AS total FROM " + QuoteIdentifier(SourceDatabase) + ".`parent_items`;");
        EnsureExec(db, "CREATE TRIGGER " + QuoteIdentifier(SourceDatabase) + ".`trg_child_trim` BEFORE INSERT ON " + QuoteIdentifier(SourceDatabase) + ".`child_items` FOR EACH ROW SET NEW.`note` = TRIM(NEW.`note`);");
    }

    private static void VerifyFixture(IDatabase db, string databaseName)
    {
        Assert(db.TableExists(databaseName, "parent_items") && db.TableExists(databaseName, "child_items"), "Round-trip tables are missing in " + databaseName + ".");
        Assert(db.ViewExists(databaseName, "v_parent_items"), "Round-trip view is missing in " + databaseName + ".");
        Assert(ScalarLong(db, "SELECT COUNT(*) FROM " + QuoteIdentifier(databaseName) + ".`parent_items`;") == 2, "Parent row count changed in " + databaseName + ".");
        Assert(ScalarLong(db, "SELECT COUNT(*) FROM " + QuoteIdentifier(databaseName) + ".`child_items`;") == 2, "Child row count changed in " + databaseName + ".");

        DataTable row = db.SelectSQL("SELECT `note`, HEX(`payload`) AS payload_hex FROM " + QuoteIdentifier(databaseName) + ".`parent_items` WHERE `code`='A-001';");
        Assert(row.Rows.Count == 1 && Convert.ToString(row.Rows[0]["note"]) == "O'Reilly\\path\n中文 😀", "UTF-8 or escaped text changed in " + databaseName + ".");
        Assert(Convert.ToString(row.Rows[0]["payload_hex"]) == "00AABBFF", "BLOB data changed in " + databaseName + ".");
        Assert(ScalarString(db, "SELECT " + QuoteIdentifier(databaseName) + ".`fn_item_label`(7);") == "item-7", "Function did not round-trip in " + databaseName + ".");

        DataTable routineCount = db.SelectSQL(
            "SELECT COUNT(*) FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA='" + databaseName + "' AND ROUTINE_NAME IN ('fn_item_label','sp_parent_count');");
        Assert(Convert.ToInt32(routineCount.Rows[0][0]) == 2, "Function/procedure did not round-trip in " + databaseName + ".");
        Assert(ScalarLong(db, "SELECT COUNT(*) FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA='" + databaseName + "' AND TRIGGER_NAME='trg_child_trim';") == 1, "Trigger did not round-trip in " + databaseName + ".");

        string parentDdl = db.GetTableCreateStatement(databaseName, "parent_items");
        string childDdl = db.GetTableCreateStatement(databaseName, "child_items");
        AssertContains(parentDdl, "uq_parent_code", "Unique index was not preserved.");
        AssertContains(parentDdl, "idx_parent_created", "Normal index was not preserved.");
        AssertContains(parentDdl, "主資料表", "Table comment was not preserved.");
        AssertContains(parentDdl, "編號", "Column comment was not preserved.");
        AssertContains(childDdl, "fk_child_parent", "Foreign key was not preserved.");

        EnsureExec(db, "INSERT INTO " + QuoteIdentifier(databaseName) + ".`child_items` (`parent_id`,`note`) VALUES (1,'  trigger check  ');");
        Assert(ScalarString(db, "SELECT `note` FROM " + QuoteIdentifier(databaseName) + ".`child_items` ORDER BY `id` DESC LIMIT 1;") == "trigger check", "Trigger behavior was not preserved.");
        EnsureExec(db, "DELETE FROM " + QuoteIdentifier(databaseName) + ".`child_items` WHERE `note`='trigger check';");
    }

    private static my_mysql ConnectWithRetry(int port, string password)
    {
        Exception last = null;
        for (int attempt = 0; attempt < 300; attempt++)
        {
            my_mysql db = new my_mysql();
            try
            {
                db.SetConn("Server=127.0.0.1;Port=" + port + ";User ID=root;Password=" + password + ";SslMode=None;AllowPublicKeyRetrieval=True;");
                db.Open();
                return db;
            }
            catch (Exception ex)
            {
                last = ex;
                db.Dispose();
                Thread.Sleep(1000);
            }
        }
        throw new InvalidOperationException("Database container did not become ready.", last);
    }

    private static void Cleanup(IDatabase db)
    {
        EnsureExec(db, "DROP DATABASE IF EXISTS " + QuoteIdentifier(RenamedDatabase) + ";", true);
        EnsureExec(db, "DROP DATABASE IF EXISTS " + QuoteIdentifier(SourceDatabase) + ";", true);
    }

    private static long ScalarLong(IDatabase db, string sql)
    {
        DataTable table = db.SelectSQL(sql);
        if (table == null || table.Rows.Count == 0 || table.Columns.Count == 0) throw new InvalidOperationException("Scalar query returned no value: " + sql);
        return Convert.ToInt64(table.Rows[0][0]);
    }

    private static string ScalarString(IDatabase db, string sql)
    {
        DataTable table = db.SelectSQL(sql);
        if (table == null || table.Rows.Count == 0 || table.Columns.Count == 0) throw new InvalidOperationException("Scalar query returned no value: " + sql);
        return Convert.ToString(table.Rows[0][0]);
    }

    private static void EnsureExec(IDatabase db, string sql, bool ignoreFailure = false)
    {
        Dictionary<string, string> result = db.ExecSQL(sql);
        string status = GetValue(result, "status");
        if (!ignoreFailure && !string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(GetValue(result, "reason") + "\nSQL: " + sql);
    }

    private static string GetValue(Dictionary<string, string> values, string key)
    {
        if (values == null) return string.Empty;
        foreach (KeyValuePair<string, string> pair in values)
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) return pair.Value ?? string.Empty;
        return string.Empty;
    }

    private static string QuoteIdentifier(string value)
    {
        return "`" + (value ?? string.Empty).Replace("`", "``") + "`";
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertContains(string value, string expected, string message)
    {
        if ((value ?? string.Empty).IndexOf(expected, StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException(message + " Missing: " + expected);
    }

    private static void AssertNotContains(string value, string unexpected, string message)
    {
        if ((value ?? string.Empty).IndexOf(unexpected, StringComparison.OrdinalIgnoreCase) >= 0)
            throw new InvalidOperationException(message + " Unexpected: " + unexpected);
    }
}
