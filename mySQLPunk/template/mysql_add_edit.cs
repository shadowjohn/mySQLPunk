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
        private readonly Dictionary<string, object> securitySettings = new Dictionary<string, object>();
        // 這兩個控制項的實體在 Designer 的 InitializeComponent 建立

        public mysql_add_edit()
        {
            InitializeComponent();
            Form1.ApplyModernTheme(this);
            Localization.ApplyTo(this);
            ApplyLanguage();
        }

        public Form1 F1 { get; set; }
        public int editIndex { get; set; } = -1;

        private void ApplyLanguage()
        {
            Text = "MySQL";
            label1.Text = Localization.T("Common.ConnectionNameColon");
            label2.Text = Localization.T("Common.HostNameColon");
            label3.Text = Localization.T("Common.PortColon");
            label4.Text = Localization.T("Common.UsernameColon");
            label5.Text = Localization.T("Common.PasswordColon");
            mysql_add_edit_test_connection.Text = Localization.T("Common.TestConnection");
            mysql_add_edit_security.Text = "SSL / SSH...";
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
            ConnectionSecuritySettingsService.Copy(conn, securitySettings);
            UpdateSecuritySummary();
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
            ConnectionSecuritySettingsService.Copy(securitySettings, conn);
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

        private async void button1_Click(object sender, EventArgs e)
        {
            if (!ValidateInput())
            {
                return;
            }

            // Test Connection
            // 連線期間停用按鈕並丟到背景執行緒：以前同步 Open 會把整個視窗凍住，
            // 逾時期間的連點還會在恢復後再觸發一輪
            Button testButton = sender as Button;
            if (testButton != null) testButton.Enabled = false;
            try
            {
                // 用 builder 組字串：密碼含 ; 或 = 時字串串接會被拆錯，
                // 也不要硬連 mysql 系統庫（一般帳號沒有權限，會測試失敗但實際能連）
                var builder = new MySqlConnectionStringBuilder
                {
                    Server = mysql_host.Text.Trim(),
                    UserID = mysql_username.Text.Trim(),
                    Password = mysql_pwd.Text,
                    SslMode = MySqlSslMode.Preferred // 跟正式連線一致：能加密就加密，不支援自動退回
                };
                uint testPort;
                if (uint.TryParse(mysql_port.Text.Trim(), out testPort) && testPort > 0) builder.Port = testPort;
                string testInitialDb = mysql_initial_database.Text.Trim();
                if (testInitialDb.Length > 0) builder.Database = testInitialDb;
                string testConnectionString = builder.ConnectionString;
                await System.Threading.Tasks.Task.Run(() =>
                {
                    using (IDatabase db = ConnectionOpenService.Open(BuildConnection(), false).Database)
                    {
                        db.Close();
                    }
                });
                MessageBox.Show(Localization.Format("Connection.TestSucceeded", "MySQL"), Localization.T("Common.Success"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ConnectionDialogMessageService.BuildTestFailedMessage("MySQL", ex), Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (testButton != null && !testButton.IsDisposed) testButton.Enabled = true;
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

        private void securityButton_Click(object sender, EventArgs e)
        {
            Dictionary<string, object> draft = BuildConnection();
            if (ConnectionSecurityForm.Edit(this, "mysql", draft))
            {
                ConnectionSecuritySettingsService.Copy(draft, securitySettings);
                UpdateSecuritySummary();
            }
        }

        private void UpdateSecuritySummary()
        {
            if (mysql_add_edit_security != null)
                mysql_add_edit_security.Text = "SSL / SSH...  " + ConnectionSecuritySettingsService.GetSummary(securitySettings);
        }
    }
}
