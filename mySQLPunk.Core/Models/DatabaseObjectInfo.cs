namespace MySqlPunk.Core.Models;

public enum DatabaseObjectKind
{
    Table,
    View
}

public sealed record DatabaseObjectInfo(
    string Schema,
    string Name,
    DatabaseObjectKind Kind)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Schema)
        ? Name
        : $"{Schema}.{Name}";
}
