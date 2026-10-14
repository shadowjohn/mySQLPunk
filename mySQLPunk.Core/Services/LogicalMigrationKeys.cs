using System.Data.Common;
using System.Text.RegularExpressions;
using MySqlPunk.Core.Models;

namespace MySqlPunk.Core.Services;

public static partial class LogicalMigrationService
{
    internal static bool PostgreSqlIndexKeyFits(int blockSize, IReadOnlyList<object?> values)
    {
        if (blockSize != 8192 || values.Count is < 1 or > 32) return false;
        long bytes = 40;
        foreach (var value in values)
        {
            long width = value switch
            {
                null => 0,
                long => 8,
                string text => 4L + System.Text.Encoding.UTF8.GetByteCount(text),
                byte[] binary => 4L + binary.Length,
                _ => 2049
            };
            bytes += (width + 7) / 8 * 8;
            if (bytes > 2048) return false;
        }
        return true;
    }

    private sealed record Keys(IReadOnlyList<string> PrimaryKey, IReadOnlyList<LogicalMigrationIndex> Indexes);

    private static async Task<Keys> ReadKeysAsync(DbConnection connection, DbTransaction? transaction,
        DatabaseProviderKind provider, string table, List<LogicalMigrationColumn> columns, List<string> blockers, CancellationToken token)
    {
        var primary = new List<string>();
        var indexes = new List<LogicalMigrationIndex>();
        if (provider == DatabaseProviderKind.Sqlite)
        {
            var ordered = new SortedDictionary<long, string>();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction; command.CommandText = "PRAGMA table_xinfo(" + Quote(table) + ");";
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                    if (reader.GetInt64(5) > 0) ordered.Add(reader.GetInt64(5), reader.GetString(1));
            }
            primary.AddRange(ordered.Values);
            var definitions = new List<(string Name, bool Unique, string Origin, bool Partial)>();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction; command.CommandText = "PRAGMA index_list(" + Quote(table) + ");";
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                    definitions.Add((reader.GetString(1), reader.GetInt64(2) != 0, reader.GetString(3), reader.GetInt64(4) != 0));
            }
            foreach (var definition in definitions)
            {
                if (definition.Origin != "pk" && (!Identifier.IsMatch(definition.Name) || definition.Unique || definition.Partial || definition.Origin != "c"))
                    blockers.Add("不支援 unique／partial／自訂索引：" + definition.Name);
                var fields = new List<string>();
                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction; command.CommandText = "PRAGMA index_xinfo(" + Quote(definition.Name) + ");";
                    await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        if (reader.GetInt64(5) == 0) continue;
                        if (reader.GetInt64(1) < 0 || reader.GetInt64(3) != 0 || reader.GetString(4) != "BINARY")
                            blockers.Add("索引只允許普通欄位、升冪、BINARY 定序：" + definition.Name);
                        else fields.Add(reader.GetString(2));
                    }
                }
                if (definition.Origin == "pk")
                {
                    if (!fields.SequenceEqual(primary)) blockers.Add("主鍵索引與欄位順序不符：" + table);
                    continue;
                }
                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "SELECT sql FROM sqlite_schema WHERE type='index' AND name=@name;";
                    var parameter = command.CreateParameter(); parameter.ParameterName = "@name"; parameter.Value = definition.Name; command.Parameters.Add(parameter);
                    var sql = Convert.ToString(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) ?? "";
                    static string Pattern(string name) => "(?:" + Regex.Escape(name) + "|\"" + Regex.Escape(name) + "\")";
                    var grammar = "\\ACREATE\\s+INDEX\\s+" + Pattern(definition.Name) + "\\s+ON\\s+" + Pattern(table) +
                        "\\s*\\(\\s*" + string.Join("\\s*,\\s*", fields.Select(name => Pattern(name) + "(?:\\s+ASC)?")) + "\\s*\\)\\s*;?\\s*\\z";
                    if (!Regex.IsMatch(sql, grammar, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                        blockers.Add("索引定義含不支援語意：" + definition.Name);
                }
                indexes.Add(new(definition.Name, fields.ToArray()));
            }
        }
        else
        {
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT ic.relname, i.indisprimary, a.attname FROM pg_index i JOIN pg_class c ON c.oid=i.indrelid JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_class ic ON ic.oid=i.indexrelid CROSS JOIN LATERAL unnest(i.indkey) WITH ORDINALITY k(attnum,position) LEFT JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum=k.attnum WHERE n.nspname='public' AND c.relname=@table ORDER BY ic.relname COLLATE \"C\", k.position;";
            var parameter = command.CreateParameter(); parameter.ParameterName = "@table"; parameter.Value = table; command.Parameters.Add(parameter);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var indexFields = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (reader.IsDBNull(2)) { blockers.Add("不支援 expression 索引：" + reader.GetString(0)); continue; }
                if (reader.GetBoolean(1)) primary.Add(reader.GetString(2));
                else
                {
                    var name = reader.GetString(0);
                    if (!Identifier.IsMatch(name)) blockers.Add("索引名稱超出安全子集：" + name);
                    if (!indexFields.TryGetValue(name, out var fields)) indexFields.Add(name, fields = new());
                    fields.Add(reader.GetString(2));
                }
            }
            indexes.AddRange(indexFields.Select(entry => new LogicalMigrationIndex(entry.Key, entry.Value.ToArray())));
        }
        if (primary.Any(name => columns.All(column => column.Name != name || column.Nullable)))
            blockers.Add("主鍵必須明確 NOT NULL：" + table);
        return new(primary.ToArray(), indexes.OrderBy(index => index.Name, StringComparer.Ordinal).ToArray());
    }
}
