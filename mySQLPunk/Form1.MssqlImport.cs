using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using mySQLPunk.lib;
using mySQLPunk.lib.MssqlImport;
namespace mySQLPunk
{
    public partial class Form1
    {
        internal Func<Dictionary<string, object>, MssqlImportRequest, MssqlImportResult> MssqlImportWizardRunner;
        internal Func<OpenFileDialog, DialogResult> SqlImportOpenDialogRunner;
        internal Action<string> MssqlImportStatusSink;
        private static bool IsMssqlImportTarget(TreeDatabaseTarget target) => target != null &&
            (string.Equals(target.ProviderName, "mssql", StringComparison.OrdinalIgnoreCase) || string.Equals(target.ProviderName, "sqlserver", StringComparison.OrdinalIgnoreCase));
        private DialogResult ShowSqlImportFileDialog(OpenFileDialog dialog) => SqlImportOpenDialogRunner == null ? dialog.ShowDialog() : SqlImportOpenDialogRunner(dialog);
        private void SetMssqlImportStatus(string text) { if (MssqlImportStatusSink == null) UpdateMainStatus(text); else MssqlImportStatusSink(text); }
        private void OpenMssqlImportWizard(TreeDatabaseTarget target)
        {
            try
            {
                var settings = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in target.ConnectionInfo)
                {
                    if (string.Equals(pair.Key, "pdo", StringComparison.OrdinalIgnoreCase) || string.Equals(pair.Key, "isConnect", StringComparison.OrdinalIgnoreCase) || string.Equals(pair.Key, "connString", StringComparison.OrdinalIgnoreCase)) continue;
                    if (pair.Value == null || pair.Value is string || pair.Value.GetType().IsPrimitive || pair.Value is decimal) settings[pair.Key] = pair.Value;
                }
                settings["db_kind"] = "mssql"; settings["provider"] = "mssql"; settings["initial_database"] = target.DatabaseName;
                var initial = new MssqlImportRequest(Guid.NewGuid(), "", target.DatabaseName);
                MssqlImportResult result;
                if (MssqlImportWizardRunner != null) result = MssqlImportWizardRunner(settings, initial);
                else
                {
                    using (var wizard = new MssqlImportWizardForm(initial, () => new MssqlImportService(
                        () => new MssqlImportSqlSession(ConnectionOpenService.OpenMssqlImportSession(settings)), job => new MssqlImportLog(job))))
                    { wizard.ShowDialog(this); result = wizard.Result; }
                }
                if (result == null) return;
                string text;
                switch (result.Status)
                {
                    case MssqlImportStatus.Completed: text = ReportText.Get("SQL Server 匯入執行完成", "SQL Server import execution completed"); break;
                    case MssqlImportStatus.CompletedWithErrors: text = ReportText.Get("SQL Server 執行完成，但有錯誤／部分寫入", "SQL Server completed with errors/partial writes"); break;
                    case MssqlImportStatus.Cancelled: text = ReportText.Get("SQL Server 匯入已取消；先前寫入可能保留", "SQL Server import cancelled; previous writes may remain"); break;
                    case MssqlImportStatus.CleanupUncertain: text = ReportText.Get("SQL Server 匯入／清理狀態未確認", "SQL Server import/cleanup unconfirmed"); break;
                    default: text = ReportText.Get("SQL Server 匯入停止：發生錯誤", "SQL Server import stopped: error"); break;
                }
                if (!string.IsNullOrWhiteSpace(result.LogPath)) text += " · " + result.LogPath;
                SetMssqlImportStatus(text);
            }
            catch (Exception) { SetMssqlImportStatus(ReportText.Get("無法開啟 SQL Server 匯入精靈，請確認已選目標。", "Cannot open SQL Server import wizard; review the selected target.")); }
        }
    }
}
