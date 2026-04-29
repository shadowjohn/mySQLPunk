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
    public partial class mysql_add_edit : Form
    {
        public mysql_add_edit()
        {
            InitializeComponent();
            Form1.ApplyModernTheme(this);
        }
        public Form1 F1 { get; set; }
        public int editIndex { get; set; } = -1;

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
            conn["initial_database"] = "";
            conn["db_kind"] = "mysql";
            conn["username"] = mysql_username.Text.Trim();
            conn["pwd"] = mysql_pwd.Text;
            conn["isConnect"] = "F";
            return conn;
        }

        private void button1_Click(object sender, EventArgs e)
        {
            // Test Connection
            try
            {
                string connStr = $"server={mysql_host.Text};user={mysql_username.Text};database=mysql;port={mysql_port.Text};password={mysql_pwd.Text};";
                my_mysql db = new my_mysql();
                db.SetConn(connStr);
                db.MCT.Open();
                MessageBox.Show("連線成功！", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                db.MCT.Close();
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
