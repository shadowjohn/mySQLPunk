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

namespace mySQLPunk.template
{
    public partial class postgresql_add_edit : Form
    {
        public postgresql_add_edit()
        {
            InitializeComponent();
            Form1.ApplyModernTheme(this);
        }
        public Form1 F1 { get; set; }
        public int editIndex { get; set; } = -1;

        private void postgresql_add_edit_Load(object sender, EventArgs e)
        {
            if (F1 == null || editIndex < 0)
            {
                return;
            }

            Dictionary<string, object> conn = F1.get_connection(editIndex);
            postgresql_connection_name.Text = GetValue(conn, "conn_name");
            postgresql_host.Text = GetValue(conn, "host");
            postgresql_port.Text = GetValue(conn, "port");
            postgresql_initial_database.Text = GetValue(conn, "initial_database");
            postgresql_username.Text = GetValue(conn, "username");
            postgresql_pwd.Text = GetValue(conn, "pwd");
        }

        private static string GetValue(Dictionary<string, object> conn, string key)
        {
            if (conn != null && conn.ContainsKey(key) && conn[key] != null)
            {
                return conn[key].ToString();
            }

            return string.Empty;
        }

        private string GetInitialDatabase()
        {
            return string.IsNullOrWhiteSpace(postgresql_initial_database.Text)
                ? "postgres"
                : postgresql_initial_database.Text.Trim();
        }

        private bool ValidateInput()
        {
            if (string.IsNullOrWhiteSpace(postgresql_connection_name.Text))
            {
                MessageBox.Show("請輸入連線名稱！", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                postgresql_connection_name.Focus();
                return false;
            }

            if (string.IsNullOrWhiteSpace(postgresql_host.Text))
            {
                MessageBox.Show("請輸入主機名稱或 IP！", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                postgresql_host.Focus();
                return false;
            }

            if (string.IsNullOrWhiteSpace(postgresql_port.Text))
            {
                MessageBox.Show("請輸入連接埠！", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                postgresql_port.Focus();
                return false;
            }

            if (string.IsNullOrWhiteSpace(postgresql_username.Text))
            {
                MessageBox.Show("請輸入使用者名稱！", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                postgresql_username.Focus();
                return false;
            }

            return true;
        }

        private Dictionary<string, object> BuildConnection()
        {
            Dictionary<string, object> conn = new Dictionary<string, object>();
            conn["conn_name"] = postgresql_connection_name.Text.Trim();
            conn["host"] = postgresql_host.Text.Trim();
            conn["port"] = postgresql_port.Text.Trim();
            conn["initial_database"] = GetInitialDatabase();
            conn["db_kind"] = "postgresql";
            conn["username"] = postgresql_username.Text.Trim();
            conn["pwd"] = postgresql_pwd.Text;
            conn["isConnect"] = "F";
            return conn;
        }

        private void button3_Click(object sender, EventArgs e)
        {
            
            this.Close();
            //Form1.ActiveForm.Parent.Parent.Parent.Parent.Parent.Enabled = true;
        }

        private void button1_Click(object sender, EventArgs e)
        {
            if (!ValidateInput())
            {
                return;
            }

            try
            {
                string connStr = string.Format(
                    "Server={0};Port={1};User Id={2};Password={3};Database={4};",
                    postgresql_host.Text.Trim(),
                    postgresql_port.Text.Trim(),
                    postgresql_username.Text.Trim(),
                    postgresql_pwd.Text,
                    GetInitialDatabase());

                my_postgresql db = new my_postgresql();
                db.SetConn(connStr);
                db.Open();
                MessageBox.Show("連線成功！", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                db.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show("連線失敗：" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        
        private void button2_Click(object sender, EventArgs e)
        {
            if (F1 == null)
            {
                MessageBox.Show("主視窗未初始化！", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

            Close();
        }
    }
}
