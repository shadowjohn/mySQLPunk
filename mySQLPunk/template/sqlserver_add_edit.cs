using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Drawing;
using System.Windows.Forms;
using mySQLPunk.lib;

namespace mySQLPunk.template
{
    public class sqlserver_add_edit : Form
    {
        public Form1 F1 { get; set; }
        public int editIndex { get; set; } = -1;

        private TextBox txtName;
        private TextBox txtHost;
        private TextBox txtPort;
        private TextBox txtDatabase;
        private TextBox txtUser;
        private TextBox txtPassword;
        private CheckBox chkWindowsAuth;
        private Button btnTest;
        private Button btnOk;
        private Button btnCancel;

        public sqlserver_add_edit()
        {
            InitializeUi();
            Form1.ApplyModernTheme(this);
            Localization.ApplyTo(this);
        }

        private void InitializeUi()
        {
            Text = Localization.T("Common.SqlServerConnection");

            Label lblName = new Label { Text = Localization.T("Common.ConnectionName") };
            txtName = new TextBox();
            Label lblHost = new Label { Text = Localization.T("Common.Host") };
            txtHost = new TextBox { Text = "localhost" };
            Label lblPort = new Label { Text = Localization.T("Common.Port") };
            txtPort = new TextBox { Text = "1433" };
            Label lblDatabase = new Label { Text = Localization.T("Common.InitialDatabase") };
            txtDatabase = new TextBox { Text = "master" };
            chkWindowsAuth = new CheckBox { Text = Localization.T("Common.WindowsAuth") };
            Label lblUser = new Label { Text = Localization.T("Common.Username") };
            txtUser = new TextBox();
            Label lblPassword = new Label { Text = Localization.T("Common.Password") };
            txtPassword = new TextBox { UseSystemPasswordChar = true };
            btnTest = new Button { Text = Localization.T("Common.TestConnection") };
            btnOk = new Button { Text = Localization.T("Common.OK") };
            btnCancel = new Button { Text = Localization.T("Common.Cancel") };

            ConnectionDialogUi.Shell shell = ConnectionDialogUi.Build(this, "SQL Server", ConnectionDialogUi.SqlServerColor);
            ConnectionDialogUi.AddField(shell, lblName, txtName, ConnectionDialogUi.FieldWide);
            ConnectionDialogUi.AddField(shell, lblHost, txtHost, ConnectionDialogUi.FieldWide);
            ConnectionDialogUi.AddField(shell, lblPort, txtPort, ConnectionDialogUi.FieldNarrow);
            ConnectionDialogUi.AddField(shell, lblDatabase, txtDatabase, ConnectionDialogUi.FieldMedium);
            ConnectionDialogUi.AddFieldOnly(shell, chkWindowsAuth);
            ConnectionDialogUi.AddField(shell, lblUser, txtUser, ConnectionDialogUi.FieldMedium);
            ConnectionDialogUi.AddField(shell, lblPassword, txtPassword, ConnectionDialogUi.FieldMedium);
            ConnectionDialogUi.Finish(this, shell, btnTest, btnOk, btnCancel);

            chkWindowsAuth.CheckedChanged += (s, e) => ToggleAuthFields();
            Load += sqlserver_add_edit_Load;
            btnTest.Click += btnTest_Click;
            btnOk.Click += btnOk_Click;
            btnCancel.Click += (s, e) => Close();
        }

        private void sqlserver_add_edit_Load(object sender, EventArgs e)
        {
            if (F1 == null || editIndex < 0) return;

            Dictionary<string, object> conn = F1.get_connection(editIndex);
            txtName.Text = GetValue(conn, "conn_name");
            txtHost.Text = GetValue(conn, "host");
            txtPort.Text = string.IsNullOrWhiteSpace(GetValue(conn, "port")) ? "1433" : GetValue(conn, "port");
            txtDatabase.Text = string.IsNullOrWhiteSpace(GetValue(conn, "initial_database")) ? "master" : GetValue(conn, "initial_database");
            txtUser.Text = GetValue(conn, "username");
            txtPassword.Text = GetValue(conn, "pwd");
            chkWindowsAuth.Checked = GetValue(conn, "trusted_connection") == "T";
            ToggleAuthFields();
        }

        private void ToggleAuthFields()
        {
            bool enabled = !chkWindowsAuth.Checked;
            txtUser.Enabled = enabled;
            txtPassword.Enabled = enabled;
        }

        private bool ValidateInput()
        {
            if (string.IsNullOrWhiteSpace(txtName.Text))
            {
                MessageBox.Show(Localization.T("Connection.EnterConnectionName"), Localization.T("Common.Warning"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtName.Focus();
                return false;
            }

            if (string.IsNullOrWhiteSpace(txtHost.Text))
            {
                MessageBox.Show(Localization.T("Connection.EnterHost"), Localization.T("Common.Warning"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtHost.Focus();
                return false;
            }

            if (!chkWindowsAuth.Checked && string.IsNullOrWhiteSpace(txtUser.Text))
            {
                MessageBox.Show(Localization.T("Connection.EnterUsernameOrWindowsAuth"), Localization.T("Common.Warning"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtUser.Focus();
                return false;
            }

            return true;
        }

        private Dictionary<string, object> BuildConnection()
        {
            Dictionary<string, object> conn = new Dictionary<string, object>();
            conn["conn_name"] = txtName.Text.Trim();
            conn["host"] = txtHost.Text.Trim();
            conn["port"] = txtPort.Text.Trim();
            conn["initial_database"] = GetInitialDatabase();
            conn["db_kind"] = "sqlserver";
            conn["username"] = txtUser.Text.Trim();
            conn["pwd"] = txtPassword.Text;
            conn["trusted_connection"] = chkWindowsAuth.Checked ? "T" : "F";
            conn["isConnect"] = "F";
            return conn;
        }

        private string GetInitialDatabase()
        {
            return string.IsNullOrWhiteSpace(txtDatabase.Text) ? "master" : txtDatabase.Text.Trim();
        }

        private string BuildConnectionString()
        {
            SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder();
            builder.DataSource = BuildDataSource(txtHost.Text.Trim(), txtPort.Text.Trim());
            builder.InitialCatalog = GetInitialDatabase();
            builder.IntegratedSecurity = chkWindowsAuth.Checked;
            builder.TrustServerCertificate = true;
            builder.MultipleActiveResultSets = true;
            builder.ConnectTimeout = 8;

            if (!chkWindowsAuth.Checked)
            {
                builder.UserID = txtUser.Text.Trim();
                builder.Password = txtPassword.Text;
            }

            return builder.ConnectionString;
        }

        private static string BuildDataSource(string host, string port)
        {
            if (string.IsNullOrWhiteSpace(port)) return host;
            if (host.Contains(",") || host.Contains("\\")) return host;
            return host + "," + port;
        }

        private async void btnTest_Click(object sender, EventArgs e)
        {
            if (!ValidateInput()) return;

            // 連線期間停用按鈕並丟到背景執行緒：以前同步 Open 會把整個視窗凍住
            Button testButton = sender as Button;
            if (testButton != null) testButton.Enabled = false;
            try
            {
                string testConnectionString = BuildConnectionString();
                await System.Threading.Tasks.Task.Run(() =>
                {
                    using (my_mssql db = new my_mssql())
                    {
                        db.SetConn(testConnectionString);
                        db.Open();
                        db.Close();
                    }
                });

                MessageBox.Show(Localization.Format("Connection.TestSucceeded", "SQL Server"), Localization.T("Common.Success"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ConnectionDialogMessageService.BuildTestFailedMessage("SQL Server", ex), Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (testButton != null && !testButton.IsDisposed) testButton.Enabled = true;
            }
        }

        private void btnOk_Click(object sender, EventArgs e)
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

        private static string GetValue(Dictionary<string, object> conn, string key)
        {
            if (conn != null && conn.ContainsKey(key) && conn[key] != null)
                return conn[key].ToString();
            return string.Empty;
        }
    }
}
