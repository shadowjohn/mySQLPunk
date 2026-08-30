using System.Text;
using MySqlPunk.Core.Models;

namespace MySqlPunk.Core.Services;

public sealed record QueryExecutionHistoryEntry(
    DateTimeOffset ExecutedAt,
    DatabaseProviderKind Provider,
    string Database,
    string Sql,
    bool UsedSelection,
    TimeSpan Duration,
    string Summary)
{
    public string ProviderDisplayName => Provider switch
    {
        DatabaseProviderKind.MySql => "MySQL / MariaDB",
        DatabaseProviderKind.PostgreSql => "PostgreSQL",
        DatabaseProviderKind.Sqlite => "SQLite",
        DatabaseProviderKind.SqlServer => "SQL Server",
        _ => Provider.ToString()
    };

    public string SourceDisplay => $"{ProviderDisplayName} · {Database}";

    public string DisplayText =>
        $"{ExecutedAt:HH:mm:ss} · {SourceDisplay}{(UsedSelection ? " · 選取範圍" : string.Empty)} · " +
        QueryExecutionHistory.BuildPreview(Sql);
}

public sealed class QueryExecutionHistory
{
    public const int DefaultCapacity = 50;
    public const int DefaultByteBudget = 2 * 1024 * 1024;

    private readonly int _capacity;
    private readonly int _byteBudget;
    private readonly List<QueryExecutionHistoryEntry> _entries = new();
    private int _totalBytes;

    public QueryExecutionHistory(
        int capacity = DefaultCapacity,
        int byteBudget = DefaultByteBudget)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        if (byteBudget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteBudget));
        }

        _capacity = capacity;
        _byteBudget = byteBudget;
    }

    public IReadOnlyList<QueryExecutionHistoryEntry> Entries => _entries;

    public bool Add(QueryExecutionHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var entryBytes = Encoding.UTF8.GetByteCount(entry.Sql);
        if (string.IsNullOrWhiteSpace(entry.Sql) || entryBytes > _byteBudget)
        {
            return false;
        }

        var duplicateIndex = _entries.FindIndex(existing =>
            existing.Provider == entry.Provider &&
            string.Equals(existing.Database, entry.Database, StringComparison.Ordinal) &&
            string.Equals(existing.Sql, entry.Sql, StringComparison.Ordinal));
        if (duplicateIndex >= 0)
        {
            _totalBytes -= Encoding.UTF8.GetByteCount(_entries[duplicateIndex].Sql);
            _entries.RemoveAt(duplicateIndex);
        }

        _entries.Insert(0, entry);
        _totalBytes += entryBytes;
        while (_entries.Count > _capacity || _totalBytes > _byteBudget)
        {
            var lastIndex = _entries.Count - 1;
            _totalBytes -= Encoding.UTF8.GetByteCount(_entries[lastIndex].Sql);
            _entries.RemoveAt(lastIndex);
        }

        return true;
    }

    public void Clear()
    {
        _entries.Clear();
        _totalBytes = 0;
    }

    public static string BuildPreview(string sql, int maximumLength = 80)
    {
        ArgumentNullException.ThrowIfNull(sql);
        if (maximumLength < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLength));
        }

        var builder = new StringBuilder(sql.Length);
        var pendingSpace = false;
        foreach (var character in sql)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        var preview = builder.ToString();
        if (preview.Length <= maximumLength)
        {
            return preview;
        }

        var retainedLength = maximumLength - 1;
        if (retainedLength > 0 && char.IsHighSurrogate(preview[retainedLength - 1]))
        {
            retainedLength--;
        }

        return preview[..retainedLength] + "…";
    }
}
