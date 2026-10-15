using System;
using System.Collections.Generic;
using System.Windows.Forms;
using mySQLPunk.lib;
using mySQLPunk.lib.MySqlImport;
using MySqlImportResult = mySQLPunk.lib.MySqlImport.MySqlImportResult;

namespace mySQLPunk
{
    public partial class Form1
    {
        internal Func<IDatabase, string, string, MySqlImportResult> MySqlImportWizardRunner = null;
        internal Action<string> MySqlImportStatusSink = null;

        private void SetMySqlImportStatus(string message)
        {
            if (MySqlImportStatusSink == null) UpdateMainStatus(message);
            else MySqlImportStatusSink(message);
        }

        private void OpenMySqlImportWizard(TreeDatabaseTarget target)
        {
            if (target == null) return;
            try
            {
                MySqlImportResult result;
                if (MySqlImportWizardRunner != null)
                {
                    result = MySqlImportWizardRunner(target.Database, target.DatabaseName, "");
                }
                else
                {
                    using (var wizard = new MySqlImportWizardForm(target.Database, target.DatabaseName))
                    {
                        wizard.ShowDialog(this);
                        result = wizard.Result;
                    }
                }

                if (result != null)
                {
                    string status = result.Status == MySqlImportStatus.Completed
                        ? string.Format(ReportText.Get("MySQL 匯入執行完成：{0} 句成功 · 日誌：{1}", "MySQL import execution completed: {0} statements · Log: {1}"), result.ExecutedStatements, result.LogPath)
                        : (result.Status == MySqlImportStatus.Cancelled
                            ? ReportText.Get("MySQL 匯入已取消；先前寫入可能保留", "MySQL import cancelled; previous writes may remain")
                            : string.Format(ReportText.Get("MySQL 匯入結束：{0} 失敗，{1} 略過 · 日誌：{2}", "MySQL import finished: {0} failed, {1} skipped · Log: {2}"), result.FailedStatements, result.SkippedStatements, result.LogPath));
                    SetMySqlImportStatus(status);
                    RefreshDatabaseObjectNodes(target.DatabaseNode);
                }
            }
            catch (Exception ex)
            {
                SetMySqlImportStatus(ReportText.Get("無法開啟 MySQL 匯入精靈", "Cannot open MySQL import wizard") + ": " + ex.Message);
            }
        }
    }
}
