using MySqlPunk.Core.Models;

namespace MySqlPunk.Core.Services;

public static class DatabaseObjectFilterService
{
    public static IReadOnlyList<DatabaseObjectInfo> Filter(
        IReadOnlyList<DatabaseObjectInfo> objects,
        string? searchText,
        DatabaseObjectKind? kind = null)
    {
        ArgumentNullException.ThrowIfNull(objects);

        var terms = (searchText ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return objects
            .Where(item => (!kind.HasValue || item.Kind == kind.Value) &&
                           terms.All(term => item.DisplayName.Contains(
                               term,
                               StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }
}
