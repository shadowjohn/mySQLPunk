using MySqlConnector;
using System.Data.Common;
using MySqlPunk.Core.Models;
using MySqlPunk.Core.Services;

namespace MySqlPunk.Core.Providers;

internal sealed class MySqlDatabaseSession : AdoDatabaseSession
{
    public MySqlDatabaseSession(ConnectionProfile profile)
        : base(profile)
    {
    }

    protected override MySqlConnection CreateConnection(string? database)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = Profile.Host,
            Port = (uint)Profile.Port,
            UserID = Profile.Username,
            Password = Profile.Password,
            ConnectionTimeout = (uint)Profile.TimeoutSeconds,
            DefaultCommandTimeout = (uint)Math.Max(1, Profile.TimeoutSeconds * 4),
            SslMode = Profile.UseSsl ? MySqlSslMode.Preferred : MySqlSslMode.None,
            AllowUserVariables = false
        };

        if (!string.IsNullOrWhiteSpace(database))
        {
            builder.Database = database;
        }

        return new MySqlConnection(builder.ConnectionString);
    }

    protected override string QuoteIdentifier(string value) => $"`{value.Replace("`", "``")}`";

    protected override async Task ValidateMutationDiagnosticsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string operation,
        CancellationToken cancellationToken)
    {
        if (connection is not MySqlConnection)
        {
            throw new InvalidOperationException("無法讀取 MySQL／MariaDB 寫入診斷資訊。");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = Math.Max(1, Profile.TimeoutSeconds * 2);
        command.CommandText = "SHOW COUNT(*) WARNINGS";
        var warningCount = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        if (warningCount == 0)
        {
            return;
        }

        command.CommandText = "SHOW WARNINGS";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var diagnostics = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var level = reader.GetString(0);
            var code = reader.GetInt32(1);
            var message = reader.GetString(2);
            diagnostics.Add($"{level} {code}: {message}");
        }

        const int maximumReportedDiagnostics = 5;
        var summary = diagnostics.Count == 0
            ? $"server 回報 {warningCount:N0} 項，但目前 session 未保存診斷明細"
            : string.Join("；", diagnostics.Take(maximumReportedDiagnostics));
        var unreportedCount = Math.Max(0, warningCount - Math.Min(diagnostics.Count, maximumReportedDiagnostics));
        if (unreportedCount > 0 && diagnostics.Count > 0)
        {
            summary += $"；另有 {unreportedCount:N0} 項";
        }

        throw new InvalidOperationException(
            $"MySQL／MariaDB 回報寫入警告；為避免截斷、替換或其他資料失真，本次{operation}已回復。{summary}");
    }

    protected override void ConfigureParameter(
        System.Data.Common.DbParameter parameter,
        TableColumnInfo column)
    {
        base.ConfigureParameter(parameter, column);
        if (column.ValueKind == TableColumnValueKind.Json && parameter is MySqlParameter mySqlParameter)
        {
            mySqlParameter.MySqlDbType = MySqlDbType.JSON;
        }
        else if (column.ValueKind == TableColumnValueKind.UnsignedInteger &&
                 column.DataTypeName.StartsWith("bit(", StringComparison.OrdinalIgnoreCase) &&
                 parameter is MySqlParameter bitParameter)
        {
            bitParameter.MySqlDbType = MySqlDbType.Bit;
        }
        else if (column.ValueKind == TableColumnValueKind.MySqlTime && parameter is MySqlParameter timeParameter)
        {
            timeParameter.MySqlDbType = MySqlDbType.Time;
        }
        else if (column.ValueKind == TableColumnValueKind.MySqlTemporal &&
                 parameter is MySqlParameter temporalParameter)
        {
            var baseType = TableCellValueConverter.GetMySqlTemporalBaseType(column);
            temporalParameter.MySqlDbType = baseType switch
            {
                "date" => MySqlDbType.Date,
                "datetime" => MySqlDbType.DateTime,
                "timestamp" => MySqlDbType.Timestamp,
                _ => throw new InvalidOperationException(
                    $"無法建立 MySQL／MariaDB temporal 型別「{column.StorageDataTypeName}」的參數。")
            };
            temporalParameter.Scale = TableCellValueConverter.GetMySqlTemporalScale(column);
        }
        else if (column.ValueKind == TableColumnValueKind.MySqlYear && parameter is MySqlParameter yearParameter)
        {
            yearParameter.MySqlDbType = MySqlDbType.Year;
        }
        else if (column.ValueKind == TableColumnValueKind.ExactDecimal && parameter is MySqlParameter decimalParameter)
        {
            decimalParameter.MySqlDbType = MySqlDbType.VarChar;
        }
        else if (column.ValueKind is TableColumnValueKind.SinglePrecisionFloatingPoint or
                     TableColumnValueKind.DoublePrecisionFloatingPoint &&
                 parameter is MySqlParameter floatingPointParameter)
        {
            floatingPointParameter.MySqlDbType =
                column.ValueKind == TableColumnValueKind.SinglePrecisionFloatingPoint
                    ? MySqlDbType.Float
                    : MySqlDbType.Double;
        }
        else if (column.ValueKind == TableColumnValueKind.Spatial && parameter is MySqlParameter spatialParameter)
        {
            spatialParameter.MySqlDbType = MySqlDbType.VarChar;
        }
        else if (column.ValueKind == TableColumnValueKind.Guid &&
                 column.StorageDataTypeName.Equals("uuid", StringComparison.OrdinalIgnoreCase) &&
                 parameter is MySqlParameter uuidParameter)
        {
            uuidParameter.MySqlDbType = MySqlDbType.VarChar;
            uuidParameter.Size = 36;
        }
        else if (column.ValueKind == TableColumnValueKind.NetworkAddress &&
                 column.StorageDataTypeName is var networkDataType &&
                 (networkDataType.Equals("inet4", StringComparison.OrdinalIgnoreCase) ||
                  networkDataType.Equals("inet6", StringComparison.OrdinalIgnoreCase)) &&
                 parameter is MySqlParameter networkParameter)
        {
            networkParameter.MySqlDbType = MySqlDbType.VarChar;
            networkParameter.Size = networkDataType.Equals("inet4", StringComparison.OrdinalIgnoreCase)
                ? 15
                : 45;
        }
        else if (column.ValueKind == TableColumnValueKind.String && parameter is MySqlParameter stringParameter)
        {
            if (column.DataTypeName.StartsWith("enum(", StringComparison.OrdinalIgnoreCase))
            {
                stringParameter.MySqlDbType = MySqlDbType.Enum;
            }
            else if (column.DataTypeName.StartsWith("set(", StringComparison.OrdinalIgnoreCase))
            {
                stringParameter.MySqlDbType = MySqlDbType.Set;
            }
        }
    }

    protected override string BuildOriginalValuePredicate(TableColumnInfo column, string parameterName)
    {
        if (column.ValueKind == TableColumnValueKind.Json)
        {
            return $"CAST(CAST({QuoteIdentifier(column.Name)} AS CHAR) AS BINARY) = " +
                   $"CAST(CAST({parameterName} AS CHAR) AS BINARY)";
        }

        if (column.ValueKind is TableColumnValueKind.ExactDecimal or
            TableColumnValueKind.Guid or
            TableColumnValueKind.NetworkAddress)
        {
            return $"{QuoteIdentifier(column.Name)} = {BuildParameterValueExpression(column, parameterName)}";
        }

        if (column.ValueKind == TableColumnValueKind.SinglePrecisionFloatingPoint)
        {
            return $"CAST({QuoteIdentifier(column.Name)} AS DOUBLE) = {parameterName}";
        }

        if (column.ValueKind == TableColumnValueKind.Spatial)
        {
            var quotedName = QuoteIdentifier(column.Name);
            return $"ST_SRID({quotedName}) = {BuildSpatialSridExpression(parameterName)} AND " +
                   $"CAST(ST_AsText({quotedName}) AS BINARY) = " +
                   $"CAST({BuildSpatialWktExpression(parameterName)} AS BINARY)";
        }

        return base.BuildOriginalValuePredicate(column, parameterName);
    }

    protected override void ConfigureOriginalParameter(
        System.Data.Common.DbParameter parameter,
        TableColumnInfo column)
    {
        if (column.ValueKind == TableColumnValueKind.SinglePrecisionFloatingPoint &&
            parameter is MySqlParameter floatingPointParameter)
        {
            floatingPointParameter.MySqlDbType = MySqlDbType.Double;
            return;
        }

        base.ConfigureOriginalParameter(parameter, column);
    }

    protected override object? PrepareOriginalParameterValue(TableColumnInfo column, object? value)
    {
        if (column.ValueKind == TableColumnValueKind.SinglePrecisionFloatingPoint &&
            value is not null and not DBNull)
        {
            return Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        return base.PrepareOriginalParameterValue(column, value);
    }

    protected override object? PrepareParameterValue(TableColumnInfo column, object? value)
    {
        if (column.ValueKind == TableColumnValueKind.MySqlTime && value is string time)
        {
            return TableCellValueConverter.Parse(
                column,
                new TableCellInput(column.Name, TableCellInputMode.Value, time));
        }

        if (column.ValueKind == TableColumnValueKind.MySqlTemporal && value is string temporal)
        {
            return TableCellValueConverter.Parse(
                column,
                new TableCellInput(column.Name, TableCellInputMode.Value, temporal));
        }

        if (column.ValueKind == TableColumnValueKind.Guid &&
            column.StorageDataTypeName.Equals("uuid", StringComparison.OrdinalIgnoreCase) &&
            value is Guid uuid)
        {
            return uuid.ToString("D").ToLowerInvariant();
        }

        return base.PrepareParameterValue(column, value);
    }

    protected override string BuildTableDataSelectExpression(TableColumnInfo column)
    {
        var quotedName = QuoteIdentifier(column.Name);
        return column.ValueKind switch
        {
            TableColumnValueKind.MySqlTemporal => BuildMySqlTemporalSelectExpression(column, quotedName),
            TableColumnValueKind.MySqlTime or
            TableColumnValueKind.ExactDecimal or
            TableColumnValueKind.Guid or
            TableColumnValueKind.NetworkAddress =>
                $"CAST({quotedName} AS CHAR) AS {quotedName}",
            TableColumnValueKind.SinglePrecisionFloatingPoint =>
                $"CAST({quotedName} AS DOUBLE) AS {quotedName}",
            TableColumnValueKind.Spatial =>
                $"CONCAT('SRID=', ST_SRID({quotedName}), ';', ST_AsText({quotedName})) AS {quotedName}",
            _ => base.BuildTableDataSelectExpression(column)
        };
    }

    protected override string BuildParameterValueExpression(TableColumnInfo column, string parameterName)
    {
        if (column.ValueKind == TableColumnValueKind.Spatial)
        {
            return BuildSpatialValueExpression(parameterName);
        }

        if (column.ValueKind == TableColumnValueKind.Guid &&
            column.StorageDataTypeName.Equals("uuid", StringComparison.OrdinalIgnoreCase))
        {
            return $"CAST({parameterName} AS UUID)";
        }

        if (column.ValueKind == TableColumnValueKind.NetworkAddress &&
            column.StorageDataTypeName is var networkDataType &&
            (networkDataType.Equals("inet4", StringComparison.OrdinalIgnoreCase) ||
             networkDataType.Equals("inet6", StringComparison.OrdinalIgnoreCase)))
        {
            var networkTypeName = networkDataType.Equals("inet4", StringComparison.OrdinalIgnoreCase)
                ? "INET4"
                : "INET6";
            return $"CAST({parameterName} AS {networkTypeName})";
        }

        if (column.ValueKind != TableColumnValueKind.ExactDecimal)
        {
            return base.BuildParameterValueExpression(column, parameterName);
        }

        var definition = TableCellValueConverter.GetExactDecimalDefinition(column);
        var typeName = definition is { Precision: { } precision, Scale: { } scale }
            ? $"DECIMAL({precision},{scale})"
            : "DECIMAL";
        return $"CAST({parameterName} AS {typeName})";
    }

    private static string BuildSpatialSridExpression(string parameterName) =>
        $"CAST(SUBSTRING({parameterName}, 6, LOCATE(';', {parameterName}) - 6) AS UNSIGNED)";

    private static string BuildMySqlTemporalSelectExpression(TableColumnInfo column, string quotedName)
    {
        var baseType = TableCellValueConverter.GetMySqlTemporalBaseType(column);
        if (baseType == "date")
        {
            return $"DATE_FORMAT({quotedName}, '%Y-%m-%d') AS {quotedName}";
        }

        var scale = TableCellValueConverter.GetMySqlTemporalScale(column);
        var formatted = $"DATE_FORMAT({quotedName}, '%Y-%m-%dT%H:%i:%s.%f')";
        var length = scale == 0 ? 19 : 20 + scale;
        return $"LEFT({formatted}, {length}) AS {quotedName}";
    }

    private static string BuildSpatialWktExpression(string parameterName) =>
        $"SUBSTRING({parameterName}, LOCATE(';', {parameterName}) + 1)";

    private static string BuildSpatialValueExpression(string parameterName)
    {
        var sridExpression = BuildSpatialSridExpression(parameterName);
        var wktExpression = BuildSpatialWktExpression(parameterName);
        var parsedExpression = $"ST_GeomFromText({wktExpression}, {sridExpression})";

        // MariaDB reports malformed WKT as NULL plus a warning, even in strict mode. Force the
        // NULL branch to raise a numeric overflow so nullable spatial columns cannot silently lose data.
        var failClosedWktExpression =
            $"CASE WHEN {parsedExpression} IS NULL " +
            $"THEN CONCAT('POINT(', EXP(10000), ' 0)') ELSE {wktExpression} END";
        return $"ST_GeomFromText({failClosedWktExpression}, {sridExpression})";
    }

    public override async Task<IReadOnlyList<string>> GetDatabasesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection(null);
        return await ReadStringsAsync(
            connection,
            "SELECT SCHEMA_NAME FROM information_schema.SCHEMATA ORDER BY SCHEMA_NAME",
            cancellationToken).ConfigureAwait(false);
    }

    public override async Task<IReadOnlyList<DatabaseObjectInfo>> GetObjectsAsync(
        string database,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(database))
        {
            return Array.Empty<DatabaseObjectInfo>();
        }

        await using var connection = CreateConnection(database);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE
            FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = @database
            ORDER BY TABLE_TYPE, TABLE_NAME
            """;
        command.Parameters.AddWithValue("@database", database);
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
        await using var connection = CreateConnection(database);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COLUMN_NAME, COLUMN_TYPE, DATA_TYPE, IS_NULLABLE, COLUMN_KEY, EXTRA, GENERATION_EXPRESSION, COLUMN_DEFAULT
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = @database
              AND TABLE_NAME = @table
            ORDER BY ORDINAL_POSITION
            """;
        command.Parameters.AddWithValue("@database", database);
        command.Parameters.AddWithValue("@table", table.Name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var columns = new List<TableColumnInfo>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var columnType = reader.GetString(1);
            var dataType = reader.GetString(2);
            var extra = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
            var generationExpression = reader.IsDBNull(6) ? string.Empty : reader.GetString(6);
            var generated = extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase) ||
                            extra.Contains("virtual generated", StringComparison.OrdinalIgnoreCase) ||
                            extra.Contains("stored generated", StringComparison.OrdinalIgnoreCase) ||
                            !string.IsNullOrWhiteSpace(generationExpression);
            var declaredStringValues = GetDeclaredStringValues(dataType, columnType);
            var valueKind = MapValueKind(dataType, columnType);
            if (dataType.Equals("set", StringComparison.OrdinalIgnoreCase) &&
                declaredStringValues?.Any(value => value.Length == 0 || value.Contains(',')) == true)
            {
                valueKind = TableColumnValueKind.Unsupported;
            }
            var integerBounds = GetIntegerBounds(dataType, columnType, valueKind);
            var requiredBinaryLength = GetRequiredBinaryLength(dataType, columnType);
            columns.Add(new TableColumnInfo(
                columns.Count,
                reader.GetString(0),
                columnType,
                string.Equals(reader.GetString(3), "YES", StringComparison.OrdinalIgnoreCase),
                string.Equals(reader.GetString(4), "PRI", StringComparison.OrdinalIgnoreCase),
                generated,
                !reader.IsDBNull(7),
                valueKind)
            {
                IntegerMinimum = integerBounds?.Minimum,
                IntegerMaximum = integerBounds?.Maximum,
                RequiredBinaryLength = requiredBinaryLength,
                AllowedStringValues = dataType.Equals("enum", StringComparison.OrdinalIgnoreCase)
                    ? declaredStringValues
                    : null,
                StringSetMembers = dataType.Equals("set", StringComparison.OrdinalIgnoreCase)
                    ? declaredStringValues
                    : null,
                TrailingSpacesAreNotRoundTrippable = dataType.Equals(
                    "char",
                    StringComparison.OrdinalIgnoreCase)
            });
        }

        return columns;
    }

    private static IReadOnlyList<string>? GetDeclaredStringValues(string dataType, string columnType) =>
        dataType.Equals("enum", StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("set", StringComparison.OrdinalIgnoreCase)
            ? ParseDeclaredStringValues(dataType, columnType)
            : null;

    private static IReadOnlyList<string> ParseDeclaredStringValues(string dataType, string columnType)
    {
        var prefix = dataType + "(";
        var definition = columnType.Trim();
        if (!definition.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !definition.EndsWith(')'))
        {
            throw BuildStringMetadataException(dataType, columnType);
        }

        var values = new List<string>();
        var index = prefix.Length;
        var end = definition.Length - 1;
        while (index < end)
        {
            if (definition[index] != '\'')
            {
                throw BuildStringMetadataException(dataType, columnType);
            }

            index++;
            var value = new System.Text.StringBuilder();
            var closed = false;
            while (index < end)
            {
                var character = definition[index++];
                if (character == '\\')
                {
                    if (index >= end)
                    {
                        throw BuildStringMetadataException(dataType, columnType);
                    }

                    value.Append(DecodeMetadataEscape(definition[index++]));
                    continue;
                }

                if (character != '\'')
                {
                    value.Append(character);
                    continue;
                }

                if (index < end && definition[index] == '\'')
                {
                    value.Append('\'');
                    index++;
                    continue;
                }

                closed = true;
                break;
            }

            if (!closed)
            {
                throw BuildStringMetadataException(dataType, columnType);
            }

            values.Add(value.ToString());
            if (index == end)
            {
                break;
            }

            if (definition[index++] != ',' || index == end)
            {
                throw BuildStringMetadataException(dataType, columnType);
            }
        }

        if (values.Count == 0 || index != end)
        {
            throw BuildStringMetadataException(dataType, columnType);
        }

        return values;
    }

    private static InvalidOperationException BuildStringMetadataException(string dataType, string columnType) =>
        new($"無法解析 {dataType.ToUpperInvariant()} metadata「{columnType}」。");

    private static char DecodeMetadataEscape(char escaped) => escaped switch
    {
        '0' => '\0',
        'b' => '\b',
        'n' => '\n',
        'r' => '\r',
        't' => '\t',
        'Z' => '\u001A',
        _ => escaped
    };

    protected override string BuildDefaultInsertSql(DatabaseObjectInfo table) =>
        $"INSERT INTO {BuildQualifiedName(table)} () VALUES ();";

    private static TableColumnValueKind MapValueKind(string dataType, string columnType)
    {
        var normalizedDataType = dataType.ToLowerInvariant();
        var normalizedColumnType = columnType.ToLowerInvariant();
        if (normalizedDataType == "tinyint" && normalizedColumnType.StartsWith("tinyint(1)", StringComparison.Ordinal))
        {
            return TableColumnValueKind.Boolean;
        }

        if (normalizedDataType is "tinyint" or "smallint" or "mediumint" or "int" or "integer" or "bigint")
        {
            return normalizedColumnType.Contains("unsigned", StringComparison.Ordinal) ||
                   normalizedColumnType.Contains("zerofill", StringComparison.Ordinal)
                ? TableColumnValueKind.UnsignedInteger
                : TableColumnValueKind.Integer;
        }

        return normalizedDataType switch
        {
            "year" => TableColumnValueKind.MySqlYear,
            "decimal" or "numeric" => TableColumnValueKind.ExactDecimal,
            "float" when IsDoublePrecisionFloat(columnType) =>
                TableColumnValueKind.DoublePrecisionFloatingPoint,
            "float" => TableColumnValueKind.SinglePrecisionFloatingPoint,
            "double" or "real" => TableColumnValueKind.DoublePrecisionFloatingPoint,
            "bool" or "boolean" => TableColumnValueKind.Boolean,
            "bit" => TableColumnValueKind.UnsignedInteger,
            "date" or "datetime" or "timestamp" => TableColumnValueKind.MySqlTemporal,
            "time" => TableColumnValueKind.MySqlTime,
            "json" => TableColumnValueKind.Json,
            "uuid" => TableColumnValueKind.Guid,
            "inet4" or "inet6" => TableColumnValueKind.NetworkAddress,
            "binary" or "varbinary" or "tinyblob" or "blob" or "mediumblob" or "longblob" =>
                TableColumnValueKind.Binary,
            "geometry" or "point" or "linestring" or "polygon" or "multipoint" or "multilinestring" or
                "multipolygon" or "geomcollection" or "geometrycollection" => TableColumnValueKind.Spatial,
            "char" or "varchar" or "tinytext" or "text" or "mediumtext" or "longtext" or "enum" or "set" =>
                TableColumnValueKind.String,
            _ => TableColumnValueKind.Unsupported
        };
    }

    private static bool IsDoublePrecisionFloat(string columnType)
    {
        var normalized = columnType.Trim().ToLowerInvariant();
        var definitionStart = normalized.IndexOf('(');
        var definitionEnd = normalized.IndexOf(')', definitionStart + 1);
        if (definitionStart < 0 || definitionEnd < 0)
        {
            return false;
        }

        var arguments = normalized[(definitionStart + 1)..definitionEnd].Split(',');
        return arguments.Length == 1 &&
               int.TryParse(arguments[0].Trim(), out var precision) &&
               precision >= 24;
    }

    private static int? GetRequiredBinaryLength(string dataType, string columnType)
    {
        if (!dataType.Equals("binary", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        const string prefix = "binary(";
        var normalized = columnType.Trim();
        if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !normalized.EndsWith(')') ||
            !int.TryParse(
                normalized.AsSpan(prefix.Length, normalized.Length - prefix.Length - 1),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var length) ||
            length < 0)
        {
            throw new InvalidOperationException($"無法解析固定長度 binary metadata「{columnType}」。");
        }

        return length;
    }

    private static (long Minimum, ulong Maximum)? GetIntegerBounds(
        string dataType,
        string columnType,
        TableColumnValueKind valueKind)
    {
        if (valueKind is not (TableColumnValueKind.Integer or TableColumnValueKind.UnsignedInteger))
        {
            return null;
        }

        var isUnsigned = columnType.Contains("unsigned", StringComparison.OrdinalIgnoreCase) ||
                         columnType.Contains("zerofill", StringComparison.OrdinalIgnoreCase);
        return dataType.ToLowerInvariant() switch
        {
            "tinyint" => isUnsigned ? (0, byte.MaxValue) : (sbyte.MinValue, (ulong)sbyte.MaxValue),
            "smallint" => isUnsigned ? (0, ushort.MaxValue) : (short.MinValue, (ulong)short.MaxValue),
            "mediumint" => isUnsigned ? (0, 16_777_215UL) : (-8_388_608, 8_388_607UL),
            "int" or "integer" => isUnsigned ? (0, uint.MaxValue) : (int.MinValue, (ulong)int.MaxValue),
            "bigint" => isUnsigned ? (0, ulong.MaxValue) : (long.MinValue, (ulong)long.MaxValue),
            _ => null
        };
    }
}
