using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MySqlPunk.Core.Models;

namespace MySqlPunk.Core.Services;

/// <summary>
/// Builds provider-native EXPLAIN statements and parses their output into a provider-neutral plan tree.
/// Plans are always produced without executing the statement (no ANALYZE, SHOWPLAN only), so explaining an
/// INSERT／UPDATE／DELETE never modifies data.
/// </summary>
public static class QueryPlanService
{
    private static readonly HashSet<string> ExplainableStatements = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "WITH", "INSERT", "UPDATE", "DELETE", "REPLACE"
    };

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    public static bool SupportsProvider(DatabaseProviderKind provider) => provider is
        DatabaseProviderKind.MySql or
        DatabaseProviderKind.PostgreSql or
        DatabaseProviderKind.SqlServer or
        DatabaseProviderKind.Sqlite;

    /// <summary>
    /// Returns the exact statement sent to the server for MySQL／PostgreSQL／SQLite, and the preview text for
    /// SQL Server (whose SHOWPLAN is a session setting rather than a statement prefix).
    /// </summary>
    public static string BuildExplainSql(DatabaseProviderKind provider, string? sql)
    {
        if (!SupportsProvider(provider))
        {
            throw new NotSupportedException($"{provider} 尚未支援執行計畫。");
        }

        var statement = NormalizeSingleStatement(sql);
        var firstKeyword = GetFirstKeyword(statement);
        if (!ExplainableStatements.Contains(firstKeyword))
        {
            throw new InvalidOperationException("執行計畫只支援 SELECT、WITH、INSERT、UPDATE、DELETE 與 REPLACE。");
        }

        return provider switch
        {
            DatabaseProviderKind.MySql => "EXPLAIN FORMAT=JSON " + statement,
            DatabaseProviderKind.SqlServer =>
                "SET SHOWPLAN_ALL ON;" + Environment.NewLine + statement + Environment.NewLine + "SET SHOWPLAN_ALL OFF;",
            DatabaseProviderKind.Sqlite => "EXPLAIN QUERY PLAN " + statement,
            // ANALYZE stays off so explaining a mutation never runs it.
            _ => "EXPLAIN (FORMAT JSON, ANALYZE FALSE, COSTS TRUE, VERBOSE FALSE, BUFFERS FALSE) " + statement
        };
    }

    /// <summary>
    /// TiDB 走 MySQL 協定但不支援 FORMAT=JSON；改用 TiDB 原生的 tidb_json（同樣不執行 statement）。
    /// </summary>
    public static string BuildTiDbExplainSql(string statement) => TiDbExplainPrefix + NormalizeSingleStatement(statement);

    private const string MySqlExplainPrefix = "EXPLAIN FORMAT=JSON ";
    private const string TiDbExplainPrefix = "EXPLAIN FORMAT='tidb_json' ";
    private const string TiDbJsonColumn = "TiDB_JSON";

    /// <summary>Single statement with trailing separator／trivia removed; rejects multi-statement input.</summary>
    public static string NormalizeSingleStatement(string? sql)
    {
        // Leading comments are dropped so the EXPLAIN prefix is never glued onto a `--` line comment.
        var statement = StripLeadingTrivia((sql ?? string.Empty).Trim());
        if (statement.Length == 0)
        {
            throw new InvalidOperationException("請先輸入要解釋的 SQL。");
        }

        var separator = FindStatementSeparator(statement);
        if (separator >= 0)
        {
            var tail = statement[(separator + 1)..].Trim();
            if (tail.Length > 0 && !IsSqlTriviaOnly(tail))
            {
                throw new InvalidOperationException("執行計畫一次只能解釋一個 statement；請反白單一 statement。");
            }

            statement = statement[..separator].TrimEnd();
        }

        if (statement.Length == 0)
        {
            throw new InvalidOperationException("請先輸入要解釋的 SQL。");
        }

        return statement;
    }

    public static QueryPlanDocument Parse(DatabaseProviderKind provider, QueryResult result, string explainSql)
    {
        ArgumentNullException.ThrowIfNull(result);
        return provider switch
        {
            DatabaseProviderKind.SqlServer => ParseSqlServer(result, explainSql),
            DatabaseProviderKind.Sqlite => ParseSqlite(result, explainSql),
            DatabaseProviderKind.MySql when result.Columns.Count == 1 && result.Columns[0].Equals(TiDbJsonColumn, StringComparison.OrdinalIgnoreCase) =>
                ParseTiDbJson(ExtractJson(result), explainSql.StartsWith(MySqlExplainPrefix, StringComparison.Ordinal)
                    ? TiDbExplainPrefix + explainSql[MySqlExplainPrefix.Length..]
                    : explainSql),
            DatabaseProviderKind.MySql or DatabaseProviderKind.PostgreSql => ParseJson(provider, ExtractJson(result), explainSql),
            _ => throw new NotSupportedException($"{provider} 尚未支援執行計畫。")
        };
    }

    public static QueryPlanDocument ParseJson(DatabaseProviderKind provider, string? rawJson, string explainSql = "")
    {
        if (provider is not (DatabaseProviderKind.MySql or DatabaseProviderKind.PostgreSql))
        {
            throw new NotSupportedException($"{provider} 的執行計畫不是 JSON 格式。");
        }

        if (string.IsNullOrWhiteSpace(rawJson))
        {
            throw new InvalidOperationException("伺服器沒有回傳 JSON 執行計畫。");
        }

        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(rawJson, new JsonDocumentOptions { MaxDepth = 256 });
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"執行計畫 JSON 無法解析：{exception.Message}", exception);
        }

        using (json)
        {
            var document = new QueryPlanDocument
            {
                Provider = provider,
                ExplainSql = explainSql,
                RawFormat = provider == DatabaseProviderKind.MySql && IsOceanBasePlan(json.RootElement) ? "OceanBase JSON" : "JSON",
                RawPlan = JsonSerializer.Serialize(json.RootElement, IndentedJson)
            };
            if (provider == DatabaseProviderKind.PostgreSql)
            {
                ParsePostgreSql(document, json.RootElement);
            }
            else if (IsOceanBasePlan(json.RootElement))
            {
                document.Roots.Add(ParseOceanBaseOperator(json.RootElement, 0));
            }
            else
            {
                ParseMySql(document, json.RootElement);
            }

            CompleteDocument(document);
            return document;
        }
    }

    /// <summary>
    /// TiDB tidb_json：陣列中每個運算子有 id（如 TableReader_6、IndexReader_34(Probe)）、estRows、taskType、
    /// accessObject、operatorInfo 與 subOperators。TiDB 的 JSON 不含成本，因此只標示估計列數。
    /// </summary>
    public static QueryPlanDocument ParseTiDbJson(string? rawJson, string explainSql = "")
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            throw new InvalidOperationException("伺服器沒有回傳 TiDB 執行計畫。");
        }

        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(rawJson, new JsonDocumentOptions { MaxDepth = 256 });
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"TiDB 執行計畫 JSON 無法解析：{exception.Message}", exception);
        }

        using (json)
        {
            if (json.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("TiDB 執行計畫根節點不是陣列。");
            }

            var document = new QueryPlanDocument
            {
                Provider = DatabaseProviderKind.MySql,
                ExplainSql = explainSql,
                RawFormat = "TiDB JSON",
                RawPlan = JsonSerializer.Serialize(json.RootElement, IndentedJson)
            };
            foreach (var item in json.RootElement.EnumerateArray())
            {
                document.Roots.Add(ParseTiDbOperator(item, 0));
            }

            if (document.Roots.Count == 0)
            {
                throw new InvalidOperationException("TiDB 執行計畫沒有任何運算子。");
            }

            CompleteDocument(document);
            return document;
        }
    }

    private static QueryPlanNode ParseTiDbOperator(JsonElement obj, int depth)
    {
        if (depth > 128)
        {
            throw new InvalidOperationException("TiDB 執行計畫巢狀過深。");
        }

        if (obj.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("TiDB 執行計畫運算子不是物件。");
        }

        var id = ReadText(obj, "id");
        var role = string.Empty;
        var roleMatch = Regex.Match(id, @"\((?<role>Build|Probe|Seq)\)$");
        if (roleMatch.Success)
        {
            role = roleMatch.Groups["role"].Value;
            id = id[..roleMatch.Index];
        }

        var operation = Regex.Replace(id, @"_\d+$", string.Empty);
        var accessObject = ReadText(obj, "accessObject");
        var relation = Regex.Match(accessObject, @"(?:^|,\s*)table:(?<table>[^,]+)");
        var node = new QueryPlanNode
        {
            NodeType = operation.Length == 0 ? "Operator" : operation,
            // TiDB 以查詢中的別名（沒有別名時是表名）標示資料表。
            RelationName = relation.Success ? relation.Groups["table"].Value.Trim() : string.Empty,
            Alias = relation.Success ? relation.Groups["table"].Value.Trim() : string.Empty,
            AccessType = operation.Contains("Scan", StringComparison.OrdinalIgnoreCase) || operation.Contains("Get", StringComparison.OrdinalIgnoreCase)
                ? operation
                : string.Empty,
            JoinType = operation.Contains("Join", StringComparison.OrdinalIgnoreCase) ? operation : string.Empty,
            EstimatedRows = double.TryParse(ReadText(obj, "estRows"), NumberStyles.Float, CultureInfo.InvariantCulture, out var rows) ? rows : null
        };
        node.Details["id"] = ReadText(obj, "id");
        foreach (var name in new[] { "taskType", "accessObject", "operatorInfo" })
        {
            var value = ReadText(obj, name);
            if (value.Length > 0)
            {
                node.Details[name] = value;
            }
        }

        if (role.Length > 0)
        {
            node.Details["role"] = role;
        }

        if (obj.TryGetProperty("subOperators", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                node.Children.Add(ParseTiDbOperator(child, depth + 1));
            }
        }

        return node;
    }

    /// <summary>OceanBase（MySQL 模式）的 FORMAT=JSON：每個運算子有 ID、OPERATOR、NAME、EST.ROWS、EST.TIME(us) 與 CHILD_n。</summary>
    private static bool IsOceanBasePlan(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("OPERATOR", out _) && !root.TryGetProperty("query_block", out _);

    private static QueryPlanNode ParseOceanBaseOperator(JsonElement obj, int depth)
    {
        if (depth > 128)
        {
            throw new InvalidOperationException("OceanBase 執行計畫巢狀過深。");
        }

        var operation = ReadText(obj, "OPERATOR", "Operator");
        var name = ReadText(obj, "NAME");
        var index = string.Empty;
        var indexMatch = Regex.Match(name, @"^(?<table>[^(]+)\((?<index>[^)]*)\)$");
        if (indexMatch.Success)
        {
            name = indexMatch.Groups["table"].Value;
            index = indexMatch.Groups["index"].Value;
        }

        var node = new QueryPlanNode
        {
            NodeType = operation,
            RelationName = name,
            Alias = name,
            AccessType = operation.Contains("SCAN", StringComparison.OrdinalIgnoreCase) || operation.Contains("GET", StringComparison.OrdinalIgnoreCase) ? operation : string.Empty,
            JoinType = operation.Contains("JOIN", StringComparison.OrdinalIgnoreCase) ? operation : string.Empty,
            EstimatedRows = ReadDouble(obj, "EST.ROWS"),
            // EST.TIME 是含子節點的累計估計時間（微秒），拿來當成本比例標示高成本節點。
            TotalCost = ReadDouble(obj, "EST.TIME(us)")
        };
        if (index.Length > 0)
        {
            node.Details["index"] = index;
        }

        foreach (var property in obj.EnumerateObject())
        {
            if (property.Name.StartsWith("CHILD_", StringComparison.OrdinalIgnoreCase) || property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                continue;
            }

            node.Details[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : property.Value.GetRawText();
        }

        foreach (var child in obj.EnumerateObject()
                     .Where(property => property.Name.StartsWith("CHILD_", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Object)
                     .OrderBy(property => int.TryParse(property.Name[6..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var order) ? order : int.MaxValue))
        {
            node.Children.Add(ParseOceanBaseOperator(child.Value, depth + 1));
        }

        return node;
    }

    public static string ExtractJson(QueryResult result)
    {
        if (result.Columns.Count == 0 || result.Rows.Count == 0)
        {
            return string.Empty;
        }

        var columnIndex = 0;
        for (var index = 0; index < result.Columns.Count; index++)
        {
            var name = result.Columns[index];
            if (name.Equals("EXPLAIN", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("QUERY PLAN", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("QUERY_PLAN", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(TiDbJsonColumn, StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Query Plan", StringComparison.OrdinalIgnoreCase))
            {
                columnIndex = index;
                break;
            }
        }

        var fragments = new List<string>();
        foreach (var row in result.Rows)
        {
            var text = CellText(row[columnIndex]);
            if (!string.IsNullOrWhiteSpace(text))
            {
                fragments.Add(text.Trim());
            }
        }

        return string.Join(Environment.NewLine, fragments);
    }

    private static QueryPlanDocument ParseSqlServer(QueryResult result, string explainSql)
    {
        EnsurePlanRows(result);
        var document = new QueryPlanDocument
        {
            Provider = DatabaseProviderKind.SqlServer,
            ExplainSql = explainSql,
            RawFormat = "SHOWPLAN_ALL",
            RawPlan = SerializeTable(result)
        };

        var nodes = new Dictionary<int, QueryPlanNode>();
        var entries = new List<(int? NodeId, int? ParentId, QueryPlanNode Node)>();
        foreach (var row in result.Rows)
        {
            var nodeId = ReadCellInt(result, row, "NodeId");
            var parentId = ReadCellInt(result, row, "Parent");
            var physical = ReadCellText(result, row, "PhysicalOp");
            var logical = ReadCellText(result, row, "LogicalOp");
            var statementText = ReadCellText(result, row, "StmtText");
            var node = new QueryPlanNode
            {
                NodeType = FirstNonEmpty(physical, logical, ReadCellText(result, row, "Type"), "Statement"),
                RelationName = ExtractSqlServerObject(ReadCellText(result, row, "Argument")),
                AccessType = logical,
                JoinType = logical.Contains("Join", StringComparison.OrdinalIgnoreCase) ? logical : string.Empty,
                StartupCost = SumNullable(ReadCellDouble(result, row, "EstimateIO"), ReadCellDouble(result, row, "EstimateCPU")),
                TotalCost = ReadCellDouble(result, row, "TotalSubtreeCost"),
                EstimatedRows = ReadCellDouble(result, row, "EstimateRows")
            };
            CopyRowDetails(node, result, row);
            if (!string.IsNullOrWhiteSpace(statementText))
            {
                node.Details["StmtText"] = statementText;
            }

            entries.Add((nodeId, parentId, node));
            if (nodeId.HasValue)
            {
                nodes.TryAdd(nodeId.Value, node);
            }
        }

        BuildHierarchy(document, entries, nodes);
        CompleteDocument(document);
        return document;
    }

    private static QueryPlanDocument ParseSqlite(QueryResult result, string explainSql)
    {
        if (result.Rows.Count == 0)
        {
            // SQLite reports no plan steps for trivial statements such as a single-row INSERT ... VALUES.
            var trivial = new QueryPlanDocument
            {
                Provider = DatabaseProviderKind.Sqlite,
                ExplainSql = explainSql,
                RawFormat = "EXPLAIN QUERY PLAN",
                RawPlan = string.Join('\t', result.Columns)
            };
            trivial.Roots.Add(new QueryPlanNode
            {
                NodeType = "No Plan Steps",
                Details = { ["note"] = "SQLite 對這個 statement 沒有回報查詢計畫步驟（例如單列 INSERT ... VALUES）。" }
            });
            CompleteDocument(trivial);
            return trivial;
        }

        EnsurePlanRows(result);
        var document = new QueryPlanDocument
        {
            Provider = DatabaseProviderKind.Sqlite,
            ExplainSql = explainSql,
            RawFormat = "EXPLAIN QUERY PLAN",
            RawPlan = SerializeTable(result)
        };

        var nodes = new Dictionary<int, QueryPlanNode>();
        var entries = new List<(int? NodeId, int? ParentId, QueryPlanNode Node)>();
        foreach (var row in result.Rows)
        {
            var nodeId = ReadCellInt(result, row, "id");
            var parentId = ReadCellInt(result, row, "parent");
            var detail = ReadCellText(result, row, "detail");
            var node = new QueryPlanNode
            {
                NodeType = SqliteOperation(detail),
                RelationName = ExtractSqliteRelation(detail),
                AccessType = ExtractSqliteAccess(detail)
            };
            CopyRowDetails(node, result, row);
            entries.Add((nodeId, parentId, node));
            if (nodeId.HasValue)
            {
                nodes.TryAdd(nodeId.Value, node);
            }
        }

        BuildHierarchy(document, entries, nodes);
        CompleteDocument(document);
        return document;
    }

    private static void ParsePostgreSql(QueryPlanDocument document, JsonElement root)
    {
        var envelope = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().FirstOrDefault()
            : root;
        if (envelope.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("PostgreSQL 執行計畫根節點不是物件。");
        }

        JsonElement plan;
        if (!envelope.TryGetProperty("Plan", out plan) || plan.ValueKind != JsonValueKind.Object)
        {
            if (!envelope.TryGetProperty("Node Type", out _))
            {
                throw new InvalidOperationException("PostgreSQL 執行計畫缺少 Plan 物件。");
            }

            plan = envelope;
        }

        document.PlanningTimeMs = ReadDouble(envelope, "Planning Time");
        document.ExecutionTimeMs = ReadDouble(envelope, "Execution Time");
        document.Roots.Add(ParsePostgreSqlNode(plan, 0));
        document.TotalCost = document.Roots[0].TotalCost;
    }

    private static QueryPlanNode ParsePostgreSqlNode(JsonElement obj, int depth)
    {
        if (depth > 128)
        {
            throw new InvalidOperationException("PostgreSQL 執行計畫巢狀過深。");
        }

        var node = new QueryPlanNode
        {
            NodeType = ReadText(obj, "Node Type", "Plan"),
            RelationName = ReadText(obj, "Relation Name"),
            Alias = ReadText(obj, "Alias"),
            AccessType = ReadText(obj, "Scan Direction"),
            JoinType = ReadText(obj, "Join Type"),
            StartupCost = ReadDouble(obj, "Startup Cost"),
            TotalCost = ReadDouble(obj, "Total Cost"),
            EstimatedRows = ReadDouble(obj, "Plan Rows"),
            ActualRows = ReadDouble(obj, "Actual Rows"),
            ActualTotalTimeMs = ReadDouble(obj, "Actual Total Time")
        };

        foreach (var property in obj.EnumerateObject())
        {
            if (property.Name == "Plans" ||
                property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                continue;
            }

            node.Details[property.Name] = ScalarText(property.Value);
        }

        if (obj.TryGetProperty("Plans", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray().Where(child => child.ValueKind == JsonValueKind.Object))
            {
                node.Children.Add(ParsePostgreSqlNode(child, depth + 1));
            }
        }

        return node;
    }

    private static void ParseMySql(QueryPlanDocument document, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("MySQL 執行計畫根節點不是物件。");
        }

        var hasQueryBlock = root.TryGetProperty("query_block", out var queryBlock) &&
                            queryBlock.ValueKind == JsonValueKind.Object;
        var source = hasQueryBlock ? queryBlock : root;
        document.Roots.Add(ParseMySqlObject(source, hasQueryBlock ? "query_block" : "plan", 0));
        document.TotalCost = FindMySqlQueryCost(source, 0) ?? document.Roots[0].TotalCost;
    }

    private static QueryPlanNode ParseMySqlObject(JsonElement obj, string context, int depth)
    {
        if (depth > 128)
        {
            throw new InvalidOperationException("MySQL 執行計畫巢狀過深。");
        }

        if (context == "query_block")
        {
            // A single-table query block carries "table" directly; keep the query block as the root so the
            // tree shape is stable regardless of whether MySQL wrapped the access in nested_loop.
            var block = new QueryPlanNode { NodeType = FriendlyMySqlOperation(context) };
            ApplyMySqlCost(block, obj);
            CopyMySqlScalarDetails(block, obj);
            if (obj.TryGetProperty("table", out var directTable) && directTable.ValueKind == JsonValueKind.Object)
            {
                block.Children.Add(ParseMySqlObject(obj, "step", depth + 1));
            }
            else
            {
                AddMySqlStructuralChildren(block, obj, depth);
            }

            return block;
        }

        if (obj.TryGetProperty("table", out var table) && table.ValueKind == JsonValueKind.Object)
        {
            var tableNode = new QueryPlanNode
            {
                NodeType = "Table Access",
                RelationName = ReadText(table, "table_name"),
                Alias = ReadText(table, "table_name"),
                AccessType = ReadText(table, "access_type"),
                EstimatedRows = ReadDouble(table, "rows_produced_per_join") ?? ReadDouble(table, "rows_examined_per_scan")
            };
            ApplyMySqlCost(tableNode, table);
            CopyMySqlScalarDetails(tableNode, table);
            AddMySqlStructuralChildren(tableNode, table, depth);
            return tableNode;
        }

        var node = new QueryPlanNode { NodeType = FriendlyMySqlOperation(context) };
        ApplyMySqlCost(node, obj);
        CopyMySqlScalarDetails(node, obj);
        AddMySqlStructuralChildren(node, obj, depth);
        return node;
    }

    private static void AddMySqlStructuralChildren(QueryPlanNode parent, JsonElement obj, int depth)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (!IsMySqlStructuralProperty(property.Name))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                parent.Children.Add(ParseMySqlObject(property.Value, property.Name, depth + 1));
            }
            else if (property.Value.ValueKind == JsonValueKind.Array)
            {
                var collection = new QueryPlanNode { NodeType = FriendlyMySqlOperation(property.Name) };
                foreach (var child in property.Value.EnumerateArray().Where(child => child.ValueKind == JsonValueKind.Object))
                {
                    collection.Children.Add(ParseMySqlObject(child, "step", depth + 1));
                }

                if (collection.Children.Count > 0)
                {
                    parent.Children.Add(collection);
                }
            }
        }
    }

    private static bool IsMySqlStructuralProperty(string name) => name.ToLowerInvariant() is
        "nested_loop" or "ordering_operation" or "grouping_operation" or "duplicates_removal" or "union_result" or
        "query_specifications" or "materialized_from_subquery" or "attached_subqueries" or
        "optimized_away_subqueries" or "buffer_result" or "windowing";

    private static string FriendlyMySqlOperation(string context) => context.ToLowerInvariant() switch
    {
        "query_block" => "Query Block",
        "nested_loop" => "Nested Loop",
        "ordering_operation" => "Sort",
        "grouping_operation" => "Group",
        "duplicates_removal" => "Duplicate Removal",
        "union_result" => "Union Result",
        "query_specifications" => "Query Specifications",
        "materialized_from_subquery" => "Materialize Subquery",
        "attached_subqueries" => "Attached Subqueries",
        "optimized_away_subqueries" => "Optimized-away Subqueries",
        "buffer_result" => "Buffer Result",
        "windowing" => "Window",
        "step" => "Plan Step",
        _ => "Plan"
    };

    private static void ApplyMySqlCost(QueryPlanNode node, JsonElement obj)
    {
        if (!obj.TryGetProperty("cost_info", out var cost) || cost.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        node.StartupCost = ReadDouble(cost, "read_cost");
        node.TotalCost = ReadDouble(cost, "query_cost") ?? ReadDouble(cost, "prefix_cost") ?? ReadDouble(cost, "eval_cost");
        foreach (var property in cost.EnumerateObject())
        {
            node.Details["cost_info." + property.Name] = ScalarText(property.Value);
        }
    }

    private static double? FindMySqlQueryCost(JsonElement obj, int depth)
    {
        if (depth > 128)
        {
            return null;
        }

        if (obj.TryGetProperty("cost_info", out var cost) && cost.ValueKind == JsonValueKind.Object)
        {
            var direct = ReadDouble(cost, "query_cost");
            if (direct.HasValue)
            {
                return direct;
            }
        }

        foreach (var property in obj.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                var nested = FindMySqlQueryCost(property.Value, depth + 1);
                if (nested.HasValue)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static void CopyMySqlScalarDetails(QueryPlanNode node, JsonElement obj)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (property.Name == "cost_info" ||
                property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                continue;
            }

            node.Details[property.Name] = ScalarText(property.Value);
        }
    }

    private static void EnsurePlanRows(QueryResult result)
    {
        if (result.Rows.Count == 0 || result.Columns.Count == 0)
        {
            throw new InvalidOperationException("伺服器沒有回傳執行計畫資料。");
        }
    }

    private static void BuildHierarchy(
        QueryPlanDocument document,
        IEnumerable<(int? NodeId, int? ParentId, QueryPlanNode Node)> entries,
        IReadOnlyDictionary<int, QueryPlanNode> nodes)
    {
        foreach (var (nodeId, parentId, node) in entries)
        {
            if (parentId.HasValue &&
                (!nodeId.HasValue || parentId.Value != nodeId.Value) &&
                nodes.TryGetValue(parentId.Value, out var parent) &&
                !ReferenceEquals(parent, node))
            {
                parent.Children.Add(node);
            }
            else
            {
                document.Roots.Add(node);
            }
        }
    }

    private static void CopyRowDetails(QueryPlanNode node, QueryResult result, IReadOnlyList<object?> row)
    {
        for (var index = 0; index < result.Columns.Count && index < row.Count; index++)
        {
            var value = CellText(row[index]);
            if (!string.IsNullOrWhiteSpace(value))
            {
                node.Details[result.Columns[index]] = value;
            }
        }
    }

    private static string SerializeTable(QueryResult result)
    {
        var output = new StringBuilder();
        output.AppendLine(string.Join('\t', result.Columns));
        foreach (var row in result.Rows)
        {
            output.AppendLine(string.Join(
                '\t',
                row.Select(value => CellText(value).Replace("\r", " ").Replace("\n", " ").Replace("\t", " "))));
        }

        return output.ToString().TrimEnd();
    }

    private static string ReadCellText(QueryResult result, IReadOnlyList<object?> row, string columnName)
    {
        for (var index = 0; index < result.Columns.Count && index < row.Count; index++)
        {
            if (result.Columns[index].Equals(columnName, StringComparison.OrdinalIgnoreCase))
            {
                return CellText(row[index]);
            }
        }

        return string.Empty;
    }

    private static double? ReadCellDouble(QueryResult result, IReadOnlyList<object?> row, string columnName) =>
        double.TryParse(ReadCellText(result, row, columnName), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static int? ReadCellInt(QueryResult result, IReadOnlyList<object?> row, string columnName) =>
        int.TryParse(ReadCellText(result, row, columnName), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static string CellText(object? value) => value switch
    {
        null or DBNull => string.Empty,
        string text => text,
        byte[] bytes => Convert.ToHexString(bytes),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static double? SumNullable(double? first, double? second) =>
        !first.HasValue && !second.HasValue ? null : first.GetValueOrDefault() + second.GetValueOrDefault();

    private static string ExtractSqlServerObject(string argument)
    {
        var match = Regex.Match(argument, @"OBJECT:\s*\((?<object>[^\)]+)\)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return string.Empty;
        }

        var value = match.Groups["object"].Value;
        var comma = value.IndexOf(',');
        if (comma >= 0)
        {
            value = value[..comma];
        }

        return value.Trim().Replace("[", string.Empty).Replace("]", string.Empty);
    }

    private static string SqliteOperation(string detail)
    {
        var value = detail.Trim();
        if (Regex.IsMatch(value, @"^SCAN\b", RegexOptions.IgnoreCase)) return "Scan";
        if (Regex.IsMatch(value, @"^SEARCH\b", RegexOptions.IgnoreCase)) return "Search";
        if (Regex.IsMatch(value, @"^USE TEMP B-TREE\b", RegexOptions.IgnoreCase)) return "Temporary B-Tree";
        if (Regex.IsMatch(value, @"^COMPOUND QUERY\b", RegexOptions.IgnoreCase)) return "Compound Query";
        if (Regex.IsMatch(value, @"^CO-ROUTINE\b", RegexOptions.IgnoreCase)) return "Co-routine";
        if (Regex.IsMatch(value, @"^MATERIALIZE\b", RegexOptions.IgnoreCase)) return "Materialize";
        if (Regex.IsMatch(value, @"^MULTI-INDEX OR\b", RegexOptions.IgnoreCase)) return "Multi-index OR";
        var firstWords = Regex.Match(value, @"^([A-Za-z-]+(?:\s+[A-Za-z-]+)?)");
        return firstWords.Success ? firstWords.Groups[1].Value : "Plan Step";
    }

    private static string ExtractSqliteRelation(string detail)
    {
        var match = Regex.Match(
            detail,
            "^(?:SCAN|SEARCH)(?:\\s+TABLE)?\\s+(?<relation>(?:\"[^\"]+\"|`[^`]+`|\\[[^\\]]+\\]|\\S+))",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["relation"].Value.Trim('"', '`', '[', ']') : string.Empty;
    }

    private static string ExtractSqliteAccess(string detail)
    {
        var match = Regex.Match(detail, @"\bUSING\s+(?<access>.+)$", RegexOptions.IgnoreCase);
        return match.Success ? "USING " + match.Groups["access"].Value.Trim() : string.Empty;
    }

    private static void CompleteDocument(QueryPlanDocument document)
    {
        foreach (var root in document.Roots)
        {
            PopulateAggregateCost(root);
        }

        if (!document.TotalCost.HasValue)
        {
            var costs = document.Roots.Where(root => root.TotalCost.HasValue).Select(root => root.TotalCost!.Value).ToList();
            if (costs.Count > 0)
            {
                document.TotalCost = costs.Max();
            }
        }

        var totalCost = document.TotalCost.GetValueOrDefault();
        var executionTime = document.ExecutionTimeMs.GetValueOrDefault();
        document.NodeCount = 0;
        foreach (var root in document.Roots)
        {
            ApplySeverity(root, totalCost, executionTime, 0, document);
        }

        document.TextPlan = BuildTextPlan(document);
    }

    private static void PopulateAggregateCost(QueryPlanNode node)
    {
        foreach (var child in node.Children)
        {
            PopulateAggregateCost(child);
        }

        if (!node.TotalCost.HasValue && node.Children.Any(child => child.TotalCost.HasValue))
        {
            node.TotalCost = node.Children.Select(child => child.TotalCost ?? 0d).DefaultIfEmpty(0d).Max();
        }
    }

    private static void ApplySeverity(QueryPlanNode node, double totalCost, double executionTime, int depth, QueryPlanDocument document)
    {
        document.NodeCount++;
        var costRatio = totalCost > 0d && node.TotalCost.HasValue ? node.TotalCost.Value / totalCost : 0d;
        var timeRatio = executionTime > 0d && node.ActualTotalTimeMs.HasValue ? node.ActualTotalTimeMs.Value / executionTime : 0d;
        var ratio = Math.Max(costRatio, timeRatio);
        node.Severity = depth > 0 && ratio >= 0.5d ? QueryPlanSeverity.High
            : depth > 0 && ratio >= 0.2d ? QueryPlanSeverity.Medium
            : QueryPlanSeverity.Normal;

        foreach (var child in node.Children)
        {
            ApplySeverity(child, totalCost, executionTime, depth + 1, document);
        }
    }

    private static string BuildTextPlan(QueryPlanDocument document)
    {
        var text = new StringBuilder();
        foreach (var root in document.Roots)
        {
            AppendTextNode(text, root, 0);
        }

        return text.ToString().TrimEnd();
    }

    private static void AppendTextNode(StringBuilder text, QueryPlanNode node, int depth)
    {
        text.Append(' ', depth * 2);
        if (node.Severity == QueryPlanSeverity.High)
        {
            text.Append("[HIGH] ");
        }
        else if (node.Severity == QueryPlanSeverity.Medium)
        {
            text.Append("[MEDIUM] ");
        }

        text.AppendLine(node.Summary);
        foreach (var child in node.Children)
        {
            AppendTextNode(text, child, depth + 1);
        }
    }

    private static int FindStatementSeparator(string sql)
    {
        var quote = '\0';
        var lineComment = false;
        var blockComment = false;
        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';
            if (lineComment)
            {
                if (c is '\r' or '\n')
                {
                    lineComment = false;
                }

                continue;
            }

            if (blockComment)
            {
                if (c == '*' && next == '/')
                {
                    blockComment = false;
                    i++;
                }

                continue;
            }

            if (quote != '\0')
            {
                if (c == quote)
                {
                    if (next == quote)
                    {
                        i++;
                        continue;
                    }

                    quote = '\0';
                }
                else if (c == '\\' && next != '\0')
                {
                    i++;
                }

                continue;
            }

            if (c == '-' && next == '-')
            {
                lineComment = true;
                i++;
                continue;
            }

            if (c == '/' && next == '*')
            {
                blockComment = true;
                i++;
                continue;
            }

            if (c is '\'' or '"' or '`')
            {
                quote = c;
                continue;
            }

            if (c == ';')
            {
                return i;
            }
        }

        return -1;
    }

    private static string StripLeadingTrivia(string sql) =>
        Regex.Replace(
            sql,
            @"\A(?:(?:\s+)|(?:--[^\r\n]*(?:\r?\n|\z))|(?:/\*.*?\*/))*",
            string.Empty,
            RegexOptions.Singleline).TrimStart();

    private static string GetFirstKeyword(string sql)
    {
        var withoutComments = Regex.Replace(
            sql,
            @"\A(?:(?:\s+)|(?:--[^\r\n]*(?:\r?\n|\z))|(?:/\*.*?\*/))*",
            string.Empty,
            RegexOptions.Singleline);
        var match = Regex.Match(withoutComments, @"\A\s*([A-Za-z]+)");
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : string.Empty;
    }

    private static bool IsSqlTriviaOnly(string value) =>
        Regex.IsMatch(value, @"\A(?:(?:\s+)|(?:--[^\r\n]*(?:\r?\n|\z))|(?:/\*.*?\*/))*\z", RegexOptions.Singleline);

    private static double? ReadDouble(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var token))
        {
            return null;
        }

        return token.ValueKind switch
        {
            JsonValueKind.Number => token.TryGetDouble(out var number) ? number : null,
            JsonValueKind.String => double.TryParse(token.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null,
            _ => null
        };
    }

    private static string ReadText(JsonElement obj, string name, string fallback = "")
    {
        if (!obj.TryGetProperty(name, out var token) || token.ValueKind == JsonValueKind.Null)
        {
            return fallback;
        }

        var value = ScalarText(token);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static string ScalarText(JsonElement token) => token.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        JsonValueKind.String => token.GetString() ?? string.Empty,
        _ => token.GetRawText()
    };
}
