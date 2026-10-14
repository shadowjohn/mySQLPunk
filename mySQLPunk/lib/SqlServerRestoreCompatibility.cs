using System;
using System.Data;
using System.Globalization;

namespace mySQLPunk.lib
{
    public static class SqlServerRestoreCompatibility
    {
        public static void Validate(DataTable headers, string targetVersion)
        {
            if (!Version.TryParse(targetVersion, out var target) || headers == null || headers.Rows.Count != 1 ||
                !headers.Columns.Contains("SoftwareVersionMajor") || !headers.Columns.Contains("BackupType"))
                throw new InvalidOperationException("無法確認備份或目標引擎版本，已停止還原。請選擇單一完整備份並改用受控邏輯搬移。");
            int source = Convert.ToInt32(headers.Rows[0]["SoftwareVersionMajor"], CultureInfo.InvariantCulture);
            if (source < 8 || source > target.Major)
                throw new InvalidOperationException("新版 SQL Server 的 .bak 不能還原到舊引擎；compatibility level 不會改變備份格式。請改用相容搬移預檢。");
            if (Convert.ToInt32(headers.Rows[0]["BackupType"], CultureInfo.InvariantCulture) != 1)
                throw new InvalidOperationException("此介面僅支援單一完整資料庫備份；差異或交易記錄備份需由 DBA 規劃還原鏈。");
        }
    }
}
