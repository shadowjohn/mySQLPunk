namespace my_navicat
{
    partial class Form1
    {
        /// <summary>
        /// 設計工具所需的變數。
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// 清除任何使用中的資源。
        /// </summary>
        /// <param name="disposing">如果應該處置 Managed 資源則為 true，否則為 false。</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form 設計工具產生的程式碼

        /// <summary>
        /// 此為設計工具支援所需的方法 - 請勿使用程式碼編輯器修改
        /// 這個方法的內容。
        /// </summary>
        public void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(Form1));
            this.menuStrip1 = new System.Windows.Forms.MenuStrip();
            this.檔案ToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.newConnectionToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.openConnectionToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.closeConnectionToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.toolStripMenuItem2 = new System.Windows.Forms.ToolStripMenuItem();
            this.exportToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.importConnectionsToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.toolStripMenuItem3 = new System.Windows.Forms.ToolStripMenuItem();
            this.closeToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.exitToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.ttToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.helpToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.aboutToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.splitContainer1 = new System.Windows.Forms.SplitContainer();
            this.tool_Connection = new System.Windows.Forms.ToolStrip();
            this.toolStripSeparator1 = new System.Windows.Forms.ToolStripSeparator();
            this.splitContainer2 = new System.Windows.Forms.SplitContainer();
            this.splitContainer4 = new System.Windows.Forms.SplitContainer();
            this.label1 = new System.Windows.Forms.Label();
            this.mTs = new System.Windows.Forms.ToolStrip();
            this.splitContainer3 = new System.Windows.Forms.SplitContainer();
            this.db_tree = new System.Windows.Forms.TreeView();
            this.splitContainer5 = new System.Windows.Forms.SplitContainer();
            this.table_top = new System.Windows.Forms.DataGridView();
            this.statusStrip1 = new System.Windows.Forms.StatusStrip();
            this.connection_btn = new System.Windows.Forms.ToolStripDropDownButton();
            this.mysqlStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.postgreSQLToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.oracleToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.sQLiteToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.sQLServerToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.user_btn = new System.Windows.Forms.ToolStripButton();
            this.table_btn = new System.Windows.Forms.ToolStripButton();
            this.view_btn = new System.Windows.Forms.ToolStripButton();
            this.OpenTable = new System.Windows.Forms.ToolStripButton();
            this.DesignTable = new System.Windows.Forms.ToolStripButton();
            this.NewTable = new System.Windows.Forms.ToolStripButton();
            this.DeleteTable = new System.Windows.Forms.ToolStripButton();
            this.ImportWizard = new System.Windows.Forms.ToolStripButton();
            this.ExportWizard = new System.Windows.Forms.ToolStripButton();
            this.OpenView = new System.Windows.Forms.ToolStripButton();
            this.DesignView = new System.Windows.Forms.ToolStripButton();
            this.NewView = new System.Windows.Forms.ToolStripButton();
            this.DeleteView = new System.Windows.Forms.ToolStripButton();
            this.View_ExportWizard = new System.Windows.Forms.ToolStripButton();
            this.DesignFunction = new System.Windows.Forms.ToolStripButton();
            this.NewFunction = new System.Windows.Forms.ToolStripButton();
            this.DeleteFunction = new System.Windows.Forms.ToolStripButton();
            this.ExecuteFunction = new System.Windows.Forms.ToolStripButton();
            this.menuStrip1.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer1)).BeginInit();
            this.splitContainer1.Panel1.SuspendLayout();
            this.splitContainer1.Panel2.SuspendLayout();
            this.splitContainer1.SuspendLayout();
            this.tool_Connection.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer2)).BeginInit();
            this.splitContainer2.Panel1.SuspendLayout();
            this.splitContainer2.Panel2.SuspendLayout();
            this.splitContainer2.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer4)).BeginInit();
            this.splitContainer4.Panel1.SuspendLayout();
            this.splitContainer4.Panel2.SuspendLayout();
            this.splitContainer4.SuspendLayout();
            this.mTs.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer3)).BeginInit();
            this.splitContainer3.Panel1.SuspendLayout();
            this.splitContainer3.Panel2.SuspendLayout();
            this.splitContainer3.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer5)).BeginInit();
            this.splitContainer5.Panel1.SuspendLayout();
            this.splitContainer5.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.table_top)).BeginInit();
            this.SuspendLayout();
            // 
            // menuStrip1
            // 
            this.menuStrip1.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.檔案ToolStripMenuItem,
            this.ttToolStripMenuItem,
            this.helpToolStripMenuItem});
            this.menuStrip1.Location = new System.Drawing.Point(0, 0);
            this.menuStrip1.Name = "menuStrip1";
            this.menuStrip1.Size = new System.Drawing.Size(1232, 24);
            this.menuStrip1.TabIndex = 0;
            this.menuStrip1.Text = "menuStrip1";
            this.menuStrip1.ItemClicked += new System.Windows.Forms.ToolStripItemClickedEventHandler(this.menuStrip1_ItemClicked);
            // 
            // 檔案ToolStripMenuItem
            // 
            this.檔案ToolStripMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.newConnectionToolStripMenuItem,
            this.openConnectionToolStripMenuItem,
            this.closeConnectionToolStripMenuItem,
            this.toolStripMenuItem2,
            this.exportToolStripMenuItem,
            this.importConnectionsToolStripMenuItem,
            this.toolStripMenuItem3,
            this.closeToolStripMenuItem,
            this.exitToolStripMenuItem});
            this.檔案ToolStripMenuItem.Name = "檔案ToolStripMenuItem";
            this.檔案ToolStripMenuItem.Size = new System.Drawing.Size(38, 20);
            this.檔案ToolStripMenuItem.Text = "File";
            // 
            // newConnectionToolStripMenuItem
            // 
            this.newConnectionToolStripMenuItem.Name = "newConnectionToolStripMenuItem";
            this.newConnectionToolStripMenuItem.Size = new System.Drawing.Size(189, 22);
            this.newConnectionToolStripMenuItem.Text = "New Connection";
            // 
            // openConnectionToolStripMenuItem
            // 
            this.openConnectionToolStripMenuItem.Name = "openConnectionToolStripMenuItem";
            this.openConnectionToolStripMenuItem.Size = new System.Drawing.Size(189, 22);
            this.openConnectionToolStripMenuItem.Text = "Open Connection";
            // 
            // closeConnectionToolStripMenuItem
            // 
            this.closeConnectionToolStripMenuItem.Name = "closeConnectionToolStripMenuItem";
            this.closeConnectionToolStripMenuItem.Size = new System.Drawing.Size(189, 22);
            this.closeConnectionToolStripMenuItem.Text = "Close Connection";
            // 
            // toolStripMenuItem2
            // 
            this.toolStripMenuItem2.Enabled = false;
            this.toolStripMenuItem2.Name = "toolStripMenuItem2";
            this.toolStripMenuItem2.Size = new System.Drawing.Size(189, 22);
            this.toolStripMenuItem2.Text = "-----------------------";
            this.toolStripMenuItem2.Click += new System.EventHandler(this.toolStripMenuItem2_Click);
            // 
            // exportToolStripMenuItem
            // 
            this.exportToolStripMenuItem.Name = "exportToolStripMenuItem";
            this.exportToolStripMenuItem.Size = new System.Drawing.Size(189, 22);
            this.exportToolStripMenuItem.Text = "Export Connections";
            // 
            // importConnectionsToolStripMenuItem
            // 
            this.importConnectionsToolStripMenuItem.Name = "importConnectionsToolStripMenuItem";
            this.importConnectionsToolStripMenuItem.Size = new System.Drawing.Size(189, 22);
            this.importConnectionsToolStripMenuItem.Text = "Import Connections";
            // 
            // toolStripMenuItem3
            // 
            this.toolStripMenuItem3.Enabled = false;
            this.toolStripMenuItem3.Name = "toolStripMenuItem3";
            this.toolStripMenuItem3.Size = new System.Drawing.Size(189, 22);
            this.toolStripMenuItem3.Text = "-----------------------";
            // 
            // closeToolStripMenuItem
            // 
            this.closeToolStripMenuItem.Name = "closeToolStripMenuItem";
            this.closeToolStripMenuItem.Size = new System.Drawing.Size(189, 22);
            this.closeToolStripMenuItem.Text = "Close";
            // 
            // exitToolStripMenuItem
            // 
            this.exitToolStripMenuItem.Name = "exitToolStripMenuItem";
            this.exitToolStripMenuItem.Size = new System.Drawing.Size(189, 22);
            this.exitToolStripMenuItem.Text = "Exit";
            this.exitToolStripMenuItem.Click += new System.EventHandler(this.exitToolStripMenuItem_Click);
            // 
            // ttToolStripMenuItem
            // 
            this.ttToolStripMenuItem.Name = "ttToolStripMenuItem";
            this.ttToolStripMenuItem.Size = new System.Drawing.Size(66, 20);
            this.ttToolStripMenuItem.Text = "Window";
            // 
            // helpToolStripMenuItem
            // 
            this.helpToolStripMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.aboutToolStripMenuItem});
            this.helpToolStripMenuItem.Name = "helpToolStripMenuItem";
            this.helpToolStripMenuItem.Size = new System.Drawing.Size(46, 20);
            this.helpToolStripMenuItem.Text = "Help";
            // 
            // aboutToolStripMenuItem
            // 
            this.aboutToolStripMenuItem.Name = "aboutToolStripMenuItem";
            this.aboutToolStripMenuItem.Size = new System.Drawing.Size(109, 22);
            this.aboutToolStripMenuItem.Text = "About";
            this.aboutToolStripMenuItem.Click += new System.EventHandler(this.aboutToolStripMenuItem_Click);
            // 
            // splitContainer1
            // 
            this.splitContainer1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.splitContainer1.FixedPanel = System.Windows.Forms.FixedPanel.Panel1;
            this.splitContainer1.ImeMode = System.Windows.Forms.ImeMode.Off;
            this.splitContainer1.IsSplitterFixed = true;
            this.splitContainer1.Location = new System.Drawing.Point(0, 24);
            this.splitContainer1.Name = "splitContainer1";
            this.splitContainer1.Orientation = System.Windows.Forms.Orientation.Horizontal;
            // 
            // splitContainer1.Panel1
            // 
            this.splitContainer1.Panel1.Controls.Add(this.tool_Connection);
            // 
            // splitContainer1.Panel2
            // 
            this.splitContainer1.Panel2.Controls.Add(this.splitContainer2);
            this.splitContainer1.Size = new System.Drawing.Size(1232, 675);
            this.splitContainer1.SplitterDistance = 80;
            this.splitContainer1.TabIndex = 1;
            // 
            // tool_Connection
            // 
            this.tool_Connection.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.connection_btn,
            this.user_btn,
            this.toolStripSeparator1,
            this.table_btn,
            this.view_btn});
            this.tool_Connection.Location = new System.Drawing.Point(0, 0);
            this.tool_Connection.Name = "tool_Connection";
            this.tool_Connection.ShowItemToolTips = false;
            this.tool_Connection.Size = new System.Drawing.Size(1232, 73);
            this.tool_Connection.TabIndex = 0;
            this.tool_Connection.Text = "Table";
            this.tool_Connection.ItemClicked += new System.Windows.Forms.ToolStripItemClickedEventHandler(this.tool_Connection_ItemClicked);
            // 
            // toolStripSeparator1
            // 
            this.toolStripSeparator1.Name = "toolStripSeparator1";
            this.toolStripSeparator1.Size = new System.Drawing.Size(6, 73);
            // 
            // splitContainer2
            // 
            this.splitContainer2.Dock = System.Windows.Forms.DockStyle.Fill;
            this.splitContainer2.IsSplitterFixed = true;
            this.splitContainer2.Location = new System.Drawing.Point(0, 0);
            this.splitContainer2.Name = "splitContainer2";
            this.splitContainer2.Orientation = System.Windows.Forms.Orientation.Horizontal;
            // 
            // splitContainer2.Panel1
            // 
            this.splitContainer2.Panel1.Controls.Add(this.splitContainer4);
            // 
            // splitContainer2.Panel2
            // 
            this.splitContainer2.Panel2.Controls.Add(this.splitContainer3);
            this.splitContainer2.Size = new System.Drawing.Size(1232, 591);
            this.splitContainer2.SplitterDistance = 25;
            this.splitContainer2.TabIndex = 0;
            // 
            // splitContainer4
            // 
            this.splitContainer4.Dock = System.Windows.Forms.DockStyle.Fill;
            this.splitContainer4.Location = new System.Drawing.Point(0, 0);
            this.splitContainer4.Name = "splitContainer4";
            // 
            // splitContainer4.Panel1
            // 
            this.splitContainer4.Panel1.Controls.Add(this.label1);
            // 
            // splitContainer4.Panel2
            // 
            this.splitContainer4.Panel2.Controls.Add(this.mTs);
            this.splitContainer4.Size = new System.Drawing.Size(1232, 25);
            this.splitContainer4.SplitterDistance = 268;
            this.splitContainer4.TabIndex = 0;
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(12, 7);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(63, 12);
            this.label1.TabIndex = 1;
            this.label1.Text = "Connections";
            // 
            // mTs
            // 
            this.mTs.AllowMerge = false;
            this.mTs.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.OpenTable,
            this.DesignTable,
            this.NewTable,
            this.DeleteTable,
            this.ImportWizard,
            this.ExportWizard,
            this.OpenView,
            this.DesignView,
            this.NewView,
            this.DeleteView,
            this.View_ExportWizard,
            this.DesignFunction,
            this.NewFunction,
            this.DeleteFunction,
            this.ExecuteFunction});
            this.mTs.Location = new System.Drawing.Point(0, 0);
            this.mTs.Name = "mTs";
            this.mTs.Size = new System.Drawing.Size(960, 25);
            this.mTs.TabIndex = 0;
            this.mTs.Text = "toolStrip1";
            // 
            // splitContainer3
            // 
            this.splitContainer3.Dock = System.Windows.Forms.DockStyle.Fill;
            this.splitContainer3.Location = new System.Drawing.Point(0, 0);
            this.splitContainer3.Name = "splitContainer3";
            // 
            // splitContainer3.Panel1
            // 
            this.splitContainer3.Panel1.Controls.Add(this.db_tree);
            this.splitContainer3.Panel1.Resize += new System.EventHandler(this.splitContainer3_Panel1_Resize);
            // 
            // splitContainer3.Panel2
            // 
            this.splitContainer3.Panel2.Controls.Add(this.splitContainer5);
            this.splitContainer3.Size = new System.Drawing.Size(1232, 562);
            this.splitContainer3.SplitterDistance = 267;
            this.splitContainer3.TabIndex = 0;
            // 
            // db_tree
            // 
            this.db_tree.BorderStyle = System.Windows.Forms.BorderStyle.None;
            this.db_tree.Location = new System.Drawing.Point(0, 0);
            this.db_tree.Name = "db_tree";
            this.db_tree.Size = new System.Drawing.Size(121, 97);
            this.db_tree.TabIndex = 0;
            this.db_tree.AfterSelect += new System.Windows.Forms.TreeViewEventHandler(this.db_tree_AfterSelect);
            this.db_tree.DoubleClick += new System.EventHandler(this.db_tree_DoubleClick);
            // 
            // splitContainer5
            // 
            this.splitContainer5.Dock = System.Windows.Forms.DockStyle.Fill;
            this.splitContainer5.Location = new System.Drawing.Point(0, 0);
            this.splitContainer5.Name = "splitContainer5";
            this.splitContainer5.Orientation = System.Windows.Forms.Orientation.Horizontal;
            // 
            // splitContainer5.Panel1
            // 
            this.splitContainer5.Panel1.Controls.Add(this.table_top);
            this.splitContainer5.Panel1.Resize += new System.EventHandler(this.splitContainer5_Panel1_Resize);
            this.splitContainer5.Size = new System.Drawing.Size(961, 562);
            this.splitContainer5.SplitterDistance = 243;
            this.splitContainer5.TabIndex = 0;
            // 
            // table_top
            // 
            this.table_top.AutoSizeColumnsMode = System.Windows.Forms.DataGridViewAutoSizeColumnsMode.ColumnHeader;
            this.table_top.AutoSizeRowsMode = System.Windows.Forms.DataGridViewAutoSizeRowsMode.AllHeaders;
            this.table_top.BackgroundColor = System.Drawing.SystemColors.ControlLightLight;
            this.table_top.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.table_top.Location = new System.Drawing.Point(0, 0);
            this.table_top.Margin = new System.Windows.Forms.Padding(0);
            this.table_top.Name = "table_top";
            this.table_top.RowHeadersWidthSizeMode = System.Windows.Forms.DataGridViewRowHeadersWidthSizeMode.AutoSizeToAllHeaders;
            this.table_top.RowTemplate.Height = 24;
            this.table_top.Size = new System.Drawing.Size(240, 150);
            this.table_top.TabIndex = 0;
            // 
            // statusStrip1
            // 
            this.statusStrip1.Location = new System.Drawing.Point(0, 677);
            this.statusStrip1.Name = "statusStrip1";
            this.statusStrip1.Size = new System.Drawing.Size(1232, 22);
            this.statusStrip1.TabIndex = 2;
            this.statusStrip1.Text = "statusStrip1";
            // 
            // connection_btn
            // 
            this.connection_btn.AutoSize = false;
            this.connection_btn.AutoToolTip = false;
            this.connection_btn.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.mysqlStripMenuItem,
            this.postgreSQLToolStripMenuItem,
            this.oracleToolStripMenuItem,
            this.sQLiteToolStripMenuItem,
            this.sQLServerToolStripMenuItem});
            this.connection_btn.Image = global::my_navicat.Properties.Resources.database;
            this.connection_btn.ImageScaling = System.Windows.Forms.ToolStripItemImageScaling.None;
            this.connection_btn.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.connection_btn.Name = "connection_btn";
            this.connection_btn.Size = new System.Drawing.Size(80, 70);
            this.connection_btn.Text = "Connection";
            this.connection_btn.TextAlign = System.Drawing.ContentAlignment.BottomCenter;
            this.connection_btn.TextImageRelation = System.Windows.Forms.TextImageRelation.Overlay;
            // 
            // mysqlStripMenuItem
            // 
            this.mysqlStripMenuItem.Name = "mysqlStripMenuItem";
            this.mysqlStripMenuItem.Size = new System.Drawing.Size(180, 22);
            this.mysqlStripMenuItem.Text = "MySQL";
            this.mysqlStripMenuItem.Click += new System.EventHandler(this.mysqlStripMenuItem_Click);
            // 
            // postgreSQLToolStripMenuItem
            // 
            this.postgreSQLToolStripMenuItem.Name = "postgreSQLToolStripMenuItem";
            this.postgreSQLToolStripMenuItem.Size = new System.Drawing.Size(180, 22);
            this.postgreSQLToolStripMenuItem.Text = "PostgreSQL";
            this.postgreSQLToolStripMenuItem.Click += new System.EventHandler(this.postgreSQLToolStripMenuItem_Click);
            // 
            // oracleToolStripMenuItem
            // 
            this.oracleToolStripMenuItem.Name = "oracleToolStripMenuItem";
            this.oracleToolStripMenuItem.Size = new System.Drawing.Size(180, 22);
            this.oracleToolStripMenuItem.Text = "Oracle";
            this.oracleToolStripMenuItem.Click += new System.EventHandler(this.oracleToolStripMenuItem_Click);
            // 
            // sQLiteToolStripMenuItem
            // 
            this.sQLiteToolStripMenuItem.Name = "sQLiteToolStripMenuItem";
            this.sQLiteToolStripMenuItem.Size = new System.Drawing.Size(180, 22);
            this.sQLiteToolStripMenuItem.Text = "SQLite";
            // 
            // sQLServerToolStripMenuItem
            // 
            this.sQLServerToolStripMenuItem.Name = "sQLServerToolStripMenuItem";
            this.sQLServerToolStripMenuItem.Size = new System.Drawing.Size(180, 22);
            this.sQLServerToolStripMenuItem.Text = "SQL Server";
            // 
            // user_btn
            // 
            this.user_btn.AutoSize = false;
            this.user_btn.AutoToolTip = false;
            this.user_btn.Image = global::my_navicat.Properties.Resources.user;
            this.user_btn.ImageScaling = System.Windows.Forms.ToolStripItemImageScaling.None;
            this.user_btn.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.user_btn.Name = "user_btn";
            this.user_btn.Size = new System.Drawing.Size(80, 70);
            this.user_btn.Text = "User";
            this.user_btn.TextAlign = System.Drawing.ContentAlignment.BottomCenter;
            this.user_btn.TextImageRelation = System.Windows.Forms.TextImageRelation.Overlay;
            this.user_btn.Click += new System.EventHandler(this.user_btn_Click);
            // 
            // table_btn
            // 
            this.table_btn.AutoSize = false;
            this.table_btn.AutoToolTip = false;
            this.table_btn.BackgroundImageLayout = System.Windows.Forms.ImageLayout.Stretch;
            this.table_btn.Checked = true;
            this.table_btn.CheckState = System.Windows.Forms.CheckState.Checked;
            this.table_btn.Image = global::my_navicat.Properties.Resources.tables_32;
            this.table_btn.ImageScaling = System.Windows.Forms.ToolStripItemImageScaling.None;
            this.table_btn.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.table_btn.Name = "table_btn";
            this.table_btn.Size = new System.Drawing.Size(80, 70);
            this.table_btn.Text = "Table";
            this.table_btn.TextAlign = System.Drawing.ContentAlignment.BottomCenter;
            this.table_btn.TextImageRelation = System.Windows.Forms.TextImageRelation.Overlay;
            this.table_btn.Click += new System.EventHandler(this.table_btn_Click);
            // 
            // view_btn
            // 
            this.view_btn.AutoSize = false;
            this.view_btn.AutoToolTip = false;
            this.view_btn.Image = global::my_navicat.Properties.Resources.views_32;
            this.view_btn.ImageScaling = System.Windows.Forms.ToolStripItemImageScaling.None;
            this.view_btn.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.view_btn.Name = "view_btn";
            this.view_btn.Size = new System.Drawing.Size(80, 70);
            this.view_btn.Text = "View";
            this.view_btn.TextAlign = System.Drawing.ContentAlignment.BottomCenter;
            this.view_btn.TextImageRelation = System.Windows.Forms.TextImageRelation.Overlay;
            this.view_btn.Click += new System.EventHandler(this.view_btn_Click);
            // 
            // OpenTable
            // 
            this.OpenTable.AutoToolTip = false;
            this.OpenTable.Image = ((System.Drawing.Image)(resources.GetObject("OpenTable.Image")));
            this.OpenTable.ImageScaling = System.Windows.Forms.ToolStripItemImageScaling.None;
            this.OpenTable.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.OpenTable.Name = "OpenTable";
            this.OpenTable.Size = new System.Drawing.Size(94, 22);
            this.OpenTable.Text = "Open Table";
            // 
            // DesignTable
            // 
            this.DesignTable.AutoToolTip = false;
            this.DesignTable.Image = ((System.Drawing.Image)(resources.GetObject("DesignTable.Image")));
            this.DesignTable.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.DesignTable.Name = "DesignTable";
            this.DesignTable.Size = new System.Drawing.Size(101, 22);
            this.DesignTable.Text = "Design Table";
            // 
            // NewTable
            // 
            this.NewTable.AutoToolTip = false;
            this.NewTable.Image = ((System.Drawing.Image)(resources.GetObject("NewTable.Image")));
            this.NewTable.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.NewTable.Name = "NewTable";
            this.NewTable.Size = new System.Drawing.Size(88, 22);
            this.NewTable.Text = "New Table";
            // 
            // DeleteTable
            // 
            this.DeleteTable.AutoToolTip = false;
            this.DeleteTable.Image = ((System.Drawing.Image)(resources.GetObject("DeleteTable.Image")));
            this.DeleteTable.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.DeleteTable.Name = "DeleteTable";
            this.DeleteTable.Size = new System.Drawing.Size(99, 22);
            this.DeleteTable.Text = "Delete Table";
            // 
            // ImportWizard
            // 
            this.ImportWizard.AutoToolTip = false;
            this.ImportWizard.Image = ((System.Drawing.Image)(resources.GetObject("ImportWizard.Image")));
            this.ImportWizard.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.ImportWizard.Name = "ImportWizard";
            this.ImportWizard.Size = new System.Drawing.Size(108, 22);
            this.ImportWizard.Text = "Import Wizard";
            this.ImportWizard.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            // 
            // ExportWizard
            // 
            this.ExportWizard.AutoToolTip = false;
            this.ExportWizard.Image = ((System.Drawing.Image)(resources.GetObject("ExportWizard.Image")));
            this.ExportWizard.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.ExportWizard.Name = "ExportWizard";
            this.ExportWizard.Size = new System.Drawing.Size(107, 22);
            this.ExportWizard.Text = "Export Wizard";
            // 
            // OpenView
            // 
            this.OpenView.AutoToolTip = false;
            this.OpenView.Image = ((System.Drawing.Image)(resources.GetObject("OpenView.Image")));
            this.OpenView.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.OpenView.Name = "OpenView";
            this.OpenView.Size = new System.Drawing.Size(89, 22);
            this.OpenView.Text = "Open View";
            // 
            // DesignView
            // 
            this.DesignView.Image = ((System.Drawing.Image)(resources.GetObject("DesignView.Image")));
            this.DesignView.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.DesignView.Name = "DesignView";
            this.DesignView.Size = new System.Drawing.Size(96, 22);
            this.DesignView.Text = "Design View";
            // 
            // NewView
            // 
            this.NewView.AutoToolTip = false;
            this.NewView.Image = ((System.Drawing.Image)(resources.GetObject("NewView.Image")));
            this.NewView.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.NewView.Name = "NewView";
            this.NewView.Size = new System.Drawing.Size(83, 22);
            this.NewView.Text = "New View";
            // 
            // DeleteView
            // 
            this.DeleteView.AutoToolTip = false;
            this.DeleteView.Image = ((System.Drawing.Image)(resources.GetObject("DeleteView.Image")));
            this.DeleteView.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.DeleteView.Name = "DeleteView";
            this.DeleteView.Size = new System.Drawing.Size(94, 20);
            this.DeleteView.Text = "Delete View";
            // 
            // View_ExportWizard
            // 
            this.View_ExportWizard.AutoToolTip = false;
            this.View_ExportWizard.Image = ((System.Drawing.Image)(resources.GetObject("View_ExportWizard.Image")));
            this.View_ExportWizard.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.View_ExportWizard.Name = "View_ExportWizard";
            this.View_ExportWizard.Size = new System.Drawing.Size(107, 20);
            this.View_ExportWizard.Text = "Export Wizard";
            // 
            // DesignFunction
            // 
            this.DesignFunction.AutoToolTip = false;
            this.DesignFunction.Image = ((System.Drawing.Image)(resources.GetObject("DesignFunction.Image")));
            this.DesignFunction.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.DesignFunction.Name = "DesignFunction";
            this.DesignFunction.Size = new System.Drawing.Size(117, 20);
            this.DesignFunction.Text = "Design Function";
            // 
            // NewFunction
            // 
            this.NewFunction.AutoToolTip = false;
            this.NewFunction.Image = ((System.Drawing.Image)(resources.GetObject("NewFunction.Image")));
            this.NewFunction.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.NewFunction.Name = "NewFunction";
            this.NewFunction.Size = new System.Drawing.Size(104, 20);
            this.NewFunction.Text = "New Function";
            // 
            // DeleteFunction
            // 
            this.DeleteFunction.AutoToolTip = false;
            this.DeleteFunction.Image = ((System.Drawing.Image)(resources.GetObject("DeleteFunction.Image")));
            this.DeleteFunction.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.DeleteFunction.Name = "DeleteFunction";
            this.DeleteFunction.Size = new System.Drawing.Size(115, 20);
            this.DeleteFunction.Text = "Delete Function";
            // 
            // ExecuteFunction
            // 
            this.ExecuteFunction.AutoToolTip = false;
            this.ExecuteFunction.Image = ((System.Drawing.Image)(resources.GetObject("ExecuteFunction.Image")));
            this.ExecuteFunction.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.ExecuteFunction.Name = "ExecuteFunction";
            this.ExecuteFunction.Size = new System.Drawing.Size(122, 20);
            this.ExecuteFunction.Text = "Execute Function";
            // 
            // Form1
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 12F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(1232, 699);
            this.Controls.Add(this.statusStrip1);
            this.Controls.Add(this.splitContainer1);
            this.Controls.Add(this.menuStrip1);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.MainMenuStrip = this.menuStrip1;
            this.Name = "Form1";
            this.Text = "羽山的 Navicat";
            this.Load += new System.EventHandler(this.Form1_Load);
            this.menuStrip1.ResumeLayout(false);
            this.menuStrip1.PerformLayout();
            this.splitContainer1.Panel1.ResumeLayout(false);
            this.splitContainer1.Panel1.PerformLayout();
            this.splitContainer1.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer1)).EndInit();
            this.splitContainer1.ResumeLayout(false);
            this.tool_Connection.ResumeLayout(false);
            this.tool_Connection.PerformLayout();
            this.splitContainer2.Panel1.ResumeLayout(false);
            this.splitContainer2.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer2)).EndInit();
            this.splitContainer2.ResumeLayout(false);
            this.splitContainer4.Panel1.ResumeLayout(false);
            this.splitContainer4.Panel1.PerformLayout();
            this.splitContainer4.Panel2.ResumeLayout(false);
            this.splitContainer4.Panel2.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer4)).EndInit();
            this.splitContainer4.ResumeLayout(false);
            this.mTs.ResumeLayout(false);
            this.mTs.PerformLayout();
            this.splitContainer3.Panel1.ResumeLayout(false);
            this.splitContainer3.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer3)).EndInit();
            this.splitContainer3.ResumeLayout(false);
            this.splitContainer5.Panel1.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer5)).EndInit();
            this.splitContainer5.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.table_top)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.MenuStrip menuStrip1;
        private System.Windows.Forms.ToolStripMenuItem 檔案ToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem ttToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem helpToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem aboutToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem newConnectionToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem openConnectionToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem closeConnectionToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem toolStripMenuItem2;
        private System.Windows.Forms.ToolStripMenuItem exportToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem importConnectionsToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem toolStripMenuItem3;
        private System.Windows.Forms.ToolStripMenuItem closeToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem exitToolStripMenuItem;
        private System.Windows.Forms.SplitContainer splitContainer1;
        private System.Windows.Forms.ToolStrip tool_Connection;
        private System.Windows.Forms.ToolStripDropDownButton connection_btn;
        private System.Windows.Forms.ToolStripMenuItem postgreSQLToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem oracleToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem sQLiteToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem sQLServerToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem mysqlStripMenuItem;
        private System.Windows.Forms.StatusStrip statusStrip1;
        private System.Windows.Forms.SplitContainer splitContainer2;
        private System.Windows.Forms.SplitContainer splitContainer3;
        private System.Windows.Forms.TreeView db_tree;
        private System.Windows.Forms.SplitContainer splitContainer4;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.ToolStrip mTs;
        private System.Windows.Forms.ToolStripButton OpenTable;
        private System.Windows.Forms.ToolStripButton DesignTable;
        private System.Windows.Forms.ToolStripButton NewTable;
        private System.Windows.Forms.ToolStripButton DeleteTable;
        private System.Windows.Forms.ToolStripButton ImportWizard;
        private System.Windows.Forms.ToolStripButton ExportWizard;
        private System.Windows.Forms.ToolStripButton OpenView;
        private System.Windows.Forms.ToolStripButton DesignView;
        private System.Windows.Forms.ToolStripButton NewView;
        private System.Windows.Forms.ToolStripButton DeleteView;
        private System.Windows.Forms.ToolStripButton View_ExportWizard;
        private System.Windows.Forms.ToolStripButton DesignFunction;
        private System.Windows.Forms.ToolStripButton NewFunction;
        private System.Windows.Forms.ToolStripButton DeleteFunction;
        private System.Windows.Forms.ToolStripButton ExecuteFunction;
        private System.Windows.Forms.SplitContainer splitContainer5;
        private System.Windows.Forms.DataGridView table_top;
        private System.Windows.Forms.ToolStripButton user_btn;
        private System.Windows.Forms.ToolStripSeparator toolStripSeparator1;
        private System.Windows.Forms.ToolStripButton table_btn;
        private System.Windows.Forms.ToolStripButton view_btn;
    }
}

