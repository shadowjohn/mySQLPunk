using System.Security.Cryptography;
using System.Text;

namespace MySqlPunk.Core.Services;

public enum SqlDocumentEncoding
{
    Utf8,
    Utf8WithBom,
    Utf16LittleEndian,
    Utf16BigEndian
}

public sealed record SqlDocument(
    string Path,
    string Text,
    SqlDocumentEncoding Encoding,
    string Sha256,
    long Bytes);

public sealed class SqlDocumentConflictException : IOException
{
    public SqlDocumentConflictException(string message)
        : base(message)
    {
    }
}

public static class SqlDocumentService
{
    public const int MaximumDocumentBytes = 4 * 1024 * 1024;

    private static readonly UTF8Encoding Utf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly UTF8Encoding Utf8WithBom = new(
        encoderShouldEmitUTF8Identifier: true,
        throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding Utf16LittleEndian = new(
        bigEndian: false,
        byteOrderMark: true,
        throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding Utf16BigEndian = new(
        bigEndian: true,
        byteOrderMark: true,
        throwOnInvalidBytes: true);

    public static string? ResolveLaunchPath(IEnumerable<string>? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        var values = arguments.Where(argument => !string.IsNullOrWhiteSpace(argument)).ToList();
        if (values.Count != 1)
        {
            return null;
        }

        var value = values[0];
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            value = uri.LocalPath;
        }

        try
        {
            var path = Path.GetFullPath(value);
            return HasSqlExtension(path) ? path : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public static bool HasSqlExtension(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        string.Equals(Path.GetExtension(path), ".sql", StringComparison.OrdinalIgnoreCase);

    public static async Task<SqlDocument> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var targetPath = ValidatePath(path);
        var bytes = await ReadBytesAsync(targetPath, cancellationToken).ConfigureAwait(false);
        var (text, encoding) = Decode(bytes);
        ValidateText(text);
        return CreateDocument(targetPath, text, encoding, bytes);
    }

    public static async Task<SqlDocument> SaveAsync(
        string path,
        string text,
        SqlDocumentEncoding encoding = SqlDocumentEncoding.Utf8,
        string? expectedOriginalSha256 = null,
        CancellationToken cancellationToken = default)
    {
        var targetPath = ValidatePath(path);
        ArgumentNullException.ThrowIfNull(text);
        ValidateText(text);
        ValidateExpectedHash(expectedOriginalSha256);
        var bytes = Encode(text, encoding);
        if (bytes.Length > MaximumDocumentBytes)
        {
            throw new InvalidDataException(
                $"SQL 文件不可超過 {MaximumDocumentBytes / (1024 * 1024):N0} MiB。這個內容編碼後有 {bytes.Length:N0} bytes。");
        }

        var directory = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("SQL 文件的儲存目錄不存在。");
        }

        await VerifyExpectedHashAsync(targetPath, expectedOriginalSha256, cancellationToken).ConfigureAwait(false);
        var unixMode = GetTargetUnixMode(targetPath);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 64 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
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

            cancellationToken.ThrowIfCancellationRequested();
            await VerifyExpectedHashAsync(targetPath, expectedOriginalSha256, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, targetPath, overwrite: true);
            TrySetUnixFileMode(targetPath, unixMode);
            return CreateDocument(targetPath, text, encoding, bytes);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("請選擇 SQL 文件。", nameof(path));
        }

        var targetPath = Path.GetFullPath(path);
        if (!HasSqlExtension(targetPath))
        {
            throw new InvalidDataException("跨平台 SQL 編輯器只會開啟或儲存 .sql 文件。");
        }

        return targetPath;
    }

    private static async Task<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumDocumentBytes)
        {
            throw new InvalidDataException(
                $"SQL 文件不可超過 {MaximumDocumentBytes / (1024 * 1024):N0} MiB。這個文件有 {stream.Length:N0} bytes。");
        }

        var buffer = new byte[MaximumDocumentBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        if (total > MaximumDocumentBytes)
        {
            throw new InvalidDataException(
                $"SQL 文件不可超過 {MaximumDocumentBytes / (1024 * 1024):N0} MiB。");
        }

        return buffer.AsSpan(0, total).ToArray();
    }

    private static (string Text, SqlDocumentEncoding Encoding) Decode(byte[] bytes)
    {
        try
        {
            if (bytes.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }) ||
                bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }))
            {
                throw new InvalidDataException("不支援 UTF-32 SQL 文件；請先另存為 UTF-8 或 UTF-16。");
            }

            if (bytes.AsSpan().StartsWith(Utf8WithBom.GetPreamble()))
            {
                return (
                    Utf8.GetString(bytes.AsSpan(Utf8WithBom.GetPreamble().Length)),
                    SqlDocumentEncoding.Utf8WithBom);
            }

            if (bytes.AsSpan().StartsWith(Utf16LittleEndian.GetPreamble()))
            {
                return (
                    Utf16LittleEndian.GetString(bytes.AsSpan(Utf16LittleEndian.GetPreamble().Length)),
                    SqlDocumentEncoding.Utf16LittleEndian);
            }

            if (bytes.AsSpan().StartsWith(Utf16BigEndian.GetPreamble()))
            {
                return (
                    Utf16BigEndian.GetString(bytes.AsSpan(Utf16BigEndian.GetPreamble().Length)),
                    SqlDocumentEncoding.Utf16BigEndian);
            }

            return (Utf8.GetString(bytes), SqlDocumentEncoding.Utf8);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("SQL 文件不是有效的 UTF-8 或帶 BOM 的 UTF-16。", exception);
        }
    }

    private static byte[] Encode(string text, SqlDocumentEncoding encoding)
    {
        try
        {
            Encoding selectedEncoding = encoding switch
            {
                SqlDocumentEncoding.Utf8 => Utf8,
                SqlDocumentEncoding.Utf8WithBom => Utf8WithBom,
                SqlDocumentEncoding.Utf16LittleEndian => Utf16LittleEndian,
                SqlDocumentEncoding.Utf16BigEndian => Utf16BigEndian,
                _ => throw new ArgumentOutOfRangeException(nameof(encoding))
            };
            var preamble = selectedEncoding.GetPreamble();
            var content = selectedEncoding.GetBytes(text);
            if (preamble.Length == 0)
            {
                return content;
            }

            var bytes = new byte[preamble.Length + content.Length];
            preamble.CopyTo(bytes, 0);
            content.CopyTo(bytes, preamble.Length);
            return bytes;
        }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidDataException("SQL 文件含有無法以原始編碼保存的字元。", exception);
        }
    }

    private static void ValidateText(string text)
    {
        if (text.Contains('\0'))
        {
            throw new InvalidDataException("SQL 文件不可包含 NUL 字元。");
        }
    }

    private static void ValidateExpectedHash(string? expectedSha256)
    {
        if (expectedSha256 is not null &&
            (expectedSha256.Length != 64 || expectedSha256.Any(character => !Uri.IsHexDigit(character))))
        {
            throw new ArgumentException("原始 SQL 文件 SHA-256 格式無效。", nameof(expectedSha256));
        }
    }

    private static async Task VerifyExpectedHashAsync(
        string path,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        if (expectedSha256 is null)
        {
            return;
        }

        if (!File.Exists(path))
        {
            throw new SqlDocumentConflictException("SQL 文件已被其他程式刪除，未覆寫；請改用另存新檔。");
        }

        var currentBytes = await ReadBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var currentSha256 = CalculateSha256(currentBytes);
        if (!string.Equals(currentSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new SqlDocumentConflictException("SQL 文件已被其他程式修改，未覆寫；請重新開啟確認內容或另存新檔。");
        }
    }

    private static UnixFileMode GetTargetUnixMode(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            return File.GetUnixFileMode(path);
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or UnauthorizedAccessException or IOException)
        {
            return UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
    }

    private static void TrySetUnixFileMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or UnauthorizedAccessException or IOException)
        {
            // The committed file remains at the safer 0600 mode on compatibility filesystems.
        }
    }

    private static SqlDocument CreateDocument(
        string path,
        string text,
        SqlDocumentEncoding encoding,
        byte[] bytes) =>
        new(path, text, encoding, CalculateSha256(bytes), bytes.LongLength);

    private static string CalculateSha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort cleanup. Each save uses an independent staging path.
        }
    }
}
