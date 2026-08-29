using System.Globalization;
using MySqlPunk.Core.Models;

namespace MySqlPunk.Core.Services;

public static class TableCellValueConverter
{
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
                TableColumnValueKind.UnsignedInteger => ulong.Parse(input.Text, NumberStyles.Integer, CultureInfo.InvariantCulture),
                TableColumnValueKind.Decimal => decimal.Parse(input.Text, NumberStyles.Number, CultureInfo.InvariantCulture),
                TableColumnValueKind.FloatingPoint => ParseFiniteDouble(input.Text),
                TableColumnValueKind.Boolean => ParseBoolean(input.Text),
                TableColumnValueKind.Date => DateTime.ParseExact(
                    input.Text,
                    new[] { "yyyy-MM-dd", "O" },
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind),
                TableColumnValueKind.DateTime => DateTime.Parse(
                    input.Text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind),
                TableColumnValueKind.DateTimeOffset => DateTimeOffset.Parse(
                    input.Text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind),
                TableColumnValueKind.Time => TimeSpan.Parse(input.Text, CultureInfo.InvariantCulture),
                TableColumnValueKind.Guid => System.Guid.Parse(input.Text),
                _ => throw new InvalidOperationException($"「{column.Name}」的型別目前不支援直接編輯。")
            };
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            throw new InvalidOperationException(
                $"「{column.Name}」的值不符合 {column.DataTypeName} 格式。",
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
        bool boolean => boolean ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

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

        return Format(value);
    }

    public static bool MatchesOriginal(TableColumnInfo column, TableCellInput input, object? original)
    {
        if (input.Mode == TableCellInputMode.Null)
        {
            return original is null or DBNull;
        }

        return input.Mode == TableCellInputMode.Value &&
               original is not null and not DBNull &&
               string.Equals(input.Text, Format(original), StringComparison.Ordinal);
    }

    private static double ParseFiniteDouble(string text)
    {
        var value = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        return double.IsFinite(value)
            ? value
            : throw new FormatException("浮點數必須是有限值。");
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
}
