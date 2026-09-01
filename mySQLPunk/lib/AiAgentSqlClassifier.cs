using System;
using System.Collections.Generic;
using System.Linq;
using mySQLPunk;

namespace mySQLPunk.lib
{
    /// <summary>AI 代為操作時,一句 SQL 的風險等級。</summary>
    public enum AiSqlRisk
    {
        /// <summary>唯讀(SELECT/SHOW/EXPLAIN 等),可靜默執行。</summary>
        ReadOnly,
        /// <summary>會變更資料或結構(INSERT/UPDATE 含 WHERE/CREATE/ALTER 等),直接執行但全程稽核。</summary>
        Mutating,
        /// <summary>破壞性或不可逆(DROP/TRUNCATE/DELETE、無 WHERE 的 UPDATE 等),執行前必須經使用者確認。</summary>
        Dangerous,
        /// <summary>不接受(空白、多語句),一律拒絕。</summary>
        Blocked
    }

    /// <summary>
    /// AI 代為操作的 SQL 三級分類。與排程工作共用 ScheduledJobValidator 的
    /// 註解/字串感知 tokenizer,確保兩邊對同一句 SQL 的判定一致。
    /// </summary>
    public static class AiAgentSqlClassifier
    {
        /// <summary>出現即視為危險的關鍵字:破壞性 DDL/DML、權限變更,以及可執行任意內容的呼叫。</summary>
        private static readonly HashSet<string> DangerousKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DROP", "TRUNCATE", "DELETE", "GRANT", "REVOKE", "ATTACH", "DETACH",
            // 預存程序可能做任何事;/*! ... */ 可執行註解由 tokenizer 注入 EXECUTE token,一併落入此類
            "CALL", "EXEC", "EXECUTE"
        };

        public static AiSqlRisk Classify(string sql, out string reason)
        {
            reason = string.Empty;
            List<string> allTokens;
            List<string> topLevelTokens;
            bool multipleStatements;
            ScheduledJobValidator.TryGetSqlTokens(sql, out allTokens, out topLevelTokens, out multipleStatements);

            if (allTokens.Count == 0)
            {
                reason = Localization.T("Ai.AgentSqlBlockedEmpty");
                return AiSqlRisk.Blocked;
            }
            if (multipleStatements)
            {
                reason = Localization.T("Ai.AgentSqlBlockedMultiple");
                return AiSqlRisk.Blocked;
            }

            string readOnlyReason;
            if (ScheduledJobValidator.IsReadOnlySql(sql, out readOnlyReason)) return AiSqlRisk.ReadOnly;

            foreach (string token in allTokens)
            {
                if (DangerousKeywords.Contains(token))
                {
                    reason = Localization.Format("Ai.AgentSqlDangerKeyword", token.ToUpperInvariant());
                    return AiSqlRisk.Dangerous;
                }
            }

            string leading = topLevelTokens.Count == 0 ? string.Empty : topLevelTokens[0];
            if (string.Equals(leading, "UPDATE", StringComparison.OrdinalIgnoreCase)
                && !topLevelTokens.Contains("WHERE", StringComparer.OrdinalIgnoreCase))
            {
                reason = Localization.Format("Ai.AgentSqlDangerNoWhere", leading.ToUpperInvariant());
                return AiSqlRisk.Dangerous;
            }

            return AiSqlRisk.Mutating;
        }
    }
}
