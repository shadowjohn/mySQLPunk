using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk
{
    public sealed class ConnectionUriImportForm : Form
    {
        private readonly TextBox uriTextBox;
        private readonly CheckBox showUriCheckBox;

        public ConnectionUriImportForm()
        {
            Text = Localization.T("ConnectionUri.Title");
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(650, 245);
            Padding = new Padding(UiMetrics.Space5);

            Label title = new Label
            {
                Text = Localization.T("ConnectionUri.Label"),
                Dock = DockStyle.Top,
                Height = 28,
                Font = UiKit.BodyBold
            };
            Label description = new Label
            {
                Text = Localization.T("ConnectionUri.Description"),
                Dock = DockStyle.Top,
                Height = 45,
                ForeColor = ThemeManager.MutedTextColor
            };
            Label supported = new Label
            {
                Text = Localization.T("ConnectionUri.Supported"),
                Dock = DockStyle.Top,
                Height = 38,
                ForeColor = ThemeManager.MutedTextColor
            };

            uriTextBox = new TextBox
            {
                UseSystemPasswordChar = true
            };
            Control uriField = UiField.Wrap(uriTextBox);
            uriField.Dock = DockStyle.Top;
            uriField.Height = UiMetrics.ControlHeight;
            showUriCheckBox = new CheckBox
            {
                Text = Localization.T("ConnectionUri.Show"),
                Dock = DockStyle.Top,
                Height = 28
            };
            showUriCheckBox.CheckedChanged += (s, e) => uriTextBox.UseSystemPasswordChar = !showUriCheckBox.Checked;

            Button importButton = new Button
            {
                Text = Localization.T("ConnectionUri.Import"),
                DialogResult = DialogResult.None,
                AutoSize = true,
                MinimumSize = new Size(96, UiMetrics.ControlHeight)
            };
            Button cancelButton = new Button
            {
                Text = Localization.T("Common.Cancel"),
                DialogResult = DialogResult.Cancel,
                AutoSize = true,
                MinimumSize = new Size(96, UiMetrics.ControlHeight)
            };
            importButton.Click += ImportButton_Click;

            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false
            };
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(importButton);

            Controls.Add(showUriCheckBox);
            Controls.Add(uriField);
            Controls.Add(supported);
            Controls.Add(description);
            Controls.Add(title);
            Controls.Add(buttons);

            AcceptButton = importButton;
            CancelButton = cancelButton;
            ThemeManager.ApplyTo(this);
            ThemeManager.MarkAsPrimary(importButton);
        }

        public Dictionary<string, object> ConnectionDraft { get; private set; }

        private void ImportButton_Click(object sender, EventArgs e)
        {
            ConnectionUriParseResult result = ConnectionUriImportService.Parse(uriTextBox.Text);
            if (!result.Success)
            {
                MessageBox.Show(
                    GetErrorMessage(result),
                    Localization.T("ConnectionUri.ErrorTitle"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                uriTextBox.Focus();
                uriTextBox.SelectAll();
                return;
            }

            ConnectionDraft = result.Connection;
            DialogResult = DialogResult.OK;
            Close();
        }

        internal static string GetErrorMessage(ConnectionUriParseResult result)
        {
            if (result == null) return Localization.T("ConnectionUri.Error.InvalidFormat");
            string key;
            switch (result.Error)
            {
                case ConnectionUriError.Empty: key = "ConnectionUri.Error.Empty"; break;
                case ConnectionUriError.TooLong: key = "ConnectionUri.Error.TooLong"; break;
                case ConnectionUriError.InvalidCharacters: key = "ConnectionUri.Error.InvalidCharacters"; break;
                case ConnectionUriError.InvalidEscape: key = "ConnectionUri.Error.InvalidEscape"; break;
                case ConnectionUriError.UnsupportedScheme: key = "ConnectionUri.Error.UnsupportedScheme"; break;
                case ConnectionUriError.MissingHost: key = "ConnectionUri.Error.MissingHost"; break;
                case ConnectionUriError.InvalidPort: key = "ConnectionUri.Error.InvalidPort"; break;
                case ConnectionUriError.FragmentNotAllowed: key = "ConnectionUri.Error.Fragment"; break;
                case ConnectionUriError.InvalidQuery: key = "ConnectionUri.Error.InvalidQuery"; break;
                case ConnectionUriError.DuplicateParameter: key = "ConnectionUri.Error.DuplicateParameter"; break;
                case ConnectionUriError.UnsupportedParameter: key = "ConnectionUri.Error.UnsupportedParameter"; break;
                case ConnectionUriError.ConflictingParameter: key = "ConnectionUri.Error.ConflictingParameter"; break;
                case ConnectionUriError.MissingDatabaseOrPath: key = "ConnectionUri.Error.MissingDatabaseOrPath"; break;
                default: key = "ConnectionUri.Error.InvalidFormat"; break;
            }
            return Localization.Format(key, result.Detail);
        }
    }
}
