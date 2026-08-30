using System.Text.Json;

namespace MySqlPunk.Core.Services;

public sealed record TableColumnPreferenceKey(
    Guid ProfileId,
    string Database,
    string Schema,
    string Table);

public sealed class TableColumnPreferenceStore
{
    private const int CurrentVersion = 1;
    private const int MaximumEntries = 500;
    private const int MaximumHiddenColumns = 4096;
    private const int MaximumIdentifierCharacters = 1024;
    private const long MaximumFileBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public TableColumnPreferenceStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            ConnectionProfileStore.GetDefaultApplicationDataDirectory(),
            "table-column-preferences.json");
    }

    public string FilePath { get; }

    public async Task<IReadOnlySet<string>> LoadHiddenColumnsAsync(
        TableColumnPreferenceKey key,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var entry = document.Entries.SingleOrDefault(candidate => KeysEqual(candidate, key));
            return entry?.HiddenColumns.ToHashSet(StringComparer.Ordinal) ??
                   new HashSet<string>(StringComparer.Ordinal);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveHiddenColumnsAsync(
        TableColumnPreferenceKey key,
        IEnumerable<string> hiddenColumns,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(hiddenColumns);
        var hidden = hiddenColumns.Distinct(StringComparer.Ordinal).ToList();
        ValidateHiddenColumns(hidden);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await ReadAsync(cancellationToken).ConfigureAwait(false);
            document.Entries.RemoveAll(entry => KeysEqual(entry, key));
            if (hidden.Count > 0)
            {
                document.Entries.Add(new PreferenceEntry
                {
                    ProfileId = key.ProfileId,
                    Database = key.Database,
                    Schema = key.Schema,
                    Table = key.Table,
                    HiddenColumns = hidden,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                });
            }

            document.Entries = document.Entries
                .OrderByDescending(entry => entry.UpdatedAtUtc)
                .Take(MaximumEntries)
                .ToList();
            await WriteAsync(document, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("連線 ID 不可為空。", nameof(profileId));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (document.Entries.RemoveAll(entry => entry.ProfileId == profileId) > 0)
            {
                await WriteAsync(document, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PreferenceDocument> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(FilePath))
        {
            return new PreferenceDocument();
        }

        var fileInfo = new FileInfo(FilePath);
        if (fileInfo.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("Table 欄位偏好檔超過 1 MiB 安全上限。");
        }

        try
        {
            await using var stream = new FileStream(
                FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var document = await JsonSerializer.DeserializeAsync<PreferenceDocument>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("Table 欄位偏好檔是空的。");
            ValidateDocument(document);
            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Table 欄位偏好檔不是有效 JSON。", exception);
        }
    }

    private async Task WriteAsync(PreferenceDocument document, CancellationToken cancellationToken)
    {
        ValidateDocument(document);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.LongLength > MaximumFileBytes)
        {
            throw new InvalidDataException("Table 欄位偏好檔超過 1 MiB 安全上限。");
        }

        var directory = Path.GetDirectoryName(FilePath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("Table 欄位偏好檔必須位於明確目錄中。");
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using (var stream = new FileStream(temporaryPath, options))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, FilePath, overwrite: true);
            RestrictFilePermissions(FilePath);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static void ValidateDocument(PreferenceDocument document)
    {
        if (document.Version != CurrentVersion)
        {
            throw new InvalidDataException($"不支援的 Table 欄位偏好版本：{document.Version}");
        }

        if (document.Entries is null || document.Entries.Count > MaximumEntries)
        {
            throw new InvalidDataException($"Table 欄位偏好最多只能有 {MaximumEntries:N0} 筆。");
        }

        var keys = new HashSet<TableColumnPreferenceKey>();
        foreach (var entry in document.Entries)
        {
            var key = new TableColumnPreferenceKey(entry.ProfileId, entry.Database, entry.Schema, entry.Table);
            ValidateKey(key);
            ValidateHiddenColumns(entry.HiddenColumns);
            if (!keys.Add(key))
            {
                throw new InvalidDataException("Table 欄位偏好檔含有重複資料表 key。");
            }
        }
    }

    private static void ValidateKey(TableColumnPreferenceKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.ProfileId == Guid.Empty)
        {
            throw new ArgumentException("連線 ID 不可為空。", nameof(key));
        }

        ValidateIdentifier(key.Database, "database", allowEmpty: false);
        ValidateIdentifier(key.Schema, "schema", allowEmpty: true);
        ValidateIdentifier(key.Table, "table", allowEmpty: false);
    }

    private static void ValidateHiddenColumns(IReadOnlyCollection<string>? hiddenColumns)
    {
        if (hiddenColumns is null || hiddenColumns.Count > MaximumHiddenColumns)
        {
            throw new InvalidDataException($"單一資料表最多只能保存 {MaximumHiddenColumns:N0} 個隱藏欄位。");
        }

        var distinct = new HashSet<string>(StringComparer.Ordinal);
        foreach (var columnName in hiddenColumns)
        {
            ValidateIdentifier(columnName, "欄位", allowEmpty: false);
            if (!distinct.Add(columnName))
            {
                throw new InvalidDataException("Table 欄位偏好含有重複隱藏欄名。");
            }
        }
    }

    private static void ValidateIdentifier(string? value, string label, bool allowEmpty)
    {
        if (value is null || (!allowEmpty && value.Length == 0))
        {
            throw new InvalidDataException($"Table 欄位偏好的 {label} 不可空白。");
        }

        if (value.Length > MaximumIdentifierCharacters || value.Contains('\0'))
        {
            throw new InvalidDataException(
                $"Table 欄位偏好的 {label} 不可含 NUL，且最多 {MaximumIdentifierCharacters:N0} 字元。");
        }
    }

    private static bool KeysEqual(PreferenceEntry entry, TableColumnPreferenceKey key) =>
        entry.ProfileId == key.ProfileId &&
        string.Equals(entry.Database, key.Database, StringComparison.Ordinal) &&
        string.Equals(entry.Schema, key.Schema, StringComparison.Ordinal) &&
        string.Equals(entry.Table, key.Table, StringComparison.Ordinal);

    private static void RestrictFilePermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or UnauthorizedAccessException or IOException)
        {
            // Identifiers only; keep the usable preference when a compatibility filesystem cannot chmod.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort cleanup. Each atomic write uses its own staging path.
        }
    }

    private sealed class PreferenceDocument
    {
        public int Version { get; set; } = CurrentVersion;

        public List<PreferenceEntry> Entries { get; set; } = new();
    }

    private sealed class PreferenceEntry
    {
        public Guid ProfileId { get; set; }

        public string Database { get; set; } = string.Empty;

        public string Schema { get; set; } = string.Empty;

        public string Table { get; set; } = string.Empty;

        public List<string> HiddenColumns { get; set; } = new();

        public DateTimeOffset UpdatedAtUtc { get; set; }
    }
}
