using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
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
    /// Automation email: SMTP settings persist without the password, plain SMTP is only allowed to localhost,
    /// recipients are validated, and a notification reaches a loopback SMTP server with the run summary.
    /// </summary>
    /// <summary>取出 DATA 內容的本文並依 Content-Transfer-Encoding（base64／quoted-printable）解碼。</summary>
    private static string DecodeMailBody(string raw)
    {
        string text = raw.Replace("\r\n", "\n");
        int split = text.IndexOf("\n\n", StringComparison.Ordinal);
        string headers = split < 0 ? text : text.Substring(0, split);
        string body = split < 0 ? string.Empty : text.Substring(split + 2);
        if (headers.IndexOf("Content-Transfer-Encoding: base64", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(body.Replace("\n", string.Empty).Trim()));
        }
        if (headers.IndexOf("Content-Transfer-Encoding: quoted-printable", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            body = body.Replace("=\n", string.Empty);
            List<byte> bytes = new List<byte>();
            for (int index = 0; index < body.Length; index++)
            {
                if (body[index] == '=' && index + 2 < body.Length && Uri.IsHexDigit(body[index + 1]) && Uri.IsHexDigit(body[index + 2]))
                {
                    bytes.Add(Convert.ToByte(body.Substring(index + 1, 2), 16));
                    index += 2;
                }
                else
                {
                    bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(body[index].ToString()));
                }
            }
            return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
        }
        return body;
    }

    public static void AssertAutomationEmailSemantics(string directory)
    {
        ScheduledJobStore store = new ScheduledJobStore(Path.Combine(directory, "automation-mail"));
        FakeSmtpServer smtp = new FakeSmtpServer();
        int port = smtp.Port;
        AutomationEmailService.Save(store, new AutomationSmtpSettings { Host = "127.0.0.1", Port = port, UseTls = false, From = "bot@example.com" }, null);
        AutomationSmtpSettings loaded = AutomationEmailService.Load(store);
        Assert(loaded.Port == port && !loaded.UseTls && loaded.From == "bot@example.com", "SMTP settings should round-trip.");
        Assert(File.ReadAllText(AutomationEmailService.SettingsPath(store)).IndexOf("password", StringComparison.OrdinalIgnoreCase) < 0, "The SMTP password is never written to the settings file.");

        ScheduledJobDefinition job = new ScheduledJobDefinition { Name = "nightly", Type = ScheduledJobType.Query, EmailTo = "ops@example.com; dev@example.com" };
        ScheduledJobRunRecord record = new ScheduledJobRunRecord { JobName = "nightly", JobType = ScheduledJobType.Query, Status = "Failed", Attempts = 3, Rows = -1, Message = "boom", StartedUtc = "s", FinishedUtc = "f" };
        string outcome = AutomationEmailService.Notify(store, job, record);
        Assert(outcome != null && outcome.Contains("2"), "The notification should report both recipients: " + outcome);
        FakeSmtpServer.Session first = smtp.WaitForSession(0);
        Assert(first.Commands.Any(line => line.StartsWith("RCPT TO:<ops@example.com>", StringComparison.OrdinalIgnoreCase)) &&
               first.Commands.Any(line => line.StartsWith("RCPT TO:<dev@example.com>", StringComparison.OrdinalIgnoreCase)), "Every recipient should be addressed: " + string.Join(" | ", first.Commands));
        string message = DecodeMailBody(first.Data);
        Assert(message.Contains("nightly") && message.IndexOf("Failed", StringComparison.Ordinal) >= 0 && message.Contains("boom"),
            "The email should carry the job, status and message: " + message);

        string report = Path.Combine(directory, "report.html");
        File.WriteAllText(report, "<html>dictionary</html>");
        job.EmailAttachOutput = true;
        ScheduledJobRunRecord withFile = new ScheduledJobRunRecord { JobName = "nightly", JobType = ScheduledJobType.DataDictionary, Status = "Success", Attempts = 1, Rows = -1, OutputPath = report, StartedUtc = "s", FinishedUtc = "f" };
        string attached = AutomationEmailService.Notify(store, job, withFile);
        FakeSmtpServer.Session second = smtp.WaitForSession(1);
        Assert(attached != null && second.Data.Contains("report.html") && second.Data.IndexOf("multipart/mixed", StringComparison.OrdinalIgnoreCase) >= 0,
            "A successful run should attach its output file: " + attached);
        smtp.Stop();

        job.EmailAttachOutput = false;
        job.NotifyOnlyOnFailure = true;
        record.Status = "Success";
        Assert(AutomationEmailService.Notify(store, job, record) != null && !AutomationEmailService.Notify(store, job, record).Contains("@"), "Successful runs are skipped when only failures notify.");
        AssertThrows<InvalidOperationException>(() => AutomationEmailService.Validate(new AutomationSmtpSettings { Host = "smtp.example.com", Port = 25, UseTls = false, From = "a@example.com" }),
            "Plain SMTP to a remote server must be rejected.");
        AssertThrows<InvalidOperationException>(() => AutomationEmailService.ParseRecipients("ops@example.com, not an address"), "Invalid recipients must be rejected.");
        AssertThrows<InvalidOperationException>(() => AutomationEmailService.ParseRecipients(string.Join(",", Enumerable.Range(0, 11).Select(index => "u" + index + "@example.com"))),
            "More than ten recipients must be rejected.");
        AssertThrows<InvalidOperationException>(() => ScheduledJobValidator.Validate(new ScheduledJobDefinition
        {
            Name = "x", Type = ScheduledJobType.Backup, ConnectionName = "a", DatabaseName = "b", OutputPath = "x.sql", EmailTo = "nope"
        }), "Jobs with invalid recipients must not validate.");
    }

    /// <summary>
    /// Automation jobs: CSV import (quotes, embedded delimiters／newlines, empty → NULL, typed values), a failed import
    /// after a written batch is not retried, a transfer job resumes via retry, the webhook receives the result JSON,
    /// and validation rejects unsafe webhooks, unconfirmed replace transfers and malformed table lines.
    /// </summary>
    public static void AssertAutomationSemantics(Func<string, IDatabase> openSqlite, string directory)
    {
        Directory.CreateDirectory(directory);
        ScheduledJobStore store = new ScheduledJobStore(Path.Combine(directory, "automation"));
        string sourcePath = Path.Combine(directory, "auto-source.db");
        string targetPath = Path.Combine(directory, "auto-target.db");
        using (IDatabase setup = openSqlite(sourcePath))
        {
            AssertEquals("OK", setup.ExecSQL("CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT, note TEXT, score NUMERIC)")["status"], "Import fixture");
            AssertEquals("OK", setup.ExecSQL("CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT)")["status"], "Transfer fixture");
            for (int index = 1; index <= 12; index++) setup.ExecSQL("INSERT INTO customers VALUES (" + index + ", 'c" + index + "')");
        }

        string csv = Path.Combine(directory, "people.csv");
        File.WriteAllText(csv, "id,name,note,score\r\n1,Alice,\"hello, world\",1.5\r\n2,\"Bob \"\"B\"\"\",\"line1\nline2\",\r\n3,Carol,,7\r\n", new System.Text.UTF8Encoding(false));
        ScheduledJobDefinition import = new ScheduledJobDefinition
        {
            Name = "import people",
            Type = ScheduledJobType.Import,
            ConnectionName = "local",
            DatabaseName = "main",
            InputPath = csv,
            TargetTable = "people",
            RetryCount = 2,
            RetryDelaySeconds = 0
        };
        ScheduledJobRunRecord imported = ScheduledJobExecutionService.Execute(import, store, () => openSqlite(sourcePath), null, _ => { });
        AssertEquals("Success", imported.Status, "CSV import should succeed: " + imported.Message);
        AssertEquals("3", imported.Rows.ToString(), "Every CSV row should be imported.");
        using (IDatabase check = openSqlite(sourcePath))
        {
            System.Data.DataTable rows = check.SelectSQL("SELECT name, note, score FROM people ORDER BY id");
            AssertEquals("hello, world", Convert.ToString(rows.Rows[0]["note"]), "Quoted delimiters should stay in the value.");
            AssertEquals("Bob \"B\"", Convert.ToString(rows.Rows[1]["name"]), "Escaped quotes should be unescaped.");
            AssertEquals("line1\nline2", Convert.ToString(rows.Rows[1]["note"]), "Quoted newlines should stay in the value.");
            Assert(rows.Rows[1]["score"] is DBNull && rows.Rows[2]["note"] is DBNull, "Empty fields should be NULL.");
        }

        System.Text.StringBuilder big = new System.Text.StringBuilder("id,name\n");
        for (int index = 100; index < 700; index++) big.Append(index == 650 ? "not-a-number" : index.ToString()).Append(",n").Append(index).Append('\n');
        File.WriteAllText(csv, big.ToString());
        ScheduledJobRunRecord partial = ScheduledJobExecutionService.Execute(import, store, () => openSqlite(sourcePath), null, _ => { });
        Assert(partial.Status == "Failed" && partial.Attempts == 1 && partial.Message.Contains("500"), "A failure after a written batch must not be retried: " + partial.Message);

        int opens = 0;
        ScheduledJobDefinition transfer = new ScheduledJobDefinition
        {
            Name = "copy customers",
            Type = ScheduledJobType.Transfer,
            ConnectionName = "local",
            DatabaseName = "main",
            TargetConnectionName = "backup",
            TargetDatabaseName = "main",
            TransferTables = ScheduledTransferTableText.Parse("customers => customers_copy : create"),
            RetryCount = 1,
            RetryDelaySeconds = 0
        };
        System.Net.Sockets.TcpListener listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        string received = null;
        System.Threading.Thread server = new System.Threading.Thread(() =>
        {
            using (System.Net.Sockets.TcpClient client = listener.AcceptTcpClient())
            using (System.Net.Sockets.NetworkStream stream = client.GetStream())
            {
                byte[] buffer = new byte[16384];
                MemoryStream request = new MemoryStream();
                while (true)
                {
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    request.Write(buffer, 0, read);
                    // Content-Length 是位元組數；訊息含中文時字元數會比較少。
                    string headers = System.Text.Encoding.ASCII.GetString(request.ToArray());
                    int split = headers.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (split < 0) continue;
                    System.Text.RegularExpressions.Match length = System.Text.RegularExpressions.Regex.Match(headers.Substring(0, split), "Content-Length: (\\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (length.Success && request.Length - split - 4 >= int.Parse(length.Groups[1].Value)) break;
                }
                received = System.Text.Encoding.UTF8.GetString(request.ToArray());
                byte[] response = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                stream.Write(response, 0, response.Length);
            }
        });
        server.IsBackground = true;
        server.Start();
        transfer.WebhookUrl = "http://127.0.0.1:" + ((System.Net.IPEndPoint)listener.LocalEndpoint).Port + "/hook";
        ScheduledJobRunRecord copied = ScheduledJobExecutionService.Execute(transfer, store, () => openSqlite(sourcePath), () =>
        {
            if (++opens == 1) throw new System.IO.IOException("target temporarily unavailable");
            return openSqlite(targetPath);
        }, _ => { });
        server.Join(10000);
        listener.Stop();
        Assert(copied.Status == "Success" && copied.Attempts == 2 && copied.Rows == 12, "The transfer job should succeed on its retry: " + copied.Message);
        using (IDatabase check = openSqlite(targetPath)) AssertEquals("12", check.CountRows("main", "customers_copy").ToString(), "The transfer job should copy every row.");
        Assert(received != null && received.StartsWith("POST /hook", StringComparison.Ordinal) && received.Contains("\"status\":\"Success\"") &&
               received.Contains("\"attempts\":2") && received.Contains("\"rows\":12"), "The webhook should receive the run result: " + received);
        AssertContains(copied.Notification ?? string.Empty, "204", "The run record should keep the webhook outcome.");

        Func<Action<ScheduledJobDefinition>, bool> rejects = change =>
        {
            ScheduledJobDefinition candidate = new ScheduledJobDefinition
            {
                Name = "x",
                Type = ScheduledJobType.Transfer,
                ConnectionName = "a",
                DatabaseName = "db",
                TargetConnectionName = "b",
                TargetDatabaseName = "db2"
            };
            change(candidate);
            try
            {
                ScheduledJobValidator.Validate(candidate);
                return false;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        };
        Assert(!rejects(job => { }), "A plain transfer job should validate.");
        Assert(rejects(job => job.WebhookUrl = "http://example.com/hook"), "Plain http webhooks outside localhost must be rejected.");
        Assert(rejects(job => job.WebhookUrl = "https://user:secret@example.com/hook"), "Webhooks with credentials must be rejected.");
        Assert(!rejects(job => job.WebhookUrl = "https://example.com/hook"), "https webhooks should be accepted.");
        Assert(rejects(job => job.TransferTables = ScheduledTransferTableText.Parse("a : replace")), "Replace transfers need the typed confirmation.");
        Assert(!rejects(job => { job.TransferTables = ScheduledTransferTableText.Parse("a : replace"); job.ConfirmedTargetDatabase = "db2"; }), "A confirmed replace should validate.");
        Assert(rejects(job => job.RetryCount = 9), "Retry counts above 5 must be rejected.");
        Assert(rejects(job => { job.ConnectionName = "b"; job.DatabaseName = "db2"; }), "Transferring a database onto itself must be rejected.");
        AssertEquals("a => b : create" + Environment.NewLine + "c : replace", ScheduledTransferTableText.Format(ScheduledTransferTableText.Parse("a => b : create\n\n# note\nc:replace")),
            "Table lines should round-trip.");
        AssertThrows<FormatException>(() => ScheduledTransferTableText.Parse("a => b : sideways"), "Unknown modes must be rejected.");
    }

    /// <summary>
    /// DataTransferService between two SQLite databases: append with automatic column mapping, create new, replace
    /// data, a stop in the middle of a table with a checkpoint and resume, row-count verification, the no-primary-key
    /// resume guard, a create-new conflict with continue-on-error, and HTML escaping in the report.
    /// </summary>
    public static void AssertDataTransferSemantics(IDatabase source, IDatabase target, string checkpointDirectory)
    {
        Action<IDatabase, string> exec = (db, sql) => AssertEquals("OK", db.ExecSQL(sql)["status"], "Transfer fixture: " + sql);
        exec(source, "CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL, city TEXT)");
        exec(source, "CREATE TABLE logs (msg TEXT)");
        exec(source, "CREATE TABLE items (id INTEGER PRIMARY KEY, label TEXT)");
        for (int index = 1; index <= 25; index++) exec(source, "INSERT INTO customers VALUES (" + index + ", 'name" + index + "', 'city')");
        for (int index = 1; index <= 7; index++) exec(source, "INSERT INTO logs VALUES ('log " + index + "')");
        for (int index = 1; index <= 5; index++) exec(source, "INSERT INTO items VALUES (" + index + ", 'item" + index + "')");
        exec(target, "CREATE TABLE customers_archive (id INTEGER PRIMARY KEY, name TEXT NOT NULL, extra TEXT)");
        exec(target, "INSERT INTO customers_archive VALUES (1000, 'old', NULL), (1001, 'old2', NULL)");
        exec(target, "CREATE TABLE items (id INTEGER PRIMARY KEY, label TEXT)");
        exec(target, "INSERT INTO items VALUES (7, 'stale'), (8, 'stale'), (9, 'stale')");
        exec(target, "CREATE TABLE taken (id INTEGER PRIMARY KEY)");

        TransferPlan plan = DataTransferService.BuildPlan(source, "main", "Source", target, "main", "Target", new[] { "customers", "logs", "items" });
        AssertEquals("CreateNew,CreateNew,Append", string.Join(",", plan.Items.Select(item => item.Mode.ToString())), "Existing target tables should default to append.");
        plan.Items[0].TargetTable = "customers_archive";
        plan.Items[0].Mode = TransferMode.Append;
        plan.Items[2].Mode = TransferMode.ReplaceData;
        plan.BatchSize = 10;
        HashSet<string> keyed = new HashSet<string>(new[] { "customers", "items" }, StringComparer.OrdinalIgnoreCase);
        Action<TransferPlan> save = current => DataTransferService.SaveCheckpoint(current, checkpointDirectory);

        System.Threading.CancellationTokenSource stop = new System.Threading.CancellationTokenSource();
        int batches = 0;
        try
        {
            DataTransferService.Run(plan, source, target, keyed, progress => { if (++batches == 2) stop.Cancel(); }, save, stop.Token);
            throw new Exception("The transfer should stop when cancelled.");
        }
        catch (OperationCanceledException)
        {
        }
        TransferPlan resumed = DataTransferService.LoadUnfinished(checkpointDirectory, plan);
        Assert(resumed != null && resumed.Id == plan.Id, "The stopped transfer should be found as an unfinished checkpoint.");
        AssertEquals("20", resumed.Items[0].CopiedRows.ToString(), "The checkpoint should record the rows already written.");
        AssertEquals("Running", resumed.Items[0].Status.ToString(), "The interrupted table should stay resumable.");

        DataTransferService.Run(resumed, source, target, keyed, null, save, System.Threading.CancellationToken.None);
        Assert(resumed.IsFinished && resumed.Items.All(item => item.Verified == true), "Every table should finish and verify: " +
            string.Join("; ", resumed.Items.Select(item => item.SourceTable + " " + item.Status + " " + item.Error + " " + item.Warning)));
        AssertEquals("27", target.CountRows("main", "customers_archive").ToString(), "Append should keep existing rows and add every source row once.");
        AssertEquals("7", target.CountRows("main", "logs").ToString(), "Create new should copy every row.");
        AssertEquals("5", target.CountRows("main", "items").ToString(), "Replace data should leave exactly the source rows.");
        Assert(DataTransferService.LoadUnfinished(checkpointDirectory, plan) == null, "A finished transfer is no longer offered for resume.");
        string json = File.ReadAllText(Path.Combine(checkpointDirectory, plan.Id + ".json"));
        Assert(json.Contains("customers_archive") && json.IndexOf("pwd", StringComparison.OrdinalIgnoreCase) < 0, "Checkpoints only keep names, never secrets.");

        TransferPlan guard = DataTransferService.BuildPlan(source, "main", "Source", target, "main", "Target2", new[] { "logs", "customers" });
        guard.Items[0].Mode = TransferMode.Append;
        guard.Items[0].CopiedRows = 3;
        guard.Items[1].TargetTable = "taken";
        guard.Items[1].Mode = TransferMode.CreateNew;
        guard.ContinueOnError = true;
        DataTransferService.Run(guard, source, target, keyed, null, null, System.Threading.CancellationToken.None);
        Assert(guard.Items.All(item => item.Status == TransferItemStatus.Failed), "Unsafe resumes and existing create-new targets must fail explicitly.");
        AssertEquals("7", target.CountRows("main", "logs").ToString(), "A refused append resume must not write anything.");

        guard.Items[0].SourceTable = "<script>alert(1)</script>";
        string report = DataTransferService.BuildHtmlReport(guard, "test");
        Assert(report.Contains("&lt;script&gt;") && !report.Contains("<script>alert"), "Report content must be HTML-escaped.");
        AssertContains(report, "Content-Security-Policy", "The report should forbid scripts.");
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
        DataGeneratorDictionary cityDictionary = DataGeneratorDictionaryStore.Parse("cities", "# weighted\nTaipei\t9\nTainan\t1\n");
        DataGenerationResult fromDictionary = DataGeneratorCore.Generate(new List<DataGeneratorPlan>
        {
            new DataGeneratorPlan("customers", 300, new Dictionary<string, DataGeneratorRule> { { "city", new DataGeneratorRule(DataGeneratorRuleKind.Dictionary, "cities", 0, cityDictionary) } })
        }, load, 3);
        Assert(fromDictionary.Succeeded, "Dictionary rule should generate: " + fromDictionary.Error);
        List<string> generatedCities = fromDictionary.Tables.Single().Changes.Select(change => (string)change.Values["city"]).ToList();
        Assert(generatedCities.All(city => city == "Taipei" || city == "Tainan") && generatedCities.Count(city => city == "Taipei") > 3 * generatedCities.Count(city => city == "Tainan") &&
               generatedCities.Contains("Tainan"), "Dictionary values follow their weights.");
        AssertContains(single("city", new DataGeneratorRule(DataGeneratorRuleKind.Dictionary, "no-such-dictionary")).Error, "no-such-dictionary", "A missing dictionary must fail before writing.");
        DataGenerationResult derived = DataGeneratorCore.Generate(new List<DataGeneratorPlan>
        {
            new DataGeneratorPlan("customers", 25, new Dictionary<string, DataGeneratorRule>
            {
                { "city", new DataGeneratorRule(DataGeneratorRuleKind.Expression, "CONCAT(UPPER(LEFT([name], 3)), '-', LEN([email]))") }
            })
        }, load, 4);
        Assert(derived.Succeeded && derived.Tables.Single().Changes.All(change =>
                (string)change.Values["city"] == ((string)change.Values["name"]).Substring(0, 3).ToUpperInvariant() + "-" + ((string)change.Values["email"]).Length),
            "Expression rules compute from other columns in the same row: " + derived.Error);
        AssertContains(single("city", new DataGeneratorRule(DataGeneratorRuleKind.Expression, "[nope] + 1")).Error, "nope", "Expressions may only use generated columns.");
        AssertContains(single("city", new DataGeneratorRule(DataGeneratorRuleKind.Expression, "[id]")).Error, "[id]", "Columns left to the database cannot be referenced.");
        AssertContains(single("city", new DataGeneratorRule(DataGeneratorRuleKind.Expression, "UPPER(")).Error, "city", "Broken expressions fail before writing.");
        AssertContains(single("name", new DataGeneratorRule(DataGeneratorRuleKind.Expression, "IF(LEN([email]) > 0, NULL, 'x')")).Error, "NOT NULL", "Expression NULLs cannot go into NOT NULL columns.");
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

    /// <summary>只回應必要指令的本機 SMTP 伺服器，可連續服務多個連線，每個連線記錄指令與 DATA 內容。</summary>
    private sealed class FakeSmtpServer
    {
        private readonly System.Net.Sockets.TcpListener listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        private readonly List<Session> sessions = new List<Session>();
        private readonly System.Threading.Thread thread;

        public FakeSmtpServer()
        {
            listener.Start();
            Port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            thread = new System.Threading.Thread(Serve) { IsBackground = true };
            thread.Start();
        }

        public int Port { get; private set; }

        public sealed class Session
        {
            public readonly List<string> Commands = new List<string>();
            public string Data = string.Empty;
        }

        public Session WaitForSession(int index)
        {
            for (int attempt = 0; attempt < 200; attempt++)
            {
                lock (sessions)
                {
                    if (sessions.Count > index) return sessions[index];
                }
                System.Threading.Thread.Sleep(50);
            }
            throw new Exception("No SMTP session " + index);
        }

        public void Stop()
        {
            listener.Stop();
        }

        private void Serve()
        {
            while (true)
            {
                System.Net.Sockets.TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (Exception) { return; }
                Session session = new Session();
                System.Text.StringBuilder data = new System.Text.StringBuilder();
                using (client)
                using (System.Net.Sockets.NetworkStream stream = client.GetStream())
                using (StreamReader reader = new StreamReader(stream, System.Text.Encoding.ASCII))
                using (StreamWriter writer = new StreamWriter(stream, System.Text.Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true })
                {
                    writer.WriteLine("220 localhost ESMTP test");
                    bool inData = false;
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (inData)
                        {
                            if (line == ".")
                            {
                                inData = false;
                                writer.WriteLine("250 queued");
                                continue;
                            }
                            data.AppendLine(line);
                            continue;
                        }
                        session.Commands.Add(line);
                        string verb = line.Split(' ')[0].ToUpperInvariant();
                        if (verb == "EHLO" || verb == "HELO") writer.WriteLine("250 localhost");
                        else if (verb == "DATA") { inData = true; writer.WriteLine("354 go ahead"); }
                        else if (verb == "QUIT") { writer.WriteLine("221 bye"); break; }
                        else writer.WriteLine("250 ok");
                    }
                }
                session.Data = data.ToString();
                lock (sessions) sessions.Add(session);
            }
        }
    }

    /// <summary>
    /// Native backup builders: SQL Server BACKUP／VERIFY／RESTORE statements (COPY_ONLY, CHECKSUM, MOVE into the
    /// server's default directories on Linux and Windows), tool arguments that never carry the password, the MongoDB
    /// config file escaping, Windows argument quoting, and the new-database name guard.
    /// </summary>
    public static void AssertNativeBackupSemantics()
    {
        AssertEquals("BACKUP DATABASE [shop]]x] TO DISK = N'/var/opt/mssql/data/o''k.bak' WITH COPY_ONLY, CHECKSUM, INIT, FORMAT, COMPRESSION, STATS = 10",
            NativeBackupService.BuildSqlServerBackupSql("shop]x", "/var/opt/mssql/data/o'k.bak", true), "Backups should be copy-only with checksums and escaped names.");
        AssertEquals("RESTORE VERIFYONLY FROM DISK = N'C:\\backup\\a.bak' WITH CHECKSUM", NativeBackupService.BuildSqlServerVerifySql("C:\\backup\\a.bak"), "Verify should check checksums.");
        System.Data.DataTable files = new System.Data.DataTable();
        files.Columns.Add("LogicalName");
        files.Columns.Add("Type");
        files.Columns.Add("PhysicalName");
        files.Rows.Add("shop", "D", "C:\\data\\shop.mdf");
        files.Rows.Add("shop_2", "D", "C:\\data\\shop_2.ndf");
        files.Rows.Add("shop_log", "L", "C:\\data\\shop_log.ldf");
        string linux = NativeBackupService.BuildSqlServerRestoreSql("shop_copy", "/b/shop.bak", files, "/var/opt/mssql/data/", "/var/opt/mssql/log");
        AssertContains(linux, "MOVE N'shop' TO N'/var/opt/mssql/data/shop_copy.mdf'", "Primary data files should move into the default data directory.");
        AssertContains(linux, "MOVE N'shop_2' TO N'/var/opt/mssql/data/shop_copy_1.ndf'", "Secondary files should get unique names.");
        AssertContains(linux, "MOVE N'shop_log' TO N'/var/opt/mssql/log/shop_copy_log.ldf'", "Log files should move into the default log directory.");
        AssertContains(NativeBackupService.BuildSqlServerRestoreSql("shop_copy", "C:\\b.bak", files, "D:\\SQLData", "E:\\Logs\\"), "TO N'D:\\SQLData\\shop_copy.mdf'", "Windows paths should keep backslashes.");
        AssertThrows<InvalidOperationException>(() => NativeBackupService.BuildSqlServerRestoreSql("x]; DROP DATABASE y --", "/b.bak", files, "/d", "/l"), "Unsafe database names must be refused.");
        AssertThrows<InvalidOperationException>(() => NativeBackupService.BuildSqlServerBackupSql("shop", "/b.bak\n; DROP", false), "Paths with line breaks must be refused.");

        NativeBackupEndpoint pg = new NativeBackupEndpoint { Provider = "postgresql", Host = "db.example.com", Port = 5432, User = "admin", Password = "s3cr3t!", Database = "shop" };
        List<string> dump = NativeBackupService.BuildPgDumpArguments(pg, "C:\\backups\\shop.dump");
        Assert(dump.Contains("--no-password") && dump.Contains("--format=custom") && !string.Join(" ", dump).Contains("s3cr3t"), "pg_dump arguments must never contain the password.");
        List<string> restore = NativeBackupService.BuildPgRestoreArguments(pg, "shop_copy", "C:\\backups\\shop.dump");
        Assert(restore.Contains("--exit-on-error") && restore.Contains("shop_copy") && !string.Join(" ", restore).Contains("s3cr3t"), "pg_restore should stop at the first error without the password.");
        AssertEquals("verify-full", NativeBackupService.ToPgSslMode("VerifyFull"), "Npgsql TLS modes should map to libpq names.");
        AssertEquals("prefer", NativeBackupService.ToPgSslMode(""), "Unknown TLS modes should fall back to prefer.");

        NativeBackupEndpoint mongo = new NativeBackupEndpoint { Provider = "mongodb", Host = "::1", Port = 27017, User = "ad\"min", Password = "p@ss:w/rd", Database = "shop", AuthDatabase = "admin", UseTls = true };
        string config = NativeBackupService.BuildMongoConfig(mongo);
        AssertEquals("uri: \"mongodb://ad%22min:p%40ss%3Aw%2Frd@[::1]:27017/?directConnection=true&authSource=admin&tls=true\"\n", config, "The Mongo URI should be percent-encoded and quoted for YAML.");
        List<string> mongodump = NativeBackupService.BuildMongoDumpArguments(mongo, "C:\\t\\c.yaml", "C:\\b\\shop.gz");
        Assert(!string.Join(" ", mongodump).Contains("p@ss") && mongodump.Contains("--gzip"), "mongodump arguments must not contain the password.");
        Assert(NativeBackupService.BuildMongoRestoreArguments(mongo, "c.yaml", "shop_copy", "b.gz").Contains("--nsTo=shop_copy.*"), "Restores should rename into the new database.");

        AssertEquals("plain", NativeBackupService.QuoteArgument("plain"), "Plain arguments stay unquoted.");
        AssertEquals("\"C:\\Program Files\\x\"", NativeBackupService.QuoteArgument("C:\\Program Files\\x"), "Spaces should be quoted.");
        AssertEquals("\"a\\\\\\\"b\"", NativeBackupService.QuoteArgument("a\\\"b"), "Embedded quotes and preceding backslashes should be escaped.");
        AssertEquals("\"C:\\my dir\\\\\"", NativeBackupService.QuoteArgument("C:\\my dir\\"), "Trailing backslashes before the closing quote should be doubled.");
        Assert(NativeBackupService.FindTool("pg_dump", Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N"), "pg_dump.exe")) == null, "A missing preferred tool path should not be replaced silently.");
        Assert(!NativeBackupService.RunTool(null, new List<string>(), null, null, null, "x").Succeeded, "Running without a tool should fail clearly.");
        AssertThrows<InvalidOperationException>(() => NativeBackupService.ValidateNewDatabaseName("shop copy"), "Spaces are not allowed in new database names.");
        NativeBackupService.ValidateNewDatabaseName("shop_copy-2");
    }

    /// <summary>格式異常偵測：語意格式、字元樣式、佔多數格式與不符的值、空白／占位值／大小寫提醒。</summary>
    public static void AssertDataFormatSemantics()
    {
        AssertEquals("Email", DataFormatAnalyzer.Classify("a.b+c@example.co"), "Email format.");
        AssertEquals("Date yyyy-MM-dd", DataFormatAnalyzer.Classify("2025-02-28"), "ISO date format.");
        AssertEquals("DateTime yyyy-MM-dd HH:mm:ss", DataFormatAnalyzer.Classify("2025-02-28T13:45:00Z"), "ISO datetime format.");
        AssertEquals("UUID", DataFormatAnalyzer.Classify("123e4567-e89b-12d3-a456-426614174000"), "UUID format.");
        AssertEquals("Integer", DataFormatAnalyzer.Classify("0912345678"), "Digits only are integers.");
        AssertEquals("Phone", DataFormatAnalyzer.Classify("+886 912-345-678"), "Phone with separators.");
        AssertEquals("IPv4", DataFormatAnalyzer.Classify("192.168.0.1"), "IPv4 format.");
        AssertEquals("AAA-9+", DataFormatAnalyzer.Shape("SKU-12345"), "Shape collapses long runs.");
        AssertEquals("Aa+_Aaaa", DataFormatAnalyzer.Shape("Taipei City"), "Shape maps case and spaces.");

        List<KeyValuePair<string, long>> emails = Enumerable.Range(1, 20).Select(i => new KeyValuePair<string, long>("user" + i + "@example.com", 2)).ToList();
        emails.Add(new KeyValuePair<string, long>("not-an-email", 3));
        emails.Add(new KeyValuePair<string, long>(" padded@example.com ", 1));
        emails.Add(new KeyValuePair<string, long>("N/A", 4));
        emails.Add(new KeyValuePair<string, long>("Bob@example.com", 1));
        emails.Add(new KeyValuePair<string, long>("bob@example.com", 1));
        emails.Add(new KeyValuePair<string, long>(string.Empty, 2));
        DataFormatReport report = DataFormatAnalyzer.Analyze(emails);
        Assert(report.DominantFormat == "Email" && report.Coverage > 0.8 && report.Coverage < 1, "Email dominates: " + report.DominantFormat + " " + report.Coverage);
        Assert(report.Anomalies.Any(item => item.Value == "not-an-email" && item.Count == 3) && report.Anomalies.Any(item => item.Value == "N/A"), "Non-matching and placeholder values are anomalies.");
        Assert(report.Anomalies.First().Value == "N/A", "Anomalies are ordered by frequency.");
        Assert(report.Issues.Count == 4 && report.Issues.Any(issue => issue.Contains("Bob@example.com")), "Empty, whitespace, placeholder and case issues are reported: " + string.Join(" | ", report.Issues));
        Assert(report.Summary.Contains("Email"), "Summary names the dominant format.");

        DataFormatReport mixed = DataFormatAnalyzer.Analyze(new[] { new KeyValuePair<string, long>("a", 1), new KeyValuePair<string, long>("1", 1), new KeyValuePair<string, long>("x@y.io", 1) });
        Assert(mixed.DominantFormat == null && mixed.Anomalies.Count == 0, "Without a dominant format nothing is flagged as an anomaly.");
        Assert(DataFormatAnalyzer.Analyze(null).ValueCount == 0, "Empty input is handled.");
    }

    /// <summary>資料產生器字典：解析、權重、內建字典保護、儲存／列出／刪除與 CSV 匯入。</summary>
    public static void AssertDataGeneratorDictionarySemantics(string directory)
    {
        DataGeneratorDictionary parsed = DataGeneratorDictionaryStore.Parse("demo", "# header\r\n  alpha \t 3\r\n\r\nbeta\ngamma\t1\n");
        AssertEquals("alpha,beta,gamma", string.Join(",", parsed.Values), "Dictionary values are trimmed and comments skipped.");
        AssertEquals("3,1,1", string.Join(",", parsed.Weights), "Weights default to 1.");
        Random random = new Random(11);
        int alpha = Enumerable.Range(0, 5000).Count(_ => parsed.Pick(random) == "alpha");
        Assert(alpha > 2700 && alpha < 3300, "Weighted picks follow the weights (expected about 60%): " + alpha);
        AssertThrows<InvalidOperationException>(() => DataGeneratorDictionaryStore.Parse("bad", "a\nb\tzero\n"), "Non-numeric weights are rejected.");
        try
        {
            DataGeneratorDictionaryStore.Parse("bad", "a\nb\t0\n");
            throw new Exception("Zero weight accepted.");
        }
        catch (InvalidOperationException exception)
        {
            AssertContains(exception.Message, "2", "Weight errors name the line.");
        }
        AssertThrows<InvalidOperationException>(() => DataGeneratorDictionaryStore.Parse("bad", "a\u0007b"), "Control characters are rejected.");
        AssertThrows<InvalidOperationException>(() => DataGeneratorDictionaryStore.Parse("empty", "# only comments\n"), "Empty dictionaries are rejected.");

        Assert(DataGeneratorDictionaryStore.List(directory).Contains("zh-TW 姓氏") && DataGeneratorDictionaryStore.Load(directory, "zh-TW 姓氏").Values.Contains("陳"), "Built-in dictionaries are listed and loadable.");
        AssertThrows<InvalidOperationException>(() => DataGeneratorDictionaryStore.Save(directory, "zh-TW 姓氏", "x"), "Built-in dictionaries are read-only.");
        AssertThrows<InvalidOperationException>(() => DataGeneratorDictionaryStore.Delete(directory, "order status"), "Built-in dictionaries cannot be deleted.");
        foreach (string badName in new[] { "../escape", "a/b", "", "name.txt", new string('x', 61) })
        {
            AssertThrows<InvalidOperationException>(() => DataGeneratorDictionaryStore.Save(directory, badName, "x"), "Invalid dictionary name should be rejected: " + badName);
        }
        Assert(DataGeneratorDictionaryStore.Load(directory, "../escape") == null, "Loading an invalid name never touches other paths.");

        DataGeneratorDictionaryStore.Save(directory, "產品 類別", "書籍\t5\n文具\n");
        Assert(DataGeneratorDictionaryStore.List(directory).Contains("產品 類別"), "Saved dictionaries are listed.");
        DataGeneratorDictionary product = DataGeneratorDictionaryStore.Load(directory, "產品 類別");
        Assert(product.Values.Count == 2 && product.TotalWeight == 6 && !product.BuiltIn, "Saved dictionaries round trip.");
        DataGeneratorDictionaryStore.Delete(directory, "產品 類別");
        Assert(DataGeneratorDictionaryStore.Load(directory, "產品 類別") == null, "Deleted dictionaries are gone.");

        string imported = DataGeneratorDictionaryStore.ImportText("name,weight\n\"Lee, Jr.\",4\nKim,x\n\n\"Say \"\"hi\"\"\"\n", true);
        DataGeneratorDictionary fromCsv = DataGeneratorDictionaryStore.Parse("csv", imported);
        AssertEquals("Lee, Jr.|Kim|Say \"hi\"", string.Join("|", fromCsv.Values), "CSV import keeps quoted commas and quotes.");
        AssertEquals("4,1,1", string.Join(",", fromCsv.Weights), "A numeric second column becomes the weight.");
    }

    /// <summary>模型中的函式／預存程序：定義比較、差異語句、合併進同步腳本與驗證。</summary>
    public static void AssertRoutineModelSemantics()
    {
        AssertEquals(
            RoutineModelService.NormalizeDefinition("mysql", "CREATE DEFINER=`root`@`%` FUNCTION `f`(v decimal(10,2)) RETURNS decimal(10,2)\n    DETERMINISTIC\nRETURN v * 1.10"),
            RoutineModelService.NormalizeDefinition("mysql", "create function `f` ( v DECIMAL(10,2) ) returns DECIMAL(10,2) deterministic return v * 1.10;"),
            "MySQL definitions compare without DEFINER, whitespace or keyword case differences.");
        Assert(RoutineModelService.NormalizeDefinition("mysql", "RETURN 'Hi'") != RoutineModelService.NormalizeDefinition("mysql", "RETURN 'hi'"), "String literals stay case-sensitive.");
        AssertEquals(
            RoutineModelService.NormalizeDefinition("postgresql", "CREATE OR REPLACE FUNCTION public.f(v numeric)\n RETURNS numeric\n LANGUAGE sql\nAS $function$ SELECT v * 2 $function$\n"),
            RoutineModelService.NormalizeDefinition("postgresql", "create or replace function public.f(v numeric) returns numeric language sql as $$SELECT v * 2$$"),
            "PostgreSQL dollar-quote tags and layout are normalized.");
        Assert(RoutineModelService.NormalizeDefinition("postgresql", "AS $$ SELECT 'A' $$") != RoutineModelService.NormalizeDefinition("postgresql", "AS $$ select 'A' $$"), "Dollar-quoted bodies keep their case.");

        List<ErModelRoutine> live = new List<ErModelRoutine>
        {
            new ErModelRoutine { Name = "f", Kind = "Function", Definition = "CREATE FUNCTION f() RETURNS int RETURN 1" },
            new ErModelRoutine { Name = "old_proc", Kind = "Procedure", Definition = "CREATE PROCEDURE old_proc() SELECT 1" }
        };
        List<ErModelRoutine> model = new List<ErModelRoutine>
        {
            new ErModelRoutine { Name = "f", Kind = "Function", Definition = "CREATE DEFINER=`x`@`%` FUNCTION f() RETURNS int RETURN 2" },
            new ErModelRoutine { Name = "new_proc", Kind = "Procedure", Definition = "CREATE PROCEDURE new_proc() BEGIN SELECT 1; SELECT 2; END" }
        };
        List<RoutineChange> mysql = RoutineModelService.Compare("mysql", model, live);
        Assert(mysql.Count == 3 && mysql.Single(c => c.Change == RoutineChangeKind.Replace).Destructive &&
               mysql.Single(c => c.Change == RoutineChangeKind.Replace).Statement.StartsWith("DROP FUNCTION IF EXISTS `f`;") &&
               !mysql.Single(c => c.Change == RoutineChangeKind.Replace).Statement.Contains("DEFINER") &&
               !mysql.Single(c => c.Change == RoutineChangeKind.Create).Destructive && mysql.Single(c => c.Change == RoutineChangeKind.Drop).Destructive,
            "MySQL routine changes: drop+create replace needs confirmation, creates run, drops need confirmation.");
        List<RoutineChange> sqlServer = RoutineModelService.Compare("mssql", new List<ErModelRoutine> { new ErModelRoutine { Name = "dbo.f", Kind = "Function", Definition = "/* v2 */ CREATE FUNCTION dbo.f() RETURNS INT AS BEGIN RETURN 2 END" } },
            new List<ErModelRoutine> { new ErModelRoutine { Name = "dbo.f", Kind = "Function", Definition = "CREATE FUNCTION dbo.f() RETURNS INT AS BEGIN RETURN 1 END" } });
        Assert(sqlServer.Single().Statement.StartsWith("/* v2 */ ALTER FUNCTION dbo.f()") && !sqlServer.Single().Destructive, "SQL Server replaces with ALTER.");
        Assert(RoutineModelService.Compare("sqlite", model, live).Count == 0, "Providers without routine sync report nothing.");

        SchemaSyncScript merged = RoutineModelService.Merge("mysql", null, mysql);
        Assert(merged.Statements.Count == 1 && merged.DestructiveStatements.Count == 2 && merged.DestructiveItems.Count == 2 &&
               merged.Text.Contains("DELIMITER $$") && merged.Text.Contains("-- DROP PROCEDURE IF EXISTS `old_proc`"),
            "Merged scripts keep creates runnable, drops commented and use DELIMITER for MySQL text.");

        AssertThrows<InvalidOperationException>(() => RoutineModelService.Validate(new List<ErModelRoutine> { new ErModelRoutine { Name = "f", Kind = "Function", Definition = " " } }), "Routines need a definition.");
        AssertThrows<InvalidOperationException>(() => RoutineModelService.Validate(new List<ErModelRoutine> { new ErModelRoutine { Name = "f", Definition = "x" }, new ErModelRoutine { Name = "F", Kind = "function", Definition = "y" } }), "Duplicate routines are rejected.");
        ErModelSchema schema = new ErModelSchema { Provider = "mysql" };
        schema.Tables.Add(new ErModelTable { Name = "t", Columns = { new ErModelColumn { Name = "id", DataType = "int" } } });
        schema.Routines.AddRange(model);
        ErModelDocument document = new ErModelDocument { Schema = schema };
        document.Diagrams.Add(new ErModelDiagram { Name = "d" });
        ErModelDocument reloaded = Newtonsoft.Json.JsonConvert.DeserializeObject<ErModelDocument>(Newtonsoft.Json.JsonConvert.SerializeObject(document));
        ErModelService.Validate(reloaded);
        Assert(reloaded.Schema.Routines.Count == 2 && reloaded.Schema.Routines[1].Kind == "Procedure", "Routines survive the model file round trip.");
    }

    /// <summary>模型內結構：型別白名單、驗證、擷取／轉快照往返、改名與刪表連帶更新外鍵及圖表。</summary>
    public static void AssertErModelSchemaSemantics()
    {
        foreach (string type in new[] { "", "int", "INTEGER", "varchar(20)", "numeric(10, 2)", "int unsigned", "decimal(10,2) unsigned zerofill", "enum('a','in progress')", "enum('it''s')", "text[]", "character varying(50)", "timestamp(6) with time zone", "nvarchar(max)", "VARCHAR2(20 BYTE)", "VARCHAR (20)", "double precision" })
        {
            Assert(ErModelService.IsSafeDataType(type), "Model data type should be accepted: " + type);
        }
        foreach (string type in new[] { "int; DROP TABLE x", "int -- x", "varchar(20)) ; x", "int /* x */", "enum('a';'b')", "text\nNULL", "int, name text", "'x'" })
        {
            Assert(!ErModelService.IsSafeDataType(type), "Model data type should be rejected: " + type);
        }

        SchemaModelSnapshot live = new SchemaModelSnapshot { DatabaseName = "shop", ProviderName = "sqlite" };
        SchemaTableModel customers = new SchemaTableModel { Name = "customers" };
        customers.Columns.Add(new SchemaColumnModel { Name = "id", DataType = "INTEGER", IsNullable = true, IsPrimaryKey = true, Ordinal = 1 });
        customers.Columns.Add(new SchemaColumnModel { Name = "name", DataType = "TEXT", IsNullable = false, Ordinal = 2 });
        SchemaTableModel orders = new SchemaTableModel { Name = "orders" };
        orders.Columns.Add(new SchemaColumnModel { Name = "id", DataType = "INTEGER", IsNullable = true, IsPrimaryKey = true, Ordinal = 1 });
        orders.Columns.Add(new SchemaColumnModel { Name = "customer_id", DataType = "INTEGER", IsNullable = true, Ordinal = 2 });
        live.Tables.Add(customers);
        live.Tables.Add(orders);
        live.Relationships.Add(new SchemaRelationshipModel { Name = "fk_orders_0", FromTable = "orders", FromColumn = "customer_id", ToTable = "customers", ToColumn = "id", Ordinal = 1 });

        ErModelSchema schema = ErModelService.CaptureSchema(live);
        ErModelService.ValidateSchema(schema);
        SchemaComparisonResult same = SchemaComparisonService.Compare(live, ErModelService.ToSnapshot(schema, "shop"));
        Assert(same.Differences.All(item => item.Kind == SchemaDifferenceKind.MetadataWarning), "A captured model should compare equal to its database.");

        ErModelDocument document = ErModelService.CreateDefault(live, "Main");
        document.Schema = schema;
        ErModelTable renamed = new ErModelTable { Name = "clients" };
        renamed.Columns.Add(new ErModelColumn { Name = "id", DataType = "INTEGER", PrimaryKey = true });
        renamed.Columns.Add(new ErModelColumn { Name = "full_name", DataType = "varchar(80)", Nullable = false });
        AssertEquals("0", ErModelService.ReplaceTable(document, "customers", renamed, null).ToString(), "Renaming keeps the incoming key when its column survives.");
        Assert(document.Schema.Relationships.Single().ToTable == "clients" && document.Diagrams[0].Find("clients") != null && document.Diagrams[0].Find("customers") == null,
            "Renaming a model table updates foreign keys and diagram placements.");

        ErModelTable withoutId = new ErModelTable { Name = "clients" };
        withoutId.Columns.Add(new ErModelColumn { Name = "code", DataType = "TEXT", PrimaryKey = true });
        AssertEquals("1", ErModelService.ReplaceTable(document, "clients", withoutId, null).ToString(), "Removing a referenced column drops the incoming key.");
        Assert(document.Schema.Relationships.Count == 0, "The dangling key is gone.");

        ErModelTable lines = new ErModelTable { Name = "order_lines" };
        lines.Columns.Add(new ErModelColumn { Name = "order_id", DataType = "INTEGER", Nullable = false });
        lines.Columns.Add(new ErModelColumn { Name = "sku", DataType = "TEXT" });
        ErModelService.ReplaceTable(document, null, lines, new List<ErModelRelationship> { new ErModelRelationship { FromColumn = "order_id", ToTable = "orders", ToColumn = "id" } });
        SchemaModelSnapshot modelSnapshot = ErModelService.ToSnapshot(document.Schema, "shop");
        Assert(modelSnapshot.Relationships.Single().Name == "fk_order_lines_orders" && modelSnapshot.Relationships.Single().FromTable == "order_lines", "Unnamed model keys get a stable name.");

        AssertThrows<InvalidOperationException>(() => ErModelService.ReplaceTable(document, null, lines, null), "Duplicate model tables are rejected.");
        ErModelTable badType = new ErModelTable { Name = "bad" };
        badType.Columns.Add(new ErModelColumn { Name = "x", DataType = "int); DROP TABLE orders; --" });
        AssertThrows<InvalidOperationException>(() => ErModelService.ReplaceTable(document, null, badType, null), "Unsafe model types are rejected.");
        Assert(document.Schema.Find("bad") == null, "A rejected edit leaves the model unchanged.");
        ErModelTable badKey = new ErModelTable { Name = "bad" };
        badKey.Columns.Add(new ErModelColumn { Name = "x", DataType = "INTEGER" });
        AssertThrows<InvalidOperationException>(() => ErModelService.ReplaceTable(document, null, badKey, new List<ErModelRelationship> { new ErModelRelationship { FromColumn = "x", ToTable = "missing", ToColumn = "id" } }), "Keys to missing tables are rejected.");
        AssertThrows<InvalidOperationException>(() => ErModelService.ReplaceTable(document, null, new ErModelTable { Name = "empty" }, null), "Tables need columns.");

        ErModelService.DropTable(document, "orders");
        Assert(document.Schema.Find("orders") == null && document.Schema.Relationships.Count == 0 && document.Diagrams[0].Find("orders") == null, "Dropping a model table removes its keys and placements.");

        string json = Newtonsoft.Json.JsonConvert.SerializeObject(document);
        ErModelDocument reloaded = Newtonsoft.Json.JsonConvert.DeserializeObject<ErModelDocument>(json);
        ErModelService.Validate(reloaded);
        Assert(reloaded.Schema.Find("order_lines") != null && reloaded.Schema.Provider == "sqlite", "The model schema survives a save round trip.");
        reloaded.Schema.Tables[0].Columns[0].DataType = "int; drop table x";
        AssertThrows<InvalidOperationException>(() => ErModelService.Validate(reloaded), "Loading a model with an unsafe type fails.");
    }

    /// <summary>BI 運算式、計算欄位、彙總、跨圖表篩選與 .punkbi 往返（不需要資料庫）。</summary>
    public static void AssertBiSemantics()
    {
        Func<string, object> row = name =>
        {
            switch (name.ToLowerInvariant())
            {
                case "price": return 12.5m;
                case "qty": return 4m;
                case "name": return "Widget";
                case "shipped": return new DateTime(2025, 3, 14);
                case "note": return null;
                default: throw new FormatException("unknown " + name);
            }
        };
        Func<string, object> eval = text => BiExpression.Parse(text).Evaluate(row);
        AssertEquals("50", BiExpression.ToText(eval("[price] * [qty]")), "BI multiplication.");
        AssertEquals("14", BiExpression.ToText(eval("2 + 3 * 4")), "BI precedence.");
        AssertEquals("20", BiExpression.ToText(eval("(2 + 3) * 4")), "BI parentheses.");
        AssertEquals("-3", BiExpression.ToText(eval("-(1 + 2)")), "BI unary minus.");
        AssertEquals("big", BiExpression.ToText(eval("IF([price] * [qty] >= 50, 'big', 'small')")), "BI IF.");
        AssertEquals("3.14", BiExpression.ToText(eval("ROUND(3.14159, 2)")), "BI ROUND.");
        AssertEquals("WIDGET-2025-3", BiExpression.ToText(eval("CONCAT(UPPER([name]), '-', YEAR([shipped]), '-', MONTH([shipped]))")), "BI CONCAT with dates.");
        AssertEquals("Widget x4", BiExpression.ToText(eval("[name] + ' x' + [qty]")), "BI string concatenation.");
        AssertEquals("n/a", BiExpression.ToText(eval("COALESCE([note], 'n/a')")), "BI COALESCE.");
        Assert(eval("[note] + 1") == null, "BI NULL propagation.");
        Assert(eval("[qty] / 0") == null, "BI division by zero yields NULL.");
        Assert((bool)eval("[qty] > 3 AND NOT [name] = 'x' OR FALSE"), "BI boolean logic.");
        Assert((bool)eval("[shipped] >= '2025-01-01'"), "BI date comparison against a literal.");
        AssertEquals("it's", BiExpression.ToText(eval("'it''s'")), "BI escaped quote.");
        AssertEquals("Wid", BiExpression.ToText(eval("left([name], 3)")), "BI function names are case-insensitive.");
        CollectionEquals(new[] { "price", "qty" }, BiExpression.Parse("[price] * [qty] + [PRICE]").Fields, "BI field references are de-duplicated.");
        foreach (string bad in new[] { "", "1 +", "[unclosed", "'open", "NOPE(1)", "IF(1, 2)", "1 ; DROP TABLE x", "(1 + 2", "[]" })
        {
            AssertThrows<FormatException>(() => BiExpression.Parse(bad), "BI rejects malformed expression: " + bad);
        }
        AssertThrows<FormatException>(() => BiExpression.Parse("[name] * 2").Evaluate(row), "BI arithmetic on text fails with a readable error.");

        System.Data.DataTable source = new System.Data.DataTable();
        source.Columns.Add("region", typeof(string));
        source.Columns.Add("product", typeof(string));
        source.Columns.Add("amount", typeof(double));
        source.Columns.Add("ordered", typeof(DateTime));
        source.Rows.Add("North", "Tea", 10.0, new DateTime(2025, 1, 5));
        source.Rows.Add("North", "Coffee", 30.0, new DateTime(2025, 1, 20));
        source.Rows.Add("South", "Tea", 5.0, new DateTime(2025, 2, 2));
        source.Rows.Add("South", "Tea", 7.5, new DateTime(2025, 2, 9));
        source.Rows.Add(DBNull.Value, "Coffee", 1.0, new DateTime(2025, 3, 1));

        BiDataset dataset = new BiDataset { Name = "sales", Query = "SELECT * FROM sales" };
        dataset.CalculatedFields.Add(new BiCalculatedField { Name = "taxed", Expression = "ROUND([amount] * 1.05, 2)" });
        dataset.CalculatedFields.Add(new BiCalculatedField { Name = "size", Expression = "IF([taxed] >= 10, 'large', 'small')" });
        BiDatasetData data = BiDashboardService.Prepare(source, dataset);
        AssertEquals("region,product,amount,ordered,taxed,size", string.Join(",", data.Columns), "BI calculated fields are appended.");
        AssertEquals("31.5", BiExpression.ToText(data.Rows[1][4]), "BI calculated field value.");
        AssertEquals("large", BiExpression.ToText(data.Rows[1][5]), "BI calculated field can reference an earlier one.");

        BiDataset backwards = new BiDataset { Name = "bad", Query = "SELECT 1" };
        backwards.CalculatedFields.Add(new BiCalculatedField { Name = "a", Expression = "[b] + 1" });
        backwards.CalculatedFields.Add(new BiCalculatedField { Name = "b", Expression = "1" });
        AssertThrows<InvalidOperationException>(() => BiDashboardService.Prepare(source, backwards), "BI calculated field cannot reference a later one.");
        BiDataset collides = new BiDataset { Name = "bad", Query = "SELECT 1" };
        collides.CalculatedFields.Add(new BiCalculatedField { Name = "Amount", Expression = "1" });
        AssertThrows<InvalidOperationException>(() => BiDashboardService.Prepare(source, collides), "BI calculated field name cannot shadow a column.");

        BiWidget byRegion = new BiWidget { Title = "By region", Dataset = "sales", Kind = BiChartKind.Bar, Category = "region", Value = "amount", Aggregate = BiAggregate.Sum };
        BiWidget byMonth = new BiWidget { Title = "By month", Dataset = "sales", Kind = BiChartKind.Line, Category = "ordered", DateGrain = BiDateGrain.Month, Aggregate = BiAggregate.Count, SortByValue = false };
        BiWidget total = new BiWidget { Title = "Total", Dataset = "sales", Kind = BiChartKind.Number, Value = "taxed", Aggregate = BiAggregate.Sum };
        BiWidget teaOnly = new BiWidget { Title = "Tea", Dataset = "sales", Kind = BiChartKind.Pie, Category = "region", Value = "product", Aggregate = BiAggregate.DistinctCount, Filter = "[product] = 'Tea'", CrossFilter = false };

        BiWidgetResult regions = BiDashboardService.Compute(byRegion, 0, data, null);
        AssertEquals("North=40;South=12.5;(NULL)=1", string.Join(";", regions.Points.Select(p => (p.Key.StartsWith("\u0000", StringComparison.Ordinal) ? "(NULL)" : p.Label) + "=" + BiExpression.ToText(p.Value))), "BI sum by category sorted by value.");
        BiWidgetResult months = BiDashboardService.Compute(byMonth, 1, data, null);
        AssertEquals("2025-01=2;2025-02=2;2025-03=1", string.Join(";", months.Points.Select(p => p.Key + "=" + BiExpression.ToText(p.Value))), "BI date grain groups by month in order.");
        AssertEquals("56.18", BiExpression.ToText(Math.Round(BiDashboardService.Compute(total, 2, data, null).Total.Value, 2)), "BI number card total.");
        AssertEquals("2", BiExpression.ToText(BiDashboardService.Compute(teaOnly, 3, data, null).Points.Count), "BI widget filter expression.");

        List<BiFilter> filters = BiDashboardService.Toggle(null, 0, byRegion, "North");
        AssertEquals("2025-01=2", string.Join(";", BiDashboardService.Compute(byMonth, 1, data, filters).Points.Select(p => p.Key + "=" + BiExpression.ToText(p.Value))), "BI cross-filter narrows other widgets.");
        AssertEquals("3", BiExpression.ToText(BiDashboardService.Compute(byRegion, 0, data, filters).Points.Count), "BI cross-filter source keeps all categories.");
        AssertEquals("2", BiExpression.ToText(BiDashboardService.Compute(teaOnly, 3, data, filters).Points.Count), "BI widget with cross-filter off ignores filters.");
        filters = BiDashboardService.Toggle(filters, 1, byMonth, "2025-01");
        AssertEquals("2", BiExpression.ToText(filters.Count), "BI filters from two widgets combine.");
        AssertEquals("North=40", string.Join(";", BiDashboardService.Compute(byRegion, 0, data, filters).Points.Select(p => p.Key + "=" + BiExpression.ToText(p.Value))), "BI month filter reaches the region chart.");
        AssertEquals("1", BiExpression.ToText(BiDashboardService.Toggle(filters, 0, byRegion, "North").Count), "BI clicking the selected category clears it.");
        AssertEquals("South", BiDashboardService.Toggle(filters, 0, byRegion, "South").Single(f => f.SourceWidget == 0).Key, "BI clicking another category replaces the filter.");
        List<BiFilter> shifted = BiDashboardService.RemoveWidget(filters, 0);
        Assert(shifted.Count == 1 && shifted[0].SourceWidget == 0 && shifted[0].Field == "ordered", "BI removing a widget renumbers filter sources.");

        BiWidgetResult broken = BiDashboardService.Compute(new BiWidget { Title = "x", Dataset = "sales", Kind = BiChartKind.Bar, Category = "missing", Aggregate = BiAggregate.Count }, 4, data, null);
        Assert(!string.IsNullOrEmpty(broken.Error), "BI missing column is reported on the widget.");
        BiWidgetResult notNumeric = BiDashboardService.Compute(new BiWidget { Title = "x", Dataset = "sales", Kind = BiChartKind.Bar, Category = "region", Value = "product", Aggregate = BiAggregate.Sum }, 4, data, null);
        Assert(!string.IsNullOrEmpty(notNumeric.Error), "BI sum over text is reported on the widget.");

        BiDashboard dashboard = new BiDashboard { Title = "Sales" };
        dashboard.Datasets.Add(dataset);
        dashboard.Widgets.AddRange(new[] { byRegion, byMonth, total, teaOnly });
        string json = BiDashboardService.Serialize(dashboard);
        AssertContains(json, "\"Kind\": \"Line\"", "BI enums are saved by name.");
        BiDashboard loaded = BiDashboardService.Deserialize(json);
        Assert(loaded.Widgets.Count == 4 && loaded.Datasets[0].CalculatedFields.Count == 2 && loaded.Widgets[1].DateGrain == BiDateGrain.Month && !loaded.Widgets[3].CrossFilter, "BI dashboard round trip.");
        AssertThrows<InvalidOperationException>(() => BiDashboardService.Deserialize(json.Replace("\"Dataset\": \"sales\"", "\"Dataset\": \"gone\"")), "BI rejects widget with unknown dataset.");
        AssertThrows<InvalidOperationException>(() => BiDashboardService.Deserialize("{\"Version\": 9}"), "BI rejects unknown version.");
        AssertThrows<InvalidOperationException>(() => BiDashboardService.Deserialize("{\"Version\": 1, \"Datasets\": [{\"Name\": \"a\", \"Query\": \"SELECT 1\"}, {\"Name\": \"A\", \"Query\": \"SELECT 2\"}]}"), "BI rejects duplicate dataset names.");
        AssertThrows<InvalidOperationException>(() => BiDashboardService.Deserialize("{\"Version\": 1, \"Datasets\": [{\"Name\": \"a\", \"Query\": \"SELECT 1\", \"CalculatedFields\": [{\"Name\": \"x\", \"Expression\": \"1 +\"}]}]}"), "BI rejects a broken calculated field.");

        string report = BiReportService.BuildHtml(dashboard, new Dictionary<string, BiDatasetData> { { "sales", data } }, new Dictionary<string, string>(), null, "shop", new DateTime(2025, 1, 2, 3, 4, 0));
        Assert(report.Contains("Content-Security-Policy") && !report.Contains("<script") && report.Contains("2025-01-02 03:04"), "BI HTML report has a CSP, no scripts and the generation time.");
        Assert(report.Split(new[] { "<svg" }, StringSplitOptions.None).Length - 1 == 3 && report.Contains("<polyline") && report.Contains("<path") && report.Contains("class=\"number\""),
            "BI HTML report draws bar, line and pie SVGs plus the number card.");
        BiDataset hostile = new BiDataset { Name = "hostile", Query = "SELECT 1" };
        System.Data.DataTable hostileTable = new System.Data.DataTable();
        hostileTable.Columns.Add("label", typeof(string));
        hostileTable.Rows.Add("<script>alert(1)</script>");
        BiDashboard hostileDashboard = new BiDashboard { Title = "<img src=x>" };
        hostileDashboard.Datasets.Add(hostile);
        hostileDashboard.Widgets.Add(new BiWidget { Title = "t", Dataset = "hostile", Kind = BiChartKind.Bar, Category = "label", Aggregate = BiAggregate.Count });
        hostileDashboard.Widgets.Add(new BiWidget { Title = "missing", Dataset = "hostile", Kind = BiChartKind.Number, Aggregate = BiAggregate.Count });
        string hostileHtml = BiReportService.BuildHtml(hostileDashboard, new Dictionary<string, BiDatasetData> { { "hostile", BiDashboardService.Prepare(hostileTable, hostile) } }, null, null, "x", DateTime.Now);
        Assert(!hostileHtml.Contains("<script>") && !hostileHtml.Contains("<img src") && hostileHtml.Contains("&lt;script&gt;"), "BI HTML report escapes labels and titles.");
        string failedHtml = BiReportService.BuildHtml(hostileDashboard, new Dictionary<string, BiDatasetData>(), new Dictionary<string, string> { { "hostile", "boom <x>" } }, null, "x", DateTime.Now);
        Assert(failedHtml.Contains("boom &lt;x&gt;") && failedHtml.Contains("class=\"error\""), "BI HTML report shows dataset errors on the cards.");
        string filteredHtml = BiReportService.BuildHtml(dashboard, new Dictionary<string, BiDatasetData> { { "sales", data } }, null, BiDashboardService.Toggle(null, 0, byRegion, "North"), "shop", DateTime.Now);
        Assert(filteredHtml.Contains("region = North"), "BI HTML report lists active filters.");

        string reason;
        Assert(!ScheduledJobValidator.IsReadOnlySql("DELETE FROM sales", out reason), "BI dataset query must be read-only.");
        AssertThrows<InvalidOperationException>(() => BiDashboardService.Query(null, "db", dataset), "BI query without connection fails.");
        Assert(!BiDashboardService.SupportsProvider("Redis") && BiDashboardService.SupportsProvider("MongoDB") && BiDashboardService.SupportsProvider("Snowflake"), "BI provider support.");
    }

    private static void CollectionEquals(IEnumerable<string> expected, IEnumerable<string> actual, string message)
    {
        AssertEquals(string.Join(",", expected), string.Join(",", actual), message);
    }

    /// <summary>
    /// ER model service: foreign-key layered layout (parents left, isolated tables last, cycles terminate), model
    /// validation, save／load round trip with coordinate clamping, and SVG output that escapes names and hides
    /// tables in hidden groups.
    /// </summary>
    public static void AssertErModelSemantics(string directory)
    {
        SchemaModelSnapshot snapshot = new SchemaModelSnapshot { DatabaseName = "shop", ProviderName = "sqlite" };
        foreach (string name in new[] { "customers", "orders", "order_items", "products", "audit_log", "<script>x</script>", "a", "b" })
        {
            SchemaTableModel table = new SchemaTableModel { Name = name };
            table.Columns.Add(new SchemaColumnModel { Name = "id", DataType = "INTEGER", IsPrimaryKey = true, Ordinal = 1 });
            table.Columns.Add(new SchemaColumnModel { Name = "ref_id", DataType = "INTEGER", IsNullable = true, Ordinal = 2 });
            snapshot.Tables.Add(table);
        }
        Action<string, string> fk = (from, to) => snapshot.Relationships.Add(new SchemaRelationshipModel { Name = from + "_" + to, FromTable = from, FromColumn = "ref_id", ToTable = to, ToColumn = "id", Ordinal = 1 });
        fk("orders", "customers");
        fk("order_items", "orders");
        fk("order_items", "products");
        fk("a", "b");
        fk("b", "a");

        ErModelDocument document = ErModelService.CreateDefault(snapshot, "Main");
        ErModelDiagram diagram = document.Diagrams[0];
        Func<string, int> x = name => diagram.Find(name).X;
        Assert(x("customers") < x("orders") && x("orders") < x("order_items") && x("products") < x("order_items"), "Parents should be placed left of their children.");
        Assert(x("audit_log") > x("order_items") && x("<script>x</script>") == x("audit_log"), "Tables without relationships should share the last column.");
        Assert(diagram.Tables.Select(item => item.X + "," + item.Y).Distinct().Count() == diagram.Tables.Count, "No two tables may share a position.");

        document.Groups.Add(new ErModelGroup { Name = "Sales", Color = "#16a34a" });
        document.Groups.Add(new ErModelGroup { Name = "Hidden", Color = "#6b7280", Visible = false });
        diagram.Find("orders").Group = "Sales";
        diagram.Find("audit_log").Group = "Hidden";
        diagram.Find("products").X = 999999;
        diagram.Find("customers").Group = "Missing";
        string path = Path.Combine(directory, "shop.punkmodel");
        ErModelService.Save(document, path);
        ErModelDocument loaded = ErModelService.Load(path);
        Assert(loaded.Diagrams[0].Find("orders").Group == "Sales" && loaded.Diagrams[0].Find("customers").Group == null,
            "Groups should round-trip and unknown group references should be dropped.");
        AssertEquals(ErModelService.MaximumCoordinate.ToString(), loaded.Diagrams[0].Find("products").X.ToString(), "Coordinates should be clamped.");

        string svg = ErModelService.BuildSvg(snapshot, loaded, loaded.Diagrams[0]);
        Assert(svg.Contains("&lt;script&gt;x&lt;/script&gt;") && !svg.Contains("<script>"), "SVG names must be escaped.");
        Assert(!svg.Contains(">audit_log<") && svg.Contains(">orders<"), "Tables in hidden groups must be left out of the SVG.");
        AssertEquals("#c4e8d1", ErModelService.Tint("#16a34a"), "Group tints should blend three parts white.");

        Func<Action<ErModelDocument>, bool> rejects = change =>
        {
            ErModelDocument candidate = ErModelService.CreateDefault(snapshot, "Main");
            change(candidate);
            try { ErModelService.Validate(candidate); return false; }
            catch (InvalidOperationException) { return true; }
        };
        Assert(rejects(doc => doc.Groups.Add(new ErModelGroup { Name = "x", Color = "red" })), "Group colors must be #RRGGBB.");
        Assert(rejects(doc => { doc.Groups.Add(new ErModelGroup { Name = "x" }); doc.Groups.Add(new ErModelGroup { Name = "X" }); }), "Group names must be unique.");
        Assert(rejects(doc => doc.Diagrams.Clear()), "A model needs a diagram.");
        Assert(rejects(doc => doc.Diagrams.Add(new ErModelDiagram { Name = "main" })), "Diagram names must be unique.");
        Assert(rejects(doc => doc.Version = 2), "Unknown model versions must be rejected.");
        File.WriteAllText(path, "{ not json");
        AssertThrows<InvalidOperationException>(() => ErModelService.Load(path), "Corrupt model files must be rejected with a message.");
    }

    /// <summary>
    /// Data dictionary templates on SQLite: full output keeps CREATE statements behind a CSP, the compact template
    /// normalizes column details, the column list gathers every table, filters limit the objects, personal text is
    /// escaped, and invalid colors are rejected. A data dictionary automation job writes the file.
    /// </summary>
    public static void AssertDataDictionarySemantics(IDatabase database, string directory)
    {
        Action<string> exec = sql => AssertEquals("OK", database.ExecSQL(sql)["status"], "Dictionary fixture: " + sql);
        exec("CREATE TABLE sales_orders (id INTEGER PRIMARY KEY, total NUMERIC NOT NULL DEFAULT 0)");
        exec("CREATE TABLE sales_items (id INTEGER PRIMARY KEY, order_id INTEGER, sku TEXT)");
        exec("CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT)");
        exec("CREATE INDEX idx_items_order ON sales_items(order_id)");

        string full = DataDictionaryService.BuildHtml(database, "main", "sqlite", "", "test", new DataDictionaryOptions { Title = "<b>Catalog</b>", Author = "Data & Ops", AccentColor = "#aa3300" });
        Assert(full.Contains("Content-Security-Policy") && full.Contains("CREATE TABLE") && full.Contains("--accent: #aa3300"), "The full template should keep DDL, CSP and the accent color.");
        Assert(full.Contains("&lt;b&gt;Catalog&lt;/b&gt;") && full.Contains("Data &amp; Ops") && !full.Contains("<b>Catalog"), "Personal text must be escaped.");

        string compact = DataDictionaryService.BuildHtml(database, "main", "sqlite", "", "test", new DataDictionaryOptions { Template = DataDictionaryTemplate.Compact });
        Assert(!compact.Contains("<details") && compact.Contains(">PRI<") && compact.Contains(">NO<"), "The compact template should normalize keys and nullability without DDL.");

        string columns = DataDictionaryService.BuildHtml(database, "main", "sqlite", "", "test", new DataDictionaryOptions { Template = DataDictionaryTemplate.ColumnsOnly, TableFilter = "sales_*" });
        Assert(columns.Contains("sales_orders") && columns.Contains("sales_items") && !columns.Contains("customers") && !columns.Contains("<h2"),
            "The column list should gather only the filtered tables into one table.");

        string noIndexes = DataDictionaryService.BuildHtml(database, "main", "sqlite", "", "test", new DataDictionaryOptions { IncludeIndexes = false, IncludeDdl = false, IncludeToc = false });
        Assert(!noIndexes.Contains("idx_items_order") && !noIndexes.Contains("class=\"toc\""), "Sections can be switched off.");
        AssertThrows<InvalidOperationException>(() => new DataDictionaryOptions { AccentColor = "red; background:url(x)" }.Validate(), "Colors must be #RRGGBB.");

        ScheduledJobStore store = new ScheduledJobStore(Path.Combine(directory, "dictionary-automation"));
        string output = Path.Combine(directory, "dict-{yyyyMMdd}.html");
        ScheduledJobDefinition job = new ScheduledJobDefinition
        {
            Name = "dictionary",
            Type = ScheduledJobType.DataDictionary,
            ConnectionName = "local",
            DatabaseName = "main",
            OutputPath = output,
            DictionaryOptions = new DataDictionaryOptions { Template = DataDictionaryTemplate.Compact, Title = "Nightly catalog" }
        };
        ScheduledJobRunRecord record = ScheduledJobExecutionService.Execute(job, store, () => new NonDisposingDatabase(database));
        Assert(record.Status == "Success" && File.Exists(record.OutputPath) && File.ReadAllText(record.OutputPath).Contains("Nightly catalog"),
            "The dictionary job should write the configured document: " + record.Message);

        BiDashboard dashboard = new BiDashboard { Title = "Nightly <objects>" };
        dashboard.Datasets.Add(new BiDataset { Name = "objects", Query = "SELECT type, name FROM sqlite_master" });
        dashboard.Widgets.Add(new BiWidget { Title = "Objects", Dataset = "objects", Kind = BiChartKind.Number, Aggregate = BiAggregate.Count });
        dashboard.Widgets.Add(new BiWidget { Title = "By type", Dataset = "objects", Kind = BiChartKind.Pie, Category = "type", Aggregate = BiAggregate.Count });
        string dashboardPath = Path.Combine(directory, "nightly" + BiDashboardService.FileExtension);
        BiDashboardService.Save(dashboardPath, dashboard);
        ScheduledJobDefinition biJob = new ScheduledJobDefinition
        {
            Name = "bi",
            Type = ScheduledJobType.BiDashboard,
            ConnectionName = "local",
            DatabaseName = "main",
            InputPath = dashboardPath,
            OutputPath = Path.Combine(directory, "bi-{yyyyMMdd}.html")
        };
        ScheduledJobRunRecord biRecord = ScheduledJobExecutionService.Execute(biJob, store, () => new NonDisposingDatabase(database));
        string biHtml = biRecord.OutputPath == null || !File.Exists(biRecord.OutputPath) ? string.Empty : File.ReadAllText(biRecord.OutputPath);
        Assert(biRecord.Status == "Success" && biRecord.Rows > 0 && biHtml.Contains("Nightly &lt;objects&gt;") && biHtml.Contains("<svg") && biHtml.Contains("<path") &&
               !biHtml.Contains("<script") && biHtml.Contains("Content-Security-Policy"), "The BI job should write an HTML report with SVG charts: " + biRecord.Message);

        dashboard.Datasets.Add(new BiDataset { Name = "unsafe", Query = "DELETE FROM customers" });
        dashboard.Widgets.Add(new BiWidget { Title = "Unsafe", Dataset = "unsafe", Kind = BiChartKind.Number, Aggregate = BiAggregate.Count });
        BiDashboardService.Save(dashboardPath, dashboard);
        long customersBefore = Convert.ToInt64(database.SelectSQL("SELECT COUNT(*) FROM customers").Rows[0][0]);
        ScheduledJobRunRecord failed = ScheduledJobExecutionService.Execute(biJob, store, () => new NonDisposingDatabase(database));
        Assert(failed.Status != "Success" && File.Exists(failed.OutputPath) && File.ReadAllText(failed.OutputPath).Contains("DELETE") &&
               Convert.ToInt64(database.SelectSQL("SELECT COUNT(*) FROM customers").Rows[0][0]) == customersBefore,
            "A dataset that is not read-only fails the job, is reported in the HTML and never runs.");
        AssertThrows<InvalidOperationException>(() => ScheduledJobValidator.Validate(new ScheduledJobDefinition { Name = "x", Type = ScheduledJobType.BiDashboard, ConnectionName = "c", DatabaseName = "d", InputPath = "a.txt", OutputPath = "o.html" }),
            "BI jobs need a .punkbi file.");
    }

    /// <summary>讓作業執行結束時不關閉測試共用的連線。</summary>
    private sealed class NonDisposingDatabase : IDatabase
    {
        private readonly IDatabase inner;
        public NonDisposingDatabase(IDatabase inner) { this.inner = inner; }
        public void SetConn(string connectionString) { inner.SetConn(connectionString); }
        public void Open() { }
        public void Close() { }
        public System.Data.ConnectionState State { get { return inner.State; } }
        public string ProviderName { get { return inner.ProviderName; } }
        public System.Data.DataTable SelectSQL(string sql, Dictionary<string, object> parameters = null) { return inner.SelectSQL(sql, parameters); }
        public Dictionary<string, string> ExecSQL(string sql, Dictionary<string, object> parameters = null) { return inner.ExecSQL(sql, parameters); }
        public System.Threading.Tasks.Task<System.Data.DataTable> SelectSQLAsync(string sql, Dictionary<string, object> parameters = null) { return inner.SelectSQLAsync(sql, parameters); }
        public System.Threading.Tasks.Task<Dictionary<string, string>> ExecSQLAsync(string sql, Dictionary<string, object> parameters = null) { return inner.ExecSQLAsync(sql, parameters); }
        public List<string> GetDatabases() { return inner.GetDatabases(); }
        public List<string> GetTables(string databaseName) { return inner.GetTables(databaseName); }
        public List<string> GetViews(string databaseName) { return inner.GetViews(databaseName); }
        public System.Data.DataTable GetColumns(string databaseName, string tableName) { return inner.GetColumns(databaseName, tableName); }
        public System.Data.DataTable GetIndexes(string databaseName, string tableName) { return inner.GetIndexes(databaseName, tableName); }
        public System.Data.DataTable GetTableStatus(string databaseName) { return inner.GetTableStatus(databaseName); }
        public Dictionary<string, string> GetDatabaseInfo(string databaseName) { return inner.GetDatabaseInfo(databaseName); }
        public string GetTableCreateStatement(string databaseName, string tableName) { return inner.GetTableCreateStatement(databaseName, tableName); }
        public bool TableExists(string databaseName, string tableName) { return inner.TableExists(databaseName, tableName); }
        public bool ViewExists(string databaseName, string viewName) { return inner.ViewExists(databaseName, viewName); }
        public void RenameTable(string databaseName, string oldTableName, string newTableName) { inner.RenameTable(databaseName, oldTableName, newTableName); }
        public void RenameView(string databaseName, string oldViewName, string newViewName) { inner.RenameView(databaseName, oldViewName, newViewName); }
        public long CountRows(string databaseName, string tableName) { return inner.CountRows(databaseName, tableName); }
        public System.Data.DataTable GetCopyColumns(string databaseName, string tableName) { return inner.GetCopyColumns(databaseName, tableName); }
        public System.Data.DataTable GetCopyIndexes(string databaseName, string tableName) { return inner.GetCopyIndexes(databaseName, tableName); }
        public void CreateTableForCopy(string databaseName, string tableName, System.Data.DataTable sourceColumns, string sourceProvider) { inner.CreateTableForCopy(databaseName, tableName, sourceColumns, sourceProvider); }
        public void DropTableForCopy(string databaseName, string tableName) { inner.DropTableForCopy(databaseName, tableName); }
        public void CreateIndexesForCopy(string databaseName, string tableName, System.Data.DataTable sourceIndexes, string sourceProvider) { inner.CreateIndexesForCopy(databaseName, tableName, sourceIndexes, sourceProvider); }
        public System.Data.DataTable SelectTablePage(string databaseName, string tableName, long offset, int limit) { return inner.SelectTablePage(databaseName, tableName, offset, limit); }
        public void InsertTableBatch(string databaseName, string tableName, System.Data.DataTable rows) { inner.InsertTableBatch(databaseName, tableName, rows); }
        public string GetViewCreateStatement(string databaseName, string viewName) { return inner.GetViewCreateStatement(databaseName, viewName); }
        public void CreateViewFromStatement(string databaseName, string viewName, string sourceViewSql) { inner.CreateViewFromStatement(databaseName, viewName, sourceViewSql); }
        public void Dispose() { }
    }
}
