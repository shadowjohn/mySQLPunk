using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace mySQLPunk.lib
{
    public enum SqlCompletionKind
    {
        Snippet,
        Alias,
        Column,
        Table,
        Keyword
    }

    public sealed class SqlCompletionSuggestion
    {
        public string InsertText { get; set; }
        public string MatchText { get; set; }
        public string Detail { get; set; }
        public SqlCompletionKind Kind { get; set; }
        public bool AppendSpace { get; set; }
        public SqlCodeSnippet Snippet { get; set; }

        public string DisplayText { get; set; }

        public override string ToString()
        {
            return string.IsNullOrWhiteSpace(DisplayText) ? InsertText : DisplayText;
        }
    }

    public sealed class SqlCompletionContext
    {
        public string Prefix { get; set; }
        public string Qualifier { get; set; }
        public int ReplacementStart { get; set; }
        public int ReplacementLength { get; set; }
        public bool IsTableContext { get; set; }
        public Dictionary<string, string> AliasToTable { get; private set; }
        public List<string> ReferencedTables { get; private set; }

        public SqlCompletionContext()
        {
            Prefix = string.Empty;
            Qualifier = string.Empty;
            AliasToTable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ReferencedTables = new List<string>();
        }
    }

    public static class SqlEditorCompletionService
    {
        private const string IdentifierPartPattern = @"(?:\[[^\]]+\]|`[^`]+`|""(?:[^""]|"""")+""|[A-Za-z_][A-Za-z0-9_$#]*)";
        private static readonly Regex SourceRegex = new Regex(
            @"\b(?:FROM|JOIN|UPDATE|INTO)\s+(?<table>" + IdentifierPartPattern + @"(?:\s*\.\s*" + IdentifierPartPattern + @"){0,2})(?:\s+(?:AS\s+)?(?<alias>" + IdentifierPartPattern + @"))?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex TableContextRegex = new Regex(
            @"\b(?:FROM|JOIN|UPDATE|INTO|TABLE)\s+$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly HashSet<string> ReservedAliases = new HashSet<string>(new[]
        {
            "WHERE", "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "FULL", "CROSS", "ON", "GROUP", "ORDER",
            "HAVING", "LIMIT", "OFFSET", "FETCH", "UNION", "SET", "VALUES", "RETURNING", "USING", "WHEN"
        }, StringComparer.OrdinalIgnoreCase);

        public static SqlCompletionContext Analyze(string sql, int cursorPosition)
        {
            sql = sql ?? string.Empty;
            cursorPosition = Math.Max(0, Math.Min(cursorPosition, sql.Length));
            string cleaned = MaskCommentsAndStrings(sql);
            int statementStart = FindStatementStart(cleaned.Substring(0, cursorPosition));
            int statementEnd = cleaned.IndexOf(';', cursorPosition);
            if (statementEnd < 0) statementEnd = cleaned.Length;

            SqlCompletionContext context = new SqlCompletionContext();
            int wordStart = cursorPosition;
            while (wordStart > statementStart && IsIdentifierCharacter(sql[wordStart - 1])) wordStart--;
            context.Prefix = sql.Substring(wordStart, cursorPosition - wordStart);
            context.ReplacementStart = wordStart;
            context.ReplacementLength = cursorPosition - wordStart;

            int qualifierEnd = wordStart - 1;
            if (qualifierEnd >= statementStart && sql[qualifierEnd] == '.')
            {
                int qualifierStart = qualifierEnd;
                while (qualifierStart > statementStart && IsIdentifierCharacter(sql[qualifierStart - 1])) qualifierStart--;
                context.Qualifier = UnquoteIdentifier(sql.Substring(qualifierStart, qualifierEnd - qualifierStart));
            }

            string activeCleaned = cleaned.Substring(statementStart, statementEnd - statementStart);
            string activeOriginal = sql.Substring(statementStart, statementEnd - statementStart);
            foreach (Match match in SourceRegex.Matches(activeCleaned))
            {
                Group tableGroup = match.Groups["table"];
                if (!tableGroup.Success) continue;

                string rawTable = activeOriginal.Substring(tableGroup.Index, tableGroup.Length);
                string tableName = NormalizeQualifiedIdentifier(rawTable);
                if (tableName.Length == 0 || ReservedAliases.Contains(LastIdentifierPart(tableName))) continue;
                AddUnique(context.ReferencedTables, tableName);
                context.AliasToTable[tableName] = tableName;

                Group aliasGroup = match.Groups["alias"];
                if (aliasGroup.Success)
                {
                    string alias = UnquoteIdentifier(activeOriginal.Substring(aliasGroup.Index, aliasGroup.Length));
                    if (alias.Length > 0 && !ReservedAliases.Contains(alias)) context.AliasToTable[alias] = tableName;
                }
            }

            string beforeWord = cleaned.Substring(statementStart, Math.Max(0, wordStart - statementStart));
            context.IsTableContext = string.IsNullOrEmpty(context.Qualifier) && TableContextRegex.IsMatch(beforeWord);
            return context;
        }

        public static List<SqlCompletionSuggestion> BuildSuggestions(
            SqlCompletionContext context,
            IEnumerable<string> keywords,
            IEnumerable<string> tables,
            IDictionary<string, List<string>> columnsByTable,
            IEnumerable<SqlCodeSnippet> snippets)
        {
            if (context == null) return new List<SqlCompletionSuggestion>();

            List<RankedSuggestion> ranked = new List<RankedSuggestion>();
            HashSet<string> dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string prefix = context.Prefix ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(context.Qualifier))
            {
                string table;
                if (!context.AliasToTable.TryGetValue(context.Qualifier, out table)) table = context.Qualifier;
                foreach (string column in FindColumns(columnsByTable, table))
                {
                    AddSuggestion(ranked, dedupe, prefix, column, column, table, SqlCompletionKind.Column, false, null, 0);
                }
                return Finish(ranked);
            }

            if (context.IsTableContext)
            {
                foreach (string table in tables ?? Enumerable.Empty<string>())
                {
                    AddSuggestion(ranked, dedupe, prefix, table, table, string.Empty, SqlCompletionKind.Table, true, null, 0);
                }
                return Finish(ranked);
            }

            foreach (SqlCodeSnippet snippet in snippets ?? Enumerable.Empty<SqlCodeSnippet>())
            {
                if (snippet == null) continue;
                string matchText = ((snippet.Shortcut ?? string.Empty) + " " + (snippet.Name ?? string.Empty)).Trim();
                AddSuggestion(ranked, dedupe, prefix, snippet.Shortcut, matchText, snippet.Name, SqlCompletionKind.Snippet, false, snippet, 0);
            }

            foreach (KeyValuePair<string, string> alias in context.AliasToTable)
            {
                if (string.Equals(alias.Key, alias.Value, StringComparison.OrdinalIgnoreCase)) continue;
                AddSuggestion(ranked, dedupe, prefix, alias.Key, alias.Key, alias.Value, SqlCompletionKind.Alias, false, null, 1);
            }

            bool oneTable = context.ReferencedTables.Count == 1;
            foreach (string table in context.ReferencedTables)
            {
                List<string> columns = FindColumns(columnsByTable, table);
                foreach (string column in columns)
                {
                    if (oneTable)
                    {
                        AddSuggestion(ranked, dedupe, prefix, column, column, table, SqlCompletionKind.Column, false, null, 2);
                    }

                    foreach (KeyValuePair<string, string> alias in context.AliasToTable)
                    {
                        if (!string.Equals(alias.Value, table, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(alias.Key, table, StringComparison.OrdinalIgnoreCase)) continue;
                        AddSuggestion(ranked, dedupe, prefix, alias.Key + "." + column, column, alias.Key, SqlCompletionKind.Column, false, null, 2);
                    }
                }
            }

            foreach (string keyword in keywords ?? Enumerable.Empty<string>())
            {
                AddSuggestion(ranked, dedupe, prefix, keyword, keyword, string.Empty, SqlCompletionKind.Keyword, true, null, 3);
            }
            foreach (string table in tables ?? Enumerable.Empty<string>())
            {
                AddSuggestion(ranked, dedupe, prefix, table, table, string.Empty, SqlCompletionKind.Table, true, null, 4);
            }

            return Finish(ranked);
        }

        private static void AddSuggestion(
            List<RankedSuggestion> ranked,
            HashSet<string> dedupe,
            string prefix,
            string insertText,
            string matchText,
            string detail,
            SqlCompletionKind kind,
            bool appendSpace,
            SqlCodeSnippet snippet,
            int kindRank)
        {
            insertText = (insertText ?? string.Empty).Trim();
            if (insertText.Length == 0 || dedupe.Contains(insertText)) return;
            int matchRank = GetMatchRank(prefix, insertText, matchText);
            if (matchRank < 0) return;
            dedupe.Add(insertText);
            ranked.Add(new RankedSuggestion
            {
                MatchRank = matchRank,
                KindRank = kindRank,
                Suggestion = new SqlCompletionSuggestion
                {
                    InsertText = insertText,
                    MatchText = matchText,
                    Detail = detail ?? string.Empty,
                    Kind = kind,
                    AppendSpace = appendSpace,
                    Snippet = snippet
                }
            });
        }

        private static int GetMatchRank(string prefix, string insertText, string matchText)
        {
            prefix = prefix ?? string.Empty;
            if (prefix.Length == 0) return 0;
            if ((insertText ?? string.Empty).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return 0;
            string[] terms = (matchText ?? string.Empty).Split(new[] { ' ', '.', '-', '_', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (terms.Any(term => term.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return 1;
            return -1;
        }

        private static List<SqlCompletionSuggestion> Finish(List<RankedSuggestion> ranked)
        {
            return ranked
                .OrderBy(item => item.MatchRank)
                .ThenBy(item => item.KindRank)
                .ThenBy(item => item.Suggestion.InsertText, StringComparer.OrdinalIgnoreCase)
                .Take(100)
                .Select(item => item.Suggestion)
                .ToList();
        }

        private static List<string> FindColumns(IDictionary<string, List<string>> columnsByTable, string table)
        {
            if (columnsByTable == null || string.IsNullOrWhiteSpace(table)) return new List<string>();
            List<string> columns;
            if (columnsByTable.TryGetValue(table, out columns) && columns != null) return columns;
            string tail = LastIdentifierPart(table);
            if (columnsByTable.TryGetValue(tail, out columns) && columns != null) return columns;
            KeyValuePair<string, List<string>> qualified = columnsByTable.FirstOrDefault(
                item => string.Equals(LastIdentifierPart(item.Key), tail, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(qualified.Key) && qualified.Value != null) return qualified.Value;
            return new List<string>();
        }

        private static string MaskCommentsAndStrings(string sql)
        {
            if (string.IsNullOrEmpty(sql)) return string.Empty;
            char[] output = sql.ToCharArray();
            bool singleQuote = false;
            bool lineComment = false;
            bool blockComment = false;
            for (int i = 0; i < output.Length; i++)
            {
                char current = sql[i];
                char next = i + 1 < sql.Length ? sql[i + 1] : '\0';
                if (lineComment)
                {
                    if (current == '\r' || current == '\n') lineComment = false;
                    else output[i] = ' ';
                    continue;
                }
                if (blockComment)
                {
                    output[i] = (current == '\r' || current == '\n') ? current : ' ';
                    if (current == '*' && next == '/')
                    {
                        output[i + 1] = ' ';
                        i++;
                        blockComment = false;
                    }
                    continue;
                }
                if (singleQuote)
                {
                    output[i] = (current == '\r' || current == '\n') ? current : ' ';
                    if (current == '\'' && next == '\'')
                    {
                        output[i + 1] = ' ';
                        i++;
                    }
                    else if (current == '\'') singleQuote = false;
                    continue;
                }
                if (current == '-' && next == '-')
                {
                    output[i] = output[i + 1] = ' ';
                    i++;
                    lineComment = true;
                }
                else if (current == '/' && next == '*')
                {
                    output[i] = output[i + 1] = ' ';
                    i++;
                    blockComment = true;
                }
                else if (current == '\'')
                {
                    output[i] = ' ';
                    singleQuote = true;
                }
            }
            return new string(output);
        }

        private static int FindStatementStart(string cleaned)
        {
            int index = cleaned.LastIndexOf(';');
            return index < 0 ? 0 : index + 1;
        }

        private static bool IsIdentifierCharacter(char value)
        {
            return char.IsLetterOrDigit(value) || value == '_' || value == '$' || value == '#';
        }

        private static string LastIdentifierPart(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string[] parts = value.Split('.');
            return UnquoteIdentifier(parts[parts.Length - 1].Trim());
        }

        private static string NormalizeQualifiedIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return string.Join(".", value.Split('.')
                .Select(part => UnquoteIdentifier(part))
                .Where(part => part.Length > 0));
        }

        private static string UnquoteIdentifier(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length >= 2)
            {
                if ((value[0] == '[' && value[value.Length - 1] == ']') ||
                    (value[0] == '`' && value[value.Length - 1] == '`') ||
                    (value[0] == '"' && value[value.Length - 1] == '"'))
                {
                    value = value.Substring(1, value.Length - 2);
                }
            }
            return value.Trim();
        }

        private static void AddUnique(List<string> values, string value)
        {
            if (string.IsNullOrWhiteSpace(value) || values.Any(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase))) return;
            values.Add(value);
        }

        private sealed class RankedSuggestion
        {
            public int MatchRank { get; set; }
            public int KindRank { get; set; }
            public SqlCompletionSuggestion Suggestion { get; set; }
        }
    }

    public sealed class SqlCodeSnippet
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Shortcut { get; set; }
        public string Description { get; set; }
        public string Sql { get; set; }
        [JsonIgnore]
        public bool IsBuiltIn { get; set; }

        public SqlCodeSnippet Clone()
        {
            return new SqlCodeSnippet
            {
                Id = Id,
                Name = Name,
                Shortcut = Shortcut,
                Description = Description,
                Sql = Sql,
                IsBuiltIn = IsBuiltIn
            };
        }
    }

    public sealed class SqlSnippetExpansion
    {
        public string Text { get; set; }
        public int CursorOffset { get; set; }
    }

    public sealed class SqlSnippetService
    {
        public const string CursorMarker = "$CURSOR$";
        private readonly string _path;

        public SqlSnippetService(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Snippet path is required.", "path");
            _path = path;
        }

        public List<SqlCodeSnippet> GetAll()
        {
            List<SqlCodeSnippet> output = GetBuiltIns();
            output.AddRange(LoadCustom());
            return output;
        }

        public List<SqlCodeSnippet> LoadCustom()
        {
            try
            {
                if (!File.Exists(_path)) return new List<SqlCodeSnippet>();
                List<SqlCodeSnippet> items = JsonConvert.DeserializeObject<List<SqlCodeSnippet>>(File.ReadAllText(_path, Encoding.UTF8));
                return NormalizeCustom(items, false);
            }
            catch
            {
                return new List<SqlCodeSnippet>();
            }
        }

        public SqlCodeSnippet Save(SqlCodeSnippet snippet)
        {
            SqlCodeSnippet normalized = NormalizeOne(snippet, true);
            List<SqlCodeSnippet> items = LoadCustom();
            SqlCodeSnippet existing = items.FirstOrDefault(item => string.Equals(item.Id, normalized.Id, StringComparison.OrdinalIgnoreCase));
            if (existing != null) items.Remove(existing);
            if (GetBuiltIns().Concat(items).Any(item => string.Equals(item.Shortcut, normalized.Shortcut, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(Localization.Format("Snippet.DuplicateShortcut", normalized.Shortcut));
            }
            items.Add(normalized);
            SaveCustom(items);
            return normalized.Clone();
        }

        public void Delete(string id)
        {
            List<SqlCodeSnippet> items = LoadCustom();
            items.RemoveAll(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            SaveCustom(items);
        }

        public int Import(string sourcePath)
        {
            List<SqlCodeSnippet> imported = NormalizeCustom(
                JsonConvert.DeserializeObject<List<SqlCodeSnippet>>(File.ReadAllText(sourcePath, Encoding.UTF8)), true);
            List<SqlCodeSnippet> current = LoadCustom();
            foreach (SqlCodeSnippet item in imported)
            {
                current.RemoveAll(existing => string.Equals(existing.Id, item.Id, StringComparison.OrdinalIgnoreCase) ||
                                              string.Equals(existing.Shortcut, item.Shortcut, StringComparison.OrdinalIgnoreCase));
                if (GetBuiltIns().Any(builtIn => string.Equals(builtIn.Shortcut, item.Shortcut, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException(Localization.Format("Snippet.DuplicateShortcut", item.Shortcut));
                }
                current.Add(item);
            }
            SaveCustom(current);
            return imported.Count;
        }

        public void Export(string destinationPath)
        {
            WriteJson(destinationPath, LoadCustom());
        }

        public static SqlSnippetExpansion Expand(string sql, string indentation, string newLine)
        {
            string normalized = (sql ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");
            string lineBreak = string.IsNullOrEmpty(newLine) ? Environment.NewLine : newLine;
            string indent = indentation ?? string.Empty;
            normalized = normalized.Replace("\n", lineBreak + indent);
            int cursor = normalized.IndexOf(CursorMarker, StringComparison.Ordinal);
            if (cursor < 0) cursor = normalized.Length;
            else normalized = normalized.Remove(cursor, CursorMarker.Length);
            normalized = normalized.Replace(CursorMarker, string.Empty);
            return new SqlSnippetExpansion { Text = normalized, CursorOffset = cursor };
        }

        public static List<SqlCodeSnippet> GetBuiltIns()
        {
            return new List<SqlCodeSnippet>
            {
                BuiltIn("builtin-select", "sel", "Snippet.BuiltinSelect", "Snippet.BuiltinSelectDescription", "SELECT $CURSOR$\r\nFROM table_name\r\nWHERE condition;"),
                BuiltIn("builtin-join", "join", "Snippet.BuiltinJoin", "Snippet.BuiltinJoinDescription", "SELECT a.*, b.*\r\nFROM table_a AS a\r\nINNER JOIN table_b AS b ON b.foreign_id = a.id\r\nWHERE $CURSOR$;"),
                BuiltIn("builtin-insert", "ins", "Snippet.BuiltinInsert", "Snippet.BuiltinInsertDescription", "INSERT INTO table_name (column_name)\r\nVALUES ($CURSOR$);"),
                BuiltIn("builtin-update", "upd", "Snippet.BuiltinUpdate", "Snippet.BuiltinUpdateDescription", "UPDATE table_name\r\nSET column_name = value\r\nWHERE $CURSOR$;"),
                BuiltIn("builtin-delete", "del", "Snippet.BuiltinDelete", "Snippet.BuiltinDeleteDescription", "DELETE FROM table_name\r\nWHERE $CURSOR$;"),
                BuiltIn("builtin-cte", "cte", "Snippet.BuiltinCte", "Snippet.BuiltinCteDescription", "WITH source_data AS (\r\n    SELECT $CURSOR$\r\n    FROM table_name\r\n)\r\nSELECT *\r\nFROM source_data;"),
                BuiltIn("builtin-transaction", "tran", "Snippet.BuiltinTransaction", "Snippet.BuiltinTransactionDescription", "BEGIN;\r\n\r\n$CURSOR$\r\n\r\nCOMMIT;"),
                BuiltIn("builtin-create-table", "ct", "Snippet.BuiltinCreateTable", "Snippet.BuiltinCreateTableDescription", "CREATE TABLE table_name (\r\n    id INTEGER NOT NULL,\r\n    $CURSOR$\r\n    PRIMARY KEY (id)\r\n);"),
            };
        }

        private static SqlCodeSnippet BuiltIn(string id, string shortcut, string nameKey, string descriptionKey, string sql)
        {
            return new SqlCodeSnippet
            {
                Id = id,
                Shortcut = shortcut,
                Name = Localization.T(nameKey),
                Description = Localization.T(descriptionKey),
                Sql = sql,
                IsBuiltIn = true
            };
        }

        private static List<SqlCodeSnippet> NormalizeCustom(IEnumerable<SqlCodeSnippet> snippets, bool rejectInvalid)
        {
            List<SqlCodeSnippet> output = new List<SqlCodeSnippet>();
            foreach (SqlCodeSnippet snippet in (snippets ?? Enumerable.Empty<SqlCodeSnippet>()).Take(200))
            {
                try
                {
                    SqlCodeSnippet normalized = NormalizeOne(snippet, false);
                    if (output.Any(item => string.Equals(item.Shortcut, normalized.Shortcut, StringComparison.OrdinalIgnoreCase))) continue;
                    output.Add(normalized);
                }
                catch
                {
                    if (rejectInvalid) throw;
                }
            }
            return output;
        }

        private static SqlCodeSnippet NormalizeOne(SqlCodeSnippet snippet, bool createId)
        {
            if (snippet == null) throw new InvalidOperationException(Localization.T("Snippet.Invalid"));
            string name = (snippet.Name ?? string.Empty).Trim();
            string shortcut = (snippet.Shortcut ?? string.Empty).Trim();
            string sql = snippet.Sql ?? string.Empty;
            if (name.Length == 0 || name.Length > 100 ||
                !Regex.IsMatch(shortcut, @"^[A-Za-z][A-Za-z0-9_-]{0,31}$") ||
                string.IsNullOrWhiteSpace(sql) || sql.Length > 200000)
            {
                throw new InvalidOperationException(Localization.T("Snippet.Invalid"));
            }
            return new SqlCodeSnippet
            {
                Id = string.IsNullOrWhiteSpace(snippet.Id) || createId && snippet.IsBuiltIn ? Guid.NewGuid().ToString("N") : snippet.Id.Trim(),
                Name = name,
                Shortcut = shortcut,
                Description = (snippet.Description ?? string.Empty).Trim(),
                Sql = sql,
                IsBuiltIn = false
            };
        }

        private void SaveCustom(IEnumerable<SqlCodeSnippet> snippets)
        {
            WriteJson(_path, NormalizeCustom(snippets, true));
        }

        private static void WriteJson(string path, object value)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, JsonConvert.SerializeObject(value, Formatting.Indented), new UTF8Encoding(false));
            try
            {
                if (File.Exists(path)) File.Replace(temp, path, null, true);
                else File.Move(temp, path);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
    }

    public sealed class SqlCompletionMetadataEntry
    {
        public string Provider { get; set; }
        public string Database { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public List<string> Tables { get; set; }
        public Dictionary<string, List<string>> Columns { get; set; }

        public SqlCompletionMetadataEntry()
        {
            Tables = new List<string>();
            Columns = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public sealed class SqlCompletionMetadataStore
    {
        private sealed class CacheFile
        {
            public int Version { get; set; }
            public Dictionary<string, SqlCompletionMetadataEntry> Databases { get; set; }

            public CacheFile()
            {
                Version = 1;
                Databases = new Dictionary<string, SqlCompletionMetadataEntry>();
            }
        }

        private readonly string _path;

        public SqlCompletionMetadataStore(string path)
        {
            _path = path;
        }

        public SqlCompletionMetadataEntry Load(string provider, string database)
        {
            CacheFile file = Read();
            SqlCompletionMetadataEntry entry;
            if (!file.Databases.TryGetValue(Key(provider, database), out entry) || entry == null)
            {
                return NewEntry(provider, database);
            }
            entry.Provider = provider ?? string.Empty;
            entry.Database = database ?? string.Empty;
            entry.Tables = (entry.Tables ?? new List<string>()).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Take(5000).ToList();
            entry.Columns = NormalizeColumns(entry.Columns);
            return entry;
        }

        public void Save(SqlCompletionMetadataEntry entry)
        {
            if (entry == null) return;
            entry.UpdatedUtc = DateTime.UtcNow;
            entry.Tables = (entry.Tables ?? new List<string>()).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Take(5000).ToList();
            entry.Columns = NormalizeColumns(entry.Columns);
            CacheFile file = Read();
            file.Databases[Key(entry.Provider, entry.Database)] = entry;
            string directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(_path, JsonConvert.SerializeObject(file, Formatting.Indented), new UTF8Encoding(false));
        }

        private CacheFile Read()
        {
            try
            {
                if (!File.Exists(_path)) return new CacheFile();
                CacheFile file = JsonConvert.DeserializeObject<CacheFile>(File.ReadAllText(_path, Encoding.UTF8));
                if (file == null || file.Version != 1) return new CacheFile();
                if (file.Databases == null) file.Databases = new Dictionary<string, SqlCompletionMetadataEntry>();
                return file;
            }
            catch
            {
                return new CacheFile();
            }
        }

        private static SqlCompletionMetadataEntry NewEntry(string provider, string database)
        {
            return new SqlCompletionMetadataEntry { Provider = provider ?? string.Empty, Database = database ?? string.Empty };
        }

        private static string Key(string provider, string database)
        {
            return (provider ?? string.Empty).Trim().ToLowerInvariant() + "\n" + (database ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static Dictionary<string, List<string>> NormalizeColumns(IDictionary<string, List<string>> source)
        {
            Dictionary<string, List<string>> output = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (source == null) return output;
            foreach (KeyValuePair<string, List<string>> item in source.Take(5000))
            {
                if (string.IsNullOrWhiteSpace(item.Key)) continue;
                output[item.Key.Trim()] = (item.Value ?? new List<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(2000)
                    .ToList();
            }
            return output;
        }
    }
}
