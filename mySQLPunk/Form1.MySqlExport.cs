using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using mySQLPunk.lib;
using mySQLPunk.lib.MySqlExport;
using MySqlExportResult = mySQLPunk.lib.MySqlExport.MySqlExportResult;

namespace mySQLPunk
{
    public partial class Form1
    {
        internal Func<IDatabase, string, MySqlExportWizardOptions, MySqlExportResult> MySqlExportWizardRunner = null;
        internal Action<string> MySqlExportStatusSink = null;

        private void SetMySqlExportStatus(string message)
        {
            if (MySqlExportStatusSink == null) UpdateMainStatus(message);
            else MySqlExportStatusSink(message);
        }

        private void OpenMySqlExportWizard(TreeDatabaseTarget target, MySqlExportMode mode = MySqlExportMode.SchemaAndData, string specificTable = null)
        {
            if (target == null) return;
            try
            {
                string defaultFileName = target.DatabaseName + "_dump_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".sql";
                string targetPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), defaultFileName);
                var options = new MySqlExportWizardOptions(mode, targetPath);
                if (!string.IsNullOrWhiteSpace(specificTable))
                {
                    options.SelectedTables.Add(specificTable);
                    options.IncludeViews = false;
                    options.IncludeRoutines = false;
                    options.IncludeTriggers = false;
                }

                MySqlExportResult result;
                if (MySqlExportWizardRunner != null)
                {
                    result = MySqlExportWizardRunner(target.Database, target.DatabaseName, options);
                }
                else
                {
                    using (var session = WizardOwnedSession.Open(target.ConnectionInfo, target.ProviderName, target.DatabaseName))
                    using (var wizard = new MySqlExportWizardForm(session.Database, target.DatabaseName, options))
                    {
                        wizard.ShowDialog(this);
                        result = wizard.Result;
                    }
                }

                if (result != null)
                {
                    string status = result.Success
                        ? string.Format(ReportText.Get("MySQL 匯出完成：{0} 張資料表，共 {1:N0} 列 · {2}", "MySQL export completed: {0} tables, {1:N0} rows · {2}"), result.TableCount, result.TotalRows, result.TargetPath)
                        : (result.Stage == MySqlExportStage.Cancelled
                            ? ReportText.Get("MySQL 匯出已取消", "MySQL export cancelled")
                            : ReportText.Get("MySQL 匯出失敗：", "MySQL export failed: ") + result.ErrorMessage);
                    SetMySqlExportStatus(status);
                }
            }
            catch (Exception ex)
            {
                SetMySqlExportStatus(ReportText.Get("無法開啟 MySQL 匯出精靈", "Cannot open MySQL export wizard") + ": " + ex.Message);
            }
        }
    }
}
