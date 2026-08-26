namespace mySQLPunk.template
{
    partial class oracle_add_edit
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
        /// 版面改用 ConnectionDialogUi 的共用格線；Basic／TNS 仍是兩個面板切換，
        /// 各自內部用同寬的標籤欄，讓兩種模式的欄位對齊一致。
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(oracle_add_edit));
            this.oracle_connection_name = new System.Windows.Forms.TextBox();
            this.oracle_connection_type = new System.Windows.Forms.ComboBox();
            this.label1 = new System.Windows.Forms.Label();
            this.label2 = new System.Windows.Forms.Label();
            this.oracle_panel_basic = new System.Windows.Forms.Panel();
            this.textBox1 = new System.Windows.Forms.TextBox();
            this.textBox2 = new System.Windows.Forms.TextBox();
            this.textBox3 = new System.Windows.Forms.TextBox();
            this.textBox4 = new System.Windows.Forms.TextBox();
            this.textBox5 = new System.Windows.Forms.TextBox();
            this.label3 = new System.Windows.Forms.Label();
            this.label4 = new System.Windows.Forms.Label();
            this.label5 = new System.Windows.Forms.Label();
            this.label6 = new System.Windows.Forms.Label();
            this.label7 = new System.Windows.Forms.Label();
            this.radioButton1 = new System.Windows.Forms.RadioButton();
            this.radioButton2 = new System.Windows.Forms.RadioButton();
            this.oracle_panel_tns = new System.Windows.Forms.Panel();
            this.comboBox1 = new System.Windows.Forms.ComboBox();
            this.textBox6 = new System.Windows.Forms.TextBox();
            this.textBox7 = new System.Windows.Forms.TextBox();
            this.label8 = new System.Windows.Forms.Label();
            this.label9 = new System.Windows.Forms.Label();
            this.label10 = new System.Windows.Forms.Label();
            this.oracle_add_edit_test_connection = new System.Windows.Forms.Button();
            this.oracle_add_edit_security = new System.Windows.Forms.Button();
            this.oracle_add_edit_ok = new System.Windows.Forms.Button();
            this.oracle_add_edit_cancel = new System.Windows.Forms.Button();
            this.SuspendLayout();
            //
            // 共用欄位
            //
            this.oracle_connection_name.Name = "oracle_connection_name";
            this.oracle_connection_name.TabIndex = 0;
            this.oracle_connection_type.Name = "oracle_connection_type";
            this.oracle_connection_type.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.oracle_connection_type.FormattingEnabled = true;
            this.oracle_connection_type.Items.AddRange(new object[] { "Basic", "TNS" });
            this.oracle_connection_type.TabIndex = 1;
            this.oracle_connection_type.SelectedIndexChanged += new System.EventHandler(this.oracle_connection_type_SelectedIndexChanged);
            this.label1.Text = "Connection Name:";
            this.label2.Text = "Connection Type:";
            //
            // Basic 模式欄位
            //
            this.textBox1.Name = "textBox1";
            this.textBox1.Text = "localhost";
            this.textBox1.TabIndex = 0;
            this.textBox2.Name = "textBox2";
            this.textBox2.Text = "1521";
            this.textBox2.TabIndex = 1;
            this.radioButton1.Name = "radioButton1";
            this.radioButton1.Text = "Service Name";
            this.radioButton1.AutoSize = true;
            this.radioButton1.Checked = true;
            this.radioButton1.TabIndex = 2;
            this.radioButton2.Name = "radioButton2";
            this.radioButton2.Text = "SID";
            this.radioButton2.AutoSize = true;
            this.radioButton2.TabIndex = 3;
            this.textBox3.Name = "textBox3";
            this.textBox3.Text = "ORCLPDB1";
            this.textBox3.TabIndex = 4;
            this.textBox4.Name = "textBox4";
            this.textBox4.TabIndex = 5;
            this.textBox5.Name = "textBox5";
            this.textBox5.PasswordChar = '*';
            this.textBox5.TabIndex = 6;
            this.label3.Text = "Host Name/IP Address:";
            this.label4.Text = "Port:";
            this.label5.Text = "Service Name/SID:";
            this.label6.Text = "User Name:";
            this.label7.Text = "Password:";
            //
            // TNS 模式欄位
            //
            this.comboBox1.Name = "comboBox1";
            this.comboBox1.FormattingEnabled = true;
            this.comboBox1.TabIndex = 0;
            this.textBox7.Name = "textBox7";
            this.textBox7.TabIndex = 1;
            this.textBox6.Name = "textBox6";
            this.textBox6.PasswordChar = '*';
            this.textBox6.TabIndex = 2;
            this.label8.Text = "Net Service Name:";
            this.label10.Text = "User Name:";
            this.label9.Text = "Password:";
            //
            // 按鈕
            //
            this.oracle_add_edit_test_connection.Name = "oracle_add_edit_test_connection";
            this.oracle_add_edit_test_connection.Text = "Test Connection";
            this.oracle_add_edit_test_connection.TabIndex = 3;
            this.oracle_add_edit_test_connection.UseVisualStyleBackColor = true;
            this.oracle_add_edit_test_connection.Click += new System.EventHandler(this.button1_Click);
            this.oracle_add_edit_security.Name = "oracle_add_edit_security";
            this.oracle_add_edit_security.Text = "SSL / SSH...";
            this.oracle_add_edit_security.AutoSize = true;
            this.oracle_add_edit_security.Click += new System.EventHandler(this.securityButton_Click);
            this.oracle_add_edit_ok.Name = "oracle_add_edit_ok";
            this.oracle_add_edit_ok.Text = "OK";
            this.oracle_add_edit_ok.TabIndex = 4;
            this.oracle_add_edit_ok.UseVisualStyleBackColor = true;
            this.oracle_add_edit_ok.Click += new System.EventHandler(this.button2_Click);
            this.oracle_add_edit_cancel.Name = "oracle_add_edit_cancel";
            this.oracle_add_edit_cancel.Text = "Cancel";
            this.oracle_add_edit_cancel.TabIndex = 5;
            this.oracle_add_edit_cancel.UseVisualStyleBackColor = true;
            this.oracle_add_edit_cancel.Click += new System.EventHandler(this.button3_Click);
            //
            // Basic 面板：內部用與外層同寬的標籤欄，兩種模式欄位才會對齊
            //
            System.Windows.Forms.TableLayoutPanel basicFields = new System.Windows.Forms.TableLayoutPanel();
            basicFields.AutoSize = true;
            basicFields.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            basicFields.ColumnCount = 2;
            basicFields.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, ConnectionDialogUi.LabelColumnWidth));
            basicFields.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.AutoSize));
            basicFields.Location = new System.Drawing.Point(0, 0);
            AddPanelField(basicFields, this.label3, this.textBox1, ConnectionDialogUi.FieldWide);
            AddPanelField(basicFields, this.label4, this.textBox2, ConnectionDialogUi.FieldNarrow);

            System.Windows.Forms.FlowLayoutPanel identifierChoices = new System.Windows.Forms.FlowLayoutPanel();
            identifierChoices.AutoSize = true;
            identifierChoices.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            identifierChoices.WrapContents = false;
            this.radioButton1.Margin = new System.Windows.Forms.Padding(0, 0, 16, 0);
            this.radioButton2.Margin = new System.Windows.Forms.Padding(0);
            identifierChoices.Controls.Add(this.radioButton1);
            identifierChoices.Controls.Add(this.radioButton2);
            AddPanelField(basicFields, this.label5, identifierChoices, 0);
            AddPanelControl(basicFields, this.textBox3, ConnectionDialogUi.FieldMedium);
            AddPanelField(basicFields, this.label6, this.textBox4, ConnectionDialogUi.FieldMedium);
            AddPanelField(basicFields, this.label7, this.textBox5, ConnectionDialogUi.FieldMedium);

            this.oracle_panel_basic.Name = "oracle_panel_basic";
            this.oracle_panel_basic.AutoSize = true;
            this.oracle_panel_basic.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            this.oracle_panel_basic.Controls.Add(basicFields);
            //
            // TNS 面板
            //
            System.Windows.Forms.TableLayoutPanel tnsFields = new System.Windows.Forms.TableLayoutPanel();
            tnsFields.AutoSize = true;
            tnsFields.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            tnsFields.ColumnCount = 2;
            tnsFields.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, ConnectionDialogUi.LabelColumnWidth));
            tnsFields.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.AutoSize));
            tnsFields.Location = new System.Drawing.Point(0, 0);
            AddPanelField(tnsFields, this.label8, this.comboBox1, ConnectionDialogUi.FieldMedium);
            AddPanelField(tnsFields, this.label10, this.textBox7, ConnectionDialogUi.FieldMedium);
            AddPanelField(tnsFields, this.label9, this.textBox6, ConnectionDialogUi.FieldMedium);

            this.oracle_panel_tns.Name = "oracle_panel_tns";
            this.oracle_panel_tns.AutoSize = true;
            this.oracle_panel_tns.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            this.oracle_panel_tns.Controls.Add(tnsFields);
            this.oracle_panel_tns.Visible = false;
            //
            // 兩個面板疊在同一個位置，依連線類型切換 Visible
            //
            System.Windows.Forms.Panel panelHost = new System.Windows.Forms.Panel();
            panelHost.AutoSize = true;
            panelHost.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            panelHost.Controls.Add(this.oracle_panel_basic);
            panelHost.Controls.Add(this.oracle_panel_tns);
            this.oracle_panel_basic.Location = new System.Drawing.Point(0, 0);
            this.oracle_panel_tns.Location = new System.Drawing.Point(0, 0);
            //
            // oracle_add_edit
            //
            ConnectionDialogUi.Shell shell = ConnectionDialogUi.Build(this, "Oracle", ConnectionDialogUi.OracleColor);
            ConnectionDialogUi.AddField(shell, this.label1, this.oracle_connection_name, ConnectionDialogUi.FieldWide);
            ConnectionDialogUi.AddField(shell, this.label2, this.oracle_connection_type, ConnectionDialogUi.FieldMedium);
            ConnectionDialogUi.AddSpanRow(shell, panelHost);
            ConnectionDialogUi.AddFieldOnly(shell, this.oracle_add_edit_security);
            ConnectionDialogUi.Finish(this, shell, this.oracle_add_edit_test_connection, this.oracle_add_edit_ok, this.oracle_add_edit_cancel);
            this.Icon = mySQLPunk.lib.AppIconService.AppIcon; // 看板娘 Punky，取代 resx 裡的舊圖示
            this.Name = "oracle_add_edit";
            this.Text = "Oracle";
            this.Load += new System.EventHandler(this.oracle_add_edit_Load);
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        private static void AddPanelField(System.Windows.Forms.TableLayoutPanel table, System.Windows.Forms.Label label, System.Windows.Forms.Control field, int width)
        {
            label.AutoSize = true;
            label.Anchor = System.Windows.Forms.AnchorStyles.Left;
            label.Margin = new System.Windows.Forms.Padding(0, 0, 12, 0);
            System.Windows.Forms.Control cell = ConnectionDialogUi.WrapInput(field);
            ConnectionDialogUi.StyleField(cell, width);
            int row = table.RowCount;
            table.RowCount = row + 1;
            table.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
            table.Controls.Add(label, 0, row);
            table.Controls.Add(cell, 1, row);
        }

        private static void AddPanelControl(System.Windows.Forms.TableLayoutPanel table, System.Windows.Forms.Control field, int width)
        {
            System.Windows.Forms.Control cell = ConnectionDialogUi.WrapInput(field);
            ConnectionDialogUi.StyleField(cell, width);
            int row = table.RowCount;
            table.RowCount = row + 1;
            table.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
            table.Controls.Add(cell, 1, row);
        }

        #endregion

        private System.Windows.Forms.Panel oracle_panel_tns;
        private System.Windows.Forms.TextBox textBox6;
        private System.Windows.Forms.TextBox textBox7;
        private System.Windows.Forms.Label label9;
        private System.Windows.Forms.Label label10;
        private System.Windows.Forms.ComboBox comboBox1;
        private System.Windows.Forms.Label label8;
        public System.Windows.Forms.ComboBox oracle_connection_type;
        private System.Windows.Forms.TextBox oracle_connection_name;
        private System.Windows.Forms.Label label2;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.Button oracle_add_edit_test_connection;
        private System.Windows.Forms.Button oracle_add_edit_security;
        private System.Windows.Forms.Button oracle_add_edit_ok;
        private System.Windows.Forms.Button oracle_add_edit_cancel;
        private System.Windows.Forms.Panel oracle_panel_basic;
        private System.Windows.Forms.TextBox textBox5;
        private System.Windows.Forms.TextBox textBox4;
        private System.Windows.Forms.Label label7;
        private System.Windows.Forms.Label label6;
        private System.Windows.Forms.RadioButton radioButton2;
        private System.Windows.Forms.RadioButton radioButton1;
        private System.Windows.Forms.TextBox textBox3;
        private System.Windows.Forms.Label label5;
        private System.Windows.Forms.TextBox textBox2;
        private System.Windows.Forms.Label label4;
        private System.Windows.Forms.TextBox textBox1;
        private System.Windows.Forms.Label label3;
    }
}
