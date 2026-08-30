namespace MySqlPunk.Core.Models;

public sealed record CrossPlatformUpdateApplyResult(
    string Status,
    string Version,
    string RuntimeIdentifier,
    string Message,
    string LogPath)
{
    public bool WasRolledBack => string.Equals(Status, "rollback", StringComparison.Ordinal);
}
