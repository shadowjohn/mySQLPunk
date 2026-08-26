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
        /// 版面改用 ConnectionDialogUi 的共用格線，控制項名稱與事件沿用原本的。
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(mysql_add_edit));
            this.mysql_connection_name = new System.Windows.Forms.TextBox();
            this.mysql_host = new System.Windows.Forms.TextBox();
            this.mysql_port = new System.Windows.Forms.TextBox();
            this.mysql_initial_database = new System.Windows.Forms.TextBox();
            this.mysql_username = new System.Windows.Forms.TextBox();
            this.mysql_pwd = new System.Windows.Forms.TextBox();
            this.label1 = new System.Windows.Forms.Label();
            this.label2 = new System.Windows.Forms.Label();
            this.label3 = new System.Windows.Forms.Label();
            this.initialDatabaseLabel = new System.Windows.Forms.Label();
            this.label4 = new System.Windows.Forms.Label();
            this.label5 = new System.Windows.Forms.Label();
            this.mysql_add_edit_test_connection = new System.Windows.Forms.Button();
            this.mysql_add_edit_security = new System.Windows.Forms.Button();
            this.mysql_add_edit_ok = new System.Windows.Forms.Button();
            this.mysql_add_edit_cancel = new System.Windows.Forms.Button();
            this.SuspendLayout();
            //
            // 欄位預設值與名稱
            //
            this.mysql_connection_name.Name = "mysql_connection_name";
            this.mysql_connection_name.TabIndex = 0;
            this.mysql_host.Name = "mysql_host";
            this.mysql_host.Text = "localhost";
            this.mysql_host.TabIndex = 1;
            this.mysql_port.Name = "mysql_port";
            this.mysql_port.Text = "3306";
            this.mysql_port.TabIndex = 2;
            this.mysql_initial_database.Name = "mysql_initial_database";
            this.mysql_initial_database.TabIndex = 3;
            this.mysql_username.Name = "mysql_username";
            this.mysql_username.Text = "root";
            this.mysql_username.TabIndex = 4;
            this.mysql_pwd.Name = "mysql_pwd";
            this.mysql_pwd.PasswordChar = '*';
            this.mysql_pwd.TabIndex = 5;
            this.label1.Text = "Connection Name:";
            this.label2.Text = "Host Name/IP Address:";
            this.label3.Text = "Port:";
            this.initialDatabaseLabel.Text = "Initial Database:";
            this.label4.Text = "User Name:";
            this.label5.Text = "Password:";
            this.mysql_add_edit_test_connection.Name = "mysql_add_edit_test_connection";
            this.mysql_add_edit_test_connection.Text = "Test Connection";
            this.mysql_add_edit_test_connection.TabIndex = 6;
            this.mysql_add_edit_test_connection.UseVisualStyleBackColor = true;
            this.mysql_add_edit_test_connection.Click += new System.EventHandler(this.button1_Click);
            this.mysql_add_edit_security.Name = "mysql_add_edit_security";
            this.mysql_add_edit_security.Text = "SSL / SSH...";
            this.mysql_add_edit_security.AutoSize = true;
            this.mysql_add_edit_security.Click += new System.EventHandler(this.securityButton_Click);
            this.mysql_add_edit_ok.Name = "mysql_add_edit_ok";
            this.mysql_add_edit_ok.Text = "OK";
            this.mysql_add_edit_ok.TabIndex = 7;
            this.mysql_add_edit_ok.UseVisualStyleBackColor = true;
            this.mysql_add_edit_ok.Click += new System.EventHandler(this.button2_Click);
            this.mysql_add_edit_cancel.Name = "mysql_add_edit_cancel";
            this.mysql_add_edit_cancel.Text = "Cancel";
            this.mysql_add_edit_cancel.TabIndex = 8;
            this.mysql_add_edit_cancel.UseVisualStyleBackColor = true;
            this.mysql_add_edit_cancel.Click += new System.EventHandler(this.button3_Click);
            //
            // mysql_add_edit
            //
            ConnectionDialogUi.Shell shell = ConnectionDialogUi.Build(this, "MySQL / MariaDB", ConnectionDialogUi.MySqlColor);
            ConnectionDialogUi.AddField(shell, this.label1, this.mysql_connection_name, ConnectionDialogUi.FieldWide);
            ConnectionDialogUi.AddField(shell, this.label2, this.mysql_host, ConnectionDialogUi.FieldWide);
            ConnectionDialogUi.AddField(shell, this.label3, this.mysql_port, ConnectionDialogUi.FieldNarrow);
            ConnectionDialogUi.AddField(shell, this.initialDatabaseLabel, this.mysql_initial_database, ConnectionDialogUi.FieldMedium);
            ConnectionDialogUi.AddField(shell, this.label4, this.mysql_username, ConnectionDialogUi.FieldMedium);
            ConnectionDialogUi.AddField(shell, this.label5, this.mysql_pwd, ConnectionDialogUi.FieldMedium);
            ConnectionDialogUi.AddFieldOnly(shell, this.mysql_add_edit_security);
            ConnectionDialogUi.Finish(this, shell, this.mysql_add_edit_test_connection, this.mysql_add_edit_ok, this.mysql_add_edit_cancel);
            this.Icon = mySQLPunk.lib.AppIconService.AppIcon; // 看板娘 Punky，取代 resx 裡的舊圖示
            this.Name = "mysql_add_edit";
            this.Text = "MySQL";
            this.Load += new System.EventHandler(this.mysql_add_edit_Load);
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        #endregion

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
        private System.Windows.Forms.Button mysql_add_edit_security;
        private System.Windows.Forms.Button mysql_add_edit_ok;
        private System.Windows.Forms.Button mysql_add_edit_cancel;
    }
}
