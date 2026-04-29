namespace mySQLPunk.template
{
    partial class mysql_add_edit
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
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(mysql_add_edit));
            this.tabControl1 = new System.Windows.Forms.TabControl();
            this.tabPage1 = new System.Windows.Forms.TabPage();
            this.mysql_add_edit_test_connection = new System.Windows.Forms.Button();
            this.label1 = new System.Windows.Forms.Label();
            this.label2 = new System.Windows.Forms.Label();
            this.label3 = new System.Windows.Forms.Label();
            this.label4 = new System.Windows.Forms.Label();
            this.label5 = new System.Windows.Forms.Label();
            this.mysql_connection_name = new System.Windows.Forms.TextBox();
            this.mysql_host = new System.Windows.Forms.TextBox();
            this.mysql_port = new System.Windows.Forms.TextBox();
            this.mysql_username = new System.Windows.Forms.TextBox();
            this.mysql_pwd = new System.Windows.Forms.TextBox();
            this.mysql_add_edit_ok = new System.Windows.Forms.Button();
            this.mysql_add_edit_cancel = new System.Windows.Forms.Button();
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
            this.tabPage1.Controls.Add(this.mysql_pwd);
            this.tabPage1.Controls.Add(this.mysql_username);
            this.tabPage1.Controls.Add(this.mysql_port);
            this.tabPage1.Controls.Add(this.mysql_host);
            this.tabPage1.Controls.Add(this.mysql_connection_name);
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
            // mysql_add_edit_test_connection
            // 
            this.mysql_add_edit_test_connection.Font = new System.Drawing.Font("新細明體", 12F);
            this.mysql_add_edit_test_connection.Location = new System.Drawing.Point(3, 441);
            this.mysql_add_edit_test_connection.Name = "mysql_add_edit_test_connection";
            this.mysql_add_edit_test_connection.Size = new System.Drawing.Size(211, 39);
            this.mysql_add_edit_test_connection.TabIndex = 1;
            this.mysql_add_edit_test_connection.Text = "Test Connection";
            this.mysql_add_edit_test_connection.UseVisualStyleBackColor = true;
            this.mysql_add_edit_test_connection.Click += new System.EventHandler(this.button1_Click);
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
            // label4
            // 
            this.label4.AutoSize = true;
            this.label4.Font = new System.Drawing.Font("新細明體", 12F);
            this.label4.Location = new System.Drawing.Point(9, 142);
            this.label4.Name = "label4";
            this.label4.Size = new System.Drawing.Size(82, 16);
            this.label4.TabIndex = 3;
            this.label4.Text = "User Name:";
            // 
            // label5
            // 
            this.label5.AutoSize = true;
            this.label5.Font = new System.Drawing.Font("新細明體", 12F);
            this.label5.Location = new System.Drawing.Point(9, 175);
            this.label5.Name = "label5";
            this.label5.Size = new System.Drawing.Size(71, 16);
            this.label5.TabIndex = 4;
            this.label5.Text = "Password:";
            // 
            // mysql_connection_name
            // 
            this.mysql_connection_name.Font = new System.Drawing.Font("新細明體", 12F);
            this.mysql_connection_name.Location = new System.Drawing.Point(185, 21);
            this.mysql_connection_name.Name = "mysql_connection_name";
            this.mysql_connection_name.Size = new System.Drawing.Size(291, 27);
            this.mysql_connection_name.TabIndex = 5;
            // 
            // mysql_host
            // 
            this.mysql_host.Font = new System.Drawing.Font("新細明體", 12F);
            this.mysql_host.Location = new System.Drawing.Point(185, 73);
            this.mysql_host.Name = "mysql_host";
            this.mysql_host.Size = new System.Drawing.Size(291, 27);
            this.mysql_host.TabIndex = 6;
            this.mysql_host.Text = "localhost";
            // 
            // mysql_port
            // 
            this.mysql_port.Font = new System.Drawing.Font("新細明體", 12F);
            this.mysql_port.Location = new System.Drawing.Point(185, 106);
            this.mysql_port.Name = "mysql_port";
            this.mysql_port.Size = new System.Drawing.Size(55, 27);
            this.mysql_port.TabIndex = 7;
            this.mysql_port.Text = "3306";
            // 
            // mysql_username
            // 
            this.mysql_username.Font = new System.Drawing.Font("新細明體", 12F);
            this.mysql_username.Location = new System.Drawing.Point(185, 139);
            this.mysql_username.Name = "mysql_username";
            this.mysql_username.Size = new System.Drawing.Size(167, 27);
            this.mysql_username.TabIndex = 8;
            this.mysql_username.Text = "root";
            // 
            // mysql_pwd
            // 
            this.mysql_pwd.Font = new System.Drawing.Font("新細明體", 12F);
            this.mysql_pwd.Location = new System.Drawing.Point(185, 172);
            this.mysql_pwd.Name = "mysql_pwd";
            this.mysql_pwd.PasswordChar = '*';
            this.mysql_pwd.Size = new System.Drawing.Size(167, 27);
            this.mysql_pwd.TabIndex = 9;
            // 
            // mysql_add_edit_ok
            // 
            this.mysql_add_edit_ok.Font = new System.Drawing.Font("新細明體", 12F);
            this.mysql_add_edit_ok.Location = new System.Drawing.Point(309, 441);
            this.mysql_add_edit_ok.Name = "mysql_add_edit_ok";
            this.mysql_add_edit_ok.Size = new System.Drawing.Size(86, 39);
            this.mysql_add_edit_ok.TabIndex = 2;
            this.mysql_add_edit_ok.Text = "OK";
            this.mysql_add_edit_ok.UseVisualStyleBackColor = true;
            this.mysql_add_edit_ok.Click += new System.EventHandler(this.button2_Click);
            // 
            // mysql_add_edit_cancel
            // 
            this.mysql_add_edit_cancel.Font = new System.Drawing.Font("新細明體", 12F);
            this.mysql_add_edit_cancel.Location = new System.Drawing.Point(413, 441);
            this.mysql_add_edit_cancel.Name = "mysql_add_edit_cancel";
            this.mysql_add_edit_cancel.Size = new System.Drawing.Size(86, 39);
            this.mysql_add_edit_cancel.TabIndex = 3;
            this.mysql_add_edit_cancel.Text = "Cancel";
            this.mysql_add_edit_cancel.UseVisualStyleBackColor = true;
            this.mysql_add_edit_cancel.Click += new System.EventHandler(this.button3_Click);
            // 
            // mysql_add_edit
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 12F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(511, 483);
            this.Controls.Add(this.mysql_add_edit_cancel);
            this.Controls.Add(this.mysql_add_edit_ok);
            this.Controls.Add(this.mysql_add_edit_test_connection);
            this.Controls.Add(this.tabControl1);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "mysql_add_edit";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "MySQL";
            this.Load += new System.EventHandler(this.mysql_add_edit_Load);
            this.tabControl1.ResumeLayout(false);
            this.tabPage1.ResumeLayout(false);
            this.tabPage1.PerformLayout();
            this.ResumeLayout(false);

        }

        #endregion

        private System.Windows.Forms.TabControl tabControl1;
        private System.Windows.Forms.TabPage tabPage1;
        private System.Windows.Forms.TextBox mysql_pwd;
        private System.Windows.Forms.TextBox mysql_username;
        private System.Windows.Forms.TextBox mysql_port;
        private System.Windows.Forms.TextBox mysql_host;
        private System.Windows.Forms.TextBox mysql_connection_name;
        private System.Windows.Forms.Label label5;
        private System.Windows.Forms.Label label4;
        private System.Windows.Forms.Label label3;
        private System.Windows.Forms.Label label2;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.Button mysql_add_edit_test_connection;
        private System.Windows.Forms.Button mysql_add_edit_ok;
        private System.Windows.Forms.Button mysql_add_edit_cancel;
    }
}