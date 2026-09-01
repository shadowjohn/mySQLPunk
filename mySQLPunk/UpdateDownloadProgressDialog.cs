using System;
using System.Drawing;
using System.Windows.Forms;

namespace mySQLPunk
{
    /// <summary>
    /// 更新下載進度視窗：檔名＋進度條＋MB/百分比，附「取消」。
    /// 以非強制回應（modeless）方式跟著下載流程開關，主視窗仍可操作。
    /// </summary>
    public sealed class UpdateDownloadProgressDialog : Form
    {
        private readonly Label _fileLabel;
        private readonly Label _progressLabel;
        private readonly ProgressBar _progressBar;
        private readonly Button _cancelButton;
        private bool _cancelRequested;

        /// <summary>使用者按下「取消」時觸發；由呼叫端負責中止下載。</summary>
        public event EventHandler CancelRequested;

        public bool IsCancelRequested { get { return _cancelRequested; } }

        public UpdateDownloadProgressDialog(string fileName)
        {
            Text = Localization.T("Update.DownloadProgressTitle");
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ControlBox = false;
            ClientSize = new Size(420, 128);
            Padding = new Padding(20, 16, 20, 16);

            TableLayoutPanel root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _fileLabel = new Label
            {
                AutoSize = true,
                Text = fileName ?? "",
                Margin = new Padding(0, 0, 0, 8)
            };
            _progressBar = new ProgressBar
            {
                Style = ProgressBarStyle.Marquee,
                Minimum = 0,
                Maximum = 100,
                Height = 14,
                Dock = DockStyle.Top,
                Margin = new Padding(0, 0, 0, 6)
            };
            _progressLabel = new Label
            {
                AutoSize = true,
                Text = Localization.T("Update.DownloadPreparing"),
                ForeColor = ThemeManager.MutedTextColor,
                Margin = new Padding(0, 0, 0, 4)
            };
            _cancelButton = new Button
            {
                Text = Localization.T("Common.Cancel"),
                AutoSize = true,
                MinimumSize = new Size(UiMetrics.ButtonMinWidth, UiMetrics.ControlHeight),
                Padding = new Padding(10, 2, 10, 2),
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
                Margin = new Padding(0)
            };
            _cancelButton.Click += (s, e) =>
            {
                _cancelRequested = true;
                _cancelButton.Enabled = false;
                EventHandler handler = CancelRequested;
                if (handler != null) handler(this, EventArgs.Empty);
            };

            root.Controls.Add(_fileLabel, 0, 0);
            root.Controls.Add(_progressBar, 0, 1);
            root.Controls.Add(_progressLabel, 0, 2);
            root.Controls.Add(_cancelButton, 0, 3);
            Controls.Add(root);

            ThemeManager.ApplyTo(this);
        }

        /// <summary>回報下載進度；totalBytes 未知（&lt;= 0）時維持跑馬燈。</summary>
        public void ReportProgress(long receivedBytes, long totalBytes)
        {
            if (IsDisposed) return;
            if (totalBytes > 0)
            {
                if (_progressBar.Style != ProgressBarStyle.Continuous) _progressBar.Style = ProgressBarStyle.Continuous;
                int percent = (int)Math.Min(100, receivedBytes * 100 / totalBytes);
                _progressBar.Value = percent;
                _progressLabel.Text = Localization.Format(
                    "Update.DownloadProgress",
                    (receivedBytes / 1048576.0).ToString("0.0"),
                    (totalBytes / 1048576.0).ToString("0.0"),
                    percent);
            }
            else
            {
                _progressLabel.Text = (receivedBytes / 1048576.0).ToString("0.0") + " MB";
            }
        }

        /// <summary>下載完成後進入「校驗中」等不可取消的階段。</summary>
        public void SetStatus(string text)
        {
            if (IsDisposed) return;
            _progressLabel.Text = text ?? "";
            _progressBar.Style = ProgressBarStyle.Marquee;
            _cancelButton.Enabled = false;
        }
    }
}
