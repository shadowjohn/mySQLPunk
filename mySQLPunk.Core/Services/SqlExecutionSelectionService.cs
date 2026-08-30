using MySqlPunk.Core.Models;

namespace MySqlPunk.Core.Services;

public enum SqlExecutionScope
{
    Document,
    Selection,
    CurrentStatement
}

public sealed record SqlExecutionSelection(string Sql, SqlExecutionScope Scope)
{
    public bool UsesSelection => Scope == SqlExecutionScope.Selection;

    public bool UsesCurrentStatement => Scope == SqlExecutionScope.CurrentStatement;
}

public static class SqlExecutionSelectionService
{
    public static SqlExecutionSelection Resolve(
        string? editorText,
        int selectionStart,
        int selectionEnd,
        DatabaseProviderKind provider,
        bool executeDocument = false)
    {
        var sql = editorText ?? string.Empty;
        if (executeDocument)
        {
            return new SqlExecutionSelection(sql, SqlExecutionScope.Document);
        }

        var start = Math.Clamp(Math.Min(selectionStart, selectionEnd), 0, sql.Length);
        var end = Math.Clamp(Math.Max(selectionStart, selectionEnd), 0, sql.Length);
        if (end > start)
        {
            var selectedSql = sql[start..end];
            if (!string.IsNullOrWhiteSpace(selectedSql))
            {
                return new SqlExecutionSelection(selectedSql, SqlExecutionScope.Selection);
            }
        }

        var caret = Math.Clamp(selectionEnd, 0, sql.Length);
        var statement = ResolveCurrentStatement(sql, caret, provider);
        return new SqlExecutionSelection(statement, SqlExecutionScope.CurrentStatement);
    }

    private static string ResolveCurrentStatement(
        string sql,
        int caret,
        DatabaseProviderKind provider)
    {
        if (sql.Length == 0)
        {
            return string.Empty;
        }

        var ranges = BuildStatementRanges(sql, provider, mysqlBackslashEscapes: false);
        var statement = SelectStatement(sql, caret, ranges, provider);
        if (provider != DatabaseProviderKind.MySql)
        {
            return statement;
        }

        var escapedRanges = BuildStatementRanges(sql, provider, mysqlBackslashEscapes: true);
        var escapedStatement = SelectStatement(sql, caret, escapedRanges, provider);
        return string.Equals(statement, escapedStatement, StringComparison.Ordinal)
            ? statement
            : string.Empty;
    }

    private static string SelectStatement(
        string sql,
        int caret,
        List<StatementRange> ranges,
        DatabaseProviderKind provider)
    {
        var selectedIndex = ranges.FindIndex(range => caret >= range.Start && caret < range.End);
        if (selectedIndex < 0)
        {
            selectedIndex = ranges.Count - 1;
        }

        if (!HasStatementContent(sql, ranges[selectedIndex], provider))
        {
            if (!ContainsOnlyWhitespaceAndDelimiters(sql, ranges[selectedIndex]))
            {
                return string.Empty;
            }

            var forward = ranges.FindIndex(
                selectedIndex + 1,
                range => HasStatementContent(sql, range, provider));
            if (forward >= 0)
            {
                selectedIndex = forward;
            }
            else
            {
                var found = false;
                for (var index = selectedIndex - 1; index >= 0; index--)
                {
                    if (HasStatementContent(sql, ranges[index], provider))
                    {
                        selectedIndex = index;
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    return string.Empty;
                }
            }
        }

        var selected = ranges[selectedIndex];
        return sql[selected.Start..selected.End];
    }

    private static List<StatementRange> BuildStatementRanges(
        string sql,
        DatabaseProviderKind provider,
        bool mysqlBackslashEscapes)
    {
        var ranges = new List<StatementRange>();
        var rangeStart = 0;
        var index = 0;
        while (index < sql.Length)
        {
            switch (sql[index])
            {
                case '\'':
                    index = SkipQuoted(
                        sql,
                        index,
                        '\'',
                        provider == DatabaseProviderKind.MySql
                            ? mysqlBackslashEscapes
                            : IsPostgreSqlEscapeString(sql, index, provider));
                    break;
                case '"':
                    index = SkipQuoted(
                        sql,
                        index,
                        '"',
                        provider == DatabaseProviderKind.MySql && mysqlBackslashEscapes);
                    break;
                case '`' when provider is DatabaseProviderKind.MySql or DatabaseProviderKind.Sqlite:
                    index = SkipQuoted(sql, index, '`', backslashEscapes: false);
                    break;
                case '[' when provider is DatabaseProviderKind.SqlServer or DatabaseProviderKind.Sqlite:
                    index = SkipBracketIdentifier(sql, index);
                    break;
                case '-' when IsDashLineComment(sql, index, provider):
                    index = SkipLineComment(sql, index + 2);
                    break;
                case '#' when provider == DatabaseProviderKind.MySql:
                    index = SkipLineComment(sql, index + 1);
                    break;
                case '/' when index + 1 < sql.Length && sql[index + 1] == '*':
                    index = SkipBlockComment(
                        sql,
                        index + 2,
                        supportsNesting: provider is DatabaseProviderKind.PostgreSql or DatabaseProviderKind.SqlServer);
                    break;
                case '$' when provider == DatabaseProviderKind.PostgreSql &&
                                   TryReadDollarDelimiter(sql, index, out var delimiter):
                    index = SkipDollarQuoted(sql, index, delimiter);
                    break;
                case ';':
                    ranges.Add(new StatementRange(rangeStart, index + 1));
                    rangeStart = index + 1;
                    index++;
                    break;
                default:
                    index++;
                    break;
            }
        }

        if (rangeStart < sql.Length || ranges.Count == 0)
        {
            ranges.Add(new StatementRange(rangeStart, sql.Length));
        }

        return ranges;
    }

    private static int SkipQuoted(
        string sql,
        int index,
        char quote,
        bool backslashEscapes)
    {
        index++;
        while (index < sql.Length)
        {
            if (backslashEscapes && sql[index] == '\\' && index + 1 < sql.Length)
            {
                index += 2;
                continue;
            }

            if (sql[index] != quote)
            {
                index++;
                continue;
            }

            if (index + 1 < sql.Length && sql[index + 1] == quote)
            {
                index += 2;
                continue;
            }

            return index + 1;
        }

        return sql.Length;
    }

    private static int SkipBracketIdentifier(string sql, int index)
    {
        index++;
        while (index < sql.Length)
        {
            if (sql[index] != ']')
            {
                index++;
                continue;
            }

            if (index + 1 < sql.Length && sql[index + 1] == ']')
            {
                index += 2;
                continue;
            }

            return index + 1;
        }

        return sql.Length;
    }

    private static int SkipLineComment(string sql, int index)
    {
        while (index < sql.Length && sql[index] is not '\r' and not '\n')
        {
            index++;
        }

        return index;
    }

    private static int SkipBlockComment(string sql, int index, bool supportsNesting)
    {
        var depth = 1;
        while (index < sql.Length && depth > 0)
        {
            if (supportsNesting && index + 1 < sql.Length && sql[index] == '/' && sql[index + 1] == '*')
            {
                depth++;
                index += 2;
            }
            else if (index + 1 < sql.Length && sql[index] == '*' && sql[index + 1] == '/')
            {
                depth--;
                index += 2;
            }
            else
            {
                index++;
            }
        }

        return index;
    }

    private static bool TryReadDollarDelimiter(string sql, int index, out string delimiter)
    {
        delimiter = string.Empty;
        if (index > 0 && IsIdentifierPart(sql[index - 1]))
        {
            return false;
        }

        var closing = index + 1;
        if (closing < sql.Length && sql[closing] != '$' &&
            !IsIdentifierStart(sql[closing]))
        {
            return false;
        }

        while (closing < sql.Length && sql[closing] != '$')
        {
            var character = sql[closing];
            if (!(char.IsLetterOrDigit(character) || character == '_'))
            {
                return false;
            }

            closing++;
        }

        if (closing >= sql.Length || sql[closing] != '$')
        {
            return false;
        }

        delimiter = sql[index..(closing + 1)];
        return true;
    }

    private static int SkipDollarQuoted(string sql, int index, string delimiter)
    {
        var contentStart = index + delimiter.Length;
        var closing = sql.IndexOf(delimiter, contentStart, StringComparison.Ordinal);
        return closing < 0 ? sql.Length : closing + delimiter.Length;
    }

    private static bool HasStatementContent(
        string sql,
        StatementRange range,
        DatabaseProviderKind provider)
    {
        var index = range.Start;
        while (index < range.End)
        {
            if (char.IsWhiteSpace(sql[index]) || sql[index] == ';')
            {
                index++;
            }
            else if (IsDashLineComment(sql, index, provider, range.End))
            {
                index = Math.Min(SkipLineComment(sql, index + 2), range.End);
            }
            else if (provider == DatabaseProviderKind.MySql && sql[index] == '#')
            {
                index = Math.Min(SkipLineComment(sql, index + 1), range.End);
            }
            else if (sql[index] == '/' && index + 1 < range.End && sql[index + 1] == '*')
            {
                index = Math.Min(
                    SkipBlockComment(
                        sql,
                        index + 2,
                        supportsNesting: provider is DatabaseProviderKind.PostgreSql or DatabaseProviderKind.SqlServer),
                    range.End);
            }
            else
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDashLineComment(
        string sql,
        int index,
        DatabaseProviderKind provider,
        int? boundary = null)
    {
        var end = boundary ?? sql.Length;
        if (index + 1 >= end || sql[index] != '-' || sql[index + 1] != '-')
        {
            return false;
        }

        return provider != DatabaseProviderKind.MySql ||
               index + 2 >= end ||
               char.IsWhiteSpace(sql[index + 2]) ||
               char.IsControl(sql[index + 2]);
    }

    private static bool IsPostgreSqlEscapeString(
        string sql,
        int quoteIndex,
        DatabaseProviderKind provider)
    {
        if (provider != DatabaseProviderKind.PostgreSql || quoteIndex == 0 ||
            sql[quoteIndex - 1] is not ('E' or 'e'))
        {
            return false;
        }

        return quoteIndex < 2 ||
               !IsIdentifierPart(sql[quoteIndex - 2]);
    }

    private static bool IsIdentifierStart(char character) =>
        character == '_' || char.IsLetter(character);

    private static bool IsIdentifierPart(char character) =>
        character is '_' or '$' || char.IsLetterOrDigit(character);

    private static bool ContainsOnlyWhitespaceAndDelimiters(string sql, StatementRange range)
    {
        for (var index = range.Start; index < range.End; index++)
        {
            if (!char.IsWhiteSpace(sql[index]) && sql[index] != ';')
            {
                return false;
            }
        }

        return true;
    }

    private readonly record struct StatementRange(int Start, int End);
}
