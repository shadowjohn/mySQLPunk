using System.Collections;
using System.Net.NetworkInformation;
using MySqlPunk.Core.Models;
using MySqlPunk.Core.Services;
using Npgsql;
using NpgsqlTypes;

namespace MySqlPunk.Core.Providers;

internal sealed class PostgreSqlDatabaseSession : AdoDatabaseSession
{
    public PostgreSqlDatabaseSession(ConnectionProfile profile)
        : base(profile)
    {
    }

    protected override NpgsqlConnection CreateConnection(string? database)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = Profile.Host,
            Port = Profile.Port,
            Username = Profile.Username,
            Password = Profile.Password,
            Database = string.IsNullOrWhiteSpace(database) ? "postgres" : database,
            Timeout = Profile.TimeoutSeconds,
            CommandTimeout = Math.Max(1, Profile.TimeoutSeconds * 4),
            SslMode = Profile.UseSsl ? SslMode.Prefer : SslMode.Disable
        };

        return new NpgsqlConnection(builder.ConnectionString);
    }

    protected override string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    protected override void ConfigureParameter(
        System.Data.Common.DbParameter parameter,
        TableColumnInfo column)
    {
        if (column.ValueKind == TableColumnValueKind.UnsignedInteger && parameter is NpgsqlParameter unsignedParameter)
        {
            unsignedParameter.NpgsqlDbType = column.StorageDataTypeName.ToLowerInvariant() switch
            {
                "oid" => NpgsqlDbType.Oid,
                "xid" => NpgsqlDbType.Xid,
                "cid" => NpgsqlDbType.Cid,
                "xid8" => NpgsqlDbType.Xid8,
                _ => throw new InvalidOperationException(
                    $"不支援的 PostgreSQL unsigned integer 型別：{column.DataTypeName}")
            };
            return;
        }

        base.ConfigureParameter(parameter, column);
        if (column.ValueKind == TableColumnValueKind.Json && parameter is NpgsqlParameter npgsqlParameter)
        {
            npgsqlParameter.NpgsqlDbType = string.Equals(
                column.StorageDataTypeName,
                "jsonb",
                StringComparison.OrdinalIgnoreCase)
                ? NpgsqlDbType.Jsonb
                : NpgsqlDbType.Json;
        }
        else if (column.ValueKind == TableColumnValueKind.Xml && parameter is NpgsqlParameter xmlParameter)
        {
            xmlParameter.NpgsqlDbType = NpgsqlDbType.Xml;
        }
        else if (column.ValueKind == TableColumnValueKind.NetworkAddress && parameter is NpgsqlParameter networkParameter)
        {
            networkParameter.NpgsqlDbType = column.StorageDataTypeName.ToLowerInvariant() switch
            {
                "inet" => NpgsqlDbType.Inet,
                "cidr" => NpgsqlDbType.Cidr,
                "macaddr" => NpgsqlDbType.MacAddr,
                "macaddr8" => NpgsqlDbType.MacAddr8,
                _ => throw new InvalidOperationException($"不支援的 PostgreSQL 網路位址型別：{column.DataTypeName}")
            };
        }
        else if (column.ValueKind == TableColumnValueKind.BitString && parameter is NpgsqlParameter bitParameter)
        {
            bitParameter.NpgsqlDbType = column.StorageDataTypeName.StartsWith(
                "bit varying",
                StringComparison.OrdinalIgnoreCase)
                ? NpgsqlDbType.Varbit
                : NpgsqlDbType.Bit;
        }
        else if (column.ValueKind == TableColumnValueKind.TimeWithTimeZone && parameter is NpgsqlParameter timeZoneParameter)
        {
            timeZoneParameter.NpgsqlDbType = NpgsqlDbType.TimeTz;
            timeZoneParameter.Scale = TableCellValueConverter.GetPostgreSqlTemporalScale(column);
        }
        else if (column.ValueKind == TableColumnValueKind.PostgreSqlTemporal &&
                 parameter is NpgsqlParameter temporalParameter)
        {
            var baseType = TableCellValueConverter.GetPostgreSqlTemporalBaseType(column);
            temporalParameter.NpgsqlDbType = baseType switch
            {
                "timestamp without time zone" => NpgsqlDbType.Timestamp,
                "timestamp with time zone" => NpgsqlDbType.TimestampTz,
                "time without time zone" => NpgsqlDbType.Time,
                _ => throw new InvalidOperationException(
                    $"無法建立 PostgreSQL temporal 型別「{column.StorageDataTypeName}」的參數。")
            };
            temporalParameter.Scale = TableCellValueConverter.GetPostgreSqlTemporalScale(column);
        }
        else if (column.ValueKind == TableColumnValueKind.Interval && parameter is NpgsqlParameter intervalParameter)
        {
            intervalParameter.NpgsqlDbType = NpgsqlDbType.Interval;
        }
        else if (column.ValueKind == TableColumnValueKind.LogSequenceNumber && parameter is NpgsqlParameter lsnParameter)
        {
            lsnParameter.NpgsqlDbType = NpgsqlDbType.PgLsn;
        }
        else if (column.ValueKind is TableColumnValueKind.FullTextVector or
                     TableColumnValueKind.FullTextQuery or
                     TableColumnValueKind.PostgreSqlRange or
                     TableColumnValueKind.PostgreSqlArray or
                     TableColumnValueKind.PostgreSqlGeometric or
                     TableColumnValueKind.PostgreSqlServerValidatedText or
                     TableColumnValueKind.ExactDecimal &&
                 parameter is NpgsqlParameter serverValidatedTextParameter)
        {
            serverValidatedTextParameter.NpgsqlDbType = NpgsqlDbType.Unknown;
        }
    }

    protected override object? PrepareParameterValue(TableColumnInfo column, object? value)
    {
        if (column.ValueKind == TableColumnValueKind.PostgreSqlTemporal && value is string temporal)
        {
            return TableCellValueConverter.Parse(
                column,
                new TableCellInput(column.Name, TableCellInputMode.Value, temporal));
        }

        if (column.ValueKind == TableColumnValueKind.TimeWithTimeZone && value is string timeWithTimeZone)
        {
            return TableCellValueConverter.Parse(
                column,
                new TableCellInput(column.Name, TableCellInputMode.Value, timeWithTimeZone));
        }

        if (column.ValueKind == TableColumnValueKind.Interval)
        {
            var components = value switch
            {
                IntervalComponents parsed => parsed,
                string intervalText => (IntervalComponents)TableCellValueConverter.Parse(
                    column,
                    new TableCellInput(column.Name, TableCellInputMode.Value, intervalText))!,
                _ => null
            };
            if (components is not null)
            {
                return new NpgsqlInterval(components.Months, components.Days, components.Microseconds);
            }
        }

        if (column.ValueKind == TableColumnValueKind.LogSequenceNumber && value is string lsn)
        {
            return NpgsqlLogSequenceNumber.Parse(lsn);
        }

        if (column.ValueKind == TableColumnValueKind.UnsignedInteger && value is not null)
        {
            var unsigned = Convert.ToUInt64(value, System.Globalization.CultureInfo.InvariantCulture);
            if (column.StorageDataTypeName.Equals("xid8", StringComparison.OrdinalIgnoreCase))
            {
                return unsigned;
            }

            return checked((uint)unsigned);
        }

        if (column.ValueKind != TableColumnValueKind.NetworkAddress || value is not string text)
        {
            return column.ValueKind == TableColumnValueKind.BitString && value is string bits
                ? new BitArray(bits.Select(character => character == '1').ToArray())
                : base.PrepareParameterValue(column, value);
        }

        return column.StorageDataTypeName.ToLowerInvariant() switch
        {
            "inet" => new NpgsqlInet(text),
            "cidr" => new NpgsqlCidr(text),
            "macaddr" or "macaddr8" => new PhysicalAddress(
                Convert.FromHexString(text.Replace(":", string.Empty, StringComparison.Ordinal))),
            _ => throw new InvalidOperationException($"不支援的 PostgreSQL 網路位址型別：{column.DataTypeName}")
        };
    }

    protected override string BuildOriginalValuePredicate(TableColumnInfo column, string parameterName)
    {
        if (column.ValueKind == TableColumnValueKind.Json &&
            string.Equals(column.StorageDataTypeName, "json", StringComparison.OrdinalIgnoreCase))
        {
            return $"{QuoteIdentifier(column.Name)}::jsonb = CAST({parameterName} AS jsonb)";
        }

        if (column.ValueKind == TableColumnValueKind.Xml)
        {
            return $"CAST({QuoteIdentifier(column.Name)} AS text) = CAST({parameterName} AS text)";
        }

        if (column.ValueKind == TableColumnValueKind.ExactDecimal)
        {
            return $"{QuoteIdentifier(column.Name)} = {BuildParameterValueExpression(column, parameterName)}";
        }

        if (column.ValueKind is TableColumnValueKind.PostgreSqlGeometric or
            TableColumnValueKind.PostgreSqlServerValidatedText)
        {
            return $"CAST({QuoteIdentifier(column.Name)} AS text) = CAST({parameterName} AS text)";
        }

        if (column.ValueKind == TableColumnValueKind.Interval)
        {
            return $"{BuildIntervalComponentsExpression(QuoteIdentifier(column.Name))} = " +
                   BuildIntervalComponentsExpression(parameterName);
        }

        return base.BuildOriginalValuePredicate(column, parameterName);
    }

    protected override string BuildTableDataSelectExpression(TableColumnInfo column)
    {
        var quotedName = QuoteIdentifier(column.Name);
        if (column.ValueKind == TableColumnValueKind.PostgreSqlTemporal)
        {
            return BuildPostgreSqlTemporalSelectExpression(column, quotedName);
        }

        if (column.ValueKind == TableColumnValueKind.Interval)
        {
            return $"CASE WHEN {quotedName} IS NULL THEN NULL ELSE " +
                   $"{BuildIntervalComponentsExpression(quotedName)} END AS {quotedName}";
        }

        return column.ValueKind is TableColumnValueKind.NetworkAddress or
            TableColumnValueKind.BitString or
            TableColumnValueKind.TimeWithTimeZone or
            TableColumnValueKind.LogSequenceNumber or
            TableColumnValueKind.FullTextVector or
            TableColumnValueKind.FullTextQuery or
            TableColumnValueKind.PostgreSqlRange or
            TableColumnValueKind.PostgreSqlArray or
            TableColumnValueKind.PostgreSqlGeometric or
            TableColumnValueKind.PostgreSqlServerValidatedText or
            TableColumnValueKind.ExactDecimal
            ? $"CAST({quotedName} AS text) AS {quotedName}"
            : base.BuildTableDataSelectExpression(column);
    }

    private static string BuildIntervalComponentsExpression(string expression) =>
        $"CONCAT('months=', ((EXTRACT(YEAR FROM {expression}) * 12 + EXTRACT(MONTH FROM {expression}))::bigint), " +
        $"';days=', (EXTRACT(DAY FROM {expression})::bigint), " +
        $"';microseconds=', (ROUND((EXTRACT(HOUR FROM {expression}) * 3600 + " +
        $"EXTRACT(MINUTE FROM {expression}) * 60 + EXTRACT(SECOND FROM {expression})) * 1000000)::bigint))";

    private static string BuildPostgreSqlTemporalSelectExpression(TableColumnInfo column, string quotedName)
    {
        var baseType = TableCellValueConverter.GetPostgreSqlTemporalBaseType(column);
        if (baseType == "time without time zone")
        {
            return $"CAST({quotedName} AS text) AS {quotedName}";
        }

        var scale = TableCellValueConverter.GetPostgreSqlTemporalScale(column);
        var length = scale == 0 ? 19 : 20 + scale;
        var source = baseType == "timestamp with time zone"
            ? $"{quotedName} AT TIME ZONE 'UTC'"
            : quotedName;
        var formatted = $"LEFT(to_char({source}, 'YYYY-MM-DD\"T\"HH24:MI:SS.US'), {length})";
        return baseType == "timestamp with time zone"
            ? $"({formatted} || 'Z') AS {quotedName}"
            : $"{formatted} AS {quotedName}";
    }

    protected override string BuildParameterValueExpression(TableColumnInfo column, string parameterName)
    {
        if (column.ValueKind != TableColumnValueKind.ExactDecimal)
        {
            return base.BuildParameterValueExpression(column, parameterName);
        }

        var definition = TableCellValueConverter.GetExactDecimalDefinition(column);
        var typeName = definition is { Precision: { } precision, Scale: { } scale }
            ? $"numeric({precision},{scale})"
            : "numeric";
        return $"CAST({parameterName} AS {typeName})";
    }

    public override async Task<IReadOnlyList<string>> GetDatabasesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection(Profile.Database);
        return await ReadStringsAsync(
            connection,
            "SELECT datname FROM pg_database WHERE datallowconn AND NOT datistemplate ORDER BY datname",
            cancellationToken).ConfigureAwait(false);
    }

    public override async Task<IReadOnlyList<DatabaseObjectInfo>> GetObjectsAsync(
        string database,
        CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection(database);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT table_schema, table_name, table_type
            FROM information_schema.tables
            WHERE table_schema NOT IN ('pg_catalog', 'information_schema')
            ORDER BY table_schema, table_type, table_name
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var objects = new List<DatabaseObjectInfo>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var kind = string.Equals(reader.GetString(2), "VIEW", StringComparison.OrdinalIgnoreCase)
                ? DatabaseObjectKind.View
                : DatabaseObjectKind.Table;
            objects.Add(new DatabaseObjectInfo(reader.GetString(0), reader.GetString(1), kind));
        }

        return objects;
    }

    protected override async Task<IReadOnlyList<TableColumnInfo>> GetTableColumnsAsync(
        string database,
        DatabaseObjectInfo table,
        CancellationToken cancellationToken)
    {
        var schema = string.IsNullOrWhiteSpace(table.Schema) ? "public" : table.Schema;
        await using var connection = CreateConnection(database);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.column_name,
                   CASE WHEN c.data_type = 'USER-DEFINED' THEN c.udt_name ELSE c.data_type END AS display_type,
                   c.data_type,
                   c.udt_name,
                   c.is_nullable,
                   EXISTS (
                       SELECT 1
                       FROM information_schema.table_constraints tc
                       JOIN information_schema.key_column_usage kcu
                         ON kcu.constraint_catalog = tc.constraint_catalog
                        AND kcu.constraint_schema = tc.constraint_schema
                        AND kcu.constraint_name = tc.constraint_name
                       WHERE tc.constraint_type = 'PRIMARY KEY'
                         AND tc.table_schema = c.table_schema
                         AND tc.table_name = c.table_name
                         AND kcu.column_name = c.column_name
                   ) AS is_primary_key,
                   c.is_identity,
                   c.is_generated,
                   c.column_default,
                   c.character_maximum_length,
                   pg_catalog.format_type(a.atttypid, a.atttypmod),
                   c.domain_schema,
                   c.domain_name,
                   CASE WHEN declared_ty.typtype = 'd'
                       THEN pg_catalog.format_type(declared_ty.typbasetype, declared_ty.typtypmod)
                       ELSE pg_catalog.format_type(a.atttypid, a.atttypmod)
                   END AS storage_type
            FROM information_schema.columns c
            JOIN pg_catalog.pg_namespace n ON n.nspname = c.table_schema
            JOIN pg_catalog.pg_class rel
              ON rel.relnamespace = n.oid
             AND rel.relname = c.table_name
            JOIN pg_catalog.pg_attribute a
              ON a.attrelid = rel.oid
             AND a.attname = c.column_name
             AND a.attnum > 0
             AND NOT a.attisdropped
            JOIN pg_catalog.pg_type declared_ty ON declared_ty.oid = a.atttypid
            WHERE c.table_schema = @schema
              AND c.table_name = @table
            ORDER BY c.ordinal_position
            """;
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table.Name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var columns = new List<TableColumnInfo>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var dataType = reader.GetString(2);
            var userDefinedType = reader.GetString(3);
            var displayType = reader.GetString(1);
            var storageType = reader.GetString(13);
            var isDomain = !reader.IsDBNull(11) && !reader.IsDBNull(12);
            if (isDomain)
            {
                displayType = $"{QuoteIdentifier(reader.GetString(11))}." +
                              $"{QuoteIdentifier(reader.GetString(12))} ({storageType})";
            }
            else if (dataType.Equals("ARRAY", StringComparison.OrdinalIgnoreCase))
            {
                displayType = reader.GetString(10);
            }
            else if (dataType is "numeric" or "decimal")
            {
                displayType = reader.GetString(10);
            }
            else if (dataType is "bit" or "bit varying" && !reader.IsDBNull(9))
            {
                displayType = $"{displayType}({reader.GetInt32(9)})";
            }
            var generated = string.Equals(reader.GetString(6), "YES", StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(reader.GetString(7), "NEVER", StringComparison.OrdinalIgnoreCase);
            columns.Add(new TableColumnInfo(
                columns.Count,
                reader.GetString(0),
                displayType,
                string.Equals(reader.GetString(4), "YES", StringComparison.OrdinalIgnoreCase),
                reader.GetBoolean(5),
                generated,
                !reader.IsDBNull(8),
                MapValueKind(dataType, userDefinedType))
            {
                StorageDataTypeName = storageType
            });
        }

        return columns;
    }

    private static TableColumnValueKind MapValueKind(string dataType, string userDefinedType) =>
        dataType.ToLowerInvariant() switch
        {
            "smallint" or "integer" or "bigint" => TableColumnValueKind.Integer,
            "numeric" or "decimal" => TableColumnValueKind.ExactDecimal,
            "money" => TableColumnValueKind.Decimal,
            "real" or "double precision" => TableColumnValueKind.FloatingPoint,
            "boolean" => TableColumnValueKind.Boolean,
            "date" => TableColumnValueKind.Date,
            "timestamp without time zone" or "timestamp with time zone" or "time without time zone" =>
                TableColumnValueKind.PostgreSqlTemporal,
            "time with time zone" => TableColumnValueKind.TimeWithTimeZone,
            "interval" => TableColumnValueKind.Interval,
            "pg_lsn" => TableColumnValueKind.LogSequenceNumber,
            "tsvector" => TableColumnValueKind.FullTextVector,
            "tsquery" => TableColumnValueKind.FullTextQuery,
            "int4range" or "int8range" or "numrange" or "tsrange" or "tstzrange" or "daterange" or
                "int4multirange" or "int8multirange" or "nummultirange" or "tsmultirange" or
            "tstzmultirange" or "datemultirange" => TableColumnValueKind.PostgreSqlRange,
            "array" => TableColumnValueKind.PostgreSqlArray,
            "point" or "line" or "lseg" or "box" or "path" or "polygon" or "circle" =>
                TableColumnValueKind.PostgreSqlGeometric,
            "jsonpath" or "pg_snapshot" or "txid_snapshot" or
                "regclass" or "regcollation" or "regconfig" or "regdictionary" or "regnamespace" or
                "regoper" or "regoperator" or "regproc" or "regprocedure" or "regrole" or "regtype" =>
                TableColumnValueKind.PostgreSqlServerValidatedText,
            "oid" or "xid" or "cid" or "xid8" => TableColumnValueKind.UnsignedInteger,
            "uuid" => TableColumnValueKind.Guid,
            "json" or "jsonb" => TableColumnValueKind.Json,
            "xml" => TableColumnValueKind.Xml,
            "inet" or "cidr" or "macaddr" or "macaddr8" => TableColumnValueKind.NetworkAddress,
            "bit" or "bit varying" => TableColumnValueKind.BitString,
            "bytea" => TableColumnValueKind.Binary,
            "character" or "character varying" or "text" => TableColumnValueKind.String,
            "user-defined" when string.Equals(userDefinedType, "citext", StringComparison.OrdinalIgnoreCase) =>
                TableColumnValueKind.String,
            "user-defined" when string.Equals(userDefinedType, "pg_lsn", StringComparison.OrdinalIgnoreCase) =>
                TableColumnValueKind.LogSequenceNumber,
            "user-defined" when userDefinedType.Equals("hstore", StringComparison.OrdinalIgnoreCase) ||
                                userDefinedType.Equals("ltree", StringComparison.OrdinalIgnoreCase) ||
                                userDefinedType.Equals("lquery", StringComparison.OrdinalIgnoreCase) ||
                                userDefinedType.Equals("ltxtquery", StringComparison.OrdinalIgnoreCase) =>
                TableColumnValueKind.PostgreSqlServerValidatedText,
            "user-defined" => TableColumnValueKind.PostgreSqlServerValidatedText,
            _ => TableColumnValueKind.Unsupported
        };
}
