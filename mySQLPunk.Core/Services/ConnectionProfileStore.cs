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
        Converters = { new JsonStringEnumConverter() }
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
            var profiles = await JsonSerializer.DeserializeAsync<List<ConnectionProfile>>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false) ?? new List<ConnectionProfile>();

            foreach (var profile in profiles)
            {
                profile.Password = string.Empty;
                profile.ApplyProviderDefaults();
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

    private static string GetDefaultFilePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            appData = GetFallbackApplicationDataPath();
        }

        return Path.Combine(appData, "mySQLPunk", "connections.json");
    }

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
