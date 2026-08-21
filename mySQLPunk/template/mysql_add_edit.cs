using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using mySQLPunk.lib;
using MySqlConnector;

namespace mySQLPunk.template
{
    public partial class mysql_add_edit : Form
    {
        private Label initialDatabaseLabel;
        private TextBox mysql_initial_database;

        public mysql_add_edit()
        {
            InitializeComponent();
            InitializeInitialDatabaseField();
            Form1.ApplyModernTheme(this);
            Localization.ApplyTo(this);
            ApplyLanguage();
        }

        private void InitializeInitialDatabaseField()
        {
            initialDatabaseLabel = new Label { AutoSize = true, Location = new System.Drawing.Point(9, 208) };
            mysql_initial_database = new TextBox { Location = new System.Drawing.Point(185, 205), Size = new System.Drawing.Size(167, 27), TabIndex = 10 };
            tabPage1.Controls.Add(initialDatabaseLabel);
            tabPage1.Controls.Add(mysql_initial_database);
        }
        public Form1 F1 { get; set; }
        public int editIndex { get; set; } = -1;

        private void ApplyLanguage()
        {
            Text = "MySQL";
            tabPage1.Text = Localization.T("Common.General");
            label1.Text = Localization.T("Common.ConnectionNameColon");
            label2.Text = Localization.T("Common.HostNameColon");
            label3.Text = Localization.T("Common.PortColon");
            label4.Text = Localization.T("Common.UsernameColon");
            label5.Text = Localization.T("Common.PasswordColon");
            mysql_add_edit_test_connection.Text = Localization.T("Common.TestConnection");
            if (initialDatabaseLabel != null) initialDatabaseLabel.Text = Localization.T("Common.InitialDatabaseColon");
            mysql_add_edit_ok.Text = Localization.T("Common.OK");
            mysql_add_edit_cancel.Text = Localization.T("Common.Cancel");
        }

        private void mysql_add_edit_Load(object sender, EventArgs e)
        {
            if (F1 == null || editIndex < 0)
            {
                return;
            }

            Dictionary<string, object> conn = F1.get_connection(editIndex);
            mysql_connection_name.Text = GetValue(conn, "conn_name");
            mysql_host.Text = GetValue(conn, "host");
            mysql_port.Text = GetValue(conn, "port");
            mysql_username.Text = GetValue(conn, "username");
            mysql_pwd.Text = GetValue(conn, "pwd");
            mysql_initial_database.Text = GetValue(conn, "initial_database");
        }

        private static string GetValue(Dictionary<string, object> conn, string key)
        {
            if (conn != null && conn.ContainsKey(key) && conn[key] != null)
            {
                return conn[key].ToString();
            }

            return string.Empty;
        }

        private Dictionary<string, object> BuildConnection()
        {
            Dictionary<string, object> conn = new Dictionary<string, object>();
            conn["conn_name"] = mysql_connection_name.Text.Trim();
            conn["host"] = mysql_host.Text.Trim();
            conn["port"] = mysql_port.Text.Trim();
            conn["initial_database"] = mysql_initial_database.Text.Trim();
            conn["db_kind"] = "mysql";
            conn["username"] = mysql_username.Text.Trim();
            conn["pwd"] = mysql_pwd.Text;
            conn["isConnect"] = "F";
            return conn;
        }

        private bool ValidateInput()
        {
            if (string.IsNullOrWhiteSpace(mysql_connection_name.Text))
            {
                MessageBox.Show(Localization.T("Connection.EnterConnectionName"), Localization.T("Common.Warning"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                mysql_connection_name.Focus();
                return false;
            }

            if (string.IsNullOrWhiteSpace(mysql_host.Text))
            {
                MessageBox.Show(Localization.T("Connection.EnterHost"), Localization.T("Common.Warning"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                mysql_host.Focus();
                return false;
            }

            if (string.IsNullOrWhiteSpace(mysql_port.Text))
            {
                MessageBox.Show(Localization.T("Connection.EnterPort"), Localization.T("Common.Warning"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                mysql_port.Focus();
                return false;
            }

            if (string.IsNullOrWhiteSpace(mysql_username.Text))
            {
                MessageBox.Show(Localization.T("Connection.EnterUsername"), Localization.T("Common.Warning"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                mysql_username.Focus();
                return false;
            }

            return true;
        }

        private void button1_Click(object sender, EventArgs e)
        {
            if (!ValidateInput())
            {
                return;
            }

            // Test Connection
            try
            {
                // 用 builder 組字串：密碼含 ; 或 = 時字串串接會被拆錯，
                // 也不要硬連 mysql 系統庫（一般帳號沒有權限，會測試失敗但實際能連）
                var builder = new MySqlConnectionStringBuilder
                {
                    Server = mysql_host.Text.Trim(),
                    UserID = mysql_username.Text.Trim(),
                    Password = mysql_pwd.Text,
                    SslMode = MySqlSslMode.None
                };
                uint testPort;
                if (uint.TryParse(mysql_port.Text.Trim(), out testPort) && testPort > 0) builder.Port = testPort;
                string testInitialDb = mysql_initial_database.Text.Trim();
                if (testInitialDb.Length > 0) builder.Database = testInitialDb;
                my_mysql db = new my_mysql();
                db.SetConn(builder.ConnectionString);
                db.MCT.Open();
                MessageBox.Show(Localization.Format("Connection.TestSucceeded", "MySQL"), Localization.T("Common.Success"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                db.MCT.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ConnectionDialogMessageService.BuildTestFailedMessage("MySQL", ex), Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        
        private void button2_Click(object sender, EventArgs e)
        {
            if (F1 == null)
            {
                MessageBox.Show(Localization.T("Connection.MainWindowNotInitialized"), Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (!ValidateInput())
            {
                return;
            }

            Dictionary<string, object> conn = BuildConnection();

            if (editIndex >= 0)
            {
                F1.update_connection(editIndex, conn);
            }
            else
            {
                F1.add_connection(conn);
            }

            this.Close();
        }

        private void button3_Click(object sender, EventArgs e)
        {
            // Cancel
            this.Close();
        }
    }
}
