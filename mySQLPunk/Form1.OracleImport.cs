using System;
using System.Windows.Forms;
using mySQLPunk.lib;
using mySQLPunk.lib.OracleImport;
using OracleImportResult = mySQLPunk.lib.OracleImport.OracleImportResult;

namespace mySQLPunk
{
    public partial class Form1
    {
        internal Func<IDatabase, string, string, OracleImportResult> OracleImportWizardRunner = null;
        internal Action<string> OracleImportStatusSink = null;

        private void SetOracleImportStatus(string message)
        {
            if (OracleImportStatusSink == null) UpdateMainStatus(message);
            else OracleImportStatusSink(message);
        }

        private void OpenOracleImportWizard(TreeDatabaseTarget target)
        {
            if (target == null) return;
            try
            {
                OracleImportResult result;
                if (OracleImportWizardRunner != null)
                {
                    result = OracleImportWizardRunner(target.Database, target.DatabaseName, "");
                }
                else
                {
                    using (var session = WizardOwnedSession.Open(target.ConnectionInfo, target.ProviderName, target.DatabaseName))
                    using (var wizard = new OracleImportWizardForm(session.Database, target.DatabaseName))
                    {
                        wizard.ShowDialog(this);
                        result = wizard.Result;
                    }
                }

                if (result != null)
                {
                    string status = result.Success
                        ? string.Format(ReportText.Get("Oracle 匯入執行完成：{0} 句成功 · 影響 {1:N0} 列 · 日誌：{2}", "Oracle import execution completed: {0} statements · {1:N0} rows · Log: {2}"), result.StatementsExecuted, result.TotalRowsAffected, result.LogFilePath)
                        : string.Format(ReportText.Get("Oracle 匯入結束：{0} 失敗 · 日誌：{1}", "Oracle import finished: {0} failed · Log: {1}"), result.StatementsFailed, result.LogFilePath);
                    SetOracleImportStatus(status);
                    RefreshDatabaseObjectNodes(target.DatabaseNode);
                }
            }
            catch (Exception ex)
            {
                SetOracleImportStatus(ReportText.Get("無法開啟 Oracle 匯入精靈", "Cannot open Oracle import wizard") + ": " + ex.Message);
            }
        }
    }
}
