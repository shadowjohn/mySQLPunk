using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace mySQLPunk.lib.MssqlExport
{
    // Native catalog DDL only. Data/IDENTITY_INSERT belong to the streaming data writer.
    internal sealed class MssqlSchemaScriptWriter
    {
        internal void WriteBeforeData(MssqlExportSchema schema, MssqlExportRequest request, TextWriter output, CancellationToken cancellation)
        {
            Validate(schema, request, output, cancellation);
            if (request.Mode == MssqlExportMode.DataOnly) return;
            Settings(output);
            output.WriteLine("-- Restore into an empty, compatible target selected by the caller; existing objects fail.");
            output.WriteLine("-- Named filegroups must already exist. Rowversion values are regenerated.");
            output.WriteLine("-- Identity preserves seed/increment and inserted values, not historical allocation gaps.");
            foreach (string name in schema.Tables.Select(t => t.Key.Schema).Concat(schema.Views.Select(v => v.Key.Schema)).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
            {
                cancellation.ThrowIfCancellationRequested();
                output.WriteLine("IF SCHEMA_ID(" + Literal(name) + ") IS NULL EXEC(" + Literal("CREATE SCHEMA " + Quote(name)) + ");");
            }
            Batch(output);
            foreach (var table in schema.Tables.OrderBy(t => t.Key.ObjectId))
            {
                cancellation.ThrowIfCancellationRequested();
                output.WriteLine("CREATE TABLE " + Qualified(table.Key) + " (");
                var columns = table.Columns.OrderBy(c => c.Ordinal).ToArray();
                for (int i = 0; i < columns.Length; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    output.Write("    " + Column(columns[i]));
                    output.WriteLine(i + 1 == columns.Length ? "" : ",");
                }
                var heap = table.Indexes.FirstOrDefault(i => i.Number("type") == 0);
                output.WriteLine(")" + Placement(heap) + ";");
                if (table.Text("lock_escalation_desc") != null)
                    output.WriteLine("ALTER TABLE " + Qualified(table.Key) + " SET (LOCK_ESCALATION = " + table.Text("lock_escalation_desc") + ");");
                TableOption(output, table, "text_in_row_limit", "text in row");
                TableOption(output, table, "large_value_types_out_of_row", "large value types out of row");
                Batch(output);
            }
        }

        internal void WriteAfterData(MssqlExportSchema schema, MssqlExportRequest request, TextWriter output, CancellationToken cancellation)
        {
            Validate(schema, request, output, cancellation);
            if (request.Mode == MssqlExportMode.DataOnly) return;
            Settings(output);
            // All candidate keys precede any FK, including cross-table cycles.
            foreach (var table in schema.Tables.OrderBy(t => t.Key.ObjectId))
                foreach (var index in table.Indexes.Where(i => i.Number("type") != 0).OrderBy(i => i.Number("type")).ThenBy(i => i.IndexId))
                {
                    cancellation.ThrowIfCancellationRequested();
                    WriteIndex(table, index, output);
                }
            foreach (var table in schema.Tables.OrderBy(t => t.Key.ObjectId))
            {
                foreach (var constraint in table.Constraints.OrderBy(c => c.Name, StringComparer.Ordinal))
                {
                    cancellation.ThrowIfCancellationRequested();
                    WriteConstraint(table, constraint, output);
                }
                Comment(output, table.Key, null, table.Comment);
                foreach (var column in table.Columns) { cancellation.ThrowIfCancellationRequested(); Comment(output, table.Key, column.Name, column.Comment); }
            }
            var pending = schema.Views.ToDictionary(v => v.Key.ObjectId);
            while (pending.Count != 0)
            {
                var ready = pending.Values.Where(v => !schema.Dependencies.Any(d => d.ObjectId == v.Key.ObjectId && d.ReferencedId.HasValue && pending.ContainsKey(d.ReferencedId.Value))).OrderBy(v => v.Key.ObjectId).ToArray();
                if (ready.Length == 0) throw new InvalidOperationException("View dependency cycle.");
                foreach (var view in ready)
                {
                    cancellation.ThrowIfCancellationRequested();
                    output.WriteLine("SET ANSI_NULLS " + OnOff(view.AnsiNulls) + ";");
                    output.WriteLine("SET QUOTED_IDENTIFIER " + OnOff(view.QuotedIdentifier) + ";");
                    Batch(output);
                    output.WriteLine(ViewDefinition(view));
                    Batch(output);
                    pending.Remove(view.Key.ObjectId);
                }
            }
            // Disabling a clustered index first makes its table inaccessible and can disable FKs.
            // Delay all requested disabled states until every object is in place.
            Settings(output);
            foreach (var table in schema.Tables)
                foreach (var index in table.Indexes.Where(i => i.Flag("is_disabled")).OrderByDescending(i => i.Number("type")))
                {
                    cancellation.ThrowIfCancellationRequested();
                    output.WriteLine("ALTER INDEX " + Quote(index.Name) + " ON " + Qualified(table.Key) + " DISABLE;");
                }
            Batch(output);
        }

        private static void Validate(MssqlExportSchema schema, MssqlExportRequest request, TextWriter output, CancellationToken cancellation)
        {
            if (schema == null) throw new ArgumentNullException(nameof(schema));
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (output == null) throw new ArgumentNullException(nameof(output));
            cancellation.ThrowIfCancellationRequested();
            var issues = new MssqlExportMetadataReader().Validate(schema, request);
            if (issues.Count != 0) throw new InvalidOperationException(string.Join("; ", issues.Select(i => i.Code + ": " + i.Message)));
            if (request.Mode == MssqlExportMode.DataOnly) return;
            foreach (var view in schema.Views) ViewDefinition(view); // Fail before any partial DDL.
            foreach (var table in schema.Tables)
            {
                if (table.Columns.Count == 0) throw new InvalidOperationException("Table has no visible columns: " + Qualified(table.Key));
                foreach (var c in table.Constraints)
                    if (c.Kind != "FK" && c.Kind != "CHECK") throw new InvalidOperationException("Unsupported constraint: " + c.Kind);
            }
        }
        private static string Column(MssqlColumnDefinition c)
        {
            var sql = new StringBuilder(Quote(c.Name));
            if (c.Computed)
                return sql.Append(" AS ").Append(c.ComputedDefinition).Append(c.Persisted ? " PERSISTED" : "").Append(c.Persisted && !c.Nullable ? " NOT NULL" : "").ToString();
            sql.Append(' ').Append(NativeType(c));
            if (c.Collation != null) sql.Append(" COLLATE ").Append(c.Collation);
            if (c.Identity) sql.Append(" IDENTITY(").Append(c.IdentitySeed).Append(',').Append(c.IdentityIncrement).Append(')').Append(c.Flag("identity_not_for_replication") ? " NOT FOR REPLICATION" : "");
            if (c.Flag("is_rowguidcol")) sql.Append(" ROWGUIDCOL");
            sql.Append(c.Nullable ? " NULL" : " NOT NULL");
            if (c.DefaultDefinition != null) sql.Append(" CONSTRAINT ").Append(Quote(c.DefaultName)).Append(" DEFAULT ").Append(c.DefaultDefinition);
            return sql.ToString();
        }
        private static string NativeType(MssqlColumnDefinition c)
        {
            string type = c.NativeType.ToLowerInvariant();
            if (c.RowVersion) return "rowversion";
            switch (type)
            {
                case "varchar": case "char": case "binary": case "varbinary":
                    return type + "(" + (c.Length == -1 ? "max" : Number(c.Length)) + ")";
                case "nvarchar": case "nchar":
                    return type + "(" + (c.Length == -1 ? "max" : Number(c.Length / 2)) + ")";
                case "decimal": case "numeric": return type + "(" + Number(c.Precision) + "," + Number(c.Scale) + ")";
                case "datetime2": case "datetimeoffset": case "time": return type + "(" + Number(c.Scale) + ")";
                case "float": return type + "(" + Number(c.Precision) + ")";
                default: return type;
            }
        }
        private static void WriteIndex(MssqlTableDefinition table, MssqlIndexDefinition index, TextWriter output)
        {
            string keys = string.Join(", ", index.Columns.Where(c => c.Number("key_ordinal") > 0).OrderBy(c => c.Number("key_ordinal")).Select(c => Quote(c.Text("name")) + (c.Flag("is_descending_key") ? " DESC" : " ASC")));
            string kind = index.Number("type") == 1 ? "CLUSTERED" : "NONCLUSTERED";
            bool constraint = index.Flag("is_primary_key") || index.Flag("is_unique_constraint");
            string sql = constraint
                ? "ALTER TABLE " + Qualified(table.Key) + " ADD CONSTRAINT " + Quote(index.Name) + (index.Flag("is_primary_key") ? " PRIMARY KEY " : " UNIQUE ") + kind
                : "CREATE " + (index.Flag("is_unique") ? "UNIQUE " : "") + kind + " INDEX " + Quote(index.Name) + " ON " + Qualified(table.Key);
            sql += " (" + keys + ")";
            var includes = index.Columns.Where(c => c.Flag("is_included_column")).OrderBy(c => c.Number("index_column_id")).Select(c => Quote(c.Text("name"))).ToArray();
            if (includes.Length != 0) sql += " INCLUDE (" + string.Join(", ", includes) + ")";
            if (index.Flag("has_filter")) sql += " WHERE " + index.Text("filter_definition");
            var options = new List<string>();
            foreach (var pair in new[] { new[] {"is_padded","PAD_INDEX"}, new[] {"ignore_dup_key","IGNORE_DUP_KEY"}, new[] {"allow_row_locks","ALLOW_ROW_LOCKS"}, new[] {"allow_page_locks","ALLOW_PAGE_LOCKS"}, new[] {"no_recompute","STATISTICS_NORECOMPUTE"}, new[] {"optimize_for_sequential_key","OPTIMIZE_FOR_SEQUENTIAL_KEY"} })
                if (index.Text(pair[0]) != null) options.Add(pair[1] + " = " + OnOff(index.Flag(pair[0])));
            if (index.Number("fill_factor") != 0) options.Add("FILLFACTOR = " + Number(index.Number("fill_factor")));
            if (options.Count != 0) sql += " WITH (" + string.Join(", ", options) + ")";
            output.WriteLine(sql + Placement(index) + ";");
            Batch(output);
        }
        private static void WriteConstraint(MssqlTableDefinition table, MssqlConstraintDefinition c, TextWriter output)
        {
            string sql = "ALTER TABLE " + Qualified(table.Key) + (c.Flag("is_not_trusted") || c.Flag("is_disabled") ? " WITH NOCHECK" : " WITH CHECK") + " ADD CONSTRAINT " + Quote(c.Name) + " ";
            if (c.Kind == "CHECK") sql += "CHECK " + (c.Flag("is_not_for_replication") ? "NOT FOR REPLICATION " : "") + c.Text("definition");
            else
            {
                var columns = c.Columns.OrderBy(x => x.Number("constraint_column_id")).ToArray();
                sql += "FOREIGN KEY (" + string.Join(", ", columns.Select(x => Quote(x.Text("parent_column")))) + ") REFERENCES " + Quote(c.Text("referenced_schema")) + "." + Quote(c.Text("referenced_table")) + " (" + string.Join(", ", columns.Select(x => Quote(x.Text("referenced_column")))) + ")";
                foreach (string action in new[] { "delete", "update" })
                    if (c.Text(action + "_referential_action_desc") != null) sql += " ON " + action.ToUpperInvariant() + " " + c.Text(action + "_referential_action_desc").Replace('_', ' ');
                if (c.Flag("is_not_for_replication")) sql += " NOT FOR REPLICATION";
            }
            output.WriteLine(sql + ";");
            output.WriteLine("ALTER TABLE " + Qualified(table.Key) + (c.Flag("is_disabled") ? " NOCHECK" : " CHECK") + " CONSTRAINT " + Quote(c.Name) + ";");
            Batch(output);
        }
        private static void Comment(TextWriter output, MssqlObjectKey key, string column, string value)
        {
            if (value == null) return;
            output.WriteLine("EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=" + Literal(value) + ", @level0type=N'SCHEMA', @level0name=" + Literal(key.Schema) + ", @level1type=N'TABLE', @level1name=" + Literal(key.Name) + (column == null ? "" : ", @level2type=N'COLUMN', @level2name=" + Literal(column)) + ";");
            Batch(output);
        }
        private static void TableOption(TextWriter output, MssqlTableDefinition table, string fact, string option)
        {
            if (table.Number(fact) != 0) output.WriteLine("EXEC sys.sp_tableoption @TableNamePattern=" + Literal(Qualified(table.Key)) + ", @OptionName=" + Literal(option) + ", @OptionValue=" + Literal(table.Text(fact) == "True" ? "1" : table.Text(fact) == "False" ? "0" : table.Text(fact)) + ";");
        }
        // Read only the module header. Comments and quoted identifiers may contain keywords.
        // Rewrite ALTER/CREATE OR ALTER to CREATE so an existing target cannot be overwritten.
        // The catalog name wins over stale module text left by sp_rename.
        private static string ViewDefinition(MssqlViewDefinition view)
        {
            string text = view.Definition; int position = 0;
            string token = Token(text, ref position);
            if (string.Equals(token, "CREATE", StringComparison.OrdinalIgnoreCase))
            {
                token = Token(text, ref position);
                if (string.Equals(token, "OR", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.Equals(Token(text, ref position), "ALTER", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid view header.");
                    token = Token(text, ref position);
                }
            }
            else if (string.Equals(token, "ALTER", StringComparison.OrdinalIgnoreCase)) token = Token(text, ref position);
            else throw new InvalidOperationException("Unsupported view module header: " + Qualified(view.Key));
            if (!string.Equals(token, "VIEW", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid view header.");
            if (Token(text, ref position).Length == 0) throw new InvalidOperationException("Missing view name.");
            int end = position; int look = position;
            if (Token(text, ref look) == ".") { if (Token(text, ref look).Length == 0) throw new InvalidOperationException("Missing view name."); end = look; }
            return "CREATE VIEW " + Qualified(view.Key) + text.Substring(end);
        }
        private static string Token(string text, ref int position)
        {
            while (position < text.Length)
            {
                if (char.IsWhiteSpace(text[position])) { position++; continue; }
                if (position + 1 < text.Length && text.Substring(position, 2) == "--")
                { while (position < text.Length && text[position] != '\n') position++; continue; }
                if (position + 1 < text.Length && text.Substring(position, 2) == "/*")
                {
                    position += 2; int depth = 1;
                    while (position < text.Length && depth != 0)
                    {
                        if (position + 1 < text.Length && text.Substring(position, 2) == "/*") { depth++; position += 2; }
                        else if (position + 1 < text.Length && text.Substring(position, 2) == "*/") { depth--; position += 2; }
                        else position++;
                    }
                    if (depth != 0) throw new InvalidOperationException("Unclosed view header comment.");
                    continue;
                }
                break;
            }
            int start = position;
            if (position == text.Length) return "";
            char first = text[position++];
            if (first == '[' || first == '"')
            {
                char closing = first == '[' ? ']' : '"'; bool closed = false;
                while (position < text.Length)
                {
                    if (text[position++] != closing) continue;
                    if (position < text.Length && text[position] == closing) { position++; continue; }
                    closed = true; break;
                }
                if (!closed) throw new InvalidOperationException("Unclosed view identifier.");
            }
            else if (char.IsLetterOrDigit(first) || first == '_' || first == '@' || first == '#')
                while (position < text.Length && (char.IsLetterOrDigit(text[position]) || "_@$#".IndexOf(text[position]) >= 0)) position++;
            return text.Substring(start, position - start);
        }
        private static string Placement(MssqlIndexDefinition index) => index?.Text("data_space_name") == null ? "" : " ON " + Quote(index.Text("data_space_name"));
        private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
        private static string OnOff(bool value) => value ? "ON" : "OFF";
        private static string Quote(string value) => MssqlExportMetadataReader.Quote(value);
        private static string Qualified(MssqlObjectKey key) => MssqlExportMetadataReader.Qualified(key);
        private static string Literal(string value) => "N'" + value.Replace("'", "''") + "'";
        private static void Batch(TextWriter output) { output.WriteLine("GO"); }
        private static void Settings(TextWriter output)
        {
            output.WriteLine("SET ANSI_NULLS ON; SET QUOTED_IDENTIFIER ON; SET ANSI_PADDING ON; SET ANSI_WARNINGS ON;");
            output.WriteLine("SET ARITHABORT ON; SET CONCAT_NULL_YIELDS_NULL ON; SET NUMERIC_ROUNDABORT OFF;");
            Batch(output);
        }
    }
}
