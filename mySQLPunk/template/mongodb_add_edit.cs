using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk.template
{
    public sealed class mongodb_add_edit : Form, IConnectionDraftForm
    {
        private readonly TextBox connectionName = new TextBox();
        private readonly ComboBox scheme = new ComboBox();
        private readonly TextBox host = new TextBox();
        private readonly TextBox port = new TextBox();
        private readonly TextBox database = new TextBox();
        private readonly TextBox authSource = new TextBox();
        private readonly TextBox username = new TextBox();
        private readonly TextBox password = new TextBox();
        private readonly TextBox replicaSet = new TextBox();
        private readonly CheckBox directConnection = new CheckBox();
        private readonly CheckBox retryWrites = new CheckBox();
        private readonly CheckBox useTls = new CheckBox();
        private readonly Button testButton = new Button();
        private readonly Button okButton = new Button();
        private readonly Button cancelButton = new Button();

        public Form1 F1 { get; set; }
        public int editIndex { get; set; } = -1;

        public mongodb_add_edit()
        {
            Text = "MongoDB";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(570, 505);
            MinimumSize = Size;
            MaximumSize = Size;

            scheme.DropDownStyle = ComboBoxStyle.DropDownList;
            scheme.Items.AddRange(new object[] { "mongodb", "mongodb+srv" });
            scheme.SelectedIndex = 0;
            scheme.SelectedIndexChanged += (s, e) => UpdateSchemeState();
            port.Text = "27017";
            authSource.Text = "admin";
            retryWrites.Text = Localization.T("MongoDB.RetryWrites");
            retryWrites.Checked = true;
            directConnection.Text = Localization.T("MongoDB.DirectConnection");
            useTls.Text = "TLS";
            password.UseSystemPasswordChar = true;

            TableLayoutPanel fields = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 12,
                Padding = new Padding(20, 18, 20, 8)
            };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddField(fields, 0, Localization.T("Common.ConnectionNameColon"), connectionName);
            AddField(fields, 1, Localization.T("MongoDB.SchemeColon"), scheme);
            AddField(fields, 2, Localization.T("Common.HostNameColon"), host);
            AddField(fields, 3, Localization.T("Common.PortColon"), port);
            AddField(fields, 4, Localization.T("Common.InitialDatabaseColon"), database);
            AddField(fields, 5, Localization.T("MongoDB.AuthSourceColon"), authSource);
            AddField(fields, 6, Localization.T("Common.UsernameColon"), username);
            AddField(fields, 7, Localization.T("Common.PasswordColon"), password);
            AddField(fields, 8, Localization.T("MongoDB.ReplicaSetColon"), replicaSet);

            FlowLayoutPanel options = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
            options.Controls.Add(directConnection);
            options.Controls.Add(retryWrites);
            options.Controls.Add(useTls);
            fields.Controls.Add(new Label { Text = Localization.T("MongoDB.OptionsColon"), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, AutoSize = true }, 0, 9);
            fields.Controls.Add(options, 1, 9);

            Label note = new Label
            {
                Text = Localization.T("MongoDB.ReadOnlyNote"),
                Dock = DockStyle.Fill,
                AutoSize = true,
                ForeColor = ThemeManager.MutedTextColor
            };
            fields.Controls.Add(note, 0, 10);
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
            UpdateSchemeState();
        }

        public void ApplyConnectionDraft(Dictionary<string, object> conn)
        {
            if (conn == null) return;
            connectionName.Text = GetValue(conn, "conn_name");
            scheme.SelectedItem = IsTrue(conn, "mongo_srv") ? "mongodb+srv" : "mongodb";
            host.Text = GetValue(conn, "host");
            port.Text = GetValue(conn, "port");
            database.Text = GetValue(conn, "initial_database");
            authSource.Text = GetValue(conn, "mongo_auth_source");
            username.Text = GetValue(conn, "username");
            password.Text = GetValue(conn, "pwd");
            replicaSet.Text = GetValue(conn, "mongo_replica_set");
            directConnection.Checked = IsTrue(conn, "mongo_direct_connection");
            retryWrites.Checked = !conn.ContainsKey("mongo_retry_writes") || IsTrue(conn, "mongo_retry_writes");
            useTls.Checked = IsTrue(conn, "mongo_tls") || string.Equals(GetValue(conn, "tls_mode"), "Required", StringComparison.OrdinalIgnoreCase);
            UpdateSchemeState();
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
                MessageBox.Show(Localization.Format("Connection.TestSucceeded", "MongoDB"), Localization.T("Common.Success"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ConnectionDialogMessageService.BuildTestFailedMessage("MongoDB", ex), Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                { "db_kind", "mongodb" },
                { "host", host.Text.Trim() },
                { "port", scheme.SelectedIndex == 1 ? string.Empty : port.Text.Trim() },
                { "initial_database", database.Text.Trim() },
                { "username", username.Text.Trim() },
                { "pwd", password.Text },
                { "mongo_srv", scheme.SelectedIndex == 1 ? "T" : "F" },
                { "mongo_auth_source", authSource.Text.Trim() },
                { "mongo_replica_set", replicaSet.Text.Trim() },
                { "mongo_direct_connection", directConnection.Checked ? "T" : "F" },
                { "mongo_retry_writes", retryWrites.Checked ? "T" : "F" },
                { "mongo_tls", useTls.Checked ? "T" : "F" },
                { "isConnect", "F" }
            };
        }

        private bool ValidateInput()
        {
            if (string.IsNullOrWhiteSpace(connectionName.Text)) return WarnAndFocus("Connection.EnterConnectionName", connectionName);
            if (string.IsNullOrWhiteSpace(host.Text)) return WarnAndFocus("Connection.EnterHost", host);
            if (scheme.SelectedIndex == 0)
            {
                int parsedPort;
                if (!int.TryParse(port.Text.Trim(), out parsedPort) || parsedPort < 1 || parsedPort > 65535)
                    return WarnAndFocus("MongoDB.InvalidPort", port);
            }
            if (directConnection.Checked && scheme.SelectedIndex == 1)
                return WarnAndFocus("MongoDB.SrvDirectConflict", scheme);
            return true;
        }

        private bool WarnAndFocus(string key, Control control)
        {
            MessageBox.Show(Localization.T(key), Localization.T("Common.Warning"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            control.Focus();
            return false;
        }

        private void UpdateSchemeState()
        {
            bool srv = scheme.SelectedIndex == 1;
            port.Enabled = !srv;
            directConnection.Enabled = !srv;
            if (srv)
            {
                directConnection.Checked = false;
                useTls.Checked = true;
            }
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
