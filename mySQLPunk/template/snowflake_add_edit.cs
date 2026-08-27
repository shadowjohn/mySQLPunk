using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk.template
{
    public sealed class snowflake_add_edit : Form, IConnectionDraftForm
    {
        private readonly TextBox connectionName = new TextBox();
        private readonly TextBox account = new TextBox();
        private readonly TextBox username = new TextBox();
        private readonly TextBox token = new TextBox();
        private readonly TextBox database = new TextBox();
        private readonly TextBox schema = new TextBox();
        private readonly TextBox warehouse = new TextBox();
        private readonly TextBox role = new TextBox();
        private readonly CheckBox useOAuth = new CheckBox();
        private readonly Button testButton = new Button();
        private readonly Button okButton = new Button();
        private readonly Button cancelButton = new Button();

        public Form1 F1 { get; set; }
        public int editIndex { get; set; } = -1;

        public snowflake_add_edit()
        {
            Text = "Snowflake";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(600, 540);
            MinimumSize = Size;
            MaximumSize = Size;

            token.UseSystemPasswordChar = true;
            useOAuth.Text = Localization.T("Snowflake.UseOAuth");
            useOAuth.AutoSize = true;

            TableLayoutPanel fields = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 11,
                Padding = new Padding(20, 18, 20, 8)
            };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddField(fields, 0, Localization.T("Common.ConnectionNameColon"), connectionName);
            AddField(fields, 1, Localization.T("Snowflake.AccountColon"), account);
            AddField(fields, 2, Localization.T("Common.UsernameColon"), username);
            AddField(fields, 3, Localization.T("Snowflake.TokenColon"), token);
            AddField(fields, 4, Localization.T("Snowflake.DatabaseColon"), database);
            AddField(fields, 5, Localization.T("Snowflake.SchemaColon"), schema);
            AddField(fields, 6, Localization.T("Snowflake.WarehouseColon"), warehouse);
            AddField(fields, 7, Localization.T("Snowflake.RoleColon"), role);

            FlowLayoutPanel options = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
            options.Controls.Add(useOAuth);
            fields.Controls.Add(new Label { Text = Localization.T("MongoDB.OptionsColon"), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, AutoSize = true }, 0, 8);
            fields.Controls.Add(options, 1, 8);

            Label note = new Label
            {
                Text = Localization.T("Snowflake.ReadOnlyNote"),
                Dock = DockStyle.Fill,
                AutoSize = true,
                ForeColor = ThemeManager.MutedTextColor
            };
            fields.Controls.Add(note, 0, 9);
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
            account.Text = GetValue(conn, "host");
            username.Text = GetValue(conn, "username");
            token.Text = GetValue(conn, "pwd");
            database.Text = GetValue(conn, "initial_database");
            schema.Text = GetValue(conn, "snowflake_schema");
            warehouse.Text = GetValue(conn, "snowflake_warehouse");
            role.Text = GetValue(conn, "snowflake_role");
            useOAuth.Checked = IsTrue(conn, "snowflake_oauth");
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
                MessageBox.Show(Localization.Format("Connection.TestSucceeded", "Snowflake"), Localization.T("Common.Success"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ConnectionDialogMessageService.BuildTestFailedMessage("Snowflake", ex), Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                { "db_kind", "snowflake" },
                { "host", account.Text.Trim() },
                { "username", username.Text.Trim() },
                { "pwd", token.Text },
                { "initial_database", database.Text.Trim() },
                { "snowflake_schema", schema.Text.Trim() },
                { "snowflake_warehouse", warehouse.Text.Trim() },
                { "snowflake_role", role.Text.Trim() },
                { "snowflake_oauth", useOAuth.Checked ? "T" : "F" },
                { "isConnect", "F" }
            };
        }

        private bool ValidateInput()
        {
            if (string.IsNullOrWhiteSpace(connectionName.Text)) return WarnAndFocus("Connection.EnterConnectionName", connectionName);
            if (string.IsNullOrWhiteSpace(account.Text)) return WarnAndFocus("Snowflake.AccountRequired", account);
            if (string.IsNullOrEmpty(token.Text)) return WarnAndFocus("Snowflake.TokenRequired", token);
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
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(8, 5, 0, 5);
            panel.Controls.Add(label, 0, row);
            panel.Controls.Add(control, 1, row);
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
