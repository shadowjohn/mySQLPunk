using Microsoft.Data.SqlClient;
using MySqlPunk.Core.Models;

namespace MySqlPunk.Core.Providers;

internal sealed partial class SqlServerDatabaseSession
{
    public override async Task<TableStructureInfo> GetTableStructureAsync(
        string database,
        DatabaseObjectInfo table,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        var schema = string.IsNullOrWhiteSpace(table.Schema) ? "dbo" : table.Schema;
        await using var connection = (SqlConnection)await CreateConnectionAsync(database, cancellationToken).ConfigureAwait(false);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var columns = new List<StructureColumnInfo>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandTimeout = Math.Max(1, Profile.TimeoutSeconds * 2);
            command.CommandText = """
                SELECT c.name,
                       CASE
                           WHEN ty.is_user_defined = 1 THEN schema_name(ty.schema_id) + '.' + ty.name
                           WHEN ty.name IN ('varchar', 'char', 'varbinary', 'binary')
                               THEN ty.name + '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length AS varchar(10)) END + ')'
                           WHEN ty.name IN ('nvarchar', 'nchar')
                               THEN ty.name + '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length / 2 AS varchar(10)) END + ')'
                           WHEN ty.name IN ('decimal', 'numeric')
                               THEN ty.name + '(' + CAST(c.precision AS varchar(10)) + ',' + CAST(c.scale AS varchar(10)) + ')'
                           WHEN ty.name IN ('datetime2', 'datetimeoffset', 'time')
                               THEN ty.name + '(' + CAST(c.scale AS varchar(10)) + ')'
                           ELSE ty.name
                       END,
                       c.is_nullable,
                       CASE WHEN EXISTS (
                           SELECT 1 FROM sys.indexes i
                           JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                           WHERE i.object_id = c.object_id AND i.is_primary_key = 1 AND ic.column_id = c.column_id
                       ) THEN 1 ELSE 0 END,
                       dc.definition,
                       CASE WHEN c.is_identity = 1 THEN 'IDENTITY'
                            WHEN c.is_computed = 1 THEN 'COMPUTED AS ' + cc.definition
                            ELSE '' END,
                       c.collation_name,
                       CAST(ep.value AS nvarchar(max))
                FROM sys.columns c
                JOIN sys.objects o ON o.object_id = c.object_id
                JOIN sys.schemas s ON s.schema_id = o.schema_id
                JOIN sys.types ty ON ty.user_type_id = c.user_type_id
                LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
                LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
                LEFT JOIN sys.extended_properties ep
                       ON ep.major_id = c.object_id AND ep.minor_id = c.column_id AND ep.class = 1 AND ep.name = 'MS_Description'
                WHERE s.name = @schema AND o.name = @table AND o.type IN ('U', 'V')
                ORDER BY c.column_id
                """;
            command.Parameters.AddWithValue("@schema", schema);
            command.Parameters.AddWithValue("@table", table.Name);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                columns.Add(new StructureColumnInfo(
                    columns.Count,
                    reader.GetString(0),
                    ReadText(reader, 1),
                    ReadFlag(reader, 2),
                    ReadFlag(reader, 3),
                    ReadText(reader, 4),
                    ReadText(reader, 5),
                    ReadText(reader, 6),
                    ReadText(reader, 7)));
            }
        }

        string comment;
        string definition;
        await using (var command = connection.CreateCommand())
        {
            command.CommandTimeout = Math.Max(1, Profile.TimeoutSeconds * 2);
            command.CommandText = """
                SELECT CAST(ep.value AS nvarchar(max)), m.definition
                FROM sys.objects o
                JOIN sys.schemas s ON s.schema_id = o.schema_id
                LEFT JOIN sys.extended_properties ep
                       ON ep.major_id = o.object_id AND ep.minor_id = 0 AND ep.class = 1 AND ep.name = 'MS_Description'
                LEFT JOIN sys.sql_modules m ON m.object_id = o.object_id
                WHERE s.name = @schema AND o.name = @table AND o.type IN ('U', 'V')
                """;
            command.Parameters.AddWithValue("@schema", schema);
            command.Parameters.AddWithValue("@table", table.Name);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                comment = ReadText(reader, 0);
                definition = ReadText(reader, 1);
            }
            else
            {
                comment = string.Empty;
                definition = string.Empty;
            }
        }

        var indexes = new List<StructureIndexInfo>();
        if (table.Kind == DatabaseObjectKind.Table)
        {
            var rows = new List<(string Name, bool Unique, bool Primary, string Type, int KeyOrdinal, string Column, bool Included, bool Descending)>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandTimeout = Math.Max(1, Profile.TimeoutSeconds * 2);
                command.CommandText = """
                    SELECT i.name, i.is_unique, i.is_primary_key, i.type_desc, ic.key_ordinal, c.name, ic.is_included_column, ic.is_descending_key
                    FROM sys.indexes i
                    JOIN sys.objects o ON o.object_id = i.object_id
                    JOIN sys.schemas s ON s.schema_id = o.schema_id
                    JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                    JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                    WHERE s.name = @schema AND o.name = @table AND i.index_id > 0 AND i.name IS NOT NULL
                    ORDER BY i.is_primary_key DESC, i.name, ic.is_included_column, ic.key_ordinal
                    """;
                command.Parameters.AddWithValue("@schema", schema);
                command.Parameters.AddWithValue("@table", table.Name);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rows.Add((
                        reader.GetString(0),
                        ReadFlag(reader, 1),
                        ReadFlag(reader, 2),
                        ReadText(reader, 3),
                        Convert.ToInt32(reader.GetValue(4), System.Globalization.CultureInfo.InvariantCulture),
                        reader.GetString(5),
                        ReadFlag(reader, 6),
                        ReadFlag(reader, 7)));
                }
            }

            foreach (var group in rows.GroupBy(row => row.Name))
            {
                var keys = group.Where(row => !row.Included).OrderBy(row => row.KeyOrdinal)
                    .Select(row => row.Descending ? $"{row.Column} DESC" : row.Column).ToList();
                var included = group.Where(row => row.Included).Select(row => row.Column).ToList();
                var first = group.First();
                indexes.Add(new StructureIndexInfo(
                    group.Key,
                    first.Unique,
                    first.Primary,
                    first.Type,
                    keys,
                    included.Count == 0 ? string.Empty : "INCLUDE (" + string.Join(", ", included) + ")"));
            }
        }

        var foreignKeys = new List<StructureForeignKeyInfo>();
        if (table.Kind == DatabaseObjectKind.Table)
        {
            var rows = new List<(string Name, int Ordinal, string Column, string RefSchema, string RefTable, string RefColumn, string OnUpdate, string OnDelete)>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandTimeout = Math.Max(1, Profile.TimeoutSeconds * 2);
                command.CommandText = """
                    SELECT fk.name, fkc.constraint_column_id, pc.name,
                           schema_name(ro.schema_id), ro.name, rc.name,
                           fk.update_referential_action_desc, fk.delete_referential_action_desc
                    FROM sys.foreign_keys fk
                    JOIN sys.objects o ON o.object_id = fk.parent_object_id
                    JOIN sys.schemas s ON s.schema_id = o.schema_id
                    JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
                    JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
                    JOIN sys.objects ro ON ro.object_id = fk.referenced_object_id
                    JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
                    WHERE s.name = @schema AND o.name = @table
                    ORDER BY fk.name, fkc.constraint_column_id
                    """;
                command.Parameters.AddWithValue("@schema", schema);
                command.Parameters.AddWithValue("@table", table.Name);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rows.Add((
                        reader.GetString(0),
                        Convert.ToInt32(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture),
                        reader.GetString(2),
                        ReadText(reader, 3),
                        reader.GetString(4),
                        reader.GetString(5),
                        ReadText(reader, 6).Replace('_', ' '),
                        ReadText(reader, 7).Replace('_', ' ')));
                }
            }

            foreach (var group in rows.GroupBy(row => row.Name))
            {
                var ordered = group.OrderBy(row => row.Ordinal).ToList();
                var referenced = ordered[0].RefSchema.Equals(schema, StringComparison.Ordinal)
                    ? ordered[0].RefTable
                    : $"{ordered[0].RefSchema}.{ordered[0].RefTable}";
                foreignKeys.Add(new StructureForeignKeyInfo(
                    group.Key,
                    ordered.Select(row => row.Column).ToList(),
                    referenced,
                    ordered.Select(row => row.RefColumn).ToList(),
                    ordered[0].OnUpdate,
                    ordered[0].OnDelete));
            }
        }

        return new TableStructureInfo(table, columns, indexes, foreignKeys, comment, definition);
    }
}
