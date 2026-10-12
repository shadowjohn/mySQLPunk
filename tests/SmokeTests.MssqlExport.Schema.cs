using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using mySQLPunk.lib.MssqlExport;

public static partial class SmokeTests
{
    private static Dictionary<string, object> SchemaFacts(params object[] pairs)
    {
        var result = new Dictionary<string, object>();
        for (int i = 0; i < pairs.Length; i += 2) result.Add((string)pairs[i], pairs[i + 1]);
        return result;
    }
    private static MssqlExportRequest SchemaRequest(MssqlExportSchema schema, MssqlExportMode mode = MssqlExportMode.SchemaAndData)
    { return new MssqlExportRequest(Guid.NewGuid(), "source_db", schema.Tables.Select(t => t.Key).ToArray(), mode, mode != MssqlExportMode.DataOnly && schema.Views.Count != 0, Path.Combine(Path.GetTempPath(), "schema.sql"), null, null); }
    private static void Schema_RequiresWriter()
    { Assert(typeof(MssqlObjectKey).Assembly.GetType("mySQLPunk.lib.MssqlExport.MssqlSchemaScriptWriter") != null, "Native two-stage schema writer must exist"); }
    private static MssqlExportSchema SchemaFixture()
    {
        var key = new MssqlObjectKey(1, "sa'les]", "ord]ers");
        var columns = new[] {
            new MssqlColumnDefinition(SchemaFacts("column_id",1,"name","id","type_name","decimal","precision",38,"scale",0,"is_identity",true,"seed_value","-5","increment_value","-2","identity_not_for_replication",true)),
            new MssqlColumnDefinition(SchemaFacts("column_id",2,"name","name","type_name","varchar","max_length",80,"is_nullable",true,"collation_name","Latin1_General_100_BIN2","default_name","df'name","default_definition","('x')","comment","owner's column")),
            new MssqlColumnDefinition(SchemaFacts("column_id",3,"name","amount","type_name","decimal","precision",38,"scale",10)),
            new MssqlColumnDefinition(SchemaFacts("column_id",4,"name","offset","type_name","datetimeoffset","scale",7)),
            new MssqlColumnDefinition(SchemaFacts("column_id",5,"name","calc","type_name","decimal","is_computed",true,"computed_definition","([amount]*(2))","is_persisted",true,"is_nullable",false)),
            new MssqlColumnDefinition(SchemaFacts("column_id",6,"name","rv","type_name","timestamp","is_nullable",false)) };
        var ixcols = new[] { new MssqlNativeFacts(SchemaFacts("name","amount","key_ordinal",2,"index_column_id",2,"is_descending_key",true)), new MssqlNativeFacts(SchemaFacts("name","id","key_ordinal",1,"index_column_id",1)), new MssqlNativeFacts(SchemaFacts("name","name","key_ordinal",0,"index_column_id",3,"is_included_column",true)) };
        var indexes = new[] {
            new MssqlIndexDefinition(SchemaFacts("index_id",1,"name","PK_orders","type",1,"is_primary_key",true,"is_unique",true,"allow_row_locks",true,"allow_page_locks",true),new[]{ixcols[1]}),
            new MssqlIndexDefinition(SchemaFacts("index_id",2,"name","ix","type",2,"has_filter",true,"filter_definition","([amount]>(0))","fill_factor",80,"allow_row_locks",true,"allow_page_locks",false,"is_disabled",true),ixcols) };
        var constraints = new[] {
            new MssqlConstraintDefinition(SchemaFacts("name","ck","kind","CHECK","definition","([amount]>=(0))","is_not_trusted",true),null),
            new MssqlConstraintDefinition(SchemaFacts("name","fk","kind","FK","referenced_object_id",1,"referenced_schema",key.Schema,"referenced_table",key.Name,"delete_referential_action_desc","NO_ACTION","update_referential_action_desc","NO_ACTION","is_disabled",true,"is_not_trusted",true,"is_not_for_replication",true),new[]{new MssqlNativeFacts(SchemaFacts("constraint_column_id",1,"parent_column","id","referenced_column","id"))}) };
        var table = new MssqlTableDefinition(key,SchemaFacts("comment","owner's table","lock_escalation_desc","TABLE","text_in_row_limit",0,"large_value_types_out_of_row",false),columns,indexes,constraints);
        var v1 = new MssqlViewDefinition(new MssqlObjectKey(2,"dbo","v1"),SchemaFacts("definition","CREATE VIEW [dbo].[v1] AS SELECT [id] FROM [sa'les]]].[ord]]ers]","uses_ansi_nulls",true,"uses_quoted_identifier",true));
        var v2 = new MssqlViewDefinition(new MssqlObjectKey(3,"dbo","v2"),SchemaFacts("definition","ALTER VIEW [dbo].[v2] AS SELECT * FROM [dbo].[v1]","uses_ansi_nulls",true,"uses_quoted_identifier",false));
        return new MssqlExportSchema(new[]{table},new[]{v2,v1},new[]{new MssqlDependency(3,2,null,null,"dbo","v1"),new MssqlDependency(2,1,null,null,key.Schema,key.Name)},1,null);
    }
    private static void Schema_WritesNativeBeforeAndAfter()
    {
        var schema=SchemaFixture(); var writer=new MssqlSchemaScriptWriter(); var before=new StringWriter(); var after=new StringWriter();
        writer.WriteBeforeData(schema,SchemaRequest(schema),before,CancellationToken.None);
        writer.WriteAfterData(schema,SchemaRequest(schema),after,CancellationToken.None);
        var first=before.ToString(); var last=after.ToString();
        AssertContains(first,"decimal(38,10)","Keep native precision");
        AssertContains(first,"datetimeoffset(7)","Keep temporal precision");
        AssertContains(first,"varchar(80) COLLATE Latin1_General_100_BIN2","Keep source text collation");
        AssertContains(first,"IDENTITY(-5,-2) NOT FOR REPLICATION","Keep descending identity definition");
        AssertContains(first,"[calc] AS ([amount]*(2)) PERSISTED NOT NULL","Keep computed definition");
        AssertContains(first,"[rv] rowversion NOT NULL","Regenerate rowversion");
        AssertContains(first,"CONSTRAINT [df'name] DEFAULT ('x')","Keep named defaults");
        AssertContains(first,"CREATE TABLE [sa'les]]].[ord]]ers]","Quote names independently");
        AssertNotContains(first,"sp_tableoption", "Default table options need no unsupported text-in-row call on tables without legacy LOBs");
        AssertNotContains(first,"FOREIGN KEY","FK belongs after data");
        AssertNotContains(first,"CREATE INDEX","Indexes belong after data");
        AssertNotContains(first+last,"USE [source_db]","Restore database selected by caller");
        AssertNotContains(first+last,"DROP TABLE","Never silently replace a target");
        AssertContains(last,"PRIMARY KEY CLUSTERED ([id] ASC)","Native primary key");
        AssertContains(last,"([id] ASC, [amount] DESC) INCLUDE ([name]) WHERE ([amount]>(0))","Index ordering include and filter");
        AssertContains(last,"FILLFACTOR = 80","Preserve index option");
        AssertContains(last,"WITH NOCHECK ADD CONSTRAINT [ck] CHECK","Preserve untrusted check");
        AssertContains(last,"NOCHECK CONSTRAINT [fk]","Preserve disabled FK");
        AssertContains(last,"owner''s column","Escape extended property values");
        AssertContains(last,"SET QUOTED_IDENTIFIER OFF;","Preserve view SET option");
        Assert(last.IndexOf("CREATE VIEW [dbo].[v1]",StringComparison.Ordinal)<last.IndexOf("CREATE VIEW [dbo].[v2]",StringComparison.Ordinal),"Topological view order");
        AssertContains(last,"GO","View DDL own batch");
        Assert(last.LastIndexOf("DISABLE",StringComparison.Ordinal)>last.IndexOf("FOREIGN KEY",StringComparison.Ordinal),"Disable indexes only after dependent creation");
        AssertNotContains(first+last,"RESEED","Untouched empty identity retains original seed");
    }
    private static void Schema_ModesCancellationAndFailures()
    {
        var schema=SchemaFixture(); var writer=new MssqlSchemaScriptWriter();
        var output=new StringWriter(); var request=SchemaRequest(schema,MssqlExportMode.DataOnly);
        writer.WriteBeforeData(schema,request,output,CancellationToken.None); writer.WriteAfterData(schema,request,output,CancellationToken.None);
        AssertContains(output.ToString(),"SET QUOTED_IDENTIFIER ON;","Data only supplies indexed-computed-column DML session settings");
        AssertNotContains(output.ToString(),"CREATE TABLE","Data only does not create objects");
        AssertNotContains(output.ToString(),"ALTER TABLE","Data only does not alter objects");
        output=new StringWriter(); request=SchemaRequest(schema,MssqlExportMode.SchemaOnly);
        writer.WriteBeforeData(schema,request,output,CancellationToken.None);writer.WriteAfterData(schema,request,output,CancellationToken.None);
        AssertContains(output.ToString(),"FOREIGN KEY","Schema only includes complete constraints");
        AssertThrows<OperationCanceledException>(()=>writer.WriteBeforeData(schema,request,new StringWriter(),new CancellationToken(true)),"Cancellation before writing");
        var badDependencies=new[]{new MssqlDependency(1,900,null,null,"dbo","unselected_function")};
        var bad=new MssqlExportSchema(schema.Tables,null,badDependencies,1,null);
        AssertThrows<InvalidOperationException>(()=>writer.WriteBeforeData(bad,SchemaRequest(bad),new StringWriter(),CancellationToken.None),"Missing function/sequence/table dependencies fail");
        var cycle=new MssqlExportSchema(schema.Tables,schema.Views,new[]{new MssqlDependency(2,3,null,null,"dbo","v2"),new MssqlDependency(3,2,null,null,"dbo","v1")},1,null);
        AssertThrows<InvalidOperationException>(()=>writer.WriteAfterData(cycle,SchemaRequest(cycle),new StringWriter(),CancellationToken.None),"View cycle fails");
        var hidden=new MssqlExportSchema(schema.Tables,new[]{new MssqlViewDefinition(new MssqlObjectKey(4,"dbo","hidden"),SchemaFacts())},null,1,null);
        AssertThrows<InvalidOperationException>(()=>writer.WriteBeforeData(hidden,SchemaRequest(hidden),new StringWriter(),CancellationToken.None),"Invisible view fails before output");
        var trigger=new MssqlExportSchema(new[]{new MssqlTableDefinition(schema.Tables[0].Key,SchemaFacts("trigger_count",1),schema.Tables[0].Columns,null,null)},null,null,1,null);
        AssertThrows<InvalidOperationException>(()=>writer.WriteBeforeData(trigger,SchemaRequest(trigger),new StringWriter(),CancellationToken.None),"Triggers cannot silently disappear");
        // Self-reference is already in fixture; a two-table FK cycle is valid because both are created before FK DDL.
        var a=new MssqlObjectKey(10,"dbo","a"); var b=new MssqlObjectKey(11,"dbo","b");
        Func<MssqlObjectKey,MssqlObjectKey,MssqlTableDefinition> table=(x,y)=>new MssqlTableDefinition(x,SchemaFacts(),new[]{new MssqlColumnDefinition(SchemaFacts("name","id","column_id",1,"type_name","int"))},null,new[]{new MssqlConstraintDefinition(SchemaFacts("name","fk_"+x.Name,"kind","FK","referenced_object_id",y.ObjectId,"referenced_schema",y.Schema,"referenced_table",y.Name),new[]{new MssqlNativeFacts(SchemaFacts("parent_column","id","referenced_column","id","constraint_column_id",1))})});
        var circular=new MssqlExportSchema(new[]{table(a,b),table(b,a)},null,null,1,null);output=new StringWriter();
        writer.WriteBeforeData(circular,SchemaRequest(circular),output,CancellationToken.None);writer.WriteAfterData(circular,SchemaRequest(circular),output,CancellationToken.None);
        Assert(output.ToString().LastIndexOf("CREATE TABLE",StringComparison.Ordinal)<output.ToString().IndexOf("FOREIGN KEY",StringComparison.Ordinal),"All tables precede circular FK creation");
    }
    private static void Schema_RejectsLegacyAnsiSemantics()
    {
        var key=new MssqlObjectKey(8,"dbo","legacy");
        foreach (var type in new[]{"varchar","char","binary","varbinary"})
        {
            var column=new MssqlColumnDefinition(SchemaFacts("name","value","type_name",type,"max_length",10,"is_ansi_padded",false));
            var table=new MssqlTableDefinition(key,SchemaFacts(),new[]{column},null,null);
            var schema=new MssqlExportSchema(new[]{table},null,null,1,null);
            Assert(new MssqlExportMetadataReader().Validate(schema,SchemaRequest(schema)).Any(x=>x.Code=="UnsupportedAnsiPadding"),"Cannot recreate legacy padding on current engines");
        }
        var legacy=new MssqlExportSchema(new[]{new MssqlTableDefinition(key,SchemaFacts("uses_ansi_nulls",false),new[]{new MssqlColumnDefinition(SchemaFacts("name","id","type_name","int"))},null,null)},null,null,1,null);
        Assert(new MssqlExportMetadataReader().Validate(legacy,SchemaRequest(legacy)).Any(x=>x.Code=="UnsupportedAnsiNulls"),"Cannot recreate legacy ANSI NULLS semantics");
    }
    private static void Schema_RejectsUndocumentedIndexOption()
    {
        var schema=SchemaFixture(); var original=schema.Tables[0];
        var index=new MssqlIndexDefinition(SchemaFacts("index_id",2,"name","unsupported","type",2,"suppress_dup_key_messages",true),original.Indexes[0].Columns);
        var candidate=new MssqlExportSchema(new[]{new MssqlTableDefinition(original.Key,SchemaFacts(),original.Columns,new[]{index},null)},null,null,1,null);
        Assert(new MssqlExportMetadataReader().Validate(candidate,SchemaRequest(candidate)).Any(x=>x.Code=="UnsupportedIndex"),"Do not emit undocumented CREATE INDEX options");
    }
    private static void Schema_NormalizesOnlyViewHeader()
    {
        var original=SchemaFixture();
        foreach (string prefix in new[]{"/* CREATE VIEW fake */ CREATE OR ALTER VIEW ", "-- ALTER VIEW fake\r\n ALTER /* nested /* comment */ fine */ VIEW ", "CREATE VIEW "})
        {
            var view=new MssqlViewDefinition(new MssqlObjectKey(9,"sales]","renamed$(name)"),SchemaFacts("definition",prefix+"[old schema].[old]]name] AS SELECT 'ALTER VIEW unchanged' AS [text]","uses_ansi_nulls",true,"uses_quoted_identifier",true));
            var schema=new MssqlExportSchema(original.Tables,new[]{view},null,1,null);var output=new StringWriter();
            new MssqlSchemaScriptWriter().WriteAfterData(schema,SchemaRequest(schema),output,CancellationToken.None);
            AssertContains(output.ToString(),"CREATE VIEW [sales]]].[renamed$(name)] AS SELECT 'ALTER VIEW unchanged' AS [text]","Normalize executable header and stale name, preserve body and sqlcmd -x identifier");
            AssertNotContains(output.ToString(),"CREATE OR ALTER VIEW","Restore must reject existing view");
        }
    }
}
