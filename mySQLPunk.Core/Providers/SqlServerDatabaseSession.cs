using System.Data;
using Microsoft.Data.SqlClient;
using MySqlPunk.Core.Models;
using MySqlPunk.Core.Services;

namespace MySqlPunk.Core.Providers;

internal sealed class SqlServerDatabaseSession : AdoDatabaseSession
{
    public SqlServerDatabaseSession(ConnectionProfile profile)
        : base(profile)
    {
    }

    protected override SqlConnection CreateConnection(string? database)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = $"{Profile.Host},{Profile.Port}",
            InitialCatalog = string.IsNullOrWhiteSpace(database) ? "master" : database,
            UserID = Profile.Username,
            Password = Profile.Password,
            IntegratedSecurity = false,
            ConnectTimeout = Profile.TimeoutSeconds,
            CommandTimeout = Math.Max(1, Profile.TimeoutSeconds * 4),
            Encrypt = Profile.UseSsl,
            TrustServerCertificate = false,
            ApplicationName = "mySQLPunk"
        };

        return new SqlConnection(builder.ConnectionString);
    }

    protected override string QuoteIdentifier(string value) => $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";

    protected override void ConfigureParameter(
        System.Data.Common.DbParameter parameter,
        TableColumnInfo column)
    {
        base.ConfigureParameter(parameter, column);
        if (column.ValueKind == TableColumnValueKind.Xml && parameter is SqlParameter sqlParameter)
        {
            sqlParameter.SqlDbType = System.Data.SqlDbType.Xml;
        }
        else if (column.ValueKind == TableColumnValueKind.ExactDecimal && parameter is SqlParameter decimalParameter)
        {
            decimalParameter.SqlDbType = System.Data.SqlDbType.VarChar;
            decimalParameter.Size = -1;
        }
        else if (column.ValueKind == TableColumnValueKind.SqlServerMoney &&
                 parameter is SqlParameter moneyParameter)
        {
            moneyParameter.SqlDbType = GetBaseTypeName(column.StorageDataTypeName) switch
            {
                "money" => SqlDbType.Money,
                "smallmoney" => SqlDbType.SmallMoney,
                _ => throw new InvalidOperationException(
                    $"無法建立 SQL Server money 型別「{column.StorageDataTypeName}」的參數。")
            };
        }
        else if (column.ValueKind is TableColumnValueKind.SinglePrecisionFloatingPoint or
                     TableColumnValueKind.DoublePrecisionFloatingPoint &&
                 parameter is SqlParameter floatingPointParameter)
        {
            floatingPointParameter.SqlDbType =
                column.ValueKind == TableColumnValueKind.SinglePrecisionFloatingPoint
                    ? SqlDbType.Real
                    : SqlDbType.Float;
        }
        else if (column.ValueKind == TableColumnValueKind.Spatial && parameter is SqlParameter spatialParameter)
        {
            spatialParameter.SqlDbType = System.Data.SqlDbType.VarChar;
            spatialParameter.Size = -1;
        }
        else if (column.ValueKind == TableColumnValueKind.SqlServerHierarchyId &&
                 parameter is SqlParameter hierarchyIdParameter)
        {
            hierarchyIdParameter.SqlDbType = System.Data.SqlDbType.NVarChar;
            hierarchyIdParameter.Size = -1;
        }
        else if (column.ValueKind == TableColumnValueKind.SqlServerVariant &&
                 parameter is SqlParameter variantParameter)
        {
            variantParameter.SqlDbType = SqlDbType.Variant;
        }
        else if (column.ValueKind == TableColumnValueKind.SqlServerTemporal &&
                 parameter is SqlParameter temporalParameter)
        {
            var baseType = GetBaseTypeName(column.StorageDataTypeName);
            temporalParameter.SqlDbType = baseType switch
            {
                "date" => SqlDbType.Date,
                "datetime" => SqlDbType.DateTime,
                "smalldatetime" => SqlDbType.SmallDateTime,
                "datetime2" => SqlDbType.DateTime2,
                "datetimeoffset" => SqlDbType.DateTimeOffset,
                "time" => SqlDbType.Time,
                _ => throw new InvalidOperationException(
                    $"無法建立 SQL Server temporal 型別「{column.StorageDataTypeName}」的參數。")
            };
            if (baseType is "datetime2" or "datetimeoffset" or "time")
            {
                temporalParameter.Scale = TableCellValueConverter.GetSqlServerTemporalScale(column);
            }
        }
        else if (column.ValueKind == TableColumnValueKind.String &&
                 parameter is SqlParameter stringParameter)
        {
            // Keep the user's original Unicode text intact until the SQL expression can
            // validate it against the destination column's collation and byte limit.
            stringParameter.SqlDbType = SqlDbType.NVarChar;
            stringParameter.Size = -1;
        }
        else if (parameter is SqlParameter legacyParameter)
        {
            legacyParameter.SqlDbType = GetBaseTypeName(column.StorageDataTypeName) switch
            {
                "image" => System.Data.SqlDbType.Image,
                _ => legacyParameter.SqlDbType
            };
        }
    }

    protected override object? PrepareParameterValue(TableColumnInfo column, object? value)
    {
        if (column.ValueKind == TableColumnValueKind.SqlServerMoney && value is string moneyText)
        {
            value = TableCellValueConverter.Parse(
                column,
                new TableCellInput(column.Name, TableCellInputMode.Value, moneyText));
        }

        if (column.ValueKind == TableColumnValueKind.SqlServerVariant && value is string text)
        {
            return TableCellValueConverter.Parse(
                column,
                new TableCellInput(column.Name, TableCellInputMode.Value, text));
        }

        return base.PrepareParameterValue(column, value);
    }

    protected override object? PrepareOriginalParameterValue(TableColumnInfo column, object? value)
    {
        if (column.ValueKind == TableColumnValueKind.SqlServerVariant && value is string text)
        {
            return new SqlServerVariantOriginalText(text);
        }

        return base.PrepareOriginalParameterValue(column, value);
    }

    protected override void ConfigurePreparedParameter(
        System.Data.Common.DbParameter parameter,
        TableColumnInfo column)
    {
        base.ConfigurePreparedParameter(parameter, column);
        if (column.ValueKind != TableColumnValueKind.SqlServerVariant ||
            parameter is not SqlParameter sqlParameter)
        {
            return;
        }

        if (parameter.Value is SqlServerVariantOriginalText original)
        {
            sqlParameter.SqlDbType = SqlDbType.NVarChar;
            sqlParameter.Size = -1;
            sqlParameter.Value = original.Text;
            return;
        }
        if (parameter.Value is not SqlServerVariantValue variant)
        {
            return;
        }

        if (variant is { BaseTypeName: "datetimeoffset", Value: DateTimeOffset dateTimeOffset })
        {
            sqlParameter.SqlDbType = SqlDbType.NVarChar;
            sqlParameter.Size = 48;
            sqlParameter.Value = dateTimeOffset.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz",
                System.Globalization.CultureInfo.InvariantCulture);
            return;
        }
        if (variant is
            {
                BaseTypeName: "char" or "varchar" or "nchar" or "nvarchar",
                Value: string stringValue
            })
        {
            sqlParameter.SqlDbType = SqlDbType.NVarChar;
            sqlParameter.Size = -1;
            sqlParameter.Value = stringValue;
            return;
        }

        sqlParameter.SqlDbType = variant.BaseTypeName switch
        {
            "tinyint" => SqlDbType.TinyInt,
            "smallint" => SqlDbType.SmallInt,
            "int" => SqlDbType.Int,
            "bigint" => SqlDbType.BigInt,
            "bit" => SqlDbType.Bit,
            "decimal" => SqlDbType.Decimal,
            "numeric" => SqlDbType.Decimal,
            "money" => SqlDbType.Money,
            "smallmoney" => SqlDbType.SmallMoney,
            "float" => SqlDbType.Float,
            "real" => SqlDbType.Real,
            "date" => SqlDbType.Date,
            "datetime" => SqlDbType.DateTime,
            "smalldatetime" => SqlDbType.SmallDateTime,
            "datetime2" => SqlDbType.DateTime2,
            "datetimeoffset" => SqlDbType.DateTimeOffset,
            "time" => SqlDbType.Time,
            "uniqueidentifier" => SqlDbType.UniqueIdentifier,
            "char" => SqlDbType.Char,
            "varchar" => SqlDbType.VarChar,
            "nchar" => SqlDbType.NChar,
            "nvarchar" => SqlDbType.NVarChar,
            "binary" => SqlDbType.Binary,
            "varbinary" => SqlDbType.VarBinary,
            _ => throw new InvalidOperationException(
                $"無法建立 sql_variant 內層型別「{variant.BaseTypeName}」的 SQL Server 參數。")
        };
        if (variant.Size is { } size)
        {
            sqlParameter.Size = size;
        }
        if (variant.Precision is { } precision)
        {
            sqlParameter.Precision = precision;
        }
        if (variant.Scale is { } scale)
        {
            sqlParameter.Scale = scale;
        }
        sqlParameter.Value = variant.Value;
    }

    protected override string BuildOriginalValuePredicate(TableColumnInfo column, string parameterName)
    {
        if (column.ValueKind == TableColumnValueKind.String)
        {
            var stringDataType = GetBaseTypeName(column.StorageDataTypeName);
            var collationClause = BuildStringCollationClause(column);
            var unicodeInput = $"CONVERT(nvarchar(max), {parameterName}){collationClause}";
            var originalInput = stringDataType is "char" or "varchar" or "text"
                ? $"CONVERT(varchar(max), {unicodeInput}){collationClause}"
                : unicodeInput;
            var storedValue = stringDataType is "char" or "varchar" or "text"
                ? $"CONVERT(varchar(max), {QuoteIdentifier(column.Name)})"
                : $"CONVERT(nvarchar(max), {QuoteIdentifier(column.Name)})";
            return $"CONVERT(varbinary(max), {storedValue}) = " +
                   $"CONVERT(varbinary(max), {originalInput})";
        }

        if (column.ValueKind == TableColumnValueKind.Xml)
        {
            return $"CONVERT(varbinary(max), CONVERT(nvarchar(max), {QuoteIdentifier(column.Name)})) = " +
                   $"CONVERT(varbinary(max), CONVERT(nvarchar(max), {parameterName}))";
        }

        if (column.ValueKind == TableColumnValueKind.ExactDecimal)
        {
            return $"{QuoteIdentifier(column.Name)} = {BuildParameterValueExpression(column, parameterName)}";
        }

        if (column.ValueKind == TableColumnValueKind.Spatial)
        {
            var quotedName = QuoteIdentifier(column.Name);
            return $"{quotedName}.STSrid = {BuildSpatialSridExpression(parameterName)} AND " +
                   $"{quotedName}.STAsText() = CONVERT(nvarchar(max), {BuildSpatialWktExpression(parameterName)})";
        }

        if (column.ValueKind == TableColumnValueKind.SqlServerHierarchyId)
        {
            return $"{QuoteIdentifier(column.Name)} = {BuildParameterValueExpression(column, parameterName)}";
        }

        var dataType = GetBaseTypeName(column.StorageDataTypeName);
        if (dataType == "image")
        {
            return $"CONVERT(varbinary(max), {QuoteIdentifier(column.Name)}) = " +
                   $"CONVERT(varbinary(max), {parameterName})";
        }

        return base.BuildOriginalValuePredicate(column, parameterName);
    }

    protected override string BuildOriginalValuePredicate(
        TableColumnInfo column,
        string parameterName,
        object originalValue)
    {
        if (column.ValueKind == TableColumnValueKind.SqlServerVariant)
        {
            return $"CONVERT(varbinary(max), {BuildSqlVariantTextExpression(QuoteIdentifier(column.Name))}) = " +
                   $"CONVERT(varbinary(max), {parameterName})";
        }

        return base.BuildOriginalValuePredicate(column, parameterName, originalValue);
    }

    protected override string BuildTableDataSelectExpression(TableColumnInfo column)
    {
        var quotedName = QuoteIdentifier(column.Name);
        return column.ValueKind switch
        {
            TableColumnValueKind.ExactDecimal => $"CONVERT(varchar(max), {quotedName}) AS {quotedName}",
            TableColumnValueKind.SqlServerMoney => BuildSqlServerMoneySelectExpression(column, quotedName),
            TableColumnValueKind.Spatial =>
                $"CASE WHEN {quotedName} IS NULL THEN NULL ELSE " +
                $"CONCAT('SRID=', {quotedName}.STSrid, ';', {quotedName}.STAsText()) END AS {quotedName}",
            TableColumnValueKind.SqlServerHierarchyId =>
                $"CASE WHEN {quotedName} IS NULL THEN NULL ELSE " +
                $"CONVERT(nvarchar(max), {quotedName}.ToString()) END AS {quotedName}",
            TableColumnValueKind.SqlServerVariant =>
                $"{BuildSqlVariantTextExpression(quotedName)} AS {quotedName}",
            _ => base.BuildTableDataSelectExpression(column)
        };
    }

    private static string BuildSqlServerMoneySelectExpression(TableColumnInfo column, string quotedName)
    {
        var precision = GetBaseTypeName(column.StorageDataTypeName) switch
        {
            "money" => 19,
            "smallmoney" => 10,
            _ => throw new InvalidOperationException(
                $"無法載入 SQL Server money 型別「{column.StorageDataTypeName}」。")
        };
        return $"CONVERT(varchar(max), CONVERT(decimal({precision},4), {quotedName})) AS {quotedName}";
    }

    private static string BuildSqlVariantTextExpression(string quotedName)
    {
        var baseType = $"CONVERT(varchar(30), SQL_VARIANT_PROPERTY({quotedName}, 'BaseType'))";
        var precision = $"CONVERT(varchar(3), SQL_VARIANT_PROPERTY({quotedName}, 'Precision'))";
        var scale = $"CONVERT(varchar(3), SQL_VARIANT_PROPERTY({quotedName}, 'Scale'))";
        var maximumLength = $"CONVERT(int, SQL_VARIANT_PROPERTY({quotedName}, 'MaxLength'))";
        var collation = $"CONVERT(nvarchar(128), SQL_VARIANT_PROPERTY({quotedName}, 'Collation'))";
        var localeId = $"CONVERT(varchar(10), COLLATIONPROPERTY({collation}, 'LCID'))";
        var comparisonStyle =
            $"CONVERT(varchar(10), COLLATIONPROPERTY({collation}, 'ComparisonStyle'))";
        var typeDefinition =
            $"CASE {baseType} " +
            $"WHEN 'decimal' THEN CONCAT('decimal(', {precision}, ',', {scale}, ')') " +
            $"WHEN 'numeric' THEN CONCAT('numeric(', {precision}, ',', {scale}, ')') " +
            $"WHEN 'char' THEN CONCAT('char(', {maximumLength}, ')@', {collation}, '|', {localeId}, '|', {comparisonStyle}) " +
            $"WHEN 'varchar' THEN CONCAT('varchar(', {maximumLength}, ')@', {collation}, '|', {localeId}, '|', {comparisonStyle}) " +
            $"WHEN 'nchar' THEN CONCAT('nchar(', {maximumLength} / 2, ')@', {collation}, '|', {localeId}, '|', {comparisonStyle}) " +
            $"WHEN 'nvarchar' THEN CONCAT('nvarchar(', {maximumLength} / 2, ')@', {collation}, '|', {localeId}, '|', {comparisonStyle}) " +
            $"WHEN 'binary' THEN CONCAT('binary(', {maximumLength}, ')') " +
            $"WHEN 'varbinary' THEN CONCAT('varbinary(', {maximumLength}, ')') " +
            $"WHEN 'datetime2' THEN CONCAT('datetime2(', {scale}, ')') " +
            $"WHEN 'datetimeoffset' THEN CONCAT('datetimeoffset(', {scale}, ')') " +
            $"WHEN 'time' THEN CONCAT('time(', {scale}, ')') " +
            $"ELSE {baseType} END";
        var valueText =
            $"CASE {baseType} " +
            $"WHEN 'binary' THEN CONCAT('0x', CONVERT(varchar(max), CONVERT(varbinary(8000), {quotedName}), 2)) " +
            $"WHEN 'varbinary' THEN CONCAT('0x', CONVERT(varchar(max), CONVERT(varbinary(8000), {quotedName}), 2)) " +
            $"WHEN 'date' THEN CONVERT(varchar(10), CONVERT(date, {quotedName}), 23) " +
            $"WHEN 'datetime' THEN CONVERT(varchar(33), CONVERT(datetime, {quotedName}), 126) " +
            $"WHEN 'smalldatetime' THEN CONVERT(varchar(33), CONVERT(smalldatetime, {quotedName}), 126) " +
            $"WHEN 'datetime2' THEN CONVERT(varchar(33), CONVERT(datetime2(7), {quotedName}), 126) " +
            $"WHEN 'datetimeoffset' THEN CONVERT(varchar(40), CONVERT(datetimeoffset(7), {quotedName}), 126) " +
            $"WHEN 'time' THEN CONVERT(varchar(30), CONVERT(time(7), {quotedName}), 126) " +
            $"WHEN 'float' THEN CONVERT(varchar(99), CONVERT(float, {quotedName}), 3) " +
            $"WHEN 'real' THEN CONVERT(varchar(99), CONVERT(real, {quotedName}), 3) " +
            $"ELSE CONVERT(nvarchar(max), {quotedName}) END";
        return $"CASE WHEN {quotedName} IS NULL THEN NULL ELSE CONCAT({typeDefinition}, ':', {valueText}) END";
    }

    protected override string BuildParameterValueExpression(TableColumnInfo column, string parameterName)
    {
        if (column.ValueKind == TableColumnValueKind.Spatial)
        {
            var spatialTypeName = column.DataTypeName.Equals("geography", StringComparison.OrdinalIgnoreCase)
                ? "geography"
                : "geometry";
            return $"{spatialTypeName}::STGeomFromText(" +
                   $"CONVERT(nvarchar(max), {BuildSpatialWktExpression(parameterName)}), " +
                   $"{BuildSpatialSridExpression(parameterName)})";
        }

        if (column.ValueKind == TableColumnValueKind.SqlServerHierarchyId)
        {
            return $"hierarchyid::Parse(CONVERT(nvarchar(max), {parameterName}))";
        }

        if (column.ValueKind != TableColumnValueKind.ExactDecimal)
        {
            return base.BuildParameterValueExpression(column, parameterName);
        }

        var definition = TableCellValueConverter.GetExactDecimalDefinition(column);
        var typeName = definition is { Precision: { } precision, Scale: { } scale }
            ? $"decimal({precision},{scale})"
            : "decimal";
        return $"CAST({parameterName} AS {typeName})";
    }

    protected override string BuildParameterValueExpression(
        TableColumnInfo column,
        string parameterName,
        object? value)
    {
        if (column.ValueKind == TableColumnValueKind.String && value is string)
        {
            return BuildStringValueExpression(column, parameterName);
        }

        if (column.ValueKind == TableColumnValueKind.SqlServerVariant &&
            value is SqlServerVariantValue { BaseTypeName: "numeric", Precision: { } precision, Scale: { } scale })
        {
            return $"CAST({parameterName} AS numeric({precision},{scale}))";
        }
        if (column.ValueKind == TableColumnValueKind.SqlServerVariant &&
            value is SqlServerVariantValue { BaseTypeName: "datetimeoffset", Scale: { } offsetScale })
        {
            return $"CAST({parameterName} AS datetimeoffset({offsetScale}))";
        }
        if (column.ValueKind == TableColumnValueKind.SqlServerVariant &&
            value is SqlServerVariantValue { Size: not null } stringVariant &&
            stringVariant.BaseTypeName is "char" or "varchar" or "nchar" or "nvarchar")
        {
            return BuildSqlVariantStringExpression(parameterName, stringVariant);
        }

        return base.BuildParameterValueExpression(column, parameterName, value);
    }

    private static string BuildSqlVariantStringExpression(
        string parameterName,
        SqlServerVariantValue variant)
    {
        var size = variant.Size!.Value;
        var collationClause = variant.CollationName is { } collationName
            ? $" COLLATE {ValidateCollationName(collationName)}"
            : string.Empty;
        var unicodeInput = $"CONVERT(nvarchar(max), {parameterName}){collationClause}";
        string fitsWithoutLoss;
        string typedValue;
        if (variant.BaseTypeName is "char" or "varchar")
        {
            var ansiInput = $"CONVERT(varchar(max), {unicodeInput}){collationClause}";
            fitsWithoutLoss =
                $"DATALENGTH({ansiInput}) <= {size} AND " +
                $"CONVERT(varbinary(max), CONVERT(nvarchar(max), {ansiInput})) = " +
                $"CONVERT(varbinary(max), {unicodeInput})";
            typedValue =
                $"CAST({ansiInput} AS {variant.BaseTypeName}({size})){collationClause}";
        }
        else
        {
            fitsWithoutLoss = $"DATALENGTH({unicodeInput}) <= {size * 2}";
            typedValue =
                $"CAST({unicodeInput} AS {variant.BaseTypeName}({size})){collationClause}";
        }

        var rejectedValue =
            $"CONVERT(sql_variant, CONVERT(int, CONCAT('sql_variant-invalid-', DATALENGTH({parameterName}))))";
        return $"CASE WHEN {fitsWithoutLoss} THEN CONVERT(sql_variant, {typedValue}) " +
               $"ELSE {rejectedValue} END";
    }

    private static string BuildStringValueExpression(TableColumnInfo column, string parameterName)
    {
        var baseType = GetBaseTypeName(column.StorageDataTypeName);
        if (baseType is not ("char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext"))
        {
            throw new InvalidOperationException(
                $"無法建立 SQL Server 字串型別「{column.StorageDataTypeName}」的無損參數。");
        }

        var collationClause = BuildStringCollationClause(column);
        var unicodeInput = $"CONVERT(nvarchar(max), {parameterName}){collationClause}";

        if (baseType is "text" or "char" or "varchar")
        {
            var ansiInput = $"CONVERT(varchar(max), {unicodeInput}){collationClause}";
            var fitsWithoutLoss =
                $"CONVERT(varbinary(max), CONVERT(nvarchar(max), {ansiInput})) = " +
                $"CONVERT(varbinary(max), {unicodeInput})";
            if (baseType is "char" or "varchar")
            {
                var maximumBytes = GetRequiredStringMaximumBytes(column);
                if (maximumBytes >= 0)
                {
                    fitsWithoutLoss = $"DATALENGTH({ansiInput}) <= {maximumBytes} AND {fitsWithoutLoss}";
                }
            }

            var typedValue = baseType == "text"
                ? ansiInput
                : $"CAST({ansiInput} AS {baseType}({FormatStringSize(column)})){collationClause}";
            return BuildStringValidationCase(parameterName, fitsWithoutLoss, typedValue, unicode: false, collationClause);
        }

        if (baseType == "ntext")
        {
            return unicodeInput;
        }

        var maximumUnicodeBytes = GetRequiredStringMaximumBytes(column);
        var unicodeFits = maximumUnicodeBytes < 0
            ? "1 = 1"
            : $"DATALENGTH({unicodeInput}) <= {maximumUnicodeBytes}";
        var unicodeTypedValue =
            $"CAST({unicodeInput} AS {baseType}({FormatStringSize(column)})){collationClause}";
        return BuildStringValidationCase(
            parameterName,
            unicodeFits,
            unicodeTypedValue,
            unicode: true,
            collationClause);
    }

    private static string BuildStringValidationCase(
        string parameterName,
        string fitsWithoutLoss,
        string typedValue,
        bool unicode,
        string collationClause)
    {
        var resultType = unicode ? "nvarchar(max)" : "varchar(max)";
        var rejectedValue =
            $"CONVERT({resultType}, CONVERT(int, CONCAT('mysqlpunk-string-invalid-', DATALENGTH({parameterName}))))" +
            collationClause;
        return $"CASE WHEN {fitsWithoutLoss} THEN {typedValue} ELSE {rejectedValue} END";
    }

    private static int GetRequiredStringMaximumBytes(TableColumnInfo column)
    {
        if (column.MaximumStringLengthInBytes is not { } maximumBytes || maximumBytes is 0 or < -1)
        {
            throw new InvalidOperationException(
                $"SQL Server 字串欄位「{column.Name}」缺少有效的長度 metadata，已停止寫入以避免資料失真。");
        }

        return maximumBytes;
    }

    private static string BuildStringCollationClause(TableColumnInfo column)
    {
        var collationName = column.StorageCollationName is { } value
            ? ValidateCollationName(value)
            : throw new InvalidOperationException(
                $"SQL Server 字串欄位「{column.Name}」缺少 collation metadata，已停止寫入以避免資料失真。");
        return $" COLLATE {collationName}";
    }

    private static string FormatStringSize(TableColumnInfo column)
    {
        var maximumBytes = GetRequiredStringMaximumBytes(column);
        if (maximumBytes < 0)
        {
            return "max";
        }

        var baseType = GetBaseTypeName(column.StorageDataTypeName);
        var size = baseType is "nchar" or "nvarchar" ? maximumBytes / 2 : maximumBytes;
        return size.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ValidateCollationName(string value)
    {
        if (value.Length is < 1 or > 128 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
        {
            throw new InvalidOperationException("SQL Server collation 名稱只允許英數字與底線。");
        }

        return value;
    }

    private static string BuildSpatialSridExpression(string parameterName) =>
        $"CONVERT(int, SUBSTRING({parameterName}, 6, CHARINDEX(';', {parameterName}) - 6))";

    private static string BuildSpatialWktExpression(string parameterName) =>
        $"SUBSTRING({parameterName}, CHARINDEX(';', {parameterName}) + 1, LEN({parameterName}))";

    public override async Task<IReadOnlyList<string>> GetDatabasesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection("master");
        return await ReadStringsAsync(
            connection,
            "SELECT name FROM sys.databases WHERE state = 0 AND HAS_DBACCESS(name) = 1 ORDER BY name",
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
            SELECT schema_name(schema_id), name, type
            FROM sys.objects
            WHERE type IN ('U', 'V')
              AND is_ms_shipped = 0
            ORDER BY schema_name(schema_id), type, name
            """;
        command.CommandTimeout = Math.Max(1, Profile.TimeoutSeconds * 2);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var objects = new List<DatabaseObjectInfo>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var kind = string.Equals(reader.GetString(2), "V", StringComparison.OrdinalIgnoreCase)
                ? DatabaseObjectKind.View
                : DatabaseObjectKind.Table;
            objects.Add(new DatabaseObjectInfo(reader.GetString(0), reader.GetString(1), kind));
        }

        return objects;
    }

    public override string BuildSelectPreview(DatabaseObjectInfo databaseObject, int rowLimit = 200)
    {
        rowLimit = Math.Clamp(rowLimit, 1, 10_000);
        return $"SELECT TOP ({rowLimit}) * FROM {BuildQualifiedName(databaseObject)};";
    }

    protected override async Task<IReadOnlyList<TableColumnInfo>> GetTableColumnsAsync(
        string database,
        DatabaseObjectInfo table,
        CancellationToken cancellationToken)
    {
        var schema = string.IsNullOrWhiteSpace(table.Schema) ? "dbo" : table.Schema;
        await using var connection = CreateConnection(database);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.name,
                   ty.name,
                   schema_name(ty.schema_id),
                   ty.is_user_defined,
                   base_ty.name,
                   c.is_nullable,
                   CASE WHEN EXISTS (
                       SELECT 1
                       FROM sys.indexes i
                       JOIN sys.index_columns ic
                         ON ic.object_id = i.object_id
                        AND ic.index_id = i.index_id
                       WHERE i.object_id = c.object_id
                         AND i.is_primary_key = 1
                         AND ic.column_id = c.column_id
                   ) THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS is_primary_key,
                   c.is_identity,
                   c.is_computed,
                   c.default_object_id,
                   c.max_length,
                   c.precision,
                   c.scale,
                   c.collation_name
            FROM sys.columns c
            JOIN sys.tables t ON t.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            JOIN sys.types base_ty ON base_ty.user_type_id = c.system_type_id
            WHERE s.name = @schema
              AND t.name = @table
            ORDER BY c.column_id
            """;
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table.Name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var columns = new List<TableColumnInfo>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var declaredType = reader.GetString(1);
            var declaredTypeSchema = reader.GetString(2);
            var isUserDefined = reader.GetBoolean(3);
            var baseType = reader.GetString(4);
            var storageType = BuildStorageTypeName(
                baseType,
                reader.GetInt16(10),
                reader.GetByte(11),
                reader.GetByte(12));
            var declaredTypeDisplayName = isUserDefined
                ? $"{QuoteTypeNamePart(declaredTypeSchema)}.{QuoteTypeNamePart(declaredType)}"
                : declaredType;
            var displayType = declaredType.Equals(baseType, StringComparison.OrdinalIgnoreCase)
                ? storageType
                : $"{declaredTypeDisplayName} ({storageType})";
            var generated = reader.GetBoolean(7) || reader.GetBoolean(8) ||
                            baseType.Equals("timestamp", StringComparison.OrdinalIgnoreCase) ||
                            baseType.Equals("rowversion", StringComparison.OrdinalIgnoreCase);
            var valueKind = MapValueKind(baseType, reader.GetByte(11));
            var integerBounds = GetIntegerBounds(baseType, valueKind);
            int? requiredBinaryLength = baseType.Equals("binary", StringComparison.OrdinalIgnoreCase)
                ? reader.GetInt16(10)
                : null;
            int? maximumStringLengthInBytes = baseType.ToLowerInvariant() switch
            {
                "char" or "varchar" or "nchar" or "nvarchar" => reader.GetInt16(10),
                _ => null
            };
            columns.Add(new TableColumnInfo(
                columns.Count,
                reader.GetString(0),
                displayType,
                reader.GetBoolean(5),
                reader.GetBoolean(6),
                generated,
                reader.GetInt32(9) != 0,
                valueKind)
            {
                StorageDataTypeName = storageType,
                IntegerMinimum = integerBounds?.Minimum,
                IntegerMaximum = integerBounds?.Maximum,
                RequiredBinaryLength = requiredBinaryLength,
                MaximumStringLengthInBytes = maximumStringLengthInBytes,
                StorageCollationName = reader.IsDBNull(13) ? null : reader.GetString(13)
            });
        }

        return columns;
    }

    private static string BuildStorageTypeName(
        string baseType,
        short maxLength,
        byte precision,
        byte scale) =>
        baseType.ToLowerInvariant() switch
        {
            "decimal" or "numeric" => $"{baseType}({precision},{scale})",
            "float" => $"{baseType}({precision})",
            "char" or "varchar" or "binary" or "varbinary" =>
                $"{baseType}({(maxLength < 0 ? "max" : maxLength.ToString(System.Globalization.CultureInfo.InvariantCulture))})",
            "nchar" or "nvarchar" =>
                $"{baseType}({(maxLength < 0 ? "max" : (maxLength / 2).ToString(System.Globalization.CultureInfo.InvariantCulture))})",
            "time" or "datetime2" or "datetimeoffset" => $"{baseType}({scale})",
            _ => baseType
        };

    private static string QuoteTypeNamePart(string value) =>
        $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string GetBaseTypeName(string storageTypeName)
    {
        var normalized = storageTypeName.Trim();
        var definitionStart = normalized.IndexOf('(');
        return (definitionStart < 0 ? normalized : normalized[..definitionStart]).ToLowerInvariant();
    }

    protected override string BuildTableDataSql(
        DatabaseObjectInfo table,
        IReadOnlyList<TableColumnInfo> columns,
        int fetchLimit,
        int rowOffset,
        TableDataSort? sort,
        string? filterPredicate)
    {
        var selectColumns = string.Join(", ", columns.Select(BuildTableDataSelectExpression));
        var where = filterPredicate is null ? string.Empty : $" WHERE {filterPredicate}";
        var orderBy = BuildTableDataOrderBy(columns, sort);
        if (rowOffset == 0)
        {
            return $"SELECT TOP ({fetchLimit}) {selectColumns} FROM {BuildQualifiedName(table)}{where}{orderBy};";
        }

        return $"SELECT {selectColumns} FROM {BuildQualifiedName(table)}{where}{orderBy} " +
               $"OFFSET {rowOffset} ROWS FETCH NEXT {fetchLimit} ROWS ONLY;";
    }

    private static TableColumnValueKind MapValueKind(string dataType, byte precision) => dataType.ToLowerInvariant() switch
    {
        "tinyint" or "smallint" or "int" or "bigint" => TableColumnValueKind.Integer,
        "decimal" or "numeric" => TableColumnValueKind.ExactDecimal,
        "money" or "smallmoney" => TableColumnValueKind.SqlServerMoney,
        "real" => TableColumnValueKind.SinglePrecisionFloatingPoint,
        "float" when precision <= 24 => TableColumnValueKind.SinglePrecisionFloatingPoint,
        "float" => TableColumnValueKind.DoublePrecisionFloatingPoint,
        "bit" => TableColumnValueKind.Boolean,
        "date" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" or "time" =>
            TableColumnValueKind.SqlServerTemporal,
        "uniqueidentifier" => TableColumnValueKind.Guid,
        "xml" => TableColumnValueKind.Xml,
        "hierarchyid" => TableColumnValueKind.SqlServerHierarchyId,
        "sql_variant" => TableColumnValueKind.SqlServerVariant,
        "geometry" or "geography" => TableColumnValueKind.Spatial,
        "binary" or "varbinary" or "image" => TableColumnValueKind.Binary,
        "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" => TableColumnValueKind.String,
        _ => TableColumnValueKind.Unsupported
    };

    private static (long Minimum, ulong Maximum)? GetIntegerBounds(
        string dataType,
        TableColumnValueKind valueKind)
    {
        if (valueKind != TableColumnValueKind.Integer)
        {
            return null;
        }

        return dataType.ToLowerInvariant() switch
        {
            "tinyint" => (0, byte.MaxValue),
            "smallint" => (short.MinValue, (ulong)short.MaxValue),
            "int" => (int.MinValue, (ulong)int.MaxValue),
            "bigint" => (long.MinValue, (ulong)long.MaxValue),
            _ => null
        };
    }

    private sealed record SqlServerVariantOriginalText(string Text);
}
