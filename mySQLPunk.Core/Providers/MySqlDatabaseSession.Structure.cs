using MySqlConnector;
using MySqlPunk.Core.Models;

namespace MySqlPunk.Core.Providers;

internal sealed partial class MySqlDatabaseSession
{
    public override async Task<TableStructureInfo> GetTableStructureAsync(
        string database,
        DatabaseObjectInfo table,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (string.IsNullOrWhiteSpace(database))
        {
            throw new InvalidOperationException("請先選擇資料庫。");
        }

        await using var connection = (MySqlConnection)await CreateConnectionAsync(database, cancellationToken).ConfigureAwait(false);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var columns = new List<StructureColumnInfo>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, COLUMN_KEY, COLUMN_DEFAULT, EXTRA, COLLATION_NAME, COLUMN_COMMENT
                FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = @database AND TABLE_NAME = @table
                ORDER BY ORDINAL_POSITION
                """;
            command.Parameters.AddWithValue("@database", database);
            command.Parameters.AddWithValue("@table", table.Name);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                columns.Add(new StructureColumnInfo(
                    columns.Count,
                    reader.GetString(0),
                    ReadText(reader, 1),
                    ReadFlag(reader, 2),
                    ReadText(reader, 3).Equals("PRI", StringComparison.OrdinalIgnoreCase),
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
            command.CommandText = table.Kind == DatabaseObjectKind.View
                ? "SELECT VIEW_DEFINITION, '' FROM information_schema.VIEWS WHERE TABLE_SCHEMA = @database AND TABLE_NAME = @table"
                : "SELECT '', TABLE_COMMENT FROM information_schema.TABLES WHERE TABLE_SCHEMA = @database AND TABLE_NAME = @table";
            command.Parameters.AddWithValue("@database", database);
            command.Parameters.AddWithValue("@table", table.Name);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                definition = ReadText(reader, 0);
                comment = ReadText(reader, 1);
            }
            else
            {
                definition = string.Empty;
                comment = string.Empty;
            }
        }

        if (table.Kind == DatabaseObjectKind.Table)
        {
            await using var showCreate = connection.CreateCommand();
            showCreate.CommandText = $"SHOW CREATE TABLE {QuoteIdentifier(database)}.{QuoteIdentifier(table.Name)}";
            await using var reader = await showCreate.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && reader.FieldCount >= 2)
            {
                definition = ReadText(reader, 1);
            }
        }

        var indexes = new List<StructureIndexInfo>();
        if (table.Kind == DatabaseObjectKind.Table)
        {
            var rows = new List<(string Name, bool NonUnique, long Seq, string Column, string Type, string Expression)>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT INDEX_NAME, NON_UNIQUE, SEQ_IN_INDEX, COLUMN_NAME, INDEX_TYPE, SUB_PART, COLLATION
                    FROM information_schema.STATISTICS
                    WHERE TABLE_SCHEMA = @database AND TABLE_NAME = @table
                    ORDER BY INDEX_NAME, SEQ_IN_INDEX
                    """;
                command.Parameters.AddWithValue("@database", database);
                command.Parameters.AddWithValue("@table", table.Name);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    // Prefix length (SUB_PART) and direction (COLLATION = 'D') are part of the index definition;
                    // dropping them would make a prefix index look like a full-column one.
                    var column = reader.IsDBNull(3) ? "(expression)" : reader.GetString(3);
                    var prefix = ReadText(reader, 5);
                    if (prefix.Length > 0 && !reader.IsDBNull(3))
                    {
                        column += $"({prefix})";
                    }

                    if (ReadText(reader, 6).Equals("D", StringComparison.OrdinalIgnoreCase))
                    {
                        column += " DESC";
                    }

                    rows.Add((
                        reader.GetString(0),
                        ReadFlag(reader, 1),
                        Convert.ToInt64(reader.GetValue(2), System.Globalization.CultureInfo.InvariantCulture),
                        column,
                        ReadText(reader, 4),
                        string.Empty));
                }
            }

            foreach (var group in rows.GroupBy(row => row.Name))
            {
                var ordered = group.OrderBy(row => row.Seq).ToList();
                var isPrimary = group.Key.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase);
                indexes.Add(new StructureIndexInfo(
                    group.Key,
                    !ordered[0].NonUnique,
                    isPrimary,
                    ordered[0].Type,
                    ordered.Select(row => row.Column).ToList(),
                    string.Empty));
            }

            indexes = indexes.OrderByDescending(index => index.IsPrimaryKey).ThenBy(index => index.Name, StringComparer.Ordinal).ToList();
        }

        var foreignKeys = new List<StructureForeignKeyInfo>();
        if (table.Kind == DatabaseObjectKind.Table)
        {
            var rows = new List<(string Name, long Position, string Column, string RefSchema, string RefTable, string RefColumn, string OnUpdate, string OnDelete)>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT k.CONSTRAINT_NAME, k.ORDINAL_POSITION, k.COLUMN_NAME,
                           k.REFERENCED_TABLE_SCHEMA, k.REFERENCED_TABLE_NAME, k.REFERENCED_COLUMN_NAME,
                           r.UPDATE_RULE, r.DELETE_RULE
                    FROM information_schema.KEY_COLUMN_USAGE k
                    JOIN information_schema.REFERENTIAL_CONSTRAINTS r
                      ON r.CONSTRAINT_SCHEMA = k.CONSTRAINT_SCHEMA
                     AND r.CONSTRAINT_NAME = k.CONSTRAINT_NAME
                     AND r.TABLE_NAME = k.TABLE_NAME
                    WHERE k.TABLE_SCHEMA = @database AND k.TABLE_NAME = @table
                      AND k.REFERENCED_TABLE_NAME IS NOT NULL
                    ORDER BY k.CONSTRAINT_NAME, k.ORDINAL_POSITION
                    """;
                command.Parameters.AddWithValue("@database", database);
                command.Parameters.AddWithValue("@table", table.Name);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rows.Add((
                        reader.GetString(0),
                        Convert.ToInt64(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture),
                        ReadText(reader, 2),
                        ReadText(reader, 3),
                        ReadText(reader, 4),
                        ReadText(reader, 5),
                        ReadText(reader, 6),
                        ReadText(reader, 7)));
                }
            }

            foreach (var group in rows.GroupBy(row => row.Name))
            {
                var ordered = group.OrderBy(row => row.Position).ToList();
                var referenced = ordered[0].RefSchema.Equals(database, StringComparison.Ordinal)
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
