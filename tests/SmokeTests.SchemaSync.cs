using System;
using System.Collections.Generic;
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
        AssertContains(sqlServer.Text, "\nGO\n", "SQL Server scripts should separate batches with GO.");
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

    private static void AssertStatement(SchemaSyncScript script, string expected)
    {
        Assert(script.Statements.Contains(expected),
            "Expected statement not generated: " + expected + Environment.NewLine + "Script:" + Environment.NewLine + script.Text);
    }
}
