namespace MySqlPunk.Core.Services;

public sealed record SqlExecutionSelection(string Sql, bool UsesSelection);

public static class SqlExecutionSelectionService
{
    public static SqlExecutionSelection Resolve(
        string? editorText,
        int selectionStart,
        int selectionEnd)
    {
        var sql = editorText ?? string.Empty;
        var start = Math.Clamp(Math.Min(selectionStart, selectionEnd), 0, sql.Length);
        var end = Math.Clamp(Math.Max(selectionStart, selectionEnd), 0, sql.Length);
        if (end > start)
        {
            var selectedSql = sql[start..end];
            if (!string.IsNullOrWhiteSpace(selectedSql))
            {
                return new SqlExecutionSelection(selectedSql, UsesSelection: true);
            }
        }

        return new SqlExecutionSelection(sql, UsesSelection: false);
    }
}
