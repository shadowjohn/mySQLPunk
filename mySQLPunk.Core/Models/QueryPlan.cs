namespace MySqlPunk.Core.Models;

public enum QueryPlanSeverity
{
    Normal,
    Medium,
    High
}

public sealed class QueryPlanNode
{
    public string NodeType { get; set; } = "Plan";

    public string RelationName { get; set; } = string.Empty;

    public string Alias { get; set; } = string.Empty;

    public string AccessType { get; set; } = string.Empty;

    public string JoinType { get; set; } = string.Empty;

    public double? StartupCost { get; set; }

    public double? TotalCost { get; set; }

    public double? EstimatedRows { get; set; }

    public double? ActualRows { get; set; }

    public double? ActualTotalTimeMs { get; set; }

    public QueryPlanSeverity Severity { get; set; }

    public Dictionary<string, string> Details { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<QueryPlanNode> Children { get; } = new();

    /// <summary>One-line summary used by tree views and the text plan.</summary>
    public string Summary
    {
        get
        {
            var text = new System.Text.StringBuilder(NodeType);
            if (!string.IsNullOrWhiteSpace(RelationName))
            {
                text.Append(' ').Append(RelationName);
            }

            if (!string.IsNullOrWhiteSpace(AccessType))
            {
                text.Append(" [").Append(AccessType).Append(']');
            }

            if (TotalCost.HasValue)
            {
                text.Append(" | cost ").Append(QueryPlanFormatting.Number(TotalCost.Value));
            }

            if (EstimatedRows.HasValue)
            {
                text.Append(" | rows ").Append(QueryPlanFormatting.Number(EstimatedRows.Value));
            }

            if (ActualTotalTimeMs.HasValue)
            {
                text.Append(" | actual ").Append(QueryPlanFormatting.Number(ActualTotalTimeMs.Value)).Append(" ms");
            }

            return text.ToString();
        }
    }
}

public sealed class QueryPlanDocument
{
    public DatabaseProviderKind Provider { get; init; }

    public string ExplainSql { get; init; } = string.Empty;

    /// <summary>Describes <see cref="RawPlan"/>: JSON, SHOWPLAN_ALL or EXPLAIN QUERY PLAN.</summary>
    public string RawFormat { get; init; } = string.Empty;

    public string RawPlan { get; init; } = string.Empty;

    public string TextPlan { get; internal set; } = string.Empty;

    public double? TotalCost { get; internal set; }

    public double? PlanningTimeMs { get; internal set; }

    public double? ExecutionTimeMs { get; internal set; }

    public int NodeCount { get; internal set; }

    public List<QueryPlanNode> Roots { get; } = new();

    public string Summary
    {
        get
        {
            var parts = new List<string> { $"{NodeCount:N0} 個節點" };
            if (TotalCost.HasValue)
            {
                parts.Add($"總成本 {QueryPlanFormatting.Number(TotalCost.Value)}");
            }

            if (PlanningTimeMs.HasValue)
            {
                parts.Add($"規劃 {QueryPlanFormatting.Number(PlanningTimeMs.Value)} ms");
            }

            if (ExecutionTimeMs.HasValue)
            {
                parts.Add($"執行 {QueryPlanFormatting.Number(ExecutionTimeMs.Value)} ms");
            }

            return string.Join("，", parts);
        }
    }
}

public static class QueryPlanFormatting
{
    public static string Number(double value) =>
        value.ToString(value == Math.Truncate(value) ? "0" : "0.###", System.Globalization.CultureInfo.InvariantCulture);
}
