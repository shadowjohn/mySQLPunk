using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace mySQLPunk.lib
{
    /// <summary>模型中的函式或預存程序；Definition 為完整的 CREATE 敘述。</summary>
    public sealed class ErModelRoutine
    {
        /// <summary>識別名稱：MySQL 為名稱，SQL Server 為 schema.name，PostgreSQL 為含參數型別的簽章（區分多載）。</summary>
        public string Name { get; set; }
        /// <summary>Function 或 Procedure。</summary>
        public string Kind { get; set; }
        public string ReturnType { get; set; }
        public string Definition { get; set; }
    }

    public enum RoutineChangeKind
    {
        Create,
        Replace,
        Drop
    }

    public sealed class RoutineChange
    {
        public RoutineChangeKind Change { get; set; }
        public string Name { get; set; }
        public string Kind { get; set; }
        public string Statement { get; set; }
        /// <summary>刪除（或 MySQL 需先刪除再建立的取代）需要確認，預設不執行。</summary>
        public bool Destructive { get; set; }
        public string Description { get; set; }
    }

    /// <summary>
    /// 函式／預存程序的讀取與模型比較：讀取 MySQL、PostgreSQL、SQL Server、Oracle 的定義，
    /// 並產生讓資料庫跟上模型的語句（新增、取代、刪除），交給結構同步視窗逐句審核執行。
    /// </summary>
    public static class RoutineModelService
    {
        public const int MaximumDefinitionLength = 1024 * 1024;
        public const string FunctionKind = "Function";
        public const string ProcedureKind = "Procedure";
        private static readonly Regex MySqlDefiner = new Regex(@"\bDEFINER\s*=\s*(`[^`]*`|'[^']*'|[^\s@]+)@(`[^`]*`|'[^']*'|[^\s]+)\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex SqlServerCreate = new Regex(@"\bCREATE\s+(OR\s+ALTER\s+)?(PROC(EDURE)?|FUNCTION)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool SupportsSync(string providerName)
        {
            string provider = SchemaSyncScriptService.NormalizeProvider(providerName);
            return provider == "mysql" || provider == "postgresql" || provider == "mssql";
        }

        /// <summary>讀取資料庫中的函式與預存程序；SQLite 沒有這類物件，回傳空清單。</summary>
        public static List<ErModelRoutine> Load(IDatabase database, string databaseName)
        {
            string provider = SchemaSyncScriptService.NormalizeProvider(database.ProviderName);
            List<ErModelRoutine> routines = new List<ErModelRoutine>();
            if (provider == "mysql")
            {
                DataTable list = Select(database, "SELECT ROUTINE_NAME, ROUTINE_TYPE, COALESCE(DATA_TYPE, '') FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = '" +
                    Literal(databaseName) + "' ORDER BY ROUTINE_TYPE, ROUTINE_NAME");
                foreach (DataRow row in list.Rows)
                {
                    string name = Convert.ToString(row[0], CultureInfo.InvariantCulture);
                    string kind = NormalizeKind(Convert.ToString(row[1], CultureInfo.InvariantCulture));
                    DataTable create = Select(database, "SHOW CREATE " + (kind == ProcedureKind ? "PROCEDURE " : "FUNCTION ") +
                        "`" + (databaseName ?? string.Empty).Replace("`", "``") + "`.`" + name.Replace("`", "``") + "`");
                    string definition = string.Empty;
                    if (create.Rows.Count > 0)
                    {
                        DataColumn column = create.Columns.Cast<DataColumn>().FirstOrDefault(item => item.ColumnName.StartsWith("Create ", StringComparison.OrdinalIgnoreCase) &&
                            (item.ColumnName.EndsWith("Function", StringComparison.OrdinalIgnoreCase) || item.ColumnName.EndsWith("Procedure", StringComparison.OrdinalIgnoreCase)));
                        if (column != null) definition = Convert.ToString(create.Rows[0][column], CultureInfo.InvariantCulture);
                    }
                    routines.Add(new ErModelRoutine { Name = name, Kind = kind, ReturnType = Convert.ToString(row[2], CultureInfo.InvariantCulture), Definition = definition });
                }
            }
            else if (provider == "postgresql")
            {
                DataTable list = Select(database,
                    "SELECT CASE WHEN n.nspname = 'public' THEN regexp_replace(p.oid::regprocedure::text, '^public\\.', '') ELSE p.oid::regprocedure::text END, " +
                    "CASE WHEN p.prokind = 'p' THEN 'Procedure' ELSE 'Function' END, COALESCE(pg_catalog.pg_get_function_result(p.oid), ''), pg_catalog.pg_get_functiondef(p.oid) " +
                    "FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace " +
                    "WHERE n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg\\_%' AND p.prokind IN ('f', 'p') " +
                    "AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_depend d WHERE d.classid = 'pg_catalog.pg_proc'::regclass AND d.objid = p.oid AND d.deptype = 'e') " +
                    "ORDER BY 1");
                foreach (DataRow row in list.Rows)
                {
                    routines.Add(new ErModelRoutine
                    {
                        Name = Convert.ToString(row[0], CultureInfo.InvariantCulture),
                        Kind = Convert.ToString(row[1], CultureInfo.InvariantCulture),
                        ReturnType = Convert.ToString(row[2], CultureInfo.InvariantCulture),
                        Definition = Convert.ToString(row[3], CultureInfo.InvariantCulture)
                    });
                }
            }
            else if (provider == "mssql")
            {
                string db = "[" + (databaseName ?? string.Empty).Replace("]", "]]") + "]";
                DataTable list = Select(database,
                    "SELECT s.name + '.' + o.name, CASE WHEN o.type = 'P' THEN 'Procedure' ELSE 'Function' END, COALESCE(m.definition, '') " +
                    "FROM " + db + ".sys.objects o JOIN " + db + ".sys.schemas s ON s.schema_id = o.schema_id " +
                    "LEFT JOIN " + db + ".sys.sql_modules m ON m.object_id = o.object_id " +
                    "WHERE o.type IN ('FN','IF','TF','P') AND o.is_ms_shipped = 0 ORDER BY 1");
                foreach (DataRow row in list.Rows)
                {
                    routines.Add(new ErModelRoutine
                    {
                        Name = Convert.ToString(row[0], CultureInfo.InvariantCulture),
                        Kind = Convert.ToString(row[1], CultureInfo.InvariantCulture),
                        ReturnType = string.Empty,
                        Definition = Convert.ToString(row[2], CultureInfo.InvariantCulture)
                    });
                }
            }
            else if (provider == "oracle")
            {
                string owner = (databaseName ?? string.Empty).ToUpperInvariant().Replace("'", "''");
                DataTable list = Select(database,
                    "SELECT o.OBJECT_NAME, CASE WHEN o.OBJECT_TYPE = 'PROCEDURE' THEN 'Procedure' ELSE 'Function' END, " +
                    "(SELECT RTRIM(XMLAGG(XMLELEMENT(e, s.TEXT) ORDER BY s.LINE).EXTRACT('//text()').GETCLOBVAL()) FROM ALL_SOURCE s " +
                    "WHERE s.OWNER = o.OWNER AND s.NAME = o.OBJECT_NAME AND s.TYPE = o.OBJECT_TYPE) " +
                    "FROM ALL_OBJECTS o WHERE o.OWNER = '" + owner + "' AND o.OBJECT_TYPE IN ('FUNCTION','PROCEDURE') ORDER BY 1");
                foreach (DataRow row in list.Rows)
                {
                    string source = Convert.ToString(row[2], CultureInfo.InvariantCulture);
                    routines.Add(new ErModelRoutine
                    {
                        Name = Convert.ToString(row[0], CultureInfo.InvariantCulture),
                        Kind = Convert.ToString(row[1], CultureInfo.InvariantCulture),
                        ReturnType = string.Empty,
                        Definition = source.Length == 0 ? string.Empty : "CREATE OR REPLACE " + source.TrimStart()
                    });
                }
            }
            return routines;
        }

        public static void Validate(IList<ErModelRoutine> routines)
        {
            HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ErModelRoutine routine in routines)
            {
                if (routine == null) throw new InvalidOperationException(Localization.T("ErModel.Error.RoutineName"));
                routine.Name = (routine.Name ?? string.Empty).Trim();
                if (routine.Name.Length == 0 || routine.Name.Length > 512 || routine.Name.Any(char.IsControl))
                {
                    throw new InvalidOperationException(Localization.T("ErModel.Error.RoutineName"));
                }
                routine.Kind = NormalizeKind(routine.Kind);
                if (!keys.Add(routine.Kind + "\u0001" + routine.Name)) throw new InvalidOperationException(Localization.Format("ErModel.Error.DuplicateRoutine", routine.Name));
                routine.ReturnType = routine.ReturnType ?? string.Empty;
                routine.Definition = routine.Definition ?? string.Empty;
                if (routine.Definition.Trim().Length == 0) throw new InvalidOperationException(Localization.Format("ErModel.Error.RoutineDefinition", routine.Name));
                if (routine.Definition.Length > MaximumDefinitionLength) throw new InvalidOperationException(Localization.Format("ErModel.Error.RoutineTooLong", routine.Name));
            }
        }

        public static string NormalizeKind(string kind)
        {
            return string.Equals((kind ?? string.Empty).Trim(), "procedure", StringComparison.OrdinalIgnoreCase) ? ProcedureKind : FunctionKind;
        }

        /// <summary>
        /// 比較用的定義：伺服器回傳的定義會重新排版（MySQL 的 SHOW CREATE、PostgreSQL 的 pg_get_functiondef），
        /// 所以字串常值以外的空白收斂、關鍵字不分大小寫、標點旁的空白移除；PostgreSQL 的 $tag$ 一律視為 $$；
        /// MySQL 另移除 DEFINER 子句（重建時以目前帳號為定義者）。字串常值與 $$ 本體內容保持原樣比較。
        /// </summary>
        public static string NormalizeDefinition(string provider, string definition)
        {
            string text = (definition ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            string normalizedProvider = SchemaSyncScriptService.NormalizeProvider(provider);
            if (normalizedProvider == "mysql") text = MySqlDefiner.Replace(text, string.Empty);
            StringBuilder output = new StringBuilder();
            bool pendingSpace = false;
            int index = 0;
            while (index < text.Length)
            {
                char c = text[index];
                if (c == '\'')
                {
                    int end = index + 1;
                    while (end < text.Length)
                    {
                        if (text[end] == '\'' && end + 1 < text.Length && text[end + 1] == '\'') { end += 2; continue; }
                        if (text[end] == '\'') break;
                        end++;
                    }
                    AppendToken(output, ref pendingSpace, text.Substring(index, Math.Min(text.Length, end + 1) - index));
                    index = end + 1;
                    continue;
                }
                if (c == '$' && normalizedProvider == "postgresql")
                {
                    int close = text.IndexOf('$', index + 1);
                    string tag = close > index ? text.Substring(index, close - index + 1) : null;
                    if (tag != null && tag.Skip(1).Take(tag.Length - 2).All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
                    {
                        int end = text.IndexOf(tag, close + 1, StringComparison.Ordinal);
                        if (end < 0) end = text.Length;
                        string body = text.Substring(close + 1, end - close - 1).Trim();
                        AppendToken(output, ref pendingSpace, "$$" + body + "$$");
                        index = Math.Min(text.Length, end + tag.Length);
                        continue;
                    }
                }
                if (char.IsWhiteSpace(c))
                {
                    pendingSpace = output.Length > 0;
                    index++;
                    continue;
                }
                if ("(),;=".IndexOf(c) >= 0)
                {
                    pendingSpace = false;
                    output.Append(c);
                    index++;
                    continue;
                }
                AppendToken(output, ref pendingSpace, char.ToLowerInvariant(c).ToString());
                index++;
            }
            return output.ToString().TrimEnd(';', ' ');
        }

        private static void AppendToken(StringBuilder output, ref bool pendingSpace, string token)
        {
            if (pendingSpace && output.Length > 0 && "(),;=".IndexOf(output[output.Length - 1]) < 0) output.Append(' ');
            pendingSpace = false;
            output.Append(token);
        }

        /// <summary>讓資料庫跟上模型的函式／程序變更。</summary>
        public static List<RoutineChange> Compare(string providerName, IList<ErModelRoutine> model, IList<ErModelRoutine> database)
        {
            string provider = SchemaSyncScriptService.NormalizeProvider(providerName);
            List<RoutineChange> changes = new List<RoutineChange>();
            if (!SupportsSync(provider)) return changes;
            Dictionary<string, ErModelRoutine> live = (database ?? new List<ErModelRoutine>())
                .GroupBy(Key, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            HashSet<string> wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ErModelRoutine routine in model ?? new List<ErModelRoutine>())
            {
                string key = Key(routine);
                wanted.Add(key);
                ErModelRoutine current;
                string createSql = CreateStatement(provider, routine.Definition);
                if (!live.TryGetValue(key, out current))
                {
                    changes.Add(new RoutineChange
                    {
                        Change = RoutineChangeKind.Create, Name = routine.Name, Kind = routine.Kind, Statement = createSql,
                        Description = Localization.Format("ErModel.Routine.Create", KindText(routine.Kind), routine.Name)
                    });
                    continue;
                }
                if (NormalizeDefinition(provider, current.Definition) == NormalizeDefinition(provider, routine.Definition)) continue;
                if (provider == "postgresql")
                {
                    // pg_get_functiondef 產生 CREATE OR REPLACE，可直接取代而不刪除。
                    changes.Add(new RoutineChange
                    {
                        Change = RoutineChangeKind.Replace, Name = routine.Name, Kind = routine.Kind, Statement = createSql,
                        Description = Localization.Format("ErModel.Routine.Replace", KindText(routine.Kind), routine.Name)
                    });
                }
                else if (provider == "mssql")
                {
                    changes.Add(new RoutineChange
                    {
                        Change = RoutineChangeKind.Replace, Name = routine.Name, Kind = routine.Kind, Statement = SqlServerCreate.Replace(createSql, match => "ALTER " + match.Groups[2].Value, 1),
                        Description = Localization.Format("ErModel.Routine.Replace", KindText(routine.Kind), routine.Name)
                    });
                }
                else
                {
                    // MySQL 沒有 CREATE OR REPLACE：同一個批次先刪除再建立，列為需確認的變更。
                    changes.Add(new RoutineChange
                    {
                        Change = RoutineChangeKind.Replace, Name = routine.Name, Kind = routine.Kind, Destructive = true,
                        Statement = DropStatement(provider, routine) + ";\n" + createSql,
                        Description = Localization.Format("ErModel.Routine.ReplaceByDrop", KindText(routine.Kind), routine.Name)
                    });
                }
            }
            foreach (KeyValuePair<string, ErModelRoutine> pair in live.Where(pair => !wanted.Contains(pair.Key)).OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                changes.Add(new RoutineChange
                {
                    Change = RoutineChangeKind.Drop, Name = pair.Value.Name, Kind = pair.Value.Kind, Destructive = true,
                    Statement = DropStatement(provider, pair.Value),
                    Description = Localization.Format("ErModel.Routine.Drop", KindText(pair.Value.Kind), pair.Value.Name)
                });
            }
            return changes;
        }

        /// <summary>把函式／程序的變更併進結構同步腳本（新增與可直接取代的語句預設執行，刪除類需確認）。</summary>
        public static SchemaSyncScript Merge(string providerName, SchemaSyncScript tables, IList<RoutineChange> changes)
        {
            string provider = SchemaSyncScriptService.NormalizeProvider(providerName);
            List<string> statements = tables == null ? new List<string>() : tables.Statements.ToList();
            List<string> manual = tables == null ? new List<string>() : tables.ManualItems.ToList();
            List<string> destructiveItems = tables == null ? new List<string>() : tables.DestructiveItems.ToList();
            List<string> destructiveStatements = tables == null ? new List<string>() : tables.DestructiveStatements.ToList();
            StringBuilder text = new StringBuilder(tables == null ? string.Empty : tables.Text);
            if (changes.Count > 0)
            {
                if (text.Length > 0) text.Append("\n\n");
                text.Append("-- ").Append(Localization.T("ErModel.Routine.Section")).Append('\n');
            }
            foreach (RoutineChange change in changes)
            {
                string separator = provider == "mssql" ? "\nGO\n" : provider == "mysql" ? "\n$$\n" : ";\n";
                if (change.Destructive)
                {
                    destructiveItems.Add(change.Description);
                    destructiveStatements.Add(change.Statement);
                    text.Append("-- ").Append(change.Description).Append('\n');
                    foreach (string line in change.Statement.Replace("\r\n", "\n").Split('\n')) text.Append("-- ").Append(line).Append('\n');
                }
                else
                {
                    statements.Add(change.Statement);
                    text.Append("-- ").Append(change.Description).Append('\n');
                    if (provider == "mysql") text.Append("DELIMITER $$\n");
                    text.Append(change.Statement.TrimEnd().TrimEnd(';')).Append(separator);
                    if (provider == "mysql") text.Append("DELIMITER ;\n");
                }
            }
            SchemaSyncScript merged = new SchemaSyncScript(provider, text.ToString(), statements, manual, destructiveItems);
            merged.DestructiveStatements.AddRange(destructiveStatements);
            return merged;
        }

        private static string CreateStatement(string provider, string definition)
        {
            string text = (definition ?? string.Empty).Trim();
            if (provider == "mysql") text = MySqlDefiner.Replace(text, string.Empty);
            return text.TrimEnd(';').TrimEnd();
        }

        private static string DropStatement(string provider, ErModelRoutine routine)
        {
            string kind = routine.Kind == ProcedureKind ? "PROCEDURE" : "FUNCTION";
            if (provider == "mysql") return "DROP " + kind + " IF EXISTS `" + routine.Name.Replace("`", "``") + "`";
            if (provider == "mssql")
            {
                int dot = routine.Name.IndexOf('.');
                string quoted = dot > 0
                    ? "[" + routine.Name.Substring(0, dot).Replace("]", "]]") + "].[" + routine.Name.Substring(dot + 1).Replace("]", "]]") + "]"
                    : "[" + routine.Name.Replace("]", "]]") + "]";
                return "DROP " + kind + " " + quoted;
            }
            // PostgreSQL 的名稱是 regprocedure 文字（例如 public.f(integer)），可直接用於 DROP。
            return "DROP " + kind + " " + routine.Name;
        }

        private static string Key(ErModelRoutine routine)
        {
            return NormalizeKind(routine.Kind) + "\u0001" + (routine.Name ?? string.Empty).Trim();
        }

        private static string KindText(string kind)
        {
            return Localization.T(kind == ProcedureKind ? "ErModel.Routine.Procedure" : "ErModel.Routine.Function");
        }

        private static DataTable Select(IDatabase database, string sql)
        {
            DataTable table = database.SelectSQL(sql);
            DataSyncService.ThrowIfQueryFailed(table);
            return table;
        }

        private static string Literal(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("'", "''");
        }
    }
}
