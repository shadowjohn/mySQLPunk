using System.Collections.Generic;
using System.Threading.Tasks;

namespace mySQLPunk.lib
{
    /// <summary>給 AI 的連線摘要。只含非敏感欄位，絕不放帳號、密碼或連線字串。</summary>
    public sealed class AiAgentConnectionInfo
    {
        public int Index;
        public string Name;
        public string Kind;
        public string Host;
        public bool IsOpen;
    }

    /// <summary>一次工具執行的目標:已開啟連線的 IDatabase 與資料庫名稱。</summary>
    public sealed class AiAgentDbContext
    {
        public IDatabase Db;
        public string ConnectionName;
        public string DatabaseName;
        public string ProviderName;
        /// <summary>連線主機顯示字串（開查詢分頁的標題用），非敏感。</summary>
        public string HostName;
    }

    /// <summary>
    /// Punky 代為操作時,需要主視窗（UI 執行緒）配合的操作面。由 Form1 實作;
    /// AiAgentToolService 只透過這個介面碰應用程式,讓工具層可以用假實作進 SmokeTests。
    /// 所有方法都假設在 UI 執行緒被呼叫。
    /// </summary>
    public interface IAiAgentHost
    {
        /// <summary>列出既有連線（非敏感欄位）。</summary>
        List<AiAgentConnectionInfo> ListConnections();

        /// <summary>開啟指定名稱的既有連線。找不到或開啟失敗回 false，不會建立新連線或要求憑證。</summary>
        Task<bool> OpenConnectionAsync(string connectionName);

        /// <summary>
        /// 解析工具目標。connectionName/databaseName 為 null 時採用物件樹目前選取。
        /// 解析不到或連線未開啟時丟出帶本地化訊息的 InvalidOperationException。
        /// </summary>
        AiAgentDbContext ResolveDatabase(string connectionName, string databaseName);

        /// <summary>以指定目標開新查詢分頁並帶入 SQL（只放進編輯器，不執行）。</summary>
        void OpenQueryTab(AiAgentDbContext context, string sql);

        /// <summary>導覽到 mysqlpunk:// 物件 URI（只選取樹節點，不變更）。</summary>
        Task<bool> NavigateToObjectAsync(string objectUri);

        /// <summary>重新整理指定目標的物件樹。</summary>
        Task RefreshTreeAsync(AiAgentDbContext context);

        /// <summary>危險 SQL 的模態確認。回 false 代表使用者拒絕。</summary>
        bool ConfirmDangerousSql(AiAgentDbContext context, string sql, string riskReason);

        /// <summary>把一次工具執行寫入查詢歷史/診斷紀錄（含被拒絕的執行）。</summary>
        void Audit(AiAgentDbContext context, string toolName, string description, string status, long elapsedMs, int rows, bool isQuery);
    }
}
