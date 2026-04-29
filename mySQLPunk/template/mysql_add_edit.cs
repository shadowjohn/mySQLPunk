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

        private void mysql_add_edit_Load(object sender, EventArgs e)
        {

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
            // OK
            Dictionary<string, object> conn = new Dictionary<string, object>();
            conn["name"] = mysql_connection_name.Text;
            conn["ip"] = mysql_host.Text;
            conn["port"] = mysql_port.Text;
            conn["kind"] = "mysql";
            conn["login_id"] = mysql_username.Text;
            conn["pwd"] = mysql_pwd.Text;
            conn["isConnect"] = "F";

            F1.add_connection(conn);
            this.Close();
        }

        private void button3_Click(object sender, EventArgs e)
        {
            // Cancel
            this.Close();
        }
    }
}
