using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace mySQLPunk.lib.MssqlExport
{
    internal sealed class MssqlExportMetadataReader
    {
        internal static string Quote(string name) { return "[" + name.Replace("]", "]]") + "]"; }
        internal static string Qualified(MssqlObjectKey key) { return Quote(key.Schema) + "." + Quote(key.Name); }
        internal MssqlExportCatalog ReadCatalog(SqlConnection connection, string databaseName, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            connection.ChangeDatabase(databaseName);
            EnsureVisibility(connection, null, cancellation);
            var tables = ReadTables(connection, null, cancellation);
            var issues = tables.Where(t => !t.Flag("can_select")).Select(t => new MssqlExportIssue("UnreadableTable", t.Key, null, "SELECT permission is required."));
            return new MssqlExportCatalog(tables, Snapshot(connection, null, cancellation), issues);
        }
        internal MssqlExportSchema ReadSchema(SqlConnection connection, SqlTransaction transaction, MssqlExportRequest request, CancellationToken cancellation)
        {
            EnsureVisibility(connection, transaction, cancellation);
            var issues = new List<MssqlExportIssue>();
            var selected = new List<MssqlTableDefinition>();
            var catalog = ReadTables(connection, transaction, cancellation);
            foreach (var key in request.Tables)
            {
                var table = catalog.SingleOrDefault(t => t.Key.ObjectId == key.ObjectId && t.Key.Schema == key.Schema && t.Key.Name == key.Name);
                if (table == null) { issues.Add(new MssqlExportIssue("ObjectChanged", key, null, "Selected table is missing, renamed or replaced.")); continue; }
                string id = key.ObjectId.ToString(CultureInfo.InvariantCulture);
                var columnSql = @"SELECT c.column_id,c.name,ty.name type_name,SCHEMA_NAME(ty.schema_id) type_schema,ty.is_user_defined,ty.is_assembly_type,
c.max_length,c.precision,c.scale,c.collation_name,c.is_nullable,c.is_ansi_padded,c.is_identity,c.is_computed,c.is_filestream,c.is_sparse,c.is_column_set,c.is_rowguidcol,c.xml_collection_id,c.is_xml_document,c.rule_object_id,
CONVERT(nvarchar(100),ic.seed_value) seed_value,CONVERT(nvarchar(100),ic.increment_value) increment_value,ic.is_not_for_replication identity_not_for_replication,
cc.definition computed_definition,cc.is_persisted,dc.name default_name,dc.definition default_definition,
c.default_object_id,CONVERT(nvarchar(max),ep.value) comment," + Optional(connection, transaction, "sys.columns", "c", cancellation, "generated_always_type", "encryption_type", "is_hidden", "vector_dimensions") + "," + (HasCatalog(connection, transaction, "masked_columns", cancellation) ? "(SELECT MAX(CONVERT(int,mc.is_masked)) FROM sys.masked_columns mc WHERE mc.object_id=c.object_id AND mc.column_id=c.column_id)" : "0") + " is_masked" + @"
FROM sys.columns c JOIN sys.types ty ON c.user_type_id=ty.user_type_id
LEFT JOIN sys.identity_columns ic ON ic.object_id=c.object_id AND ic.column_id=c.column_id
LEFT JOIN sys.computed_columns cc ON cc.object_id=c.object_id AND cc.column_id=c.column_id
LEFT JOIN sys.default_constraints dc ON dc.object_id=c.default_object_id
LEFT JOIN sys.extended_properties ep ON ep.class=1 AND ep.major_id=c.object_id AND ep.minor_id=c.column_id AND ep.name=N'MS_Description'
WHERE c.object_id=" + id + " ORDER BY c.column_id";
                var columns = Query(connection, transaction, columnSql, cancellation).Select(r => new MssqlColumnDefinition(r)).ToList();
                var indexes = new List<MssqlIndexDefinition>();
                foreach (var r in Query(connection, transaction, @"SELECT i.index_id,i.name,i.type,i.is_unique,i.is_primary_key,i.is_unique_constraint,i.is_disabled,i.is_hypothetical,
i.fill_factor,i.is_padded,i.ignore_dup_key,i.allow_row_locks,i.allow_page_locks,i.has_filter,i.filter_definition,
ds.name data_space_name,ds.type data_space_type,s.no_recompute," + Optional(connection, transaction, "sys.indexes", "i", cancellation, "optimize_for_sequential_key", "suppress_dup_key_messages") + @",
(SELECT MAX(p.data_compression) FROM sys.partitions p WHERE p.object_id=i.object_id AND p.index_id=i.index_id) data_compression
FROM sys.indexes i LEFT JOIN sys.data_spaces ds ON ds.data_space_id=i.data_space_id
LEFT JOIN sys.stats s ON s.object_id=i.object_id AND s.stats_id=i.index_id
WHERE i.object_id=" + id + " ORDER BY i.index_id", cancellation))
                {
                    var f = new MssqlNativeFacts(r);
                    var cols = Query(connection, transaction, "SELECT ic.index_column_id,ic.column_id,c.name,ic.key_ordinal,ic.is_descending_key,ic.is_included_column,ic.partition_ordinal FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE ic.object_id=" + id + " AND ic.index_id=" + f.Number("index_id") + " ORDER BY ic.index_column_id", cancellation).Select(x => new MssqlNativeFacts(x));
                    indexes.Add(new MssqlIndexDefinition(r, cols));
                }
                var constraints = new List<MssqlConstraintDefinition>();
                foreach (var r in Query(connection, transaction, "SELECT object_id,name,'CHECK' kind,definition,is_disabled,is_not_trusted,is_not_for_replication,parent_column_id,uses_database_collation FROM sys.check_constraints WHERE parent_object_id=" + id + " ORDER BY object_id", cancellation)) constraints.Add(new MssqlConstraintDefinition(r, null));
                foreach (var r in Query(connection, transaction, @"SELECT fk.object_id,fk.name,'FK' kind,fk.referenced_object_id,SCHEMA_NAME(o.schema_id) referenced_schema,o.name referenced_table,
fk.delete_referential_action_desc,fk.update_referential_action_desc,fk.is_disabled,fk.is_not_trusted,fk.is_not_for_replication
FROM sys.foreign_keys fk JOIN sys.objects o ON o.object_id=fk.referenced_object_id WHERE fk.parent_object_id=" + id + " ORDER BY fk.object_id", cancellation))
                {
                    var f = new MssqlNativeFacts(r);
                    var cols = Query(connection, transaction, @"SELECT f.constraint_column_id,pc.name parent_column,rc.name referenced_column FROM sys.foreign_key_columns f
JOIN sys.columns pc ON pc.object_id=f.parent_object_id AND pc.column_id=f.parent_column_id
JOIN sys.columns rc ON rc.object_id=f.referenced_object_id AND rc.column_id=f.referenced_column_id WHERE f.constraint_object_id=" + f.Number("object_id") + " ORDER BY f.constraint_column_id", cancellation).Select(x => new MssqlNativeFacts(x));
                    constraints.Add(new MssqlConstraintDefinition(r, cols));
                }
                selected.Add(new MssqlTableDefinition(key, table.Facts.ToDictionary(x => x.Key, x => x.Value), columns, indexes, constraints));
            }
            var views = new List<MssqlViewDefinition>();
            if (request.IncludeViews)
                foreach (var r in Query(connection, transaction, @"SELECT v.object_id,SCHEMA_NAME(v.schema_id) schema_name,v.name,m.definition,m.uses_ansi_nulls,m.uses_quoted_identifier,m.is_schema_bound,v.with_check_option,
(SELECT COUNT(*) FROM sys.indexes i WHERE i.object_id=v.object_id AND i.index_id>0) index_count,
(SELECT COUNT(*) FROM sys.triggers t WHERE t.parent_id=v.object_id) trigger_count
FROM sys.views v LEFT JOIN sys.sql_modules m ON m.object_id=v.object_id WHERE v.is_ms_shipped=0 ORDER BY v.object_id", cancellation))
                { var f = new MssqlNativeFacts(r); views.Add(new MssqlViewDefinition(new MssqlObjectKey(f.Number("object_id"), f.Text("schema_name"), f.Text("name")), r)); }
            var objectIds = new HashSet<int>(selected.Select(x => x.Key.ObjectId).Concat(views.Select(x => x.Key.ObjectId)));
            var dependencies = new List<MssqlDependency>();
            foreach (var r in Query(connection, transaction, @"SELECT CASE WHEN o.parent_object_id<>0 THEN o.parent_object_id ELSE d.referencing_id END object_id,
d.referenced_id,d.referenced_server_name,d.referenced_database_name,d.referenced_schema_name,d.referenced_entity_name,
d.is_ambiguous,d.is_caller_dependent,DB_ID() source_database_id,DB_ID(d.referenced_database_name) referenced_database_id,
local_object.object_id resolved_local_id
FROM sys.sql_expression_dependencies d JOIN sys.objects o ON o.object_id=d.referencing_id
LEFT JOIN sys.objects local_object ON d.referenced_server_name IS NULL AND d.referenced_database_name IS NOT NULL
AND DB_ID(d.referenced_database_name)=DB_ID() AND d.referenced_schema_name IS NOT NULL
AND local_object.object_id=OBJECT_ID(QUOTENAME(d.referenced_schema_name)+N'.'+QUOTENAME(d.referenced_entity_name))
AND local_object.type IN ('U','V')
WHERE d.referenced_class=1", cancellation))
            {
                var f = new MssqlNativeFacts(r);
                if (objectIds.Contains(f.Number("object_id"))) dependencies.Add(new MssqlDependency(f.Number("object_id"), NullableNumber(f, "referenced_id"),
                    f.Text("referenced_server_name"), f.Text("referenced_database_name"), f.Text("referenced_schema_name"), f.Text("referenced_entity_name"),
                    NullableNumber(f, "source_database_id"), NullableNumber(f, "referenced_database_id"), NullableNumber(f, "resolved_local_id"),
                    f.Flag("is_ambiguous"), f.Flag("is_caller_dependent")));
            }
            var properties = ReadExtendedProperties(connection, transaction, objectIds, cancellation);
            var schema = new MssqlExportSchema(selected, views, dependencies, Snapshot(connection, transaction, cancellation), issues, extendedProperties: properties);
            return PrepareViewReferences(schema, request, (database, ownerSchema, name) => ResolveViewReference(connection, transaction, database, ownerSchema, name, cancellation));
        }
        internal static MssqlExportSchema PrepareViewReferences(MssqlExportSchema schema, MssqlExportRequest request,
            Func<string, string, string, MssqlViewReferenceResolution> resolver)
        {
            if (request.Mode == MssqlExportMode.DataOnly) return schema;
            var views = schema.Views.ToList();
            var dependencies = schema.Dependencies.ToList();
            var issues = schema.Issues.ToList();
            var selected = new HashSet<int>(schema.Tables.Select(x => x.Key.ObjectId).Concat(views.Select(x => x.Key.ObjectId)));
            bool changed = false;
            for (int i = 0; i < views.Count; i++)
            {
                var view = views[i];
                var owned = dependencies.Where(d => d.ObjectId == view.Key.ObjectId).ToArray();
                var qualified = owned.Where(d => d.Database != null || d.Server != null).ToArray();
                if (qualified.Length == 0) continue;
                // Preserve the existing failure boundary for every other dependency and
                // never authorize a table expression by preparing an unrelated view.
                if (owned.Any(d => d.IsAmbiguous || d.IsCallerDependent || d.Server != null ||
                    (d.Database != null ? !d.HasSameDatabaseIdentity || !selected.Contains(d.ResolvedLocalId.Value) :
                    !d.ReferencedId.HasValue || !selected.Contains(d.ReferencedId.Value)))) continue;
                int? sourceId = qualified[0].SourceDatabaseId;
                if (qualified.Any(d => d.SourceDatabaseId != sourceId)) continue;
                var result = MssqlViewReferenceRewriter.Rewrite(view, (database, ownerSchema, name) =>
                {
                    var resolution = resolver(database, ownerSchema, name);
                    return resolution != null && resolution.SourceDatabaseId == sourceId ? resolution : null;
                }, qualified.Select(d => d.ResolvedLocalId), selected);
                changed = true;
                if (!result.Success) { issues.Add(result.Issue); continue; }
                var facts = view.Facts.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
                facts["prepared_definition"] = result.Sql;
                facts["prepared_same_database_references"] = true;
                views[i] = new MssqlViewDefinition(view.Key, facts);
                for (int j = 0; j < dependencies.Count; j++)
                    if (dependencies[j].ObjectId == view.Key.ObjectId && dependencies[j].Database != null)
                        dependencies[j] = dependencies[j].WithPreparedSameDatabaseReference();
            }
            return changed ? new MssqlExportSchema(schema.Tables, views, dependencies, schema.SnapshotState, issues, schema.ExtendedProperties) : schema;
        }
        internal IReadOnlyList<MssqlExportIssue> Validate(MssqlExportSchema schema, MssqlExportRequest request)
        {
            var issues = new List<MssqlExportIssue>(schema.Issues);
            if (request.Mode != MssqlExportMode.SchemaOnly && schema.SnapshotState != 1) issues.Add(new MssqlExportIssue("SnapshotUnavailable", null, null, "ALLOW_SNAPSHOT_ISOLATION must be ON (state 1). Current state: " + MssqlSnapshotState.Describe(schema.SnapshotState) + ". Ask the DBA to review this database setting; retry after state ON, or choose schema only. The exporter does not change settings or require ALTER permission when already ON."));
            var ids = new HashSet<int>(schema.Tables.Select(x => x.Key.ObjectId).Concat(schema.Views.Select(x => x.Key.ObjectId)));
            foreach (var t in schema.Tables)
            {
                Action<string, string, string> add = (code, col, reason) => issues.Add(new MssqlExportIssue(code, t.Key, col, reason));
                if (request.Mode != MssqlExportMode.SchemaOnly && t.Facts.ContainsKey("can_select") && !t.Flag("can_select")) add("UnreadableTable", null, "SELECT permission is required.");
                foreach (var flag in new[] { "temporal_type", "ledger_type", "is_memory_optimized", "is_filetable", "is_node", "is_edge", "is_external", "is_remote_data_archive_enabled", "trigger_count", "fulltext_count", "security_policy_count" })
                    if (t.Number(flag) != 0) add("UnsupportedTableFeature", null, "Unsupported table feature: " + flag);
                if (request.Mode != MssqlExportMode.DataOnly && t.Text("uses_ansi_nulls") != null && !t.Flag("uses_ansi_nulls")) add("UnsupportedAnsiNulls", null, "Legacy ANSI_NULLS OFF cannot be recreated on modern SQL Server.");
                foreach (var c in t.Columns)
                {
                    if (request.Mode != MssqlExportMode.DataOnly && c.Text("is_ansi_padded") != null && !c.Flag("is_ansi_padded") && new[] { "char", "varchar", "binary", "varbinary" }.Contains(c.NativeType)) add("UnsupportedAnsiPadding", c.Name, "Legacy ANSI_PADDING OFF cannot be recreated on modern SQL Server.");
                    bool nativeGeometry = c.NativeType == "geometry" && c.TypeSchema == "sys" && !c.Flag("is_user_defined");
                    if (c.NativeType == "geography" || (c.NativeType == "geometry" && !nativeGeometry)) add("UnsupportedSpatial", c.Name, "Only native sys.geometry has a supported spatial codec.");
                    if (!nativeGeometry && (c.Flag("is_user_defined") || c.Flag("is_assembly_type"))) add("UnsupportedUdt", c.Name, "Alias and CLR user types are not supported.");
                    if (c.Number("xml_collection_id") != 0) add("UnsupportedTypedXml", c.Name, "Typed XML schema collections are not exported.");
                    if (c.NativeType == "sql_variant" || !SupportedTypes.Contains(c.NativeType ?? "")) add("UnsupportedType", c.Name, "Unsupported native type: " + c.NativeType);
                    foreach (var flag in new[] { "is_filestream", "is_sparse", "is_column_set", "generated_always_type", "encryption_type", "is_hidden", "is_masked", "rule_object_id", "vector_dimensions" })
                        if (c.Flag(flag) || c.Number(flag) != 0) add("UnsupportedColumnFeature", c.Name, "Unsupported column feature: " + flag);
                    if (c.Computed && c.ComputedDefinition == null) add("UnreadableDefinition", c.Name, "Computed definition is unavailable.");
                    if (c.Number("default_object_id") != 0 && c.DefaultDefinition == null) add("UnreadableDefinition", c.Name, "Default is hidden or is a legacy bound default.");
                }
                foreach (var i in t.Indexes)
                {
                    if (i.Number("type") > 2 || i.Text("data_space_type") == "PS" || i.Number("data_compression") != 0 || i.Flag("is_hypothetical") || i.Flag("suppress_dup_key_messages")) add("UnsupportedIndex", null, "Unsupported index/partition/compression: " + i.Name);
                    if (i.Flag("has_filter") && i.Text("filter_definition") == null) add("UnreadableDefinition", null, "Index filter unavailable: " + i.Name);
                }
                foreach (var c in t.Constraints)
                {
                    if (c.Kind == "CHECK" && c.Text("definition") == null) add("UnreadableDefinition", null, "CHECK definition unavailable: " + c.Name);
                    if (c.Kind == "FK" && !ids.Contains(c.Number("referenced_object_id")) && request.Mode != MssqlExportMode.DataOnly) add("MissingDependency", null, "Foreign key " + c.Name + " requires " + c.Text("referenced_schema") + "." + c.Text("referenced_table"));
                }
            }
            foreach (var v in schema.Views)
            {
                if (v.Text("uses_ansi_nulls") != null && !v.AnsiNulls) issues.Add(new MssqlExportIssue("UnsupportedAnsiNulls", v.Key, null, "Legacy view ANSI_NULLS OFF cannot be recreated on modern SQL Server."));
                if (string.IsNullOrEmpty(v.Definition)) issues.Add(new MssqlExportIssue("UnreadableDefinition", v.Key, null, "View definition is encrypted or unreadable."));
                if (v.Number("index_count") != 0) issues.Add(new MssqlExportIssue("UnsupportedViewFeature", v.Key, null, "Indexed views are unsupported."));
                if (v.Number("trigger_count") != 0) issues.Add(new MssqlExportIssue("UnsupportedViewFeature", v.Key, null, "View triggers are unsupported."));
            }
            ValidateExtendedProperties(schema, issues);
            if (request.Mode != MssqlExportMode.DataOnly)
                foreach (var d in schema.Dependencies)
                    if (d.IsAmbiguous || d.IsCallerDependent || d.Server != null || !d.EffectiveReferencedId.HasValue || !ids.Contains(d.EffectiveReferencedId.Value) ||
                        (d.Database != null && (!d.HasSameDatabaseIdentity || !d.PreparedSameDatabaseReference ||
                            !schema.Views.Any(v => v.Key.ObjectId == d.ObjectId && v.HasPreparedReferences))))
                        issues.Add(new MssqlExportIssue("MissingDependency", schema.Tables.Select(x => x.Key).Concat(schema.Views.Select(x => x.Key)).FirstOrDefault(x => x.ObjectId == d.ObjectId), null, "Definition requires unexported object: " + string.Join(".", new[] { d.Server, d.Database, d.Schema, d.Name }.Where(x => x != null))));
            if (schema.Tables.GroupBy(x => x.Key.ObjectId).Any(g => g.Count() > 1))
                issues.Add(new MssqlExportIssue("DuplicateSelection", null, null, "Each table may be selected only once."));
            var pending = new HashSet<int>(schema.Views.Select(x => x.Key.ObjectId));
            while (pending.Count != 0)
            {
                var ready = pending.Where(id => !schema.Dependencies.Any(d => d.ObjectId == id && d.EffectiveReferencedId.HasValue && pending.Contains(d.EffectiveReferencedId.Value))).ToList();
                if (ready.Count == 0) { issues.Add(new MssqlExportIssue("DependencyCycle", null, null, "View dependencies contain a cycle and cannot be scripted in dependency order.")); break; }
                foreach (int id in ready) pending.Remove(id);
            }
            return issues.AsReadOnly();
        }
        private static int? NullableNumber(MssqlNativeFacts facts, string name) => facts.Text(name) == null ? (int?)null : facts.Number(name);
        private static MssqlViewReferenceResolution ResolveViewReference(SqlConnection connection, SqlTransaction transaction,
            string database, string schema, string name, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            // Reject before setting sized parameters: truncation cannot establish identity.
            if (new[] { database, schema, name }.Any(x => string.IsNullOrEmpty(x) || x.Length > 128))
                return new MssqlViewReferenceResolution(null, null, null, null);
            const string sql = @"SELECT CONVERT(int,DB_ID()) source_database_id,CONVERT(int,DB_ID(@database)) referenced_database_id,o.object_id,RTRIM(o.type) object_type
FROM (VALUES(1)) seed(n)
LEFT JOIN sys.objects o ON DB_ID(@database)=DB_ID()
AND o.object_id=OBJECT_ID(QUOTENAME(@schema)+N'.'+QUOTENAME(@name))";
            var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 0 };
            command.Parameters.Add("@database", SqlDbType.NVarChar, 128).Value = database;
            command.Parameters.Add("@schema", SqlDbType.NVarChar, 128).Value = schema;
            command.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = name;
            using (var owner = new MssqlCommandOwner(command, cancellation))
            using (var reader = owner.ExecuteReader(CommandBehavior.SingleRow))
            {
                cancellation.ThrowIfCancellationRequested();
                if (!reader.Read()) return new MssqlViewReferenceResolution(null, null, null, null);
                var result = new MssqlViewReferenceResolution(reader.IsDBNull(0) ? (int?)null : reader.GetInt32(0),
                    reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1), reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3));
                cancellation.ThrowIfCancellationRequested();
                return result;
            }
        }
        private static IReadOnlyList<MssqlExtendedPropertyDefinition> ReadExtendedProperties(SqlConnection connection, SqlTransaction transaction, IEnumerable<int> objectIds, CancellationToken cancellation)
        {
            string ids = string.Join(",", objectIds.OrderBy(x => x).Select(x => x.ToString(CultureInfo.InvariantCulture)));
            if (ids.Length == 0) return new MssqlExtendedPropertyDefinition[0];
            // Read only the selected owners, and only convert values whose native type we support.
            string sql = @"SELECT o.object_id,SCHEMA_NAME(o.schema_id) schema_name,o.name object_name,
CASE WHEN o.type='V' THEN 'VIEW' ELSE 'TABLE' END owner_type,
e.minor_id,c.name column_name,e.name,CONVERT(nvarchar(128),SQL_VARIANT_PROPERTY(e.value,'BaseType')) base_type,
CONVERT(int,SQL_VARIANT_PROPERTY(e.value,'MaxLength')) max_length,
CONVERT(nvarchar(128),SQL_VARIANT_PROPERTY(e.value,'Collation')) collation,
CASE WHEN e.value IS NULL THEN 1 ELSE 0 END is_null,
CASE WHEN SQL_VARIANT_PROPERTY(e.value,'BaseType')='nvarchar' THEN CONVERT(varchar(max),CONVERT(varbinary(8000),CONVERT(nvarchar(4000),e.value)),2)
     WHEN SQL_VARIANT_PROPERTY(e.value,'BaseType')='int' THEN CONVERT(varchar(max),CONVERT(varbinary(4),CONVERT(int,e.value)),2)
     ELSE NULL END value_hex
FROM sys.extended_properties e JOIN sys.objects o ON o.object_id=e.major_id
LEFT JOIN sys.columns c ON c.object_id=e.major_id AND c.column_id=e.minor_id
WHERE e.class=1 AND o.type IN ('U','V') AND e.major_id IN (" + ids + ") ORDER BY e.major_id,e.minor_id,e.name";
            return Query(connection, transaction, sql, cancellation).Select(row =>
            {
                var f = new MssqlNativeFacts(row);
                return new MssqlExtendedPropertyDefinition(new MssqlObjectKey(f.Number("object_id"), f.Text("schema_name"), f.Text("object_name")), f.Text("owner_type"), row);
            }).ToList().AsReadOnly();
        }
        private static void ValidateExtendedProperties(MssqlExportSchema schema, List<MssqlExportIssue> issues)
        {
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in schema.ExtendedProperties)
            {
                Action<string, string> add = (code, reason) => issues.Add(new MssqlExportIssue(code, p.Owner, p.ColumnName, "Extended property " + Quote(p.Name ?? "<unnamed>") + ": " + reason));
                var table = schema.Tables.FirstOrDefault(t => t.Key.ObjectId == p.Owner.ObjectId && t.Key.Schema == p.Owner.Schema && t.Key.Name == p.Owner.Name);
                var view = schema.Views.FirstOrDefault(v => v.Key.ObjectId == p.Owner.ObjectId && v.Key.Schema == p.Owner.Schema && v.Key.Name == p.Owner.Name);
                bool ownerValid = (p.OwnerType == "TABLE" && table != null) || (p.OwnerType == "VIEW" && view != null);
                if (!ownerValid || p.Name == null || p.MinorId < 0 || (p.MinorId == 0 && p.ColumnName != null) ||
                    (p.MinorId != 0 && (p.ColumnName == null || (table != null && !table.Columns.Any(c => c.Ordinal == p.MinorId && c.Name == p.ColumnName)))))
                { add("InvalidExtendedProperty", "Owner or column scope is unavailable."); continue; }
                string identity = p.Owner.ObjectId.ToString(CultureInfo.InvariantCulture) + ":" + p.MinorId.ToString(CultureInfo.InvariantCulture) + ":" + p.Name;
                if (!identities.Add(identity)) { add("InvalidExtendedProperty", "Duplicate property identity."); continue; }
                if (p.IsNull)
                {
                    if (p.BaseType != null || p.MaxLengthBytes.HasValue || p.Collation != null || p.ValueHex != null) add("InvalidExtendedProperty", "NULL variant metadata is inconsistent.");
                    continue;
                }
                if (p.BaseType != "nvarchar" && p.BaseType != "int")
                { add("UnsupportedExtendedPropertyType", "Unsupported SQL type: " + (p.BaseType ?? "<unknown>") + ". Supported types are nvarchar, int and NULL."); continue; }
                string hex = p.ValueHex;
                bool validHex = hex != null && hex.Length % 2 == 0 && hex.All(c => (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F'));
                int bytes = hex == null ? -1 : hex.Length / 2;
                bool valid = validHex && bytes <= 7500;
                if (p.BaseType == "int") valid &= p.MaxLengthBytes == 4 && bytes == 4 && p.Collation == null;
                else valid &= p.MaxLengthBytes.HasValue && p.MaxLengthBytes >= 2 && p.MaxLengthBytes <= 8000 && p.MaxLengthBytes % 2 == 0 &&
                    bytes % 2 == 0 && bytes <= p.MaxLengthBytes && !string.IsNullOrEmpty(p.Collation) &&
                    p.Collation.All(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_');
                if (!valid) add("InvalidExtendedProperty", "Native value length, encoding or collation metadata is inconsistent for SQL type " + p.BaseType + ".");
            }
        }
        private static readonly HashSet<string> SupportedTypes = new HashSet<string>("bigint int smallint tinyint bit decimal numeric money smallmoney float real date time datetime datetime2 smalldatetime datetimeoffset char varchar text nchar nvarchar ntext binary varbinary image uniqueidentifier xml timestamp rowversion geometry".Split(' '), StringComparer.OrdinalIgnoreCase);
        private static void EnsureVisibility(SqlConnection c, SqlTransaction t, CancellationToken token)
        {
            var row = Query(c, t, "SELECT HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','VIEW DEFINITION') allowed", token)[0];
            if (!new MssqlNativeFacts(row).Flag("allowed")) throw new MssqlExportPrerequisiteException(MssqlPrerequisite.ViewDefinitionRequired);
        }
        private static int Snapshot(SqlConnection c, SqlTransaction t, CancellationToken token) { return new MssqlNativeFacts(Query(c, t, "SELECT snapshot_isolation_state FROM sys.databases WHERE database_id=DB_ID()", token).Single()).Number("snapshot_isolation_state"); }
        private static bool HasCatalog(SqlConnection c, SqlTransaction t, string name, CancellationToken token)
        { return Query(c, t, "SELECT object_id FROM sys.all_objects WHERE name='" + name + "' AND schema_id=SCHEMA_ID('sys')", token).Count != 0; }
        private static List<MssqlTableDefinition> ReadTables(SqlConnection c, SqlTransaction t, CancellationToken token)
        {
            string optional = Optional(c, t, "sys.tables", "t", token, "temporal_type", "ledger_type", "is_memory_optimized", "is_filetable", "is_node", "is_edge", "is_external", "is_remote_data_archive_enabled");
            bool security = Query(c, t, "SELECT object_id FROM sys.all_objects WHERE name='security_predicates' AND schema_id=SCHEMA_ID('sys')", token).Count != 0;
            string sql = @"SELECT t.object_id,SCHEMA_NAME(t.schema_id) schema_name,t.name,t.uses_ansi_nulls,t.lock_escalation_desc,t.text_in_row_limit,t.large_value_types_out_of_row,CONVERT(nvarchar(max),ep.value) comment,
HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(t.schema_id))+'.'+QUOTENAME(t.name),'OBJECT','SELECT') can_select,
(SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id=t.object_id AND p.index_id IN(0,1)) estimated_rows,
(SELECT COUNT(*) FROM sys.triggers tr WHERE tr.parent_id=t.object_id) trigger_count,
(SELECT COUNT(*) FROM sys.fulltext_indexes f WHERE f.object_id=t.object_id) fulltext_count," +
(security ? "(SELECT COUNT(*) FROM sys.security_predicates sp WHERE sp.target_object_id=t.object_id)" : "0") + " security_policy_count," + optional + @"
FROM sys.tables t LEFT JOIN sys.extended_properties ep ON ep.class=1 AND ep.major_id=t.object_id AND ep.minor_id=0 AND ep.name=N'MS_Description'
WHERE t.is_ms_shipped=0 ORDER BY t.object_id";
            var rows = Query(c, t, sql, token);
            // Allocation metadata is a bounded catalog read. It is advisory and must not
            // make catalog loading depend on a new permission or a table scan.
            IReadOnlyList<Dictionary<string, object>> storage = null;
            try
            {
                storage = Query(c, t, @"SELECT p.object_id,
SUM(CONVERT(bigint, a.used_pages)) * CONVERT(bigint, 8192) estimated_bytes
FROM sys.partitions p JOIN sys.allocation_units a
ON (a.type IN (1,3) AND a.container_id=p.hobt_id)
OR (a.type=2 AND a.container_id=p.partition_id)
WHERE p.index_id IN (0,1)
GROUP BY p.object_id", token);
            }
            catch (SqlException) { /* Optional estimate unavailable; actual export can continue. */ }
            return MapTables(rows, storage);
        }
        internal static List<MssqlTableDefinition> MapTables(IReadOnlyList<Dictionary<string, object>> rows, IReadOnlyList<Dictionary<string, object>> storage)
        {
            var sizes = (storage ?? new Dictionary<string, object>[0])
                .Where(r => r.ContainsKey("object_id") && r.ContainsKey("estimated_bytes"))
                .ToDictionary(r => Convert.ToInt32(r["object_id"], CultureInfo.InvariantCulture), r => r["estimated_bytes"]);
            return rows.Select(row =>
            {
                var values = new Dictionary<string, object>(row, StringComparer.Ordinal);
                int id = Convert.ToInt32(values["object_id"], CultureInfo.InvariantCulture);
                object size;
                values["estimated_bytes"] = sizes.TryGetValue(id, out size) ? size : null;
                var facts = new MssqlNativeFacts(values);
                return new MssqlTableDefinition(new MssqlObjectKey(id, facts.Text("schema_name"), facts.Text("name")), values, null, null, null);
            }).ToList();
        }
        // Probe catalog columns first: SQL Server still compiles a missing column inside CASE.
        private static string Optional(SqlConnection c, SqlTransaction t, string catalog, string alias, CancellationToken token, params string[] names)
        {
            var available = new HashSet<string>(Query(c, t, "SELECT name FROM sys.all_columns WHERE object_id=OBJECT_ID(N'" + catalog + "')", token).Select(r => Convert.ToString(r["name"], CultureInfo.InvariantCulture)), StringComparer.OrdinalIgnoreCase);
            return string.Join(",", names.Select(n => OptionalProjection(alias, n, available)));
        }
        internal static string OptionalProjection(string alias, string name, IEnumerable<string> available)
        {
            return available.Contains(name, StringComparer.OrdinalIgnoreCase)
                ? alias + "." + Quote(name)
                : "CAST(" + (name == "optimize_for_sequential_key" || name == "suppress_dup_key_messages" ? "NULL" : "0") + " AS int) " + Quote(name);
        }
        internal static List<Dictionary<string, object>> Query(SqlConnection connection, SqlTransaction transaction, string sql, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var result = new List<Dictionary<string, object>>();
            using (var owner = new MssqlCommandOwner(new SqlCommand(sql, connection, transaction) { CommandTimeout = 0 }, token))
            using (var reader = owner.ExecuteReader(CommandBehavior.Default))
            {
                while (reader.Read())
                { token.ThrowIfCancellationRequested(); var row = new Dictionary<string, object>(StringComparer.Ordinal); for (int i = 0; i < reader.FieldCount; i++) row.Add(reader.GetName(i), reader.IsDBNull(i) ? null : reader.GetValue(i)); result.Add(row); }
            }
            token.ThrowIfCancellationRequested(); return result;
        }
    }
}
