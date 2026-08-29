namespace MySqlPunk.Core.Models;

public sealed record CrossPlatformUpdateInfo
{
    public Version CurrentVersion { get; init; } = new(0, 0, 0, 0);

    public Version LatestVersion { get; init; } = new(0, 0, 0, 0);

    public string LatestVersionText { get; init; } = string.Empty;

    public string ReleaseName { get; init; } = string.Empty;

    public Uri ReleasePageUri { get; init; } = new("https://github.com/shadowjohn/mySQLPunk/releases");

    public string RuntimeIdentifier { get; init; } = string.Empty;

    public string PackageFileName { get; init; } = string.Empty;

    public Uri? PackageDownloadUri { get; init; }

    public Uri? Sha256DownloadUri { get; init; }

    public bool IsPrerelease { get; init; }

    public bool UpdateAvailable => Normalize(LatestVersion).CompareTo(Normalize(CurrentVersion)) > 0;

    public bool HasPackageAndChecksum => PackageDownloadUri is not null && Sha256DownloadUri is not null;

    private static Version Normalize(Version version) => new(
        Math.Max(0, version.Major),
        Math.Max(0, version.Minor),
        Math.Max(0, version.Build),
        Math.Max(0, version.Revision));
}
