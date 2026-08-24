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
        /// 版面改用 ConnectionDialogUi 的共用格線，控制項名稱與事件沿用原本的。
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(postgresql_add_edit));
            this.postgresql_connection_name = new System.Windows.Forms.TextBox();
            this.postgresql_host = new System.Windows.Forms.TextBox();
            this.postgresql_port = new System.Windows.Forms.TextBox();
            this.postgresql_initial_database = new System.Windows.Forms.TextBox();
            this.postgresql_username = new System.Windows.Forms.TextBox();
            this.postgresql_pwd = new System.Windows.Forms.TextBox();
            this.label1 = new System.Windows.Forms.Label();
            this.label2 = new System.Windows.Forms.Label();
            this.label3 = new System.Windows.Forms.Label();
            this.label6 = new System.Windows.Forms.Label();
            this.label4 = new System.Windows.Forms.Label();
            this.label5 = new System.Windows.Forms.Label();
            this.postgresql_add_edit_test_connection = new System.Windows.Forms.Button();
            this.postgresql_add_edit_ok = new System.Windows.Forms.Button();
            this.postgresql_add_edit_cancel = new System.Windows.Forms.Button();
            this.SuspendLayout();
            //
            // 欄位預設值與名稱
            //
            this.postgresql_connection_name.Name = "postgresql_connection_name";
            this.postgresql_connection_name.TabIndex = 0;
            this.postgresql_host.Name = "postgresql_host";
            this.postgresql_host.Text = "localhost";
            this.postgresql_host.TabIndex = 1;
            this.postgresql_port.Name = "postgresql_port";
            this.postgresql_port.Text = "5432";
            this.postgresql_port.TabIndex = 2;
            this.postgresql_initial_database.Name = "postgresql_initial_database";
            this.postgresql_initial_database.Text = "postgres";
            this.postgresql_initial_database.TabIndex = 3;
            this.postgresql_username.Name = "postgresql_username";
            this.postgresql_username.Text = "postgres";
            this.postgresql_username.TabIndex = 4;
            this.postgresql_pwd.Name = "postgresql_pwd";
            this.postgresql_pwd.PasswordChar = '*';
            this.postgresql_pwd.TabIndex = 5;
            this.label1.Text = "Connection Name:";
            this.label2.Text = "Host Name/IP Address:";
            this.label3.Text = "Port:";
            this.label6.Text = "Initial Database:";
            this.label4.Text = "User Name:";
            this.label5.Text = "Password:";
            this.postgresql_add_edit_test_connection.Name = "postgresql_add_edit_test_connection";
            this.postgresql_add_edit_test_connection.Text = "Test Connection";
            this.postgresql_add_edit_test_connection.TabIndex = 6;
            this.postgresql_add_edit_test_connection.UseVisualStyleBackColor = true;
            this.postgresql_add_edit_test_connection.Click += new System.EventHandler(this.button1_Click);
            this.postgresql_add_edit_ok.Name = "postgresql_add_edit_ok";
            this.postgresql_add_edit_ok.Text = "OK";
            this.postgresql_add_edit_ok.TabIndex = 7;
            this.postgresql_add_edit_ok.UseVisualStyleBackColor = true;
            this.postgresql_add_edit_ok.Click += new System.EventHandler(this.button2_Click);
            this.postgresql_add_edit_cancel.Name = "postgresql_add_edit_cancel";
            this.postgresql_add_edit_cancel.Text = "Cancel";
            this.postgresql_add_edit_cancel.TabIndex = 8;
            this.postgresql_add_edit_cancel.UseVisualStyleBackColor = true;
            this.postgresql_add_edit_cancel.Click += new System.EventHandler(this.button3_Click);
            //
            // postgresql_add_edit
            //
            ConnectionDialogUi.Shell shell = ConnectionDialogUi.Build(this, "PostgreSQL", ConnectionDialogUi.PostgresColor);
            ConnectionDialogUi.AddField(shell, this.label1, this.postgresql_connection_name, ConnectionDialogUi.FieldWide);
            ConnectionDialogUi.AddField(shell, this.label2, this.postgresql_host, ConnectionDialogUi.FieldWide);
            ConnectionDialogUi.AddField(shell, this.label3, this.postgresql_port, ConnectionDialogUi.FieldNarrow);
            ConnectionDialogUi.AddField(shell, this.label6, this.postgresql_initial_database, ConnectionDialogUi.FieldMedium);
            ConnectionDialogUi.AddField(shell, this.label4, this.postgresql_username, ConnectionDialogUi.FieldMedium);
            ConnectionDialogUi.AddField(shell, this.label5, this.postgresql_pwd, ConnectionDialogUi.FieldMedium);
            ConnectionDialogUi.Finish(this, shell, this.postgresql_add_edit_test_connection, this.postgresql_add_edit_ok, this.postgresql_add_edit_cancel);
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.Name = "postgresql_add_edit";
            this.Text = "Postgresql";
            this.Load += new System.EventHandler(this.postgresql_add_edit_Load);
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        #endregion

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
