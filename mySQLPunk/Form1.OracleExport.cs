using System;
using System.IO;
using System.Windows.Forms;
using mySQLPunk.lib;
using mySQLPunk.lib.OracleExport;
using OracleExportResult = mySQLPunk.lib.OracleExport.OracleExportResult;

namespace mySQLPunk
{
    public partial class Form1
    {
        internal Func<IDatabase, string, OracleExportWizardOptions, OracleExportResult> OracleExportWizardRunner = null;
        internal Action<string> OracleExportStatusSink = null;

        private void SetOracleExportStatus(string message)
        {
            if (OracleExportStatusSink == null) UpdateMainStatus(message);
            else OracleExportStatusSink(message);
        }

        private void OpenOracleExportWizard(TreeDatabaseTarget target, OracleExportMode mode = OracleExportMode.SchemaAndData, string specificTable = null)
        {
            if (target == null) return;
            try
            {
                string dbStem = string.IsNullOrWhiteSpace(target.DatabaseName) ? "oracle_dump" : target.DatabaseName;
                string defaultFileName = dbStem + (string.IsNullOrWhiteSpace(specificTable) ? "" : "_" + specificTable) + "_oracle_dump_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".sql";
                string targetPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), defaultFileName);
                var options = new OracleExportWizardOptions(mode, targetPath);
                if (!string.IsNullOrWhiteSpace(specificTable))
                {
                    options.SelectedTables.Add(specificTable);
                }

                OracleExportResult result;
                if (OracleExportWizardRunner != null)
                {
                    result = OracleExportWizardRunner(target.Database, target.DatabaseName, options);
                }
                else
                {
                    using (var wizard = new OracleExportWizardForm(target.Database, target.DatabaseName, options))
                    {
                        wizard.ShowDialog(this);
                        result = wizard.Result;
                    }
                }

                if (result != null)
                {
                    string status = result.Success
                        ? string.Format(ReportText.Get("Oracle 傾印完成：{0} 個物件，共 {1:N0} 列 · {2}", "Oracle dump completed: {0} objects, {1:N0} rows · {2}"), result.TablesExported + result.ViewsExported, result.TotalRowsExported, result.FilePath)
                        : ReportText.Get("Oracle 匯出失敗：", "Oracle export failed: ") + result.ErrorMessage;
                    SetOracleExportStatus(status);
                }
            }
            catch (Exception ex)
            {
                SetOracleExportStatus(ReportText.Get("無法開啟 Oracle 匯出精靈", "Cannot open Oracle export wizard") + ": " + ex.Message);
            }
        }
    }
}
