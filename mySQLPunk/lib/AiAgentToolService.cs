using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using mySQLPunk;

namespace mySQLPunk.lib
{
    /// <summary>
    /// Punky 代為操作的工具分派表。所有工具透過 IAiAgentHost 碰應用程式;
    /// 資料庫工作包在 Task.Run,UI 操作直接呼叫 host(呼叫端在 UI 執行緒)。
    /// 工具目錄文字(BuildToolCatalogPrompt)與實作放同一檔,避免說明與行為漂移。
    /// </summary>
    public static class AiAgentToolService
    {
        /// <summary>run_select 回傳列數上限。</summary>
        public const int SelectRowCap = 200;
        /// <summary>單一工具結果序列化後的字元上限,超過會截斷並標記 truncated。</summary>
        public const int ResultTextCap = 16000;
        /// <summary>模型單一回覆最多執行的動作數。</summary>
        public const int MaxActionsPerTurn = 5;
        /// <summary>單次代為操作任務的動作總數上限。</summary>
        public const int MaxActionsPerRun = 12;

        public const string AuditStatusOk = "AI_AGENT_OK";
        public const string AuditStatusFailed = "AI_AGENT_NO";
        public const string AuditStatusDenied = "AI_AGENT_DENIED";

        public static async Task<AiAgentToolResult> ExecuteAsync(AiAgentAction action, IAiAgentHost host)
        {
            var watch = Stopwatch.StartNew();
            AiAgentToolResult result;
            try
            {
                result = await DispatchAsync(action, host, watch);
            }
            catch (Exception ex)
            {
                result = new AiAgentToolResult
                {
                    Tool = action != null ? action.Tool : null,
                    Ok = false,
                    Error = ExceptionMessageService.GetReason(ex)
                };
            }
            result.ElapsedMs = watch.ElapsedMilliseconds;
            return result;
        }

        private static async Task<AiAgentToolResult> DispatchAsync(AiAgentAction action, IAiAgentHost host, Stopwatch watch)
        {
            if (host == null) throw new ArgumentNullException("host");
            if (action == null || string.IsNullOrWhiteSpace(action.Tool))
            {
                return new AiAgentToolResult
                {
                    Ok = false,
                    Error = Localization.T("Ai.AgentActionInvalidJson")
                };
            }

            string tool = action.Tool.Trim().ToLowerInvariant();
            JObject args = action.Args ?? new JObject();
            string connection = ((string)args["connection"] ?? "").Trim();
            string database = ((string)args["database"] ?? "").Trim();
            if (connection.Length == 0) connection = null;
            if (database.Length == 0) database = null;

            switch (tool)
            {
                case "list_connections":
                    return ListConnections(host, tool);
                case "open_connection":
                    return await OpenConnectionAsync(host, tool, connection, watch);
                case "list_databases":
                    return await ListDatabasesAsync(host, tool, connection);
                case "describe_schema":
                    return await DescribeSchemaAsync(host, tool, connection, database);
                case "get_ddl":
                    return await GetDdlAsync(host, tool, connection, database, args);
                case "run_select":
                    return await RunSelectAsync(host, tool, connection, database, args, watch);
                case "explain_query":
                    return await ExplainQueryAsync(host, tool, connection, database, args);
                case "execute_sql":
                    return await ExecuteSqlAsync(host, tool, connection, database, args, watch);
                case "open_query_tab":
                    return OpenQueryTab(host, tool, connection, database, args, watch);
                case "navigate_to_object":
                    return await NavigateToObjectAsync(host, tool, args);
                case "refresh_tree":
                    return await RefreshTreeAsync(host, tool, connection, database);
                default:
                    return new AiAgentToolResult
                    {
                        Tool = action.Tool,
                        Ok = false,
                        Error = Localization.Format("Ai.AgentUnknownTool", action.Tool)
                    };
            }
        }

        // ── 唯讀工具 ─────────────────────────────────────────────

        private static AiAgentToolResult ListConnections(IAiAgentHost host, string tool)
        {
            var data = new JArray();
            foreach (AiAgentConnectionInfo info in host.ListConnections())
            {
                data.Add(new JObject
                {
                    ["name"] = info.Name,
                    ["engine"] = info.Kind,
                    ["host"] = info.Host,
                    ["open"] = info.IsOpen
                });
            }
            return new AiAgentToolResult { Tool = tool, Ok = true, Summary = data.Count + " connections", Data = data };
        }

        private static async Task<AiAgentToolResult> OpenConnectionAsync(IAiAgentHost host, string tool, string connection, Stopwatch watch)
        {
            if (connection == null) return MissingArg(tool, "connection");
            bool opened = await host.OpenConnectionAsync(connection);
            host.Audit(null, tool, connection, opened ? AuditStatusOk : AuditStatusFailed, watch.ElapsedMilliseconds, 0, false);
            if (!opened)
            {
                return new AiAgentToolResult { Tool = tool, Ok = false, Error = Localization.Format("Ai.AgentConnectionNotFound", connection) };
            }
            return new AiAgentToolResult { Tool = tool, Ok = true, Summary = connection };
        }

        private static async Task<AiAgentToolResult> ListDatabasesAsync(IAiAgentHost host, string tool, string connection)
        {
            AiAgentDbContext ctx = host.ResolveDatabase(connection, null);
            List<string> databases = await Task.Run(() => ctx.Db.GetDatabases());
            return new AiAgentToolResult
            {
                Tool = tool,
                Ok = true,
                Summary = (databases != null ? databases.Count : 0) + " databases",
                Data = databases != null ? JArray.FromObject(databases) : new JArray()
            };
        }

        private static async Task<AiAgentToolResult> DescribeSchemaAsync(IAiAgentHost host, string tool, string connection, string database)
        {
            AiAgentDbContext ctx = host.ResolveDatabase(connection, database);
            SchemaModelSnapshot snapshot = await Task.Run(() => SchemaModelService.Load(ctx.Db, ctx.DatabaseName));
            var sb = new StringBuilder();
            foreach (SchemaTableModel table in snapshot.Tables)
            {
                sb.Append(table.Name).Append('(');
                for (int i = 0; i < table.Columns.Count; i++)
                {
                    SchemaColumnModel column = table.Columns[i];
                    if (i > 0) sb.Append(", ");
                    sb.Append(column.Name).Append(' ').Append(column.DataType);
                    if (column.IsPrimaryKey) sb.Append(" PK");
                    if (!column.IsNullable) sb.Append(" NOT NULL");
                }
                sb.AppendLine(")");
            }
            foreach (SchemaRelationshipModel rel in snapshot.Relationships)
            {
                sb.AppendLine("FK: " + rel.FromTable + "." + rel.FromColumn + " -> " + rel.ToTable + "." + rel.ToColumn);
            }

            bool truncated = sb.Length > ResultTextCap;
            string text = truncated ? sb.ToString(0, ResultTextCap) : sb.ToString();
            return new AiAgentToolResult
            {
                Tool = tool,
                Ok = true,
                Summary = snapshot.Tables.Count + " tables",
                Data = new JObject { ["schema"] = text.TrimEnd() },
                Truncated = truncated
            };
        }

        private static async Task<AiAgentToolResult> GetDdlAsync(IAiAgentHost host, string tool, string connection, string database, JObject args)
        {
            string objectName = ((string)args["object"] ?? "").Trim();
            if (objectName.Length == 0) return MissingArg(tool, "object");
            string type = ((string)args["type"] ?? "table").Trim().ToLowerInvariant();

            AiAgentDbContext ctx = host.ResolveDatabase(connection, database);
            string ddl = await Task.Run(() =>
            {
                if (type == "view") return ctx.Db.GetViewCreateStatement(ctx.DatabaseName, objectName);
                return ctx.Db.GetTableCreateStatement(ctx.DatabaseName, objectName);
            });
            if (string.IsNullOrWhiteSpace(ddl))
            {
                return new AiAgentToolResult { Tool = tool, Ok = false, Error = Localization.Format("Ai.AgentObjectNotFound", objectName) };
            }
            bool truncated = ddl.Length > ResultTextCap;
            if (truncated) ddl = ddl.Substring(0, ResultTextCap);
            return new AiAgentToolResult { Tool = tool, Ok = true, Summary = objectName, Data = new JObject { ["ddl"] = ddl }, Truncated = truncated };
        }

        private static async Task<AiAgentToolResult> RunSelectAsync(IAiAgentHost host, string tool, string connection, string database, JObject args, Stopwatch watch)
        {
            string sql = ((string)args["sql"] ?? "").Trim();
            if (sql.Length == 0) return MissingArg(tool, "sql");

            string riskReason;
            if (AiAgentSqlClassifier.Classify(sql, out riskReason) != AiSqlRisk.ReadOnly)
            {
                return new AiAgentToolResult
                {
                    Tool = tool,
                    Ok = false,
                    Error = Localization.Format("Ai.AgentSelectOnly", string.IsNullOrEmpty(riskReason) ? sql : riskReason)
                };
            }

            AiAgentDbContext ctx = host.ResolveDatabase(connection, database);
            try
            {
                DataTable table = await Task.Run(() => ctx.Db.SelectSQL(sql));
                string queryError = GetSelectError(table);
                if (queryError != null)
                {
                    host.Audit(ctx, tool, sql, AuditStatusFailed, watch.ElapsedMilliseconds, 0, true);
                    return new AiAgentToolResult { Tool = tool, Ok = false, Error = queryError };
                }

                var columns = new JArray();
                foreach (DataColumn column in table.Columns) columns.Add(column.ColumnName);
                var rows = new JArray();
                int total = table.Rows.Count;
                int taken = Math.Min(total, SelectRowCap);
                for (int i = 0; i < taken; i++)
                {
                    var row = new JArray();
                    foreach (object cell in table.Rows[i].ItemArray)
                    {
                        row.Add(cell == null || cell == DBNull.Value ? null : (JToken)Convert.ToString(cell));
                    }
                    rows.Add(row);
                }

                host.Audit(ctx, tool, sql, AuditStatusOk, watch.ElapsedMilliseconds, total, true);
                var result = new AiAgentToolResult
                {
                    Tool = tool,
                    Ok = true,
                    Summary = total + " rows",
                    Data = new JObject { ["columns"] = columns, ["rows"] = rows },
                    Truncated = total > taken
                };
                ClampResultData(result);
                return result;
            }
            catch (Exception ex)
            {
                host.Audit(ctx, tool, sql, AuditStatusFailed, watch.ElapsedMilliseconds, 0, true);
                return new AiAgentToolResult { Tool = tool, Ok = false, Error = ExceptionMessageService.GetReason(ex) };
            }
        }

        private static async Task<AiAgentToolResult> ExplainQueryAsync(IAiAgentHost host, string tool, string connection, string database, JObject args)
        {
            string sql = ((string)args["sql"] ?? "").Trim();
            if (sql.Length == 0) return MissingArg(tool, "sql");

            AiAgentDbContext ctx = host.ResolveDatabase(connection, database);
            QueryPlanDocument plan = await Task.Run(() => QueryPlanService.Execute(ctx.Db, sql));
            string text = !string.IsNullOrWhiteSpace(plan.TextPlan) ? plan.TextPlan : plan.RawPlan;
            bool truncated = text != null && text.Length > ResultTextCap;
            if (truncated) text = text.Substring(0, ResultTextCap);
            return new AiAgentToolResult
            {
                Tool = tool,
                Ok = true,
                Summary = plan.NodeCount + " plan nodes",
                Data = new JObject { ["plan"] = text ?? "" },
                Truncated = truncated
            };
        }

        // ── 變更工具(安全閘門)────────────────────────────────────

        private static async Task<AiAgentToolResult> ExecuteSqlAsync(IAiAgentHost host, string tool, string connection, string database, JObject args, Stopwatch watch)
        {
            string sql = ((string)args["sql"] ?? "").Trim();
            if (sql.Length == 0) return MissingArg(tool, "sql");

            string riskReason;
            AiSqlRisk risk = AiAgentSqlClassifier.Classify(sql, out riskReason);
            if (risk == AiSqlRisk.Blocked)
            {
                return new AiAgentToolResult { Tool = tool, Ok = false, Error = riskReason };
            }

            AiAgentDbContext ctx = host.ResolveDatabase(connection, database);
            if (risk == AiSqlRisk.Dangerous)
            {
                if (!host.ConfirmDangerousSql(ctx, sql, riskReason))
                {
                    host.Audit(ctx, tool, sql, AuditStatusDenied, watch.ElapsedMilliseconds, 0, false);
                    return new AiAgentToolResult { Tool = tool, Ok = false, Error = Localization.T("Ai.AgentUserDenied") };
                }
            }

            if (risk == AiSqlRisk.ReadOnly)
            {
                // 模型偶爾會把查詢丟進 execute_sql;直接轉唯讀路徑,拿得到結果而不是 rowsAffected=0
                return await RunSelectAsync(host, tool, connection, database, args, watch);
            }

            Dictionary<string, string> outcome = await Task.Run(() => ctx.Db.ExecSQL(sql));
            bool ok = outcome != null && outcome.ContainsKey("status") && outcome["status"] == "OK";
            int rowsAffected = 0;
            if (ok && outcome.ContainsKey("rowsAffected")) int.TryParse(outcome["rowsAffected"], out rowsAffected);

            host.Audit(ctx, tool, sql, ok ? AuditStatusOk : AuditStatusFailed, watch.ElapsedMilliseconds, rowsAffected, false);
            if (!ok)
            {
                return new AiAgentToolResult
                {
                    Tool = tool,
                    Ok = false,
                    Error = DatabaseExecutionResultService.GetFailureReason(outcome)
                };
            }
            return new AiAgentToolResult
            {
                Tool = tool,
                Ok = true,
                Summary = Localization.Format("Ai.AgentRowsAffected", rowsAffected),
                Data = new JObject { ["rows_affected"] = rowsAffected }
            };
        }

        // ── 應用程式 UI 工具 ─────────────────────────────────────

        private static AiAgentToolResult OpenQueryTab(IAiAgentHost host, string tool, string connection, string database, JObject args, Stopwatch watch)
        {
            string sql = ((string)args["sql"] ?? "").Trim();
            if (sql.Length == 0) return MissingArg(tool, "sql");
            AiAgentDbContext ctx = host.ResolveDatabase(connection, database);
            host.OpenQueryTab(ctx, sql);
            host.Audit(ctx, tool, sql, AuditStatusOk, watch.ElapsedMilliseconds, 0, false);
            return new AiAgentToolResult { Tool = tool, Ok = true, Summary = ctx.DatabaseName };
        }

        private static async Task<AiAgentToolResult> NavigateToObjectAsync(IAiAgentHost host, string tool, JObject args)
        {
            string uri = ((string)args["uri"] ?? "").Trim();
            if (uri.Length == 0) return MissingArg(tool, "uri");
            ObjectUriParseResult parsed = ObjectUriService.Parse(uri);
            if (!parsed.Success)
            {
                return new AiAgentToolResult { Tool = tool, Ok = false, Error = Localization.Format("Ai.AgentInvalidUri", parsed.Error.ToString()) };
            }
            bool navigated = await host.NavigateToObjectAsync(uri);
            return new AiAgentToolResult
            {
                Tool = tool,
                Ok = navigated,
                Summary = navigated ? uri : null,
                Error = navigated ? null : Localization.Format("Ai.AgentNavigateFailed", uri)
            };
        }

        private static async Task<AiAgentToolResult> RefreshTreeAsync(IAiAgentHost host, string tool, string connection, string database)
        {
            AiAgentDbContext ctx = host.ResolveDatabase(connection, database);
            await host.RefreshTreeAsync(ctx);
            return new AiAgentToolResult { Tool = tool, Ok = true, Summary = ctx.DatabaseName ?? ctx.ConnectionName };
        }

        // ── 共用 ─────────────────────────────────────────────────

        private static AiAgentToolResult MissingArg(string tool, string argName)
        {
            return new AiAgentToolResult
            {
                Tool = tool,
                Ok = false,
                Error = Localization.Format("Ai.AgentMissingArg", argName)
            };
        }

        /// <summary>SQLite 的查詢錯誤放在 ExtendedProperties,不會丟例外;其餘 provider 直接丟。</summary>
        private static string GetSelectError(DataTable table)
        {
            if (table == null) return Localization.T("Common.SqlExecutionFailed");
            try
            {
                object error = table.ExtendedProperties[my_sqlite.QueryErrorExtendedProperty];
                if (error != null && !string.IsNullOrWhiteSpace(error.ToString())) return error.ToString();
            }
            catch { }
            return null;
        }

        /// <summary>結果序列化後過大時,砍列數直到符合上限,並標記 truncated。</summary>
        private static void ClampResultData(AiAgentToolResult result)
        {
            if (result.Data == null) return;
            JArray rows = result.Data["rows"] as JArray;
            string serialized = result.Data.ToString(Formatting.None);
            while (serialized.Length > ResultTextCap && rows != null && rows.Count > 0)
            {
                int remove = Math.Max(1, rows.Count / 4);
                for (int i = 0; i < remove && rows.Count > 0; i++) rows.RemoveAt(rows.Count - 1);
                result.Truncated = true;
                serialized = result.Data.ToString(Formatting.None);
            }
        }

        /// <summary>工具目錄(zh),接在 Ai.AgentSystemPrompt 後注入 system prompt。</summary>
        public static string BuildToolCatalogPrompt()
        {
            var sb = new StringBuilder();
            sb.AppendLine("可用工具:");
            sb.AppendLine("- list_connections:列出既有連線(名稱、引擎、主機、是否已開啟)。args:無");
            sb.AppendLine("- open_connection:開啟既有連線。args:{\"connection\":\"連線名稱\"}");
            sb.AppendLine("- list_databases:列出資料庫。args:{\"connection\":\"可省略\"}");
            sb.AppendLine("- describe_schema:取得資料表、欄位、主鍵與外鍵摘要。args:{\"connection\":\"可省略\",\"database\":\"可省略\"}");
            sb.AppendLine("- get_ddl:取得單一物件的 CREATE 語句。args:{\"object\":\"名稱\",\"type\":\"table|view\",\"database\":\"可省略\"}");
            sb.AppendLine("- run_select:執行唯讀查詢(最多回傳 " + SelectRowCap + " 列)。args:{\"sql\":\"SELECT ...\",\"database\":\"可省略\"}");
            sb.AppendLine("- explain_query:取得查詢執行計畫(不會真的執行語句)。args:{\"sql\":\"...\",\"database\":\"可省略\"}");
            sb.AppendLine("- execute_sql:執行一句變更 SQL(INSERT/UPDATE/CREATE 等;DROP/TRUNCATE/DELETE 與無 WHERE 的 UPDATE 會先徵求使用者同意)。args:{\"sql\":\"...\",\"database\":\"可省略\"}");
            sb.AppendLine("- open_query_tab:開新查詢分頁並帶入 SQL(只放進編輯器,不執行)。args:{\"sql\":\"...\",\"database\":\"可省略\"}");
            sb.AppendLine("- navigate_to_object:在物件樹選取指定物件。args:{\"uri\":\"mysqlpunk://object?connection=...&database=...&type=table&name=...\"}");
            sb.Append("- refresh_tree:重新整理物件樹。args:{\"connection\":\"可省略\",\"database\":\"可省略\"}");
            return sb.ToString();
        }
    }
}
