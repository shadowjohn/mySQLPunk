using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace mySQLPunk.lib
{
    public enum QueryPlanSeverity
    {
        Normal,
        Medium,
        High
    }

    public sealed class QueryPlanNode
    {
        public string NodeType { get; set; }
        public string RelationName { get; set; }
        public string Alias { get; set; }
        public string AccessType { get; set; }
        public string JoinType { get; set; }
        public double? StartupCost { get; set; }
        public double? TotalCost { get; set; }
        public double? EstimatedRows { get; set; }
        public double? ActualRows { get; set; }
        public double? ActualTotalTimeMs { get; set; }
        public QueryPlanSeverity Severity { get; set; }
        public Dictionary<string, string> Details { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<QueryPlanNode> Children { get; } = new List<QueryPlanNode>();
    }

    public sealed class QueryPlanDocument
    {
        private string rawPlan;

        public string Provider { get; set; }
        public string ExplainSql { get; set; }
        public string RawFormat { get; set; }
        public string RawPlan
        {
            get { return rawPlan ?? string.Empty; }
            set { rawPlan = value ?? string.Empty; }
        }
        public string RawJson
        {
            get { return RawPlan; }
            set { RawPlan = value; }
        }
        public string TextPlan { get; set; }
        public double? TotalCost { get; set; }
        public double? PlanningTimeMs { get; set; }
        public double? ExecutionTimeMs { get; set; }
        public int NodeCount { get; set; }
        public List<QueryPlanNode> Roots { get; } = new List<QueryPlanNode>();
    }

    public static class QueryPlanService
    {
        private static readonly HashSet<string> ExplainableStatements = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SELECT", "WITH", "INSERT", "UPDATE", "DELETE", "REPLACE"
        };

        public static bool SupportsProvider(string provider)
        {
            string normalized = NormalizeProvider(provider);
            return normalized == "mysql" || normalized == "postgresql" || normalized == "sqlserver" ||
                normalized == "oracle" || normalized == "sqlite";
        }

        public static string BuildExplainSql(string provider, string sql)
        {
            string normalizedProvider = NormalizeProvider(provider);
            if (!SupportsProvider(normalizedProvider))
            {
                throw new NotSupportedException(Localization.Format("Query.PlanUnsupportedProvider", provider ?? string.Empty));
            }

            string statement = NormalizeSingleStatement(sql);
            string firstKeyword = GetFirstKeyword(statement);
            if (!ExplainableStatements.Contains(firstKeyword))
            {
                throw new InvalidOperationException(Localization.T("Query.PlanUnsupportedStatement"));
            }

            if (normalizedProvider == "mysql")
            {
                return "EXPLAIN FORMAT=JSON " + statement;
            }

            if (normalizedProvider == "sqlserver")
            {
                return "SET SHOWPLAN_ALL ON;" + Environment.NewLine + statement + Environment.NewLine + "SET SHOWPLAN_ALL OFF;";
            }

            if (normalizedProvider == "oracle")
            {
                return "EXPLAIN PLAN FOR " + statement;
            }

            if (normalizedProvider == "sqlite")
            {
                return "EXPLAIN QUERY PLAN " + statement;
            }

            // 不使用 ANALYZE，避免 INSERT／UPDATE／DELETE 在產生計畫時真的修改資料。
            return "EXPLAIN (FORMAT JSON, ANALYZE FALSE, COSTS TRUE, VERBOSE FALSE, BUFFERS FALSE) " + statement;
        }

        public static QueryPlanDocument Execute(IDatabase database, string sql)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));

            string provider = NormalizeProvider(database.ProviderName);
            string previewSql = BuildExplainSql(provider, sql);
            string statement = NormalizeSingleStatement(sql);
            if (provider == "sqlserver") return ExecuteSqlServer(database, statement, previewSql);
            if (provider == "oracle") return ExecuteOracle(database, statement);

            DataTable result = database.SelectSQL(previewSql);
            ThrowIfQueryFailed(result);
            return Parse(provider, result, previewSql);
        }

        public static QueryPlanDocument Parse(string provider, DataTable result, string explainSql)
        {
            string normalizedProvider = NormalizeProvider(provider);
            if (!SupportsProvider(normalizedProvider))
            {
                throw new NotSupportedException(Localization.Format("Query.PlanUnsupportedProvider", provider ?? string.Empty));
            }

            if (normalizedProvider == "sqlserver") return ParseSqlServer(result, explainSql);
            if (normalizedProvider == "oracle") return ParseOracle(result, explainSql);
            if (normalizedProvider == "sqlite") return ParseSqlite(result, explainSql);

            string rawJson = ExtractJson(result);
            if (string.IsNullOrWhiteSpace(rawJson))
            {
                throw new InvalidOperationException(Localization.T("Query.PlanMissingJson"));
            }

            return ParseJson(provider, rawJson, explainSql);
        }

        public static QueryPlanDocument ParseJson(string provider, string rawJson, string explainSql = null)
        {
            string normalizedProvider = NormalizeProvider(provider);
            if (normalizedProvider != "mysql" && normalizedProvider != "postgresql")
            {
                throw new NotSupportedException(Localization.Format("Query.PlanUnsupportedProvider", provider ?? string.Empty));
            }

            try
            {
                JToken token = JToken.Parse(rawJson);
                QueryPlanDocument document = normalizedProvider == "postgresql"
                    ? ParsePostgreSql(token)
                    : ParseMySql(token);
                document.Provider = normalizedProvider;
                document.ExplainSql = explainSql ?? string.Empty;
                document.RawFormat = "JSON";
                document.RawJson = token.ToString(Formatting.Indented);
                CompleteDocument(document);
                return document;
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(Localization.Format("Query.PlanInvalidJson", ex.Message), ex);
            }
        }

        public static string ExtractJson(DataTable result)
        {
            if (result == null || result.Rows.Count == 0 || result.Columns.Count == 0) return string.Empty;

            DataColumn preferred = result.Columns.Cast<DataColumn>().FirstOrDefault(column =>
                string.Equals(column.ColumnName, "EXPLAIN", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(column.ColumnName, "QUERY PLAN", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(column.ColumnName, "QUERY_PLAN", StringComparison.OrdinalIgnoreCase));
            int columnIndex = preferred == null ? 0 : preferred.Ordinal;
            List<string> fragments = new List<string>();
            foreach (DataRow row in result.Rows)
            {
                object value = row[columnIndex];
                if (value == null || value == DBNull.Value) continue;
                string text = value is JToken jsonToken ? jsonToken.ToString(Formatting.None) : Convert.ToString(value, CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(text)) fragments.Add(text.Trim());
            }
            return string.Join(Environment.NewLine, fragments.ToArray());
        }

        private static QueryPlanDocument ExecuteSqlServer(IDatabase database, string statement, string previewSql)
        {
            bool showPlanEnabled = false;
            try
            {
                ExecuteCommand(database, "SET SHOWPLAN_ALL ON");
                showPlanEnabled = true;
                DataTable result = database.SelectSQL(statement);
                ThrowIfQueryFailed(result);
                return ParseSqlServer(result, previewSql);
            }
            finally
            {
                if (showPlanEnabled)
                {
                    // SHOWPLAN 是 session 狀態，失敗時也一定要關閉，避免後續查詢只回傳計畫。
                    ExecuteCommand(database, "SET SHOWPLAN_ALL OFF");
                }
            }
        }

        private static QueryPlanDocument ExecuteOracle(IDatabase database, string statement)
        {
            string statementId = "MYSQLPUNK_" + Guid.NewGuid().ToString("N").Substring(0, 16).ToUpperInvariant();
            string explainSql = "EXPLAIN PLAN SET STATEMENT_ID = '" + statementId + "' FOR " + statement;
            string selectSql = "SELECT ID, PARENT_ID, OPERATION, OPTIONS, OBJECT_OWNER, OBJECT_NAME, OBJECT_ALIAS, " +
                "COST, CARDINALITY, BYTES, CPU_COST, IO_COST, TEMP_SPACE, ACCESS_PREDICATES, FILTER_PREDICATES, " +
                "PROJECTION, OTHER_TAG, PARTITION_START, PARTITION_STOP FROM PLAN_TABLE WHERE STATEMENT_ID = '" +
                statementId + "' ORDER BY ID";
            string cleanupSql = "DELETE FROM PLAN_TABLE WHERE STATEMENT_ID = '" + statementId + "'";

            ExecuteCommand(database, explainSql);
            try
            {
                DataTable result = database.SelectSQL(selectSql);
                ThrowIfQueryFailed(result);
                return ParseOracle(result, explainSql);
            }
            finally
            {
                ExecuteCommand(database, cleanupSql);
            }
        }

        private static void ExecuteCommand(IDatabase database, string sql)
        {
            Dictionary<string, string> result = database.ExecSQL(sql);
            string status;
            if (result != null && result.TryGetValue("status", out status) &&
                (string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            string reason = string.Empty;
            if (result != null) result.TryGetValue("reason", out reason);
            if (string.IsNullOrWhiteSpace(reason)) reason = Localization.T("Query.UnknownError");
            throw new InvalidOperationException(reason);
        }

        private static void ThrowIfQueryFailed(DataTable result)
        {
            if (result == null || !result.ExtendedProperties.ContainsKey(my_sqlite.QueryErrorExtendedProperty)) return;
            string reason = Convert.ToString(result.ExtendedProperties[my_sqlite.QueryErrorExtendedProperty], CultureInfo.CurrentCulture);
            if (!string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException(reason);
        }

        private static QueryPlanDocument ParseSqlServer(DataTable result, string explainSql)
        {
            EnsurePlanRows(result);
            QueryPlanDocument document = new QueryPlanDocument
            {
                Provider = "sqlserver",
                ExplainSql = explainSql ?? string.Empty,
                RawFormat = "SHOWPLAN_ALL",
                RawPlan = SerializeTable(result)
            };

            Dictionary<int, QueryPlanNode> nodes = new Dictionary<int, QueryPlanNode>();
            List<Tuple<int?, int?, QueryPlanNode>> entries = new List<Tuple<int?, int?, QueryPlanNode>>();
            foreach (DataRow row in result.Rows)
            {
                int? nodeId = ReadCellInt(row, "NodeId");
                int? parentId = ReadCellInt(row, "Parent");
                string physical = ReadCellText(row, "PhysicalOp");
                string logical = ReadCellText(row, "LogicalOp");
                string statementText = ReadCellText(row, "StmtText");
                QueryPlanNode node = new QueryPlanNode
                {
                    NodeType = FirstNonEmpty(physical, logical, ReadCellText(row, "Type"), "Statement"),
                    RelationName = ExtractSqlServerObject(ReadCellText(row, "Argument")),
                    AccessType = logical,
                    JoinType = logical.IndexOf("Join", StringComparison.OrdinalIgnoreCase) >= 0 ? logical : string.Empty,
                    StartupCost = SumNullable(ReadCellDouble(row, "EstimateIO"), ReadCellDouble(row, "EstimateCPU")),
                    TotalCost = ReadCellDouble(row, "TotalSubtreeCost"),
                    EstimatedRows = ReadCellDouble(row, "EstimateRows")
                };
                CopyRowDetails(node, row);
                if (!string.IsNullOrWhiteSpace(statementText)) node.Details["StmtText"] = statementText;
                entries.Add(Tuple.Create(nodeId, parentId, node));
                if (nodeId.HasValue && !nodes.ContainsKey(nodeId.Value)) nodes[nodeId.Value] = node;
            }

            BuildHierarchy(document, entries, nodes);
            CompleteDocument(document);
            return document;
        }

        private static QueryPlanDocument ParseOracle(DataTable result, string explainSql)
        {
            EnsurePlanRows(result);
            QueryPlanDocument document = new QueryPlanDocument
            {
                Provider = "oracle",
                ExplainSql = explainSql ?? string.Empty,
                RawFormat = "PLAN_TABLE",
                RawPlan = SerializeTable(result)
            };

            Dictionary<int, QueryPlanNode> nodes = new Dictionary<int, QueryPlanNode>();
            List<Tuple<int?, int?, QueryPlanNode>> entries = new List<Tuple<int?, int?, QueryPlanNode>>();
            foreach (DataRow row in result.Rows)
            {
                int? nodeId = ReadCellInt(row, "ID");
                int? parentId = ReadCellInt(row, "PARENT_ID");
                string owner = ReadCellText(row, "OBJECT_OWNER");
                string objectName = ReadCellText(row, "OBJECT_NAME");
                QueryPlanNode node = new QueryPlanNode
                {
                    NodeType = FirstNonEmpty(ReadCellText(row, "OPERATION"), "Plan"),
                    RelationName = string.IsNullOrWhiteSpace(owner) ? objectName : owner + "." + objectName,
                    Alias = ReadCellText(row, "OBJECT_ALIAS"),
                    AccessType = ReadCellText(row, "OPTIONS"),
                    JoinType = ReadCellText(row, "OPERATION").IndexOf("JOIN", StringComparison.OrdinalIgnoreCase) >= 0
                        ? ReadCellText(row, "OPTIONS")
                        : string.Empty,
                    TotalCost = ReadCellDouble(row, "COST"),
                    EstimatedRows = ReadCellDouble(row, "CARDINALITY")
                };
                CopyRowDetails(node, row);
                entries.Add(Tuple.Create(nodeId, parentId, node));
                if (nodeId.HasValue && !nodes.ContainsKey(nodeId.Value)) nodes[nodeId.Value] = node;
            }

            BuildHierarchy(document, entries, nodes);
            CompleteDocument(document);
            return document;
        }

        private static QueryPlanDocument ParseSqlite(DataTable result, string explainSql)
        {
            EnsurePlanRows(result);
            QueryPlanDocument document = new QueryPlanDocument
            {
                Provider = "sqlite",
                ExplainSql = explainSql ?? string.Empty,
                RawFormat = "EXPLAIN QUERY PLAN",
                RawPlan = SerializeTable(result)
            };

            Dictionary<int, QueryPlanNode> nodes = new Dictionary<int, QueryPlanNode>();
            List<Tuple<int?, int?, QueryPlanNode>> entries = new List<Tuple<int?, int?, QueryPlanNode>>();
            foreach (DataRow row in result.Rows)
            {
                int? nodeId = ReadCellInt(row, "id");
                int? parentId = ReadCellInt(row, "parent");
                string detail = ReadCellText(row, "detail");
                QueryPlanNode node = new QueryPlanNode
                {
                    NodeType = SqliteOperation(detail),
                    RelationName = ExtractSqliteRelation(detail),
                    AccessType = ExtractSqliteAccess(detail)
                };
                CopyRowDetails(node, row);
                entries.Add(Tuple.Create(nodeId, parentId, node));
                if (nodeId.HasValue && !nodes.ContainsKey(nodeId.Value)) nodes[nodeId.Value] = node;
            }

            BuildHierarchy(document, entries, nodes);
            CompleteDocument(document);
            return document;
        }

        private static QueryPlanDocument ParsePostgreSql(JToken token)
        {
            JObject envelope = token.Type == JTokenType.Array ? token.First as JObject : token as JObject;
            if (envelope == null) throw new JsonReaderException("PostgreSQL plan root is not an object.");

            JObject plan = envelope["Plan"] as JObject;
            if (plan == null && envelope["Node Type"] != null) plan = envelope;
            if (plan == null) throw new JsonReaderException("PostgreSQL plan does not contain a Plan object.");

            QueryPlanDocument document = new QueryPlanDocument
            {
                PlanningTimeMs = ReadDouble(envelope["Planning Time"]),
                ExecutionTimeMs = ReadDouble(envelope["Execution Time"])
            };
            document.Roots.Add(ParsePostgreSqlNode(plan));
            document.TotalCost = document.Roots[0].TotalCost;
            return document;
        }

        private static QueryPlanNode ParsePostgreSqlNode(JObject obj)
        {
            QueryPlanNode node = new QueryPlanNode
            {
                NodeType = ReadText(obj["Node Type"], "Plan"),
                RelationName = ReadText(obj["Relation Name"]),
                Alias = ReadText(obj["Alias"]),
                AccessType = ReadText(obj["Scan Direction"]),
                JoinType = ReadText(obj["Join Type"]),
                StartupCost = ReadDouble(obj["Startup Cost"]),
                TotalCost = ReadDouble(obj["Total Cost"]),
                EstimatedRows = ReadDouble(obj["Plan Rows"]),
                ActualRows = ReadDouble(obj["Actual Rows"]),
                ActualTotalTimeMs = ReadDouble(obj["Actual Total Time"])
            };

            foreach (JProperty property in obj.Properties())
            {
                if (property.Name == "Plans") continue;
                if (property.Value.Type == JTokenType.Object || property.Value.Type == JTokenType.Array) continue;
                node.Details[property.Name] = ScalarText(property.Value);
            }

            JArray children = obj["Plans"] as JArray;
            if (children != null)
            {
                foreach (JObject child in children.OfType<JObject>())
                {
                    node.Children.Add(ParsePostgreSqlNode(child));
                }
            }
            return node;
        }

        private static QueryPlanDocument ParseMySql(JToken token)
        {
            JObject root = token as JObject;
            if (root == null) throw new JsonReaderException("MySQL plan root is not an object.");

            QueryPlanDocument document = new QueryPlanDocument();
            JObject queryBlock = root["query_block"] as JObject;
            document.Roots.Add(ParseMySqlObject(queryBlock ?? root, queryBlock == null ? "plan" : "query_block"));
            document.TotalCost = FindMySqlQueryCost(queryBlock ?? root) ?? document.Roots[0].TotalCost;
            return document;
        }

        private static QueryPlanNode ParseMySqlObject(JObject obj, string context)
        {
            JObject table = obj["table"] as JObject;
            if (table != null)
            {
                QueryPlanNode tableNode = CreateMySqlTableNode(table);
                AddMySqlStructuralChildren(tableNode, table);
                return tableNode;
            }

            QueryPlanNode node = new QueryPlanNode { NodeType = FriendlyMySqlOperation(context) };
            ApplyMySqlCost(node, obj);
            CopyMySqlScalarDetails(node, obj);
            AddMySqlStructuralChildren(node, obj);
            return node;
        }

        private static QueryPlanNode CreateMySqlTableNode(JObject table)
        {
            QueryPlanNode node = new QueryPlanNode
            {
                NodeType = "Table Access",
                RelationName = ReadText(table["table_name"]),
                Alias = ReadText(table["table_name"]),
                AccessType = ReadText(table["access_type"]),
                EstimatedRows = ReadDouble(table["rows_produced_per_join"]) ?? ReadDouble(table["rows_examined_per_scan"])
            };
            ApplyMySqlCost(node, table);
            CopyMySqlScalarDetails(node, table);
            return node;
        }

        private static void AddMySqlStructuralChildren(QueryPlanNode parent, JObject obj)
        {
            foreach (JProperty property in obj.Properties())
            {
                if (!IsMySqlStructuralProperty(property.Name)) continue;
                if (property.Value is JObject childObject)
                {
                    parent.Children.Add(ParseMySqlObject(childObject, property.Name));
                }
                else if (property.Value is JArray children)
                {
                    QueryPlanNode collection = new QueryPlanNode { NodeType = FriendlyMySqlOperation(property.Name) };
                    foreach (JObject child in children.OfType<JObject>())
                    {
                        collection.Children.Add(ParseMySqlObject(child, "step"));
                    }
                    if (collection.Children.Count > 0) parent.Children.Add(collection);
                }
            }
        }

        private static bool IsMySqlStructuralProperty(string name)
        {
            switch ((name ?? string.Empty).ToLowerInvariant())
            {
                case "nested_loop":
                case "ordering_operation":
                case "grouping_operation":
                case "duplicates_removal":
                case "union_result":
                case "query_specifications":
                case "materialized_from_subquery":
                case "attached_subqueries":
                case "optimized_away_subqueries":
                case "buffer_result":
                case "windowing":
                    return true;
                default:
                    return false;
            }
        }

        private static string FriendlyMySqlOperation(string context)
        {
            switch ((context ?? string.Empty).ToLowerInvariant())
            {
                case "query_block": return "Query Block";
                case "nested_loop": return "Nested Loop";
                case "ordering_operation": return "Sort";
                case "grouping_operation": return "Group";
                case "duplicates_removal": return "Duplicate Removal";
                case "union_result": return "Union Result";
                case "query_specifications": return "Query Specifications";
                case "materialized_from_subquery": return "Materialize Subquery";
                case "attached_subqueries": return "Attached Subqueries";
                case "optimized_away_subqueries": return "Optimized-away Subqueries";
                case "buffer_result": return "Buffer Result";
                case "windowing": return "Window";
                case "step": return "Plan Step";
                default: return "Plan";
            }
        }

        private static void ApplyMySqlCost(QueryPlanNode node, JObject obj)
        {
            JObject cost = obj["cost_info"] as JObject;
            if (cost == null) return;
            node.StartupCost = ReadDouble(cost["read_cost"]);
            node.TotalCost = ReadDouble(cost["query_cost"]) ?? ReadDouble(cost["prefix_cost"]) ?? ReadDouble(cost["eval_cost"]);
            foreach (JProperty property in cost.Properties())
            {
                node.Details["cost_info." + property.Name] = ScalarText(property.Value);
            }
        }

        private static double? FindMySqlQueryCost(JObject obj)
        {
            JObject cost = obj["cost_info"] as JObject;
            double? direct = cost == null ? null : ReadDouble(cost["query_cost"]);
            if (direct.HasValue) return direct;
            foreach (JProperty property in obj.Properties())
            {
                if (property.Value is JObject child)
                {
                    double? nested = FindMySqlQueryCost(child);
                    if (nested.HasValue) return nested;
                }
            }
            return null;
        }

        private static void CopyMySqlScalarDetails(QueryPlanNode node, JObject obj)
        {
            foreach (JProperty property in obj.Properties())
            {
                if (property.Name == "cost_info") continue;
                if (property.Value.Type == JTokenType.Object || property.Value.Type == JTokenType.Array) continue;
                node.Details[property.Name] = ScalarText(property.Value);
            }
        }

        private static void EnsurePlanRows(DataTable result)
        {
            ThrowIfQueryFailed(result);
            if (result == null || result.Rows.Count == 0 || result.Columns.Count == 0)
            {
                throw new InvalidOperationException(Localization.T("Query.PlanMissingData"));
            }
        }

        private static void BuildHierarchy(
            QueryPlanDocument document,
            IEnumerable<Tuple<int?, int?, QueryPlanNode>> entries,
            IDictionary<int, QueryPlanNode> nodes)
        {
            foreach (Tuple<int?, int?, QueryPlanNode> entry in entries)
            {
                QueryPlanNode parent;
                if (entry.Item2.HasValue && (!entry.Item1.HasValue || entry.Item2.Value != entry.Item1.Value) &&
                    nodes.TryGetValue(entry.Item2.Value, out parent))
                {
                    parent.Children.Add(entry.Item3);
                }
                else
                {
                    document.Roots.Add(entry.Item3);
                }
            }
        }

        private static void CopyRowDetails(QueryPlanNode node, DataRow row)
        {
            foreach (DataColumn column in row.Table.Columns)
            {
                string value = CellText(row[column]);
                if (!string.IsNullOrWhiteSpace(value)) node.Details[column.ColumnName] = value;
            }
        }

        private static string SerializeTable(DataTable table)
        {
            if (table == null || table.Columns.Count == 0) return string.Empty;
            StringBuilder output = new StringBuilder();
            output.AppendLine(string.Join("\t", table.Columns.Cast<DataColumn>().Select(column => column.ColumnName).ToArray()));
            foreach (DataRow row in table.Rows)
            {
                output.AppendLine(string.Join("\t", table.Columns.Cast<DataColumn>()
                    .Select(column => CellText(row[column]).Replace("\r", " ").Replace("\n", " ").Replace("\t", " "))
                    .ToArray()));
            }
            return output.ToString().TrimEnd();
        }

        private static string ReadCellText(DataRow row, string columnName)
        {
            DataColumn column = FindColumn(row == null ? null : row.Table, columnName);
            return column == null ? string.Empty : CellText(row[column]);
        }

        private static double? ReadCellDouble(DataRow row, string columnName)
        {
            string text = ReadCellText(row, columnName);
            double value;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
                ? value
                : (double?)null;
        }

        private static int? ReadCellInt(DataRow row, string columnName)
        {
            string text = ReadCellText(row, columnName);
            int value;
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? value
                : (int?)null;
        }

        private static DataColumn FindColumn(DataTable table, string columnName)
        {
            if (table == null) return null;
            return table.Columns.Cast<DataColumn>().FirstOrDefault(column =>
                string.Equals(column.ColumnName, columnName, StringComparison.OrdinalIgnoreCase));
        }

        private static string CellText(object value)
        {
            if (value == null || value == DBNull.Value) return string.Empty;
            if (value is IFormattable formattable) return formattable.ToString(null, CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        }

        private static double? SumNullable(double? first, double? second)
        {
            if (!first.HasValue && !second.HasValue) return null;
            return first.GetValueOrDefault() + second.GetValueOrDefault();
        }

        private static string ExtractSqlServerObject(string argument)
        {
            Match match = Regex.Match(argument ?? string.Empty, @"OBJECT:\s*\((?<object>[^\)]+)\)", RegexOptions.IgnoreCase);
            if (!match.Success) return string.Empty;
            string value = match.Groups["object"].Value;
            int comma = value.IndexOf(',');
            if (comma >= 0) value = value.Substring(0, comma);
            return value.Trim().Replace("[", string.Empty).Replace("]", string.Empty);
        }

        private static string SqliteOperation(string detail)
        {
            string value = (detail ?? string.Empty).Trim();
            if (Regex.IsMatch(value, @"^SCAN\b", RegexOptions.IgnoreCase)) return "Scan";
            if (Regex.IsMatch(value, @"^SEARCH\b", RegexOptions.IgnoreCase)) return "Search";
            if (Regex.IsMatch(value, @"^USE TEMP B-TREE\b", RegexOptions.IgnoreCase)) return "Temporary B-Tree";
            if (Regex.IsMatch(value, @"^COMPOUND QUERY\b", RegexOptions.IgnoreCase)) return "Compound Query";
            if (Regex.IsMatch(value, @"^CO-ROUTINE\b", RegexOptions.IgnoreCase)) return "Co-routine";
            if (Regex.IsMatch(value, @"^MATERIALIZE\b", RegexOptions.IgnoreCase)) return "Materialize";
            if (Regex.IsMatch(value, @"^MULTI-INDEX OR\b", RegexOptions.IgnoreCase)) return "Multi-index OR";
            Match firstWords = Regex.Match(value, @"^([A-Za-z-]+(?:\s+[A-Za-z-]+)?)");
            return firstWords.Success ? firstWords.Groups[1].Value : "Plan Step";
        }

        private static string ExtractSqliteRelation(string detail)
        {
            Match match = Regex.Match(detail ?? string.Empty,
                "^(?:SCAN|SEARCH)(?:\\s+TABLE)?\\s+(?<relation>(?:\"[^\"]+\"|`[^`]+`|\\[[^\\]]+\\]|\\S+))",
                RegexOptions.IgnoreCase);
            if (!match.Success) return string.Empty;
            return match.Groups["relation"].Value.Trim('"', '`', '[', ']');
        }

        private static string ExtractSqliteAccess(string detail)
        {
            Match match = Regex.Match(detail ?? string.Empty, @"\bUSING\s+(?<access>.+)$", RegexOptions.IgnoreCase);
            return match.Success ? "USING " + match.Groups["access"].Value.Trim() : string.Empty;
        }

        private static void CompleteDocument(QueryPlanDocument document)
        {
            foreach (QueryPlanNode root in document.Roots) PopulateAggregateCost(root);
            if (!document.TotalCost.HasValue)
            {
                List<double> costs = document.Roots.Where(root => root.TotalCost.HasValue).Select(root => root.TotalCost.Value).ToList();
                if (costs.Count > 0) document.TotalCost = costs.Max();
            }
            double totalCost = document.TotalCost.GetValueOrDefault();
            double executionTime = document.ExecutionTimeMs.GetValueOrDefault();
            document.NodeCount = 0;
            foreach (QueryPlanNode root in document.Roots)
            {
                ApplySeverity(root, totalCost, executionTime, 0, document);
            }
            document.TextPlan = BuildTextPlan(document);
        }

        private static void PopulateAggregateCost(QueryPlanNode node)
        {
            foreach (QueryPlanNode child in node.Children) PopulateAggregateCost(child);
            if (!node.TotalCost.HasValue && node.Children.Count > 0)
            {
                node.TotalCost = node.Children.Select(child => child.TotalCost ?? 0d).DefaultIfEmpty(0d).Max();
            }
        }

        private static void ApplySeverity(QueryPlanNode node, double totalCost, double executionTime, int depth, QueryPlanDocument document)
        {
            document.NodeCount++;
            double costRatio = totalCost > 0d && node.TotalCost.HasValue ? node.TotalCost.Value / totalCost : 0d;
            double timeRatio = executionTime > 0d && node.ActualTotalTimeMs.HasValue ? node.ActualTotalTimeMs.Value / executionTime : 0d;
            double ratio = Math.Max(costRatio, timeRatio);
            if (depth > 0 && ratio >= 0.5d) node.Severity = QueryPlanSeverity.High;
            else if (depth > 0 && ratio >= 0.2d) node.Severity = QueryPlanSeverity.Medium;
            else node.Severity = QueryPlanSeverity.Normal;

            foreach (QueryPlanNode child in node.Children)
            {
                ApplySeverity(child, totalCost, executionTime, depth + 1, document);
            }
        }

        private static string BuildTextPlan(QueryPlanDocument document)
        {
            StringBuilder text = new StringBuilder();
            foreach (QueryPlanNode root in document.Roots) AppendTextNode(text, root, 0);
            return text.ToString().TrimEnd();
        }

        private static void AppendTextNode(StringBuilder text, QueryPlanNode node, int depth)
        {
            text.Append(new string(' ', depth * 2));
            if (node.Severity == QueryPlanSeverity.High) text.Append("[HIGH] ");
            else if (node.Severity == QueryPlanSeverity.Medium) text.Append("[MEDIUM] ");
            text.Append(node.NodeType ?? "Plan");
            if (!string.IsNullOrWhiteSpace(node.RelationName)) text.Append(" ").Append(node.RelationName);
            if (!string.IsNullOrWhiteSpace(node.AccessType)) text.Append(" [").Append(node.AccessType).Append("]");
            if (node.TotalCost.HasValue) text.Append(" | cost ").Append(FormatNumber(node.TotalCost.Value));
            if (node.EstimatedRows.HasValue) text.Append(" | rows ").Append(FormatNumber(node.EstimatedRows.Value));
            if (node.ActualTotalTimeMs.HasValue) text.Append(" | actual ").Append(FormatNumber(node.ActualTotalTimeMs.Value)).Append(" ms");
            text.AppendLine();
            foreach (QueryPlanNode child in node.Children) AppendTextNode(text, child, depth + 1);
        }

        private static string NormalizeSingleStatement(string sql)
        {
            string statement = (sql ?? string.Empty).Trim();
            if (statement.Length == 0) throw new InvalidOperationException(Localization.T("Query.PlanEmptySql"));

            int separator = FindStatementSeparator(statement);
            if (separator >= 0)
            {
                string tail = statement.Substring(separator + 1).Trim();
                if (tail.Length > 0 && !IsSqlTriviaOnly(tail)) throw new InvalidOperationException(Localization.T("Query.PlanMultipleStatements"));
                statement = statement.Substring(0, separator).TrimEnd();
            }
            if (statement.Length == 0) throw new InvalidOperationException(Localization.T("Query.PlanEmptySql"));
            return statement;
        }

        private static int FindStatementSeparator(string sql)
        {
            char quote = '\0';
            bool lineComment = false;
            bool blockComment = false;
            for (int i = 0; i < sql.Length; i++)
            {
                char c = sql[i];
                char next = i + 1 < sql.Length ? sql[i + 1] : '\0';
                if (lineComment)
                {
                    if (c == '\r' || c == '\n') lineComment = false;
                    continue;
                }
                if (blockComment)
                {
                    if (c == '*' && next == '/') { blockComment = false; i++; }
                    continue;
                }
                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        if (next == quote) { i++; continue; }
                        quote = '\0';
                    }
                    else if (c == '\\' && next != '\0') i++;
                    continue;
                }
                if (c == '-' && next == '-') { lineComment = true; i++; continue; }
                if (c == '/' && next == '*') { blockComment = true; i++; continue; }
                if (c == '\'' || c == '"' || c == '`') { quote = c; continue; }
                if (c == ';') return i;
            }
            return -1;
        }

        private static string GetFirstKeyword(string sql)
        {
            string withoutComments = Regex.Replace(sql ?? string.Empty, @"\A(?:(?:\s+)|(?:--[^\r\n]*(?:\r?\n|\z))|(?:/\*.*?\*/))*", string.Empty, RegexOptions.Singleline);
            Match match = Regex.Match(withoutComments, @"\A\s*([A-Za-z]+)");
            return match.Success ? match.Groups[1].Value.ToUpperInvariant() : string.Empty;
        }

        private static bool IsSqlTriviaOnly(string value)
        {
            return Regex.IsMatch(value ?? string.Empty, @"\A(?:(?:\s+)|(?:--[^\r\n]*(?:\r?\n|\z))|(?:/\*.*?\*/))*\z", RegexOptions.Singleline);
        }

        private static string NormalizeProvider(string provider)
        {
            string value = (provider ?? string.Empty).Trim().ToLowerInvariant();
            if (value == "mariadb") return "mysql";
            if (value == "postgres" || value == "pgsql" || value == "npgsql") return "postgresql";
            if (value == "mssql" || value == "sql server" || value == "sql_server") return "sqlserver";
            if (value == "system.data.sqlite") return "sqlite";
            return value;
        }

        private static double? ReadDouble(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            double value;
            return double.TryParse(Convert.ToString(((JValue)token).Value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                ? value
                : (double?)null;
        }

        private static string ReadText(JToken token, string fallback = "")
        {
            if (token == null || token.Type == JTokenType.Null) return fallback;
            string value = ScalarText(token);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static string ScalarText(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return string.Empty;
            if (token.Type == JTokenType.String) return token.Value<string>() ?? string.Empty;
            return token.ToString(Formatting.None);
        }

        private static string FormatNumber(double value)
        {
            return value.ToString(value == Math.Truncate(value) ? "0" : "0.###", CultureInfo.InvariantCulture);
        }
    }
}
