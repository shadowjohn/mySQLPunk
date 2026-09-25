namespace MySqlPunk.Core.Models;

public enum StatementOutcome
{
    Succeeded,
    Failed,
    RolledBack,
    NotRun
}

public sealed record StatementExecution(int Index, string Sql, StatementOutcome Outcome, string Message, TimeSpan Elapsed);

public sealed record StatementBatchResult(bool UsedTransaction, IReadOnlyList<StatementExecution> Statements)
{
    public bool Succeeded => Statements.All(item => item.Outcome == StatementOutcome.Succeeded);

    public int SucceededCount => Statements.Count(item => item.Outcome == StatementOutcome.Succeeded);

    public StatementExecution? Failure => Statements.FirstOrDefault(item => item.Outcome == StatementOutcome.Failed);

    public string Summary
    {
        get
        {
            if (Succeeded)
            {
                return $"已執行 {Statements.Count} 個語句" + (UsedTransaction ? "（單一交易）。" : "。");
            }

            var failure = Failure;
            var position = failure is null ? string.Empty : $"第 {failure.Index + 1} 句失敗：{failure.Message}";
            return UsedTransaction
                ? $"{position}；交易已回滾，目標沒有任何變更。"
                : $"{position}；前 {SucceededCount} 句已生效（此資料庫的 DDL 無法回滾），其餘未執行。";
        }
    }
}
