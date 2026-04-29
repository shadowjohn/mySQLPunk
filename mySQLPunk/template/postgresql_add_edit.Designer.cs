namespace mySQLPunk.template
{
    partial class postgresql_add_edit
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(postgresql_add_edit));
            this.tabControl1 = new System.Windows.Forms.TabControl();
            this.tabPage1 = new System.Windows.Forms.TabPage();
            this.postgresql_pwd = new System.Windows.Forms.TextBox();
            this.postgresql_username = new System.Windows.Forms.TextBox();
            this.postgresql_port = new System.Windows.Forms.TextBox();
            this.postgresql_host = new System.Windows.Forms.TextBox();
            this.postgresql_connection_name = new System.Windows.Forms.TextBox();
            this.label5 = new System.Windows.Forms.Label();
            this.label4 = new System.Windows.Forms.Label();
            this.label3 = new System.Windows.Forms.Label();
            this.label2 = new System.Windows.Forms.Label();
            this.label1 = new System.Windows.Forms.Label();
            this.postgresql_add_edit_test_connection = new System.Windows.Forms.Button();
            this.postgresql_add_edit_ok = new System.Windows.Forms.Button();
            this.postgresql_add_edit_cancel = new System.Windows.Forms.Button();
            this.label6 = new System.Windows.Forms.Label();
            this.postgresql_initial_database = new System.Windows.Forms.TextBox();
            this.tabControl1.SuspendLayout();
            this.tabPage1.SuspendLayout();
            this.SuspendLayout();
            // 
            // tabControl1
            // 
            this.tabControl1.Controls.Add(this.tabPage1);
            this.tabControl1.Location = new System.Drawing.Point(-1, 0);
            this.tabControl1.Name = "tabControl1";
            this.tabControl1.SelectedIndex = 0;
            this.tabControl1.Size = new System.Drawing.Size(514, 439);
            this.tabControl1.TabIndex = 0;
            // 
            // tabPage1
            // 
            this.tabPage1.Controls.Add(this.postgresql_initial_database);
            this.tabPage1.Controls.Add(this.label6);
            this.tabPage1.Controls.Add(this.postgresql_pwd);
            this.tabPage1.Controls.Add(this.postgresql_username);
            this.tabPage1.Controls.Add(this.postgresql_port);
            this.tabPage1.Controls.Add(this.postgresql_host);
            this.tabPage1.Controls.Add(this.postgresql_connection_name);
            this.tabPage1.Controls.Add(this.label5);
            this.tabPage1.Controls.Add(this.label4);
            this.tabPage1.Controls.Add(this.label3);
            this.tabPage1.Controls.Add(this.label2);
            this.tabPage1.Controls.Add(this.label1);
            this.tabPage1.Location = new System.Drawing.Point(4, 22);
            this.tabPage1.Name = "tabPage1";
            this.tabPage1.Padding = new System.Windows.Forms.Padding(3);
            this.tabPage1.Size = new System.Drawing.Size(506, 413);
            this.tabPage1.TabIndex = 0;
            this.tabPage1.Text = "General";
            this.tabPage1.UseVisualStyleBackColor = true;
            // 
            // postgresql_pwd
            // 
            this.postgresql_pwd.Font = new System.Drawing.Font("新細明體", 12F);
            this.postgresql_pwd.Location = new System.Drawing.Point(185, 205);
            this.postgresql_pwd.Name = "postgresql_pwd";
            this.postgresql_pwd.PasswordChar = '*';
            this.postgresql_pwd.Size = new System.Drawing.Size(167, 27);
            this.postgresql_pwd.TabIndex = 9;
            // 
            // postgresql_username
            // 
            this.postgresql_username.Font = new System.Drawing.Font("新細明體", 12F);
            this.postgresql_username.Location = new System.Drawing.Point(185, 172);
            this.postgresql_username.Name = "postgresql_username";
            this.postgresql_username.Size = new System.Drawing.Size(167, 27);
            this.postgresql_username.TabIndex = 8;
            this.postgresql_username.Text = "postgres";
            // 
            // postgresql_port
            // 
            this.postgresql_port.Font = new System.Drawing.Font("新細明體", 12F);
            this.postgresql_port.Location = new System.Drawing.Point(185, 106);
            this.postgresql_port.Name = "postgresql_port";
            this.postgresql_port.Size = new System.Drawing.Size(55, 27);
            this.postgresql_port.TabIndex = 7;
            this.postgresql_port.Text = "3306";
            // 
            // postgresql_host
            // 
            this.postgresql_host.Font = new System.Drawing.Font("新細明體", 12F);
            this.postgresql_host.Location = new System.Drawing.Point(185, 73);
            this.postgresql_host.Name = "postgresql_host";
            this.postgresql_host.Size = new System.Drawing.Size(291, 27);
            this.postgresql_host.TabIndex = 6;
            this.postgresql_host.Text = "localhost";
            // 
            // postgresql_connection_name
            // 
            this.postgresql_connection_name.Font = new System.Drawing.Font("新細明體", 12F);
            this.postgresql_connection_name.Location = new System.Drawing.Point(185, 21);
            this.postgresql_connection_name.Name = "postgresql_connection_name";
            this.postgresql_connection_name.Size = new System.Drawing.Size(291, 27);
            this.postgresql_connection_name.TabIndex = 5;
            // 
            // label5
            // 
            this.label5.AutoSize = true;
            this.label5.Font = new System.Drawing.Font("新細明體", 12F);
            this.label5.Location = new System.Drawing.Point(9, 208);
            this.label5.Name = "label5";
            this.label5.Size = new System.Drawing.Size(71, 16);
            this.label5.TabIndex = 4;
            this.label5.Text = "Password:";
            // 
            // label4
            // 
            this.label4.AutoSize = true;
            this.label4.Font = new System.Drawing.Font("新細明體", 12F);
            this.label4.Location = new System.Drawing.Point(9, 175);
            this.label4.Name = "label4";
            this.label4.Size = new System.Drawing.Size(82, 16);
            this.label4.TabIndex = 3;
            this.label4.Text = "User Name:";
            // 
            // label3
            // 
            this.label3.AutoSize = true;
            this.label3.Font = new System.Drawing.Font("新細明體", 12F);
            this.label3.Location = new System.Drawing.Point(9, 109);
            this.label3.Name = "label3";
            this.label3.Size = new System.Drawing.Size(37, 16);
            this.label3.TabIndex = 2;
            this.label3.Text = "Port:";
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Font = new System.Drawing.Font("新細明體", 12F);
            this.label2.Location = new System.Drawing.Point(9, 76);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(154, 16);
            this.label2.TabIndex = 1;
            this.label2.Text = "Host Name/IP Address:";
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Font = new System.Drawing.Font("新細明體", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(136)));
            this.label1.Location = new System.Drawing.Point(9, 24);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(125, 16);
            this.label1.TabIndex = 0;
            this.label1.Text = "Connection Name:";
            // 
            // postgresql_add_edit_test_connection
            // 
            this.postgresql_add_edit_test_connection.Font = new System.Drawing.Font("新細明體", 12F);
            this.postgresql_add_edit_test_connection.Location = new System.Drawing.Point(3, 441);
            this.postgresql_add_edit_test_connection.Name = "postgresql_add_edit_test_connection";
            this.postgresql_add_edit_test_connection.Size = new System.Drawing.Size(211, 39);
            this.postgresql_add_edit_test_connection.TabIndex = 1;
            this.postgresql_add_edit_test_connection.Text = "Test Connection";
            this.postgresql_add_edit_test_connection.UseVisualStyleBackColor = true;
            this.postgresql_add_edit_test_connection.Click += new System.EventHandler(this.button1_Click);
            // 
            // postgresql_add_edit_ok
            // 
            this.postgresql_add_edit_ok.Font = new System.Drawing.Font("新細明體", 12F);
            this.postgresql_add_edit_ok.Location = new System.Drawing.Point(309, 441);
            this.postgresql_add_edit_ok.Name = "postgresql_add_edit_ok";
            this.postgresql_add_edit_ok.Size = new System.Drawing.Size(86, 39);
            this.postgresql_add_edit_ok.TabIndex = 2;
            this.postgresql_add_edit_ok.Text = "OK";
            this.postgresql_add_edit_ok.UseVisualStyleBackColor = true;
            this.postgresql_add_edit_ok.Click += new System.EventHandler(this.button2_Click);
            // 
            // postgresql_add_edit_cancel
            // 
            this.postgresql_add_edit_cancel.Font = new System.Drawing.Font("新細明體", 12F);
            this.postgresql_add_edit_cancel.Location = new System.Drawing.Point(413, 441);
            this.postgresql_add_edit_cancel.Name = "postgresql_add_edit_cancel";
            this.postgresql_add_edit_cancel.Size = new System.Drawing.Size(86, 39);
            this.postgresql_add_edit_cancel.TabIndex = 3;
            this.postgresql_add_edit_cancel.Text = "Cancel";
            this.postgresql_add_edit_cancel.UseVisualStyleBackColor = true;
            this.postgresql_add_edit_cancel.Click += new System.EventHandler(this.button3_Click);
            // 
            // label6
            // 
            this.label6.AutoSize = true;
            this.label6.Font = new System.Drawing.Font("新細明體", 12F);
            this.label6.Location = new System.Drawing.Point(9, 142);
            this.label6.Name = "label6";
            this.label6.Size = new System.Drawing.Size(109, 16);
            this.label6.TabIndex = 10;
            this.label6.Text = "Initial Database:";
            // 
            // postgresql_initial_database
            // 
            this.postgresql_initial_database.Font = new System.Drawing.Font("新細明體", 12F);
            this.postgresql_initial_database.Location = new System.Drawing.Point(185, 139);
            this.postgresql_initial_database.Name = "postgresql_initial_database";
            this.postgresql_initial_database.Size = new System.Drawing.Size(167, 27);
            this.postgresql_initial_database.TabIndex = 11;
            this.postgresql_initial_database.Text = "template1";
            // 
            // postgresql_add_edit
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 12F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(511, 483);
            this.Controls.Add(this.postgresql_add_edit_cancel);
            this.Controls.Add(this.postgresql_add_edit_ok);
            this.Controls.Add(this.postgresql_add_edit_test_connection);
            this.Controls.Add(this.tabControl1);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "postgresql_add_edit";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "Postgresql";
            this.Load += new System.EventHandler(this.postgresql_add_edit_Load);
            this.tabControl1.ResumeLayout(false);
            this.tabPage1.ResumeLayout(false);
            this.tabPage1.PerformLayout();
            this.ResumeLayout(false);

        }

        #endregion

        private System.Windows.Forms.TabControl tabControl1;
        private System.Windows.Forms.TabPage tabPage1;
        private System.Windows.Forms.TextBox postgresql_pwd;
        private System.Windows.Forms.TextBox postgresql_username;
        private System.Windows.Forms.TextBox postgresql_port;
        private System.Windows.Forms.TextBox postgresql_host;
        private System.Windows.Forms.TextBox postgresql_connection_name;
        private System.Windows.Forms.Label label5;
        private System.Windows.Forms.Label label4;
        private System.Windows.Forms.Label label3;
        private System.Windows.Forms.Label label2;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.Button postgresql_add_edit_test_connection;
        private System.Windows.Forms.Button postgresql_add_edit_ok;
        private System.Windows.Forms.Button postgresql_add_edit_cancel;
        private System.Windows.Forms.TextBox postgresql_initial_database;
        private System.Windows.Forms.Label label6;
    }
}