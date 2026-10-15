using System;
using System.Windows.Forms;
using mySQLPunk.lib;
using mySQLPunk.lib.PostgreSqlImport;
using PostgreSqlImportResult = mySQLPunk.lib.PostgreSqlImport.PostgreSqlImportResult;

namespace mySQLPunk
{
    public partial class Form1
    {
        internal Func<IDatabase, string, string, PostgreSqlImportResult> PostgreSqlImportWizardRunner = null;
        internal Action<string> PostgreSqlImportStatusSink = null;

        private void SetPostgreSqlImportStatus(string message)
        {
            if (PostgreSqlImportStatusSink == null) UpdateMainStatus(message);
            else PostgreSqlImportStatusSink(message);
        }

        private void OpenPostgreSqlImportWizard(TreeDatabaseTarget target)
        {
            if (target == null) return;
            try
            {
                PostgreSqlImportResult result;
                if (PostgreSqlImportWizardRunner != null)
                {
                    result = PostgreSqlImportWizardRunner(target.Database, target.DatabaseName, "");
                }
                else
                {
                    using (var wizard = new PostgreSqlImportWizardForm(target.Database, target.DatabaseName))
                    {
                        wizard.ShowDialog(this);
                        result = wizard.Result;
                    }
                }

                if (result != null)
                {
                    string status = result.Success
                        ? string.Format(ReportText.Get("PostgreSQL 匯入執行完成：{0} 句成功 · 影響 {1:N0} 列 · 日誌：{2}", "PostgreSQL import execution completed: {0} statements · {1:N0} rows · Log: {2}"), result.StatementsExecuted, result.TotalRowsAffected, result.LogFilePath)
                        : string.Format(ReportText.Get("PostgreSQL 匯入結束：{0} 失敗 · 日誌：{1}", "PostgreSQL import finished: {0} failed · Log: {1}"), result.StatementsFailed, result.LogFilePath);
                    SetPostgreSqlImportStatus(status);
                    RefreshDatabaseObjectNodes(target.DatabaseNode);
                }
            }
            catch (Exception ex)
            {
                SetPostgreSqlImportStatus(ReportText.Get("無法開啟 PostgreSQL 匯入精靈", "Cannot open PostgreSQL import wizard") + ": " + ex.Message);
            }
        }
    }
}
