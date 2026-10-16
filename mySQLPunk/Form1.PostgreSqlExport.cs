using System;
using System.IO;
using System.Windows.Forms;
using mySQLPunk.lib;
using mySQLPunk.lib.PostgreSqlExport;
using PostgreSqlExportResult = mySQLPunk.lib.PostgreSqlExport.PostgreSqlExportResult;

namespace mySQLPunk
{
    public partial class Form1
    {
        internal Func<IDatabase, string, PostgreSqlExportWizardOptions, PostgreSqlExportResult> PostgreSqlExportWizardRunner = null;
        internal Action<string> PostgreSqlExportStatusSink = null;

        private void SetPostgreSqlExportStatus(string message)
        {
            if (PostgreSqlExportStatusSink == null) UpdateMainStatus(message);
            else PostgreSqlExportStatusSink(message);
        }

        private void OpenPostgreSqlExportWizard(TreeDatabaseTarget target, PostgreSqlExportMode mode = PostgreSqlExportMode.SchemaAndData, string specificTable = null)
        {
            if (target == null) return;
            try
            {
                string dbStem = string.IsNullOrWhiteSpace(target.DatabaseName) ? "pg_dump" : target.DatabaseName;
                string defaultFileName = dbStem + (string.IsNullOrWhiteSpace(specificTable) ? "" : "_" + specificTable) + "_pg_dump_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".sql";
                string targetPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), defaultFileName);
                var options = new PostgreSqlExportWizardOptions(mode, targetPath);
                if (!string.IsNullOrWhiteSpace(specificTable))
                {
                    options.SelectedTables.Add(specificTable);
                }

                PostgreSqlExportResult result;
                if (PostgreSqlExportWizardRunner != null)
                {
                    result = PostgreSqlExportWizardRunner(target.Database, target.DatabaseName, options);
                }
                else
                {
                    using (var wizard = new PostgreSqlExportWizardForm(target.Database, target.DatabaseName, options))
                    {
                        wizard.ShowDialog(this);
                        result = wizard.Result;
                    }
                }

                if (result != null)
                {
                    string status = result.Success
                        ? string.Format(ReportText.Get("PostgreSQL 傾印完成：{0} 個物件，共 {1:N0} 列 · {2}", "PostgreSQL dump completed: {0} objects, {1:N0} rows · {2}"), result.TableCount + result.ViewCount, result.TotalRows, result.TargetPath)
                        : (result.Stage == PostgreSqlExportStage.Cancelled
                            ? ReportText.Get("PostgreSQL 匯出已取消", "PostgreSQL export cancelled")
                            : ReportText.Get("PostgreSQL 匯出失敗：", "PostgreSQL export failed: ") + result.ErrorMessage);
                    SetPostgreSqlExportStatus(status);
                }
            }
            catch (Exception ex)
            {
                SetPostgreSqlExportStatus(ReportText.Get("無法開啟 PostgreSQL 匯出精靈", "Cannot open PostgreSQL export wizard") + ": " + ex.Message);
            }
        }
    }
}
