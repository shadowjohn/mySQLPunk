using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace mySQLPunk.template
{
    public partial class oracle_add_edit : Form
    {
        public oracle_add_edit()
        {
            InitializeComponent();

        }
        public Form1 F1 { get; set; }

        private void oracle_add_edit_Load(object sender, EventArgs e)
        {

        }

        private void button3_Click(object sender, EventArgs e)
        {
            
            this.Close();
            //Form1.ActiveForm.Parent.Parent.Parent.Parent.Parent.Enabled = true;
        }

        private void button1_Click(object sender, EventArgs e)
        {
            //Form1.
        }
        
        private void button2_Click(object sender, EventArgs e)
        {
           
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
    }
}
