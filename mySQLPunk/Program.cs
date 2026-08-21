using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.IO;
using mySQLPunk.lib;

namespace mySQLPunk
{
    static class Program
    {
        /// <summary>
        /// 應用程式的主要進入點。
        /// </summary>
        [STAThread]
        static int Main(string[] args)
        {
            SqliteColumnCommentCliResult cliResult = SqliteColumnCommentCliService.TryRun(args);
            if (cliResult.Handled)
            {
                if (!string.IsNullOrWhiteSpace(cliResult.Message))
                {
                    Console.WriteLine(cliResult.Message);
                }
                return cliResult.ExitCode;
            }

            // 「允許重複執行」選項關閉時強制單一實例（選項頁本來就有這個開關，之前沒實作）
            System.Threading.Mutex instanceMutex = null;
            bool allowMultipleInstances = true;
            try { allowMultipleInstances = ApplicationOptionSettings.GetBool("AdvancedAllowMultipleInstances"); }
            catch { }
            if (!allowMultipleInstances)
            {
                bool createdNew;
                instanceMutex = new System.Threading.Mutex(true, @"Local\mySQLPunk_SingleInstance", out createdNew);
                if (!createdNew)
                {
                    MessageBox.Show(
                        Localization.T("Program.AlreadyRunning"),
                        "mySQLPunk",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return 0;
                }
            }

            // 檔案關聯（開啟方式 .sql）啟動時把檔案留給主視窗，等有連線後開進查詢分頁
            if (args.Length > 0 && File.Exists(args[0]) &&
                string.Equals(Path.GetExtension(args[0]), ".sql", StringComparison.OrdinalIgnoreCase))
            {
                Form1.StartupSqlFilePath = Path.GetFullPath(args[0]);
            }

            // 全域例外處理：捕捉 UI 執行緒未處理的例外，顯示訊息而非直接 crash
            Application.ThreadException += (sender, e) =>
            {
                MessageBox.Show(
                    BuildUnexpectedUiErrorMessage(e.Exception),
                    BuildUnexpectedErrorTitle(),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            };

            // 設定 ThreadException 處理模式（必須在 Application.Run 之前設定）
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            // 捕捉非 UI 執行緒（如 Task.Run）的未處理例外
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                try
                {
                    MessageBox.Show(
                        BuildUnexpectedBackgroundErrorMessage(e.ExceptionObject),
                        BuildUnexpectedErrorTitle(),
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                catch { }
            };

            // 捕捉 async/await Task 的未觀察例外
            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                e.SetObserved();
            };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Form1());
            GC.KeepAlive(instanceMutex);
            return 0;
        }

        private static string BuildUnexpectedErrorTitle()
        {
            return Localization.T("Program.UnexpectedErrorTitle");
        }

        private static string BuildUnexpectedUiErrorMessage(Exception ex)
        {
            return Localization.Format("Program.UnexpectedUiError", GetExceptionMessage(ex));
        }

        private static string BuildUnexpectedBackgroundErrorMessage(object exceptionObject)
        {
            Exception ex = exceptionObject as Exception;
            string message = ex != null ? GetExceptionMessage(ex) : (exceptionObject?.ToString() ?? Localization.T("Object.UnknownError"));
            return Localization.Format("Program.UnexpectedBackgroundError", message);
        }

        private static string GetExceptionMessage(Exception ex)
        {
            return string.IsNullOrWhiteSpace(ex?.Message) ? Localization.T("Object.UnknownError") : ex.Message;
        }
    }
}
