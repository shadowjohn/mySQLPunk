using System.Globalization;
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

        if (value is string text && text.Length > MaximumEditableStructuredTextCharacters)
        {
            const int previewCharacters = 512;
            return $"{text[..previewCharacters]}…（{text.Length:N0} chars）";
        }

        return Format(value);
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

        return string.Equals(input.Text, Format(original), StringComparison.Ordinal);
    }

    public static bool IsBinaryValueTooLargeToEdit(TableColumnInfo column, object? value) =>
        column.ValueKind == TableColumnValueKind.Binary &&
        value is byte[] bytes &&
        bytes.Length > MaximumEditableBinaryBytes;

    public static bool IsStructuredTextTooLargeToEdit(TableColumnInfo column, object? value) =>
        column.ValueKind is TableColumnValueKind.Json or TableColumnValueKind.Xml or TableColumnValueKind.BitString &&
        value is string text &&
        text.Length > MaximumEditableStructuredTextCharacters;

    private static double ParseFiniteDouble(string text)
    {
        var value = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        return double.IsFinite(value)
            ? value
            : throw new FormatException("浮點數必須是有限值。");
    }

    private static ulong ParseUnsignedInteger(TableColumnInfo column, string text)
    {
        var value = ulong.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
        var dataType = column.DataTypeName.Trim();
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
        var dataType = column.DataTypeName.Trim().ToLowerInvariant();
        return dataType switch
        {
            "inet" => ParseIpNetwork(text, requireNetworkAddress: false),
            "cidr" => ParseIpNetwork(text, requireNetworkAddress: true),
            "macaddr" => ParseMacAddress(text, 6),
            "macaddr8" => ParseMacAddress(text, 8),
            _ => throw new FormatException("不支援的網路位址型別。")
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

        var dataType = column.DataTypeName.Trim();
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
