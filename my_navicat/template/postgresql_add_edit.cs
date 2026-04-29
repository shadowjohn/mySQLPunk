using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace my_navicat.template
{
    public partial class postgresql_add_edit : Form
    {
        public postgresql_add_edit()
        {
            InitializeComponent();

        }
        public Form1 F1 { get; set; }

        private void postgresql_add_edit_Load(object sender, EventArgs e)
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
    }
}
