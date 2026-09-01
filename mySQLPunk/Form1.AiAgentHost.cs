using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    /// <summary>
    /// Punky 代為操作的主視窗操作面。集中在獨立 partial 檔,
    /// 讓 AI 工具層只透過 IAiAgentHost 碰主視窗,不直接觸及 Form1 內部。
    /// 所有方法都在 UI 執行緒被呼叫。
    /// </summary>
    public partial class Form1 : IAiAgentHost
    {
        public List<AiAgentConnectionInfo> ListConnections()
        {
            var result = new List<AiAgentConnectionInfo>();
            for (int i = 0; i < myN.connections.Count; i++)
            {
                var connInfo = myN.connections[i];
                result.Add(new AiAgentConnectionInfo
                {
                    Index = i,
                    Name = GetConnectionValue(connInfo, "conn_name"),
                    Kind = GetConnectionValue(connInfo, "db_kind"),
                    Host = GetConnectionValue(connInfo, "host"),
                    IsOpen = IsConnectionOpen(i)
                });
            }
            return result;
        }

        public async Task<bool> OpenConnectionAsync(string connectionName)
        {
            int connIndex = FindAiAgentConnectionIndex(connectionName);
            if (connIndex < 0) return false;
            if (IsConnectionOpen(connIndex)) return true;

            TreeNode connectionNode = FindConnectionNode(connIndex);
            if (connectionNode == null)
            {
                drawLists();
                connectionNode = FindConnectionNode(connIndex);
            }
            if (connectionNode == null) return false;
            return await EnsureConnectionOpenAsync(connIndex, connectionNode);
        }

        public AiAgentDbContext ResolveDatabase(string connectionName, string databaseName)
        {
            int connIndex;
            string dbName;
            if (string.IsNullOrWhiteSpace(connectionName))
            {
                if (!TryGetSelectedConnection(out connIndex, out dbName))
                {
                    throw new InvalidOperationException(Localization.T("Ai.AgentNoTarget"));
                }
            }
            else
            {
                connIndex = FindAiAgentConnectionIndex(connectionName);
                if (connIndex < 0)
                {
                    throw new InvalidOperationException(Localization.Format("Ai.AgentConnectionNotFound", connectionName));
                }
                dbName = "";
            }

            var connInfo = myN.connections[connIndex];
            string name = GetConnectionValue(connInfo, "conn_name");
            if (!IsConnectionOpen(connIndex))
            {
                throw new InvalidOperationException(Localization.Format("Ai.AgentConnectionClosed", name));
            }

            if (!string.IsNullOrWhiteSpace(databaseName)) dbName = databaseName.Trim();
            if (string.IsNullOrWhiteSpace(dbName)) dbName = GetConnectionValue(connInfo, "initial_database");

            IDatabase db = (IDatabase)connInfo["pdo"];
            return new AiAgentDbContext
            {
                Db = db,
                ConnectionName = name,
                DatabaseName = (dbName ?? "").Trim(),
                ProviderName = db.ProviderName,
                HostName = GetConnectionValue(connInfo, "host")
            };
        }

        public void OpenQueryTab(AiAgentDbContext context, string sql)
        {
            OpenQuery(context.Db, context.DatabaseName, context.HostName ?? "", sql, true);
        }

        public async Task<bool> NavigateToObjectAsync(string objectUri)
        {
            ObjectUriParseResult parsed = ObjectUriService.Parse(objectUri);
            if (!parsed.Success) return false;
            return await NavigateToObjectUriAsync(parsed.Target);
        }

        public async Task RefreshTreeAsync(AiAgentDbContext context)
        {
            int connIndex = FindAiAgentConnectionIndex(context.ConnectionName);
            TreeNode connectionNode = connIndex >= 0 ? FindConnectionNode(connIndex) : null;
            if (connectionNode == null)
            {
                drawLists();
                return;
            }

            TreeNode databaseNode = string.IsNullOrWhiteSpace(context.DatabaseName)
                ? null
                : connectionNode.Nodes.Cast<TreeNode>()
                    .FirstOrDefault(n => string.Equals(n.Text, context.DatabaseName, StringComparison.OrdinalIgnoreCase));
            if (databaseNode != null)
            {
                await RefreshDatabaseObjectNodesAsync(databaseNode);
            }
            else
            {
                RefreshConnectionDatabaseNodes(connectionNode);
            }
        }

        public bool ConfirmDangerousSql(AiAgentDbContext context, string sql, string riskReason)
        {
            string target = context.ConnectionName + (string.IsNullOrWhiteSpace(context.DatabaseName) ? "" : " / " + context.DatabaseName);
            return AiAgentDangerConfirmForm.Confirm(this, riskReason, target, sql);
        }

        public void Audit(AiAgentDbContext context, string toolName, string description, string status, long elapsedMs, int rows, bool isQuery)
        {
            string databaseName = context != null ? context.DatabaseName : "";
            bool isSqlTool = toolName == "run_select" || toolName == "execute_sql";
            string entry = isSqlTool ? description : "[agent] " + toolName + ": " + description;
            RecordQueryHistory(databaseName, entry, status, elapsedMs, rows, isQuery);
        }

        /// <summary>以顯示名稱找連線索引;找不到或名稱重複(無法安全鎖定)回 -1。</summary>
        private int FindAiAgentConnectionIndex(string connectionName)
        {
            if (string.IsNullOrWhiteSpace(connectionName)) return -1;
            var matches = Enumerable.Range(0, myN.connections.Count)
                .Where(i => string.Equals(GetConnectionValue(myN.connections[i], "conn_name"), connectionName.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();
            return matches.Count == 1 ? matches[0] : -1;
        }
    }
}
