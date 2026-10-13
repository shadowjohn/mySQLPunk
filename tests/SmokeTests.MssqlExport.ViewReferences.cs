using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using mySQLPunk.lib.MssqlExport;

public static partial class SmokeTests
{
    private static MssqlViewDefinition Reference_View(string definition, bool quotedIdentifier = true)
    {
        return new MssqlViewDefinition(new MssqlObjectKey(90, "dbo", "v"), SchemaFacts("definition", definition,
            "uses_ansi_nulls", true, "uses_quoted_identifier", quotedIdentifier));
    }

    private static MssqlViewReferenceResolution Reference_Local(string database, string schema, string name)
    {
        return new MssqlViewReferenceResolution(7, 7, name == "U" ? 12 : 11, "U");
    }

    private static void Reference_RequireFailure(MssqlViewRewriteResult result, string reason)
    {
        Assert(!result.Success && result.Sql == null && result.ReferencedIds.Count == 0, reason + ": no partially prepared SQL or IDs");
        Assert(result.Issue != null && result.Issue.Code == "UnsupportedViewReference" && result.Issue.ObjectKey.ObjectId == 90,
            reason + ": named safe view issue");
    }

    private static void Reference_RewritesTokens()
    {
        const string sql = "/* prefix \ud83d\ude80 */ CREATE VIEW dbo.old AS WITH c AS (SELECT t.id, N'Transfer.dbo.T' AS txt FROM [Transfer]/* keep . */.[dbo].[T] AS t)\r\nSELECT c.id FROM c JOIN Transfer-- keep line\r\n.dbo.U AS u ON c.id=u.id WHERE EXISTS (SELECT 1 FROM Transfer.dbo.T AS t WHERE t.id=c.id); -- Transfer.dbo.T";
        const string expected = "/* prefix \ud83d\ude80 */ CREATE VIEW [dbo].[v] AS WITH c AS (SELECT t.id, N'Transfer.dbo.T' AS txt FROM /* keep . */[dbo].[T] AS t)\r\nSELECT c.id FROM c JOIN -- keep line\r\ndbo.U AS u ON c.id=u.id WHERE EXISTS (SELECT 1 FROM dbo.T AS t WHERE t.id=c.id); -- Transfer.dbo.T";
        var result = MssqlViewReferenceRewriter.Rewrite(Reference_View(sql), Reference_Local, new int?[] { 12, 11, 11 }, new[] { 11, 12 });
        Assert(result.Success, "Qualified named table references must become target-local: " + result.Issue?.Message);
        Assert(result.Sql == expected, "Only approved qualifier/declaration spans may change; CTEs, aliases, strings, nested references and comments survive");
        Assert(result.ReferencedIds.SequenceEqual(new[] { 11, 12 }), "Catalog normalized IDs cover repeated and nested references without duplicates");
    }

    private static void Reference_QuotedNamesAndHeaders()
    {
        var calls = new List<string>();
        Func<string, string, string, MssqlViewReferenceResolution> resolve = (database, schema, name) =>
        {
            calls.Add(database + "|" + schema + "|" + name);
            return new MssqlViewReferenceResolution(8, 8, 11, "V");
        };
        const string quoted = "-- CREATE VIEW untouched\r\nALTER/* header */ VIEW [old].[name] AS SELECT t.id FROM [Tran]]s.fer].[d]]bo].[T.name] t JOIN \"Tran\"\"s.fer\" . \"dbo\" . \"T\"\"name\" x ON t.id=x.id";
        const string expected = "-- CREATE VIEW untouched\r\nCREATE/* header */ VIEW [dbo].[v] AS SELECT t.id FROM [d]]bo].[T.name] t JOIN   \"dbo\" . \"T\"\"name\" x ON t.id=x.id";
        var result = MssqlViewReferenceRewriter.Rewrite(Reference_View(quoted), resolve, new int?[] { 11 }, new[] { 11 });
        Assert(result.Success && result.Sql == expected, "Delimited identifiers are decoded for resolution and otherwise preserve original quoting");
        Assert(calls.SequenceEqual(new[] { "Tran]s.fer|d]bo|T.name", "Tran\"s.fer|dbo|T\"name" }), "Pass decoded exact identifier parts to SQL Server, without case normalization or splitting embedded dots");
        const string alternate = "/*ALTER VIEW*/ CREATE/*a*/ OR/*b*/ ALTER/*c*/ VIEW dbo.old AS SELECT t.id FROM tRaNsFeR.dbo.T t";
        var changed = MssqlViewReferenceRewriter.Rewrite(Reference_View(alternate), Reference_Local, new int?[] { 11 }, new[] { 11 });
        Assert(changed.Success && changed.Sql == "/*ALTER VIEW*/ CREATE/*a*/ /*b*/ /*c*/ VIEW [dbo].[v] AS SELECT t.id FROM dbo.T t", "Normalize executable CREATE OR ALTER tokens, preserving interleaved comments and server-approved spelling");
        var off = MssqlViewReferenceRewriter.Rewrite(Reference_View("CREATE VIEW dbo.v AS SELECT \"Transfer.dbo.T\" AS txt FROM Transfer.dbo.T", false), Reference_Local, new int?[] { 11 }, new[] { 11 });
        Assert(off.Success && off.Sql == "CREATE VIEW [dbo].[v] AS SELECT \"Transfer.dbo.T\" AS txt FROM dbo.T", "QUOTED_IDENTIFIER OFF double-quoted literal stays literal");
    }

    private static void Reference_RejectsUnsupportedContexts()
    {
        foreach (string body in new[] {
            "SELECT * FROM Transfer..T",
            "SELECT * FROM server.Transfer.dbo.T",
            "SELECT Transfer.dbo.T.id FROM Transfer.dbo.T",
            "SELECT Transfer.dbo.T.* FROM Transfer.dbo.T",
            "SELECT Transfer.dbo.f(t.id) FROM Transfer.dbo.T t",
            "SELECT NEXT VALUE FOR Transfer.dbo.seq FROM Transfer.dbo.T",
            "SELECT * FROM Transfer.dbo.T t CROSS APPLY Transfer.dbo.f(t.id) x",
            "SELECT * FROM Transfer.dbo.T t CROSS JOIN OPENQUERY(remote, 'SELECT id FROM dbo.T') x",
            "SELECT * FROM Transfer.dbo.T t CROSS JOIN OPENROWSET('SQLNCLI', 'Server=remote;Trusted_Connection=yes;', 'SELECT id FROM dbo.T') x",
            "SELECT * FROM Transfer.dbo.T t CROSS JOIN OPENDATASOURCE('SQLNCLI', 'Data Source=remote;Integrated Security=SSPI').db.dbo.T x",
            "SELECT t.shape.STArea() FROM Transfer.dbo.T t" })
        {
            var result = MssqlViewReferenceRewriter.Rewrite(Reference_View("CREATE VIEW dbo.v AS " + body), Reference_Local, new int?[] { 11 }, new[] { 11 });
            Reference_RequireFailure(result, "Unsupported multipart or external reference must fail safely: " + body);
        }
        var localColumn = MssqlViewReferenceRewriter.Rewrite(Reference_View("CREATE VIEW dbo.v AS SELECT dbo.T.id FROM Transfer.dbo.T"), Reference_Local, new int?[] { 11 }, new[] { 11 });
        Assert(localColumn.Success && localColumn.Sql == "CREATE VIEW [dbo].[v] AS SELECT dbo.T.id FROM dbo.T", "Three-part column name is schema.table.column and must not be rewritten as database.schema.table");
    }

    private static void Reference_RequiresCatalogIdentity()
    {
        var view = Reference_View("CREATE VIEW dbo.v AS SELECT * FROM Transfer.dbo.T");
        foreach (var identity in new[] {
            new MssqlViewReferenceResolution(7, 8, 11, "U"),
            new MssqlViewReferenceResolution(7, null, 11, "U"),
            new MssqlViewReferenceResolution(null, null, 11, "U"),
            new MssqlViewReferenceResolution(0, 0, 11, "U"),
            new MssqlViewReferenceResolution(7, 7, null, "U"),
            new MssqlViewReferenceResolution(7, 7, 0, "U"),
            new MssqlViewReferenceResolution(7, 7, 11, "SN"),
            new MssqlViewReferenceResolution(7, 7, 11, "FN"),
            new MssqlViewReferenceResolution(7, 7, 12, "U"),
            null })
            Reference_RequireFailure(MssqlViewReferenceRewriter.Rewrite(view, (d, s, n) => identity, new int?[] { 11 }, new[] { 11 }), "Unknown, external, missing, unsupported or unselected identity");
        foreach (var ids in new[] { new int?[] { }, new int?[] { null }, new int?[] { 12 }, new int?[] { 11, 12 } })
            Reference_RequireFailure(MssqlViewReferenceRewriter.Rewrite(view, Reference_Local, ids, new[] { 11, 12 }), "Catalog-to-AST coverage must agree in both directions");
        var secondSource = Reference_View("CREATE VIEW dbo.v AS SELECT t.id FROM Transfer.dbo.T t JOIN DifferentSpelling.dbo.U u ON t.id=u.id");
        Reference_RequireFailure(MssqlViewReferenceRewriter.Rewrite(secondSource, (d, s, n) => new MssqlViewReferenceResolution(n == "T" ? 7 : 8, n == "T" ? 7 : 8, n == "T" ? 11 : 12, "U"), new int?[] { 11, 12 }, new[] { 11, 12 }), "Every occurrence must resolve against the same source database");
        bool called = false;
        var longName = Reference_View("CREATE VIEW dbo.v AS SELECT * FROM [" + new string('a', 129) + "].dbo.T");
        Reference_RequireFailure(MssqlViewReferenceRewriter.Rewrite(longName, (d, s, n) => { called = true; return Reference_Local(d, s, n); }, new int?[] { 11 }, new[] { 11 }), "Oversized identifiers cannot be truncated by SQL parameters");
        Assert(!called, "Reject oversized identifiers before invoking the catalog resolver");
        var error = new OperationCanceledException("private diagnostic sentinel");
        try { MssqlViewReferenceRewriter.Rewrite(view, (d, s, n) => { throw error; }, new int?[] { 11 }, new[] { 11 }); throw new Exception("Expected resolver cancellation"); }
        catch (OperationCanceledException actual) { Assert(object.ReferenceEquals(actual, error), "Catalog cancellation retains ownership and is not converted into an unsafe partial success"); }
    }

    private static void Reference_RejectsInvalidModules()
    {
        foreach (string sql in new[] {
            "", "CREATE VIEW dbo.v AS SELECT FROM Transfer.dbo.T",
            "CREATE VIEW dbo.v AS SELECT * FROM Transfer.dbo.T;\r\nGO\r\nSELECT 1",
            "CREATE VIEW dbo.v AS SELECT * FROM Transfer.dbo.T;\r\nGO",
            "SELECT * FROM Transfer.dbo.T", "/* private sentinel */ CREATE VIEW dbo.v AS SELECT 'unterminated FROM Transfer.dbo.T" })
        {
            var result = MssqlViewReferenceRewriter.Rewrite(Reference_View(sql), Reference_Local, new int?[] { 11 }, new[] { 11 });
            Reference_RequireFailure(result, "Invalid or multi-batch module is not a view rewrite candidate");
            AssertNotContains(result.Issue.Message, "private sentinel", "Parser diagnostics must not copy source text");
        }
    }

    private static MssqlExportSchema Reference_Schema(IEnumerable<MssqlDependency> dependencies = null, string viewText = null)
    {
        var table = new MssqlTableDefinition(new MssqlObjectKey(11, "dbo", "T"), SchemaFacts(),
            new[] { new MssqlColumnDefinition(SchemaFacts("column_id", 1, "name", "id", "type_name", "int")) }, null, null);
        var first = new MssqlViewDefinition(new MssqlObjectKey(91, "dbo", "first"), SchemaFacts("definition", viewText ?? "CREATE VIEW dbo.first AS SELECT * FROM Transfer.dbo.T", "uses_ansi_nulls", true, "uses_quoted_identifier", true));
        var last = new MssqlViewDefinition(new MssqlObjectKey(90, "dbo", "last"), SchemaFacts("definition", "ALTER VIEW dbo.last AS SELECT * FROM Transfer.dbo.first", "uses_ansi_nulls", true, "uses_quoted_identifier", true));
        return new MssqlExportSchema(new[] { table }, new[] { last, first }, dependencies ?? new[] {
            new MssqlDependency(91, null, null, "Transfer", "dbo", "T", 7, 7, 11),
            new MssqlDependency(90, null, null, "Transfer", "dbo", "first", 7, 7, 91) }, 1, null);
    }

    private static MssqlViewReferenceResolution Reference_ResolveSchema(string database, string schema, string name)
    { return new MssqlViewReferenceResolution(7, 7, name == "first" ? 91 : name == "last" ? 90 : 11, name == "T" ? "U" : "V"); }

    private static void Reference_IntegratesPreparationAndOrdering()
    {
        var raw = Reference_Schema();
        var request = SchemaRequest(raw);
        var prepared = MssqlExportMetadataReader.PrepareViewReferences(raw, request, Reference_ResolveSchema);
        var issues = new MssqlExportMetadataReader().Validate(prepared, request);
        Assert(issues.Count == 0, "Catalog-proven same-source view dependencies must pass after preparation: " + string.Join("; ", issues.Select(x => x.Code)));
        Assert(prepared.Views.All(v => v.HasPreparedReferences) && prepared.Views.Single(v => v.Key.ObjectId == 91).Definition == raw.Views.Single(v => v.Key.ObjectId == 91).Definition,
            "Preparation retains original source definitions separately from target-local SQL");
        Assert(prepared.Dependencies.All(d => d.ReferencedId == null && d.EffectiveReferencedId.HasValue), "Keep raw nullable IDs while authorizing effective local IDs");
        var output = new StringWriter();
        new MssqlSchemaScriptWriter().WriteAfterData(prepared, request, output, CancellationToken.None);
        string sql = output.ToString();
        AssertNotContains(sql, "Transfer.dbo", "Writer must consume prepared view bodies");
        Assert(sql.IndexOf("CREATE VIEW [dbo].[first]", StringComparison.Ordinal) < sql.IndexOf("CREATE VIEW [dbo].[last]", StringComparison.Ordinal), "Normalized IDs order views even when object IDs suggest the reverse");
        Assert(raw.Dependencies.All(d => !d.EffectiveReferencedId.HasValue), "Preparation does not mutate original dependency objects");
        var cycleRaw = Reference_Schema(new[] {
            new MssqlDependency(91, null, null, "Transfer", "dbo", "last", 7, 7, 90),
            new MssqlDependency(90, null, null, "Transfer", "dbo", "first", 7, 7, 91) }, "CREATE VIEW dbo.first AS SELECT * FROM Transfer.dbo.last");
        var cycle = MssqlExportMetadataReader.PrepareViewReferences(cycleRaw, SchemaRequest(cycleRaw), Reference_ResolveSchema);
        Assert(new MssqlExportMetadataReader().Validate(cycle, SchemaRequest(cycle)).Any(x => x.Code == "DependencyCycle"), "Catalog-normalized view cycles remain rejected");
    }

    private static void Reference_IntegrationGatesAndFingerprint()
    {
        var original = Reference_Schema();
        var request = SchemaRequest(original);
        foreach (var bad in new[] {
            new MssqlDependency(91, null, null, "Other", "dbo", "T", 7, 8, 11),
            new MssqlDependency(91, null, "local", "Transfer", "dbo", "T", 7, 7, 11),
            new MssqlDependency(91, null, null, "Transfer", null, "T", 7, 7, 11),
            new MssqlDependency(91, null, null, "Transfer", "dbo", "T", 7, 7, 999),
            new MssqlDependency(91, 999, null, "Transfer", "dbo", "T", 7, 7, 11),
            new MssqlDependency(91, null, null, "Transfer", "dbo", "T", 7, 7, 11, isAmbiguous: true),
            new MssqlDependency(91, null, null, "Transfer", "dbo", "T", 7, 7, 11, isCallerDependent: true),
            new MssqlDependency(11, null, null, "Transfer", "dbo", "T", 7, 7, 11, preparedSameDatabaseReference: true) })
        {
            var candidate = Reference_Schema(new[] { bad });
            var result = MssqlExportMetadataReader.PrepareViewReferences(candidate, SchemaRequest(candidate), Reference_ResolveSchema);
            Assert(new MssqlExportMetadataReader().Validate(result, SchemaRequest(result)).Any(x => x.Code == "MissingDependency"), "Invalid identity, flags, selected scope and table expressions must remain blocked");
            AssertThrows<InvalidOperationException>(() => new MssqlSchemaScriptWriter().WriteBeforeData(result, SchemaRequest(result), new StringWriter(), CancellationToken.None), "No DDL can precede qualified-reference failure");
        }
        Assert(new MssqlExportMetadataReader().Validate(original, request).Any(x => x.Code == "MissingDependency"), "Identity resolution alone never authorizes unrevised source-qualified definitions");
        var prepared = MssqlExportMetadataReader.PrepareViewReferences(original, request, Reference_ResolveSchema);
        var reversed = new MssqlExportSchema(prepared.Tables, prepared.Views, prepared.Dependencies.Reverse(), 1, null);
        Assert(prepared.Fingerprint == reversed.Fingerprint, "Dependency catalog order must not create schema drift");
        var changedText = Reference_Schema(viewText: "CREATE VIEW dbo.first AS SELECT * FROM Transfer.dbo.T -- changed source");
        Assert(prepared.Fingerprint != MssqlExportMetadataReader.PrepareViewReferences(changedText, SchemaRequest(changedText), Reference_ResolveSchema).Fingerprint, "Original source text remains part of schema drift detection");
        var changedIdentity = Reference_Schema(new[] { new MssqlDependency(91, null, null, "Transfer", "dbo", "T", 7, 8, 11), original.Dependencies[1] });
        Assert(original.Fingerprint != changedIdentity.Fingerprint, "Resolved database identity participates in schema fingerprint");
        var dataOnly = SchemaRequest(original, MssqlExportMode.DataOnly);
        var untouched = MssqlExportMetadataReader.PrepareViewReferences(original, dataOnly, (d, s, n) => { throw new Exception("Data-only must not resolve view references"); });
        Assert(object.ReferenceEquals(original, untouched), "Data-only retains the existing schema behavior without view rewriting");
    }
}
