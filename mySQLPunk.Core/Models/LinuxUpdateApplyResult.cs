namespace MySqlPunk.Core.Models;

public sealed record LinuxUpdateApplyResult(
    string Status,
    string Version,
    string RuntimeIdentifier,
    string Message,
    string LogPath)
{
    public bool WasRolledBack => string.Equals(Status, "rollback", StringComparison.Ordinal);
}
