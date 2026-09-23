using Microsoft.Data.Sqlite;
using MySqlPunk.Core.Models;

namespace MySqlPunk.Core.Providers;

internal sealed partial class SqliteDatabaseSession
{
    public override async Task<TableStructureInfo> GetTableStructureAsync(
        string database,
        DatabaseObjectInfo table,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        await using var connection = CreateConnection(database);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        string definition;
        await using (var schemaCommand = connection.CreateCommand())
        {
            schemaCommand.CommandText = "SELECT sql FROM sqlite_schema WHERE name = @name AND type IN ('table', 'view');";
            schemaCommand.Parameters.AddWithValue("@name", table.Name);
            definition = Convert.ToString(await schemaCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) ?? string.Empty;
        }

        var columns = new List<StructureColumnInfo>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA table_xinfo({QuoteIdentifier(table.Name)});";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var hidden = reader.GetInt64(6);
                var primaryKeyPosition = reader.GetInt64(5);
                var extra = hidden switch
                {
                    2 => "GENERATED (VIRTUAL)",
                    3 => "GENERATED (STORED)",
                    1 => "HIDDEN",
                    _ => string.Empty
                };
                columns.Add(new StructureColumnInfo(
                    columns.Count,
                    reader.GetString(1),
                    ReadText(reader, 2),
                    reader.GetInt64(3) == 0,
                    primaryKeyPosition > 0,
                    ReadText(reader, 4),
                    extra,
                    string.Empty,
                    string.Empty));
            }
        }

        var indexes = new List<StructureIndexInfo>();
        if (table.Kind == DatabaseObjectKind.Table)
        {
            var indexList = new List<(string Name, bool Unique, string Origin, bool Partial)>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"PRAGMA index_list({QuoteIdentifier(table.Name)});";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    indexList.Add((
                        reader.GetString(1),
                        reader.GetInt64(2) != 0,
                        ReadText(reader, 3),
                        reader.FieldCount > 4 && !reader.IsDBNull(4) && reader.GetInt64(4) != 0));
                }
            }

            foreach (var (name, unique, origin, partial) in indexList)
            {
                var indexColumns = new List<string>();
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = $"PRAGMA index_info({QuoteIdentifier(name)});";
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    var ordered = new List<(long Position, string Column)>();
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        ordered.Add((reader.GetInt64(0), reader.IsDBNull(2) ? "(expression)" : reader.GetString(2)));
                    }

                    indexColumns.AddRange(ordered.OrderBy(entry => entry.Position).Select(entry => entry.Column));
                }

                string indexSql;
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT sql FROM sqlite_schema WHERE type = 'index' AND name = @name;";
                    command.Parameters.AddWithValue("@name", name);
                    indexSql = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) ?? string.Empty;
                }

                var indexType = origin switch
                {
                    "pk" => "PRIMARY KEY",
                    "u" => "UNIQUE constraint",
                    _ => "CREATE INDEX"
                } + (partial ? " (partial)" : string.Empty);
                indexes.Add(new StructureIndexInfo(name, unique, origin == "pk", indexType, indexColumns, indexSql));
            }

            if (indexes.All(index => !index.IsPrimaryKey) && columns.Any(column => column.IsPrimaryKey))
            {
                // INTEGER PRIMARY KEY (rowid alias) has no sqlite_autoindex entry; document it explicitly.
                indexes.Insert(0, new StructureIndexInfo(
                    "(rowid)",
                    true,
                    true,
                    "INTEGER PRIMARY KEY (rowid alias)",
                    columns.Where(column => column.IsPrimaryKey).Select(column => column.Name).ToList(),
                    string.Empty));
            }
        }

        var foreignKeys = new List<StructureForeignKeyInfo>();
        if (table.Kind == DatabaseObjectKind.Table)
        {
            var rows = new List<(long Id, long Seq, string Table, string From, string To, string OnUpdate, string OnDelete)>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"PRAGMA foreign_key_list({QuoteIdentifier(table.Name)});";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rows.Add((
                        reader.GetInt64(0),
                        reader.GetInt64(1),
                        ReadText(reader, 2),
                        ReadText(reader, 3),
                        reader.IsDBNull(4) ? "(primary key)" : reader.GetString(4),
                        ReadText(reader, 5),
                        ReadText(reader, 6)));
                }
            }

            foreach (var group in rows.GroupBy(row => row.Id).OrderBy(group => group.Key))
            {
                var ordered = group.OrderBy(row => row.Seq).ToList();
                foreignKeys.Add(new StructureForeignKeyInfo(
                    $"fk_{group.Key}",
                    ordered.Select(row => row.From).ToList(),
                    ordered[0].Table,
                    ordered.Select(row => row.To).ToList(),
                    ordered[0].OnUpdate,
                    ordered[0].OnDelete));
            }
        }

        return new TableStructureInfo(table, columns, indexes, foreignKeys, string.Empty, definition);
    }
}
