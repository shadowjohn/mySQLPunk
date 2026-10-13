using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using mySQLPunk.lib.MssqlExport;

public static partial class SmokeTests
{
    private static string PropertiesHex(string text)
    {
        var hex = new StringBuilder();
        foreach (char c in text) { hex.Append(((int)c & 255).ToString("X2", CultureInfo.InvariantCulture)); hex.Append(((int)c >> 8).ToString("X2", CultureInfo.InvariantCulture)); }
        return hex.ToString();
    }
    private static MssqlExtendedPropertyDefinition PropertiesText(MssqlObjectKey owner, string ownerType, int minorId, string columnName, string name, string value, int? declaredBytes = null, string collation = "Chinese_Taiwan_Stroke_CI_AS")
    {
        // A separately allocated key represents a native catalog row, not object reference equality.
        return new MssqlExtendedPropertyDefinition(new MssqlObjectKey(owner.ObjectId, owner.Schema, owner.Name), ownerType,
            SchemaFacts("minor_id", minorId, "column_name", columnName, "name", name, "base_type", value == null ? null : "nvarchar",
                "max_length", value == null ? null : (object)(declaredBytes ?? Math.Max(2, value.Length * 2)), "collation", value == null ? null : collation,
                "is_null", value == null, "value_hex", value == null ? null : PropertiesHex(value)));
    }
    private static MssqlExportSchema PropertiesFixture(IEnumerable<MssqlExtendedPropertyDefinition> properties = null)
    {
        var tableKey = new MssqlObjectKey(901, "dbo", "station");
        var viewKey = new MssqlObjectKey(902, "dbo", "station_view");
        var column = new MssqlColumnDefinition(SchemaFacts("column_id", 1, "name", "id", "type_name", "int", "comment", "old display comment"));
        var table = new MssqlTableDefinition(tableKey, SchemaFacts("comment", "old table display comment"), new[] { column }, null, null);
        var view = new MssqlViewDefinition(viewKey, SchemaFacts("definition", "CREATE VIEW dbo.station_view AS SELECT id FROM dbo.station", "uses_ansi_nulls", true, "uses_quoted_identifier", true));
        return new MssqlExportSchema(new[] { table }, new[] { view }, null, 1, null, extendedProperties: properties);
    }
    private static void Properties_AllowsOrdinaryNamedProperties()
    {
        var original = PropertiesFixture(); var table = original.Tables[0].Key; var view = original.Views[0].Key;
        var props = new[] {
            PropertiesText(table, "TABLE", 0, null, "WRA_WATERLEVEL_STATION", "station"),
            PropertiesText(view, "VIEW", 0, null, "MS_DiagramPane1", new string('\u4e2d', 3503)),
            new MssqlExtendedPropertyDefinition(view, "VIEW", SchemaFacts("minor_id", 0, "name", "MS_DiagramPaneCount", "base_type", "int", "max_length", 4, "is_null", false, "value_hex", "00000001")) };
        var schema = PropertiesFixture(props);
        var issues = new MssqlExportMetadataReader().Validate(schema, SchemaRequest(schema));
        Assert(issues.Count == 0, "Ordinary named properties on a table and nonindexed, trigger-free view must pass: " + string.Join("; ", issues.Select(x => x.Message)));
    }
    private static void Properties_WritesVariantValuesAndScopes()
    {
        var original = PropertiesFixture(); var table = original.Tables[0].Key; var view = original.Views[0].Key;
        string pane = "\u4e2d\u6587 '\r\nGO\r\n$(name)\ud83d\ude80";
        pane += new string('\u4e2d', 3503 - pane.Length);
        var props = new[] {
            PropertiesText(table, "TABLE", 0, null, "MS_Description", "owner's table", 100),
            PropertiesText(table, "TABLE", 1, "id", "MS_Description", "column\r\ncomment  "),
            PropertiesText(table, "TABLE", 0, null, "empty", "", 60),
            PropertiesText(view, "VIEW", 0, null, "MS_DiagramPane1", pane),
            PropertiesText(view, "VIEW", 1, "id", "caption'\u03a9", null),
            PropertiesText(view, "VIEW", 0, null, "near_limit", new string('\u4e2d', 3700)),
            new MssqlExtendedPropertyDefinition(view, "VIEW", SchemaFacts("minor_id", 0, "name", "MS_DiagramPaneCount", "base_type", "int", "max_length", 4, "is_null", false, "value_hex", "80000000")) };
        var schema = PropertiesFixture(props); var output = new StringWriter(); var request = SchemaRequest(schema);
        new MssqlSchemaScriptWriter().WriteAfterData(schema, request, output, CancellationToken.None);
        string sql = output.ToString();
        AssertContains(sql, "CONVERT(nvarchar(3503), 0x" + PropertiesHex(pane) + ") COLLATE Chinese_Taiwan_Stroke_CI_AS", "Keep 7006 bytes, declared length and source collation");
        AssertContains(sql, "CONVERT(nvarchar(3700), 0x", "Near-limit property stays bounded and native");
        AssertContains(sql, "CONVERT(nvarchar(50), 0x" + PropertiesHex("owner's table") + ")", "Declared length must not shrink to actual string length");
        AssertContains(sql, "CONVERT(nvarchar(30), 0x) COLLATE", "Empty typed property retains declared length");
        AssertContains(sql, "CONVERT(int, 0x80000000)", "Keep negative int range without widening to numeric");
        AssertContains(sql, "SET @__mp_property = NULL;", "Existing NULL property must not disappear");
        AssertContains(sql, "@name=N'caption''\u03a9'", "Quote property names independently");
        AssertContains(sql, "@level1type=N'VIEW', @level1name=N'station_view', @level2type=N'COLUMN', @level2name=N'id'", "View column scope");
        AssertContains(sql, "@level1type=N'TABLE', @level1name=N'station', @level2type=N'COLUMN', @level2name=N'id'", "Table column scope");
        Assert(sql.Split(new[] { "EXEC sys.sp_addextendedproperty" }, StringSplitOptions.None).Length - 1 == props.Length, "Exactly one write per source property, including descriptions");
        Assert(sql.IndexOf("EXEC sys.sp_addextendedproperty", StringComparison.Ordinal) > sql.IndexOf("CREATE VIEW", StringComparison.Ordinal), "All owner DDL precedes property output");
        AssertNotContains(sql, "$(name)", "Value text cannot be parsed by sqlcmd");
        AssertNotContains(sql, "old display comment", "UI comment projection cannot add duplicate properties");
        var noProps = new StringWriter();
        new MssqlSchemaScriptWriter().WriteAfterData(original, SchemaRequest(original), noProps, CancellationToken.None);
        AssertNotContains(noProps.ToString(), "sp_addextendedproperty", "Only the canonical property list is scripted");
    }
    private static void Properties_RejectsUnsupportedAndMalformedMetadata()
    {
        var original = PropertiesFixture(); var key = original.Tables[0].Key;
        foreach (string type in new[] { "varchar", "decimal", "varbinary", "bit" })
        {
            var property = new MssqlExtendedPropertyDefinition(key, "TABLE", SchemaFacts("minor_id", 0, "name", "unsupported_" + type, "base_type", type, "max_length", 4, "is_null", false));
            var schema = PropertiesFixture(new[] { property });
            var issue = new MssqlExportMetadataReader().Validate(schema, SchemaRequest(schema)).Single();
            Assert(issue.Code == "UnsupportedExtendedPropertyType" && issue.Message.Contains(property.Name) && issue.Message.Contains(type), "Unsupported property reports exact owner/name/type");
            AssertThrows<InvalidOperationException>(() => new MssqlSchemaScriptWriter().WriteBeforeData(schema, SchemaRequest(schema), new StringWriter(), CancellationToken.None), "Unsupported properties fail before any output");
        }
        var good = PropertiesText(key, "TABLE", 1, "id", "test", "x");
        foreach (var change in new[] { SchemaFacts("value_hex", "GG00"), SchemaFacts("value_hex", "780"), SchemaFacts("value_hex", "7800FFFF"), SchemaFacts("max_length", 0), SchemaFacts("max_length", 3), SchemaFacts("collation", "bad; DROP TABLE t"), SchemaFacts("column_name", "missing"), SchemaFacts("is_null", true) })
        {
            var facts = good.Facts.ToDictionary(x => x.Key, x => x.Value); foreach (var pair in change) facts[pair.Key] = pair.Value;
            var bad = new MssqlExtendedPropertyDefinition(key, "TABLE", facts); var schema = PropertiesFixture(new[] { bad });
            Assert(new MssqlExportMetadataReader().Validate(schema, SchemaRequest(schema)).Any(x => x.Code == "InvalidExtendedProperty"), "Malformed native property cannot silently convert or lose scope");
        }
        var duplicate = PropertiesFixture(new[] { good, good });
        Assert(new MssqlExportMetadataReader().Validate(duplicate, SchemaRequest(duplicate)).Any(x => x.Code == "InvalidExtendedProperty"), "Repeated property identity fails before restore");
        foreach (string feature in new[] { "index_count", "trigger_count" })
        {
            var viewFacts = original.Views[0].Facts.ToDictionary(x => x.Key, x => x.Value); viewFacts[feature] = 1;
            var schema = new MssqlExportSchema(original.Tables, new[] { new MssqlViewDefinition(original.Views[0].Key, viewFacts) }, null, 1, null);
            Assert(new MssqlExportMetadataReader().Validate(schema, SchemaRequest(schema)).Any(x => x.Code == "UnsupportedViewFeature"), "Actual view index and trigger remain unsupported");
        }
    }
    private static void Properties_FingerprintsImmutableNativeFacts()
    {
        var original = PropertiesFixture(); var key = original.Tables[0].Key;
        var first = PropertiesText(key, "TABLE", 0, null, "same_length", "ab");
        var second = PropertiesText(key, "TABLE", 1, "id", "column", null);
        var baseline = PropertiesFixture(new[] { first, second });
        Assert(baseline.Fingerprint == PropertiesFixture(new[] { second, first }).Fingerprint, "Catalog row order cannot change property fingerprint");
        foreach (var change in new[] { SchemaFacts("value_hex", PropertiesHex("ac")), SchemaFacts("max_length", 8), SchemaFacts("collation", "Latin1_General_100_BIN2"), SchemaFacts("name", "renamed"), SchemaFacts("base_type", "int", "max_length", 4, "collation", null, "value_hex", "00000001"), SchemaFacts("minor_id", 1, "column_name", "id") })
        {
            var facts = first.Facts.ToDictionary(x => x.Key, x => x.Value); foreach (var pair in change) facts[pair.Key] = pair.Value;
            var changed = new MssqlExtendedPropertyDefinition(key, "TABLE", facts);
            Assert(baseline.Fingerprint != PropertiesFixture(new[] { changed, second }).Fingerprint, "Native property values, types, length, collation and identity participate in drift detection");
        }
        var mutableFacts = first.Facts.ToDictionary(x => x.Key, x => x.Value); var copy = new MssqlExtendedPropertyDefinition(key, "TABLE", mutableFacts);
        mutableFacts["value_hex"] = PropertiesHex("zz");
        Assert(copy.ValueHex == PropertiesHex("ab"), "Property snapshot cannot follow later dictionary edits");
    }
    private static void Properties_RespectsModesAndCancellation()
    {
        var original = PropertiesFixture(); var schema = PropertiesFixture(new[] { PropertiesText(original.Tables[0].Key, "TABLE", 0, null, "MS_Description", "comment") });
        var writer = new MssqlSchemaScriptWriter(); var output = new StringWriter();
        writer.WriteAfterData(schema, SchemaRequest(schema, MssqlExportMode.SchemaOnly), output, CancellationToken.None);
        AssertContains(output.ToString(), "sp_addextendedproperty", "SchemaOnly preserves properties");
        output = new StringWriter(); writer.WriteAfterData(schema, SchemaRequest(schema, MssqlExportMode.DataOnly), output, CancellationToken.None);
        AssertNotContains(output.ToString(), "sp_addextendedproperty", "DataOnly leaves target metadata untouched");
        AssertThrows<OperationCanceledException>(() => writer.WriteAfterData(schema, SchemaRequest(schema), new StringWriter(), new CancellationToken(true)), "Property output obeys cancellation");
    }
}
