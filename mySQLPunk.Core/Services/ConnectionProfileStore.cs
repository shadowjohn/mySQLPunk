using System.Text.Json;
using System.Text.Json.Serialization;
using MySqlPunk.Core.Models;

namespace MySqlPunk.Core.Services;

public sealed class ConnectionProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) }
    };

    private static readonly HashSet<string> CertificatePathFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "tlsCaCertificatePath",
        "tlsClientCertificatePath",
        "tlsClientKeyPath"
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public ConnectionProfileStore(string? filePath = null)
    {
        FilePath = filePath ?? GetDefaultFilePath();
    }

    public string FilePath { get; }

    public async Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(FilePath))
            {
                return Array.Empty<ConnectionProfile>();
            }

            await using var stream = File.OpenRead(FilePath);
            using var document = await JsonDocument.ParseAsync(
                stream,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32
                },
                cancellationToken).ConfigureAwait(false);
            ValidateSerializedProfiles(document.RootElement);
            var profiles = document.RootElement.Deserialize<List<ConnectionProfile>>(JsonOptions) ??
                           new List<ConnectionProfile>();

            var profileIds = new HashSet<Guid>();
            foreach (var profile in profiles)
            {
                profile.Password = string.Empty;
                profile.ApplyPersistedCompatibility();
                try
                {
                    profile.Validate();
                }
                catch (InvalidOperationException exception)
                {
                    throw new InvalidDataException("連線設定包含無效或不相容的欄位。", exception);
                }

                if (profile.Id == Guid.Empty || !profileIds.Add(profile.Id))
                {
                    throw new InvalidDataException("連線設定包含空白或重複的識別碼。");
                }
            }

            return profiles;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(IEnumerable<ConnectionProfile> profiles, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var snapshot = profiles.Select(profile =>
        {
            var copy = profile.Clone();
            copy.Validate();
            copy.Password = string.Empty;
            return copy;
        }).ToList();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = FilePath + ".tmp";
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporaryPath, FilePath, true);
            RestrictFilePermissions(FilePath);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static string GetDefaultApplicationDataDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            appData = GetFallbackApplicationDataPath();
        }

        return Path.Combine(appData, "mySQLPunk");
    }

    private static string GetDefaultFilePath() =>
        Path.Combine(GetDefaultApplicationDataDirectory(), "connections.json");

    private static string GetFallbackApplicationDataPath()
    {
        if (OperatingSystem.IsLinux())
        {
            var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (!string.IsNullOrWhiteSpace(xdgConfigHome) && Path.IsPathFullyQualified(xdgConfigHome))
            {
                return xdgConfigHome;
            }

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                return Path.Combine(userProfile, ".config");
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                return Path.Combine(userProfile, "Library", "Application Support");
            }
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            return localAppData;
        }

        throw new InvalidOperationException("無法定位使用者設定目錄；不會把連線設定寫入程式安裝目錄。");
    }

    private static void ValidateSerializedProfiles(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("連線設定檔根節點必須是陣列。");
        }

        foreach (var element in root.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("每筆連線設定都必須是 JSON 物件。");
            }

            var propertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hasLegacyUseSsl = false;
            var hasTlsMode = false;
            foreach (var property in element.EnumerateObject())
            {
                if (!propertyNames.Add(property.Name))
                {
                    throw new InvalidDataException("連線設定不可包含重複欄位。");
                }

                if (property.Name.Equals("password", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("連線設定檔不可包含密碼欄位。");
                }

                if (property.Name.Equals("useSsl", StringComparison.OrdinalIgnoreCase))
                {
                    hasLegacyUseSsl = true;
                    if (property.Value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                    {
                        throw new InvalidDataException("舊 useSsl 欄位必須是布林值。");
                    }
                }
                else if (property.Name.Equals("tlsMode", StringComparison.OrdinalIgnoreCase))
                {
                    hasTlsMode = true;
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidDataException("tlsMode 欄位必須是明確的模式名稱。");
                    }
                }
                else if (CertificatePathFields.Contains(property.Name))
                {
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidDataException($"{property.Name} 欄位必須是字串路徑。");
                    }
                }
            }

            if (hasLegacyUseSsl && hasTlsMode)
            {
                throw new InvalidDataException(
                    "連線設定不可同時包含舊 useSsl 與新 tlsMode；請移除其中一個後重試。");
            }
        }
    }

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
        catch (PlatformNotSupportedException)
        {
            // The profile never includes passwords; unsupported permission APIs are non-fatal.
        }
        catch (UnauthorizedAccessException)
        {
            // Keep the usable profile file when a mounted filesystem cannot change its mode.
        }
        catch (IOException)
        {
            // Some network or compatibility filesystems cannot apply Unix modes.
        }
    }
}
