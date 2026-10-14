namespace MySqlPunk.Core.Models;

public sealed record LogicalMigrationColumn(string Name, string Kind, bool Nullable);
public sealed record LogicalMigrationIndex(string Name, IReadOnlyList<string> Columns);
public sealed record LogicalMigrationTable(string Name, IReadOnlyList<LogicalMigrationColumn> Columns, long Rows, string ContentHash)
{
    public IReadOnlyList<string> PrimaryKey { get; init; } = Array.Empty<string>();
    public string? PrimaryKeyName { get; init; }
    public IReadOnlyList<LogicalMigrationIndex> Indexes { get; init; } = Array.Empty<LogicalMigrationIndex>();
}
public sealed record LogicalMigrationPreview(
    string SourceVersion, string TargetVersion, string TargetDatabase,
    IReadOnlyList<LogicalMigrationTable> Tables, IReadOnlyList<string> Statements,
    IReadOnlyList<string> Blockers, string Fingerprint)
{
    public bool CanStart => Blockers.Count == 0 && Tables.Count > 0;
}

public enum LogicalMigrationOutcome { Verified, RolledBack, Uncertain, Refused }
public sealed record LogicalMigrationResult(LogicalMigrationOutcome Outcome, long Rows, string Report)
{
    public bool RetryBlocked => Outcome is LogicalMigrationOutcome.Uncertain or LogicalMigrationOutcome.Verified;
}
