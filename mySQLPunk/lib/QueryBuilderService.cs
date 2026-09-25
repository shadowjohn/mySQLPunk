using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace mySQLPunk.lib
{
    public enum QueryJoinType
    {
        Inner,
        Left,
        Right,
        Full
    }

    public enum QueryAggregate
    {
        None,
        Count,
        CountDistinct,
        Sum,
        Avg,
        Min,
        Max
    }

    public enum QuerySort
    {
        None,
        Ascending,
        Descending
    }

    public sealed class QueryBuilderTable
    {
        public string Name { get; set; }
        public string Alias { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
    }

    public sealed class QueryBuilderColumn
    {
        public QueryBuilderColumn()
        {
            Output = true;
        }

        public string TableAlias { get; set; }
        /// <summary>欄位名稱；"*" 代表整張表。</summary>
        public string Column { get; set; }
        public string Alias { get; set; }
        public QueryAggregate Aggregate { get; set; }
        public bool Output { get; set; }
        public QuerySort Sort { get; set; }
        public bool GroupBy { get; set; }
    }

    public sealed class QueryBuilderJoin
    {
        public string LeftAlias { get; set; }
        public string LeftColumn { get; set; }
        public string RightAlias { get; set; }
        public string RightColumn { get; set; }
        public QueryJoinType Type { get; set; }
    }

    public sealed class QueryBuilderCondition
    {
        public QueryBuilderCondition()
        {
            Connector = "AND";
            Operator = "=";
            Value = string.Empty;
            Value2 = string.Empty;
        }

        public string Connector { get; set; }
        public string TableAlias { get; set; }
        public string Column { get; set; }
        /// <summary>不是 None 時條件放在 HAVING。</summary>
        public QueryAggregate Aggregate { get; set; }
        public string Operator { get; set; }
        public string Value { get; set; }
        public string Value2 { get; set; }
    }

    public sealed class QueryBuilderModel
    {
        public QueryBuilderModel()
        {
            Tables = new List<QueryBuilderTable>();
            Columns = new List<QueryBuilderColumn>();
            Joins = new List<QueryBuilderJoin>();
            Conditions = new List<QueryBuilderCondition>();
        }

        public List<QueryBuilderTable> Tables { get; private set; }
        public List<QueryBuilderColumn> Columns { get; private set; }
        public List<QueryBuilderJoin> Joins { get; private set; }
        public List<QueryBuilderCondition> Conditions { get; private set; }
        public bool Distinct { get; set; }
        public int? Limit { get; set; }

        public QueryBuilderTable FindTable(string alias)
        {
            return Tables.FirstOrDefault(table => string.Equals(table.Alias, alias, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>依表名產生不重複的別名（orders、orders2…）。</summary>
        public string NextAlias(string tableName)
        {
            string baseName = (tableName ?? "t").Split('.').Last();
            string candidate = baseName;
            int index = 2;
            while (FindTable(candidate) != null) candidate = baseName + (index++).ToString(CultureInfo.InvariantCulture);
            return candidate;
        }
    }

    /// <summary>
    /// 視覺查詢建構器的 SQL 產生與解析。產生：依 provider 引號、JOIN 依加入順序串接、聚合時自動補 GROUP BY、
    /// 筆數限制用 LIMIT／TOP／FETCH FIRST。解析：只接受建構器能表達的 SELECT 子集，其餘明確拒絕而不猜測。
    /// </summary>
    public static class QueryBuilderService
    {
        public static readonly string[] Operators = { "=", "<>", "<", "<=", ">", ">=", "LIKE", "NOT LIKE", "IN", "NOT IN", "BETWEEN", "IS NULL", "IS NOT NULL" };

        public static string BuildSql(string providerName, QueryBuilderModel model)
        {
            string provider = SchemaSyncScriptService.NormalizeProvider(providerName);
            if (model.Tables.Count == 0) return string.Empty;
            StringBuilder sql = new StringBuilder("SELECT ");
            if (model.Distinct) sql.Append("DISTINCT ");
            if (model.Limit.HasValue && provider == "mssql") sql.Append("TOP (" + model.Limit.Value.ToString(CultureInfo.InvariantCulture) + ") ");

            List<QueryBuilderColumn> output = model.Columns.Where(column => column.Output).ToList();
            List<string> items = output.Select(column => Expression(provider, column.TableAlias, column.Column, column.Aggregate) +
                                                         (string.IsNullOrWhiteSpace(column.Alias) ? string.Empty : " AS " + Quote(provider, column.Alias))).ToList();
            sql.Append(items.Count == 0 ? "*" : string.Join("," + Environment.NewLine + "       ", items));
            sql.Append(Environment.NewLine + "FROM " + TableReference(provider, model.Tables[0]));

            HashSet<string> placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { model.Tables[0].Alias };
            List<QueryBuilderJoin> pending = model.Joins.ToList();
            foreach (QueryBuilderTable table in model.Tables.Skip(1))
            {
                List<QueryBuilderJoin> links = pending.Where(join =>
                    string.Equals(join.RightAlias, table.Alias, StringComparison.OrdinalIgnoreCase) && placed.Contains(join.LeftAlias) ||
                    string.Equals(join.LeftAlias, table.Alias, StringComparison.OrdinalIgnoreCase) && placed.Contains(join.RightAlias)).ToList();
                if (links.Count == 0)
                {
                    sql.Append(Environment.NewLine + "CROSS JOIN " + TableReference(provider, table));
                }
                else
                {
                    QueryJoinType type = links[0].Type;
                    bool reversed = string.Equals(links[0].LeftAlias, table.Alias, StringComparison.OrdinalIgnoreCase);
                    // 反向的 LEFT／RIGHT 連接要互換，語意才與圖上的方向一致。
                    if (reversed && type == QueryJoinType.Left) type = QueryJoinType.Right;
                    else if (reversed && type == QueryJoinType.Right) type = QueryJoinType.Left;
                    sql.Append(Environment.NewLine + JoinKeyword(type) + " " + TableReference(provider, table) + " ON " +
                               string.Join(" AND ", links.Select(join => Column(provider, join.LeftAlias, join.LeftColumn) + " = " + Column(provider, join.RightAlias, join.RightColumn))));
                    foreach (QueryBuilderJoin link in links) pending.Remove(link);
                }
                placed.Add(table.Alias);
            }

            // 兩端都已出現、但沒被用在 ON 的連接（例如第二條外鍵）併入 WHERE，避免無聲遺失。
            List<string> where = pending.Where(join => placed.Contains(join.LeftAlias) && placed.Contains(join.RightAlias))
                .Select(join => Column(provider, join.LeftAlias, join.LeftColumn) + " = " + Column(provider, join.RightAlias, join.RightColumn))
                .ToList();
            string whereText = Conditions(provider, model.Conditions.Where(item => item.Aggregate == QueryAggregate.None).ToList());
            if (whereText.Length > 0) where.Add(where.Count > 0 ? "(" + whereText + ")" : whereText);
            if (where.Count > 0) sql.Append(Environment.NewLine + "WHERE " + string.Join(" AND ", where));

            List<string> groups = model.Columns.Where(column => column.GroupBy && column.Aggregate == QueryAggregate.None && column.Column != "*")
                .Select(column => Column(provider, column.TableAlias, column.Column)).ToList();
            if (output.Any(column => column.Aggregate != QueryAggregate.None))
            {
                foreach (QueryBuilderColumn column in output.Where(column => column.Aggregate == QueryAggregate.None && column.Column != "*"))
                {
                    string expression = Column(provider, column.TableAlias, column.Column);
                    if (!groups.Contains(expression)) groups.Add(expression);
                }
            }
            if (groups.Count > 0) sql.Append(Environment.NewLine + "GROUP BY " + string.Join(", ", groups));

            string having = Conditions(provider, model.Conditions.Where(item => item.Aggregate != QueryAggregate.None).ToList());
            if (having.Length > 0) sql.Append(Environment.NewLine + "HAVING " + having);

            List<string> order = model.Columns.Where(column => column.Sort != QuerySort.None && column.Column != "*")
                .Select(column => Expression(provider, column.TableAlias, column.Column, column.Aggregate) + (column.Sort == QuerySort.Descending ? " DESC" : " ASC"))
                .ToList();
            if (order.Count > 0) sql.Append(Environment.NewLine + "ORDER BY " + string.Join(", ", order));

            if (model.Limit.HasValue && provider != "mssql")
            {
                sql.Append(Environment.NewLine + (provider == "oracle"
                    ? "FETCH FIRST " + model.Limit.Value.ToString(CultureInfo.InvariantCulture) + " ROWS ONLY"
                    : "LIMIT " + model.Limit.Value.ToString(CultureInfo.InvariantCulture)));
            }
            return sql.ToString() + ";";
        }

        /// <summary>把 SQL 轉回模型；不在建構器能表達的範圍內時回傳 false 與原因。</summary>
        public static bool TryParse(string providerName, string sql, IList<string> knownTables, out QueryBuilderModel model, out string error)
        {
            model = null;
            error = null;
            try
            {
                Parser parser = new Parser(Tokenize(sql ?? string.Empty), knownTables ?? new List<string>());
                model = parser.ParseSelect();
                return true;
            }
            catch (FormatException exception)
            {
                error = exception.Message;
                return false;
            }
        }

        public static string AggregateLabel(QueryAggregate aggregate)
        {
            switch (aggregate)
            {
                case QueryAggregate.CountDistinct: return "COUNT(DISTINCT)";
                case QueryAggregate.None: return string.Empty;
                default: return aggregate.ToString().ToUpperInvariant();
            }
        }

        public static string Quote(string provider, string identifier)
        {
            string value = identifier ?? string.Empty;
            if (provider == "mysql") return "`" + value.Replace("`", "``") + "`";
            if (provider == "mssql") return "[" + value.Replace("]", "]]") + "]";
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>條件值：數字與 NULL／TRUE／FALSE 原樣輸出，其餘一律當字串並跳脫。</summary>
        public static string Literal(string provider, string value)
        {
            string text = (value ?? string.Empty).Trim();
            decimal number;
            if (text.Length > 0 && decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number) &&
                !text.StartsWith("+", StringComparison.Ordinal) && !(text.Length > 1 && text[0] == '0' && text[1] != '.'))
            {
                return text;
            }
            string upper = text.ToUpperInvariant();
            if (upper == "NULL" || upper == "TRUE" || upper == "FALSE") return upper;
            if (text.Length >= 2 && text[0] == '\'' && text[text.Length - 1] == '\'') text = text.Substring(1, text.Length - 2).Replace("''", "'");
            string escaped = text.Replace("'", "''");
            if (provider == "mysql") escaped = escaped.Replace("\\", "\\\\");
            return (provider == "mssql" ? "N'" : "'") + escaped + "'";
        }

        private static string Conditions(string provider, List<QueryBuilderCondition> conditions)
        {
            StringBuilder text = new StringBuilder();
            foreach (QueryBuilderCondition condition in conditions.Where(item => !string.IsNullOrWhiteSpace(item.Column)))
            {
                if (text.Length > 0) text.Append(string.Equals(condition.Connector, "OR", StringComparison.OrdinalIgnoreCase) ? " OR " : " AND ");
                string left = Expression(provider, condition.TableAlias, condition.Column, condition.Aggregate);
                string op = (condition.Operator ?? "=").Trim().ToUpperInvariant();
                switch (op)
                {
                    case "IS NULL":
                    case "IS NOT NULL":
                        text.Append(left + " " + op);
                        break;
                    case "IN":
                    case "NOT IN":
                        text.Append(left + " " + op + " (" + string.Join(", ", SplitList(condition.Value).Select(item => Literal(provider, item))) + ")");
                        break;
                    case "BETWEEN":
                        text.Append(left + " BETWEEN " + Literal(provider, condition.Value) + " AND " + Literal(provider, condition.Value2));
                        break;
                    default:
                        if (!Operators.Contains(op)) throw new FormatException(Localization.Format("QueryBuilder.Error.Operator", condition.Operator));
                        text.Append(left + " " + op + " " + Literal(provider, condition.Value));
                        break;
                }
            }
            return text.ToString();
        }

        private static IEnumerable<string> SplitList(string value)
        {
            List<string> items = new List<string>();
            StringBuilder current = new StringBuilder();
            bool quoted = false;
            string text = value ?? string.Empty;
            for (int index = 0; index < text.Length; index++)
            {
                char c = text[index];
                if (c == '\'')
                {
                    if (quoted && index + 1 < text.Length && text[index + 1] == '\'')
                    {
                        current.Append("''");
                        index++;
                        continue;
                    }
                    quoted = !quoted;
                    current.Append(c);
                }
                else if (c == ',' && !quoted)
                {
                    items.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }
            items.Add(current.ToString());
            return items.Select(item => item.Trim()).Where(item => item.Length > 0);
        }

        private static string Expression(string provider, string alias, string column, QueryAggregate aggregate)
        {
            string target = column == "*" ? (string.IsNullOrEmpty(alias) || aggregate != QueryAggregate.None ? "*" : Quote(provider, alias) + ".*") : Column(provider, alias, column);
            switch (aggregate)
            {
                case QueryAggregate.None: return target;
                case QueryAggregate.CountDistinct: return "COUNT(DISTINCT " + target + ")";
                default: return aggregate.ToString().ToUpperInvariant() + "(" + target + ")";
            }
        }

        private static string Column(string provider, string alias, string column)
        {
            return string.IsNullOrEmpty(alias) ? Quote(provider, column) : Quote(provider, alias) + "." + Quote(provider, column);
        }

        private static string TableReference(string provider, QueryBuilderTable table)
        {
            string name = string.Join(".", (table.Name ?? string.Empty).Split('.').Select(part => Quote(provider, part)));
            string lastPart = (table.Name ?? string.Empty).Split('.').Last();
            return string.Equals(lastPart, table.Alias, StringComparison.Ordinal) ? name : name + (provider == "oracle" ? " " : " AS ") + Quote(provider, table.Alias);
        }

        private static string JoinKeyword(QueryJoinType type)
        {
            switch (type)
            {
                case QueryJoinType.Left: return "LEFT JOIN";
                case QueryJoinType.Right: return "RIGHT JOIN";
                case QueryJoinType.Full: return "FULL OUTER JOIN";
                default: return "INNER JOIN";
            }
        }

        // ------------------------------------------------------------ Parsing

        private enum TokenKind
        {
            Word,
            QuotedIdentifier,
            String,
            Number,
            Symbol,
            End
        }

        private sealed class Token
        {
            public TokenKind Kind;
            public string Text;
            public int Position;

            public bool Is(string keyword)
            {
                return Kind == TokenKind.Word && string.Equals(Text, keyword, StringComparison.OrdinalIgnoreCase);
            }

            public bool IsSymbol(string symbol)
            {
                return Kind == TokenKind.Symbol && Text == symbol;
            }
        }

        private static List<Token> Tokenize(string sql)
        {
            List<Token> tokens = new List<Token>();
            int index = 0;
            while (index < sql.Length)
            {
                char c = sql[index];
                if (char.IsWhiteSpace(c))
                {
                    index++;
                    continue;
                }

                if (c == '-' && index + 1 < sql.Length && sql[index + 1] == '-' || c == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
                {
                    throw new FormatException(Localization.T("QueryBuilder.Error.Comments"));
                }

                int start = index;
                if (c == '\'')
                {
                    StringBuilder value = new StringBuilder();
                    index++;
                    while (true)
                    {
                        if (index >= sql.Length) throw new FormatException(Localization.T("QueryBuilder.Error.UnclosedString"));
                        if (sql[index] == '\'')
                        {
                            if (index + 1 < sql.Length && sql[index + 1] == '\'')
                            {
                                value.Append('\'');
                                index += 2;
                                continue;
                            }
                            index++;
                            break;
                        }
                        if (sql[index] == '\\') throw new FormatException(Localization.T("QueryBuilder.Error.Backslash"));
                        value.Append(sql[index++]);
                    }
                    tokens.Add(new Token { Kind = TokenKind.String, Text = value.ToString(), Position = start });
                    continue;
                }

                if (c == 'N' && index + 1 < sql.Length && sql[index + 1] == '\'')
                {
                    index++;
                    continue;
                }

                if (c == '`' || c == '"' || c == '[')
                {
                    char close = c == '[' ? ']' : c;
                    StringBuilder value = new StringBuilder();
                    index++;
                    while (true)
                    {
                        if (index >= sql.Length) throw new FormatException(Localization.T("QueryBuilder.Error.UnclosedIdentifier"));
                        if (sql[index] == close)
                        {
                            if (index + 1 < sql.Length && sql[index + 1] == close)
                            {
                                value.Append(close);
                                index += 2;
                                continue;
                            }
                            index++;
                            break;
                        }
                        value.Append(sql[index++]);
                    }
                    tokens.Add(new Token { Kind = TokenKind.QuotedIdentifier, Text = value.ToString(), Position = start });
                    continue;
                }

                if (char.IsDigit(c) || c == '-' && index + 1 < sql.Length && char.IsDigit(sql[index + 1]) && PreviousAllowsSign(tokens))
                {
                    index++;
                    while (index < sql.Length && (char.IsDigit(sql[index]) || sql[index] == '.')) index++;
                    tokens.Add(new Token { Kind = TokenKind.Number, Text = sql.Substring(start, index - start), Position = start });
                    continue;
                }

                if (char.IsLetter(c) || c == '_')
                {
                    while (index < sql.Length && (char.IsLetterOrDigit(sql[index]) || sql[index] == '_' || sql[index] == '$')) index++;
                    tokens.Add(new Token { Kind = TokenKind.Word, Text = sql.Substring(start, index - start), Position = start });
                    continue;
                }

                string two = index + 1 < sql.Length ? sql.Substring(index, 2) : string.Empty;
                if (two == "<>" || two == "<=" || two == ">=" || two == "!=")
                {
                    tokens.Add(new Token { Kind = TokenKind.Symbol, Text = two == "!=" ? "<>" : two, Position = start });
                    index += 2;
                    continue;
                }

                if ("(),.*=<>;".IndexOf(c) >= 0)
                {
                    tokens.Add(new Token { Kind = TokenKind.Symbol, Text = c.ToString(), Position = start });
                    index++;
                    continue;
                }

                throw new FormatException(Localization.Format("QueryBuilder.Error.Unexpected", c.ToString(), start));
            }
            tokens.Add(new Token { Kind = TokenKind.End, Text = string.Empty, Position = sql.Length });
            return tokens;
        }

        private static bool PreviousAllowsSign(List<Token> tokens)
        {
            if (tokens.Count == 0) return true;
            Token last = tokens[tokens.Count - 1];
            return last.Kind == TokenKind.Symbol && last.Text != ")" && last.Text != "*" || last.Kind == TokenKind.Word;
        }

        private static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SELECT", "FROM", "WHERE", "GROUP", "BY", "HAVING", "ORDER", "LIMIT", "OFFSET", "FETCH", "JOIN", "INNER", "LEFT", "RIGHT", "FULL",
            "OUTER", "CROSS", "ON", "AND", "OR", "NOT", "AS", "ASC", "DESC", "DISTINCT", "TOP", "IS", "NULL", "IN", "LIKE", "BETWEEN", "UNION"
        };

        private sealed class Parser
        {
            private readonly List<Token> tokens;
            private readonly IList<string> knownTables;
            private readonly QueryBuilderModel model = new QueryBuilderModel();
            private int position;

            public Parser(List<Token> tokens, IList<string> knownTables)
            {
                this.tokens = tokens;
                this.knownTables = knownTables;
            }

            private Token Current { get { return tokens[position]; } }

            public QueryBuilderModel ParseSelect()
            {
                Expect("SELECT");
                if (Accept("DISTINCT")) model.Distinct = true;
                if (Accept("TOP"))
                {
                    bool parenthesized = AcceptSymbol("(");
                    model.Limit = ReadInteger();
                    if (parenthesized) ExpectSymbol(")");
                }

                int selectStart = position;
                SkipSelectList();
                Expect("FROM");
                ParseFrom();
                int afterFrom = position;

                // 先讀 FROM 才知道別名，再回頭解析選取清單。
                position = selectStart;
                ParseSelectList();
                position = afterFrom;

                if (Accept("WHERE")) ParseConditions(false);
                if (Accept("GROUP"))
                {
                    Expect("BY");
                    do
                    {
                        string alias, column;
                        ParseColumnReference(out alias, out column);
                        QueryBuilderColumn existing = model.Columns.FirstOrDefault(item => Same(item, alias, column) && item.Aggregate == QueryAggregate.None);
                        if (existing == null)
                        {
                            existing = new QueryBuilderColumn { TableAlias = alias, Column = column, Output = false };
                            model.Columns.Add(existing);
                        }
                        existing.GroupBy = true;
                    }
                    while (AcceptSymbol(","));
                }
                if (Accept("HAVING")) ParseConditions(true);
                if (Accept("ORDER"))
                {
                    Expect("BY");
                    do
                    {
                        ParseOrderItem();
                    }
                    while (AcceptSymbol(","));
                }
                if (Accept("LIMIT"))
                {
                    model.Limit = ReadInteger();
                }
                else if (Accept("FETCH"))
                {
                    if (!Accept("FIRST")) Expect("NEXT");
                    model.Limit = ReadInteger();
                    if (!Accept("ROWS")) Expect("ROW");
                    Expect("ONLY");
                }
                AcceptSymbol(";");
                if (Current.Kind != TokenKind.End) throw Unexpected();
                if (model.Columns.Count == 0) throw new FormatException(Localization.T("QueryBuilder.Error.NoColumns"));
                return model;
            }

            private void SkipSelectList()
            {
                int depth = 0;
                while (Current.Kind != TokenKind.End && !(depth == 0 && Current.Is("FROM")))
                {
                    if (Current.IsSymbol("(")) depth++;
                    if (Current.IsSymbol(")")) depth--;
                    position++;
                }
            }

            private void ParseSelectList()
            {
                do
                {
                    if (AcceptSymbol("*"))
                    {
                        model.Columns.Add(new QueryBuilderColumn { TableAlias = model.Tables.Count == 1 ? model.Tables[0].Alias : string.Empty, Column = "*" });
                        continue;
                    }

                    QueryAggregate aggregate = ReadAggregate();
                    string alias, column;
                    if (aggregate != QueryAggregate.None)
                    {
                        ExpectSymbol("(");
                        if (Accept("DISTINCT"))
                        {
                            if (aggregate != QueryAggregate.Count) throw Unexpected();
                            aggregate = QueryAggregate.CountDistinct;
                        }
                        if (AcceptSymbol("*"))
                        {
                            alias = string.Empty;
                            column = "*";
                        }
                        else
                        {
                            ParseColumnReference(out alias, out column);
                        }
                        ExpectSymbol(")");
                    }
                    else
                    {
                        ParseColumnReference(out alias, out column);
                    }

                    string outputAlias = null;
                    if (Accept("AS")) outputAlias = ReadIdentifier();
                    else if (IsIdentifier(Current) && !Current.Is("FROM")) outputAlias = ReadIdentifier();
                    model.Columns.Add(new QueryBuilderColumn { TableAlias = alias, Column = column, Aggregate = aggregate, Alias = outputAlias });
                }
                while (AcceptSymbol(","));
                if (!Current.Is("FROM")) throw Unexpected();
            }

            private void ParseFrom()
            {
                AddTable(QueryJoinType.Inner, false);
                while (true)
                {
                    if (AcceptSymbol(","))
                    {
                        AddTable(QueryJoinType.Inner, false);
                        continue;
                    }

                    QueryJoinType type;
                    bool cross = false;
                    if (Accept("INNER")) type = QueryJoinType.Inner;
                    else if (Accept("LEFT")) { Accept("OUTER"); type = QueryJoinType.Left; }
                    else if (Accept("RIGHT")) { Accept("OUTER"); type = QueryJoinType.Right; }
                    else if (Accept("FULL")) { Accept("OUTER"); type = QueryJoinType.Full; }
                    else if (Accept("CROSS")) { type = QueryJoinType.Inner; cross = true; }
                    else if (Current.Is("JOIN")) type = QueryJoinType.Inner;
                    else break;
                    Expect("JOIN");
                    AddTable(type, !cross);
                }
            }

            private void AddTable(QueryJoinType type, bool expectOn)
            {
                string name = ReadIdentifier();
                if (AcceptSymbol(".")) name += "." + ReadIdentifier();
                string alias = null;
                if (Accept("AS")) alias = ReadIdentifier();
                else if (IsIdentifier(Current)) alias = ReadIdentifier();
                string resolved = knownTables.FirstOrDefault(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase)) ??
                                  knownTables.FirstOrDefault(item => string.Equals(item.Split('.').Last(), name.Split('.').Last(), StringComparison.OrdinalIgnoreCase) && !name.Contains(".")) ??
                                  name;
                QueryBuilderTable table = new QueryBuilderTable { Name = resolved, Alias = alias ?? name.Split('.').Last() };
                if (model.FindTable(table.Alias) != null) throw new FormatException(Localization.Format("QueryBuilder.Error.DuplicateAlias", table.Alias));
                model.Tables.Add(table);
                if (!expectOn) return;
                Expect("ON");
                do
                {
                    string leftAlias, leftColumn, rightAlias, rightColumn;
                    ParseColumnReference(out leftAlias, out leftColumn);
                    ExpectSymbol("=");
                    ParseColumnReference(out rightAlias, out rightColumn);
                    // 連接方向以「已存在的表 → 新加入的表」儲存，LEFT／RIGHT 語意才能保持。
                    if (string.Equals(leftAlias, table.Alias, StringComparison.OrdinalIgnoreCase))
                    {
                        model.Joins.Add(new QueryBuilderJoin { LeftAlias = rightAlias, LeftColumn = rightColumn, RightAlias = leftAlias, RightColumn = leftColumn, Type = type });
                    }
                    else
                    {
                        model.Joins.Add(new QueryBuilderJoin { LeftAlias = leftAlias, LeftColumn = leftColumn, RightAlias = rightAlias, RightColumn = rightColumn, Type = type });
                    }
                }
                while (Accept("AND"));
            }

            private void ParseConditions(bool having)
            {
                string connector = "AND";
                do
                {
                    if (Current.IsSymbol("(")) throw new FormatException(Localization.T("QueryBuilder.Error.Parentheses"));
                    QueryBuilderCondition condition = new QueryBuilderCondition { Connector = connector };
                    QueryAggregate aggregate = ReadAggregate();
                    string alias, column;
                    if (aggregate != QueryAggregate.None)
                    {
                        ExpectSymbol("(");
                        if (Accept("DISTINCT")) aggregate = QueryAggregate.CountDistinct;
                        if (AcceptSymbol("*")) { alias = string.Empty; column = "*"; }
                        else ParseColumnReference(out alias, out column);
                        ExpectSymbol(")");
                    }
                    else
                    {
                        ParseColumnReference(out alias, out column);
                    }
                    if (having != (aggregate != QueryAggregate.None)) throw new FormatException(Localization.T("QueryBuilder.Error.HavingPlacement"));
                    condition.TableAlias = alias;
                    condition.Column = column;
                    condition.Aggregate = aggregate;

                    if (Accept("IS"))
                    {
                        condition.Operator = Accept("NOT") ? "IS NOT NULL" : "IS NULL";
                        Expect("NULL");
                    }
                    else
                    {
                        bool not = Accept("NOT");
                        if (Accept("IN"))
                        {
                            condition.Operator = not ? "NOT IN" : "IN";
                            ExpectSymbol("(");
                            List<string> values = new List<string>();
                            do
                            {
                                values.Add(ReadValue(true));
                            }
                            while (AcceptSymbol(","));
                            ExpectSymbol(")");
                            condition.Value = string.Join(", ", values);
                        }
                        else if (Accept("LIKE"))
                        {
                            condition.Operator = not ? "NOT LIKE" : "LIKE";
                            condition.Value = ReadValue(false);
                        }
                        else if (!not && Accept("BETWEEN"))
                        {
                            condition.Operator = "BETWEEN";
                            condition.Value = ReadValue(false);
                            Expect("AND");
                            condition.Value2 = ReadValue(false);
                        }
                        else if (!not && Current.Kind == TokenKind.Symbol && new[] { "=", "<>", "<", "<=", ">", ">=" }.Contains(Current.Text))
                        {
                            condition.Operator = Current.Text;
                            position++;
                            condition.Value = ReadValue(false);
                        }
                        else
                        {
                            throw Unexpected();
                        }
                    }
                    model.Conditions.Add(condition);

                    if (Accept("AND")) connector = "AND";
                    else if (Accept("OR")) connector = "OR";
                    else break;
                }
                while (true);
            }

            private void ParseOrderItem()
            {
                QueryAggregate aggregate = ReadAggregate();
                string alias, column;
                if (aggregate != QueryAggregate.None)
                {
                    ExpectSymbol("(");
                    if (Accept("DISTINCT")) aggregate = QueryAggregate.CountDistinct;
                    if (AcceptSymbol("*")) { alias = string.Empty; column = "*"; }
                    else ParseColumnReference(out alias, out column);
                    ExpectSymbol(")");
                }
                else
                {
                    // ORDER BY 可以用輸出別名。
                    if (IsIdentifier(Current) && !tokens[position + 1].IsSymbol("."))
                    {
                        string name = Current.Text;
                        QueryBuilderColumn byAlias = model.Columns.FirstOrDefault(item => string.Equals(item.Alias, name, StringComparison.OrdinalIgnoreCase));
                        if (byAlias != null)
                        {
                            position++;
                            byAlias.Sort = Accept("DESC") ? QuerySort.Descending : QuerySort.Ascending;
                            Accept("ASC");
                            return;
                        }
                    }
                    ParseColumnReference(out alias, out column);
                }

                QuerySort sort = Accept("DESC") ? QuerySort.Descending : QuerySort.Ascending;
                if (sort == QuerySort.Ascending) Accept("ASC");
                QueryBuilderColumn target = model.Columns.FirstOrDefault(item => Same(item, alias, column) && item.Aggregate == aggregate);
                if (target == null)
                {
                    target = new QueryBuilderColumn { TableAlias = alias, Column = column, Aggregate = aggregate, Output = false };
                    model.Columns.Add(target);
                }
                target.Sort = sort;
            }

            private void ParseColumnReference(out string alias, out string column)
            {
                string first = ReadIdentifier();
                if (AcceptSymbol("."))
                {
                    alias = first;
                    column = AcceptSymbol("*") ? "*" : ReadIdentifier();
                    if (model.FindTable(alias) == null) throw new FormatException(Localization.Format("QueryBuilder.Error.UnknownAlias", alias));
                    return;
                }
                if (model.Tables.Count != 1) throw new FormatException(Localization.Format("QueryBuilder.Error.Ambiguous", first));
                alias = model.Tables[0].Alias;
                column = first;
            }

            private QueryAggregate ReadAggregate()
            {
                if (Current.Kind != TokenKind.Word || !tokens[position + 1].IsSymbol("(")) return QueryAggregate.None;
                QueryAggregate aggregate;
                switch (Current.Text.ToUpperInvariant())
                {
                    case "COUNT": aggregate = QueryAggregate.Count; break;
                    case "SUM": aggregate = QueryAggregate.Sum; break;
                    case "AVG": aggregate = QueryAggregate.Avg; break;
                    case "MIN": aggregate = QueryAggregate.Min; break;
                    case "MAX": aggregate = QueryAggregate.Max; break;
                    default: throw new FormatException(Localization.Format("QueryBuilder.Error.Function", Current.Text));
                }
                position++;
                return aggregate;
            }

            private string ReadValue(bool inList)
            {
                Token token = Current;
                if (token.Kind == TokenKind.String)
                {
                    position++;
                    decimal ignored;
                    string upper = token.Text.ToUpperInvariant();
                    // 看起來像數字或關鍵字的字串保留引號，重新產生時才不會變成數字／NULL。
                    return decimal.TryParse(token.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out ignored) || upper == "NULL" || upper == "TRUE" || upper == "FALSE" ||
                           inList && token.Text.Contains(",")
                        ? "'" + token.Text.Replace("'", "''") + "'"
                        : token.Text;
                }
                if (token.Kind == TokenKind.Number)
                {
                    position++;
                    return token.Text;
                }
                if (token.Is("NULL") || token.Is("TRUE") || token.Is("FALSE"))
                {
                    position++;
                    return token.Text.ToUpperInvariant();
                }
                throw new FormatException(Localization.Format("QueryBuilder.Error.Value", token.Text));
            }

            private int ReadInteger()
            {
                int value;
                if (Current.Kind != TokenKind.Number || !int.TryParse(Current.Text, NumberStyles.None, CultureInfo.InvariantCulture, out value) || value < 1) throw Unexpected();
                position++;
                return value;
            }

            private string ReadIdentifier()
            {
                if (!IsIdentifier(Current)) throw Unexpected();
                return tokens[position++].Text;
            }

            private static bool IsIdentifier(Token token)
            {
                return token.Kind == TokenKind.QuotedIdentifier || token.Kind == TokenKind.Word && !Reserved.Contains(token.Text);
            }

            private static bool Same(QueryBuilderColumn item, string alias, string column)
            {
                return string.Equals(item.TableAlias, alias, StringComparison.OrdinalIgnoreCase) && string.Equals(item.Column, column, StringComparison.OrdinalIgnoreCase);
            }

            private bool Accept(string keyword)
            {
                if (!Current.Is(keyword)) return false;
                position++;
                return true;
            }

            private bool AcceptSymbol(string symbol)
            {
                if (!Current.IsSymbol(symbol)) return false;
                position++;
                return true;
            }

            private void Expect(string keyword)
            {
                if (!Accept(keyword)) throw Unexpected(keyword);
            }

            private void ExpectSymbol(string symbol)
            {
                if (!AcceptSymbol(symbol)) throw Unexpected(symbol);
            }

            private FormatException Unexpected(string expected = null)
            {
                string found = Current.Kind == TokenKind.End ? Localization.T("QueryBuilder.Error.EndOfText") : Current.Text;
                return new FormatException(expected == null
                    ? Localization.Format("QueryBuilder.Error.Unsupported", found, Current.Position)
                    : Localization.Format("QueryBuilder.Error.Expected", expected, found, Current.Position));
            }
        }
    }
}
