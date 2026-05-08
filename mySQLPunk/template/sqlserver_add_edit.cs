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
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(560, 360);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            int labelX = 24;
            int inputX = 145;
            int y = 24;

            Controls.Add(new Label { Text = Localization.T("Common.ConnectionName"), Location = new Point(labelX, y + 4), AutoSize = true });
            txtName = new TextBox { Location = new Point(inputX, y), Width = 360 };
            Controls.Add(txtName);

            y += 40;
            Controls.Add(new Label { Text = Localization.T("Common.Host"), Location = new Point(labelX, y + 4), AutoSize = true });
            txtHost = new TextBox { Location = new Point(inputX, y), Width = 360, Text = "localhost" };
            Controls.Add(txtHost);

            y += 40;
            Controls.Add(new Label { Text = Localization.T("Common.Port"), Location = new Point(labelX, y + 4), AutoSize = true });
            txtPort = new TextBox { Location = new Point(inputX, y), Width = 120, Text = "1433" };
            Controls.Add(txtPort);

            y += 40;
            Controls.Add(new Label { Text = Localization.T("Common.InitialDatabase"), Location = new Point(labelX, y + 4), AutoSize = true });
            txtDatabase = new TextBox { Location = new Point(inputX, y), Width = 160, Text = "master" };
            Controls.Add(txtDatabase);

            y += 40;
            chkWindowsAuth = new CheckBox { Text = Localization.T("Common.WindowsAuth"), Location = new Point(inputX, y), Width = 260 };
            chkWindowsAuth.CheckedChanged += (s, e) => ToggleAuthFields();
            Controls.Add(chkWindowsAuth);

            y += 40;
            Controls.Add(new Label { Text = Localization.T("Common.Username"), Location = new Point(labelX, y + 4), AutoSize = true });
            txtUser = new TextBox { Location = new Point(inputX, y), Width = 240 };
            Controls.Add(txtUser);

            y += 40;
            Controls.Add(new Label { Text = Localization.T("Common.Password"), Location = new Point(labelX, y + 4), AutoSize = true });
            txtPassword = new TextBox { Location = new Point(inputX, y), Width = 240, UseSystemPasswordChar = true };
            Controls.Add(txtPassword);

            btnTest = new Button { Text = Localization.T("Common.TestConnection"), Location = new Point(24, 284), Size = new Size(145, 34) };
            btnOk = new Button { Text = Localization.T("Common.OK"), Location = new Point(356, 284), Size = new Size(75, 34) };
            btnCancel = new Button { Text = Localization.T("Common.Cancel"), Location = new Point(440, 284), Size = new Size(75, 34) };
            Controls.Add(btnTest);
            Controls.Add(btnOk);
            Controls.Add(btnCancel);

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
                MessageBox.Show("請輸入連線名稱！", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtName.Focus();
                return false;
            }

            if (string.IsNullOrWhiteSpace(txtHost.Text))
            {
                MessageBox.Show("請輸入主機名稱或 IP！", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtHost.Focus();
                return false;
            }

            if (string.IsNullOrWhiteSpace(txtPort.Text))
            {
                MessageBox.Show("請輸入連接埠！", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtPort.Focus();
                return false;
            }

            if (!chkWindowsAuth.Checked && string.IsNullOrWhiteSpace(txtUser.Text))
            {
                MessageBox.Show("請輸入使用者名稱，或改用 Windows 驗證。", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            builder.DataSource = txtHost.Text.Trim() + "," + txtPort.Text.Trim();
            builder.InitialCatalog = GetInitialDatabase();
            builder.IntegratedSecurity = chkWindowsAuth.Checked;
            builder.TrustServerCertificate = true;

            if (!chkWindowsAuth.Checked)
            {
                builder.UserID = txtUser.Text.Trim();
                builder.Password = txtPassword.Text;
            }

            return builder.ConnectionString;
        }

        private void btnTest_Click(object sender, EventArgs e)
        {
            if (!ValidateInput()) return;

            try
            {
                using (my_mssql db = new my_mssql())
                {
                    db.SetConn(BuildConnectionString());
                    db.Open();
                    db.Close();
                }

                MessageBox.Show("SQL Server 連線成功。", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("SQL Server 連線失敗：" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnOk_Click(object sender, EventArgs e)
        {
            if (F1 == null)
            {
                MessageBox.Show("主視窗未初始化！", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
