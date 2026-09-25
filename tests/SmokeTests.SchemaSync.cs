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
    /// QueryBuilderService: provider-specific SQL for joins, aggregates, HAVING, ordering and row limits; value
    /// escaping; SQL → model → SQL round trips; and explicit rejection of SQL the builder cannot represent.
    /// </summary>
    public static void AssertQueryBuilderSemantics()
    {
        Func<QueryBuilderModel> sample = () =>
        {
            QueryBuilderModel model = new QueryBuilderModel { Limit = 10 };
            model.Tables.Add(new QueryBuilderTable { Name = "customers", Alias = "c" });
            model.Tables.Add(new QueryBuilderTable { Name = "orders", Alias = "o" });
            model.Joins.Add(new QueryBuilderJoin { LeftAlias = "c", LeftColumn = "id", RightAlias = "o", RightColumn = "customer_id", Type = QueryJoinType.Left });
            model.Columns.Add(new QueryBuilderColumn { TableAlias = "c", Column = "name" });
            model.Columns.Add(new QueryBuilderColumn { TableAlias = "o", Column = "id", Aggregate = QueryAggregate.Count, Alias = "order_count", Sort = QuerySort.Descending });
            model.Conditions.Add(new QueryBuilderCondition { TableAlias = "o", Column = "status", Operator = "IN", Value = "paid, new" });
            model.Conditions.Add(new QueryBuilderCondition { Connector = "OR", TableAlias = "c", Column = "city", Operator = "=", Value = "O'Brien" });
            model.Conditions.Add(new QueryBuilderCondition { TableAlias = "o", Column = "id", Aggregate = QueryAggregate.Count, Operator = ">", Value = "1" });
            return model;
        };

        string nl = Environment.NewLine;
        AssertEquals(
            "SELECT `c`.`name`," + nl + "       COUNT(`o`.`id`) AS `order_count`" + nl +
            "FROM `customers` AS `c`" + nl +
            "LEFT JOIN `orders` AS `o` ON `c`.`id` = `o`.`customer_id`" + nl +
            "WHERE `o`.`status` IN ('paid', 'new') OR `c`.`city` = 'O''Brien'" + nl +
            "GROUP BY `c`.`name`" + nl +
            "HAVING COUNT(`o`.`id`) > 1" + nl +
            "ORDER BY COUNT(`o`.`id`) DESC" + nl +
            "LIMIT 10;",
            QueryBuilderService.BuildSql("mysql", sample()), "MySQL SQL should use backticks, auto GROUP BY and LIMIT.");
        string mssql = QueryBuilderService.BuildSql("mssql", sample());
        Assert(mssql.StartsWith("SELECT TOP (10) [c].[name]", StringComparison.Ordinal) && mssql.Contains("N'O''Brien'") && !mssql.Contains("LIMIT"),
            "SQL Server should use TOP, brackets and N'' literals: " + mssql);
        AssertContains(QueryBuilderService.BuildSql("oracle", sample()), "FETCH FIRST 10 ROWS ONLY", "Oracle should use FETCH FIRST.");
        AssertContains(QueryBuilderService.BuildSql("oracle", sample()), "\"customers\" \"c\"", "Oracle table aliases must not use AS.");

        foreach (string provider in new[] { "mysql", "postgresql", "mssql", "sqlite", "oracle" })
        {
            string first = QueryBuilderService.BuildSql(provider, sample());
            QueryBuilderModel parsed;
            string error;
            Assert(QueryBuilderService.TryParse(provider, first, new[] { "customers", "orders" }, out parsed, out error), provider + " SQL should parse back: " + error + nl + first);
            AssertEquals(first, QueryBuilderService.BuildSql(provider, parsed), provider + " SQL → model → SQL should be stable.");
        }

        QueryBuilderModel reversed;
        string parseError;
        Assert(QueryBuilderService.TryParse("postgresql",
                "select distinct o.total as amount from orders o join customers c on c.id = o.customer_id where o.total between 10 and 20 and c.name like 'A%' and c.zip = '007' order by amount desc",
                new[] { "public.orders", "public.customers" }, out reversed, out parseError),
            "Lower-case SQL with output-alias ordering should parse: " + parseError);
        Assert(reversed.Distinct && reversed.Tables[0].Name == "public.orders" && reversed.Joins[0].LeftAlias == "o" && reversed.Joins[0].RightAlias == "c",
            "Known table names and join direction (existing → joined table) should be kept.");
        Assert(reversed.Columns.Single(column => column.Alias == "amount").Sort == QuerySort.Descending, "ORDER BY an output alias should sort that column.");
        AssertContains(QueryBuilderService.BuildSql("postgresql", reversed), "\"c\".\"zip\" = '007'", "Quoted numeric-looking strings must stay strings.");

        AssertEquals("'x'' OR ''1''=''1'", QueryBuilderService.Literal("postgresql", "x' OR '1'='1"), "Quotes in values must be escaped.");
        AssertEquals("'a\\\\b'", QueryBuilderService.Literal("mysql", "a\\b"), "MySQL backslashes must be doubled.");
        AssertEquals("12.5", QueryBuilderService.Literal("mysql", "12.5"), "Numbers should stay numbers.");
        AssertEquals("'007'", QueryBuilderService.Literal("mysql", "007"), "Leading-zero codes should stay strings.");
        AssertEquals("NULL", QueryBuilderService.Literal("mysql", "null"), "NULL should stay a keyword.");

        foreach (string unsupported in new[]
                 {
                     "SELECT * FROM (SELECT 1) x",
                     "SELECT UPPER(name) FROM customers",
                     "SELECT name FROM customers -- note",
                     "SELECT name FROM customers WHERE (city = 'a' OR city = 'b')",
                     "SELECT name FROM customers UNION SELECT name FROM orders",
                     "SELECT x.name FROM customers c",
                     "SELECT name FROM customers c JOIN orders o ON c.id = o.customer_id",
                     "SELECT name FROM customers WHERE city = other_column",
                     "DELETE FROM customers"
                 })
        {
            QueryBuilderModel ignored;
            string reason;
            Assert(!QueryBuilderService.TryParse("mysql", unsupported, new[] { "customers", "orders" }, out ignored, out reason) && !string.IsNullOrEmpty(reason),
                "Unsupported SQL must be rejected with a reason: " + unsupported);
        }

        QueryBuilderModel aliases = new QueryBuilderModel();
        aliases.Tables.Add(new QueryBuilderTable { Name = "sales.orders", Alias = "orders" });
        AssertEquals("orders2", aliases.NextAlias("orders"), "Aliases should not repeat.");
        aliases.Columns.Add(new QueryBuilderColumn { TableAlias = "orders", Column = "*" });
        AssertEquals("SELECT \"orders\".*" + nl + "FROM \"sales\".\"orders\";", QueryBuilderService.BuildSql("postgresql", aliases), "Schema-qualified tables keep the implicit alias.");
    }

    /// <summary>
    /// MongoPipelineService: templates build, disabled stages are skipped, preview can stop at a stage, write stages
    /// ($out／$merge, also nested in $facet／$lookup) and unknown stages are rejected with the stage number, and
    /// pipelines round-trip through import and the mongosh／query-JSON exports.
    /// </summary>
    public static void AssertMongoPipelineSemantics()
    {
        List<MongoPipelineStage> stages = new List<MongoPipelineStage>
        {
            new MongoPipelineStage("$match", "{ \"status\": \"paid\" }"),
            new MongoPipelineStage("$unwind", "\"$items\""),
            new MongoPipelineStage("$group", "{ \"_id\": \"$items.sku\", \"qty\": { \"$sum\": \"$items.qty\" } }"),
            new MongoPipelineStage("$sort", "{ \"qty\": -1 }", false),
            new MongoPipelineStage("$limit", "5")
        };
        AssertEquals("4", MongoPipelineService.Build(stages).Count.ToString(), "Disabled stages should be skipped.");
        AssertEquals("2", MongoPipelineService.Build(stages, 1).Count.ToString(), "Previews should stop at the selected stage.");
        AssertEquals("5", MongoPipelineService.Build(stages).Last()["$limit"].ToString(), "Scalar stage bodies should parse.");
        foreach (KeyValuePair<string, string> template in MongoPipelineService.Templates)
        {
            MongoPipelineService.ParseStage(new MongoPipelineStage(template.Key, template.Value), 1);
        }

        Func<string, string, string> reject = (name, body) =>
        {
            try
            {
                MongoPipelineService.Build(new List<MongoPipelineStage> { new MongoPipelineStage("$match", "{}"), new MongoPipelineStage(name, body) });
                return null;
            }
            catch (FormatException exception)
            {
                return exception.Message;
            }
        };
        AssertContains(reject("$out", "\"copy\""), "$out", "$out writes data and must be rejected.");
        AssertContains(reject("$facet", "{ \"a\": [ { \"$merge\": \"copy\" } ] }"), "$merge", "Nested write stages must be rejected.");
        AssertContains(reject("$lookup", "{ \"from\": \"x\", \"pipeline\": [ { \"$out\": \"copy\" } ], \"as\": \"y\" }"), "$out", "Write stages inside $lookup pipelines must be rejected.");
        AssertContains(reject("$dropDatabase", "{}"), "2", "Unknown stages must be rejected with their position.");
        AssertContains(reject("match", "{}"), "$", "Stage names must start with $.");
        AssertContains(reject("$match", "{ oops"), "2", "Invalid JSON must name the stage.");

        string shell = MongoPipelineService.ToShellText("sales \"2024\"", stages);
        Assert(shell.StartsWith("db.getCollection(\"sales \\\"2024\\\"\").aggregate([", StringComparison.Ordinal), "mongosh syntax should escape the collection name: " + shell);
        List<MongoPipelineStage> imported = MongoPipelineService.Import(shell.Substring(shell.IndexOf('['), shell.LastIndexOf(']') - shell.IndexOf('[') + 1));
        AssertEquals("4", imported.Count.ToString(), "Exported pipelines should import again.");
        AssertEquals("\"$items\"", imported[1].BodyJson, "String stage bodies should keep their quotes.");
        AssertEquals("5", imported[3].BodyJson, "Numeric stage bodies should stay plain numbers.");
        List<MongoPipelineStage> fromQuery = MongoPipelineService.Import(MongoPipelineService.ToQueryJson("sales", stages, 50));
        AssertEquals("4", fromQuery.Count.ToString(), "Query JSON with a pipeline field should import.");
        AssertContains(MongoPipelineService.ToQueryJson("sales", stages, 50), "\"limit\" : 50", "Query JSON should carry the row limit.");
        foreach (string bad in new[] { "{ \"$match\": {} }", "[ { \"$match\": {}, \"$limit\": 1 } ]", "[ { \"$out\": \"x\" } ]", "not json" })
        {
            try
            {
                MongoPipelineService.Import(bad);
                throw new Exception("Import should reject: " + bad);
            }
            catch (FormatException)
            {
            }
        }
    }

    /// <summary>
    /// MongoSchemaAnalyzer on synthetic documents: nested and array paths, presence, type mix, numbers stored as
    /// strings, IQR outliers, sparse and case-variant fields, empty strings, nulls, date ranges and the depth limit.
    /// </summary>
    public static void AssertMongoSchemaAnalyzerSemantics()
    {
        List<string> documents = new List<string>();
        for (int index = 1; index <= 40; index++)
        {
            string ageJson = index == 7 ? "999" : index == 8 ? "\"42\"" : (20 + index).ToString();
            List<string> fields = new List<string>
            {
                "\"_id\": " + index,
                "\"name\": \"user" + index + "\"",
                "\"age\": " + ageJson,
                "\"tags\": [\"a\", \"b\"]",
                "\"items\": [{ \"sku\": \"X" + index + "\", \"qty\": 1 }, { \"sku\": \"Y\", \"qty\": 2 }]",
                "\"created\": { \"$date\": \"2024-01-" + (index % 28 + 1).ToString("00") + "T00:00:00Z\" }",
                "\"nickname\": " + (index <= 5 ? "null" : "\"nick\""),
                "\"note\": \"" + (index <= 3 ? string.Empty : "ok") + "\"",
                (index == 1 ? "\"Email\"" : "\"email\"") + ": \"u" + index + "@example.com\""
            };
            if (index <= 30) fields.Add("\"address\": { \"city\": \"Taipei\", \"zip\": \"100\" }");
            if (index <= 2) fields.Add("\"legacy\": true");
            documents.Add("{ " + string.Join(", ", fields) + " }");
        }
        string deep = "1";
        for (int level = 0; level < 25; level++) deep = "{ \"d\": " + deep + " }";
        documents.Add("{ \"_id\": 99, \"deep\": " + deep + " }");

        MongoSchemaReport report = MongoSchemaAnalyzer.AnalyzeJson(documents);
        Func<string, MongoFieldStats> field = path => report.Fields.Single(item => item.Path == path);
        AssertEquals("41", report.DocumentCount.ToString(), "Every sampled document should be counted.");
        AssertEquals("_id", report.Fields[0].Path, "_id should be listed first.");
        AssertEquals("30", field("address.city").DocumentCount.ToString(), "Nested paths should count the documents containing them.");
        MongoFieldStats sku = field("items[].sku");
        Assert(sku.InsideArray && sku.Occurrences == 80 && sku.DocumentCount == 40, "Array-of-document paths should count every element.");
        Assert(field("tags[]").Occurrences == 80 && field("tags").ArrayMaxLength == 2, "Array element paths and array lengths should be tracked.");

        MongoFieldStats age = field("age");
        Assert(age.TypeCounts.ContainsKey("String") && age.TypeCounts.ContainsKey("Int32"), "Mixed types should be recorded.");
        Assert(age.Anomalies.Count(item => item.StartsWith(mySQLPunk.Localization.Format("MongoSchema.Anomaly.MixedTypes", string.Empty), StringComparison.Ordinal)) == 1,
            "A number/string mix should be reported as mixed types: " + string.Join(" | ", age.Anomalies));
        Assert(age.OutlierCount == 1 && age.OutlierSamples.Single().StartsWith("999", StringComparison.Ordinal) && age.OutlierSamples.Single().Contains("_id 7"),
            "999 should be the only IQR outlier and point at its document.");
        Assert(age.NumericMax == 999 && age.NumericMin == 21, "Numeric range should include every number.");

        Assert(field("legacy").Anomalies.Any(item => item.Contains("2") && item.Contains("41")), "Fields in under 10% of documents should be marked sparse.");
        Assert(field("Email").Anomalies.Any() && field("email").Anomalies.Any(), "Field names that differ only by case should be flagged on both.");
        AssertEquals("3", field("note").EmptyStringCount.ToString(), "Empty strings should be counted.");
        Assert(field("note").Anomalies.Any(), "Empty strings should be reported.");
        AssertEquals("5", field("nickname").NullCount.ToString(), "Nulls should be counted.");
        Assert(field("created").DateMin.HasValue && field("created").DateMin.Value.Day == 1 && field("created").DateMax.Value.Day == 28,
            "Date ranges should be tracked.");
        Assert(field("name").TopValues.Count == 5 && field("name").DistinctValues == 40, "Top values and distinct counts should be tracked.");
        Assert(!report.Fields.Any(item => item.Path.Split('.').Length > MongoSchemaAnalyzer.MaximumDepth) && report.Warnings.Count == 1,
            "Nesting beyond the depth limit should stop with a warning.");
        Assert(!field("_id").Anomalies.Any() && !field("address.city").Anomalies.Any(), "Consistent fields must not be flagged.");
        AssertContains(MongoSchemaAnalyzer.BuildTextReport(report, "demo"), "items[].sku", "The text report should list nested paths.");
    }

    /// <summary>
    /// DataGeneratorCore on in-memory table definitions: foreign keys pick existing or generated parents
    /// (including self references), unique values skip existing ones, rules apply, the same seed repeats, and
    /// invalid rules fail before anything is written. The result is then written through DataSyncCore.Apply.
    /// </summary>
    public static void AssertDataGeneratorCoreSemantics(DbConnection connection)
    {
        Action<string> exec = sql =>
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        };
        Func<string, long> scalar = sql =>
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = sql;
                return Convert.ToInt64(command.ExecuteScalar());
            }
        };

        exec("PRAGMA foreign_keys = ON");
        exec("CREATE TABLE customers (id INTEGER PRIMARY KEY, email VARCHAR(40) NOT NULL UNIQUE, name VARCHAR(40) NOT NULL, city VARCHAR(30) NULL, birth DATE NULL)");
        exec("CREATE TABLE orders (id INTEGER PRIMARY KEY, customer_id INTEGER NOT NULL REFERENCES customers(id), amount NUMERIC(10,2) NOT NULL, status VARCHAR(10) NOT NULL, note VARCHAR(40) NULL)");
        exec("CREATE TABLE employees (id INTEGER PRIMARY KEY, manager_id INTEGER NULL REFERENCES employees(id), name VARCHAR(40) NOT NULL)");
        exec("INSERT INTO customers (id, email, name) VALUES (1, 'user1@example.com', 'Existing'), (2, 'user2@example.com', 'Existing 2')");

        System.Data.DataTable existingCustomers = new System.Data.DataTable();
        existingCustomers.Columns.Add("id", typeof(long));
        existingCustomers.Columns.Add("email", typeof(string));
        existingCustomers.Rows.Add(1L, "user1@example.com");
        existingCustomers.Rows.Add(2L, "user2@example.com");

        Func<string, DataGeneratorTable> load = name =>
        {
            DataGeneratorTable table = new DataGeneratorTable { Name = name, ExistingRows = new System.Data.DataTable() };
            Func<string, GeneratedValueKind, bool, DataGeneratorColumn> add = (column, kind, nullable) =>
            {
                DataGeneratorColumn item = new DataGeneratorColumn
                {
                    Name = column,
                    TypeText = kind.ToString(),
                    Kind = kind,
                    IsNullable = nullable,
                    Ordinal = table.Columns.Count + 1,
                    IntegerMinimum = long.MinValue,
                    IntegerMaximum = long.MaxValue,
                    TemporalAsText = true,
                    BooleanAsInteger = true
                };
                table.Columns.Add(item);
                return item;
            };
            DataGeneratorColumn id = add("id", GeneratedValueKind.Integer, false);
            id.IsPrimaryKey = true;
            id.IsAutoNumber = true;
            table.UniqueSets.Add(new[] { "id" });
            switch (name)
            {
                case "customers":
                    add("email", GeneratedValueKind.String, false).MaxLength = 40;
                    add("name", GeneratedValueKind.String, false).MaxLength = 40;
                    add("city", GeneratedValueKind.String, true).MaxLength = 30;
                    add("birth", GeneratedValueKind.Date, true);
                    table.UniqueSets.Add(new[] { "email" });
                    table.ExistingRows = existingCustomers;
                    break;
                case "orders":
                    id.IsAutoNumber = false;
                    add("customer_id", GeneratedValueKind.Integer, false);
                    DataGeneratorColumn amount = add("amount", GeneratedValueKind.Decimal, false);
                    amount.Precision = 10;
                    amount.Scale = 2;
                    add("status", GeneratedValueKind.String, false).MaxLength = 10;
                    add("note", GeneratedValueKind.String, true).MaxLength = 40;
                    table.ForeignKeys.Add(new DataGeneratorForeignKey { Name = "fk_orders", Columns = new List<string> { "customer_id" }, ParentTable = "customers", ParentColumns = new List<string> { "id" } });
                    break;
                case "employees":
                    add("manager_id", GeneratedValueKind.Integer, true);
                    add("name", GeneratedValueKind.String, false).MaxLength = 40;
                    table.ForeignKeys.Add(new DataGeneratorForeignKey { Name = "fk_manager", Columns = new List<string> { "manager_id" }, ParentTable = "employees", ParentColumns = new List<string> { "id" } });
                    break;
                default:
                    return null;
            }
            return table;
        };

        Func<List<DataGeneratorPlan>> plans = () => new List<DataGeneratorPlan>
        {
            new DataGeneratorPlan("customers", 20, new Dictionary<string, DataGeneratorRule> { { "city", new DataGeneratorRule(DataGeneratorRuleKind.Auto, "", 40) } }),
            new DataGeneratorPlan("orders", 50, new Dictionary<string, DataGeneratorRule>
            {
                { "status", new DataGeneratorRule(DataGeneratorRuleKind.List, "new|paid|shipped") },
                { "amount", new DataGeneratorRule(DataGeneratorRuleKind.Range, "10..500.50") },
                { "note", new DataGeneratorRule(DataGeneratorRuleKind.Pattern, "ORD-{n}-{digits:3}", 30) }
            }),
            new DataGeneratorPlan("employees", 15, null)
        };

        DataGenerationResult first = DataGeneratorCore.Generate(plans(), load, 7);
        Assert(first.Succeeded, "Generation should succeed: " + first.Error);
        DataGenerationResult again = DataGeneratorCore.Generate(plans(), load, 7);
        Func<DataGenerationResult, string> dump = result => string.Join("|", result.Tables.SelectMany(table => table.Changes)
            .Select(change => string.Join(",", change.Values.Select(pair => pair.Key + "=" + DataGeneratorCore.Canonical(pair.Value)))));
        AssertEquals(dump(first), dump(again), "The same seed should generate the same rows.");

        DataTableComparison customers = first.Tables.Single(table => table.TableName == "customers");
        List<object> customerIds = customers.Changes.Select(change => change.Values["id"]).ToList();
        AssertEquals("3", Convert.ToString(customerIds.First()), "Referenced auto-number keys should continue after the existing maximum.");
        Assert(customers.Changes.All(change => !((string)change.Values["email"]).StartsWith("user1@", StringComparison.Ordinal) &&
                                               !((string)change.Values["email"]).StartsWith("user2@", StringComparison.Ordinal)),
            "Unique emails must not repeat existing ones.");
        HashSet<string> allowedParents = new HashSet<string>(customerIds.Select(value => Convert.ToString(value)).Concat(new[] { "1", "2" }));
        DataTableComparison orders = first.Tables.Single(table => table.TableName == "orders");
        Assert(orders.Changes.All(change => allowedParents.Contains(Convert.ToString(change.Values["customer_id"]))), "Foreign keys must reference existing or generated parents.");
        Assert(orders.Changes.All(change => new[] { "new", "paid", "shipped" }.Contains((string)change.Values["status"])), "List rule values only.");
        Assert(orders.Changes.All(change => (decimal)change.Values["amount"] >= 10m && (decimal)change.Values["amount"] <= 500.50m), "Range rule bounds.");
        Assert(orders.Changes.Any(change => change.Values["note"] is DBNull) && orders.Changes.Any(change => Convert.ToString(change.Values["note"]).StartsWith("ORD-", StringComparison.Ordinal)),
            "Pattern rule with a NULL percentage should produce both values and NULLs.");
        DataTableComparison employees = first.Tables.Single(table => table.TableName == "employees");
        Assert(employees.Changes.First().Values["manager_id"] is DBNull, "The first self-referencing row has no parent yet and must be NULL.");
        Assert(employees.Changes.Skip(1).Any(change => !(change.Values["manager_id"] is DBNull)), "Later rows should reference earlier generated rows.");

        Func<string, string> quote = name => "\"" + name.Replace("\"", "\"\"") + "\"";
        DataSyncResult applied = DataSyncCore.Apply(connection, first.Tables.Select(table => new DataSyncTableRequest
        {
            TableName = table.TableName,
            KeyColumns = table.KeyColumns,
            Changes = table.Changes,
            AfterInsertStatements = new List<string>()
        }).ToList(), quote, quote, "@", null);
        Assert(applied.Succeeded && applied.Inserted == 85, "Generated rows should be written: " + applied.Message);
        Assert(scalar("SELECT COUNT(*) FROM orders o WHERE NOT EXISTS (SELECT 1 FROM customers c WHERE c.id = o.customer_id)") == 0 &&
               scalar("SELECT COUNT(DISTINCT email) FROM customers") == 22,
            "Written rows must keep foreign keys and unique emails.");

        Func<string, DataGeneratorRule, DataGenerationResult> single = (column, rule) =>
            DataGeneratorCore.Generate(new List<DataGeneratorPlan> { new DataGeneratorPlan("customers", 1, new Dictionary<string, DataGeneratorRule> { { column, rule } }) }, load, 1);
        AssertContains(single("name", new DataGeneratorRule(DataGeneratorRuleKind.Null)).Error, "NOT NULL", "NOT NULL columns cannot use the NULL rule.");
        AssertContains(single("name", new DataGeneratorRule(DataGeneratorRuleKind.Pattern, "{bogus}")).Error, "{bogus}", "Unknown pattern tokens must be rejected.");
        AssertContains(single("birth", new DataGeneratorRule(DataGeneratorRuleKind.Fixed, "not a date")).Error, "not a date", "Values that do not fit the type must fail before writing.");
        AssertContains(single("name", new DataGeneratorRule(DataGeneratorRuleKind.Fixed, new string('x', 41))).Error, "40", "Values longer than the column must fail before writing.");
        Assert(single("missing", new DataGeneratorRule(DataGeneratorRuleKind.Fixed, "x")).Error != null, "Rules for unknown columns must be rejected.");
        DataGenerationResult exhausted = DataGeneratorCore.Generate(new List<DataGeneratorPlan>
        {
            new DataGeneratorPlan("customers", 3, new Dictionary<string, DataGeneratorRule> { { "email", new DataGeneratorRule(DataGeneratorRuleKind.Fixed, "same@example.com") } })
        }, load, 1);
        Assert(exhausted.Error != null && exhausted.Tables.Count == 0, "A unique column with a fixed value must fail instead of writing duplicates.");
        DataGenerationResult orphan = DataGeneratorCore.Generate(new List<DataGeneratorPlan> { new DataGeneratorPlan("orders", 1, null) },
            name => { DataGeneratorTable table = load(name); if (name == "customers") table.ExistingRows = new System.Data.DataTable(); return table; }, 1);
        Assert(orphan.Error != null, "A NOT NULL foreign key without any parent rows must fail.");
        Assert(DataGeneratorCore.Generate(new List<DataGeneratorPlan> { new DataGeneratorPlan("customers", DataGeneratorCore.MaximumRowsPerTable + 1, null) }, load, 1).Error != null,
            "Row counts above the limit must be rejected.");
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
