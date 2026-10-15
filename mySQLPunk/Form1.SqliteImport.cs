using System;
using System.Collections.Generic;
using System.Windows.Forms;
using mySQLPunk.lib;
using mySQLPunk.lib.SqliteImport;
using SqliteImportResult = mySQLPunk.lib.SqliteImport.SqliteImportResult;

namespace mySQLPunk
{
    public partial class Form1
    {
        internal Func<IDatabase, string, string, SqliteImportResult> SqliteImportWizardRunner = null;
        internal Action<string> SqliteImportStatusSink = null;

        private void SetSqliteImportStatus(string message)
        {
            if (SqliteImportStatusSink == null) UpdateMainStatus(message);
            else SqliteImportStatusSink(message);
        }

        private void OpenSqliteImportWizard(TreeDatabaseTarget target)
        {
            if (target == null) return;
            try
            {
                SqliteImportResult result;
                if (SqliteImportWizardRunner != null)
                {
                    result = SqliteImportWizardRunner(target.Database, target.DatabaseName, "");
                }
                else
                {
                    using (var wizard = new SqliteImportWizardForm(target.Database, target.DatabaseName))
                    {
                        wizard.ShowDialog(this);
                        result = wizard.Result;
                    }
                }

                if (result != null)
                {
                    string status = result.Status == SqliteImportStatus.Completed
                        ? string.Format(ReportText.Get("SQLite 匯入執行完成：{0} 句成功 · 影響 {1:N0} 列 · 日誌：{2}", "SQLite import execution completed: {0} statements · {1:N0} rows · Log: {2}"), result.ExecutedStatements, result.TotalRowsAffected, result.LogPath)
                        : (result.Status == SqliteImportStatus.Cancelled
                            ? ReportText.Get("SQLite 匯入已取消；交易若有開啟已自動回滾", "SQLite import cancelled; transaction rolled back if active")
                            : string.Format(ReportText.Get("SQLite 匯入結束：{0} 失敗，{1} 略過 · 日誌：{2}", "SQLite import finished: {0} failed, {1} skipped · Log: {2}"), result.FailedStatements, result.SkippedStatements, result.LogPath));
                    SetSqliteImportStatus(status);
                    RefreshDatabaseObjectNodes(target.DatabaseNode);
                }
            }
            catch (Exception ex)
            {
                SetSqliteImportStatus(ReportText.Get("無法開啟 SQLite 匯入精靈", "Cannot open SQLite import wizard") + ": " + ex.Message);
            }
        }
    }
}
