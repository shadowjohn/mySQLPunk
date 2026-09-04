using System.Globalization;
using System.Text;
using MySqlPunk.Core.Models;

namespace MySqlPunk.Core.Services;

public static class ConnectionUriImportService
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public const int MaximumUriCharacters = 8192;

    private const int MaximumNameCharacters = 256;
    private const int MaximumUsernameCharacters = 256;
    private const int MaximumPasswordCharacters = 4096;
    private const int MaximumDatabaseCharacters = 512;

    public static ConnectionProfile Parse(string? uriText)
    {
        if (string.IsNullOrEmpty(uriText))
        {
            throw new InvalidDataException("請貼上連線 URI。");
        }

        if (uriText.Length > MaximumUriCharacters)
        {
            throw new InvalidDataException($"連線 URI 不可超過 {MaximumUriCharacters:N0} 個字元。");
        }

        if (ContainsUnsafeTextCharacters(uriText) || !HasValidPercentEncoding(uriText))
        {
            throw new InvalidDataException("連線 URI 格式無效。");
        }

        if (uriText.Any(char.IsWhiteSpace) ||
            !string.Equals(uriText, uriText.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidDataException("連線 URI 不可包含未編碼的空白字元。");
        }

        ValidateRawNetworkPath(uriText);
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidDataException("連線 URI 格式無效，且不可包含 fragment。");
        }

        var query = ParseQuery(uri);
        return uri.Scheme.ToLowerInvariant() switch
        {
            "mysql" or "mariadb" => ParseNetworkProfile(
                uri,
                query,
                DatabaseProviderKind.MySql,
                3306,
                new[] { "name", "ssl", "sslmode", "timeout" }),
            "postgres" or "postgresql" => ParseNetworkProfile(
                uri,
                query,
                DatabaseProviderKind.PostgreSql,
                5432,
                new[] { "name", "sslmode", "timeout" }),
            "mssql" or "sqlserver" => ParseNetworkProfile(
                uri,
                query,
                DatabaseProviderKind.SqlServer,
                1433,
                new[] { "name", "encrypt", "timeout" }),
            "sqlite" => ParseSqliteProfile(uri, query),
            _ => throw new InvalidDataException(
                "目前只支援 mysql、mariadb、postgres、postgresql、mssql、sqlserver 與 sqlite URI。")
        };
    }

    private static ConnectionProfile ParseNetworkProfile(
        Uri uri,
        IReadOnlyDictionary<string, string> query,
        DatabaseProviderKind provider,
        int defaultPort,
        IReadOnlyCollection<string> allowedQueryKeys)
    {
        RejectUnknownQueryKeys(query, allowedQueryKeys);
        if (string.IsNullOrWhiteSpace(uri.Host) || string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidDataException("網路資料庫 URI 必須包含主機與使用者名稱。");
        }

        var host = uri.IdnHost;
        if (host.Length > 253)
        {
            throw new InvalidDataException("連線 URI 的主機名稱過長。");
        }

        var port = uri.Port < 0 ? defaultPort : uri.Port;
        if (port is < 1 or > 65535)
        {
            throw new InvalidDataException("連線 URI 的連接埠必須介於 1 到 65535。");
        }

        var encodedUserInfo = uri.GetComponents(UriComponents.UserInfo, UriFormat.UriEscaped);
        var separator = encodedUserInfo.IndexOf(':');
        var username = DecodeComponent(
            separator < 0 ? encodedUserInfo : encodedUserInfo[..separator],
            MaximumUsernameCharacters,
            "使用者名稱",
            rejectBoundaryWhitespace: true);
        var password = separator < 0
            ? string.Empty
            : DecodeComponent(encodedUserInfo[(separator + 1)..], MaximumPasswordCharacters, "密碼");
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new InvalidDataException("網路資料庫 URI 必須包含使用者名稱。");
        }

        var database = ParseSingleDatabasePath(uri);
        var timeout = ParseTimeout(query);
        var tlsMode = ParseTlsOption(provider, query);
        var displayName = GetDisplayName(provider);
        var hostDisplay = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
        var generatedName = $"{displayName} · {hostDisplay}:{port}" +
                            (database.Length == 0 ? string.Empty : $"/{database}");
        var name = query.TryGetValue("name", out var suppliedName)
            ? ValidateName(suppliedName)
            : generatedName.Length <= MaximumNameCharacters
                ? generatedName
                : throw new InvalidDataException("URI 目標過長，請用 name 參數指定較短的連線名稱。");

        var profile = new ConnectionProfile
        {
            Name = name,
            Provider = provider,
            Host = host,
            Port = port,
            Username = username,
            Password = password,
            PasswordChanged = true,
            Database = database,
            TimeoutSeconds = timeout,
            TlsMode = tlsMode,
            UseSecretStore = false
        };
        profile.Validate();
        return profile;
    }

    private static ConnectionProfile ParseSqliteProfile(
        Uri uri,
        IReadOnlyDictionary<string, string> query)
    {
        RejectUnknownQueryKeys(query, new[] { "name", "timeout" });
        if (!string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || uri.Port >= 0)
        {
            throw new InvalidDataException("SQLite URI 必須使用不含主機與認證資訊的本機絕對路徑。");
        }

        var path = DecodeComponent(uri.AbsolutePath, MaximumDatabaseCharacters, "SQLite 路徑");
        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException("SQLite URI 必須指定本機絕對路徑。");
        }

        var fullPath = Path.GetFullPath(path);
        if (Path.EndsInDirectorySeparator(fullPath) || Directory.Exists(fullPath))
        {
            throw new InvalidDataException("SQLite URI 必須指定資料庫檔案，不可指向目錄。");
        }

        var defaultName = Path.GetFileNameWithoutExtension(fullPath);
        var name = query.TryGetValue("name", out var suppliedName)
            ? ValidateName(suppliedName)
            : string.IsNullOrWhiteSpace(defaultName) ? "SQLite" : defaultName;
        var profile = new ConnectionProfile
        {
            Name = name,
            Provider = DatabaseProviderKind.Sqlite,
            Database = fullPath,
            TimeoutSeconds = ParseTimeout(query),
            PasswordChanged = true,
            TlsMode = ConnectionTlsMode.Disabled
        };
        profile.Validate();
        return profile;
    }

    private static string ParseSingleDatabasePath(Uri uri)
    {
        var encodedPath = uri.AbsolutePath;
        if (encodedPath.Length == 0 || encodedPath == "/")
        {
            return string.Empty;
        }

        if (encodedPath[0] != '/')
        {
            throw new InvalidDataException("連線 URI 的資料庫路徑格式無效。");
        }

        var database = DecodeComponent(
            encodedPath[1..],
            MaximumDatabaseCharacters,
            "資料庫名稱",
            rejectBoundaryWhitespace: true);
        if (database.Contains('/') || database.Contains('\\'))
        {
            throw new InvalidDataException("連線 URI 一次只能指定一個資料庫名稱。");
        }

        return database;
    }

    private static int ParseTimeout(IReadOnlyDictionary<string, string> query)
    {
        if (!query.TryGetValue("timeout", out var timeoutText))
        {
            return 15;
        }

        if (!int.TryParse(timeoutText, NumberStyles.None, CultureInfo.InvariantCulture, out var timeout) ||
            timeout is < 1 or > 300)
        {
            throw new InvalidDataException("URI timeout 必須是 1 到 300 的整數秒數。");
        }

        return timeout;
    }

    private static ConnectionTlsMode ParseTlsOption(
        DatabaseProviderKind provider,
        IReadOnlyDictionary<string, string> query)
    {
        if (provider == DatabaseProviderKind.MySql)
        {
            if (query.ContainsKey("ssl") && query.ContainsKey("sslmode"))
            {
                throw new InvalidDataException("URI 不可同時指定 ssl 與 sslmode。");
            }

            if (query.TryGetValue("ssl", out var ssl))
            {
                return ParseBoolean(ssl, "ssl")
                    ? ConnectionTlsMode.Required
                    : ConnectionTlsMode.Disabled;
            }

            return query.TryGetValue("sslmode", out var mysqlSslMode)
                ? ParseMySqlSslMode(mysqlSslMode)
                : ConnectionTlsMode.Default;
        }

        if (provider == DatabaseProviderKind.PostgreSql)
        {
            return query.TryGetValue("sslmode", out var postgresSslMode)
                ? ParsePostgreSqlSslMode(postgresSslMode)
                : ConnectionTlsMode.Default;
        }

        if (provider == DatabaseProviderKind.SqlServer && query.TryGetValue("encrypt", out var encrypt))
        {
            return encrypt.ToLowerInvariant() switch
            {
                "true" or "yes" or "mandatory" => ConnectionTlsMode.Mandatory,
                "false" or "no" or "optional" => ConnectionTlsMode.Optional,
                "strict" => ConnectionTlsMode.Strict,
                _ => throw new InvalidDataException(
                    "URI encrypt 只接受 true、false、yes、no、mandatory、optional 或 strict。")
            };
        }

        return ConnectionTlsMode.Default;
    }

    private static ConnectionTlsMode ParseMySqlSslMode(string value) => value.ToLowerInvariant() switch
    {
        "disable" or "disabled" or "none" => ConnectionTlsMode.Disabled,
        "prefer" or "preferred" => ConnectionTlsMode.Preferred,
        "require" or "required" => ConnectionTlsMode.Required,
        "verifyca" or "verify-ca" => ConnectionTlsMode.VerifyCertificateAuthority,
        "verifyfull" or "verify-full" => ConnectionTlsMode.VerifyFull,
        _ => throw new InvalidDataException(
            "MySQL URI sslmode 只接受 disabled、preferred、required、verify-ca 或 verify-full。")
    };

    private static ConnectionTlsMode ParsePostgreSqlSslMode(string value) => value.ToLowerInvariant() switch
    {
        "disable" or "disabled" => ConnectionTlsMode.Disabled,
        "allow" => ConnectionTlsMode.Allow,
        "prefer" or "preferred" => ConnectionTlsMode.Preferred,
        "require" or "required" => ConnectionTlsMode.Required,
        "verifyca" or "verify-ca" => ConnectionTlsMode.VerifyCertificateAuthority,
        "verifyfull" or "verify-full" => ConnectionTlsMode.VerifyFull,
        _ => throw new InvalidDataException(
            "PostgreSQL URI sslmode 只接受 disable、allow、prefer、require、verify-ca 或 verify-full。")
    };

    private static bool ParseBoolean(string value, string optionName) => value.ToLowerInvariant() switch
    {
        "true" or "1" or "yes" => true,
        "false" or "0" or "no" => false,
        _ => throw new InvalidDataException($"URI {optionName} 必須是布林值。")
    };

    private static string ValidateName(string value)
    {
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Length == 0 ||
            value.Length > MaximumNameCharacters ||
            ContainsUnsafeTextCharacters(value))
        {
            throw new InvalidDataException($"URI name 必須是 1 到 {MaximumNameCharacters:N0} 個有效字元。");
        }

        return value;
    }

    private static IReadOnlyDictionary<string, string> ParseQuery(Uri uri)
    {
        var encodedQuery = uri.GetComponents(UriComponents.Query, UriFormat.UriEscaped);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (encodedQuery.Length == 0)
        {
            return result;
        }

        foreach (var pair in encodedQuery.Split('&'))
        {
            if (pair.Length == 0)
            {
                throw new InvalidDataException("連線 URI 的查詢參數格式無效。");
            }

            var separator = pair.IndexOf('=');
            var encodedKey = separator < 0 ? pair : pair[..separator];
            var encodedValue = separator < 0 ? string.Empty : pair[(separator + 1)..];
            var key = DecodeComponent(
                encodedKey,
                64,
                "查詢參數",
                rejectBoundaryWhitespace: true).ToLowerInvariant();
            var value = DecodeComponent(encodedValue, MaximumNameCharacters, "查詢參數值");
            if (key.Length == 0 || !result.TryAdd(key, value))
            {
                throw new InvalidDataException("連線 URI 不可包含空白或重複的查詢參數。");
            }
        }

        return result;
    }

    private static void RejectUnknownQueryKeys(
        IReadOnlyDictionary<string, string> query,
        IReadOnlyCollection<string> allowedKeys)
    {
        if (query.Keys.Any(key => !allowedKeys.Contains(key, StringComparer.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("連線 URI 包含目前不支援的查詢參數。");
        }
    }

    private static string DecodeComponent(
        string encoded,
        int maximumCharacters,
        string label,
        bool rejectBoundaryWhitespace = false)
    {
        string decoded;
        try
        {
            decoded = StrictPercentDecode(encoded);
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException($"連線 URI 的{label}格式無效。");
        }

        if (decoded.Length > maximumCharacters ||
            ContainsUnsafeTextCharacters(decoded) ||
            rejectBoundaryWhitespace && !string.Equals(decoded, decoded.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidDataException($"連線 URI 的{label}過長或包含控制字元。");
        }

        return decoded;
    }

    private static bool HasValidPercentEncoding(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%')
            {
                continue;
            }

            if (index + 2 >= value.Length ||
                !Uri.IsHexDigit(value[index + 1]) ||
                !Uri.IsHexDigit(value[index + 2]))
            {
                return false;
            }

            index += 2;
        }

        return true;
    }

    private static void ValidateRawNetworkPath(string uriText)
    {
        var schemeSeparator = uriText.IndexOf(':');
        if (schemeSeparator <= 0)
        {
            return;
        }

        var scheme = uriText[..schemeSeparator];
        if (!scheme.Equals("mysql", StringComparison.OrdinalIgnoreCase) &&
            !scheme.Equals("mariadb", StringComparison.OrdinalIgnoreCase) &&
            !scheme.Equals("postgres", StringComparison.OrdinalIgnoreCase) &&
            !scheme.Equals("postgresql", StringComparison.OrdinalIgnoreCase) &&
            !scheme.Equals("mssql", StringComparison.OrdinalIgnoreCase) &&
            !scheme.Equals("sqlserver", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var authorityMarker = schemeSeparator + 1;
        if (authorityMarker + 1 >= uriText.Length ||
            uriText[authorityMarker] != '/' ||
            uriText[authorityMarker + 1] != '/')
        {
            throw new InvalidDataException("網路資料庫 URI 必須使用 scheme://user@host 格式。");
        }

        var authorityStart = authorityMarker + 2;
        var pathStart = uriText.IndexOfAny(new[] { '/', '?', '#' }, authorityStart);
        if (pathStart < 0 || uriText[pathStart] != '/')
        {
            return;
        }

        var pathEnd = uriText.IndexOfAny(new[] { '?', '#' }, pathStart);
        var rawPath = pathEnd < 0
            ? uriText[pathStart..]
            : uriText[pathStart..pathEnd];
        if (rawPath.Length <= 1)
        {
            return;
        }

        var encodedDatabase = rawPath[1..];
        if (encodedDatabase.Contains('/') || encodedDatabase.Contains('\\'))
        {
            throw new InvalidDataException("連線 URI 一次只能指定一個資料庫名稱。");
        }

        var decodedDatabase = DecodeComponent(
            encodedDatabase,
            MaximumDatabaseCharacters,
            "資料庫名稱",
            rejectBoundaryWhitespace: true);
        if (decodedDatabase is "." or "..")
        {
            throw new InvalidDataException("連線 URI 的資料庫名稱不可使用 dot-segment。");
        }
    }

    private static string StrictPercentDecode(string encoded)
    {
        var result = new StringBuilder(encoded.Length);
        var index = 0;
        while (index < encoded.Length)
        {
            if (encoded[index] != '%')
            {
                result.Append(encoded[index]);
                index++;
                continue;
            }

            var bytes = new List<byte>();
            while (index < encoded.Length && encoded[index] == '%')
            {
                bytes.Add(byte.Parse(
                    encoded.AsSpan(index + 1, 2),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture));
                index += 3;
            }

            result.Append(StrictUtf8.GetString(bytes.ToArray()));
        }

        return result.ToString();
    }

    private static bool ContainsUnsafeTextCharacters(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            UnicodeCategory category;
            int scalar;
            var character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    return true;
                }

                var rune = new Rune(character, value[++index]);
                scalar = rune.Value;
                category = Rune.GetUnicodeCategory(rune);
            }
            else if (char.IsLowSurrogate(character))
            {
                return true;
            }
            else
            {
                scalar = character;
                category = CharUnicodeInfo.GetUnicodeCategory(character);
            }

            if (scalar is >= 0xFDD0 and <= 0xFDEF ||
                (scalar & 0xFFFE) == 0xFFFE ||
                category is UnicodeCategory.Control or
                UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or
                UnicodeCategory.ParagraphSeparator or
                UnicodeCategory.Surrogate)
            {
                return true;
            }
        }

        return false;
    }

    private static string GetDisplayName(DatabaseProviderKind provider) => provider switch
    {
        DatabaseProviderKind.MySql => "MySQL / MariaDB",
        DatabaseProviderKind.PostgreSql => "PostgreSQL",
        DatabaseProviderKind.SqlServer => "SQL Server",
        _ => provider.ToString()
    };
}
