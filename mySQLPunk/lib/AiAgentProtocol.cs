using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using mySQLPunk;

namespace mySQLPunk.lib
{
    /// <summary>模型要求執行的一個工具動作。Tool == null 代表 JSON 解析失敗,RawJson 保留原文供矯正回饋。</summary>
    public sealed class AiAgentAction
    {
        public string Tool;
        public JObject Args;
        public string Why;
        public string RawJson;
    }

    /// <summary>punky-plan 清單中的一個可勾選項目;Action 一定非 null(sql 簡寫已正規化為 execute_sql)。</summary>
    public sealed class AiAgentPlanItem
    {
        public int Id;
        public string Note;
        public AiAgentAction Action;

        /// <summary>清單卡片要顯示的 SQL 預覽;非 SQL 動作回 null。</summary>
        public string SqlText
        {
            get
            {
                if (Action == null || Action.Args == null) return null;
                return (string)Action.Args["sql"];
            }
        }
    }

    public sealed class AiAgentPlan
    {
        public string Title;
        public readonly List<AiAgentPlanItem> Items = new List<AiAgentPlanItem>();
    }

    /// <summary>一個工具動作的執行結果,會序列化進 punky-result 信封回饋給模型。</summary>
    public sealed class AiAgentToolResult
    {
        public string Tool;
        public bool Ok;
        public string Summary;
        public string Error;
        public JToken Data;
        public long ElapsedMs;
        public bool Truncated;
    }

    /// <summary>
    /// Punky 代為操作的文字協定:模型以 ```punky-action / ```punky-plan fenced 區塊溝通,
    /// 應用程式以 ```punky-result 區塊回傳結果。統一走文字是因為 CLI 供應商
    /// (無金鑰使用者的預設後端)只有 string-in/string-out,沒有原生 tool-calling。
    /// </summary>
    public static class AiAgentProtocol
    {
        public const string ActionFenceTag = "punky-action";
        public const string PlanFenceTag = "punky-plan";
        public const string ResultFenceTag = "punky-result";

        /// <summary>依出現順序取出回覆中所有 punky-action。壞 JSON 不丟例外,回 Tool==null 的載體讓迴圈能回饋矯正。</summary>
        public static List<AiAgentAction> ParseActions(string reply)
        {
            var actions = new List<AiAgentAction>();
            foreach (string block in ExtractFencedBlocks(reply, ActionFenceTag))
            {
                actions.Add(ParseAction(block));
            }
            return actions;
        }

        private static AiAgentAction ParseAction(string json)
        {
            try
            {
                JObject parsed = JObject.Parse(json);
                string tool = ((string)parsed["tool"] ?? "").Trim();
                if (tool.Length == 0) return new AiAgentAction { RawJson = json };
                return new AiAgentAction
                {
                    Tool = tool,
                    Args = parsed["args"] as JObject ?? new JObject(),
                    Why = (string)parsed["why"],
                    RawJson = json
                };
            }
            catch
            {
                return new AiAgentAction { RawJson = json };
            }
        }

        /// <summary>取回覆中最後一個 punky-plan(與 ExtractLastSqlBlock 同語意)。沒有有效項目回 null。</summary>
        public static AiAgentPlan ParsePlan(string reply)
        {
            List<string> blocks = ExtractFencedBlocks(reply, PlanFenceTag);
            for (int i = blocks.Count - 1; i >= 0; i--)
            {
                AiAgentPlan plan = ParsePlanBlock(blocks[i]);
                if (plan != null) return plan;
            }
            return null;
        }

        private static AiAgentPlan ParsePlanBlock(string json)
        {
            try
            {
                JObject parsed = JObject.Parse(json);
                if (!(parsed["items"] is JArray items)) return null;
                var plan = new AiAgentPlan { Title = (string)parsed["title"] };
                foreach (JToken token in items)
                {
                    if (!(token is JObject item)) continue;
                    AiAgentAction action = NormalizePlanItemAction(item);
                    if (action == null) continue;
                    plan.Items.Add(new AiAgentPlanItem
                    {
                        Id = item["id"] != null && item["id"].Type == JTokenType.Integer ? (int)item["id"] : plan.Items.Count + 1,
                        Note = ((string)item["note"] ?? "").Trim(),
                        Action = action
                    });
                }
                return plan.Items.Count > 0 ? plan : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>{"sql":"..."} 簡寫 ≡ execute_sql;item 上的 connection/database 一併帶進 args。</summary>
        private static AiAgentAction NormalizePlanItemAction(JObject item)
        {
            AiAgentAction action = null;
            if (item["action"] is JObject explicitAction)
            {
                action = ParseAction(explicitAction.ToString(Formatting.None));
                if (action.Tool == null) return null;
            }
            else
            {
                string sql = ((string)item["sql"] ?? "").Trim();
                if (sql.Length == 0) return null;
                action = new AiAgentAction { Tool = "execute_sql", Args = new JObject { ["sql"] = sql } };
            }

            foreach (string key in new[] { "connection", "database" })
            {
                string value = ((string)item[key] ?? "").Trim();
                if (value.Length > 0 && action.Args[key] == null) action.Args[key] = value;
            }
            return action;
        }

        /// <summary>把一批工具結果包成回饋模型的 user 訊息(明示為系統資料、非使用者輸入)。</summary>
        public static string BuildToolResultMessage(IList<AiAgentToolResult> results)
        {
            var array = new JArray();
            foreach (AiAgentToolResult result in results)
            {
                var item = new JObject
                {
                    ["tool"] = result.Tool,
                    ["ok"] = result.Ok,
                    ["elapsed_ms"] = result.ElapsedMs
                };
                if (!string.IsNullOrEmpty(result.Summary)) item["summary"] = result.Summary;
                if (!string.IsNullOrEmpty(result.Error)) item["error"] = result.Error;
                if (result.Data != null) item["data"] = result.Data;
                if (result.Truncated) item["truncated"] = true;
                array.Add(item);
            }

            var sb = new StringBuilder();
            sb.AppendLine(Localization.T("Ai.AgentResultIntro"));
            sb.AppendLine("```" + ResultFenceTag);
            sb.AppendLine(array.ToString(Formatting.None));
            sb.AppendLine("```");
            sb.Append(Localization.T("Ai.AgentResultOutro"));
            return sb.ToString();
        }

        /// <summary>去掉協定區塊後的回覆文字,供聊天泡泡顯示。</summary>
        public static string StripProtocolBlocks(string reply)
        {
            string value = reply ?? "";
            var sb = new StringBuilder();
            string[] lines = value.Replace("\r\n", "\n").Split('\n');
            bool inProtocolFence = false;
            foreach (string line in lines)
            {
                string trimmed = line.TrimStart();
                if (!inProtocolFence && IsProtocolFenceStart(trimmed))
                {
                    inProtocolFence = true;
                    continue;
                }
                if (inProtocolFence)
                {
                    if (trimmed.StartsWith("```", StringComparison.Ordinal)) inProtocolFence = false;
                    continue;
                }
                sb.AppendLine(line);
            }
            return sb.ToString().Trim();
        }

        private static bool IsProtocolFenceStart(string trimmedLine)
        {
            if (!trimmedLine.StartsWith("```", StringComparison.Ordinal)) return false;
            string tag = trimmedLine.Substring(3).Trim();
            return string.Equals(tag, ActionFenceTag, StringComparison.OrdinalIgnoreCase)
                || string.Equals(tag, PlanFenceTag, StringComparison.OrdinalIgnoreCase)
                || string.Equals(tag, ResultFenceTag, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>取出所有指定 tag 的 fenced 區塊內文(依出現順序)。容忍開頭 fence 同行帶內容與缺結尾 fence。</summary>
        private static List<string> ExtractFencedBlocks(string reply, string tag)
        {
            var blocks = new List<string>();
            string value = reply ?? "";
            string[] lines = value.Replace("\r\n", "\n").Split('\n');
            StringBuilder current = null;
            foreach (string line in lines)
            {
                string trimmed = line.TrimStart();
                if (current == null)
                {
                    if (!trimmed.StartsWith("```", StringComparison.Ordinal)) continue;
                    string rest = trimmed.Substring(3).TrimStart();
                    if (!rest.StartsWith(tag, StringComparison.OrdinalIgnoreCase)) continue;
                    string remainder = rest.Substring(tag.Length).Trim();
                    current = new StringBuilder();
                    if (remainder.Length > 0) current.AppendLine(remainder);
                    continue;
                }
                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    blocks.Add(current.ToString().Trim());
                    current = null;
                    continue;
                }
                current.AppendLine(line);
            }
            if (current != null)
            {
                string tail = current.ToString().Trim();
                if (tail.Length > 0) blocks.Add(tail);
            }
            return blocks;
        }
    }
}
