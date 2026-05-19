using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
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

            // 全域例外處理：捕捉 UI 執行緒未處理的例外，顯示訊息而非直接 crash
            Application.ThreadException += (sender, e) =>
            {
                string message = e.Exception?.Message ?? "Unknown error";
                MessageBox.Show(
                    "執行時發生未預期的錯誤：\r\n\r\n" + message,
                    "未預期錯誤",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            };

            // 設定 ThreadException 處理模式（必須在 Application.Run 之前設定）
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            // 捕捉非 UI 執行緒（如 Task.Run）的未處理例外
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                string message = (e.ExceptionObject as Exception)?.Message ?? e.ExceptionObject?.ToString() ?? "Unknown error";
                try
                {
                    MessageBox.Show(
                        "背景執行緒發生未預期的錯誤：\r\n\r\n" + message,
                        "未預期錯誤",
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
            return 0;
        }
    }
}
