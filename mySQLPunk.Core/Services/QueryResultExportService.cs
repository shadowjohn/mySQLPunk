using System.Globalization;
using System.Text;
using System.Text.Json;
using MySqlPunk.Core.Models;

namespace MySqlPunk.Core.Services;

public enum QueryResultExportFormat
{
    Csv,
    Tsv,
    Json
}

public sealed record QueryResultExportSummary(
    QueryResultExportFormat Format,
    int Rows,
    long Bytes,
    string Path)
{
    public string FormatDisplayName => QueryResultExportService.GetFormatDisplayName(Format);

    public string FormattedBytes => Bytes switch
    {
        < 1024 => $"{Bytes:N0} B",
        < 1024 * 1024 => $"{Bytes / 1024d:N1} KB",
        < 1024L * 1024 * 1024 => $"{Bytes / (1024d * 1024):N1} MB",
        _ => $"{Bytes / (1024d * 1024 * 1024):N1} GB"
    };
}

public static class QueryResultExportService
{
    public const int MaximumClipboardBytes = 4 * 1024 * 1024;

    private static readonly UTF8Encoding Utf8WithBom = new(true);

    public static QueryResult CreateTablePageResult(
        TableDataSnapshot snapshot,
        IReadOnlyCollection<string>? includedColumnNames = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var allColumns = snapshot.Columns.OrderBy(column => column.Ordinal).ToList();
        if (allColumns.Select(column => column.Ordinal).Where((ordinal, index) => ordinal != index).Any())
        {
            throw new InvalidOperationException("Table 欄位 ordinal 不連續，請重新載入 schema 後再匯出。");
        }

        if (snapshot.Rows.Any(row => row.Values.Count != allColumns.Count))
        {
            throw new InvalidOperationException("Table 資料列與目前 schema 不一致，請重新整理後再匯出。");
        }

        var columns = allColumns;
        if (includedColumnNames is not null)
        {
            var knownNames = allColumns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
            var included = new HashSet<string>(StringComparer.Ordinal);
            foreach (var columnName in includedColumnNames)
            {
                if (columnName is null || !knownNames.Contains(columnName))
                {
                    throw new ArgumentException(
                        $"找不到要匯出的 Table 欄位：{columnName ?? "(null)"}",
                        nameof(includedColumnNames));
                }

                included.Add(columnName);
            }

            if (included.Count == 0)
            {
                throw new ArgumentException("本頁匯出至少需要一個可見欄位。", nameof(includedColumnNames));
            }

            columns = allColumns.Where(column => included.Contains(column.Name)).ToList();
        }

        return new QueryResult
        {
            Columns = columns.Select(column => column.Name).ToList(),
            Rows = snapshot.Rows
                .Select(row => (IReadOnlyList<object?>)columns
                    .Select(column => row.Values[column.Ordinal])
                    .ToList())
                .ToList(),
            WasTruncated = snapshot.WasTruncated || snapshot.RowOffset > 0
        };
    }

    public static string GetDefaultExtension(QueryResultExportFormat format) => format switch
    {
        QueryResultExportFormat.Csv => "csv",
        QueryResultExportFormat.Tsv => "tsv",
        QueryResultExportFormat.Json => "json",
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    public static string GetFormatDisplayName(QueryResultExportFormat format) => format switch
    {
        QueryResultExportFormat.Csv => "CSV",
        QueryResultExportFormat.Tsv => "TSV",
        QueryResultExportFormat.Json => "JSON",
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    public static QueryResultExportFormat ResolveFormat(
        string path,
        QueryResultExportFormat fallback)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".csv" => QueryResultExportFormat.Csv,
            ".tsv" or ".tab" => QueryResultExportFormat.Tsv,
            ".json" => QueryResultExportFormat.Json,
            _ => fallback
        };
    }

    public static string BuildClipboardTsv(
        QueryResult result,
        IReadOnlyList<int> rowIndices)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(rowIndices);
        ValidateResult(result);
        if (rowIndices.Count == 0)
        {
            throw new ArgumentException("請先選擇至少一列查詢結果。", nameof(rowIndices));
        }

        var requestedIndices = new List<int>(rowIndices.Count);
        var seen = new HashSet<int>();
        foreach (var rowIndex in rowIndices)
        {
            if (rowIndex < 0 || rowIndex >= result.Rows.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(rowIndices),
                    $"查詢結果列索引超出範圍：{rowIndex}");
            }

            if (!seen.Add(rowIndex))
            {
                throw new ArgumentException($"查詢結果列索引重複：{rowIndex}", nameof(rowIndices));
            }

            requestedIndices.Add(rowIndex);
        }

        var builder = new StringBuilder();
        var bytes = 0;
        AppendClipboardLine(builder, BuildDelimitedRow(result.Columns, '\t'), ref bytes);
        foreach (var rowIndex in requestedIndices)
        {
            AppendClipboardLine(builder, BuildDelimitedRow(result.Rows[rowIndex], '\t'), ref bytes);
        }

        return builder.ToString();
    }

    public static QueryResult CreateReorderedResult(
        QueryResult result,
        IReadOnlyList<int> rowIndices)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(rowIndices);
        ValidateResult(result);
        if (rowIndices.Count != result.Rows.Count)
        {
            throw new ArgumentException(
                "匯出列順序必須完整包含目前查詢結果的每一列。",
                nameof(rowIndices));
        }

        var rows = new List<IReadOnlyList<object?>>(rowIndices.Count);
        var seen = new HashSet<int>();
        foreach (var rowIndex in rowIndices)
        {
            if (rowIndex < 0 || rowIndex >= result.Rows.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(rowIndices),
                    $"查詢結果列索引超出範圍：{rowIndex}");
            }

            if (!seen.Add(rowIndex))
            {
                throw new ArgumentException($"查詢結果列索引重複：{rowIndex}", nameof(rowIndices));
            }

            rows.Add(result.Rows[rowIndex]);
        }

        return new QueryResult
        {
            Columns = result.Columns,
            Rows = rows,
            RowsAffected = result.RowsAffected,
            Elapsed = result.Elapsed,
            WasTruncated = result.WasTruncated
        };
    }

    public static async Task<QueryResultExportSummary> WriteFileAsync(
        QueryResult result,
        string path,
        QueryResultExportFormat format,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("請選擇匯出檔案。", nameof(path));
        }

        ValidateResult(result);
        var targetPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("匯出目錄不存在。");
        }

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await WriteAsync(result, stream, format, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, targetPath, overwrite: true);
            var bytes = new FileInfo(targetPath).Length;
            return new QueryResultExportSummary(format, result.Rows.Count, bytes, targetPath);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    public static async Task WriteAsync(
        QueryResult result,
        Stream destination,
        QueryResultExportFormat format,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("匯出串流不可寫入。", nameof(destination));
        }

        ValidateResult(result);
        switch (format)
        {
            case QueryResultExportFormat.Csv:
                await WriteDelimitedAsync(result, destination, ',', cancellationToken).ConfigureAwait(false);
                break;
            case QueryResultExportFormat.Tsv:
                await WriteDelimitedAsync(result, destination, '\t', cancellationToken).ConfigureAwait(false);
                break;
            case QueryResultExportFormat.Json:
                await WriteJsonAsync(result, destination, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    private static async Task WriteDelimitedAsync(
        QueryResult result,
        Stream destination,
        char delimiter,
        CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(
            destination,
            Utf8WithBom,
            bufferSize: 64 * 1024,
            leaveOpen: true)
        {
            NewLine = "\r\n"
        };

        await writer.WriteLineAsync(BuildDelimitedRow(result.Columns, delimiter).AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        foreach (var row in result.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(BuildDelimitedRow(row, delimiter).AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string BuildDelimitedRow(IReadOnlyList<string> values, char delimiter)
    {
        var cells = values.Select(value => EscapeDelimited(value, delimiter, forceQuote: value.Length == 0));
        return string.Join(delimiter, cells);
    }

    private static string BuildDelimitedRow(IReadOnlyList<object?> values, char delimiter)
    {
        var cells = values.Select(value =>
        {
            if (value is null or DBNull)
            {
                return string.Empty;
            }

            var text = FormatTextValue(value);
            return EscapeDelimited(text, delimiter, forceQuote: value is string && text.Length == 0);
        });
        return string.Join(delimiter, cells);
    }

    private static string EscapeDelimited(string value, char delimiter, bool forceQuote)
    {
        value = NeutralizeSpreadsheetFormula(value);
        if (forceQuote ||
            value.Contains(delimiter) ||
            value.Contains('"') ||
            value.Contains('\r') ||
            value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }

        return value;
    }

    private static void AppendClipboardLine(StringBuilder builder, string line, ref int bytes)
    {
        var lineBytes = Encoding.UTF8.GetByteCount(line);
        if (lineBytes > MaximumClipboardBytes - bytes - 2)
        {
            throw new InvalidOperationException(
                $"選取結果超過剪貼簿 {MaximumClipboardBytes / (1024 * 1024)} MiB 安全上限，請減少選取列或改用匯出功能。");
        }

        builder.Append(line).Append("\r\n");
        bytes += lineBytes + 2;
    }

    private static string NeutralizeSpreadsheetFormula(string value)
    {
        if (value.Length == 0 || value[0] is not ('=' or '+' or '-' or '@' or '\t' or '\r' or '\n'))
        {
            return value;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return value;
        }

        return $"'{value}";
    }

    private static async Task WriteJsonAsync(
        QueryResult result,
        Stream destination,
        CancellationToken cancellationToken)
    {
        var names = BuildUniqueColumnNames(result.Columns);
        using var writer = new Utf8JsonWriter(destination, new JsonWriterOptions { Indented = true });
        writer.WriteStartArray();
        foreach (var row in result.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteStartObject();
            for (var index = 0; index < names.Count; index++)
            {
                writer.WritePropertyName(names[index]);
                WriteJsonValue(writer, row[index]);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> BuildUniqueColumnNames(IReadOnlyList<string> columns)
    {
        var names = new List<string>(columns.Count);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < columns.Count; index++)
        {
            var baseName = string.IsNullOrWhiteSpace(columns[index]) ? $"Column{index + 1}" : columns[index];
            var candidate = baseName;
            var suffix = 2;
            while (!used.Add(candidate))
            {
                candidate = $"{baseName}_{suffix++}";
            }

            names.Add(candidate);
        }

        return names;
    }

    private static void WriteJsonValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null or DBNull:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case char character:
                writer.WriteStringValue(character.ToString());
                break;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                break;
            case byte number:
                writer.WriteNumberValue(number);
                break;
            case sbyte number:
                writer.WriteNumberValue(number);
                break;
            case short number:
                writer.WriteNumberValue(number);
                break;
            case ushort number:
                writer.WriteNumberValue(number);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case uint number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case ulong number:
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            case float number when float.IsFinite(number):
                writer.WriteNumberValue(number);
                break;
            case double number when double.IsFinite(number):
                writer.WriteNumberValue(number);
                break;
            case DateTime dateTime:
                writer.WriteStringValue(dateTime.ToString("O", CultureInfo.InvariantCulture));
                break;
            case DateTimeOffset dateTimeOffset:
                writer.WriteStringValue(dateTimeOffset.ToString("O", CultureInfo.InvariantCulture));
                break;
            case DateOnly date:
                writer.WriteStringValue(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                break;
            case TimeOnly time:
                writer.WriteStringValue(time.ToString("O", CultureInfo.InvariantCulture));
                break;
            case TimeSpan duration:
                writer.WriteStringValue(duration.ToString("c", CultureInfo.InvariantCulture));
                break;
            case Guid guid:
                writer.WriteStringValue(guid);
                break;
            case byte[] bytes:
                writer.WriteStringValue($"0x{Convert.ToHexString(bytes)}");
                break;
            case IFormattable formattable:
                writer.WriteStringValue(formattable.ToString(null, CultureInfo.InvariantCulture));
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    private static string FormatTextValue(object value) => value switch
    {
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

    private static void ValidateResult(QueryResult result)
    {
        if (!result.HasResultSet)
        {
            throw new InvalidOperationException("目前沒有可匯出的查詢結果。");
        }

        if (result.Rows.Any(row => row.Count != result.Columns.Count))
        {
            throw new InvalidDataException("查詢結果的欄位與資料列數量不一致。");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
