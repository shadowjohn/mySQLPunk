using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using mySQLPunk.entity;
using utility;
using System.Runtime.InteropServices;
//using MySql.Data.MySqlClient;
using mySQLPunk.lib;
using mySQLPunk.template;

namespace mySQLPunk
{

    public partial class Form1 : Form
    {
        public Form dialog = new Form();
        public Label dialogLabel = new Label();
        public int dialogFlag = 0;
        public string test = "";
        public static Dictionary<string, List<object>> displayTools = new Dictionary<string, List<object>>();
        mySQLPunk_main myN = new mySQLPunk_main();
        myinclude my = new myinclude();
        ToolStripButton query_btn = new ToolStripButton(); // 新增查詢按鈕
        public Form1()
        {
            InitializeComponent();
        }
        
        public static void ApplyModernTheme(Control parent)
        {
            Color baseWhite = Color.White;
            Color textGray = Color.FromArgb(51, 51, 51);
            Color borderGray = Color.FromArgb(224, 224, 224);
            Color professionalBlue = Color.FromArgb(0, 120, 212);

            parent.BackColor = baseWhite;
            parent.ForeColor = textGray;

            if (parent is Form f)
            {
                f.Opacity = 1.0f;
                f.KeyPreview = true;
                f.KeyDown += (s, e) => {
                    if (e.KeyCode == Keys.Escape) f.Close();
                };
            }

            foreach (Control c in parent.Controls)
            {
                if (c is MenuStrip || c is ToolStrip || c is StatusStrip)
                {
                    c.BackColor = Color.FromArgb(245, 245, 245);
                    c.ForeColor = textGray;
                    if (c is ToolStrip ts)
                    {
                        ts.Renderer = new ToolStripProfessionalRenderer(new ProfessionalColorTable());
                        ts.ImageScalingSize = new Size(24, 24);
                        ts.GripStyle = ToolStripGripStyle.Hidden;
                    }
                }
                else if (c is TreeView tv)
                {
                    tv.BackColor = baseWhite;
                    tv.ForeColor = textGray;
                    tv.LineColor = borderGray;
                    tv.BorderStyle = BorderStyle.None;
                    tv.FullRowSelect = true;
                    tv.ItemHeight = 24;
                }
                else if (c is DataGridView dgv)
                {
                    dgv.BackgroundColor = Color.FromArgb(240, 240, 240);
                    dgv.GridColor = borderGray;
                    dgv.BorderStyle = BorderStyle.None;
                    dgv.DefaultCellStyle.BackColor = baseWhite;
                    dgv.DefaultCellStyle.ForeColor = textGray;
                    dgv.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(245, 245, 245);
                    dgv.ColumnHeadersDefaultCellStyle.ForeColor = textGray;
                    dgv.EnableHeadersVisualStyles = true;
                }
                else if (c is SplitContainer sc)
                {
                    sc.BorderStyle = BorderStyle.None;
                    sc.SplitterWidth = 1;
                    ApplyModernTheme(sc.Panel1);
                    ApplyModernTheme(sc.Panel2);
                }
                else
                {
                    ApplyModernTheme(c);
                }
            }
        }

        public class CyberpunkColorTable : ProfessionalColorTable
        {
            public override Color ToolStripGradientBegin => Color.FromArgb(26, 26, 26);
            public override Color ToolStripGradientEnd => Color.FromArgb(26, 26, 26);
            public override Color ToolStripBorder => Color.FromArgb(0, 255, 204);
            public override Color MenuItemSelected => Color.FromArgb(0, 100, 100);
            public override Color MenuItemSelectedGradientBegin => Color.FromArgb(0, 100, 100);
            public override Color MenuItemSelectedGradientEnd => Color.FromArgb(0, 100, 100);
            public override Color MenuItemBorder => Color.FromArgb(0, 255, 204);
        }
        public void showTools(string kind)
        {
            switch (kind)
            {
                case "點到連線":
                case "點到未展開的資料庫":
                    {
                        foreach (var k in displayTools.Keys)
                        {
                            if (k == "tables")
                            {
                                foreach (var item in displayTools[k])
                                {
                                    ((ToolStripButton)item).Visible = true;
                                    ((ToolStripButton)item).Enabled = false;
                                }
                            }
                            else
                            {
                                foreach (var item in displayTools[k])
                                {
                                    ((ToolStripButton)item).Visible = false;
                                }
                            }
                        }
                    }
                    break;
                case "點到展開的資料庫":
                case "點到Tables":
                    {
                        foreach (var k in displayTools.Keys)
                        {
                            if (k == "tables")
                            {
                                foreach (var item in displayTools[k])
                                {
                                    ((ToolStripButton)item).Visible = true;
                                    ((ToolStripButton)item).Enabled = false;
                                }
                                ((ToolStripButton)displayTools[k][2]).Enabled = true; //New Table
                                ((ToolStripButton)displayTools[k][4]).Enabled = true; //Import Wizard
                                ((ToolStripButton)displayTools[k][5]).Enabled = true; //Export Wizard
                            }
                            else
                            {
                                foreach (var item in displayTools[k])
                                {
                                    ((ToolStripButton)item).Visible = false;
                                }
                            }
                        }
                    }
                    break;
                case "點到Table本身":
                    {
                        foreach (var k in displayTools.Keys)
                        {
                            if (k == "tables")
                            {
                                foreach (var item in displayTools[k])
                                {
                                    ((ToolStripButton)item).Visible = true;
                                    ((ToolStripButton)item).Enabled = true;
                                }
                            }
                            else
                            {
                                foreach (var item in displayTools[k])
                                {
                                    ((ToolStripButton)item).Visible = false;
                                }
                            }
                        }
                    }
                    break;
                case "點到Views本身":
                    {
                        foreach (var k in displayTools.Keys)
                        {
                            if (k == "views")
                            {
                                foreach (var item in displayTools[k])
                                {
                                    ((ToolStripButton)item).Visible = true;
                                    ((ToolStripButton)item).Enabled = false;
                                }
                                ((ToolStripButton)displayTools[k][2]).Enabled = true; //New View
                                ((ToolStripButton)displayTools[k][4]).Enabled = true; //View Export Wizard
                            }
                            else
                            {
                                foreach (var item in displayTools[k])
                                {
                                    ((ToolStripButton)item).Visible = false;
                                }
                            }
                        }
                    }
                    break;
                case "點到Functions本身":
                    {
                        foreach (var k in displayTools.Keys)
                        {
                            if (k == "functions")
                            {
                                foreach (var item in displayTools[k])
                                {
                                    ((ToolStripButton)item).Visible = true;
                                    ((ToolStripButton)item).Enabled = false;
                                }
                                ((ToolStripButton)displayTools[k][1]).Enabled = true; //New Function                                
                            }
                            else
                            {
                                foreach (var item in displayTools[k])
                                {
                                    ((ToolStripButton)item).Visible = false;
                                }
                            }
                        }
                    }
                    break;
            }
        }
        public void drawLists()
        {
            ImageList myImageList = new ImageList();
            string pwd = my.pwd();
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\mysql_close.png")); //0
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\mysql_open.png")); //1
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\postgresql_close.png")); //2
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\postgresql_open.png")); //3
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\oracle_close.png")); //4
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\oracle_open.png")); //5
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\sqlite_close.png")); //6
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\sqlite_open.png")); //7
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\sqlserver_close.png"));  //8
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\sqlserver_open.png"));  //9           
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\db_close.png"));  //10           
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\db_open.png"));  //11

            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\tables.png"));  //12
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\views.png"));  //13
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\functions.png"));  //14
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\events.png"));  //15
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\queries.png"));  //16
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\reports.png"));  //17
            myImageList.Images.Add(Image.FromFile(pwd + "\\image\\backups.png"));  //18

            // Assign the ImageList to the TreeView.

            db_tree.ShowRootLines = false;
            db_tree.ShowLines = true;

            db_tree.ShowPlusMinus = true;
            db_tree.Nodes.Clear();
            for (int i = 0, max_i = myN.connections.Count; i < max_i; i++)
            {
                //From : https://stackoverflow.com/questions/3415354/how-to-avoid-winforms-treeview-icon-changes-when-item-selected
                TreeNode newNode = new TreeNode(myN.connections[i]["name"].ToString(), i, i);
                switch (myN.connections[i]["kind"].ToString())
                {
                    case "mysql":
                        newNode.ImageIndex = (myN.connections[i]["isConnect"].ToString() == "F") ? 0 : 1;
                        newNode.SelectedImageIndex = (myN.connections[i]["isConnect"].ToString() == "F") ? 0 : 1;
                        break;
                    case "postgresql":
                        newNode.ImageIndex = (myN.connections[i]["isConnect"].ToString() == "F") ? 2 : 3;
                        newNode.SelectedImageIndex = (myN.connections[i]["isConnect"].ToString() == "F") ? 2 : 3;
                        break;
                    case "oracle":
                        newNode.ImageIndex = (myN.connections[i]["isConnect"].ToString() == "F") ? 4 : 5;
                        newNode.SelectedImageIndex = (myN.connections[i]["isConnect"].ToString() == "F") ? 4 : 5;
                        break;
                    case "sqlite":
                        newNode.ImageIndex = (myN.connections[i]["isConnect"].ToString() == "F") ? 6 : 7;
                        newNode.SelectedImageIndex = (myN.connections[i]["isConnect"].ToString() == "F") ? 6 : 7;
                        break;
                    case "sqlserver":
                        newNode.ImageIndex = (myN.connections[i]["isConnect"].ToString() == "F") ? 8 : 9;
                        newNode.SelectedImageIndex = (myN.connections[i]["isConnect"].ToString() == "F") ? 8 : 9;
                        break;
                }

                db_tree.Nodes.Add(newNode);
            }
            db_tree.ImageList = myImageList;
            SetWindowTheme(db_tree.Handle, "explorer", null);

        }
        private void thirty_two_change(string kind)
        {
            List<object> btns = new List<object>();
            btns.Add(table_btn);
            btns.Add(view_btn);
            for (int i = 0, max_i = btns.Count; i < max_i; i++)
            {
                ((ToolStripButton)btns[i]).Checked = false;
                ((ToolStripButton)btns[i]).BackColor = Form1.DefaultBackColor;
            }
            switch (kind.ToLower())
            {
                case "user":
                    user_btn.BackColor = Color.LightBlue;
                    user_btn.Checked = true;

                    break;
                case "table":
                    table_btn.BackColor = Color.LightBlue;
                    table_btn.Checked = true;
                    break;
                case "view":
                    view_btn.BackColor = Color.LightBlue;
                    view_btn.Checked = true;
                    break;
            }
        }
        private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            MessageBox.Show("羽山帥");
        }

        private void toolStripMenuItem2_Click(object sender, EventArgs e)
        {

        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }

        private void posgreSQLToolStripMenuItem_Click(object sender, EventArgs e)
        {

        }

        private void mySQLToolStripMenuItem_Click(object sender, EventArgs e)
        {

        }

        private void splitContainer2_Panel1_Paint(object sender, PaintEventArgs e)
        {

        }
        private void UI_init()
        {
            db_tree.Width = splitContainer3.Width;
            db_tree.Height = splitContainer3.Height - 20;
            List<object> d = new List<object>();
            d.Add(OpenTable);
            d.Add(DesignTable);
            d.Add(NewTable);
            d.Add(DeleteTable);
            d.Add(ImportWizard);
            d.Add(ExportWizard);
            displayTools.Add("tables", d);
            d = new List<object>();
            d.Add(OpenView);
            d.Add(DesignView);
            d.Add(NewView);
            d.Add(DeleteView);
            d.Add(View_ExportWizard);
            displayTools.Add("views", d);
            d = new List<object>();
            d.Add(DesignFunction);
            d.Add(NewFunction);
            d.Add(DeleteFunction);
            d.Add(ExecuteFunction);
            displayTools.Add("functions", d);
            showTools("點到連線");
            table_top.Width = splitContainer5.Width;
            table_top.Height = splitContainer5.Height;

            // 初始化 New Query 按鈕
            query_btn.Text = "New Query";
            query_btn.ImageScaling = ToolStripItemImageScaling.None;
            query_btn.TextImageRelation = TextImageRelation.Overlay;
            query_btn.TextAlign = ContentAlignment.BottomCenter;
            query_btn.AutoSize = false;
            query_btn.Size = new Size(80, 70);
            query_btn.Click += Query_btn_Click;
            tool_Connection.Items.Add(query_btn);

            // 連結 Design Table 事件
            DesignTable.Click += DesignTable_Click;

            // 嘗試從 image 資料夾載入切好的高解析圖標
            string imgPath = Path.Combine(Application.StartupPath, "image");
            LoadIcon(connection_btn, Path.Combine(imgPath, "connection.png"), global::mySQLPunk.Properties.Resources.database);
            LoadIcon(user_btn, Path.Combine(imgPath, "user.png"), global::mySQLPunk.Properties.Resources.user);
            LoadIcon(table_btn, Path.Combine(imgPath, "table.png"), global::mySQLPunk.Properties.Resources.tables_32);
            LoadIcon(view_btn, Path.Combine(imgPath, "view.png"), global::mySQLPunk.Properties.Resources.views_32);
            LoadIcon(query_btn, Path.Combine(imgPath, "query.png"), null);
        }

        private void LoadIcon(ToolStripItem btn, string path, Image defaultImg)
        {
            if (File.Exists(path))
            {
                try 
                { 
                    btn.Image = Image.FromFile(path);
                    if (btn is ToolStripButton tsb) tsb.ImageScaling = ToolStripItemImageScaling.SizeToFit;
                    if (btn is ToolStripDropDownButton tsddb) tsddb.ImageScaling = ToolStripItemImageScaling.SizeToFit;
                }
                catch { btn.Image = defaultImg; }
            }
            else
            {
                btn.Image = defaultImg;
            }
        }

        private void DesignTable_Click(object sender, EventArgs e)
        {
            if (db_tree.SelectedNode == null) return;

            TreeNode node = db_tree.SelectedNode;
            string fullPath = node.FullPath;
            var pathParts = my.explode("\\", fullPath);

            // 只有點到 Table 本身時才允許設計 (假設路徑是 Conn\DB\Tables\TableName)
            if (pathParts.Length >= 4 && pathParts[2] == "Tables")
            {
                int connIndex = -1;
                TreeNode root = node;
                while (root.Parent != null) root = root.Parent;
                connIndex = root.Index;

                string dbName = pathParts[1];
                string tableName = pathParts[3];

                var connInfo = myN.connections[connIndex];
                IDatabase db = (IDatabase)connInfo["pdo"];

                TableDesignerForm tdf = new TableDesignerForm(db, dbName, tableName);
                tdf.Show();
            }
            else
            {
                MessageBox.Show("請先在左側選取一個具體的資料表 (Table)！");
            }
        }

        private void Query_btn_Click(object sender, EventArgs e)
        {
            if (db_tree.SelectedNode == null)
            {
                MessageBox.Show("請先選擇一個資料庫或連線！");
                return;
            }

            // 解析目前選中的連線與資料庫
            TreeNode node = db_tree.SelectedNode;
            string fullPath = node.FullPath;
            var pathParts = my.explode("\\", fullPath);

            int connIndex = -1;
            string dbName = "";

            if (pathParts.Length >= 1)
            {
                // 找根節點 (Connection)
                TreeNode root = node;
                while (root.Parent != null) root = root.Parent;
                connIndex = root.Index;
            }

            if (pathParts.Length >= 2)
            {
                // 找資料庫節點
                dbName = pathParts[1];
            }

            if (connIndex != -1)
            {
                var connInfo = myN.connections[connIndex];
                if (connInfo["isConnect"].ToString() == "T")
                {
                    IDatabase db = (IDatabase)connInfo["pdo"];
                    QueryForm qf = new QueryForm(db, dbName);
                    qf.Show();
                }
                else
                {
                    MessageBox.Show("請先雙擊連線以開啟資料庫！");
                }
            }
        }
        private void Form1_Load(object sender, EventArgs e)
        {
            ApplyModernTheme(this);
            UI_init();
            myN.getSettingINI();
            drawLists();
        }

        [DllImport("uxtheme.dll", SetLastError = true, ExactSpelling = true, CharSet = CharSet.Unicode)]
        public static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

        private void menuStrip1_ItemClicked(object sender, ToolStripItemClickedEventArgs e)
        {

        }

        private void splitContainer3_Panel1_Resize(object sender, EventArgs e)
        {
            db_tree.Width = splitContainer3.Width;
            db_tree.Height = splitContainer3.Height;
        }

        private void db_tree_AfterSelect(object sender, TreeViewEventArgs e)
        {

        }
        private void db_tree_second_click(int father_index, int index, string databaseName)
        {
            //MessageBox.Show(father_index + "," + index+","+databaseName);
            //開啟 database
            switch (myN.connections[father_index]["kind"].ToString())
            {
                case "postgresql":
                    {

                    }
                    break;
                case "mysql":
                    if (db_tree.Nodes[father_index].Nodes[index].Nodes.Count == 0)
                    {
                        TreeNode newNode = new TreeNode("Tables");
                        newNode.ImageIndex = 12;
                        newNode.SelectedImageIndex = 12;
                        db_tree.Nodes[father_index].Nodes[index].Nodes.Add(newNode);
                        //查出有哪些 table
                        string SQL = @"
                            show tables from `" + databaseName + @"`
                        ";
                        //MySqlCommand cmd = new MySqlCommand(SQL, ((MySqlConnection)myN.connections[father_index]["pdo"]));
                        DataTable dt = ((my_mysql)myN.connections[father_index]["pdo"]).selectSQL_SAFE(SQL);
                        //List<MySqlParameter> PA = new List<MySqlParameter>();
                        //cmd.Parameters.AddWithValue("@table", databaseName);
                        //dt.Load(cmd.ExecuteReader());
                        for (int i = 0, max_i = dt.Rows.Count; i < max_i; i++)
                        {
                            TreeNode tN = new TreeNode(dt.Rows[i]["Tables_in_" + databaseName].ToString());
                            tN.SelectedImageIndex = 12;
                            tN.ImageIndex = 12;
                            db_tree.Nodes[father_index].Nodes[index].Nodes[0].Nodes.Add(tN);
                        }

                        newNode = new TreeNode("Views");
                        newNode.ImageIndex = 13;
                        newNode.SelectedImageIndex = 13;
                        db_tree.Nodes[father_index].Nodes[index].Nodes.Add(newNode);

                        newNode = new TreeNode("Functions");
                        newNode.ImageIndex = 14;
                        newNode.SelectedImageIndex = 14;
                        db_tree.Nodes[father_index].Nodes[index].Nodes.Add(newNode);

                        newNode = new TreeNode("Events");
                        newNode.ImageIndex = 15;
                        newNode.SelectedImageIndex = 15;
                        db_tree.Nodes[father_index].Nodes[index].Nodes.Add(newNode);

                        newNode = new TreeNode("Queries");
                        newNode.ImageIndex = 16;
                        newNode.SelectedImageIndex = 16;
                        db_tree.Nodes[father_index].Nodes[index].Nodes.Add(newNode);

                        newNode = new TreeNode("Reports");
                        newNode.ImageIndex = 17;
                        newNode.SelectedImageIndex = 17;
                        db_tree.Nodes[father_index].Nodes[index].Nodes.Add(newNode);

                        newNode = new TreeNode("Backups");
                        newNode.ImageIndex = 18;
                        newNode.SelectedImageIndex = 18;
                        db_tree.Nodes[father_index].Nodes[index].Nodes.Add(newNode);

                        db_tree.Nodes[father_index].Nodes[index].Expand();

                        db_tree.Nodes[father_index].Nodes[index].ImageIndex = 11;
                        db_tree.Nodes[father_index].Nodes[index].SelectedImageIndex = 11;
                    }
                    break;
            }
        }
        private void db_tree_third_click(int father_index, int index, string databaseName, string name)
        {
            MessageBox.Show(father_index + "," + index + "," + name);
        }
        public void dialogMyBoxOn(string message, bool can_close)
        {
            /*
            var w = new Form() { Size = new Size(0, 0) };
            (new System.Threading.Thread(CloseIt)).Start();
            MessageBox.Show("HI");
            MessageBox.Show(w, "Wait...", "載入中...");
            */
            dialog.Size = new Size(250, 80);
            dialog.MaximizeBox = false;
            dialog.MinimizeBox = false;
            dialog.AutoSize = true;
            dialog.ControlBox = false;
            dialog.FormBorderStyle = FormBorderStyle.FixedSingle;
            dialog.StartPosition = FormStartPosition.CenterScreen;

            dialogLabel.Location = new Point(0, 0);
            dialogLabel.AutoSize = false;
            dialogLabel.Size = new Size(250, 80);
            dialogLabel.TextAlign = ContentAlignment.MiddleCenter;
            dialogLabel.Text = message;
            dialogLabel.Font = new Font("Arial", 18, FontStyle.Bold);
            dialog.Controls.Add(dialogLabel);
            dialog.TopMost = true;
            dialog.Show();
        }
        public void dialogMyBoxOff()
        {
            dialog.Controls.Remove(dialogLabel);
            dialog.Hide();
        }
        private void db_tree_DoubleClick(object sender, EventArgs e)
        {
            var tree = (TreeView)sender;
            if (tree.SelectedNode == null) return;

            dialogMyBoxOn("資料載入中...", false);
            int index = tree.SelectedNode.Index;
            //Console.WriteLine(((TreeView)sender).SelectedNode.FullPath);
            string fullPath = ((TreeView)sender).SelectedNode.FullPath;
            var m = my.explode("\\", fullPath);

            if (m.Length == 2)
            {
                //代表是子層

                db_tree_second_click(
                    ((TreeView)sender).SelectedNode.Parent.Index,
                    ((TreeView)sender).SelectedNode.Index,
                    ((TreeView)sender).SelectedNode.Text
                    );
                dialogMyBoxOff();
                return;
            }
            if (m.Length == 3)
            {
                //代表是點到 view、table 之類層
                db_tree_third_click(
                    ((TreeView)sender).SelectedNode.Parent.Parent.Index,
                    ((TreeView)sender).SelectedNode.Parent.Index,
                    ((TreeView)sender).SelectedNode.Parent.Text,
                    ((TreeView)sender).SelectedNode.Text);
                dialogMyBoxOff();
                return;
            }
            var db = myN.connections[index];
            //連線測試
            if (db["isConnect"].ToString() == "T")
            {
                //展開
                //收合
                //((TreeView)sender).SelectedNode.Toggle();
                /*switch (((TreeView)sender).SelectedNode.IsExpanded)
                {
                    case false:
                        ((TreeView)sender).SelectedNode.ExpandAll();
                        break;
                    default:
                        
                        break;
                }
                */
            }
            else
            {
                //連線，展開
                switch (db["kind"].ToString().ToLower())
                {
                    case "postgresql":
                        {
                            //Server=127.0.0.1;Port=5432;Database=myDataBase;User Id=myUsername;
                            //Password = myPassword;
                            myN.connections[index]["connString"] = "Server=" + myN.connections[index]["ip"].ToString() + ";" +
                               "Port=" + myN.connections[index]["port"].ToString() + ";" +
                               "User Id=" + myN.connections[index]["login_id"].ToString() + ";" +
                               "Password=" + myN.connections[index]["pwd"].ToString() + ";" +
                               "Database=postgres;";
                            myN.connections[index]["pdo"] = new my_postgresql();
                            //((MySqlConnection)myN.connections[index]["pdo"]).ConnectionString = myN.connections[index]["connString"].ToString();
                            ((my_postgresql)myN.connections[index]["pdo"]).setConn(myN.connections[index]["connString"].ToString());
                            if (((my_postgresql)myN.connections[index]["pdo"]).MCT.State != ConnectionState.Open)
                            {
                                try
                                {
                                    ((my_postgresql)myN.connections[index]["pdo"]).open();
                                    myN.connections[index]["isConnect"] = "T";
                                    db_tree.Nodes[index].SelectedImageIndex = 1;
                                    db_tree.Nodes[index].ImageIndex = 1;
                                    //取得 databases 列表
                                    string SQL = @"
                                        SELECT
                                           ""datname"" AS ""Database""
                                        FROM
                                           ""pg_database""
                                    ";
                                    //MySqlCommand cmd = new MySqlCommand(SQL, ((MySqlConnection)myN.connections[index]["pdo"]));
                                    DataTable dt = ((my_postgresql)myN.connections[index]["pdo"]).selectSQL_SAFE(SQL);
                                    //dt.Load(cmd.ExecuteReader());
                                    for (int i = 0, max_i = dt.Rows.Count; i < max_i; i++)
                                    {
                                        TreeNode newNode = new TreeNode(dt.Rows[i]["Database"].ToString(), i, i);
                                        newNode.ImageIndex = 10;
                                        newNode.SelectedImageIndex = 10;
                                        db_tree.Nodes[index].Nodes.Add(newNode);
                                    }
                                    ((TreeView)sender).SelectedNode.ExpandAll();
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine(ex.Message);
                                    myN.connections[index]["isConnect"] = "F";
                                    //db_tree.Nodes[index].ImageIndex = 0;
                                    //db_tree.Nodes[index].SelectedImageIndex = 0;                                
                                    ((TreeView)sender).SelectedNode.Collapse();
                                }

                            }
                        }
                        break;
                    case "sqlite":
                        {
                            //Console.WriteLine(myN.connections[index]["path"].ToString());
                            myN.connections[index]["connString"] = "Data Source=" + myN.connections[index]["path"].ToString() + ";Version=3;";
                            myN.connections[index]["pdo"] = new my_sqlite();
                            //((MySqlConnection)myN.connections[index]["pdo"]).ConnectionString = myN.connections[index]["connString"].ToString();
                            ((my_sqlite)myN.connections[index]["pdo"]).setConn(myN.connections[index]["connString"].ToString());
                            if (((my_sqlite)myN.connections[index]["pdo"]).MCT.State != ConnectionState.Open)
                            {
                                try
                                {
                                    ((my_sqlite)myN.connections[index]["pdo"]).open();
                                    myN.connections[index]["isConnect"] = "T";
                                    db_tree.Nodes[index].SelectedImageIndex = 1;
                                    db_tree.Nodes[index].ImageIndex = 1;
                                    //取得 databases 列表
                                    string SQL = @"
                                    select 'main' AS `Database`;
                                ";
                                    DataTable dt = ((my_sqlite)myN.connections[index]["pdo"]).selectSQL_SAFE(SQL);
                                    for (int i = 0, max_i = dt.Rows.Count; i < max_i; i++)
                                    {
                                        TreeNode newNode = new TreeNode(dt.Rows[i]["Database"].ToString(), i, i);
                                        newNode.ImageIndex = 10;
                                        newNode.SelectedImageIndex = 10;
                                        db_tree.Nodes[index].Nodes.Add(newNode);
                                    }
                                    ((TreeView)sender).SelectedNode.ExpandAll();
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine(ex.Message);
                                    myN.connections[index]["isConnect"] = "F";
                                    //db_tree.Nodes[index].ImageIndex = 0;
                                    //db_tree.Nodes[index].SelectedImageIndex = 0;                                
                                    ((TreeView)sender).SelectedNode.Collapse();
                                }

                            }
                        }
                        break;
                    case "mysql":
                        {
                            myN.connections[index]["connString"] = "server=" + myN.connections[index]["ip"].ToString() + ";" +
                                "port=" + myN.connections[index]["port"].ToString() + ";" +
                                "user id=" + myN.connections[index]["login_id"].ToString() + ";" +
                                "Password=" + myN.connections[index]["pwd"].ToString() + ";" +
                                "database=;sslmode=none;charset=utf8;";
                            myN.connections[index]["pdo"] = new my_mysql();
                            //((MySqlConnection)myN.connections[index]["pdo"]).ConnectionString = myN.connections[index]["connString"].ToString();
                            ((my_mysql)myN.connections[index]["pdo"]).setConn(myN.connections[index]["connString"].ToString());
                            if (((my_mysql)myN.connections[index]["pdo"]).MCT.State != ConnectionState.Open)
                            {
                                try
                                {
                                    ((my_mysql)myN.connections[index]["pdo"]).open();
                                    myN.connections[index]["isConnect"] = "T";
                                    db_tree.Nodes[index].SelectedImageIndex = 1;
                                    db_tree.Nodes[index].ImageIndex = 1;
                                    //取得 databases 列表
                                    string SQL = @"
                                    show databases;
                                ";
                                    //MySqlCommand cmd = new MySqlCommand(SQL, ((MySqlConnection)myN.connections[index]["pdo"]));
                                    DataTable dt = ((my_mysql)myN.connections[index]["pdo"]).selectSQL_SAFE(SQL);
                                    //dt.Load(cmd.ExecuteReader());
                                    for (int i = 0, max_i = dt.Rows.Count; i < max_i; i++)
                                    {
                                        TreeNode newNode = new TreeNode(dt.Rows[i]["Database"].ToString(), i, i);
                                        newNode.ImageIndex = 10;
                                        newNode.SelectedImageIndex = 10;
                                        db_tree.Nodes[index].Nodes.Add(newNode);
                                    }
                                    ((TreeView)sender).SelectedNode.ExpandAll();
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine(ex.Message);
                                    myN.connections[index]["isConnect"] = "F";
                                    //db_tree.Nodes[index].ImageIndex = 0;
                                    //db_tree.Nodes[index].SelectedImageIndex = 0;                                
                                    ((TreeView)sender).SelectedNode.Collapse();
                                }

                            }
                        }
                        break;
                    case "mssql":
                    case "sqlserver":
                        {
                            myN.connections[index]["connString"] = "Data Source=" + myN.connections[index]["ip"].ToString() + "," + myN.connections[index]["port"].ToString() + "; " +
                                // + "," + myN.connections[index]["port"].ToString() + "
                                "Integrated Security=True;" +
                                "Initial Catalog=master;" +
                                "User ID=" + myN.connections[index]["login_id"].ToString() + ";" +
                                "Password=" + myN.connections[index]["pwd"].ToString() + "";
                            //Console.WriteLine(myN.connections[index]["connString"]);
                            myN.connections[index]["pdo"] = new my_mssql();
                            //((MySqlConnection)myN.connections[index]["pdo"]).ConnectionString = myN.connections[index]["connString"].ToString();
                            ((my_mssql)myN.connections[index]["pdo"]).setConn(myN.connections[index]["connString"].ToString());
                            if (((my_mssql)myN.connections[index]["pdo"]).MCT.State != ConnectionState.Open)
                            {
                                try
                                {
                                    ((my_mssql)myN.connections[index]["pdo"]).open();
                                    myN.connections[index]["isConnect"] = "T";
                                    db_tree.Nodes[index].SelectedImageIndex = 1;
                                    db_tree.Nodes[index].ImageIndex = 1;
                                    //取得 databases 列表
                                    string SQL = @"
                                  select [name] as [Database] from sys.databases WHERE name NOT IN ('master', 'tempdb', 'model', 'msdb')
                                ";
                                    //MySqlCommand cmd = new MySqlCommand(SQL, ((MySqlConnection)myN.connections[index]["pdo"]));
                                    DataTable dt = ((my_mssql)myN.connections[index]["pdo"]).selectSQL_SAFE(SQL);
                                    //dt.Load(cmd.ExecuteReader());
                                    for (int i = 0, max_i = dt.Rows.Count; i < max_i; i++)
                                    {
                                        TreeNode newNode = new TreeNode(dt.Rows[i]["Database"].ToString(), i, i);
                                        newNode.ImageIndex = 10;
                                        newNode.SelectedImageIndex = 10;
                                        db_tree.Nodes[index].Nodes.Add(newNode);
                                    }
                                    ((TreeView)sender).SelectedNode.ExpandAll();
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine(ex.Message);
                                    myN.connections[index]["isConnect"] = "F";
                                    //db_tree.Nodes[index].ImageIndex = 0;
                                    //db_tree.Nodes[index].SelectedImageIndex = 0;                                
                                    ((TreeView)sender).SelectedNode.Collapse();
                                }

                            }
                        }
                        break;
                }

            }
            dialogMyBoxOff();
        }

        private void label1_Click(object sender, EventArgs e)
        {

        }

        private void splitContainer5_Panel1_Resize(object sender, EventArgs e)
        {
            table_top.Width = splitContainer5.Width;
            table_top.Height = splitContainer5.Height;
        }

        private void table_btn_Click(object sender, EventArgs e)
        {
            thirty_two_change("table");
        }

        private void view_btn_Click(object sender, EventArgs e)
        {
            thirty_two_change("view");
        }

        private void user_btn_Click(object sender, EventArgs e)
        {
            thirty_two_change("user");
        }

        private void tool_Connection_ItemClicked(object sender, ToolStripItemClickedEventArgs e)
        {

        }

        private void mysqlStripMenuItem_Click(object sender, EventArgs e)
        {
            //Console.WriteLine(sender.ToString());
            //MySQL
            //Form1.ActiveForm.Enabled = false;
            mysql_add_edit form = new mysql_add_edit();
            form.F1 = this;
            form.ShowDialog();
        }

        private void postgreSQLToolStripMenuItem_Click(object sender, EventArgs e)
        {
            postgresql_add_edit form = new postgresql_add_edit();
            form.F1 = this;
            form.ShowDialog();
        }

        private void oracleToolStripMenuItem_Click(object sender, EventArgs e)
        {
            oracle_add_edit form = new oracle_add_edit();
            form.F1 = this;
            form.oracle_connection_type.Text = "Basic";
            form.oracle_connection_type_selected_trigger_change();
            form.ShowDialog();
        }
        public void add_connection(Dictionary<string, object> conn)
        {
            myN.connections.Add(conn);
            myN.setSettingINI();
            drawLists();
        }
    }
}
