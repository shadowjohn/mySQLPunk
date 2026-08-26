using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace mySQLPunk.lib
{
    public sealed class TableDataProfile
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string FilterExpression { get; set; }
        public string SortColumn { get; set; }
        public bool SortDescending { get; set; }
        public List<string> VisibleColumns { get; set; }

        public TableDataProfile()
        {
            VisibleColumns = new List<string>();
        }

        public TableDataProfile Clone()
        {
            return new TableDataProfile
            {
                Id = Id,
                Name = Name,
                FilterExpression = FilterExpression,
                SortColumn = SortColumn,
                SortDescending = SortDescending,
                VisibleColumns = new List<string>(VisibleColumns ?? new List<string>())
            };
        }
    }

    internal sealed class TableDataProfileSet
    {
        public string Provider { get; set; }
        public string Database { get; set; }
        public string Table { get; set; }
        public string ActiveProfileId { get; set; }
        public List<TableDataProfile> Profiles { get; set; }

        public TableDataProfileSet()
        {
            Profiles = new List<TableDataProfile>();
        }
    }

    internal sealed class TableDataProfileFile
    {
        public int Version { get; set; }
        public List<TableDataProfileSet> Tables { get; set; }

        public TableDataProfileFile()
        {
            Version = 1;
            Tables = new List<TableDataProfileSet>();
        }
    }

    /// <summary>
    /// Persists named table-data views. The file contains display preferences and
    /// SQL filter text only; connection passwords and result data are never stored.
    /// </summary>
    public sealed class TableDataProfileStore
    {
        private const int MaxTables = 500;
        private const int MaxProfilesPerTable = 50;
        private readonly string _path;

        public TableDataProfileStore(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A profile path is required.", "path");
            _path = path;
        }

        public List<TableDataProfile> GetProfiles(string provider, string database, string table)
        {
            TableDataProfileSet set = FindSet(Read(false), provider, database, table);
            if (set == null) return new List<TableDataProfile>();
            return set.Profiles
                .Where(profile => profile != null)
                .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
                .Select(profile => profile.Clone())
                .ToList();
        }

        public TableDataProfile GetActiveProfile(string provider, string database, string table)
        {
            TableDataProfileSet set = FindSet(Read(false), provider, database, table);
            if (set == null || string.IsNullOrWhiteSpace(set.ActiveProfileId)) return null;
            TableDataProfile profile = set.Profiles.FirstOrDefault(item =>
                item != null && string.Equals(item.Id, set.ActiveProfileId, StringComparison.OrdinalIgnoreCase));
            return profile == null ? null : profile.Clone();
        }

        public TableDataProfile Save(
            string provider,
            string database,
            string table,
            TableDataProfile profile,
            IEnumerable<string> availableColumns)
        {
            List<string> columns = NormalizeColumns(availableColumns);
            TableDataProfile normalized = NormalizeProfile(profile, columns, true);
            TableDataProfileFile file = Read(true);
            TableDataProfileSet set = FindSet(file, provider, database, table);
            if (set == null)
            {
                if (file.Tables.Count >= MaxTables) throw new InvalidOperationException(Localization.T("TableProfile.TooManyTables"));
                set = NewSet(provider, database, table);
                file.Tables.Add(set);
            }

            TableDataProfile duplicate = set.Profiles.FirstOrDefault(item =>
                item != null &&
                !string.Equals(item.Id, normalized.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Name, normalized.Name, StringComparison.OrdinalIgnoreCase));
            if (duplicate != null)
            {
                throw new InvalidOperationException(Localization.Format("TableProfile.DuplicateName", normalized.Name));
            }

            int index = set.Profiles.FindIndex(item => item != null && string.Equals(item.Id, normalized.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                set.Profiles[index] = normalized;
            }
            else
            {
                if (set.Profiles.Count >= MaxProfilesPerTable) throw new InvalidOperationException(Localization.T("TableProfile.TooManyProfiles"));
                set.Profiles.Add(normalized);
            }

            Write(file);
            return normalized.Clone();
        }

        public void Delete(string provider, string database, string table, string profileId)
        {
            if (string.IsNullOrWhiteSpace(profileId)) return;
            TableDataProfileFile file = Read(true);
            TableDataProfileSet set = FindSet(file, provider, database, table);
            if (set == null) return;
            set.Profiles.RemoveAll(item => item != null && string.Equals(item.Id, profileId, StringComparison.OrdinalIgnoreCase));
            if (string.Equals(set.ActiveProfileId, profileId, StringComparison.OrdinalIgnoreCase)) set.ActiveProfileId = string.Empty;
            if (set.Profiles.Count == 0) file.Tables.Remove(set);
            Write(file);
        }

        public void SetActive(string provider, string database, string table, string profileId)
        {
            TableDataProfileFile file = Read(true);
            TableDataProfileSet set = FindSet(file, provider, database, table);
            if (set == null)
            {
                if (string.IsNullOrWhiteSpace(profileId)) return;
                throw new InvalidOperationException(Localization.T("TableProfile.NotFound"));
            }

            if (string.IsNullOrWhiteSpace(profileId))
            {
                set.ActiveProfileId = string.Empty;
            }
            else if (set.Profiles.Any(item => item != null && string.Equals(item.Id, profileId, StringComparison.OrdinalIgnoreCase)))
            {
                set.ActiveProfileId = profileId;
            }
            else
            {
                throw new InvalidOperationException(Localization.T("TableProfile.NotFound"));
            }
            Write(file);
        }

        public static TableDataProfile NormalizeForColumns(TableDataProfile profile, IEnumerable<string> availableColumns)
        {
            return NormalizeProfile(profile, NormalizeColumns(availableColumns), false);
        }

        public static string NormalizeFilterExpression(string value)
        {
            string filter = (value ?? string.Empty).Trim();
            filter = Regex.Replace(filter, @"^WHERE\b\s*", string.Empty, RegexOptions.IgnoreCase).Trim();
            if (filter.Length > 4000 || filter.Any(character => char.IsControl(character) && !char.IsWhiteSpace(character)))
            {
                throw new InvalidOperationException(Localization.T("TableProfile.InvalidFilter"));
            }
            if (filter.Length == 0) return string.Empty;
            if (ContainsUnsafeSqlBoundary(filter) || ContainsForbiddenTopLevelClause(filter))
            {
                throw new InvalidOperationException(Localization.T("TableProfile.InvalidFilter"));
            }
            return filter;
        }

        private static bool ContainsUnsafeSqlBoundary(string sql)
        {
            bool single = false;
            bool quoted = false;
            bool bracket = false;
            bool backtick = false;
            for (int index = 0; index < sql.Length; index++)
            {
                char current = sql[index];
                char next = index + 1 < sql.Length ? sql[index + 1] : '\0';
                if (single)
                {
                    if (current == '\'' && next == '\'') index++;
                    else if (current == '\'') single = false;
                    continue;
                }
                if (quoted)
                {
                    if (current == '"' && next == '"') index++;
                    else if (current == '"') quoted = false;
                    continue;
                }
                if (bracket)
                {
                    if (current == ']' && next == ']') index++;
                    else if (current == ']') bracket = false;
                    continue;
                }
                if (backtick)
                {
                    if (current == '`' && next == '`') index++;
                    else if (current == '`') backtick = false;
                    continue;
                }

                if (current == '\'') single = true;
                else if (current == '"') quoted = true;
                else if (current == '[') bracket = true;
                else if (current == '`') backtick = true;
                else if (current == ';' || current == '#' || current == '-' && next == '-' || current == '/' && next == '*') return true;
            }
            return single || quoted || bracket || backtick;
        }

        private static bool ContainsForbiddenTopLevelClause(string sql)
        {
            StringBuilder topLevel = new StringBuilder(sql.Length);
            bool single = false;
            bool quoted = false;
            bool bracket = false;
            bool backtick = false;
            int depth = 0;
            for (int index = 0; index < sql.Length; index++)
            {
                char current = sql[index];
                char next = index + 1 < sql.Length ? sql[index + 1] : '\0';
                if (single)
                {
                    if (current == '\'' && next == '\'') index++;
                    else if (current == '\'') single = false;
                    topLevel.Append(' ');
                    continue;
                }
                if (quoted)
                {
                    if (current == '"' && next == '"') index++;
                    else if (current == '"') quoted = false;
                    topLevel.Append(' ');
                    continue;
                }
                if (bracket)
                {
                    if (current == ']' && next == ']') index++;
                    else if (current == ']') bracket = false;
                    topLevel.Append(' ');
                    continue;
                }
                if (backtick)
                {
                    if (current == '`' && next == '`') index++;
                    else if (current == '`') backtick = false;
                    topLevel.Append(' ');
                    continue;
                }

                if (current == '\'') { single = true; topLevel.Append(' '); }
                else if (current == '"') { quoted = true; topLevel.Append(' '); }
                else if (current == '[') { bracket = true; topLevel.Append(' '); }
                else if (current == '`') { backtick = true; topLevel.Append(' '); }
                else if (current == '(') { depth++; topLevel.Append(' '); }
                else if (current == ')') { depth = Math.Max(0, depth - 1); topLevel.Append(' '); }
                else topLevel.Append(depth == 0 ? current : ' ');
            }

            return Regex.IsMatch(
                topLevel.ToString(),
                @"\b(ORDER\s+BY|GROUP\s+BY|HAVING|LIMIT|OFFSET|FETCH|UNION|INTERSECT|EXCEPT)\b",
                RegexOptions.IgnoreCase);
        }

        private static TableDataProfile NormalizeProfile(TableDataProfile profile, List<string> availableColumns, bool requireVisibleColumn)
        {
            if (profile == null) throw new InvalidOperationException(Localization.T("TableProfile.Invalid"));
            string name = (profile.Name ?? string.Empty).Trim();
            if (name.Length == 0 || name.Length > 80 || name.Any(char.IsControl))
            {
                throw new InvalidOperationException(Localization.T("TableProfile.Invalid"));
            }

            string sortColumn = MatchColumn(profile.SortColumn, availableColumns);
            if (requireVisibleColumn && !string.IsNullOrWhiteSpace(profile.SortColumn) && string.IsNullOrWhiteSpace(sortColumn))
            {
                throw new InvalidOperationException(Localization.T("TableProfile.InvalidSortColumn"));
            }

            List<string> visibleColumns = (profile.VisibleColumns ?? new List<string>())
                .Select(value => MatchColumn(value, availableColumns))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2000)
                .ToList();
            if (requireVisibleColumn && availableColumns.Count > 0 && visibleColumns.Count == 0)
            {
                throw new InvalidOperationException(Localization.T("TableProfile.VisibleColumnRequired"));
            }

            string profileId = (profile.Id ?? string.Empty).Trim();
            if (!Regex.IsMatch(profileId, "^[A-Fa-f0-9]{32}$")) profileId = Guid.NewGuid().ToString("N");
            return new TableDataProfile
            {
                Id = profileId,
                Name = name,
                FilterExpression = NormalizeFilterExpression(profile.FilterExpression),
                SortColumn = sortColumn,
                SortDescending = profile.SortDescending,
                VisibleColumns = visibleColumns
            };
        }

        private static List<string> NormalizeColumns(IEnumerable<string> columns)
        {
            return (columns ?? Enumerable.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2000)
                .ToList();
        }

        private static string MatchColumn(string value, IEnumerable<string> columns)
        {
            string candidate = (value ?? string.Empty).Trim();
            if (candidate.Length == 0) return string.Empty;
            return (columns ?? Enumerable.Empty<string>()).FirstOrDefault(column =>
                string.Equals(column, candidate, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        }

        private TableDataProfileFile Read(bool preserveCorruptFile)
        {
            try
            {
                if (!File.Exists(_path)) return new TableDataProfileFile();
                TableDataProfileFile file = JsonConvert.DeserializeObject<TableDataProfileFile>(File.ReadAllText(_path, Encoding.UTF8));
                if (file == null || file.Version != 1) throw new InvalidDataException("Unsupported table profile file.");
                file.Tables = (file.Tables ?? new List<TableDataProfileSet>())
                    .Where(set => set != null && !string.IsNullOrWhiteSpace(set.Table))
                    .Take(MaxTables)
                    .ToList();
                foreach (TableDataProfileSet set in file.Tables)
                {
                    set.Profiles = (set.Profiles ?? new List<TableDataProfile>())
                        .Where(profile => profile != null && !string.IsNullOrWhiteSpace(profile.Id) && !string.IsNullOrWhiteSpace(profile.Name))
                        .Take(MaxProfilesPerTable)
                        .ToList();
                }
                return file;
            }
            catch
            {
                if (preserveCorruptFile) PreserveCorruptFile();
                return new TableDataProfileFile();
            }
        }

        private void PreserveCorruptFile()
        {
            if (!File.Exists(_path)) return;
            string backup = _path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".json";
            File.Move(_path, backup);
        }

        private void Write(TableDataProfileFile file)
        {
            string directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            string temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, JsonConvert.SerializeObject(file, Formatting.Indented), new UTF8Encoding(false));
            try
            {
                if (File.Exists(_path)) File.Replace(temp, _path, null, true);
                else File.Move(temp, _path);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        private static TableDataProfileSet FindSet(TableDataProfileFile file, string provider, string database, string table)
        {
            if (file == null) return null;
            return file.Tables.FirstOrDefault(set =>
                set != null &&
                string.Equals(set.Provider ?? string.Empty, provider ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(set.Database ?? string.Empty, database ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(set.Table ?? string.Empty, table ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        }

        private static TableDataProfileSet NewSet(string provider, string database, string table)
        {
            return new TableDataProfileSet
            {
                Provider = (provider ?? string.Empty).Trim(),
                Database = (database ?? string.Empty).Trim(),
                Table = (table ?? string.Empty).Trim(),
                ActiveProfileId = string.Empty
            };
        }
    }

    public static class TableDataProfileSqlBuilder
    {
        public static string ExtractTableName(string provider, string sql)
        {
            if (string.IsNullOrWhiteSpace(sql)) return string.Empty;
            Match match = Regex.Match(sql, @"\bFROM\s+([`\[\]\w\.\x22]+)", RegexOptions.IgnoreCase);
            if (!match.Success) return string.Empty;
            string path = match.Groups[1].Value
                .Replace("`", string.Empty)
                .Replace("\"", string.Empty)
                .Replace("[", string.Empty)
                .Replace("]", string.Empty);
            string[] parts = path.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return string.Empty;
            string normalizedProvider = NormalizeProvider(provider);
            if ((normalizedProvider == "postgresql" || normalizedProvider == "mssql") && parts.Length >= 2)
            {
                return parts[parts.Length - 2] + "." + parts[parts.Length - 1];
            }
            return parts[parts.Length - 1];
        }

        public static string BuildBaseSql(
            string provider,
            string qualifiedTable,
            TableDataProfile profile,
            IEnumerable<string> availableColumns)
        {
            TableDataProfile normalized = TableDataProfileStore.NormalizeForColumns(profile, availableColumns);
            string sql = "SELECT * FROM " + qualifiedTable;
            if (!string.IsNullOrWhiteSpace(normalized.FilterExpression)) sql += " WHERE " + normalized.FilterExpression;
            if (!string.IsNullOrWhiteSpace(normalized.SortColumn))
            {
                sql += " ORDER BY " + QuoteIdentifier(provider, normalized.SortColumn) + (normalized.SortDescending ? " DESC" : " ASC");
            }
            return TerminateSql(provider, sql);
        }

        public static string BuildCountSql(string provider, string qualifiedTable, TableDataProfile profile)
        {
            string filter = TableDataProfileStore.NormalizeFilterExpression(profile == null ? string.Empty : profile.FilterExpression);
            string sql = "SELECT COUNT(*) FROM " + qualifiedTable + (filter.Length == 0 ? string.Empty : " WHERE " + filter);
            return TerminateSql(provider, sql);
        }

        public static string BuildPageSql(
            string provider,
            string qualifiedTable,
            TableDataProfile profile,
            IEnumerable<string> availableColumns,
            IEnumerable<string> fallbackSortColumns,
            long offset,
            int limit)
        {
            if (offset < 0 || limit < 1 || limit > 1000000) throw new ArgumentOutOfRangeException("limit");
            List<string> columns = (availableColumns ?? Enumerable.Empty<string>()).ToList();
            TableDataProfile normalized = TableDataProfileStore.NormalizeForColumns(profile, columns);
            string sql = "SELECT * FROM " + qualifiedTable;
            if (!string.IsNullOrWhiteSpace(normalized.FilterExpression)) sql += " WHERE " + normalized.FilterExpression;

            List<string> orderParts = new List<string>();
            HashSet<string> orderedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(normalized.SortColumn))
            {
                orderParts.Add(QuoteIdentifier(provider, normalized.SortColumn) + (normalized.SortDescending ? " DESC" : " ASC"));
                orderedColumns.Add(normalized.SortColumn);
            }
            foreach (string fallback in fallbackSortColumns ?? Enumerable.Empty<string>())
            {
                string matched = columns.FirstOrDefault(column => string.Equals(column, fallback, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(matched) || !orderedColumns.Add(matched)) continue;
                orderParts.Add(QuoteIdentifier(provider, matched) + " ASC");
            }

            string normalizedProvider = NormalizeProvider(provider);
            if (orderParts.Count > 0)
            {
                sql += " ORDER BY " + string.Join(", ", orderParts.ToArray());
            }
            else if (normalizedProvider == "mssql")
            {
                sql += " ORDER BY (SELECT NULL)";
            }

            if (normalizedProvider == "mssql" || normalizedProvider == "oracle")
            {
                return TerminateSql(provider, sql + " OFFSET " + offset + " ROWS FETCH NEXT " + limit + " ROWS ONLY");
            }
            return TerminateSql(provider, sql + " LIMIT " + limit + " OFFSET " + offset);
        }

        public static string QuoteIdentifier(string provider, string identifier)
        {
            string value = identifier ?? string.Empty;
            switch (NormalizeProvider(provider))
            {
                case "mysql":
                    return "`" + value.Replace("`", "``") + "`";
                case "mssql":
                    return "[" + value.Replace("]", "]]") + "]";
                default:
                    return "\"" + value.Replace("\"", "\"\"") + "\"";
            }
        }

        private static string NormalizeProvider(string provider)
        {
            string value = (provider ?? string.Empty).Trim().ToLowerInvariant();
            if (value == "sqlserver" || value == "sql server") return "mssql";
            if (value == "postgres" || value == "postgresql") return "postgresql";
            return value;
        }

        private static string TerminateSql(string provider, string sql)
        {
            return NormalizeProvider(provider) == "oracle" ? sql : sql + ";";
        }
    }
}
