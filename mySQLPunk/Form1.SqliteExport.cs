using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using mySQLPunk.lib;
using mySQLPunk.lib.SqliteExport;
using SqliteExportResult = mySQLPunk.lib.SqliteExport.SqliteExportResult;

namespace mySQLPunk
{
    public partial class Form1
    {
        internal Func<IDatabase, string, SqliteExportWizardOptions, SqliteExportResult> SqliteExportWizardRunner = null;
        internal Action<string> SqliteExportStatusSink = null;

        private void SetSqliteExportStatus(string message)
        {
            if (SqliteExportStatusSink == null) UpdateMainStatus(message);
            else SqliteExportStatusSink(message);
        }

        private void OpenSqliteExportWizard(TreeDatabaseTarget target, SqliteExportMode mode = SqliteExportMode.SchemaAndData, string specificTable = null)
        {
            if (target == null) return;
            try
            {
                string dbStem = string.IsNullOrWhiteSpace(target.DatabaseName) ? "sqlite_dump" : Path.GetFileNameWithoutExtension(target.DatabaseName);
                if (string.IsNullOrWhiteSpace(dbStem)) dbStem = "sqlite_dump";
                string defaultFileName = dbStem + (string.IsNullOrWhiteSpace(specificTable) ? "" : "_" + specificTable) + "_dump_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".sql";
                string targetPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), defaultFileName);
                var options = new SqliteExportWizardOptions(mode, targetPath);
                if (!string.IsNullOrWhiteSpace(specificTable))
                {
                    options.SelectedTables.Add(specificTable);
                }

                SqliteExportResult result;
                if (SqliteExportWizardRunner != null)
                {
                    result = SqliteExportWizardRunner(target.Database, target.DatabaseName, options);
                }
                else
                {
                    using (var wizard = new SqliteExportWizardForm(target.Database, target.DatabaseName, options))
                    {
                        wizard.ShowDialog(this);
                        result = wizard.Result;
                    }
                }

                if (result != null)
                {
                    string status = result.Success
                        ? string.Format(ReportText.Get("SQLite 匯出完成：{0} 張資料表，共 {1:N0} 列 · {2}", "SQLite export completed: {0} tables, {1:N0} rows · {2}"), result.TableCount, result.TotalRows, result.TargetPath)
                        : (result.Stage == SqliteExportStage.Cancelled
                            ? ReportText.Get("SQLite 匯出已取消", "SQLite export cancelled")
                            : ReportText.Get("SQLite 匯出失敗：", "SQLite export failed: ") + result.ErrorMessage);
                    SetSqliteExportStatus(status);
                }
            }
            catch (Exception ex)
            {
                SetSqliteExportStatus(ReportText.Get("無法開啟 SQLite 匯出精靈", "Cannot open SQLite export wizard") + ": " + ex.Message);
            }
        }
    }
}
