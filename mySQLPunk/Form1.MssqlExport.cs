using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using mySQLPunk.lib;
using mySQLPunk.lib.MssqlExport;

namespace mySQLPunk
{
    public partial class Form1
    {
        // Instance boundaries keep modal routing testable without opening saved profiles or connections.
        internal Func<Dictionary<string, object>, string, MssqlExportWizardOptions, MssqlExportResult> MssqlExportWizardRunner = null;
        internal Func<SaveFileDialog, DialogResult> SqlExportSaveDialogRunner = null;
        internal Func<MssqlMirrorOptions> MssqlExportMirrorCapture = null;
        internal Action<string> MssqlExportStatusSink = null;

        private static bool IsMssqlExportTarget(TreeDatabaseTarget target)
        {
            return target != null && (string.Equals(target.ProviderName, "mssql", StringComparison.OrdinalIgnoreCase)
                || string.Equals(target.ProviderName, "sqlserver", StringComparison.OrdinalIgnoreCase));
        }

        private DialogResult ShowSqlExportSaveDialog(SaveFileDialog dialog)
        {
            return SqlExportSaveDialogRunner == null ? dialog.ShowDialog() : SqlExportSaveDialogRunner(dialog);
        }

        private void SetMssqlExportStatus(string message)
        {
            if (MssqlExportStatusSink == null) UpdateMainStatus(message);
            else MssqlExportStatusSink(message);
        }

        private void OpenMssqlExportWizard(TreeDatabaseTarget target, MssqlExportMode mode,
            string initialSchema, string initialTable, bool backupEntry)
        {
            try
            {
                var settings = new Dictionary<string, object>();
                foreach (var pair in target.ConnectionInfo)
                {
                    if (string.Equals(pair.Key, "pdo", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(pair.Key, "isConnect", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(pair.Key, "connString", StringComparison.OrdinalIgnoreCase)) continue;
                    if (pair.Value == null || pair.Value is string || pair.Value.GetType().IsPrimitive || pair.Value is decimal)
                        settings[pair.Key] = pair.Value;
                }
                settings["initial_database"] = target.DatabaseName;

                MssqlMirrorOptions mirror = null;
                if (backupEntry)
                {
                    if (MssqlExportMirrorCapture != null) mirror = MssqlExportMirrorCapture();
                    else
                    {
                        string directory = BackupMirrorSettings.RemoteDirectory;
                        int retainCount = BackupMirrorSettings.RetainCount;
                        if (!string.IsNullOrWhiteSpace(directory)) mirror = new MssqlMirrorOptions(directory, retainCount);
                    }
                }

                // Tree callers pass initialSchema=null and the provider's canonical raw ObjectName unchanged.
                // Only the background native catalog may resolve that seed; never split on dots here.
                string seed = initialSchema == null ? initialTable : initialSchema + "." + initialTable;
                string fileName = seed == null
                    ? target.DatabaseName + (backupEntry ? "_backup_" : "_dump_") + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".sql"
                    : target.DatabaseName + "_" + seed + (mode == MssqlExportMode.DataOnly ? "_data" : "_structure_data") + ".sql";
                foreach (char invalid in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(invalid, '_');
                var options = new MssqlExportWizardOptions(mode, null, seed == null, fileName, mirror, seed);
                MssqlExportResult result;
                if (MssqlExportWizardRunner != null) result = MssqlExportWizardRunner(settings, target.DatabaseName, options);
                else
                {
                    using (var wizard = new MssqlExportWizardForm(settings, target.DatabaseName, options))
                    {
                        wizard.ShowDialog(this);
                        result = wizard.Result;
                    }
                }
                // Closing setup has no result and must not produce a success notification.
                if (result != null) SetMssqlExportStatus(BuildMssqlExportStatus(result));
            }
            catch (Exception ex)
            {
                SetMssqlExportStatus(ReportText.Get("SQL Server 匯出失敗", "SQL Server export failed") + " (" + ex.GetType().Name + ")");
            }
        }

        private static string BuildMssqlExportStatus(MssqlExportResult result)
        {
            string message;
            switch (result.Stage)
            {
                case MssqlExportStage.Completed:
                    message = ReportText.Get("SQL Server 匯出完成", "SQL Server export completed");
                    break;
                case MssqlExportStage.LocalCompletedMirrorIncomplete:
                    message = ReportText.Get("SQL Server 本機匯出完成；鏡像未完成", "SQL Server local export completed; mirror incomplete");
                    break;
                case MssqlExportStage.Canceled:
                    message = ReportText.Get("SQL Server 匯出已取消", "SQL Server export canceled");
                    break;
                default:
                    message = ReportText.Get("SQL Server 匯出失敗", "SQL Server export failed");
                    break;
            }
            if (!string.IsNullOrWhiteSpace(result.PublishedPath)) message += " · " + result.PublishedPath;
            if (!string.IsNullOrWhiteSpace(result.MirrorPath)) message += " · " + result.MirrorPath;
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage)) message += " · " + result.ErrorMessage;
            if (result.SnapshotCleanup?.State == MssqlSnapshotCleanupState.Unconfirmed)
                message += " · " + ReportText.Get("警告：資料庫設定尚未確認恢復", "Warning: database setting restoration is unconfirmed") + MssqlExportWizardForm.CleanupText(result.SnapshotCleanup);
            return message;
        }
    }
}
