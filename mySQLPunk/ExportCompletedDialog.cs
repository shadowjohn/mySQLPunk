using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    public sealed class ExportCompletedDialog : Form
    {
        private readonly QueryResultExportSummary _summary;

        public ExportCompletedDialog(QueryResultExportSummary summary)
        {
            if (summary == null) throw new ArgumentNullException(nameof(summary));
            _summary = summary;

            InitializeComponent();
            ThemeManager.ApplyTo(this);
        }

        private void InitializeComponent()
        {
            Text = Localization.T("Query.ExportSummaryTitle");
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(560, 280);
            Padding = new Padding(18);

            TableLayoutPanel root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            Label titleLabel = new Label
            {
                AutoSize = true,
                Text = Localization.T("Query.ExportSummaryMessage"),
                Font = new Font(Font.FontFamily, 10F, FontStyle.Bold),
                Margin = new Padding(0, 0, 0, 12)
            };

            TableLayoutPanel details = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 5,
                Margin = new Padding(0, 0, 0, 16)
            };
            details.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            AddDetail(details, 0, Localization.T("Query.ExportSummaryFormat"), _summary.FormatName);
            AddDetail(details, 1, Localization.T("Query.ExportSummaryRows"), _summary.Rows.ToString("N0"));
            AddDetail(details, 2, Localization.T("Query.ExportSummarySize"), QueryResultExportSummary.FormatByteCount(_summary.BytesWritten));
            AddDetail(details, 3, Localization.T("Query.ExportSummaryFile"), _summary.FileName);
            AddDetail(details, 4, Localization.T("Query.ExportSummaryPath"), _summary.Path);

            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                WrapContents = false
            };

            Button okButton = new Button
            {
                Text = Localization.T("Common.OK"),
                DialogResult = DialogResult.OK,
                AutoSize = true,
                MinimumSize = new Size(92, 30),
                Margin = new Padding(8, 0, 0, 0)
            };

            Button openFolderButton = new Button
            {
                Text = Localization.T("Query.OpenExportFolder"),
                AutoSize = true,
                MinimumSize = new Size(110, 30),
                Enabled = Directory.Exists(_summary.DirectoryPath)
            };
            openFolderButton.Click += (s, e) => OpenTarget(_summary.DirectoryPath);

            Button openFileButton = new Button
            {
                Text = Localization.T("Query.OpenExportedFile"),
                AutoSize = true,
                MinimumSize = new Size(110, 30),
                Enabled = File.Exists(_summary.Path)
            };
            openFileButton.Click += (s, e) => OpenTarget(_summary.Path);

            buttons.Controls.Add(okButton);
            buttons.Controls.Add(openFolderButton);
            buttons.Controls.Add(openFileButton);

            AcceptButton = okButton;
            CancelButton = okButton;

            root.Controls.Add(titleLabel, 0, 0);
            root.Controls.Add(details, 0, 1);
            root.Controls.Add(buttons, 0, 2);
            Controls.Add(root);
        }

        private static void AddDetail(TableLayoutPanel details, int row, string label, string value)
        {
            details.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            Label keyLabel = new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 5, 10, 5)
            };

            TextBox valueText = new TextBox
            {
                Text = value ?? "",
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 3, 0, 3)
            };

            details.Controls.Add(keyLabel, 0, row);
            details.Controls.Add(valueText, 1, row);
        }

        private static void OpenTarget(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return;

            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(BuildOpenTargetFailedMessage(ex), Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static string BuildOpenTargetFailedMessage(Exception ex)
        {
            string reason = ex == null ? null : ex.Message;
            if (string.IsNullOrWhiteSpace(reason)) reason = Localization.T("Object.UnknownError");
            return Localization.Format("Query.OpenExportTargetFailed", reason);
        }
    }
}
