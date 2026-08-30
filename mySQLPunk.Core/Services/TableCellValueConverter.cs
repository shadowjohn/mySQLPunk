using System.Globalization;
using System.Data.SqlTypes;
using System.Net;
using System.Text.Json;
using System.Xml;
using MySqlPunk.Core.Models;

namespace MySqlPunk.Core.Services;

public static class TableCellValueConverter
{
    public const int MaximumEditableBinaryBytes = 1024 * 1024;
    public const int MaximumEditableStructuredTextCharacters = 1024 * 1024;

    public static object? Parse(TableColumnInfo column, TableCellInput input)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(input);
        if (!string.Equals(column.Name, input.ColumnName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("資料欄位與輸入內容不一致。");
        }

        if (input.Mode == TableCellInputMode.Default)
        {
            throw new InvalidOperationException($"「{column.Name}」的預設值不應被轉成參數。");
        }

        if (input.Mode == TableCellInputMode.Null)
        {
            return column.IsNullable
                ? null
                : throw new InvalidOperationException($"「{column.Name}」不可設為 NULL。");
        }

        if (!column.IsEditable)
        {
            throw new InvalidOperationException($"「{column.Name}」的型別目前不支援直接編輯。");
        }

        try
        {
            return column.ValueKind switch
            {
                TableColumnValueKind.String => input.Text,
                TableColumnValueKind.Integer => long.Parse(input.Text, NumberStyles.Integer, CultureInfo.InvariantCulture),
                TableColumnValueKind.UnsignedInteger => ParseUnsignedInteger(column, input.Text),
                TableColumnValueKind.SqliteNumeric => ParseSqliteNumeric(column, input.Text),
                TableColumnValueKind.ExactDecimal => ParseExactDecimal(column, input.Text),
                TableColumnValueKind.PostgreSqlMoney => ParsePostgreSqlMoney(column, input.Text),
                TableColumnValueKind.SqlServerMoney => ParseSqlServerMoney(column, input.Text),
                TableColumnValueKind.FloatingPoint => ParseFiniteDouble(input.Text),
                TableColumnValueKind.Boolean => ParseBoolean(input.Text),
                TableColumnValueKind.Date => DateTime.ParseExact(
                    input.Text.Trim(),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None),
                TableColumnValueKind.PostgreSqlDate => ParsePostgreSqlDate(input.Text),
                TableColumnValueKind.DateTime => DateTime.Parse(
                    input.Text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind),
                TableColumnValueKind.DateTimeOffset => DateTimeOffset.Parse(
                    input.Text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind),
                TableColumnValueKind.Time => TimeSpan.Parse(input.Text, CultureInfo.InvariantCulture),
                TableColumnValueKind.MySqlTemporal => ParseMySqlTemporal(column, input.Text),
                TableColumnValueKind.MySqlTime => ParseMySqlTime(column, input.Text),
                TableColumnValueKind.MySqlYear => ParseMySqlYear(input.Text),
                TableColumnValueKind.PostgreSqlTemporal => ParsePostgreSqlTemporal(column, input.Text),
                TableColumnValueKind.TimeWithTimeZone => ParseTimeWithTimeZone(column, input.Text),
                TableColumnValueKind.Interval => ParseInterval(column, input.Text),
                TableColumnValueKind.LogSequenceNumber => ParseLogSequenceNumber(input.Text),
                TableColumnValueKind.FullTextVector => ParseServerValidatedText(
                    input.Text,
                    "PostgreSQL",
                    "全文檢索"),
                TableColumnValueKind.FullTextQuery => ParseServerValidatedText(
                    input.Text,
                    "PostgreSQL",
                    "全文檢索"),
                TableColumnValueKind.PostgreSqlRange => ParseServerValidatedText(
                    input.Text,
                    "PostgreSQL",
                    "range／multirange"),
                TableColumnValueKind.PostgreSqlArray => ParseServerValidatedText(
                    input.Text,
                    "PostgreSQL",
                    "array"),
                TableColumnValueKind.PostgreSqlGeometric => ParseServerValidatedText(
                    input.Text,
                    "PostgreSQL",
                    "geometric"),
                TableColumnValueKind.PostgreSqlServerValidatedText => ParseServerValidatedText(
                    input.Text,
                    "PostgreSQL",
                    column.DataTypeName),
                TableColumnValueKind.SqlServerTemporal => ParseSqlServerTemporal(column, input.Text),
                TableColumnValueKind.SqlServerHierarchyId => ParseServerValidatedText(
                    input.Text,
                    "SQL Server",
                    "hierarchyid"),
                TableColumnValueKind.SqlServerVariant => ParseSqlServerVariant(input.Text),
                TableColumnValueKind.Spatial => ParseSpatial(input.Text),
                TableColumnValueKind.Guid => System.Guid.Parse(input.Text),
                TableColumnValueKind.Json => ParseJson(input.Text),
                TableColumnValueKind.Xml => ParseXml(input.Text),
                TableColumnValueKind.NetworkAddress => ParseNetworkAddress(column, input.Text),
                TableColumnValueKind.BitString => ParseBitString(column, input.Text),
                TableColumnValueKind.Binary => ParseBinary(input.Text),
                _ => throw new InvalidOperationException($"「{column.Name}」的型別目前不支援直接編輯。")
            };
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or JsonException or XmlException)
        {
            throw new InvalidOperationException(
                $"「{column.Name}」的值不符合 {column.DataTypeName} 格式：{exception.Message}",
                exception);
        }
    }

    public static string Format(object? value) => value switch
    {
        null or DBNull => string.Empty,
        byte[] bytes => $"0x{Convert.ToHexString(bytes)}",
        DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString("O", CultureInfo.InvariantCulture),
        TimeSpan duration => duration.ToString("c", CultureInfo.InvariantCulture),
        SqliteNumericValue numeric => numeric.Text,
        ExactDecimalValue exactDecimal => exactDecimal.Text,
        PostgreSqlMoneyValue money => money.Text,
        SqlServerMoneyValue money => money.Text,
        SqlServerVariantValue variant => variant.CanonicalText,
        bool boolean => boolean ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    public static string Format(TableColumnInfo column, object? value)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (column.ValueKind == TableColumnValueKind.Date)
        {
            return value switch
            {
                DateTime dateTime => dateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                _ => Format(value)
            };
        }

        return Format(value);
    }

    public static string FormatForDisplay(object? value)
    {
        if (value is null or DBNull)
        {
            return "(NULL)";
        }

        if (value is byte[] bytes)
        {
            const int previewBytes = 16;
            var preview = Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, previewBytes)));
            return bytes.Length > previewBytes
                ? $"0x{preview}…（{bytes.Length:N0} bytes）"
                : $"0x{preview}";
        }

        if (value is string text && text.Length > MaximumEditableStructuredTextCharacters)
        {
            const int previewCharacters = 512;
            return $"{text[..previewCharacters]}…（{text.Length:N0} chars）";
        }

        return Format(value);
    }

    public static string FormatForDisplay(TableColumnInfo column, object? value)
    {
        ArgumentNullException.ThrowIfNull(column);
        return value is null or DBNull
            ? FormatForDisplay(value)
            : column.ValueKind == TableColumnValueKind.Date
                ? Format(column, value)
                : FormatForDisplay(value);
    }

    public static bool MatchesOriginal(TableColumnInfo column, TableCellInput input, object? original)
    {
        if (input.Mode == TableCellInputMode.Null)
        {
            return original is null or DBNull;
        }

        if (input.Mode != TableCellInputMode.Value || original is null or DBNull)
        {
            return false;
        }

        if (column.ValueKind == TableColumnValueKind.Binary && original is byte[] originalBytes)
        {
            return Parse(column, input) is byte[] parsedBytes &&
                   parsedBytes.AsSpan().SequenceEqual(originalBytes);
        }

        if (column.ValueKind == TableColumnValueKind.Date)
        {
            return string.Equals(input.Text.Trim(), Format(column, original), StringComparison.Ordinal);
        }

        if (column.ValueKind == TableColumnValueKind.PostgreSqlMoney)
        {
            return ParsePostgreSqlMoney(column, input.Text).Text == Format(original);
        }

        if (column.ValueKind == TableColumnValueKind.SqlServerMoney)
        {
            return ParseSqlServerMoney(column, input.Text).Text == Format(original);
        }

        if (column.ValueKind == TableColumnValueKind.SqliteNumeric)
        {
            return ParseSqliteNumeric(column, input.Text).Text ==
                   NormalizeSqliteNumericText(column, Format(original));
        }

        return string.Equals(input.Text, Format(original), StringComparison.Ordinal);
    }

    public static bool IsBinaryValueTooLargeToEdit(TableColumnInfo column, object? value) =>
        column.ValueKind == TableColumnValueKind.Binary &&
        value is byte[] bytes &&
        bytes.Length > MaximumEditableBinaryBytes;

    public static bool IsStructuredTextTooLargeToEdit(TableColumnInfo column, object? value) =>
        column.ValueKind is TableColumnValueKind.Json or
            TableColumnValueKind.Xml or
            TableColumnValueKind.BitString or
            TableColumnValueKind.FullTextVector or
            TableColumnValueKind.FullTextQuery or
            TableColumnValueKind.PostgreSqlRange or
            TableColumnValueKind.PostgreSqlArray or
            TableColumnValueKind.PostgreSqlGeometric or
            TableColumnValueKind.PostgreSqlServerValidatedText or
            TableColumnValueKind.SqlServerHierarchyId or
            TableColumnValueKind.SqlServerVariant or
            TableColumnValueKind.Spatial or
            TableColumnValueKind.ExactDecimal &&
        value is string text &&
        text.Length > MaximumEditableStructuredTextCharacters;

    private static SqlServerVariantValue ParseSqlServerVariant(string text)
    {
        if (text.Length > MaximumEditableStructuredTextCharacters || text.Contains('\0'))
        {
            throw new FormatException(
                $"SQL Server sql_variant 值不可包含 NUL，且不可超過 {MaximumEditableStructuredTextCharacters / 1024:N0} KiB 字元。");
        }

        var separator = text.IndexOf(':');
        if (separator <= 0)
        {
            throw new FormatException(
                "SQL Server sql_variant 必須使用 type:value 格式，例如 int:42 或 nvarchar(30):文字。");
        }

        var typeSpec = text[..separator].Trim();
        var valueText = text[(separator + 1)..];
        var metadataSeparator = typeSpec.IndexOf('@');
        var typeDefinition = metadataSeparator < 0 ? typeSpec : typeSpec[..metadataSeparator];
        var collationMetadata = metadataSeparator < 0 ? null : typeSpec[(metadataSeparator + 1)..];
        ParseSqlServerVariantTypeDefinition(typeDefinition, out var baseType, out var arguments);

        int? size = null;
        byte? precision = null;
        byte? scale = null;
        int? localeId = null;
        int? comparisonStyle = null;
        string? collationName = null;
        object value;
        string canonicalValue;

        switch (baseType)
        {
            case "tinyint":
                RequireNoVariantArguments(baseType, arguments, collationMetadata);
                value = byte.Parse(valueText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
                canonicalValue = ((byte)value).ToString(CultureInfo.InvariantCulture);
                break;
            case "smallint":
                RequireNoVariantArguments(baseType, arguments, collationMetadata);
                value = short.Parse(valueText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
                canonicalValue = ((short)value).ToString(CultureInfo.InvariantCulture);
                break;
            case "int":
                RequireNoVariantArguments(baseType, arguments, collationMetadata);
                value = int.Parse(valueText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
                canonicalValue = ((int)value).ToString(CultureInfo.InvariantCulture);
                break;
            case "bigint":
                RequireNoVariantArguments(baseType, arguments, collationMetadata);
                value = long.Parse(valueText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
                canonicalValue = ((long)value).ToString(CultureInfo.InvariantCulture);
                break;
            case "bit":
                RequireNoVariantArguments(baseType, arguments, collationMetadata);
                value = ParseBoolean(valueText);
                canonicalValue = (bool)value ? "true" : "false";
                break;
            case "decimal":
            case "numeric":
                {
                    RequireNoVariantCollation(baseType, collationMetadata);
                    var numericArguments = ParseVariantIntegerArguments(baseType, arguments, 2);
                    if (numericArguments[0] is < 1 or > 38 ||
                        numericArguments[1] < 0 ||
                        numericArguments[1] > numericArguments[0])
                    {
                        throw new FormatException($"{baseType} precision 必須介於 1–38，scale 必須介於 0–precision。");
                    }

                    precision = checked((byte)numericArguments[0]);
                    scale = checked((byte)numericArguments[1]);
                    var definition = $"{baseType}({precision},{scale})";
                    var numericColumn = new TableColumnInfo(
                        0,
                        "sql_variant",
                        definition,
                        true,
                        false,
                        false,
                        false,
                        TableColumnValueKind.ExactDecimal)
                    {
                        StorageDataTypeName = definition
                    };
                    var exact = ParseExactDecimal(numericColumn, valueText);
                    value = SqlDecimal.Parse(exact.Text);
                    canonicalValue = exact.Text;
                    break;
                }
            case "money":
            case "smallmoney":
                RequireNoVariantArguments(baseType, arguments, collationMetadata);
                var money = ParseSqlServerMoneyAmount(baseType, valueText);
                value = money;
                canonicalValue = money.ToString(CultureInfo.InvariantCulture);
                break;
            case "float":
                RequireNoVariantArguments(baseType, arguments, collationMetadata);
                value = ParseFiniteDouble(valueText);
                canonicalValue = ((double)value).ToString("R", CultureInfo.InvariantCulture);
                break;
            case "real":
                RequireNoVariantArguments(baseType, arguments, collationMetadata);
                var single = float.Parse(valueText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
                value = float.IsFinite(single) ? single : throw new FormatException("real 必須是有限值。");
                canonicalValue = single.ToString("R", CultureInfo.InvariantCulture);
                break;
            case "date":
                RequireNoVariantArguments(baseType, arguments, collationMetadata);
                value = DateTime.ParseExact(
                    valueText.Trim(),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None);
                canonicalValue = ((DateTime)value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                break;
            case "datetime":
                RequireNoVariantArguments(baseType, arguments, collationMetadata);
                var dateTime = ParseSqlServerVariantDateTime(valueText);
                SqlDateTime roundedDateTime;
                try
                {
                    roundedDateTime = new SqlDateTime(dateTime);
                }
                catch (SqlTypeException exception)
                {
                    throw new OverflowException("datetime 必須介於 1753-01-01 與 9999-12-31。", exception);
                }
                if (roundedDateTime.Value != dateTime)
                {
                    throw new FormatException("datetime 的小數秒無法由 SQL Server 無損保存。");
                }
                value = dateTime;
                canonicalValue = dateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
                break;
            case "smalldatetime":
                RequireNoVariantArguments(baseType, arguments, collationMetadata);
                var smallDateTime = ParseSqlServerVariantDateTime(valueText);
                if (smallDateTime < new DateTime(1900, 1, 1) ||
                    smallDateTime > new DateTime(2079, 6, 6, 23, 59, 0) ||
                    smallDateTime.Ticks % TimeSpan.TicksPerMinute != 0)
                {
                    throw new OverflowException(
                        "smalldatetime 必須介於 1900-01-01T00:00 與 2079-06-06T23:59，且只能使用整分鐘。");
                }
                value = smallDateTime;
                canonicalValue = smallDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
                break;
            case "datetime2":
                RequireNoVariantCollation(baseType, collationMetadata);
                scale = ParseVariantScale(baseType, arguments);
                var dateTime2 = ParseSqlServerVariantDateTime(valueText);
                EnsureSqlServerFractionalScale(dateTime2.Ticks, scale.Value, baseType);
                value = dateTime2;
                canonicalValue = dateTime2.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
                break;
            case "datetimeoffset":
                RequireNoVariantCollation(baseType, collationMetadata);
                scale = ParseVariantScale(baseType, arguments);
                var offsetText = valueText.Trim();
                if (!offsetText.EndsWith('Z') && !offsetText.EndsWith('z') &&
                    offsetText.LastIndexOfAny(['+', '-']) <= 10)
                {
                    throw new FormatException("datetimeoffset 必須明確包含 Z 或 ±HH:mm offset。");
                }
                var dateTimeOffset = DateTimeOffset.Parse(
                    offsetText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind);
                EnsureSqlServerFractionalScale(dateTimeOffset.Ticks, scale.Value, baseType);
                value = dateTimeOffset;
                canonicalValue = dateTimeOffset.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);
                break;
            case "time":
                RequireNoVariantCollation(baseType, collationMetadata);
                scale = ParseVariantScale(baseType, arguments);
                value = TimeSpan.Parse(valueText.Trim(), CultureInfo.InvariantCulture);
                if ((TimeSpan)value < TimeSpan.Zero || (TimeSpan)value >= TimeSpan.FromDays(1))
                {
                    throw new OverflowException("SQL Server time 必須介於 00:00:00 與 23:59:59.9999999。");
                }
                EnsureSqlServerFractionalScale(((TimeSpan)value).Ticks, scale.Value, baseType);
                canonicalValue = ((TimeSpan)value).ToString("c", CultureInfo.InvariantCulture);
                break;
            case "uniqueidentifier":
                RequireNoVariantArguments(baseType, arguments, collationMetadata);
                value = Guid.Parse(valueText.Trim());
                canonicalValue = ((Guid)value).ToString("D", CultureInfo.InvariantCulture);
                break;
            case "char":
            case "varchar":
            case "nchar":
            case "nvarchar":
                {
                    size = ParseVariantSize(baseType, arguments);
                    ParseVariantCollationMetadata(
                        collationMetadata,
                        out localeId,
                        out comparisonStyle,
                        out collationName);
                    if (valueText.Length > size)
                    {
                        throw new OverflowException($"{baseType}({size}) 不可保存超過 {size} 個字元的值。");
                    }
                    value = valueText;
                    canonicalValue = valueText;
                    break;
                }
            case "binary":
            case "varbinary":
                RequireNoVariantCollation(baseType, collationMetadata);
                size = ParseVariantSize(baseType, arguments);
                value = ParseBinary(valueText);
                if (((byte[])value).Length > size)
                {
                    throw new OverflowException($"{baseType}({size}) 不可保存超過 {size} bytes 的值。");
                }
                canonicalValue = $"0x{Convert.ToHexString((byte[])value)}";
                break;
            default:
                throw new FormatException($"sql_variant 不支援或無法辨識內層型別「{baseType}」。");
        }

        var canonicalType = arguments is null ? baseType : $"{baseType}({arguments})";
        if (collationName is not null)
        {
            canonicalType += $"@{collationName}|{localeId}|{comparisonStyle}";
        }

        return new SqlServerVariantValue(
            baseType,
            value,
            $"{canonicalType}:{canonicalValue}",
            size,
            precision,
            scale,
            localeId,
            comparisonStyle,
            collationName);
    }

    private static void ParseSqlServerVariantTypeDefinition(
        string typeDefinition,
        out string baseType,
        out string? arguments)
    {
        var normalized = typeDefinition.Trim().ToLowerInvariant();
        var open = normalized.IndexOf('(');
        if (open < 0)
        {
            if (normalized.Length == 0 ||
                !char.IsAsciiLetter(normalized[0]) ||
                normalized.Any(character => !char.IsAsciiLetterOrDigit(character)))
            {
                throw new FormatException("sql_variant 內層型別名稱無效。");
            }
            baseType = normalized;
            arguments = null;
            return;
        }

        if (!normalized.EndsWith(')') || normalized.IndexOf('(', open + 1) >= 0)
        {
            throw new FormatException("sql_variant 內層型別宣告的括號無效。");
        }
        baseType = normalized[..open].Trim();
        arguments = normalized[(open + 1)..^1].Trim();
        if (baseType.Length == 0 ||
            !char.IsAsciiLetter(baseType[0]) ||
            baseType.Any(character => !char.IsAsciiLetterOrDigit(character)) ||
            arguments.Length == 0)
        {
            throw new FormatException("sql_variant 內層型別宣告無效。");
        }
    }

    private static void RequireNoVariantArguments(string baseType, string? arguments, string? collationMetadata)
    {
        RequireNoVariantCollation(baseType, collationMetadata);
        if (arguments is not null)
        {
            throw new FormatException($"sql_variant 的 {baseType} 不可包含型別參數。");
        }
    }

    private static void RequireNoVariantCollation(string baseType, string? collationMetadata)
    {
        if (collationMetadata is not null)
        {
            throw new FormatException($"只有 sql_variant 字串型別可包含 collation metadata；{baseType} 不可使用。");
        }
    }

    private static int[] ParseVariantIntegerArguments(string baseType, string? arguments, int expectedCount)
    {
        var parts = arguments?.Split(',', StringSplitOptions.TrimEntries) ?? Array.Empty<string>();
        if (parts.Length != expectedCount ||
            parts.Any(part => !int.TryParse(
                part,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out _)))
        {
            throw new FormatException($"sql_variant 的 {baseType} 型別參數格式無效。");
        }
        return parts.Select(part => int.Parse(part, CultureInfo.InvariantCulture)).ToArray();
    }

    private static int ParseVariantSize(string baseType, string? arguments)
    {
        var values = ParseVariantIntegerArguments(baseType, arguments, 1);
        var maximum = baseType is "nchar" or "nvarchar" ? 4000 : 8000;
        return values[0] >= 1 && values[0] <= maximum
            ? values[0]
            : throw new FormatException($"sql_variant 的 {baseType} 長度必須介於 1–{maximum}。");
    }

    private static byte ParseVariantScale(string baseType, string? arguments)
    {
        var values = ParseVariantIntegerArguments(baseType, arguments, 1);
        return values[0] is >= 0 and <= 7
            ? checked((byte)values[0])
            : throw new FormatException($"sql_variant 的 {baseType} scale 必須介於 0–7。");
    }

    private static void EnsureSqlServerFractionalScale(long ticks, byte scale, string baseType)
    {
        var tickQuantum = (long)Math.Pow(10, 7 - scale);
        if (ticks % tickQuantum != 0)
        {
            throw new FormatException($"{baseType}({scale}) 無法無損保存輸入的小數秒。");
        }
    }

    private static void ParseVariantCollationMetadata(
        string? metadata,
        out int? localeId,
        out int? comparisonStyle,
        out string? collationName)
    {
        localeId = null;
        comparisonStyle = null;
        collationName = null;
        if (metadata is null)
        {
            return;
        }

        var parts = metadata.Split('|');
        if (parts.Length != 3 || parts[0].Length is < 1 or > 128 ||
            parts[0].Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_') ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedLocaleId) ||
            parsedLocaleId <= 0 ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedComparisonStyle) ||
            parsedComparisonStyle < 0)
        {
            throw new FormatException(
                "sql_variant collation metadata 必須使用 CollationName|LCID|ComparisonStyle 格式。");
        }

        collationName = parts[0];
        localeId = parsedLocaleId;
        comparisonStyle = parsedComparisonStyle;
    }

    private static DateTime ParseSqlServerVariantDateTime(string text) =>
        DateTime.TryParseExact(
            text.Trim(),
            new[]
            {
                "yyyy-MM-dd'T'HH:mm:ss",
                "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
                "yyyy-MM-dd HH:mm:ss",
                "yyyy-MM-dd HH:mm:ss.FFFFFFF"
            },
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var value)
            ? value
            : throw new FormatException("SQL Server 日期時間必須包含日期與時間，且不可包含 offset 或時區。");

    private static object ParseSqlServerTemporal(TableColumnInfo column, string text)
    {
        var baseType = GetSqlServerTemporalBaseType(column);
        switch (baseType)
        {
            case "date":
                return DateTime.TryParseExact(
                    text.Trim(),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var date)
                    ? date
                    : throw new FormatException("SQL Server date 必須使用 yyyy-MM-dd 格式，不可包含時間。");
            case "datetime":
                {
                    var dateTime = ParseSqlServerVariantDateTime(text);
                    try
                    {
                        var canonical = new SqlDateTime(dateTime).Value;
                        if (canonical.Ticks != dateTime.Ticks)
                        {
                            throw new FormatException(
                                "SQL Server datetime 只能無損保存 .000、.003、.007 秒循環精度；輸入值會被取整。");
                        }
                    }
                    catch (SqlTypeException exception)
                    {
                        throw new FormatException("SQL Server datetime 必須介於 1753-01-01 與 9999-12-31。", exception);
                    }

                    return dateTime;
                }
            case "smalldatetime":
                {
                    var dateTime = ParseSqlServerVariantDateTime(text);
                    var minimum = new DateTime(1900, 1, 1);
                    var maximum = new DateTime(2079, 6, 6, 23, 59, 0);
                    if (dateTime < minimum || dateTime > maximum)
                    {
                        throw new FormatException(
                            "SQL Server smalldatetime 必須介於 1900-01-01 00:00 與 2079-06-06 23:59。");
                    }
                    if (dateTime.Ticks % TimeSpan.TicksPerMinute != 0)
                    {
                        throw new FormatException("SQL Server smalldatetime 只能無損保存整分鐘，秒與小數秒必須為 0。");
                    }

                    return dateTime;
                }
            case "datetime2":
                {
                    var dateTime = ParseSqlServerVariantDateTime(text);
                    var scale = GetSqlServerTemporalScale(column);
                    EnsureSqlServerFractionalScale(dateTime.Ticks, scale, baseType);
                    return dateTime;
                }
            case "datetimeoffset":
                {
                    var dateTimeOffset = ParseSqlServerDateTimeOffset(text);
                    var scale = GetSqlServerTemporalScale(column);
                    EnsureSqlServerFractionalScale(dateTimeOffset.Ticks, scale, baseType);
                    return dateTimeOffset;
                }
            case "time":
                {
                    if (!TimeSpan.TryParse(text.Trim(), CultureInfo.InvariantCulture, out var time) ||
                        time < TimeSpan.Zero ||
                        time >= TimeSpan.FromDays(1))
                    {
                        throw new FormatException("SQL Server time 必須是 00:00:00 到 23:59:59.9999999 的純時間。");
                    }

                    var scale = GetSqlServerTemporalScale(column);
                    EnsureSqlServerFractionalScale(time.Ticks, scale, baseType);
                    return time;
                }
            default:
                throw new FormatException($"不支援的 SQL Server temporal 型別：{column.StorageDataTypeName}。");
        }
    }

    private static DateTimeOffset ParseSqlServerDateTimeOffset(string text)
    {
        var trimmed = text.Trim();
        var offsetFormats = new[]
        {
            "yyyy-MM-dd'T'HH:mm:sszzz",
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
            "yyyy-MM-dd HH:mm:sszzz",
            "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz"
        };
        if (DateTimeOffset.TryParseExact(
                trimmed,
                offsetFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var value))
        {
            return value;
        }

        var utcFormats = new[]
        {
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            "yyyy-MM-dd HH:mm:ss'Z'",
            "yyyy-MM-dd HH:mm:ss.FFFFFFF'Z'"
        };
        return DateTimeOffset.TryParseExact(
            trimmed,
            utcFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out value)
            ? value
            : throw new FormatException(
                "SQL Server datetimeoffset 必須包含明確的 ±HH:mm offset 或 Z，不可使用本機時區推測。");
    }

    public static byte GetSqlServerTemporalScale(TableColumnInfo column)
    {
        ArgumentNullException.ThrowIfNull(column);
        var baseType = GetSqlServerTemporalBaseType(column);
        if (baseType is not ("datetime2" or "datetimeoffset" or "time"))
        {
            throw new InvalidOperationException($"SQL Server {baseType} 沒有可設定的小數秒 scale。");
        }

        var storageType = column.StorageDataTypeName.Trim();
        var openingParenthesis = storageType.LastIndexOf('(');
        if (openingParenthesis < 0 || !storageType.EndsWith(')') ||
            !byte.TryParse(
                storageType[(openingParenthesis + 1)..^1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var scale) ||
            scale > 7)
        {
            throw new InvalidOperationException(
                $"SQL Server {baseType} 欄位缺少有效的 0–7 小數秒 scale metadata。");
        }

        return scale;
    }

    private static string GetSqlServerTemporalBaseType(TableColumnInfo column)
    {
        var storageType = column.StorageDataTypeName.Trim();
        var openingParenthesis = storageType.IndexOf('(');
        return (openingParenthesis < 0 ? storageType : storageType[..openingParenthesis]).ToLowerInvariant();
    }

    private static double ParseFiniteDouble(string text)
    {
        var value = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        return double.IsFinite(value)
            ? value
            : throw new FormatException("浮點數必須是有限值。");
    }

    public static ExactDecimalDefinition GetExactDecimalDefinition(TableColumnInfo column)
    {
        ArgumentNullException.ThrowIfNull(column);
        var normalized = column.StorageDataTypeName.Trim();
        var openParenthesis = normalized.IndexOf('(');
        var typeEnd = openParenthesis >= 0
            ? openParenthesis
            : normalized.IndexOfAny([' ', '\t']);
        if (typeEnd < 0)
        {
            typeEnd = normalized.Length;
        }

        var typeName = normalized[..typeEnd];
        if (!typeName.Equals("decimal", StringComparison.OrdinalIgnoreCase) &&
            !typeName.Equals("numeric", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"無法辨識 exact decimal 型別：{column.StorageDataTypeName}");
        }

        var isUnsigned = normalized.Contains("unsigned", StringComparison.OrdinalIgnoreCase);
        if (openParenthesis < 0)
        {
            return new ExactDecimalDefinition(null, null, isUnsigned);
        }

        var closeParenthesis = normalized.IndexOf(')', openParenthesis + 1);
        if (closeParenthesis < 0)
        {
            throw new InvalidOperationException($"無法辨識 exact decimal precision／scale：{column.StorageDataTypeName}");
        }

        var parts = normalized[(openParenthesis + 1)..closeParenthesis]
            .Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var precision) ||
            !int.TryParse(
                parts[1],
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var scale) ||
            precision <= 0)
        {
            throw new InvalidOperationException($"無法辨識 exact decimal precision／scale：{column.DataTypeName}");
        }

        return new ExactDecimalDefinition(precision, scale, isUnsigned);
    }

    public static int GetPostgreSqlMoneyScale(TableColumnInfo column)
    {
        ArgumentNullException.ThrowIfNull(column);
        return column.MonetaryScale is >= 0 and <= 127
            ? column.MonetaryScale.Value
            : throw new InvalidOperationException(
                $"PostgreSQL money 欄位缺少有效的 lc_monetary 小數位 metadata：{column.DataTypeName}");
    }

    private static PostgreSqlMoneyValue ParsePostgreSqlMoney(TableColumnInfo column, string text)
    {
        const int maximumMoneyCharacters = 256;
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Length > maximumMoneyCharacters || trimmed.Contains('\0'))
        {
            throw new FormatException(
                $"PostgreSQL money 不可為空、包含 NUL，且不可超過 {maximumMoneyCharacters} 個字元。");
        }

        var position = trimmed[0] is '+' or '-' ? 1 : 0;
        var isNegative = trimmed[0] == '-';
        var integerStart = position;
        while (position < trimmed.Length && char.IsAsciiDigit(trimmed[position]))
        {
            position++;
        }

        if (position == integerStart)
        {
            throw new FormatException(
                "PostgreSQL money 必須包含小數點前的十進位數字，不可使用幣別符號、指數或千分位格式。");
        }

        var integerEnd = position;
        var fractionStart = -1;
        if (position < trimmed.Length && trimmed[position] == '.')
        {
            fractionStart = ++position;
            while (position < trimmed.Length && char.IsAsciiDigit(trimmed[position]))
            {
                position++;
            }

            if (position == fractionStart)
            {
                throw new FormatException("PostgreSQL money 的小數點後必須包含至少一位十進位數字。");
            }
        }

        if (position != trimmed.Length)
        {
            throw new FormatException(
                "PostgreSQL money 只能使用正負號、十進位數字與一個小數點，不可使用幣別符號、指數或千分位格式。");
        }

        var scale = GetPostgreSqlMoneyScale(column);
        var fractionDigits = fractionStart < 0 ? 0 : trimmed.Length - fractionStart;
        if (fractionDigits > scale)
        {
            throw new OverflowException(
                $"{column.DataTypeName} 依目前 lc_monetary 只能無損保存 {scale} 位小數；拒絕由 PostgreSQL 自動取整。");
        }

        var integerPart = trimmed[integerStart..integerEnd].TrimStart('0');
        var fractionPart = fractionStart < 0 ? string.Empty : trimmed[fractionStart..];
        var scaledMagnitude = (integerPart + fractionPart + new string('0', scale - fractionDigits))
            .TrimStart('0');
        var maximumMagnitude = isNegative ? "9223372036854775808" : "9223372036854775807";
        if (scaledMagnitude.Length > maximumMagnitude.Length ||
            scaledMagnitude.Length == maximumMagnitude.Length &&
            string.CompareOrdinal(scaledMagnitude, maximumMagnitude) > 0)
        {
            throw new OverflowException($"{column.DataTypeName} 超出 PostgreSQL 8-byte money 的可保存範圍。");
        }

        var canonicalInteger = integerPart.Length == 0 ? "0" : integerPart;
        var canonicalFraction = scale == 0 ? string.Empty : "." + fractionPart.PadRight(scale, '0');
        var canonicalSign = isNegative && scaledMagnitude.Length > 0 ? "-" : string.Empty;
        return new PostgreSqlMoneyValue(canonicalSign + canonicalInteger + canonicalFraction);
    }

    private static SqlServerMoneyValue ParseSqlServerMoney(TableColumnInfo column, string text)
    {
        var baseType = column.StorageDataTypeName.Trim().ToLowerInvariant();
        var value = ParseSqlServerMoneyAmount(baseType, text);
        return new SqlServerMoneyValue(value, value.ToString("0.0000", CultureInfo.InvariantCulture));
    }

    private static decimal ParseSqlServerMoneyAmount(string baseType, string text)
    {
        if (baseType is not ("money" or "smallmoney"))
        {
            throw new InvalidOperationException($"無法辨識 SQL Server money 型別：{baseType}");
        }

        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 64 || trimmed.Contains('\0'))
        {
            throw new FormatException("SQL Server money 不可為空、包含 NUL，且不可超過 64 個字元。");
        }

        var value = decimal.Parse(
            trimmed,
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture);
        var minimum = baseType == "smallmoney" ? -214_748.3648m : -922_337_203_685_477.5808m;
        var maximum = baseType == "smallmoney" ? 214_748.3647m : 922_337_203_685_477.5807m;
        if (value < minimum || value > maximum || value * 10_000 != decimal.Truncate(value * 10_000))
        {
            throw new OverflowException(
                $"{baseType} 必須介於 {minimum} 與 {maximum}，且不可包含需要取整的第 5 位小數。");
        }

        return value;
    }

    private static ExactDecimalValue ParseExactDecimal(TableColumnInfo column, string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaximumEditableStructuredTextCharacters ||
            trimmed.Contains('\0'))
        {
            throw new FormatException(
                $"Exact decimal 不可為空、包含 NUL，且不可超過 {MaximumEditableStructuredTextCharacters / 1024:N0} KiB 字元。");
        }

        var definition = GetExactDecimalDefinition(column);
        var position = trimmed[0] is '+' or '-' ? 1 : 0;
        var isNegative = trimmed[0] == '-';
        var integerStart = position;
        while (position < trimmed.Length && char.IsAsciiDigit(trimmed[position]))
        {
            position++;
        }

        if (position == integerStart)
        {
            throw new FormatException("Exact decimal 必須包含小數點前的十進位數字，且不可使用指數或千分位格式。");
        }

        var integerEnd = position;
        var fractionStart = -1;
        if (position < trimmed.Length && trimmed[position] == '.')
        {
            fractionStart = ++position;
            while (position < trimmed.Length && char.IsAsciiDigit(trimmed[position]))
            {
                position++;
            }

            if (position == fractionStart)
            {
                throw new FormatException("Exact decimal 的小數點後必須包含至少一位十進位數字。");
            }
        }

        if (position != trimmed.Length)
        {
            throw new FormatException("Exact decimal 只能使用正負號、十進位數字與一個小數點，不可使用指數或千分位格式。");
        }

        if (definition.IsUnsigned && isNegative)
        {
            throw new FormatException("Unsigned DECIMAL 不可輸入負值。");
        }

        if (definition is { Precision: { } precision, Scale: { } scale })
        {
            var integerPart = trimmed.AsSpan(integerStart, integerEnd - integerStart);
            var significantIntegerPart = integerPart.TrimStart('0');
            var integerDigits = significantIntegerPart.Length;
            var fractionDigits = fractionStart < 0 ? 0 : trimmed.Length - fractionStart;
            var fitsWithoutRounding = scale >= 0
                ? FitsNonNegativeExactDecimalScale(
                    trimmed,
                    fractionStart,
                    integerDigits,
                    fractionDigits,
                    precision,
                    scale)
                : fractionDigits == 0 &&
                  integerDigits <= precision - scale &&
                  HasRequiredTrailingZeros(integerPart, -scale);
            if (!fitsWithoutRounding)
            {
                throw new OverflowException(
                    $"{column.DataTypeName} 無法無損保存這個值；請確認 precision、scale、前導零與取整位數。");
            }
        }

        return new ExactDecimalValue(trimmed);
    }

    private static SqliteNumericValue ParseSqliteNumeric(TableColumnInfo column, string text)
    {
        var normalized = NormalizeSqliteNumeric(column, text);
        if (normalized.IsSignedInt64 || normalized.IsZero)
        {
            return new SqliteNumericValue(normalized.Text);
        }

        if (normalized.SignificantDigits > 15)
        {
            throw new OverflowException(
                $"{column.DataTypeName} 的非 64 位整數值最多只能有 15 位有效數字，避免 SQLite 轉成 REAL 時失真。");
        }

        if (!double.TryParse(
                normalized.Text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var doubleValue) ||
            !double.IsFinite(doubleValue) ||
            doubleValue == 0)
        {
            throw new OverflowException($"{column.DataTypeName} 超出 SQLite REAL 可無損處理的有限範圍。");
        }

        var sqliteRoundTrip = NormalizeSqliteNumeric(
            column,
            doubleValue.ToString("G15", CultureInfo.InvariantCulture));
        if (!string.Equals(normalized.Text, sqliteRoundTrip.Text, StringComparison.Ordinal))
        {
            throw new OverflowException(
                $"{column.DataTypeName} 無法在 SQLite REAL 的 15 位有效數 round-trip 中無損保存。");
        }

        return new SqliteNumericValue(normalized.Text);
    }

    private static string NormalizeSqliteNumericText(TableColumnInfo column, string text)
        => NormalizeSqliteNumeric(column, text).Text;

    private static SqliteNumericParts NormalizeSqliteNumeric(TableColumnInfo column, string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 64 || trimmed.Contains('\0'))
        {
            throw new FormatException($"{column.DataTypeName} 不可為空、包含 NUL，且不可超過 64 個字元。");
        }

        var position = trimmed[0] is '+' or '-' ? 1 : 0;
        var isNegative = trimmed[0] == '-';
        var integerStart = position;
        while (position < trimmed.Length && char.IsAsciiDigit(trimmed[position]))
        {
            position++;
        }

        var integerEnd = position;
        var fractionStart = -1;
        if (position < trimmed.Length && trimmed[position] == '.')
        {
            fractionStart = ++position;
            while (position < trimmed.Length && char.IsAsciiDigit(trimmed[position]))
            {
                position++;
            }
        }

        if (integerEnd == integerStart && (fractionStart < 0 || position == fractionStart))
        {
            throw new FormatException($"{column.DataTypeName} 必須包含十進位數字。");
        }

        var fractionEnd = position;
        var exponent = 0;
        if (position < trimmed.Length && trimmed[position] is 'e' or 'E')
        {
            var exponentStart = ++position;
            if (position < trimmed.Length && trimmed[position] is '+' or '-')
            {
                position++;
            }

            var exponentDigitsStart = position;
            while (position < trimmed.Length && char.IsAsciiDigit(trimmed[position]))
            {
                position++;
            }

            if (position == exponentDigitsStart ||
                !int.TryParse(
                    trimmed.AsSpan(exponentStart, position - exponentStart),
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out exponent))
            {
                throw new FormatException($"{column.DataTypeName} 的指數格式或範圍無效。");
            }
        }

        if (position != trimmed.Length)
        {
            throw new FormatException(
                $"{column.DataTypeName} 只能使用正負號、十進位數字、小數點與科學記號，不可使用千分位。");
        }

        var integerDigits = trimmed[integerStart..integerEnd];
        var fractionDigits = fractionStart < 0 ? string.Empty : trimmed[fractionStart..fractionEnd];
        var digits = integerDigits + fractionDigits;
        var firstSignificant = 0;
        while (firstSignificant < digits.Length && digits[firstSignificant] == '0')
        {
            firstSignificant++;
        }

        if (firstSignificant == digits.Length)
        {
            return new SqliteNumericParts("0", 0, true, true);
        }

        var lastSignificant = digits.Length - 1;
        while (lastSignificant > firstSignificant && digits[lastSignificant] == '0')
        {
            lastSignificant--;
        }

        var significant = digits[firstSignificant..(lastSignificant + 1)];
        var trailingZeros = digits.Length - lastSignificant - 1;
        var decimalPlaces = fractionDigits.Length;
        var power = checked(exponent - decimalPlaces + trailingZeros);
        var canonical = BuildSqliteNumericText(significant, power, isNegative);
        var isSignedInt64 = power >= 0 &&
                            long.TryParse(
                                canonical,
                                NumberStyles.Integer,
                                CultureInfo.InvariantCulture,
                                out _);
        return new SqliteNumericParts(canonical, significant.Length, false, isSignedInt64);
    }

    private static string BuildSqliteNumericText(string significant, int power, bool isNegative)
    {
        var sign = isNegative ? "-" : string.Empty;
        var decimalPosition = checked(significant.Length + power);
        if (power >= 0 && decimalPosition <= 60)
        {
            return sign + significant + new string('0', power);
        }

        if (power < 0 && decimalPosition > 0)
        {
            return sign + significant[..decimalPosition] + "." + significant[decimalPosition..];
        }

        if (power < 0 && checked(significant.Length - decimalPosition) <= 60)
        {
            return sign + "0." + new string('0', -decimalPosition) + significant;
        }

        var scientificExponent = checked(power + significant.Length - 1);
        var coefficient = significant.Length == 1
            ? significant
            : significant[0] + "." + significant[1..];
        var exponentText = scientificExponent >= 0
            ? "+" + scientificExponent.ToString(CultureInfo.InvariantCulture)
            : scientificExponent.ToString(CultureInfo.InvariantCulture);
        return $"{sign}{coefficient}e{exponentText}";
    }

    private sealed record SqliteNumericParts(
        string Text,
        int SignificantDigits,
        bool IsZero,
        bool IsSignedInt64);

    private static bool FitsNonNegativeExactDecimalScale(
        string text,
        int fractionStart,
        int integerDigits,
        int fractionDigits,
        int precision,
        int scale)
    {
        if (integerDigits > Math.Max(0, precision - scale) || fractionDigits > scale)
        {
            return false;
        }

        var requiredLeadingFractionZeros = Math.Max(0, scale - precision);
        if (requiredLeadingFractionZeros == 0 || fractionStart < 0)
        {
            return true;
        }

        var checkedDigits = Math.Min(requiredLeadingFractionZeros, fractionDigits);
        return text.AsSpan(fractionStart, checkedDigits).IndexOfAnyExcept('0') < 0;
    }

    private static bool HasRequiredTrailingZeros(ReadOnlySpan<char> integerPart, int count)
    {
        var checkedDigits = Math.Min(count, integerPart.Length);
        return integerPart[^checkedDigits..].IndexOfAnyExcept('0') < 0;
    }

    private static ulong ParseUnsignedInteger(TableColumnInfo column, string text)
    {
        var value = ulong.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
        var dataType = column.StorageDataTypeName.Trim();
        if (dataType.Equals("oid", StringComparison.OrdinalIgnoreCase) ||
            dataType.Equals("xid", StringComparison.OrdinalIgnoreCase) ||
            dataType.Equals("cid", StringComparison.OrdinalIgnoreCase))
        {
            return value <= uint.MaxValue
                ? value
                : throw new OverflowException($"PostgreSQL {dataType} 的十進位值不可超過 {uint.MaxValue}。");
        }

        if (!dataType.StartsWith("bit(", StringComparison.OrdinalIgnoreCase) || !dataType.EndsWith(')'))
        {
            return value;
        }

        var widthText = dataType[4..^1];
        if (!int.TryParse(widthText, NumberStyles.None, CultureInfo.InvariantCulture, out var width) ||
            width is < 1 or > 64)
        {
            throw new FormatException("BIT 寬度必須介於 1 與 64。");
        }

        var maximum = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
        return value <= maximum
            ? value
            : throw new OverflowException($"BIT({width}) 的十進位值不可超過 {maximum}。");
    }

    private static DateTimeOffset ParseTimeWithTimeZone(TableColumnInfo column, string text)
    {
        var trimmed = text.Trim();
        var formats = new[]
        {
            "HH:mm:sszz",
            "HH:mm:sszzz",
            "HH:mm:ss.FFFFFFFzz",
            "HH:mm:ss.FFFFFFFzzz"
        };
        if (!DateTimeOffset.TryParseExact(
            trimmed,
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var value))
        {
            throw new FormatException("帶時區時間必須使用 HH:mm:ss.ffffff±HH:mm 格式，時區不可省略。");
        }

        EnsurePostgreSqlFractionalScale(value.TimeOfDay.Ticks, GetPostgreSqlTemporalScale(column), column);
        return value;
    }

    private static object ParsePostgreSqlTemporal(TableColumnInfo column, string text)
    {
        var baseType = GetPostgreSqlTemporalBaseType(column);
        return baseType switch
        {
            "timestamp without time zone" => ParsePostgreSqlTimestamp(column, text),
            "timestamp with time zone" => ParsePostgreSqlTimestampWithTimeZone(column, text),
            "time without time zone" => ParsePostgreSqlTime(column, text),
            _ => throw new FormatException($"不支援的 PostgreSQL temporal 型別：{column.StorageDataTypeName}。")
        };
    }

    private static string ParsePostgreSqlDate(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Equals("infinity", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("-infinity", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed.ToLowerInvariant();
        }

        var isBeforeCommonEra = trimmed.EndsWith(" BC", StringComparison.OrdinalIgnoreCase);
        var dateText = isBeforeCommonEra ? trimmed[..^3] : trimmed;
        var parts = dateText.Split('-');
        if (parts.Length != 3 ||
            parts[0].Length is < 4 or > 7 ||
            parts[1].Length != 2 ||
            parts[2].Length != 2 ||
            parts.Any(part => part.Any(character => !char.IsAsciiDigit(character))) ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var year) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var month) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var day) ||
            year == 0 ||
            isBeforeCommonEra && year > 4713 ||
            !isBeforeCommonEra && year > 5_874_897 ||
            month is < 1 or > 12 ||
            day < 1 || day > GetPostgreSqlDaysInMonth(year, month, isBeforeCommonEra))
        {
            throw new FormatException(
                "PostgreSQL date 必須使用 YYYY-MM-DD、YYYY-MM-DD BC、infinity 或 -infinity 格式，" +
                "且介於 4713-01-01 BC 與 5874897-12-31。");
        }

        var canonicalYear = year.ToString("D4", CultureInfo.InvariantCulture);
        var canonicalMonth = month.ToString("D2", CultureInfo.InvariantCulture);
        var canonicalDay = day.ToString("D2", CultureInfo.InvariantCulture);
        return $"{canonicalYear}-{canonicalMonth}-{canonicalDay}{(isBeforeCommonEra ? " BC" : string.Empty)}";
    }

    private static int GetPostgreSqlDaysInMonth(int year, int month, bool isBeforeCommonEra)
    {
        if (month != 2)
        {
            return month is 4 or 6 or 9 or 11 ? 30 : 31;
        }

        var astronomicalYear = isBeforeCommonEra ? 1L - year : year;
        var isLeapYear = astronomicalYear % 4 == 0 &&
                         (astronomicalYear % 100 != 0 || astronomicalYear % 400 == 0);
        return isLeapYear ? 29 : 28;
    }

    private static DateTime ParsePostgreSqlTimestamp(TableColumnInfo column, string text)
    {
        var (wholeSeconds, fraction) = SplitPostgreSqlFraction(text);
        if (!DateTime.TryParseExact(
                wholeSeconds,
                new[] { "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd HH:mm:ss" },
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var timestamp))
        {
            throw new FormatException(
                "PostgreSQL timestamp without time zone 必須使用 yyyy-MM-ddTHH:mm:ss[.ffffff]，不可包含 offset 或時區。");
        }

        var result = timestamp.AddTicks(ParsePostgreSqlFractionTicks(fraction));
        EnsurePostgreSqlFractionalScale(result.Ticks, GetPostgreSqlTemporalScale(column), column);
        return DateTime.SpecifyKind(result, DateTimeKind.Unspecified);
    }

    private static DateTimeOffset ParsePostgreSqlTimestampWithTimeZone(TableColumnInfo column, string text)
    {
        var trimmed = text.Trim();
        var offsetFormats = new[]
        {
            "yyyy-MM-dd'T'HH:mm:sszzz",
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFzzz",
            "yyyy-MM-dd HH:mm:sszzz",
            "yyyy-MM-dd HH:mm:ss.FFFFFFzzz"
        };
        if (!DateTimeOffset.TryParseExact(
                trimmed,
                offsetFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var timestamp))
        {
            var utcFormats = new[]
            {
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                "yyyy-MM-dd'T'HH:mm:ss.FFFFFF'Z'",
                "yyyy-MM-dd HH:mm:ss'Z'",
                "yyyy-MM-dd HH:mm:ss.FFFFFF'Z'"
            };
            if (!DateTimeOffset.TryParseExact(
                    trimmed,
                    utcFormats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out timestamp))
            {
                throw new FormatException(
                    "PostgreSQL timestamp with time zone 必須包含明確的 ±HH:mm offset 或 Z，不可使用本機時區推測。");
            }
        }

        EnsurePostgreSqlFractionalScale(timestamp.Ticks, GetPostgreSqlTemporalScale(column), column);
        return timestamp.ToUniversalTime();
    }

    private static TimeSpan ParsePostgreSqlTime(TableColumnInfo column, string text)
    {
        var (wholeSeconds, fraction) = SplitPostgreSqlFraction(text);
        var parts = wholeSeconds.Split(':');
        if (parts.Length != 3 ||
            parts[0].Length != 2 ||
            parts[1].Length != 2 ||
            parts[2].Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            hours > 24 || minutes > 59 || seconds > 59 ||
            hours == 24 && (minutes != 0 || seconds != 0 || fraction.Length != 0))
        {
            throw new FormatException(
                "PostgreSQL time without time zone 必須使用 HH:mm:ss[.ffffff]，範圍為 00:00:00–24:00:00。");
        }

        var time = TimeSpan.FromHours(hours) +
                   TimeSpan.FromMinutes(minutes) +
                   TimeSpan.FromSeconds(seconds) +
                   TimeSpan.FromTicks(ParsePostgreSqlFractionTicks(fraction));
        EnsurePostgreSqlFractionalScale(time.Ticks, GetPostgreSqlTemporalScale(column), column);
        return time;
    }

    private static (string WholeSeconds, string Fraction) SplitPostgreSqlFraction(string text)
    {
        var trimmed = text.Trim();
        var separator = trimmed.IndexOf('.');
        if (separator < 0)
        {
            return (trimmed, string.Empty);
        }

        var fraction = trimmed[(separator + 1)..];
        if (fraction.Length is < 1 or > 6 || fraction.Any(character => !char.IsAsciiDigit(character)))
        {
            throw new FormatException("PostgreSQL temporal 小數秒必須包含 1–6 位十進位數字。");
        }

        return (trimmed[..separator], fraction);
    }

    private static long ParsePostgreSqlFractionTicks(string fraction) => fraction.Length == 0
        ? 0
        : int.Parse(fraction.PadRight(6, '0'), NumberStyles.None, CultureInfo.InvariantCulture) * 10L;

    private static void EnsurePostgreSqlFractionalScale(
        long ticks,
        byte scale,
        TableColumnInfo column)
    {
        var tickQuantum = (long)Math.Pow(10, 7 - scale);
        if (ticks % tickQuantum != 0)
        {
            throw new FormatException(
                $"PostgreSQL {column.StorageDataTypeName} 最多接受 {scale} 位小數秒，輸入值會被取整。");
        }
    }

    public static byte GetPostgreSqlTemporalScale(TableColumnInfo column)
    {
        ArgumentNullException.ThrowIfNull(column);
        var storageType = column.StorageDataTypeName.Trim();
        var openingParenthesis = storageType.IndexOf('(');
        if (openingParenthesis < 0)
        {
            return 6;
        }

        var closingParenthesis = storageType.IndexOf(')', openingParenthesis + 1);
        if (closingParenthesis < 0 ||
            !byte.TryParse(
                storageType[(openingParenthesis + 1)..closingParenthesis],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var scale) ||
            scale > 6)
        {
            throw new InvalidOperationException(
                $"PostgreSQL temporal 欄位缺少有效的 0–6 小數秒精度 metadata：{column.StorageDataTypeName}");
        }

        return scale;
    }

    public static string GetPostgreSqlTemporalBaseType(TableColumnInfo column)
    {
        ArgumentNullException.ThrowIfNull(column);
        var storageType = column.StorageDataTypeName.Trim();
        var openingParenthesis = storageType.IndexOf('(');
        if (openingParenthesis < 0)
        {
            return storageType.ToLowerInvariant();
        }

        var closingParenthesis = storageType.IndexOf(')', openingParenthesis + 1);
        return closingParenthesis < 0
            ? storageType.ToLowerInvariant()
            : (storageType[..openingParenthesis] + storageType[(closingParenthesis + 1)..]).ToLowerInvariant();
    }

    private static TimeSpan ParseMySqlTime(TableColumnInfo column, string text)
    {
        var trimmed = text.Trim();
        var isNegative = trimmed.StartsWith("-", StringComparison.Ordinal);
        var valueText = isNegative || trimmed.StartsWith("+", StringComparison.Ordinal)
            ? trimmed[1..]
            : trimmed;
        var parts = valueText.Split(':');
        var secondParts = parts.Length == 3 ? parts[2].Split('.') : Array.Empty<string>();
        var fractionalPrecision = GetMySqlTimeFractionalPrecision(column.DataTypeName);
        if (parts.Length != 3 ||
            parts[0].Length is < 1 or > 3 ||
            parts[1].Length != 2 ||
            secondParts.Length is < 1 or > 2 ||
            secondParts[0].Length != 2 ||
            parts[0].Any(character => !char.IsAsciiDigit(character)) ||
            parts[1].Any(character => !char.IsAsciiDigit(character)) ||
            secondParts[0].Any(character => !char.IsAsciiDigit(character)) ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            !int.TryParse(secondParts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            hours > 838 || minutes > 59 || seconds > 59)
        {
            throw new FormatException("MySQL TIME 必須使用 [-]HHH:mm:ss[.ffffff] 格式，範圍不可超過 ±838:59:59。");
        }

        var fraction = secondParts.Length == 2 ? secondParts[1] : string.Empty;
        if (secondParts.Length == 2 && fraction.Length == 0 ||
            fraction.Length > fractionalPrecision ||
            fraction.Length > 6 ||
            fraction.Any(character => !char.IsAsciiDigit(character)))
        {
            throw new FormatException(
                $"MySQL {column.DataTypeName} 最多接受 {fractionalPrecision} 位小數秒。");
        }

        var microseconds = fraction.Length == 0
            ? 0
            : int.Parse(fraction.PadRight(6, '0'), NumberStyles.None, CultureInfo.InvariantCulture);
        if (hours == 838 && minutes == 59 && seconds == 59 && microseconds != 0)
        {
            throw new FormatException("MySQL TIME 的絕對值不可超過 838:59:59；邊界值不可再包含小數秒。");
        }

        var ticks = ((long)hours * 3600 + minutes * 60L + seconds) * TimeSpan.TicksPerSecond +
                    microseconds * 10L;
        return TimeSpan.FromTicks(isNegative ? -ticks : ticks);
    }

    private static DateTime ParseMySqlTemporal(TableColumnInfo column, string text)
    {
        var baseType = GetMySqlTemporalBaseType(column);
        var trimmed = text.Trim();
        if (baseType == "date")
        {
            if (!DateTime.TryParseExact(
                    trimmed,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var date) ||
                date < new DateTime(1000, 1, 1))
            {
                throw new FormatException("MySQL／MariaDB DATE 必須使用 yyyy-MM-dd，且介於 1000-01-01 與 9999-12-31。");
            }

            return DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
        }

        var fractionSeparator = trimmed.IndexOf('.');
        var dateTimeText = fractionSeparator < 0 ? trimmed : trimmed[..fractionSeparator];
        var fraction = fractionSeparator < 0 ? string.Empty : trimmed[(fractionSeparator + 1)..];
        if (fractionSeparator >= 0 &&
            (fraction.Length == 0 || fraction.Length > 6 || fraction.Any(character => !char.IsAsciiDigit(character))))
        {
            throw new FormatException("MySQL／MariaDB DATETIME／TIMESTAMP 小數秒必須包含 1–6 位十進位數字。");
        }

        if (!DateTime.TryParseExact(
                dateTimeText,
                new[] { "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd HH:mm:ss" },
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var dateTime))
        {
            throw new FormatException(
                "MySQL／MariaDB DATETIME／TIMESTAMP 必須使用 yyyy-MM-ddTHH:mm:ss[.ffffff]，不可包含 offset 或時區。");
        }

        var scale = GetMySqlTemporalScale(column);
        if (fraction.Length > scale)
        {
            throw new FormatException($"MySQL／MariaDB {column.StorageDataTypeName} 最多接受 {scale} 位小數秒，輸入值會被取整。");
        }

        if (baseType == "datetime" && dateTime < new DateTime(1000, 1, 1))
        {
            throw new FormatException("MySQL／MariaDB DATETIME 必須介於 1000-01-01 與 9999-12-31。");
        }

        var microseconds = fraction.Length == 0
            ? 0
            : int.Parse(fraction.PadRight(6, '0'), NumberStyles.None, CultureInfo.InvariantCulture);
        return DateTime.SpecifyKind(dateTime.AddTicks(microseconds * 10L), DateTimeKind.Unspecified);
    }

    public static byte GetMySqlTemporalScale(TableColumnInfo column)
    {
        ArgumentNullException.ThrowIfNull(column);
        var baseType = GetMySqlTemporalBaseType(column);
        if (baseType == "date")
        {
            return 0;
        }

        if (baseType is not ("datetime" or "timestamp"))
        {
            throw new InvalidOperationException($"無法辨識 MySQL／MariaDB temporal 型別：{column.StorageDataTypeName}");
        }

        var storageType = column.StorageDataTypeName.Trim();
        var openingParenthesis = storageType.IndexOf('(');
        if (openingParenthesis < 0)
        {
            return 0;
        }

        var closingParenthesis = storageType.IndexOf(')', openingParenthesis + 1);
        if (closingParenthesis != storageType.Length - 1 ||
            !byte.TryParse(
                storageType[(openingParenthesis + 1)..closingParenthesis],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var scale) ||
            scale > 6)
        {
            throw new InvalidOperationException(
                $"MySQL／MariaDB {baseType} 欄位缺少有效的 0–6 小數秒精度 metadata。");
        }

        return scale;
    }

    public static string GetMySqlTemporalBaseType(TableColumnInfo column)
    {
        ArgumentNullException.ThrowIfNull(column);
        var storageType = column.StorageDataTypeName.Trim();
        var openingParenthesis = storageType.IndexOf('(');
        return (openingParenthesis < 0 ? storageType : storageType[..openingParenthesis]).ToLowerInvariant();
    }

    private static int GetMySqlTimeFractionalPrecision(string dataTypeName)
    {
        var normalized = dataTypeName.Trim();
        if (normalized.Equals("time", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (normalized.StartsWith("time(", StringComparison.OrdinalIgnoreCase) &&
            normalized.EndsWith(')') &&
            int.TryParse(normalized[5..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var precision) &&
            precision is >= 0 and <= 6)
        {
            return precision;
        }

        throw new FormatException($"無法辨識 MySQL TIME 精度：{dataTypeName}");
    }

    private static ushort ParseMySqlYear(string text)
    {
        var trimmed = text.Trim();
        if (!ushort.TryParse(
                trimmed,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var value) ||
            value != 0 && value is < 1901 or > 2155)
        {
            throw new FormatException("MySQL YEAR 必須是 0，或介於 1901 與 2155 的四位數年份。");
        }

        return value;
    }

    private static IntervalComponents ParseInterval(TableColumnInfo column, string text)
    {
        var parts = text.Trim().Split(';', StringSplitOptions.TrimEntries);
        if (parts.Length != 3 ||
            !TryParseNamedInteger(parts[0], "months", out var months) ||
            !TryParseNamedInteger(parts[1], "days", out var days) ||
            !TryParseNamedLong(parts[2], "microseconds", out var microseconds))
        {
            throw new FormatException(
                "Interval 必須使用 months=<整數>;days=<整數>;microseconds=<整數> 格式。");
        }

        var components = new IntervalComponents(months, days, microseconds);
        EnsurePostgreSqlIntervalDefinition(column, components);
        return components;
    }

    private static void EnsurePostgreSqlIntervalDefinition(
        TableColumnInfo column,
        IntervalComponents components)
    {
        var definition = GetPostgreSqlIntervalDefinition(column);
        var isLossless = definition.SmallestField switch
        {
            PostgreSqlIntervalField.Year =>
                components.Months % 12 == 0 && components.Days == 0 && components.Microseconds == 0,
            PostgreSqlIntervalField.Month => components.Days == 0 && components.Microseconds == 0,
            PostgreSqlIntervalField.Day => components.Microseconds == 0,
            PostgreSqlIntervalField.Hour => components.Microseconds % MicrosecondsPerHour == 0,
            PostgreSqlIntervalField.Minute => components.Microseconds % MicrosecondsPerMinute == 0,
            PostgreSqlIntervalField.Second =>
                components.Microseconds % Pow10(6 - definition.FractionalSecondScale) == 0,
            _ => false
        };
        if (!isLossless)
        {
            throw new FormatException(
                $"PostgreSQL {column.StorageDataTypeName} 無法無損保存輸入的 interval 分量；" +
                "較小欄位會被丟棄或小數秒會被取整。");
        }
    }

    private static PostgreSqlIntervalDefinition GetPostgreSqlIntervalDefinition(TableColumnInfo column)
    {
        var normalized = string.Join(
            ' ',
            column.StorageDataTypeName.Trim().ToLowerInvariant().Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));
        if (!normalized.StartsWith("interval", StringComparison.Ordinal) ||
            normalized.Length > "interval".Length && normalized["interval".Length] is not (' ' or '('))
        {
            throw new InvalidOperationException(
                $"無法辨識 PostgreSQL interval 型別：{column.StorageDataTypeName}");
        }

        var qualifier = normalized["interval".Length..].Trim();
        byte scale = 6;
        var openingParenthesis = qualifier.LastIndexOf('(');
        if (openingParenthesis >= 0)
        {
            if (!qualifier.EndsWith(')') ||
                !byte.TryParse(
                    qualifier[(openingParenthesis + 1)..^1],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out scale) ||
                scale > 6)
            {
                throw new InvalidOperationException(
                    $"PostgreSQL interval 欄位缺少有效的 0–6 小數秒精度 metadata：{column.StorageDataTypeName}");
            }

            qualifier = qualifier[..openingParenthesis].TrimEnd();
        }

        var smallestField = qualifier switch
        {
            "" => PostgreSqlIntervalField.Second,
            "year" => PostgreSqlIntervalField.Year,
            "month" or "year to month" => PostgreSqlIntervalField.Month,
            "day" => PostgreSqlIntervalField.Day,
            "hour" or "day to hour" => PostgreSqlIntervalField.Hour,
            "minute" or "day to minute" or "hour to minute" => PostgreSqlIntervalField.Minute,
            "second" or "day to second" or "hour to second" or "minute to second" =>
                PostgreSqlIntervalField.Second,
            _ => throw new InvalidOperationException(
                $"無法辨識 PostgreSQL interval 欄位限制：{column.StorageDataTypeName}")
        };
        if (openingParenthesis >= 0 && smallestField != PostgreSqlIntervalField.Second)
        {
            throw new InvalidOperationException(
                $"PostgreSQL interval 精度只可套用於 SECOND：{column.StorageDataTypeName}");
        }

        return new PostgreSqlIntervalDefinition(smallestField, scale);
    }

    private static long Pow10(int exponent)
    {
        long value = 1;
        for (var index = 0; index < exponent; index++)
        {
            value *= 10;
        }

        return value;
    }

    private const long MicrosecondsPerMinute = 60L * 1_000_000;
    private const long MicrosecondsPerHour = 60L * MicrosecondsPerMinute;

    private enum PostgreSqlIntervalField
    {
        Year,
        Month,
        Day,
        Hour,
        Minute,
        Second
    }

    private readonly record struct PostgreSqlIntervalDefinition(
        PostgreSqlIntervalField SmallestField,
        byte FractionalSecondScale);

    private static bool TryParseNamedInteger(string part, string name, out int value)
    {
        value = default;
        var prefix = name + "=";
        return part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(
                   part[prefix.Length..].Trim(),
                   NumberStyles.AllowLeadingSign,
                   CultureInfo.InvariantCulture,
                   out value);
    }

    private static bool TryParseNamedLong(string part, string name, out long value)
    {
        value = default;
        var prefix = name + "=";
        return part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               long.TryParse(
                   part[prefix.Length..].Trim(),
                   NumberStyles.AllowLeadingSign,
                   CultureInfo.InvariantCulture,
                   out value);
    }

    private static string ParseLogSequenceNumber(string text)
    {
        var trimmed = text.Trim();
        var slash = trimmed.IndexOf('/');
        if (slash <= 0 || slash != trimmed.LastIndexOf('/') ||
            !TryParseLogSequenceNumberPart(trimmed.AsSpan(0, slash), out var high) ||
            !TryParseLogSequenceNumberPart(trimmed.AsSpan(slash + 1), out var low))
        {
            throw new FormatException(
                "PostgreSQL WAL LSN 必須使用 XXXXXXXX/XXXXXXXX 格式；斜線兩側各為 1–8 個十六進位字元。");
        }

        return $"{high:X}/{low:X}";
    }

    private static bool TryParseLogSequenceNumberPart(ReadOnlySpan<char> text, out uint value)
    {
        value = default;
        return text.Length is >= 1 and <= 8 &&
               uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }

    private static string ParseServerValidatedText(
        string text,
        string providerDisplayName,
        string valueDescription)
    {
        var trimmed = text.Trim();
        if (trimmed.Length > MaximumEditableStructuredTextCharacters)
        {
            throw new FormatException(
                $"{providerDisplayName} {valueDescription} 值不可超過 {MaximumEditableStructuredTextCharacters / 1024:N0} KiB 字元。");
        }

        if (trimmed.Contains('\0'))
        {
            throw new FormatException($"{providerDisplayName} {valueDescription} 值不可包含 NUL 字元。");
        }

        return trimmed;
    }

    private static string ParseSpatial(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length > MaximumEditableStructuredTextCharacters)
        {
            throw new FormatException(
                $"Spatial 值不可超過 {MaximumEditableStructuredTextCharacters / 1024:N0} KiB 字元。");
        }

        if (trimmed.Contains('\0'))
        {
            throw new FormatException("Spatial 值不可包含 NUL 字元。");
        }

        var separator = trimmed.IndexOf(';');
        if (separator <= 5 ||
            !trimmed.StartsWith("SRID=", StringComparison.OrdinalIgnoreCase) ||
            !uint.TryParse(
                trimmed.AsSpan(5, separator - 5),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var srid))
        {
            throw new FormatException("Spatial 必須使用 SRID=<非負整數>;<WKT> 格式。");
        }

        var wellKnownText = trimmed[(separator + 1)..].Trim();
        if (wellKnownText.Length == 0)
        {
            throw new FormatException("Spatial 的 WKT 不可空白。");
        }

        return $"SRID={srid};{wellKnownText}";
    }

    private static bool ParseBoolean(string text)
    {
        if (bool.TryParse(text, out var boolean))
        {
            return boolean;
        }

        return text.Trim() switch
        {
            "1" => true,
            "0" => false,
            _ => throw new FormatException("布林值必須是 true、false、1 或 0。")
        };
    }

    private static byte[] ParseBinary(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("二進位值必須以 0x 開頭，後接偶數個十六進位字元。");
        }

        var hex = trimmed[2..];
        if (hex.Length % 2 != 0 || hex.Length / 2 > MaximumEditableBinaryBytes)
        {
            throw new FormatException($"二進位值必須是偶數個十六進位字元，且不可超過 {MaximumEditableBinaryBytes / 1024:N0} KiB。");
        }
        if (hex.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new FormatException("0x 後只能包含 0-9、A-F 或 a-f。");
        }

        return Convert.FromHexString(hex);
    }

    private static string ParseJson(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaximumEditableStructuredTextCharacters)
        {
            throw new FormatException(
                $"JSON 不可為空，且不可超過 {MaximumEditableStructuredTextCharacters / 1024:N0} KiB 字元。");
        }

        using var _ = JsonDocument.Parse(trimmed, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        });
        return trimmed;
    }

    private static string ParseXml(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaximumEditableStructuredTextCharacters)
        {
            throw new FormatException(
                $"XML 不可為空，且不可超過 {MaximumEditableStructuredTextCharacters / 1024:N0} KiB 字元。");
        }

        var settings = new XmlReaderSettings
        {
            CheckCharacters = true,
            ConformanceLevel = ConformanceLevel.Document,
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = false,
            IgnoreProcessingInstructions = false,
            IgnoreWhitespace = false,
            MaxCharactersFromEntities = 1024,
            MaxCharactersInDocument = MaximumEditableStructuredTextCharacters,
            XmlResolver = null
        };
        using var stringReader = new StringReader(trimmed);
        using var xmlReader = XmlReader.Create(stringReader, settings);
        while (xmlReader.Read())
        {
            if (xmlReader.Depth > 64)
            {
                throw new FormatException("XML 巢狀深度不可超過 64 層。");
            }
        }

        return trimmed;
    }

    private static string ParseNetworkAddress(TableColumnInfo column, string text)
    {
        var dataType = column.StorageDataTypeName.Trim().ToLowerInvariant();
        return dataType switch
        {
            "inet" => ParseIpNetwork(text, requireNetworkAddress: false),
            "cidr" => ParseIpNetwork(text, requireNetworkAddress: true),
            "macaddr" => ParseMacAddress(text, 6),
            "macaddr8" => ParseMacAddress(text, 8),
            "inet6" => ParseMariaDbInet6(text),
            _ => throw new FormatException("不支援的網路位址型別。")
        };
    }

    private static string ParseMariaDbInet6(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Contains('/') || trimmed.Contains('%') ||
            !IPAddress.TryParse(trimmed, out var address))
        {
            throw new FormatException("MariaDB INET6 必須是不含 prefix 或 zone identifier 的 IPv4／IPv6 位址。");
        }

        return address.AddressFamily switch
        {
            System.Net.Sockets.AddressFamily.InterNetwork => address.MapToIPv6().ToString(),
            System.Net.Sockets.AddressFamily.InterNetworkV6 => address.ToString(),
            _ => throw new FormatException("MariaDB INET6 只接受 IPv4／IPv6 位址。")
        };
    }

    private static string ParseBitString(TableColumnInfo column, string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaximumEditableStructuredTextCharacters ||
            trimmed.Any(character => character is not ('0' or '1')))
        {
            throw new FormatException(
                $"Bit string 只能包含 0 或 1，且長度必須介於 1 與 {MaximumEditableStructuredTextCharacters:N0}。");
        }

        var dataType = column.StorageDataTypeName.Trim();
        var openingParenthesis = dataType.LastIndexOf('(');
        if (openingParenthesis < 0 || !dataType.EndsWith(')') ||
            !int.TryParse(dataType[(openingParenthesis + 1)..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var width) ||
            width < 1)
        {
            throw new FormatException("Bit string 欄位缺少有效的長度 metadata。");
        }

        var varying = dataType.StartsWith("bit varying", StringComparison.OrdinalIgnoreCase);
        if (varying ? trimmed.Length > width : trimmed.Length != width)
        {
            throw new FormatException(varying
                ? $"BIT VARYING({width}) 最多接受 {width} bits。"
                : $"BIT({width}) 必須剛好是 {width} bits。");
        }

        return trimmed;
    }

    private static string ParseIpNetwork(string text, bool requireNetworkAddress)
    {
        var trimmed = text.Trim();
        var slashIndex = trimmed.IndexOf('/');
        if (slashIndex != trimmed.LastIndexOf('/'))
        {
            throw new FormatException("IP 位址最多只能包含一個 CIDR prefix。");
        }

        var addressText = slashIndex < 0 ? trimmed : trimmed[..slashIndex];
        if (!IPAddress.TryParse(addressText, out var address))
        {
            throw new FormatException("IP 位址格式無效。");
        }
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && address.ScopeId != 0)
        {
            throw new FormatException("PostgreSQL inet／cidr 不接受 IPv6 zone identifier。");
        }

        var addressBytes = address.GetAddressBytes();
        var maximumPrefix = addressBytes.Length * 8;
        var prefix = maximumPrefix;
        if (slashIndex >= 0 &&
            (!int.TryParse(trimmed[(slashIndex + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out prefix) ||
             prefix < 0 ||
             prefix > maximumPrefix))
        {
            throw new FormatException($"CIDR prefix 必須介於 0 與 {maximumPrefix}。");
        }

        if (requireNetworkAddress && HasHostBits(addressBytes, prefix))
        {
            throw new FormatException("CIDR 必須使用網段起始位址，host bits 必須為 0。");
        }

        return slashIndex >= 0 || requireNetworkAddress
            ? $"{address}/{prefix}"
            : address.ToString();
    }

    private static bool HasHostBits(byte[] addressBytes, int prefix)
    {
        var fullBytes = prefix / 8;
        var remainingBits = prefix % 8;
        if (remainingBits != 0)
        {
            var hostMask = (byte)(0xFF >> remainingBits);
            if ((addressBytes[fullBytes] & hostMask) != 0)
            {
                return true;
            }
            fullBytes++;
        }

        return addressBytes.AsSpan(fullBytes).IndexOfAnyExcept((byte)0) >= 0;
    }

    private static string ParseMacAddress(string text, int expectedBytes)
    {
        var trimmed = text.Trim();
        string hex;
        if (trimmed.Contains(':', StringComparison.Ordinal) || trimmed.Contains('-', StringComparison.Ordinal))
        {
            var separator = trimmed.Contains(':', StringComparison.Ordinal) ? ':' : '-';
            if (trimmed.Contains(separator == ':' ? '-' : ':', StringComparison.Ordinal))
            {
                throw new FormatException("MAC 位址不可混用冒號與連字號。");
            }

            var parts = trimmed.Split(separator);
            if (parts.Length != expectedBytes || parts.Any(part => part.Length != 2))
            {
                throw new FormatException($"MAC 位址必須是 {expectedBytes} 組兩位數 hex。");
            }
            hex = string.Concat(parts);
        }
        else if (trimmed.Contains('.', StringComparison.Ordinal))
        {
            var parts = trimmed.Split('.');
            if (parts.Length != expectedBytes / 2 || parts.Any(part => part.Length != 4))
            {
                throw new FormatException($"MAC 位址必須是 {expectedBytes / 2} 組四位數 hex。");
            }
            hex = string.Concat(parts);
        }
        else
        {
            hex = trimmed;
        }

        if (hex.Length != expectedBytes * 2 || hex.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new FormatException($"MAC 位址必須是 {expectedBytes} bytes 的十六進位值。");
        }

        var bytes = Convert.FromHexString(hex);
        return string.Join(":", bytes.Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
    }
}
