using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using mySQLPunk.lib;

public static partial class SmokeTests
{
    /// <summary>
    /// Builds the same source／target pair used by the cross-platform live round trip, expressed as Windows
    /// schema snapshots: the target lacks extra_table, parent.note and two foreign keys, has a narrower
    /// nullable parent.code, and carries child.legacy, target_only and one extra foreign key.
    /// </summary>
    public static SchemaComparisonResult BuildSchemaSyncFixture(string provider)
    {
        string integer = provider == "mysql" ? "int(11)" : provider == "postgresql" ? "integer" : provider == "sqlite" ? "INTEGER" : "int";
        string code20 = provider == "postgresql" ? "character varying(20)" : provider == "sqlite" ? "TEXT" : "varchar(20)";
        string code10 = provider == "postgresql" ? "character varying(10)" : provider == "sqlite" ? "VARCHAR(10)" : "varchar(10)";
        string text50 = provider == "postgresql" ? "character varying(50)" : provider == "sqlite" ? "TEXT" : "varchar(50)";
        string money = provider == "postgresql" ? "numeric(10,2)" : provider == "sqlite" ? "REAL" : "decimal(10,2)";

        SchemaModelSnapshot source = new SchemaModelSnapshot { DatabaseName = "sync_source", ProviderName = provider };
        source.Tables.Add(SyncTable("parent",
            SyncColumn("id", integer, false, true, 1), SyncColumn("code", code20, false, false, 2), SyncColumn("note", text50, true, false, 3)));
        source.Tables.Add(SyncTable("child",
            SyncColumn("id", integer, false, true, 1), SyncColumn("parent_id", integer, false, false, 2), SyncColumn("amount", money, false, false, 3)));
        source.Tables.Add(SyncTable("extra_table",
            SyncColumn("id", integer, false, true, 1), SyncColumn("label", text50, true, false, 2), SyncColumn("parent_id", integer, true, false, 3)));
        source.Relationships.Add(new SchemaRelationshipModel { Name = "fk_child_parent", FromTable = "child", FromColumn = "parent_id", ToTable = "parent", ToColumn = "id", Ordinal = 1 });
        source.Relationships.Add(new SchemaRelationshipModel { Name = "fk_extra_parent", FromTable = "extra_table", FromColumn = "parent_id", ToTable = "parent", ToColumn = "id", Ordinal = 1 });

        SchemaModelSnapshot target = new SchemaModelSnapshot { DatabaseName = "sync_target", ProviderName = provider };
        target.Tables.Add(SyncTable("parent", SyncColumn("id", integer, false, true, 1), SyncColumn("code", code10, true, false, 2)));
        target.Tables.Add(SyncTable("child",
            SyncColumn("id", integer, false, true, 1), SyncColumn("parent_id", integer, false, false, 2), SyncColumn("amount", money, false, false, 3),
            SyncColumn("legacy", integer, true, false, 4)));
        target.Tables.Add(SyncTable("target_only", SyncColumn("id", integer, false, true, 1)));
        target.Relationships.Add(new SchemaRelationshipModel { Name = "fk_child_legacy", FromTable = "child", FromColumn = "legacy", ToTable = "target_only", ToColumn = "id", Ordinal = 1 });
        return SchemaComparisonService.Compare(source, target);
    }

    private static SchemaTableModel SyncTable(string name, params SchemaColumnModel[] columns)
    {
        SchemaTableModel table = new SchemaTableModel { Name = name };
        table.Columns.AddRange(columns);
        return table;
    }

    private static SchemaColumnModel SyncColumn(string name, string type, bool nullable, bool primaryKey, int ordinal)
    {
        return new SchemaColumnModel { Name = name, DataType = type, IsNullable = nullable, IsPrimaryKey = primaryKey, Ordinal = ordinal };
    }

    private static void TestSchemaSyncScript()
    {
        DateTime fixedTime = new DateTime(2026, 9, 25, 12, 0, 0);

        SchemaSyncScript postgres = SchemaSyncScriptService.Generate(BuildSchemaSyncFixture("postgresql"), fixedTime);
        AssertStatement(postgres, "CREATE TABLE \"extra_table\" (\n    \"id\" integer NOT NULL,\n    \"label\" character varying(50) NULL,\n    \"parent_id\" integer NULL,\n    PRIMARY KEY (\"id\")\n)");
        AssertStatement(postgres, "ALTER TABLE \"parent\" ADD COLUMN \"note\" character varying(50) NULL");
        AssertStatement(postgres, "ALTER TABLE \"parent\" ALTER COLUMN \"code\" TYPE character varying(20)");
        AssertStatement(postgres, "ALTER TABLE \"parent\" ALTER COLUMN \"code\" SET NOT NULL");
        AssertStatement(postgres, "ALTER TABLE \"child\" ADD CONSTRAINT \"fk_child_parent\" FOREIGN KEY (\"parent_id\") REFERENCES \"parent\" (\"id\")");
        AssertStatement(postgres, "ALTER TABLE \"extra_table\" ADD CONSTRAINT \"fk_extra_parent\" FOREIGN KEY (\"parent_id\") REFERENCES \"parent\" (\"id\")");
        AssertEquals("6", postgres.Statements.Count.ToString(), "PostgreSQL sync should emit exactly the additive and alter statements.");
        AssertEquals("3", postgres.DestructiveItems.Count.ToString(), "Dropping target-only table, column and foreign key must be destructive items.");
        AssertContains(postgres.Text, "-- DROP TABLE \"target_only\";", "Target-only tables must only appear commented out.");
        AssertContains(postgres.Text, "-- ALTER TABLE \"child\" DROP COLUMN \"legacy\";", "Target-only columns must only appear commented out.");
        AssertContains(postgres.Text, "-- ALTER TABLE \"child\" DROP CONSTRAINT \"fk_child_legacy\";", "Target-only foreign keys must only appear commented out.");
        Assert(!postgres.Statements.Any(item => item.IndexOf("DROP", StringComparison.OrdinalIgnoreCase) >= 0),
            "No executable statement may drop anything.");
        Assert(postgres.Text.IndexOf("CREATE TABLE \"extra_table\"", StringComparison.Ordinal) <
               postgres.Text.IndexOf("ADD CONSTRAINT \"fk_extra_parent\"", StringComparison.Ordinal),
            "Foreign keys must be added after the tables they reference are created.");
        AssertContains(postgres.Text, "2026-09-25 12:00:00", "The script header should record the generation time.");

        SchemaSyncScript sqlServer = SchemaSyncScriptService.Generate(BuildSchemaSyncFixture("mssql"), fixedTime);
        AssertStatement(sqlServer, "ALTER TABLE [parent] ADD [note] varchar(50) NULL");
        AssertStatement(sqlServer, "ALTER TABLE [parent] ALTER COLUMN [code] varchar(20) NOT NULL");
        AssertEquals("1", sqlServer.Statements.Count(item => item.IndexOf("ALTER COLUMN [code]", StringComparison.Ordinal) >= 0).ToString(),
            "A type and nullability change on one SQL Server column should produce a single ALTER COLUMN.");
        AssertContains(sqlServer.Text.Replace("\r\n", "\n"), "\nGO\n", "SQL Server scripts should separate batches with GO.");
        AssertContains(sqlServer.Text, "-- ALTER TABLE [child] DROP CONSTRAINT [fk_child_legacy];", "SQL Server foreign-key drops must stay commented.");

        SchemaSyncScript mysql = SchemaSyncScriptService.Generate(BuildSchemaSyncFixture("mysql"), fixedTime);
        AssertStatement(mysql, "ALTER TABLE `parent` ADD COLUMN `note` varchar(50) NULL");
        Assert(!mysql.Statements.Any(item => item.IndexOf("MODIFY", StringComparison.OrdinalIgnoreCase) >= 0),
            "MySQL MODIFY would wipe defaults and AUTO_INCREMENT, so it must only be suggested as a manual item.");
        AssertEquals("1", mysql.ManualItems.Count(item => item.IndexOf("MODIFY COLUMN `code` varchar(20) NOT NULL", StringComparison.Ordinal) >= 0).ToString(),
            "MySQL column changes should appear once as a manual suggestion.");
        AssertContains(mysql.Text, "-- ALTER TABLE `child` DROP FOREIGN KEY `fk_child_legacy`;", "MySQL uses DROP FOREIGN KEY, commented out.");

        SchemaSyncScript sqlite = SchemaSyncScriptService.Generate(BuildSchemaSyncFixture("sqlite"), fixedTime);
        Assert(!sqlite.Statements.Any(item => item.IndexOf("FOREIGN KEY", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                              item.IndexOf("ALTER COLUMN", StringComparison.OrdinalIgnoreCase) >= 0),
            "SQLite cannot add foreign keys or alter columns; those must be manual items.");
        Assert(sqlite.ManualItems.Any(item => item.IndexOf("fk_extra_parent", StringComparison.Ordinal) >= 0),
            "SQLite foreign keys should be listed for manual rebuild.");

        SchemaModelSnapshot hostileSource = new SchemaModelSnapshot { DatabaseName = "evil\nDROP TABLE header_escape;", ProviderName = "postgresql" };
        hostileSource.Tables.Add(SyncTable("keep", SyncColumn("id", "integer", false, true, 1)));
        SchemaModelSnapshot hostileTarget = new SchemaModelSnapshot { DatabaseName = "b", ProviderName = "postgresql" };
        hostileTarget.Tables.Add(SyncTable("keep", SyncColumn("id", "integer", false, true, 1)));
        hostileTarget.Tables.Add(SyncTable("x\nDROP TABLE victim;\r\n--", SyncColumn("id", "integer", false, true, 1)));
        SchemaSyncScript hostile = SchemaSyncScriptService.Generate(SchemaComparisonService.Compare(hostileSource, hostileTarget), fixedTime);
        string[] executable = hostile.Text.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0 && !line.StartsWith("--", StringComparison.Ordinal) && line != "GO")
            .ToArray();
        AssertEquals("0", executable.Length.ToString(), "Names containing line breaks must not escape SQL comments: " + hostile.Text);

        string reason;
        Assert(!SchemaSyncScriptService.CanGenerate(SchemaComparisonService.Compare(
            new SchemaModelSnapshot { DatabaseName = "a", ProviderName = "mysql" },
            new SchemaModelSnapshot { DatabaseName = "b", ProviderName = "postgresql" }), out reason),
            "Cross-provider comparisons must not produce a sync script.");
        Assert(!SchemaSyncScriptService.CanGenerate(SchemaComparisonService.Compare(
            new SchemaModelSnapshot { DatabaseName = "a", ProviderName = "oracle" },
            new SchemaModelSnapshot { DatabaseName = "b", ProviderName = "oracle" }), out reason),
            "Unsupported providers must not produce a sync script.");
        AssertThrows<InvalidOperationException>(() => SchemaSyncScriptService.Generate(SchemaComparisonService.Compare(
            new SchemaModelSnapshot { DatabaseName = "a", ProviderName = "mysql" },
            new SchemaModelSnapshot { DatabaseName = "b", ProviderName = "mysql" })),
            "Identical schemas have nothing to synchronize.");
    }

    /// <summary>
    /// Transaction semantics of the execution core on an open SQLite connection: success commits, a failure in
    /// statement 2 rolls statement 1 back and leaves statement 3 unrun, and the non-transactional mode (MySQL
    /// behaviour) reports statement 1 as applied.
    /// </summary>
    public static void AssertSchemaSyncExecutionSemantics(DbConnection connection)
    {
        Func<string, bool> tableExists = name =>
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '" + name + "'";
                return Convert.ToInt64(command.ExecuteScalar()) > 0;
            }
        };

        SchemaSyncBatchResult ok = SchemaSyncExecutionService.ExecuteOnConnection(connection, true, new List<string>
        {
            "CREATE TABLE sync_ok (id INTEGER PRIMARY KEY)",
            "ALTER TABLE sync_ok ADD COLUMN note TEXT NULL"
        });
        Assert(ok.Succeeded && ok.UsedTransaction && ok.SucceededCount == 2 && tableExists("sync_ok"),
            "A successful transactional batch should commit every statement: " + ok.Summary);

        SchemaSyncBatchResult rolledBack = SchemaSyncExecutionService.ExecuteOnConnection(connection, true, new List<string>
        {
            "CREATE TABLE sync_rollback (id INTEGER PRIMARY KEY)",
            "ALTER TABLE sync_missing_table ADD COLUMN x INTEGER",
            "CREATE TABLE sync_never (id INTEGER PRIMARY KEY)"
        });
        Assert(!rolledBack.Succeeded && rolledBack.Failure != null && rolledBack.Failure.Index == 1,
            "The batch should stop at the failing statement: " + rolledBack.Summary);
        Assert(rolledBack.Statements[0].Outcome == SchemaSyncStatementOutcome.RolledBack &&
               rolledBack.Statements[2].Outcome == SchemaSyncStatementOutcome.NotRun &&
               !tableExists("sync_rollback") && !tableExists("sync_never"),
            "A failed transactional batch must roll back earlier statements and skip later ones.");

        SchemaSyncBatchResult partial = SchemaSyncExecutionService.ExecuteOnConnection(connection, false, new List<string>
        {
            "CREATE TABLE sync_partial (id INTEGER PRIMARY KEY)",
            "ALTER TABLE sync_missing_table ADD COLUMN x INTEGER"
        });
        Assert(!partial.UsedTransaction && partial.Statements[0].Outcome == SchemaSyncStatementOutcome.Succeeded &&
               tableExists("sync_partial") && partial.SucceededCount == 1,
            "Without a transaction the result must report which statements were already applied: " + partial.Summary);

        AssertThrows<InvalidOperationException>(() => SchemaSyncExecutionService.ExecuteOnConnection(connection, true, new List<string>()),
            "An empty batch must be rejected.");
        AssertThrows<InvalidOperationException>(() => SchemaSyncExecutionService.ExecuteOnConnection(connection, true, new List<string> { "  " }),
            "A blank statement must be rejected.");

        SchemaSyncScript script = SchemaSyncScriptService.Generate(BuildSchemaSyncFixture("postgresql"));
        AssertEquals(script.DestructiveItems.Count.ToString(), script.DestructiveStatements.Count.ToString(),
            "Every destructive description should keep its SQL for optional execution.");
        Assert(script.DestructiveStatements.All(sql => sql.StartsWith("DROP", StringComparison.Ordinal) || sql.IndexOf(" DROP ", StringComparison.Ordinal) > 0) &&
               !script.Statements.Intersect(script.DestructiveStatements).Any(),
            "Destructive statements must stay separate from the default executable statements.");
    }

    /// <summary>
    /// DataSyncCore on one SQLite connection holding src_* and dst_* tables: compare, apply in dependency order
    /// (deletes children first), rollback on a concurrent change, and value equality rules.
    /// </summary>
    public static void AssertDataSyncCoreSemantics(DbConnection connection)
    {
        Action<string> exec = sql =>
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        };
        Func<string, System.Data.DataTable> read = sql =>
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = sql;
                System.Data.DataTable table = new System.Data.DataTable();
                using (DbDataReader reader = command.ExecuteReader()) table.Load(reader);
                return table;
            }
        };

        exec("PRAGMA foreign_keys = ON");
        exec("CREATE TABLE parent (id INTEGER PRIMARY KEY, name TEXT NOT NULL, payload BLOB NULL)");
        exec("CREATE TABLE child (id INTEGER PRIMARY KEY, parent_id INTEGER NOT NULL REFERENCES parent(id), note TEXT NULL)");
        exec("INSERT INTO parent VALUES (1, 'same', X'00FF'), (2, 'old name', NULL), (4, 'target only', NULL)");
        exec("INSERT INTO child VALUES (12, 4, 'orphan')");

        System.Data.DataTable sourceParent = read("SELECT 1 AS id, 'same' AS name, X'00FF' AS payload UNION ALL SELECT 2, 'renamed ''quote''', NULL UNION ALL SELECT 3, 'new' || char(10) || 'line', X'01'");
        System.Data.DataTable sourceChild = read("SELECT 10 AS id, 1 AS parent_id, 'a' AS note UNION ALL SELECT 11, 3, NULL");
        Func<List<DataTableComparison>> compare = () => new List<DataTableComparison>
        {
            DataSyncCore.Compare("parent", sourceParent, read("SELECT id, name, payload FROM parent"), new List<string> { "id" }, null),
            DataSyncCore.Compare("child", sourceChild, read("SELECT id, parent_id, note FROM child"), new List<string> { "id" }, null)
        };

        List<DataTableComparison> comparisons = compare();
        Assert(comparisons[0].Inserts == 1 && comparisons[0].Updates == 1 && comparisons[0].Deletes == 1 && comparisons[0].IdenticalRows == 1,
            "parent should need one insert, update and delete: " + comparisons[0].Inserts + "/" + comparisons[0].Updates + "/" + comparisons[0].Deletes);
        Assert(comparisons[0].Changes.Single(item => item.Kind == DataRowChangeKind.Update).Values.Keys.SequenceEqual(new[] { "name" }),
            "Updates should only carry the columns that differ.");
        Assert(comparisons[1].Inserts == 2 && comparisons[1].Deletes == 1, "child should need two inserts and one delete.");

        Func<string, string> quote = name => "\"" + name.Replace("\"", "\"\"") + "\"";
        Func<List<DataTableComparison>, List<DataSyncTableRequest>> requests = items => items.Select(item => new DataSyncTableRequest
        {
            TableName = item.TableName,
            KeyColumns = item.KeyColumns,
            Changes = item.Changes,
            AfterInsertStatements = new List<string>()
        }).ToList();

        exec("UPDATE parent SET name = 'changed behind' WHERE id = 2");
        DataSyncResult stale = DataSyncCore.Apply(connection, requests(comparisons), quote, quote, "@", null);
        Assert(!stale.Succeeded && stale.FailedTable == "parent", "A row changed after comparing must fail the whole batch: " + stale.Message);
        Assert(read("SELECT id FROM child").Rows.Count == 1 && read("SELECT id FROM parent").Rows.Count == 3,
            "A failed batch must roll back every earlier delete and insert.");

        DataSyncResult ok = DataSyncCore.Apply(connection, requests(compare()), quote, quote, "@", null);
        Assert(ok.Succeeded && ok.Inserted == 3 && ok.Updated == 1 && ok.Deleted == 2, "Sync should succeed: " + ok.Message);
        List<DataTableComparison> after = compare();
        Assert(after.All(item => item.Changes.Count == 0), "After syncing, a new comparison should find no differences.");

        string preview = DataSyncCore.BuildPreviewSql(DataSyncCore.Compare("tags",
            read("SELECT 'k1' AS code"), read("SELECT 'x' || char(10) || 'DELETE FROM parent;' AS code"), new List<string> { "code" }, null),
            quote, quote, false);
        Assert(preview.Split('\n').Where(line => line.Trim().Length > 0 && !line.StartsWith("INSERT", StringComparison.Ordinal))
                   .All(line => line.StartsWith("--", StringComparison.Ordinal)),
            "A commented DELETE must stay on one line even when the key contains a newline: " + preview);

        Assert(DataSyncCore.ValuesEqual(1, 1L) && DataSyncCore.ValuesEqual(1.50m, 1.5m) && DataSyncCore.ValuesEqual(DBNull.Value, null) &&
               DataSyncCore.ValuesEqual(new byte[] { 1, 2 }, new byte[] { 1, 2 }) && !DataSyncCore.ValuesEqual(new byte[] { 1 }, new byte[] { 2 }) &&
               !DataSyncCore.ValuesEqual("a", "A") && !DataSyncCore.ValuesEqual(DBNull.Value, 0),
            "Value equality should treat numbers numerically, bytes by content, NULLs alike, and text case-sensitively.");
        Assert(DataSyncCore.Compare("x", read("SELECT 1 AS id"), read("SELECT 1 AS id UNION ALL SELECT 1"), new List<string> { "id" }, null).IsSkipped,
            "Duplicate key values must skip the table rather than guess.");
        Assert(DataSyncCore.Compare("x", read("SELECT 1 AS id"), read("SELECT 1 AS id"), new List<string>(), null).IsSkipped,
            "Tables without a primary key must be skipped.");
    }

    private static void AssertStatement(SchemaSyncScript script, string expected)
    {
        Assert(script.Statements.Contains(expected),
            "Expected statement not generated: " + expected + Environment.NewLine + "Script:" + Environment.NewLine + script.Text);
    }
}
