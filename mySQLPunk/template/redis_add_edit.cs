using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk.template
{
    public sealed class redis_add_edit : Form, IConnectionDraftForm
    {
        private readonly TextBox connectionName = new TextBox();
        private readonly TextBox host = new TextBox();
        private readonly TextBox port = new TextBox();
        private readonly TextBox username = new TextBox();
        private readonly TextBox password = new TextBox();
        private readonly TextBox databaseIndex = new TextBox();
        private readonly CheckBox useTls = new CheckBox();
        private readonly Button testButton = new Button();
        private readonly Button okButton = new Button();
        private readonly Button cancelButton = new Button();

        public Form1 F1 { get; set; }
        public int editIndex { get; set; } = -1;

        public redis_add_edit()
        {
            Text = "Redis / Garnet";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(570, 420);
            MinimumSize = Size;
            MaximumSize = Size;

            port.Text = "6379";
            databaseIndex.Text = "0";
            useTls.Text = "TLS";
            password.UseSystemPasswordChar = true;

            TableLayoutPanel fields = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 9,
                Padding = new Padding(20, 18, 20, 8)
            };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddField(fields, 0, Localization.T("Common.ConnectionNameColon"), connectionName);
            AddField(fields, 1, Localization.T("Common.HostNameColon"), host);
            AddField(fields, 2, Localization.T("Common.PortColon"), port);
            AddField(fields, 3, Localization.T("Redis.UsernameColon"), username);
            AddField(fields, 4, Localization.T("Common.PasswordColon"), password);
            AddField(fields, 5, Localization.T("Redis.DatabaseIndexColon"), databaseIndex);

            FlowLayoutPanel options = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
            options.Controls.Add(useTls);
            fields.Controls.Add(new Label { Text = Localization.T("MongoDB.OptionsColon"), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, AutoSize = true }, 0, 6);
            fields.Controls.Add(options, 1, 6);

            Label note = new Label
            {
                Text = Localization.T("Redis.ReadOnlyNote"),
                Dock = DockStyle.Fill,
                AutoSize = true,
                ForeColor = ThemeManager.MutedTextColor
            };
            fields.Controls.Add(note, 0, 7);
            fields.SetColumnSpan(note, 2);

            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 58,
                Padding = new Padding(18, 10, 18, 10),
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false
            };
            okButton.Text = Localization.T("Common.OK");
            cancelButton.Text = Localization.T("Common.Cancel");
            testButton.Text = Localization.T("Common.TestConnection");
            okButton.AutoSize = cancelButton.AutoSize = testButton.AutoSize = true;
            okButton.Click += SaveConnection;
            cancelButton.Click += (s, e) => Close();
            testButton.Click += TestConnection;
            buttons.Controls.Add(okButton);
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(testButton);

            Controls.Add(fields);
            Controls.Add(buttons);
            AcceptButton = okButton;
            CancelButton = cancelButton;
            Load += LoadConnection;
            Form1.ApplyModernTheme(this);
            Localization.ApplyTo(this);
        }

        public void ApplyConnectionDraft(Dictionary<string, object> conn)
        {
            if (conn == null) return;
            connectionName.Text = GetValue(conn, "conn_name");
            host.Text = GetValue(conn, "host");
            port.Text = string.IsNullOrWhiteSpace(GetValue(conn, "port")) ? "6379" : GetValue(conn, "port");
            username.Text = GetValue(conn, "username");
            password.Text = GetValue(conn, "pwd");
            databaseIndex.Text = string.IsNullOrWhiteSpace(GetValue(conn, "initial_database")) ? "0" : GetValue(conn, "initial_database");
            useTls.Checked = IsTrue(conn, "redis_tls");
        }

        private void LoadConnection(object sender, EventArgs e)
        {
            if (F1 == null || editIndex < 0) return;
            ApplyConnectionDraft(F1.get_connection(editIndex));
        }

        private async void TestConnection(object sender, EventArgs e)
        {
            if (!ValidateInput()) return;
            testButton.Enabled = false;
            try
            {
                await Task.Run(() =>
                {
                    using (IDatabase db = ConnectionOpenService.Open(BuildConnection(), false).Database) db.Close();
                });
                MessageBox.Show(Localization.Format("Connection.TestSucceeded", "Redis"), Localization.T("Common.Success"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ConnectionDialogMessageService.BuildTestFailedMessage("Redis", ex), Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (!IsDisposed) testButton.Enabled = true;
            }
        }

        private void SaveConnection(object sender, EventArgs e)
        {
            if (F1 == null)
            {
                MessageBox.Show(Localization.T("Connection.MainWindowNotInitialized"), Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (!ValidateInput()) return;
            Dictionary<string, object> conn = BuildConnection();
            if (editIndex >= 0) F1.update_connection(editIndex, conn);
            else F1.add_connection(conn);
            Close();
        }

        private Dictionary<string, object> BuildConnection()
        {
            return new Dictionary<string, object>
            {
                { "conn_name", connectionName.Text.Trim() },
                { "db_kind", "redis" },
                { "host", host.Text.Trim() },
                { "port", port.Text.Trim() },
                { "username", username.Text.Trim() },
                { "pwd", password.Text },
                { "initial_database", databaseIndex.Text.Trim() },
                { "redis_tls", useTls.Checked ? "T" : "F" },
                { "redis_auth_required", !string.IsNullOrWhiteSpace(username.Text) || !string.IsNullOrEmpty(password.Text) ? "T" : "F" },
                { "isConnect", "F" }
            };
        }

        private bool ValidateInput()
        {
            if (string.IsNullOrWhiteSpace(connectionName.Text)) return WarnAndFocus("Connection.EnterConnectionName", connectionName);
            if (string.IsNullOrWhiteSpace(host.Text)) return WarnAndFocus("Connection.EnterHost", host);
            int parsedPort;
            if (!int.TryParse(port.Text.Trim(), out parsedPort) || parsedPort < 1 || parsedPort > 65535)
                return WarnAndFocus("Redis.InvalidPort", port);
            int parsedIndex;
            if (!int.TryParse(databaseIndex.Text.Trim(), out parsedIndex) || parsedIndex < 0)
                return WarnAndFocus("Redis.InvalidDatabaseIndex", databaseIndex);
            return true;
        }

        private bool WarnAndFocus(string key, Control control)
        {
            MessageBox.Show(Localization.T(key), Localization.T("Common.Warning"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            control.Focus();
            return false;
        }

        private static void AddField(TableLayoutPanel panel, int row, string labelText, Control control)
        {
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            Label label = new Label { Text = labelText, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, AutoSize = true };
            Control field = UiField.Wrap(control);
            field.Dock = DockStyle.Fill;
            field.Margin = new Padding(8, 4, 0, 4);
            panel.Controls.Add(label, 0, row);
            panel.Controls.Add(field, 1, row);
        }

        private static bool IsTrue(Dictionary<string, object> conn, string key)
        {
            return string.Equals(GetValue(conn, key), "T", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(GetValue(conn, key), "true", StringComparison.OrdinalIgnoreCase) ||
                   GetValue(conn, key) == "1";
        }

        private static string GetValue(Dictionary<string, object> conn, string key)
        {
            return conn != null && conn.ContainsKey(key) && conn[key] != null ? conn[key].ToString() : string.Empty;
        }
    }
}
