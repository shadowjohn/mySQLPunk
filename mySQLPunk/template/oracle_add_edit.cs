using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Oracle.ManagedDataAccess.Client;
using mySQLPunk.lib;

namespace mySQLPunk.template
{
    public partial class oracle_add_edit : Form
    {
        public oracle_add_edit()
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
            Text = "Oracle";
            tabPage1.Text = Localization.T("Common.General");
            label1.Text = Localization.T("Common.ConnectionNameColon");
            label2.Text = Localization.T("Common.ConnectionType") + ":";
            label3.Text = Localization.T("Common.HostNameColon");
            label4.Text = Localization.T("Common.PortColon");
            label5.Text = Localization.T("Common.ServiceNameSid") + ":";
            label6.Text = Localization.T("Common.UsernameColon");
            label7.Text = Localization.T("Common.PasswordColon");
            label8.Text = Localization.T("Common.NetServiceName") + ":";
            label10.Text = Localization.T("Common.UsernameColon");
            label9.Text = Localization.T("Common.PasswordColon");
            radioButton1.Text = Localization.T("Common.ServiceName");
            radioButton2.Text = Localization.T("Common.SID");
            oracle_add_edit_test_connection.Text = Localization.T("Common.TestConnection");
            oracle_add_edit_ok.Text = Localization.T("Common.OK");
            oracle_add_edit_cancel.Text = Localization.T("Common.Cancel");
        }

        private void oracle_add_edit_Load(object sender, EventArgs e)
        {
            comboBox1.DropDownStyle = ComboBoxStyle.DropDown;
            ApplyLanguage();

            if (oracle_connection_type.SelectedIndex < 0)
            {
                oracle_connection_type.Text = "Basic";
            }

            if (string.IsNullOrWhiteSpace(textBox1.Text)) textBox1.Text = "localhost";
            if (string.IsNullOrWhiteSpace(textBox2.Text)) textBox2.Text = "1521";
            if (string.IsNullOrWhiteSpace(textBox3.Text)) textBox3.Text = "ORCLPDB1";

            if (F1 != null && editIndex >= 0)
            {
                Dictionary<string, object> conn = F1.get_connection(editIndex);
                oracle_connection_name.Text = GetValue(conn, "conn_name");
                oracle_connection_type.Text = string.IsNullOrWhiteSpace(GetValue(conn, "connection_type")) ? "Basic" : GetValue(conn, "connection_type");
                textBox1.Text = GetValue(conn, "host");
                textBox2.Text = string.IsNullOrWhiteSpace(GetValue(conn, "port")) ? "1521" : GetValue(conn, "port");
                textBox3.Text = GetServiceValue(conn);
                textBox4.Text = GetValue(conn, "username");
                textBox5.Text = GetValue(conn, "pwd");
                comboBox1.Text = GetValue(conn, "tns_name");
                textBox7.Text = GetValue(conn, "username");
                textBox6.Text = GetValue(conn, "pwd");
                radioButton1.Checked = GetValue(conn, "oracle_identifier_type") != "sid";
                radioButton2.Checked = GetValue(conn, "oracle_identifier_type") == "sid";
            }

            oracle_connection_type_selected_trigger_change();
        }

        private void button3_Click(object sender, EventArgs e)
        {
            
            this.Close();
            //Form1.ActiveForm.Parent.Parent.Parent.Parent.Parent.Enabled = true;
        }

        private void button1_Click(object sender, EventArgs e)
        {
            if (!ValidateInput()) return;

            try
            {
                using (my_oracle db = new my_oracle())
                {
                    db.SetConn(BuildConnectionString());
                    db.Open();
                    db.Close();
                }

                MessageBox.Show(Localization.Format("Connection.TestSucceeded", "Oracle"), Localization.T("Common.Success"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Localization.Format("Connection.TestFailed", "Oracle", ex.Message), Localization.T("Common.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        
        private void button2_Click(object sender, EventArgs e)
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
        public void oracle_connection_type_selected_trigger_change()
        {
            switch (oracle_connection_type.Text)
            {
                case "Basic":
                    oracle_panel_basic.BringToFront();
                    oracle_panel_tns.Visible = false;
                    oracle_panel_basic.Visible = false;
                    oracle_panel_basic.Visible = true;                                                            
                    break;
                case "TNS":
                    oracle_panel_tns.BringToFront();
                    oracle_panel_basic.Visible = false;
                    oracle_panel_tns.Visible = false; 
                    oracle_panel_tns.Visible = true;                    
                    break;
            }
        }
        private void oracle_connection_type_SelectedIndexChanged(object sender, EventArgs e)
        {
            //Console.WriteLine(oracle_connection_type.Text);
            oracle_connection_type_selected_trigger_change();
        }

        private bool ValidateInput()
        {
            if (string.IsNullOrWhiteSpace(oracle_connection_name.Text))
            {
                MessageBox.Show(Localization.T("Connection.EnterConnectionName"), Localization.T("Common.Warning"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                oracle_connection_name.Focus();
                return false;
            }

            if (IsTnsMode())
            {
                if (string.IsNullOrWhiteSpace(comboBox1.Text))
                {
                    MessageBox.Show(Localization.T("Connection.EnterNetServiceName"), Localization.T("Common.Warning"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    comboBox1.Focus();
                    return false;
                }
                if (string.IsNullOrWhiteSpace(textBox7.Text))
                {
                    MessageBox.Show(Localization.T("Connection.EnterUsername"), Localization.T("Common.Warning"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    textBox7.Focus();
                    return false;
                }
                return true;
            }

            if (string.IsNullOrWhiteSpace(textBox1.Text))
            {
                MessageBox.Show(Localization.T("Connection.EnterHost"), Localization.T("Common.Warning"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                textBox1.Focus();
                return false;
            }
            if (string.IsNullOrWhiteSpace(textBox2.Text))
            {
                MessageBox.Show(Localization.T("Connection.EnterPort"), Localization.T("Common.Warning"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                textBox2.Focus();
                return false;
            }
            if (string.IsNullOrWhiteSpace(textBox3.Text))
            {
                MessageBox.Show(Localization.T("Connection.EnterServiceNameOrSid"), Localization.T("Common.Warning"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                textBox3.Focus();
                return false;
            }
            if (string.IsNullOrWhiteSpace(textBox4.Text))
            {
                MessageBox.Show(Localization.T("Connection.EnterUsername"), Localization.T("Common.Warning"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                textBox4.Focus();
                return false;
            }

            return true;
        }

        private Dictionary<string, object> BuildConnection()
        {
            Dictionary<string, object> conn = new Dictionary<string, object>();
            conn["conn_name"] = oracle_connection_name.Text.Trim();
            conn["db_kind"] = "oracle";
            conn["connection_type"] = IsTnsMode() ? "TNS" : "Basic";
            conn["host"] = textBox1.Text.Trim();
            conn["port"] = textBox2.Text.Trim();
            conn["service_name"] = textBox3.Text.Trim();
            conn["sid"] = textBox3.Text.Trim();
            conn["oracle_identifier_type"] = radioButton2.Checked ? "sid" : "service_name";
            conn["tns_name"] = comboBox1.Text.Trim();
            conn["username"] = IsTnsMode() ? textBox7.Text.Trim() : textBox4.Text.Trim();
            conn["pwd"] = IsTnsMode() ? textBox6.Text : textBox5.Text;
            conn["connString"] = BuildConnectionString();
            conn["isConnect"] = "F";
            return conn;
        }

        private string BuildConnectionString()
        {
            OracleConnectionStringBuilder builder = new OracleConnectionStringBuilder();
            builder.UserID = IsTnsMode() ? textBox7.Text.Trim() : textBox4.Text.Trim();
            builder.Password = IsTnsMode() ? textBox6.Text : textBox5.Text;
            builder.DataSource = IsTnsMode() ? comboBox1.Text.Trim() : BuildBasicDataSource();
            return builder.ConnectionString;
        }

        private string BuildBasicDataSource()
        {
            string connectDataKey = radioButton2.Checked ? "SID" : "SERVICE_NAME";
            return "(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=" + textBox1.Text.Trim() + ")(PORT=" + textBox2.Text.Trim() + "))" +
                   "(CONNECT_DATA=(" + connectDataKey + "=" + textBox3.Text.Trim() + ")))";
        }

        private bool IsTnsMode()
        {
            return string.Equals(oracle_connection_type.Text, "TNS", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetServiceValue(Dictionary<string, object> conn)
        {
            string identifierType = GetValue(conn, "oracle_identifier_type");
            if (identifierType == "sid" && !string.IsNullOrWhiteSpace(GetValue(conn, "sid"))) return GetValue(conn, "sid");
            if (!string.IsNullOrWhiteSpace(GetValue(conn, "service_name"))) return GetValue(conn, "service_name");
            return GetValue(conn, "sid");
        }

        private static string GetValue(Dictionary<string, object> conn, string key)
        {
            if (conn != null && conn.ContainsKey(key) && conn[key] != null)
                return conn[key].ToString();
            return string.Empty;
        }
    }
}
