using System;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using mySQLPunk.lib.MssqlExport;

public static partial class SmokeTests
{
    private const string CompatibilityPropertiesSql = @"SELECT SCHEMA_NAME(o.schema_id) owner_schema,o.name owner_name,o.type owner_type,
CASE WHEN e.minor_id=0 THEN 'OBJECT' ELSE 'COLUMN' END property_scope,c.name column_name,e.name property_name,
CONVERT(nvarchar(128),SQL_VARIANT_PROPERTY(e.value,'BaseType')) base_type,
CONVERT(int,SQL_VARIANT_PROPERTY(e.value,'MaxLength')) max_length,
CONVERT(int,SQL_VARIANT_PROPERTY(e.value,'Precision')) precision_value,
CONVERT(int,SQL_VARIANT_PROPERTY(e.value,'Scale')) scale_value,
CONVERT(nvarchar(128),SQL_VARIANT_PROPERTY(e.value,'Collation')) collation_name,
CASE WHEN e.value IS NULL THEN 1 ELSE 0 END is_null,
CONVERT(varchar(max),CONVERT(varbinary(8000),e.value),2) native_hex
FROM sys.extended_properties e JOIN sys.objects o ON o.object_id=e.major_id
LEFT JOIN sys.columns c ON c.object_id=e.major_id AND c.column_id=e.minor_id
WHERE e.class=1 AND o.is_ms_shipped=0";

    private static void IntegrationProperties(IntegrationFixture f)
    {
        Assert(Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_INTEGRATION_CLIENT") == "Classic", "Properties requires documented classic sqlcmd");
        f.Setup("compatibility.sql");
        using (var c = f.Open(f.Source))
        {
            Assert(Convert.ToInt32(IntegrationFixture.Scalar(c, "SELECT DATALENGTH(value) FROM sys.extended_properties WHERE class=1 AND major_id=OBJECT_ID(N'dbo.PropertyView') AND name=N'MS_DiagramPane1'")) == 7006, "Production-sized 7006-byte SSMS diagram fixture");
            Assert(Convert.ToInt32(IntegrationFixture.Scalar(c, "SELECT DATALENGTH(value) FROM sys.extended_properties WHERE class=1 AND major_id=OBJECT_ID(N'dbo.LongView') AND name=N'MS_DiagramPane1'")) == 7400, "Near-limit 7400-byte diagram fixture");
            Assert(Convert.ToInt32(IntegrationFixture.Scalar(c, "SELECT column_id FROM sys.columns WHERE object_id=OBJECT_ID(N'[報表].[Property]') AND name=N'note欄'")) == 3, "Source column property follows a dropped-column gap");
            f.Evidence["property-source-minor-ids"] = IntegrationCatalog(c, "SELECT major_id,minor_id,name FROM sys.extended_properties WHERE class=1");
        }
        var all = f.Request("properties-all.sql");
        var check = f.Service().Preflight(all, CancellationToken.None);
        Assert(check.CanStart, "Supported actual metadata passes: " + string.Join(";", check.Issues.Select(x => x.Code + ":" + x.Message)));
        var result = f.Export(all);
        Assert(result.Stage == MssqlExportStage.Completed, "Properties schema/data service artifact: " + result.ErrorMessage);
        f.RecordArtifact(result.PublishedPath); f.Restore(result.PublishedPath);
        CompatibilityCompareProperties(f, "schema-data");
        using (var c = f.Open(f.Target))
        {
            Assert(Convert.ToInt32(IntegrationFixture.Scalar(c, "SELECT COUNT(*) FROM [報表].[Property] WHERE [note欄]=N'exported'")) == 1, "Properties data restored");
            Assert(Convert.ToInt32(IntegrationFixture.Scalar(c, "SELECT column_id FROM sys.columns WHERE object_id=OBJECT_ID(N'[報表].[Property]') AND name=N'note欄'")) == 2, "Restored properties resolve by column name after renumbering");
            f.Evidence["property-target-minor-ids"] = IntegrationCatalog(c, "SELECT major_id,minor_id,name FROM sys.extended_properties WHERE class=1");
            IntegrationFixture.Exec(c, "DROP VIEW dbo.LongView; DROP VIEW dbo.PropertyView; DROP TABLE [報表].[Property];");
        }
        var schema = f.Request("properties-schema.sql", true, MssqlExportMode.SchemaOnly);
        result = f.Export(schema);
        Assert(result.Stage == MssqlExportStage.Completed, "Property schema-only export: " + result.ErrorMessage);
        f.RecordArtifact(result.PublishedPath); f.Restore(result.PublishedPath);
        CompatibilityCompareProperties(f, "schema-only");
        string beforeData;
        using (var c = f.Open(f.Target))
        {
            Assert(Convert.ToInt32(IntegrationFixture.Scalar(c, "SELECT COUNT(*) FROM [報表].[Property]")) == 0, "Schema-only does not insert rows");
            IntegrationFixture.Exec(c, "EXEC sys.sp_addextendedproperty @name=N'target-only',@value=N'keep',@level0type=N'SCHEMA',@level0name=N'報表',@level1type=N'TABLE',@level1name=N'Property';");
            beforeData = IntegrationCatalog(c, CompatibilityPropertiesSql);
        }
        var data = f.Request("properties-data.sql", false, MssqlExportMode.DataOnly);
        result = f.Export(data);
        Assert(result.Stage == MssqlExportStage.Completed, "Property data-only export: " + result.ErrorMessage);
        AssertNotContains(File.ReadAllText(result.PublishedPath), "sp_addextendedproperty", "DataOnly emits no duplicate properties");
        f.RecordArtifact(result.PublishedPath); f.Restore(result.PublishedPath);
        using (var c = f.Open(f.Target))
        {
            Assert(IntegrationCatalog(c, CompatibilityPropertiesSql) == beforeData, "DataOnly preserves every preexisting target property");
            Assert(Convert.ToInt32(IntegrationFixture.Scalar(c, "SELECT COUNT(*) FROM [報表].[Property]")) == 1, "DataOnly restored one row after schema-only");
        }
        f.Pass("Typed object/column/view properties preserve scope type length precision scale collation hex and NULL through all three modes");
        CompatibilityPropertyDrift(f);
        CompatibilityPropertyGates(f);
        CompatibilityPermissions(f);
    }

    private static void CompatibilityCompareProperties(IntegrationFixture f, string phase)
    {
        using (var a = f.Open(f.Source)) using (var b = f.Open(f.Target))
        {
            string source = IntegrationCatalog(a, CompatibilityPropertiesSql), target = IntegrationCatalog(b, CompatibilityPropertiesSql);
            File.WriteAllText(Path.Combine(f.Root, "properties-" + phase + "-source.jsonl"), source);
            File.WriteAllText(Path.Combine(f.Root, "properties-" + phase + "-target.jsonl"), target);
            Assert(source == target, "Native property catalog equality " + phase + "; compare semantic column scope, not recreated column IDs");
        }
    }

    private static string CompatibilityUpdateProperty(string text, string collation)
    {
        return "DECLARE @v sql_variant; SET @v=CONVERT(nvarchar(30)," + IntegrationFixture.Lit(text) + ") COLLATE " + collation + "; EXEC sys.sp_updateextendedproperty @name=N'drift',@value=@v,@level0type=N'SCHEMA',@level0name=N'報表',@level1type=N'TABLE',@level1name=N'Property';";
    }

    private static void CompatibilityPropertyDrift(IntegrationFixture f)
    {
        var request = f.Request("property-fingerprint.sql", true, MssqlExportMode.SchemaOnly);
        string original = f.Service().Preflight(request, CancellationToken.None).Schema.Fingerprint;
        using (var c = f.Open(f.Source)) IntegrationFixture.Exec(c, CompatibilityUpdateProperty("drift-changed", "Chinese_Taiwan_Stroke_CI_AS"));
        string valueChanged = f.Service().Preflight(request, CancellationToken.None).Schema.Fingerprint;
        Assert(original != valueChanged, "Actual property value participates in source fingerprint");
        using (var c = f.Open(f.Source)) IntegrationFixture.Exec(c, CompatibilityUpdateProperty("drift-changed", "Latin1_General_100_BIN2"));
        string collationChanged = f.Service().Preflight(request, CancellationToken.None).Schema.Fingerprint;
        Assert(valueChanged != collationChanged, "Actual property collation participates in source fingerprint");
        foreach (string kind in new[] { "value", "collation" })
        {
            using (var c = f.Open(f.Source)) IntegrationFixture.Exec(c, CompatibilityUpdateProperty("drift-original", "Chinese_Taiwan_Stroke_CI_AS"));
            string path = Path.Combine(f.Root, "property-drift-" + kind + ".sql");
            File.WriteAllText(path, "original");
            request = f.Request(Path.GetFileName(path), true, MssqlExportMode.SchemaOnly);
            bool changed = false;
            f.AfterBegin = () => { using (var c = f.Open(f.Source)) IntegrationFixture.Exec(c, CompatibilityUpdateProperty(kind == "value" ? "drift-changed" : "drift-original", kind == "value" ? "Chinese_Taiwan_Stroke_CI_AS" : "Latin1_General_100_BIN2")); changed = true; };
            try
            {
                var result = f.Export(request);
                Assert(changed && result.Stage == MssqlExportStage.Failed && result.PublishedPath == null && File.ReadAllText(path) == "original", "Live " + kind + " drift rejects publication and preserves original");
                Assert(!Directory.GetFiles(f.Root, "*.partial", SearchOption.AllDirectories).Any(), "Drift partial artifacts cleaned");
            }
            finally { f.AfterBegin = null; }
        }
        f.Pass("Actual value/collation drift changes fingerprint and rejects frozen-schema publication");
    }

    private static void CompatibilityPropertyGates(IntegrationFixture f)
    {
        foreach (string type in new[] { "varchar(20)", "decimal(8,2)" })
        {
            string value = type.StartsWith("varchar", StringComparison.Ordinal) ? "'unsupported'" : "12.34";
            using (var c = f.Open(f.Source)) IntegrationFixture.Exec(c, "DECLARE @v " + type + "=" + value + "; EXEC sys.sp_addextendedproperty @name=N'unsupported',@value=@v,@level0type=N'SCHEMA',@level0name=N'報表',@level1type=N'TABLE',@level1name=N'Property';");
            try { CompatibilityExpectBlocked(f, "property-unsupported-" + type.Split('(')[0], x => x.Code.IndexOf("Property", StringComparison.OrdinalIgnoreCase) >= 0); }
            finally { using (var c = f.Open(f.Source)) IntegrationFixture.Exec(c, "EXEC sys.sp_dropextendedproperty @name=N'unsupported',@level0type=N'SCHEMA',@level0name=N'報表',@level1type=N'TABLE',@level1name=N'Property';"); }
        }
        using (var c = f.Open(f.Source))
        {
            IntegrationFixture.Exec(c, "SET ANSI_NULLS ON; SET QUOTED_IDENTIFIER ON; SET ANSI_PADDING ON; SET ANSI_WARNINGS ON; SET CONCAT_NULL_YIELDS_NULL ON; SET ARITHABORT ON; SET NUMERIC_ROUNDABORT OFF;");
            IntegrationFixture.Exec(c, "CREATE VIEW dbo.IndexedProperty WITH SCHEMABINDING AS SELECT id FROM [報表].[Property];");
            IntegrationFixture.Exec(c, "CREATE UNIQUE CLUSTERED INDEX IX_IndexedProperty ON dbo.IndexedProperty(id);");
        }
        try { CompatibilityExpectBlocked(f, "indexed-view", x => x.Code == "UnsupportedViewFeature"); }
        finally { using (var c = f.Open(f.Source)) IntegrationFixture.Exec(c, "DROP VIEW dbo.IndexedProperty;"); }
        using (var c = f.Open(f.Source)) IntegrationFixture.Exec(c, "CREATE TRIGGER dbo.PropertyTrigger ON dbo.PropertyView INSTEAD OF INSERT AS BEGIN SET NOCOUNT ON; END;");
        try { CompatibilityExpectBlocked(f, "view-trigger", x => x.Code == "UnsupportedViewFeature"); }
        finally { using (var c = f.Open(f.Source)) IntegrationFixture.Exec(c, "DROP TRIGGER dbo.PropertyTrigger;"); }
        f.Pass("Unsupported varchar/decimal properties indexed views and view triggers still fail before artifact publication");
    }

    private static void CompatibilityExpectBlocked(IntegrationFixture f, string name, Func<MssqlExportIssue, bool> expected, Func<MssqlTableDefinition, bool> select = null)
    {
        string path = Path.Combine(f.Root, name + ".sql");
        File.WriteAllText(path, "original");
        var request = f.Request(Path.GetFileName(path), true, MssqlExportMode.SchemaOnly, select);
        var check = f.Service().Preflight(request, CancellationToken.None);
        f.Evidence["blocked-" + name] = check.Issues.Select(x => x.Code + ":" + x.Message).ToArray();
        Assert(!check.CanStart && check.Issues.Any(expected), "Expected preflight refusal " + name);
        var result = f.Export(request);
        Assert(result.Stage == MssqlExportStage.Failed && result.PublishedPath == null && File.ReadAllText(path) == "original", "Rejected " + name + " preserves original and publishes nothing");
        Assert(!Directory.GetFiles(f.Root, "*.partial", SearchOption.AllDirectories).Any(), "Rejected " + name + " leaves no partial");
    }

    private static void CompatibilityPermissions(IntegrationFixture f)
    {
        var data = f.Request("permission-data.sql", false, useSnapshot: false);
        var schema = f.Request("permission-schema.sql", false, MssqlExportMode.SchemaOnly);
        using (var c = f.Open(f.Source)) IntegrationFixture.Exec(c, "CREATE USER fixture_reader WITHOUT LOGIN; GRANT CONNECT TO fixture_reader; GRANT SELECT TO fixture_reader; DENY VIEW DEFINITION TO fixture_reader;");
        f.RestrictedReader = true;
        try
        {
            try { f.Service().LoadCatalog(f.Source, CancellationToken.None); Assert(false, "Missing VIEW DEFINITION must fail"); }
            catch (MssqlExportPrerequisiteException ex) { Assert(ex.Reason == MssqlPrerequisite.ViewDefinitionRequired, "Live missing VIEW DEFINITION keeps typed diagnostic"); }
            var failed = f.Export(schema);
            Assert(failed.Stage == MssqlExportStage.Failed && failed.PublishedPath == null && !File.Exists(schema.TargetPath), "Live missing definitions cannot publish schema-only artifact");
            using (var c = f.Open(f.Source)) IntegrationFixture.Exec(c, "REVOKE VIEW DEFINITION FROM fixture_reader; GRANT VIEW DEFINITION TO fixture_reader; GRANT SELECT ON OBJECT::sys.sql_expression_dependencies TO fixture_reader;");
            using (var c = f.Open(f.Source))
            {
                IntegrationFixture.Exec(c, "EXECUTE AS USER=N'fixture_reader';");
                Assert(Convert.ToInt32(IntegrationFixture.Scalar(c, "SELECT HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','ALTER')")) == 0, "Fixture reader has no database ALTER permission");
            }
            Assert(f.Service().Preflight(data, CancellationToken.None).CanStart, "Live ordinary reader with definitions and SELECT can export");
            var selected = f.Request("permission-snapshot.sql", false, useSnapshot: true);
            Assert(!f.Service().Preflight(selected, CancellationToken.None).CanStart, "Selected Snapshot without ALTER is refused even ON");
            var dataResult = f.Export(data);
            Assert(dataResult.Stage == MssqlExportStage.Completed, "Actual ordinary data export does not require ALTER: " + dataResult.ErrorMessage);
            using (var c = f.Open(f.Source)) IntegrationFixture.Exec(c, "DENY SELECT ON OBJECT::[報表].[Property] TO fixture_reader;");
            Assert(f.Service().Preflight(data, CancellationToken.None).Issues.Any(x => x.Code == "UnreadableTable"), "Live SELECT denial blocks data mode");
            Assert(f.Service().Preflight(schema, CancellationToken.None).CanStart, "Definitions without table SELECT permit schema-only");
            var schemaResult = f.Export(schema);
            Assert(schemaResult.Stage == MssqlExportStage.Completed, "Actual low-privilege schema-only artifact: " + schemaResult.ErrorMessage);
            using (var c = f.Open(f.Source)) IntegrationFixture.Exec(c, "DENY SELECT ON OBJECT::sys.sql_expression_dependencies TO fixture_reader;");
            try { f.Service().Preflight(schema, CancellationToken.None); Assert(false, "Denied dependency catalog must fail"); }
            catch (MssqlExportPrerequisiteException ex) { Assert(ex.Reason == MssqlPrerequisite.SqlPermissionDenied, "Live dependency catalog denial keeps safe permission category"); }
        }
        finally { f.RestrictedReader = false; }
        f.Pass("Owned WITHOUT LOGIN user distinguishes definition SELECT and dependency-catalog permissions without production or server grants");
    }

    private static void IntegrationReferences(IntegrationFixture f)
    {
        Assert(Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_INTEGRATION_CLIENT") == "Classic", "References requires documented classic sqlcmd");
        string db = IntegrationFixture.Q(f.Source), literal = f.Source + ".dbo.RefTable stays literal", comment = "/* " + f.Source + ".dbo.RefTable stays comment */";
        using (var c = f.Open(f.Source))
        {
            IntegrationFixture.Exec(c, "SET ANSI_NULLS ON; SET QUOTED_IDENTIFIER ON;");
            IntegrationFixture.Exec(c, "CREATE TABLE dbo.RefTable(id int NOT NULL PRIMARY KEY,payload nvarchar(40) NOT NULL); INSERT dbo.RefTable VALUES(1,N'exported'); CREATE TABLE dbo.Unselected(id int);");
            // Give the dependent view the lower object_id so restore must honor dependencies.
            IntegrationFixture.Exec(c, "CREATE VIEW [dbo].[Ref Nested] AS SELECT 1 AS id,CONVERT(nvarchar(40),N'placeholder') AS payload;");
            IntegrationFixture.Exec(c, "CREATE VIEW dbo.RefBase AS SELECT t.id,t.payload," + IntegrationFixture.Lit(literal) + " AS marker FROM " + db + " /* qualifier retained */ . [dbo] . [RefTable] AS t " + comment + ";");
            IntegrationFixture.Exec(c, "ALTER VIEW [dbo].[Ref Nested] AS WITH cte AS (SELECT a.id,a.payload FROM " + db + ".[dbo].[RefBase] AS a) SELECT cte.id,cte.payload FROM cte JOIN (SELECT z.id FROM \"" + f.Source + "\".\"dbo\".\"RefTable\" AS z) AS b ON b.id=cte.id;");
        }
        var request = f.Request("references.sql", true, MssqlExportMode.SchemaAndData, t => t.Key.Name == "RefTable");
        var check = f.Service().Preflight(request, CancellationToken.None);
        Assert(check.CanStart, "Same-database references pass: " + string.Join(";", check.Issues.Select(x => x.Code + ":" + x.Message)));
        var result = f.Export(request);
        Assert(result.Stage == MssqlExportStage.Completed, "Reference service artifact: " + result.ErrorMessage);
        string script = File.ReadAllText(result.PublishedPath);
        AssertContains(script, literal, "Database-like string unchanged");
        AssertContains(script, comment, "Database-like comment unchanged");
        AssertContains(script, "/* qualifier retained */", "Inter-token comment unchanged");
        using (var c = f.Open(f.Source)) IntegrationFixture.Exec(c, "UPDATE dbo.RefTable SET payload=N'source_only';");
        f.RecordArtifact(result.PublishedPath); f.Restore(result.PublishedPath);
        using (var c = f.Open(f.Target))
        {
            foreach (string view in new[] { "dbo.RefBase", "dbo.[Ref Nested]" }) Assert(Convert.ToString(IntegrationFixture.Scalar(c, "SELECT payload FROM " + view)) == "exported", "Restored view reads target exported row: " + view);
            Assert(Convert.ToString(IntegrationFixture.Scalar(c, "SELECT marker FROM dbo.RefBase")) == literal, "Restored view literal unchanged");
            Assert(Convert.ToInt32(IntegrationFixture.Scalar(c, "SELECT COUNT(*) FROM sys.sql_expression_dependencies d JOIN sys.views v ON v.object_id=d.referencing_id WHERE d.referenced_server_name IS NOT NULL OR d.referenced_database_name IS NOT NULL OR d.referenced_id IS NULL")) == 0, "All target view dependencies are local resolved IDs");
            IntegrationFixture.Exec(c, "UPDATE dbo.RefTable SET payload=N'target_only';");
            foreach (string view in new[] { "dbo.RefBase", "dbo.[Ref Nested]" }) Assert(Convert.ToString(IntegrationFixture.Scalar(c, "SELECT payload FROM " + view)) == "target_only", "Target updates flow through both view levels: " + view);
        }
        using (var c = f.Open(f.Source)) Assert(Convert.ToString(IntegrationFixture.Scalar(c, "SELECT payload FROM dbo.RefBase")) == "source_only", "Retained source remains independently changed");
        f.Pass("Same-database quoted CTE nested/join/view references restore into a different database with target-local semantics");
        CompatibilityReferenceGates(f);
    }

    private static void CompatibilityReferenceGates(IntegrationFixture f)
    {
        string db = IntegrationFixture.Q(f.Source), target = IntegrationFixture.Q(f.Target);
        var definitions = new[] {
            new { Name="cross-database", Sql="SELECT t.id FROM " + target + ".dbo.RefTable t" },
            new { Name="missing-schema", Sql="SELECT t.id FROM " + db + "..RefTable t" },
            new { Name="unselected", Sql="SELECT t.id FROM " + db + ".dbo.Unselected t" },
            new { Name="qualified-column", Sql="SELECT " + db + ".dbo.RefTable.id FROM " + db + ".dbo.RefTable" }
        };
        foreach (var item in definitions)
        {
            using (var c = f.Open(f.Source)) IntegrationFixture.Exec(c, "CREATE VIEW dbo.RejectReference AS " + item.Sql + ";");
            try { CompatibilityExpectBlocked(f, "reference-" + item.Name, x => x.Code == "MissingDependency" || x.Code == "UnsupportedViewReference", t => t.Key.Name == "RefTable"); }
            finally { using (var c = f.Open(f.Source)) IntegrationFixture.Exec(c, "DROP VIEW dbo.RejectReference;"); }
        }
        // Four-part named reference to this existing local server: no linked server is created.
        string server;
        using (var c = f.Open(f.Source)) server = Convert.ToString(IntegrationFixture.Scalar(c, "SELECT CONVERT(nvarchar(128),SERVERPROPERTY('ServerName'))"), CultureInfo.InvariantCulture);
        bool created = false;
        try
        {
            using (var c = f.Open(f.Source))
            {
                try { IntegrationFixture.Exec(c, "CREATE VIEW dbo.RejectServer AS SELECT t.id FROM " + IntegrationFixture.Q(server) + "." + db + ".dbo.RefTable t;"); created = true; }
                catch (SqlException ex) { f.Evidence["server-reference-fixture-not-creatable"] = ex.Number; }
            }
            if (created) CompatibilityExpectBlocked(f, "reference-server", x => x.Code == "MissingDependency" || x.Code == "UnsupportedViewReference", t => t.Key.Name == "RefTable");
        }
        finally { if (created) using (var c = f.Open(f.Source)) IntegrationFixture.Exec(c, "DROP VIEW dbo.RejectServer;"); }
        f.Pass("Cross-database omitted-schema unselected-object and four-part-column references remain blocked");
        if (created) f.Pass("Existing local server four-part reference remains blocked without configuring linked servers");
        else { Console.WriteLine("SKIP server-reference live fixture: local provider rejected its creation; parser fixture remains separate evidence"); f.Save(); }
    }
}
